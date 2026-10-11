using Tapline;
using Tapline.Tests;

/// <summary>A subscription's own rate: its MinInterval, conflating; Hurry and a source's Barrier letting one through.</summary>
internal static partial class T
{
    public static Task SubscriptionMinInterval()
    {
        var clock = new ManualTime();
        var wheel = new TimingWheel(TimeSpan.FromMilliseconds(5), time: clock, autoStart: false);
        var s = NewOutlet(new OutletRegistry(wheel));
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var values = new LastValues<int>("rated");
        var slow = s.Tap(values[0], activate: true);
        var fast = s.Tap(values[1], activate: true);
        slow.MinInterval = TimeSpan.FromMilliseconds(100);
        Assert(slow.MinInterval == TimeSpan.FromMilliseconds(100), "its interval, as set");

        byte Last(Tap t) => c.Values.TryGetValue(t.Id, out var v) ? v[0] : (byte)0;

        values.Publish(0, [1]);
        values.Publish(1, [1]);
        DrainFrames(s, owner, c);
        Assert(Last(slow) == 1 && Last(fast) == 1, "a first state at once, its interval or not");

        values.Publish(0, [2]);
        values.Publish(0, [3]);
        values.Publish(1, [2]);
        DrainFrames(s, owner, c);
        Assert(Last(slow) == 1 && Last(fast) == 2, $"within its interval: held ({Last(slow)}), another subscription not ({Last(fast)})");

        // A mailbox entry ordered after its state (a rendezvous, a Tap.Sync) waits with it: the post doesn't let it out.
        Assert(slow.TryRendezvous([42], values[0].Version), "a rendezvous after its held state, posted");
        DrainFrames(s, owner, c);
        Assert(Last(slow) == 1 && c.Rendezvous.Count == 0, $"still held, the rendezvous with it ({Last(slow)})");

        clock.Advance(TimeSpan.FromMilliseconds(100));
        DrainFrames(s, owner, c);
        Assert(Last(slow) == 3, $"its interval up: the state as it stands, 2 conflated into 3 ({Last(slow)})");
        Assert(c.Rendezvous is [{ Correlation: [42] }], "then the rendezvous, after it");

        values.Publish(0, [4]);
        DrainFrames(s, owner, c);
        Assert(Last(slow) == 3, "sent one: the next waits again");
        slow.Hurry();
        DrainFrames(s, owner, c);
        Assert(Last(slow) == 4, "hurried: at once (a client waiting for its own change)");

        values.Publish(0, [5]);
        DrainFrames(s, owner, c);
        Assert(Last(slow) == 4, "and waits again after");
        values[0].Barrier();
        DrainFrames(s, owner, c);
        Assert(Last(slow) == 5, "a barrier: every subscription of the source sent its state now");

        clock.Advance(TimeSpan.FromMilliseconds(200));
        var before = c.Values.Count;
        Assert(DrainFrames(s, owner, c) >= 0 && Last(slow) == 5 && c.Values.Count == before,
            "its interval up with nothing changed: nothing more");

        // A message topic's stream isn't held: nothing of it conflates.
        var topic = new MessageTopic(64, 64 * 1024);
        var messages = s.Tap(topic, activate: true);
        messages.MinInterval = TimeSpan.FromSeconds(10);
        topic.Publish([1]);
        topic.Publish([2]);
        DrainFrames(s, owner, c);
        topic.Publish([3]);
        DrainFrames(s, owner, c);
        Assert(c.TopicMessages.Count(m => m.TapId == messages.Id) == 3, "a message topic's: every message, at once");

        slow.MinInterval = TimeSpan.Zero;
        values.Publish(0, [6]);
        DrainFrames(s, owner, c);
        Assert(Last(slow) == 6, "its interval taken away: at once again");

        // A table fed by an ingest (the router's fold applying posts on its own thread): hurried once its post is
        // applied, its state goes at once — a client waiting for its own change, as the pivot's provider does it.
        var add = Tapline.BatchFold.Of<long, long, Add<long>>();
        var book = new Table<Level, long>("RATED-BOOK");
        var ingest = book.Ingest<long>(add);
        var booked = s.Tap(book, activate: true);
        booked.MinInterval = TimeSpan.FromMilliseconds(100);
        ingest.Post([new Level(0, 100)], [5L], upstreamVersion: 1);
        for (var i = 0; i < 200 && book.UpstreamVersion < 1; i++) Thread.Sleep(5);
        DrainFrames(s, owner, c);
        Assert(c.Tables[booked.Id].Seq >= 0 && c.Tables[booked.Id].Rows.Count == 1, "its first state");
        ingest.Post([new Level(0, 100)], [2L], 1, 2);
        var applied = new ManualResetEventSlim();
        ingest.WhenApplied(_ =>
        {
            booked.Hurry();
            applied.Set();
        });
        Assert(applied.Wait(2000), "applied");
        DrainFrames(s, owner, c);
        var row = c.Tables[booked.Id].Rows.Values.Single().Row;
        Assert(book.UpstreamVersion == 2 && BitConverter.ToInt64(row) == 7,
            $"hurried once applied: its state at once ({BitConverter.ToInt64(row)})");
        return Task.CompletedTask;
    }
}

