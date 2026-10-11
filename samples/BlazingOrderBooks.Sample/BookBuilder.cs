using System.Runtime.CompilerServices;

namespace BlazingOrderBooks.Sample;

/// <summary>A message's net change to a book's levels, for the posts: levels and the size change of each.</summary>
internal sealed class ChangeSet(int capacity)
{
    public readonly long[] Deltas = new long[capacity];
    public readonly Level[] Levels = new Level[capacity];
    public int Count;

    public ReadOnlySpan<Level> LevelSpan => Levels.AsSpan(0, Count);
    public ReadOnlySpan<long> DeltaSpan => Deltas.AsSpan(0, Count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(Level level, long delta)
    {
        Levels[Count] = level;
        Deltas[Count++] = delta;
    }
}

/// <summary>
///     Builds the books from the wire. Decodes each message in place and keeps the <b>authoritative</b> L2 book
///     (<see cref="Book" />): the depth feed applied as it comes, each update checked against the sequence it should
///     follow — a gap, and it waits for a snapshot (the handler asks the exchange for one), applying only depth that
///     reaches past the snapshot. Every message leaves its net level changes in <see cref="BookChanges" /> and
///     <see cref="AcceleratedChanges" /> for the handler to post.
///     <para>
///         The <b>accelerated</b> book is the authoritative one seen through the prints the depth has yet to catch up
///         with: an overlay, not a copy. A buyer's print at p for q says the asks below p are gone and the ask at p is q
///         smaller (never below zero) — and, since the book never crosses, that no bid at or above p is left; a seller's
///         print, the mirror. The overlay is two sweep boundaries (asks below one gone, bids above the other) and a size
///         adjustment per printed level; a level shows as its authoritative size plus its adjustment, or nothing inside
///         a sweep. When a depth update arrives reflecting prints through trade seq T, the authoritative book absorbs
///         them, the overlay is rebuilt from the prints after T alone, and the levels whose shown size moved are posted.
///         Each print's effect on the top of the book is a prediction, judged by the depth update that catches up with it
///         (<see cref="FeedStats" />).
///     </para>
///     <para>
///         Prices and gaps: every price must be on the venue's tick grid (one that isn't makes the update untrustworthy:
///         a resync). A depth update's removals are applied before its additions, so a book that jumped moves its window
///         once, to the new touch, with nothing of the old book in the way. Levels beyond the window are the ladder's to
///         keep (<see cref="Ladder" />); one it had no room for is a lost level, and the book resyncs. A snapshot it can't
///         hold whole — a book wider than the window and its overflow — isn't resynced in a loop: it's
///         <see cref="Outcome.TooWide" />, said once, and the handler tries again later.
///     </para>
///     <para>
///         Nothing is allocated after construction: the dirty levels of a message are tracked by a stamp per slot
///         (no set, no scan to dedupe; the few beyond the window in a short list), adjustments live in slot-indexed
///         arrays, prints in a ring.
///     </para>
/// </summary>
internal sealed unsafe class BookBuilder : IDisposable
{
    public enum Outcome : byte
    {
        Trade,
        Depth,
        Snapshot,
        Stale, // a depth update reflecting nothing new (before a snapshot caught up)
        Gap, // the sequence broke, or the book can't be trusted: waiting for a snapshot
        TooWide // a snapshot the books can't hold whole: waiting for another, later
    }

