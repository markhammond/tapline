using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tapline.Grid.Provider;

/// <summary>An order, as a test grid shows it: worked out from its id and a seed, or as last updated.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct GridOrderRow
{
    public long Placed; // µs since 1970
    public long Price; // scaled by 100
    public int Qty;
    public int Version;
    public byte Side; // 0 buy, 1 sell
    public byte Status; // 0 to 3
}

/// <summary>A test grid's filter: every condition holds, a column against a value ('&lt;', '=' or '&gt;').</summary>
internal sealed record GridFilter(params (string Column, char Op, long Value)[] All);

/// <summary>
///     A computed grid source: orders whose fields are a function of their id and a seed, newest first in its own
///     order, with arrivals, updates and deletes; any sort of its columns, any filter of them. Each read is answered
///     from the whole view sorted afresh after a change (a few thousand rows at most): nothing allocated while nothing
///     changes. Thread-safe: a session reads on its loop while a test changes it.
/// </summary>
internal sealed class ComputedGrid(int seed, long rows) : IGridSource<long, GridOrderRow>
{
    private readonly Dictionary<long, GridOrderRow> _changed = [];
    private readonly HashSet<long> _deleted = [];
    private readonly Lock _gate = new();
    private readonly List<Action> _watchers = [];
    private long _next = rows;
    private long[] _sorted = [];
    private long _sortedAt = -1;
    private GridView? _sortedFor;
    private long _version;
    public int Reads;

    /// <summary>The memory budget the last read carried (<see cref="GridRead{TKey,TRow}.MaxBytes" />).</summary>
    public uint Budget;

    /// <summary>Says its own order is by id, greatest first (as it is): the grid's live top then goes without keys.</summary>
    public bool ByKey { get; init; }

    public long Version => Volatile.Read(ref _version);

    public bool NewestFirstByKey(GridView view)
    {
        return ByKey && view.Sort.Count == 0;
    }

