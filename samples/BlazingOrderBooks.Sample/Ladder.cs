using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BlazingOrderBooks.Sample;

/// <summary>
///     One side of an L2 book, the way a feed handler keeps it: a window of the venue's ticks, a size per tick in native
///     memory, indexed by the tick's low bits (<c>tick &amp; mask</c>: a ring over the window, so moving the window clears
///     what falls out of it and nothing else moves), and an occupancy bit per tick, so the best level and the next are
///     bit scans (TrailingZeroCount, LeadingZeroCount), not walks — with a summary bit per 64 of them, so a scan over an
///     empty stretch of a wide window is two or three words, not hundreds. The best is cached besides. Sizes and bits are
///     64-byte aligned. Nothing is allocated after construction; Dispose frees it. One writer: the thread that owns it.
///     <para>
///         <b>Prices.</b> Fixed-point longs (the wire's unit, 0.01), indexed by the venue's tick: a tick of 10 (0.10) puts
///         adjacent levels in adjacent slots, not ten apart. A price off the tick grid isn't the venue's: callers check
///         <see cref="OnGrid" />. A tick that's a power of two indexes by a shift; any other by a division.
///     </para>
///     <para>
///         <b>The window follows the touch.</b> It's placed with the best price a quarter of the way in from its better
///         edge — room for the price to improve, three quarters for depth behind it — and moved only when the touch would
///         leave it: a better price beyond its edge (a gap), the touch drifting within an eighth of its worse edge, or the
///         side's last level in it gone. A level worse than the best and beyond the window — a deep order, or the book a
///         jump left behind — is kept aside, in a short sorted list (<see cref="OverflowCapacity" /> levels), not dropped
///         and never at the expense of the touch: levels the window leaves go there, and come back when it returns. Only
///         when that list is full is a level lost (<see cref="Lost" />), the worst first: the caller then can't trust the
///         side, and resyncs.
///     </para>
/// </summary>
internal sealed unsafe class Ladder : IDisposable
{
    public const long None = long.MinValue;

    /// <summary>Levels kept beyond the window: more than a venue's published depth, so in practice never full.</summary>
    public const int OverflowCapacity = 64;

    private readonly ulong* _bits; // a bit per slot: a level there
    private readonly long[] _farPrices = new long[OverflowCapacity], _farSizes = new long[OverflowCapacity]; // best first
    private readonly int _mask, _words, _shift; // shift: log2 of the tick when it's a power of two, else −1
    private readonly int _sign; // +1: the best is the lowest price (asks); −1: the highest (bids)
    private readonly long* _sizes; // the size at each slot (0: no level)
    private readonly ulong* _summary; // a bit per word of _bits: a level in it
    private readonly long _tick;
    private long _base; // the window, in ticks: base ≤ t < base + Window
    private bool _disposed;
    private int _farCount, _inWindow;

    /// <param name="window">Ticks the window spans: a power of two, 64 or more.</param>
    /// <param name="ascending">Whether the best price is the lowest (asks) rather than the highest (bids).</param>
    /// <param name="around">A price to place the window at first.</param>
    /// <param name="tick">The venue's tick, in the price's unit.</param>
    public Ladder(int window, bool ascending, long around, long tick = 1)
    {
        if (window < 64 || !BitOperations.IsPow2(window))
            throw new ArgumentOutOfRangeException(nameof(window), "A power of two, 64 or more.");
        ArgumentOutOfRangeException.ThrowIfLessThan(tick, 1);
        Window = window;
        _tick = tick;
        _shift = BitOperations.IsPow2(tick) ? BitOperations.Log2((ulong)tick) : -1;
        _mask = window - 1;
        _words = window / 64;
        var summaryWords = (_words + 63) / 64;
        _sign = ascending ? 1 : -1;
        _sizes = (long*)NativeMemory.AlignedAlloc((nuint)window * sizeof(long), 64);
        _bits = (ulong*)NativeMemory.AlignedAlloc((nuint)_words * sizeof(ulong), 64);
        _summary = (ulong*)NativeMemory.AlignedAlloc((nuint)summaryWords * sizeof(ulong), 64);
        NativeMemory.Clear(_sizes, (nuint)window * sizeof(long));
        NativeMemory.Clear(_bits, (nuint)_words * sizeof(ulong));
        NativeMemory.Clear(_summary, (nuint)summaryWords * sizeof(ulong));
        _base = BaseFor(Ticks(around - around % tick));
    }

