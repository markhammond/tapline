using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Tapline.Alerts;

using LiveGrid.Sample.Alerts;

namespace LiveGrid.Sample.Domain;

/// <summary>How long a row has waited for the database, as <c>live.waits</c> holds it: until it landed, or for good.</summary>
/// <remarks>In microseconds, as the tables are: a wait with no end runs to 9999, which nanoseconds, the default, do not reach.</remarks>
public sealed record Wait(
    Guid Id,
    [property: ChalkColumn(Precision = 6)] DateTime Since,
    [property: ChalkColumn(Precision = 6)] DateTime Until);

/// <summary>How many matches a counter saw in one slice of time, as <c>live.match_counts</c> holds it.</summary>
public sealed record MatchCount(int Counter, [property: ChalkColumn(Precision = 6)] DateTime SliceStart, long Matched);

/// <summary>
///     ① Evaluation (virtual-grid.md §15.2) for the sample: the ChalkQL blotter's alert views, lifted out. Each distinct shape
///     — a tab, a condition in SQL and a trigger — is a ChalkQL standing view, maintained incrementally, over tables of its
///     own, kept from the house's batches as the ledger hears them and lands them (<see cref="Ledger.Heard" />,
///     <see cref="Ledger.Landed" />), and only while a shape reads them: <c>live.arrived_fills</c>, every fill heard in the
///     last <see cref="FillsFor" />; <c>live.latest_orders</c>, each order at the newest version heard, until unchanged for
///     <see cref="OrdersFor" />; <c>live.waiting_orders</c> and <c>live.waiting_fills</c> beside <c>live.waits</c>, the rows
///     the database hasn't landed and since when they've waited; <c>live.match_counts</c>, what each view a count reads
///     matched, by slice of time. A row entering a view fires its shapes — once, keyed; an order again only after it has
///     stopped passing.
/// </summary>
/// <remarks>
///     An engine of its own, on the sample's planner, its clock (<c>@now</c>) moved by every refresh — four a second while a
///     view is open. As the blotter's tail has it, an order waits from the time of the oldest version heard that the database
///     hasn't landed (a fill, from its own) until the database lands its newest, or till now. The ledger lands writes out of
///     order: a newer version landing ends an older's wait, and an older landing alone moves the wait on to the next. A wait
///     fires once a row that passes has waited longer than its rule allows: by the clock, or, once landed, by when it did — a
///     landed row keeps its wait <see cref="LandedFor" />, so the views see how long it took. A count fires as the matches in
///     its window come to be enough, or none (judged only once a whole window has passed since counting began), and again
///     only after that has stopped. A fill's condition reads its order along a path, <c>order.status = 'Partial'</c>: a fill
///     whose order hasn't arrived yet enters its view when it does.
/// </remarks>
internal sealed class StandingAlerts : IAlertEvaluator
{
    /// <summary>How long a fill stays in <c>live.arrived_fills</c>: long enough for its order to arrive.</summary>
    public static readonly TimeSpan FillsFor = TimeSpan.FromSeconds(10);

    /// <summary>How long an order that has not changed stays in <c>live.latest_orders</c>.</summary>
    public static readonly TimeSpan OrdersFor = TimeSpan.FromMinutes(10);

    /// <summary>How long a row the database has landed keeps its wait: as long as the blotter's tail keeps one.</summary>
    public static readonly TimeSpan LandedFor = TimeSpan.FromSeconds(5);

    /// <summary>How often the tables are refreshed and the views brought to them, while a view is open.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(250);

