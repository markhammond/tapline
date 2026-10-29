using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Tapline.Alerts;
using Tapline.Grid.Provider;

using LiveGrid.Sample.Alerts;

namespace LiveGrid.Sample.Domain;

/// <summary>
///     What the alerts hear and read, tab by tab: the stream its grid hears, the database its waits are proved against, and
///     what a row is.
/// </summary>
internal sealed record AlertSources(
    IGridStream<OrderRow> Orders,
    IGridStore<Guid, OrderRow, DateTime> OrderStore,
    IGridRows<Guid, OrderRow, DateTime> OrderRows,
    IGridStream<FillRow> Fills,
    IGridStore<Guid, FillRow, DateTime> FillStore,
    IGridRows<Guid, FillRow, DateTime> FillRows);

/// <summary>
///     ① Evaluation (virtual-grid.md §15.2) for the sample: the ChalkQL blotter's alert views, lifted out, hearing the grids'
///     streams as the grids do — a subscription of its own to each — and reading the database itself for its waits
///     (grid-waits.md). Each distinct condition on a tab is a ChalkQL standing view, maintained incrementally, over tables of
///     its own, kept from what the streams send and only while a shape reads them: <c>live.latest_orders</c>, each order at
///     the newest version heard, until unchanged for <see cref="OrdersFor" />; <c>live.arrived_fills</c>, each fill heard,
///     for <see cref="FillsFor" /> and the longest wait on fills. A row entering a view fires the shapes that fire on
///     arrival: once, keyed; an order again only after it has stopped passing.
/// </summary>
/// <remarks>
///     <para>
///         Nothing ticks. The tables are refreshed as rows are heard, no oftener than <see cref="RefreshEvery" /> (what's heard
///         meanwhile taken together); between, only deadlines run.
///     </para>
///     <para>
///         A wait is the database's: one <see cref="GridWaits{TKey,TRow,TStamp}" /> a tab, watching each distinct threshold,
///         proof and anchor its rules ask for. An order waits from the anchor of its oldest version heard that the database
///         hasn't been seen to hold until it holds the newest (a fill, from its own); told past a threshold, proved as its rule
///         asks, it fires the rules whose condition its newest version passes, once a wait: then, or as it comes to pass while
///         it still waits.
///     </para>
///     <para>
///         A count is kept here, in steps of a sixtieth of its window (a second at least) falling at whole multiples of the
///         step since the Unix epoch, a step counting while it ends within the window: the matches its condition's view sees,
///         as heard. At least so many fires as the match that makes them is heard; none, once the window holds no match,
///         judged only once a whole window has passed since counting began; each again only after it has stopped. A count's
///         one deadline — its oldest step leaving the window, or the window emptying — is all that runs between matches.
///     </para>
///     <para>
///         A fill's condition reads its order along a path, <c>order.status = 'Partial'</c>: a fill whose order hasn't
///         arrived yet enters its view when it does.
///     </para>
/// </remarks>
internal sealed class StandingAlerts : IAlertEvaluator
{
    /// <summary>How long a fill stays in <c>live.arrived_fills</c>: long enough for its order to arrive (and, past it, for the longest wait on fills).</summary>
    public static readonly TimeSpan FillsFor = TimeSpan.FromSeconds(10);

    /// <summary>How long an order that has not changed stays in <c>live.latest_orders</c>.</summary>
    public static readonly TimeSpan OrdersFor = TimeSpan.FromMinutes(10);

    /// <summary>The tables are refreshed as rows are heard, no oftener than this: what's heard meanwhile, taken together.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMilliseconds(50);

    private const long StateBudget = 64L << 20; // (a view's: the alerts' own, apart from a grid session's)

    // The waits' proofs: a lookup's memory at the database (1,024 keys' rows at most), the rest as GridWaits has it.
    private static readonly GridWaitsOptions WaitOptions = new() { MaxReadBytes = 8u << 20 };

    // The times a wait on orders may run from besides a version's own (Tabs.Orders' WaitsFrom), as the waits read them.
    private static readonly GridWaitAnchor<OrderRow, DateTime> OrderTime = (in OrderRow row) => row.OrderTime;