    private const int MaxPrints = 256;
    private const byte TopKind = 2; // a print-ring entry that's a best bid and offer, not a print
    private readonly long[] _adjustAsk, _adjustBid; // per slot: the overlay's size adjustment there
    private readonly Level[] _adjusted; // the levels adjusted (to clear and to mark)
    private readonly Level[] _dirty; // this message's levels whose shown size may have moved
    private readonly Level[] _farDirty = new Level[2 * Ladder.OverflowCapacity + 8]; // …of them, beyond the window
    private readonly long[] _farShown = new long[2 * Ladder.OverflowCapacity + 8]; // their shown size when marked
    private readonly Print[] _prints = new Print[MaxPrints]; // since the depth caught up: a ring
    private readonly long[] _shownAsk, _shownBid; // per slot: the shown size when marked dirty
    private readonly int[] _stampAsk, _stampBid; // per slot: the generation it was marked in
    public readonly ChangeSet AcceleratedChanges, BookChanges;
    public readonly L2Book Book;
    public readonly FeedStats Stats;
    private int _adjustedCount, _dirtyCount, _farDirtyCount, _gen = 1, _printHead, _printCount;
    private long _askSweptBelow = long.MinValue, _bidSweptAbove = long.MaxValue;
    private long _expectedFirst = 1; // the first event the next depth update must reflect
    private Prediction _predictAsk, _predictBid;

    public BookBuilder(int window, long around, FeedStats stats, long tick = 1)
    {
        Stats = stats;
        Book = new L2Book(window, around, tick);
        _stampBid = new int[window];
        _stampAsk = new int[window];
        _shownBid = new long[window];
        _shownAsk = new long[window];
        _adjustBid = new long[window];
        _adjustAsk = new long[window];
        _adjusted = new Level[2 * window];
        _dirty = new Level[2 * window];
        BookChanges = new ChangeSet(2 * window);
        AcceleratedChanges = new ChangeSet(2 * window);
    }

    /// <summary>Waiting for a snapshot (at first, and after a gap): the book isn't to be trusted or posted.</summary>
    public bool Recovering { get; private set; } = true;

    /// <summary>The exchange seq the authoritative book is as of: the last depth update's, or the snapshot's.</summary>
    public long Version { get; private set; }

    /// <summary>The exchange seq of the message last handled (a print's own; a depth update's last).</summary>
    public long MessageSeq { get; private set; }

    /// <summary>
    ///     The message moved a window: its changes to the accelerated book aren't gathered (a level's slot may have meant
    ///     another price before), so the handler posts that book whole — rare, and no bigger than a few dozen levels.
    /// </summary>
    public bool AcceleratedWhole { get; private set; }

    /// <summary>The exchange's timestamp on the message last handled.</summary>
    public long MessageTimestamp { get; private set; }

    public void Dispose()
    {
        Book.Dispose();
    }

    /// <summary>
    ///     Handles the message at <paramref name="at" /> (read in place), as of <paramref name="nowNs" />: the books
    ///     updated, their changes in <see cref="BookChanges" /> and <see cref="AcceleratedChanges" />.
    /// </summary>
    public Outcome Handle(byte* at, long nowNs)
    {
        BookChanges.Count = 0;
        AcceleratedChanges.Count = 0;
        AcceleratedWhole = false;
        ref var header = ref Wire.At<Wire.Header>(at);
        MessageSeq = header.Seq;
        switch (header.Template)
        {
            case Wire.TradeTemplate:
                return OnTrade(ref Wire.At<Wire.Trade>(at), nowNs);
            case Wire.TopTemplate:
                return OnTop(ref Wire.At<Wire.Top>(at), nowNs);
            case Wire.DepthTemplate:
                return OnDepth(ref Wire.At<Wire.Depth>(at), (Wire.DepthLevel*)(at + sizeof(Wire.Depth)), nowNs);
            default:
                return OnSnapshot(ref Wire.At<Wire.Depth>(at), (Wire.DepthLevel*)(at + sizeof(Wire.Depth)));
        }
    }

