using System.Numerics;
using System.Runtime.CompilerServices;

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

    private readonly byte[] _correlation = new byte[16];
    private readonly Lock _gate = new();
    private readonly long _heldEvery, _liveEvery; // in the clock's ticks
    private readonly StrongBox<int> _open; // the client's grids
    private readonly GridOptions _options;
    private readonly GridPage<TKey, TRow> _page;
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
    private bool _disposed, _reading, _requested, _viewChanged, _visible = true;
    private Locating? _locating;
    private long _readFrom, _readTo; // the window being read, in view positions: a request not touching it is a jump
    private GridRequest _request;
    private GridStatus _status = new() { Live = true };
    private GridView _view;

    // The loop's own.
    private long _armedFor = long.MinValue;
    private bool _pending; // a change not yet read
    private long _readAt; // when the last read began

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
            (_source, _view, _time) = (source, view ?? GridView.Natural, time ?? TimeProvider.System);
            _liveEvery = Ticks(_options.LiveEvery);
            _heldEvery = Ticks(_options.HeldEvery);
            var name = $"grid/{Guid.CreateVersion7():N}";
            Rows = new Table<TKey, TRow>($"{name}/rows", LogCapacity(_options));
            Order = new LastValueSource { Label = $"{name}/order" };
            _window = new GridWindow<TKey, TRow>(Rows, Order, _options.MaxWindowRows, _options.MaxWindowRows,
                _options.RevealNear, _options.RevealAtOnce);
            _page = new GridPage<TKey, TRow>(_window.PageCapacity);
            _tap = outlet.Watch(name, WatchMember.Of(Rows), WatchMember.Of(Order));
            _timer = _time.CreateTimer(static s => ((GridSession<TKey, TRow>)s!)._wake.Set(), this,
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

    /// <summary>What the page subscribes to (<c>tapline.subscribe(binding, …, { changed: true })</c>): rows, then order.</summary>
    public TapBinding Binding => _tap.Binding;

    /// <summary>The window's rows, in view and pre-fetch either side (the watch's first member).</summary>
    internal Table<TKey, TRow> Rows { get; }

    /// <summary>Their order (<see cref="GridOrder" />: the watch's second member).</summary>
    internal LastValueSource Order { get; }

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

    /// <summary>Why the session ended, if not by its owner: its loop's failure, or null.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>Answers applied (for tests: one has once this moves).</summary>
    internal int Answers;

    /// <summary>Nothing asked of the loop and nothing read (for tests): a jump's rest read too.</summary>
    internal bool Settled
    {
        get
        {
            lock (_gate) return !_reading && !_requested && _locating is null && !_viewChanged && !_window.NeedsRest;
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
        await _run.ConfigureAwait(false);
        _tap.Dispose();
        lock (_open) _open.Value--;
    }

    /// <summary>
    ///     The page asks for a window: read next, the latest asked for — once the read in flight is done, or at once if
    ///     this is a jump (its window doesn't touch the one being read: that read is cancelled). Refused (thrown) if it
    ///     spans more than <see cref="GridOptions.MaxWindowRows" /> or a count is negative.
    /// </summary>
    public void Request(in GridRequest request)
    {
        if (request.First < 0 || request.Count < 0 || request.Before < 0 || request.After < 0)
            throw new ArgumentOutOfRangeException(nameof(request), request, "A window's position and counts can't be negative.");
        if (request.Rows > _options.MaxWindowRows)
            throw new ArgumentOutOfRangeException(nameof(request), request,
                $"A window of {request.Rows} rows: more than its most, {_options.MaxWindowRows} (GridOptions.MaxWindowRows).");
        CancellationTokenSource? superseded;
        lock (_gate)
        {
            if (_disposed) return;
            _request = request;
            _requested = true;
            var (from, to) = Span(request);
            superseded = to < _readFrom || from > _readTo ? TakeRead() : null; // (a jump: what's read is of no use)
        }

        Cancel(superseded);
        _wake.Set();
    }

    /// <summary>A new sort or filter: the window goes back to the top, read at once, the read in flight cancelled.</summary>
    public void SetView(GridView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        CancellationTokenSource? superseded;
        lock (_gate)
        {
            if (_disposed) return;
            _view = view;
            _viewChanged = true;
            superseded = TakeRead();
        }

        Cancel(superseded);
        _wake.Set();
    }

    /// <summary>
    ///     The page is shown, or hidden. Hidden, the source's changes aren't read, and the subscription waits
    ///     <see cref="GridOptions.HiddenInterval" /> between records; shown, what changed meanwhile is read at once.
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
                        if (Interlocked.Exchange(ref _changed, 0) != 0) _pending = true;
                        var reset = _viewChanged;
                        if (reset)
                        {
                            _viewChanged = false;
                            _window.Reset();
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
                            read = _window.Plan(request);
                            (_readFrom, _readTo) = Span(request);
                        }
                        else if (_window.NeedsRest && !reset)
                        {
                            read = _window.PlanRest(); // (a jump's view went out with its rendezvous: now the rest)
                            (_readFrom, _readTo) = _window.Rest;
                        }
                        else if (!((reset || (_pending && _visible && IsDue(_time.GetTimestamp(), _readAt,
                                       _window.Live ? _liveEvery : _heldEvery))) && _window.PlanRefresh(out read)))
                        {
                            Arm();
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
                        _reading = true;
                    }

                    GridAnswer answer;
                    _page.Clear();
                    try
                    {
                        answer = await _source.ReadAsync(view, read, _page, token).ConfigureAwait(false);
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

                    _window.Apply(answer, _page);
                    Report(answer.Version); // (before the page can know it's answered)
                    Interlocked.Increment(ref Answers);
                    if (answering) Rendezvous(request.Id);
                    locating?.Done.TrySetResult(_window.Located);
                }
            }
        }
        catch (Exception e)
        {
            Fault = e; // (the session is over; its owner sees why)
            lock (_gate) _disposed = true;
        }
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
        var ticks = due - _time.GetTimestamp();
        _timer.Change(ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)Math.Ceiling(ticks * (double)TimeSpan.TicksPerSecond / _time.TimestampFrequency)),
            Timeout.InfiniteTimeSpan);
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
            Above = _window.Above, LetIn = _window.LetIn, Version = version, Refusal = _window.Refusal
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