    public ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<long, GridOrderRow> read,
        GridPage<long, GridOrderRow> page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Reads);
        Budget = read.MaxBytes;
        lock (_gate)
        {
            var sorted = Sorted(view);
            int start, at;
            long count;
            if (read.Kind == GridReadKind.AtRank)
            {
                start = (int)Math.Min(read.Rank, sorted.Length);
                at = start;
                count = read.Count;
            }
            else
            {
                if (read.Kind == GridReadKind.AroundKey &&
                    !(HasUnlocked(read.Key) && Passes(view, read.Key, RowUnlocked(read.Key))))
                    return new ValueTask<GridAnswer>(new GridAnswer
                        { Anchor = GridAnswer.NotFound, Total = sorted.Length, Version = _version });
                var row = read.Kind == GridReadKind.AroundKey ? RowUnlocked(read.Key) : read.Row;
                at = LowerBound(sorted, view, read.Key, row); // (where it sorts, by its row as given)
                start = Math.Max(0, at - read.Before);
                count = at - start + (long)read.Count;
            }

            var end = (int)Math.Min(sorted.Length, start + count);
            for (var i = start; i < end; i++) page.Add(sorted[i], RowUnlocked(sorted[i]));
            return new ValueTask<GridAnswer>(new GridAnswer
            {
                FirstRank = start, Anchor = at - start, Total = sorted.Length, TotalKind = TotalKind.Exact,
                Version = _version
            });
        }
    }

    public IDisposable Watch(Action changed)
    {
        lock (_watchers) _watchers.Add(changed);
        return new Unwatch(this, changed);
    }

    public static GridOrderRow Computed(long id, int seed)
    {
        var h = unchecked((ulong)id * 0x9E3779B97F4A7C15UL ^ (ulong)seed * 0x632BE59BD9B4E019UL);
        h ^= h >> 31;
        h *= 0xD6E8FEB86659FD93;
        h ^= h >> 29;
        return new GridOrderRow
        {
            Placed = 1_760_000_000_000_000 + id * 1_000, Price = 10_000 + (long)(h % 5_000),
            Qty = (int)((h >> 16) % 1_000), Side = (byte)((h >> 32) & 1), Status = (byte)((h >> 40) % 4)
        };
    }

    public GridOrderRow RowOf(long id)
    {
        lock (_gate) return RowUnlocked(id);
    }

    public bool Has(long id)
    {
        lock (_gate) return HasUnlocked(id);
    }

    private GridOrderRow RowUnlocked(long id)
    {
        return _changed.TryGetValue(id, out var row) ? row : Computed(id, seed);
    }

    private bool HasUnlocked(long id)
    {
        return id >= 0 && id < _next && !_deleted.Contains(id);
    }

    /// <summary>Its rows' ids, newest first.</summary>
    public long[] Ids()
    {
        lock (_gate)
        {
            var ids = new List<long>();
            for (var id = _next - 1; id >= 0; id--)
                if (!_deleted.Contains(id))
                    ids.Add(id);
            return [.. ids];
        }
    }

    public void Arrive(int n)
    {
        lock (_gate) _next += n;
        Changed();
    }

    public void Update(long id, int qty)
    {
        lock (_gate)
        {
            var row = _changed.TryGetValue(id, out var was) ? was : Computed(id, seed);
            row.Qty = qty;
            row.Version++;
            _changed[id] = row;
        }

        Changed();
    }

    public void Delete(long id)
    {
        lock (_gate) _deleted.Add(id);
        Changed();
    }

    public static long Value(in GridOrderRow row, long id, string column)
    {
        return column switch
        {
            "placed" => row.Placed,
            "price" => row.Price,
            "qty" => row.Qty,
            "version" => row.Version,
            "side" => row.Side,
            "status" => row.Status,
            _ => id
        };
    }

    private void Changed()
    {
        Interlocked.Increment(ref _version);
        Action[] watchers;
        lock (_watchers) watchers = [.. _watchers];
        foreach (var watcher in watchers) watcher();
    }

    private long[] Sorted(GridView view)
    {
        if (ReferenceEquals(view, _sortedFor) && _sortedAt == _version) return _sorted;
        var ids = new List<long>();
        for (var id = _next - 1; id >= 0; id--)
            if (!_deleted.Contains(id) && Passes(view, id, RowUnlocked(id)))
                ids.Add(id);
        var sorted = ids.ToArray();
        Array.Sort(sorted, (a, b) => Compare(view, a, RowUnlocked(a), b, RowUnlocked(b)));
        (_sorted, _sortedFor, _sortedAt) = (sorted, view, _version);
        return sorted;
    }

    private int LowerBound(long[] sorted, GridView view, long key, in GridOrderRow row)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (Compare(view, sorted[middle], RowUnlocked(sorted[middle]), key, row) < 0)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static int Compare(GridView view, long a, in GridOrderRow ra, long b, in GridOrderRow rb)
    {
        foreach (var sort in view.Sort)
        {
            var c = Value(ra, a, sort.Column).CompareTo(Value(rb, b, sort.Column));
            if (c != 0) return sort.Descending ? -c : c;
        }

        return b.CompareTo(a); // newest first
    }

    private static bool Passes(GridView view, long id, in GridOrderRow row)
    {
        if (view.Filter is not GridFilter filter) return true;
        foreach (var (column, op, value) in filter.All)
        {
            var v = Value(row, id, column);
            if (op == '<' ? v >= value : op == '>' ? v <= value : v != value) return false;
        }

        return true;
    }

    private sealed class Unwatch(ComputedGrid grid, Action changed) : IDisposable
    {
        public void Dispose()
        {
            lock (grid._watchers) grid._watchers.Remove(changed);
        }
    }
}

