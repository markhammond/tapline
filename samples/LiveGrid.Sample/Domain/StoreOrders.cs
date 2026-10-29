using System.Diagnostics;
using System.Runtime.CompilerServices;
using Chalk.Arrow;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using LiveGrid.Sample.Postgres;
using Tapline.Alerts;
using Tapline.Grid;
using Tapline.Grid.Provider;
using Tapline.Grid.ChalkQL;

namespace LiveGrid.Sample.Domain;

/// <summary>The grid's source of orders: how it's read, how a filter is said to it, and what it keeps.</summary>
public interface IOrderSource : IGridSource<Guid, OrderRow>, ISourceMemory
{
    /// <summary>The grid's filter for <paramref name="filter" />: the source's own model, or the SQL it states.</summary>
    object? FilterOf(ViewFilter? filter);
}

/// <summary>
///     The orders as the ChalkQL blotter serves them (grid-sources.md §6): a database in the blotter's schema
///     (<c>orders_current</c>, <c>order_versions</c>, <c>fills</c>) — PostgreSQL's, or ChalkQL's tables in process —
///     written by the <see cref="Ledger" /> behind the house, and the house's stream, merged by
///     <see cref="StoreAndStream{TKey,TRow,TStamp}" />. A filter is SQL, stated once: pushed down to the tables for pages,
///     and kept over the tail as standing views; so an order stays in a filtered view while its newest version passes, or
///     the database's row of it last did, as the blotter's does. What a read brings from the database is kept, changed by
///     the stream (<see cref="StoreWindows.Kept" />), so the reads it covers ask the database nothing. Reads are bounded
///     by the statement timeout and the read's memory budget, nothing else. An order's fills are its run, kept as the
///     windows are: read from the database once, with those the ledger hasn't yet landed, then each written added.
/// </summary>
internal sealed class StoreOrders : IOrderSource, IAsyncDisposable
{
    private static readonly int[] OwnerSizes = [16, 64, 256]; // (a runs read's orders padded to one of these: few plans serve them all)

    /// <summary>The longest a statement runs, in ChalkQL and in the database.</summary>
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(5);

    /// <summary>How long after a version the database may still change it: the ledger lands within 3 s, stalls 3 s.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    private readonly StandingAlerts _alerts;
    private readonly IAsyncDisposable? _database;
    private readonly StoreFills _fills;
    private readonly ChalkScopes<Guid, OrderRow> _scopes;
    private readonly ChalkEngine _engine;
    private readonly string _fillsTable;
    private readonly House _house;
    private readonly Ledger _ledger;
    private readonly PlannerProcess _planner;
    private readonly Dictionary<int, Task<PreparedQuery>> _runs = [];
    private readonly Lock _runsGate = new();
    private readonly Dictionary<Guid, Run> _kept = []; // each order's fills read, kept by those written since
    private readonly Dictionary<GridView, Dictionary<Guid, Run>> _held = new(ReferenceEqualityComparer.Instance); // each view's runs read
    private long _runsUsed;

    /// <summary>
    ///     What a grid's session may keep here — a Blazor session's, as each reads a view of its own — shared out: its kept
    ///     windows (<see cref="WindowsShare" />), its open orders' fills (<see cref="KeptRuns" />), and the rest its filter's
    ///     two standing views. The state tail, the event log and the read arenas are the provider's, every session's.
    /// </summary>
    public const long SessionBudget = 64L << 20;

    /// <summary>A session's kept windows: some 17,700 rows, many times what a grid scrolled about reads.</summary>
    private const long WindowsShare = 8L << 20;

    /// <summary>The most orders whose fills a session keeps: as many rows as a grid's session keeps open.</summary>
    private const int KeptRuns = 4_096;

    /// <summary>An order's kept fills, as counted: the run, its list and its key (about), and six fills at most (its lots).</summary>
    private static readonly long RunBytes = 96 + 6 * Unsafe.SizeOf<Fill>();

    /// <summary>Each of a session's filter's two standing views: half what the session's budget leaves.</summary>
    private static readonly long StateBudget = (SessionBudget - WindowsShare - KeptRuns * RunBytes) / 2;