    // Until it lands, a wait has no end.
    private static readonly DateTime Never = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);
    private const long StateBudget = 64L << 20; // (a view's: the alerts' own, apart from a grid session's)

    private readonly PocoTable<FillRecord> _arrivedFills, _waitingFills;
    private readonly Dictionary<(string Matcher, TimeSpan Slice), Counter> _counters = [];
    private readonly Dictionary<string, CountView> _counts = new(StringComparer.Ordinal);
    private readonly List<MatchCount> _countsGone = [];
    private readonly ChalkEngine _engine;
    private readonly Dictionary<Guid, FillRecord> _fills = [];
    private readonly Queue<(DateTime At, List<FillRecord> Fills)> _fillsKept = new();
    private readonly Dictionary<Guid, FillWait> _fillWaits = [];
    private readonly SemaphoreSlim _gate = new(1, 1); // (the loop, and a change of watches: one at a time)
    private readonly ConcurrentQueue<(OrderRow[] Versions, (OrderRow Order, Fill Fill)[] Fills)> _heard = new();
    private readonly ConcurrentQueue<(OrderRow[] Versions, (OrderRow Order, Fill Fill)[] Fills, DateTime At)> _landed = new();
    private readonly PocoTable<OrderRecord> _latestOrders, _waitingOrders;
    private readonly Ledger _ledger;
    private readonly Task _loop;
    private readonly PocoTable<MatchCount> _matchCounts;
    private readonly Dictionary<Guid, (OrderRecord Order, DateTime At)> _orders = [];
    private readonly Queue<(DateTime At, Guid Id)> _ordersKept = new();
    private readonly Dictionary<Guid, OrderWait> _orderWaits = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, AlertView> _views = new(StringComparer.Ordinal);
    private readonly PocoTable<Wait> _waits;
    private DateTimeOffset _clock = DateTimeOffset.MinValue;
    private Dictionary<string, AlertWatch[]> _fires = new(StringComparer.Ordinal); // a view's name → the shapes its entries fire
    private int _lastCounter;
    private int _disposed;
    private IAlertSink? _sink;
    private volatile bool _watching, _waiting; // (a view open; one that waits: read on the house's thread and the ledger's)

    private StandingAlerts(ChalkEngine engine, PocoTable<FillRecord> arrivedFills, PocoTable<OrderRecord> latestOrders,
        PocoTable<OrderRecord> waitingOrders, PocoTable<FillRecord> waitingFills, PocoTable<Wait> waits, PocoTable<MatchCount> matchCounts,
        Ledger ledger, TimeProvider time)
    {
        (_engine, _arrivedFills, _latestOrders, _waitingOrders, _waitingFills, _waits, _matchCounts, _ledger, _time) =
            (engine, arrivedFills, latestOrders, waitingOrders, waitingFills, waits, matchCounts, ledger, time);
        ledger.Heard = Heard;
        ledger.Landed = Landed;
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>The views open, count views among them.</summary>
    public int Views => _views.Count + _counts.Count;

    /// <summary>The evaluator, on an engine of its own over its tables, on <paramref name="planner" />; told by <paramref name="ledger" />.</summary>
    public static async Task<StandingAlerts> StartAsync(PlannerProcess planner, Ledger ledger, TimeProvider? time = null)
    {
        var live = new PocoSourceBuilder("live", "live")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("arrived_fills", (IReadOnlyList<FillRecord>)[], out var arrivedFills, t => t.Keyed(f => f.FillId))
            .AddTable("latest_orders", (IReadOnlyList<OrderRecord>)[], out var latestOrders, t => t.Keyed(o => o.OrderId))
            .AddTable("waiting_orders", (IReadOnlyList<OrderRecord>)[], out var waitingOrders, t => t.Keyed(o => o.OrderId))
            .AddTable("waiting_fills", (IReadOnlyList<FillRecord>)[], out var waitingFills, t => t.Keyed(f => f.FillId))
            .AddTable("waits", (IReadOnlyList<Wait>)[], out var waits, t => t.Keyed(w => w.Id))
            .AddTable("match_counts", (IReadOnlyList<MatchCount>)[], out var matchCounts, t => t.Keyed(c => c.Counter, c => c.SliceStart))
            .Build();
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "livegrid-alerts", Sources = [live], Planner = planner.CreatePlanner(),
            Clock = "now" // (a wait's view, and a count's, read the time as @now: the clock every refresh moves)
        });
        return new StandingAlerts(engine, arrivedFills, latestOrders, waitingOrders, waitingFills, waits, matchCounts, ledger,
            time ?? TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        (_ledger.Heard, _ledger.Landed) = (null, null);
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        await _gate.WaitAsync();
        foreach (var view in _counts.Values) await view.DisposeAsync();
        foreach (var view in _views.Values) await view.DisposeAsync();
        _counts.Clear();
        _views.Clear();
        await _engine.DisposeAsync();
    }

    /// <summary>
    ///     Null if the shape can be evaluated: its tab known, its condition one (names it knows, no statement), and a view of
    ///     it one ChalkQL makes. Else why not, without the planner's correlation id.
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
    ///     The shapes to evaluate, from now: a view opened for each new (a count's beside the view whose matches it counts),
    ///     those no shape has closed; a counter for each width of slice a count reads, which begins counting now.
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<AlertWatch, string>> WatchAsync(IReadOnlyCollection<AlertWatch> watches, IAlertSink sink,
        CancellationToken cancellationToken)
    {
        var refused = new Dictionary<AlertWatch, string>();
        var specs = new Dictionary<string, AlertViewSpec>(StringComparer.Ordinal);
        var windows = new Dictionary<string, (AlertWindowSpec Spec, AlertWatch Watch)>(StringComparer.Ordinal);
        var fires = new Dictionary<string, List<AlertWatch>>(StringComparer.Ordinal);
        foreach (var watch in watches)
        {
            if (Spec(watch, out var why) is not { } spec)
            {
                refused[watch] = why!;
                continue;
            }

            specs.TryAdd(spec.Name, spec);
            if (watch.Trigger is AlertTrigger.Counting counting)
            {
                var window = new AlertWindowSpec(spec, counting);
                windows[window.Name] = (window, watch);
            }
            else
            {
                (fires.TryGetValue(spec.Name, out var list) ? list : fires[spec.Name] = []).Add(watch);
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _sink = sink;
            // The clock where it is now: a view that reads it (a wait's, a count's) can't be made till it's set, and starts from it.
            var now = _time.GetUtcNow().UtcDateTime;
            await _engine.RefreshAsync(refresh => refresh.AdvanceClock(Tick(now)), cancellationToken).ConfigureAwait(false);
            foreach (var name in _counts.Keys.Where(n => !windows.ContainsKey(n)).ToList())
            {
                await _counts[name].DisposeAsync().ConfigureAwait(false);
                _counts.Remove(name);
            }

            foreach (var name in _views.Keys.Where(n => !specs.ContainsKey(n)).ToList())
            {
                await _views[name].DisposeAsync().ConfigureAwait(false);
                _views.Remove(name);
            }

            // Counters no window reads go, their slices with them; each kept is kept for its longest window.
            foreach (var (key, counter) in _counters.Where(c => !windows.Values.Any(w => (w.Spec.Matcher.Name, w.Spec.Slice) == c.Key)).ToList())
            {
                _counters.Remove(key);
                _countsGone.AddRange(counter.Slices.Select(s => new MatchCount(counter.Id, s.Key, s.Value)));
            }

            foreach (var (name, spec) in specs.Where(s => !_views.ContainsKey(s.Key)))
                try
                {
                    _views[name] = await AlertView.CreateAsync(_engine, spec, StateBudget, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    foreach (var watch in watches.Where(w => Spec(w, out _)?.Name == name)) refused[watch] = Readable(failure.Message);
                }

            foreach (var (name, (window, watch)) in windows)
            {
                if (!_views.ContainsKey(window.Matcher.Name))
                {
                    refused.TryAdd(watch, "the view whose matches it counts couldn't be made");
                    continue;
                }

                if (!_counters.TryGetValue((window.Matcher.Name, window.Slice), out var counter))
                    _counters[(window.Matcher.Name, window.Slice)] = counter = new Counter(++_lastCounter, window.Matcher.Name, window.Slice, now);
                counter.KeepFor = windows.Values.Where(w => (w.Spec.Matcher.Name, w.Spec.Slice) == (counter.Matcher, counter.Slice))
                    .Max(w => w.Spec.Count.Window) + counter.Slice;
                if (_counts.ContainsKey(name)) continue;
                try
                {
                    _counts[name] = await CountView.CreateAsync(_engine, window, watch, counter.Id, Micros(counter.Began + window.Count.Window),
                        StateBudget, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    refused[watch] = Readable(failure.Message);
                }
            }

            _fires = fires.ToDictionary(f => f.Key, f => f.Value.ToArray(), StringComparer.Ordinal);
            Watched();
            return refused;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The ledger's batch, heard: noted, for the next refresh (on the house's thread, under its lock: a copy, and no more).</summary>
    private void Heard(ReadOnlySpan<OrderRow> versions, ReadOnlySpan<(OrderRow Order, Fill Fill)> fills)
    {
        if (_watching) _heard.Enqueue((versions.ToArray(), fills.ToArray()));
    }

    /// <summary>The ledger's batch, landed: the waits it ends, for the next refresh.</summary>
    private void Landed(OrderRow[] versions, (OrderRow Order, Fill Fill)[] fills)
    {
        if (_waiting) _landed.Enqueue((versions, fills, _time.GetUtcNow().UtcDateTime));
    }

    /// <summary>Which tables the views open read: any view, the arrivals; a wait's, the waits.</summary>
    private void Watched()
    {
        _watching = _views.Count > 0 || _counts.Count > 0;
        _waiting = _views.Values.Any(v => v.Spec.LagOver is not null);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Every, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = _time.GetUtcNow().UtcDateTime;
                var changes = Collect(now);
                // Nothing new, and no view reads the clock: nothing to refresh.
                if (changes.Count == 0 && _counts.Count == 0 && !_waiting) continue;
                var clock = Tick(now);
                await _engine.RefreshAsync(refresh =>
                {
                    foreach (var change in changes) change(refresh);
                    refresh.AdvanceClock(clock);
                }, ct).ConfigureAwait(false);
                await UpdateAsync(now, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                Console.Error.WriteLine($"The alerts' refresh failed, to be tried again: {failure.Message}");
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>The engine's clock moved to <paramref name="now" />: forward only, by a tick at least, however the wall clock steps.</summary>
    private DateTimeOffset Tick(DateTime now)
    {
        var clock = new DateTimeOffset(now, TimeSpan.Zero); // (UTC, said so: an unmarked time is taken at the machine's offset)
        return _clock = clock > _clock ? clock : _clock.AddTicks(1);
    }

    /// <summary>
    ///     What the tables gain and lose since the last refresh: what was heard, what landed, what's old enough to go, the counts —
    ///     or, while no view reads them, all they kept.
    /// </summary>
    private List<Action<RefreshBuilder>> Collect(DateTime now)
    {
        // The landings first, then what was heard: a batch is heard before it can land, so every landing taken has its batch heard.
        List<(OrderRow[] Versions, (OrderRow Order, Fill Fill)[] Fills, DateTime At)> landings = [];
        while (_landed.TryDequeue(out var landing)) landings.Add(landing);
        List<(OrderRow[] Versions, (OrderRow Order, Fill Fill)[] Fills)> heard = [];
        while (_heard.TryDequeue(out var batch)) heard.Add(batch);
        var (watching, waiting) = (_watching, _waiting);
        if (!watching) heard.Clear(); // (told as the last view closed: not kept)
        if (!waiting) landings.Clear();

        List<FillRecord> newFills = [], goneFills = [], fillsWaiting = [], fillsDone = [];
        Dictionary<Guid, OrderRecord> newest = [];
        List<OrderRecord> goneOrders = [], ordersWaiting = [], ordersDone = [];
        List<Wait> waitsSet = [], waitsGone = [];
        HashSet<OrderWait> ordersTold = [];
        HashSet<FillWait> fillsTold = [];
        foreach (var (versions, fills) in heard)
        {
            foreach (var version in versions)
            {
                var record = OrderRecord.Of(version);
                if ((!newest.TryGetValue(record.OrderId, out var was) || was.Version < record.Version) &&
                    (!_orders.TryGetValue(record.OrderId, out var kept) || kept.Order.Version < record.Version))
                    newest[record.OrderId] = record;
                if (!waiting) continue;
                if (!_orderWaits.TryGetValue(record.OrderId, out var wait)) _orderWaits[record.OrderId] = wait = new OrderWait(record);
                if (wait.Heard(record)) ordersTold.Add(wait);
            }

            foreach (var (order, fill) in fills)
            {
                var record = FillRecord.Of(order, fill, order.ClOrdId.ToString());
                if (_fills.TryAdd(record.FillId, record)) newFills.Add(record);
                if (waiting && !_fillWaits.ContainsKey(record.FillId)) fillsTold.Add(_fillWaits[record.FillId] = new FillWait(record));
            }
        }

        if (newFills.Count > 0) _fillsKept.Enqueue((now, newFills));
        foreach (var record in newest.Values)
        {
            _orders[record.OrderId] = (record, now);
            _ordersKept.Enqueue((now, record.OrderId));
        }

        // The waits: those heard and landed, settled; those landed a while, gone.
        foreach (var (versions, fills, at) in landings)
        {
            foreach (var version in versions)
                if (_orderWaits.TryGetValue(version.OrderId, out var wait) && wait.Landed(version.Version, at))
                    ordersTold.Add(wait);
            foreach (var (_, fill) in fills)
                if (_fillWaits.TryGetValue(fill.Id, out var wait) && wait.Landed(at))
                    fillsTold.Add(wait);
        }

        foreach (var wait in ordersTold)
        {
            if (wait.Settle(now) is { } shown) waitsSet.Add(shown);
            if (!wait.Newer) continue;
            ordersWaiting.Add(wait.Order);
            wait.Newer = false;
        }

        foreach (var wait in fillsTold)
        {
            if (wait.Settle(now) is { } shown) waitsSet.Add(shown);
            if (wait.Told) continue;
            fillsWaiting.Add(wait.Fill);
            wait.Told = true;
        }

        foreach (var (id, wait) in _orderWaits.Where(w => !waiting || w.Value.GoesAt <= now).ToList())
        {
            _orderWaits.Remove(id);
            ordersDone.Add(wait.Order);
            if (wait.Shown is { } shown) waitsGone.Add(shown);
        }

        foreach (var (id, wait) in _fillWaits.Where(w => !waiting || w.Value.GoesAt <= now).ToList())
        {
            _fillWaits.Remove(id);
            fillsDone.Add(wait.Fill);
            if (wait.Shown is { } shown) waitsGone.Add(shown);
        }

        // The arrivals: fills heard a while ago, orders unchanged a while, gone — or all, while no view reads them.
        while (_fillsKept.TryPeek(out var oldest) && (!watching || oldest.At < now - FillsFor))
            foreach (var fill in _fillsKept.Dequeue().Fills.Where(f => _fills.Remove(f.FillId)))
                goneFills.Add(fill);
        while (_ordersKept.TryPeek(out var oldest) && (!watching || oldest.At < now - OrdersFor))
        {
            _ordersKept.Dequeue();
            if (_orders.TryGetValue(oldest.Id, out var kept) && (!watching || kept.At == oldest.At) && _orders.Remove(oldest.Id))
                goneOrders.Add(kept.Order);
        }

        // The counts: what the views counted since the last refresh, and the slices no window reads any more.
        List<MatchCount> countsSet = [], countsGone = [.. _countsGone];
        _countsGone.Clear();
        foreach (var counter in _counters.Values)
        {
            countsSet.AddRange(counter.Changed.Select(slice => new MatchCount(counter.Id, slice, counter.Slices[slice])));
            counter.Changed.Clear();
            foreach (var slice in counter.Slices.Keys.Where(slice => slice < Micros(now - counter.KeepFor)).ToList())
            {
                countsGone.Add(new MatchCount(counter.Id, slice, counter.Slices[slice]));
                counter.Slices.Remove(slice);
            }
        }

        List<Action<RefreshBuilder>> changes = [];
        if (goneFills.Count > 0) changes.Add(r => r.Delete(_arrivedFills, goneFills));
        if (newFills.Count > 0) changes.Add(r => r.Upsert(_arrivedFills, newFills));
        if (goneOrders.Count > 0) changes.Add(r => r.Delete(_latestOrders, goneOrders));
        if (newest.Count > 0) changes.Add(r => r.Upsert(_latestOrders, [.. newest.Values]));
        if (ordersDone.Count > 0) changes.Add(r => r.Delete(_waitingOrders, ordersDone));
        if (ordersWaiting.Count > 0) changes.Add(r => r.Upsert(_waitingOrders, ordersWaiting));
        if (fillsDone.Count > 0) changes.Add(r => r.Delete(_waitingFills, fillsDone));
        if (fillsWaiting.Count > 0) changes.Add(r => r.Upsert(_waitingFills, fillsWaiting));
        if (waitsGone.Count > 0) changes.Add(r => r.Delete(_waits, waitsGone));
        if (waitsSet.Count > 0) changes.Add(r => r.Upsert(_waits, waitsSet));
        if (countsGone.Count > 0) changes.Add(r => r.Delete(_matchCounts, countsGone));
        if (countsSet.Count > 0) changes.Add(r => r.Upsert(_matchCounts, countsSet));
        return changes;
    }

    /// <summary>Every view brought to the refresh: what entered each, fired; the matches counted, for the next; each count's row entering, fired.</summary>
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
                Watched();
                continue;
            }

            if (update.Matches > 0)
                foreach (var counter in _counters.Values.Where(c => c.Matcher == view.Spec.Name))
                    counter.Add(now, update.Matches);
            if (update.Entered.Count == 0 || !_fires.TryGetValue(view.Spec.Name, out var watches)) continue;
            var hits = new List<AlertHit>(update.Entered.Count);
            foreach (var key in update.Entered)
                if (Describe(view.Spec, key, now) is { } summary)
                    hits.Add(new AlertHit(key.Id.ToString(), summary));
            if (hits.Count == 0) continue;
            foreach (var watch in watches) _sink?.Fired(watch, CollectionsMarshal.AsSpan(hits));
        }

        foreach (var view in _counts.Values.ToList())
        {
            long? matches;
            try
            {
                matches = await view.UpdateAsync(ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                Console.Error.WriteLine($"A count view failed, and was closed: {failure.Message}");
                _counts.Remove(view.Spec.Name);
                await view.DisposeAsync().ConfigureAwait(false);
                Watched();
                continue;
            }

            if (matches is not { } inWindow) continue;
            var count = view.Spec.Count;
            var line = count.Kind == AlertCountKind.AtLeast
                ? string.Create(CultureInfo.InvariantCulture, $"{inWindow:N0} in the last {AlertChoices.Span(count.Window)}")
                : $"none in the last {AlertChoices.Span(count.Window)}";
            _sink?.Fired(view.Watch, [new AlertHit("", line)]);
        }
    }

    /// <summary>A row that entered a view, in a line, as the blotter describes one: and, a wait's, how long it waited.</summary>
    private string? Describe(AlertViewSpec spec, AlertKey key, DateTime now)
    {
        if (spec.LagOver is null)
            return spec.Tab.Overrides
                ? _orders.TryGetValue(key.Id, out var kept) ? Order(kept.Order) : null
                : _fills.TryGetValue(key.Id, out var fill) ? Fill(fill) : null;
        var (line, shown) = spec.Tab.Overrides
            ? _orderWaits.TryGetValue(key.Id, out var order) ? (Order(order.Order), order.Shown) : (null, null)
            : _fillWaits.TryGetValue(key.Id, out var waiting) ? (Fill(waiting.Fill), waiting.Shown) : (null, null);
        if (line is null || shown is null) return line;
        var landed = shown.Until != Never;
        var waited = ((landed ? shown.Until : Micros(now)) - key.Since).TotalSeconds;
        return landed
            ? string.Create(CultureInfo.InvariantCulture, $"{line} · landed after {waited:0.0} s")
            : string.Create(CultureInfo.InvariantCulture, $"{line} · {waited:0.0} s waiting for the database");

        static string Order(OrderRecord o)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{o.Client} {o.Side} {o.Qty:#,0.####} {o.Instrument} · {o.Status}");
        }

        static string Fill(FillRecord f)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{f.Client} {f.Side} {f.Qty:#,0.####} {f.Instrument} @ {f.Price:#,0.######}");
        }
    }

    /// <summary>A shape's view, or why there's none: its tab, and its condition as the tab reads it.</summary>
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

        return new AlertViewSpec(tab, condition, watch.Trigger is AlertTrigger.Waiting waiting ? waiting.Over : null);
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

    /// <summary>A time as the tables hold it: to the microsecond, the UTC wall clock.</summary>
    private static DateTime Micros(DateTime time)
    {
        return new DateTime(time.Ticks - time.Ticks % 10, DateTimeKind.Unspecified);
    }

    /// <summary>
    ///     An order the database hasn't landed at its newest version heard, or landed within <see cref="LandedFor" />: that
    ///     version, the times of those it hasn't landed, and its wait as <c>live.waits</c> has it.
    /// </summary>
    private sealed class OrderWait(OrderRecord order)
    {
        private readonly List<(int Version, DateTime Time)> _pending = [];
        private int _landed;
        private DateTime _landedAt;

        /// <summary>Its newest version heard: what a condition reads.</summary>
        public OrderRecord Order { get; private set; } = order;

        /// <summary>Whether <see cref="Order" /> is newer than <c>live.waiting_orders</c> has it.</summary>
        public bool Newer { get; set; } = true;

        /// <summary>Its wait, as <c>live.waits</c> has it (none yet: null).</summary>
        public Wait? Shown { get; private set; }

        /// <summary>Landed, when it goes; null while it waits.</summary>
        public DateTime? GoesAt { get; private set; }

        /// <summary>A version heard; false if the database has a newer already.</summary>
        public bool Heard(OrderRecord version)
        {
            if (version.Version <= _landed) return false;
            if (version.Version > Order.Version)
            {
                Order = version;
                Newer = true;
            }

            _pending.Add((version.Version, Micros(version.EventTime)));
            return true;
        }

        /// <summary>The database has landed <paramref name="version" /> at <paramref name="at" />: those before it, it has too.</summary>
        public bool Landed(int version, DateTime at)
        {
            if (version <= _landed) return false;
            (_landed, _landedAt) = (version, at);
            return true;
        }

        /// <summary>
        ///     Its wait, if it isn't as <c>live.waits</c> has it: from the time of the oldest version not landed; all landed,
        ///     ended as the last did, to go after <see cref="LandedFor" />.
        /// </summary>
        public Wait? Settle(DateTime now)
        {
            DateTime? since = null;
            foreach (var (version, time) in _pending)
                if (version > _landed && (since is null || time < since))
                    since = time;
            Wait wait;
            if (since is { } waiting)
            {
                _pending.RemoveAll(p => p.Version <= _landed);
                if (Shown is { } shown && shown.Since == waiting && shown.Until == Never) return null;
                (wait, GoesAt) = (new Wait(Order.OrderId, waiting, Never), null);
            }
            else
            {
                if (Shown is { } ended && ended.Until != Never) return null;
                // (heard and landed between two refreshes: it waited from its oldest)
                var began = Shown?.Since ?? (_pending.Count > 0 ? _pending.Min(p => p.Time) : Micros(_landedAt));
                _pending.Clear();
                (wait, GoesAt) = (new Wait(Order.OrderId, began, Micros(_landedAt)), now + LandedFor);
            }

            return Shown = wait;
        }
    }

    /// <summary>A fill the database hasn't landed, or landed within <see cref="LandedFor" />: and its wait, from its own time.</summary>
    private sealed class FillWait(FillRecord fill)
    {
        private DateTime? _landedAt;

        public FillRecord Fill => fill;

        /// <summary>Whether <c>live.waiting_fills</c> has it.</summary>
        public bool Told { get; set; }

        /// <summary>Its wait, as <c>live.waits</c> has it (none yet: null).</summary>
        public Wait? Shown { get; private set; }

        /// <summary>Landed, when it goes; null while it waits.</summary>
        public DateTime? GoesAt { get; private set; }

        public bool Landed(DateTime at)
        {
            if (_landedAt is not null) return false;
            _landedAt = at;
            return true;
        }

        /// <summary>Its wait, if it isn't as <c>live.waits</c> has it: till now, or ended as it landed, to go after <see cref="LandedFor" />.</summary>
        public Wait? Settle(DateTime now)
        {
            var until = _landedAt is { } at ? Micros(at) : Never;
            if (Shown is { } shown && shown.Until == until) return null;
            if (_landedAt is not null) GoesAt = now + LandedFor;
            return Shown = new Wait(fill.FillId, Micros(fill.EventTime), until);
        }
    }

    /// <summary>
    ///     The matches one alert view saw, by slice of one width, since it began: what count views of that view and width
    ///     read, through <c>live.match_counts</c>.
    /// </summary>
    private sealed class Counter(int id, string matcher, TimeSpan slice, DateTime began)
    {
        public int Id => id;

        public string Matcher => matcher;

        public TimeSpan Slice => slice;

        public DateTime Began => began;

        /// <summary>How long a slice is kept: the longest window that reads it, and a slice more.</summary>
        public TimeSpan KeepFor { get; set; }

        /// <summary>The matches in each slice, by when it starts, in the tables' wall-clock time.</summary>
        public Dictionary<DateTime, long> Slices { get; } = [];

        /// <summary>The slices counted since the last refresh.</summary>
        public HashSet<DateTime> Changed { get; } = [];

        public void Add(DateTime at, int matches)
        {
            var start = Micros(new DateTime(at.Ticks - at.Ticks % slice.Ticks, DateTimeKind.Utc));
            Slices[start] = Slices.GetValueOrDefault(start) + matches;
            Changed.Add(start);
        }
    }
}

/// <summary>
///     What one alert view watches: a condition on a tab's rows, and either what arrives or how long a row waits for the
///     database. Shapes alike share one view.
/// </summary>
internal sealed record AlertViewSpec(Tab Tab, Condition Condition, TimeSpan? LagOver)
{
    /// <summary>What a view is shared by.</summary>
    public string Name => Tab.Name + "\n" + Condition.Text +
                          (LagOver is { } lag ? "\nlag " + lag.TotalSeconds.ToString(CultureInfo.InvariantCulture) : "");

    /// <summary>
    ///     The view's statement, over the evaluator's tables only. On arrival, a fill is read from <c>live.arrived_fills</c>,
    ///     an order from <c>live.latest_orders</c>, each order once at the newest version sent. On the wait, a row is read
    ///     from those waiting, beside its wait in <c>live.waits</c>, and passes once it has waited longer than the rule
    ///     allows by the engine's clock: for good, if the database had it only after that. A path joins
    ///     <c>live.latest_orders</c>, so a fill whose order hasn't arrived yet enters the view when it does.
    /// </summary>
    public string Sql()
    {
        var table = (Tab.Overrides, LagOver is null) switch
        {
            (true, true) => "live.latest_orders",
            (true, false) => "live.waiting_orders",
            (false, true) => "live.arrived_fills",
            (false, false) => "live.waiting_fills"
        };
        var sql = new StringBuilder($"SELECT a.{Tab.Key.Name} AS id");
        sql.Append(LagOver is null ? ", TIMESTAMP '2000-01-01 00:00:00' AS since" : ", w.since AS since");
        // An order's version, so that each newer version that passes is a match a count sees.
        sql.Append(Tab.Overrides && LagOver is null ? ", a.version AS version" : ", CAST(0 AS BIGINT) AS version");
        sql.Append($"\nFROM {table} AS a");
        if (LagOver is not null) sql.Append($"\nJOIN live.waits AS w ON w.id = a.{Tab.Key.Name}");
        foreach (var path in Condition.Paths)
            sql.Append($"\nLEFT JOIN live.latest_orders AS p_{path.Name} ON p_{path.Name}.{path.Target.Key.Name} = a.{path.Via}");
        sql.Append("\nWHERE (").Append(Condition.Sql(c => "a." + c.Name, (p, c) => $"p_{p.Name}.{c.Name}")).Append(')');
        if (LagOver is { } over)
        {
            // Waited longer than allowed: by the clock, or, once the database had it, by when it did.
            var interval = string.Create(CultureInfo.InvariantCulture, $"INTERVAL '{over.TotalSeconds:0}' SECOND");
            sql.Append($"\n  AND w.since < @now - {interval} AND w.since < w.until - {interval}");
        }

        return sql.ToString();
    }
}

/// <summary>A row in an alert view: its key, and, for a wait, when the wait began; and an order's version, which is no part of the key.</summary>
internal struct AlertKey : IEquatable<AlertKey>
{
    public Guid Id;
    public DateTime Since;
    public long Version;

    public readonly bool Equals(AlertKey other)
    {
        return Id == other.Id && Since == other.Since;
    }

    public readonly override bool Equals(object? obj)
    {
        return obj is AlertKey other && Equals(other);
    }

    public readonly override int GetHashCode()
    {
        return HashCode.Combine(Id, Since);
    }
}

/// <summary>What an update of an alert view found: the rows that entered it, and how many matches, entries and newer versions alike.</summary>
internal readonly record struct AlertViewUpdate(IReadOnlyList<AlertKey> Entered, int Matches);

/// <summary>
///     One alert view: a ChalkQL standing view, maintained incrementally, of the rows that pass a condition now. A row that
///     enters it fires the shapes that share it; one already in it, changing, doesn't, though a newer version of an order in
///     it is a match for the counts; one that leaves it may fire them again when it comes back. What it held when it was
///     made is taken as it stands: a shape starts from now.
/// </summary>
internal sealed class AlertView : IAsyncDisposable, IViewSink<AlertKey, AlertKey>
{
    private readonly List<AlertKey> _entered = [];
    private readonly Dictionary<AlertKey, long> _present = [];
    private readonly StandingView<AlertKey, AlertKey> _view;
    private ViewCursor _cursor = ViewCursor.Start;
    private int _matches;
    private bool _started;

    private AlertView(AlertViewSpec spec, StandingView<AlertKey, AlertKey> view)
    {
        Spec = spec;
        _view = view;
    }

    public AlertViewSpec Spec { get; }

    public ValueTask DisposeAsync()
    {
        return _view.DisposeAsync();
    }

    void IViewSink<AlertKey, AlertKey>.Reset()
    {
        _present.Clear();
    }

    void IViewSink<AlertKey, AlertKey>.Upsert(in AlertKey key, in AlertKey row)
    {
        var entered = _present.TryAdd(key, row.Version);
        var newer = !entered && row.Version > _present[key];
        if (newer) _present[key] = row.Version;
        if (_started && entered) _entered.Add(key);
        if (_started && (entered || newer)) _matches++;
    }

    void IViewSink<AlertKey, AlertKey>.Delete(in AlertKey key)
    {
        _present.Remove(key);
    }

    void IViewSink<AlertKey, AlertKey>.Commit(long version, uint checksum)
    {
    }

    public static async Task<AlertView> CreateAsync(ChalkEngine engine, AlertViewSpec spec, long budget, CancellationToken ct)
    {
        var view = await engine.MaterializeAsync(spec.Sql(), new ViewOptions<AlertKey, AlertKey>
        {
            Key = ["id", "since"],
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

    private static void Map(in ViewRow row, out AlertKey key, out AlertKey value)
    {
        key = new AlertKey { Id = (Guid)row.GetValue(0)!, Since = (DateTime)row.GetValue(1)! };
        value = key with { Version = Convert.ToInt64(row.GetValue(2), CultureInfo.InvariantCulture) };
    }
}

/// <summary>
///     What one count view watches: the matches of an alert view in a sliding window, at least so many or none. Shapes alike
///     share one view.
/// </summary>
internal sealed record AlertWindowSpec(AlertViewSpec Matcher, AlertTrigger.Counting Count)
{
    /// <summary>What a view is shared by.</summary>
    public string Name => Matcher.Name + "\n" + (Count.Kind == AlertCountKind.AtLeast ? $"at least {Count.Matches}" : "none") + " in " +
                          AlertChoices.Span(Count.Window);

    /// <summary>
    ///     How finely matches are counted: a sixtieth of the window, to the second at least. The window slides a slice at a
    ///     time, so a match is counted in it up to a slice longer than the window.
    /// </summary>
    public TimeSpan Slice => TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(Count.Window.TotalSeconds / 60)));

    /// <summary>
    ///     The view's statement: the matches the counter has counted in the slices that started within the window, by the
    ///     engine's clock, held as a single row while they're enough, or none. No count of none is judged before
    ///     <paramref name="judgedFrom" />: one window after the counter began.
    /// </summary>
    public string Sql(int counter, DateTime judgedFrom)
    {
        var window = string.Create(CultureInfo.InvariantCulture, $"INTERVAL '{Count.Window.TotalSeconds:0}' SECOND");
        var sql = new StringBuilder("SELECT 1 AS id, COALESCE(SUM(c.matched), 0) AS matched");
        sql.Append("\nFROM live.match_counts AS c");
        sql.Append(CultureInfo.InvariantCulture, $"\nWHERE c.counter = {counter} AND c.slice_start > @now - {window}");
        sql.Append(Count.Kind == AlertCountKind.AtLeast
            ? string.Create(CultureInfo.InvariantCulture, $"\nHAVING COALESCE(SUM(c.matched), 0) >= {Count.Matches}")
            : string.Create(CultureInfo.InvariantCulture,
                $"\nHAVING COALESCE(SUM(c.matched), 0) = 0 AND @now >= TIMESTAMP '{judgedFrom:yyyy-MM-dd HH:mm:ss.ffffff}'"));
        return sql.ToString();
    }
}

/// <summary>
///     One count view: a ChalkQL standing view, maintained incrementally, that holds its one row while the matches in its
///     window are enough, or none. Its row entering fires its shape; again only after it has left. As the clock moves, the
///     window slides with it.
/// </summary>
internal sealed class CountView : IAsyncDisposable, IViewSink<int, long>
{
    private readonly StandingView<int, long> _view;
    private ViewCursor _cursor = ViewCursor.Start;
    private long? _entered;
    private bool _present;

    private CountView(AlertWindowSpec spec, AlertWatch watch, StandingView<int, long> view)
    {
        (Spec, Watch, _view) = (spec, watch, view);
    }

    public AlertWindowSpec Spec { get; }

    /// <summary>The shape it fires.</summary>
    public AlertWatch Watch { get; }

    public ValueTask DisposeAsync()
    {
        return _view.DisposeAsync();
    }

    void IViewSink<int, long>.Reset()
    {
        _present = false;
    }

    void IViewSink<int, long>.Upsert(in int key, in long matches)
    {
        if (_present) return;
        _present = true;
        _entered = matches;
    }

    void IViewSink<int, long>.Delete(in int key)
    {
        _present = false;
    }

    void IViewSink<int, long>.Commit(long version, uint checksum)
    {
    }

    public static async Task<CountView> CreateAsync(ChalkEngine engine, AlertWindowSpec spec, AlertWatch watch, int counter, DateTime judgedFrom,
        long budget, CancellationToken ct)
    {
        var view = await engine.MaterializeAsync(spec.Sql(counter, judgedFrom), new ViewOptions<int, long>
        {
            Key = ["id"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Map = Map,
            StateBudget = budget,
            Maintenance = ViewMaintenance.Incremental,
            ResetWhen = double.PositiveInfinity
        }, ct).ConfigureAwait(false);
        var made = new CountView(spec, watch, view);
        await made.UpdateAsync(ct).ConfigureAwait(false);
        return made;
    }

    /// <summary>Brings the view to the engine's latest epoch: the matches in the window, if its row entered it.</summary>
    public async Task<long?> UpdateAsync(CancellationToken ct)
    {
        _entered = null;
        await _view.UpdateAsync(ct).ConfigureAwait(false);
        while (_view.Drain(ref _cursor, this, 1024) == DrainOutcome.Partial)
        {
        }

        return _entered;
    }

    private static void Map(in ViewRow row, out int key, out long matches)
    {
        key = Convert.ToInt32(row.GetValue(0), CultureInfo.InvariantCulture);
        matches = Convert.ToInt64(row.GetValue(1), CultureInfo.InvariantCulture);
    }
}
