using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using Microsoft.Extensions.Options;
using Tapline;

namespace BlazingOrderBooks.Sample;

/// <summary>The feed's knobs (configuration section <c>Market</c>; <c>Market__Spin=true</c> as an environment variable).</summary>
public sealed class MarketFeedOptions
{
    /// <summary>How often the exchange publishes depth: the levels changed over the interval, conflated (venues: 10–100 ms).</summary>
    public int DepthIntervalMs { get; set; } = 20;

    /// <summary>Extra delay on depth updates against the prints, as a slower channel's (at most the interval).</summary>
    public int DepthDelayMs { get; set; }

    /// <summary>
    ///     Busy-poll the wire — the HFT norm, a core for the handler. Off (the default), the handler spins briefly, then
    ///     parks until the exchange writes again.
    /// </summary>
    public bool Spin { get; set; } = Environment.ProcessorCount > 2;

    /// <summary>
    ///     The venue's tick, in the wire's price unit (0.01): 1 by default; 10 for a venue quoting in tenths. The ladders
    ///     index by it, so adjacent levels share cache lines whatever the tick.
    /// </summary>
    public long TickSize { get; set; } = 1;

    /// <summary>
    ///     Ticks the book ladders span (a power of two). Size it for the instrument: twice the published depth's span plus
    ///     the largest jump expected between depth updates. 16,384 ticks: 128 KB of sizes a side, a few cache lines of
    ///     it in use. Levels beyond it are kept aside, not lost; the window follows the touch.
    /// </summary>
    public int WindowTicks { get; set; } = 16_384;

    /// <summary>The wire's buffer (a power of two): full, the exchange drops a message, and the handler sees the gap.</summary>
    public int WireBytes { get; set; } = 1 << 20;

    /// <summary>Drop every nth depth update on the wire (0, the default: none): gaps, to see the recovery.</summary>
    public int DropEvery { get; set; }

    /// <summary>
    ///     Make the price jump every this many milliseconds (0, the default: never) by <see cref="JumpTicks" />, up and
    ///     down in turn: the book swept, emptied and rebuilt that far away — an instantaneous gap, to see the handler
    ///     (and the accelerated book) through one.
    /// </summary>
    public int JumpEveryMs { get; set; }

    /// <summary>How far each jump goes, in ticks (<see cref="JumpEveryMs" />).</summary>
    public int JumpTicks { get; set; } = 2_000;
}

