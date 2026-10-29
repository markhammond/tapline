using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tapline.Grid.Provider;

/// <summary>
///     A grid's source bolted onto a database and its stream: the store's pages laid over with what the stream sent that
///     the store hasn't reached, each key once, at the newest version either side knows (grid-sources.md §3).
/// </summary>
/// <remarks>
///     <para>
///         Two tails. The state tail holds one row per key the stream sent that the store hasn't reached: its newest
///         version, beside the store's row for it as last seen (a read's, the gap fill's, a recheck's), so each view's
///         filter (<see cref="IGridScopes{TKey,TRow}" />) tests both — top-level rows and run rows alike, each a key of its
///         own. A view holds a key while its newest version passes, or the store's row of it last did, shown at its newest,
///         as the blotter's does: the store's rows laid over with the stream's, a row leaving once the store, too, has a
///         version that doesn't pass. The event log holds every version the
///         stream sent, as received, for views that need history rather than state (an alert over a window): back the
///         longest any of them asks for (<see cref="KeepHistory" />), up to the policy's, and none without one — a grid's
///         pages read the state tail alone, so a source with no such view keeps one tail.
///     </para>
///     <para>
///         The state tail changes in one place, the catch-up, as the blotter's does: what the stream sent, the gap fill's
///         versions and the store's rows the reads saw, taken a turn at a time, evaluated by every view's filter, then
///         published with the tail. A read waits for what was sent before it began, so it sees the tail and the filters'
///         answers as of one moment, never a version older than one sent before it. Filters that answer at once
///         complete the catch-up on the caller's thread.
///     </para>
///     <para>
///         The state tail forgets by the store's watermark: the store is an append-only log, so its newest stamp says how
///         far it has caught up, and a row stamped a haircut or more before it is in the store — once every read under way
///         began with the store past it, so no read finds the store without a version the tail has let go. The watermark
///         comes from reads made anyway (the stamps of each page read, and the gap fill's), and from rechecks: while the
///         tail holds rows, reads have the store's newest stamp asked (<see cref="IGridStore{TKey,TRow,TStamp}.NewestStampAsync" />)
///         and every version stamped from a haircut before it read again (<see cref="IGridStore{TKey,TRow,TStamp}.SinceAsync" />),
///         at most once a <see cref="AppendOnlyStorePolicy.Recheck" /> — each tail key's store row then as the store holds
///         it. Nothing is looked up on a clock, nor key by key.
///     </para>
///     <para>
///         It subscribes as it's made, before any read; a haircut later the gap fill takes the versions stamped from
///         <see cref="AppendOnlyStorePolicy.GapFill" /> before the subscription, as if streamed (its moment as a stamp,
///         <see cref="IGridRows{TKey,TRow,TStamp}.StampAt" />: the only time the merge takes from the host's clock): a version written as the
///         grid subscribed, which the stream never sends, changes the grids' rows like any other. Past
///         <see cref="AppendOnlyStorePolicy.MaxTailRows" />, the oldest rows are let go before the store is seen to have
///         them — the likeliest in it already — and reads go on, those keys shown as the store has them; once it has
///         passed them by a haircut, the grids are told to read again.
///         Ranks are the store's (<see cref="IGridStore{TKey,TRow,TStamp}.RankAsync" />, <see cref="IGridStore{TKey,TRow,TStamp}.SizeAsync" />)
///         moved by the tail's rows above.
///     </para>
///     <para>
///         What a read brings from the store is kept, by default (<see cref="AppendOnlyStorePolicy.Windows" />): a window of
///         the view — every row of it from the first the read found to the last, the store's merged with the tail's — changed
///         by every change published after, as the catch-up publishes it, so the reads it covers ask the store nothing; the
///         store is read again for rows past it, or once a <see cref="AppendOnlyStorePolicy.Refresh" /> while it's read, off
///         the read's path, unless the store says it's settled (<see cref="IGridStore{TKey,TRow,TStamp}.SettledAsync" />). Its place and the
///         view's total move with each change: by a guess where the key's row before is known to neither the tail nor the
///         window — taken as arriving — and then shown as estimates, a refresh asking the store again.
///     </para>
/// </remarks>
public sealed class StoreAndStream<TKey, TRow, TStamp> : IGridSource<TKey, TRow>, IDisposable
    where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TStamp : struct, IComparable<TStamp>
{
    private const int LeastLimit = 64; // a store read's limit at least: rounded up to a power of two, prepared statements few
    private const int MostLimit = 1 << 14; // and at most, read again longer when the tail's overrides left a page short
    private const int Moves = 3; // the most times a read from a rank moves the store's rank it reads from
    private const string Closed = "The source has closed."; // (a read of it once disposed: refused, its rows kept, marked stale)
    private static readonly TimeSpan KeepIdle = TimeSpan.FromSeconds(10); // a kept window no read uses this long goes

    private readonly Lock _gate = new();
    private readonly AppendOnlyStorePolicy _policy;
    private readonly IGridStore<TKey, TRow, TStamp> _store;
    private readonly IGridRows<TKey, TRow, TStamp> _rows;
    private readonly IGridScopes<TKey, TRow> _scopes;
    private readonly TimeProvider _time;
    private readonly Dictionary<TKey, Entry> _tail; // the state tail, as published
    private readonly PriorityQueue<TKey, TStamp> _byStamp; // its keys by stamp (a key changed since: there twice)
    private readonly Dictionary<GridView, View> _views = new(ReferenceEqualityComparer.Instance);
    private readonly List<TStamp?> _reading = []; // the watermark each read under way began with
    private readonly ConcurrentBag<GridPage<TKey, TRow>> _pages = []; // store reads' pages, kept
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _gapFilled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IDisposable _subscription;
    private readonly ITimer _gapFill;
    private readonly Asker _asker; // (queued to the thread pool to ask the store its newest stamp: one, reused)
    private readonly List<long> _keeps = []; // the reach each view keeping history asked for (timestamp ticks)
    private long _keep; // the longest of them: the event log's reach (0: none kept)
    private (long Received, TRow Row)[] _log = []; // the event log: a ring, while a view keeps history
    private int _logStart, _logCount;
    private Action[] _watching = [];
    private long _version, _tailVersion;
    private TStamp? _watermark, _newest; // the store's newest stamp, as seen; the stream's
    private TStamp? _pruned; // the newest stamp let go past MaxTailRows before the store was seen to have it (none: null)
    private bool _retell; // (the store past what was pruned: the grids told to read again at the next turn)
    private readonly TStamp _subscribed; // the subscription's moment, as a stamp
    private long _askedAt, _checkedAt; // (timestamp ticks) when the store was last asked its newest stamp; its recent versions read again
    private bool _rechecking; // (the ask queued is a recheck)
    private bool _askReady; // (an ask decided under the lock, the cheap one: started by the caller, the lock let go)
    internal int Rechecks; // (for tests: rechecks done)
    private bool _asking, _disposed;

    // The catch-up: what the views' filters have yet to evaluate, taken a turn at a time (the pending and the taken
    // swapped), evaluated, then published with the tail. Each change numbered (_received); the reads wait on the number
    // published (_published).
    private List<(TRow Row, bool Stored)> _pendingRows = [], _takenRows = [];
    private Dictionary<TKey, TRow> _pendingStored, _takenStored; // the store's rows the reads saw, the newest of each
    private Dictionary<TKey, GridTailRow<TKey, TRow>> _pendingGone, _takenGone; // let go, as they were
    private List<Opening> _pendingOpens = [], _takenOpens = [];
    private List<IGridScope<TKey, TRow>> _pendingCloses = [], _takenCloses = [];
    private readonly Dictionary<GridView, Opening> _opening = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TKey, Entry> _staged;
    private readonly List<(long Target, TaskCompletionSource Done)> _waiters = [];
    private GridTailRow<TKey, TRow>[] _changed, _goneRows;
    private GridTailRow<TKey, TRow>[] _snapshot = [];
    private long _received, _published;
    private bool _catchingUp;


    // Under _gate, a read's: the store's rows its statements brought, by key, the newest of each.
    private readonly Dictionary<TKey, TRow> _readRows = [];

    // The kept windows, every view's: how many, and the rows they hold; the keys a turn changes, each one's tail entry before
    // the turn (and whether the tail had it), every windowed view's answer for it then beside (View.WasIn).
    private int _windowCount;
    private long _windowRows, _expiredAt;
    private TKey[] _changing;
    private Entry[] _changingWas;
    private bool[] _changingHad;

    /// <summary>
    ///     Composes a source of <paramref name="store" /> and <paramref name="stream" />, subscribing at once, before any
    ///     read. Refused (<see cref="GridRefusedException" />) if the store's policy can't be kept to.
    /// </summary>
    public StoreAndStream(IGridStore<TKey, TRow, TStamp> store, IGridStream<TRow> stream, IGridRows<TKey, TRow, TStamp> rows,
        IGridScopes<TKey, TRow> scopes, TimeProvider? time = null)
    {
        (_store, _rows, _scopes, _time) = (store, rows, scopes, time ?? TimeProvider.System);
        _policy = store.Policy;
        if (_policy.Broken() is { } broken) throw new GridRefusedException(broken);
        // (sized for a tail of up to a thousand rows, a turn's changes and rows let go as many: grown past, they stay grown
        // — a warm source allocates nothing however its store's watermark is seen, by a page read or by asking)
        var size = Math.Min(_policy.MaxTailRows, 1_024);
        (_tail, _staged, _byStamp) = (new Dictionary<TKey, Entry>(size), new Dictionary<TKey, Entry>(size), new PriorityQueue<TKey, TStamp>(size));
        (_pendingGone, _takenGone) = (new Dictionary<TKey, GridTailRow<TKey, TRow>>(size), new Dictionary<TKey, GridTailRow<TKey, TRow>>(size));
        (_pendingStored, _takenStored) = (new Dictionary<TKey, TRow>(size), new Dictionary<TKey, TRow>(size));
        (_changed, _goneRows) = (new GridTailRow<TKey, TRow>[size], new GridTailRow<TKey, TRow>[size]);
        (_changing, _changingWas, _changingHad) = (new TKey[size], new Entry[size], new bool[size]);
        _asker = new Asker(this);
        _subscribed = rows.StampAt(_time.GetUtcNow());
        _subscription = stream.Subscribe(Streamed);
        _gapFill = _time.CreateTimer(static s => _ = ((StoreAndStream<TKey, TRow, TStamp>)s!).GapFillAsync(), this, _policy.Haircut,
            Timeout.InfiniteTimeSpan);
    }

    public long Version => Volatile.Read(ref _version);

    /// <summary>Done once the gap fill has been taken (or failed: <see cref="GapFillFault" />).</summary>
    public Task GapFilled => _gapFilled.Task;

    /// <summary>Why the gap fill failed, or null.</summary>
    public Exception? GapFillFault { get; private set; }

    /// <summary>
    ///     Why the store's last ask failed — its newest stamp, or a recheck's recent versions — or null once one hasn't: the
    ///     tail keeps what it holds meanwhile, and asks again at the next.
    /// </summary>
    public Exception? RecheckFault { get; private set; }

    /// <summary>Why a kept window's last refresh failed, or null once one hasn't: it stays as it was, read again at the next.</summary>
    public Exception? RefreshFault { get; private set; }

    /// <summary>
    ///     What it keeps for its grids, in bytes, beside the most its policy lets it keep: the state tail (each key's entry,
    ///     keyed and queued by stamp), the event log (none while no view keeps history) and the kept windows (each row,
    ///     ordered and keyed). From the rows held and their sizes; its pre-sized arrays and the views' sorted tails, which
    ///     follow the tail's size, aren't counted apart.
    /// </summary>
    public (GridMemory Tail, GridMemory History, GridMemory Windows) Memory
    {
        get
        {
            long key = Unsafe.SizeOf<TKey>(), row = Unsafe.SizeOf<TRow>();
            var entry = 2 * key + Unsafe.SizeOf<Entry>() + Unsafe.SizeOf<TStamp>() + 8;
            var logged = 8 + row;
            var windowed = WindowRowBytes;
            lock (_gate)
                return (new GridMemory(_tail.Count * entry, _policy.MaxTailRows * entry),
                    new GridMemory(_logCount * logged, _keep > 0 ? _policy.MaxHistoryRows * logged : 0),
                    new GridMemory(_windowRows * windowed, _policy.Windows == StoreWindows.Kept ? _policy.MaxWindowRows * windowed : 0));
        }
    }

    /// <summary>
    ///     A kept window's row, as its memory is counted: its key and row twice (the window's arrays, its map of them), and
    ///     the map's own. A share of memory for a view's windows is so many rows (<see cref="AppendOnlyStorePolicy.MaxWindowRowsPerView" />).
    /// </summary>
    public static long WindowRowBytes => 2 * (Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TRow>()) + 8;

    /// <summary>
    ///     What one view keeps — a session's, as each reads a view of its own: its kept windows, beside the most the policy
    ///     lets a view keep (<see cref="AppendOnlyStorePolicy.MaxWindowRowsPerView" />), and its filter's (the scope's own
    ///     count). None for a view not read, or let go.
    /// </summary>
    public (GridMemory Windows, GridMemory Filter) MemoryOf(GridView view)
    {
        var windowed = WindowRowBytes;
        GridMemory windows;
        IGridScope<TKey, TRow> scope;
        lock (_gate)
        {
            if (!_views.TryGetValue(view, out var v)) return default;
            var bound = _policy.Windows == StoreWindows.Kept ? Math.Min(_policy.MaxWindowRowsPerView, _policy.MaxWindowRows) * windowed : 0;
            (windows, scope) = (new GridMemory(v.WindowRows * windowed, bound), v.Scope);
        }

        return (windows, scope.Memory); // (the scope's count its own: not under the source's lock)
    }

    /// <summary>The rows the kept windows hold, counted window by window, and as the counts have it, every view's and in all (for tests).</summary>
    internal (long Counted, long Held, bool ViewsAgree) WindowCounts
    {
        get
        {
            lock (_gate)
            {
                long counted = 0;
                var agree = true;
                foreach (var v in _views.Values)
                {
                    long rows = 0;
                    foreach (var w in v.Windows) rows += w.Count;
                    (counted, agree) = (counted + rows, agree && rows == v.WindowRows);
                }

                return (counted, _windowRows, agree);
            }
        }
    }

    /// <summary>The kept windows, every view's, and the rows they hold.</summary>
    public (int Windows, long Rows) Kept
    {
        get
        {
            lock (_gate) return (_windowCount, _windowRows);
        }
    }

    /// <summary>The refreshes done, those the store said settled, and the reads a kept window answered (for tests).</summary>
    internal int Refreshes, Settles, WindowReads;

    /// <summary>The state tail's rows, as published: the stream's versions the store hasn't been seen to reach.</summary>
    public int TailRows
    {
        get
        {
            lock (_gate) return _tail.Count;
        }
    }

    /// <summary>The event log's versions (none while no view keeps history).</summary>
    public int HistoryRows
    {
        get
        {
            lock (_gate) return _logCount;
        }
    }

    /// <summary>The store's watermark: the newest stamp it has been seen to hold (none yet: null).</summary>
    public TStamp? Watermark
    {
        get
        {
            lock (_gate) return _watermark;
        }
    }

    /// <summary>How far the store is behind the stream: its watermark against the stream's newest stamp (null: not seen yet).</summary>
    public TimeSpan? Lag
    {
        get
        {
            lock (_gate) return LagUnlocked();
        }
    }

    public IDisposable Watch(Action changed)
    {
        lock (_gate) _watching = [.. _watching, changed];
        return new Unwatch(this, changed);
    }

    public bool NewestFirstByKey(GridView view)
    {
        return _rows.NewestFirstByKey(view);
    }

    public void Leave(GridView view)
    {
        bool start;
        lock (_gate)
        {
            if (!_views.Remove(view, out var v)) return;
            Unhold(v);
            Close(v.Scope);
            start = Starting();
        }

        if (start) _ = CatchUpAsync();
    }

    /// <summary>
    ///     Keeps the event log back <paramref name="back" /> from the newest version's reception while the handle lives, for a
    ///     view that needs history rather than state (an alert over a window), from the versions the stream sends next.
    ///     Refused (<see cref="GridRefusedException" />) past the store's policy (<see cref="AppendOnlyStorePolicy.History" />,
    ///     <see cref="AppendOnlyStorePolicy.MaxHistoryRows" />).
    /// </summary>
    public IDisposable KeepHistory(TimeSpan back)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(back, TimeSpan.Zero);
        if (back > _policy.History || _policy.MaxHistoryRows == 0)
            throw new GridRefusedException($"A view needs {back.TotalSeconds:N0} s of history, past the {_policy.History.TotalSeconds:N0} s " +
                                           "the store's policy keeps (AppendOnlyStorePolicy.History, MaxHistoryRows).");
        var reach = Ticks(back);
        lock (_gate)
        {
            _keeps.Add(reach);
            _keep = Math.Max(_keep, reach);
        }

        return new Unkeep(this, reach);
    }

    /// <summary>The event log's versions received within <paramref name="back" /> of the newest, oldest first, into <paramref name="into" />: how many.</summary>
    public int CopyHistory(TimeSpan back, List<TRow> into)
    {
        lock (_gate)
        {
            if (_logCount == 0) return 0;
            var newest = _log[(_logStart + _logCount - 1) % _log.Length].Received;
            var since = newest - Ticks(back);
            var n = 0;
            for (var i = 0; i < _logCount; i++)
            {
                ref readonly var at = ref _log[(_logStart + i) % _log.Length];
                if (at.Received < since) continue;
                into.Add(at.Row);
                n++;
            }

            return n;
        }
    }

    public async ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<TKey, TRow> read, GridPage<TKey, TRow> page,
        CancellationToken cancellationToken)
    {
        View? v = null;
        Task<View>? opening = null;
        string? refusal = null;
        long target = 0;
        TStamp? began = null;
        bool start;
        lock (_gate)
        {
            if (_disposed)
            {
                refusal = Closed;
            }
            else if (_views.TryGetValue(view, out v) && v.Scope.Fault is { } fault)
            {
                _views.Remove(view);
                Unhold(v);
                Close(v.Scope); // (the next read opens it again)
                refusal = $"The view's filter failed: {fault.Message}";
            }
            else
            {
                if (v is null) opening = Open(view);
                target = _received; // (what was sent before it began: published before it reads)
                began = _watermark;
                _reading.Add(began); // (what the tail lets go meanwhile, this read's store reads hold)
            }

            start = Starting();
        }

        if (start) _ = CatchUpAsync();
        if (refusal is not null) return GridAnswer.Refused(refusal);
        GridPage<TKey, TRow> ahead = Rent(), back = Rent();
        try
        {
            if (opening is not null)
                try
                {
                    v = await opening.ConfigureAwait(false);
                }
                catch (Exception refused) when (refused is not OperationCanceledException and not ObjectDisposedException)
                {
                    return GridAnswer.Refused(refused.Message);
                }

            await Published(target).ConfigureAwait(false);
            if (_policy.Windows == StoreWindows.Kept)
                lock (_gate)
                    if (FromWindow(v!, read, page) is { } kept)
                        return kept;
            return read.Kind == GridReadKind.AtRank
                ? await AtRankAsync(v!, read, page, ahead, back, cancellationToken).ConfigureAwait(false)
                : await AboutAsync(v!, read, page, ahead, back, cancellationToken).ConfigureAwait(false);
        }
        catch (GridStoreRefusedException refused) // (past the store's time limit, or the read's memory budget)
        {
            page.Clear();
            return GridAnswer.Refused(refused.Message);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed)) // (closed under the read: as a read of a closed source)
        {
            page.Clear();
            return GridAnswer.Refused(Closed);
        }
        finally
        {
            Return(ahead);
            Return(back);
            bool ask;
            lock (_gate)
            {
                _reading.Remove(began);
                Forget();
                Recheck(); // (a read's: the store's recent versions read again, at most once a Recheck)
                Ask();
                start = Starting();
                ask = AskReady();
            }

            if (start) _ = CatchUpAsync();
            if (ask) _ = AskAsync();
        }
    }

    public void Dispose()
    {
        bool start;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var v in _views.Values)
            {
                Unhold(v);
                Close(v.Scope);
            }

            _views.Clear();
            var disposed = new ObjectDisposedException(GetType().Name);
            foreach (var opening in _pendingOpens) opening.Done.TrySetException(disposed);
            _pendingOpens.Clear();
            foreach (var (_, done) in _waiters) done.TrySetException(disposed);
            _waiters.Clear();
            start = Starting(); // (the scopes disposed by the catch-up, between evaluations)
        }

        _stop.Cancel();
        _subscription.Dispose();
        _gapFill.Dispose();
        _gapFilled.TrySetResult();
        if (start) _ = CatchUpAsync();
    }

    /// <summary>
    ///     From a rank: from the view's first row (the store's, keyset), or a far jump (the store's OFFSET). The tail's rows
    ///     above move the store's rows, so the store is read again from where the read's rank lands, a few times at most.
    /// </summary>
    private async ValueTask<GridAnswer> AtRankAsync(View v, GridRead<TKey, TRow> read, GridPage<TKey, TRow> page,
        GridPage<TKey, TRow> ahead, GridPage<TKey, TRow> merged, CancellationToken cancellationToken)
    {
        var at = read.Rank; // (the store's rank read from)
        var limit = Limit(read.Count);
        for (var moves = 0;;)
        {
            ahead.Clear();
            if (at == 0)
                await _store.ReadAsync(v.Grid, GridSeek<TKey, TRow>.First, limit, read.MaxBytes, ahead, cancellationToken).ConfigureAwait(false);
            else await _store.ReadAtAsync(v.Grid, at, limit, read.MaxBytes, ahead, cancellationToken).ConfigureAwait(false);
            // The store's size: a page short of its limit ends the view in it, counted (nothing asked); else the store's
            // word, an estimate enough, or none.
            var sized = ahead.Count < limit && (ahead.Count > 0 || at == 0)
                ? (at + ahead.Count, TotalKind.Exact)
                : await _store.SizeAsync(v.Grid, read.MaxBytes, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _readRows.Clear();
                Saw(ahead);
                if (ahead.Count == 0 && at > 0 && sized is { Rows: var size and > 0 } && at >= size && moves++ < Moves)
                {
                    at = size - 1; // (past the store's last row: read from it, for the tail's rows after it)
                    continue;
                }

                var full = ahead.Count == limit;
                merged.Clear();
                if (at == 0) Forward(v, GridSeek<TKey, TRow>.First, ahead, full, limit, merged);
                else if (ahead.Count > 0) Forward(v, GridSeek<TKey, TRow>.From(ahead.Keys[0], ahead.Rows[0]), ahead, full, limit, merged);
                var first = at == 0 || merged.Count == 0 ? at : at + Before(v, ahead, merged.Rows[0]) + Above(v, merged.Rows[0]);
                var skip = read.Rank - first; // (the merge's rows before the rank read)
                var more = full || merged.Count == limit; // (rows past the merge's)
                if (merged.Count > 0 && skip != 0 && at > 0 && moves < Moves && (skip < 0 || (more && merged.Count - skip < read.Count)))
                {
                    moves++;
                    at = Math.Max(0, at + skip); // (moved by the tail's rows above: read from where the rank lands)
                    continue;
                }

                if (skip >= 0 && merged.Count - skip < read.Count && more && limit < MostLimit)
                {
                    limit *= 2; // (the tail's overrides left it short)
                    continue;
                }

                page.Clear();
                var from = (int)Math.Clamp(skip, 0, merged.Count);
                for (var i = from; i < merged.Count && page.Count < read.Count; i++) page.Add(merged.Keys[i], merged.Rows[i]);
                var firstRank = page.Count > 0 ? first + from : read.Rank;
                var (total, sure) = Total(v, sized?.Rows ?? at + ahead.Count);
                var answer = new GridAnswer
                {
                    FirstRank = firstRank, Total = Math.Max(total, firstRank + page.Count), TotalKind = KindOf(sized, sure),
                    Version = _version
                };
                Keep(v, null, ahead, full, at == 0, page, answer, read);
                return answer;
            }
        }
    }

    /// <summary>About a row (or a key, its row the tail's or the store's): rows before its place, then it and the rows after.</summary>
    private async ValueTask<GridAnswer> AboutAsync(View v, GridRead<TKey, TRow> read, GridPage<TKey, TRow> page,
        GridPage<TKey, TRow> ahead, GridPage<TKey, TRow> back, CancellationToken cancellationToken)
    {
        var key = read.Key;
        var row = read.Row;
        if (read.Kind == GridReadKind.AroundKey)
        {
            if (await RowOfAsync(key, ahead, read.MaxBytes, cancellationToken).ConfigureAwait(false) is not { } found)
                return new GridAnswer { Anchor = GridAnswer.NotFound, Version = Version };
            row = found;
        }

        for (var limit = Limit(Math.Max(read.Before, read.Count));; limit *= 2)
        {
            back.Clear();
            ahead.Clear();
            // Back from its place, whatever the read wants before it: short of the limit, those are all the rows before
            // it, and their count its place (nothing asked); else the store's word. Short ahead, the view ends there.
            await _store.ReadAsync(v.Grid, GridSeek<TKey, TRow>.Before(key, row), limit, read.MaxBytes, back, cancellationToken)
                .ConfigureAwait(false);
            await _store.ReadAsync(v.Grid, GridSeek<TKey, TRow>.From(key, row), limit, read.MaxBytes, ahead, cancellationToken)
                .ConfigureAwait(false);
            var rank = back.Count < limit
                ? (back.Count, TotalKind.Exact)
                : await _store.RankAsync(v.Grid, key, row, read.MaxBytes, cancellationToken).ConfigureAwait(false);
            if (rank is not { } placed) return GridAnswer.Refused("The store can't say where this row sorts in the view: a held grid is placed by rank.");
            var sized = ahead.Count < limit
                ? (placed.Rows + ahead.Count, placed.Kind)
                : await _store.SizeAsync(v.Grid, read.MaxBytes, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _readRows.Clear();
                Saw(back);
                Saw(ahead);
                page.Clear();
                var behind = read.Before > 0 ? Backward(v, row, back, back.Count == limit, read.Before, page) : 0;
                Forward(v, GridSeek<TKey, TRow>.From(key, row), ahead, ahead.Count == limit, read.Count, page);
                var cut = (read.Before > 0 && behind < read.Before && back.Count == limit) ||
                          (page.Count - behind < read.Count && ahead.Count == limit);
                if (cut && limit < MostLimit) continue; // (the tail's overrides left it short)
                if (read.Kind == GridReadKind.AroundKey && !Has(page, behind, key))
                    return new GridAnswer { Anchor = GridAnswer.NotFound, Version = _version };
                var (total, sure) = Total(v, sized?.Rows ?? placed.Rows + ahead.Count);
                var first = placed.Rows + Above(v, row) - behind;
                var answer = new GridAnswer
                {
                    FirstRank = Math.Max(0, first), Anchor = behind, Total = Math.Max(total, first + page.Count),
                    TotalKind = KindOf(sized, sure), Version = _version
                };
                Keep(v, back, ahead, ahead.Count == limit, back.Count < limit, page, answer, read);
                return answer;
            }
        }
    }

    /// <summary>
    ///     The view's rows from a seek on, merged into <paramref name="page" /> up to <paramref name="count" />: the store's
    ///     (<paramref name="ahead" />, read from the seek) but those the tail has moved on, and the tail's passing rows in
    ///     the range — to the store's last row read, if it read as many as asked (more lie past it), else on — each key once,
    ///     at the newest version either side knows.
    /// </summary>
    private void Forward(View v, GridSeek<TKey, TRow> seek, GridPage<TKey, TRow> ahead, bool full, int count,
        GridPage<TKey, TRow> page)
    {
        var order = v.Order;
        Sorted(v);
        var end = page.Count + count;
        int i = 0, j = seek.Kind == GridSeekKind.First ? 0 : LowerBound(v, seek.Row);
        while (page.Count < end)
        {
            while (i < ahead.Count && Superseded(ahead.Keys[i], ahead.Rows[i])) i++;
            while (j < v.Count && StoreNewer(v.Keys[j])) j++;
            var tail = j < v.Count && (!full || ahead.Count == 0 || order.Compare(v.Rows[j], ahead.Rows[ahead.Count - 1]) <= 0);
            var store = i < ahead.Count;
            if (!tail && !store) break;
            var added = store && (!tail || order.Compare(ahead.Rows[i], v.Rows[j]) < 0)
                ? page.Add(ahead.Keys[i], ahead.Rows[i++])
                : page.Add(v.Keys[j], v.Rows[j++]);
            if (!added) break;
        }
    }

    /// <summary>
    ///     The view's rows back from a row's place (<paramref name="back" />: the store's, nearest first), merged as
    ///     <see cref="Forward" /> does, at most <paramref name="count" />, into <paramref name="page" /> in the view's order: how many.
    /// </summary>
    private int Backward(View v, in TRow row, GridPage<TKey, TRow> back, bool full, int count, GridPage<TKey, TRow> page)
    {
        var order = v.Order;
        Sorted(v);
        var start = page.Count;
        int i = 0, j = LowerBound(v, row) - 1;
        while (page.Count - start < count)
        {
            while (i < back.Count && Superseded(back.Keys[i], back.Rows[i])) i++;
            while (j >= 0 && StoreNewer(v.Keys[j])) j--;
            var tail = j >= 0 && (!full || back.Count == 0 || order.Compare(v.Rows[j], back.Rows[back.Count - 1]) >= 0);
            var store = i < back.Count;
            if (!tail && !store) break;
            var added = store && (!tail || order.Compare(back.Rows[i], v.Rows[j]) > 0)
                ? page.Add(back.Keys[i], back.Rows[i++])
                : page.Add(v.Keys[j], v.Rows[j--]);
            if (!added) break;
        }

        var n = page.Count - start; // (nearest first: turned to the view's order)
        page.Reverse(start, n);
        return n;
    }

    /// <summary>
    ///     A store row the merge passes over: the tail has moved its key on (a newer version), or the read brought a newer
    ///     row of its key (the store moving between the read's statements).
    /// </summary>
    private bool Superseded(in TKey key, in TRow row)
    {
        var version = _rows.VersionOf(row);
        return (_readRows.TryGetValue(key, out var read) && _rows.VersionOf(read) > version) ||
               (_tail.TryGetValue(key, out var e) && version < e.Version);
    }

    /// <summary>
    ///     A tail row the merge passes over: the read brought its key as new (the store's row stands), or the store has been
    ///     seen past the stream (its row stands, wherever it sorts), or the tail let it go.
    /// </summary>
    private bool StoreNewer(in TKey key)
    {
        if (!_tail.TryGetValue(key, out var e)) return true;
        if (_readRows.TryGetValue(key, out var read) && _rows.VersionOf(read) >= e.Version) return true;
        return e.Known == 1 && _rows.VersionOf(e.Stored) > e.Version;
    }

    /// <summary>
    ///     How the merge moves a place's rank from the store's: the tail's passing rows above it, less the store's rows above
    ///     it the tail has moved on — the read's own (they pass: the store filtered them), else each key's as last seen.
    /// </summary>
    private long Above(View v, in TRow place)
    {
        long above = 0;
        foreach (var (key, e) in _tail)
        {
            if (!v.Scope.TryMatch(key, out var newest, out var stored)) continue;
            if (_readRows.TryGetValue(key, out var read))
            {
                if (_rows.VersionOf(read) >= e.Version) continue; // (the store has it: as it counts)
                if ((newest || stored) && v.Order.Compare(e.Newest, place) < 0) above++;
                if (v.Order.Compare(read, place) < 0) above--; // (the store counted the read's row: it passes)
                continue;
            }

            if (e.Known == 1 && _rows.VersionOf(e.Stored) >= e.Version) continue;
            if ((newest || stored) && v.Order.Compare(e.Newest, place) < 0) above++; // (in the view, at its newest)
            if (stored && v.Order.Compare(e.Stored, place) < 0) above--; // (the store counts its row where it sorts)
        }

        return above;
    }

    /// <summary>A total's kind: the store's (counted, or an estimate), an estimate while the tail holds keys whose store rows aren't known, at least the rows seen if the store couldn't say.</summary>
    private static TotalKind KindOf((long Rows, TotalKind Kind)? sized, bool sure)
    {
        return sized is not { } size ? TotalKind.AtLeast : sure ? size.Kind : TotalKind.Estimated;
    }

    /// <summary>How many of a store read's rows sort before a place: those the merge passed over, which the store's ranks count.</summary>
    private static int Before(View v, GridPage<TKey, TRow> read, in TRow place)
    {
        var n = 0;
        while (n < read.Count && v.Order.Compare(read.Rows[n], place) < 0) n++;
        return n;
    }

    /// <summary>The view's rows in all: the store's, moved by the tail's — sure only if every tail key's store row is known.</summary>
    private (long Total, bool Sure) Total(View v, long size)
    {
        var sure = true;
        foreach (var (key, e) in _tail)
        {
            if (!v.Scope.TryMatch(key, out var newest, out var stored)) continue;
            if (_readRows.TryGetValue(key, out var read))
            {
                if (_rows.VersionOf(read) >= e.Version) continue;
                if (newest || stored) size++;
                size--; // (the read's row, passing, counted by the store)
                continue;
            }

            if (e.Known == 1 && _rows.VersionOf(e.Stored) >= e.Version) continue;
            if (e.Known == 0) sure = false;
            if (newest || stored) size++;
            if (stored) size--;
        }

        return (Math.Max(0, size), sure);
    }

    /// <summary>A key's row, for a read about it: the newest the tail knows, else the store's (null: neither has it).</summary>
    private async ValueTask<TRow?> RowOfAsync(TKey key, GridPage<TKey, TRow> page, uint maxBytes, CancellationToken cancellationToken)
    {
        lock (_gate)
            if (_tail.TryGetValue(key, out var e))
                return e.Known == 1 && _rows.VersionOf(e.Stored) > e.Version ? e.Stored : e.Newest;
        var keys = ArrayPool<TKey>.Shared.Rent(1);
        var below = ArrayPool<long>.Shared.Rent(1);
        try
        {
            (keys[0], below[0]) = (key, long.MaxValue);
            page.Clear();
            await _store.EarlierAsync(keys.AsMemory(0, 1), below.AsMemory(0, 1), maxBytes, page, cancellationToken).ConfigureAwait(false);
            return page.Count > 0 ? page.Rows[0] : null;
        }
        finally
        {
            ArrayPool<TKey>.Shared.Return(keys);
            ArrayPool<long>.Shared.Return(below);
        }
    }

    private static bool Has(GridPage<TKey, TRow> page, int from, in TKey key)
    {
        for (var i = from; i < page.Count; i++)
            if (page.Keys[i].Equals(key))
                return true;
        return false;
    }

    /// <summary>The view's passing tail rows, in its order: sorted again once the tail has changed.</summary>
    private void Sorted(View v)
    {
        if (v.At == _tailVersion) return;
        v.Count = 0;
        foreach (var (key, e) in _tail)
        {
            if (!v.Scope.TryMatch(key, out var newest, out var stored) || !(newest || stored)) continue;
            if (v.Count == v.Rows.Length)
            {
                Array.Resize(ref v.Rows, 2 * v.Count);
                Array.Resize(ref v.Keys, 2 * v.Count);
            }

            (v.Rows[v.Count], v.Keys[v.Count]) = (e.Newest, key);
            v.Count++;
        }

        Array.Sort(v.Rows, v.Keys, 0, v.Count, v.Order);
        v.At = _tailVersion;
    }

    /// <summary>The first of the view's sorted tail rows at or after a place.</summary>
    private static int LowerBound(View v, in TRow place)
    {
        int low = 0, high = v.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (v.Order.Compare(v.Rows[middle], place) < 0) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    /// <summary>A view read for the first time: its filter opened by the catch-up, over the tail as published.</summary>
    private Task<View> Open(GridView view)
    {
        if (!_opening.TryGetValue(view, out var opening))
        {
            _opening[view] = opening = new Opening(view);
            _pendingOpens.Add(opening);
            _received++;
        }

        return opening.Done.Task;
    }

    /// <summary>A view's filter done with: disposed by the catch-up, between evaluations.</summary>
    private void Close(IGridScope<TKey, TRow> scope)
    {
        _pendingCloses.Add(scope);
        _received++;
    }

    /// <summary>A batch from the stream.</summary>
    private void Streamed(ReadOnlySpan<TRow> rows)
    {
        Take(rows, true);
    }

    /// <summary>
    ///     Versions from the stream (the event log's at once), or the gap fill's as if streamed (the store's rows too), for
    ///     the catch-up to take into the state tail.
    /// </summary>
    private void Take(ReadOnlySpan<TRow> rows, bool streamed)
    {
        bool start;
        lock (_gate)
        {
            if (_disposed) return;
            var now = _time.GetTimestamp();
            foreach (ref readonly var row in rows)
            {
                if (streamed) Log(now, row);
                var stamp = _rows.StampOf(row);
                if (_newest is not { } newest || stamp.CompareTo(newest) > 0) _newest = stamp;
                _pendingRows.Add((row, !streamed));
            }

            _received++;
            start = Starting();
        }

        if (start) _ = CatchUpAsync();
    }

    /// <summary>
    ///     The catch-up, one turn at a time: what's pending taken; the views done with disposed and those asked for opened;
    ///     the tail's changes staged, evaluated by every view's filter, and published with them. On the caller's thread while
    ///     the filters answer at once.
    /// </summary>
    private async Task CatchUpAsync()
    {
        while (true)
        {
            long target;
            lock (_gate)
            {
                if (!Pending())
                {
                    _catchingUp = false;
                    return;
                }

                (_pendingRows, _takenRows) = (_takenRows, _pendingRows);
                (_pendingStored, _takenStored) = (_takenStored, _pendingStored);
                (_pendingGone, _takenGone) = (_takenGone, _pendingGone);
                (_pendingOpens, _takenOpens) = (_takenOpens, _pendingOpens);
                (_pendingCloses, _takenCloses) = (_takenCloses, _pendingCloses);
                target = _received;
            }

            foreach (var scope in _takenCloses)
                try
                {
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // (done with, however it ends)
                }

            _takenCloses.Clear();
            if (_takenOpens.Count > 0)
            {
                ReadOnlyMemory<GridTailRow<TKey, TRow>> tail;
                lock (_gate) tail = Snapshot();
                foreach (var opening in _takenOpens)
                    try
                    {
                        opening.Order = _rows.OrderOf(opening.View); // (an order it can't keep refuses the view)
                        opening.Scope = await _scopes.OpenAsync(opening.View, tail, _stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception refused)
                    {
                        opening.Refused = refused;
                    }
            }

            int changed, gone, rows;
            lock (_gate) (changed, gone, rows) = Stage();
            Exception? failed = null;
            if (changed > 0 || gone > 0)
                try
                {
                    await _scopes.EvaluateAsync(_changed.AsMemory(0, changed), _goneRows.AsMemory(0, gone), _stop.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    failed = failure;
                }

            Action[] watching;
            bool ask;
            lock (_gate) (watching, ask) = (Publish(target, rows > 0, failed), AskReady());
            if (ask) _ = AskAsync();
            foreach (var opening in _takenOpens) Settle(opening);
            _takenOpens.Clear();
            foreach (var watcher in watching) watcher();
        }
    }

    /// <summary>Whether the catch-up has anything to take (disposed: only the views to close).</summary>
    private bool Pending()
    {
        if (_disposed)
        {
            _pendingRows.Clear();
            _pendingStored.Clear();
            _pendingGone.Clear();
            return _pendingCloses.Count > 0 || _pendingOpens.Count > 0;
        }

        return _pendingRows.Count > 0 || _pendingStored.Count > 0 || _pendingGone.Count > 0 || _pendingOpens.Count > 0 ||
               _pendingCloses.Count > 0 || _retell;
    }

    /// <summary>Whether the caller is to start the catch-up: none running, and something pending.</summary>
    private bool Starting()
    {
        if (_catchingUp || !Pending()) return false;
        _catchingUp = true;
        return true;
    }

    /// <summary>
    ///     What the rows taken and the store rows seen change in the state tail, staged against it (published) — a version a
    ///     haircut behind the store's newest, not held, is in the store — and the keys let go: for the filters to evaluate.
    /// </summary>
    private (int Changed, int Gone, int Rows) Stage()
    {
        var cutoff = Cutoff();
        foreach (var (row, stored) in _takenRows)
        {
            var key = _rows.KeyOf(row);
            var version = _rows.VersionOf(row);
            var stamp = _rows.StampOf(row);
            var had = _staged.TryGetValue(key, out var e) || _tail.TryGetValue(key, out e);
            // (a version a haircut behind the store's newest is in it: the tail needn't take it — but for kept windows, which
            // may hold an older row of its key, it passes through, let go as the turn is published, the store's row now)
            var newer = had ? version > e.Version : cutoff is not { } c || stamp.CompareTo(c) > 0 || _windowCount > 0;
            var seen = stored && (!had || e.Known != 1 || version > _rows.VersionOf(e.Stored));
            if (!newer && !(had && seen)) continue; // (the tail has it, or newer; or the store has, and nothing newer)
            if (newer) (e.Newest, e.Version, e.Stamp, e.Fresh) = (row, version, stamp, true);
            if (seen) (e.Stored, e.Known) = (row, 1);
            _staged[key] = e;
        }

        foreach (var (key, row) in _takenStored)
        {
            if (!_staged.TryGetValue(key, out var e) && !_tail.TryGetValue(key, out e)) continue; // (let go: the store has it)
            if (e.Known == 1 && _rows.VersionOf(row) <= _rows.VersionOf(e.Stored)) continue;
            (e.Stored, e.Known) = (row, 1);
            _staged[key] = e;
        }

        var rows = _takenRows.Count;
        _takenRows.Clear();
        _takenStored.Clear();
        var n = 0;
        foreach (var (key, e) in _staged) Put(ref _changed, n++, new GridTailRow<TKey, TRow>(key, e.Newest, e.Stored, e.Known == 1));
        var g = 0;
        foreach (var (key, row) in _takenGone)
            if (!_staged.ContainsKey(key))
                Put(ref _goneRows, g++, row);
        _takenGone.Clear();
        return (n, g, rows);
    }

    /// <summary>
    ///     The staged changes into the state tail, and the filters' answers made visible with them; a turn that took
    ///     versions moves the source's version. The watchers to tell, returned.
    /// </summary>
    private Action[] Publish(long target, bool taken, Exception? failed)
    {
        if (_retell) (_retell, taken) = (false, true); // (the store has what was pruned: the grids read again)
        var changing = _windowCount > 0 ? Was() : 0;
        var fresh = false;
        foreach (var (key, staged) in _staged)
        {
            var e = staged;
            if (!e.Fresh && !_tail.ContainsKey(key)) continue; // (the store's row seen of a key let go meanwhile)
            if (e.Fresh)
            {
                _byStamp.Enqueue(key, e.Stamp);
                (e.Fresh, fresh) = (false, true);
            }

            _tail[key] = e;
            _pendingGone.Remove(key); // (taken again: not to be let go of)
        }

        _staged.Clear();
        if (failed is null && !_disposed)
        {
            _scopes.Publish();
            if (changing > 0) Changed(changing);
            foreach (var opening in _takenOpens)
                if (opening.Scope is { } scope)
                    _views[opening.View] = opening.Opened = new View(opening.View, scope, opening.Order!, _changing.Length);
        }
        else
        {
            foreach (var opening in _takenOpens)
                if (opening.Scope is { } scope)
                    Close(scope);
            if (failed is not null) // (the filters' answers unknown: each view opened again by its next read)
            {
                foreach (var v in _views.Values)
                {
                    Unhold(v);
                    Close(v.Scope);
                }

                _views.Clear();
                foreach (var opening in _takenOpens) opening.Refused ??= failed;
            }
        }

        foreach (var opening in _takenOpens) _opening.Remove(opening.View);
        _published = target;
        if (fresh) _tailVersion++;
        if (taken) _version++;
        for (var i = _waiters.Count - 1; i >= 0; i--)
            if (_waiters[i].Target <= _published)
            {
                _waiters[i].Done.TrySetResult();
                _waiters.RemoveAt(i);
            }

        Forget();
        Ask();
        Expire();
        return taken ? _watching : [];
    }

    /// <summary>A view asked for, opened (or refused): its reads go on.</summary>
    private void Settle(Opening opening)
    {
        if (opening.Opened is { } view) opening.Done.TrySetResult(view);
        else if (_disposed) opening.Done.TrySetException(new ObjectDisposedException(GetType().Name));
        else opening.Done.TrySetException(new GridRefusedException(opening.Refused?.Message ?? "The view's filter wasn't opened."));
    }

    /// <summary>A task done once the catch-up has published <paramref name="target" />.</summary>
    private Task Published(long target)
    {
        lock (_gate)
        {
            if (_published >= target) return Task.CompletedTask;
            ObjectDisposedException.ThrowIf(_disposed, this);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, done));
            return done.Task;
        }
    }

    /// <summary>The state tail as published, for a view opening over it.</summary>
    private ReadOnlyMemory<GridTailRow<TKey, TRow>> Snapshot()
    {
        if (_snapshot.Length < _tail.Count) _snapshot = new GridTailRow<TKey, TRow>[BitOperations.RoundUpToPowerOf2((uint)_tail.Count)];
        var n = 0;
        foreach (var (key, e) in _tail) _snapshot[n++] = new GridTailRow<TKey, TRow>(key, e.Newest, e.Stored, e.Known == 1);
        return _snapshot.AsMemory(0, n);
    }

    private static void Put(ref GridTailRow<TKey, TRow>[] into, int at, in GridTailRow<TKey, TRow> row)
    {
        if (at == into.Length) Array.Resize(ref into, 2 * at);
        into[at] = row;
    }

    /// <summary>A version into the event log, if a view keeps history: those older than its reach, and past its most, gone first.</summary>
    private void Log(long now, in TRow row)
    {
        if (_keep == 0) return;
        if (_logCount == _log.Length && _logCount < _policy.MaxHistoryRows)
        {
            var grown = new (long, TRow)[Math.Min(Math.Max(16, 2 * _log.Length), _policy.MaxHistoryRows)];
            for (var i = 0; i < _logCount; i++) grown[i] = _log[(_logStart + i) % _log.Length];
            (_log, _logStart) = (grown, 0);
        }

        if (_logCount == _log.Length) (_logStart, _logCount) = ((_logStart + 1) % _log.Length, _logCount - 1);
        _log[(_logStart + _logCount++) % _log.Length] = (now, row);
        var since = now - _keep;
        while (_logCount > 0 && _log[_logStart].Received < since) (_logStart, _logCount) = ((_logStart + 1) % _log.Length, _logCount - 1);
    }

    /// <summary>
    ///     A store read's rows, seen: the newest of each key it brought (for the merge), each tail key's store row noted for
    ///     the catch-up, and the watermark moved on — the store holds them.
    /// </summary>
    private void Saw(GridPage<TKey, TRow> read)
    {
        if (read.Count == 0) return;
        TStamp? newest = null;
        for (var i = 0; i < read.Count; i++)
        {
            var key = read.Keys[i];
            var row = read.Rows[i];
            var stamp = _rows.StampOf(row);
            if (newest is not { } was || stamp.CompareTo(was) > 0) newest = stamp;
            if (!_readRows.TryGetValue(key, out var held) || _rows.VersionOf(held) < _rows.VersionOf(row)) _readRows[key] = row;
            Note(key, row);
        }

        Saw(newest!.Value);
    }

    /// <summary>The store's row for a tail key, seen: for the catch-up to take, if newer than the one last seen.</summary>
    private void Note(in TKey key, in TRow stored)
    {
        if (!_tail.TryGetValue(key, out var e) || (e.Known == 1 && _rows.VersionOf(stored) <= _rows.VersionOf(e.Stored))) return;
        ref var pending = ref CollectionsMarshal.GetValueRefOrAddDefault(_pendingStored, key, out var had);
        if (had && _rows.VersionOf(pending) >= _rows.VersionOf(stored)) return;
        pending = stored;
        _received++;
    }

    /// <summary>The store holds versions stamped up to <paramref name="stamp" />: its watermark moves on.</summary>
    private void Saw(TStamp stamp)
    {
        if (_watermark is { } watermark && stamp.CompareTo(watermark) <= 0) return;
        _watermark = stamp;
        Forget();
    }

    /// <summary>
    ///     The state tail's rows the store has reached let go: stamped at or before <see cref="Cutoff" />. The views' filters
    ///     are told by the next turn of the catch-up; till then they aren't asked of them.
    /// </summary>
    private void Forget()
    {
        var cutoff = Cutoff();
        var gone = false;
        // Let go what the store has reached; and past MaxTailRows, the oldest, the likeliest in the store already (the
        // haircut is a worst case): reads go on, the keys pruned shown as the store has them.
        while (_byStamp.TryPeek(out var key, out var stamp) &&
               ((cutoff is { } c && stamp.CompareTo(c) <= 0) || _tail.Count > _policy.MaxTailRows))
        {
            _byStamp.Dequeue();
            if (!_tail.TryGetValue(key, out var e) || e.Stamp.CompareTo(stamp) != 0) continue;
            if (cutoff is not { } reached || stamp.CompareTo(reached) > 0) // (pruned, not reached: the store owes it)
            {
                _pruned = _pruned is { } was && was.CompareTo(stamp) >= 0 ? was : stamp;
                DropWindows(); // (its row the store's, which no window knows: read again)
            }
            else if (_windowCount > 0)
            {
                Reached(key, e);
            }

            _tail.Remove(key);
            _pendingGone[key] = new GridTailRow<TKey, TRow>(key, e.Newest, e.Stored, e.Known == 1);
            _received++;
            gone = true;
        }

        if (gone) _tailVersion++;
        if (_pruned is { } pruned && cutoff is { } past && pruned.CompareTo(past) <= 0)
        {
            // The store surely holds every row pruned: grids showing one as the store had it then read it again.
            (_pruned, _retell) = (null, true);
            DropWindows();
            _received++;
        }
    }

    /// <summary>
    ///     The newest stamp the store is sure to hold, for every read under way: a haircut before its watermark when the
    ///     earliest of them began (none seen yet: null).
    /// </summary>
    private TStamp? Cutoff()
    {
        if (_watermark is not { } watermark) return null;
        foreach (var began in _reading)
        {
            if (began is not { } stamp) return null;
            if (stamp.CompareTo(watermark) < 0) watermark = stamp;
        }

        return _rows.Back(watermark, _policy.Haircut);
    }

    /// <summary>
    ///     The store asked its newest stamp, for the tail to let go what it has reached — once a haircut at most — when the
    ///     tail's oldest row is getting old (two haircuts behind the stream's newest), or reads are refused (<paramref name="behind" />).
    /// </summary>
    private void Ask(bool behind = false)
    {
        if (_asking || _disposed || !_byStamp.TryPeek(out _, out var oldest) || _newest is not { } newest) return;
        if (!behind && _pruned is null && oldest.CompareTo(_rows.Back(newest, 2 * _policy.Haircut)) >= 0) return; // (pruned: owed)
        if (_time.GetTimestamp() - _askedAt < Ticks(_policy.Haircut)) return;
        Queue(false);
    }

    /// <summary>
    ///     A recheck, asked by a read: the store's recent versions read again, at most once a
    ///     <see cref="AppendOnlyStorePolicy.Recheck" />, while the tail holds rows — never on a clock, nor with no one reading.
    /// </summary>
    private void Recheck()
    {
        if (_asking || _disposed || _tail.Count == 0) return;
        if (_time.GetTimestamp() - _checkedAt < Ticks(_policy.Recheck)) return;
        Queue(true);
    }

    /// <summary>
    ///     An ask queued: a recheck to the thread pool (it reads rows: off any reader's thread); the store's newest stamp to
    ///     the caller, started once it lets the lock go — taken at once from a store that has it at once.
    /// </summary>
    private void Queue(bool recheck)
    {
        (_asking, _rechecking) = (true, recheck);
        if (recheck) ThreadPool.UnsafeQueueUserWorkItem(_asker, false); // (the store not called under the lock)
        else _askReady = true;
    }

    /// <summary>Whether an ask is ready for the caller to start, the lock let go (taken: it's the caller's to start).</summary>
    private bool AskReady()
    {
        if (!_askReady) return false;
        _askReady = false;
        return true;
    }

    /// <summary>
    ///     A recheck: the store's newest stamp, then every version stamped from a haircut before it, matching a view or not —
    ///     each tail key's row as the store now holds it, for the catch-up to take (a row a view kept by the store's last
    ///     leaves once the store has its newer version); and the watermark moved on, the tail letting go what it has reached.
    /// </summary>
    private async Task AskAsync()
    {
        TStamp? stamp = null;
        List<TRow>? recent = null;
        Exception? fault = null;
        bool recheck;
        lock (_gate) recheck = _rechecking;
        try
        {
            stamp = await _store.NewestStampAsync(_stop.Token).ConfigureAwait(false);
            if (recheck && stamp is { } newest)
            {
                recent = [];
                await foreach (var row in _store.SinceAsync(_rows.Back(newest, _policy.Haircut), _stop.Token).ConfigureAwait(false))
                    recent.Add(row);
            }
        }
        catch (Exception failure)
        {
            (fault, recent) = (failure, null); // (read again at the next recheck)
        }

        bool start;
        lock (_gate)
        {
            RecheckFault = fault;
            _asking = false;
            _askedAt = _time.GetTimestamp();
            if (recheck) _checkedAt = _askedAt;
            if (recent is not null)
            {
                foreach (var row in recent) Note(_rows.KeyOf(row), row);
            }

            if (stamp is { } newest) Saw(newest);
            if (recheck) Rechecks++;
            // (what it let go, and the store's rows it saw, the next turn takes — a batch's or a read's, on their own thread,
            // not this one, racing them into turns of thousands; only grids to be told to read again start one now)
            start = _retell && Starting();
        }

        if (start) _ = CatchUpAsync();
    }

    /// <summary>The gap fill: the versions stamped from GapFill before the subscription, taken as if streamed.</summary>
    private async Task GapFillAsync()
    {
        try
        {
            var rows = new List<TRow>();
            TStamp? newest = null;
            await foreach (var row in _store.SinceAsync(_rows.Back(_subscribed, _policy.GapFill), _stop.Token).ConfigureAwait(false))
            {
                rows.Add(row);
                var stamp = _rows.StampOf(row);
                if (newest is not { } was || stamp.CompareTo(was) > 0) newest = stamp;
            }

            if (newest is { } seen) lock (_gate) Saw(seen); // (first: what the store has passed by a haircut, the tail needn't take)
            Take(CollectionsMarshal.AsSpan(rows), false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            GapFillFault = e;
        }
        finally
        {
            _gapFilled.TrySetResult();
        }
    }

    /// <summary>
    ///     A read a kept window covers, answered from it (null: none does): its rows, placed by its start; one due a refresh
    ///     has one started. A rank is found only in a window sure of its place.
    /// </summary>
    private GridAnswer? FromWindow(View v, GridRead<TKey, TRow> read, GridPage<TKey, TRow> page)
    {
        for (var n = v.Windows.Count - 1; n >= 0; n--)
        {
            var w = v.Windows[n];
            int from, to, anchor = 0;
            if (read.Kind == GridReadKind.AtRank)
            {
                if (!w.Top && (w.Guessed || w.Kind != TotalKind.Exact)) continue;
                var at = read.Rank - w.Start;
                if (at < 0 || at > w.Count || (at + read.Count > w.Count && !w.End)) continue;
                (from, to) = ((int)at, (int)Math.Min(w.Count, at + read.Count));
            }
            else
            {
                TRow place;
                if (read.Kind == GridReadKind.AroundKey)
                {
                    if (!w.Held.TryGetValue(read.Key, out place)) continue;
                }
                else
                {
                    place = read.Row;
                }

                if (!InRange(v, w, place)) continue;
                var at = LowerBound(v, w, place);
                if ((at < read.Before && !w.Top) || (w.Count - at < read.Count && !w.End)) continue;
                (from, to) = (Math.Max(0, at - read.Before), Math.Min(w.Count, at + read.Count));
                anchor = at - from;
            }

            page.Clear();
            for (var i = from; i < to && page.Add(w.Keys[i], w.Rows[i]); i++)
            {
            }

            var now = _time.GetTimestamp();
            (w.UsedAt, w.Wanted) = (now, Math.Max(w.Wanted, read.Kind == GridReadKind.AtRank ? read.Count : read.Before + read.Count));
            if (page.Count > 0) Remember(w, page.Rows[0], now);
            if (_policy.Refresh > TimeSpan.Zero && !w.Refreshing && now - w.ReadAt >= Ticks(_policy.Refresh)) Refresh(v, w);
            WindowReads++;
            var first = read.Kind == GridReadKind.AtRank && page.Count == 0 ? read.Rank : w.Start + from;
            var (total, kind) = w.Top && w.End ? (w.Count, TotalKind.Exact)
                : (w.Total, w.Guessed && w.Kind == TotalKind.Exact ? TotalKind.Estimated : w.Kind);
            return new GridAnswer
            {
                FirstRank = first, Anchor = anchor, Total = Math.Max(total, first + page.Count), TotalKind = kind, Version = _version
            };
        }

        return null;
    }

    /// <summary>
    ///     What a read's statements brought, kept as a window of the view (<see cref="Made" />), placed by the read's answer,
    ///     in place of the windows it overlaps.
    /// </summary>
    private void Keep(View v, GridPage<TKey, TRow>? back, GridPage<TKey, TRow> ahead, bool full, bool top, GridPage<TKey, TRow> page,
        in GridAnswer answer, GridRead<TKey, TRow> read)
    {
        if (_policy.Windows != StoreWindows.Kept || !_views.TryGetValue(v.Grid, out var live) || live != v) return;
        if (!top && back is not { Count: > 0 } && ahead.Count == 0) return;
        var lo = top ? default : back is { Count: > 0 } ? back.Rows[back.Count - 1] : ahead.Rows[0];
        var wanted = read.Kind == GridReadKind.AtRank ? read.Count : read.Before + read.Count;
        var w = Made(v, back, ahead, full, top, lo, wanted);
        if (page.Count > 0)
        {
            var at = LowerBound(v, w, page.Rows[0]);
            if (at == w.Count || !w.Keys[at].Equals(page.Keys[0])) return; // (not the merge the answer was: kept none)
            w.Start = top ? 0 : answer.FirstRank - at; // (nothing before the view's first row, whatever an estimate said)
        }
        else if (!top)
        {
            return;
        }

        var now = _time.GetTimestamp();
        if (page.Count > 0) Remember(w, page.Rows[0], now);
        (w.Total, w.Kind, w.ReadAt, w.UsedAt, w.MaxBytes, w.Wanted) = (answer.Total, answer.TotalKind, now, now, read.MaxBytes, wanted);
        Hold(v, w);
    }

    /// <summary>
    ///     A window of the view over what statements brought: every row from <paramref name="lo" /> (or the view's first,
    ///     <paramref name="top" />) to the last store row read (or the view's last, the read short of its limit) — the
    ///     store's, <paramref name="back" /> (nearest first) then <paramref name="ahead" />, but those the tail has moved on,
    ///     and the tail's passing rows between, but those the store has passed — in the view's order. Unplaced.
    /// </summary>
    private Window Made(View v, GridPage<TKey, TRow>? back, GridPage<TKey, TRow> ahead, bool full, bool top, in TRow lo, int wanted)
    {
        var w = new Window { Top = top, End = !full, Lo = lo };
        if (full) w.Hi = ahead.Rows[ahead.Count - 1];
        Sorted(v);
        int from = top ? 0 : LowerBound(v, lo), to = from;
        while (to < v.Count && (w.End || v.Order.Compare(v.Rows[to], w.Hi) <= 0)) to++;
        var size = (back?.Count ?? 0) + ahead.Count + (to - from);
        var room = Math.Max(size, Room(wanted)); // (room to grow to its trim: inserts allocate nothing)
        (w.Keys, w.Rows) = (new TKey[room], new TRow[room]);
        w.Held.EnsureCapacity(room);
        var stored = 0; // (the store's rows first, in order; then merged with the tail's from the back)
        if (back is not null)
            for (var i = back.Count - 1; i >= 0; i--)
                if (!Superseded(back.Keys[i], back.Rows[i]))
                    (w.Keys[stored], w.Rows[stored++]) = (back.Keys[i], back.Rows[i]);
        for (var i = 0; i < ahead.Count; i++)
            if (!Superseded(ahead.Keys[i], ahead.Rows[i]))
                (w.Keys[stored], w.Rows[stored++]) = (ahead.Keys[i], ahead.Rows[i]);
        var tail = 0;
        for (var j = from; j < to; j++)
            if (!StoreNewer(v.Keys[j]))
                tail++;
        w.Count = stored + tail;
        int s = stored - 1, t = to - 1;
        for (var k = w.Count - 1; k >= 0; k--)
        {
            while (t >= from && StoreNewer(v.Keys[t])) t--;
            if (t >= from && (s < 0 || v.Order.Compare(v.Rows[t], w.Rows[s]) > 0)) (w.Keys[k], w.Rows[k]) = (v.Keys[t], v.Rows[t--]);
            else (w.Keys[k], w.Rows[k]) = (w.Keys[s], w.Rows[s--]);
        }

        for (var i = 0; i < w.Count; i++) w.Held[w.Keys[i]] = w.Rows[i];
        return w;
    }

    /// <summary>A window kept, joined with those it overlaps; past the policy's rows, the least recently read let go.</summary>
    private void Hold(View v, Window w)
    {
        for (var i = v.Windows.Count - 1; i >= 0; i--)
            if (!Before(v, v.Windows[i], w) && !Before(v, w, v.Windows[i]))
            {
                w = Union(v, v.Windows[i], w);
                Drop(v, i);
            }

        v.Windows.Add(w);
        _windowCount++;
        Count(v, w.Count);
        Fit(v, w);
        while (_windowRows > _policy.MaxWindowRows)
        {
            View? from = null;
            var (at, used) = (-1, long.MaxValue);
            foreach (var other in _views.Values)
                for (var i = 0; i < other.Windows.Count; i++)
                    if (other.Windows[i] != w && other.Windows[i].UsedAt < used)
                        (from, at, used) = (other, i, other.Windows[i].UsedAt);
            if (from is null) break; // (the one just kept alone: past the bound till it's read past)
            Drop(from, at);
        }
    }

    /// <summary>
    ///     Two overlapping windows of a view as one: <paramref name="newer" />'s rows over its range, <paramref name="older" />'s
    ///     either side of it — placed from whichever starts first, its total the newer's, read again as old as the older.
    /// </summary>
    private Window Union(View v, Window older, Window newer)
    {
        var before = !newer.Top && (older.Top || v.Order.Compare(older.Lo, newer.Lo) < 0); // (the older reaching further, each way)
        var after = !newer.End && (older.End || v.Order.Compare(older.Hi, newer.Hi) > 0);
        if (!before && !after) return newer;
        var lead = before ? LowerBound(v, older, newer.Lo) : 0; // (the older's rows before the newer's range)
        var tail = older.Count;
        if (after)
        {
            tail = LowerBound(v, older, newer.Hi);
            if (tail < older.Count && v.Order.Compare(older.Rows[tail], newer.Hi) == 0) tail++;
        }

        var w = new Window
        {
            Top = before ? older.Top : newer.Top, End = after ? older.End : newer.End, Lo = before ? older.Lo : newer.Lo,
            Hi = after ? older.Hi : newer.Hi, Start = before ? older.Start : newer.Start, Total = newer.Total, Kind = newer.Kind,
            Guessed = before ? older.Guessed : newer.Guessed, ReadAt = Math.Min(older.ReadAt, newer.ReadAt),
            UsedAt = Math.Max(older.UsedAt, newer.UsedAt), Wanted = Math.Max(older.Wanted, newer.Wanted), MaxBytes = newer.MaxBytes
        };
        var size = lead + newer.Count + (older.Count - tail);
        var room = Math.Max(size, Room(w.Wanted));
        (w.Keys, w.Rows) = (new TKey[room], new TRow[room]);
        w.Held.EnsureCapacity(room);
        Array.Copy(older.Keys, w.Keys, lead);
        Array.Copy(older.Rows, w.Rows, lead);
        Array.Copy(newer.Keys, 0, w.Keys, lead, newer.Count);
        Array.Copy(newer.Rows, 0, w.Rows, lead, newer.Count);
        Array.Copy(older.Keys, tail, w.Keys, lead + newer.Count, older.Count - tail);
        Array.Copy(older.Rows, tail, w.Rows, lead + newer.Count, older.Count - tail);
        w.Count = size;
        for (var i = 0; i < size; i++) w.Held[w.Keys[i]] = w.Rows[i];

        for (var i = 0; i < older.PlaceCount; i++) Remember(w, older.Places[i].Row, older.Places[i].At);
        for (var i = 0; i < newer.PlaceCount; i++) Remember(w, newer.Places[i].Row, newer.Places[i].At);
        return w; // (its rows counted as it's kept; the older's let go as it's dropped)
    }

    /// <summary>A read's first row in a window, remembered (the oldest of four let go): rows near a recent read aren't trimmed.</summary>
    private static void Remember(Window w, in TRow place, long at)
    {
        var slot = w.PlaceCount;
        if (slot == w.Places.Length)
        {
            slot = 0;
            for (var i = 1; i < w.Places.Length; i++)
                if (w.Places[i].At < w.Places[slot].At)
                    slot = i;
        }
        else
        {
            w.PlaceCount++;
        }

        w.Places[slot] = (place, at);
    }

    /// <summary>
    ///     A view's windows past the policy's rows for a view: its least recently read let go, but <paramref name="kept" />
    ///     (none named: the most recently read).
    /// </summary>
    private void Fit(View v, Window? kept)
    {
        if (v.WindowRows <= _policy.MaxWindowRowsPerView) return;
        if (kept is null)
            foreach (var w in v.Windows)
                if (kept is null || w.UsedAt > kept.UsedAt)
                    kept = w;
        while (v.WindowRows > _policy.MaxWindowRowsPerView && v.Windows.Count > 1)
        {
            var (at, used) = (-1, long.MaxValue);
            for (var i = 0; i < v.Windows.Count; i++)
                if (v.Windows[i] != kept && v.Windows[i].UsedAt < used)
                    (at, used) = (i, v.Windows[i].UsedAt);
            if (at < 0) break;
            Drop(v, at);
        }
    }

    /// <summary>Rows into a view's windows (out, negative): its count and the windows' in all.</summary>
    private void Count(View v, long rows)
    {
        _windowRows += rows;
        v.WindowRows += rows;
    }

    /// <summary>Whether <paramref name="a" /> ends before <paramref name="b" /> starts.</summary>
    private static bool Before(View v, Window a, Window b)
    {
        return !a.End && !b.Top && v.Order.Compare(a.Hi, b.Lo) < 0;
    }

    private void Drop(View v, int at)
    {
        _windowCount--;
        Count(v, -v.Windows[at].Count);
        v.Windows.RemoveAt(at);
    }

    /// <summary>A view's kept windows let go (the view done with).</summary>
    private void Unhold(View v)
    {
        for (var i = v.Windows.Count - 1; i >= 0; i--) Drop(v, i);
    }

    /// <summary>Every kept window let go: rows the store holds that no window knows (pruned from the tail before it had them).</summary>
    private void DropWindows()
    {
        if (_windowCount == 0) return;
        foreach (var v in _views.Values) Unhold(v);
    }

    /// <summary>The kept windows no read has used for a while let go (looked at once a second at most).</summary>
    private void Expire()
    {
        if (_windowCount == 0) return;
        var now = _time.GetTimestamp();
        if (now - _expiredAt < Ticks(TimeSpan.FromSeconds(1))) return;
        _expiredAt = now;
        foreach (var v in _views.Values)
            for (var i = v.Windows.Count - 1; i >= 0; i--)
                if (now - v.Windows[i].UsedAt > Ticks(KeepIdle) && !v.Windows[i].Refreshing)
                    Drop(v, i);
    }

    /// <summary>
    ///     The keys a turn changes, each one's tail entry as it stands, before the turn (if the tail has the key), and every
    ///     windowed view's answer for it then: how many.
    /// </summary>
    private int Was()
    {
        var n = 0;
        foreach (var key in _staged.Keys)
        {
            if (n == _changing.Length)
            {
                Array.Resize(ref _changing, 2 * n);
                Array.Resize(ref _changingWas, 2 * n);
                Array.Resize(ref _changingHad, 2 * n);
            }

            _changing[n] = key;
            _changingHad[n] = _tail.TryGetValue(key, out _changingWas[n]);
            n++;
        }

        foreach (var v in _views.Values)
        {
            if (v.Windows.Count == 0) continue;
            if (v.WasIn.Length < n) v.WasIn = new bool[_changing.Length];
            for (var i = 0; i < n; i++) v.WasIn[i] = _changingHad[i] && InView(v, _changing[i], _changingWas[i], out _);
        }

        return n;
    }

    /// <summary>A turn's changes, published, into every windowed view's windows.</summary>
    private void Changed(int n)
    {
        foreach (var v in _views.Values)
        {
            if (v.Windows.Count == 0) continue;
            for (var i = 0; i < n; i++)
            {
                if (!_tail.TryGetValue(_changing[i], out var e)) continue; // (the store's row seen of a key let go meanwhile: no change)
                var now = InView(v, _changing[i], e, out var row);
                var was = _changingHad[i] ? (true, v.WasIn[i], RowOf(_changingWas[i])) : default((bool, bool, TRow));
                Change(v, _changing[i], was, now, row);
            }

            Fit(v, null); // (grown past its rows by what arrived: its least recently read let go)
        }
    }

    /// <summary>A tail key's row in a view as the merge shows it: the store's if it's as new, else the newest.</summary>
    private TRow RowOf(in Entry e)
    {
        return e.Known == 1 && _rows.VersionOf(e.Stored) >= e.Version ? e.Stored : e.Newest;
    }

    /// <summary>A key the store has reached, let go by the tail: in each windowed view its row now the store's, in it if that passes.</summary>
    private void Reached(in TKey key, in Entry e)
    {
        foreach (var v in _views.Values)
        {
            if (v.Windows.Count == 0) continue;
            var was = InView(v, key, e, out var row);
            var standing = e.Known == 1 && _rows.VersionOf(e.Stored) >= e.Version; // (the store's row stood already)
            var now = v.Scope.TryMatch(key, out var newest, out var stored) && (standing ? stored : newest);
            Change(v, key, (true, was, row), now, row);
            Fit(v, null);
        }
    }

    /// <summary>A tail key's row in a view as the merge shows it — the store's if it's as new, else the newest — and whether the view holds it.</summary>
    private bool InView(View v, in TKey key, in Entry e, out TRow row)
    {
        var match = v.Scope.TryMatch(key, out var newest, out var stored);
        if (e.Known == 1 && _rows.VersionOf(e.Stored) >= e.Version)
        {
            row = e.Stored;
            return match && stored;
        }

        row = e.Newest;
        return match && (newest || stored);
    }

    /// <summary>
    ///     A key's change into a view's windows: out of each that held it, into each whose range its row now falls in, if
    ///     the view holds it; each window's place and the view's total moved by it — by a guess where its row before is
    ///     unknown (neither the tail nor the window held it), taken as arriving: a row added where it sorts, none taken away.
    ///     A window holding the key at a newer version (the store's, the stream behind it) keeps it.
    /// </summary>
    private void Change(View v, in TKey key, in (bool Known, bool In, TRow Row) was, bool now, in TRow row)
    {
        foreach (var w in v.Windows)
        {
            var held = w.Held.TryGetValue(key, out var had);
            if (held && _rows.VersionOf(had) > _rows.VersionOf(row)) continue;
            int before = 0, counted = 0;
            if (held)
            {
                Remove(v, w, key, had);
                counted = 1;
            }
            else if (was.Known)
            {
                counted = was.In ? 1 : 0;
                before = was.In && !w.Top && v.Order.Compare(was.Row, w.Lo) < 0 ? 1 : 0;
            }
            else
            {
                w.Guessed = true;
            }

            if (now && InRange(v, w, row)) Insert(v, w, key, row);
            w.Start += (now && !w.Top && v.Order.Compare(row, w.Lo) < 0 ? 1 : 0) - before;
            w.Total += (now ? 1 : 0) - counted;
            if (w.Count >= Room(w.Wanted)) Trim(v, w);
        }
    }

    /// <summary>How many rows a window holds before it's trimmed: four times its reads' (at least a least read's).</summary>
    private static int Room(int wanted)
    {
        return 4 * Math.Max(wanted, LeastLimit);
    }

    private static bool InRange(View v, Window w, in TRow row)
    {
        return (w.Top || v.Order.Compare(row, w.Lo) >= 0) && (w.End || v.Order.Compare(row, w.Hi) <= 0);
    }

    /// <summary>The first of a window's rows at or after a place.</summary>
    private static int LowerBound(View v, Window w, in TRow place)
    {
        int low = 0, high = w.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (v.Order.Compare(w.Rows[middle], place) < 0) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    private void Insert(View v, Window w, in TKey key, in TRow row)
    {
        var at = LowerBound(v, w, row);
        if (w.Count == w.Rows.Length)
        {
            Array.Resize(ref w.Keys, Math.Max(16, 2 * w.Count));
            Array.Resize(ref w.Rows, Math.Max(16, 2 * w.Count));
        }

        Array.Copy(w.Keys, at, w.Keys, at + 1, w.Count - at);
        Array.Copy(w.Rows, at, w.Rows, at + 1, w.Count - at);
        (w.Keys[at], w.Rows[at]) = (key, row);
        w.Count++;
        w.Held[key] = row;
        Count(v, 1);
    }

    private void Remove(View v, Window w, in TKey key, in TRow row)
    {
        var at = LowerBound(v, w, row);
        if (at == w.Count || !w.Keys[at].Equals(key)) at = Array.IndexOf(w.Keys, key, 0, w.Count); // (not where it sorts: found)
        Array.Copy(w.Keys, at + 1, w.Keys, at, w.Count - at - 1);
        Array.Copy(w.Rows, at + 1, w.Rows, at, w.Count - at - 1);
        w.Count--;
        w.Held.Remove(key);
        Count(v, -1);
    }

    /// <summary>
    ///     A window grown far past what its reads ask (rows arriving at the top, say) cut back about its recent reads' first
    ///     rows: twice its reads' rows after the furthest, as many before the nearest (all, from the view's top) — its range
    ///     shrunk to match.
    /// </summary>
    private void Trim(View v, Window w)
    {
        var keep = Math.Max(w.Wanted, LeastLimit);
        var now = _time.GetTimestamp();
        int low = int.MaxValue, high = 0;
        for (var i = 0; i < w.PlaceCount; i++)
        {
            if (now - w.Places[i].At > Ticks(KeepIdle)) continue;
            var at = LowerBound(v, w, w.Places[i].Row);
            (low, high) = (Math.Min(low, at), Math.Max(high, at));
        }

        if (low == int.MaxValue) low = 0;
        var from = w.Top ? 0 : Math.Max(0, low - keep);
        var to = Math.Min(w.Count, Math.Max(high, from) + 2 * keep);
        if (from == 0 && to == w.Count) return;
        for (var i = 0; i < w.Count; i++)
        {
            if (i < from || i >= to) w.Held.Remove(w.Keys[i]);
        }

        if (from > 0) (w.Lo, w.Start) = (w.Rows[from], w.Start + from);
        if (to < w.Count) (w.Hi, w.End) = (w.Rows[to - 1], false);
        Array.Copy(w.Keys, from, w.Keys, 0, to - from);
        Array.Copy(w.Rows, from, w.Rows, 0, to - from);
        Count(v, -(w.Count - (to - from)));
        w.Count = to - from;
    }

    /// <summary>
    ///     A window's refresh started, off the read's path — a read under way, so the tail keeps what its store rows may lack
    ///     — its rows as they stand for the store to say whether they're settled.
    /// </summary>
    private void Refresh(View v, Window w)
    {
        w.Refreshing = true;
        var began = _watermark;
        _reading.Add(began);
        ThreadPool.UnsafeQueueUserWorkItem(static r => _ = r.Source.RefreshAsync(r.View, r.Window, r.Began, r.Rows),
            new Refreshing(this, v, w, began, w.Rows.AsSpan(0, w.Count).ToArray()), false);
    }

    /// <summary>
    ///     A kept window read again from the store — unless the store says its rows are settled: its range from its first
    ///     place, merged with the tail as it stands then, put in its place — the grids told if the rows differ (what the
    ///     stream didn't carry); its place and the total asked again if a guess had moved them.
    /// </summary>
    private async Task RefreshAsync(View v, Window w, TStamp? began, TRow[] rows)
    {
        var ahead = Rent();
        Action[] tell = [];
        Exception? fault = null;
        var settled = false;
        try
        {
            try
            {
                settled = rows.Length > 0 && await _store.SettledAsync(rows, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                // (a veto: read again)
            }

            if (settled) return;
            bool top, guessed;
            TRow lo;
            int limit;
            uint maxBytes;
            lock (_gate) (top, guessed, lo, limit, maxBytes) = (w.Top, w.Guessed, w.Lo, Limit(Math.Max(w.Count, w.Wanted)), w.MaxBytes);
            var seek = top ? GridSeek<TKey, TRow>.First : GridSeek<TKey, TRow>.From(_rows.KeyOf(lo), lo);
            await _store.ReadAsync(v.Grid, seek, limit, maxBytes, ahead, _stop.Token).ConfigureAwait(false);
            var placed = guessed && !top ? await _store.RankAsync(v.Grid, _rows.KeyOf(lo), lo, maxBytes, _stop.Token).ConfigureAwait(false) : null;
            var sized = !guessed ? null
                : ahead.Count < limit && (top || placed is not null) ? ((placed?.Rows ?? 0) + ahead.Count, placed?.Kind ?? TotalKind.Exact)
                : await _store.SizeAsync(v.Grid, maxBytes, _stop.Token).ConfigureAwait(false);
            lock (_gate)
            {
                var at = v.Windows.IndexOf(w);
                if (at >= 0 && _views.TryGetValue(v.Grid, out var live) && live == v)
                {
                    _readRows.Clear();
                    Saw(ahead);
                    var fresh = Made(v, null, ahead, ahead.Count == limit, top, lo, w.Wanted);
                    (fresh.Start, fresh.Total, fresh.Kind, fresh.Guessed) = (w.Start, w.Total, w.Kind, w.Guessed);
                    if (guessed)
                    {
                        fresh.Start = top ? 0 : placed is { } p ? Math.Max(0, p.Rows + Above(v, lo)) : w.Start;
                        if (sized is { } size)
                        {
                            var (total, sure) = Total(v, size.Rows);
                            (fresh.Total, fresh.Kind) = (total, KindOf(size, sure));
                        }

                        fresh.Guessed = !top && placed is null;
                    }

                    (fresh.UsedAt, fresh.Wanted, fresh.MaxBytes) = (w.UsedAt, w.Wanted, w.MaxBytes);
                    for (var i = 0; i < w.PlaceCount; i++) Remember(fresh, w.Places[i].Row, w.Places[i].At);
                    fresh.ReadAt = _time.GetTimestamp();
                    var healed = !Same(v, w, fresh);
                    Count(v, fresh.Count - w.Count);
                    v.Windows[at] = fresh;
                    if (healed)
                    {
                        _version++;
                        tell = _watching;
                    }
                }
            }
        }
        catch (Exception failure) when (!_stop.IsCancellationRequested)
        {
            fault = failure; // (kept as it was: read again at the next refresh)
        }
        finally
        {
            Return(ahead);
            bool start;
            lock (_gate)
            {
                (w.Refreshing, w.ReadAt) = (false, _time.GetTimestamp());
                if (settled)
                {
                    Settles++;
                }
                else
                {
                    RefreshFault = fault;
                    Refreshes++;
                }

                _reading.Remove(began);
                Forget();
                start = Starting();
            }

            if (start) _ = CatchUpAsync();
        }

        foreach (var watcher in tell) watcher();
    }

    /// <summary>Whether two windows of a view hold the same rows — keys and versions — where both reach.</summary>
    private bool Same(View v, Window a, Window b)
    {
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            if (!a.Keys[i].Equals(b.Keys[j]) || _rows.VersionOf(a.Rows[i]) != _rows.VersionOf(b.Rows[j])) return false;
            i++;
            j++;
        }

        // (past the shorter's end, only rows the other's range doesn't reach)
        for (; i < a.Count; i++)
            if (InRange(v, b, a.Rows[i]))
                return false;
        for (; j < b.Count; j++)
            if (InRange(v, a, b.Rows[j]))
                return false;
        return true;
    }

    private TimeSpan? LagUnlocked()
    {
        return _watermark is { } watermark && _newest is { } newest
            ? watermark.CompareTo(newest) >= 0 ? TimeSpan.Zero : _rows.Between(watermark, newest)
            : null;
    }

    private GridPage<TKey, TRow> Rent()
    {
        return _pages.TryTake(out var page) ? page : new GridPage<TKey, TRow>(MostLimit);
    }

    private void Return(GridPage<TKey, TRow> page)
    {
        page.Clear();
        _pages.Add(page);
    }

    private static int Limit(int rows)
    {
        return (int)Math.Min(MostLimit, BitOperations.RoundUpToPowerOf2((uint)Math.Max(LeastLimit, rows)));
    }

    private long Ticks(TimeSpan time)
    {
        return (long)(time.TotalSeconds * _time.TimestampFrequency);
    }

    private struct Entry
    {
        public TRow Newest, Stored;
        public long Version;
        public TStamp Stamp;
        public byte Known; // the store's row for the key: 0 not known, 1 Stored, 2 none
        public bool Fresh; // staged: a newer version, its stamp for the tail's queue
    }

    private sealed class View(GridView grid, IGridScope<TKey, TRow> scope, IComparer<TRow> order, int size)
    {
        public readonly GridView Grid = grid;
        public readonly IComparer<TRow> Order = order;
        public readonly IGridScope<TKey, TRow> Scope = scope;
        public long At = -1; // the tail's version its sorted rows are of
        public int Count;
        public TKey[] Keys = new TKey[size];
        public TRow[] Rows = new TRow[size]; // its passing tail rows, in its order
        public readonly List<Window> Windows = []; // its kept windows, the most recently made last
        public long WindowRows; // the rows they hold
        public bool[] WasIn = new bool[size]; // each key a turn changes: whether it held it before
    }

    /// <summary>
    ///     A run of a view's rows a read found — every row of the view from <see cref="Lo" /> to <see cref="Hi" />, the store's
    ///     merged with the tail's — kept, and changed by every change published after.
    /// </summary>
    private sealed class Window
    {
        public readonly Dictionary<TKey, TRow> Held = []; // its keys, at the rows it holds
        public bool Top, End; // from the view's first row; to its last
        public TRow Lo, Hi; // else its first place and its last, both in its range
        public TKey[] Keys = [];
        public TRow[] Rows = [];
        public int Count;
        public long Start, Total; // the view's rows before it; in all
        public TotalKind Kind; // the total's, as read
        public bool Guessed; // placed by a guess since it was read (a change to a key neither the tail nor it held)
        public long ReadAt, UsedAt; // (timestamp ticks) read from the store; last read from
        public readonly (TRow Row, long At)[] Places = new (TRow, long)[4]; // recent reads' first rows (and when): trimmed about them
        public int PlaceCount;
        public int Wanted; // the most rows a read asked of it
        public uint MaxBytes; // its last read's budget: its refresh's
        public bool Refreshing;
    }

    private sealed record Refreshing(StoreAndStream<TKey, TRow, TStamp> Source, View View, Window Window, TStamp? Began, TRow[] Rows);

    /// <summary>A view asked for: opened by the catch-up, its reads waiting.</summary>
    private sealed class Opening(GridView view)
    {
        public readonly TaskCompletionSource<View> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly GridView View = view;
        public View? Opened;
        public IComparer<TRow>? Order;
        public Exception? Refused;
        public IGridScope<TKey, TRow>? Scope;
    }

    private sealed class Unwatch(StoreAndStream<TKey, TRow, TStamp> source, Action changed) : IDisposable
    {
        public void Dispose()
        {
            lock (source._gate) source._watching = [.. source._watching.Where(w => w != changed)];
        }
    }

    private sealed class Asker(StoreAndStream<TKey, TRow, TStamp> source) : IThreadPoolWorkItem
    {
        public void Execute()
        {
            _ = source.AskAsync();
        }
    }

    private sealed class Unkeep(StoreAndStream<TKey, TRow, TStamp> source, long reach) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (source._gate)
            {
                source._keeps.Remove(reach);
                source._keep = 0;
                foreach (var keep in source._keeps) source._keep = Math.Max(source._keep, keep);
                if (source._keep == 0) (source._log, source._logStart, source._logCount) = ([], 0, 0); // (no view needs history: let go)
            }
        }
    }
}
