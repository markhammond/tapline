using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Tapline;
using Tapline.Client;

/// <summary>Tapline.Client against a router's outlet: frames into the client, its control messages straight back.</summary>
internal static partial class T
{
    /// <summary>
    ///     Frames from <paramref name="outlet" /> into <paramref name="client" /> until it has nothing more — each
    ///     copied, as a transport does (and tampered with, if asked).
    /// </summary>
    private static int Pump(Outlet outlet, object owner, TaplineClient client, Func<byte[], byte[]>? tamper = null)
    {
        var frames = 0;
        for (var idle = 0; idle < 3 && frames < 10_000;)
        {
            if (!outlet.TryBuildFrame(owner, false, out var frame, out var seq))
            {
                idle++;
                continue;
            }

            idle = 0;
            frames++;
            var bytes = frame.ToArray();
            outlet.ReleaseFrame();
            client.Receive(seq, tamper is null ? bytes : tamper(bytes));
        }

        return frames;
    }

    /// <summary>
    ///     Pumps, then waits for the out-of-band fetches it started (each confirmed: the router goes on), until all is
    ///     quiet.
    /// </summary>
    private static async Task Settle(Outlet outlet, object owner, TaplineClient client)
    {
        for (var round = 0; round < 50; round++)
        {
            var frames = Pump(outlet, owner, client);
            await client.WhenIdleAsync();
            if (frames == 0 && Pump(outlet, owner, client) == 0) return;
        }
    }

    /// <summary>
    ///     A client whose out-of-band fetches lease the blob from <paramref name="registry" />'s store, as
    ///     Tapline.AspNetCore's endpoint does.
    /// </summary>
    private static TaplineClient ClientOf(LoopConnection loop, OutletRegistry registry)
    {
        return new TaplineClient(loop, new TaplineClientOptions
        {
            BlobBase = new Uri("http://router.test/_tapline/blob/"),
            Fetch = (url, _) =>
            {
                var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (!registry.Blobs.TryLease(parts[^3], int.Parse(parts[^2]), parts[^1], registry, out var lease))
                    throw new InvalidOperationException($"no blob at {url}");
                using (lease)
                {
                    return Task.FromResult(lease.Data.ToArray());
                }
            }
        });
    }