/// <summary>
///     The exchange feed handler, fed by a simulated venue (<see cref="Exchange" />) over its wire
///     (<see cref="SpscByteRing" />) — and the "market" service's data provider (<see cref="IDataProvider" />), in process, at an endpoint of that
///     name. A subscription names a topic: <c>book</c> (the BTC-PERP book as the depth feed has it: a snapshot, then
///     deltas; viewers shown the top of the book if the feed breaks), <c>book-accelerated</c> (the same book seen through
///     the prints the depth has yet to catch up with: <see cref="BookBuilder" />) or <c>mark</c> (its mid price: a last
///     value). A book may be asked in a shape — <c>book?depth=10&amp;group=10</c>, its best levels a side, grouped
///     (<see cref="BookShape" />) — a topic of its own, shared by those asking alike (<see cref="ShapedBooks" />). The router sends it step 1 of each, records its instance on the route it accepts, and checks its health
///     while it holds routes; it keeps its routers (<see cref="RouterSessions" />), heartbeats them, and refuses one it
///     presumes dead.
///     <para>
///         Three threads. The exchange's steps the venue every millisecond and writes its two feeds — prints at once,
///         depth conflated per interval — onto the wire: a single-producer, single-consumer ring, as a socket buffer. The
///         handler's reads each message in place, updates the books (<see cref="BookBuilder" />), and posts their changes
///         to Tapline before reading the next: it never waits for the fold nor the readers, and conflates nothing itself.
///         Waiting for the wire, it spins (<see cref="SpinWait.SpinOnce(int)" /> with no sleeping, until the spinner would
///         yield), then — unless told to busy-poll (<see cref="MarketFeedOptions.Spin" />) — parks until the exchange
///         writes; busy-polling, it yields the core and spins again. The service's own thread does the provider's
///         housekeeping: heartbeats, alerts. What the handler measures as it goes is in <see cref="FeedStats" />.
///     </para>
///     <para>
///         It produces for a topic only while it has subscribers: a publish answered 202 for the epoch it accepted a
///         subscriber on last stops it (one from an earlier epoch crossed a new subscriber, and is ignored). The exchange
///         goes on meanwhile, numbering its events; a new subscriber has the topic brought up to date first — the whole
///         book (superseding whatever is staged), the mark as it is. The books' posts name the exchange's sequence
///         numbers: a depth update's last, a print's own. Should the router fall so far behind that the staging is full
///         (507), or a book fault, it posts the whole book instead. A gap on the wire fails the topics (viewers get the
///         degraded top of book) and asks the exchange for a snapshot, which recovers them. The feed itself is healthy:
///         outages to test against are injected for /test pages only (TaplineOptions.FaultInjection). Its alerts go out
///         directly, not through the service.
///     </para>
/// </summary>
internal sealed class MarketFeedHandler : BackgroundService, IDataProvider
{
    private const long Mid = 6_500_000; // the price at the start, in ticks of 0.01
    private readonly Topic _accelerated, _book, _mark;
    private readonly ShapedBooks _acceleratedShapes, _bookShapes;
    private readonly BookFeed _feed;
    private readonly Lock _gate = new();
    private readonly ILogger<MarketFeedHandler> _log;
    private readonly MarketFeedOptions _options;

    private readonly Publisher _publisher;
    private readonly RouterSessions _routers;
    private readonly LoadMeter _load = new(); // its work over the time that passes: its health reports' load
    private volatile bool _stopping;

    public MarketFeedHandler(Publisher publisher, BookFeed feed, FeedStats stats, IOptions<MarketFeedOptions> options,
        ILogger<MarketFeedHandler> log)
    {
        _publisher = publisher;
        _feed = feed;
        _log = log;
        _options = options.Value;
        Stats = stats;
        Endpoint = ProviderEndpoint.InProcess("market");
        Instance = ProviderInstance.Start("market", Endpoint);
        publisher.Alerts().Format =
            Market.AlertsFormat; // (published here, not through the service: for the router's viewer)
        _book = new Topic(publisher.Book("BTC-PERP"), "book:BTC-PERP", Market.BookPolicy, Market.BookFormat);
        _accelerated = new Topic(publisher.AcceleratedBook("BTC-PERP"), "book:BTC-PERP:accelerated",
            Market.BookPolicy, Market.BookFormat);
        _mark = new Topic(publisher.Marks()["BTC-PERP"], "mark:BTC-PERP", Market.MarkPolicy, Market.MarkFormat);
        var tick = Math.Max(1, _options.TickSize);
        _bookShapes = new ShapedBooks(publisher, "book:BTC-PERP", Market.BookFormat, tick, Posts.Capacity, _gate,
            Acknowledged);
        _acceleratedShapes = new ShapedBooks(publisher, "book:BTC-PERP:accelerated", Market.BookFormat, tick,
            Posts.Capacity, _gate, Acknowledged);
        _routers = new RouterSessions(() => Instance)
        {
            Lost = (router, routes) =>
                log.LogInformation("Market: router {Router} presumed dead, {Routes} routes forgotten", router,
                    routes.Length)
        };
    }

    /// <summary>Where the router reaches the feed: in this process, by its name.</summary>
    public ProviderEndpoint Endpoint { get; }

    /// <summary>The feed as it runs: its name, endpoint, and an id drawn when it started.</summary>
    public ProviderInstance Instance { get; }

    /// <summary>What the handler measures as it runs.</summary>
    public FeedStats Stats { get; }