    private readonly PocoTable<FillRecord> _arrivedFills;
    private readonly List<CountShape> _counts = [];
    private readonly ChalkEngine _engine;
    private readonly Dictionary<Guid, FillRecord> _fills = [];
    private readonly Queue<(DateTime At, List<FillRecord> Fills)> _fillsKept = new();
    private readonly TabWaits<FillRow> _fillWaits;
    private readonly SemaphoreSlim _gate = new(1, 1); // (a turn of the loop, and a change of watches: one at a time)
    private readonly ConcurrentQueue<FillRow[]> _heardFills = new();
    private readonly ConcurrentQueue<OrderRow[]> _heardOrders = new();
    private readonly IDisposable _hearingFills, _hearingOrders;
    private readonly Dictionary<AlertWatch, List<AlertHit>> _hits = []; // (a turn's, by shape: each told once)
    private readonly PocoTable<OrderRecord> _latestOrders;
    private readonly Task _loop;
    private readonly Dictionary<Guid, (OrderRecord Order, DateTime At)> _orders = [];
    private readonly Queue<(DateTime At, Guid Id)> _ordersKept = new();
    private readonly TabWaits<OrderRow> _orderWaits;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly ConcurrentQueue<Told> _told = new();
    private readonly Dictionary<string, AlertView> _views = new(StringComparer.Ordinal);
    private int _disposed, _signaled;
    private int _failing; // refreshes failed in a row: said as it starts, each minute it goes on, and as it ends
    private DateTimeOffset _failingSaid;
    private Dictionary<string, AlertWatch[]> _enters = new(StringComparer.Ordinal); // a view's name → the shapes its entries fire
    private DateTime _refreshed = DateTime.MinValue;
    private IAlertSink? _sink;
    private volatile bool _watching; // (a view open: read on the streams' threads)