/// <summary>BatchPipe: every batch, in order, to a slow reader; the producer held back past its batches; nothing allocated.</summary>
internal static partial class T
{
    private sealed class Numbered
    {
        public int[] Items = new int[4];
        public int Count;
        public long Version;
    }

    public static async Task BatchPipeRoundTrip()
    {
        var pipe = new BatchPipe<Numbered>(4, () => new Numbered());
        const int Versions = 2000;
        var producer = Task.Run(async () =>
        {
            for (var v = 1; v <= Versions; v++)
            {
                var batch = await pipe.RentAsync();
                batch.Count = 0;
                batch.Version = v;
                for (var i = 0; i < v % 7; i++) // (a batch grows to what it carries, and keeps it)
                {
                    if (batch.Count == batch.Items.Length) Array.Resize(ref batch.Items, batch.Count * 2);
                    batch.Items[batch.Count++] = v;
                }

                pipe.Publish(batch);
            }

            pipe.Complete();
        });
        long expected = 1, read = 0;
        var wrong = 0;
        await foreach (var batch in pipe.ReadAllAsync())
        {
            if (batch.Version != expected++ || batch.Count != batch.Version % 7) wrong++;
            if (++read % 100 == 0) await Task.Delay(2); // (a slow reader, now and then)
            pipe.Return(batch);
        }

        await producer;
        Assert(read == Versions && wrong == 0, $"every batch, in order, whole: {read} read, {wrong} wrong");
        Assert(pipe.MaxInFlight <= pipe.Capacity && pipe.Waits > 0 && pipe.InFlight == 0,
            $"the producer held back past its batches ({pipe.Waits} waits, {pipe.WaitedMs:F0} ms), never more in flight than it has ({pipe.MaxInFlight})");

        // Going round on one thread, every step at once: nothing allocated.
        var quick = new BatchPipe<Numbered>(2, () => new Numbered());
        for (var i = 0; i < 100; i++) // (warmed)
        {
            var b = await quick.RentAsync();
            quick.Publish(b);
            quick.TryRead(out var r);
            quick.Return(r);
        }

        var before = AllocationBaseline();
        for (var i = 0; i < 100_000; i++)
        {
            var b = await quick.RentAsync();
            quick.Publish(b);
            quick.TryRead(out var r);
            quick.Return(r);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated < 8 * 1024, $"100,000 batches round: {allocated:N0} B allocated");

        // A batch rented and not published (nothing to send): kept on the producer's side, rented again first.
        var spare = await quick.RentAsync();
        quick.Unrent(spare);
        Assert(ReferenceEquals(await quick.RentAsync(), spare), "an unpublished batch, rented again first");
    }
}