    private static void AssertReplica(TableReplica? replica, Table<Level, long> table, string what)
    {
        Assert(replica is not null, $"{what}: no replica");
        var rows = new List<KeyValuePair<Level, long>>();
        table.CopyTo(rows);
        Assert(
            replica.Verified && replica.Count == rows.Count && replica.Checksum == table.Checksum &&
            replica.Version == table.Version,
            $"{what}: {replica.Count} rows at version {replica.Version} (checksum {replica.Checksum:X8}), the table {rows.Count} at {table.Version} ({table.Checksum:X8})");
        foreach (var (level, value) in rows)
        {
            var key = level;
            var expected = value;
            Assert(
                replica.TryGetRow(MemoryMarshal.AsBytes(new ReadOnlySpan<Level>(in key)), out var row) &&
                row.SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<long>(in expected))),
                $"{what}: the row of {level}");
        }
    }

    private static void Churn(Table<Level, long> book, Random rnd)
    {
        using var u = book.BeginUpdate();
        for (var i = rnd.Next(1, 6); i > 0; i--)
        {
            var level = new Level((byte)rnd.Next(2), 1000 + rnd.Next(80));
            if (rnd.Next(4) == 0) u.Delete(level);
            else u.Set(level, rnd.Next(1, 5_000_000));
        }

        u.Complete();
    }

    private static byte[] Int64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    public static async Task ClientReplicas()
    {
        var registry = new OutletRegistry();
        var outlet = NewOutlet(registry, o => o.FrameBudget = 2048);
        var owner = new object();
        outlet.Attach(owner);
        var loop = new LoopConnection(outlet);
        var client = new TaplineClient(loop);
        var book = new Table<Level, long>("CLIENT", 4096);
        var mark = new LastValueSource();
        var alerts = new MessageTopic(4096);

        // Step 1 on the server, step 2 by the client — subscribed before it's bound: activated once it is.
        var bookTap = outlet.Tap(book, "book");
        int snapshots = 0, deltas = 0, entries = 0, unverified = 0;
        var mail = new List<long>();
        var books = client.Subscribe(bookTap.Binding, new SubscriptionHandler
        {
            Table = (_, table, batch) =>
            {
                if (batch.IsSnapshot) snapshots++;
                else deltas++;
                foreach (var entry in batch) entries++;
                if (!table.Verified) unverified++;
            },
            Message = (_, message, info) =>
            {
                if (info.Mailbox) mail.Add(BinaryPrimitives.ReadInt64LittleEndian(message));
            }
        });
        var values = new List<long>();
        var marks = client.Subscribe(outlet.Tap(mark, "mark").Binding,
            new SubscriptionHandler
                { Value = (_, value) => values.Add(BinaryPrimitives.ReadInt64LittleEndian(value.Span)) });
        var messages = new List<(long Seq, long Value, bool Gap)>();
        client.Subscribe(outlet.Tap(alerts, "alerts").Binding,
            new SubscriptionHandler
            {
                Message = (_, message, info) =>
                    messages.Add((info.Seq, BinaryPrimitives.ReadInt64LittleEndian(message), info.Gap))
            });
        Assert(!loop.Sent.Any(s => s.StartsWith("Activate")), "not bound yet: nothing sent");
        client.Bind(outlet.Id, outlet.Nonce);
        Assert(
            loop.Sent.SequenceEqual([
                "Resume 0", $"Activate {bookTap.Id}", $"Activate {marks.TapId}", $"Activate {bookTap.Id + 2}"
            ]) && client.Reachable,
            $"bound: resumed from nothing, then each activated ({string.Join(", ", loop.Sent)})");

        var rnd = new Random(7);
        long lastMark = -1;
        var published = new List<long>();
        for (var step = 0; step < 3000; step++)
        {
            Churn(book, rnd);
            if (step % 10 == 0) mark.Publish(Int64(lastMark = step));
            if (step % 7 == 0)
            {
                alerts.Publish(Int64(step));
                published.Add(step);
            }

            if (step % 500 == 0) bookTap.TryPost(Int64(1_000_000 + step));
            if (rnd.Next(3) == 0) Pump(outlet, owner, client);
        }

        Pump(outlet, owner, client);

        AssertReplica(books.Table, book, "the table");
        Assert(unverified == 0 && snapshots == 1 && deltas > 100,
            $"every record verified: one snapshot, then deltas ({snapshots}, {deltas})");
        Assert(values[^1] == lastMark && marks.Value.Length == 8 && values.SequenceEqual(values.Order()),
            "a value: its latest, the stream in order (conflated, never older)");
        Assert(
            messages.Select(m => m.Value).SequenceEqual(published) &&
            messages.Select(m => m.Seq).SequenceEqual(Enumerable.Range(1, published.Count).Select(i => (long)i)) &&
            !messages.Any(m => m.Gap),
            "every message of the topic, in order, once, numbered from 1");
        Assert(mail.SequenceEqual(Enumerable.Range(0, 6).Select(i => 1_000_000L + i * 500)),
            "the subscription's mailbox, in order");
        Assert(outlet.Describe().FramesAcked == client.LastFrame && loop.Acks > 0 && client.BytesReceived > 0,
            "every frame acknowledged");
        Notes.Add(
            $"Tapline.Client: {snapshots} snapshot, {deltas} deltas, {entries} entries; {client.LastFrame} frames, {client.BytesReceived:N0} bytes");

        // A delta that doesn't check out (its checksum flipped): a resync, once — then a snapshot heals the replica.
        Churn(book, rnd);
        var tampered = false;
        Pump(outlet, owner, client, bytes =>
        {
            if (tampered || bytes[0] != Wire.Table || (bytes[5] & Wire.FlagReset) != 0) return bytes;
            tampered = true;
            bytes[22] ^= 0xFF;
            return bytes;
        });
        Assert(tampered && loop.Sent.Count(s => s == $"Resync {bookTap.Id}") == 1,
            "a delta that doesn't check out: one resync");
        Churn(book, rnd);
        Pump(outlet, owner, client);
        AssertReplica(books.Table, book, "healed");
        Assert(snapshots == 2, $"healed by a snapshot ({snapshots})");

        // A reconnect: the connection gone, changes meanwhile; bound again, every subscription goes on from the last frame
        // received — the table with its next delta, no snapshot.
        bool? reachable = null;
        client.ReachabilityChanged += r => reachable = r;
        client.Disconnected();
        Assert(reachable == false && !client.Reachable, "the router out of reach");
        outlet.Detach();
        for (var i = 0; i < 20; i++) Churn(book, rnd);
        mark.Publish(Int64(lastMark = 99_999));
        alerts.Publish(Int64(99_999));
        published.Add(99_999);
        var again = new object();
        outlet.Attach(again);
        client.Bind(outlet.Id, outlet.Nonce);
        Assert(reachable == true && loop.Sent.Contains($"Resume {client.LastFrame}"),
            "bound again: resumed from the last frame received");
        Pump(outlet, again, client);
        AssertReplica(books.Table, book, "after the reconnect");
        Assert(snapshots == 2 && values[^1] == 99_999 && messages.Select(m => m.Value).SequenceEqual(published),
            "resumed exactly: no snapshot, the value, the next message");

        // A rendezvous: through the table's subscription, strictly after the state it names.
        var correlation = TaplineClient.NewCorrelation();
        var awaited = client.RendezvousAsync(correlation, bookTap.Id);
        Churn(book, rnd);
        Assert(bookTap.TryRendezvous(correlation, book.Version), "rendezvous posted");
        Pump(outlet, again, client);
        var info = await awaited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(
            info.TapId == bookTap.Id && info.Seq == book.Version && !info.Missed &&
            books.Table!.Version == book.Version, "the rendezvous: after the state it named");

        // Unsubscribed: the router told, nothing more handed over.
        var before = values.Count;
        marks.Dispose();
        mark.Publish(Int64(123));
        Pump(outlet, again, client);
        Assert(marks.IsEnded && values.Count == before && loop.Sent.Contains($"Close {marks.TapId} None"),
            "dropped: closed, nothing more");
    }

    /// <summary>
    ///     Tap.Sync: a client asks whether it is up to date on a subscription — a quiet one answered at once, up to date; one
    ///     whose changes its interval holds back not answered till they're delivered, then up to date as of the ask; each
    ///     ask its own id; asked while unbound, nothing sent (its timeout answers).
    /// </summary>
    public static async Task ClientSync()
    {
        var registry = new OutletRegistry();
        var outlet = NewOutlet(registry);
        var owner = new object();
        outlet.Attach(owner);
        var loop = new LoopConnection(outlet);
        var client = new TaplineClient(loop);
        var book = new Table<Level, long>("SYNC", 4096);
        var rnd = new Random(7);
        Churn(book, rnd);
        var tap = outlet.Tap(book, "book");
        var books = client.Subscribe(tap.Binding, new SubscriptionHandler());

        using var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var unbound = client.SyncAsync(tap.Id, soon.Token);
        Assert(!loop.Sent.Any(s => s.StartsWith("Sync", StringComparison.Ordinal)), "not bound: nothing asked");
        client.Bind(outlet.Id, outlet.Nonce);
        Pump(outlet, owner, client);
        await Task.WhenAny(unbound, Task.Delay(2000));
        Assert(unbound.IsCanceled, "asked unbound: its timeout answers");

        // Quiet: nothing held back — answered at once, the client up to date.
        var quiet = client.SyncAsync(tap.Id);
        Pump(outlet, owner, client);
        var info = await quiet.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(info.TapId == tap.Id && info.Seq == book.Version && books.Table!.Version == book.Version,
            $"quiet: answered at once, up to date (seq {info.Seq}, the book's {book.Version})");

        // Held back (its interval): not answered till what's held is delivered — then up to date as of the ask.
        void Change(long size)
        {
            using var u = book.BeginUpdate();
            u.Set(new Level(0, 2000), size); // (a new version, always)
            u.Complete();
        }

        tap.MinInterval = TimeSpan.FromHours(1);
        Change(1);
        Pump(outlet, owner, client); // (sent: the interval runs from here)
        Change(2);
        Pump(outlet, owner, client); // (held)
        var asked = book.Version;
        var held = client.SyncAsync(tap.Id);
        for (var i = 0; i < 5; i++) Pump(outlet, owner, client);
        await Task.Delay(50);
        Assert(!held.IsCompleted && books.Table!.Version < asked,
            $"held back by its interval: not answered, not up to date ({books.Table!.Version} of {asked})");
        tap.Hurry();
        Pump(outlet, owner, client);
        info = await held.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(info.Seq == asked && books.Table!.Version >= asked, "delivered: answered, up to date as of the ask");
        Assert(loop.Sent.Where(s => s.StartsWith($"Sync {tap.Id} ", StringComparison.Ordinal)).Distinct().Count() == 2,
            "each ask its own id");

        // The router says the control messages it takes, on each connection: all of them, here. One that says it doesn't
        // take Tap.Sync (an older router) has an ask fail at once, nothing sent, not wait out its timeout.
        Assert(client.RouterControls == ControlKinds.Supported, $"the router takes every kind ({client.RouterControls:x})");
        loop.Muted = true; // (a frame of the test's own: its acknowledgement isn't the outlet's)
        var older = new byte[Wire.HeartbeatSize + Wire.RouterChangeHeader];
        older[0] = Wire.Heartbeat;
        older[Wire.HeartbeatSize] = Wire.RouterChange;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            older.AsSpan(Wire.HeartbeatSize + Wire.RouterChangeControls), ControlKinds.Supported & ~(1u << (int)ControlKind.Sync));
        client.Receive(1_000_000, older);
        var sent = loop.Sent.Count;
        var refused = client.SyncAsync(tap.Id);
        Assert(refused is { IsFaulted: true, Exception.InnerException: NotSupportedException } && loop.Sent.Count == sent,
            "a router that doesn't take Tap.Sync: the ask failed at once, nothing sent");
    }

    public static Task ClientWatch()
    {
        var registry = new OutletRegistry();
        var outlet = NewOutlet(registry, o => o.FrameBudget = 2048);
        var owner = new object();
        outlet.Attach(owner);
        var client = new TaplineClient(new LoopConnection(outlet));
        client.Bind(outlet.Id, outlet.Nonce);
        var table = new Table<int, long>("watched");
        var value = new LastValueSource();
        var watch = outlet.Watch("w", WatchMember.Of(value), WatchMember.Of(table));

        // Each unit writes a row and the value naming it, together: every cut handed over has the value's row in step.
        int cuts = 0, torn = 0;
        var watched = client.Subscribe(watch.Binding, new SubscriptionHandler
        {
            Group = (_, group) =>
            {
                cuts++;
                var (rows, last) = Members(group);
                if (rows is null || last is not { } at) return;
                var key = (int)(at & 63);
                if (!rows.TryGetRow(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(in key)), out var row) ||
                    BinaryPrimitives.ReadInt64LittleEndian(row) != at) torn++;
            }
        });
        for (var i = 0; i < 2000; i++)
        {
            using (var tx = Transaction.Begin(table, value))
            {
                tx.Of(table).Set(i & 63, i);
                tx.Of(value).Publish(Int64(i));
                tx.Complete();
            }

            if (i % 3 == 0) Pump(outlet, owner, client);
        }

        Pump(outlet, owner, client);
        var (final, latest) = Members(watched.Group!);
        Assert(cuts > 50 && torn == 0, $"every cut coherent: the value's row in step ({cuts} cuts, {torn} torn)");
        Assert(final is { Count: 64, Verified: true } && final.Checksum == table.Checksum && latest == 1999,
            "the watch's members: the table's rows, the value's latest");
        return Task.CompletedTask;

        static (TableReplica? Rows, long? Last) Members(GroupReplica group)
        {
            TableReplica? rows = null;
            long? last = null;
            for (var i = 0; i < group.Count; i++)
                if (group.TableAt(i) is { } t) rows = t;
                else if (group.ValueAt(i) is { Length: 8 } v) last = BinaryPrimitives.ReadInt64LittleEndian(v.Span);
            return (rows, last);
        }
    }

    public static async Task ClientOutOfBand()
    {
        var registry = new OutletRegistry();
        var outlet = NewOutlet(registry, o => (o.OutOfBandThreshold, o.FrameBudget) = (64, 2048));
        var owner = new object();
        outlet.Attach(owner);
        var loop = new LoopConnection(outlet);
        var client = ClientOf(loop, registry);
        client.Bind(outlet.Id, outlet.Nonce);
        var book = new Table<Level, long>("OOB", 4096);
        var rnd = new Random(11);
        for (var i = 0; i < 40; i++) Churn(book, rnd);
        var big = new LastValueSource();
        big.Publish(new byte[4096]);

        var snapshots = 0;
        var books = client.Subscribe(outlet.Tap(book, "book").Binding, new SubscriptionHandler
        {
            Table = (_, _, batch) =>
            {
                if (batch.IsSnapshot) snapshots++;
            }
        });
        var bigs = client.Subscribe(outlet.Tap(big, "big").Binding, new SubscriptionHandler());
        await Settle(outlet, owner, client);
        AssertReplica(books.Table, book, "a snapshot out of band");
        Assert(snapshots == 1 && loop.Sent.Count(s => s == $"Fetched {books.TapId}") >= 1 && bigs.Value.Length == 4096,
            "fetched, applied, confirmed: the table and a large value");

        for (var step = 0; step < 300; step++)
        {
            Churn(book, rnd);
            if (step % 50 == 0) big.Publish(Enumerable.Repeat((byte)step, 4096).ToArray());
            if (rnd.Next(4) == 0) await Settle(outlet, owner, client);
        }

        await Settle(outlet, owner, client);
        AssertReplica(books.Table, book, "deltas, in band and out");
        Assert(bigs.Value.Span[0] == 250, "the large value's latest, out of band");

        // By reference: the handle handed over, the bytes fetched on demand — checked against their content address.
        var reference = new LastValueSource();
        var blobs = new List<BlobRef>();
        client.Subscribe(outlet.Tap(reference, "reference").Binding,
            new SubscriptionHandler { Blob = (_, blob) => blobs.Add(blob) });
        var payload = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        reference.Publish(payload, Delivery.Reference);
        await Settle(outlet, owner, client);
        Assert(
            blobs.Count == 1 && blobs[0].Length == payload.Length &&
            (await blobs[0].FetchAsync()).SequenceEqual(payload),
            "by reference: a handle, its bytes fetched (and checked) on demand");

        // A fetch that fails: the subscription resynced (a fresh handle) — failing every time, it fails.
        var broken = new LoopConnection(outlet);
        var failing = new TaplineClient(broken,
            new TaplineClientOptions
            {
                BlobBase = new Uri("http://router.test/_tapline/blob/"),
                Fetch = (_, _) => throw new HttpRequestException("unreachable")
            });
        failing.Bind(outlet.Id, outlet.Nonce);
        TapError? error = null;
        var lost = failing.Subscribe(outlet.Tap(book, "book again").Binding,
            new SubscriptionHandler { Error = (_, e) => error = e });
        await Settle(outlet, owner, failing);
        Assert(
            lost.IsEnded && error is { Code: FaultCode.Unknown, Local: true } &&
            broken.Sent.Count(s => s.StartsWith("Resync")) == 2,
            $"fetches failing: resynced twice, then failed ({error?.Code}, {string.Join(", ", broken.Sent)})");
    }

    public static async Task ClientNotices()
    {
        // Faults from the router: each subscription's error policy, then its error — over.
        var registry = new OutletRegistry();
        var outlet = NewOutlet(registry);
        var owner = new object();
        outlet.Attach(owner);
        var loop = new LoopConnection(outlet);
        var client = new TaplineClient(loop);
        client.Bind(outlet.Id, outlet.Nonce);
        var book = new Table<Level, long>("FAULT");
        Churn(book, new Random(3));
        var errors = new List<TapError>();
        var books = client.Subscribe(outlet.Tap(book, "book").Binding,
            new SubscriptionHandler { Error = (_, e) => errors.Add(e) });
        Pump(outlet, owner, client);
        var injector = new FaultInjector();
        outlet.JoinFaultInjector(injector);
        injector.Fail(FaultCode.SourceFailed, "injected");
        Pump(outlet, owner, client);
        Assert(books.IsEnded && errors is [{ Code: FaultCode.SourceFailed, Local: false }],
            $"a fault: the router's error, the subscription over ({string.Join(", ", errors)})");
        injector.Recover(); // (this outlet's new subscriptions stream again)

        // Stalled: frames arrive, but nothing this client says reaches the router (half open) — it ends them, and says so
        // once the connection is whole again.
        var stalling = NewOutlet(registry, o => o.StallTimeout = TimeSpan.FromMilliseconds(50));
        var stallOwner = new object();
        stalling.Attach(stallOwner);
        var half = new LoopConnection(stalling);
        var stalled = new TaplineClient(half);
        stalled.Bind(stalling.Id, stalling.Nonce);
        var value = new LastValueSource();
        value.Publish([1]);
        TapError? why = null;
        (FaultCode Code, uint From, uint To)? ended = null;
        stalled.Ended += (code, from, to) => ended = (code, from, to);
        var watched = stalled.Subscribe(stalling.Tap(value, "value").Binding,
            new SubscriptionHandler { Error = (_, e) => why = e });
        Pump(stalling, stallOwner, stalled);
        half.Muted = true;
        for (var i = 0; i < 400 && ended is null; i++)
        {
            value.Publish([(byte)i]);
            stalling.Heartbeat();
            Pump(stalling, stallOwner, stalled);
            Thread.Sleep(5);
        }

        half.Muted = false;
        Pump(stalling, stallOwner, stalled);
        Assert(
            ended is { Code: FaultCode.Stalled } && why is { Code: FaultCode.Stalled, Local: false } && watched.IsEnded,
            $"stalled: ended by the router, told ({ended}, {why})");

        // A service's instance gone: each subscription through it fails (ServiceDown), then the notice — by its node id, with the change's correlation id.
        var publisher = new Publisher(registry);
        var topic = new LastValueSource();
        topic.Publish([5]);
        Provide(publisher, "svc", "client-station", new TestProvider(topic));
        var provided = (await publisher.SubscribeAsync(outlet, "svc"))!;
        Guid? down = null, downWith = null;
        client.ServiceDown += (node, correlation) => (down, downWith) = (node, correlation);
        TapError? gone = null;
        var through = client.Subscribe(provided.Binding, new SubscriptionHandler { Error = (_, e) => gone = e });
        Pump(outlet, owner, client);
        Assert(through.Provider == ProviderState.Serving && through.Value.Span.SequenceEqual((byte[])[5]),
            "through a provider: serving, its state");
        publisher.Withdraw(At("client-station"));
        Pump(outlet, owner, client);
        Assert(
            gone is { Code: FaultCode.ServiceDown } && down == provided.Binding.NodeId && downWith is { Version: 7 } &&
            through.IsEnded,
            $"the instance gone: ServiceDown, then the notice ({gone}, {down}, correlation {downWith})");
        // One subscription to move (its instance rebalancing): told once, with its grace and the move's correlation id.
        var mover = new Publisher(registry) { MoveDwell = TimeSpan.Zero };
        Provide(mover, "mv", "client-move-a", new TestProvider(topic));
        Provide(mover, "mv", "client-move-b", new TestProvider(topic));
        var movable = (await mover.SubscribeAsync(outlet, "mv"))!;
        var toMove = client.Subscribe(movable.Binding, new SubscriptionHandler());
        Pump(outlet, owner, client);
        var moves = new List<(Subscription Subscription, TimeSpan Grace, Guid Correlation)>();
        client.SubscriptionMoving += (sub, grace, correlation) => moves.Add((sub, grace, correlation));
        var answer = mover.MoveNode(movable.Binding.NodeId!.Value, null, null, TimeSpan.Zero, TimeSpan.FromSeconds(2), "test");
        for (var i = 0; i < 100 && moves.Count == 0; i++)
        {
            Pump(outlet, owner, client);
            Thread.Sleep(5);
        }

        Pump(outlet, owner, client);
        Assert(answer.Accepted && moves is [var move] && move.Subscription == toMove &&
               move.Grace == TimeSpan.FromSeconds(2) && move.Correlation == answer.Correlation && !toMove.IsEnded,
            $"a subscription to move: told once, with its grace and correlation, not ended ({answer}, {moves.Count})");

        client.Disconnected();
        Assert(client.Subscribe(outlet.Tap(topic, "direct").Binding, new SubscriptionHandler()).Provider is null,
            "bound directly: no provider");
    }

    /// <summary>
    ///     The client's status reads wait on nothing: a handler waiting on a lock of the app's, while a thread holding that
    ///     lock
    ///     reads the client's status — each getter answers (were they under the client's lock, each would wait on the handler,
    ///     and the handler on them: deadlocked, as the headless sample's watch once was).
    /// </summary>
    public static async Task ClientStatusReads()
    {
        var registry = new OutletRegistry();
        var outlet = NewOutlet(registry);
        var owner = new object();
        outlet.Attach(owner);
        var client = new TaplineClient(new LoopConnection(outlet));
        var book = new Table<Level, long>("STATUS", 64);
        var app = new Lock();
        using var inHandler = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        client.Subscribe(outlet.Tap(book, "book").Binding, new SubscriptionHandler
        {
            Table = (_, _, _) =>
            {
                inHandler.Set();
                lock (app)
                {
                }
            } // (the app's lock, taken in a handler: a watch's)
        });
        client.Bind(outlet.Id, outlet.Nonce);
        using (var u = book.BeginUpdate())
        {
            u.Set(new Level(0, 1000), 1);
            u.Complete();
        }

        var reading = Task.Run(() => // the app's lock held, the client's status read while the handler waits on it
        {
            lock (app)
            {
                held.Set();
                if (!inHandler.Wait(TimeSpan.FromSeconds(5))) return "the handler never ran";
                _ = (client.Reachable, client.Router, client.LastFrame, client.SinceLastFrame, client.BytesReceived,
                    client.TransferRate, client.Resyncs);
                return null;
            }
        });
        Assert(held.Wait(TimeSpan.FromSeconds(5)), "the app's lock never taken");
        var receiving =
            Task.Run(() => Pump(outlet, owner, client)); // the snapshot: its handler waits on the app's lock
        var deadlocked = false;
        try
        {
            await Task.WhenAll(reading, receiving).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            deadlocked = true;
        }

        Assert(!deadlocked,
            "a status read waited on the client's lock — held by the handler, waiting on the app's: deadlocked");
        Assert(reading.Result is null, reading.Result ?? "");
        Assert(client.LastFrame > 0 && client.BytesReceived > 0, "the frame received, its status read");
    }

    /// <summary>
    ///     The client's side of the wire, looped into an outlet: what the client says, the router hears at once — unless
    ///     muted (a half-open connection).
    /// </summary>
    private sealed class LoopConnection(Outlet outlet) : ITaplineConnection
    {
        public readonly List<string> Sent = [];
        public int Acks;
        public bool Muted;

        public void Send(in ControlMessage message)
        {
            if (message.Kind == ControlKind.Ack) Acks++;
            else
                Sent.Add(message.Kind switch
                {
                    ControlKind.Activate or ControlKind.Resync => $"{message.Kind} {message.A}",
                    ControlKind.Resume => $"Resume {message.A}",
                    ControlKind.Close => $"Close {message.A} {(FaultCode)(ushort)message.B}",
                    ControlKind.Fetched => $"Fetched {message.A}",
                    ControlKind.Auth => $"Auth {message.Payload.Span[0]}",
                    _ => $"{message.Kind} {message.A} {message.B}"
                });
            if (!Muted) outlet.Apply(message);
        }
    }
}