/// <summary>
///     A grid source of any size in its own order, newest first, a row a function of its id: arrivals only, nothing
///     held per row (50 M rows cost nothing). Reads from a rank or about a row; it allocates nothing.
/// </summary>
internal sealed class NaturalGrid<TRow>(long rows, Func<long, TRow> row) : IGridSource<long, TRow>
    where TRow : unmanaged
{
    private readonly List<Action> _watchers = [];
    private long _next = rows;
    public int Reads;

    public long Version => Volatile.Read(ref _next);

    public ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<long, TRow> read, GridPage<long, TRow> page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Reads);
        var next = Volatile.Read(ref _next);
        long at = read.Kind == GridReadKind.AtRank ? Math.Min(read.Rank, next) : next - 1 - read.Key;
        var start = read.Kind == GridReadKind.AtRank ? at : Math.Max(0, at - read.Before);
        var end = Math.Min(next, at + read.Count);
        for (var rank = start; rank < end; rank++) page.Add(next - 1 - rank, row(next - 1 - rank));
        return new ValueTask<GridAnswer>(new GridAnswer
            { FirstRank = start, Anchor = (int)(at - start), Total = next, TotalKind = TotalKind.Exact, Version = next });
    }

    public IDisposable Watch(Action changed)
    {
        lock (_watchers) _watchers.Add(changed);
        return new Unwatch(this, changed);
    }

    public void Arrive(int n)
    {
        Interlocked.Add(ref _next, n);
        Action[] watchers;
        lock (_watchers) watchers = [.. _watchers];
        foreach (var watcher in watchers) watcher();
    }

    private sealed class Unwatch(NaturalGrid<TRow> grid, Action changed) : IDisposable
    {
        public void Dispose()
        {
            lock (grid._watchers) grid._watchers.Remove(changed);
        }
    }
}

/// <summary>
///     A computed grid with nested rows: <see cref="ComputedGrid" />'s orders are the view's rows, and under each a run — its
///     descendants, depth first — worked out from its id: none under a fifth of them, a small tree of up to four levels
///     under the rest, a chain thirty-two deep under <see cref="Deep" />, a million children under <see cref="Big" />. Runs
///     grow at their head, as the newest fills come first, and shrink there. A run's rows have keys from 2^40 up, none an
///     order's. Thread-safe: a session reads on its loop while a test changes it.
/// </summary>
internal sealed class TreeGrid(int seed, long rows) : IGridSource<long, GridOrderRow>
{
    public const long Deep = 7, Big = 11, Million = 1_000_000;
    private const long Small = 1L << 40, Huge = 1L << 50, Grown = 1L << 52; // (run keys: a small tree's, the million's, rows grown)
    private readonly Dictionary<long, List<long>> _grown = []; // rows a run grew by, newest first
    private readonly Dictionary<long, long> _grownOwner = [];
    private readonly Lock _gate = new();
    private readonly Dictionary<long, (long Key, byte Depth)[]> _trees = [];
    private readonly Dictionary<long, long> _shrunk = []; // rows a run lost at its head, of those it had
    private readonly List<Action> _watchers = [];
    private long _nextGrown, _version;
    public int RunReads, RunRows;

    /// <summary>The memory budget the last run read carried (<see cref="GridRunRead{TKey}.MaxBytes" />).</summary>
    public uint RunBudget;

    public ComputedGrid Top { get; } = new(seed, rows) { ByKey = true };

    public long Version => Top.Version + Volatile.Read(ref _version);

    public bool NewestFirstByKey(GridView view)
    {
        return Top.NewestFirstByKey(view);
    }

