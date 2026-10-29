using System.Numerics;
using System.Runtime.CompilerServices;
using Tapline.Grid.Provider;

namespace Tapline.Grid;

/// <summary>
///     One grid's window for one client, kept by the server and carried by Tapline: the rows in view and pre-fetch
///     either side, as a table, and their order, as a value, written together in one transaction per answer and read by
///     the page through one watch (<see cref="Binding" />). The page asks for windows (<see cref="Request" />), each
///     answered by a rendezvous carrying its id; between, the source's changes are read again at the cadence the window's
///     state allows (live top or held).
/// </summary>
/// <remarks>
///     Reads go one at a time, on the session's own loop; a newer request, sort or filter cancels the read in flight,
///     its token going into the source. Warm, an answer allocates nothing on the session's way to the wire.
/// </remarks>
public sealed class GridSession<TKey, TRow> : IAsyncDisposable
    where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private static readonly ConditionalWeakTable<Outlet, StrongBox<int>> OpenByClient = new();
    private static readonly int RowBytes = 1 + Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TRow>(); // a row coming in, on the wire

    private readonly long _climbEvery, _climbLiveEvery; // the overscan's climb, held and live: in the clock's ticks (0: none)
    private readonly long _stepFor; // the most a climb's step takes to arrive, four fills: waiting longer, the link is the limit
    private readonly int _stepRows; // the most rows either side a step adds: its record in band, a quarter of the outlet's bound
    private readonly ITimer _climbTimer; // (wakes the loop for the overscan's next step while nothing else does)
    private readonly ITimer _restTimer; // (wakes the loop to look whether a window's first answer is in front of the page)
    private readonly byte[] _correlation = new byte[16];
    private readonly Lock _gate = new();
    private readonly long _heldEvery, _liveEvery; // in the clock's ticks
    private readonly StrongBox<int> _open; // the client's grids
    private readonly GridOptions _options;
    private readonly Outlet _outlet;
    private readonly double _overscanFill; // seconds (0: no overscan told, the page's own count)
    private readonly GridPage<TKey, TRow> _page;
    private GridRunPage<TKey, TRow>? _runPage; // (made as a row is first opened)
    private readonly Task _run;
    private readonly IGridSource<TKey, TRow> _source;
    private readonly Tap _tap;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Wake _wake = new();
    private readonly IDisposable _watching;
    private readonly GridWindow<TKey, TRow> _window;

    // What's asked of the loop: under _gate (the source's changes by a flag of their own, set on its thread).
    private CancellationTokenSource? _cancel; // the read in flight's (null: taken, to be cancelled)
    private int _changed;
    private bool _byKey; // the view's order is its keys', greatest first (GridWindow.ByKey)
    private bool _disposed, _reading, _requested, _viewChanged, _visible = true;
    private bool _turning; // (for Settled: the loop from deciding to read till it decides again — the answer applied, reported)
    private bool _keepPlace; // (the new view keeps the page's place: GridWindow.Reset)
    private int _views; // how often the view has been set: the page's requests carry the one they're in
    private Locating? _locating;
    private readonly GridRecentSet<TKey> _openRows; // the rows opened — every row open, closed — as the session's callers
                                                   // have said (and it remembers)
    private bool _allOpen; // every row open but those (SetAllOpen)
    private readonly List<(TKey Key, bool Open, bool All)> _toggles = []; // and as the loop hasn't yet told its window
    private readonly List<TKey> _forgotten = []; // (rows forgotten to make room for another: as every row is again)
    private long _readFrom, _readTo; // the window being read, in view positions: a request not touching it is a jump
    private long _asks, _askedAt; // windows asked for, and when the last was
    private int _askedRows; // and the rows in view it asked with
    private GridRequest _request;
    private TKey? _anchor; // the row the page shows at the request's first position, by key (GridWindow.Plan)
    private GridStatus _status = new() { Live = true };
    private GridView _view;

    // The loop's own.
    private long _armedFor = long.MinValue, _climbArmedFor = long.MinValue;
    private int _fillOverscan; // what the link carries in OverscanFill (OverscanFor): the overscan told is never less
    private int _climbedOverscan; // and what it has climbed to, the reader at rest and the link with room
    private long _overscanLookedAt, _climbDue; // when the overscan was last looked at; its next look while it may climb (0: none)
    private long _asksAtStep; // the windows asked for as it last climbed or fell: the page has asked with it once there are more
    private long _waitingSince; // the watch's state waiting at every look since (0: not at the last)
    private bool _wasLive; // the window live at the last look (climbing afresh as it turns live)
    private bool _pending; // a change not yet read
    private long _restAfter; // a window's first answer staged, its watch's version: its rest read once the page has it (0: none)
    private long _restPoll; // how long till the loop looks again whether it has (ms; 0: not yet looked)
    private long _readAt; // when the last read began
    private readonly HashSet<GridView> _readViews = new(ReferenceEqualityComparer.Instance); // read since one was answered (Leave)

    /// <summary>
    ///     Opens a grid on <paramref name="source" /> for <paramref name="outlet" />'s client: refused, with the reason
    ///     (<see cref="GridRefusedException" />), if <paramref name="options" /> can't work or the client has its most
    ///     grids open already.
    /// </summary>
    /// <exception cref="OutletFullException">The client's subscriptions hold its Tapline budget.</exception>
    public GridSession(Outlet outlet, IGridSource<TKey, TRow> source, GridView? view = null, GridOptions? options = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(outlet);
        ArgumentNullException.ThrowIfNull(source);
        _options = options ?? GridOptions.Default;
        if (_options.Broken() is { } broken) throw new GridRefusedException(broken);
        _open = OpenByClient.GetValue(outlet, static _ => new StrongBox<int>());
        lock (_open)
        {
            if (_open.Value >= _options.MaxSessionsPerClient)
                throw new GridRefusedException(
                    $"This client has {_open.Value} grids open, its most (GridOptions.MaxSessionsPerClient).");
            _open.Value++;
        }

        try
        {
            (_source, _view, _time, _outlet) = (source, view ?? GridView.Natural, time ?? TimeProvider.System, outlet);
            _overscanFill = outlet.FlowControl == FlowControlMode.Adaptive ? _options.OverscanFill.TotalSeconds : 0;
            _liveEvery = Ticks(_options.LiveEvery);
            _heldEvery = Ticks(_options.HeldEvery);
            (_climbEvery, _climbLiveEvery) = _overscanFill > 0
                ? (Ticks(_options.OverscanClimb), Ticks(_options.OverscanClimbLive))
                : (0, 0); // (no overscan told: none to climb)
            _stepFor = Ticks(4 * _options.OverscanFill);
            _stepRows = outlet.OutOfBandThreshold > 0 ? outlet.OutOfBandThreshold / (4 * 2 * RowBytes) : int.MaxValue; // (and the held order's keys beside it)
            var name = $"grid/{Guid.CreateVersion7():N}";
            Rows = new Table<TKey, TRow>($"{name}/rows", LogCapacity(_options));
            Order = new LastValueSource { Label = $"{name}/order" };
            StatusValue = new LastValueSource { Label = $"{name}/status" };
            _window = new GridWindow<TKey, TRow>(Rows, Order, _options.MaxWindowRows, _options.MaxWindowRows,
                _options.RevealNear, _options.RevealAtOnce, StatusValue, _options.MaxOpenRows)
            {
                ByKey = ByKey(source, _view), Overscan = _overscanFill > 0 ? 0 : -1, // (none, till the link's measured)
                MaxBytes = _options.MaxReadBytes
            };
            _openRows = new GridRecentSet<TKey>(_options.MaxOpenRows);
            _page = new GridPage<TKey, TRow>(_window.PageCapacity);
            _tap = outlet.Watch(name, WatchMember.Of(Rows), WatchMember.Of(Order), WatchMember.Of(StatusValue));
            _timer = _time.CreateTimer(static s => ((GridSession<TKey, TRow>)s!)._wake.Set(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _climbTimer = _time.CreateTimer(static s => ((GridSession<TKey, TRow>)s!)._wake.Set(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _restTimer = _time.CreateTimer(static s => ((GridSession<TKey, TRow>)s!)._wake.Set(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _watching = source.Watch(Changed);
            _run = RunAsync();
            _tap.Completion.ContinueWith(static (_, s) => ((GridSession<TKey, TRow>)s!).Ended(), this,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch
        {
            lock (_open) _open.Value--;
            throw;
        }
    }

    /// <summary>
    ///     What the page subscribes to (<c>tapline.subscribe(binding, …, { changed: true })</c>): rows, their order, then the
    ///     status.
    /// </summary>
    public TapBinding Binding => _tap.Binding;

    /// <summary>The window's rows, in view and pre-fetch either side (the watch's first member).</summary>
    internal Table<TKey, TRow> Rows { get; }

    /// <summary>Their order (<see cref="GridOrder" />: the watch's second member).</summary>
    internal LastValueSource Order { get; }

    /// <summary>What its status line shows (<see cref="GridStatusValue" />: the watch's third member), staged with the rows.</summary>
    internal LastValueSource StatusValue { get; }

    internal Tap Tap => _tap;

    /// <summary>The sort and filter shown.</summary>
    public GridView View
    {
        get
        {
            lock (_gate) return _view;
        }
    }

    /// <summary>What the chrome shows, as of the last answer: <see cref="StatusChanged" /> says when it changes.</summary>
    public GridStatus Status
    {
        get
        {
            lock (_gate) return _status;
        }
    }

    /// <summary>Completes once the session has ended: disposed, or its subscription over (the client gone, stalled).</summary>
    public Task Completion => _run;

    /// <summary>
    ///     The most rows either side of the view its page holds (<c>LiveGrid.OverscanCount</c>): the overscan told climbs no
    ///     higher (<see cref="GridOptions.OverscanClimb" />). Unbounded unless said, but by the window's own bound.
    /// </summary>
    public int MostOverscan { get; init; } = int.MaxValue;

    /// <summary>Why the session ended, if not by its owner: its loop's failure, or null.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>Answers applied (for tests: one has once this moves).</summary>
    internal int Answers;

    /// <summary>
    ///     The link as the overscan reads it — its delivery rate (B/s) and whether flow control paces to the path — when a
    ///     test says (null: the outlet's).
    /// </summary>
    internal Func<(long Rate, bool Paced)>? Link { get; init; }

    /// <summary>Whether the page has the watch's state at a version, when a test says (null: the tap's).</summary>
    internal Func<long, bool>? Reached { get; init; }

    /// <summary>
    ///     Nothing asked of the loop and nothing read (for tests): the rest of a window read too, rows opened or closed taken,
    ///     and the last answer applied and reported — not between a read's end and its frames, nor with a toggle waiting
    ///     for the loop, however a read in flight meanwhile ends.
    /// </summary>
    internal bool Settled
    {
        get
        {
            lock (_gate)
                return !_reading && !_turning && !_requested && _locating is null && !_viewChanged && _toggles.Count == 0 &&
                       !_window.NeedsRest;
        }
    }

    /// <summary>The status changed (on the session's loop: hand it to the component's own thread).</summary>
    public event Action? StatusChanged;

    public async ValueTask DisposeAsync()
    {
        Locating? locating;
        CancellationTokenSource? superseded;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            superseded = TakeRead();
            locating = _locating;
            _locating = null;
        }

        Cancel(superseded);
        locating?.Done.TrySetResult(null);
        _wake.Set();
        _watching.Dispose();
        await _timer.DisposeAsync().ConfigureAwait(false);
        await _climbTimer.DisposeAsync().ConfigureAwait(false);
        await _restTimer.DisposeAsync().ConfigureAwait(false);
        await _run.ConfigureAwait(false);
        _tap.Dispose();
        lock (_open) _open.Value--;
    }

    /// <summary>
    ///     The page asks for a window: read next, the latest asked for — once the read in flight is done, or at once if
    ///     this is a jump (its window doesn't touch the one being read: that read is cancelled). Asked in a view since
    ///     replaced (<see cref="GridRequest.View" />), it's dropped, unanswered. Refused (thrown) if it spans more than
    ///     <see cref="GridOptions.MaxWindowRows" /> or a count is negative. A page asks only while in sight, so a request
    ///     says it's shown too (<see cref="SetVisible" />), should the word of that have been lost with a connection.
    ///     <paramref name="anchor" />: the row the page shows at the view's first position, by key, asked from the live
    ///     top — held, the window keeps it there, however many rows arrived since the page drew it.
    /// </summary>
    public void Request(in GridRequest request, TKey? anchor = null)
    {
        if (request.First < 0 || request.Count < 0 || request.Before < 0 || request.After < 0)
            throw new ArgumentOutOfRangeException(nameof(request), request, "A window's position and counts can't be negative.");
        if (request.Rows > _options.MaxWindowRows)
            throw new ArgumentOutOfRangeException(nameof(request), request,
                $"A window of {request.Rows} rows: more than its most, {_options.MaxWindowRows} (GridOptions.MaxWindowRows).");
        CancellationTokenSource? superseded;
        bool shown;
        lock (_gate)
        {
            if (_disposed || request.View != _views) return; // (its positions are another view's)
            shown = !_visible; // (a page asks only in sight: shown, should the word of it have been lost)
            _visible = true;
            (_asks, _askedAt, _askedRows) = (_asks + 1, _time.GetTimestamp(), request.Count); // (the overscan climbs as it rests)
            (_request, _anchor) = (request, anchor);
            _requested = true;
            var (from, to) = Span(request);
            superseded = to < _readFrom || from > _readTo ? TakeRead() : null; // (a jump: what's read is of no use)
        }

        if (shown) _tap.MinInterval = TimeSpan.Zero;
        Cancel(superseded);
        _wake.Set();
    }

    /// <summary>
    ///     A new sort or filter: the window goes back to the top, read at once, the read in flight cancelled, a request
    ///     waiting dropped — or, keeping its place (<paramref name="keepPlace" />), held, to where the row the page shows
    ///     first sorts in the new view, held there (the page goes there with it).
    ///     Its number, which the page's requests carry from now (<see cref="GridRequest.View" />), and the order its rows
    ///     are of (<see cref="GridOrderHeader.View" />).
    /// </summary>
    public int SetView(GridView view, bool keepPlace = false)
    {
        ArgumentNullException.ThrowIfNull(view);
        var byKey = ByKey(_source, view); // (the source's, asked outside the lock)
        CancellationTokenSource? superseded;
        int views;
        lock (_gate)
        {
            if (_disposed) return _views;
            _view = view;
            _byKey = byKey;
            views = ++_views;
            (_viewChanged, _keepPlace) = (true, keepPlace);
            _requested = false; // (in the view replaced: its positions are another sort's)
            superseded = TakeRead();
        }

        Cancel(superseded);
        _wake.Set();
        return views;
    }

    /// <summary>
    ///     The page is shown, or passive (hidden, or its grid out of view). Passive, the source's changes aren't read — its
    ///     status says once the source has moved past the page's rows (<see cref="GridStatusValue.Moved" />) — and the
    ///     subscription waits <see cref="GridOptions.HiddenInterval" /> between records; shown, what changed meanwhile is
    ///     read at once, and a request is answered once read.
    /// </summary>
    public void SetVisible(bool visible)
    {
        lock (_gate)
        {
            if (_disposed || _visible == visible) return;
            _visible = visible;
        }

        _tap.MinInterval = visible ? TimeSpan.Zero : _options.HiddenInterval; // (zero lets the state through at once)
        _wake.Set();
    }

    /// <summary>
    ///     Brings <paramref name="key" />'s row into the window, wherever it is, with <paramref name="before" /> rows above
    ///     it and <paramref name="count" /> from it on, as a place of its own: its view position, or null if the view
    ///     hasn't it (the window as it was) or a newer locate, sort or filter came first.
    /// </summary>
    public Task<long?> LocateAsync(TKey key, int before, int count)
    {
        if (before < 0 || count < 1 || (long)before + count > _options.MaxWindowRows)
            throw new ArgumentOutOfRangeException(nameof(count),
                $"{before} rows above and {count} from it: at least one, and at most {_options.MaxWindowRows} (GridOptions.MaxWindowRows).");
        var locating = new Locating(key, before, count);
        Locating? replaced;
        CancellationTokenSource? superseded;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<long?>(null);
            replaced = _locating;
            _locating = locating;
            superseded = TakeRead();
        }

        Cancel(superseded);
        replaced?.Done.TrySetResult(null);
        _wake.Set();
        return locating.Done.Task;
    }

    /// <summary>
    ///     Opens a row of the view, so that its run — its descendants, depth first
    ///     (<see cref="IGridSource{TKey,TRow}.ReadRunsAsync" />) — is shown after it, or closes it: read at once, the window
    ///     as asked for, the view's rows below it moving down (or up) by as many; held, a row opened or closed above the
    ///     page's first moves the page's scroll too, so its rows stay where they're drawn. A row stays open as the reader
    ///     scrolls away and back, and as the sort or filter changes — but past <see cref="GridOptions.MaxOpenRows" />, one
    ///     opened forgets the row open least recently used (opened, or held by the window), never one the window holds:
    ///     that one is closed with it. Every row open (<see cref="SetAllOpen" />), the rows closed are remembered so
    ///     instead, one forgotten open again. False if the session is over.
    /// </summary>
    public bool SetOpen(TKey key, bool open)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (open != _allOpen) // (apart from the rest: remembered)
            {
                if (!_openRows.Open(key, _forgotten)) return true; // (so already: used now)
                foreach (var gone in _forgotten) _toggles.Add((gone, _allOpen, false));
                _forgotten.Clear();
            }
            else if (!_openRows.Close(key))
            {
                return true;
            }

            _toggles.Add((key, open, false));
        }

        _wake.Set();
        return true;
    }

    /// <summary>Whether a row is open (<see cref="SetOpen" />).</summary>
    public bool IsOpen(TKey key)
    {
        lock (_gate) return _openRows.Contains(key) != _allOpen;
    }

    /// <summary>
    ///     Every row of the view opened — rows arriving too — or closed (<paramref name="open" />), the rows opened or closed
    ///     before as the rest: read at once. Every row open, a row's view position counts the runs above it that reads have
    ///     crossed, a run not read yet none, so the total is at least so many and settles as the reader goes; held, runs
    ///     counted above the page's first row move the page's scroll with them, so its rows stay where they're drawn.
    ///     <see cref="SetOpen" /> closes one, and opens it again. False if the session is over.
    /// </summary>
    public bool SetAllOpen(bool open)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            _allOpen = open;
            _openRows.Clear();
            _toggles.Add((default, open, true));
        }

        _wake.Set();
        return true;
    }

    /// <summary>Every row open but those closed since (<see cref="SetAllOpen" />).</summary>
    public bool AllOpen
    {
        get
        {
            lock (_gate) return _allOpen;
        }
    }

    /// <summary>Held, the key of the view's row the page shows first; none at the live top.</summary>
    public TKey? HeldAt
    {
        get
        {
            lock (_gate) return _window.HeldAt;
        }
    }

    /// <summary>A request's window, in view positions: [from, to).</summary>
    private static (long From, long To) Span(in GridRequest request)
    {
        return (Math.Max(0, request.First - request.Before), request.First + request.Count + request.After);
    }

    /// <summary>
    ///     The read in flight's cancellation, taken (under _gate) to be cancelled: the next read gets one of its own, so a
    ///     cancellation late to arrive never reaches it.
    /// </summary>
    private CancellationTokenSource? TakeRead()
    {
        if (!_reading || _cancel is not { } cancel) return null;
        _cancel = null;
        return cancel;
    }

    /// <summary>
    ///     Cancels a superseded read on the pool: the source's callbacks — and the loop, should it go on from them — never
    ///     run on the thread that superseded it (a component's).
    /// </summary>
    private static void Cancel(CancellationTokenSource? superseded)
    {
        if (superseded is not null)
            ThreadPool.UnsafeQueueUserWorkItem(static c => c.Cancel(), superseded, false);
    }

    /// <summary>Its subscription is over (the client gone, stalled, never activated): so is the session.</summary>
    private void Ended()
    {
        _ = DisposeAsync().AsTask();
    }

    /// <summary>The source moved: its thread's, so a flag and a wake (the loop decides when to read).</summary>
    private void Changed()
    {
        if (Interlocked.Exchange(ref _changed, 1) == 0) _wake.Set();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync().ConfigureAwait(false);
                var moved = false;
                while (true)
                {
                    GridRead<TKey, TRow> read;
                    GridView view;
                    CancellationToken token;
                    Locating? locating = null;
                    var request = default(GridRequest);
                    var answering = false;
                    lock (_gate)
                    {
                        if (_disposed) return;
                        _turning = false; // (the turn before, if any, done: applied and reported)
                        if (Interlocked.Exchange(ref _changed, 0) != 0) _pending = true;
                        var toggled = _toggles.Count > 0; // (rows opened or closed: the window read as asked, at once)
                        foreach (var (key, open, all) in _toggles)
                            if (all) _window.SetAllOpen(open);
                            else if (open) _window.OpenRow(key);
                            else _window.CloseRow(key);
                        _toggles.Clear();
                        var reset = _viewChanged;
                        if (reset)
                        {
                            _viewChanged = false;
                            _window.Reset(_views, _keepPlace ? Math.Max(1, _askedRows) : 0); // (kept: the page's view)
                            _window.ByKey = _byKey;
                            _restAfter = 0;
                        }

                        view = _view;
                        if (_locating is not null)
                        {
                            locating = _locating;
                            _locating = null;
                            read = _window.PlanLocate(locating.Key, locating.Before, locating.Count);
                            (_readFrom, _readTo) = (long.MinValue, long.MaxValue); // (a request doesn't cancel a locate)
                        }
                        else if (_requested)
                        {
                            request = _request;
                            _requested = false;
                            answering = true;
                            read = _window.Plan(request, _anchor, FirstReached());
                            (_readFrom, _readTo) = Span(request);
                        }
                        else if (_window.NeedsRest && !reset && FirstReached())
                        {
                            read = _window.PlanRest(); // (a view's first answer in front of the page: now the rest)
                            (_readFrom, _readTo) = _window.Rest;
                        }
                        else if (!((reset || toggled || (_pending && _visible && IsDue(_time.GetTimestamp(), _readAt,
                                       _window.Live ? _liveEvery : _heldEvery))) && _window.PlanRefresh(out read)))
                        {
                            moved = _pending && !_visible; // (passive: the source moved past the page's rows, unread)
                            Arm();
                            if (_window.NeedsRest) ArmRest(); // (the first answer on its way: looked at again shortly)
                            break;
                        }
                        else
                        {
                            (_readFrom, _readTo) = _window.Asked;
                        }

                        _pending = false; // (whatever it reads, it reads as the source is now)
                        _readAt = _time.GetTimestamp();
                        if (_cancel is null || !_cancel.TryReset()) _cancel = new CancellationTokenSource(); // (taken: a new one)
                        token = _cancel.Token;
                        (_reading, _turning) = (true, true);
                    }

                    _readViews.Add(view);
                    GridAnswer answer;
                    _page.Clear();
                    try
                    {
                        answer = await _source.ReadAsync(view, read with { MaxBytes = _options.MaxReadBytes }, _page, token)
                            .ConfigureAwait(false); // (the host's budget for this user's reads)
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        lock (_gate) _reading = false;
                        locating?.Done.TrySetResult(null);
                        continue; // superseded: what superseded it is next
                    }
                    catch (Exception e)
                    {
                        answer = GridAnswer.Refused($"The source failed: {e.Message}");
                    }

                    bool replaced;
                    lock (_gate)
                    {
                        _reading = false;
                        replaced = _viewChanged || _disposed; // (an answer for a view no longer shown)
                    }

                    if (replaced)
                    {
                        locating?.Done.TrySetResult(null);
                        continue;
                    }

                    if (_overscanFill > 0) _window.Overscan = TellOverscan(false); // (told with the rows)
                    var staged = _window.Apply(answer, _page);
                    if (_window.NeedsRuns) // (the runs the window crosses, read now: published with the view's rows)
                    {
                        _runPage ??= new GridRunPage<TKey, TRow>(2 * _window.PageCapacity);
                        _runPage.Clear();
                        GridAnswer runs;
                        lock (_gate)
                        {
                            if (_cancel?.Token != token || token.IsCancellationRequested) // (superseded meanwhile)
                            {
                                locating?.Done.TrySetResult(null);
                                continue;
                            }

                            _reading = true;
                        }

                        try
                        {
                            runs = await _source.ReadRunsAsync(view, _window.RunReads, _runPage, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            lock (_gate) _reading = false;
                            locating?.Done.TrySetResult(null);
                            continue;
                        }
                        catch (Exception e)
                        {
                            runs = GridAnswer.Refused($"The source failed: {e.Message}");
                        }

                        lock (_gate)
                        {
                            _reading = false;
                            replaced = _viewChanged || _disposed;
                        }

                        if (replaced)
                        {
                            locating?.Done.TrySetResult(null);
                            continue;
                        }

                        staged = _window.ApplyRuns(runs, _runPage);
                    }

                    lock (_gate) // (those apart the window holds used now: never forgotten while it holds them)
                        if (_openRows.Count > 0)
                            _openRows.Shown(_window.ToggledHeld());
                    if (_window.PlannedFirst) (_restAfter, _restPoll) = (staged ? _tap.Source.Version : 0, 0); // (its rest once it's there)
                    else if (!_window.NeedsRest) _restAfter = 0;
                    Report(answer.Version); // (before the page can know it's answered)
                    Interlocked.Increment(ref Answers);
                    if (answering) Rendezvous(request.Id);
                    locating?.Done.TrySetResult(_window.Located);
                    if (_readViews.Count > 1) Leave(view); // (the newest view answered: those before it are done with)
                }

                if (moved) _window.StageMoved(); // (said once, in the status alone; the next answer unsays it)
                if (_climbEvery > 0) // (a step with nothing read — the reader at rest — in the status alone)
                {
                    _window.StageOverscan(TellOverscan(true));
                    if (_climbDue != 0) ArmClimb(_climbDue);
                }
            }
        }
        catch (Exception e)
        {
            Fault = e; // (the session is over; its owner sees why)
            lock (_gate) _disposed = true;
        }
        finally
        {
            try
            {
                Leave(null); // (the grid closed: its views are done with too)
            }
            catch (Exception e)
            {
                Fault ??= e;
            }
        }
    }

    /// <summary>Tells the source of each view read since one was answered, but <paramref name="kept" />: done with.</summary>
    private void Leave(GridView? kept)
    {
        foreach (var view in _readViews)
            if (!ReferenceEquals(view, kept))
                _source.Leave(view);
        _readViews.Clear();
        if (kept is not null) _readViews.Add(kept);
    }

    /// <summary>
    ///     Whether <paramref name="view" />'s order goes without keys at the live top: the source's order is its keys',
    ///     greatest first, and the page can compare them (<see cref="GridKeys{TKey}.Order" />).
    /// </summary>
    private static bool ByKey(IGridSource<TKey, TRow> source, GridView view)
    {
        return GridKeys<TKey>.Order is not null && source.NewestFirstByKey(view);
    }

    /// <summary>
    ///     The overscan to tell the page: the rows either side of the view that <paramref name="deliveryRate" /> (bytes a
    ///     second) carries in <paramref name="fill" /> seconds, both sides, at <paramref name="rowBytes" /> a row — a power
    ///     of two (at most 32,768), moved up once the next fits; down once a quarter fewer than told fit, but only while the
    ///     link is the limit (<paramref name="linkLimited" />: flow control paces to it). A link the application leaves idle
    ///     delivers only what it's given, which says nothing of what it could: a reader at rest keeps the rows held.
    ///     Without a rate (none measured yet), as it was.
    /// </summary>
    internal static int OverscanFor(long deliveryRate, bool linkLimited, double fill, int rowBytes, int was)
    {
        if (deliveryRate <= 0) return was;
        var fits = (long)Math.Min(deliveryRate * fill / (2.0 * rowBytes), 1 << 15);
        var step = fits < 1 ? 0 : 1 << BitOperations.Log2((ulong)fits);
        return step > was || (linkLimited && fits < was * 3L / 4) ? step : was;
    }

    /// <summary>
    ///     The overscan told after a look: up, if it may <paramref name="climb" /> (the reader at rest, the link with room),
    ///     doubled (from one, if none) — but by no more than the link carries in four fills, four times
    ///     <paramref name="fill" /> (one, before it's measured), so a step's rows take no longer than that to arrive, nor
    ///     more than <paramref name="stepMost" /> (rows either side), so its record goes in band — up to
    ///     <paramref name="most" />; halved if the link is the <paramref name="limit" />; never below
    ///     <paramref name="fill" />, what the link carries in <see cref="GridOptions.OverscanFill" />; else as it was.
    /// </summary>
    internal static int OverscanStep(int told, int fill, int most, bool climb, bool limit, int stepMost = int.MaxValue)
    {
        if (limit) return Math.Max(fill, told / 2);
        if (climb && told < most)
            return Math.Max(fill, (int)Math.Min(most,
                told + Math.Min(Math.Min(Math.Max(told, 1), 4L * Math.Max(fill, 1)), Math.Max(1, stepMost))));
        return Math.Max(fill, told);
    }

    /// <summary>
    ///     The overscan to tell the page: at least what the link carries in <see cref="GridOptions.OverscanFill" />
    ///     (<see cref="OverscanFor" />), and as it has climbed — looked at once each <see cref="GridOptions.OverscanClimb" />,
    ///     a step at a look (<see cref="OverscanStep" />). Up, once the page has asked with the last step (so a step's
    ///     word goes behind the last step's rows, and the steps go as fast as the link delivers them), the reader has
    ///     rested a climb's length since (<see cref="GridOptions.OverscanClimbLive" /> at the live top) and the watch's
    ///     state isn't waiting to go (<see cref="Tap.Waiting" />), to the page's most (<see cref="MostOverscan" />) and the
    ///     window's bound. Down, halved — again once the page has asked with that — while its state has been waiting longer
    ///     than a step's rows take to arrive (four fills): its changes come faster than the link carries them. Back at the
    ///     live top, where every row held is in every update, what was climbed held goes. Its next look while it may climb:
    ///     <see cref="_climbDue" />. In the status <paramref name="alone" /> (nothing read), nothing new while the page is
    ///     passive or hasn't asked: it hears with the rows.
    /// </summary>
    private int TellOverscan(bool alone)
    {
        var now = _time.GetTimestamp();
        var (rate, paced) = Link?.Invoke() ?? (_outlet.DeliveryRate, _outlet.PacingRate > 0);
        _fillOverscan = OverscanFor(rate, paced, _overscanFill, RowBytes, _fillOverscan);
        _climbDue = 0;
        if (_climbEvery <= 0) return _fillOverscan; // (no climbing: the fill's alone)
        var live = _window.Live;
        if (live && !_wasLive) _climbedOverscan = 0; // (back at the live top: climbs afresh, at its own pace)
        _wasLive = live;
        bool visible;
        long asks, askedAt;
        int askedRows;
        lock (_gate) (visible, asks, askedAt, askedRows) = (_visible, _asks, _askedAt, _askedRows);
        var told = Math.Max(_fillOverscan, _climbedOverscan);
        if (!visible || asks == 0) return alone ? _window.Overscan : told; // (passive, or nothing asked: no window to fill, nothing said)
        var most = (int)Math.Min(MostOverscan, Math.Max(0, (_options.MaxWindowRows - (long)askedRows) / 2));
        if (now - _overscanLookedAt >= _climbEvery)
        {
            _overscanLookedAt = now;
            var waiting = _tap.Waiting;
            if (!waiting) _waitingSince = 0;
            else if (_waitingSince == 0) _waitingSince = now;
            var every = live ? _climbLiveEvery : _climbEvery;
            var taken = asks > _asksAtStep; // (the page has asked with the last step: a step's word reaches it first)
            var climb = taken && !waiting && every > 0 && now - askedAt >= every;
            var stepped = OverscanStep(told, _fillOverscan, most, climb, taken && waiting && now - _waitingSince > _stepFor,
                _stepRows);
            if (stepped != told) (_climbedOverscan, _asksAtStep, told) = (stepped, asks, stepped);
        }

        if (told < most) _climbDue = _overscanLookedAt + _climbEvery;
        return told;
    }

    /// <summary>
    ///     Whether the page has the window's first answer last staged (a view read first, a hold's): till then the window
    ///     doesn't widen, so on a slow link the first goes out alone, not merged into its rest, which waits behind it.
    /// </summary>
    private bool FirstReached()
    {
        return _restAfter == 0 || (Reached?.Invoke(_restAfter) ?? _tap.HasReached(_restAfter));
    }

    /// <summary>
    ///     The loop looks again shortly whether the page has the first answer: 2 ms, then twice as long each time, up to
    ///     50 ms (a fast link's at once, a slow link's seconds looked at now and then).
    /// </summary>
    private void ArmRest()
    {
        var ms = Math.Min(50, _restPoll == 0 ? 2 : 2 * _restPoll);
        _restPoll = ms;
        _restTimer.Change(TimeSpan.FromMilliseconds(ms), Timeout.InfiniteTimeSpan);
    }

    /// <summary>The overscan's next look, while it may climb: the clock wakes the loop then (nothing else does, the reader at rest).</summary>
    private void ArmClimb(long due)
    {
        if (due == _climbArmedFor) return;
        _climbArmedFor = due;
        _climbTimer.Change(Until(due), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    ///     The rows table's change log: twice the widest window — a client further behind than it takes the window whole,
    ///     which is small anyway — not Tapline's default (65,536 entries, 256 KB).
    /// </summary>
    internal static int LogCapacity(GridOptions options)
    {
        return (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, 2 * (options.MaxWindowRows + options.RevealAtOnce)));
    }

    /// <summary>Whether a read for the source's changes may begin: <paramref name="every" /> since the last began.</summary>
    internal static bool IsDue(long now, long readAt, long every)
    {
        return now - readAt >= every;
    }

    /// <summary>A change waiting, and not yet due: the clock wakes the loop when it is (under _gate).</summary>
    private void Arm()
    {
        if (!_pending || !_visible) return;
        var due = _readAt + (_window.Live ? _liveEvery : _heldEvery);
        if (due == _armedFor) return;
        _armedFor = due;
        _timer.Change(Until(due), Timeout.InfiniteTimeSpan);
    }

    /// <summary>The time from now till the clock reads <paramref name="due" /> (none, if it has).</summary>
    private TimeSpan Until(long due)
    {
        var ticks = due - _time.GetTimestamp();
        return ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)Math.Ceiling(ticks * (double)TimeSpan.TicksPerSecond / _time.TimestampFrequency));
    }

    /// <summary>The request's answer is in front of the page once it has the watch's state as of now.</summary>
    private void Rendezvous(Guid id)
    {
        if (id == Guid.Empty) return;
        id.TryWriteBytes(_correlation, true, out _); // (as tapline.correlation() orders its bytes)
        _tap.TryRendezvous(_correlation, _tap.Source.Version);
    }

    private void Report(long version)
    {
        var status = new GridStatus
        {
            Live = _window.Live, Holds = _window.Holds, Total = _window.Total, TotalKind = _window.TotalKind,
            Above = _window.Unseen, LetIn = _window.LetIn, Version = version, Refusal = _window.Refusal
        };
        lock (_gate)
        {
            if (status == _status) return;
            _status = status;
        }

        StatusChanged?.Invoke();
    }

    private long Ticks(TimeSpan span)
    {
        return (long)Math.Ceiling(span.Ticks * (double)_time.TimestampFrequency / TimeSpan.TicksPerSecond);
    }

    private sealed class Locating(TKey key, int before, int count)
    {
        public readonly int Before = before, Count = count;
        public readonly TaskCompletionSource<long?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TKey Key = key;
    }
}