    /// <summary>Each of a fills session's filter's two standing views: half what the budget leaves (no runs kept for it).</summary>
    private static readonly long FillStateBudget = (SessionBudget - WindowsShare) / 2;
    private readonly StoreAndStream<Guid, OrderRow, DateTime> _source;
    private readonly ChalkStore<Guid, OrderRow, DateTime> _store;

    private StoreOrders(PlannerProcess planner, ChalkEngine engine, ChalkStore<Guid, OrderRow, DateTime> store, StoreAndStream<Guid, OrderRow, DateTime> source,
        ChalkScopes<Guid, OrderRow> scopes, string fillsTable, StoreFills fills, StandingAlerts alerts, House house, Ledger ledger,
        IAsyncDisposable? database)
    {
        (_planner, _engine, _store, _source, _scopes, _fillsTable, _fills, _alerts, _house, _ledger, _database) =
            (planner, engine, store, source, scopes, fillsTable, fills, alerts, house, ledger, database);
        ledger.Filled = Written;
    }

    public long Version => _source.Version;

    /// <summary>The fills tab's source: the table <c>fills</c> and the house's fills, on this engine.</summary>
    public IFillSource Fills => _fills;

    /// <summary>
    ///     The alerts' evaluator: the house's batches as the ledger hears them and lands them, in ChalkQL views of their own, on
    ///     this planner. Its desk's to dispose (<see cref="AlertDesk" />), before this.
    /// </summary>
    public IAlertEvaluator Alerts => _alerts;

    public async ValueTask DisposeAsync()
    {
        await _alerts.DisposeAsync(); // (its desk's, first, as a rule: again, nothing)
        _source.Dispose();
        _fills.Dispose();
        await _ledger.DisposeAsync();
        _store.Dispose();
        await _engine.DisposeAsync();
        await _planner.DisposeAsync();
        if (_database is not null) await _database.DisposeAsync();
    }

    public object? FilterOf(ViewFilter? filter)
    {
        return filter?.Sql;
    }

    /// <summary>
    ///     The provider's own: the state tail and the event log (StoreAndStream's), the read arenas kept warm (ChalkQL's), and
    ///     the fills of orders no session holds open, kept for a session's next view.
    /// </summary>
    public IReadOnlyList<(string Part, GridMemory Memory)> Memory
    {
        get
        {
            var (tail, history, _) = _source.Memory;
            return [("state tail", tail), ("event log", history), ("warm read arenas", _store.Memory), ("fills between views", Unheld())];
        }
    }

    /// <summary>
    ///     A session's: its view's kept windows (StoreAndStream's), its filter's standing views (ChalkQL's), and the fills of
    ///     the orders it holds open — each beside its share of <see cref="SessionBudget" />, whether it has read yet or not.
    /// </summary>
    public IReadOnlyList<(string Part, GridMemory Memory)> MemoryOf(GridView? view)
    {
        var (windows, filter) = view is null ? default : _source.MemoryOf(view);
        long fills = 0;
        lock (_runsGate)
            if (view is not null && _held.TryGetValue(view, out var mine))
                foreach (var (owner, run) in mine)
                    if (_kept.TryGetValue(owner, out var kept) && kept == run)
                        fills += Bytes(run);
        return
        [
            ("kept windows", new GridMemory(windows.Held, WindowRows * StoreAndStream<Guid, OrderRow, DateTime>.WindowRowBytes)),
            ("filter's views", new GridMemory(filter.Held, 2 * StateBudget)), ("open orders' fills", new GridMemory(fills, KeptRuns * RunBytes))
        ];
    }

    /// <summary>The most rows a session's kept windows hold: its share.</summary>
    private static int WindowRows => (int)(WindowsShare / StoreAndStream<Guid, OrderRow, DateTime>.WindowRowBytes);

    /// <summary>The most rows a fills session's kept windows hold: its share, of fills.</summary>
    private static int FillWindowRows => (int)(WindowsShare / StoreAndStream<Guid, FillRow, DateTime>.WindowRowBytes);

