using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tapline.Grid.Provider;

namespace Tapline.Grid;

/// <summary>
///     A grid's window, worked out with no threads and no clock: what to read for a request or a refresh, and, given the
///     answer, the rows and the order it publishes, in one transaction, an order that hasn't changed left out.
/// </summary>
/// <remarks>
///     <para>
///         View positions: following the live top they're ranks, so rows arriving push the rest down. Held — the reader
///         scrolled away — they hold: rows arriving above are counted (<see cref="Above" />), not shown, so the reader's
///         rows don't move, until they're let in a few at a time as the reader scrolls up near the top, every position
///         moving down by as many (<see cref="LetIn" />, which the page moves its scroll by); the reader is told those
///         they haven't seen (<see cref="Unseen" />), and racing up goes back to the live top when those are fewer than
///         the view holds. A held read near the window
///         is made from its rows, the arrivals above learned from where its row now stands; one far from it is placed by
///         rank. Not thread-safe: its session's loop alone uses it.
///     </para>
///     <para>
///         Open rows: a row the reader opened is followed by its run (<see cref="IGridSource{TKey,TRow}.ReadRunsAsync" />),
///         so view positions are those of the rows drawn, the view's and the runs open among them. The view's rows are
///         read in top positions — where they'd stand with none open (<see cref="GridOpenRows{TKey}" />) — and the runs the
///         window crosses after them, the slices in it (<see cref="NeedsRuns" />). Held, rows of runs come or gone above
///         the row the page shows first move every position by as many, the page its scroll with them
///         (<see cref="LetIn" />), so the reader's rows stay where they're drawn.
///     </para>
/// </remarks>
internal sealed class GridWindow<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private const byte Open = 0x80; // in a row of the view's depth: it's open (its run follows)
    private const int RunSlack = 16; // a run read past its length as last read: room for rows come since
    private static readonly int KeySize = Unsafe.SizeOf<TKey>();

    private readonly HashSet<TKey> _keep = [];
    private readonly int _maxRows, _near, _revealNear, _revealAtOnce;
    private readonly GridOpenRows<TKey> _open;
    private readonly LastValueSource _order;
    private readonly LastValueSource? _status; // what the status line shows (GridStatusValue): staged with the rows
    private readonly Table<TKey, TRow> _table;
    private byte[] _built, _published; // the order as built, and as last published
    private byte[] _statusBuilt = new byte[GridStatusValue.Size], _statusPublished = [];
    private TKey[] _keys = new TKey[64]; // the window as published: its rows from view position _first (grown as needed)
    private TRow[] _rows = new TRow[64];
    private byte[] _depths = new byte[64]; // and their depths: 0, a row of the view (Open, if its run follows); 1 on, a run's
    private TKey[] _topKeys, _ownTopKeys = []; // the window's rows of the view as read, from top position _topFirst: those
    private TRow[] _topRows, _ownTopRows = []; // published (the same arrays, with none open), and an open row above them
    private TKey[] _composed = new TKey[64]; // a window being composed from the view's rows and its runs
    private TRow[] _composedRows = new TRow[64];
    private byte[] _composedDepths = new byte[64];
    private TKey[] _lingering = new TKey[64]; // rows a view read first left in the table, for the publish after it
    private int _lingered;
    private bool _anchored; // the window's rows are the view's: a held read may start from them
    private int _count, _topCount;
    private long _first, _topFirst, _from, _to; // and the window asked for: [_from, _to)
    private HeldTotal _held;
    private int _locateBefore, _locateCount;
    private long _locatedRank = -1; // a located row's rank: placed in view positions once its runs are read
    private long _restFrom, _restTo; // a whole window, its view read first
    private long _askedFirst = -1; // held: the view's first row the page last asked for, in its positions now (-1: none)
    private long _planFrom, _planTo; // the read in flight: the window it's for, in view positions
    private long _planTopFrom, _planTopTo, _planAt; // and the view's rows it reads, in top positions; about a row, its own
    private GridReadKind _planKind;
    private bool _anchoring; // the read in flight is about the page's row: a hold from the live top set on it
    private int _publishedSize = -1;
    private long _topTotal; // the view's rows, as the last read said (in top positions)
    private TotalKind _topKind;
    private bool _ended; // the last read found where the view's rows end
    private bool _nested; // the window published was composed with rows open: the next is too, as their runs go
    private bool _short; // the window published came short of the one asked for (its runs moved meanwhile): read again
    private readonly List<(TKey Key, bool Open, bool All)> _toggles = []; // rows opened or closed since the last read (All:
                                                                           // every row): the read's planned as the page's
                                                                           // positions have it, the rows as they are now
    private bool _allOpen; // every row of the view open but those closed: their runs counted as read (_counted)
    private readonly HashSet<TKey> _closed = [];
    private readonly GridRecentSet<TKey> _counted; // every row open: the rows whose runs count, the most recently read
    private readonly List<TKey> _uncounted = []; // (rows forgotten from it: their runs counted no more)
    private GridRunRead<TKey>[] _runReads = new GridRunRead<TKey>[8]; // the runs to read for the window, and each one's
    private int[] _runOwners = new int[8]; // open row, as the top window's index
    private int _runCount;
    private TKey _leadKey; // the window published starts in a run: its open row, above the window
    private long _leadAt, _leadTop; // (its view position, and top position)
    private bool _hasLead;
    private TKey _refKey, _refOwner; // held: the row the page shows first, as published — a run's, with its open row and
    private long _refAt, _refOffset; // its offset in the run — kept where it's drawn as runs above it come or go
    private bool _hasRef, _refRun;
    private TKey[] _toggledHeld = new TKey[8]; // the rows apart the window published holds (ToggledHeld)
    private int _keepRows; // a new view keeping the page's place, held: the rows of its view (its read next, of _keptKey's row)
    private long _unseen; // held, of the rows let in, those above the first row the page has shown since: top positions [0, _unseen)
    private bool _placed; // held by a locate the page hasn't scrolled to yet: its asks from the top, as it was, don't release it
    private bool _keeping; // the read in flight is that one
    private TKey _keptKey;
    private TRow _keptRow;
    private TKey _lastKey; // held: the last row of the view the window held — by key and values, at its top position —
    private TRow _lastRow; // which a window none of whose rows are left (the view moved past them) is read about
    private long _lastTop;
    private bool _hasLast;

    /// <param name="maxRows">Rows a window may span.</param>
    /// <param name="near">A held window's rows read from its own rows within this many of them; farther, by rank.</param>
    /// <param name="revealNear">Held, rows arrived above are let in once the view's first row is above this.</param>
    /// <param name="revealAtOnce">At most this many at a time.</param>
    /// <param name="status">Where its status goes (<see cref="GridStatusValue" />), staged with the rows; none: not kept.</param>
    /// <param name="counted">Every row open (<see cref="SetAllOpen" />), the rows whose runs it counts, the most recently read (a read's at least).</param>
    public GridWindow(Table<TKey, TRow> table, LastValueSource order, int maxRows, int near, int revealNear,
        int revealAtOnce, LastValueSource? status = null, int counted = 4096)
    {
        (_table, _order, _status, _maxRows, _near, _revealNear, _revealAtOnce) =
            (table, order, status, maxRows, near, revealNear, revealAtOnce);
        _open = new GridOpenRows<TKey>();
        _built = new byte[GridOrder.SizeOf(_keys.Length, KeySize)];
        _published = new byte[_built.Length];
        (_topKeys, _topRows) = (_keys, _rows);
        PageCapacity = maxRows + revealAtOnce + near; // (letting rows in may widen a window by as many)
        _counted = new GridRecentSet<TKey>(Math.Max(counted, PageCapacity)); // (a read's rows never forgotten as it's counted)
    }

    /// <summary>Rows the largest read asks for: a window, and as far again as a held read may start from its rows.</summary>
    public int PageCapacity { get; }

    /// <summary>Following the live top (positions are ranks); else held.</summary>
    public bool Live { get; private set; } = true;

    /// <summary>How often the grid has gone from the live top to held (or been placed at a row).</summary>
    public int Holds { get; private set; }

    /// <summary>Held: a row's rank less its top position — rows arrived above and not let in (less, rows gone above).</summary>
    public long Hidden { get; private set; }

    /// <summary>Rows arrived above a held grid and not let in.</summary>
    public long Above => Live ? 0 : Math.Max(0, Hidden);

    /// <summary>
    ///     Rows arrived above a held grid the reader hasn't seen: those not let in (<see cref="Above" />), and those let in
    ///     above the first row the page has shown since — what its pill and status line count. Rows let in go in above the
    ///     view, a batch at a time as the reader scrolls up near the top, ahead of being looked at.
    /// </summary>
    public long Unseen => Live ? 0 : Math.Max(0, Hidden) + _unseen;

    /// <summary>
    ///     Held, what the view positions have moved by so far in this hold: rows let in above, and rows of runs come or gone
    ///     above the page's first (the page moves its scroll by as many, so its rows stay where they're drawn).
    /// </summary>
    public long LetIn { get; private set; }

    /// <summary>Rows in all, in view positions — the runs open among them counted: held, kept as it was within 1 % (<see cref="HeldTotal" />).</summary>
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

    /// <summary>The most memory the source may take answering one of its reads of runs (<see cref="GridRunRead{TKey}.MaxBytes" />).</summary>
    public uint MaxBytes { get; init; }

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

    /// <summary>The view position a located key was found at (<see cref="PlanLocate" />), or null: not found.</summary>
    public long? Located { get; private set; }

    /// <summary>The order as last published (empty: none yet).</summary>
    public ReadOnlySpan<byte> PublishedOrder => _published.AsSpan(0, Math.Max(0, _publishedSize));

    /// <summary>The window's keys and rows, as published.</summary>
    public ReadOnlySpan<TKey> Keys => _keys.AsSpan(0, _count);

    public ReadOnlySpan<TRow> Rows => _rows.AsSpan(0, _count);

    /// <summary>Their depths, as published: 0, a row of the view (0x80 in it: open, its run following); 1 on, a run's.</summary>
    public ReadOnlySpan<byte> Depths => _depths.AsSpan(0, _count);

    /// <summary>Rows of the window before a view read first, left in the table until the publish after it.</summary>
    public int Lingering => _lingered;

    /// <summary>Rows open (their runs shown).</summary>
    public int OpenRows => _open.Count;

    /// <summary>
    ///     For tests: the row of the view the window published starts at — its first row, or the open row above it whose
    ///     run it starts in — by its top position, its view position, and the rows of the runs above it as the window
    ///     counts them (null: no rows).
    /// </summary>
    internal (long Top, long At, long Runs)? FirstOwner
    {
        get
        {
            if (_count == 0) return null;
            var top = -1L;
            if (_hasLead) top = _leadTop;
            else
                for (var k = 0; k < _topCount && top < 0; k++)
                    if (_topKeys[k].Equals(_keys[0]))
                        top = _topFirst + k;
            return top < 0 ? null : (top, _open.Display(top), _open.Before(top));
        }
    }

    /// <summary>For tests: how long the window takes an open row's run to be (null: unread, or not open).</summary>
    internal long? KnownLength(in TKey key)
    {
        return _open.TryLength(key, out var length) ? length : null;
    }

    /// <summary>
    ///     The rows the window published holds that its session remembers apart from the rest: the open ones (the view's
    ///     in it, and the one above it whose run it starts in) — every row open, the closed ones in it. (Remembered used,
    ///     and never forgotten while the window holds them: <see cref="GridRecentSet{TKey}" />.)
    /// </summary>
    public ReadOnlySpan<TKey> ToggledHeld()
    {
        var n = 0;
        if (_hasLead && !_allOpen) _toggledHeld[n++] = _leadKey;
        for (var i = 0; i < _count; i++)
        {
            if (_depths[i] != (_allOpen ? 0 : Open)) continue;
            if (n == _toggledHeld.Length) Array.Resize(ref _toggledHeld, 2 * n);
            _toggledHeld[n++] = _keys[i];
        }

        return _toggledHeld.AsSpan(0, n);
    }

    public bool IsOpen(in TKey key)
    {
        for (var i = _toggles.Count - 1; i >= 0; i--)
            if (_toggles[i].All || _toggles[i].Key.Equals(key))
                return _toggles[i].Open;
        return _allOpen ? !_closed.Contains(key) : _open.IsOpen(key);
    }

    /// <summary>Every row of the view open but those closed (<see cref="SetAllOpen" />), as the next read has it.</summary>
    public bool AllOpen
    {
        get
        {
            for (var i = _toggles.Count - 1; i >= 0; i--)
                if (_toggles[i].All)
                    return _toggles[i].Open;
            return _allOpen;
        }
    }

    /// <summary>
    ///     Opens a row of the view: its run follows it from the next read (<see cref="PlanRefresh" />) — which is planned in
    ///     the positions the page has, the rows as they were, and answered with it open. False if it was open. (How many
    ///     may be is its session's to say: <see cref="GridRecentSet{TKey}" />.)
    /// </summary>
    public bool OpenRow(in TKey key)
    {
        if (IsOpen(key)) return false;
        _toggles.Add((key, true, false));
        return true;
    }

    /// <summary>Closes a row: its run goes with the next read. False if it wasn't open.</summary>
    public bool CloseRow(in TKey key)
    {
        if (!IsOpen(key)) return false;
        _toggles.Add((key, false, false));
        return true;
    }

    /// <summary>
    ///     Every row of the view opened — rows arriving too — or closed, from the next read, as <see cref="OpenRow" />; the
    ///     rows opened or closed before, as the rest. Every row open, a row's view position counts the runs above it that
    ///     reads have crossed, a run not read yet none, so the total is at least so many (<see cref="TotalKind.AtLeast" />)
    ///     and settles as the reader goes; <see cref="CloseRow" /> closes one (the rows closed go on the wire, the rest
    ///     open), <see cref="OpenRow" /> opens it again.
    /// </summary>
    public void SetAllOpen(bool open)
    {
        _toggles.Add((default, open, true));
    }

    /// <summary>The rows opened and closed since the last read, as they are now: the read planned, its answer laid out with them.</summary>
    private void Toggled()
    {
        foreach (var (key, open, all) in _toggles)
            if (all) // (every row: the runs counted before go, counted afresh as read)
            {
                (_allOpen, _nested) = (open, _nested || _open.Count > 0);
                _closed.Clear();
                _counted.Clear();
                _open.Clear();
            }
            else if (_allOpen)
            {
                if (open)
                {
                    _closed.Remove(key); // (counted once read)
                }
                else
                {
                    _closed.Add(key);
                    _open.Close(key);
                    _counted.Close(key);
                }
            }
            else if (open)
            {
                _open.Open(key);
            }
            else
            {
                _open.Close(key);
            }

        _toggles.Clear();
    }

    /// <summary>
    ///     Every row open, the view's rows read for the window — but those closed — counted: their runs read and counted
    ///     from now. Past the most counted, the least recently read are forgotten, their runs counted no more (above the
    ///     page's first row: its rows kept where they're drawn), never one the window holds.
    /// </summary>
    private void Count(ReadOnlySpan<TKey> keys)
    {
        _counted.Shown(keys); // (those counted already: kept while it holds them)
        foreach (var key in keys)
            if (!_closed.Contains(key) && _counted.Open(key, _uncounted))
                _open.Open(key);
        foreach (var gone in _uncounted) _open.Close(gone);
        _uncounted.Clear();
        _counted.Shown(keys);
    }

    /// <summary>
    ///     The view's rows read, the runs the window crosses are to read next (<see cref="RunReads" />), their answer
    ///     <see cref="ApplyRuns" />.
    /// </summary>
    public bool NeedsRuns { get; private set; }

    /// <summary>The slices of the runs the window crosses, to read (<see cref="NeedsRuns" />).</summary>
    public ReadOnlyMemory<GridRunRead<TKey>> RunReads => _runReads.AsMemory(0, _runCount);

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
        if (_keepRows > 0) return PlanKept(); // (asked before it's placed: placed first, the page asking again from there)
        var first = request.First;
        long from, to;
        var current = !Live && request.Holds == Holds; // (asked in this hold)
        if (current && request.LetIn != LetIn)
            first += LetIn - request.LetIn; // (in its positions: before the moves it hasn't applied — rows let in, runs above)

        // The live top: at it; asked for (the pill, Home); or, fewer arrived above than the view holds, the first row shown
        // scrolled up to — or the reader racing up, anywhere, with fewer above than the view holds that they haven't seen,
        // as the pill counts them (those let in above the view among them: not only those to let in). (As many or more
        // above: the reader scrolls on into them, let in. Held at the first row already — a filter's new view placed
        // there — and not moved: held still.)
        var few = Hidden < request.Count;
        var scrolledUp = first == 0 && _askedFirst != 0 && !_placed; // (placed by a locate: the page still where it was)
        var racing = request.Racing && Math.Max(0, Hidden) + Math.Min(_unseen, _open.Locate(first).Top) < request.Count;
        if (current && first != 0) _placed = false; // (the page has gone to the row: from now, scrolling up to the top releases it)
        if (Live ? first == 0 : request.Top || (current ? (few && scrolledUp) || racing : first == 0 && !_placed))
        {
            var missing = !Covers(0, request.Count); // (in the positions shown until now)
            if (!Live) Release();
            (from, to) = (0, request.Count + (long)request.After);
            _hasRef = false;
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
        if (current) _unseen = Math.Min(_unseen, _open.Locate(first).Top); // (those the page has shown, and below them, seen)
        Reference(first);
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
        Reference(_askedFirst);
        return PlanFrom(_restFrom, _restTo);
    }

    /// <summary>The window again, as it was asked for — a new view keeping the page's place, placed first: false if there's none yet.</summary>
    public bool PlanRefresh(out GridRead<TKey, TRow> read)
    {
        if (_keepRows > 0)
        {
            read = PlanKept();
            return true;
        }

        if (_to <= _from)
        {
            read = default;
            return false;
        }

        PlannedFirst = false;
        Reference(_askedFirst >= 0 ? _askedFirst : _from);
        read = PlanFrom(_from, _to);
        return true;
    }

    /// <summary>
    ///     A hold from the live top set on the row the page shows at <paramref name="first" />: read about it, wherever it
    ///     stands now — first the view and a view below (<paramref name="count" /> rows each), small, so on a slow link it
    ///     goes out alone, and with it the rows arrived above (the page's pill); the rest of the window
    ///     [<paramref name="from" />, <paramref name="to" />) once the page has it. A run's row: the hold is set on its open
    ///     row, above it, the run read after it as ever.
    /// </summary>
    private GridRead<TKey, TRow> PlanAnchored(in TKey key, long first, int count, long from, long to)
    {
        var end = Math.Min(to, first + 2L * Math.Max(1, count));
        (_restFrom, _restTo) = (from, to);
        NeedsRest = from < first || to > end;
        PlannedFirst = NeedsRest;
        (_planFrom, _planTo, _askedFirst) = (first, end, first);
        (_hasRef, _keeping) = (false, false);
        var row = key;
        var at = _open.Locate(first).Top;
        if (_open.Count > 0 && OwnerOf(key, out var owner) && _open.AtOf(owner) is >= 0 and var placed)
            (row, at) = (owner, placed);
        (_planAt, _planTopFrom, _planTopTo) = (at, at, Math.Max(at + 1, _open.Locate(end - 1).Top + 1));
        (_planKind, _anchoring) = (GridReadKind.AroundKey, true);
        return GridRead<TKey, TRow>.AroundKey(row, 0, (int)(_planTopTo - at));
    }

    /// <summary>
    ///     A new view keeping the page's place: about where its first row, as it was, sorts in it — whether or not the new
    ///     view has it — the view's rows from there and as many above (<see cref="Reset" />).
    /// </summary>
    private GridRead<TKey, TRow> PlanKept()
    {
        (NeedsRest, PlannedFirst, _anchoring, _hasRef, _keeping) = (false, false, false, false, true);
        _planKind = GridReadKind.AroundRow;
        return GridRead<TKey, TRow>.AroundRow(_keptKey, _keptRow, _keepRows, _keepRows);
    }

    /// <summary>A key's row, wherever it is, with <paramref name="before" /> rows above it and <paramref name="count" /> on.</summary>
    public GridRead<TKey, TRow> PlanLocate(in TKey key, int before, int count)
    {
        _keepRows = 0; // (a place of its own instead)
        (NeedsRest, PlannedFirst) = (false, false);
        (_anchoring, _hasRef, _keeping) = (false, false, false);
        _planKind = GridReadKind.AroundKey;
        (_locateBefore, _locateCount) = (before, count);
        return GridRead<TKey, TRow>.AroundKey(key, before, count);
    }

    /// <summary>
    ///     A new sort or filter, the <paramref name="view" />th: nothing held carries over — the window's rows no longer
    ///     stand where they did (they go as the new window is published) — and the next read is of the top, as many rows.
    ///     Keeping the page's place (<paramref name="keep" />: the rows of its view), held, the next read is about where the
    ///     row the page shows first sorts in the new view instead — its new sort's order, its new filter's rows — whether
    ///     the view has it or not: the view's rows from there (past their end, the last of them), in a hold of their own — at the
    ///     top too, held till the reader goes to it (scrolls up to it, the pill, Home). Rows open stay open, their runs read
    ///     afresh (another view's).
    /// </summary>
    public void Reset(int view, int keep = 0)
    {
        View = view;
        _keepRows = keep > 0 && !Live && FirstShown(out _keptKey, out _keptRow) ? keep : 0;
        (_keeping, _hasLast) = (false, false);
        if (!Live) Release();
        _anchored = false;
        (NeedsRest, PlannedFirst, NeedsRuns, _hasRef) = (false, false, false, false);
        Toggled();
        if (_allOpen) // (every row open: the runs counted another view's, counted afresh as read)
        {
            _open.Clear();
            _counted.Clear();
        }
        else
        {
            _open.Unplace();
        }
        _to -= _from;
        _from = 0;
    }

    /// <summary>
    ///     The answer to the read last planned, rows in <paramref name="page" />: published, in one transaction. False if
    ///     nothing went out (refused and already stale; or nothing changed) — or if the runs the window crosses are to read
    ///     first (<see cref="NeedsRuns" />), their answer published with it (<see cref="ApplyRuns" />).
    /// </summary>
    public bool Apply(in GridAnswer answer, GridPage<TKey, TRow> page)
    {
        NeedsRuns = false;
        if (answer.Refusal is { } refusal) return Refuse(refusal);
        Toggled();

        long firstView; // the top position of the page's first row
        _locatedRank = -1;
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
            case GridReadKind.AroundRow when _keeping:
                var rows = _keepRows;
                (_keeping, _keepRows) = (false, 0);
                if (answer.Anchor == GridAnswer.NotFound) // (not placed: the new view's top, as with no place to keep)
                {
                    (NeedsRest, PlannedFirst, _restFrom, _restTo) = (true, false, 0, rows);
                    return false;
                }

                var place = answer.FirstRank + answer.Anchor; // (the new view's first row not before the page's)
                var start = Math.Max(0, Math.Min(place, answer.Total - rows)); // (past their end, their last)
                Hold(); // (a hold of its own, positions ranks from here: at the top too, till the reader goes to it)
                firstView = answer.FirstRank;
                (_planTopFrom, _planTopTo) = (start, start + rows);
                (_planFrom, _planTo, _askedFirst) = (start, start + rows, start);
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
                    _placed = true; // (the page brings it into view next, asking from where it was till then)
                }

                firstView = answer.FirstRank;
                _open.Learn(page.Keys, firstView); // (where the rows open are: the row's view position counts their runs)
                _locatedRank = rank;
                Located = _open.Display(rank);
                (_planTopFrom, _planTopTo) = (Math.Max(0, rank - _locateBefore), rank + _locateCount);
                (_planFrom, _planTo) = (Math.Max(0, Located.Value - _locateBefore), Located.Value + _locateCount);
                break;
            default:
                if (!Live && Hidden > answer.Total) Hidden = answer.Total; // (rows above can't outnumber the view's)
                firstView = answer.FirstRank - (Live ? 0 : Hidden);
                break;
        }

        var i0 = (int)Math.Clamp(_planTopFrom - firstView, 0, page.Count);
        var i1 = (int)Math.Clamp(_planTopTo - firstView, i0, page.Count);
        var keys = page.Keys[i0..i1];
        _keep.Clear();
        foreach (var key in keys)
            if (!_keep.Add(key))
                return Refuse("The source answered a key twice.");

        // In top positions. A page ending short of what was asked for found where the rows end.
        var total = answer.Total - (Live ? 0 : Hidden);
        var end = firstView + page.Count;
        _ended = page.Count > 0 && end < _planTopTo;
        if (_ended) total = end;
        (_topTotal, _topKind) = (total, _ended ? TotalKind.Exact : answer.TotalKind);
        Refusal = null;
        (_from, _to) = (_planFrom, _planTo);
        if (_allOpen) Count(keys);
        if (_open.Count == 0 && !_nested) // (none open, none shown open: the view's rows are the window, as they stand)
        {
            Total = Live ? total : _ended ? Set(total) : _held.Hold(total);
            TotalKind = Kind();
            _hasLead = false;
            var published = Publish(keys, page.Rows[i0..i1], default, firstView + i0, NeedsRest);
            (_topKeys, _topRows, _topFirst, _topCount) = (_keys, _rows, _first, _count);
            KeepLast();
            return published;
        }

        _open.Learn(page.Keys, firstView);
        KeepTop(keys, page.Rows[i0..i1], firstView + i0);
        if (PlanRuns())
        {
            NeedsRuns = true; // (published with them)
            return false;
        }

        return Compose(null);
    }

    /// <summary>
    ///     The answer to the runs last planned (<see cref="RunReads" />), their rows in <paramref name="page" />: the window
    ///     published with them, in one transaction. False if nothing went out.
    /// </summary>
    public bool ApplyRuns(in GridAnswer answer, GridRunPage<TKey, TRow> page)
    {
        NeedsRuns = false;
        if (answer.Refusal is { } refusal) return Refuse(refusal);
        if (page.Slices != _runCount)
            return Refuse($"The source answered {page.Slices} slices of runs for {_runCount} asked for.");
        for (var s = 0; s < _runCount; s++) _open.SetLength(_runReads[s].Owner, page.AnswerOf(s).Length);
        return Compose(page);
    }

    private long Set(long total)
    {
        _held.Set(total);
        return total;
    }

    private bool Refuse(string reason)
    {
        (Refusal, Moved, NeedsRuns) = (reason, false, false); // (read as the source is now)
        using var tx = _status is null ? Transaction.Begin(_order) : Transaction.Begin(_order, _status);
        var changed = StageOrder(tx);
        changed |= StageStatus(tx);
        tx.Complete();
        return changed;
    }

    /// <summary>The window's rows of the view, as read: those published, and an open row above them whose run they start in.</summary>
    private void KeepTop(ReadOnlySpan<TKey> keys, ReadOnlySpan<TRow> rows, long first)
    {
        if (_ownTopKeys.Length < keys.Length)
        {
            _ownTopKeys = new TKey[Math.Max(keys.Length, 2 * _ownTopKeys.Length)];
            _ownTopRows = new TRow[_ownTopKeys.Length];
        }

        keys.CopyTo(_ownTopKeys);
        rows.CopyTo(_ownTopRows);
        (_topKeys, _topRows, _topFirst, _topCount) = (_ownTopKeys, _ownTopRows, first, keys.Length);
        KeepLast();
    }

    /// <summary>The window's first row of the view, kept: a held window none of whose rows are left is read about it.</summary>
    private void KeepLast()
    {
        if (_topCount > 0) (_lastKey, _lastRow, _lastTop, _hasLast) = (_topKeys[0], _topRows[0], _topFirst, true);
    }

    /// <summary>
    ///     The slices of the runs the window crosses, of the rows open among the view's rows read, each from where the
    ///     window starts in it (or about the row the page shows first, held in it) to where the window ends: true if any.
    /// </summary>
    private bool PlanRuns()
    {
        _runCount = 0;
        for (var k = 0; k < _topCount; k++)
        {
            var key = _topKeys[k];
            if (!_open.IsOpen(key)) continue;
            var at = _open.Display(_topFirst + k);
            if (at + 1 >= _planTo) break; // (its run starts past the window, and the rest's do)
            var offset = Math.Max(0, _planFrom - at - 1);
            var span = _planTo - Math.Max(_planFrom, at + 1);
            if (_open.TryLength(key, out var length)) span = Math.Min(span, Math.Max(0, length - offset) + RunSlack);
            else if (_allOpen) span = Math.Min(span, RunSlack); // (every row open, one not read yet: a few, the rest once known)
            var count = (int)Math.Min(PageCapacity, span);
            GridRunRead<TKey> read;
            if (_hasRef && _refRun && _refOwner.Equals(key))
            {
                var before = (int)Math.Clamp(_refOffset - offset, 0, count);
                read = GridRunRead<TKey>.AroundKey(key, _refKey, before, count - before) with { MaxBytes = MaxBytes };
            }
            else
            {
                read = GridRunRead<TKey>.AtOffset(key, offset, count) with { MaxBytes = MaxBytes };
            }

            if (_runCount == _runReads.Length)
            {
                Array.Resize(ref _runReads, 2 * _runCount);
                Array.Resize(ref _runOwners, 2 * _runCount);
            }

            (_runReads[_runCount], _runOwners[_runCount++]) = (read, k);
        }

        return _runCount > 0;
    }

    /// <summary>
    ///     The window composed from the view's rows read and the slices of their runs (none: no run in it), each row at its
    ///     view position, and published. Held, the page's first row is kept where it's drawn: runs come, gone or grown
    ///     above it move every position by as many (<see cref="LetIn" />). Short of the window asked for — runs grown or
    ///     gone since the view's rows were read, or a gap between a run's slice and the next row — the rest is read next.
    /// </summary>
    private bool Compose(GridRunPage<TKey, TRow>? runs)
    {
        var shift = 0L;
        if (_hasRef && !Live && ReferenceNow(runs, out var now)) shift = now - _refAt;
        _hasRef = false;
        var (from, to) = (Math.Max(0, _planFrom + shift), _planTo + shift);
        int n = 0, slice = 0, lead = -1;
        long first = from, next = -1;
        var whole = true;
        for (var k = 0; k < _topCount && whole; k++)
        {
            var at = _open.Display(_topFirst + k);
            if (at >= to) break;
            var open = _open.IsOpen(_topKeys[k]);
            if (at >= from) whole = Add(ref n, ref first, ref next, at, _topKeys[k], _topRows[k], open ? Open : (byte)0);
            if (runs is null || slice >= _runCount || _runOwners[slice] != k) continue;
            var (s0, s1) = runs.Slice(slice);
            var start = at + 1 + runs.AnswerOf(slice++).FirstOffset;
            for (var j = s0; j < s1 && whole; j++)
            {
                if (start + (j - s0) >= to) break;
                if (start + (j - s0) < from) continue;
                if (n == 0) lead = k; // (the window starts in its run)
                whole = Add(ref n, ref first, ref next, start + (j - s0), runs.Keys[j], runs.Rows[j], runs.Depths[j]);
            }
        }

        var keys = _composed.AsSpan(0, n);
        _keep.Clear();
        foreach (var key in keys)
            if (!_keep.Add(key))
                return Refuse("The source answered a key twice (a run's row and another).");

        // The window starts in a run: its open row, read above it.
        (_hasLead, _leadKey, _leadTop) = lead >= 0 ? (true, _topKeys[lead], _topFirst + lead) : (false, default, 0);
        _leadAt = _hasLead ? _open.Display(_leadTop) : 0;
        var total = _topTotal + _open.RunRows(_topTotal);
        if (shift != 0) Moving(shift);
        Total = Live ? total : _ended ? Set(total) : _held.Hold(total);
        TotalKind = Kind();
        if (_locatedRank >= 0) Located = _open.Display(_locatedRank);
        var reached = n == 0 ? from : first + n;
        var cut = !whole || (n > 0 && first > from) || (reached < to && !_ended); // (the view's rows ending sooner: the window ends with them)
        if (cut && !NeedsRest && !_short) (NeedsRest, _restFrom, _restTo) = (true, from, to); // (once: a source that keeps moving waits its cadence)
        _short = cut;
        _nested = _open.Count > 0;
        return Publish(keys, _composedRows.AsSpan(0, n), _composedDepths.AsSpan(0, n), first, NeedsRest);
    }

    /// <summary>The total's kind: every row open, at least so many while some rows' runs aren't counted.</summary>
    private TotalKind Kind()
    {
        return _allOpen && _topKind == TotalKind.Exact && _counted.Count < _topTotal ? TotalKind.AtLeast : _topKind;
    }

    /// <summary>A composed window's next row: false if it isn't next to the last (a gap), and nothing added.</summary>
    private bool Add(ref int n, ref long first, ref long next, long at, in TKey key, in TRow row, byte depth)
    {
        if (n == 0) first = at;
        else if (at != next) return false;
        if (n == _composed.Length)
        {
            Array.Resize(ref _composed, 2 * n);
            Array.Resize(ref _composedRows, 2 * n);
            Array.Resize(ref _composedDepths, 2 * n);
        }

        (_composed[n], _composedRows[n], _composedDepths[n++]) = (key, row, depth);
        next = at + 1;
        return true;
    }

    /// <summary>
    ///     Held: the row the page shows first at view position <paramref name="at" />, as published, to keep where it's
    ///     drawn as runs above it come or go (none live, or with none open or shown open).
    /// </summary>
    private void Reference(long at)
    {
        _hasRef = false;
        if (Live || !_anchored || (_open.Count == 0 && !_nested && _toggles.Count == 0) || at < _first || at >= _first + _count) return;
        var i = (int)(at - _first);
        (_refKey, _refAt, _hasRef) = (_keys[i], at, true);
        _refRun = _depths[i] is > 0 and < Open;
        if (!_refRun) return;
        var j = i - 1; // (its open row: the nearest row of the view above it, else the one above the window)
        while (j >= 0 && _depths[j] is > 0 and < Open) j--;
        if (j < 0 && !_hasLead)
        {
            _hasRef = false;
            return;
        }

        (_refOwner, _refOffset) = j >= 0 ? (_keys[j], at - (_first + j) - 1) : (_leadKey, at - _leadAt - 1);
    }

    /// <summary>
    ///     Where the page's first row stands now, in view positions — a run's, its run closed since: its open row, drawn where
    ///     the run's row was. False if it's gone from what was read.
    /// </summary>
    private bool ReferenceNow(GridRunPage<TKey, TRow>? runs, out long now)
    {
        now = 0;
        var owner = _refRun ? _refOwner : _refKey;
        for (var k = 0; k < _topCount; k++)
        {
            if (!_topKeys[k].Equals(owner)) continue;
            now = _open.Display(_topFirst + k);
            if (!_refRun || !_open.IsOpen(owner)) return true;
            if (runs is null) return false;
            for (var s = 0; s < _runCount; s++)
            {
                if (_runOwners[s] != k) continue;
                var (s0, s1) = runs.Slice(s);
                ref readonly var answer = ref runs.AnswerOf(s);
                if (_runReads[s].Kind == GridRunReadKind.AroundKey)
                {
                    if (answer.Anchor == GridAnswer.NotFound) return false;
                    now += 1 + answer.FirstOffset + answer.Anchor;
                    return true;
                }

                for (var j = s0; j < s1; j++)
                    if (runs.Keys[j].Equals(_refKey))
                    {
                        now += 1 + answer.FirstOffset + (j - s0);
                        return true;
                    }

                return false;
            }

            return false;
        }

        return false;
    }

    /// <summary>Every view position moves by <paramref name="by" />: runs above the page's first row came, went or grew.</summary>
    private void Moving(long by)
    {
        LetIn += by;
        _from += by;
        _to += by;
        if (_askedFirst >= 0) _askedFirst += by;
        _restFrom += by;
        _restTo += by;
        _held.Shift(by);
    }

    /// <summary>
    ///     The window as composed, published (<paramref name="depths" /> none: every row the view's, none open) in one
    ///     transaction with its order and status: true if anything changed.
    /// </summary>
    private bool Publish(ReadOnlySpan<TKey> keys, ReadOnlySpan<TRow> rows, ReadOnlySpan<byte> depths, long first, bool linger)
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
            if (keys.Length > _keys.Length) // (a wider window than any before: room for it, kept)
            {
                if (ReferenceEquals(_topKeys, _keys)) (_topKeys, _topRows, _topCount) = ([], [], 0); // (the old window's, shared)
                _keys = new TKey[Math.Max(keys.Length, 2 * _keys.Length)];
                _rows = new TRow[_keys.Length];
                _depths = new byte[_keys.Length];
                Array.Resize(ref _lingering, _keys.Length); // (those left kept)
            }

            keys.CopyTo(_keys);
            rows.CopyTo(_rows);
            if (depths.IsEmpty) _depths.AsSpan(0, keys.Length).Clear();
            else depths.CopyTo(_depths);
            (_first, _count, _anchored) = (first, keys.Length, true);
            staged = StageOrder(tx);
            staged |= StageStatus(tx);
            tx.Complete();
        }

        return staged || _table.Version != version;
    }

    /// <summary>
    ///     Builds the order of the window as published; stages it if it isn't as last published. True if staged. Following
    ///     the live top in the keys' order, the table holding the window's rows alone (none lingering), it goes without
    ///     keys: the page orders the rows of the view by theirs, and an update carries only the rows that changed. Rows
    ///     open, their runs follow the view's keys: each open row's, by key, with its run's rows in the window and their
    ///     depths (<see cref="GridOrder" />).
    /// </summary>
    private bool StageOrder(Transaction tx)
    {
        var byKey = ByKey && Live && _lingered == 0;
        var (top, groups, rows) = Shape();
        var size = GridOrder.SizeOf(byKey ? 0 : top, KeySize) +
                   (groups == 0 ? 0 : 4 + groups * (KeySize + 5) + rows * (KeySize + 1));
        if (size > _built.Length) // (room for a wider window's, kept)
        {
            var room = Math.Max(size, 2 * _built.Length);
            _built = new byte[room];
            Array.Resize(ref _published, room);
        }

        var header = new GridOrderHeader
        {
            Live = Live, Stale = Stale, ByKey = byKey, Runs = groups > 0, AllOpen = _allOpen, TotalKind = TotalKind,
            KeySize = KeySize, First = _first, Total = Total, LetIn = LetIn, Holds = Holds, View = View
        };
        int o;
        if (rows == 0) // (every row the view's: its keys as they stand)
        {
            o = GridOrder.Write(_built, header, Keys);
        }
        else
        {
            o = GridOrder.WriteHeader(_built, header with { Count = top });
            if (!byKey)
                for (var i = 0; i < _count; i++)
                    if (_depths[i] is 0 or Open)
                        o += Key(_keys[i], o);
        }

        if (groups > 0) o = WriteRuns(o, groups);
        var built = _built.AsSpan(0, o);
        if (o == _publishedSize && built.SequenceEqual(_published.AsSpan(0, o))) return false;
        tx.Of(_order).Publish(built);
        built.CopyTo(_published);
        _publishedSize = o;
        return true;
    }

    /// <summary>
    ///     The window as published: the view's rows in it, the runs (open rows: theirs, and one above it — every row open,
    ///     those with rows in it, and the closed rows'), their rows.
    /// </summary>
    private (int Top, int Groups, int Rows) Shape()
    {
        int top = 0, groups = _hasLead ? 1 : 0, rows = 0;
        for (var i = 0; i < _count; i++)
            if (_depths[i] is 0 or Open)
                (top, groups) = (top + 1, groups + (Grouped(i) ? 1 : 0));
            else
                rows++;
        return (top, groups, rows);
    }

    /// <summary>
    ///     Whether the view's row at <paramref name="i" /> has a run in the order: open (every row open, with rows of its
    ///     run in the window), or closed with every row open.
    /// </summary>
    private bool Grouped(int i)
    {
        if (!_allOpen) return _depths[i] == Open;
        return _depths[i] == 0 || (i + 1 < _count && _depths[i + 1] is > 0 and < Open);
    }

    /// <summary>
    ///     The runs of the window as published, at <paramref name="o" />: u32 runs, then each run — its open row's key | u8
    ///     flags (1: the open row is above the window, its run the window's first rows) | u32 rows | their keys | their
    ///     depths. The offset past them.
    /// </summary>
    private int WriteRuns(int o, int groups)
    {
        var into = _built.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(into[o..], groups);
        o += 4;
        var i = 0;
        if (_hasLead) o = WriteRun(o, _leadKey, GridOrder.LeadRun, ref i);
        for (; i < _count;)
        {
            var at = i++;
            if (!Grouped(at)) continue;
            o = WriteRun(o, _keys[at], _depths[at] == 0 ? GridOrder.ClosedRun : (byte)0, ref i); // (closed: no rows follow)
        }

        return o;
    }

    /// <summary>A run: its open row's key, flags and the rows from <paramref name="i" /> to the next row of the view.</summary>
    private int WriteRun(int o, in TKey owner, byte flags, ref int i)
    {
        var into = _built.AsSpan();
        o += Key(owner, o);
        into[o++] = flags;
        var start = i;
        while (i < _count && _depths[i] is > 0 and < Open) i++;
        BinaryPrimitives.WriteInt32LittleEndian(into[o..], i - start);
        o += 4;
        for (var j = start; j < i; j++) o += Key(_keys[j], o);
        for (var j = start; j < i; j++) into[o++] = _depths[j];
        return o;
    }

    private int Key(in TKey key, int o)
    {
        MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)).CopyTo(_built.AsSpan(o));
        return KeySize;
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
        size = GridStatusValue.Write(_statusBuilt, Unseen, Overscan, refusal, Moved);
        var built = _statusBuilt.AsSpan(0, size);
        if (built.SequenceEqual(_statusPublished)) return false;
        tx.Of(_status).Publish(built);
        if (_statusPublished.Length != size) _statusPublished = new byte[size];
        built.CopyTo(_statusPublished);
        return true;
    }

    /// <summary>
    ///     A read for the window [from, to), in view positions: of the view's rows it holds, in top positions, the runs the
    ///     window crosses to read after them (<see cref="NeedsRuns" />).
    /// </summary>
    private GridRead<TKey, TRow> PlanFrom(long from, long to)
    {
        (_planFrom, _planTo) = (from, to);
        (_anchoring, _keeping) = (false, false);
        var topFrom = _open.Locate(from).Top; // (in a run: from its open row, above it)
        var topTo = to > from ? _open.Locate(to - 1).Top + 1 : topFrom;
        (_planTopFrom, _planTopTo) = (topFrom, topTo);
        if (!Live && _anchored && _topCount > 0)
        {
            // Held, near the window: from its row nearest where what's wanted starts — rows arriving above it don't
            // move what's read, and where it stands now says how many have.
            var at = Math.Clamp(topFrom, _topFirst, _topFirst + _topCount - 1);
            if (Math.Abs(topFrom - at) <= _near)
            {
                var i = (int)(at - _topFirst);
                _planKind = GridReadKind.AroundRow;
                _planAt = at;
                return GridRead<TKey, TRow>.AroundRow(_topKeys[i], _topRows[i], (int)Math.Max(0, at - topFrom),
                    (int)Math.Max(0, topTo - at));
            }
        }
        else if (!Live && _topCount == 0 && _hasLast && Math.Abs(topFrom - _lastTop) <= _near)
        {
            // Held, none of the window's rows left (the view moved past them — rows that stopped passing its filter), and
            // asked for near them: about the last it held, where it sorts now, so the rows above are counted as they stand,
            // not as they came.
            _planKind = GridReadKind.AroundRow;
            _planAt = _lastTop;
            return GridRead<TKey, TRow>.AroundRow(_lastKey, _lastRow, (int)Math.Clamp(_lastTop - topFrom, 0, int.MaxValue),
                (int)Math.Clamp(topTo - _lastTop, 1, int.MaxValue));
        }

        // Live, positions are ranks; held and far from the window, placed by rank past the rows not let in.
        _planKind = GridReadKind.AtRank;
        var hidden = Live ? 0 : Hidden;
        var rank = Math.Max(0, topFrom + hidden);
        return GridRead<TKey, TRow>.AtRank(rank, (int)Math.Max(0, topTo + hidden - rank));
    }

    /// <summary>
    ///     The row the page shows first, as published (the window's first, if the page hasn't asked in this hold) — a run's,
    ///     its open row — by key and values: false if there's none.
    /// </summary>
    /// <summary>Held, the key of the view's row the page shows first (a run's row: its open row's); none at the live top.</summary>
    public TKey? HeldAt => !Live && FirstShown(out var key, out _) ? key : null;

    private bool FirstShown(out TKey key, out TRow row)
    {
        (key, row) = (default, default);
        if (!_anchored || _count == 0) return false;
        var i = (int)Math.Clamp((_askedFirst >= 0 ? _askedFirst : _from) - _first, 0, _count - 1);
        if (_depths[i] is > 0 and < Open)
        {
            while (i >= 0 && _depths[i] is > 0 and < Open) i--; // (its open row: the nearest row of the view above it)
            if (i < 0)
            {
                if (!_hasLead) return false;
                for (var k = 0; k < _topCount; k++) // (above the window: read with the view's rows)
                    if (_topKeys[k].Equals(_leadKey))
                    {
                        (key, row) = (_leadKey, _topRows[k]);
                        return true;
                    }

                return false;
            }
        }

        (key, row) = (_keys[i], _rows[i]);
        return true;
    }

    /// <summary>The open row whose run <paramref name="key" />'s row is in, as published: false if it's a row of the view.</summary>
    private bool OwnerOf(in TKey key, out TKey owner)
    {
        owner = default;
        for (var i = 0; i < _count; i++)
        {
            if (!_keys[i].Equals(key)) continue;
            if (_depths[i] is 0 or Open) return false;
            var j = i - 1;
            while (j >= 0 && _depths[j] is > 0 and < Open) j--;
            if (j >= 0) owner = _keys[j];
            else if (_hasLead) owner = _leadKey;
            else return false;
            return true;
        }

        return false;
    }

    private void Hold()
    {
        Live = false;
        Holds++;
        Hidden = 0;
        LetIn = 0;
        _unseen = 0;
        _placed = false;
        _askedFirst = -1;
        _hasRef = false;
        _held.Clear();
    }

    private void Release()
    {
        _open.Shift(Hidden); // (held positions turned to ranks)
        Live = true;
        Hidden = 0;
        LetIn = 0;
        _unseen = 0;
        _placed = false;
        _askedFirst = -1;
        _hasRef = false;
        _held.Clear();
    }

    /// <summary>Lets in <paramref name="rows" /> of those arrived above: every position moves down by as many.</summary>
    private void LetInAbove(int rows)
    {
        Hidden -= rows;
        LetIn += rows;
        _unseen += rows; // (in at the top, above the view: unseen, as those let in before them)
        _first += rows;
        if (!ReferenceEquals(_topKeys, _keys)) _topFirst += rows;
        else _topFirst = _first;
        _from += rows;
        _to += rows;
        _held.Shift(rows);
        Total += rows;
        _open.Shift(rows);
        _lastTop += rows;
        if (!_hasLead) return;
        _leadAt += rows;
        _leadTop += rows;
    }
}
