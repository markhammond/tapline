using System.Runtime.CompilerServices;
using Tapline.Grid.Provider;

/// <summary>A version of an order in the append-only log: its key, version and stamp, and the columns a view sorts and filters by.</summary>
internal record struct LogRow
{
    public long Id;
    public int Version;
    public long Stamp; // µs since the Unix epoch
    public int Qty;
    public byte Status;
}

/// <summary>What a log row is, and a view's order and filter as the test store applies them: its columns, then its key, newest first.</summary>
internal sealed class LogRows : IGridRows<long, LogRow, long>
{

    public long KeyOf(in LogRow row)
    {
        return row.Id;
    }

    public long VersionOf(in LogRow row)
    {
        return row.Version;
    }

    public long StampOf(in LogRow row)
    {
        return row.Stamp;
    }

    public long Back(long stamp, TimeSpan span)
    {
        return stamp - span.Ticks / 10; // (microseconds)
    }

    public TimeSpan Between(long earlier, long later)
    {
        return TimeSpan.FromTicks((later - earlier) * 10);
    }

    public long StampAt(DateTimeOffset time)
    {
        return (time - DateTimeOffset.UnixEpoch).Ticks / 10;
    }

    public IComparer<LogRow> OrderOf(GridView view)
    {
        return new LogOrder(view);
    }

    public bool NewestFirstByKey(GridView view)
    {
        return view.Sort.Count == 0;
    }

    public static long Value(in LogRow row, string column)
    {
        return column switch { "qty" => row.Qty, "status" => row.Status, _ => row.Id };
    }

    public static bool Passes(GridView view, in LogRow row)
    {
        if (view.Filter is not GridFilter filter) return true;
        foreach (var (column, op, value) in filter.All)
        {
            var v = Value(row, column);
            if (op == '<' ? v >= value : op == '>' ? v <= value : v != value) return false;
        }

        return true;
    }
}

internal sealed class LogOrder(GridView view) : IComparer<LogRow>
{
    public int Compare(LogRow a, LogRow b)
    {
        foreach (var (column, descending) in view.Sort)
        {
            var c = LogRows.Value(a, column).CompareTo(LogRows.Value(b, column));
            if (c != 0) return descending ? -c : c;
        }

        return b.Id.CompareTo(a.Id); // (ties: the newest first)
    }
}

/// <summary>
///     An append-only store in memory: versions land when the test says (behind the stream, ahead of it, out of stamp
///     order), each key shown at the newest landed. A read can be held after taking its rows, as a slow statement is.
/// </summary>
internal sealed class LogStore(AppendOnlyStorePolicy policy) : IGridStore<long, LogRow, long>
{
    private readonly Dictionary<long, LogRow> _current = [];
    private readonly Lock _gate = new();
    private readonly List<LogRow> _log = [];
    private long _newest = long.MinValue;
    public int NewestAsks;

    /// <summary>Set, a page read takes its rows, then waits for it.</summary>
    public TaskCompletionSource? Pause;

    /// <summary>The budgets its statements were given (<see cref="GridRead{TKey,TRow}.MaxBytes" />), as made.</summary>
    public readonly List<uint> Budgets = [];

    /// <summary>Set, every statement is refused with it, as one past its bounds is.</summary>
    public string? Refuse;

    /// <summary>The sizes and places asked of it (pages short of their limit tell their own), and whether it can't say a size.</summary>
    public int Sizes, Ranks;

    /// <summary>
    ///     Set, rows are settled once all final (status 3) and its newest stamp past the newest of them by more than it: its
    ///     own clock seen to move on. Unset, none are.
    /// </summary>
    public TimeSpan? Settle;

    public bool Unsized;

    public AppendOnlyStorePolicy Policy => policy;

    public async ValueTask ReadAsync(GridView view, GridSeek<long, LogRow> seek, int limit, uint maxBytes, GridPage<long, LogRow> page,
        CancellationToken cancellationToken)
    {
        Seen(maxBytes);
        var order = new LogOrder(view);
        var sorted = Sorted(view);
        var rows = seek.Kind switch
        {
            GridSeekKind.First => sorted.Take(limit).ToArray(),
            GridSeekKind.From => sorted.Where(r => order.Compare(r, seek.Row) >= 0).Take(limit).ToArray(),
            _ => sorted.Where(r => order.Compare(r, seek.Row) < 0).Reverse().Take(limit).ToArray()
        };
        if (Pause is { } pause) await pause.Task;
        foreach (var row in rows) page.Add(row.Id, row);
    }

    public ValueTask ReadAtAsync(GridView view, long rank, int limit, uint maxBytes, GridPage<long, LogRow> page,
        CancellationToken cancellationToken)
    {
        Seen(maxBytes);
        foreach (var row in Sorted(view).Skip((int)rank).Take(limit)) page.Add(row.Id, row);
        return ValueTask.CompletedTask;
    }

    public ValueTask<(long Rows, TotalKind Kind)?> RankAsync(GridView view, long key, LogRow row, uint maxBytes,
        CancellationToken cancellationToken)
    {
        Seen(maxBytes);
        Interlocked.Increment(ref Ranks);
        var order = new LogOrder(view);
        return new ValueTask<(long, TotalKind)?>((Sorted(view).Count(r => order.Compare(r, row) < 0), TotalKind.Exact));
    }

    public ValueTask<(long Rows, TotalKind Kind)?> SizeAsync(GridView view, uint maxBytes, CancellationToken cancellationToken)
    {
        Seen(maxBytes);
        Interlocked.Increment(ref Sizes);
        return new ValueTask<(long, TotalKind)?>(Unsized ? null : (Sorted(view).Length, TotalKind.Exact));
    }

    public ValueTask<bool> SettledAsync(ReadOnlyMemory<LogRow> rows, CancellationToken cancellationToken)
    {
        if (Settle is not { } settle) return ValueTask.FromResult(false);
        var newest = long.MinValue;
        foreach (var row in rows.Span)
        {
            if (row.Status != 3) return ValueTask.FromResult(false);
            newest = Math.Max(newest, row.Stamp);
        }

        lock (_gate) return ValueTask.FromResult(TimeSpan.FromTicks((_newest - newest) * 10) > settle);
    }

    public ValueTask<long?> NewestStampAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref NewestAsks);
        lock (_gate) return new ValueTask<long?>(_newest == long.MinValue ? null : _newest);
    }

    public async IAsyncEnumerable<LogRow> SinceAsync(long stamp, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LogRow[] since;
        lock (_gate) since = [.. _log.Where(r => r.Stamp >= stamp)];
        foreach (var row in since)
        {
            await Task.Yield();
            yield return row;
        }
    }

    public ValueTask EarlierAsync(ReadOnlyMemory<long> keys, ReadOnlyMemory<long> below, uint maxBytes, GridPage<long, LogRow> page,
        CancellationToken cancellationToken)
    {
        Seen(maxBytes);
        lock (_gate)
            for (var i = 0; i < keys.Length; i++)
            {
                var (key, under) = (keys.Span[i], below.Span[i]);
                LogRow? best = null;
                foreach (var row in _log)
                    if (row.Id == key && row.Version < under && (best is null || row.Version > best.Value.Version))
                        best = row;
                if (best is { } b) page.Add(b.Id, b);
            }

        return ValueTask.CompletedTask;
    }

    /// <summary>A key's row as the store shows it.</summary>
    public LogRow Row(long id)
    {
        lock (_gate) return _current[id];
    }

    /// <summary>A key's row as the store shows it, if it has one.</summary>
    public LogRow? Find(long id)
    {
        lock (_gate) return _current.TryGetValue(id, out var row) ? row : null;
    }

    /// <summary>A version lands: shown, if it's its key's newest.</summary>
    public void Land(in LogRow row)
    {
        lock (_gate)
        {
            _log.Add(row);
            if (!_current.TryGetValue(row.Id, out var at) || at.Version < row.Version) _current[row.Id] = row;
            _newest = Math.Max(_newest, row.Stamp);
        }
    }

    private LogRow[] Sorted(GridView view)
    {
        lock (_gate) return [.. _current.Values.Where(r => LogRows.Passes(view, r)).OrderBy(r => r, new LogOrder(view))];
    }

    private void Seen(uint maxBytes)
    {
        lock (_gate) Budgets.Add(maxBytes);
        if (Refuse is { } why) throw new GridStoreRefusedException(why);
    }
}

/// <summary>The stream: each batch to its one subscriber, as sent.</summary>
internal sealed class LogStream : IGridStream<LogRow>
{
    private GridBatch<LogRow>? _batch;

    public IDisposable Subscribe(GridBatch<LogRow> batch)
    {
        _batch = batch;
        return new Ended(this);
    }

    public void Send(params LogRow[] rows)
    {
        _batch?.Invoke(rows);
    }

    private sealed class Ended(LogStream stream) : IDisposable
    {
        public void Dispose()
        {
            stream._batch = null;
        }
    }
}

