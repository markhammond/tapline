using System.Globalization;
using Tapline;

namespace BlazingOrderBooks.Sample;

/// <summary>
///     A book's shape, as a subscription asks it after its topic (<c>book?depth=10&amp;group=10&amp;price=vwap</c>): its
///     depth, the best levels a side (0: all); its grouping, the price levels a shown level spans in ticks (1: each its
///     own); and its sizes and prices — each level's own (a group's, its nearer edge), or cumulative (<see cref="Vwap" />):
///     each level the size from the best price down to it, at the volume-weighted average price of filling all of it.
///     Served from a few of each, a request rounded up to the next — so subscribers asking alike
///     share a topic — and the whole book (all levels, ungrouped, each at its price) is the topic itself.
/// </summary>
internal readonly record struct BookShape(int Depth, int Group, bool Vwap = false)
{
    /// <summary>The depths served, besides all (0).</summary>
    public static readonly int[] Depths = [5, 10, 20];

    /// <summary>The groupings served, in ticks.</summary>
    public static readonly int[] Groups = [1, 10, 100];

    public static BookShape Whole => new(0, 1);

    public bool IsWhole => Depth == 0 && Group == 1 && !Vwap;

    /// <summary>What a shaped topic's name adds to its book's: <c>:depth10:group10:vwap</c>.</summary>
    public string Suffix =>
        (Depth > 0 ? $":depth{Depth}" : "") + (Group > 1 ? $":group{Group}" : "") + (Vwap ? ":vwap" : "");

    /// <summary>
    ///     The parameter's shape: <c>depth=</c> (a number, or <c>all</c>), <c>group=</c> and <c>price=</c> (<c>level</c>, or
    ///     <c>vwap</c>), each optional.
    /// </summary>
    public string Query => $"depth={(Depth > 0 ? Depth.ToString(CultureInfo.InvariantCulture) : "all")}&group={Group}" +
                           (Vwap ? "&price=vwap" : "");

    /// <summary>
    ///     A subscription's parameter as its topic and shape: what's before <c>?</c>, and the shape after it (the whole book
    ///     if none). False if the shape isn't one served: a key it doesn't know, a grouping coarser than the coarsest.
    /// </summary>
    public static bool Split(string parameter, out string topic, out BookShape shape)
    {
        var at = parameter.IndexOf('?');
        topic = at < 0 ? parameter : parameter[..at];
        shape = Whole;
        if (at < 0) return true;
        int depth = 0, group = 1;
        var vwap = false;
        foreach (var pair in parameter.AsSpan(at + 1).Split('&'))
        {
            var part = parameter.AsSpan(at + 1)[pair];
            var equals = part.IndexOf('=');
            if (equals < 0) return false;
            var key = part[..equals];
            var value = part[(equals + 1)..];
            if (key is "depth")
            {
                if (value is "all") depth = 0;
                else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out depth)) return false;
                else depth = depth == 0 ? 0 : Depths.FirstOrDefault(d => d >= depth); // (deeper than the deepest: all)
            }
            else if (key is "group")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out group) || group < 1)
                    return false;
                group = Groups.FirstOrDefault(g => g >= group);
                if (group == 0) return false; // coarser than any served
            }
            else if (key is "price")
            {
                if (value is "vwap") vwap = true;
                else if (value is not "level") return false;
            }
            else return false;
        }

        shape = new BookShape(depth, group, vwap);
        return true;
    }
}