    private StandingAlerts(ChalkEngine engine, PocoTable<OrderRecord> latestOrders, PocoTable<FillRecord> arrivedFills, AlertSources sources,
        TimeProvider time)
    {
        (_engine, _latestOrders, _arrivedFills, _time) = (engine, latestOrders, arrivedFills, time);
        _orderWaits = new TabWaits<OrderRow>(this, sources.OrderStore, sources.OrderRows, from => from == "order_time" ? OrderTime : null,
            row => Order(OrderRecord.Of(row)), time);
        _fillWaits = new TabWaits<FillRow>(this, sources.FillStore, sources.FillRows, _ => null, row => Fill(FillRecord.Of(row)), time);
        _timer = time.CreateTimer(static state => ((StandingAlerts)state!).Wake(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _loop = Task.Run(() => LoopAsync(_stop.Token));
        _hearingOrders = sources.Orders.Subscribe(HeardOrders);
        _hearingFills = sources.Fills.Subscribe(HeardFills);
    }

    /// <summary>The views open.</summary>
    public int Views => _views.Count;

    /// <summary>The evaluator, on an engine of its own over its tables, on <paramref name="planner" />; hearing and reading <paramref name="sources" />.</summary>
    public static async Task<StandingAlerts> StartAsync(PlannerProcess planner, AlertSources sources, TimeProvider? time = null)
    {
        var live = new PocoSourceBuilder("live", "live")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("arrived_fills", (IReadOnlyList<FillRecord>)[], out var arrivedFills, t => t.Keyed(f => f.FillId))
            .AddTable("latest_orders", (IReadOnlyList<OrderRecord>)[], out var latestOrders, t => t.Keyed(o => o.OrderId))
            .Build();
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "livegrid-alerts", Sources = [live], Planner = planner.CreatePlanner() // (no clock: no view reads the time)
        });
        return new StandingAlerts(engine, latestOrders, arrivedFills, sources, time ?? TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _hearingOrders.Dispose();
        _hearingFills.Dispose();
        await _stop.CancelAsync();
        await _timer.DisposeAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        await _orderWaits.DisposeAsync();
        await _fillWaits.DisposeAsync();
        await _gate.WaitAsync();
        foreach (var view in _views.Values) await view.DisposeAsync();
        _views.Clear();
        await _engine.DisposeAsync();
    }

    /// <summary>
    ///     Null if the shape can be evaluated: its tab known, its condition one (names it knows, no statement), a wait's anchor
    ///     and proof ones its tab offers, and a view of it one ChalkQL makes. Else why not, without the planner's correlation id.
    /// </summary>
    public async ValueTask<string?> CheckAsync(AlertWatch watch, CancellationToken cancellationToken)
    {
        if (Spec(watch, out var refusal) is not { } spec) return refusal;
        try
        {
            await _engine.PrepareAsync(spec.Sql(), ct: cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception refused) when (refused is not OperationCanceledException)
        {
            return Readable(refused.Message);
        }
    }

    /// <summary>
    ///     The shapes to evaluate, from now: a view opened for each condition new, those no shape reads closed; each tab's waits
    ///     told the distinct watches its wait shapes ask for; a count begun now for each new, those kept as they were.
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<AlertWatch, string>> WatchAsync(IReadOnlyCollection<AlertWatch> watches, IAlertSink sink,
        CancellationToken cancellationToken)
    {
        var refused = new Dictionary<AlertWatch, string>();
        var specs = new Dictionary<string, AlertViewSpec>(StringComparer.Ordinal);
        var shapes = new List<(AlertWatch Watch, AlertViewSpec Spec)>();
        foreach (var watch in watches)
        {
            if (Spec(watch, out var why) is not { } spec)
            {
                refused[watch] = why!;
                continue;
            }

            specs.TryAdd(spec.Name, spec);
            shapes.Add((watch, spec));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _sink = sink;
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var name in _views.Keys.Where(n => !specs.ContainsKey(n)).ToList())
            {
                await _views[name].DisposeAsync().ConfigureAwait(false);
                _views.Remove(name);
            }

            foreach (var (name, spec) in specs.Where(s => !_views.ContainsKey(s.Key)))
                try
                {
                    _views[name] = await AlertView.CreateAsync(_engine, spec, StateBudget, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    foreach (var (watch, _) in shapes.Where(s => s.Spec.Name == name)) refused[watch] = Readable(failure.Message);
                }

            // The shapes whose views were made, by trigger: those firing as rows enter, the waits a tab's, the counts.
            var enters = new Dictionary<string, List<AlertWatch>>(StringComparer.Ordinal);
            List<(AlertWatch, string)> orderWaits = [], fillWaits = [];
            var counts = new List<CountShape>();
            foreach (var (watch, spec) in shapes.Where(s => !refused.ContainsKey(s.Watch)))
                switch (watch.Trigger)
                {
                    case AlertTrigger.Waiting:
                        (spec.Tab.Overrides ? orderWaits : fillWaits).Add((watch, spec.Name));
                        break;
                    case AlertTrigger.Counting counting:
                        counts.Add(_counts.Find(c => c.Watch == watch) ?? new CountShape(watch, counting, spec.Name, now));
                        break;
                    default:
                        (enters.TryGetValue(spec.Name, out var list) ? list : enters[spec.Name] = []).Add(watch);
                        break;
                }

            _enters = enters.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
            _counts.Clear();
            _counts.AddRange(counts);
            _orderWaits.Watch(orderWaits);
            _fillWaits.Watch(fillWaits);
            _watching = _views.Count > 0;
            if (!_watching) await ForgetAsync(cancellationToken).ConfigureAwait(false);
            Arm(now);
            return refused;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Versions the orders' stream sent, on its thread (what's done must be quick): the waits told at once — each waits
    ///     from its own anchor, however long till the tables are refreshed — and a copy kept for the next refresh.
    /// </summary>
    private void HeardOrders(ReadOnlySpan<OrderRow> versions)
    {
        if (!_watching || versions.IsEmpty) return;
        _orderWaits.Waits.Heard(versions);
        _heardOrders.Enqueue(versions.ToArray());
        Wake();
    }

    /// <summary>Fills the fills' stream sent: as the orders' versions are.</summary>
    private void HeardFills(ReadOnlySpan<FillRow> fills)
    {
        if (!_watching || fills.IsEmpty) return;
        _fillWaits.Waits.Heard(fills);
        _heardFills.Enqueue(fills.ToArray());
        Wake();
    }

    /// <summary>A turn of the loop asked for: once, until it begins.</summary>
    private void Wake()
    {
        if (Interlocked.Exchange(ref _signaled, 1) == 0) _signal.Release();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await _signal.WaitAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _signaled, 0);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await TurnAsync(ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (!ct.IsCancellationRequested) // (a cancellation of ChalkQL's own, a timeout: a failure)
            {
                Failed(failure);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    ///     One turn: what the waits told, fired where the conditions pass; the tables refreshed with what was heard, if a
    ///     refresh is due, and the views brought to them; the counts judged; the timer set for what comes next.
    /// </summary>
    private async Task TurnAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            TakeTold(now);
            if (Heard() && now - _refreshed >= RefreshEvery)
            {
                _refreshed = now;
                var changes = Collect(now);
                if (changes.Count > 0)
                    await _engine.RefreshAsync(refresh =>
                    {
                        foreach (var change in changes) change(refresh);
                    }, ct).ConfigureAwait(false);
                await UpdateAsync(now, ct).ConfigureAwait(false);
                Recovered();
            }

            foreach (var count in _counts)
                if (count.Judge(now) is { } line)
                    Hit(count.Watch, new AlertHit("", line));
        }
        finally
        {
            Tell();
            Arm(now);
        }
    }

    /// <summary>
    ///     A refresh failed, to be tried again with the next rows heard: said the first time, then once a minute while they
    ///     go on failing (a planner gone fails every one).
    /// </summary>
    private void Failed(Exception failure)
    {
        var now = _time.GetUtcNow();
        if (_failing++ > 0 && now - _failingSaid < TimeSpan.FromMinutes(1)) return;
        _failingSaid = now;
        Console.Error.WriteLine(_failing == 1
            ? $"The alerts' refresh failed, to be tried again: {failure.Message}"
            : $"The alerts' refreshes are still failing, {_failing:N0} in a row: {failure.Message}");
    }

    /// <summary>A refresh done: if those before it failed, said how many.</summary>
    private void Recovered()
    {
        if (_failing == 0) return;
        Console.Error.WriteLine($"The alerts' refreshes are working again, after {_failing:N0} failed.");
        _failing = 0;
    }

    private bool Heard()
    {
        return !_heardOrders.IsEmpty || !_heardFills.IsEmpty;
    }

    /// <summary>The timer set for the next refresh, if rows were heard, or the soonest count's deadline: whichever comes first.</summary>
    private void Arm(DateTime now)
    {
        DateTime? next = Heard() ? _refreshed + RefreshEvery : null;
        foreach (var count in _counts)
            if (count.Deadline(now) is { } due && (next is null || due < next))
                next = due;
        if (next is not { } at)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var wait = at - now;
        _timer.Change(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>What the waits told: each wait past a threshold noted on its watch's shapes, fired where its condition passes now; each ended, forgotten.</summary>
    private void TakeTold(DateTime now)
    {
        while (_told.TryDequeue(out var told))
            foreach (var shape in told.Waits.Shapes)
            {
                if (told.Line is null)
                {
                    shape.Overdue.Remove(told.Key);
                    shape.Fired.Remove(told.Key);
                }
                else if (shape.Grid.Equals(told.Watch))
                {
                    shape.Overdue[told.Key] = (told.Waited, now, told.Line);
                    if (_views.TryGetValue(shape.View, out var view) && view.Holds(told.Key) && shape.Fired.Add(told.Key))
                        Hit(shape.Watch, new AlertHit(told.Key.ToString(), Waited(told.Line, told.Waited)));
                }
            }
    }

    /// <summary>
    ///     What the tables gain and lose since the last refresh: what was heard, and what's old enough to go — a fill once its
    ///     order could have arrived and the longest wait on fills has passed, an order unchanged a while.
    /// </summary>
    private List<Action<RefreshBuilder>> Collect(DateTime now)
    {
        List<OrderRow[]> orders = [];
        while (_heardOrders.TryDequeue(out var batch)) orders.Add(batch);
        List<FillRow[]> fills = [];
        while (_heardFills.TryDequeue(out var batch)) fills.Add(batch);
        if (!_watching) return []; // (heard as the last view closed, the tables emptied: not kept)
        Dictionary<Guid, OrderRecord> newest = [];
        List<FillRecord> newFills = [], goneFills = [];
        List<OrderRecord> goneOrders = [];
        foreach (var batch in orders)
        foreach (var version in batch)
            if ((!newest.TryGetValue(version.OrderId, out var was) || was.Version < version.Version) &&
                (!_orders.TryGetValue(version.OrderId, out var kept) || kept.Order.Version < version.Version))
                newest[version.OrderId] = OrderRecord.Of(version);
        foreach (var batch in fills)
        foreach (var fill in batch)
            if (!_fills.ContainsKey(fill.FillId) && FillRecord.Of(fill) is var record)
            {
                _fills[record.FillId] = record;
                newFills.Add(record);
            }

        if (newFills.Count > 0) _fillsKept.Enqueue((now, newFills));
        foreach (var record in newest.Values)
        {
            _orders[record.OrderId] = (record, now);
            _ordersKept.Enqueue((now, record.OrderId));
        }

        var fillsFor = FillsFor + _fillWaits.Longest;
        while (_fillsKept.TryPeek(out var oldest) && oldest.At < now - fillsFor)
            foreach (var fill in _fillsKept.Dequeue().Fills.Where(f => _fills.Remove(f.FillId)))
                goneFills.Add(fill);
        while (_ordersKept.TryPeek(out var oldest) && oldest.At < now - OrdersFor)
        {
            _ordersKept.Dequeue();
            if (_orders.TryGetValue(oldest.Id, out var kept) && kept.At == oldest.At && _orders.Remove(oldest.Id)) goneOrders.Add(kept.Order);
        }

        List<Action<RefreshBuilder>> changes = [];
        if (goneFills.Count > 0) changes.Add(r => r.Delete(_arrivedFills, goneFills));
        if (newFills.Count > 0) changes.Add(r => r.Upsert(_arrivedFills, newFills));
        if (goneOrders.Count > 0) changes.Add(r => r.Delete(_latestOrders, goneOrders));
        if (newest.Count > 0) changes.Add(r => r.Upsert(_latestOrders, [.. newest.Values]));
        return changes;
    }

    /// <summary>The tables emptied, no view reading them: what was heard, and all they kept.</summary>
    private async Task ForgetAsync(CancellationToken ct)
    {
        _heardOrders.Clear();
        _heardFills.Clear();
        List<OrderRecord> orders = [.. _orders.Values.Select(o => o.Order)];
        List<FillRecord> fills = [.. _fills.Values];
        _orders.Clear();
        _fills.Clear();
        _ordersKept.Clear();
        _fillsKept.Clear();
        if (orders.Count > 0 || fills.Count > 0)
            await _engine.RefreshAsync(refresh =>
            {
                if (orders.Count > 0) refresh.Delete(_latestOrders, orders);
                if (fills.Count > 0) refresh.Delete(_arrivedFills, fills);
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Every view brought to the refresh: what entered each, fired, by the shapes firing on arrival and by the waits told
    ///     past their thresholds that it now passes; its matches counted by the counts that read it.
    /// </summary>
    private async Task UpdateAsync(DateTime now, CancellationToken ct)
    {
        foreach (var view in _views.Values.ToList())
        {
            AlertViewUpdate update;
            try
            {
                update = await view.UpdateAsync(ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                Console.Error.WriteLine($"An alert view failed, and was closed: {failure.Message}");
                _views.Remove(view.Spec.Name);
                await view.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            if (update.Matches > 0)
                foreach (var count in _counts)
                    if (count.View == view.Spec.Name)
                        count.Add(now, update.Matches);
            if (update.Entered.Count == 0) continue;
            if (_enters.TryGetValue(view.Spec.Name, out var watches))
                foreach (var id in update.Entered)
                    if (Describe(view.Spec, id) is { } summary)
                        foreach (var watch in watches)
                            Hit(watch, new AlertHit(id.ToString(), summary));
            foreach (var shape in (view.Spec.Tab.Overrides ? _orderWaits : (TabWaits)_fillWaits).Shapes)
                if (shape.View == view.Spec.Name)
                    foreach (var id in update.Entered)
                        if (shape.Overdue.TryGetValue(id, out var told) && shape.Fired.Add(id))
                            Hit(shape.Watch, new AlertHit(id.ToString(), Waited(told.Line, told.Waited + (now - told.At))));
        }
    }

    private void Hit(AlertWatch watch, AlertHit hit)
    {
        if (!_hits.TryGetValue(watch, out var hits)) _hits[watch] = hits = [];
        hits.Add(hit);
    }

    /// <summary>What the turn fired, told: each shape's rows at once.</summary>
    private void Tell()
    {
        foreach (var (watch, hits) in _hits)
        {
            if (hits.Count > 0) _sink?.Fired(watch, CollectionsMarshal.AsSpan(hits));
            hits.Clear();
        }
    }

    /// <summary>A row that entered a view, in a line, as the blotter describes one.</summary>
    private string? Describe(AlertViewSpec spec, Guid id)
    {
        return spec.Tab.Overrides
            ? _orders.TryGetValue(id, out var kept) ? Order(kept.Order) : null
            : _fills.TryGetValue(id, out var fill) ? Fill(fill) : null;
    }

    private static string Order(OrderRecord o)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{o.Client} {o.Side} {o.Qty:#,0.####} {o.Instrument} · {o.Status}");
    }

    private static string Fill(FillRecord f)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{f.Client} {f.Side} {f.Qty:#,0.####} {f.Instrument} @ {f.Price:#,0.######}");
    }

    private static string Waited(string line, TimeSpan waited)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{line} · {waited.TotalSeconds:0.0} s waiting for the database");
    }

    /// <summary>A shape's view, or why there's none: its tab, its condition as the tab reads it, and a wait's anchor and proof.</summary>
    private static AlertViewSpec? Spec(AlertWatch watch, out string? refusal)
    {
        refusal = null;
        if (Tabs.Of(watch.Source) is not { } tab)
        {
            refusal = $"there's no tab '{watch.Source}' to watch";
            return null;
        }

        var condition = Condition.Parse(tab, watch.Condition);
        if (!condition.Errors.IsEmpty)
        {
            refusal = condition.Errors[0].Message;
            return null;
        }

        if (condition.Paths.FirstOrDefault(p => !p.Target.Overrides) is { } path)
        {
            refusal = $"a path to {path.Target.Title} reads the orders the house sent, which {path.Target.Title} are not";
            return null;
        }

        if (watch.Trigger is AlertTrigger.Waiting waiting)
        {
            if (tab.WaitsFrom.Count == 0) refusal = $"{tab.Title} rows aren't waited for";
            else if (waiting.From is { } from && tab.WaitsFrom.All(a => a.Column != from))
                refusal = $"a wait on {tab.Title} runs from {string.Join(" or ", tab.WaitsFrom.Select(a => a.Column ?? "its own time"))}, not '{from}'";
            else if (!tab.WaitProofs.Contains(waiting.Proof))
                refusal = $"a wait on {tab.Title} is {string.Join(" or ", tab.WaitProofs.Select(p => AlertChoices.Proof(p).Label))}, not {waiting.Proof}";
            if (refusal is not null) return null;
        }

        return new AlertViewSpec(tab, condition);
    }

    /// <summary>
    ///     What ChalkQL said, as a reader of the condition needs it: no correlation id, no list of all the parser expected, no
    ///     code, and no place in the view's statement, which isn't the condition as written.
    /// </summary>
    private static string Readable(string message)
    {
        foreach (var tail in (string[])[" [correlation id", "\nWas expecting"])
            if (message.IndexOf(tail, StringComparison.Ordinal) is var at and >= 0)
                message = message[..at];
        message = Regex.Replace(message, @"\s*\[[A-Za-z]+\]\s*$", "");
        return Regex.Replace(message, @" at line \d+, column \d+", "").Trim();
    }

    /// <summary>A wait past a threshold — its watch, how long it had waited, its newest version in a line — or, with no line, one ended.</summary>
    private readonly record struct Told(TabWaits Waits, Guid Key, object? Watch, TimeSpan Waited, string? Line);

    /// <summary>
    ///     A wait's shape: its rule's watch, its condition's view, and its watch as the waits keep it; the keys told past its
    ///     threshold this wait (how long each had waited, when told, and its newest version in a line), and those it fired for.
    /// </summary>
    private sealed class WaitShape(AlertWatch watch, string view, object grid)
    {
        public AlertWatch Watch => watch;

        public string View => view;

        /// <summary>Its watch as its tab's waits keep it: a <see cref="GridWaitWatch{TRow,TStamp}" />, equal to the one told.</summary>
        public object Grid { get; set; } = grid;

        public readonly Dictionary<Guid, (TimeSpan Waited, DateTime At, string Line)> Overdue = [];

        public readonly HashSet<Guid> Fired = [];
    }

    /// <summary>A tab's waits for the database, as the turns read them: the wait shapes on it, and the longest wait in force (none: zero).</summary>
    private abstract class TabWaits
    {
        public WaitShape[] Shapes { get; protected set; } = [];

        public TimeSpan Longest { get; protected set; }
    }

    /// <summary>
    ///     A tab's waits: its <see cref="GridWaits{TKey,TRow,TStamp}" />, told the distinct watches its wait shapes ask for, and
    ///     what it tells queued for the next turn.
    /// </summary>
    private sealed class TabWaits<TRow> : TabWaits, IGridWaitSink<Guid, TRow, DateTime>, IAsyncDisposable where TRow : unmanaged
    {
        private readonly StandingAlerts _alerts;
        private readonly Func<string?, GridWaitAnchor<TRow, DateTime>?> _anchor;
        private readonly Func<TRow, string> _line;

        public TabWaits(StandingAlerts alerts, IGridStore<Guid, TRow, DateTime> store, IGridRows<Guid, TRow, DateTime> rows,
            Func<string?, GridWaitAnchor<TRow, DateTime>?> anchor, Func<TRow, string> line, TimeProvider time)
        {
            (_alerts, _anchor, _line) = (alerts, anchor, line);
            Waits = new GridWaits<Guid, TRow, DateTime>(store, rows, this, WaitOptions, time);
        }

        public GridWaits<Guid, TRow, DateTime> Waits { get; }

        public ValueTask DisposeAsync()
        {
            return Waits.DisposeAsync();
        }

        /// <summary>The wait shapes in force, from now: one kept keeps what it was told; the waits keep the distinct watches.</summary>
        public void Watch(IReadOnlyList<(AlertWatch Watch, string View)> shapes)
        {
            var watches = new List<GridWaitWatch<TRow, DateTime>>();
            var kept = new WaitShape[shapes.Count];
            for (var i = 0; i < shapes.Count; i++)
            {
                var (watch, view) = shapes[i];
                var waiting = (AlertTrigger.Waiting)watch.Trigger;
                var grid = new GridWaitWatch<TRow, DateTime>(waiting.Over,
                    waiting.Proof == AlertWaitProof.Trusted ? GridWaitProof.Trusted : GridWaitProof.Confirmed, _anchor(waiting.From));
                if (!watches.Contains(grid)) watches.Add(grid);
                kept[i] = Array.Find(Shapes, s => s.Watch == watch) ?? new WaitShape(watch, view, grid);
                kept[i].Grid = grid;
            }

            Shapes = kept;
            Longest = watches.Count == 0 ? TimeSpan.Zero : watches.Max(w => w.Over);
            Waits.SetWatches(CollectionsMarshal.AsSpan(watches));
        }

        void IGridWaitSink<Guid, TRow, DateTime>.Overdue(ReadOnlySpan<GridOverdue<Guid, TRow, DateTime>> overdue)
        {
            foreach (ref readonly var told in overdue) _alerts._told.Enqueue(new Told(this, told.Key, told.Watch, told.Waited, _line(told.Newest)));
            _alerts.Wake();
        }

        void IGridWaitSink<Guid, TRow, DateTime>.Ended(ReadOnlySpan<GridWaitEnded<Guid>> ended)
        {
            foreach (ref readonly var end in ended) _alerts._told.Enqueue(new Told(this, end.Key, null, default, null));
            _alerts.Wake();
        }

        void IGridWaitSink<Guid, TRow, DateTime>.Untracked(long keys)
        {
            Console.Error.WriteLine($"The alerts' waits are at their cap: {keys:N0} more rows heard not waited for (GridWaitsOptions.MaxWaits).");
        }
    }

    /// <summary>
    ///     A count's shape: the matches its condition's view sees, as heard, by step of a sixtieth of its window (a second at
    ///     least) falling at whole multiples of the step since the Unix epoch, a step counting while it ends within the
    ///     window; at least so many, or none, and whether that's been fired for and still holds.
    /// </summary>
    private sealed class CountShape
    {
        private readonly long _judgedFrom; // (ticks since the epoch: none, judged a whole window after counting began)
        private readonly long[] _matches, _steps; // a ring: each slot's matches, and its step (since the epoch; MinValue: none)
        private readonly long _step, _window; // (ticks)
        private bool _holds; // fired, and still so: at least, enough; none, no match since

        public CountShape(AlertWatch watch, AlertTrigger.Counting count, string view, DateTime began)
        {
            (Watch, Count, View) = (watch, count, view);
            _step = TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(count.Window.TotalSeconds / 60))).Ticks;
            _window = count.Window.Ticks;
            var slots = (int)((_window + _step - 1) / _step) + 1; // (every step that can end within the window, apart)
            _steps = new long[slots];
            Array.Fill(_steps, long.MinValue);
            _matches = new long[slots];
            _judgedFrom = Ticks(began) + _window;
        }

        public AlertWatch Watch { get; }

        public AlertTrigger.Counting Count { get; }

        /// <summary>The view whose matches it counts.</summary>
        public string View { get; }

        /// <summary>Matches heard: counted in the step now falls in; a count of none watching for one again.</summary>
        public void Add(DateTime now, int matches)
        {
            var step = Ticks(now) / _step;
            var slot = (int)(step % _steps.Length);
            if (_steps[slot] != step) (_steps[slot], _matches[slot]) = (step, 0);
            _matches[slot] += matches;
            if (Count.Kind == AlertCountKind.None) _holds = false;
        }

        /// <summary>
        ///     Its line, if it fires now: at least so many, as they come to be (and, fallen below, ready again); none, once the
        ///     window holds no match and a whole window has passed since counting began.
        /// </summary>
        public string? Judge(DateTime now)
        {
            var at = Ticks(now);
            var matched = 0L;
            for (var i = 0; i < _steps.Length; i++)
                if (Within(_steps[i], at))
                    matched += _matches[i];
            if (Count.Kind == AlertCountKind.AtLeast)
            {
                if (matched < Count.Matches)
                {
                    _holds = false; // (fallen below: it fires again once there are enough)
                    return null;
                }

                if (_holds) return null;
                _holds = true;
                return string.Create(CultureInfo.InvariantCulture, $"{matched:N0} in the last {AlertChoices.Span(Count.Window)}");
            }

            if (matched > 0 || _holds || at < _judgedFrom) return null;
            _holds = true;
            return $"none in the last {AlertChoices.Span(Count.Window)}";
        }

        /// <summary>
        ///     When it next needs judging if nothing more is heard: at least, while it holds, the oldest step within the window
        ///     leaving it; none, until it has fired, the newest leaving (no sooner than a whole window from its start). Null:
        ///     only a match changes it.
        /// </summary>
        public DateTime? Deadline(DateTime now)
        {
            var at = Ticks(now);
            long oldest = long.MaxValue, newest = long.MinValue;
            for (var i = 0; i < _steps.Length; i++)
                if (Within(_steps[i], at) && _matches[i] > 0)
                    (oldest, newest) = (Math.Min(oldest, _steps[i]), Math.Max(newest, _steps[i]));
            long? due = Count.Kind == AlertCountKind.AtLeast
                ? _holds && oldest != long.MaxValue ? Leaves(oldest) : null
                : _holds ? null : Math.Max(_judgedFrom, newest == long.MinValue ? 0 : Leaves(newest));
            return due is { } ticks ? DateTime.UnixEpoch.AddTicks(ticks) : null;
        }

        /// <summary>Whether a step counts at <paramref name="at" />: it ends within the window.</summary>
        private bool Within(long step, long at)
        {
            return step != long.MinValue && Leaves(step) > at;
        }

        /// <summary>When a step leaves the window: a window after it ends.</summary>
        private long Leaves(long step)
        {
            return (step + 1) * _step + _window;
        }

        private static long Ticks(DateTime time)
        {
            return time.Ticks - DateTime.UnixEpoch.Ticks;
        }
    }
}

/// <summary>What one alert view watches: a condition on a tab's rows, as they arrive. Shapes alike in tab and condition share one.</summary>
internal sealed record AlertViewSpec(Tab Tab, Condition Condition)
{
    /// <summary>What a view is shared by.</summary>
    public string Name => Tab.Name + "\n" + Condition.Text;

    /// <summary>
    ///     The view's statement, over the evaluator's tables only: a fill is read from <c>live.arrived_fills</c>, an order from
    ///     <c>live.latest_orders</c>, each order once at the newest version heard. A path joins <c>live.latest_orders</c>, so a
    ///     fill whose order hasn't arrived yet enters the view when it does.
    /// </summary>
    public string Sql()
    {
        var sql = new StringBuilder($"SELECT a.{Tab.Key.Name} AS id");
        // An order's version, so that each newer version that passes is a match a count sees.
        sql.Append(Tab.Overrides ? ", a.version AS version" : ", CAST(0 AS BIGINT) AS version");
        sql.Append($"\nFROM {(Tab.Overrides ? "live.latest_orders" : "live.arrived_fills")} AS a");
        foreach (var path in Condition.Paths)
            sql.Append($"\nLEFT JOIN live.latest_orders AS p_{path.Name} ON p_{path.Name}.{path.Target.Key.Name} = a.{path.Via}");
        sql.Append("\nWHERE (").Append(Condition.Sql(c => "a." + c.Name, (p, c) => $"p_{p.Name}.{c.Name}")).Append(')');
        return sql.ToString();
    }
}

/// <summary>What an update of an alert view found: the rows that entered it, and how many matches, entries and newer versions alike.</summary>
internal readonly record struct AlertViewUpdate(IReadOnlyList<Guid> Entered, int Matches);

/// <summary>
///     One alert view: a ChalkQL standing view, maintained incrementally, of the rows that pass a condition now. A row that
///     enters it fires the shapes that fire on arrival; one already in it, changing, doesn't, though a newer version of an
///     order in it is a match for the counts; one that leaves it may fire them again when it comes back. What it held when it
///     was made is taken as it stands: a shape starts from now.
/// </summary>
internal sealed class AlertView : IAsyncDisposable, IViewSink<Guid, long>
{
    private readonly List<Guid> _entered = [];
    private readonly Dictionary<Guid, long> _present = [];
    private readonly StandingView<Guid, long> _view;
    private ViewCursor _cursor = ViewCursor.Start;
    private int _matches;
    private bool _started;

    private AlertView(AlertViewSpec spec, StandingView<Guid, long> view)
    {
        Spec = spec;
        _view = view;
    }

    public AlertViewSpec Spec { get; }

    public ValueTask DisposeAsync()
    {
        return _view.DisposeAsync();
    }

    void IViewSink<Guid, long>.Reset()
    {
        _present.Clear();
    }

    void IViewSink<Guid, long>.Upsert(in Guid key, in long version)
    {
        var entered = _present.TryAdd(key, version);
        var newer = !entered && version > _present[key];
        if (newer) _present[key] = version;
        if (_started && entered) _entered.Add(key);
        if (_started && (entered || newer)) _matches++;
    }

    void IViewSink<Guid, long>.Delete(in Guid key)
    {
        _present.Remove(key);
    }

    void IViewSink<Guid, long>.Commit(long version, uint checksum)
    {
    }

    /// <summary>Whether the row passes the condition now, as the view last had it.</summary>
    public bool Holds(Guid id)
    {
        return _present.ContainsKey(id);
    }

    public static async Task<AlertView> CreateAsync(ChalkEngine engine, AlertViewSpec spec, long budget, CancellationToken ct)
    {
        var view = await engine.MaterializeAsync(spec.Sql(), new ViewOptions<Guid, long>
        {
            Key = ["id"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Map = Map,
            StateBudget = budget,
            Maintenance = ViewMaintenance.Incremental,
            ResetWhen = double.PositiveInfinity // (drained after every update: never far enough behind for a reset to pay)
        }, ct).ConfigureAwait(false);
        var made = new AlertView(spec, view);
        await made.UpdateAsync(ct).ConfigureAwait(false);
        made._started = true;
        return made;
    }

    /// <summary>Brings the view to the engine's latest epoch, and says which rows entered it and how many matches it saw.</summary>
    public async Task<AlertViewUpdate> UpdateAsync(CancellationToken ct)
    {
        _entered.Clear();
        _matches = 0;
        await _view.UpdateAsync(ct).ConfigureAwait(false);
        while (_view.Drain(ref _cursor, this, 65536) == DrainOutcome.Partial)
        {
        }

        return new AlertViewUpdate([.. _entered], _matches);
    }

    private static void Map(in ViewRow row, out Guid key, out long version)
    {
        key = (Guid)row.GetValue(0)!;
        version = Convert.ToInt64(row.GetValue(1), CultureInfo.InvariantCulture);
    }
}