/// <summary>
///     The views' filters over the tail, as the test store applies them (ChalkQL's, in a real host): each change staged,
///     then published. <paramref name="yields" />: each opening and evaluation goes on later, as a ChalkQL refresh does.
/// </summary>
internal sealed class LogScopes(bool yields = false) : IGridScopes<long, LogRow>
{
    private readonly List<LogScope> _open = [];
    public int Opened, Evaluations;

    /// <summary>Set, each opening and evaluation waits for it, as a slow refresh does.</summary>
    public TaskCompletionSource? Hold;

    /// <summary>Set, the next evaluation throws it.</summary>
    public Exception? Throw;

    public ValueTask<IGridScope<long, LogRow>> OpenAsync(GridView view, ReadOnlyMemory<GridTailRow<long, LogRow>> tail,
        CancellationToken cancellationToken)
    {
        return yields || Hold is not null ? OpenLaterAsync(view, tail, Hold) : new ValueTask<IGridScope<long, LogRow>>(Open(view, tail.Span));
    }

    public ValueTask EvaluateAsync(ReadOnlyMemory<GridTailRow<long, LogRow>> changed, ReadOnlyMemory<GridTailRow<long, LogRow>> gone,
        CancellationToken cancellationToken)
    {
        if (Throw is { } thrown)
        {
            Throw = null;
            return ValueTask.FromException(thrown);
        }

        if (yields || Hold is not null) return EvaluateLaterAsync(changed, gone, Hold);
        Evaluate(changed.Span, gone.Span);
        return ValueTask.CompletedTask;
    }

    /// <summary>Every view's filter failed, as an update past its budget does.</summary>
    public void FailAll(Exception failure)
    {
        foreach (var scope in _open) scope.Fault = failure;
    }

    public void Publish()
    {
        foreach (var scope in _open) scope.Publish();
    }

    private LogScope Open(GridView view, ReadOnlySpan<GridTailRow<long, LogRow>> tail)
    {
        var scope = new LogScope(this, view);
        scope.Stage(tail, false);
        _open.Add(scope);
        Opened++;
        return scope;
    }

    private async ValueTask<IGridScope<long, LogRow>> OpenLaterAsync(GridView view, ReadOnlyMemory<GridTailRow<long, LogRow>> tail,
        TaskCompletionSource? hold)
    {
        if (hold is not null) await hold.Task;
        else await Task.Yield();
        return Open(view, tail.Span);
    }

    private void Evaluate(ReadOnlySpan<GridTailRow<long, LogRow>> changed, ReadOnlySpan<GridTailRow<long, LogRow>> gone)
    {
        foreach (var scope in _open)
        {
            scope.Stage(changed, false);
            scope.Stage(gone, true);
        }

        Evaluations++;
    }

    private async ValueTask EvaluateLaterAsync(ReadOnlyMemory<GridTailRow<long, LogRow>> changed,
        ReadOnlyMemory<GridTailRow<long, LogRow>> gone, TaskCompletionSource? hold)
    {
        if (hold is not null) await hold.Task;
        else await Task.Yield();
        Evaluate(changed.Span, gone.Span);
    }

    private sealed class LogScope(LogScopes scopes, GridView view) : IGridScope<long, LogRow>
    {
        private readonly Dictionary<long, (bool Newest, bool Stored)> _matches = new(4_096); // (sized past a tail's turn at its worst:
        private readonly List<(long Key, bool Gone, bool Newest, bool Stored)> _staged = new(4_096); // a warm filter allocates nothing either)

        public Exception? Fault { get; set; }

        public bool TryMatch(in long key, out bool newest, out bool stored)
        {
            var found = _matches.TryGetValue(key, out var match);
            (newest, stored) = match;
            return found;
        }

        public ValueTask DisposeAsync()
        {
            scopes._open.Remove(this);
            return ValueTask.CompletedTask;
        }

        public void Stage(ReadOnlySpan<GridTailRow<long, LogRow>> rows, bool gone)
        {
            foreach (var row in rows)
                _staged.Add((row.Key, gone, !gone && LogRows.Passes(view, row.Newest),
                    !gone && row.HasStored && LogRows.Passes(view, row.Stored)));
        }

        public void Publish()
        {
            foreach (var (key, gone, newest, stored) in _staged)
                if (gone) _matches.Remove(key);
                else _matches[key] = (newest, stored);
            _staged.Clear();
        }
    }
}

/// <summary>The orders' world: each key's newest version made, stamped by the clock.</summary>
internal sealed class LogWorld(ManualClock clock, int seed)
{
    public readonly Dictionary<long, LogRow> Made = [];
    private readonly Random _random = new(seed);
    private long _next;

    public long Now => (clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / 10;

    /// <summary>A new version of <paramref name="id" /> (its first, if it has none).</summary>
    public LogRow Make(long id, int? qty = null, byte? status = null)
    {
        var version = Made.TryGetValue(id, out var was) ? was.Version + 1 : 1;
        var row = new LogRow
        {
            Id = id, Version = version, Stamp = Now, Qty = qty ?? _random.Next(1_000), Status = status ?? (byte)_random.Next(4)
        };
        Made[id] = row;
        _next = Math.Max(_next, id + 1);
        return row;
    }

    public LogRow New(int? qty = null, byte? status = null)
    {
        return Make(_next, qty, status);
    }

    public long Any()
    {
        return _random.NextInt64(Math.Max(1, _next));
    }

    /// <summary>The view as it truly is: each key's newest version, passing, in the view's order.</summary>
    public LogRow[] Truth(GridView view)
    {
        return [.. Made.Values.Where(r => LogRows.Passes(view, r)).OrderBy(r => r, new LogOrder(view))];
    }
}

/// <summary>Newest first, by key alone: the natural order, compared without allocating.</summary>
internal sealed class NewestFirst : IComparer<LogRow>
{
    public static readonly NewestFirst Instance = new();

    public int Compare(LogRow a, LogRow b)
    {
        return b.Id.CompareTo(a.Id);
    }
}

/// <summary>Log rows in their natural order alone, newest first.</summary>
internal sealed class FlatRows : IGridRows<long, LogRow, long>
{
    public long KeyOf(in LogRow row)
    {
        return row.Id;
    }

    public long VersionOf(in LogRow row)
    {
        return row.Version;
    }

    public long StampOf(in LogRow row)
    {
        return row.Stamp;
    }

    public long Back(long stamp, TimeSpan span)
    {
        return stamp - span.Ticks / 10; // (microseconds)
    }

    public TimeSpan Between(long earlier, long later)
    {
        return TimeSpan.FromTicks((later - earlier) * 10);
    }

    public long StampAt(DateTimeOffset time)
    {
        return (time - DateTimeOffset.UnixEpoch).Ticks / 10;
    }

    public IComparer<LogRow> OrderOf(GridView view)
    {
        return NewestFirst.Instance;
    }

    public bool NewestFirstByKey(GridView view)
    {
        return true;
    }
}

/// <summary>
///     An append-only store as an array by id, in its natural order (ids landed from the first, none skipped; a newer
///     version replacing its key's): reads that allocate nothing, so a measure counts the merge's own.
/// </summary>
internal sealed class FlatStore(AppendOnlyStorePolicy policy) : IGridStore<long, LogRow, long>
{
    private readonly LogRow[] _rows = new LogRow[1 << 16];
    private long _newest = long.MinValue, _top = -1; // (the greatest id landed)

    public AppendOnlyStorePolicy Policy => policy;

    public ValueTask ReadAsync(GridView view, GridSeek<long, LogRow> seek, int limit, uint maxBytes, GridPage<long, LogRow> page,
        CancellationToken cancellationToken)
    {
        if (seek.Kind == GridSeekKind.Before)
            for (var id = seek.Row.Id + 1; id <= _top && page.Count < limit; id++) page.Add(id, _rows[id]);
        else
            for (var id = seek.Kind == GridSeekKind.First ? _top : Math.Min(seek.Row.Id, _top); id >= 0 && page.Count < limit; id--)
                page.Add(id, _rows[id]);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReadAtAsync(GridView view, long rank, int limit, uint maxBytes, GridPage<long, LogRow> page,
        CancellationToken cancellationToken)
    {
        for (var id = _top - rank; id >= 0 && page.Count < limit; id--) page.Add(id, _rows[id]);
        return ValueTask.CompletedTask;
    }

    public ValueTask<(long Rows, TotalKind Kind)?> RankAsync(GridView view, long key, LogRow row, uint maxBytes,
        CancellationToken cancellationToken)
    {
        return new ValueTask<(long, TotalKind)?>((Math.Max(0, _top - row.Id), TotalKind.Exact));
    }

    public ValueTask<(long Rows, TotalKind Kind)?> SizeAsync(GridView view, uint maxBytes, CancellationToken cancellationToken)
    {
        return new ValueTask<(long, TotalKind)?>((_top + 1, TotalKind.Exact));
    }

    public ValueTask<long?> NewestStampAsync(CancellationToken cancellationToken)
    {
        return new ValueTask<long?>(_newest == long.MinValue ? null : _newest);
    }

    public async IAsyncEnumerable<LogRow> SinceAsync(long stamp, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        for (var id = 0L; id <= _top; id++)
            if (_rows[id].Stamp >= stamp)
                yield return _rows[id];
    }

    public ValueTask EarlierAsync(ReadOnlyMemory<long> keys, ReadOnlyMemory<long> below, uint maxBytes, GridPage<long, LogRow> page,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < keys.Length; i++)
        {
            var id = keys.Span[i];
            if (id <= _top && _rows[id].Version < below.Span[i]) page.Add(id, _rows[id]);
        }

        return ValueTask.CompletedTask;
    }