/// <summary>
///     The shaped topics of one book — the raw or the accelerated — made as subscribers ask for them, one per shape, each a
///     table of its own folded by the book's fold (<see cref="Market.BookFold" />). After each message the provider has the
///     book as it stands; each live shape is computed from it (the best levels a side, grouped, the deepest group whole)
///     and posted as its changes from what it posted last — size changes, as the book's are: a level entering the shape
///     its size, one leaving minus its size. Grouping is a pure mapping of the book's changes (a group's size is the sum of
///     its levels', so a level moving within its group changes nothing, and nothing is sent); depth isn't — which level
///     enters as one leaves depends on levels outside the shape — so both are computed here, where the whole book is.
///     <para>
///         A VWAP shape is cumulative: each level's state, kept at the router, is the size and the notional (price ×
///         size) from the best price down to it, summed — a change near the top moving every level's below it, a level
///         moving within its group moving the notionals alone. The provider posts the changes to both sums; the router's
///         finalizer makes each state the row subscribers see: the cumulative size, and the price it averages to — what
///         filling it would cost, a share (<see cref="Market.VwapFinalizer" />). The sums are never sent.
///     </para>
///     <para>
///         Bids are grouped down and asks up (to a multiple of the grouping), so the spread never looks crossed. Nothing is
///         allocated per message: the book is read into buffers kept here, and each shape keeps what it posted.
///     </para>
/// </summary>
internal sealed class ShapedBooks(
    Publisher publisher,
    string name,
    PayloadFormat format,
    long tick,
    int capacity,
    Lock gate,
    Action<ShapedBook, PublishResult> acknowledged)
{
    private readonly PayloadFormat _vwapFormat = Market.VwapFormat(format);
    private static readonly IComparer<Level> Order = new BestFirst(); // (boxed once: sorting by it allocates nothing)
    private readonly Level[] _levels = new Level[capacity];
    private readonly long[] _sizes = new long[capacity];
    private ShapedBook[] _books = []; // copy-on-write: the handler reads it without the lock

    /// <summary>Some shape has subscribers (under the provider's lock).</summary>
    public bool AnyLive => Volatile.Read(ref _books).Any(b => b.Live);

    /// <summary>The shape's topic, made the first time it's asked for (under the provider's lock).</summary>
    public ShapedBook For(BookShape shape)
    {
        foreach (var book in _books)
            if (book.Shape == shape)
                return book;
        var made = new ShapedBook(publisher, name + shape.Suffix, shape, shape.Vwap ? _vwapFormat : format, tick,
            capacity);
        Volatile.Write(ref _books, [.. _books, made]);
        return made;
    }

    /// <summary>
    ///     After a message, on the handler's thread: each shape with subscribers computed from the book as it stands and
    ///     posted as of <paramref name="version" /> — whole if <paramref name="whole" /> (a snapshot), or if the shape is
    ///     new to its subscribers, else as its changes.
    /// </summary>
    public void Post(BookBuilder builder, bool accelerated, long version, bool whole)
    {
        var books = Volatile.Read(ref _books);
        if (books.Length == 0 || builder.Recovering) return;
        var count = -1;
        foreach (var book in books)
        {
            bool fresh;
            lock (gate)
            {
                fresh = book.Fresh;
                book.Fresh = false;
                if (!book.Live) continue;
            }

            if (count < 0) // the book as it stands, best first a side: read once for every shape
            {
                count = builder.Fill(_levels, _sizes, accelerated);
                _levels.AsSpan(0, count).Sort(_sizes.AsSpan(0, count), Order);
            }

            book.Compute(_levels, _sizes, count);
            if (book.Post(version, whole || fresh) is { } answer) acknowledged(book, answer);
        }
    }

    /// <summary>The book broke: its shapes fail with it, till a snapshot recovers them.</summary>
    public void Fail(string why)
    {
        foreach (var book in Volatile.Read(ref _books)) book.Source.Fail(FaultCode.SourceFailed, why);
    }
}

/// <summary>One shape of a book: its table and ingest, its subscribers' epoch, and what it posted last.</summary>
internal sealed class ShapedBook
{
    private readonly ChangeSet _changes;
    private readonly long[] _notionalChanges; // with the size changes, a VWAP shape's
    private readonly Sums[] _sums; // a VWAP shape's posts: its changes, or its states whole
    private readonly TableIngest<Level, long, long>? _ingest;
    private readonly FinalizedIngest<Level, VwapRow, Sums, Sums>? _vwapIngest;
    private readonly long _step; // a group's span, in price units
    private Level[] _next, _posted;
    private long[] _nextSizes, _postedSizes, _nextNotional, _postedNotional;
    private int _nextCount, _postedCount;
    private long _chain;

    public ShapedBook(Publisher publisher, string name, BookShape shape, PayloadFormat format, long tick, int capacity)
    {
        Name = name;
        Shape = shape;
        Format = format;
        _step = tick * shape.Group;
        if (shape.Vwap)
        {
            var table = publisher.Table<Level, VwapRow>(name);
            Source = table;
            _vwapIngest = table.Ingest<Sums, Sums>(Market.VwapFold, Market.VwapFinalizer);
        }
        else
        {
            var table = publisher.Table<Level, long>(name);
            Source = table;
            _ingest = table.Ingest<long>(Market.BookFold);
        }

        (_next, _posted) = (new Level[capacity], new Level[capacity]);
        (_nextSizes, _postedSizes) = (new long[capacity], new long[capacity]);
        (_nextNotional, _postedNotional) = (new long[capacity], new long[capacity]);
        _changes = new ChangeSet(2 * capacity);
        _notionalChanges = new long[2 * capacity];
        _sums = new Sums[2 * capacity];
    }

    public string Name { get; }
    public BookShape Shape { get; }
    public Source Source { get; }

    /// <summary>Its payloads' format, for step 1: the book's, or a VWAP book's.</summary>
    public PayloadFormat Format { get; }

    /// <summary>Its subscribers' epoch, and whether to post (under the provider's lock).</summary>
    public long Epoch;

    public bool Live, Fresh;