    ValueTask IDataProvider.ConnectAsync(IRouterChannel router, CancellationToken cancellationToken)
    {
        _routers.Connected(router);
        return default;
    }

    /// <summary>
    ///     Step 1: onto the topic asked for (shared), with its stale-data policy and its payloads' format; its epoch
    ///     recorded, the feed producing for it.
    /// </summary>
    ValueTask IDataProvider.SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
    {
        if (!_routers.Admits(request.Router))
        {
            request.Nack();
            return default;
        }

        if (request.Parameters is null || !BookShape.Split(request.Parameters, out var name, out var shape))
            return default; // no such topic: refused
        var topic = name switch
        {
            "book" => _book, "book-accelerated" => _accelerated, "mark" => shape.IsWhole ? _mark : null, _ => null
        };
        if (topic is null) return default;
        lock (_gate)
        {
            if (!shape.IsWhole) // a shape of the book: its own topic, made the first time it's asked for
            {
                var shaped = (topic == _book ? _bookShapes : _acceleratedShapes).For(shape);
                shaped.Epoch = request.Accept(shaped.Source, Instance, shaped.Name, InitialState.Snapshot,
                    Market.BookPolicy, shaped.Format);
                if (!shaped.Live) shaped.Live = shaped.Fresh = true; // posted whole after the next message
            }
            else
            {
                topic.Epoch = request.Accept(topic.Source, Instance, topic.Name, InitialState.Snapshot, topic.Policy,
                    topic.Format);
                if (!topic.Live) topic.Live = topic.Fresh = true; // stopped meanwhile: brought up to date first
            }
        }

        _routers.Accepted(request.Router, request.Route);
        return default;
    }

    /// <summary>A route ended: forgotten in its router's session (the topics learn of it from their next publish's 202).</summary>
    ValueTask IDataProvider.UnsubscribeAsync(Unsubscription notice, CancellationToken cancellationToken)
    {
        _routers.Ended(notice.Router, notice.Route);
        return default;
    }

    /// <summary>The router's health check: serving — refusing a router it presumes dead.</summary>
    ValueTask<HealthReport> IProviderHealth.CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
    {
        return new ValueTask<HealthReport>(new HealthReport(Instance, ProviderStatus.Serving,
            _routers.Refuses(probe.Router), Load: _load.Load)); // (a struct, from memory: nothing allocated)
    }

    /// <summary>
    ///     Whether to produce for the topic now, and whether it must be brought up to date first (taken: then up to
    ///     date).
    /// </summary>
    private bool Producing(Topic topic, out bool fresh)
    {
        lock (_gate)
        {
            fresh = topic.Fresh;
            topic.Fresh = false;
            return topic.Live;
        }
    }

    /// <summary>A publish's answer: 202 for the epoch last accepted on stops the topic (earlier epochs': stale, ignored).</summary>
    private void Acknowledged(Topic topic, PublishResult result)
    {
        if (result.Status != PublishStatus.NoListeners) return;
        lock (_gate)
        {
            if (topic.Epoch == result.Epoch) topic.Live = false;
        }
    }

