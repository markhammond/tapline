using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tapline.Grid.Provider;

/// <summary>How a wait is shown to be still waiting when its deadline comes: a rule's policy (grid-waits.md (b)).</summary>
public enum GridWaitProof : byte
{
    /// <summary>
    ///     At the deadline the store is asked: its newest stamp (every version a haircut behind it has landed), then the keys
    ///     still in question looked up. A wait the store is found to have ended is not reported.
    /// </summary>
    Confirmed = 0,

    /// <summary>
    ///     On what the last proof found, at most <see cref="GridWaitsOptions.ProveEvery" /> old: sooner, the store not on the
    ///     deadline's path, but a row the store took since that proof may be reported.
    /// </summary>
    Trusted = 1
}

/// <summary>Reads the timestamp a wait runs from out of a version: another the record carries than its stamp (grid-waits.md (a)).</summary>
public delegate TStamp GridWaitAnchor<TRow, TStamp>(in TRow row) where TRow : unmanaged;

/// <summary>
///     Waits watched: over a threshold, proved by a policy, from an anchor (none: the version's own stamp). One of an alert
///     evaluator's wait triggers in force, each distinct.
/// </summary>
public readonly record struct GridWaitWatch<TRow, TStamp>(
    TimeSpan Over,
    GridWaitProof Proof = GridWaitProof.Confirmed,
    GridWaitAnchor<TRow, TStamp>? From = null) where TRow : unmanaged;

/// <summary>
///     A wait past a watch's threshold: the key, its newest version heard (what a condition reads), the watch (its place in
///     the set), and how long the key has waited, from its anchor. <see cref="Newer" />: told again, a newer version heard
///     while it waits.
/// </summary>
public readonly record struct GridOverdue<TKey, TRow>(TKey Key, TRow Newest, int Watch, TimeSpan Waited, bool Newer)
    where TKey : unmanaged where TRow : unmanaged;

/// <summary>A wait ended: the store holds the newest version heard (<see cref="Landed" />), or let go past the longest threshold.</summary>
public readonly record struct GridWaitEnded<TKey>(TKey Key, bool Landed) where TKey : unmanaged;

/// <summary>Told what <see cref="GridWaits{TKey,TRow,TStamp}" /> finds: by its passes, one call at a time.</summary>
public interface IGridWaitSink<TKey, TRow> where TKey : unmanaged where TRow : unmanaged
{
    /// <summary>
    ///     Waits past a watch's threshold, proved as the watch asks: each key and watch once a wait, and again (as
    ///     <see cref="GridOverdue{TKey,TRow}.Newer" />) when a newer version of a key past a threshold is heard while it waits.
    /// </summary>
    void Overdue(ReadOnlySpan<GridOverdue<TKey, TRow>> overdue);

    /// <summary>Waits ended: landed, or let go once past every threshold, nothing more to report for them.</summary>
    void Ended(ReadOnlySpan<GridWaitEnded<TKey>> ended);

    /// <summary>Keys not tracked since last told: heard past <see cref="GridWaitsOptions.MaxWaits" /> (grid-waits.md (d)).</summary>
    void Untracked(long keys);
}

/// <summary>Bounds and cadences of <see cref="GridWaits{TKey,TRow,TStamp}" />.</summary>
public sealed record GridWaitsOptions
{
    public static GridWaitsOptions Default { get; } = new();

    /// <summary>
    ///     The most keys waiting at once: past it, the oldest are kept (those nearest their thresholds) and new ones not
    ///     tracked, the sink told how many (grid-waits.md (d)). A wait costs its entry and the versions it waits on, rows and
    ///     all (<see cref="GridWaits{TKey,TRow,TStamp}.Memory" />).
    /// </summary>
    public int MaxWaits { get; init; } = 100_000;