    /// <summary>The shape of the book given (best first a side): the best levels a side, grouped, into the next.</summary>
    public void Compute(Level[] levels, long[] sizes, int count)
    {
        var n = 0;
        var buckets = 0;
        for (var i = 0; i < count; i++)
        {
            var size = sizes[i];
            if (size <= 0) continue;
            var side = levels[i].Side;
            if (n > 0 && _next[n - 1].Side != side) buckets = 0; // (bids done: the asks start)
            var price = Group(levels[i].Price, side);
            var notional = levels[i].Price * size;
            if (n > 0 && _next[n - 1] == new Level(side, price))
            {
                _nextSizes[n - 1] += size; // within the group: summed
                _nextNotional[n - 1] += notional;
                continue;
            }

            if (Shape.Depth > 0 && buckets == Shape.Depth) continue; // deeper than the shape: (asks may follow)
            _next[n] = new Level(side, price);
            _nextNotional[n] = notional;
            _nextSizes[n++] = size;
            buckets++;
        }

        _nextCount = n;
        if (!Shape.Vwap) return;
        for (var i = 1; i < n; i++) // cumulative, a side from its best: the size and notional down to each level
            if (_next[i].Side == _next[i - 1].Side)
            {
                _nextSizes[i] += _nextSizes[i - 1];
                _nextNotional[i] += _nextNotional[i - 1];
            }
    }

    /// <summary>
    ///     The next posted: whole, or as its changes from what was posted last (nothing posted if none). The answer, or
    ///     null if nothing was posted.
    /// </summary>
    public PublishResult? Post(long version, bool whole)
    {
        if (!whole)
        {
            Diff();
            if (_changes.Count == 0)
            {
                Swap();
                return null;
            }

            try
            {
                var posted = _vwapIngest is null
                    ? _ingest!.Post(_changes.LevelSpan, _changes.DeltaSpan, _chain, version)
                    : _vwapIngest.Post(_changes.LevelSpan, Sums(_changes.Count, _changes.Deltas, _notionalChanges), _chain,
                        version);
                if (posted.Status != PublishStatus.Full)
                {
                    _chain = version;
                    Swap();
                    return posted;
                }
            }
            catch (StreamFaultException) // faulted: the whole shape recovers it
            {
            }
        }

        var levels = _next.AsSpan(0, _nextCount);
        var result = _vwapIngest is null
            ? _ingest!.PostSnapshot(levels, _nextSizes.AsSpan(0, _nextCount), version)
            : _vwapIngest.PostSnapshot(levels, Sums(_nextCount, _nextSizes, _nextNotional), version); // its states
        _chain = version;
        Swap();
        return result;
    }

    /// <summary>A VWAP shape's posts: sizes and notionals paired.</summary>
    private ReadOnlySpan<Sums> Sums(int count, long[] sizes, long[] notionals)
    {
        for (var i = 0; i < count; i++) _sums[i] = new Sums(sizes[i], notionals[i]);
        return _sums.AsSpan(0, count);
    }

    /// <summary>A level's group: bids down, asks up, to a multiple of the grouping (each level its own at 1).</summary>
    private long Group(long price, byte side)
    {
        if (_step <= 1) return price;
        var below = price - ((price % _step) + _step) % _step;
        return side == Level.Bid || below == price ? below : below + _step;
    }

    /// <summary>
    ///     What moved from the last posted to the next, both best first a side: a merge, as size changes — and, for a VWAP
    ///     shape, notional changes with them (cumulative: a change near the top moves every level's below it).
    /// </summary>
    private void Diff()
    {
        _changes.Count = 0;
        var vwap = Shape.Vwap;
        var order = new BestFirst();
        int i = 0, j = 0;
        while (i < _postedCount || j < _nextCount)
        {
            var c = i == _postedCount ? 1 : j == _nextCount ? -1 : order.Compare(_posted[i], _next[j]);
            if (c < 0) // gone
            {
                Change(_posted[i], -_postedSizes[i], -_postedNotional[i]);
                i++;
            }
            else if (c > 0) // new
            {
                Change(_next[j], _nextSizes[j], _nextNotional[j]);
                j++;
            }
            else
            {
                if (_nextSizes[j] != _postedSizes[i] || (vwap && _nextNotional[j] != _postedNotional[i]))
                    Change(_next[j], _nextSizes[j] - _postedSizes[i], _nextNotional[j] - _postedNotional[i]);
                i++;
                j++;
            }
        }
    }

    private void Change(Level level, long size, long notional)
    {
        _notionalChanges[_changes.Count] = notional;
        _changes.Add(level, size);
    }

    private void Swap()
    {
        (_posted, _next) = (_next, _posted);
        (_postedSizes, _nextSizes) = (_nextSizes, _postedSizes);
        (_postedNotional, _nextNotional) = (_nextNotional, _postedNotional);
        _postedCount = _nextCount;
    }
}

/// <summary>Bids before asks, each best first: bids by price down, asks up.</summary>
internal readonly struct BestFirst : IComparer<Level>
{
    public int Compare(Level a, Level b)
    {
        if (a.Side != b.Side) return a.Side.CompareTo(b.Side);
        return a.Side == Level.Bid ? b.Price.CompareTo(a.Price) : a.Price.CompareTo(b.Price);
    }
}
