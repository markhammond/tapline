using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Chalk.Client;
using Chalk.Sources.Poco;
using Tapline.Grid.Provider;
using Tapline.Grid.ChalkQL;

// Tapline.Grid.ChalkQL's tests: StoreAndStream over ChalkQL — the views' filters standing views over the tail's tables, the
// store's pages pushed down to a database in process — against the truth worked out apart. ChalkQL's planner (a Java 21
// sidecar) starts once for them all. TAPLINE_TEST=<part of a name> runs only those.
var only = Environment.GetEnvironmentVariable("TAPLINE_TEST");
(string Name, Func<PlannerProcess, Task> Run)[] tests =
[
    ("ChalkQL store and stream merged: the store's pages through ChalkQL, the tail's rows passing its standing views — the stream ahead of the store and behind it, in four views (sorted, filtered by SQL with values bound): the top, a page from a rank and one about a row the truth's, each key once at its newest, ranked and counted exactly; a key the stream alone has found about it, filtered out not; a view's filter's state counted as its own; the store caught up, the tail let go",
        T.Merged),
    ("ChalkQL against an oracle: three seeds of versions streamed in order and landed out of stamp order within the haircut (the store behind, ahead, stalled), reads from ranks, about rows and about keys in four views — every page exactly what the source can know, in order, no key twice; quiet, every page the truth's at its ranks, the tail let go",
        T.Oracle),
    ("ChalkQL refusals: a filter ChalkQL can't prepare, a filter not ChalkQL's, a parameter named as the grid's, a sort by no column — each read refused saying why; a view past its state budget refused; a read past its memory budget refused saying so, served in one it fits; sized without counting, the provider's estimate past a page, none at least the rows seen, a view ending in its page counted from it",
        T.Refusals),
    ("ChalkQL times: stamps and fields of DateTime (UTC), bound as the TIMESTAMP wall clocks ChalkQL's tables hold — the gap fill and a recheck reading the versions since a stamp, the newest stamp read back as UTC, pages sorted by a time from a rank and about a key, a filter on a time over the store and the tail — each the truth's, nothing refused",
        T.Times),
    ("ChalkQL rows that never change (fills, trades: no version, one table their current and their versions): five streamed ahead of the store, at the top, each once, counted; sent again, and landed, still once; filtered over store and tail alike; the store past them by a haircut, the tail let go",
        T.Immutable)
];
await using var planner = await PlannerProcess.StartAsync(new PlannerProcessOptions());
var failed = 0;
foreach (var (name, run) in tests.Where(t => only is null || t.Name.Contains(only, StringComparison.OrdinalIgnoreCase)))
{
    var timer = Stopwatch.StartNew();
    try
    {
        await run(planner);
        Console.WriteLine($"PASS  {name}  ({timer.ElapsedMilliseconds:N0} ms)");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
    }
}

Console.WriteLine(failed == 0 ? "\nAll passed." : $"\n{failed} failed.");
return failed == 0 ? 0 : 1;

/// <summary>A trade: it never changes — no version — and its stamp is its time, µs since the Unix epoch.</summary>
internal record struct TradeRow
{
    public long Id;
    public long Stamp;
    public int Qty;
}

/// <summary>An order's version, as the database, the stream and the grid have it.</summary>
internal record struct OrderRow
{
    public long Id;
    public int Version;
    public long Stamp; // µs since the Unix epoch
    public int Qty;
    public byte Status;
}

/// <summary>An order's version timed as the blotter's are: its stamp and when it was placed, UTC.</summary>
internal record struct TimedRow
{
    public long Id;
    public int Version;
    public DateTime Stamp;
    public DateTime Placed;
}

/// <summary>A view as the grid reads it, and as the truth is worked out apart: its filter in .NET, its order compared by hand.</summary>
internal sealed record Shown(GridView View, Func<OrderRow, bool> Passes, IComparer<OrderRow> Order);

/// <summary>A view's order, by hand: its columns, then the key, newest first.</summary>
internal sealed class ByHand(params (string Column, bool Descending)[] sort) : IComparer<OrderRow>
{
    public int Compare(OrderRow a, OrderRow b)
    {
        foreach (var (column, descending) in sort)
        {
            var c = column == "qty" ? a.Qty.CompareTo(b.Qty) : a.Status.CompareTo(b.Status);
            if (c != 0) return descending ? -c : c;
        }

        return b.Id.CompareTo(a.Id);
    }
}

/// <summary>
///     The engine over a database in process (<c>db.orders</c>, each order's newest version landed; <c>db.order_versions</c>,
///     every version) and the tail's tables, the store and the scopes over it, and the source once subscribed.
/// </summary>
internal sealed class Rig : IAsyncDisposable
{
    public readonly ManualClock Clock = new();
    public readonly Dictionary<long, int> Landed = [];
    public readonly ChalkRows<long, OrderRow, long> Rows = new(nameof(OrderRow.Id), nameof(OrderRow.Version), nameof(OrderRow.Stamp));
    public readonly Sent<OrderRow> Stream = new();
    public readonly World World;
    private PocoTable<OrderRow> _current, _versions;
    public ChalkEngine Engine = null!;
    public ChalkScopes<long, OrderRow> Scopes = null!;
    public StoreAndStream<long, OrderRow, long>? Source;
    public ChalkStore<long, OrderRow, long> Store = null!;

