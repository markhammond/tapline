using System.Runtime.CompilerServices;

namespace Tapline.Grid;

/// <summary>
///     A grid's window, worked out with no threads and no clock: what to read for a request or a refresh, and, given the
///     answer, the rows and the order it publishes, in one transaction, an order that hasn't changed left out.
/// </summary>
/// <remarks>
///     View positions: following the live top they're ranks, so rows arriving push the rest down. Held — the reader
///     scrolled away — they hold: rows arriving above are counted (<see cref="Above" />), not shown, so the reader's rows
///     don't move, until they're let in a few at a time as the reader scrolls up near the top, every position moving down
///     by as many (<see cref="LetIn" />, which the page moves its scroll by). A held read near the window is made from its
///     rows, the arrivals above learned from where its row now stands; one far from it is placed by rank. Not
///     thread-safe: its session's loop alone uses it.
/// </remarks>
internal sealed class GridWindow<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private static readonly int KeySize = Unsafe.SizeOf<TKey>();

    private readonly HashSet<TKey> _keep = [];
    private readonly int _maxRows, _near, _revealNear, _revealAtOnce;
    private readonly LastValueSource _order;
    private readonly LastValueSource? _status; // what the status line shows (GridStatusValue): staged with the rows
    private readonly Table<TKey, TRow> _table;
    private byte[] _built, _published; // the order as built, and as last published
    private byte[] _statusBuilt = new byte[GridStatusValue.Size], _statusPublished = [];
    private TKey[] _keys = new TKey[64]; // the window as published: its rows from view position _first (grown as needed)
    private TRow[] _rows = new TRow[64];
    private TKey[] _lingering = new TKey[64]; // rows a view read first left in the table, for the publish after it
    private int _lingered;
    private bool _anchored; // the window's rows are the view's: a held read may start from them
    private int _count;
    private long _first, _from, _to; // and the window asked for: [_from, _to)
    private HeldTotal _held;
    private int _locateBefore, _locateCount;
    private long _restFrom, _restTo; // a whole window, its view read first
    private long _askedFirst = -1; // held: the view's first row the page last asked for, in its positions now (-1: none)
    private long _planFrom, _planTo, _planAt; // the read in flight: the window it's for; about a row, its position
    private GridReadKind _planKind;
    private bool _anchoring; // the read in flight is about the page's row: a hold from the live top set on it
    private int _publishedSize = -1;

    /// <param name="maxRows">Rows a window may span.</param>
    /// <param name="near">A held window's rows read from its own rows within this many of them; farther, by rank.</param>
    /// <param name="revealNear">Held, rows arrived above are let in once the view's first row is above this.</param>
    /// <param name="revealAtOnce">At most this many at a time.</param>
    /// <param name="status">Where its status goes (<see cref="GridStatusValue" />), staged with the rows; none: not kept.</param>
    public GridWindow(Table<TKey, TRow> table, LastValueSource order, int maxRows, int near, int revealNear,
        int revealAtOnce, LastValueSource? status = null)
    {
        (_table, _order, _status, _maxRows, _near, _revealNear, _revealAtOnce) =
            (table, order, status, maxRows, near, revealNear, revealAtOnce);
        _built = new byte[GridOrder.SizeOf(_keys.Length, KeySize)];
        _published = new byte[_built.Length];
        PageCapacity = maxRows + revealAtOnce + near; // (letting rows in may widen a window by as many)
    }

    /// <summary>Rows the largest read asks for: a window, and as far again as a held read may start from its rows.</summary>
    public int PageCapacity { get; }

    /// <summary>Following the live top (positions are ranks); else held.</summary>
    public bool Live { get; private set; } = true;

    /// <summary>How often the grid has gone from the live top to held (or been placed at a row).</summary>
    public int Holds { get; private set; }

    /// <summary>Held: a row's rank less its view position — rows arrived above and not let in (less, rows gone above).</summary>
    public long Hidden { get; private set; }

    /// <summary>Rows arrived above a held grid and not let in.</summary>
    public long Above => Live ? 0 : Math.Max(0, Hidden);

    /// <summary>Rows let in above so far in this hold.</summary>
    public long LetIn { get; private set; }

    /// <summary>Rows in all, in view positions: held, kept as it was within 1 % (<see cref="HeldTotal" />).</summary>
    public long Total { get; private set; }

    public TotalKind TotalKind { get; private set; }

    /// <summary>How often the sort or filter has been set: the view the rows published are of.</summary>
    public int View { get; private set; }

    /// <summary>
    ///     The view's order is its keys', greatest first (<see cref="IGridSource{TKey,TRow}.NewestFirstByKey" />, and keys
    ///     the page can compare): following the live top, with no rows lingering, its order goes without keys
    ///     (<see cref="GridOrder.ByKey" />).
    /// </summary>
    public bool ByKey { get; set; }

    /// <summary>
    ///     The overscan its status tells the page (<see cref="GridStatusValue" />): rows either side of the view its link
    ///     carries in time (<see cref="GridOptions.OverscanFill" />); below 0, none told (the page's own count).
    /// </summary>
    public int Overscan { get; set; } = -1;

    /// <summary>
    ///     The page passive, the source has moved past the rows it holds (<see cref="GridStatusValue.Moved" />): said by
    ///     <see cref="StageMoved" />, unsaid by the next answer.
    /// </summary>
    public bool Moved { get; private set; }

    /// <summary>The last read was refused: the rows shown are as they were.</summary>
    public bool Stale => Refusal is not null;

    /// <summary>Why the last read was refused, or null.</summary>
    public string? Refusal { get; private set; }

    /// <summary>The window's first row's view position, and how many rows it has.</summary>
    public (long First, int Count) Window => (_first, _count);

    /// <summary>The window last asked for, in view positions: [From, To).</summary>
    public (long From, long To) Asked => (_from, _to);

    /// <summary>The rank a located key was found at (<see cref="PlanLocate" />), or null: not found.</summary>
    public long? Located { get; private set; }

    /// <summary>The order as last published (empty: none yet).</summary>
    public ReadOnlySpan<byte> PublishedOrder => _published.AsSpan(0, Math.Max(0, _publishedSize));

    /// <summary>The window's keys and rows, as published.</summary>
    public ReadOnlySpan<TKey> Keys => _keys.AsSpan(0, _count);

    public ReadOnlySpan<TRow> Rows => _rows.AsSpan(0, _count);

    /// <summary>Rows of the window before a view read first, left in the table until the publish after it.</summary>
    public int Lingering => _lingered;

    /// <summary>
    ///     What to read for the page's <paramref name="request" />: the live top, or a held window. A view the window
    ///     published doesn't hold (a jump, or a reader outrunning the pre-fetch) is read first, then the rest
    ///     (<see cref="NeedsRest" />, <see cref="PlanRest" />): the rows the reader looks at go out ahead of those
    ///     fetched round them. Held from the live top, on the page's <paramref name="anchor" /> — the row it shows at the
    ///     view's first position, by key — the hold is set so that row keeps that position: the page's positions are of
    ///     the live order it last drew, which rows arriving since may have moved on from (a slow link's seconds of them).
    ///     Till the page has the window's first answer (<paramref name="firstReached" />: the session's tap says), the
    ///     window doesn't widen — a slow link's answers would merge into one, the first waiting for the rest: a view it
    ///     holds is read as the window stands, the rest after (<see cref="NeedsRest" />).
    /// </summary>
    public GridRead<TKey, TRow> Plan(in GridRequest request, TKey? anchor = null, bool firstReached = true)
    {
        var first = request.First;
        long from, to;
        var current = !Live && request.Holds == Holds; // (asked in this hold)
        if (current && request.LetIn < LetIn)
            first += LetIn - request.LetIn; // (in its positions: before the rows let in that it hasn't applied)

        // The live top: at it; asked for (the pill, Home); or, fewer arrived above than the view holds, the first row shown
        // scrolled to, or the reader racing up. (As many or more above: the reader scrolls on into them, let in.)
        var few = Hidden < request.Count;
        if (Live ? first == 0 : request.Top || (current ? few && (first == 0 || request.Racing) : first == 0))
        {
            var missing = !Covers(0, request.Count); // (in the positions shown until now)
            if (!Live) Release();
            (from, to) = (0, request.Count + (long)request.After);
            return Staged(missing, 0, request.Count, from, to, firstReached);
        }

        from = Math.Max(0, first - request.Before);
        to = first + request.Count + request.After;
        if (Live)
        {
            Hold();
            if (anchor is { } key) return PlanAnchored(key, first, request.Count, from, to);
        }

        // Near the top, the reader scrolled up since the page last asked (not its pre-fetch widening where it rests), the
        // page having applied what was let in before: a few more — thirty; racing up, as many as three views, to keep
        // pace — no more than it fetches above, unless its window reaches the top: that takes them, widening by as many
        // (no wider than a window and a let-in's thirty, a read's room).
        var up = current && first < _askedFirst;
        if (up && first < _revealNear && Hidden > 0 && request.LetIn == LetIn)
        {
            var batch = request.Racing ? Math.Max(_revealAtOnce, 3L * request.Count) : _revealAtOnce;
            var room = from > 0 ? first - from : _maxRows + _revealAtOnce - (to - from);
            var n = (int)Math.Min(Math.Min(Hidden, batch), room);
            if (n > 0)
            {
                LetInAbove(n);
                if (from > 0) from += n; // (a window reaching the top takes the rows let in)
                to += n;
                first += n;
            }
        }

        _askedFirst = first;
        return Staged(!Covers(first, first + request.Count), first, request.Count, from, to, firstReached);
    }

    /// <summary>
    ///     A view the window doesn't hold read first, the rest to follow; else the whole window — but, the page without
    ///     the window's first answer yet, not wider than it stands: read as it is (the request's answer with what's on its
    ///     way), the rest once that's there.
    /// </summary>
    private GridRead<TKey, TRow> Staged(bool missing, long first, int count, long from, long to, bool firstReached)
    {
        PlannedFirst = false;
        if (!missing && !firstReached && _anchored && (from < _from || to > _to))
        {
            (_restFrom, _restTo, NeedsRest) = (from, to, true);
            return PlanFrom(_from, _to);
        }

        NeedsRest = missing && count > 0 && (from < first || to > first + count);
        if (!NeedsRest) return PlanFrom(from, to);
        PlannedFirst = true;
        (_restFrom, _restTo) = (from, to);
        return PlanFrom(first, first + count);
    }

    /// <summary>Whether the window published holds every position of [from, to).</summary>
    private bool Covers(long from, long to)
    {
        return _anchored && from >= _first && to <= _first + _count;
    }

    /// <summary>A view was read and published alone; the rest of its window is to read.</summary>
    public bool NeedsRest { get; private set; }

    /// <summary>
    ///     The read planned is a window's first answer — a view read first, or a hold's — its rest read once the page has
    ///     it (<see cref="NeedsRest" />).
    /// </summary>
    public bool PlannedFirst { get; private set; }

    /// <summary>The whole window of a view read first, in view positions: [From, To).</summary>
    public (long From, long To) Rest => (_restFrom, _restTo);

    /// <summary>The rest of a window whose view was read first, the view published: read from the view's rows.</summary>
    public GridRead<TKey, TRow> PlanRest()
    {
        (NeedsRest, PlannedFirst) = (false, false);
        return PlanFrom(_restFrom, _restTo);
    }

    /// <summary>The window again, as it was asked for: false if there's none yet.</summary>
    public bool PlanRefresh(out GridRead<TKey, TRow> read)
    {
        if (_to <= _from)
        {
            read = default;
            return false;
        }

        PlannedFirst = false;
        read = PlanFrom(_from, _to);
        return true;
    }

    /// <summary>
    ///     A hold from the live top set on the row the page shows at <paramref name="first" />: read about it, wherever it
    ///     stands now — first the view and a view below (<paramref name="count" /> rows each), small, so on a slow link it
    ///     goes out alone, and with it the rows arrived above (the page's pill); the rest of the window
    ///     [<paramref name="from" />, <paramref name="to" />) once the page has it.
    /// </summary>
    private GridRead<TKey, TRow> PlanAnchored(in TKey key, long first, int count, long from, long to)
    {
        var end = Math.Min(to, first + 2L * Math.Max(1, count));
        (_restFrom, _restTo) = (from, to);
        NeedsRest = from < first || to > end;
        PlannedFirst = NeedsRest;
        (_planFrom, _planTo, _planAt, _askedFirst) = (first, end, first, first);
        (_planKind, _anchoring) = (GridReadKind.AroundKey, true);
        return GridRead<TKey, TRow>.AroundKey(key, 0, (int)(end - first));
    }

    /// <summary>A key's row, wherever it is, with <paramref name="before" /> rows above it and <paramref name="count" /> on.</summary>
    public GridRead<TKey, TRow> PlanLocate(in TKey key, int before, int count)
    {
        (NeedsRest, PlannedFirst) = (false, false);
        _anchoring = false;
        _planKind = GridReadKind.AroundKey;
        (_locateBefore, _locateCount) = (before, count);
        return GridRead<TKey, TRow>.AroundKey(key, before, count);
    }

    /// <summary>
    ///     A new sort or filter, the <paramref name="view" />th: nothing held carries over — the window's rows no longer
    ///     stand where they did (they go as the new window is published) — and the next read is of the top, as many rows.
    /// </summary>
    public void Reset(int view)
    {
        View = view;
        if (!Live) Release();
        _anchored = false;
        (NeedsRest, PlannedFirst) = (false, false);
        _to -= _from;
        _from = 0;
    }

    /// <summary>
    ///     The answer to the read last planned, rows in <paramref name="page" />: published, in one transaction. False if
    ///     nothing went out (refused and already stale; or nothing changed).
    /// </summary>
    public bool Apply(in GridAnswer answer, GridPage<TKey, TRow> page)
    {
        if (answer.Refusal is { } refusal) return Refuse(refusal);

        long firstView; // the view position of the page's first row
        switch (_planKind)
        {
            case GridReadKind.AroundKey when _anchoring:
                _anchoring = false;
                if (answer.Anchor == GridAnswer.NotFound) // (gone: the hold placed by rank instead, as it would be unanchored)
                {
                    (NeedsRest, PlannedFirst) = (true, false); // (the whole window, read next: nothing went out to wait for)
                    return false;
                }

                Hidden = answer.FirstRank + answer.Anchor - _planAt; // (the page's row: where it stands now, less where it's shown)
                firstView = _planAt - answer.Anchor;
                break;
            case GridReadKind.AroundRow:
                Hidden = answer.FirstRank + answer.Anchor - _planAt; // (where its row stands now)
                firstView = _planAt - answer.Anchor;
                break;
            case GridReadKind.AroundKey:
                if (answer.Anchor == GridAnswer.NotFound)
                {
                    Located = null;
                    return false;
                }

                var rank = answer.FirstRank + answer.Anchor;
                if (rank == 0)
                {
                    if (!Live) Release();
                }
                else
                {
                    Hold(); // (a place of its own: positions are ranks from here)
                }

                Located = rank;
                (_planFrom, _planTo) = (Math.Max(0, rank - _locateBefore), rank + _locateCount);
                firstView = answer.FirstRank;
                break;
            default:
                firstView = answer.FirstRank - (Live ? 0 : Hidden);
                break;
        }

        var i0 = (int)Math.Clamp(_planFrom - firstView, 0, page.Count);
        var i1 = (int)Math.Clamp(_planTo - firstView, i0, page.Count);
        var keys = page.Keys[i0..i1];
        _keep.Clear();
        foreach (var key in keys)
            if (!_keep.Add(key))
                return Refuse("The source answered a key twice.");

        // In view positions. A page ending short of what was asked for found where the rows end.
        var total = answer.Total - (Live ? 0 : Hidden);
        var end = firstView + page.Count;
        var ended = page.Count > 0 && end < _planTo;
        if (ended) total = end;
        Total = Live ? total : ended ? Set(total) : _held.Hold(total);
        TotalKind = ended ? TotalKind.Exact : answer.TotalKind;
        Refusal = null;
        (_from, _to) = (_planFrom, _planTo);
        return Publish(keys, page.Rows[i0..i1], firstView + i0, NeedsRest);
    }

    private long Set(long total)
    {
        _held.Set(total);
        return total;
    }

    private bool Refuse(string reason)
    {
        (Refusal, Moved) = (reason, false); // (read as the source is now)
        using var tx = _status is null ? Transaction.Begin(_order) : Transaction.Begin(_order, _status);
        var changed = StageOrder(tx, Keys, _first);
        changed |= StageStatus(tx);
        tx.Complete();
        return changed;
    }

    private bool Publish(ReadOnlySpan<TKey> keys, ReadOnlySpan<TRow> rows, long first, bool linger)
    {
        // _keep holds the new window's keys: the rows leaving it are deleted, those in it set (unchanged: no change). A
        // view read first leaves the rest of the window before in the table — the view goes out without their deletes,
        // and those its rest still holds aren't sent again — until the publish after it.
        bool staged;
        var version = _table.Version;
        Moved = false; // (read as the source is now: said with the rows)
        using (var tx = _status is null ? Transaction.Begin(_table, _order) : Transaction.Begin(_table, _order, _status))
        {
            var update = tx.Of(_table);
            for (var i = 0; i < _lingered; i++)
                if (!_keep.Contains(_lingering[i]))
                    update.Delete(_lingering[i]);
            _lingered = 0;
            for (var i = 0; i < _count; i++)
                if (!_keep.Contains(_keys[i]))
                {
                    if (linger) _lingering[_lingered++] = _keys[i];
                    else update.Delete(_keys[i]);
                }

            for (var i = 0; i < keys.Length; i++) update.Set(keys[i], rows[i]);
            staged = StageOrder(tx, keys, first);
            staged |= StageStatus(tx);
            tx.Complete();
        }

        if (keys.Length > _keys.Length) // (a wider window than any before: room for it, kept)
        {
            _keys = new TKey[Math.Max(keys.Length, 2 * _keys.Length)];
            _rows = new TRow[_keys.Length];
            Array.Resize(ref _lingering, _keys.Length); // (those left kept)
        }

        keys.CopyTo(_keys);
        rows.CopyTo(_rows);
        (_first, _count, _anchored) = (first, keys.Length, true);
        return staged || _table.Version != version;
    }

    /// <summary>
    ///     Builds the order; stages it if it isn't as last published. True if staged. Following the live top in the keys'
    ///     order, the table holding the window's rows alone (none lingering), it goes without keys: the page orders the
    ///     rows by theirs, and an update carries only the rows that changed.
    /// </summary>
    private bool StageOrder(Transaction tx, ReadOnlySpan<TKey> keys, long first)
    {
        if (GridOrder.SizeOf(keys.Length, KeySize) > _built.Length) // (room for a wider window's, kept)
        {
            var room = Math.Max(GridOrder.SizeOf(keys.Length, KeySize), 2 * _built.Length);
            _built = new byte[room];
            Array.Resize(ref _published, room);
        }

        var size = GridOrder.Write(_built, new GridOrderHeader
        {
            Live = Live, Stale = Stale, ByKey = ByKey && Live && _lingered == 0, TotalKind = TotalKind,
            KeySize = KeySize, First = first, Total = Total, LetIn = LetIn, Holds = Holds, View = View
        }, keys);
        var built = _built.AsSpan(0, size);
        if (size == _publishedSize && built.SequenceEqual(_published.AsSpan(0, size))) return false;
        tx.Of(_order).Publish(built);
        built.CopyTo(_published);
        _publishedSize = size;
        return true;
    }

    /// <summary>The page passive and the source moved past its rows: said in the status alone (nothing read). True if staged.</summary>
    public bool StageMoved()
    {
        if (Moved) return false;
        Moved = true;
        return StageStatusAlone();
    }

    /// <summary>
    ///     The overscan told changed with nothing read (the reader resting, the link with room; or the link the limit): said
    ///     in the status alone, the page asking for the window it makes. True if staged.
    /// </summary>
    public bool StageOverscan(int overscan)
    {
        if (overscan == Overscan) return false;
        Overscan = overscan;
        return StageStatusAlone();
    }

    private bool StageStatusAlone()
    {
        if (_status is null) return false;
        using var tx = Transaction.Begin(_status);
        var staged = StageStatus(tx);
        tx.Complete();
        return staged;
    }

    /// <summary>
    ///     Builds the status the page's status line shows beside the order's (<see cref="GridStatusValue" />); stages it if
    ///     it isn't as last published. True if staged. (A held window's rows arriving above change only it: it goes out
    ///     alone, small.)
    /// </summary>
    private bool StageStatus(Transaction tx)
    {
        if (_status is null) return false;
        var refusal = Refusal;
        var size = GridStatusValue.SizeOf(refusal);
        if (size > _statusBuilt.Length) _statusBuilt = new byte[size]; // (a refusal's words: rare)
        size = GridStatusValue.Write(_statusBuilt, Above, Overscan, refusal, Moved);
        var built = _statusBuilt.AsSpan(0, size);
        if (built.SequenceEqual(_statusPublished)) return false;
        tx.Of(_status).Publish(built);
        if (_statusPublished.Length != size) _statusPublished = new byte[size];
        built.CopyTo(_statusPublished);
        return true;
    }

    private GridRead<TKey, TRow> PlanFrom(long from, long to)
    {
        (_planFrom, _planTo) = (from, to);
        _anchoring = false;
        if (!Live && _anchored && _count > 0)
        {
            // Held, near the window: from its row nearest where what's wanted starts — rows arriving above it don't
            // move what's read, and where it stands now says how many have.
            var at = Math.Clamp(from, _first, _first + _count - 1);
            if (Math.Abs(from - at) <= _near)
            {
                var i = (int)(at - _first);
                _planKind = GridReadKind.AroundRow;
                _planAt = at;
                return GridRead<TKey, TRow>.AroundRow(_keys[i], _rows[i], (int)Math.Max(0, at - from),
                    (int)Math.Max(0, to - at));
            }
        }

        // Live, positions are ranks; held and far from the window, placed by rank past the rows not let in.
        _planKind = GridReadKind.AtRank;
        var hidden = Live ? 0 : Hidden;
        var rank = Math.Max(0, from + hidden);
        return GridRead<TKey, TRow>.AtRank(rank, (int)Math.Max(0, to + hidden - rank));
    }

    private void Hold()
    {
        Live = false;
        Holds++;
        Hidden = 0;
        LetIn = 0;
        _askedFirst = -1;
        _held.Clear();
    }

    private void Release()
    {
        Live = true;
        Hidden = 0;
        LetIn = 0;
        _askedFirst = -1;
        _held.Clear();
    }

    /// <summary>Lets in <paramref name="rows" /> of those arrived above: every position moves down by as many.</summary>
    private void LetInAbove(int rows)
    {
        Hidden -= rows;
        LetIn += rows;
        _first += rows;
        _from += rows;
        _to += rows;
        _held.Shift(rows);
        Total += rows;
    }
}