    public ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<long, GridOrderRow> read, GridPage<long, GridOrderRow> page,
        CancellationToken cancellationToken)
    {
        return Top.ReadAsync(view, read, page, cancellationToken);
    }

    public ValueTask<GridAnswer> ReadRunsAsync(GridView view, ReadOnlyMemory<GridRunRead<long>> reads,
        GridRunPage<long, GridOrderRow> page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref RunReads);
        lock (_gate)
        {
            foreach (var read in reads.Span)
            {
                RunBudget = read.MaxBytes;
                var length = Length(read.Owner);
                long start, at = -1;
                if (read.Kind == GridRunReadKind.AtOffset)
                {
                    start = Math.Min(read.Offset, length);
                }
                else
                {
                    at = IndexOf(read.Owner, read.Key);
                    start = at < 0 ? length : Math.Max(0, at - read.Before);
                }

                var end = at < 0 ? Math.Min(length, start + read.Count) : Math.Min(length, at + read.Count);
                if (read.Kind == GridRunReadKind.AroundKey && at < 0) end = start;
                for (var i = start; i < end; i++)
                {
                    var (key, depth) = At(read.Owner, i);
                    if (!page.Add(key, RowOf(key), depth)) break;
                    RunRows++;
                }

                page.End(new GridRunAnswer
                {
                    FirstOffset = start, Anchor = read.Kind == GridRunReadKind.AroundKey ? at < 0 ? GridAnswer.NotFound : (int)(at - start) : 0,
                    Length = length, LengthKind = TotalKind.Exact
                });
            }

            return new ValueTask<GridAnswer>(new GridAnswer { Version = Version });
        }
    }

    public IDisposable Watch(Action changed)
    {
        lock (_watchers) _watchers.Add(changed);
        var top = Top.Watch(changed);
        return new Unwatch(this, changed, top);
    }

    /// <summary>A run's row, worked out from its key.</summary>
    public static GridOrderRow RowOf(long key)
    {
        return ComputedGrid.Computed(key, 99) with { Placed = key };
    }

    /// <summary>An owner's run as it stands: its keys and depths, depth first (the million's whole: for a test's oracle).</summary>
    public (long Key, byte Depth)[] Run(long owner)
    {
        lock (_gate)
        {
            var length = Length(owner);
            var run = new (long, byte)[length];
            for (var i = 0; i < length; i++) run[i] = At(owner, i);
            return run;
        }
    }

    public long LengthOf(long owner)
    {
        lock (_gate) return Length(owner);
    }

    /// <summary>The run's row at <paramref name="offset" />.</summary>
    public (long Key, byte Depth) RunAt(long owner, long offset)
    {
        lock (_gate) return At(owner, offset);
    }

    /// <summary>A run's row's offset in it (-1: not its).</summary>
    public long OffsetOf(long owner, long key)
    {
        lock (_gate) return IndexOf(owner, key);
    }

    /// <summary>The owner of a run's row, by its key (-1: an order's own).</summary>
    public long OwnerOf(long key)
    {
        lock (_gate)
            return key >= Grown ? _grownOwner.GetValueOrDefault(key, -1) : key >= Huge ? Big : key >= Small ? (key - Small) >> 12 : -1;
    }

    /// <summary>The run grows by <paramref name="n" /> children at its head, the newest first.</summary>
    public void Grow(long owner, int n)
    {
        lock (_gate)
        {
            if (!_grown.TryGetValue(owner, out var grown)) _grown[owner] = grown = [];
            for (var i = 0; i < n; i++)
            {
                var key = Grown + _nextGrown++;
                grown.Insert(0, key);
                _grownOwner[key] = owner;
            }
        }

        Changed();
    }

    /// <summary>The run loses its first <paramref name="n" /> rows (those grown first, then its own).</summary>
    public void Shrink(long owner, int n)
    {
        lock (_gate)
            for (var i = 0; i < n && Length(owner) > 0; i++)
                if (_grown.TryGetValue(owner, out var grown) && grown.Count > 0) grown.RemoveAt(0);
                else _shrunk[owner] = _shrunk.GetValueOrDefault(owner) + 1;
        Changed();
    }

    private long Length(long owner)
    {
        var grown = _grown.TryGetValue(owner, out var g) ? g.Count : 0;
        var own = owner == Big ? Million : Tree(owner).Length;
        return grown + own - _shrunk.GetValueOrDefault(owner);
    }

    private (long Key, byte Depth) At(long owner, long i)
    {
        if (_grown.TryGetValue(owner, out var grown) && i < grown.Count) return (grown[(int)i], (byte)1);
        i += _shrunk.GetValueOrDefault(owner) - (grown?.Count ?? 0);
        return owner == Big ? (Huge + i, (byte)1) : Tree(owner)[i];
    }

    private long IndexOf(long owner, long key)
    {
        var g = _grown.TryGetValue(owner, out var grown) ? grown.Count : 0;
        if (key >= Grown) return grown?.IndexOf(key) ?? -1;
        var lost = _shrunk.GetValueOrDefault(owner);
        long own;
        if (owner == Big) own = key >= Huge && key < Huge + Million ? key - Huge : -1;
        else own = Array.FindIndex(Tree(owner), r => r.Key == key);
        return own < lost ? -1 : g + own - lost;
    }

    /// <summary>An order's own tree, depth first: generated from its id, kept.</summary>
    private (long Key, byte Depth)[] Tree(long owner)
    {
        if (_trees.TryGetValue(owner, out var tree)) return tree;
        var rnd = new Random(unchecked(seed * 1_000_003 + (int)owner)); // (not HashCode: the same trees every run)
        var rows = new List<(long, byte)>();
        if (owner == Deep)
        {
            for (var depth = 1; depth <= GridRuns.MaxDepth; depth++) rows.Add((Small + (owner << 12) + rows.Count, (byte)depth));
            for (var i = 0; i < 3; i++) rows.Add((Small + (owner << 12) + rows.Count, 1));
        }
        else if (owner >= 0 && rnd.Next(5) > 0)
        {
            void Node(int depth)
            {
                rows.Add((Small + (owner << 12) + rows.Count, (byte)depth));
                if (depth < 4 && rows.Count < 40)
                    for (var n = rnd.Next(depth == 1 ? 3 : 2); n > 0; n--) Node(depth + 1);
            }

            for (var n = rnd.Next(1, 4); n > 0; n--) Node(1);
        }

        return _trees[owner] = [.. rows];
    }

    private void Changed()
    {
        Interlocked.Increment(ref _version);
        Action[] watchers;
        lock (_watchers) watchers = [.. _watchers];
        foreach (var watcher in watchers) watcher();
    }

    private sealed class Unwatch(TreeGrid grid, Action changed, IDisposable top) : IDisposable
    {
        public void Dispose()
        {
            lock (grid._watchers) grid._watchers.Remove(changed);
            top.Dispose();
        }
    }
}

