using System.Runtime.InteropServices;

namespace LiveGrid.Sample.Domain;

/// <summary>How busy the house is: the ChalkQL blotter's exchange's rates and shares, by default.</summary>
internal sealed record HouseOptions
{
    /// <summary>Changes a second, on average, outside a burst: new orders, fills and cancels.</summary>
    public double Rate { get; init; } = 200;

    /// <summary>How often a burst comes; zero for never.</summary>
    public TimeSpan BurstEvery { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BurstFor { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The rate in a burst, as a multiple of <see cref="Rate" />.</summary>
    public double BurstFactor { get; init; } = 10;

    /// <summary>Of the changes, the share that are new orders rather than fills or cancels.</summary>
    public double NewShare { get; init; } = 0.3;

    /// <summary>Of new orders, the share filled at once.</summary>
    public double FilledAtOnce { get; init; } = 0.25;

    /// <summary>Of the changes to working orders, the share that cancel rather than fill.</summary>
    public double CancelShare { get; init; } = 0.03;

    /// <summary>Past this many orders working, the house fills rather than takes new ones.</summary>
    public int MostOpen { get; init; } = 5000;

    public int Seed { get; init; } = 20261026;
}

/// <summary>
///     The house, taking client orders and working them, as the ChalkQL blotter's exchange does: <c>Rate</c> changes a
///     second (bursts of <c>BurstFactor</c> times as many), each a new order (a share of them filled at once) or a fill
///     or cancel of a working one, prices walking as it goes. Its book holds every order made or changed since the start;
///     the rest are the <see cref="History" />'s, as they were. Seeded: a run of one size and options makes the same
///     changes, in the same order, if not at the same moments.
/// </summary>
internal sealed class House : IAsyncDisposable
{
    private const int MostNoted = 1 << 18; // changes noted between asks (Changes), at most
    private readonly Dictionary<long, OrderRow> _book = []; // orders made or changed since the start, by number
    private readonly Dictionary<long, List<Fill>> _fills = []; // the fills made since the start, by order, oldest first
    private readonly Lock _gate = new();
    private readonly History _history;
    private readonly double[] _mids;
    private readonly Dictionary<Guid, long> _numbers = []; // the book's new orders' numbers, by id
    private readonly List<long> _open = []; // numbers of the orders working
    private readonly HouseOptions _options;
    private readonly Random _random;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Action> _watchers = [];
    private readonly IHouseLog? _log;
    private readonly List<OrderRow> _wrote = []; // (a send's versions, and its fills with their orders' versions: for the log)
    private readonly List<(OrderRow Order, Fill Fill)> _wroteFills = [];
    private long _last; // the last change's time, µs since 1970: strictly increasing
    private long _next, _version, _nextFill = 1L << 40; // (fills made since the start: numbered apart from the history's)
    private HashSet<long>? _changed; // orders changed since last asked (Changes): null until someone asks
    private bool _lost; // more changed than noted since: the asker reads them all again
    private volatile bool _paused;
    private readonly Task _running;

    /// <param name="log">Told every version and fill the house writes, in the order written: the database's ledger.</param>
    public House(History history, HouseOptions options, long started, IHouseLog? log = null)
    {
        (_history, _options, _next, _last, _log) = (history, options, history.Orders, started, log);
        _random = new Random(options.Seed);
        _mids = [.. Reference.Assets.Select(a => a.Price)];
        for (var n = history.Orders - history.Open; n < history.Orders; n++) _open.Add(n); // (left new: worked from now)
        _running = Task.Run(() => TradeAsync(_stop.Token));
    }

    public HouseOptions Options => _options;

    /// <summary>The orders there at the start (the history's).</summary>
    public long Initial => _history.Orders;

    /// <summary>The rate the house is working at now, changes a second.</summary>
    public double CurrentRate { get; private set; }

    /// <summary>Whether the house has stopped taking and working orders (an outage).</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Orders 0 to <c>Count − 1</c>, and a number that moves with every change.</summary>
    public (long Count, long Version) Now()
    {
        lock (_gate) return (_next, _version);
    }

    public long Version => Interlocked.Read(ref _version);

    /// <summary>Order <paramref name="n" /> as it stands.</summary>
    public OrderRow RowOf(long n)
    {
        lock (_gate) return _book.TryGetValue(n, out var row) ? row : _history.Make(n);
    }

    /// <summary>Orders <paramref name="numbers" /> as they stand, in one look: into <paramref name="rows" />.</summary>
    public void RowsOf(ReadOnlySpan<long> numbers, Span<OrderRow> rows)
    {
        lock (_gate)
            for (var i = 0; i < numbers.Length; i++)
                rows[i] = _book.TryGetValue(numbers[i], out var row) ? row : _history.Make(numbers[i]);
    }

    /// <summary>
    ///     The orders changed since this was last asked, into <paramref name="changes" />, and the count and version
    ///     they're as of. The first time, and when more changed between than it notes, <c>Whole</c>: read them all.
    /// </summary>
    public (long Count, long Version, bool Whole) Changes(List<(long Number, OrderRow Row)> changes)
    {
        changes.Clear();
        lock (_gate)
        {
            var whole = _changed is null || _lost;
            _changed ??= [];
            if (!whole)
                foreach (var n in _changed)
                    changes.Add((n, _book[n])); // (a changed order is in the book)
            _changed.Clear();
            _lost = false;
            return (_next, _version, whole);
        }
    }

    /// <summary>
    ///     Order <paramref name="n" />'s fills as they stand, newest first, into <paramref name="fills" /> (cleared): the
    ///     history's, then those made since.
    /// </summary>
    public void FillsOf(long n, List<Fill> fills)
    {
        fills.Clear();
        _history.FillsOf(n, fills);
        lock (_gate)
            if (_fills.TryGetValue(n, out var made))
                fills.AddRange(made);
        fills.Reverse();
    }

    /// <summary>An order's number from its id: −1 for none.</summary>
    public long NumberOf(Guid id)
    {
        lock (_gate)
            if (_numbers.TryGetValue(id, out var n))
                return n;
        return _history.NumberOf(id);
    }

    public IDisposable Watch(Action changed)
    {
        lock (_watchers) _watchers.Add(changed);
        return new Unwatch(this, changed);
    }

    /// <summary>Makes <paramref name="count" /> changes at once: a burst, from the page or the clock.</summary>
    public void Send(int count)
    {
        lock (_gate)
        {
            for (var i = 0; i < count; i++) Next();
            _version++;
            if (_log is not null) _log.Wrote(CollectionsMarshal.AsSpan(_wrote), CollectionsMarshal.AsSpan(_wroteFills));
            _wrote.Clear();
            _wroteFills.Clear();
        }

        Action[] watchers;
        lock (_watchers) watchers = [.. _watchers];
        foreach (var watcher in watchers) watcher();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task TradeAsync(CancellationToken ct)
    {
        var tick = TimeSpan.FromMilliseconds(10);
        using var timer = new PeriodicTimer(tick);
        var started = DateTime.UtcNow;
        var owed = 0.0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            var bursting = _options.BurstEvery > TimeSpan.Zero &&
                           (DateTime.UtcNow - started).Ticks % _options.BurstEvery.Ticks < _options.BurstFor.Ticks;
            CurrentRate = _paused ? 0 : _options.Rate * (bursting ? _options.BurstFactor : 1);
            owed = _paused ? 0 : owed + CurrentRate * tick.TotalSeconds;
            var count = (int)owed;
            owed -= count;
            if (count > 0) Send(count);
        }
    }

    /// <summary>One change: a new order (filled at once, now and then), or a fill or cancel of a working one.</summary>
    private void Next()
    {
        for (var a = 0; a < _mids.Length; a++) _mids[a] *= 1 + (_random.NextDouble() - 0.5) * 0.0004;
        var fill = _open.Count > 0 && (_random.NextDouble() >= _options.NewShare || _open.Count > _options.MostOpen);
        if (!fill)
        {
            var rank = 1 + Pick(Reference.ClientWeights);
            var channels = Reference.ChannelsOf(rank);
            var channel = channels[Pick([.. channels.Select(c => c.Weight)])].Channel;
            var asset = Pick(Reference.AssetWeights);
            var n = _next++;
            var time = Tick();
            var order = History.New(n, Ids.V7(time, (ulong)n), time, rank - 1, channel, asset,
                _random.Next(2) == 0 ? Side.Buy : Side.Sell, (1 + _random.Next(6)) * Reference.Assets[asset].Lot);
            _book[n] = order;
            if (_log is not null) _wrote.Add(order);
            _numbers[order.OrderId] = n;
            Noted(n);
            if (_random.NextDouble() < _options.FilledAtOnce) Work(n, false);
            else _open.Add(n);
            return;
        }

        var at = _random.Next(_open.Count);
        var working = _open[at];
        _open[at] = _open[^1];
        _open.RemoveAt(_open.Count - 1);
        Work(working, _random.NextDouble() < _options.CancelShare);
    }

    /// <summary>Fills or cancels an order, keeping it working if it is still.</summary>
    private void Work(long n, bool cancel)
    {
        var row = _book.TryGetValue(n, out var booked) ? booked : _history.Make(n);
        var now = Tick();
        row.EventTime = Times.Of(now);
        row.Version++;
        var status = OrderStatus.Cancelled;
        if (!cancel)
        {
            var asset = Reference.AssetOf(InlineText.Bytes(row.Instrument)); // (the house's own: one of its perpetuals)
            var lot = Reference.Assets[asset].Lot;
            var left = (int)Math.Round((row.Qty - row.FilledQty) / lot);
            var qty = (1 + _random.Next(Math.Max(1, left))) * lot;
            var mid = Math.Round(_mids[asset], 6);
            var price = Math.Round(mid * (1 + (InlineText.Is(row.Side, "Buy"u8) ? 1 : -1) * row.MarkupBps / 10_000), 4);
            var filled = row.FilledQty + qty;
            row.AvgPrice = Math.Round((row.AvgPrice * row.FilledQty + price * qty) / filled, 4);
            row.FilledAmt = Math.Round(row.FilledAmt + Math.Round(qty * price, 2), 2);
            row.FilledQty = filled;
            status = filled >= row.Qty - 1e-9 ? OrderStatus.Filled : OrderStatus.Partial;
            if (!_fills.TryGetValue(n, out var made)) _fills[n] = made = []; // (the house works orders the history left new)
            var fill = new Fill(Ids.V7(now, Fill.Salt | (ulong)_nextFill++ << 2 | 3), now, qty, price, Math.Round(qty * price, 2),
                made.Count + 1);
            made.Add(fill);
            if (_log is not null) _wroteFills.Add((row, fill)); // (the order as it stands before this version's status: its names)
        }

        row.Status = Reference.StatusTexts[(int)status];
        _book[n] = row;
        if (_log is not null) _wrote.Add(row);
        Noted(n);
        if (status == OrderStatus.Partial) _open.Add(n);
    }

    /// <summary>An order changed, for <see cref="Changes" />: once it has been asked, up to a bound.</summary>
    private void Noted(long n)
    {
        if (_changed is null || _lost) return;
        if (_changed.Count < MostNoted)
        {
            _changed.Add(n);
            return;
        }

        (_changed, _lost) = ([], true); // (no one asking: let them go, and say so)
    }

    /// <summary>The next change's time: now, in microseconds, but never two changes at one.</summary>
    private long Tick()
    {
        var now = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        return _last = Math.Max(now, _last + 1);
    }

    private int Pick(double[] weights)
    {
        var r = _random.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length - 1; i++)
            if ((r -= weights[i]) < 0)
                return i;
        return weights.Length - 1;
    }

    private sealed class Unwatch(House house, Action changed) : IDisposable
    {
        public void Dispose()
        {
            lock (house._watchers) house._watchers.Remove(changed);
        }
    }
}

/// <summary>Told what a house writes, as it writes it, under its lock: each batch in order, then let go.</summary>
internal interface IHouseLog
{
    /// <summary>A batch: the order versions written, and the fills, each beside its order's version.</summary>
    void Wrote(ReadOnlySpan<OrderRow> versions, ReadOnlySpan<(OrderRow Order, Fill Fill)> fills);
}