    private Rig(int seed)
    {
        World = new World(Clock, seed);
    }

    public async ValueTask DisposeAsync()
    {
        Source?.Dispose();
        await Task.Delay(50); // (the catch-up closing the views)
        await Engine.DisposeAsync();
    }

    public static async Task<Rig> StartAsync(PlannerProcess planner, int seed, AppendOnlyStorePolicy? policy = null, long stateBudget = 64L << 20,
        ChalkSizes sizes = ChalkSizes.Count, ChalkSizer? estimate = null)
    {
        var rig = new Rig(seed);
        var tail = new ChalkTail<long, OrderRow>(rig.Rows);
        var db = new PocoSourceBuilder("db", "db")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("orders", (IReadOnlyList<OrderRow>)[], out rig._current, t => t.Keyed(r => r.Id))
            .AddTable("order_versions", (IReadOnlyList<OrderRow>)[], out rig._versions, t => t.Keyed(r => r.Id, r => r.Version))
            .Build();
        rig.Engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = $"grid-{seed}", Sources = [db, tail.Source], Planner = planner.CreatePlanner()
        });
        rig.Scopes = new ChalkScopes<long, OrderRow>(rig.Engine, tail, rig.Rows, stateBudget: stateBudget);
        rig.Store = new ChalkStore<long, OrderRow, long>(rig.Engine, rig.Rows, "db.orders", "db.order_versions", policy,
            estimate: estimate, sizes: sizes); // (counted by default here: the tests hold totals to the truth)
        return rig;
    }

    public void Subscribe()
    {
        Source = new StoreAndStream<long, OrderRow, long>(Store, Stream, Rows, Scopes, Clock);
    }

    /// <summary>Versions land in the database: every one in its versions, the newest of each order in its current rows.</summary>
    public async Task LandAsync(params OrderRow[] rows)
    {
        if (rows.Length == 0) return;
        OrderRow[] newer = [.. rows.Where(r => r.Version > Landed.GetValueOrDefault(r.Id)).GroupBy(r => r.Id).Select(g => g.MaxBy(r => r.Version))];
        foreach (var row in newer) Landed[row.Id] = row.Version;
        await Engine.RefreshAsync(refresh =>
        {
            refresh.Upsert(_versions, rows);
            if (newer.Length > 0) refresh.Upsert(_current, newer);
        });
    }

    /// <summary>A recheck done: the clock past the policy's, a read to ask for it, then waited for.</summary>
    public async Task RecheckedAsync()
    {
        var done = Source!.Rechecks;
        Clock.Advance(AppendOnlyStorePolicy.Default.Recheck);
        await ReadAsync(GridView.Natural, GridRead<long, OrderRow>.AtRank(0, 1));
        for (var i = 0; Source.Rechecks == done; i++)
        {
            if (i == 500) throw new TimeoutException("no recheck");
            await Task.Delay(10);
        }
    }

    public async Task<(GridAnswer Answer, OrderRow[] Rows)> ReadAsync(GridView view, GridRead<long, OrderRow> read)
    {
        var page = new GridPage<long, OrderRow>(4_096);
        var answer = await Source!.ReadAsync(view, read, page, CancellationToken.None);
        return (answer, page.Rows.ToArray());
    }
}

/// <summary>The stream: each batch to its one subscriber, as sent.</summary>
internal sealed class Sent<TRow> : IGridStream<TRow> where TRow : unmanaged
{
    private GridBatch<TRow>? _batch;

    public IDisposable Subscribe(GridBatch<TRow> batch)
    {
        _batch = batch;
        return new Ended(this);
    }

    public void Send(params TRow[] rows)
    {
        _batch?.Invoke(rows);
    }

    private sealed class Ended(Sent<TRow> stream) : IDisposable
    {
        public void Dispose()
        {
            stream._batch = null;
        }
    }
}

/// <summary>The orders: each one's newest version made, every version kept, stamped by the clock.</summary>
internal sealed class World(ManualClock clock, int seed)
{
    public readonly Dictionary<(long, int), OrderRow> All = [];
    public readonly Dictionary<long, OrderRow> Made = [];
    private readonly Random _random = new(seed);
    private long _next;

    public long Now => (clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / 10;

    public OrderRow Make(long id, byte? status = null)
    {
        var version = Made.TryGetValue(id, out var was) ? was.Version + 1 : 1;
        var row = new OrderRow { Id = id, Version = version, Stamp = Now, Qty = _random.Next(1_000), Status = status ?? (byte)_random.Next(4) };
        Made[id] = All[(id, version)] = row;
        _next = Math.Max(_next, id + 1);
        return row;
    }

    public OrderRow New(byte? status = null)
    {
        return Make(_next, status);
    }

    public long Any()
    {
        return _random.NextInt64(Math.Max(1, _next));
    }

    public OrderRow[] Truth(Shown view)
    {
        return [.. Made.Values.Where(view.Passes).OrderBy(r => r, view.Order)];
    }
}

/// <summary>A clock moved by hand, its timers firing as it passes their time: nanosecond timestamps.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly List<Timer> _timers = [];
    private long _now = 1_000_000_000_000;