/// <summary>Text of a declared width, as a row carries it (UTF-8, cut at a character's boundary).</summary>
[InlineArray(24)]
internal struct Text24
{
    private byte _first;
}

[InlineArray(16)]
internal struct Text16
{
    private byte _first;
}

/// <summary>A row of the blotter's orders, as the grid would carry it: its columns at their declared widths (136 B).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct BlotterRow
{
    public long OrderTime;
    public int Version;
    public Text24 ClOrdId, Client;
    public byte Region, Channel;
    public Text16 Instrument;
    public byte Side;
    public long Qty, FilledQty, AvgPrice, FilledAmt, MarkupBps;
    public byte Status;
    public long EventTime, Reconciled;
}

/// <summary>
///     A clock moved by hand, its timers firing as it passes their time (on the mover's thread): nanosecond timestamps,
///     so a rule's boundary can be tested a microsecond either side.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _now = 1_000_000_000_000;

    public override long TimestampFrequency => 1_000_000_000;

    public override long GetTimestamp()
    {
        return Volatile.Read(ref _now);
    }

    public override DateTimeOffset GetUtcNow()
    {
        return DateTimeOffset.UnixEpoch.AddTicks(Volatile.Read(ref _now) / 100);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on, firing each timer due by then.</summary>
    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref _now, by.Ticks * 100);
        while (true)
        {
            ManualTimer? due = null;
            lock (_timers)
                foreach (var t in _timers)
                    if (t.DueAt >= 0 && t.DueAt <= _now)
                    {
                        due = t;
                        break;
                    }

            if (due is null) return;
            due.DueAt = -1;
            due.Fire();
        }
    }

    /// <summary>When the timer next fires (null: not set).</summary>
    public long? NextDue()
    {
        lock (_timers)
        {
            long? next = null;
            foreach (var t in _timers)
                if (t.DueAt >= 0 && (next is null || t.DueAt < next))
                    next = t.DueAt;
            return next;
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long DueAt = -1;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            bool now;
            lock (clock._timers)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? -1 : clock._now + dueTime.Ticks * 100;
                now = dueTime != Timeout.InfiniteTimeSpan && dueTime <= TimeSpan.Zero;
                if (now) DueAt = -1;
            }

            if (now) ThreadPool.QueueUserWorkItem(static t => t.Fire(), this, false); // (due already: as a real timer, soon)
            return true;
        }

        public void Dispose()
        {
            lock (clock._timers)
            {
                DueAt = -1;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        public void Fire()
        {
            callback(state);
        }
    }
}
