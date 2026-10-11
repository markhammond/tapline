using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tapline;

namespace BlazingOrderBooks.Sample;

/// <summary>The exchange's clock and the handler's: one epoch, nanoseconds since (Stopwatch ticks, scaled).</summary>
internal static class Clock
{
    private static readonly long Epoch = Stopwatch.GetTimestamp();
    private static readonly double NsPerTick = 1e9 / Stopwatch.Frequency;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Ns()
    {
        return (long)((Stopwatch.GetTimestamp() - Epoch) * NsPerTick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Ns(long timestamp)
    {
        return (long)((timestamp - Epoch) * NsPerTick);
    }
}

/// <summary>
///     A simulated venue. Its order flow is a stochastic limit order book in the spirit of zero-intelligence models
///     (Smith, Farmer et al.), tuned so its churn looks like a liquid venue's: most events are at the best price — new
///     orders join its queue (or one just behind), cancels take part of a queue near the top — while market orders, now
///     and then, eat into the top and orders that improve the price are rare. So the price moves a few times a second,
///     deep levels rarely change, and each event touches one or two levels. The book never crosses and is kept to
///     <see cref="Depth" /> levels a side, as a venue's L2 depth feed shows it. Its matching engine keeps the book in the
///     same ladder the handler uses (<see cref="L2Book" />).
///     <para>
///         It publishes as a venue does, onto its wire (<see cref="SpscByteRing" />, <see cref="Wire" />): every fill is
///         printed at once — the trade feed — while the depth feed is conflated: the levels that changed over an interval
///         (<c>DepthIntervalMs</c>; venues range from 10 to 100 ms), each with its size as it stands, published when the
///         interval closes, and, if configured, held back a little longer (<c>DepthDelayMs</c>) as a slower channel would
///         be. Each depth update names the events it reflects (first and last sequence) and the last trade among them, so
///         a handler can tell which prints the depth has caught up with. Every event — an order, a cancel, a fill — takes
///         a sequence number, published or not: an update dropped on the wire is a gap the handler sees. Asked to
///         (<see cref="RequestSnapshot" />), it publishes the whole book: the recovery channel.
///     </para>
///     <para>
///         Its prices are on a tick grid (<c>tick</c>, in the wire's unit of 0.01). To try the handler against an
///         instantaneous gap, it can jump (<c>jumpEveryMs</c>, <c>jumpTicks</c>): a buyer (or a seller: they alternate)
///         sweeps the other side's whole book, its liquidity vanishes, the book is rebuilt that many ticks away, and the
///         sweeper trades there — the trades printed at once, the book's change in the next depth update.
///     </para>
/// </summary>
internal sealed unsafe class Exchange : IDisposable
{
    public const int Depth = 25;
    private const int MinLevels = 5; // cancels and market orders never thin a side below this
    private const double LimitsPerSecond = 200, CancelsPerSecond = 170, MarketOrdersPerSecond = 8;

    private const double
        JoinFalloff = 0.6; // a new order joins the best with this chance, else one tick behind with it, …

    private const double ImproveChance = 0.05; // …unless the spread is over a tick and it improves the price

    private const double
        CancelFalloff = 0.6,
        FullCancelChance = 0.03; // cancels: the best queue with this chance, else the next…; rarely all of it

    private const int MaxDirty = 512; // levels one depth update can carry (more: it goes out early)
    private readonly Level[] _all = new Level[2 * (Depth + 1)]; // a snapshot's levels
    private readonly L2Book _book;
    private readonly int _depthIntervalMs, _depthDelayMs, _dropEvery, _jumpEveryMs, _jumpTicks;
    private readonly Level[] _dirty = new Level[MaxDirty]; // levels changed since the last depth update
    private readonly SpscByteRing _ring;
    private readonly Random _rnd;

    private readonly byte[]
        _scratch = new byte[48 + MaxDirty * Wire.DepthLevelSize]; // a depth update or snapshot, built

    private readonly long[] _markedBid, _markedAsk; // per slot: the price it was marked for (two may share a slot)
    private readonly int[] _stampBid, _stampAsk; // per slot: the generation it was marked dirty in (no scan to dedupe)
    private readonly long _tick;
    private long _depthFirst = 1; // the first event the next depth update reflects
    private int _dirtyCount, _gen = 1;
    private bool _jumpUp = true;
    private double _nextDepthMs, _nextJumpMs, _stagedDueMs;
    private long _seq, _tradeId, _lastTradeSeq;
    private int _snapshotRequested = 1; // the first message is the book
    private int _stagedLength; // a depth update held back until its due time (DepthDelayMs)

    /// <param name="tick">The tick, in the wire's unit; <paramref name="mid" /> is on its grid.</param>
    /// <param name="dropEvery">Drop every nth depth update on the wire (0: none): gaps to test the recovery against.</param>
    /// <param name="jumpEveryMs">Jump the price this often (0: never): instantaneous gaps, to test the handler against.</param>
    /// <param name="jumpTicks">How far each jump goes, in ticks.</param>
    public Exchange(long mid, Random rnd, SpscByteRing ring, int window, int depthIntervalMs, int depthDelayMs,
        int dropEvery = 0, long tick = 1, int jumpEveryMs = 0, int jumpTicks = 0)
    {
        _dropEvery = dropEvery;
        _tick = tick;
        _jumpEveryMs = jumpEveryMs;
        _jumpTicks = jumpTicks;
        _nextJumpMs = jumpEveryMs;
        _rnd = rnd;
        _ring = ring;
        mid -= mid % tick;
        _book = new L2Book(window, mid, tick);
        _stampBid = new int[window];
        _stampAsk = new int[window];
        _markedBid = new long[window];
        _markedAsk = new long[window];
        _depthIntervalMs = Math.Max(1, depthIntervalMs);
        _depthDelayMs = Math.Clamp(depthDelayMs, 0, _depthIntervalMs);
        Seed(mid, false);
    }

    public long Seq => _seq;
    public long Trades { get; private set; }
    public long DepthUpdates { get; private set; }
    public long Snapshots { get; private set; }

    /// <summary>Depth updates dropped on purpose (<c>dropEvery</c>).</summary>
    public long Dropped { get; private set; }

    /// <summary>Instantaneous gaps made (<c>jumpEveryMs</c>).</summary>
    public long Jumps { get; private set; }

    public void Dispose()
    {
        _book.Dispose();
    }

    /// <summary>The handler lost the sequence: the whole book goes out at the next step.</summary>
    public void RequestSnapshot()
    {
        Volatile.Write(ref _snapshotRequested, 1);
    }

    /// <summary>
    ///     Trades for <paramref name="elapsedMs" />, printing fills as they happen; publishes the depth interval that
    ///     closed, if one did, and a snapshot if asked for. <paramref name="nowMs" />: the exchange's time.
    /// </summary>
    public void Step(double nowMs, double elapsedMs)
    {
        if (Volatile.Read(ref _snapshotRequested) != 0)
        {
            Volatile.Write(ref _snapshotRequested, 0);
            Snapshot();
        }

        for (var k = Poisson(LimitsPerSecond * elapsedMs / 1000); k > 0; k--) Limit(_rnd.Next(2) == 0);
        for (var k = Poisson(CancelsPerSecond * elapsedMs / 1000); k > 0; k--) Cancel(_rnd.Next(2) == 0);
        for (var k = Poisson(MarketOrdersPerSecond * elapsedMs / 1000); k > 0; k--) Market(_rnd.Next(2) == 0);
        if (_jumpEveryMs > 0 && nowMs >= _nextJumpMs)
        {
            Jump(_jumpUp);
            _jumpUp = !_jumpUp;
            while (_nextJumpMs <= nowMs) _nextJumpMs += _jumpEveryMs;
        }

        if (nowMs >= _nextDepthMs)
        {
            if (_dirtyCount > 0) CloseInterval(nowMs);
            do
            {
                _nextDepthMs += _depthIntervalMs;
            } while (_nextDepthMs <= nowMs);
        }

        if (_stagedLength > 0 && nowMs >= _stagedDueMs)
        {
            Publish(_stagedLength);
            _stagedLength = 0;
        }
    }

    /// <summary>A resting order: joins the queue at or just behind its side's best, or now and then improves the price.</summary>
    private void Limit(bool bid)
    {
        var side = bid ? _book.Bids : _book.Asks;
        long best = side.Best,
            ticks = _book.Asks.Best - _book.Bids.Best > _tick && _rnd.NextDouble() < ImproveChance
                ? -1
                : Geometric(JoinFalloff);
        _seq++;
        Change(side, bid ? best - ticks * _tick : best + ticks * _tick, Size(200));
        if (side.Count > Depth) // beyond the published depth
        {
            var worst = side.Worst;
            Change(side, worst, -side.Size(worst));
        }
    }

    /// <summary>Cancels part of a queue near the top (rarely all of it).</summary>
    private void Cancel(bool bid)
    {
        var side = bid ? _book.Bids : _book.Asks;
        if (side.Count <= MinLevels) return;
        var price = side.Best;
        for (var rank = Math.Min(Geometric(CancelFalloff), side.Count - 1); rank > 0; rank--) price = side.Next(price);
        var size = side.Size(price);
        _seq++;
        Change(side, price,
            _rnd.NextDouble() < FullCancelChance ? -size : -Math.Max(1, (long)(size * _rnd.NextDouble() / 2)));
    }

    /// <summary>
    ///     A market order taking liquidity from the opposite side's top, level by level (never emptying it): a fill at
    ///     each level, each printed as its own event.
    /// </summary>
    private void Market(bool buy)
    {
        var side = buy ? _book.Asks : _book.Bids;
        for (var left = Size(100); left > 0 && side.Count > MinLevels;)
        {
            long price = side.Best, take = Math.Min(left, side.Size(price));
            _lastTradeSeq = ++_seq;
            Print(price, take, buy ? Level.Bid : Level.Ask);
            Change(side, price, -take);
            left -= take;
        }
    }

    /// <summary>
    ///     An instantaneous gap: the aggressor sweeps the other side's whole book (each fill printed), the book's
    ///     liquidity vanishes and is rebuilt <see cref="_jumpTicks" /> away, and the aggressor trades there too.
    /// </summary>
    private void Jump(bool up)
    {
        Jumps++;
        var mid = _book.Mid + (up ? 1 : -1) * (long)_jumpTicks * _tick; // from where the book is now
        mid -= mid % _tick;
        var taken = up ? _book.Asks : _book.Bids;
        var aggressor = up ? Level.Bid : Level.Ask;
        for (var price = taken.Best; price != Ladder.None; price = taken.Best) // through the whole side
        {
            _lastTradeSeq = ++_seq;
            var size = taken.Size(price);
            Print(price, size, aggressor);
            Change(taken, price, -size);
        }

        for (var price = (up ? _book.Bids : _book.Asks).Best; price != Ladder.None;
             price = (up ? _book.Bids : _book.Asks).Best) // the resting side pulled too: nobody stays behind a gap
        {
            _seq++;
            var side = up ? _book.Bids : _book.Asks;
            Change(side, price, -side.Size(price));
        }

        Seed(mid, true); // both sides empty now: the book again, a jump away
        var touch = up ? _book.Asks.Best : _book.Bids.Best; // the sweeper's rest, filled at the new touch
        var take = Math.Max(1, taken.Size(touch) / 2);
        _lastTradeSeq = ++_seq;
        Print(touch, take, aggressor);
        Change(taken, touch, -take);
    }

    /// <summary>The book around <paramref name="mid" />: <see cref="Depth" /> levels a side, a tick apart.</summary>
    private void Seed(long mid, bool asEvents)
    {
        for (var i = 0; i < Depth; i++)
        {
            if (asEvents) _seq++;
            Change(_book.Bids, mid - i * _tick, Size(500), asEvents);
            if (asEvents) _seq++;
            Change(_book.Asks, mid + (1 + i) * _tick, Size(500), asEvents);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Change(Ladder side, long price, long delta, bool published = true)
    {
        side.Add(price, delta);
        if (!published) return; // (the opening book: the first snapshot carries it)
        var ask = side == _book.Asks;
        var stamps = ask ? _stampAsk : _stampBid;
        var marked = ask ? _markedAsk : _markedBid;
        var slot = side.Slot(price);
        if (stamps[slot] == _gen && marked[slot] == price) return;
        stamps[slot] = _gen;
        marked[slot] = price;
        _dirty[_dirtyCount++] = new Level(side == _book.Asks ? Level.Ask : Level.Bid, price);
        if (_dirtyCount == MaxDirty) CloseInterval(_nextDepthMs - _depthIntervalMs); // full: it goes out now
    }

    /// <summary>The trade feed: a fill, on the wire at once.</summary>
    private void Print(long price, long size, byte aggressor)
    {
        Trades++;
        var at = _ring.TryClaim(sizeof(Wire.Trade));
        if (at.IsEmpty) return; // dropped: the wire is full
        ref var trade = ref MemoryMarshal.AsRef<Wire.Trade>(at);
        trade = default;
        trade.Header.Template = Wire.TradeTemplate;
        trade.Header.Length = (ushort)sizeof(Wire.Trade);
        trade.Header.Seq = _seq;
        trade.TradeId = ++_tradeId;
        trade.Price = price;
        trade.Size = size;
        trade.Aggressor = aggressor;
        trade.Timestamp = Clock.Ns();
        _ring.Commit();
    }

    /// <summary>
    ///     The depth feed: the levels that changed over the interval, as they stand, reflecting events
    ///     <see cref="_depthFirst" /> through <see cref="_seq" /> — on the wire now, or staged till its delay is up.
    /// </summary>
    private void CloseInterval(double nowMs)
    {
        DepthUpdates++;
        var length = Build(Wire.DepthTemplate, _depthFirst, _dirty, _dirtyCount);
        _depthFirst = _seq + 1;
        _dirtyCount = 0;
        _gen++;
        if (_dropEvery > 0 && DepthUpdates % _dropEvery == 0) // a lossy wire, to order: the update never goes out
        {
            Dropped++;
            return;
        }

        if (_depthDelayMs == 0)
        {
            Publish(length);
            return;
        }

        if (_stagedLength > 0) Publish(_stagedLength); // (one held back at a time: an earlier one goes first)
        _stagedLength = length;
        _stagedDueMs = nowMs + _depthDelayMs;
    }

    /// <summary>The recovery channel: every level, as of now (the interval's dirty levels stay for its update).</summary>
    private void Snapshot()
    {
        Snapshots++;
        Span<long> prices = stackalloc long[Depth + 1], sizes = stackalloc long[Depth + 1];
        var count = 0;
        var n = _book.Bids.Top(prices, sizes);
        for (var i = 0; i < n; i++) _all[count++] = new Level(Level.Bid, prices[i]);
        n = _book.Asks.Top(prices, sizes);
        for (var i = 0; i < n; i++) _all[count++] = new Level(Level.Ask, prices[i]);
        Publish(Build(Wire.SnapshotTemplate, 0, _all, count));
    }

    /// <summary>
    ///     A depth update or snapshot of <paramref name="levels" /> as they stand, into the scratch buffer; returns its
    ///     length.
    /// </summary>
    private int Build(ushort template, long firstSeq, Level[] levels, int count)
    {
        fixed (byte* at = _scratch)
        {
            ref var depth = ref Wire.At<Wire.Depth>(at);
            depth = default;
            depth.Header.Template = template;
            depth.Header.Seq = _seq;
            depth.FirstSeq = firstSeq;
            depth.LastTradeSeq = _lastTradeSeq;
            depth.Timestamp = Clock.Ns();
            depth.Count = (ushort)count;
            var length = sizeof(Wire.Depth) + count * Wire.DepthLevelSize;
            depth.Header.Length = (ushort)length;
            var entries = (Wire.DepthLevel*)(at + sizeof(Wire.Depth));
            for (var i = 0; i < count; i++)
            {
                var level = levels[i];
                entries[i] = default;
                entries[i].Side = level.Side;
                entries[i].Price = level.Price;
                entries[i].Size = _book.Side(level.Side).Size(level.Price);
            }

            return length;
        }
    }

    private void Publish(int length)
    {
        var at = _ring.TryClaim(length);
        if (at.IsEmpty) return; // dropped: a gap the handler will see
        _scratch.AsSpan(0, length).CopyTo(at);
        _ring.Commit();
    }

    /// <summary>Log-uniform in [1, max): mostly small orders, some large.</summary>
    private long Size(int max)
    {
        return Math.Max(1, (long)Math.Exp(_rnd.NextDouble() * Math.Log(max)));
    }

    /// <summary>Failures before the first success: 0, 1, 2… with mean (1 − p) / p.</summary>
    private int Geometric(double p)
    {
        var k = 0;
        while (_rnd.NextDouble() > p) k++;
        return k;
    }

    private int Poisson(double lambda)
    {
        double limit = Math.Exp(-lambda), product = _rnd.NextDouble();
        var k = 0;
        for (; product > limit; k++) product *= _rnd.NextDouble();
        return k;
    }
}