    public override long TimestampFrequency => 1_000_000_000;

    public override long GetTimestamp()
    {
        return Volatile.Read(ref _now);
    }

    public override DateTimeOffset GetUtcNow()
    {
        return DateTimeOffset.UnixEpoch.AddTicks(Volatile.Read(ref _now) / 100);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_timers) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref _now, by.Ticks * 100);
        while (true)
        {
            Timer? due;
            lock (_timers) due = _timers.FirstOrDefault(t => t.DueAt >= 0 && t.DueAt <= _now);
            if (due is null) return;
            due.DueAt = -1;
            due.Fire();
        }
    }

    private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long DueAt = -1;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timers) DueAt = dueTime == Timeout.InfiniteTimeSpan ? -1 : clock._now + dueTime.Ticks * 100;
            return true;
        }

        public void Dispose()
        {
            lock (clock._timers) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        public void Fire()
        {
            callback(state);
        }
    }
}

internal static class T
{
    private static Shown[] Views()
    {
        return
        [
            new Shown(GridView.Natural, _ => true, new ByHand()),
            new Shown(new GridView([new SortKey("qty", true)]), _ => true, new ByHand(("qty", true))),
            new Shown(new GridView([], new ChalkFilter("status = @status", new Dictionary<string, object?> { ["status"] = 1 })),
                r => r.Status == 1, new ByHand()),
            new Shown(new GridView([new SortKey("status"), new SortKey("qty", true)],
                    new ChalkFilter("qty > @least AND status <> @not", new Dictionary<string, object?> { ["least"] = 300, ["not"] = 3 })),
                r => r.Qty > 300 && r.Status != 3, new ByHand(("status", false), ("qty", true)))
        ];
    }