    public void Land(in LogRow row)
    {
        if (row.Id <= _top && _rows[row.Id].Version >= row.Version) return;
        _rows[row.Id] = row;
        _top = Math.Max(_top, row.Id);
        _newest = Math.Max(_newest, row.Stamp);
    }
}

/// <summary>
///     Tapline.Grid's StoreAndStream under an append-only store's policy: the store's pages laid over with what the stream
///     sent that it hasn't reached; the state tail forgotten by the store's watermark, guarded for reads under way; the
///     gap fill; the event log kept for views that ask; refused past the tail's cap — against the truth worked out apart.
/// </summary>
internal static partial class T
{
    private static async Task<(GridAnswer Answer, LogRow[] Rows)> StoreRead(StoreAndStream<long, LogRow, long> source, GridView view,
        GridRead<long, LogRow> read)
    {
        var page = new GridPage<long, LogRow>(4_096);
        var answer = await source.ReadAsync(view, read, page, CancellationToken.None);
        return (answer, page.Rows.ToArray());
    }

    private static string StoreIds(IEnumerable<LogRow> rows)
    {
        return string.Join(",", rows.Take(12).Select(r => $"{r.Id}v{r.Version}"));
    }

    private static bool StoreRefuses(Action make)
    {
        try
        {
            make();
            return false;
        }
        catch (GridRefusedException)
        {
            return true;
        }
    }

    /// <summary>The policy most of these tests run under: a haircut of 5 s, as their clocks move.</summary>
    private static readonly AppendOnlyStorePolicy Five = new() { Haircut = TimeSpan.FromSeconds(5) };

    public static async Task GridStoreMerge()
    {
        var clock = new ManualClock();
        var world = new LogWorld(clock, 1);
        var store = new LogStore(new AppendOnlyStorePolicy
            { Haircut = TimeSpan.FromSeconds(5), GapFill = TimeSpan.FromSeconds(2), Windows = StoreWindows.None }); // (each read the store's)
        var stream = new LogStream();
        for (var i = 0; i < 200; i++) store.Land(world.New()); // (the store's, from before the subscription: in a recheck's reach)
        clock.Advance(TimeSpan.FromSeconds(3));
        using var source = new StoreAndStream<long, LogRow, long>(store, stream, new LogRows(), new LogScopes(), clock);

        // The stream ahead of the store: thirty versions of its orders and twenty new orders sent, every other one landed.
        // And behind it: five versions landed, not yet sent.
        var sent = new List<LogRow>();
        for (var i = 0; i < 30; i++) sent.Add(world.Make(world.Any()));
        for (var i = 0; i < 20; i++) sent.Add(world.New());
        stream.Send([.. sent]);
        foreach (var row in sent.Where((_, i) => i % 2 == 0)) store.Land(row);
        var early = Enumerable.Range(0, 5).Select(i => world.Make(sent[2 * i + 1].Id)).ToArray();
        foreach (var row in early) store.Land(row);
        await Rechecked(source, clock); // (the store's rows seen: each key's row known, the counts exact)

        foreach (var view in new[]
                 {
                     GridView.Natural, new GridView([new SortKey("qty", true)]),
                     new GridView([], new GridFilter(("status", '=', 1))),
                     new GridView([new SortKey("qty")], new GridFilter(("qty", '<', 500)))
                 })
        {
            // (Ten new orders the store hasn't landed: their store rows unknown, the total right but an estimate.)
            var truth = Merged(view);
            var (answer, rows) = await StoreRead(source, view, GridRead<long, LogRow>.AtRank(0, 60));
            Assert(rows.SequenceEqual(truth.Take(60)) && answer.Total == truth.Length && answer.TotalKind == TotalKind.Estimated,
                $"the top merged, each key at its newest ({StoreIds(rows)} for {StoreIds(truth)}; {answer.Total} of {truth.Length}, {answer.TotalKind})");
            (answer, rows) = await StoreRead(source, view, GridRead<long, LogRow>.AtRank(25, 30));
            Assert(answer.FirstRank == 25 && rows.SequenceEqual(truth.Skip(25).Take(30)),
                $"from a rank: the truth's from 25 ({answer.FirstRank}: {StoreIds(rows)} for {StoreIds(truth.Skip(25))})");
            var at = Math.Min(40, truth.Length - 1);
            var about = truth[at];
            (answer, rows) = await StoreRead(source, view, GridRead<long, LogRow>.AroundRow(about.Id, about, 10, 20));
            Assert(answer.Anchor == Math.Min(10, at) && answer.FirstRank == at - answer.Anchor &&
                   rows.SequenceEqual(truth.Skip((int)answer.FirstRank).Take(answer.Anchor + 20)),
                $"about a row: ten before it, it and nineteen after, ranked ({answer.FirstRank}, anchor {answer.Anchor}: {StoreIds(rows)})");
        }

        // A key the stream alone has, about it: found; filtered out: not.
        var fresh = sent[^1];
        var (found, near) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AroundKey(fresh.Id, 2, 3));
        Assert(found.Anchor != GridAnswer.NotFound && near[found.Anchor] == fresh, "a key the stream alone has: found");
        var other = new GridView([], new GridFilter(("status", '=', (fresh.Status + 1) % 4)));
        (found, _) = await StoreRead(source, other, GridRead<long, LogRow>.AroundKey(fresh.Id, 2, 3));
        Assert(found.Anchor == GridAnswer.NotFound, "filtered out: not found");