/// <summary>SpscRing: one writer, one reader, no lock — everything in order across threads, waits on both sides, nothing allocated.</summary>
internal static partial class T
{
    public static async Task SpscRingRoundTrip()
    {
        // A million items through eight slots, a thread each side: both sides wait, often; every item, in order.
        var ring = new SpscRing<long>(8);
        const long Items = 1_000_000;
        var before = GC.GetTotalAllocatedBytes(true);
        var writer = Task.Run(async () =>
        {
            for (long i = 1; i <= Items; i++) await ring.WriteAsync(i);
            ring.Complete();
        });
        var reader = Task.Run(async () =>
        {
            long expected = 1, wrong = 0;
            await foreach (var item in ring.ReadAllAsync())
                if (item != expected++)
                    wrong++;
            return (Read: expected - 1, Wrong: wrong);
        });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await writer;
        var (read, outOfOrder) = await reader;
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Console.WriteLine($"  spsc ring: {Items:N0} items through 8 slots in {timer.ElapsedMilliseconds:N0} ms, {allocated:N0} B allocated");
        Assert(read == Items && outOfOrder == 0, $"every item, in order: {read:N0} read, {outOfOrder} out of order");
        Assert(allocated < 256 * 1024,
            $"a million items through eight slots, both sides waiting: {allocated:N0} B allocated (the process's: its waits pooled)");
        Assert(!await ring.WaitToReadAsync() && ring.IsCompleted, "completed and empty: reading ends");

        // A reference read is let go of: the slot no longer holds it.
        var objects = new SpscRing<object>(2);
        var held = WrittenAndRead(objects); // (not here: an async method's frame and state machine may hold what it read)
        GC.Collect();
        Assert(!held.IsAlive, "a slot read is cleared");
        GC.KeepAlive(objects); // (the ring alive throughout: only its slot could hold the item)

        // Full: TryWrite says so; a write waits for room, the read making it.
        var small = new SpscRing<int>(2);
        Assert(small.TryWrite(1) && small.TryWrite(2) && !small.TryWrite(3), "full: TryWrite says so");
        var waiting = small.WriteAsync(3).AsTask();
        Assert(!waiting.IsCompleted, "a write waits for room");
        small.TryRead(out _);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(small.Count == 2, "room made: written");

        // A wait cancelled: the reader's, on an empty ring.
        var empty = new SpscRing<int>(4);
        using var cancel = new CancellationTokenSource(50);
        var cancelled = false;
        try
        {
            await empty.ReadAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled, "a wait cancelled");
        Assert(empty.TryWrite(7) && await empty.ReadAsync() == 7, "and the ring as it was after");

        // One thread going round, every step at once: nothing allocated.
        var quick = new SpscRing<int>(4);
        for (var i = 0; i < 100; i++)
        {
            quick.TryWrite(i);
            quick.TryRead(out _);
        }

        var start = AllocationBaseline();
        for (var i = 0; i < 1_000_000; i++)
        {
            quick.TryWrite(i);
            quick.TryRead(out _);
        }

        var spent = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert(spent < 8 * 1024, $"a million items round on one thread: {spent:N0} B allocated");
    }