    /// <summary>
    ///     The whole book — the authoritative one, or the accelerated one as shown — into <paramref name="levels" /> and
    ///     <paramref name="sizes" /> (bids then asks, best first); returns how many levels.
    /// </summary>
    public int Fill(Span<Level> levels, Span<long> sizes, bool accelerated)
    {
        var n = 0;
        for (var p = Book.Bids.Best; p != Ladder.None && n < levels.Length; p = Book.Bids.Next(p))
        {
            var size = accelerated ? Shown(Level.Bid, p) : Book.Bids.Size(p);
            if (size == 0) continue;
            levels[n] = new Level(Level.Bid, p);
            sizes[n++] = size;
        }

        for (var p = Book.Asks.Best; p != Ladder.None && n < levels.Length; p = Book.Asks.Next(p))
        {
            var size = accelerated ? Shown(Level.Ask, p) : Book.Asks.Size(p);
            if (size == 0) continue;
            levels[n] = new Level(Level.Ask, p);
            sizes[n++] = size;
        }

        if (!accelerated) return n;
        for (var i = 0; i < _adjustedCount && n < levels.Length; i++) // levels the overlay alone has: a top not yet in depth
        {
            var level = _adjusted[i];
            if (Book.Side(level.Side).Size(level.Price) != 0) continue;
            var size = Shown(level.Side, level.Price);
            if (size <= 0) continue;
            levels[n] = level;
            sizes[n++] = size;
        }

        return n;
    }

    private Outcome OnTrade(ref Wire.Trade trade, long nowNs)
    {
        Stats.Trades++;
        MessageTimestamp = trade.Timestamp;
        if (_printCount == MaxPrints) // the oldest goes: the overlay is the poorer for it
        {
            _printHead = (_printHead + 1) & (MaxPrints - 1);
            _printCount--;
            Stats.TradesOverflowed++;
        }

        if (!Book.Asks.OnGrid(trade.Price)) // not a price the venue trades at: not one to act on
        {
            Stats.OffGrid++;
            return Outcome.Trade;
        }

        _prints[(_printHead + _printCount++) & (MaxPrints - 1)] =
            new Print(trade.Header.Seq, trade.Price, trade.Size, trade.Aggressor);
        if (Recovering) return Outcome.Trade; // kept for after the snapshot

        NewMessage();
        var passive = trade.Aggressor == Level.Bid ? Level.Ask : Level.Bid;
        AcceleratedTop(passive, out var priceBefore, out var sizeBefore);
        Apply(trade.Price, trade.Size, trade.Aggressor);
        EmitAccelerated();

        AcceleratedTop(passive, out var price, out var size);
        if (price != priceBefore || size != sizeBefore) // the top moved on the print: a prediction, judged by the depth
        {
            ref var prediction = ref passive == Level.Ask ? ref _predictAsk : ref _predictBid;
            prediction = new Prediction(true, price != priceBefore, price, size, trade.Header.Seq, nowNs);
        }

        return Outcome.Trade;
    }

    /// <summary>
    ///     The venue's best bid and offer: the accelerated book shows nothing better on either side and each at its size,
    ///     until a depth update reflecting it arrives. Kept with the prints and replayed as they are, in order.
    /// </summary>
    private Outcome OnTop(ref Wire.Top top, long nowNs)
    {
        Stats.Tops++;
        MessageTimestamp = top.Timestamp;
        if (top.BidSize <= 0 || top.AskSize <= 0 || top.BidPrice >= top.AskPrice || !Book.Asks.OnGrid(top.BidPrice) ||
            !Book.Asks.OnGrid(top.AskPrice))
        {
            Stats.OffGrid++; // (not a top this book can show)
            return Outcome.Trade;
        }

        // A top states what's best on both sides outright: it supersedes every print and top before it (whatever they
        // swept is behind it, hidden), so the ring starts again from it — a busy instrument's hundreds of tops between
        // two depth updates replay as one.
        _printHead = (_printHead + _printCount) & (MaxPrints - 1);
        _printCount = 0;
        _prints[_printHead] = new Print(top.Header.Seq, top.BidPrice, top.BidSize, TopKind, top.AskPrice, top.AskSize);
        _printCount = 1;
        if (Recovering) return Outcome.Trade;

        NewMessage();
        AcceleratedTop(Level.Bid, out var bidBefore, out var bidSizeBefore);
        AcceleratedTop(Level.Ask, out var askBefore, out var askSizeBefore);
        ApplyTop(top.BidPrice, top.BidSize, top.AskPrice, top.AskSize);
        EmitAccelerated();
        Predict(ref _predictBid, Level.Bid, bidBefore, bidSizeBefore, top.Header.Seq, nowNs);
        Predict(ref _predictAsk, Level.Ask, askBefore, askSizeBefore, top.Header.Seq, nowNs);
        return Outcome.Trade;
    }