        // It lands, the store looked up before without it: seen in a read, counted once.
        store.Land(fresh);
        var now = world.Truth(GridView.Natural);
        var (counted, top) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 60));
        Assert(top.SequenceEqual(now.Take(60)) && counted.Total == now.Length, $"landed, counted once ({counted.Total} of {now.Length})");
        (counted, top) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(25, 30));
        Assert(counted.FirstRank == 25 && top.SequenceEqual(now.Skip(25).Take(30)), $"landed, ranked once ({counted.FirstRank})");

        // The store catches up and the stream sends what landed first; a new order a haircut on: the tail lets go.
        foreach (var row in sent.Where((r, i) => i % 2 == 1 && r != fresh)) store.Land(row);
        stream.Send(early);
        clock.Advance(TimeSpan.FromSeconds(6));
        await source.GapFilled;
        var later = world.New();
        stream.Send(later);
        store.Land(later);
        await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 10)); // (the store's newest stamp, seen)
        Assert(source.TailRows == 1 && source.Watermark == later.Stamp && source.Lag == TimeSpan.Zero,
            $"the store caught up: the tail its last version alone ({source.TailRows} rows), the store's lag none");
        var (_, all) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 1_000));
        Assert(all.SequenceEqual(world.Truth(GridView.Natural)), "the whole view the truth, from the store");
        Assert(source.HistoryRows == 0, "no view keeping history: one tail");

        // The view as the merge shows it, the store's rows seen: each key at the newest version either side holds — in the
        // view if that passes, or, the stream ahead of the store, if the store's row of it does (the blotter's rule).
        LogRow[] Merged(GridView view)
        {
            return [.. world.Made.Values
                .Where(newest => LogRows.Passes(view, newest) ||
                                 (store.Find(newest.Id) is { } stored && stored.Version < newest.Version && LogRows.Passes(view, stored)))
                .OrderBy(r => r, new LogOrder(view))];
        }
    }

    /// <summary>
    ///     Sizes without counting: a page short of its limit ends the view, so its total is counted from the page, nothing
    ///     asked of the store; past the limit, the store's word — here a count — or, if it can't say, at least the rows
    ///     seen. A read about a row reads back from it: short, its place is counted from the rows back; else the store's.
    /// </summary>
    public static async Task GridStoreSizes()
    {
        var clock = new ManualClock();
        var world = new LogWorld(clock, 7);
        var store = new LogStore(Five with { Windows = StoreWindows.None }); // (each read the store's: its sizes asked)
        var stream = new LogStream();
        for (var i = 0; i < 300; i++) store.Land(world.New(status: (byte)(i % 100 == 0 ? 3 : i % 3))); // (three of status 3)
        clock.Advance(TimeSpan.FromSeconds(3));
        using var source = new StoreAndStream<long, LogRow, long>(store, stream, new LogRows(), new LogScopes(), clock);
        var few = new GridView([], new GridFilter(("status", '=', 3)));

        // Three rows: one page, short of its limit — counted from it, the store asked nothing.
        var (answer, rows) = await StoreRead(source, few, GridRead<long, LogRow>.AtRank(0, 10));
        Assert(answer.Total == 3 && answer.TotalKind == TotalKind.Exact && rows.Length == 3 && store.Sizes == 0,
            $"a short page counts the view ({answer.Total}, {answer.TotalKind}; {store.Sizes} sizes asked)");

        // Three hundred: the page full, more past it — the store's word (a count here), asked once.
        (answer, _) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 10));
        Assert(answer.Total == 300 && answer.TotalKind == TotalKind.Exact && store.Sizes == 1,
            $"past the limit: the store's size ({answer.Total}, {answer.TotalKind}; {store.Sizes} asked)");

        // A store that can't say: at least the rows seen.
        store.Unsized = true;
        (answer, _) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 10));
        Assert(answer.TotalKind == TotalKind.AtLeast && answer.Total >= 64, $"it can't say: at least the rows seen ({answer.Total}, {answer.TotalKind})");
        store.Unsized = false;

        // About a row near the top: the rows back from it short — its place counted from them, none asked. Far down: asked.
        var truth = world.Truth(GridView.Natural);
        (answer, _) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AroundRow(truth[5].Id, truth[5], 2, 5));
        Assert(answer.FirstRank == 3 && answer.Anchor == 2 && store.Ranks == 0, $"near the top: placed from the rows back ({answer.FirstRank}; {store.Ranks} asked)");
        (answer, _) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AroundRow(truth[200].Id, truth[200], 2, 5));
        Assert(answer.FirstRank == 198 && store.Ranks == 1, $"far down: the store's place ({answer.FirstRank}; {store.Ranks} asked)");
    }

    /// <summary>A recheck done: the clock past the policy's, a read to ask for it, then waited for.</summary>
    private static async Task Rechecked(StoreAndStream<long, LogRow, long> source, ManualClock clock)
    {
        var done = source.Rechecks;
        clock.Advance(AppendOnlyStorePolicy.Default.Recheck);
        await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 1));
        await Until(() => source.Rechecks > done, "a recheck done");
    }

    public static async Task GridStoreWatermark()
    {
        var clock = new ManualClock();
        var world = new LogWorld(clock, 2);
        var store = new LogStore(new AppendOnlyStorePolicy { Haircut = TimeSpan.FromSeconds(5) });
        var stream = new LogStream();
        using var source = new StoreAndStream<long, LogRow, long>(store, stream, new LogRows(), new LogScopes(), clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        await source.GapFilled; // (the store empty: asked its newest stamp, none, no watermark)
        var asked = store.NewestAsks;

        // No reads: once the tail's oldest is two haircuts behind the stream's newest, the store is asked its newest stamp.
        var a = world.New();
        stream.Send(a);
        store.Land(a);
        clock.Advance(TimeSpan.FromSeconds(11));
        var b = world.New();
        stream.Send(b);
        await Until(() => source.Watermark == a.Stamp, "the store asked its newest stamp");
        Assert(store.NewestAsks == asked + 1 && source.TailRows == 2, "asked once; a, the store's newest, not a haircut behind it: kept");
        var c = world.New();
        stream.Send(c);
        Assert(store.NewestAsks == asked + 1, "asked a moment ago: not again");
        store.Land(b);
        clock.Advance(TimeSpan.FromSeconds(6));
        var d = world.New();
        stream.Send(d);
        await Until(() => source.Watermark == b.Stamp, "a haircut on: asked again");
        Assert(store.NewestAsks == asked + 2 && source.TailRows == 3, $"a, a haircut behind the store's newest, let go ({source.TailRows} rows)");
        Assert(source.Rechecks == 0, "no reads: no recheck");

        // A read says it, unasked; and asks a recheck — the store's recent versions read again — once a Recheck at most.
        store.Land(c);
        store.Land(d);
        clock.Advance(TimeSpan.FromSeconds(10));
        await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 5));
        Assert(source.Watermark == d.Stamp && source.TailRows == 1,
            $"a read brought the store's newest stamp: b and c let go ({source.TailRows} rows)");
        await Until(() => source.Rechecks == 1, "the read asked a recheck");
        Assert(store.NewestAsks == asked + 3, "the recheck asked the store's newest stamp, then read back a haircut from it");
        await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 5));
        await Task.Delay(50);
        Assert(source.Rechecks == 1 && store.NewestAsks == asked + 3, "within a Recheck of it: no other");
        clock.Advance(AppendOnlyStorePolicy.Default.Recheck);
        await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 5));
        await Until(() => source.Rechecks == 2, "a Recheck on: another");
    }

    public static async Task GridStoreInFlight()
    {
        var clock = new ManualClock();
        var world = new LogWorld(clock, 5);
        var store = new LogStore(Five with { Windows = StoreWindows.None }); // (a haircut of 5 s, the clock as the test moves it; each read the store's)
        var stream = new LogStream();
        using var source = new StoreAndStream<long, LogRow, long>(store, stream, new LogRows(), new LogScopes(), clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        await source.GapFilled;
        var k = world.New(status: 1);
        stream.Send(k);
        store.Land(k);
        await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 5));
        Assert(source.Watermark == k.Stamp, "the watermark: k's stamp, read");

        // Ten seconds on, k's second version sent, not yet landed; a read takes the store's rows (k's first) and waits.
        clock.Advance(TimeSpan.FromSeconds(10));
        var k2 = world.Make(k.Id, status: 2);
        stream.Send(k2);
        var pause = new TaskCompletionSource();
        store.Pause = pause;
        var paused = StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 50));
        store.Pause = null;
        Assert(!paused.IsCompleted, "the read under way, the store's rows taken");

        // Meanwhile k2 lands, and a new order; another read sees the watermark pass k2 by a haircut.
        store.Land(k2);
        clock.Advance(TimeSpan.FromSeconds(10));
        var c = world.New();
        stream.Send(c);
        store.Land(c);
        await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 5));
        Assert(source.Watermark == c.Stamp && source.TailRows == 2,
            $"k2 kept while a read that began before the store had it is under way ({source.TailRows} rows)");

        // Done: k at its newest, not the store's older one it read; c from the tail, though another read found it stored.
        pause.SetResult();
        var (_, rows) = await paused;
        Assert(rows.SequenceEqual([c, k2]), $"the read under way: c and k2 ({StoreIds(rows)})");
        Assert(source.TailRows == 1, $"the read done: k2 let go ({source.TailRows} rows)");
    }

    public static async Task GridStoreBounds()
    {
        var clock = new ManualClock();
        var world = new LogWorld(clock, 3);
        var store = new LogStore(Five with { MaxTailRows = 10, History = TimeSpan.FromSeconds(20), MaxHistoryRows = 5, Windows = StoreWindows.None });
        var stream = new LogStream();
        using var source = new StoreAndStream<long, LogRow, long>(store, stream, new LogRows(), new LogScopes(), clock);
        var keep = source.KeepHistory(TimeSpan.FromSeconds(10));
        Assert(StoreRefuses(() => source.KeepHistory(TimeSpan.FromSeconds(21))), "history past the policy's: refused");
        clock.Advance(TimeSpan.FromSeconds(5));
        await source.GapFilled;

        // The store stalled: past ten rows the stream sent that it hasn't reached, the oldest is let go — reads go on, the
        // key pruned shown as the store has it (it hasn't it yet: not shown), the rest from the tail.
        var sent = Enumerable.Range(0, 11).Select(_ => world.New()).ToArray();
        stream.Send(sent);
        var (answer, rows) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 20));
        Assert(answer.Refusal is null && source.TailRows == 10 && rows.SequenceEqual(world.Truth(GridView.Natural).Where(r => r.Id != sent[0].Id)),
            $"past the cap: the oldest pruned, served ({source.TailRows} rows held; {StoreIds(rows)})");

        // It catches up, landing what the stream sent, and one more: a read brings its newest stamp, a haircut past the
        // pruned row — the tail lets go what the store has reached, and the grids are told to read again (no batch sent).
        var version = source.Version;
        var told = 0;
        using (source.Watch(() => Interlocked.Increment(ref told)))
        {
            foreach (var row in sent) store.Land(row);
            clock.Advance(TimeSpan.FromSeconds(6));
            var next = world.New();
            store.Land(next);
            await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 10));
            await Until(() => source.Version > version && Volatile.Read(ref told) > 0, "the store past the pruned row: the grids told");
        }

        (answer, rows) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 20));
        Assert(answer.Refusal is null && source.TailRows == 0 && rows.SequenceEqual(world.Truth(GridView.Natural)),
            $"read again: the truth, the pruned row from the store ({source.TailRows} rows held)");

        // A read's memory budget (GridRead.MaxBytes): each statement it makes carries it; one past it refused, the read is too,
        // saying why, its rows none.
        store.Budgets.Clear();
        (answer, rows) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 20) with { MaxBytes = 1 << 20 });
        Assert(answer.Refusal is null && store.Budgets.Count >= 1 && store.Budgets.All(b => b == 1 << 20),
            $"each statement carries the read's budget ({string.Join(", ", store.Budgets)})");
        (answer, _) = await StoreRead(source, new GridView([new SortKey("qty", true)], null),
            GridRead<long, LogRow>.AroundKey(sent[3].Id, 2, 2) with { MaxBytes = 4096 });
        Assert(answer.Refusal is null && store.Budgets.Skip(2).All(b => b == 4096), "about a key too");
        store.Refuse = "A page needed more memory than the read's budget, 1 MiB, and was stopped.";
        (answer, rows) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 20) with { MaxBytes = 1 << 20 });
        Assert(answer.Refusal == store.Refuse && !rows.Any(), $"past it: refused, saying why ({answer.Refusal})");
        store.Refuse = null;

        // The event log, kept back ten seconds for a view: at most five versions, back ten seconds from the newest.
        Assert(source.HistoryRows == 5, $"at most five versions ({source.HistoryRows})");
        clock.Advance(TimeSpan.FromSeconds(11));
        LogRow[] last = [world.New(), world.New()];
        stream.Send(last);
        var history = new List<LogRow>();
        Assert(source.HistoryRows == 2 && source.CopyHistory(TimeSpan.FromSeconds(1), history) == 2 && history.SequenceEqual(last),
            $"older than ten seconds: gone ({source.HistoryRows})");
        keep.Dispose();
        stream.Send(world.New());
        Assert(source.HistoryRows == 0, "no view keeping history: none kept");

        // A policy that can't be kept to: refused as the source is made.
        var broken = new LogStore(new AppendOnlyStorePolicy { MaxTailRows = 0 });
        Assert(StoreRefuses(() => new StoreAndStream<long, LogRow, long>(broken, new LogStream(), new LogRows(), new LogScopes(), clock)),
            "a state tail of none: refused");
    }

    public static async Task GridStoreGapFill()
    {
        var clock = new ManualClock();
        var world = new LogWorld(clock, 4);
        var store = new LogStore(Five); // (a haircut of 5 s: the clock as the test moves it)
        var stream = new LogStream();
        var g = world.New(status: 1);
        store.Land(g);
        clock.Advance(TimeSpan.FromMinutes(5));
        var g2 = world.Make(g.Id, status: 2); // (written as the grid subscribes: the stream never sends it)
        clock.Advance(TimeSpan.FromSeconds(1));
        using var source = new StoreAndStream<long, LogRow, long>(store, stream, new LogRows(), new LogScopes(), clock);
        using var keep = source.KeepHistory(TimeSpan.FromMinutes(1));
        var watched = 0;
        using var watch = source.Watch(() => Interlocked.Increment(ref watched));
        var (_, rows) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 5));
        Assert(rows.Single() == g, "the first page: g as the store has it, g2 not landed");

        store.Land(g2);
        var version = source.Version;
        clock.Advance(TimeSpan.FromSeconds(5)); // (a haircut on: the gap fill)
        await source.GapFilled;
        Assert(source.GapFillFault is null && source.Version > version && watched == 1 && source.TailRows == 1,
            "the gap fill: g2, stamped before the subscription, taken as if streamed — the grid told");
        (_, rows) = await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 5));
        Assert(rows.Single() == g2, "g2 shown");
        Assert(source.CopyHistory(TimeSpan.FromMinutes(1), []) == 0, "the event log the stream's alone: none sent");
    }

    public static async Task GridStoreCatchUp()
    {
        var clock = new ManualClock();
        var world = new LogWorld(clock, 6);
        var store = new LogStore(Five with { Windows = StoreWindows.None }); // (a haircut of 5 s, the clock as the test moves it; each read the store's)
        var stream = new LogStream();
        var scopes = new LogScopes();
        for (var i = 0; i < 300; i++) store.Land(world.New());
        clock.Advance(TimeSpan.FromSeconds(60));
        using var source = new StoreAndStream<long, LogRow, long>(store, stream, new LogRows(), scopes, clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        await source.GapFilled;
        var natural = GridView.Natural;
        var (_, rows) = await StoreRead(source, natural, GridRead<long, LogRow>.AtRank(0, 10));
        Assert(scopes.Opened == 1 && rows.SequenceEqual(world.Truth(natural).Take(10)), "a view opened at once, its filter answering at once");

        // The filters slow: what the stream sends waits in the catch-up, unpublished, and reads wait for it; a view asked
        // for meanwhile opens after it, over the tail as published.
        scopes.Hold = new TaskCompletionSource();
        var fresh = world.New();
        var update = world.Make(3);
        var version = source.Version;
        stream.Send(fresh, update);
        Assert(source.TailRows == 0 && source.Version == version,
            $"sent, not yet evaluated: the tail as it was, the version too ({source.TailRows} rows, version {version} → {source.Version})");
        var byStatus = new GridView([], new GridFilter(("status", '=', update.Status)));
        var reading = StoreRead(source, natural, GridRead<long, LogRow>.AtRank(0, 60));
        var opening = StoreRead(source, byStatus, GridRead<long, LogRow>.AtRank(0, 60));
        await Task.Delay(50);
        Assert(!reading.IsCompleted && !opening.IsCompleted, "the reads wait for what was sent before them");
        var hold = scopes.Hold;
        scopes.Hold = null;
        hold.SetResult();
        (_, rows) = await reading;
        Assert(rows.SequenceEqual(world.Truth(natural).Take(60)) && source.Version > version && source.TailRows == 2,
            $"published: the read shows both ({StoreIds(rows)})");
        (_, rows) = await opening;
        Assert(scopes.Opened == 2 && rows.SequenceEqual(world.Truth(byStatus).Take(60)), "the view opened after, with the change in it");

        // An order the view has from the store, far below the page — its store row older than a recheck reaches back —
        // changes, its new version not passing. Nothing is looked up: the merge can't know the store's row, so the view keeps
        // the order as the store counts it, the total an estimate. Once the stream's versions land and a recheck reads them,
        // counted out, exactly.
        var leaving = world.Truth(byStatus).Last(r => r.Id != update.Id);
        var left = world.Make(leaving.Id, status: (byte)((leaving.Status + 1) % 4));
        stream.Send(left);
        var (counted, _) = await StoreRead(source, byStatus, GridRead<long, LogRow>.AtRank(0, 1));
        Assert(counted.TotalKind == TotalKind.Estimated,
            $"its store row unknown: kept as the store counts it, an estimate ({counted.Total}, {counted.TotalKind})");
        store.Land(fresh);
        store.Land(update);
        store.Land(left);
        await Rechecked(source, clock);
        (counted, _) = await StoreRead(source, byStatus, GridRead<long, LogRow>.AtRank(0, 1));
        Assert(counted.Total == world.Truth(byStatus).Length && counted.TotalKind == TotalKind.Exact,
            $"landed and read again: counted out, exactly ({counted.Total} of {world.Truth(byStatus).Length}, {counted.TotalKind})");

        // A filter failed (past its budget): its view's next read refused, saying why; the one after opens it again.
        scopes.FailAll(new InvalidOperationException("past its state budget"));
        var (refused, _) = await StoreRead(source, natural, GridRead<long, LogRow>.AtRank(0, 10));
        Assert(refused.Refusal is { } why && why.Contains("past its state budget"), $"refused: {refused.Refusal}");
        var opened = scopes.Opened;
        (_, rows) = await StoreRead(source, natural, GridRead<long, LogRow>.AtRank(0, 10));
        Assert(scopes.Opened == opened + 1 && rows.SequenceEqual(world.Truth(natural).Take(10)), "opened again, served");

        // An evaluation that fails outright: every view's filter dropped, each opened again by its next read.
        scopes.Throw = new InvalidOperationException("the engine stopped");
        stream.Send(world.New());
        opened = scopes.Opened;
        (_, rows) = await StoreRead(source, natural, GridRead<long, LogRow>.AtRank(0, 10));
        Assert(scopes.Opened == opened + 1 && rows.SequenceEqual(world.Truth(natural).Take(10)),
            $"the views dropped, the read opening its own again: served ({scopes.Opened - opened} opened)");
    }

    public static async Task GridStoreOracle()
    {
        var (checks, kept) = (0, 0);
        for (var seed = 1; seed <= 6; seed++)
        {
            var clock = new ManualClock();
            var world = new LogWorld(clock, seed);
            var random = new Random(seed * 7);
            var store = new LogStore(new AppendOnlyStorePolicy // (even seeds: windows kept by the stream; odd: each read the store's)
                { Haircut = TimeSpan.FromSeconds(2), Windows = seed % 2 == 0 ? StoreWindows.Kept : StoreWindows.None });
            var stream = new LogStream();
            var made = new Dictionary<(long, int), LogRow>(); // every version made, by key and version
            var landed = new Dictionary<long, int>(); // each key's newest version the store holds
            var delivered = new Dictionary<long, int>(); // and the stream sent
            for (var i = 0; i < 300; i++) Land(Made(world.New()));
            clock.Advance(TimeSpan.FromSeconds(60));
            var scopes = new LogScopes(seed > 4); // (the last two: filters that answer later, as ChalkQL's)
            using var source = new StoreAndStream<long, LogRow, long>(store, stream, new LogRows(), scopes, clock);
            clock.Advance(TimeSpan.FromSeconds(2));
            await source.GapFilled;
            var unsent = new List<LogRow>(); // (made, the stream yet to send: in order)
            var unlanded = new List<LogRow>(); // (made, the store yet to land)
            var frontier = long.MinValue; // (the store's: every version stamped a second before it landed, none after it)
            var stalled = false;
            GridView[] views =
            [
                GridView.Natural, new GridView([new SortKey("qty", true)]),
                new GridView([], new GridFilter(("status", '=', 1))),
                new GridView([new SortKey("status"), new SortKey("qty", true)], new GridFilter(("qty", '>', 300)))
            ];
            for (var step = 0; step < 400; step++)
            {
                var roll = random.Next(100);
                if (roll < 25)
                {
                    for (var n = random.Next(1, 6); n > 0; n--)
                    {
                        var row = Made(random.Next(3) == 0 ? world.New() : world.Make(world.Any()));
                        unsent.Add(row);
                        unlanded.Add(row);
                    }
                }
                else if (roll < 45)
                {
                    if (unsent.Count == 0) continue;
                    var n = random.Next(1, unsent.Count + 1);
                    var batch = unsent.GetRange(0, n).ToArray();
                    unsent.RemoveRange(0, n);
                    foreach (var row in batch) delivered[row.Id] = Math.Max(delivered.GetValueOrDefault(row.Id), row.Version);
                    stream.Send(batch);
                }
                else if (roll < 60)
                {
                    // The store catches up to a while ago — behind the stream, or ahead of it: every version stamped a second
                    // before then lands, those within the second at random (out of stamp order, inside the haircut).
                    if (stalled) continue;
                    frontier = Math.Max(frontier, world.Now - random.Next(0, 3_000_000));
                    foreach (var row in unlanded.Where(r => r.Stamp <= frontier && (r.Stamp <= frontier - 1_000_000 || random.Next(2) == 0)).ToList())
                    {
                        Land(row);
                        unlanded.Remove(row);
                    }
                }
                else if (roll < 63)
                {
                    stalled = !stalled;
                }
                else if (roll < 75)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(random.Next(50, 900)));
                }
                else
                {
                    var view = views[random.Next(views.Length)];
                    var truth = world.Truth(view);
                    var kind = random.Next(4);
                    GridRead<long, LogRow> read;
                    if (kind == 0 || truth.Length == 0)
                    {
                        read = GridRead<long, LogRow>.AtRank(0, random.Next(1, 40));
                    }
                    else if (kind == 1)
                    {
                        read = GridRead<long, LogRow>.AtRank(random.Next(truth.Length), random.Next(1, 40));
                    }
                    else if (kind == 2)
                    {
                        var about = truth[random.Next(truth.Length)];
                        read = GridRead<long, LogRow>.AroundRow(about.Id, about, random.Next(0, 20), random.Next(1, 30));
                    }
                    else
                    {
                        read = GridRead<long, LogRow>.AroundKey(world.Any(), random.Next(0, 20), random.Next(1, 30));
                    }

                    // What the source can know as the read begins: each key at the newer of the versions the stream sent and
                    // the store holds — but the keys the store holds a version of the stream hasn't sent, which a read finds
                    // only where its statements reach (left out on both sides).
                    var sentBefore = new Dictionary<long, int>(delivered);
                    var storeAhead = landed.Where(l => l.Value > delivered.GetValueOrDefault(l.Key)).Select(l => l.Key).ToHashSet();
                    var known = Known(view, storeAhead);
                    var (answer, rows) = await StoreRead(source, view, read);
                    var what = $"seed {seed}, step {step}, {read.Kind}";
                    Assert(answer.Refusal is null, $"{what}: served ({answer.Refusal})");
                    var order = new LogOrder(view);
                    for (var i = 1; i < rows.Length; i++)
                        Assert(order.Compare(rows[i - 1], rows[i]) < 0, $"{what}: in order ({StoreIds(rows)})");
                    Assert(rows.Select(r => r.Id).Distinct().Count() == rows.Length, $"{what}: no key twice ({StoreIds(rows)})");
                    // A row passes, or the view keeps it as the store's row of it last passed (the blotter's rule): a version of
                    // its key the store has passed, older than the row shown — the source can only have seen one it landed.
                    bool Lingers(LogRow row)
                    {
                        for (var v = 1; v < row.Version && v <= landed.GetValueOrDefault(row.Id); v++)
                            if (made.TryGetValue((row.Id, v), out var was) && LogRows.Passes(view, was))
                                return true;
                        return false;
                    }

                    foreach (var row in rows)
                    {
                        Assert(LogRows.Passes(view, row) || Lingers(row), $"{what}: {row.Id}v{row.Version} passes, or the store's row of it last did");
                        Assert(row.Version >= sentBefore.GetValueOrDefault(row.Id),
                            $"{what}: {row.Id}v{row.Version} no older than v{sentBefore.GetValueOrDefault(row.Id)}, sent before the read");
                    }

                    if (read.Kind == GridReadKind.AtRank && read.Rank == 0)
                    {
                        var shown = rows.Where(r => !storeAhead.Contains(r.Id) && LogRows.Passes(view, r)).ToArray(); // (those kept aside)
                        Assert(shown.SequenceEqual(known.Take(shown.Length)) && (rows.Length == read.Count || shown.Length == known.Length),
                            $"{what}: the top what the source knows ({StoreIds(shown)} for {StoreIds(known)})");
                    }
                    else if (read.Kind != GridReadKind.AtRank)
                    {
                        var place = read.Row;
                        var found = true;
                        if (read.Kind == GridReadKind.AroundKey)
                        {
                            var version = Math.Max(delivered.GetValueOrDefault(read.Key), landed.GetValueOrDefault(read.Key));
                            found = version > 0 && LogRows.Passes(view, place = made[(read.Key, version)]);
                            if (storeAhead.Contains(read.Key) || (!found && version > 0 && Lingers(place)))
                                found = answer.Anchor != GridAnswer.NotFound; // (as it found: the source's to know)
                            else Assert(found == (answer.Anchor != GridAnswer.NotFound), $"{what}: found as the source knows it ({answer.Anchor})");
                        }

                        if (found && !storeAhead.Contains(read.Key))
                        {
                            if (read.Kind == GridReadKind.AroundKey) Assert(rows[answer.Anchor].Id == read.Key, $"{what}: the key at the anchor");
                            var at = known.Count(r => order.Compare(r, place) < 0);
                            var before = rows[..answer.Anchor].Where(r => !storeAhead.Contains(r.Id) && LogRows.Passes(view, r)).ToArray();
                            var after = rows[answer.Anchor..].Where(r => !storeAhead.Contains(r.Id) && LogRows.Passes(view, r)).ToArray();
                            Assert(before.SequenceEqual(known[(at - before.Length)..at]) && after.SequenceEqual(known.Skip(at).Take(after.Length)) &&
                                   (answer.Anchor == read.Before || before.Length == at) &&
                                   (rows.Length - answer.Anchor == read.Count || after.Length == known.Length - at),
                                $"{what}: about its place what the source knows ({StoreIds(before)} | {StoreIds(after)} at {at} of {StoreIds(known)})");
                        }
                    }

                    checks++;
                }
            }

            // Quiet: every version sent and landed, one a haircut on, the store's newest seen — every page the truth's,
            // ranked as the truth ranks it, and the tail let go.
            stream.Send([.. unsent]);
            foreach (var row in unlanded) Land(row);
            clock.Advance(TimeSpan.FromSeconds(5));
            var last = Made(world.New());
            stream.Send(last);
            Land(last);
            await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 5));
            foreach (var view in views)
            {
                var truth = world.Truth(view);
                var (answer, rows) = await StoreRead(source, view, GridRead<long, LogRow>.AtRank(0, 1_000));
                Assert(rows.SequenceEqual(truth) && answer.Total == truth.Length && answer.TotalKind == TotalKind.Exact,
                    $"seed {seed}, quiet: the view the truth ({rows.Length} rows, {answer.Total} counted, of {truth.Length})");
                for (var i = 0; i < 5 && truth.Length > 0; i++)
                {
                    var rank = random.Next(truth.Length);
                    (answer, rows) = await StoreRead(source, view, GridRead<long, LogRow>.AtRank(rank, 10));
                    Assert(answer.FirstRank == rank && rows.SequenceEqual(truth.Skip(rank).Take(10)), $"seed {seed}, quiet: from {rank}");
                    var at = random.Next(truth.Length);
                    var about = truth[at];
                    (answer, rows) = await StoreRead(source, view, GridRead<long, LogRow>.AroundRow(about.Id, about, 5, 10));
                    var before = Math.Min(5, at);
                    Assert(answer.Anchor == before && answer.FirstRank == at - before && rows.SequenceEqual(truth.Skip(at - before).Take(before + 10)),
                        $"seed {seed}, quiet: about {at}, ranked ({answer.FirstRank}, anchor {answer.Anchor})");
                    checks += 2;
                }
            }

            Assert(source.TailRows <= 1, $"seed {seed}, quiet: the tail let go ({source.TailRows})");
            Assert(seed % 2 == 1 || source.WindowReads > 0, $"seed {seed}: reads answered by kept windows");
            kept += source.WindowReads;
            continue;

            LogRow Made(LogRow row)
            {
                made[(row.Id, row.Version)] = row;
                return row;
            }

            void Land(LogRow row)
            {
                store.Land(row);
                landed[row.Id] = Math.Max(landed.GetValueOrDefault(row.Id), row.Version);
            }

            LogRow[] Known(GridView view, HashSet<long> storeAhead)
            {
                var rows = new List<LogRow>();
                foreach (var id in world.Made.Keys)
                {
                    var version = Math.Max(delivered.GetValueOrDefault(id), landed.GetValueOrDefault(id));
                    if (version > 0 && !storeAhead.Contains(id) && made[(id, version)] is var row && LogRows.Passes(view, row)) rows.Add(row);
                }

                rows.Sort(new LogOrder(view));
                return [.. rows];
            }
        }

        Console.WriteLine($"  store and stream oracle: {checks:N0} pages checked, exactly as the source can know them ({kept:N0} answered by kept windows)");
    }

    /// <summary>
    ///     Windows kept by the stream: what a read brings kept, changed by every change the stream sends, and the reads it
    ///     covers answered from it, the store asked nothing — its place moved by changes it can't place, by a guess; read
    ///     again past its edge, and on a refresh, which heals what the stream didn't carry and places it again, but never
    ///     once settled (every row final, the store's clock seen past the newest); bounded, let go idle, with its view, and
    ///     when the tail prunes.
    /// </summary>
    public static async Task GridStoreWindows()
    {
        var clock = new ManualClock();
        var world = new LogWorld(clock, 11);
        var store = new LogStore(Five with { Refresh = TimeSpan.FromSeconds(30) }) { Settle = TimeSpan.FromSeconds(10) };
        var stream = new LogStream();
        for (var i = 0; i < 400; i++) store.Land(world.New(status: (byte)(i % 4)));
        clock.Advance(TimeSpan.FromSeconds(60)); // (the gap fill's reach passed: it finds nothing)
        using var source = new StoreAndStream<long, LogRow, long>(store, stream, new LogRows(), new LogScopes(), clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        await source.GapFilled;
        var told = 0;
        using var watch = source.Watch(() => Interlocked.Increment(ref told));
        var natural = GridView.Natural;

        // The live top, and a page about a row below it: the store read for each, then kept — one window, the two overlapping.
        var held = world.Truth(natural)[100];
        await StoreRead(source, natural, GridRead<long, LogRow>.AtRank(0, 40));
        await StoreRead(source, natural, GridRead<long, LogRow>.AroundRow(held.Id, held, 10, 20));
        var statements = store.Budgets.Count;
        Assert(source.Kept is { Windows: 1, Rows: > 150 }, $"each read's rows kept, joined ({source.Kept})");
        Counted(source, "joined");

        // The stream: new orders above both, newer versions of orders in them, the store landing each batch a step behind.
        // Every page the truth's, the store asked nothing; the held page's place moved by each order arriving above it.
        LogRow[] landing = [];
        for (var step = 0; step < 40; step++)
        {
            var truth = world.Truth(natural);
            var at = Array.FindIndex(truth, r => r.Id == held.Id);
            LogRow[] batch = [world.New(), world.Make(truth[at - 3].Id), world.Make(truth[at + 7].Id), world.Make(truth[5].Id)];
            stream.Send(batch);
            foreach (var row in landing) store.Land(row);
            landing = batch;
            truth = world.Truth(natural);
            at = Array.FindIndex(truth, r => r.Id == held.Id);
            var (live, top) = await StoreRead(source, natural, GridRead<long, LogRow>.AtRank(0, 40));
            Assert(top.SequenceEqual(truth.Take(40)) && live.FirstRank == 0, $"step {step}: the live top the truth's ({StoreIds(top)})");
            var (answer, rows) = await StoreRead(source, natural, GridRead<long, LogRow>.AroundRow(held.Id, held, 10, 20));
            Assert(rows.SequenceEqual(truth.Skip(at - 10).Take(30)) && answer.Anchor == 10 && answer.FirstRank == at - 10,
                $"step {step}: the held page the truth's, placed ({answer.FirstRank} for {at - 10}: {StoreIds(rows)})");
        }

        foreach (var row in landing) store.Land(row);
        Counted(source, "changed by the stream");
        Assert(store.Budgets.Count == statements && source.WindowReads >= 80,
            $"the store asked nothing while kept windows covered the reads ({store.Budgets.Count - statements} statements)");

        // Past a window's edge: read again from the store, kept beside.
        var far = world.Truth(natural)[300];
        await StoreRead(source, natural, GridRead<long, LogRow>.AroundRow(far.Id, far, 10, 20));
        Assert(store.Budgets.Count > statements && source.Kept.Windows == 2, $"past the window: the store read again, kept beside ({source.Kept.Windows} windows)");

        // A held window of its own (by qty, well below the top): newer versions of five orders above it the tail never held,
        // their qty as it was, placed by a guess — taken as arriving, the place drifts five, an estimate; and a version of
        // a row in it written to the store directly, which the stream never carries: not shown. A read a Refresh on starts a
        // refresh off its path: the window read again — healed, the grids told, placed by the store's count.
        var byQty = new GridView([new SortKey("qty", true)]);
        var sorted = world.Truth(byQty);
        var low = sorted[250];
        await Rechecked(source, clock); // (the store's rows of the tail's keys seen: the window placed exactly)
        await StoreRead(source, byQty, GridRead<long, LogRow>.AroundRow(low.Id, low, 10, 20));
        LogRow[] moved = [.. sorted.Take(150).Where(r => r.Version == 1).Take(5).Select(r => world.Make(r.Id, r.Qty, r.Status))];
        stream.Send(moved);
        foreach (var row in moved) store.Land(row);
        var inside = sorted[255];
        var direct = world.Make(inside.Id, inside.Qty, (byte)((inside.Status + 1) % 3));
        store.Land(direct);
        sorted = world.Truth(byQty);
        var place = Array.FindIndex(sorted, r => r.Id == low.Id);
        var (drifted, stale) = await StoreRead(source, byQty, GridRead<long, LogRow>.AroundRow(low.Id, low, 10, 20));
        Assert(moved.Length == 5 && drifted.FirstRank == place - 10 + 5 && drifted.TotalKind == TotalKind.Estimated && !stale.Contains(direct),
            $"guessed: the place drifted by the five ({drifted.FirstRank} for {place - 10}, {drifted.TotalKind}), the direct version not shown");
        await Rechecked(source, clock); // (the store's rows of the tail's keys seen: the merge sure of them)
        var (refreshes, before) = (source.Refreshes, told);
        await Held(byQty, GridRead<long, LogRow>.AroundRow(low.Id, low, 10, 20)); // (31 s, read as a held grid reads it)
        await Until(() => source.Refreshes > refreshes, "a refresh done");
        var (healed, fixedRows) = await StoreRead(source, byQty, GridRead<long, LogRow>.AroundRow(low.Id, low, 10, 20));
        Assert(fixedRows.SequenceEqual(sorted.Skip(place - 10).Take(30)) && fixedRows.Contains(direct) && told > before &&
               healed.FirstRank == place - 10 && healed.TotalKind == TotalKind.Exact,
            $"refreshed: healed, the grids told, placed by the store ({healed.FirstRank} for {place - 10}, {healed.TotalKind}: {StoreIds(fixedRows)})");

        // About a row near a view's top: the rows read back from it reach the top, so its window starts there, the store's
        // rows before the row in it — a read from the top answered from it, the truth's, nothing asked.
        var ones = new GridView([], new GridFilter(("status", '=', 1)));
        var near = world.Truth(ones)[20];
        await StoreRead(source, ones, GridRead<long, LogRow>.AroundRow(near.Id, near, 10, 20));
        statements = store.Budgets.Count;
        var (fromTop, firstRows) = await StoreRead(source, ones, GridRead<long, LogRow>.AtRank(0, 30));
        Assert(firstRows.SequenceEqual(world.Truth(ones).Take(30)) && fromTop.FirstRank == 0 && store.Budgets.Count == statements,
            $"a window from the top, made about a row: its rows before the row the store's ({StoreIds(firstRows)}; {store.Budgets.Count - statements} statements)");

        // Settled, as the store judges (its rows final, status 3, and its clock past them by 10 s): a view of final rows. Its
        // window read again while the store's clock stands — a store down settles nothing — and not once the clock moves on.
        var final = new GridView([], new GridFilter(("status", '=', 3)));
        await StoreRead(source, final, GridRead<long, LogRow>.AtRank(0, 20));
        refreshes = source.Refreshes;
        await Held(final, GridRead<long, LogRow>.AtRank(0, 20));
        await Until(() => source.Refreshes > refreshes, "the final rows' window refreshed: the store's clock stood");
        var later = world.New(status: 1);
        stream.Send(later);
        store.Land(later);
        (refreshes, var settles) = (source.Refreshes, source.Settles);
        await Held(final, GridRead<long, LogRow>.AtRank(0, 20));
        await Until(() => source.Settles > settles, "the final rows' window settled, as the store judged");
        var (_, finals) = await StoreRead(source, final, GridRead<long, LogRow>.AtRank(0, 20));
        Assert(source.Refreshes == refreshes && finals.SequenceEqual(world.Truth(final).Take(20)),
            $"settled: final rows, the store's clock past them — not read again ({source.Refreshes - refreshes} refreshes)");

        // Let go: with its view, idle (no read for 10 s, at the next turn), past the policy's rows (the least recently read
        // first), and every window when the tail prunes a key the store hasn't been seen to hold.
        source.Leave(final);
        var windows = source.Kept.Windows;
        clock.Advance(TimeSpan.FromSeconds(11));
        await StoreRead(source, natural, GridRead<long, LogRow>.AtRank(0, 40));
        stream.Send(world.New());
        await StoreRead(source, natural, GridRead<long, LogRow>.AtRank(0, 40));
        Assert(source.Kept.Windows == 1, $"the view left, and the idle, let go ({windows} then {source.Kept.Windows} windows)");
        Counted(source, "let go");

        // A view's own bound (a session's share): past it, the view's least recently read window let go, the one read kept —
        // another view's windows untouched; what each view keeps told apart.
        var perView = new LogStore(Five with { MaxWindowRowsPerView = 60 });
        for (var i = 0; i < 400; i++) perView.Land(world.New());
        clock.Advance(TimeSpan.FromSeconds(60));
        using var shared = new StoreAndStream<long, LogRow, long>(perView, stream, new LogRows(), new LogScopes(), clock);
        var other = new GridView([new SortKey("qty", true)]);
        await StoreRead(shared, other, GridRead<long, LogRow>.AtRank(0, 40));
        await StoreRead(shared, natural, GridRead<long, LogRow>.AtRank(0, 40));
        var deep = world.Truth(natural)[300];
        await StoreRead(shared, natural, GridRead<long, LogRow>.AroundRow(deep.Id, deep, 10, 20));
        var (mine, theirs) = (shared.MemoryOf(natural).Windows, shared.MemoryOf(other).Windows);
        Assert(shared.Kept.Windows == 2 && mine.Held > 0 && theirs.Held > 0 && mine.Bound == theirs.Bound && mine.Bound > 0 &&
               shared.Kept.Rows * (mine.Bound / 60) == mine.Held + theirs.Held,
            $"past a view's rows: its least recently read let go, the other view's kept ({shared.Kept}; {mine} and {theirs})");
        var (_, deepRows) = await StoreRead(shared, natural, GridRead<long, LogRow>.AroundRow(deep.Id, deep, 10, 20));
        Assert(deepRows.SequenceEqual(world.Truth(natural).Skip(290).Take(30)), $"the window read kept, the truth's ({StoreIds(deepRows)})");
        Counted(shared, "a view's bound");
        shared.Leave(natural);
        Assert(shared.MemoryOf(natural) == default, "a view let go: nothing kept for it");

        var small = new LogStore(Five with { MaxWindowRows = 150, MaxTailRows = 30 });
        for (var i = 0; i < 400; i++) small.Land(world.New());
        clock.Advance(TimeSpan.FromSeconds(60));
        using var bounded = new StoreAndStream<long, LogRow, long>(small, stream, new LogRows(), new LogScopes(), clock);
        await StoreRead(bounded, natural, GridRead<long, LogRow>.AtRank(0, 40));
        await StoreRead(bounded, new GridView([new SortKey("qty", true)]), GridRead<long, LogRow>.AtRank(0, 40));
        await StoreRead(bounded, new GridView([], new GridFilter(("status", '=', 1))), GridRead<long, LogRow>.AtRank(0, 40));
        Assert(bounded.Kept is { Windows: 2, Rows: <= 150 }, $"past the policy's rows: the least recently read let go ({bounded.Kept})");
        stream.Send([.. Enumerable.Range(0, 40).Select(_ => world.New())]); // (past the tail's 30, the store stalled: pruned)
        await StoreRead(bounded, natural, GridRead<long, LogRow>.AtRank(0, 40));
        Assert(bounded.Kept.Windows <= 1, $"pruned: every window let go, the next read kept ({bounded.Kept})");
        bounded.Dispose(); // (its provider stopping, grids still reading: refused, saying so, not thrown)
        var (closed, none) = await StoreRead(bounded, natural, GridRead<long, LogRow>.AtRank(0, 40));
        Assert(closed.Refusal == "The source has closed." && none.Length == 0 && bounded.Kept.Windows == 0,
            $"closed: a read refused, saying so ({closed.Refusal}), its windows let go");
        Console.WriteLine($"  kept windows: {source.WindowReads:N0} reads answered by them, {source.Refreshes} refreshes, {source.Settles} settled");
        return;

        // The rows the kept windows hold counted once each, every view's and in all, whatever joined, grew or was let go.
        static void Counted(StoreAndStream<long, LogRow, long> source, string after)
        {
            var (counted, held, agree) = source.WindowCounts;
            Assert(counted == held && agree, $"{after}: the windows' rows counted once ({held} for {counted}; views agree: {agree})");
        }

        // A view held for 31 s, read every 5 s as a held grid reads it (one unread for 10 s is let go).
        async Task Held(GridView view, GridRead<long, LogRow> read)
        {
            for (var s = 0; s < 31; s += 5)
            {
                clock.Advance(TimeSpan.FromSeconds(Math.Min(5, 31 - s)));
                await StoreRead(source, view, read);
            }
        }
    }

    public static async Task GridStoreAllocation()
    {
        // Warm: each millisecond a version streamed (a new order, or every third a newer version of one), the store landing
        // each eight milliseconds on, a view keeping a second of history; the state tail trimmed by the watermark, the
        // event log by its reach — and, optimised, the live top read, and every fourth step a page about a row below it.
        // 10,000 steps: nothing allocated (the test's store, stream and filter allocate nothing either).
        var clock = new ManualClock();
        var store = new FlatStore(new AppendOnlyStorePolicy
            { Haircut = TimeSpan.FromMilliseconds(50), History = TimeSpan.FromSeconds(1), MaxHistoryRows = 1_000 });
        var stream = new LogStream();
        using var source = new StoreAndStream<long, LogRow, long>(store, stream, new FlatRows(), new LogScopes(), clock);
        using var keep = source.KeepHistory(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await source.GapFilled;
        var reads = Optimised(typeof(StoreAndStream<long, LogRow, long>).Assembly);
        var page = new GridPage<long, LogRow>(4_096);
        var one = new LogRow[1];
        var landing = new LogRow[8];
        var versions = new int[1 << 16];
        long next = 0, wrong = 0;
        for (var i = 0; i < 2_000; i++) Step(i);
        var start = AllocationBaseline();
        for (var i = 2_000; i < 12_000; i++) Step(i);
        var spent = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"  store and stream, warm: {spent:N0} B over 10,000 steps, the tail {source.TailRows} rows, the log {source.HistoryRows}" +
                          (reads ? "" : " (unoptimised: the catch-up's and a read's state machines allocated per call; held to none optimised)"));
        Assert(wrong == 0, $"the live top each step the newest order first: {wrong} wrong");
        await StoreRead(source, GridView.Natural, GridRead<long, LogRow>.AtRank(0, 40)); // (its page the store's newest stamp:
        // the tail trimmed by it, whatever the thread pool's asks managed while the loop ran)
        Assert(source.HistoryRows is > 900 and <= 1_000 && source.TailRows < 100, $"the tails trimmed ({source.TailRows}, {source.HistoryRows})");
        Assert(!reads || spent < 8 * 1024, $"10,000 steps, read: {spent:N0} B allocated");
        return;

        void Step(int i)
        {
            var stamp = (clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / 10;
            var id = i % 3 == 2 && next > 40 ? next - 1 - i % 40 : next++;
            one[0] = new LogRow { Id = id, Version = ++versions[id], Stamp = stamp, Qty = i % 1_000, Status = (byte)(i % 4) };
            stream.Send(one);
            if (i >= 8) store.Land(landing[i % 8]);
            landing[i % 8] = one[0];
            clock.Advance(TimeSpan.FromMilliseconds(1));
            if (!reads) return;
            page.Clear();
            source.ReadAsync(GridView.Natural, GridRead<long, LogRow>.AtRank(0, 40), page, CancellationToken.None).GetAwaiter().GetResult();
            if (page.Count == 0 || page.Rows[0].Id != next - 1) wrong++;
            if (i % 4 != 0 || page.Count < 40) return;
            var about = page.Rows[30];
            page.Clear();
            source.ReadAsync(GridView.Natural, GridRead<long, LogRow>.AroundRow(about.Id, about, 10, 20), page, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (page.Count != 10 + Math.Min(20, about.Id + 1) || page.Rows[10].Id != about.Id) wrong++; // (ids down to 0)
        }
    }
}