    /// <summary>A run's fills, as counted.</summary>
    private static long Bytes(Run run)
    {
        return 96 + run.Fills.Count * (long)Unsafe.SizeOf<Fill>();
    }

    /// <summary>The fills of orders no session holds, beside the most kept: as many as a session's.</summary>
    private GridMemory Unheld()
    {
        long held = 0;
        lock (_runsGate)
            foreach (var run in _kept.Values)
                if (run.Views == 0)
                    held += Bytes(run);
        return new GridMemory(held, KeptRuns * RunBytes);
    }

    public ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<Guid, OrderRow> read, GridPage<Guid, OrderRow> page,
        CancellationToken cancellationToken)
    {
        return _source.ReadAsync(view, read, page, cancellationToken);
    }

    public IDisposable Watch(Action changed)
    {
        return _source.Watch(changed);
    }

    public void Leave(GridView view)
    {
        _source.Leave(view);
        lock (_runsGate)
            if (_held.Remove(view, out var mine))
            {
                foreach (var run in mine.Values) run.Views--;
                ForgetUnheld();
            }
    }

    public bool NewestFirstByKey(GridView view)
    {
        return _source.NewestFirstByKey(view);
    }

    /// <summary>
    ///     Each order's fills, newest first, each a row of the order's shape (<see cref="Fill.RowOf" />) — kept as the grid's
    ///     windows are, by the stream: an order's read from the database once (in one statement for all the orders a read
    ///     finds unkept), with those the ledger has yet to land, then each fill the house writes added as it's written. Held
    ///     by each session (view) reading them, shared: past <see cref="KeptRuns" /> of a view's, its least recently read let
    ///     go; those no view holds kept as many again, for a session's next view (sorted, filtered) to find.
    /// </summary>
    public async ValueTask<GridAnswer> ReadRunsAsync(GridView view, ReadOnlyMemory<GridRunRead<Guid>> reads, GridRunPage<Guid, OrderRow> page,
        CancellationToken cancellationToken)
    {
        List<Guid>? unkept = null;
        List<Task>? loading = null;
        TaskCompletionSource? loaded = null; // (the unkept runs this read reads: ready once read)
        lock (_runsGate)
        {
            if (!_held.TryGetValue(view, out var mine)) _held[view] = mine = [];
            foreach (var read in reads.Span)
            {
                if (_kept.TryGetValue(read.Owner, out var run))
                {
                    if (!run.Ready.IsCompleted) (loading ??= []).Add(run.Ready);
                }
                else
                {
                    loaded ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _kept[read.Owner] = run = new Run { Ready = loaded.Task };
                    var pending = new List<Fill>();
                    _ledger.PendingFills(read.Owner, pending); // (written, not landed: the database may not have them as it's read)
                    foreach (var fill in pending) run.Add(fill);
                    (unkept ??= []).Add(read.Owner);
                }

                run.Used = ++_runsUsed;
                if (mine.TryGetValue(read.Owner, out var had) && had == run) continue;
                if (had is not null) had.Views--; // (let go meanwhile, its read failed: read again)
                mine[read.Owner] = run;
                run.Views++;
            }

            Forget(mine);
        }

        if (unkept is not null) await LoadAsync(unkept, loaded!, cancellationToken).ConfigureAwait(false);
        if (loading is not null) await Task.WhenAll(loading).ConfigureAwait(false);
        // The orders' rows, the house's, first: never its lock under the runs' — it writes fills holding its own, and they
        // take the runs' lock as they're written.
        var orders = new OrderRow[reads.Length];
        for (var r = 0; r < reads.Length; r++)
        {
            var n = _house.NumberOf(reads.Span[r].Owner);
            orders[r] = n < 0 ? default : _house.RowOf(n);
        }

        lock (_runsGate)
            for (var r = 0; r < reads.Length; r++)
            {
                var read = reads.Span[r];
                var fills = _kept.TryGetValue(read.Owner, out var run) ? run.Fills : [];
                var order = orders[r];
                long start, at = -1;
                if (read.Kind == GridRunReadKind.AtOffset)
                {
                    start = Math.Min(read.Offset, fills.Count);
                }
                else
                {
                    at = fills.FindIndex(f => f.Id == read.Key);
                    start = at < 0 ? fills.Count : Math.Max(0, at - read.Before);
                }

                var end = read.Kind == GridRunReadKind.AroundKey ? at < 0 ? start : Math.Min(fills.Count, at + read.Count)
                    : Math.Min(fills.Count, start + read.Count);
                for (var i = (int)start; i < end && page.Add(fills[i].Id, (fills[i] with { Number = fills.Count - i }).RowOf(order), 1); i++)
                {
                }

                page.End(new GridRunAnswer
                {
                    FirstOffset = start, Length = fills.Count, LengthKind = TotalKind.Exact,
                    Anchor = read.Kind == GridRunReadKind.AroundKey && at < 0 ? GridAnswer.NotFound : (int)Math.Max(0, at - start)
                });
            }

        return new GridAnswer { Version = _source.Version };
    }

    /// <summary>
    ///     The orders' fills read from the database, in one statement for each so many (padded: few plans serve them all),
    ///     into their kept runs (those written meanwhile there already); ready once read — or let go, failing, for the next
    ///     read to ask again.
    /// </summary>
    private async Task LoadAsync(List<Guid> owners, TaskCompletionSource loaded, CancellationToken cancellationToken)
    {
        try
        {
            var found = new List<(Guid Order, Fill Fill)>();
            for (var start = 0; start < owners.Count; start += OwnerSizes[^1])
            {
                var count = Math.Min(OwnerSizes[^1], owners.Count - start);
                var size = OwnerSizes.First(s => s >= count);
                var batch = new Guid[size]; // (padded with its first order, which changes nothing an IN finds)
                for (var i = 0; i < size; i++) batch[i] = owners[start + (i < count ? i : 0)];
                await using var execution = await _engine.ExecuteAsync(await Prepared(size), new Dictionary<string, object?> { ["o"] = batch },
                    ct: cancellationToken).ConfigureAwait(false);
                await foreach (var rows in execution.Batches.WithCancellation(cancellationToken).ConfigureAwait(false))
                    using (rows)
                    {
                        for (var i = 0; i < rows.Length; i++)
                            if (Value(rows, 1, i) is Guid order)
                                found.Add((order, new Fill((Guid)Value(rows, 0, i)!, Micros(Value(rows, 2, i)), (double)Value(rows, 3, i)!,
                                    (double)Value(rows, 4, i)!, (double)Value(rows, 5, i)!, 0)));
                    }
            }

            lock (_runsGate)
                foreach (var (order, fill) in found)
                    if (_kept.TryGetValue(order, out var run))
                        run.Add(fill);
            loaded.TrySetResult();
        }
        catch (Exception failure)
        {
            lock (_runsGate)
                foreach (var owner in owners)
                    if (_kept.TryGetValue(owner, out var run) && run.Ready == loaded.Task)
                        _kept.Remove(owner);
            loaded.TrySetException(failure);
            throw;
        }
    }

    /// <summary>Fills the house wrote: into the kept runs of their orders.</summary>
    private void Written(ReadOnlySpan<(OrderRow Order, Fill Fill)> fills)
    {
        lock (_runsGate)
            foreach (ref readonly var made in fills)
                if (_kept.TryGetValue(made.Order.OrderId, out var run))
                    run.Add(made.Fill);
    }

    /// <summary>Past <see cref="KeptRuns" /> of a view's, its least recently read let go (a tenth of them), then those no view holds.</summary>
    private void Forget(Dictionary<Guid, Run> mine)
    {
        if (mine.Count <= KeptRuns) return;
        var used = new List<long>(mine.Count);
        foreach (var run in mine.Values) used.Add(run.Used);
        used.Sort();
        var below = used[mine.Count - KeptRuns + KeptRuns / 10];
        foreach (var (owner, run) in mine)
            if (run.Used < below)
            {
                mine.Remove(owner);
                run.Views--;
            }

        ForgetUnheld();
    }

    /// <summary>Past <see cref="KeptRuns" /> of runs no view holds, the least recently read let go (a tenth of them), none loading.</summary>
    private void ForgetUnheld()
    {
        var unheld = new List<(long Used, Guid Owner)>();
        foreach (var (owner, run) in _kept)
            if (run.Views == 0 && run.Ready.IsCompleted)
                unheld.Add((run.Used, owner));
        if (unheld.Count <= KeptRuns) return;
        unheld.Sort();
        for (var i = 0; i < unheld.Count - KeptRuns + KeptRuns / 10; i++) _kept.Remove(unheld[i].Owner);
    }

    /// <summary>
    ///     The database as the start found it, in ChalkQL's tables in process (<c>--store memory</c>): every order's newest
    ///     version (its versions so far that one) and its fills; the ledger landing what the house writes from now, by
    ///     refreshes; sizes counted.
    /// </summary>
    public static async Task<StoreOrders> StartAsync(History history, House house, Ledger ledger, Action<string> log)
    {
        var started = Stopwatch.GetTimestamp();
        var (current, fills) = Records(history);
        var db = new PocoSourceBuilder("db", "db")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("orders_current", current, out var currentTable, t => t.Keyed(r => r.OrderId))
            .AddTable("order_versions", (OrderRecord[])current.Clone(), out var versionsTable, t => t.Keyed(r => r.OrderId, r => r.Version))
            .AddTable("fills", fills, out var fillsTable, t => t.Keyed(r => r.FillId))
            .Build();
        // Sizes counted (in process, a pass over the table): ChalkQL's plan estimates can't see a filter's values (an equality
        // is taken as a share of the table, whatever it matches), so a few hundred Partial orders would estimate at 150,000.
        var orders = await StartAsync(db, "db", null, null, ChalkSizes.Count, house, ledger, null);
        ledger.Attach((versions, newest, made, ct) =>
        {
            OrderRecord[] landed = [.. versions.Select(r => OrderRecord.Of(r))], now = [.. newest.Select(r => OrderRecord.Of(r))];
            FillRecord[] filled = [.. made.Select(m => FillRecord.Of(m.Order, m.Fill, m.Order.ClOrdId.ToString()))];
            return orders._engine.RefreshAsync(refresh =>
            {
                if (landed.Length > 0) refresh.Upsert(versionsTable, landed);
                if (now.Length > 0) refresh.Upsert(currentTable, now);
                if (filled.Length > 0) refresh.Upsert(fillsTable, filled);
            }, ct).AsTask();
        });
        log($"The database: {current.Length:N0} orders and {fills.Count:N0} fills in ChalkQL's tables, made in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:N0} ms");
        return orders;
    }

    /// <summary>
    ///     The database as the start found it, in PostgreSQL (<c>--store postgres</c>, <see cref="PgOrders" />): seeded
    ///     straight from the history by binary <c>COPY</c>, then indexed and analysed; read through ChalkQL's ADO.NET source;
    ///     the ledger landing what the house writes from now, as ingestion jobs would; sizes estimated from PostgreSQL's
    ///     plans, not counted.
    /// </summary>
    public static async Task<StoreOrders> StartPostgresAsync(History history, House house, Ledger ledger, PgServer server, Action<string> log)
    {
        var (started, allocated) = (Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes());
        var pg = new PgOrders(server.ConnectionString, TimeLimit);
        try
        {
            var fills = await pg.CreateAsync(history, CancellationToken.None);
            var (took, seeding) = (Stopwatch.GetElapsedTime(started), GC.GetTotalAllocatedBytes() - allocated);
            var orders = await StartAsync(pg.Source(history.Orders, history.Orders, fills), PgOrders.Schema, pg.EstimateAsync,
                pg.EstimateFillsAsync, ChalkSizes.None, house, ledger, new Database(pg, server));
            ledger.Attach(pg.LandAsync);
            log($"The database: {history.Orders:N0} orders and {fills:N0} fills in PostgreSQL ({(server.Owned ? "started by the sample" : "given")}), " +
                $"seeded and indexed in {took.TotalMilliseconds:N0} ms, {seeding / 1048576.0:N1} MB allocated meanwhile (the whole process)");
            return orders;
        }
        catch
        {
            await pg.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    ///     The engine over the database's tables (named <paramref name="schema" /><c>.orders_current</c>, …) and the grids'
    ///     tails, the orders' and the fills', and the merges: the orders', and the fills' (<see cref="Fills" />).
    /// </summary>
    private static async Task<StoreOrders> StartAsync(ISourceRuntime db, string schema, ChalkSizer? estimate, ChalkSizer? estimateFills,
        ChalkSizes sizes, House house, Ledger ledger, IAsyncDisposable? database)
    {
        var planner = await PlannerProcess.StartAsync(new PlannerProcessOptions()); // (the Calcite sidecar: Java 21+)
        var rows = new ChalkRows<Guid, OrderRow, DateTime>(nameof(OrderRow.OrderId), nameof(OrderRow.Version), nameof(OrderRow.EventTime));
        var tail = ChalkTail<Guid, OrderRow>.Of<OrderRecord>(rows);
        var fillRows = new ChalkRows<Guid, FillRow, DateTime>(nameof(FillRow.FillId), null, nameof(FillRow.EventTime)); // (a fill: one version)
        var fillTail = ChalkTail<Guid, FillRow>.Of<FillRecord>(fillRows, "fill_tail");
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "livegrid", Sources = [db, tail.Source, fillTail.Source], Planner = planner.CreatePlanner()
        });
        // The ledger lands versions up to 3 s late and stalls for 3 s now and then: out of stamp order by 6 s at most, so
        // a haircut of 7 s — this provider knows its store, so it says so (the default, a minute, is for one that doesn't).
        // A kept window is read again each minute (the default) while it's read, unless settled (SettledAsync).
        ChalkStore<Guid, OrderRow, DateTime>? store = null;
        store = new ChalkStore<Guid, OrderRow, DateTime>(engine, rows, $"{schema}.orders_current", $"{schema}.order_versions",
            new AppendOnlyStorePolicy { Haircut = TimeSpan.FromSeconds(7), MaxWindowRowsPerView = WindowRows }, TimeLimit, estimate, sizes,
            settled: (orders, ct) => SettledAsync(store!, orders, ct));
        var scopes = new ChalkScopes<Guid, OrderRow>(engine, tail, rows, stateBudget: StateBudget);
        var source = new StoreAndStream<Guid, OrderRow, DateTime>(store, ledger, rows, scopes);
        // The fills: the table both their current and their versions' (a fill is its own one version), the same haircut.
        ChalkStore<Guid, FillRow, DateTime>? fillStore = null;
        fillStore = new ChalkStore<Guid, FillRow, DateTime>(engine, fillRows, $"{schema}.fills", $"{schema}.fills",
            new AppendOnlyStorePolicy { Haircut = TimeSpan.FromSeconds(7), MaxWindowRowsPerView = FillWindowRows }, TimeLimit, estimateFills,
            sizes, settled: (fills, ct) => SettledFillsAsync(fillStore!, fills, ct));
        var fillScopes = new ChalkScopes<Guid, FillRow>(engine, fillTail, fillRows, stateBudget: FillStateBudget);
        var fills = new StoreFills(new StoreAndStream<Guid, FillRow, DateTime>(fillStore, ledger.Fills, fillRows, fillScopes), fillStore,
            FillStateBudget, FillWindowRows);
        var alerts = await StandingAlerts.StartAsync(planner, ledger);
        return new StoreOrders(planner, engine, store, source, scopes, $"{schema}.fills", fills, alerts, house, ledger, database);
    }

    /// <summary>
    ///     Whether a kept window's fills will change no more: the database's own clock past the newest of them by more than
    ///     <see cref="Settle" /> — a fill, once there, never changes; one down for maintenance, its clock standing, settles nothing.
    /// </summary>
    private static async ValueTask<bool> SettledFillsAsync(ChalkStore<Guid, FillRow, DateTime> store, ReadOnlyMemory<FillRow> fills,
        CancellationToken ct)
    {
        var newest = DateTime.MinValue;
        foreach (ref readonly var fill in fills.Span)
            if (fill.EventTime > newest)
                newest = fill.EventTime;
        return await store.NewestStampAsync(ct).ConfigureAwait(false) is { } clock && clock - newest > Settle;
    }

    /// <summary>
    ///     Whether a kept window's orders will change no more: each filled or cancelled (no version comes after), and the
    ///     database's own clock — its newest stamp — past the newest of them by more than <see cref="Settle" />. The house is
    ///     its one producer, so its clock is the database's: one down for maintenance, its clock standing, settles nothing.
    /// </summary>
    private static async ValueTask<bool> SettledAsync(ChalkStore<Guid, OrderRow, DateTime> store, ReadOnlyMemory<OrderRow> orders,
        CancellationToken ct)
    {
        if (NewestFinal(orders.Span) is not { } newest) return false;
        return await store.NewestStampAsync(ct).ConfigureAwait(false) is { } clock && clock - newest > Settle;
    }

    /// <summary>The newest stamp of orders all filled or cancelled; null if any isn't.</summary>
    private static DateTime? NewestFinal(ReadOnlySpan<OrderRow> orders)
    {
        var newest = DateTime.MinValue;
        foreach (ref readonly var order in orders)
        {
            if (!InlineText.Is(order.Status, "Filled"u8) && !InlineText.Is(order.Status, "Cancelled"u8)) return null;
            if (order.EventTime > newest) newest = order.EventTime;
        }

        return newest;
    }

    /// <summary>The history as the database holds it: every order's newest version, and its fills.</summary>
    private static (OrderRecord[] Current, List<FillRecord> Fills) Records(History history)
    {
        var current = new OrderRecord[history.Orders];
        var fills = new List<FillRecord>((int)Math.Min(int.MaxValue, history.Orders * 2));
        var made = new List<Fill>(4);
        for (var n = 0L; n < history.Orders; n++)
        {
            made.Clear();
            var row = history.Make(n, made);
            var record = current[n] = OrderRecord.Of(row);
            foreach (var fill in made) fills.Add(FillRecord.Of(row, fill, record.ClOrdId));
        }

        return (current, fills);
    }

    private Task<PreparedQuery> Prepared(int size)
    {
        lock (_runs)
        {
            if (!_runs.TryGetValue(size, out var prepared) || prepared.IsFaulted)
                _runs[size] = prepared = _engine.PrepareAsync($"SELECT fill_id, order_id, event_time, qty, price, amt FROM {_fillsTable} WHERE order_id IN @o")
                    .AsTask();
            return prepared;
        }
    }

    private static object? Value(Apache.Arrow.RecordBatch rows, int column, int index)
    {
        return RecordBatchExtensions.ValueAt(rows.Column(column), index);
    }

    /// <summary>An order's fills, newest first: read from the database (ready once read), and each written since.</summary>
    private sealed class Run
    {
        public readonly List<Fill> Fills = [];
        public Task Ready = Task.CompletedTask;
        public long Used;
        public int Views; // the views holding it

        /// <summary>A fill, in its place (newest first), if it isn't there already.</summary>
        public void Add(in Fill fill)
        {
            var at = 0;
            for (; at < Fills.Count; at++)
            {
                if (Fills[at].Id == fill.Id) return;
                if (Fills[at].Time < fill.Time || (Fills[at].Time == fill.Time && Fills[at].Id.CompareTo(fill.Id) < 0)) break;
            }

            for (var i = at; i < Fills.Count; i++)
                if (Fills[i].Id == fill.Id)
                    return;
            Fills.Insert(at, fill);
        }
    }

    private sealed class Database(PgOrders orders, PgServer server) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await orders.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static long Micros(object? time)
    {
        return time switch
        {
            DateTime t => Times.Micros(t), DateTimeOffset t => Times.Micros(t.UtcDateTime), long micros => micros, _ => 0
        };
    }
}