    public int Window { get; }

    /// <summary>The venue's tick, in the price's unit.</summary>
    public long Tick => _tick;

    /// <summary>Levels on this side, in the window and beyond it.</summary>
    public int Count => _inWindow + _farCount;

    /// <summary>The best price, or <see cref="None" />.</summary>
    public long Best
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        private set;
    } = None;

    /// <summary>Times the window moved.</summary>
    public long Recentres { get; private set; }

    /// <summary>Levels lost for want of room beyond the window: the side can't be trusted after one.</summary>
    public long Lost { get; private set; }

    /// <summary>The worst level, or <see cref="None" />.</summary>
    public long Worst
    {
        get
        {
            if (_farCount > 0) return _farPrices[_farCount - 1];
            var t = _sign > 0 ? WindowAtOrBelow(_base + Window - 1) : WindowAtOrAbove(_base);
            return t == None ? None : t * _tick;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMemory.AlignedFree(_sizes);
        NativeMemory.AlignedFree(_bits);
        NativeMemory.AlignedFree(_summary);
    }

    /// <summary>Whether <paramref name="price" /> is on the venue's tick grid.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool OnGrid(long price)
        => _shift >= 0 ? (price & (_tick - 1)) == 0 : price % _tick == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool InWindow(long price)
        => (ulong)(Ticks(price) - _base) < (ulong)Window; // one unsigned compare for both bounds

    /// <summary>The slot a price's tick lives in (its low bits): the same wherever the window is.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Slot(long price)
        => (int)(Ticks(price) & _mask);

    /// <summary>The size at <paramref name="price" />: 0 without a level there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Size(long price)
        => InWindow(price) ? _sizes[Slot(price)] : FarSize(price);

    /// <summary>The size at a slot known to hold a level.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long SizeAt(int slot)
        => _sizes[slot];

    /// <summary>Sets the level at <paramref name="price" /> to <paramref name="size" /> (0: gone); returns the size it had.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Set(long price, long size)
    {
        if (InWindow(price))
        {
            var slot = Slot(price);
            var was = _sizes[slot];
            _sizes[slot] = size;
            if (was > 0 != size > 0) Toggle(slot, price, size > 0);
            return was;
        }

        return SetBeyond(price, size);
    }

    /// <summary>
    ///     Adds <paramref name="delta" /> to the level at <paramref name="price" /> (never below zero); returns the new
    ///     size.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Add(long price, long delta)
    {
        var size = Math.Max(0, Size(price) + delta);
        Set(price, size);
        return size;
    }

    /// <summary>Whether <paramref name="a" /> is a better price than <paramref name="b" /> on this side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Better(long a, long b)
        => (a - b) * _sign < 0;

    /// <summary>The next level away from the touch after <paramref name="price" /> (not itself), or <see cref="None" />.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Next(long price)
        => From(_sign > 0 ? price + 1 : price - 1);

    /// <summary>
    ///     The first level at <paramref name="price" /> or past it, away from the touch (asks: at or above it; bids: at
    ///     or below), beyond the window too; or <see cref="None" />.
    /// </summary>
    public long From(long price)
    {
        long t;
        if (_sign > 0)
        {
            t = WindowAtOrAbove(Math.Max(CeilingTicks(price), _base));
            if (t != None) return t * _tick;
            for (var i = 0; i < _farCount; i++)
                if (_farPrices[i] >= price)
                    return _farPrices[i];
        }
        else
        {
            t = WindowAtOrBelow(Math.Min(FloorTicks(price), _base + Window - 1));
            if (t != None) return t * _tick;
            for (var i = 0; i < _farCount; i++)
                if (_farPrices[i] <= price)
                    return _farPrices[i];
        }

        return None;
    }

    /// <summary>
    ///     The best <paramref name="prices" />.Length levels, best first; returns how many there are. A walk by the bitmap:
    ///     each step a bit scan from the last.
    /// </summary>
    public int Top(Span<long> prices, Span<long> sizes)
    {
        var n = 0;
        for (var p = Best; p != None && n < prices.Length; p = Next(p))
        {
            prices[n] = p;
            sizes[n++] = Size(p);
        }

        return n;
    }

    /// <summary>Every level gone (the window stays where it is: the next level places it).</summary>
    public void Clear()
    {
        NativeMemory.Clear(_sizes, (nuint)Window * sizeof(long));
        NativeMemory.Clear(_bits, (nuint)_words * sizeof(ulong));
        NativeMemory.Clear(_summary, (nuint)((_words + 63) / 64) * sizeof(ulong));
        _inWindow = _farCount = 0;
        Best = None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long Ticks(long price)
        => _shift >= 0 ? price >> _shift : price / _tick; // (on the grid, both exact)

    private long CeilingTicks(long price)
        => price == long.MinValue ? long.MinValue / _tick : -FloorTicks(-price);

    private long FloorTicks(long price)
        => _shift >= 0 ? price >> _shift : price / _tick - (price % _tick < 0 ? 1 : 0);

    /// <summary>The window's base with the touch at <paramref name="touch" /> ticks: a quarter in from the better edge.</summary>
    private long BaseFor(long touch)
        => _sign > 0 ? touch - Window / 4 : touch + Window / 4 - Window + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long TicksAt(int slot)
        => _base + ((slot - (int)(_base & _mask)) & _mask);

    /// <summary>A level outside the window: a new touch moves the window to it; a deeper one is kept aside.</summary>
    private long SetBeyond(long price, long size)
    {
        if (size <= 0) return RemoveFar(price);
        if (Count == 0 || Better(price, Best)) // a new touch beyond the window (a gap): the window goes to it
        {
            Recentre(price);
            var slot = Slot(price);
            _sizes[slot] = size;
            Toggle(slot, price, true);
            return 0;
        }

        return SetFar(price, size); // worse than the best and beyond the window: deep, kept aside
    }

    /// <summary>Flips the slot's occupancy bit (and its word's summary bit); keeps the counts and the cached best.</summary>
    private void Toggle(int slot, long price, bool on)
    {
        var w = slot >> 6;
        var before = _bits[w];
        _bits[w] = before ^ (1UL << (slot & 63));
        if (before == 0 || _bits[w] == 0) _summary[w >> 6] ^= 1UL << (w & 63); // the word filled or emptied
        if (on)
        {
            _inWindow++;
            if (Best == None || Better(price, Best)) Best = price;
            return;
        }

        _inWindow--;
        if (price != Best) return;
        Best = Count == 0 ? None : Next(price);
        if (Best == None) return;
        if (!InWindow(Best)) Recentre(Best); // the window's last level gone: the touch is beyond it, so it follows
        else if ((_sign > 0 ? _base + Window - 1 - Ticks(Best) : Ticks(Best) - _base) < Window / 8)
            Recentre(Best); // the touch has drifted near the worse edge: room for depth behind it again
    }

    /// <summary>
    ///     Moves the window to put <paramref name="touch" /> — the side's best, or a better price about to be — a quarter
    ///     in from its better edge. Every level is the touch or worse, so what falls out of the window is on its worse side:
    ///     kept aside; what's kept aside and now inside it comes back.
    /// </summary>
    private void Recentre(long touch)
    {
        Recentres++;
        var newBase = BaseFor(Ticks(touch));
        for (var w = 0; w < _words; w++)
        {
            var word = _bits[w];
            while (word != 0)
            {
                var slot = (w << 6) + BitOperations.TrailingZeroCount(word);
                word &= word - 1;
                var t = TicksAt(slot);
                if ((ulong)(t - newBase) < (ulong)Window) continue;
                var size = _sizes[slot];
                _sizes[slot] = 0;
                _bits[w] &= ~(1UL << (slot & 63));
                _inWindow--;
                SetFar(t * _tick, size);
            }

            if (_bits[w] == 0) _summary[w >> 6] &= ~(1UL << (w & 63));
        }

        _base = newBase;
        while (_farCount > 0 && InWindow(_farPrices[0])) // back from beyond: the best of them first
        {
            var price = _farPrices[0];
            var slot = Slot(price);
            _sizes[slot] = _farSizes[0];
            RemoveFarAt(0);
            var w = slot >> 6;
            if (_bits[w] == 0) _summary[w >> 6] |= 1UL << (w & 63);
            _bits[w] |= 1UL << (slot & 63);
            _inWindow++;
        }
    }

    private long FarSize(long price)
    {
        for (var i = 0; i < _farCount; i++)
            if (_farPrices[i] == price)
                return _farSizes[i];
        return 0;
    }

    /// <summary>Sets a level kept aside, in order (best first); full, the worst is lost. Returns the size it had.</summary>
    private long SetFar(long price, long size)
    {
        var i = 0;
        while (i < _farCount && Better(_farPrices[i], price)) i++;
        if (i < _farCount && _farPrices[i] == price)
        {
            var was = _farSizes[i];
            _farSizes[i] = size;
            return was;
        }

        if (_farCount == OverflowCapacity)
        {
            Lost++;
            if (i == _farCount) return 0; // the worst of them all: it's the one lost
            _farCount--; // the worst goes to make room
        }

        Array.Copy(_farPrices, i, _farPrices, i + 1, _farCount - i);
        Array.Copy(_farSizes, i, _farSizes, i + 1, _farCount - i);
        _farPrices[i] = price;
        _farSizes[i] = size;
        _farCount++;
        if (Best == None || Better(price, Best)) Best = price; // (the window empty: only when it's cleared)
        return 0;
    }

    private long RemoveFar(long price)
    {
        for (var i = 0; i < _farCount; i++)
        {
            if (_farPrices[i] != price) continue;
            var was = _farSizes[i];
            RemoveFarAt(i);
            return was;
        }

        return 0;
    }

    private void RemoveFarAt(int i)
    {
        _farCount--;
        Array.Copy(_farPrices, i + 1, _farPrices, i, _farCount - i);
        Array.Copy(_farSizes, i + 1, _farSizes, i, _farCount - i);
    }

    /// <summary>The lowest occupied tick in the window at or above <paramref name="from" />, or <see cref="None" />.</summary>
    private long WindowAtOrAbove(long from)
    {
        if (from < _base) from = _base;
        var end = _base + Window - 1;
        if (from > end || _inWindow == 0) return None;
        int s0 = (int)(from & _mask), s1 = (int)(end & _mask);
        if (s0 <= s1) return TickOf(ScanUp(s0, s1));
        var hit = ScanUp(s0, _mask); // the slots wrap: the top of the ring first, then its bottom
        return TickOf(hit >= 0 ? hit : ScanUp(0, s1));
    }

    /// <summary>The highest occupied tick in the window at or below <paramref name="from" />, or <see cref="None" />.</summary>
    private long WindowAtOrBelow(long from)
    {
        var end = _base + Window - 1;
        if (from > end) from = end;
        if (from < _base || _inWindow == 0) return None;
        int s0 = (int)(from & _mask), s1 = (int)(_base & _mask);
        if (s1 <= s0) return TickOf(ScanDown(s0, s1));
        var hit = ScanDown(s0, 0);
        return TickOf(hit >= 0 ? hit : ScanDown(_mask, s1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long TickOf(int slot)
        => slot < 0 ? None : TicksAt(slot);

    /// <summary>The first set bit in slots [from, to], or −1: the words in between skipped by their summary.</summary>
    private int ScanUp(int from, int to)
    {
        int w = from >> 6, last = to >> 6;
        var word = _bits[w] & (~0UL << (from & 63));
        if (w == last) word &= ~0UL >> (63 - (to & 63));
        if (word != 0) return (w << 6) + BitOperations.TrailingZeroCount(word);
        if (w == last) return -1;
        w = WordUp(w + 1, last);
        if (w < 0) return -1;
        word = _bits[w];
        if (w == last) word &= ~0UL >> (63 - (to & 63));
        return word != 0 ? (w << 6) + BitOperations.TrailingZeroCount(word) : -1;
    }

    /// <summary>The last set bit in slots [to, from], or −1.</summary>
    private int ScanDown(int from, int to)
    {
        int w = from >> 6, last = to >> 6;
        var word = _bits[w] & (~0UL >> (63 - (from & 63)));
        if (w == last) word &= ~0UL << (to & 63);
        if (word != 0) return (w << 6) + 63 - BitOperations.LeadingZeroCount(word);
        if (w == last) return -1;
        w = WordDown(w - 1, last);
        if (w < 0) return -1;
        word = _bits[w];
        if (w == last) word &= ~0UL << (to & 63);
        return word != 0 ? (w << 6) + 63 - BitOperations.LeadingZeroCount(word) : -1;
    }

    /// <summary>The first word in [from, to] with a level, by the summary, or −1.</summary>
    private int WordUp(int from, int to)
    {
        int s = from >> 6, last = to >> 6;
        var word = _summary[s] & (~0UL << (from & 63));
        while (true)
        {
            if (s == last) word &= ~0UL >> (63 - (to & 63));
            if (word != 0) return (s << 6) + BitOperations.TrailingZeroCount(word);
            if (s == last) return -1;
            word = _summary[++s];
        }
    }

    /// <summary>The last word in [to, from] with a level, by the summary, or −1.</summary>
    private int WordDown(int from, int to)
    {
        int s = from >> 6, last = to >> 6;
        var word = _summary[s] & (~0UL >> (63 - (from & 63)));
        while (true)
        {
            if (s == last) word &= ~0UL << (to & 63);
            if (word != 0) return (s << 6) + 63 - BitOperations.LeadingZeroCount(word);
            if (s == last) return -1;
            word = _summary[--s];
        }
    }
}

/// <summary>Both sides of an L2 book (<see cref="Ladder" />), bids and asks, with the derived prices.</summary>
internal sealed class L2Book : IDisposable
{
    public readonly Ladder Asks, Bids;

    public L2Book(int window, long around, long tick = 1)
    {
        Bids = new Ladder(window, false, around, tick);
        Asks = new Ladder(window, true, around, tick);
    }

    /// <summary>The mid price (rounded down), or 0 while a side is empty.</summary>
    public long Mid
    {
        get
        {
            long b = Bids.Best, a = Asks.Best;
            return b == Ladder.None || a == Ladder.None ? 0 : (b + a) / 2;
        }
    }

    /// <summary>Levels lost on either side for want of room beyond the window (<see cref="Ladder.Lost" />).</summary>
    public long Lost => Bids.Lost + Asks.Lost;

    /// <summary>Times either side's window moved.</summary>
    public long Recentres => Bids.Recentres + Asks.Recentres;

    public void Dispose()
    {
        Bids.Dispose();
        Asks.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Ladder Side(byte side)
    {
        return side == Level.Ask ? Asks : Bids;
    }

    public void Clear()
    {
        Bids.Clear();
        Asks.Clear();
    }
}