    /// <summary>A shape's publish answered: likewise.</summary>
    private void Acknowledged(ShapedBook book, PublishResult result)
    {
        if (result.Status != PublishStatus.NoListeners) return;
        lock (_gate)
        {
            if (book.Epoch == result.Epoch) book.Live = false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ExchangeJournal journal = new(), acceleratedJournal = new(); // what it posted, by exchange seq: for its views
        await using var
            views = await MarketViews.StartAsync(journal, acceleratedJournal,
                stoppingToken); // its own HTTP server, on a port the OS allocates
        lock (_gate)
        {
            _book.Format =
                _book.Format with { ViewUrl = views.Book }; // given to the router in step 1, with the layouts
            _accelerated.Format = _accelerated.Format with { ViewUrl = views.Accelerated };
            _mark.Format = _mark.Format with { ViewUrl = views.Mark };
        }

        _log.LogInformation("Market: views of the books and the mark at {Book}, {Accelerated}, {Mark}", views.Book,
            views.Accelerated, views.Mark);
        Stats.DepthIntervalMs = _options.DepthIntervalMs;
        Stats.Waiting = _options.Spin ? "busy-polling" : "spin, then park";
        var window = Math.Max(64, (int)BitOperationsPow2(_options.WindowTicks));
        using var ring = new SpscByteRing(_options.WireBytes);
        var tick = Math.Max(1, _options.TickSize);
        using var exchange = new Exchange(Mid, new Random(), ring, window, _options.DepthIntervalMs,
            _options.DepthDelayMs, _options.DropEvery, tick, _options.JumpEveryMs, _options.JumpTicks);
        using var builder = new BookBuilder(window, Mid, Stats, tick);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Thread(() => RunHandler(ring, exchange, builder, journal, acceleratedJournal, ready))
            { IsBackground = true, Name = "market feed handler" };
        var venue = new Thread(() => RunExchange(exchange)) { IsBackground = true, Name = "market exchange" };
        try
        {
            handler.Start();
            venue.Start();
            await ready.Task.WaitAsync(stoppingToken); // the first snapshot posted: the topics have a state
            _publisher.Provide("market", Endpoint, this);

            var n = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _routers.HeartbeatRouters(); // heartbeats go both ways: the feed's, to its routers
                Stats.Dropped = ring.Dropped + exchange.Dropped;
                Stats.Jumps = exchange.Jumps;
                if (++n % 5 == 0)
                    _publisher.Alerts().Publish(Encoding.UTF8.GetBytes($"alert #{n / 5}")); // to those who subscribed
            }
        }
        finally
        {
            _stopping = true;
            ring.Wake();
            venue.Join(TimeSpan.FromSeconds(2));
            handler.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static long BitOperationsPow2(int ticks)
    {
        return BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, ticks));
    }

    /// <summary>The venue's thread: a step a millisecond — as much trading as the time that passed — onto the wire.</summary>
    private void RunExchange(Exchange exchange)
    {
        var clock = Stopwatch.StartNew();
        double last = 0;
        while (!_stopping)
        {
            Thread.Sleep(1);
            var now = clock.Elapsed.TotalMilliseconds;
            exchange.Step(now, now - last);
            last = now;
        }
    }

