using System.Diagnostics;
using System.Runtime.InteropServices;
using Tapline.Grid;
using Tapline.Grid.Provider;

namespace LiveGrid.Sample.Domain;

/// <summary>
///     The house's changes as the ChalkQL blotter has them (grid-sources.md §6): each order version streamed at once — the
///     grid's stream — and each fill too (<see cref="Fills" />), and written to the database's tables later, as ingestion
///     jobs would: 150 ms to 3 s after (half a second, typically), a few at a time, and now and then stalled for a few
///     seconds. So the stream runs ahead of the database by varying amounts, and versions land out of their stamps' order,
///     within the store's haircut. The page stalls the database for a while (<see cref="Stall" />), the streams going on; and
///     puts the streams out of order (<see cref="Disordered" />), as the blotter's exchange does.
/// </summary>
internal sealed class Ledger : IHouseLog, IGridStream<OrderRow>, IAsyncDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(50); // (the ingestion's pace)
    private static readonly TimeSpan StallEvery = TimeSpan.FromSeconds(45), StallFor = TimeSpan.FromSeconds(3);

    // Out of order, as the blotter's exchange: of the rows each stream sends, a few sent late — after rows that happened
    // after them, by a delay most often short and now and then long — the database writing them as usual; and a few the
    // database writes first, at once, the stream sending them some time after.
    private const double Late = 0.02, DatabaseFirst = 0.01;
    private static readonly TimeSpan LateMedian = TimeSpan.FromMilliseconds(400), LateMost = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstMedian = TimeSpan.FromMilliseconds(200);

    private readonly List<(long Due, (OrderRow Order, Fill Fill) Made)> _fills = []; // written, not yet landed: by when each lands
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, int> _landed = []; // each order's newest version in orders_current, of those written since
    private readonly List<(long Due, OrderRow Order)> _orders = [];
    private readonly Random _random = new(20261028);
    private readonly CancellationTokenSource _stop = new();
    private readonly FillStream _fillStream = new();
    private FillRow[] _fillRows = new FillRow[64]; // (a batch's fills as rows: the house's thread's, reused)
    private OrderRow[] _sendOrders = new OrderRow[64]; // (out of order, a batch's versions sent at once: likewise)
    private (long Send, long Land)[] _plans = new (long, long)[128]; // (and when each of its rows is sent, and lands)
    private readonly PriorityQueue<(OrderRow Row, bool First), long> _lateOrders = new(); // sent late, by when: under _gate
    private readonly PriorityQueue<(FillRow Row, bool First), long> _lateFills = new();
    private volatile bool _disordered;
    private long _stalledUntil; // (timestamp ticks) the stall the page asked for: the database writes nothing till then
    private long _sent, _sentLate, _sentAfterDatabase;
    private volatile bool _stalled;
    private GridBatch<OrderRow>? _batch;
    private Task _ingesting = Task.CompletedTask;
    private Landing? _land;

    /// <summary>A landing that failed (its batch landed again at the next tick).</summary>
    public Action<Exception>? Failed { get; set; }

    /// <summary>
    ///     Whether the streams are out of order: of the rows each sends, 2 % sent late, after rows that happened after them
    ///     (log-normal about 400 ms, at most 10 s), the database writing them as usual; and 1 % the database writes first, at
    ///     once, the stream sending them after (about 200 ms). The alerts hear each as the house writes it, as ever.
    /// </summary>
    public bool Disordered
    {
        get => _disordered;
        set => _disordered = value;
    }

    /// <summary>Whether the database is writing nothing now: stalled by the page, or by the ingestion's own now and then.</summary>
    public bool Stalled => _stalled;

    /// <summary>Versions and fills the streams have sent; of them, those sent late, and those sent after the database wrote them.</summary>
    public (long Sent, long Late, long AfterDatabase) Streamed =>
        (Interlocked.Read(ref _sent), Interlocked.Read(ref _sentLate), Interlocked.Read(ref _sentAfterDatabase));

    /// <summary>The database writes nothing for a while, the streams going on (zero ends a stall).</summary>
    public void Stall(TimeSpan time)
    {
        Volatile.Write(ref _stalledUntil, Stopwatch.GetTimestamp() + Ticks(time));
    }

    /// <summary>How long ago the oldest version or fill written that the database hasn't yet was made: none, null.</summary>
    public TimeSpan? Behind(DateTime now)
    {
        var oldest = DateTime.MaxValue;
        lock (_gate)
        {
            foreach (ref readonly var pending in CollectionsMarshal.AsSpan(_orders))
                if (pending.Order.EventTime < oldest) oldest = pending.Order.EventTime;
            foreach (ref readonly var pending in CollectionsMarshal.AsSpan(_fills))
                if (Times.Of(pending.Made.Fill.Time) is var made && made < oldest) oldest = made;
        }

        return oldest == DateTime.MaxValue ? null : now - oldest;
    }

    /// <summary>The versions and fills written that the database hasn't yet.</summary>
    public int Pending
    {
        get
        {
            lock (_gate) return _orders.Count + _fills.Count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _ingesting;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    public IDisposable Subscribe(GridBatch<OrderRow> batch)
    {
        _batch = batch;
        return new Ended(this, batch);
    }

    /// <summary>
    ///     The fills the house writes, as written (after they're queued to land: <see cref="PendingFills" /> has them first) —
    ///     the stream of fills, to those keeping orders' fills.
    /// </summary>
    public FillsWritten? Filled { get; set; }

    /// <summary>The house's fills, each a row as the table <c>fills</c> holds it: streamed at once, as the versions are.</summary>
    public IGridStream<FillRow> Fills => _fillStream;

    /// <summary>
    ///     Each batch as the house writes it, versions and fills, on its thread (under its lock: what's told must be quick) —
    ///     for the alerts' evaluator, beside the grids' streams.
    /// </summary>
    public Action<ReadOnlySpan<OrderRow>, ReadOnlySpan<(OrderRow Order, Fill Fill)>>? Heard { get; set; }

    /// <summary>
    ///     Each batch the database took, as it landed: the versions and the fills (the alerts' waits end with it). Writes land
    ///     out of order, each after its own lag: a batch is all those due at once.
    /// </summary>
    public Action<OrderRow[], (OrderRow Order, Fill Fill)[]>? Landed { get; set; }

    /// <summary>
    ///     A batch the house wrote: streamed at once, in order (on the house's thread, under its lock) — out of order, some of
    ///     it later (<see cref="Disordered" />) — and queued to land.
    /// </summary>
    public void Wrote(ReadOnlySpan<OrderRow> versions, ReadOnlySpan<(OrderRow Order, Fill Fill)> fills)
    {
        var streamed = fills.Length > 0 && _fillStream.Batch is not null;
        if (streamed)
        {
            if (_fillRows.Length < fills.Length) _fillRows = new FillRow[Math.Max(fills.Length, 2 * _fillRows.Length)];
            for (var i = 0; i < fills.Length; i++) _fillRows[i] = FillRow.Of(fills[i].Order, fills[i].Fill);
        }

        var now = Stopwatch.GetTimestamp();
        if (_disordered)
        {
            Disorder(now, versions, fills, streamed);
        }
        else
        {
            _batch?.Invoke(versions);
            if (streamed) _fillStream.Batch?.Invoke(_fillRows.AsSpan(0, fills.Length));
            Interlocked.Add(ref _sent, versions.Length + fills.Length);
            Heard?.Invoke(versions, fills); // (told before it's queued to land: a landing never comes before its batch is heard)
            lock (_gate)
            {
                foreach (ref readonly var version in versions) _orders.Add((now + Lag(), version));
                foreach (ref readonly var made in fills) _fills.Add((now + Lag(), made));
            }
        }

        if (fills.Length > 0) Filled?.Invoke(fills);
    }

    /// <summary>A batch out of order: each row sent at once, late, or once the database has it (<see cref="Plan" />).</summary>
    private void Disorder(long now, ReadOnlySpan<OrderRow> versions, ReadOnlySpan<(OrderRow Order, Fill Fill)> fills, bool streamed)
    {
        if (_plans.Length < versions.Length + fills.Length) _plans = new (long, long)[Math.Max(versions.Length + fills.Length, 2 * _plans.Length)];
        if (_sendOrders.Length < versions.Length) _sendOrders = new OrderRow[Math.Max(versions.Length, 2 * _sendOrders.Length)];
        int orders = 0, made = 0;
        for (var i = 0; i < versions.Length; i++)
            if ((_plans[i] = Plan(now)).Send == now)
                _sendOrders[orders++] = versions[i];
        for (var i = 0; i < fills.Length; i++)
            if ((_plans[versions.Length + i] = Plan(now)).Send == now && streamed)
                _fillRows[made++] = _fillRows[i]; // (those sent at once to the front: the late made again below)
        _batch?.Invoke(_sendOrders.AsSpan(0, orders));
        if (streamed) _fillStream.Batch?.Invoke(_fillRows.AsSpan(0, made));
        Interlocked.Add(ref _sent, orders + made);
        Heard?.Invoke(versions, fills); // (told before it's queued to land: a landing never comes before its batch is heard)
        lock (_gate)
        {
            for (var i = 0; i < versions.Length; i++)
            {
                var (send, land) = _plans[i];
                _orders.Add((land, versions[i]));
                if (send != now) _lateOrders.Enqueue((versions[i], land == now), send);
            }

            for (var i = 0; i < fills.Length; i++)
            {
                var (send, land) = _plans[versions.Length + i];
                _fills.Add((land, fills[i]));
                if (send != now && streamed) _lateFills.Enqueue((FillRow.Of(fills[i].Order, fills[i].Fill), land == now), send);
            }
        }
    }

    /// <summary>When a row out of order is sent, and lands: at once, and after its lag; 2 % sent late; 1 % landed at once, sent after.</summary>
    private (long Send, long Land) Plan(long now)
    {
        var draw = _random.NextDouble();
        if (draw < Late) return (now + Math.Max(1, Ticks(LogNormal(LateMedian, LateMost))), now + Lag());
        if (draw < Late + DatabaseFirst) return (now + Math.Max(1, Ticks(LogNormal(FirstMedian, LateMost))), now);
        return (now, now + Lag());
    }

    /// <summary>The rows sent late, due by <paramref name="now" />, sent: the streams go on, whatever the database does.</summary>
    private void Release(long now, List<OrderRow> orders, List<FillRow> fills)
    {
        long first = 0;
        lock (_gate)
        {
            while (_lateOrders.TryPeek(out var late, out var at) && at <= now)
            {
                _lateOrders.Dequeue();
                orders.Add(late.Row);
                if (late.First) first++;
            }

            while (_lateFills.TryPeek(out var late, out var at) && at <= now)
            {
                _lateFills.Dequeue();
                fills.Add(late.Row);
                if (late.First) first++;
            }
        }

        if (orders.Count + fills.Count == 0) return;
        if (orders.Count > 0) _batch?.Invoke(CollectionsMarshal.AsSpan(orders));
        if (fills.Count > 0) _fillStream.Batch?.Invoke(CollectionsMarshal.AsSpan(fills));
        Interlocked.Add(ref _sent, orders.Count + fills.Count);
        Interlocked.Add(ref _sentLate, orders.Count + fills.Count - first);
        Interlocked.Add(ref _sentAfterDatabase, first);
        orders.Clear();
        fills.Clear();
    }

    /// <summary>The database, made: from now, what's due lands in it, by <paramref name="land" />.</summary>
    public void Attach(Landing land)
    {
        _land = land;
        _ingesting = Task.Run(() => IngestAsync(_stop.Token));
    }

    /// <summary>An order's fills written that the database hasn't yet, into <paramref name="into" />.</summary>
    public void PendingFills(Guid order, List<Fill> into)
    {
        lock (_gate)
            foreach (ref readonly var pending in CollectionsMarshal.AsSpan(_fills))
                if (pending.Made.Order.OrderId == order)
                    into.Add(pending.Made.Fill);
    }

    private async Task IngestAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Every);
        long stalledUntil = 0, nextStall = Stopwatch.GetTimestamp() + Ticks(StallEvery);
        List<OrderRow> orders = [], lateOrders = [];
        List<(OrderRow Order, Fill Fill)> fills = [];
        List<FillRow> lateFills = [];
        Dictionary<Guid, OrderRow> newest = [];
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = Stopwatch.GetTimestamp();
            Release(now, lateOrders, lateFills);
            if (now >= nextStall) (stalledUntil, nextStall) = (now + Ticks(StallFor), now + Ticks(StallEvery));
            _stalled = now < stalledUntil || now < Volatile.Read(ref _stalledUntil);
            if (_stalled) continue;
            lock (_gate)
            {
                Due(_orders, now, orders);
                Due(_fills, now, fills);
            }

            if (orders.Count == 0 && fills.Count == 0) continue;
            newest.Clear();
            foreach (var order in orders)
                if ((!_landed.TryGetValue(order.OrderId, out var landed) || landed < order.Version) &&
                    (!newest.TryGetValue(order.OrderId, out var was) || was.Version < order.Version))
                    newest[order.OrderId] = order;
            try
            {
                // (fresh arrays: a landing may keep what it's handed)
                await _land!([.. orders], [.. newest.Values], [.. fills], ct);
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Failed?.Invoke(failure); // (landed at the next tick, the batch kept as it was: an ingestion job retrying)
                continue;
            }

            foreach (var (id, order) in newest) _landed[id] = order.Version;
            Landed?.Invoke([.. orders], [.. fills]);
            orders.Clear();
            fills.Clear();
        }
    }

    /// <summary>Those due by <paramref name="now" />, out of the queue, in the order written.</summary>
    private static void Due<T>(List<(long Due, T Item)> queue, long now, List<T> into)
    {
        var kept = 0;
        for (var i = 0; i < queue.Count; i++)
            if (queue[i].Due <= now) into.Add(queue[i].Item);
            else queue[kept++] = queue[i];
        queue.RemoveRange(kept, queue.Count - kept);
    }

    /// <summary>How long a write takes to land: half a second, typically, from 150 ms to 3 s (log-normal).</summary>
    private long Lag()
    {
        return Ticks(TimeSpan.FromMilliseconds(Math.Clamp(500 * Math.Exp(0.8 * Normal()), 150, 3_000)));
    }

    /// <summary>A delay most often short, now and then long: log-normal about <paramref name="median" />, at most <paramref name="most" />.</summary>
    private TimeSpan LogNormal(TimeSpan median, TimeSpan most)
    {
        return TimeSpan.FromMilliseconds(Math.Min(median.TotalMilliseconds * Math.Exp(Normal()), most.TotalMilliseconds));
    }

    /// <summary>A standard normal draw (Box–Muller).</summary>
    private double Normal()
    {
        return Math.Sqrt(-2 * Math.Log(1 - _random.NextDouble())) * Math.Cos(2 * Math.PI * _random.NextDouble());
    }

    private static long Ticks(TimeSpan span)
    {
        return (long)(span.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>
    ///     What lands in the database: every version written (to <c>order_versions</c>), each order's newest of them, one an
    ///     order, newer than it had (to <c>orders_current</c>), and the fills, each with its order as it was (to <c>fills</c>).
    /// </summary>
    public delegate Task Landing(OrderRow[] versions, OrderRow[] current, (OrderRow Order, Fill Fill)[] fills, CancellationToken ct);

    /// <summary>Fills the house wrote, each with its order as it was.</summary>
    public delegate void FillsWritten(ReadOnlySpan<(OrderRow Order, Fill Fill)> fills);

    private sealed class Ended(Ledger ledger, GridBatch<OrderRow> batch) : IDisposable
    {
        public void Dispose()
        {
            if (ledger._batch == batch) ledger._batch = null;
        }
    }

    /// <summary>The fills' stream: one subscriber, the fills' source.</summary>
    private sealed class FillStream : IGridStream<FillRow>
    {
        public GridBatch<FillRow>? Batch;

        public IDisposable Subscribe(GridBatch<FillRow> batch)
        {
            Batch = batch;
            return new Ended(this, batch);
        }

        private sealed class Ended(FillStream stream, GridBatch<FillRow> batch) : IDisposable
        {
            public void Dispose()
            {
                if (stream.Batch == batch) stream.Batch = null;
            }
        }
    }
}
