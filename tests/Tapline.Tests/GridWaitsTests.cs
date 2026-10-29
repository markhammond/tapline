using Tapline.Grid.Provider;

/// <summary>A version for the waits: its key, version and stamp, and another timestamp it carries (when its order was placed).</summary>
internal record struct WaitRow
{
    public long Id;
    public int Version;
    public long Stamp; // µs since the Unix epoch
    public long Placed; // µs since the Unix epoch
}

internal sealed class WaitRows : IGridRows<long, WaitRow, long>
{
    public static readonly GridWaitAnchor<WaitRow, long> Placed = (in WaitRow row) => row.Placed;

    public long KeyOf(in WaitRow row)
    {
        return row.Id;
    }

    public long VersionOf(in WaitRow row)
    {
        return row.Version;
    }

    public long StampOf(in WaitRow row)
    {
        return row.Stamp;
    }

    public long Back(long stamp, TimeSpan span)
    {
        return stamp - span.Ticks / 10;
    }

    public TimeSpan Between(long earlier, long later)
    {
        return TimeSpan.FromTicks((later - earlier) * 10);
    }

    public long StampAt(DateTimeOffset time)
    {
        return (time - DateTimeOffset.UnixEpoch).Ticks / 10;
    }

    public IComparer<WaitRow> OrderOf(GridView view)
    {
        throw new NotSupportedException("The waits read no views.");
    }
}

/// <summary>A store the waits prove against: versions land when the test says; its asks counted; refused on request.</summary>
internal sealed class WaitStore(TimeSpan haircut) : IGridStore<long, WaitRow, long>
{
    private readonly Dictionary<long, WaitRow> _current = [];
    private readonly Lock _gate = new();
    private long? _newest;
    public int NewestAsks, Lookups, KeysLookedUp;

    /// <summary>Set, every ask is refused with it, as a store past its bounds, or down, refuses.</summary>
    public string? Refuse;

    public AppendOnlyStorePolicy Policy { get; } = new() { Haircut = haircut };

    public void Land(in WaitRow row)
    {
        lock (_gate)
        {
            if (!_current.TryGetValue(row.Id, out var at) || at.Version < row.Version) _current[row.Id] = row;
            _newest = _newest is { } n ? Math.Max(n, row.Stamp) : row.Stamp;
        }
    }