    /// <summary>
    ///     The handler's thread: each message off the wire, in place — decoded, the books updated, released, its changes
    ///     posted — timed; waiting as the policy says.
    /// </summary>
    private unsafe void RunHandler(SpscByteRing ring, Exchange exchange, BookBuilder builder, ExchangeJournal journal,
        ExchangeJournal acceleratedJournal, TaskCompletionSource ready)
    {
        var posts = new Posts(this, builder, _feed, journal, acceleratedJournal);
        var spinner = new SpinWait();
        long messages = 0, baseline = 0, snapshotAt = 0; // snapshotAt: a snapshot to ask for again then (0: none)
        while (true)
        {
            if (snapshotAt != 0 && Stopwatch.GetTimestamp() >= snapshotAt)
            {
                snapshotAt = 0;
                exchange.RequestSnapshot();
            }

            var message = ring.TryRead();
            if (message.IsEmpty)
            {
                if (_stopping) return;
                if (!spinner.NextSpinWillYield)
                {
                    spinner.SpinOnce(-1); // prevent sleeping
                    continue;
                }

                if (_options.Spin) Thread.Yield(); // busy-polling: the core given up only to a thread that wants it
                else ring.Park(); // until the exchange writes
                spinner.Reset();
                continue;
            }

            spinner.Reset();
            var read = Stopwatch.GetTimestamp();
            var now = Clock.Ns(read);
            var outcome = builder.Handle(Wire.Address(message), now);
            var built = Stopwatch.GetTimestamp();
            ring.Release(); // the message is done with: its changes are the builder's now
            Stats.Handoff.Record(now - builder.MessageTimestamp);
            switch (outcome)
            {
                case BookBuilder.Outcome.Snapshot:
                    posts.Snapshots();
                    ready.TrySetResult();
                    break;
                case BookBuilder.Outcome.Trade:
                    posts.Accelerated();
                    break;
                case BookBuilder.Outcome.Depth:
                    posts.Book();
                    posts.Accelerated();
                    posts.Mark();
                    break;
                case BookBuilder.Outcome.TooWide: // a book it can't hold whole: said, then asked for again in a second
                    posts.Failed("the book is wider than the handler's window: raise Market:WindowTicks");
                    snapshotAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    if (Stats.TooWide == 1 || Stats.TooWide % 60 == 0)
                        _log.LogError(
                            "BTC-PERP snapshot at exchange seq {Seq} is wider than the window ({Window} ticks) and its overflow: the topics stay failed till one fits",
                            builder.MessageSeq, builder.Book.Asks.Window);
                    break;
                case BookBuilder.Outcome.Gap:
                    posts.Failed("feed gap");
                    exchange.RequestSnapshot();
                    Stats.Resyncs++;
                    _log.LogWarning("BTC-PERP feed gap before exchange seq {Seq}: recovering from a snapshot",
                        builder.MessageSeq);
                    break;
            }

            var posted = Stopwatch.GetTimestamp();
            _load.AddTicks(posted - read); // (the handler's load: decoding and posting, its health reports')
            Stats.Decode.Record(Clock.Ns(built) - now);
            Stats.Posted.Record(Clock.Ns(posted) - now);
            messages++;
            if (messages == FeedStats.WarmUpMessages)
            {
                baseline = GC.GetAllocatedBytesForCurrentThread();
            }
            else if (messages > FeedStats.WarmUpMessages && (messages & 255) == 0)
            {
                Volatile.Write(ref Stats.AllocatedSinceBaseline, GC.GetAllocatedBytesForCurrentThread() - baseline);
                Volatile.Write(ref Stats.MessagesSinceBaseline, messages - FeedStats.WarmUpMessages);
            }
        }
    }