    /// <summary>A side's accelerated top moved: a prediction, judged by the depth update that catches up with it.</summary>
    private void Predict(ref Prediction prediction, byte side, long priceBefore, long sizeBefore, long seq, long nowNs)
    {
        AcceleratedTop(side, out var price, out var size);
        if (price != priceBefore || size != sizeBefore)
            prediction = new Prediction(true, price != priceBefore, price, size, seq, nowNs);
    }

    /// <summary>
    ///     A best bid and offer on the overlay: the sweep boundaries put at them (levels better than either hidden; ones a
    ///     print hid that are no better shown again), and each at its size — the levels whose shown size moves marked
    ///     first.
    /// </summary>
    private void ApplyTop(long bid, long bidSize, long ask, long askSize)
    {
        MarkAdjusted();
        long from = Math.Max(_bidSweptAbove, bid), to = Math.Min(_bidSweptAbove, bid);
        for (var p = Book.Bids.From(from); p != Ladder.None && p > to; p = Book.Bids.Next(p)) Mark(Level.Bid, p);
        from = Math.Min(_askSweptBelow, ask);
        to = Math.Max(_askSweptBelow, ask);
        for (var p = Book.Asks.From(from); p != Ladder.None && p < to; p = Book.Asks.Next(p)) Mark(Level.Ask, p);
        Mark(Level.Bid, bid);
        Mark(Level.Ask, ask);
        _bidSweptAbove = bid;
        _askSweptBelow = ask;
        AdjustTo(Level.Bid, bid, bidSize);
        AdjustTo(Level.Ask, ask, askSize);
    }

    /// <summary>The overlay's adjustment at a level set so the level shows <paramref name="size" />.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdjustTo(byte side, long price, long size)
    {
        var ladder = Book.Side(side);
        if (!ladder.InWindow(price)) return; // (a window from the book: shown as the depth has it)
        var adjust = side == Level.Ask ? _adjustAsk : _adjustBid;
        var slot = ladder.Slot(price);
        if (adjust[slot] == 0) _adjusted[_adjustedCount++] = new Level(side, price);
        adjust[slot] = size - ladder.SizeAt(slot);
    }