    private static void Assert([DoesNotReturnIf(false)] bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    private static string Ids(IEnumerable<OrderRow> rows)
    {
        return string.Join(",", rows.Take(12).Select(r => $"{r.Id}v{r.Version}"));
    }

    public static async Task Merged(PlannerProcess planner)
    {
        await using var rig = await Rig.StartAsync(planner, 1, // (each read the store's: the merge's counts exact)
            new AppendOnlyStorePolicy { Haircut = TimeSpan.FromSeconds(5), GapFill = TimeSpan.FromSeconds(2), Windows = StoreWindows.None });
        var world = rig.World;
        await rig.LandAsync([.. Enumerable.Range(0, 200).Select(_ => world.New())]);
        rig.Clock.Advance(TimeSpan.FromSeconds(3)); // (the store's rows, from before the subscription: in a recheck's reach)
        rig.Subscribe();
        var source = rig.Source!;

        // The stream ahead of the store: thirty versions of its orders and twenty new orders sent, every other landed; and
        // behind it, five versions landed, not yet sent.
        var sent = new List<OrderRow>();
        for (var i = 0; i < 30; i++) sent.Add(world.Make(world.Any()));
        for (var i = 0; i < 20; i++) sent.Add(world.New());
        rig.Stream.Send([.. sent]);
        await rig.LandAsync([.. sent.Where((_, i) => i % 2 == 0)]);
        var early = Enumerable.Range(0, 5).Select(i => world.Make(sent[2 * i + 1].Id)).ToArray();
        await rig.LandAsync(early);
        await rig.RecheckedAsync(); // (the store's rows read: each key's row known, the counts exact)

        var views = Views();
        foreach (var shown in views)
        {
            var (view, truth) = (shown.View, Merged(shown));
            var (answer, rows) = await rig.ReadAsync(view, GridRead<long, OrderRow>.AtRank(0, 60)); // (new orders unlanded: an estimate)
            Assert(rows.SequenceEqual(truth.Take(60)) && answer.Total == truth.Length && answer.TotalKind == TotalKind.Estimated,
                $"the top merged ({Ids(rows)} for {Ids(truth)}; {answer.Total} of {truth.Length}, {answer.TotalKind}; {answer.Refusal})");
            (answer, rows) = await rig.ReadAsync(view, GridRead<long, OrderRow>.AtRank(25, 30));
            Assert(answer.FirstRank == 25 && rows.SequenceEqual(truth.Skip(25).Take(30)), $"from a rank ({answer.FirstRank}: {Ids(rows)})");
            var at = Math.Min(40, truth.Length - 1);
            var about = truth[at];
            (answer, rows) = await rig.ReadAsync(view, GridRead<long, OrderRow>.AroundRow(about.Id, about, 10, 20));
            Assert(answer.Anchor == Math.Min(10, at) && answer.FirstRank == at - answer.Anchor &&
                   rows.SequenceEqual(truth.Skip((int)answer.FirstRank).Take(answer.Anchor + 20)),
                $"about a row ({answer.FirstRank}, anchor {answer.Anchor}: {Ids(rows)})");
        }

        // A key the stream alone has, about it: found; filtered out: not.
        var fresh = sent[^1];
        var (found, near) = await rig.ReadAsync(GridView.Natural, GridRead<long, OrderRow>.AroundKey(fresh.Id, 2, 3));
        Assert(found.Anchor != GridAnswer.NotFound && near[found.Anchor] == fresh, "a key the stream alone has: found");
        var other = new GridView([], new ChalkFilter("status = @status", new Dictionary<string, object?> { ["status"] = (fresh.Status + 1) % 4 }));
        (found, _) = await rig.ReadAsync(other, GridRead<long, OrderRow>.AroundKey(fresh.Id, 2, 3));
        Assert(found.Anchor == GridAnswer.NotFound, "filtered out: not found");

        // What a view's filter keeps, told apart from the others': its two standing views' state, beside their budgets.
        var (_, filter) = source.MemoryOf(views[2].View);
        Assert(filter is { Held: > 0, Bound: 2 * (64L << 20) } && source.MemoryOf(new GridView([])) == default,
            $"a view's filter counted, its own ({filter}); one never read, none");

        // The store catches up, the stream sends what landed first; a new order a haircut on: the tail lets go.
        await rig.LandAsync([.. sent.Where((_, i) => i % 2 == 1)]);
        rig.Stream.Send(early);
        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        await source.GapFilled;
        var later = world.New();
        rig.Stream.Send(later);
        await rig.LandAsync(later);
        await rig.ReadAsync(GridView.Natural, GridRead<long, OrderRow>.AtRank(0, 10));
        Assert(source.TailRows == 1 && source.Watermark == later.Stamp, $"caught up: the tail its last version alone ({source.TailRows})");
        var (_, all) = await rig.ReadAsync(GridView.Natural, GridRead<long, OrderRow>.AtRank(0, 1_000));
        Assert(all.SequenceEqual(world.Truth(Views()[0])), "the whole view the truth");
        Console.WriteLine($"  merged: {rig.Store.Executed:N0} statements, {rig.Scopes.Open} views open");

        // The view as the merge shows it, the store's rows read: each key at the newest version either side holds — in the
        // view if that passes, or, the stream ahead of the store, if the store's row of it does (the blotter's rule).
        OrderRow[] Merged(Shown view)
        {
            return [.. world.Made.Values
                .Where(newest => view.Passes(newest) ||
                                 (rig.Landed.TryGetValue(newest.Id, out var landed) && landed < newest.Version && view.Passes(world.All[(newest.Id, landed)])))
                .OrderBy(r => r, view.Order)];
        }
    }

    public static async Task Oracle(PlannerProcess planner)
    {
        var checks = 0;
        for (var seed = 1; seed <= 3; seed++)
        {
            await using var rig = await Rig.StartAsync(planner, seed, new AppendOnlyStorePolicy { Haircut = TimeSpan.FromSeconds(2) });
            var world = rig.World;
            var random = new Random(seed * 7);
            await rig.LandAsync([.. Enumerable.Range(0, 300).Select(_ => world.New())]);
            rig.Clock.Advance(TimeSpan.FromSeconds(60));
            rig.Subscribe();
            var source = rig.Source!;
            rig.Clock.Advance(TimeSpan.FromSeconds(2));
            await source.GapFilled;
            var (unsent, unlanded) = (new List<OrderRow>(), new List<OrderRow>());
            var delivered = new Dictionary<long, int>();
            var frontier = long.MinValue;
            var stalled = false;
            var views = Views();
            for (var step = 0; step < 250; step++)
            {
                var roll = random.Next(100);
                if (roll < 25)
                {
                    for (var n = random.Next(1, 6); n > 0; n--)
                    {
                        var row = random.Next(3) == 0 ? world.New() : world.Make(world.Any());
                        unsent.Add(row);
                        unlanded.Add(row);
                    }
                }
                else if (roll < 45)
                {
                    if (unsent.Count == 0) continue;
                    var n = random.Next(1, unsent.Count + 1);
                    var batch = unsent.GetRange(0, n).ToArray();
                    unsent.RemoveRange(0, n);
                    foreach (var row in batch) delivered[row.Id] = Math.Max(delivered.GetValueOrDefault(row.Id), row.Version);
                    rig.Stream.Send(batch);
                }
                else if (roll < 60)
                {
                    if (stalled) continue;
                    frontier = Math.Max(frontier, world.Now - random.Next(0, 3_000_000));
                    var due = unlanded.Where(r => r.Stamp <= frontier && (r.Stamp <= frontier - 1_000_000 || random.Next(2) == 0)).ToList();
                    foreach (var row in due) unlanded.Remove(row);
                    await rig.LandAsync([.. due]);
                }
                else if (roll < 63)
                {
                    stalled = !stalled;
                }
                else if (roll < 75)
                {
                    rig.Clock.Advance(TimeSpan.FromMilliseconds(random.Next(50, 900)));
                }
                else
                {
                    var shown = views[random.Next(views.Length)];
                    var (view, order) = (shown.View, shown.Order);
                    var truth = world.Truth(shown);
                    var kind = random.Next(4);
                    var read = kind == 0 || truth.Length == 0 ? GridRead<long, OrderRow>.AtRank(0, random.Next(1, 40))
                        : kind == 1 ? GridRead<long, OrderRow>.AtRank(random.Next(truth.Length), random.Next(1, 40))
                        : kind == 2 ? truth[random.Next(truth.Length)] is var about
                            ? GridRead<long, OrderRow>.AroundRow(about.Id, about, random.Next(0, 20), random.Next(1, 30))
                            : default
                        : GridRead<long, OrderRow>.AroundKey(world.Any(), random.Next(0, 20), random.Next(1, 30));

                    // What the source can know: each key at the newer of the versions sent and landed — but those the store
                    // holds a version of the stream hasn't sent (left out on both sides).
                    var sentBefore = new Dictionary<long, int>(delivered);
                    var storeAhead = rig.Landed.Where(l => l.Value > delivered.GetValueOrDefault(l.Key)).Select(l => l.Key).ToHashSet();
                    var known = world.Made.Keys
                        .Select(id => (id, version: Math.Max(delivered.GetValueOrDefault(id), rig.Landed.GetValueOrDefault(id))))
                        .Where(k => k.version > 0 && !storeAhead.Contains(k.id))
                        .Select(k => world.All[(k.id, k.version)]).Where(shown.Passes).OrderBy(r => r, order).ToArray();
                    var (answer, rows) = await rig.ReadAsync(view, read);
                    var what = $"seed {seed}, step {step}, {read.Kind}";
                    Assert(answer.Refusal is null, $"{what}: served ({answer.Refusal})");
                    for (var i = 1; i < rows.Length; i++) Assert(order.Compare(rows[i - 1], rows[i]) < 0, $"{what}: in order ({Ids(rows)})");
                    Assert(rows.Select(r => r.Id).Distinct().Count() == rows.Length, $"{what}: no key twice");
                    // A row passes, or the view keeps it as the store's row of it last passed (the blotter's rule): a version of
                    // its key the store has landed, older than the row shown.
                    bool Lingers(OrderRow row)
                    {
                        for (var v = 1; v < row.Version && v <= rig.Landed.GetValueOrDefault(row.Id); v++)
                            if (world.All.TryGetValue((row.Id, v), out var was) && shown.Passes(was))
                                return true;
                        return false;
                    }

                    foreach (var row in rows)
                        Assert((shown.Passes(row) || Lingers(row)) && row.Version >= sentBefore.GetValueOrDefault(row.Id),
                            $"{what}: {row.Id}v{row.Version} passes (or the store's row of it did), not old");
                    if (read.Kind == GridReadKind.AtRank && read.Rank == 0)
                    {
                        var top = rows.Where(r => !storeAhead.Contains(r.Id) && shown.Passes(r)).ToArray(); // (those kept aside)
                        Assert(top.SequenceEqual(known.Take(top.Length)) && (rows.Length == read.Count || top.Length == known.Length),
                            $"{what}: the top what the source knows ({Ids(top)} for {Ids(known)})");
                    }
                    else if (read.Kind != GridReadKind.AtRank && !storeAhead.Contains(read.Key))
                    {
                        var place = read.Row;
                        var found = true;
                        if (read.Kind == GridReadKind.AroundKey)
                        {
                            var version = Math.Max(delivered.GetValueOrDefault(read.Key), rig.Landed.GetValueOrDefault(read.Key));
                            found = version > 0 && shown.Passes(place = world.All[(read.Key, version)]);
                            if (!found && version > 0 && Lingers(place)) found = answer.Anchor != GridAnswer.NotFound; // (the source's to know)
                            else Assert(found == (answer.Anchor != GridAnswer.NotFound), $"{what}: found as the source knows it");
                        }

                        if (found)
                        {
                            var at = known.Count(r => order.Compare(r, place) < 0);
                            var before = rows[..answer.Anchor].Where(r => !storeAhead.Contains(r.Id) && shown.Passes(r)).ToArray();
                            var after = rows[answer.Anchor..].Where(r => !storeAhead.Contains(r.Id) && shown.Passes(r)).ToArray();
                            Assert(before.SequenceEqual(known[(at - before.Length)..at]) && after.SequenceEqual(known.Skip(at).Take(after.Length)) &&
                                   (answer.Anchor == read.Before || before.Length == at) &&
                                   (rows.Length - answer.Anchor == read.Count || after.Length == known.Length - at),
                                $"{what}: about its place what the source knows ({Ids(before)} | {Ids(after)} at {at} of {Ids(known)})");
                        }
                    }

                    checks++;
                }
            }

            // Quiet: everything sent and landed, one more a haircut on — every page the truth's, at its ranks.
            rig.Stream.Send([.. unsent]);
            await rig.LandAsync([.. unlanded]);
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            var last = world.New();
            rig.Stream.Send(last);
            await rig.LandAsync(last);
            await rig.ReadAsync(GridView.Natural, GridRead<long, OrderRow>.AtRank(0, 5));
            foreach (var shown in views)
            {
                var truth = world.Truth(shown);
                var (answer, rows) = await rig.ReadAsync(shown.View, GridRead<long, OrderRow>.AtRank(0, 1_000));
                Assert(rows.SequenceEqual(truth) && answer.Total == truth.Length && answer.TotalKind == TotalKind.Exact,
                    $"seed {seed}, quiet: the view the truth ({rows.Length} rows, {answer.Total} counted, of {truth.Length})");
                for (var i = 0; i < 3 && truth.Length > 0; i++)
                {
                    var rank = random.Next(truth.Length);
                    (answer, rows) = await rig.ReadAsync(shown.View, GridRead<long, OrderRow>.AtRank(rank, 10));
                    Assert(answer.FirstRank == rank && rows.SequenceEqual(truth.Skip(rank).Take(10)), $"seed {seed}, quiet: from {rank}");
                    checks++;
                }
            }

            Assert(source.TailRows <= 1, $"seed {seed}, quiet: the tail let go ({source.TailRows})");
        }

        Console.WriteLine($"  oracle: {checks:N0} pages checked, exactly as the source can know them");
    }

    public static async Task Refusals(PlannerProcess planner)
    {
        await using var rig = await Rig.StartAsync(planner, 4);
        await rig.LandAsync([.. Enumerable.Range(0, 20).Select(_ => rig.World.New())]);
        rig.Subscribe();
        (GridView View, string Says)[] refused =
        [
            (new GridView([], new ChalkFilter("no_such_column = 1")), "no_such_column"),
            (new GridView([], "status = 1"), "ChalkFilter"),
            (new GridView([], new ChalkFilter("status = @grid_status", new Dictionary<string, object?> { ["grid_status"] = 1 })), "grid_"),
            (new GridView([new SortKey("nothing")]), "nothing")
        ];
        foreach (var (view, says) in refused)
        {
            var (answer, _) = await rig.ReadAsync(view, GridRead<long, OrderRow>.AtRank(0, 10));
            Assert(answer.Refusal?.Contains(says, StringComparison.OrdinalIgnoreCase) == true, $"refused, naming {says}: {answer.Refusal}");
        }

        await using var tight = await Rig.StartAsync(planner, 5, stateBudget: 64);
        await tight.LandAsync([.. Enumerable.Range(0, 20).Select(_ => tight.World.New())]);
        tight.Subscribe();
        tight.Stream.Send([.. Enumerable.Range(0, 50).Select(_ => tight.World.New())]);
        var (over, _) = await tight.ReadAsync(GridView.Natural, GridRead<long, OrderRow>.AtRank(0, 10));
        Assert(over.Refusal is not null, "a view past its state budget: refused");
        Console.WriteLine($"  past its budget: {over.Refusal}");

        // A read's memory budget (GridRead.MaxBytes): its statements run in an arena of it, ChalkQL counting memory as it's
        // taken — past it, the read refused, saying so; the same read in a budget it fits, served (from a warm arena after).
        await rig.LandAsync([.. Enumerable.Range(0, 5_000).Select(_ => rig.World.New())]);
        var sorted = new GridView([new SortKey("qty", true)]);
        var (small, none) = await rig.ReadAsync(sorted, GridRead<long, OrderRow>.AtRank(4_000, 10) with { MaxBytes = 64 << 10 });
        Assert(small.Refusal?.Contains("more memory than the read's budget, 64 KiB") == true && none.Length == 0,
            $"past its memory budget: refused ({small.Refusal})");
        for (var i = 0; i < 2; i++)
        {
            var (ample, rows) = await rig.ReadAsync(sorted, GridRead<long, OrderRow>.AtRank(4_000, 10) with { MaxBytes = 256u << 20 });
            Assert(ample.Refusal is null && rows.Length == 10, $"in a budget it fits: served ({ample.Refusal})");
        }

        Console.WriteLine($"  past its memory budget: {small.Refusal}");

        // Sizes without counting: past a page, the provider's estimate (its own knowledge of its source); with none, and
        // nothing counted, at least the rows seen; a view ending in its page, counted from it.
        GridView? asked = null;
        await using var estimating = await Rig.StartAsync(planner, 6, sizes: ChalkSizes.None,
            estimate: (view, _) => new ValueTask<(long, TotalKind)?>(((asked = view) == null ? 0 : 1_234, TotalKind.Estimated)));
        await estimating.LandAsync([.. Enumerable.Range(0, 500).Select(_ => estimating.World.New())]);
        estimating.Subscribe();
        var (estimated, _) = await estimating.ReadAsync(GridView.Natural, GridRead<long, OrderRow>.AtRank(0, 10));
        Assert(estimated is { Total: 1_234, TotalKind: TotalKind.Estimated } && asked == GridView.Natural,
            $"past a page: the provider's estimate ({estimated.Total}, {estimated.TotalKind})");
        var few = new GridView([], new ChalkFilter("id < @few", new Dictionary<string, object?> { ["few"] = 5L }));
        asked = null;
        var (counted, five) = await estimating.ReadAsync(few, GridRead<long, OrderRow>.AtRank(0, 10));
        Assert(counted is { Total: 5, TotalKind: TotalKind.Exact } && five.Length == 5 && asked is null,
            $"ending in its page: counted from it, the provider not asked ({counted.Total}, {counted.TotalKind})");
        await using var unsized = await Rig.StartAsync(planner, 7, sizes: ChalkSizes.None);
        await unsized.LandAsync([.. Enumerable.Range(0, 500).Select(_ => unsized.World.New())]);
        unsized.Subscribe();
        var (seen, _) = await unsized.ReadAsync(GridView.Natural, GridRead<long, OrderRow>.AtRank(0, 10));
        Assert(seen.TotalKind == TotalKind.AtLeast && seen.Total >= 10 && seen.Total < 500, $"no estimate, none counted: at least the rows seen ({seen.Total}, {seen.TotalKind})");
        Console.WriteLine($"  sizes: past a page the provider's {estimated.Total:N0}; none, at least {seen.Total:N0}; 5 counted from their page");
    }

    public static async Task Times(PlannerProcess planner)
    {
        // Forty orders landed, stamped a minute to twenty seconds ago, placed up to 39 minutes ago (not in the keys' order).
        var clock = new ManualClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = new ChalkRows<long, TimedRow, DateTime>(nameof(TimedRow.Id), nameof(TimedRow.Version), nameof(TimedRow.Stamp));
        TimedRow[] landed = [.. Enumerable.Range(0, 40).Select(i => new TimedRow
        {
            Id = i, Version = 1, Stamp = now.AddSeconds(i - 60), Placed = now.AddMinutes(-(i * 7 % 40))
        })];
        var tail = new ChalkTail<long, TimedRow>(rows);
        var db = new PocoSourceBuilder("db", "db")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("orders", landed, out _, t => t.Keyed(r => r.Id))
            .AddTable("order_versions", (TimedRow[])landed.Clone(), out _, t => t.Keyed(r => r.Id, r => r.Version))
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "grid-times", Sources = [db, tail.Source], Planner = planner.CreatePlanner()
        });
        var store = new ChalkStore<long, TimedRow, DateTime>(engine, rows, "db.orders", "db.order_versions", sizes: ChalkSizes.Count);
        var stream = new Sent<TimedRow>();
        var source = new StoreAndStream<long, TimedRow, DateTime>(store, stream, rows, new ChalkScopes<long, TimedRow>(engine, tail, rows), clock);
        try
        {
            // The gap fill, a haircut after the subscription: the versions since a stamp (half a minute before it), a time bound.
            clock.Advance(AppendOnlyStorePolicy.Default.Haircut);
            await source.GapFilled;
            Assert(source.GapFillFault is null, $"the gap fill read the versions since a stamp: {source.GapFillFault?.Message}");
            var newest = await store.NewestStampAsync(CancellationToken.None);
            Assert(newest == landed[^1].Stamp && newest.Value.Kind == DateTimeKind.Utc, $"the newest stamp, read back as UTC: {newest:O}");

            // Sorted by a time: a page from a rank, then one about a key — its place counted, and paged to, by the time bound.
            var byPlaced = new GridView([new SortKey("placed", true)]);
            TimedRow[] truth = [.. landed.OrderByDescending(r => r.Placed).ThenByDescending(r => r.Id)];
            var (top, first) = await ReadAsync(source, byPlaced, GridRead<long, TimedRow>.AtRank(0, 10));
            Assert(top.Refusal is null && first.Select(r => r.Id).SequenceEqual(truth.Take(10).Select(r => r.Id)),
                $"sorted by a time, from the top: {top.Refusal} {string.Join(",", first.Select(r => r.Id))}");
            var at = truth[25];
            var (about, around) = await ReadAsync(source, byPlaced, GridRead<long, TimedRow>.AroundKey(at.Id, 3, 8));
            Assert(about.Refusal is null && around.Select(r => r.Id).SequenceEqual(truth.Skip(22).Take(11).Select(r => r.Id)),
                $"sorted by a time, about a key: {about.Refusal} {string.Join(",", around.Select(r => r.Id))}");

            // Filtered by a time, bound as its wall clock: the store's rows and an order the stream alone has, passing.
            var since = DateTime.SpecifyKind(now.AddMinutes(-10), DateTimeKind.Unspecified);
            var recent = new GridView([], new ChalkFilter("placed >= @since", new Dictionary<string, object?> { ["since"] = since }));
            stream.Send(new TimedRow { Id = 100, Version = 1, Stamp = now, Placed = now });
            long[] passing = [100, .. landed.Where(r => r.Placed >= since).OrderByDescending(r => r.Id).Select(r => r.Id)];
            var (filtered, shown) = await ReadAsync(source, recent, GridRead<long, TimedRow>.AtRank(0, 40));
            Assert(filtered.Refusal is null && shown.Select(r => r.Id).SequenceEqual(passing),
                $"filtered by a time, over the store and the tail: {filtered.Refusal} {string.Join(",", shown.Select(r => r.Id))}");

            // A recheck: the versions since a haircut before the newest stamp, a time bound.
            var done = source.Rechecks;
            clock.Advance(AppendOnlyStorePolicy.Default.Recheck);
            await ReadAsync(source, GridView.Natural, GridRead<long, TimedRow>.AtRank(0, 1));
            for (var i = 0; source.Rechecks == done; i++)
            {
                if (i == 500) throw new TimeoutException("no recheck");
                await Task.Delay(10);
            }

            Assert(source.RecheckFault is null, $"a recheck read the versions since a stamp: {source.RecheckFault?.Message}");
        }
        finally
        {
            source.Dispose();
            await Task.Delay(50); // (the catch-up closing the views)
            store.Dispose();
        }