    /// <summary>
    ///     The handler's posts to Tapline, on its thread: a book's changes as the ingest's deltas, naming the exchange
    ///     sequences they go from and to (the journal keeps them by those); the whole book when a topic is fresh, on
    ///     507, or on a fault; the mark when the mid moves. Nothing allocated: the levels and sizes of a snapshot go
    ///     through buffers kept here.
    /// </summary>
    private sealed class Posts(
        MarketFeedHandler owner,
        BookBuilder builder,
        BookFeed feed,
        ExchangeJournal journal,
        ExchangeJournal acceleratedJournal)
    {
        /// <summary>The levels a book holds at most, both sides, shown or the overlay's alone: its buffers' size.</summary>
        public const int Capacity = 2 * Exchange.Depth + 64;

        private readonly byte[] _buffer = new byte[8];
        private readonly Level[] _levels = new Level[Capacity];
        private readonly LastValueSource _mark = (LastValueSource)owner._mark.Source;
        private readonly long[] _sizes = new long[Capacity];
        private long _acceleratedChain, _bookChain, _published;

        /// <summary>The authoritative book's changes, from the depth update: as of its last sequence.</summary>
        public void Book()
        {
            Post(owner._book, feed.Ingest, journal, builder.BookChanges, builder.Version, ref _bookChain, false);
            owner._bookShapes.Post(builder, false, builder.Version, false);
        }

        /// <summary>The accelerated book's changes: as of the message that made them (a print's seq, or the depth's).</summary>
        public void Accelerated()
        {
            owner._acceleratedShapes.Post(builder, true, builder.MessageSeq, false);
            if (builder.AcceleratedWhole && owner.Producing(owner._accelerated, out _)) // a window moved: whole
            {
                Snapshot(owner._accelerated, feed.Accelerated, acceleratedJournal, true, builder.MessageSeq,
                    ref _acceleratedChain);
                return;
            }

            Post(owner._accelerated, feed.Accelerated, acceleratedJournal, builder.AcceleratedChanges,
                builder.MessageSeq, ref _acceleratedChain, true);
        }

        public void Mark()
        {
            var mid = builder.Book.Mid;
            if (!owner.Producing(owner._mark, out var fresh) || (!fresh && mid == _published)) return;
            BinaryPrimitives.WriteInt64LittleEndian(_buffer, _published = mid);
            owner.Acknowledged(owner._mark, _mark.Publish(_buffer)); // last value: only the newest reaches each client
        }

        /// <summary>The whole of both books and the mark, from the exchange's snapshot: the topics' state, whoever listens.</summary>
        public void Snapshots()
        {
            Snapshot(owner._book, feed.Ingest, journal, false, builder.Version, ref _bookChain);
            Snapshot(owner._accelerated, feed.Accelerated, acceleratedJournal, true, builder.MessageSeq,
                ref _acceleratedChain);
            _published = 0;
            Mark();
            owner._bookShapes.Post(builder, false, builder.Version, true);
            owner._acceleratedShapes.Post(builder, true, builder.MessageSeq, true);
        }

        /// <summary>The wire broke: the topics fail (viewers see the degraded top of book) till a snapshot recovers them.</summary>
        public void Failed(string why)
        {
            owner._book.Source.Fail(FaultCode.SourceFailed, why);
            owner._accelerated.Source.Fail(FaultCode.SourceFailed, why);
            _mark.Fail(FaultCode.SourceFailed, why);
            owner._bookShapes.Fail(why);
            owner._acceleratedShapes.Fail(why);
        }

        private void Post(Topic topic, TableIngest<Level, long, long> ingest, ExchangeJournal log, ChangeSet changes,
            long version, ref long chain, bool accelerated)
        {
            if (!owner.Producing(topic, out var fresh)) return;
            if (fresh) // (re)subscribed: the whole book first
            {
                Snapshot(topic, ingest, log, accelerated, version, ref chain);
                return;
            }

            if (changes.Count == 0) return;
            try
            {
                var posted = ingest.Post(changes.LevelSpan, changes.DeltaSpan, chain, version);
                if (posted.Status != PublishStatus.Full)
                {
                    log.Posted(version, changes.LevelSpan, changes.DeltaSpan);
                    chain = version;
                    owner.Acknowledged(topic, posted);
                    return;
                }

                Snapshot(topic, ingest, log, accelerated, version, ref chain); // the router is behind: the whole book
            }
            catch (StreamFaultException e) // faulted, nothing posted to recover it
            {
                owner._log.LogWarning("{Topic} faulted: {Message}", topic.Name, e.Message);
                _mark.Fail(FaultCode.SourceFailed, "feed gap");
                Snapshot(topic, ingest, log, accelerated, version, ref chain);
                owner._log.LogInformation("{Topic} recovering from a snapshot at exchange seq {Seq}", topic.Name,
                    version);
            }
        }

        // The whole book as of version: it supersedes what's staged, and recovers a faulted table.
        private void Snapshot(Topic topic, TableIngest<Level, long, long> ingest, ExchangeJournal log,
            bool accelerated, long version, ref long chain)
        {
            var count = builder.Fill(_levels, _sizes, accelerated);
            owner.Acknowledged(topic, ingest.PostSnapshot(_levels.AsSpan(0, count), _sizes.AsSpan(0, count), version));
            log.Snapshot(version);
            chain = version;
        }
    }

    /// <summary>
    ///     A topic of the feed's: producing since the epoch it last accepted a subscriber on; brought up to date first if
    ///     new.
    /// </summary>
    private sealed class Topic(Source source, string name, ErrorPolicy policy, PayloadFormat format)
    {
        public readonly string Name = name;
        public readonly ErrorPolicy Policy = policy;
        public readonly Source Source = source;
        public long Epoch;
        public PayloadFormat Format = format; // (its view URL added once the feed's views listen)
        public bool Live, Fresh;
    }
}