    private Outcome OnDepth(ref Wire.Depth depth, Wire.DepthLevel* levels, long nowNs)
    {
        Stats.Depths++;
        MessageTimestamp = depth.Timestamp;
        long first = depth.FirstSeq, last = depth.Header.Seq;
        if (Recovering || last < _expectedFirst) return Outcome.Stale;
        if (first > _expectedFirst) // events missing in between: the book can't be trusted
        {
            Stats.Gaps++;
            Recovering = true;
            return Outcome.Gap;
        }

        for (var i = 0; i < depth.Count; i++)
            if (!Book.Asks.OnGrid(levels[i].Price)) // off the venue's grid: the update isn't the venue's book
            {
                Stats.OffGrid++;
                Recovering = true;
                return Outcome.Gap;
            }

        // What the update and the overlay touch, as shown now: the levels whose shown size may move.
        NewMessage();
        for (var i = 0; i < depth.Count; i++) Mark(levels[i].Side, levels[i].Price);
        for (var i = 0; i < _adjustedCount; i++) Mark(_adjusted[i].Side, _adjusted[i].Price);
        for (var p = Book.Asks.Best; p != Ladder.None && p < _askSweptBelow; p = Book.Asks.Next(p)) Mark(Level.Ask, p);
        for (var p = Book.Bids.Best; p != Ladder.None && p > _bidSweptAbove; p = Book.Bids.Next(p)) Mark(Level.Bid, p);

        // The authoritative book absorbs the update: each level's size as it stands — what it removes first, so a book
        // that jumped has nothing of its old self left to move the window past.
        long lost = Book.Lost, recentres = Book.Recentres;
        for (var pass = 0; pass < 2; pass++)
        for (var i = 0; i < depth.Count; i++)
        {
            var level = levels[i];
            if (level.Size == 0 != (pass == 0)) continue;
            var was = Book.Side(level.Side).Set(level.Price, level.Size);
            if (level.Size != was) BookChanges.Add(new Level(level.Side, level.Price), level.Size - was);
        }

        Stats.Recentres += Book.Recentres - recentres;
        AcceleratedWhole = Book.Recentres != recentres;
        if (Book.Lost != lost) // a level with no room for it, beyond the window: the book is short of it — resync
        {
            Stats.Lost += Book.Lost - lost;
            Recovering = true;
            return Outcome.Gap;
        }

        // The overlay keeps the prints the depth hasn't caught up with, re-applied to the book as it is now.
        Rebuild(depth.LastTradeSeq);
        if (!AcceleratedWhole) EmitAccelerated();
        Judge(ref _predictAsk, Book.Asks, depth.LastTradeSeq, nowNs);
        Judge(ref _predictBid, Book.Bids, depth.LastTradeSeq, nowNs);
        _expectedFirst = last + 1;
        Version = last;
        return Outcome.Depth;
    }

    /// <summary>The whole book, from the recovery channel: the books start over from it.</summary>
    private Outcome OnSnapshot(ref Wire.Depth snapshot, Wire.DepthLevel* levels)
    {
        Stats.Snapshots++;
        MessageTimestamp = snapshot.Timestamp;
        Book.Clear();
        long lost = Book.Lost, recentres = Book.Recentres;
        var offGrid = false;
        for (var i = 0; i < snapshot.Count; i++)
        {
            offGrid |= !Book.Asks.OnGrid(levels[i].Price);
            Book.Side(levels[i].Side).Set(levels[i].Price, levels[i].Size);
        }

        Stats.Recentres += Book.Recentres - recentres;
        if (offGrid || Book.Lost != lost) // not a book this handler can hold whole: no use asking again at once
        {
            if (offGrid) Stats.OffGrid++;
            Stats.Lost += Book.Lost - lost;
            Stats.TooWide++;
            Book.Clear();
            Recovering = true;
            return Outcome.TooWide;
        }

        NewMessage();
        Rebuild(snapshot.LastTradeSeq);
        _predictAsk.Pending = _predictBid.Pending = false;
        _expectedFirst = snapshot.Header.Seq + 1;
        Version = snapshot.Header.Seq;
        Recovering = false;
        return Outcome.Snapshot;
    }