        static async Task<(GridAnswer Answer, TimedRow[] Rows)> ReadAsync(StoreAndStream<long, TimedRow, DateTime> source, GridView view,
            GridRead<long, TimedRow> read)
        {
            var page = new GridPage<long, TimedRow>(4_096);
            var answer = await source.ReadAsync(view, read, page, CancellationToken.None);
            return (answer, page.Rows.ToArray());
        }
    }

    public static async Task Immutable(PlannerProcess planner)
    {
        // Thirty trades landed, a minute ago and on; a table of them alone, their current rows and their versions both.
        var clock = new ManualClock();
        var micros = (clock.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks) / 10;
        var rows = new ChalkRows<long, TradeRow, long>(nameof(TradeRow.Id), null, nameof(TradeRow.Stamp));
        TradeRow[] landed = [.. Enumerable.Range(1, 30).Select(i => new TradeRow { Id = i, Stamp = micros + (i - 60) * 1_000_000L, Qty = i % 7 })];
        var tail = new ChalkTail<long, TradeRow>(rows);
        var db = new PocoSourceBuilder("db", "db")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("trades", landed, out var table, t => t.Keyed(r => r.Id))
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "grid-immutable", Sources = [db, tail.Source], Planner = planner.CreatePlanner()
        });
        var store = new ChalkStore<long, TradeRow, long>(engine, rows, "db.trades", "db.trades", sizes: ChalkSizes.Count);
        var stream = new Sent<TradeRow>();
        var source = new StoreAndStream<long, TradeRow, long>(store, stream, rows, new ChalkScopes<long, TradeRow>(engine, tail, rows), clock);
        try
        {
            clock.Advance(AppendOnlyStorePolicy.Default.Haircut);
            await source.GapFilled;

            // Five streamed ahead of the store: at the top, newest first, each once; the total the store's and theirs.
            TradeRow[] sent = [.. Enumerable.Range(31, 5).Select(i => new TradeRow { Id = i, Stamp = micros + i * 1_000L, Qty = i % 7 })];
            stream.Send(sent);
            long[] all = [.. Enumerable.Range(1, 35).Reverse().Select(i => (long)i)];
            var (top, shown) = await ReadAsync(source, GridView.Natural, GridRead<long, TradeRow>.AtRank(0, 50));
            Assert(top.Refusal is null && shown.Select(r => r.Id).SequenceEqual(all) && top.Total == 35,
                $"streamed ahead: at the top, each once ({top.Total}: {string.Join(",", shown.Select(r => r.Id))}; {top.Refusal})");

            // Sent again (a stream that delivers twice) and landed: still once each, the store's row and the stream's the same.
            stream.Send(sent[1], sent[3]);
            await engine.RefreshAsync(refresh => refresh.Upsert(table, sent));
            (top, shown) = await ReadAsync(source, GridView.Natural, GridRead<long, TradeRow>.AtRank(0, 50));
            Assert(shown.Select(r => r.Id).SequenceEqual(all) && top.Total == 35,
                $"sent again and landed: still each once ({top.Total}: {string.Join(",", shown.Select(r => r.Id))})");

            // Filtered: the store's rows and the tail's, by the same SQL.
            var some = new GridView([], new ChalkFilter("qty = @q", new Dictionary<string, object?> { ["q"] = 3 }));
            var (filtered, passing) = await ReadAsync(source, some, GridRead<long, TradeRow>.AtRank(0, 50));
            Assert(filtered.Refusal is null && passing.Select(r => r.Id).SequenceEqual(all.Where(i => i % 7 == 3)),
                $"filtered over store and tail: {string.Join(",", passing.Select(r => r.Id))} ({filtered.Refusal})");

            // The store past them by a haircut: the tail lets them go, the view as it was.
            clock.Advance(AppendOnlyStorePolicy.Default.Haircut + TimeSpan.FromSeconds(1));
            var later = new TradeRow { Id = 36, Stamp = micros + 70_000_000L, Qty = 1 };
            stream.Send(later);
            await engine.RefreshAsync(refresh => refresh.Upsert(table, [later]));
            clock.Advance(AppendOnlyStorePolicy.Default.Recheck);
            (top, shown) = await ReadAsync(source, GridView.Natural, GridRead<long, TradeRow>.AtRank(0, 50));
            Assert(shown.Select(r => r.Id).SequenceEqual([36L, .. all]) && source.TailRows <= 1,
                $"the store past them: the tail let go ({source.TailRows} left), each once ({string.Join(",", shown.Select(r => r.Id))})");
        }
        finally
        {
            source.Dispose();
            await Task.Delay(50); // (the catch-up closing the views)
            store.Dispose();
        }

        static async Task<(GridAnswer Answer, TradeRow[] Rows)> ReadAsync(StoreAndStream<long, TradeRow, long> source, GridView view,
            GridRead<long, TradeRow> read)
        {
            var page = new GridPage<long, TradeRow>(4_096);
            var answer = await source.ReadAsync(view, read, page, CancellationToken.None);
            return (answer, page.Rows.ToArray());
        }
    }
}