    public ValueTask<long?> NewestStampAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref NewestAsks);
        if (Refuse is { } why) throw new GridStoreRefusedException(why);
        lock (_gate) return new ValueTask<long?>(_newest);
    }

    public ValueTask EarlierAsync(ReadOnlyMemory<long> keys, ReadOnlyMemory<long> below, uint maxBytes, GridPage<long, WaitRow> page,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Lookups);
        Interlocked.Add(ref KeysLookedUp, keys.Length);
        if (Refuse is { } why) throw new GridStoreRefusedException(why);
        lock (_gate)
            foreach (var key in keys.Span)
                if (_current.TryGetValue(key, out var row))
                    page.Add(key, row);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReadAsync(GridView view, GridSeek<long, WaitRow> seek, int limit, uint maxBytes, GridPage<long, WaitRow> page,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public ValueTask ReadAtAsync(GridView view, long rank, int limit, uint maxBytes, GridPage<long, WaitRow> page,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public ValueTask<(long Rows, TotalKind Kind)?> RankAsync(GridView view, long key, WaitRow row, uint maxBytes,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public ValueTask<(long Rows, TotalKind Kind)?> SizeAsync(GridView view, uint maxBytes, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<WaitRow> SinceAsync(long stamp, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}

/// <summary>What the waits told, as told.</summary>
internal sealed class WaitSink : IGridWaitSink<long, WaitRow, long>
{
    private readonly Lock _gate = new();
    public readonly List<GridOverdue<long, WaitRow, long>> Overdue = [];
    public readonly List<GridWaitEnded<long>> Ended = [];
    public long Untracked;

    void IGridWaitSink<long, WaitRow, long>.Overdue(ReadOnlySpan<GridOverdue<long, WaitRow, long>> overdue)
    {
        lock (_gate) Overdue.AddRange(overdue);
    }

    void IGridWaitSink<long, WaitRow, long>.Ended(ReadOnlySpan<GridWaitEnded<long>> ended)
    {
        lock (_gate) Ended.AddRange(ended);
    }

    void IGridWaitSink<long, WaitRow, long>.Untracked(long keys)
    {
        Interlocked.Add(ref Untracked, keys);
    }

    /// <summary>What was told since last taken.</summary>
    public (GridOverdue<long, WaitRow, long>[] Overdue, GridWaitEnded<long>[] Ended) Take()
    {
        lock (_gate)
        {
            var taken = (Overdue.ToArray(), Ended.ToArray());
            Overdue.Clear();
            Ended.Clear();
            return taken;
        }
    }
}

/// <summary>What the waits told, counted and not kept: a sink that allocates nothing.</summary>
internal sealed class WaitCounts : IGridWaitSink<long, WaitRow, long>
{
    public long Overdue, Ended, Untracked;

    void IGridWaitSink<long, WaitRow, long>.Overdue(ReadOnlySpan<GridOverdue<long, WaitRow, long>> overdue)
    {
        Overdue += overdue.Length;
    }

    void IGridWaitSink<long, WaitRow, long>.Ended(ReadOnlySpan<GridWaitEnded<long>> ended)
    {
        Ended += ended.Length;
    }

    void IGridWaitSink<long, WaitRow, long>.Untracked(long keys)
    {
        Untracked += keys;
    }
}

internal static partial class T
{
    private static long StampNow(ManualClock clock)
    {
        return (clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / 10;
    }

    private static async Task Advance(ManualClock clock, GridWaits<long, WaitRow, long> waits, TimeSpan by)
    {
        clock.Advance(by);
        await waits.IdleAsync();
    }

    public static async Task GridWaitsThresholds()
    {
        // Two thresholds on the version's own stamp: a wait told at each, to the microsecond and not before, proved first
        // (Confirmed); a newer version heard while it waits, told again with it; past the last threshold, let go.
        var clock = new ManualClock();
        var store = new WaitStore(TimeSpan.FromSeconds(1));
        var sink = new WaitSink();
        await using var waits = new GridWaits<long, WaitRow, long>(store, new WaitRows(), sink,
            new GridWaitsOptions { ProveEvery = TimeSpan.FromSeconds(30) }, clock);
        waits.SetWatches([new(TimeSpan.FromSeconds(2)), new(TimeSpan.FromSeconds(5))]);
        var t0 = StampNow(clock);
        waits.Heard([new WaitRow { Id = 1, Version = 1, Stamp = t0 }]);
        await Advance(clock, waits, TimeSpan.FromSeconds(2) - TimeSpan.FromMicroseconds(1));
        var (overdue, ended) = sink.Take();
        Assert(overdue.Length == 0 && store.NewestAsks == 0 && store.Lookups == 0,
            $"a microsecond before its threshold: nothing told, the store asked nothing ({overdue.Length}, {store.NewestAsks}, {store.Lookups})");
        await Advance(clock, waits, TimeSpan.FromMicroseconds(1));
        (overdue, ended) = sink.Take();
        Assert(overdue is [{ Key: 1, Watch.Over.Seconds: 2, Newer: false } at] && at.Waited == TimeSpan.FromSeconds(2) && at.Newest.Version == 1,
            $"at its threshold: told once, waited 2 s ({string.Join(", ", overdue)})");
        Assert(store.NewestAsks == 1 && store.Lookups == 1 && ended.Length == 0,
            $"proved first: the store's newest stamp, then the key ({store.NewestAsks}, {store.Lookups}); still waiting");

        await Advance(clock, waits, TimeSpan.FromMilliseconds(500));
        waits.Heard([new WaitRow { Id = 1, Version = 2, Stamp = t0 + 2_500_000 }]);
        await waits.IdleAsync();
        (overdue, _) = sink.Take();
        Assert(overdue is [{ Key: 1, Watch.Over.Seconds: 2, Newer: true } again] && again.Newest.Version == 2 && again.Waited == TimeSpan.FromMilliseconds(2_500),
            $"a newer version heard past a threshold: told again with it, the wait from the oldest ({string.Join(", ", overdue)})");

        await Advance(clock, waits, TimeSpan.FromMilliseconds(2_500) - TimeSpan.FromMicroseconds(1));
        Assert(sink.Take().Overdue.Length == 0, "a microsecond before the second threshold: nothing");
        await Advance(clock, waits, TimeSpan.FromMicroseconds(1));
        (overdue, ended) = sink.Take();
        Assert(overdue is [{ Key: 1, Watch.Over.Seconds: 5 } last] && last.Waited == TimeSpan.FromSeconds(5), $"at 5 s, the second ({string.Join(", ", overdue)})");
        Assert(ended is [{ Key: 1, Landed: false }] && waits.Count == 0, $"past every threshold, let go ({string.Join(", ", ended)}, {waits.Count})");

        // A newer version of a key let go: a wait of its own, from its own stamp.
        var t1 = StampNow(clock);
        waits.Heard([new WaitRow { Id = 1, Version = 3, Stamp = t1 }]);
        await Advance(clock, waits, TimeSpan.FromSeconds(2));
        (overdue, _) = sink.Take();
        Assert(overdue is [{ Key: 1, Watch.Over.Seconds: 2 } own] && own.Waited == TimeSpan.FromSeconds(2) && own.Newest.Version == 3,
            $"heard again after being let go: its own wait ({string.Join(", ", overdue)})");
    }

    public static async Task GridWaitsAnchors()
    {
        // Two watches of one threshold, one on the version's stamp and one on another timestamp the row carries (when it
        // was placed, a second earlier): two deadlines for the one key, a second apart, each told from its own anchor.
        var clock = new ManualClock();
        var store = new WaitStore(TimeSpan.FromSeconds(1));
        var sink = new WaitSink();
        await using var waits = new GridWaits<long, WaitRow, long>(store, new WaitRows(), sink,
            new GridWaitsOptions { ProveEvery = TimeSpan.FromSeconds(30) }, clock);
        waits.SetWatches([new(TimeSpan.FromSeconds(2)), new(TimeSpan.FromSeconds(2), From: WaitRows.Placed)]);
        var t0 = StampNow(clock);
        waits.Heard([new WaitRow { Id = 7, Version = 1, Stamp = t0, Placed = t0 - 1_000_000 }]);
        await Advance(clock, waits, TimeSpan.FromSeconds(1));
        var (overdue, _) = sink.Take();
        Assert(overdue is [{ Key: 7, Watch.From: not null } placed] && placed.Waited == TimeSpan.FromSeconds(2),
            $"a second on: placed two seconds ago, told on that anchor ({string.Join(", ", overdue)})");
        await Advance(clock, waits, TimeSpan.FromSeconds(1));
        (overdue, var ended) = sink.Take();
        Assert(overdue is [{ Key: 7, Watch.From: null } stamped] && stamped.Waited == TimeSpan.FromSeconds(2),
            $"two seconds on: told on its stamp ({string.Join(", ", overdue)})");
        Assert(ended is [{ Key: 7, Landed: false }], "both told: let go");

        // The watches changed while a key waits: one kept keeps what was told of it, in its new place; one gone is
        // forgotten; a new one starts on it.
        waits.SetWatches([new(TimeSpan.FromSeconds(2)), new(TimeSpan.FromSeconds(4))]);
        waits.Heard([new WaitRow { Id = 8, Version = 1, Stamp = StampNow(clock) }]);
        await Advance(clock, waits, TimeSpan.FromSeconds(2));
        Assert(sink.Take().Overdue is [{ Key: 8, Watch.Over.Seconds: 2 }], "told at 2 s");
        waits.SetWatches([new(TimeSpan.FromSeconds(3)), new(TimeSpan.FromSeconds(2))]);
        await Advance(clock, waits, TimeSpan.FromSeconds(1));
        (overdue, ended) = sink.Take();
        Assert(overdue is [{ Key: 8, Watch.Over.Seconds: 3 } three] && three.Waited == TimeSpan.FromSeconds(3) && ended is [{ Key: 8, Landed: false }],
            $"the 2 s watch, moved to second place, not told again; the new 3 s one told, then let go ({string.Join(", ", overdue)})");
    }

    public static async Task GridWaitsProof()
    {
        var clock = new ManualClock();
        var store = new WaitStore(TimeSpan.FromSeconds(1));
        var sink = new WaitSink();
        var options = new GridWaitsOptions { ProveEvery = TimeSpan.FromSeconds(30), RetryAfter = TimeSpan.FromSeconds(1) };
        await using var waits = new GridWaits<long, WaitRow, long>(store, new WaitRows(), sink, options, clock);
        waits.SetWatches([new(TimeSpan.FromSeconds(2))]);

        // Landed before its deadline: the lookup finds it, nothing told, the wait ended landed.
        var t0 = StampNow(clock);
        var one = new WaitRow { Id = 1, Version = 1, Stamp = t0 };
        waits.Heard([one]);
        await Advance(clock, waits, TimeSpan.FromSeconds(1));
        store.Land(one);
        await Advance(clock, waits, TimeSpan.FromSeconds(1));
        var (overdue, ended) = sink.Take();
        Assert(overdue.Length == 0 && ended is [{ Key: 1, Landed: true }] && store.Lookups == 1,
            $"landed before its deadline: found by the lookup, not told ({overdue.Length}, {string.Join(", ", ended)}, {store.Lookups})");

        // Stamped a haircut behind the store's newest: landed by the store's promise, no lookup.
        var t1 = StampNow(clock);
        var two = new WaitRow { Id = 2, Version = 1, Stamp = t1 };
        waits.Heard([two]);
        store.Land(two);
        store.Land(new WaitRow { Id = 99, Version = 1, Stamp = t1 + 1_500_000 });
        var lookups = store.Lookups;
        await Advance(clock, waits, TimeSpan.FromSeconds(2));
        (overdue, ended) = sink.Take();
        Assert(overdue.Length == 0 && ended is [{ Key: 2, Landed: true }] && store.Lookups == lookups,
            $"a haircut behind the newest stamp: proved landed without a lookup ({string.Join(", ", ended)}, {store.Lookups - lookups})");

        // Out of order: the older version lands alone, and the wait runs on from the newer one.
        var t2 = StampNow(clock);
        var older = new WaitRow { Id = 3, Version = 1, Stamp = t2 };
        waits.Heard([older]);
        await Advance(clock, waits, TimeSpan.FromSeconds(1));
        waits.Heard([new WaitRow { Id = 3, Version = 2, Stamp = t2 + 1_000_000 }]);
        await Advance(clock, waits, TimeSpan.FromMilliseconds(500));
        store.Land(older);
        await Advance(clock, waits, TimeSpan.FromMilliseconds(500));
        (overdue, ended) = sink.Take();
        Assert(overdue.Length == 0 && ended.Length == 0, $"the older version landed: its wait moved on to the newer, a second old ({overdue.Length}, {ended.Length})");
        await Advance(clock, waits, TimeSpan.FromSeconds(1));
        (overdue, _) = sink.Take();
        Assert(overdue is [{ Key: 3 } moved] && moved.Waited == TimeSpan.FromSeconds(2) && moved.Newest.Version == 2,
            $"told two seconds after the newer version ({string.Join(", ", overdue)})");

        // A proof the store refuses: deferred, never told unproved; told once it answers.
        store.Refuse = "down for maintenance";
        waits.Heard([new WaitRow { Id = 4, Version = 1, Stamp = StampNow(clock) }]);
        await Advance(clock, waits, TimeSpan.FromSeconds(2));
        Assert(sink.Take().Overdue.Length == 0 && waits.Fault is GridStoreRefusedException, "refused: deferred, the fault kept");
        store.Refuse = null;
        await Advance(clock, waits, TimeSpan.FromSeconds(1));
        (overdue, _) = sink.Take();
        Assert(overdue is [{ Key: 4 } late] && late.Waited == TimeSpan.FromSeconds(3) && waits.Fault is null,
            $"the retry, a second on: proved, told, the fault cleared ({string.Join(", ", overdue)}, {waits.Fault?.Message})");
    }

    public static async Task GridWaitsTrusted()
    {
        // Trusted and confirmed watches of one threshold: the row lands between proofs. The trusted one is told at its
        // deadline on what the last proof found, the store not asked on its way; the confirmed one is proved, and not told.
        var clock = new ManualClock();
        var store = new WaitStore(TimeSpan.FromSeconds(1));
        var sink = new WaitSink();
        await using (var trusted = new GridWaits<long, WaitRow, long>(store, new WaitRows(), sink,
                         new GridWaitsOptions { ProveEvery = TimeSpan.FromSeconds(30) }, clock))
        {
            trusted.SetWatches([new(TimeSpan.FromSeconds(2), GridWaitProof.Trusted)]);
            var row = new WaitRow { Id = 1, Version = 1, Stamp = StampNow(clock) };
            trusted.Heard([row]);
            await Advance(clock, trusted, TimeSpan.FromSeconds(1));
            store.Land(row);
            await Advance(clock, trusted, TimeSpan.FromSeconds(1));
            Assert(sink.Take().Overdue is [{ Key: 1, Watch.Proof: GridWaitProof.Trusted }] && store.NewestAsks == 0 && store.Lookups == 0,
                $"trusted: told at its deadline, the store not asked ({store.NewestAsks}, {store.Lookups})");
        }

        await using var both = new GridWaits<long, WaitRow, long>(store, new WaitRows(), sink,
            new GridWaitsOptions { ProveEvery = TimeSpan.FromSeconds(1) }, clock);
        both.SetWatches([new(TimeSpan.FromSeconds(2), GridWaitProof.Trusted), new(TimeSpan.FromSeconds(2))]);
        var five = new WaitRow { Id = 5, Version = 1, Stamp = StampNow(clock) };
        both.Heard([five]);
        await Advance(clock, both, TimeSpan.FromSeconds(1)); // (proved: waiting)
        await Advance(clock, both, TimeSpan.FromMilliseconds(500));
        store.Land(five);
        await Advance(clock, both, TimeSpan.FromMilliseconds(500));
        var (overdue, ended) = sink.Take();
        Assert(overdue is [{ Key: 5, Watch.Proof: GridWaitProof.Trusted }] && ended is [{ Key: 5, Landed: true }],
            $"landed since the last proof: the trusted watch told, the confirmed one proved and not ({string.Join(", ", overdue)}; {string.Join(", ", ended)})");

        // While a trusted watch is in force, every tracked wait proved each second: landed ones ended between deadlines.
        var six = new WaitRow { Id = 6, Version = 1, Stamp = StampNow(clock) };
        both.Heard([six]);
        store.Land(six);
        await Advance(clock, both, TimeSpan.FromSeconds(1));
        (overdue, ended) = sink.Take();
        Assert(overdue.Length == 0 && ended is [{ Key: 6, Landed: true }] && both.Count == 0,
            $"proved within a second, before its deadline ({string.Join(", ", ended)}, {both.Count})");
    }

    public static async Task GridWaitsGathered()
    {
        // Ten keys heard 3 ms apart, from a millisecond into a grain (50 ms, the default), waits of a second confirmed: none
        // told at its own threshold, all at the grain's end, by one lookup — the store's newest stamp not asked, no version
        // waited on a haircut (7 s) old. A grain of none: each at its own moment.
        var clock = new ManualClock();
        var store = new WaitStore(TimeSpan.FromSeconds(7));
        var sink = new WaitSink();
        await using var waits = new GridWaits<long, WaitRow, long>(store, new WaitRows(), sink,
            new GridWaitsOptions { ProveEvery = TimeSpan.FromSeconds(30) }, clock);
        waits.SetWatches([new(TimeSpan.FromSeconds(1))]);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 10; i++)
        {
            waits.Heard([new WaitRow { Id = i, Version = 1, Stamp = StampNow(clock) }]);
            clock.Advance(TimeSpan.FromMilliseconds(3));
        }

        await Advance(clock, waits, TimeSpan.FromMilliseconds(970));
        Assert(sink.Take().Overdue.Length == 0 && store.Lookups == 0, "at the first key's threshold: nothing yet, its grain not ended");
        await Advance(clock, waits, TimeSpan.FromMilliseconds(49));
        var (overdue, _) = sink.Take();
        Assert(overdue.Length == 10 && overdue.All(o => o.Waited >= TimeSpan.FromSeconds(1)) &&
               store is { Lookups: 1, KeysLookedUp: 10, NewestAsks: 0 },
            $"at the grain's end, all ten, none early, one lookup and no newest stamp ({overdue.Length}, {store.Lookups}, {store.KeysLookedUp}, {store.NewestAsks})");

        await using var exact = new GridWaits<long, WaitRow, long>(store, new WaitRows(), sink,
            new GridWaitsOptions { ProveEvery = TimeSpan.FromSeconds(30), Grain = TimeSpan.Zero }, clock);
        exact.SetWatches([new(TimeSpan.FromSeconds(1))]);
        exact.Heard([new WaitRow { Id = 100, Version = 1, Stamp = StampNow(clock) }]);
        clock.Advance(TimeSpan.FromMilliseconds(3));
        exact.Heard([new WaitRow { Id = 101, Version = 1, Stamp = StampNow(clock) }]);
        await Advance(clock, exact, TimeSpan.FromMilliseconds(997));
        Assert(sink.Take().Overdue is [{ Key: 100 }], "no grain: the first at its own threshold");
        await Advance(clock, exact, TimeSpan.FromMilliseconds(3));
        Assert(sink.Take().Overdue is [{ Key: 101 }], "and the second at its own");
    }

    public static async Task GridWaitsBounds()
    {
        // Past the cap: the oldest kept, new keys not tracked, the sink told how many.
        var clock = new ManualClock();
        var store = new WaitStore(TimeSpan.FromSeconds(1));
        var sink = new WaitSink();
        await using (var capped = new GridWaits<long, WaitRow, long>(store, new WaitRows(), sink,
                         new GridWaitsOptions { MaxWaits = 3, ProveEvery = TimeSpan.FromSeconds(30) }, clock))
        {
            capped.SetWatches([new(TimeSpan.FromSeconds(1), GridWaitProof.Trusted)]);
            var t = StampNow(clock);
            capped.Heard([.. Enumerable.Range(1, 5).Select(i => new WaitRow { Id = i, Version = 1, Stamp = t })]);
            await capped.IdleAsync();
            Assert(capped.Count == 3 && Interlocked.Read(ref sink.Untracked) == 2, $"three tracked, two not, told ({capped.Count}, {sink.Untracked})");
            await Advance(clock, capped, TimeSpan.FromSeconds(1));
            var told = sink.Take().Overdue.Select(o => o.Key).Order().ToArray();
            Assert(told is [1, 2, 3], $"the first three, the oldest, told ({string.Join(", ", told)})");
        }

        // The store stalled: a key heard each 10 ms for 10 s, none landing. Each let go once past the threshold, so the
        // waits held stay at the rate times the threshold and a grain, a stall's length costing nothing more.
        sink.Take();
        await using var stalled = new GridWaits<long, WaitRow, long>(store, new WaitRows(), sink,
            new GridWaitsOptions { ProveEvery = TimeSpan.FromSeconds(1) }, clock);
        stalled.SetWatches([new(TimeSpan.FromSeconds(1))]);
        var most = 0;
        for (var i = 0; i < 1_000; i++)
        {
            stalled.Heard([new WaitRow { Id = 1_000 + i, Version = 1, Stamp = StampNow(clock) }]);
            await Advance(clock, stalled, TimeSpan.FromMilliseconds(10));
            most = Math.Max(most, stalled.Count);
        }

        var (overdue, ended) = sink.Take();
        Console.WriteLine($"  a stall of 10 s: at most {most} waits held, {overdue.Length} told, {ended.Length} let go; {stalled.Memory.Held:N0} B held");
        Assert(most <= 105 && overdue.Length >= 899 && ended.Length == overdue.Length && ended.All(e => !e.Landed),
            $"held at the rate times the threshold and a grain ({most}, {overdue.Length}, {ended.Length})");
    }

    public static async Task GridWaitsAllocation()
    {
        // No watch: versions heard, nothing kept. Watched: a hundred keys each a newer version every 100 ms, the store
        // landing each 8 ms on, proofs every 50 ms ending the waits a haircut behind its newest stamp — 10,000 versions
        // heard allocating nothing, and (optimised) nor the passes, run on the clock's thread as their deadlines come.
        var clock = new ManualClock();
        var store = new WaitStore(TimeSpan.FromMilliseconds(20));
        var counts = new WaitCounts();
        await using var waits = new GridWaits<long, WaitRow, long>(store, new WaitRows(), counts,
            new GridWaitsOptions { ProveEvery = TimeSpan.FromMilliseconds(50) }, clock);
        var passes = Optimised(typeof(GridWaits<long, WaitRow, long>).Assembly);
        var one = new WaitRow[1];
        var versions = new int[100];
        var landing = new WaitRow[8];
        var start = AllocationBaseline();
        for (var i = 0; i < 10_000; i++)
        {
            one[0] = new WaitRow { Id = i % 100, Version = ++versions[i % 100], Stamp = StampNow(clock) };
            waits.Heard(one);
        }

        var unwatched = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert(waits.Count == 0 && unwatched < 8 * 1024, $"no watch: nothing kept, {unwatched:N0} B allocated");

        waits.SetWatches([new(TimeSpan.FromSeconds(30))]);
        long heard = 0;
        for (var i = 0; i < 2_000; i++) Step(i);
        (heard, start) = (0, AllocationBaseline());
        for (var i = 2_000; i < 12_000; i++) Step(i);
        var spent = GC.GetAllocatedBytesForCurrentThread() - start;
        await waits.IdleAsync();
        Console.WriteLine($"  waits, warm: {heard:N0} B heard, {spent:N0} B in all over 10,000 versions and {counts.Ended:N0} waits ended, " +
                          $"{waits.Count} waiting" + (passes ? "" : " (unoptimised: a pass's state machines allocated per call; held to none optimised)"));
        Assert(waits.Count <= 100 && counts.Overdue == 0 && counts.Ended > 9_000 && store.Lookups == 0,
            $"the waits ended by the proofs, the store's newest stamp alone ({waits.Count}, {counts.Overdue}, {counts.Ended}, {store.Lookups})");
        Assert(heard < 8 * 1024, $"10,000 versions heard: {heard:N0} B allocated");
        Assert(!passes || spent < 8 * 1024, $"10,000 versions heard, their passes run: {spent:N0} B allocated");
        return;

        void Step(int i)
        {
            var id = i % 100;
            one[0] = new WaitRow { Id = id, Version = ++versions[id], Stamp = StampNow(clock) };
            var before = GC.GetAllocatedBytesForCurrentThread();
            waits.Heard(one);
            heard += GC.GetAllocatedBytesForCurrentThread() - before;
            if (i >= 8) store.Land(landing[i % 8]);
            landing[i % 8] = one[0];
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }
    }
}