    /// <summary>A print's effect on the overlay, the levels it moves marked first.</summary>
    private void Apply(long price, long size, byte aggressor)
    {
        MarkAdjusted();
        if (aggressor == Level.Bid) // a buyer took the asks up to price: none left below, no bid left at or above
        {
            for (var p = Book.Asks.From(_askSweptBelow); p != Ladder.None && p < price; p = Book.Asks.Next(p))
                Mark(Level.Ask, p);
            for (var p = Book.Bids.From(_bidSweptAbove); p != Ladder.None && p >= price; p = Book.Bids.Next(p))
                Mark(Level.Bid, p);
            Mark(Level.Ask, price);
            _askSweptBelow = Math.Max(_askSweptBelow, price);
            _bidSweptAbove = Math.Min(_bidSweptAbove, price - 1);
            Adjust(Level.Ask, price, -size);
        }
        else // a seller hit the bids down to price
        {
            for (var p = Book.Bids.From(_bidSweptAbove); p != Ladder.None && p > price; p = Book.Bids.Next(p))
                Mark(Level.Bid, p);
            for (var p = Book.Asks.From(_askSweptBelow); p != Ladder.None && p <= price; p = Book.Asks.Next(p))
                Mark(Level.Ask, p);
            Mark(Level.Bid, price);
            _bidSweptAbove = Math.Min(_bidSweptAbove, price);
            _askSweptBelow = Math.Max(_askSweptBelow, price + 1);
            Adjust(Level.Bid, price, -size);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Adjust(byte side, long price, long delta)
    {
        var ladder = Book.Side(side);
        if (!ladder.InWindow(price)) return; // a print a window away from the book: nothing to adjust
        var adjust = side == Level.Ask ? _adjustAsk : _adjustBid;
        var slot = ladder.Slot(price);
        if (adjust[slot] == 0) _adjusted[_adjustedCount++] = new Level(side, price);
        adjust[slot] += delta;
    }

    /// <summary>The overlay from the prints after <paramref name="throughTradeSeq" /> alone, on the book as it is now.</summary>
    private void Rebuild(long throughTradeSeq)
    {
        for (var i = 0; i < _adjustedCount; i++)
            (_adjusted[i].Side == Level.Ask ? _adjustAsk : _adjustBid)[
                Book.Side(_adjusted[i].Side).Slot(_adjusted[i].Price)] = 0;
        _adjustedCount = 0;
        _askSweptBelow = long.MinValue;
        _bidSweptAbove = long.MaxValue;
        while (_printCount > 0 && _prints[_printHead].Seq <= throughTradeSeq)
        {
            _printHead = (_printHead + 1) & (MaxPrints - 1);
            _printCount--;
        }

        for (var i = 0; i < _printCount; i++)
        {
            var print = _prints[(_printHead + i) & (MaxPrints - 1)];
            if (print.Aggressor == TopKind) ApplyTop(print.Price, print.Size, print.AskPrice, print.AskSize);
            else Apply(print.Price, print.Size, print.Aggressor);
        }
    }

    /// <summary>
    ///     The size the accelerated book shows at a level: authoritative plus adjustment, nothing inside a sweep; beyond
    ///     the window, the authoritative size (prints there, a window from the touch, adjust nothing).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long Shown(byte side, long price)
    {
        var ladder = Book.Side(side);
        if (side == Level.Ask ? price < _askSweptBelow : price > _bidSweptAbove) return 0;
        if (!ladder.InWindow(price)) return ladder.Size(price);
        var slot = ladder.Slot(price);
        return Math.Max(0, ladder.SizeAt(slot) + (side == Level.Ask ? _adjustAsk : _adjustBid)[slot]);
    }

    /// <summary>
    ///     The levels the overlay has adjusted, marked: some exist in the overlay alone (a top the depth hasn't caught up
    ///     with), so walking the book's levels wouldn't find them when a sweep hides them. There are only ever a few.
    /// </summary>
    private void MarkAdjusted()
    {
        for (var i = 0; i < _adjustedCount; i++) Mark(_adjusted[i].Side, _adjusted[i].Price);
    }

    /// <summary>A message begins: nothing of it marked yet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NewMessage()
    {
        _gen++;
        _dirtyCount = _farDirtyCount = 0;
    }

    /// <summary>Notes a level whose shown size may move, with the size it shows now (once per message).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Mark(byte side, long price)
    {
        var ladder = Book.Side(side);
        if (!ladder.InWindow(price))
        {
            MarkFar(side, price);
            return;
        }

        var slot = ladder.Slot(price);
        var stamps = side == Level.Ask ? _stampAsk : _stampBid;
        if (stamps[slot] == _gen) return;
        stamps[slot] = _gen;
        (side == Level.Ask ? _shownAsk : _shownBid)[slot] = Shown(side, price);
        _dirty[_dirtyCount++] = new Level(side, price);
    }

    /// <summary>
    ///     A level beyond the window, marked: in a short list, deduped by a scan (there are only ever a few — deep levels,
    ///     or a jumped book's new ones before the window gets to them).
    /// </summary>
    private void MarkFar(byte side, long price)
    {
        var level = new Level(side, price);
        for (var i = 0; i < _farDirtyCount; i++)
            if (_farDirty[i] == level)
                return;
        if (_farDirtyCount == _farDirty.Length) return; // (more than the overflow holds: the resync will see to it)
        _farShown[_farDirtyCount] = Shown(side, price);
        _farDirty[_farDirtyCount++] = level;
    }

    /// <summary>The marked levels whose shown size moved, as changes to the accelerated book.</summary>
    private void EmitAccelerated()
    {
        for (var i = 0; i < _farDirtyCount; i++)
        {
            var level = _farDirty[i];
            var delta = Shown(level.Side, level.Price) - _farShown[i];
            if (delta != 0) AcceleratedChanges.Add(level, delta);
        }

        for (var i = 0; i < _dirtyCount; i++)
        {
            var level = _dirty[i];
            var slot = Book.Side(level.Side).Slot(level.Price);
            var delta = Shown(level.Side, level.Price) - (level.Side == Level.Ask ? _shownAsk : _shownBid)[slot];
            if (delta != 0) AcceleratedChanges.Add(level, delta);
        }
    }

    /// <summary>
    ///     The accelerated book's best level on a side: the first shown, of the book's levels and of those the overlay
    ///     alone has (a best bid or offer the depth hasn't caught up with).
    /// </summary>
    private void AcceleratedTop(byte side, out long price, out long size)
    {
        var ladder = Book.Side(side);
        price = Ladder.None;
        size = 0;
        for (var p = ladder.Best; p != Ladder.None; p = ladder.Next(p))
        {
            var shown = Shown(side, p);
            if (shown <= 0) continue;
            price = p;
            size = shown;
            break;
        }

        for (var i = 0; i < _adjustedCount; i++)
        {
            var level = _adjusted[i];
            if (level.Side != side || ladder.Size(level.Price) != 0) continue;
            var shown = Shown(side, level.Price);
            if (shown > 0 && (price == Ladder.None || ladder.Better(level.Price, price)))
            {
                price = level.Price;
                size = shown;
            }
        }
    }

    /// <summary>The accelerated book's best bid and ask: its top, for a micro price.</summary>
    public void AcceleratedTop(out long bid, out long bidSize, out long ask, out long askSize)
    {
        AcceleratedTop(Level.Bid, out bid, out bidSize);
        AcceleratedTop(Level.Ask, out ask, out askSize);
    }

    /// <summary>A prediction the depth has caught up with: right or wrong, and how long it led.</summary>
    private void Judge(ref Prediction prediction, Ladder ladder, long throughTradeSeq, long nowNs)
    {
        if (!prediction.Pending || prediction.TradeSeq > throughTradeSeq) return;
        prediction.Pending = false;
        var best = ladder.Best;
        if (prediction.Moved) Stats.Moves++;
        if (best != prediction.Price)
        {
            Stats.Missed++;
            return;
        }

        Stats.PriceConfirmed++;
        if (prediction.Moved) Stats.MovesConfirmed++;
        if (ladder.Size(best) == prediction.Size) Stats.Confirmed++;
        Stats.Lead.Record((nowNs - prediction.AtNs) / 1000);
    }

    /// <summary>A print (its aggressor's side), or a best bid and offer (<see cref="TopKind" />: bid in Price and Size).</summary>
    private readonly record struct Print(
        long Seq,
        long Price,
        long Size,
        byte Aggressor,
        long AskPrice = 0,
        long AskSize = 0);

    /// <summary>The top of a side as a print left it (<paramref name="Moved" />: at a new price), till the depth judges it.</summary>
    private record struct Prediction(bool Pending, bool Moved, long Price, long Size, long TradeSeq, long AtNs);
}