    /// <summary>
    ///     How often, at most, every tracked wait is proved while any is: the store's newest stamp (one row), and, while a
    ///     <see cref="GridWaitProof.Trusted" /> watch is in force, the keys still in question looked up. Never with nothing
    ///     tracked.
    /// </summary>
    public TimeSpan ProveEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A proof that failed is tried again this long after: its confirmed waits deferred, never reported unproved.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The most keys one lookup asks the store for (<see cref="IGridStore{TKey,TRow,TStamp}.EarlierAsync" />).</summary>
    public int LookupBatch { get; init; } = 1_024;

    /// <summary>The memory a lookup may take at the store (0: the store's own bound).</summary>
    public uint MaxReadBytes { get; init; }

    internal string? Broken()
    {
        if (MaxWaits < 1) return $"At most {MaxWaits} waits can track none (GridWaitsOptions.MaxWaits).";
        if (ProveEvery <= TimeSpan.Zero || RetryAfter <= TimeSpan.Zero)
            return "A proof's cadence must be positive (GridWaitsOptions.ProveEvery, RetryAfter).";
        if (LookupBatch < 1) return $"A lookup of {LookupBatch} keys asks for none (GridWaitsOptions.LookupBatch).";
        return null;
    }
}

/// <summary>
///     Waits for the store, an alert evaluator's (grid-waits.md §3): for each key heard on the stream that the store hasn't
///     been seen to hold, from the anchor of its oldest version not landed until the store holds its newest — a newer
///     landing ends an older version's wait, an older landing alone moves the start to the next — reported as each passes a
///     watch's threshold, proved first as the watch asks.
/// </summary>
/// <remarks>
///     <para>
///         The evaluator owns it, and feeds it what it hears (<see cref="Heard" />): the rows a wait rule may watch. It reads
///         the store itself, beside the grids (grid-waits.md (c)), through the same contract: the store's promise first — a
///         version stamped a haircut behind its newest stamp has landed (<see cref="AppendOnlyStorePolicy.Haircut" />) — then a
///         lookup by key for the rest, one statement a batch.
///     </para>
///     <para>
///         Deadlines, not ticks: a wait is kept as its versions (its start worked out from them, never as a value that
///         grows), and each key has one deadline, the soonest of its watches' thresholds not yet passed, in a heap that knows
///         each key's place (moved, or taken out as the wait ends), on one timer armed for the soonest of all. Nothing runs
///         while nothing is due. A key is tracked until it lands, or until it has passed every threshold, when it is let go: a
///         newer version of it heard after starts a wait of its own. With no watch, nothing is kept.
///     </para>
///     <para>
///         A pass runs where it's asked for, one at a time: a deadline's on the timer's thread, one what's heard asks for (a
///         newer version to tell, keys not tracked) on the thread pool, never the stream's. Heard allocates nothing once
///         warm, and nor does a pass while the store answers at once (compiled optimised).
///     </para>
/// </remarks>
public sealed class GridWaits<TKey, TRow, TStamp> : IAsyncDisposable
    where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TStamp : struct, IComparable<TStamp>
{
    private const int MostWatches = 64; // (a key's watches told: one bit each)

    private readonly long[] _below;
    private readonly Dictionary<TKey, int> _byKey = [];
    private readonly List<(int Entry, int Serial)> _candidates = []; // the passes': keys due this pass
    private readonly List<GridWaitEnded<TKey>> _ended = []; // the passes'
    private readonly Lock _gate = new();
    private readonly TimeSpan _haircut;
    private readonly TKey[] _lookupKeys;
    private readonly List<int> _newer = [];
    private readonly GridWaitsOptions _options;
    private readonly List<GridOverdue<TKey, TRow>> _overdue = []; // the passes'
    private readonly GridPage<TKey, TRow> _page;
    private readonly List<(int Entry, int Serial)> _question = []; // the passes': keys a proof looks up
    private readonly IGridRows<TKey, TRow, TStamp> _rows;
    private readonly Runner _runner;
    private readonly IGridWaitSink<TKey, TRow> _sink;
    private readonly CancellationTokenSource _stop = new();
    private readonly IGridStore<TKey, TRow, TStamp> _store;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private long _armedFor = long.MaxValue; // when the timer is set to fire (a timestamp; MaxValue: not set)
    private bool _disposed;
    private Entry[] _entries = new Entry[16];
    private int _free = -1, _used, _live;
    private int[] _heap = new int[16]; // the keys with a deadline, soonest first, each entry knowing its place
    private int _heapCount;
    private TaskCompletionSource? _idle; // (asked for: completed once the passes are done)
    private long _nextProof; // when every tracked wait is next proved (a timestamp; 0: none)
    private bool _running, _woken; // passes under way; another asked for
    private bool _trusted; // a trusted watch in force
    private long _untracked; // keys not tracked since the sink was told
    private Pending[] _versions = new Pending[16]; // every key's versions waited on, chained from its entry
    private int _freeVersion = -1, _usedVersions, _pending; // (_pending: the versions held)
    private GridWaitWatch<TRow, TStamp>[] _watches = [];

    /// <param name="store">The store waited for: its newest stamp and its rows by key, and its haircut.</param>
    /// <param name="rows">What a row is: its key, version and stamp, and how stamps and spans relate.</param>
    /// <param name="sink">Told what is found, by the passes.</param>
    public GridWaits(IGridStore<TKey, TRow, TStamp> store, IGridRows<TKey, TRow, TStamp> rows, IGridWaitSink<TKey, TRow> sink,
        GridWaitsOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? GridWaitsOptions.Default;
        if (_options.Broken() is { } broken) throw new ArgumentException(broken, nameof(options));
        (_store, _rows, _sink, _time) = (store, rows, sink, time ?? TimeProvider.System);
        _haircut = store.Policy.Haircut;
        _lookupKeys = new TKey[_options.LookupBatch];
        _below = new long[_options.LookupBatch];
        Array.Fill(_below, long.MaxValue); // (each key's newest the store holds)
        _page = new GridPage<TKey, TRow>(_options.LookupBatch);
        _runner = new Runner(this);
        _timer = _time.CreateTimer(static state => ((GridWaits<TKey, TRow, TStamp>)state!).Wake(true), this, Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>Keys waiting, tracked.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _live;
        }
    }

    /// <summary>
    ///     What its waits keep, beside the most <see cref="GridWaitsOptions.MaxWaits" /> allows (a version each): each key's
    ///     entry, its place in the map and the heap, and the versions it waits on.
    /// </summary>
    public GridMemory Memory
    {
        get
        {
            long entry = Unsafe.SizeOf<Entry>() + Unsafe.SizeOf<TKey>() + 20, version = Unsafe.SizeOf<Pending>();
            lock (_gate) return new GridMemory(_live * entry + _pending * version, _options.MaxWaits * (entry + version));
        }
    }

    /// <summary>The last failure, a proof the store refused or the sink throwing, until a proof succeeds (null: none).</summary>
    public Exception? Fault { get; private set; }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _timer.DisposeAsync().ConfigureAwait(false);
        await IdleAsync().ConfigureAwait(false); // (a pass under way: its store call cancelled)
        _stop.Dispose();
    }

    /// <summary>
    ///     The watches in force — the evaluator's distinct wait triggers — replacing those before: a watch kept keeps what was
    ///     told of it, a new one starts on every key waiting. None: nothing kept, every wait let go untold.
    /// </summary>
    public void SetWatches(ReadOnlySpan<GridWaitWatch<TRow, TStamp>> watches)
    {
        if (watches.Length > MostWatches)
            throw new ArgumentException($"At most {MostWatches} watches at once: {watches.Length} asked for.", nameof(watches));
        foreach (var watch in watches)
            if (watch.Over <= TimeSpan.Zero)
                throw new ArgumentException($"A wait over {watch.Over} is no wait: a threshold must be positive.", nameof(watches));
        bool wake;
        lock (_gate)
        {
            if (_disposed) return;
            var was = _watches;
            _watches = watches.ToArray();
            _trusted = false;
            foreach (var watch in _watches) _trusted |= watch.Proof == GridWaitProof.Trusted;
            var nowTs = _time.GetTimestamp();
            if (_watches.Length == 0)
            {
                for (var i = 0; i < _used; i++)
                    if (_entries[i].Live)
                        Free(i);
                _newer.Clear();
                _nextProof = 0;
            }
            else
            {
                // Each key's watches told, by their place in the new set; then its deadline worked out afresh.
                Span<int> moved = stackalloc int[was.Length];
                for (var i = 0; i < was.Length; i++) moved[i] = Array.IndexOf(_watches, was[i]);
                var nowStamp = Now();
                for (var i = 0; i < _used; i++)
                {
                    ref var e = ref _entries[i];
                    if (!e.Live) continue;
                    ulong told = 0;
                    for (var w = 0; w < was.Length; w++)
                        if ((e.Told & (1UL << w)) != 0 && moved[w] >= 0)
                            told |= 1UL << moved[w];
                    e.Told = told;
                    Unschedule(i);
                    Schedule(i, nowTs, nowStamp);
                }
            }

            wake = Arm(nowTs);
        }

        if (wake) Wake(false);
    }

    /// <summary>
    ///     Versions heard on the stream — those a wait rule may watch — in the order sent: each the store hasn't been seen to
    ///     hold starts its key's wait, or joins it. Allocates nothing once warm.
    /// </summary>
    public void Heard(ReadOnlySpan<TRow> rows)
    {
        var wake = false;
        lock (_gate)
        {
            if (_watches.Length == 0 || _disposed) return;
            var timed = false;
            long nowTs = 0;
            TStamp nowStamp = default;
            foreach (ref readonly var row in rows)
            {
                var key = _rows.KeyOf(row);
                var version = _rows.VersionOf(row);
                if (!_byKey.TryGetValue(key, out var at))
                {
                    if (_live >= _options.MaxWaits)
                    {
                        if (_untracked++ == 0) wake = true; // (the sink told by the next pass)
                        continue;
                    }

                    at = Take(key);
                }

                ref var e = ref _entries[at];
                if (version <= e.Landed || Holds(e.First, version)) continue; // (the store holds it, or newer; or heard already)
                Hold(ref e, version, row);
                if (version > e.NewestVersion)
                {
                    e.NewestVersion = version;
                    if (e.Told != 0 && !e.Newer)
                    {
                        e.Newer = true;
                        _newer.Add(at);
                        wake = true;
                    }
                }

                // Its deadline worked out, kept if sooner: a new wait's, or an older version's than the wait began with.
                if (!timed) (timed, nowTs, nowStamp) = (true, _time.GetTimestamp(), Now());
                if (_nextProof == 0) _nextProof = nowTs + Timestamps(_options.ProveEvery);
                Schedule(at, nowTs, nowStamp);
            }

            if (timed) wake |= Arm(nowTs);
        }

        if (wake) Wake(false);
    }

    /// <summary>A task done once no pass is under way or asked for (the tests' way to wait for one).</summary>
    internal Task IdleAsync()
    {
        lock (_gate)
            return _running ? (_idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.CompletedTask;
    }

    /// <summary>A pass asked for: taken by the passes under way, or begun — here (the timer's thread), or on the thread pool.</summary>
    private void Wake(bool here)
    {
        lock (_gate)
        {
            _woken = true;
            if (_running) return;
            _running = true;
        }

        if (here) _ = RunAsync();
        else ThreadPool.UnsafeQueueUserWorkItem(_runner, false); // (not on the stream's thread)
    }

    /// <summary>Passes while one is asked for, then idle.</summary>
    private async Task RunAsync()
    {
        while (true)
        {
            TaskCompletionSource? idle = null;
            bool pass;
            lock (_gate)
            {
                pass = _woken && !_disposed;
                if (pass) _woken = false;
                else (_running, _woken, idle, _idle) = (false, false, _idle, null);
            }

            if (!pass)
            {
                idle?.TrySetResult();
                return;
            }

            try
            {
                await PassAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // (disposed: the next turn ends them)
            }
            catch (Exception failure)
            {
                Fault = failure; // (the sink's: what the pass found already applied)
            }
        }
    }

    /// <summary>
    ///     One pass: the keys told again for a newer version, then the deadlines due — trusted watches told on what is known,
    ///     confirmed ones once proved — and, when due, every tracked wait proved; the sink told; the timer set again.
    /// </summary>
    private async Task PassAsync(CancellationToken ct)
    {
        long untracked;
        bool proveAll, lookAll;
        lock (_gate)
        {
            _armedFor = long.MaxValue; // (set again as the pass ends)
            var (nowTs, nowStamp) = (_time.GetTimestamp(), Now());
            (untracked, _untracked) = (_untracked, 0);
            foreach (var at in _newer)
            {
                ref var e = ref _entries[at];
                if (!e.Live || !e.Newer) continue;
                e.Newer = false;
                Tell(at, e.Told, nowStamp, true);
            }

            _newer.Clear();
            _candidates.Clear();
            while (_heapCount > 0 && _entries[_heap[0]].Due <= nowTs)
            {
                var at = _heap[0];
                Unschedule(at);
                _candidates.Add((at, _entries[at].Serial));
            }

            // Trusted watches due: told at once, on what the last proof found. Confirmed ones wait for the proof.
            _question.Clear();
            foreach (var (at, serial) in _candidates)
            {
                if (Due(at, nowStamp, GridWaitProof.Trusted) is var trusted and not 0) Mark(at, trusted, nowStamp);
                if (Due(at, nowStamp, GridWaitProof.Confirmed) != 0) _question.Add((at, serial));
            }

            proveAll = _live > 0 && _nextProof != 0 && _nextProof <= nowTs;
            lookAll = proveAll && _trusted;
            if (proveAll) _nextProof = nowTs + Timestamps(_options.ProveEvery);
        }

        var proved = true;
        if (_question.Count > 0 || proveAll) proved = await ProveAsync(proveAll, lookAll, ct).ConfigureAwait(false);

        bool wake;
        lock (_gate)
        {
            var (nowTs, nowStamp) = (_time.GetTimestamp(), Now());
            foreach (var (at, serial) in _candidates)
            {
                ref var e = ref _entries[at];
                if (!e.Live || e.Serial != serial) continue; // (ended by the proof)
                if (proved && Due(at, nowStamp, GridWaitProof.Confirmed) is var confirmed and not 0) Mark(at, confirmed, nowStamp);
                if (e.Told == AllWatches)
                {
                    _ended.Add(new GridWaitEnded<TKey>(e.Key, false)); // (past every threshold: nothing more to tell)
                    Free(at);
                }
                else if (!proved && Due(at, nowStamp, GridWaitProof.Confirmed) != 0)
                {
                    SetDue(at, nowTs + Timestamps(_options.RetryAfter)); // (unproved: deferred, tried again, never told so)
                }
                else
                {
                    Schedule(at, nowTs, nowStamp);
                }
            }

            if (!proved && proveAll) _nextProof = nowTs + Timestamps(_options.RetryAfter);
            if (_live == 0) _nextProof = 0;
            wake = Arm(nowTs);
        }

        try
        {
            // (the passes' own lists, as spans: the sink can't keep them past the call)
            if (_overdue.Count > 0) _sink.Overdue(CollectionsMarshal.AsSpan(_overdue));
            if (_ended.Count > 0) _sink.Ended(CollectionsMarshal.AsSpan(_ended));
            if (untracked > 0) _sink.Untracked(untracked);
        }
        finally
        {
            _overdue.Clear();
            _ended.Clear();
            if (wake) Wake(false);
        }
    }

    /// <summary>
    ///     The store asked: its newest stamp, the versions of the keys in question (<paramref name="proveAll" />: of every
    ///     tracked key) a haircut behind it landed; then the keys still in question (<paramref name="lookAll" />: every key)
    ///     looked up. False if the store failed.
    /// </summary>
    private async Task<bool> ProveAsync(bool proveAll, bool lookAll, CancellationToken ct)
    {
        TStamp? newest;
        try
        {
            newest = await _store.NewestStampAsync(ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Fault = failure;
            return false;
        }

        lock (_gate)
        {
            if (newest is { } stamp)
            {
                var cutoff = _rows.Back(stamp, _haircut);
                if (proveAll)
                {
                    for (var i = 0; i < _used; i++)
                        if (_entries[i].Live)
                            Landed(i, cutoff);
                }
                else
                {
                    foreach (var (at, serial) in _question)
                        if (_entries[at].Live && _entries[at].Serial == serial)
                            Landed(at, cutoff);
                }
            }

            if (lookAll)
            {
                _question.Clear();
                for (var i = 0; i < _used; i++)
                    if (_entries[i].Live)
                        _question.Add((i, _entries[i].Serial));
            }
        }

        for (var from = 0; from < _question.Count; from += _options.LookupBatch)
        {
            var n = 0;
            lock (_gate)
                for (var i = from; i < Math.Min(from + _options.LookupBatch, _question.Count); i++)
                {
                    var (at, serial) = _question[i];
                    if (_entries[at].Live && _entries[at].Serial == serial) _lookupKeys[n++] = _entries[at].Key;
                }

            if (n == 0) continue;
            _page.Clear();
            try
            {
                await _store.EarlierAsync(_lookupKeys.AsMemory(0, n), _below.AsMemory(0, n), _options.MaxReadBytes, _page, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Fault = failure;
                return false;
            }

            lock (_gate)
                for (var i = 0; i < _page.Count; i++)
                    if (_byKey.TryGetValue(_page.Keys[i], out var at))
                        Landed(at, _rows.VersionOf(_page.Rows[i]));
        }

        Fault = null;
        return true;
    }

    /// <summary>The store holds versions stamped up to <paramref name="cutoff" />: the key's versions so stamped have landed.</summary>
    private void Landed(int at, TStamp cutoff)
    {
        var landed = long.MinValue;
        for (var v = _entries[at].First; v >= 0; v = _versions[v].Next)
            if (_versions[v].Version > landed && _rows.StampOf(_versions[v].Row).CompareTo(cutoff) <= 0)
                landed = _versions[v].Version;
        if (landed != long.MinValue) Landed(at, landed);
    }

    /// <summary>
    ///     The store holds <paramref name="version" /> of the key: it and every version before it have landed. All landed, the
    ///     wait has ended.
    /// </summary>
    private void Landed(int at, long version)
    {
        ref var e = ref _entries[at];
        if (version <= e.Landed) return;
        e.Landed = version;
        int before = -1, v = e.First;
        while (v >= 0)
        {
            var next = _versions[v].Next;
            if (_versions[v].Version <= version)
            {
                if (before < 0) e.First = next;
                else _versions[before].Next = next;
                Release(v);
            }
            else
            {
                before = v;
            }

            v = next;
        }

        if (e.First >= 0) return;
        _ended.Add(new GridWaitEnded<TKey>(e.Key, true));
        Free(at);
    }

    private bool Holds(int first, long version)
    {
        for (var v = first; v >= 0; v = _versions[v].Next)
            if (_versions[v].Version == version)
                return true;
        return false;
    }

    /// <summary>The watches of a policy whose thresholds the key's wait has passed, not yet told.</summary>
    private ulong Due(int at, TStamp nowStamp, GridWaitProof proof)
    {
        ref var e = ref _entries[at];
        if (!e.Live || e.First < 0) return 0;
        ulong due = 0;
        for (var w = 0; w < _watches.Length; w++)
            if ((e.Told & (1UL << w)) == 0 && _watches[w].Proof == proof && _rows.Between(Start(at, _watches[w].From), nowStamp) >= _watches[w].Over)
                due |= 1UL << w;
        return due;
    }

    /// <summary>Watches told of the key: marked, and the sink told.</summary>
    private void Mark(int at, ulong watches, TStamp nowStamp)
    {
        _entries[at].Told |= watches;
        Tell(at, watches, nowStamp, false);
    }

    private void Tell(int at, ulong watches, TStamp nowStamp, bool newer)
    {
        ref var e = ref _entries[at];
        ref readonly var newest = ref NewestOf(e);
        for (var w = 0; w < _watches.Length; w++)
            if ((watches & (1UL << w)) != 0)
                _overdue.Add(new GridOverdue<TKey, TRow>(e.Key, newest, w, _rows.Between(Start(at, _watches[w].From), nowStamp), newer));
    }

    /// <summary>The newest version heard of the key, one of those it waits on (the wait ends as it lands): first, heard in order.</summary>
    private ref readonly TRow NewestOf(in Entry e)
    {
        var v = e.First;
        while (_versions[v].Version != e.NewestVersion && _versions[v].Next >= 0) v = _versions[v].Next;
        return ref _versions[v].Row;
    }

    /// <summary>
    ///     The key's next deadline — the soonest of its watches' thresholds not yet told; every watch told, now, to be let go —
    ///     kept if sooner than it has.
    /// </summary>
    private void Schedule(int at, long nowTs, TStamp nowStamp)
    {
        ref var e = ref _entries[at];
        var due = long.MaxValue;
        for (var w = 0; w < _watches.Length; w++)
        {
            if ((e.Told & (1UL << w)) != 0) continue;
            var left = _watches[w].Over - _rows.Between(Start(at, _watches[w].From), nowStamp);
            due = Math.Min(due, nowTs + Timestamps(left));
        }

        if (due == long.MaxValue) due = nowTs;
        if (due < e.Due) SetDue(at, due);
    }

    /// <summary>The key's deadline set, sooner or later, its place in the heap found.</summary>
    private void SetDue(int at, long due)
    {
        ref var e = ref _entries[at];
        e.Due = due;
        if (e.HeapAt >= 0)
        {
            Place(e.HeapAt, at);
            return;
        }

        if (_heapCount == _heap.Length) Array.Resize(ref _heap, 2 * _heapCount);
        Place(_heapCount++, at);
    }

    /// <summary>The key's deadline taken out of the heap: none.</summary>
    private void Unschedule(int at)
    {
        ref var e = ref _entries[at];
        var i = e.HeapAt;
        if (i < 0) return;
        (e.HeapAt, e.Due) = (-1, long.MaxValue);
        var last = _heap[--_heapCount];
        if (i < _heapCount) Place(i, last);
    }

    /// <summary>An entry put in the heap's place <paramref name="i" />, moved up or down to where its deadline has it.</summary>
    private void Place(int i, int at)
    {
        var due = _entries[at].Due;
        while (i > 0 && _entries[_heap[(i - 1) / 2]].Due > due)
        {
            Put(i, _heap[(i - 1) / 2]);
            i = (i - 1) / 2;
        }

        while (2 * i + 1 < _heapCount)
        {
            var child = 2 * i + 1;
            if (child + 1 < _heapCount && _entries[_heap[child + 1]].Due < _entries[_heap[child]].Due) child++;
            if (_entries[_heap[child]].Due >= due) break;
            Put(i, _heap[child]);
            i = child;
        }

        Put(i, at);
    }

    private void Put(int i, int at)
    {
        _heap[i] = at;
        _entries[at].HeapAt = i;
    }

    /// <summary>The timer set for the soonest thing due: true if it's due already (the caller wakes the passes).</summary>
    private bool Arm(long nowTs)
    {
        if (_disposed) return false;
        var next = _heapCount > 0 ? _entries[_heap[0]].Due : long.MaxValue;
        if (_live > 0 && _nextProof != 0) next = Math.Min(next, _nextProof);
        if (next <= nowTs) return true;
        if (next == _armedFor) return false;
        _armedFor = next;
        _timer.Change(next == long.MaxValue ? Timeout.InfiniteTimeSpan : Span(next - nowTs), Timeout.InfiniteTimeSpan);
        return false;
    }

    /// <summary>When the key's wait began, from a watch's anchor: the soonest anchor of the versions it waits on.</summary>
    private TStamp Start(int at, GridWaitAnchor<TRow, TStamp>? from)
    {
        var v = _entries[at].First;
        var start = Anchor(_versions[v].Row, from);
        for (v = _versions[v].Next; v >= 0; v = _versions[v].Next)
            if (Anchor(_versions[v].Row, from) is var anchor && anchor.CompareTo(start) < 0)
                start = anchor;
        return start;
    }

    private TStamp Anchor(in TRow row, GridWaitAnchor<TRow, TStamp>? from)
    {
        return from is null ? _rows.StampOf(row) : from(row);
    }

    private TStamp Now()
    {
        return _rows.StampAt(_time.GetUtcNow());
    }

    private ulong AllWatches => _watches.Length == MostWatches ? ulong.MaxValue : (1UL << _watches.Length) - 1;

    private int Take(in TKey key)
    {
        int at;
        if (_free >= 0)
        {
            at = _free;
            _free = _entries[at].NextFree;
        }
        else
        {
            if (_used == _entries.Length) Array.Resize(ref _entries, 2 * _used);
            at = _used++;
        }

        ref var e = ref _entries[at];
        (e.Key, e.Live, e.Landed, e.NewestVersion, e.Told, e.Due, e.First, e.HeapAt, e.Newer) =
            (key, true, long.MinValue, long.MinValue, 0, long.MaxValue, -1, -1, false);
        e.Serial++;
        _byKey[key] = at;
        _live++;
        return at;
    }

    private void Free(int at)
    {
        ref var e = ref _entries[at];
        _byKey.Remove(e.Key);
        for (var v = e.First; v >= 0;)
        {
            var next = _versions[v].Next;
            Release(v);
            v = next;
        }

        e.First = -1;
        Unschedule(at);
        (e.Live, e.Newer) = (false, false);
        e.NextFree = _free;
        _free = at;
        _live--;
    }

    /// <summary>A version the key waits on, chained to its others.</summary>
    private void Hold(ref Entry e, long version, in TRow row)
    {
        int v;
        if (_freeVersion >= 0)
        {
            v = _freeVersion;
            _freeVersion = _versions[v].Next;
        }
        else
        {
            if (_usedVersions == _versions.Length) Array.Resize(ref _versions, 2 * _usedVersions);
            v = _usedVersions++;
        }

        _versions[v] = new Pending(version, row, e.First);
        e.First = v;
        _pending++;
    }

    private void Release(int v)
    {
        _versions[v].Next = _freeVersion;
        _freeVersion = v;
        _pending--;
    }

    private long Timestamps(TimeSpan span)
    {
        var ticks = Math.Max(span.Ticks, -TimeSpan.TicksPerDay); // (long since due: a day back is as good)
        var frequency = _time.TimestampFrequency;
        return frequency % TimeSpan.TicksPerSecond == 0
            ? ticks * (frequency / TimeSpan.TicksPerSecond)
            : (long)Math.Ceiling(ticks * ((double)frequency / TimeSpan.TicksPerSecond)); // (never early)
    }

    private TimeSpan Span(long timestamps)
    {
        var frequency = _time.TimestampFrequency;
        return TimeSpan.FromTicks((long)Math.Ceiling(timestamps * ((double)TimeSpan.TicksPerSecond / frequency)));
    }

    /// <summary>A version a key waits on, and its row (its anchors read from it): chained to the key's next, or the next free.</summary>
    private struct Pending(long version, TRow row, int next)
    {
        public long Version = version;
        public TRow Row = row;
        public int Next = next;
    }

    /// <summary>A key waiting: its newest version heard, the store's newest seen, the versions not landed, the watches told, its deadline.</summary>
    private struct Entry
    {
        public TKey Key;
        public long NewestVersion, Landed;
        public ulong Told;
        public long Due; // its deadline (a timestamp; MaxValue: none)
        public int First; // the versions it waits on, chained from the first (-1: none)
        public int HeapAt; // its place in the heap (-1: none)
        public int Serial; // (bumped as the entry is reused)
        public int NextFree;
        public bool Live, Newer;
    }

    private sealed class Runner(GridWaits<TKey, TRow, TStamp> waits) : IThreadPoolWorkItem
    {
        public void Execute()
        {
            _ = waits.RunAsync();
        }
    }
}