    public static async Task SpscByteRingRoundTrip()
    {
        // Two million messages of 1 to 200 bytes through 4 KiB, a thread each side: the writer retrying when it's full,
        // the reader spinning, then parking — every message whole and in order, wrapping (a pad) hundreds of times.
        const int Messages = 2_000_000;
        using var ring = new SpscByteRing(4096);
        long failed = 0;
        var writer = new Thread(() =>
        {
            for (var i = 0; i < Messages; i++)
            {
                var length = 1 + i % 200;
                Span<byte> at;
                while ((at = ring.TryClaim(length)).IsEmpty)
                {
                    failed++;
                    Thread.SpinWait(8);
                }

                at.Fill((byte)i);
                if (length >= 4) System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(at, i);
                ring.Commit();
            }

            ring.Complete();
        });
        long read = 0, wrong = 0, parks = 0;
        var reader = new Thread(() =>
        {
            var spinner = new SpinWait();
            while (true)
            {
                var message = ring.TryRead();
                if (message.IsEmpty)
                {
                    if (ring.IsCompleted && ring.TryRead().IsEmpty) return;
                    if (!spinner.NextSpinWillYield)
                    {
                        spinner.SpinOnce(-1);
                        continue;
                    }

                    parks++;
                    ring.Park();
                    spinner.Reset();
                    continue;
                }

                var i = (int)read++;
                var length = 1 + i % 200;
                if (message.Length != length || (length != 4 && message[^1] != (byte)i) ||
                    (length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(message) != i))
                    wrong++;
                ring.Release();
            }
        });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        reader.Start();
        writer.Start();
        writer.Join();
        reader.Join();
        Console.WriteLine($"  spsc byte ring: {Messages:N0} messages through 4 KiB in {timer.ElapsedMilliseconds:N0} ms, the reader parked {parks:N0}×");
        Assert(read == Messages && wrong == 0, $"every message, whole and in order: {read:N0} read, {wrong} wrong");
        Assert(ring.Dropped == failed, $"every claim that failed counted as a drop ({ring.Dropped:N0}, the writer retrying)");

        // Full: a claim fails, and counts; reading makes room. A message too long for it, refused.
        using var full = new SpscByteRing(4096);
        var claimed = 0;
        while (!full.TryClaim(1000).IsEmpty)
        {
            full.Commit();
            claimed++;
        }

        Assert(claimed == 4 && full.Dropped == 1, $"full: a claim fails and is counted ({claimed} fit, {full.Dropped} dropped)");
        Assert(full.TryRead().Length == 1000, "the first, as written");
        full.Release();
        Assert(!full.TryClaim(1000).IsEmpty, "room made");
        full.Commit();
        var refused = false;
        try
        {
            full.TryClaim(full.MaxMessageLength + 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            refused = true;
        }

        Assert(refused && full.MaxMessageLength == 2048 - 8, "a message longer than half the ring, less its record: refused");

        // Each message 8-byte aligned, from the first: a reader may cast one in place.
        using var aligned = new SpscByteRing(4096);
        aligned.TryClaim(13);
        aligned.Commit();
        aligned.TryClaim(40);
        aligned.Commit();
        ref var first = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(aligned.TryRead());
        aligned.Release();
        ref var second = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(aligned.TryRead());
        aligned.Release();
        Assert(System.Runtime.CompilerServices.Unsafe.ByteOffset(ref first, ref second) == 24,
            "13 bytes, then the next 8-byte aligned (its record before it)");

        // An async reader: waits allocation-free; completed and empty, the wait answers false.
        using var tasks = new SpscByteRing(4096);
        const int Async = 200_000;
        var before = GC.GetTotalAllocatedBytes(true);
        var producer = Task.Run(() =>
        {
            for (var i = 0; i < Async; i++)
            {
                Span<byte> at;
                while ((at = tasks.TryClaim(64)).IsEmpty) Thread.Yield();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(at, i);
                tasks.Commit();
            }

            tasks.Complete();
        });
        var consumer = Task.Run(async () =>
        {
            long got = 0, bad = 0;
            while (await tasks.WaitToReadAsync())
                while (Next(tasks, got, ref bad))
                    got++;
            return (got, bad);
        });
        await producer;
        var (gotAsync, badAsync) = await consumer;
        var allocatedAsync = GC.GetTotalAllocatedBytes(true) - before;
        Assert(gotAsync == Async && badAsync == 0, $"the async reader: {gotAsync:N0} read, {badAsync} wrong");
        Assert(allocatedAsync < 256 * 1024, $"its waits pooled: {allocatedAsync:N0} B allocated (the process's)");
        Assert(!await tasks.WaitToReadAsync(), "completed and empty: the wait answers false");

        // One thread going round: nothing allocated.
        using var quick = new SpscByteRing(4096);
        for (var i = 0; i < 1_000_000; i++) RoundTrip(quick, i);
        var start = AllocationBaseline();
        for (var i = 0; i < 1_000_000; i++) RoundTrip(quick, i);
        var spent = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert(spent < 8 * 1024, $"a million messages round on one thread: {spent:N0} B allocated");
    }

    private static bool Next(SpscByteRing ring, long expected, ref long bad)
    {
        var message = ring.TryRead();
        if (message.IsEmpty) return false;
        if (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(message) != expected) bad++;
        ring.Release();
        return true;
    }

    private static void RoundTrip(SpscByteRing ring, int i)
    {
        ring.TryClaim(8 + i % 100)[0] = 1;
        ring.Commit();
        ring.TryRead();
        ring.Release();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference WrittenAndRead(SpscRing<object> ring)
    {
        var held = new WeakReference(Hold(ring));
        ring.TryRead(out _);
        return held;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static object Hold(SpscRing<object> ring)
    {
        var o = new object();
        ring.TryWrite(o);
        return o;
    }
}
