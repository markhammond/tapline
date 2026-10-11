using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Tapline;

namespace BlazingOrderBooks.Sample.Binance;

/// <summary>
///     The <c>binance</c> service's data provider: Binance's most traded crypto perpetuals (<see cref="BinanceUniverse" />),
///     live, over a few WebSockets of combined streams. A subscription names a topic of an instrument:
///     <c>book:BTCUSDT</c> (its top 20 levels a side, as Binance publishes them every 100 ms), <c>book-accelerated:BTCUSDT</c>
///     (the same book seen through what's faster than it) or <c>micro:BTCUSDT</c> (its micro price, a last value). A
///     book may be asked in a shape — <c>book:BTCUSDT?depth=10&amp;group=10</c>, its best levels a side, grouped in
///     ticks (<see cref="BookShape" />) — a topic of its own, shared by those asking alike (<see cref="ShapedBooks" />).
///     <para>
///         <b>Three streams an instrument</b>, subscribed while it has listeners: <c>depth20@100ms</c>, the book;
///         <c>bookTicker</c>, the best bid and offer as they change — real time, and numbered in the book's own sequence
///         of updates (<c>u</c>), so a depth update says exactly which of them it reflects; and <c>trade</c>, each print as
///         it happens, reconciled by its time against the depth's. Each message is parsed in place (no strings, no
///         objects: prices to fixed point straight from the bytes) and normalized into the sample's own wire format
///         (<see cref="Wire" />), which the same <see cref="BookBuilder" /> the simulated venue feeds turns into the raw
///         and accelerated books. A depth update becomes the changes from the last one: the levels whose size moved, and
///         those it no longer shows, gone.
///     </para>
///     <para>
///         <b>Binary would be better.</b> Binance also publishes market data in SBE (Simple Binary Encoding): fixed
///         little-endian layouts, read in place as this handler reads its own wire, with no JSON to parse at all. It
///         would be the endpoint to use, but it needs an API key; this sample reads the public JSON streams, which need
///         none, and parses them without allocating instead.
///     </para>
///     <para>
///         <b>A few connections, not one.</b> Each connection is a combined stream (<see cref="BinanceOptions.StreamUrl" />,
///         streams subscribed by request), carrying a share of the instruments — dealt round by 24-hour volume, so the
///         busiest are spread out (<see cref="BinanceOptions.InstrumentsPerConnection" />) — and opened when its first
///         instrument is wanted. One connection for all of them doesn't last: Binance closes a connection, without a
///         close frame, once it carries much more than a few hundred <c>bookTicker</c> updates a second (found by trial:
///         about 750 a second was cut within a minute or two, 450 held). Messages from every connection are handled one
///         at a time, so the books and the stats have one writer; each takes a couple of microseconds.
///     </para>
///     <para>
///         <b>The grace period.</b> Tapline tells a provider a topic has no listeners left (a publish answered 202 for the
///         epoch it last accepted a subscriber on); this one stops posting to it at once, but keeps the instrument's
///         streams and its copy of the book for <see cref="BinanceOptions.GraceSeconds" /> after its last topic has gone
///         quiet. A listener back within that — a page scrolled back to it — has the book posted from that copy at once;
///         after it, the instrument's streams are unsubscribed, and its next listener waits for the next depth update.
///     </para>
///     <para>
///         A connection lost, its instruments' topics fail (viewers get the degraded top of book, and subscribe again on
///         recovery); it reconnects with back-off, subscribes again, and posts each book whole from its next depth update.
///         The other connections' instruments carry on.
///     </para>
/// </summary>
internal sealed class BinanceFeed : BackgroundService, IDataProvider
{
    public const string Service = "binance";
    private const int MicroDigits = 2; // a micro price's places past the tick's: it falls between ticks
    private readonly Lock _gate = new();
    private readonly Lock _handling = new(); // one message at a time, whichever connection it came in on
    private readonly HttpClient _http;
    private readonly ILogger<BinanceFeed> _log;
    private readonly BinanceOptions _options;
    private readonly Publisher _publisher;
    private readonly RouterSessions _routers;
    private Dictionary<string, Instrument> _bySymbol = [];
    private Instrument[] _instruments = [];
    private readonly LoadMeter _load = new(); // its work over the time that passes: its health reports' load
    private long _handled; // messages handled, for the allocation count after warm-up
    private Shard[] _shards = [];
    private CancellationToken _stopping;

    public BinanceFeed(Publisher publisher, BinanceUniverse universe, IOptions<BinanceOptions> options,
        IHttpClientFactory http, ILogger<BinanceFeed> log)
    {
        _publisher = publisher;
        Universe = universe;
        _options = options.Value;
        _http = http.CreateClient(nameof(BinanceFeed));
        _log = log;
        Endpoint = ProviderEndpoint.InProcess(Service);
        Instance = ProviderInstance.Start(Service, Endpoint);
        _routers = new RouterSessions(() => Instance);
    }

    public BinanceUniverse Universe { get; }
    public ProviderEndpoint Endpoint { get; }
    public ProviderInstance Instance { get; }

    /// <summary>What the feed measures, all instruments together.</summary>
    public FeedStats Stats { get; } = new()
    {
        DepthIntervalMs = 100, // (depth20@100ms)
        Waiting = "Binance's event time to receipt, the clocks' offset in it"
    };

    /// <summary>Instruments with their streams up now (listened to, or within their grace period).</summary>
    public int Streaming
    {
        get
        {
            lock (_gate) return _instruments.Count(i => i.Streaming);
        }
    }

    /// <summary>
    ///     Of those, the active (some topic listened to, by any page) and the warm (none: within their grace period, their
    ///     streams and book kept for a listener's return).
    /// </summary>
    public (int Active, int Warm) Instruments
    {
        get
        {
            lock (_gate)
            {
                var active = _instruments.Count(i => i.Streaming && i.Listened);
                return (active, _instruments.Count(i => i.Streaming) - active);
            }
        }
    }

    /// <summary>The connections open now, and how many the instruments are spread over.</summary>
    public (int Open, int Of) Connections => (_shards.Count(s => s.Socket?.State == WebSocketState.Open), _shards.Length);

    ValueTask IDataProvider.ConnectAsync(IRouterChannel router, CancellationToken cancellationToken)
    {
        _routers.Connected(router);
        return default;
    }

    /// <summary>
    ///     Step 1: onto the instrument's topic (shared), its epoch recorded; the instrument's streams subscribed on its
    ///     connection if they aren't up (the connection opened if it isn't), its grace period over if they are.
    /// </summary>
    ValueTask IDataProvider.SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
    {
        if (!_routers.Admits(request.Router))
        {
            request.Nack();
            return default;
        }

        if (!Parse(request.Parameters, out var instrument, out var kind, out var shape)) return default; // refused
        var shapes = instrument.ShapesOf(kind);
        if (!shape.IsWhole && shapes is null) return default; // (a micro price has no shape)
        var topic = instrument.TopicOf(kind);
        lock (_gate)
        {
            if (!shape.IsWhole) // a shape of the book: its own topic, made the first time it's asked for
            {
                var shaped = shapes!.For(shape);
                shaped.Epoch = request.Accept(shaped.Source, Instance, shaped.Name, InitialState.Snapshot,
                    Market.BookPolicy, shaped.Format);
                if (!shaped.Live) shaped.Live = shaped.Fresh = true; // posted whole after the next message
            }
            else
            {
                topic.Epoch = request.Accept(topic.Source, Instance, topic.Name, InitialState.Snapshot, topic.Policy,
                    topic.Format);
                if (!topic.Live) topic.Live = topic.Fresh = true; // brought up to date first, from the feed's copy
            }

            instrument.IdleSinceMs = 0;
            if (!instrument.Streaming)
            {
                instrument.Streaming = true;
                instrument.NeedSnapshot = true;
                var shard = instrument.Shard;
                shard.ToUnsubscribe.Remove(instrument.Info.StreamName);
                shard.ToSubscribe.Add(instrument.Info.StreamName);
                shard.SendWanted.Release();
                shard.Running ??= Task.WhenAll(Connection(shard, _stopping), Sender(shard, _stopping));
            }
        }

        _routers.Accepted(request.Router, request.Route);
        return default;
    }

    ValueTask IDataProvider.UnsubscribeAsync(Unsubscription notice, CancellationToken cancellationToken)
    {
        _routers.Ended(notice.Router, notice.Route);
        return default;
    }

    ValueTask<HealthReport> IProviderHealth.CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
    {
        return new ValueTask<HealthReport>(new HealthReport(Instance, ProviderStatus.Serving,
            _routers.Refuses(probe.Router), Load: _load.Load)); // (a struct, from memory: nothing allocated)
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        await Universe.LoadAsync(_options, _http, stoppingToken);
        if (Universe.Unavailable is { } why)
        {
            _log.LogWarning("Binance: the grid has no instruments — {Why}", why);
            return;
        }

        var window = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, _options.WindowTicks));
        var count = Universe.Instruments.Count;
        var perConnection = Math.Max(1, _options.InstrumentsPerConnection);
        _shards = Enumerable.Range(0, (count + perConnection - 1) / perConnection).Select(i => new Shard(i)).ToArray();
        _instruments = Universe.Instruments.Select((info, rank) => // dealt round by volume: the busiest spread out
            new Instrument(_publisher, info, window, Stats, _gate, Acknowledged)
                { Feed = this, Shard = _shards[rank % _shards.Length] }).ToArray();
        _bySymbol = _instruments.ToDictionary(i => i.Info.Symbol);
        _log.LogInformation("Binance: {Count} perpetuals over {Connections} connections — {Symbols}", count,
            _shards.Length, string.Join(", ", _instruments.Select(i => $"{i.Info.Symbol} ({i.Shard.Index})")));
        _publisher.Provide(Service, Endpoint, this);

        await Housekeeping(stoppingToken);
        Task[] running;
        lock (_gate) running = _shards.Select(s => s.Running).OfType<Task>().ToArray();
        await Task.WhenAll(running).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>
    ///     A connection, kept open: connected, its instruments' streams subscribed again, read till it drops; then again,
    ///     backing off.
    /// </summary>
    private async Task Connection(Shard shard, CancellationToken stoppingToken)
    {
        await Task.Yield(); // (started under the feed's lock: off it at once)
        var backoff = TimeSpan.FromSeconds(1);
        var buffer = GC.AllocateArray<byte>(1 << 16, pinned: true);
        while (!stoppingToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            var since = Stopwatch.GetTimestamp();
            shard.Messages = 0;
            try
            {
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(new Uri(_options.StreamUrl), stoppingToken);
                lock (_gate)
                {
                    shard.Socket = socket;
                    foreach (var instrument in _instruments.Where(i => i.Shard == shard && i.Streaming))
                    {
                        instrument.NeedSnapshot = true;
                        shard.ToSubscribe.Add(instrument.Info.StreamName);
                    }
                }

                shard.SendWanted.Release();
                _log.LogInformation("Binance: connection {Shard} open to {Url}", shard.Index, _options.StreamUrl);
                buffer = await Receive(shard, socket, buffer, stoppingToken);
            }
            catch (Exception e) when (e is WebSocketException or IOException or HttpRequestException ||
                                      (e is OperationCanceledException && !stoppingToken.IsCancellationRequested))
            {
                var seconds = Stopwatch.GetElapsedTime(since).TotalSeconds;
                if (seconds > 60) backoff = TimeSpan.FromSeconds(1); // it had held: start the back-off again
                _log.LogWarning(
                    "Binance: connection {Shard} lost after {Seconds:F0} s at {Rate:F0} messages/s ({Message}); again in {Backoff} s",
                    shard.Index, seconds, shard.Messages / Math.Max(1, seconds), e.Message, backoff.TotalSeconds);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                lock (_gate) shard.Socket = null;
                foreach (var instrument in _instruments.Where(i => i.Shard == shard && i.Streaming))
                    instrument.Failed("Binance connection lost");
            }

            await Task.Delay(backoff, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    /// <summary>Reads frames off a connection — each whole message handled as it lands, synchronously — till it closes.</summary>
    private async Task<byte[]> Receive(Shard shard, ClientWebSocket socket, byte[] buffer, CancellationToken stoppingToken)
    {
        var length = 0;
        while (socket.State == WebSocketState.Open)
        {
            if (length == buffer.Length) // a message bigger than the buffer: rare, so it grows (pinned again)
            {
                var bigger = GC.AllocateArray<byte>(buffer.Length * 2, pinned: true);
                buffer.AsSpan(0, length).CopyTo(bigger);
                buffer = bigger;
            }

            var result = await socket.ReceiveAsync(buffer.AsMemory(length), stoppingToken);
            if (result.MessageType == WebSocketMessageType.Close) return buffer;
            length += result.Count;
            if (!result.EndOfMessage) continue;
            shard.Messages++;
            lock (_handling) Handle(buffer.AsSpan(0, length));
            length = 0;
        }

        return buffer;
    }

    /// <summary>One message off the wire: parsed, normalized, built into the books, posted — timed, allocation counted.</summary>
    private void Handle(ReadOnlySpan<byte> message)
    {
        var start = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var reader = new Utf8JsonReader(message);
        Instrument? instrument = null;
        var kind = StreamKind.None;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("stream"u8))
            {
                reader.Read();
                instrument = Find(reader.ValueSpan, out kind);
            }
            else if (reader.ValueTextEquals("data"u8) && instrument is not null)
            {
                reader.Read();
                var eventMs = instrument.Normalize(ref reader, kind);
                if (eventMs > 0) Stats.Handoff.Record(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000 - eventMs * 1_000_000);
                break;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        _load.AddTicks(Stopwatch.GetTimestamp() - start); // (handling the feed: its load, its health reports')
        var ns = (long)Stopwatch.GetElapsedTime(start).TotalNanoseconds; // parsed, normalized, built and posted
        Stats.Decode.Record(ns);
        Stats.Posted.Record(ns);
        if (++_handled <= FeedStats.WarmUpMessages) return;
        Stats.AllocatedSinceBaseline += GC.GetAllocatedBytesForCurrentThread() - allocated; // (this thread's, this message's)
        Stats.MessagesSinceBaseline = _handled - FeedStats.WarmUpMessages;
    }

    /// <summary>The instrument and kind a stream's name says (<c>btcusdt@depth20@100ms</c>), compared as bytes.</summary>
    private Instrument? Find(ReadOnlySpan<byte> stream, out StreamKind kind)
    {
        kind = StreamKind.None;
        var at = stream.IndexOf((byte)'@');
        if (at < 0) return null;
        var name = stream[..at];
        var rest = stream[(at + 1)..];
        kind = rest.StartsWith("depth"u8) ? StreamKind.Depth :
            rest.StartsWith("bookTicker"u8) ? StreamKind.Top :
            rest.StartsWith("trade"u8) ? StreamKind.Trade : StreamKind.None;
        foreach (var instrument in _instruments)
            if (name.SequenceEqual(instrument.StreamUtf8))
                return instrument;
        return null;
    }

    /// <summary>
    ///     Subscribes and unsubscribes a connection's instruments' streams as asked, one request each way at a time and at
    ///     most a few a second (Binance takes ten messages a second on a connection).
    /// </summary>
    private async Task Sender(Shard shard, CancellationToken stoppingToken)
    {
        await Task.Yield();
        var id = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await shard.SendWanted.WaitAsync(stoppingToken);
                string[] subscribe, unsubscribe;
                ClientWebSocket? socket;
                lock (_gate)
                {
                    socket = shard.Socket;
                    if (socket is null) continue; // (sent again on connecting)
                    subscribe = [.. shard.ToSubscribe];
                    unsubscribe = [.. shard.ToUnsubscribe];
                    shard.ToSubscribe.Clear();
                    shard.ToUnsubscribe.Clear();
                }

                try
                {
                    if (subscribe.Length > 0) await Send(socket, "SUBSCRIBE", subscribe, ++id, stoppingToken);
                    if (unsubscribe.Length > 0) await Send(socket, "UNSUBSCRIBE", unsubscribe, ++id, stoppingToken);
                }
                catch (Exception e) when (e is WebSocketException or IOException or InvalidOperationException)
                {
                    lock (_gate)
                    {
                        foreach (var s in subscribe) shard.ToSubscribe.Add(s); // the next connection subscribes them
                    }
                }

                await Task.Delay(150, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static Task Send(ClientWebSocket socket, string method, string[] instruments, int id,
        CancellationToken cancellationToken)
    {
        var streams = instruments.SelectMany(s => new[] { $"{s}@depth20@100ms", $"{s}@bookTicker", $"{s}@trade" });
        var json = JsonSerializer.Serialize(new { method, @params = streams, id });
        return socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);
    }

    /// <summary>
    ///     Every second: heartbeats to the routers, and the grace periods — an instrument whose topics have all gone quiet
    ///     for <see cref="BinanceOptions.GraceSeconds" /> has its streams unsubscribed on its connection.
    /// </summary>
    private async Task Housekeeping(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _routers.HeartbeatRouters();
                var now = Environment.TickCount64;
                lock (_gate)
                {
                    foreach (var instrument in _instruments)
                    {
                        if (!instrument.Streaming) continue;
                        if (instrument.Listened)
                        {
                            instrument.IdleSinceMs = 0;
                            continue;
                        }

                        if (instrument.IdleSinceMs == 0)
                        {
                            instrument.IdleSinceMs = now;
                            continue;
                        }

                        if (now - instrument.IdleSinceMs < _options.GraceSeconds * 1000L) continue;
                        instrument.Streaming = false; // its grace is over: its streams go, its copy of the book with them
                        instrument.NeedSnapshot = true;
                        var shard = instrument.Shard;
                        shard.ToSubscribe.Remove(instrument.Info.StreamName);
                        shard.ToUnsubscribe.Add(instrument.Info.StreamName);
                        shard.SendWanted.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    ///     A topic parameter (<c>book:BTCUSDT</c>, <c>book-accelerated:…</c>, <c>micro:…</c>, a book's with its shape:
    ///     <c>book:BTCUSDT?depth=10</c>) as its instrument, kind and shape.
    /// </summary>
    private bool Parse(string? parameter, out Instrument instrument, out TopicKind kind, out BookShape shape)
    {
        instrument = null!;
        kind = TopicKind.Book;
        shape = BookShape.Whole;
        if (parameter is null || !BookShape.Split(parameter, out parameter, out shape)) return false;
        var colon = parameter.IndexOf(':');
        if (colon < 0) return false;
        kind = parameter[..colon] switch
        {
            "book" => TopicKind.Book,
            "book-accelerated" => TopicKind.Accelerated,
            "micro" => TopicKind.Micro,
            _ => (TopicKind)(-1)
        };
        return (int)kind >= 0 && _bySymbol.TryGetValue(parameter[(colon + 1)..], out instrument!);
    }

    /// <summary>Whether to post to a topic now, and whether it must be brought up to date first (taken: then up to date).</summary>
    private bool Producing(Topic topic, out bool fresh)
    {
        lock (_gate)
        {
            fresh = topic.Fresh;
            topic.Fresh = false;
            return topic.Live;
        }
    }

    /// <summary>A publish's answer: 202 for the epoch last accepted on stops the topic (and may start its grace period).</summary>
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

    internal enum TopicKind
    {
        Book,
        Accelerated,
        Micro
    }

    private enum StreamKind
    {
        None,
        Depth,
        Top,
        Trade
    }

    /// <summary>One connection and the instruments dealt to it: what to subscribe and unsubscribe next, and its socket.</summary>
    private sealed class Shard(int index)
    {
        public readonly int Index = index;
        public readonly SemaphoreSlim SendWanted = new(0);
        public readonly HashSet<string> ToSubscribe = [], ToUnsubscribe = [];
        public long Messages; // since it last connected
        public Task? Running; // its connection and sender, once an instrument first wanted it
        public ClientWebSocket? Socket;
    }

    private sealed class Topic(Source source, string name, ErrorPolicy policy, PayloadFormat format)
    {
        public readonly string Name = name;
        public readonly ErrorPolicy Policy = policy;
        public readonly Source Source = source;
        public readonly PayloadFormat Format = format;
        public long Epoch;
        public bool Live, Fresh;
    }

    /// <summary>
    ///     One perpetual's topics, books and the state of its streams: the normalizer (Binance's JSON into the wire
    ///     format, a message at a time, in a pinned scratch buffer), the book builder, and the posts. Touched by the
    ///     connection's reader alone, but for the flags under the feed's lock.
    /// </summary>
    private sealed unsafe class Instrument
    {
        private const int MaxLevels = 64, MaxPending = 256;
        private readonly BookBuilder _builder;
        private readonly long[] _askPrices = new long[MaxLevels], _askSizes = new long[MaxLevels];
        private readonly long[] _bidPrices = new long[MaxLevels], _bidSizes = new long[MaxLevels];
        private readonly long[] _newAskPrices = new long[MaxLevels], _newAskSizes = new long[MaxLevels];
        private readonly long[] _newBidPrices = new long[MaxLevels], _newBidSizes = new long[MaxLevels];
        private readonly LastValueSource _micro;
        private readonly TableIngest<Level, long, long> _bookIngest, _acceleratedIngest;
        private readonly (long Seq, long Key, bool Top)[] _pending = new (long, long, bool)[MaxPending];
        private readonly byte* _scratch;
        private readonly byte[] _scratchArray = GC.AllocateArray<byte>(64 + 2 * MaxLevels * 2 * Wire.DepthLevelSize, pinned: true);
        private readonly Level[] _snapshotLevels = new Level[4 * MaxLevels];
        private readonly long[] _snapshotSizes = new long[4 * MaxLevels];
        private readonly byte[] _value = new byte[8];
        private long _acceleratedChain, _bookChain, _lastDepthSeq, _lastDepthMs, _lastDepthU, _publishedMicro, _seq;
        private int _askCount, _bidCount, _pendingCount;

        public Instrument(Publisher publisher, BinanceInstrument info, int window, FeedStats stats, Lock gate,
            Action<ShapedBook, PublishResult> acknowledged)
        {
            Info = info;
            StreamUtf8 = Encoding.UTF8.GetBytes(info.StreamName);
            _builder = new BookBuilder(window, 0, stats, info.Tick);
            fixed (byte* at = _scratchArray) _scratch = at; // (pinned for life)
            var bookFormat = new PayloadFormat
            {
                KeyLayout = new PayloadLayout(9,
                [
                    new PayloadField("side", 0, FieldKind.UInt8)
                        { Names = new Dictionary<long, string> { [Level.Bid] = "bid", [Level.Ask] = "ask" } },
                    new PayloadField("price", 1, FieldKind.Int64) { Scale = info.PriceScale }
                ]),
                Layout = new PayloadLayout(8, [new PayloadField("size", 0, FieldKind.Int64) { Scale = info.SizeScale }])
            };
            var book = publisher.Table<Level, long>($"binance:book:{info.Symbol}");
            var accelerated = publisher.Table<Level, long>($"binance:book:{info.Symbol}:accelerated");
            _bookIngest = book.Ingest<long>(Market.BookFold);
            _acceleratedIngest = accelerated.Ingest<long>(Market.BookFold);
            _micro = (LastValueSource)publisher.LastValues<string>("binance:micro")[info.Symbol];
            Book = new Topic(book, $"binance:book:{info.Symbol}", Market.BookPolicy, bookFormat);
            Accelerated = new Topic(accelerated, $"binance:book:{info.Symbol}:accelerated", Market.BookPolicy,
                bookFormat);
            BookShapes = new ShapedBooks(publisher, Book.Name, bookFormat, info.Tick, 4 * MaxLevels, gate, acknowledged);
            AcceleratedShapes =
                new ShapedBooks(publisher, Accelerated.Name, bookFormat, info.Tick, 4 * MaxLevels, gate, acknowledged);
            Micro = new Topic(_micro, $"binance:micro:{info.Symbol}", Market.MarkPolicy,
                new PayloadFormat
                {
                    Layout = new PayloadLayout(8,
                        [new PayloadField("", 0, FieldKind.Int64) { Scale = info.PriceScale + MicroDigits }])
                });
        }

        public BinanceInstrument Info { get; }
        public byte[] StreamUtf8 { get; }
        public Topic Book { get; }
        public Topic Accelerated { get; }
        public Topic Micro { get; }

        /// <summary>The books' shapes asked for (<see cref="BookShape" />).</summary>
        public ShapedBooks BookShapes { get; }

        public ShapedBooks AcceleratedShapes { get; }

        public BinanceFeed Feed { get; set; } = null!;

        /// <summary>The connection it's dealt to.</summary>
        public Shard Shard { get; init; } = null!;

        /// <summary>Its streams up (under the feed's lock).</summary>
        public bool Streaming;

        /// <summary>The next depth update is to be its book whole (subscribed again, or the connection back).</summary>
        public volatile bool NeedSnapshot = true;

        /// <summary>When its last topic went quiet (Environment.TickCount64), or 0.</summary>
        public long IdleSinceMs;

        public bool Listened => Book.Live || Accelerated.Live || Micro.Live || BookShapes.AnyLive || AcceleratedShapes.AnyLive;

        public Topic TopicOf(TopicKind kind)
        {
            return kind switch { TopicKind.Book => Book, TopicKind.Accelerated => Accelerated, _ => Micro };
        }

        /// <summary>A book's shapes (none for the micro price).</summary>
        public ShapedBooks? ShapesOf(TopicKind kind)
        {
            return kind switch { TopicKind.Book => BookShapes, TopicKind.Accelerated => AcceleratedShapes, _ => null };
        }

        /// <summary>The connection broke: its topics fail till a snapshot recovers them.</summary>
        public void Failed(string why)
        {
            NeedSnapshot = true;
            Book.Source.Fail(FaultCode.SourceFailed, why);
            Accelerated.Source.Fail(FaultCode.SourceFailed, why);
            _micro.Fail(FaultCode.SourceFailed, why);
            BookShapes.Fail(why);
            AcceleratedShapes.Fail(why);
        }

        /// <summary>
        ///     A stream's <c>data</c> object, the reader on its start: normalized to a wire message and handled. Returns the
        ///     event's time (ms since the Unix epoch), or 0.
        /// </summary>
        public long Normalize(ref Utf8JsonReader reader, StreamKind kind)
        {
            switch (kind)
            {
                case StreamKind.Depth: return Depth(ref reader);
                case StreamKind.Top: return Top(ref reader);
                case StreamKind.Trade: return Trade(ref reader);
                default:
                    reader.Skip();
                    return 0;
            }
        }

        // depth20@100ms: {"e":"depthUpdate","E":…,"T":…,"U":…,"u":…,"pu":…,"b":[["p","q"],…],"a":[…]}
        private long Depth(ref Utf8JsonReader reader)
        {
            long eventMs = 0, transactionMs = 0, u = 0;
            int bids = 0, asks = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                if (reader.ValueTextEquals("E"u8)) eventMs = ReadInt64(ref reader);
                else if (reader.ValueTextEquals("T"u8)) transactionMs = ReadInt64(ref reader);
                else if (reader.ValueTextEquals("u"u8)) u = ReadInt64(ref reader);
                else if (reader.ValueTextEquals("b"u8)) bids = ReadLevels(ref reader, _newBidPrices, _newBidSizes);
                else if (reader.ValueTextEquals("a"u8)) asks = ReadLevels(ref reader, _newAskPrices, _newAskSizes);
                else Skip(ref reader);

            var snapshot = NeedSnapshot;
            var reflected = Reflected(u, transactionMs);
            var depth = (Wire.Depth*)_scratch;
            *depth = default;
            var levels = (Wire.DepthLevel*)(_scratch + sizeof(Wire.Depth));
            var n = 0;
            if (snapshot)
            {
                for (var i = 0; i < bids; i++) Put(levels, ref n, Level.Bid, _newBidPrices[i], _newBidSizes[i]);
                for (var i = 0; i < asks; i++) Put(levels, ref n, Level.Ask, _newAskPrices[i], _newAskSizes[i]);
            }
            else
            {
                Diff(levels, ref n, Level.Bid, _bidPrices, _bidSizes, _bidCount, _newBidPrices, _newBidSizes, bids, -1);
                Diff(levels, ref n, Level.Ask, _askPrices, _askSizes, _askCount, _newAskPrices, _newAskSizes, asks, 1);
            }

            Array.Copy(_newBidPrices, _bidPrices, bids);
            Array.Copy(_newBidSizes, _bidSizes, bids);
            Array.Copy(_newAskPrices, _askPrices, asks);
            Array.Copy(_newAskSizes, _askSizes, asks);
            (_bidCount, _askCount) = (bids, asks);
            depth->Header.Template = snapshot ? Wire.SnapshotTemplate : Wire.DepthTemplate;
            depth->Header.Length = (ushort)(sizeof(Wire.Depth) + n * Wire.DepthLevelSize);
            depth->Header.Seq = ++_seq;
            depth->FirstSeq = _lastDepthSeq + 1;
            depth->LastTradeSeq = reflected;
            depth->Timestamp = transactionMs * 1_000_000;
            depth->Count = (ushort)n;
            _lastDepthSeq = _seq;
            _lastDepthMs = transactionMs;
            _lastDepthU = u;
            if (snapshot)
            {
                NeedSnapshot = false;
                _pendingCount = 0; // what came before the book: nothing to reconcile against
            }

            Handled(_builder.Handle(_scratch, Clock.Ns()));
            return eventMs;
        }

        // bookTicker: {"e":"bookTicker","u":…,"s":…,"b":"p","B":"q","a":"p","A":"q","T":…,"E":…}
        private long Top(ref Utf8JsonReader reader)
        {
            long eventMs = 0, u = 0, bid = 0, bidSize = 0, ask = 0, askSize = 0, timeMs = 0;
            int price = Info.PriceScale, size = Info.SizeScale;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                if (reader.ValueTextEquals("u"u8)) u = ReadInt64(ref reader);
                else if (reader.ValueTextEquals("b"u8)) bid = ReadDecimal(ref reader, price);
                else if (reader.ValueTextEquals("B"u8)) bidSize = ReadDecimal(ref reader, size);
                else if (reader.ValueTextEquals("a"u8)) ask = ReadDecimal(ref reader, price);
                else if (reader.ValueTextEquals("A"u8)) askSize = ReadDecimal(ref reader, size);
                else if (reader.ValueTextEquals("T"u8)) timeMs = ReadInt64(ref reader);
                else if (reader.ValueTextEquals("E"u8)) eventMs = ReadInt64(ref reader);
                else Skip(ref reader);

            if (NeedSnapshot || u <= _lastDepthU) return eventMs; // before the book, or already in it
            var top = (Wire.Top*)_scratch;
            *top = default;
            top->Header.Template = Wire.TopTemplate;
            top->Header.Length = (ushort)sizeof(Wire.Top);
            top->Header.Seq = ++_seq;
            top->BidPrice = bid;
            top->BidSize = bidSize;
            top->AskPrice = ask;
            top->AskSize = askSize;
            top->Timestamp = timeMs * 1_000_000;
            _pendingCount = 0; // (superseded, as in the builder: a top states the top outright)
            Pending(_seq, u, true);
            Handled(_builder.Handle(_scratch, Clock.Ns()));
            return eventMs;
        }

        // trade: {"e":"trade","E":…,"T":…,"s":…,"t":…,"p":"…","q":"…","X":"MARKET","m":false}
        private long Trade(ref Utf8JsonReader reader)
        {
            long eventMs = 0, timeMs = 0, price = 0, quantity = 0, id = 0;
            bool market = false, buyerMaker = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                if (reader.ValueTextEquals("p"u8)) price = ReadDecimal(ref reader, Info.PriceScale);
                else if (reader.ValueTextEquals("q"u8)) quantity = ReadDecimal(ref reader, Info.SizeScale);
                else if (reader.ValueTextEquals("T"u8)) timeMs = ReadInt64(ref reader);
                else if (reader.ValueTextEquals("E"u8)) eventMs = ReadInt64(ref reader);
                else if (reader.ValueTextEquals("t"u8)) id = ReadInt64(ref reader);
                else if (reader.ValueTextEquals("X"u8))
                {
                    reader.Read();
                    market = reader.ValueTextEquals("MARKET"u8); // (not the insurance fund's, nor ADL's)
                }
                else if (reader.ValueTextEquals("m"u8))
                {
                    reader.Read();
                    buyerMaker = reader.GetBoolean();
                }
                else Skip(ref reader);

            if (!market || price <= 0 || quantity <= 0 || NeedSnapshot || timeMs <= _lastDepthMs) return eventMs;
            var trade = (Wire.Trade*)_scratch;
            *trade = default;
            trade->Header.Template = Wire.TradeTemplate;
            trade->Header.Length = (ushort)sizeof(Wire.Trade);
            trade->Header.Seq = ++_seq;
            trade->TradeId = id;
            trade->Price = price;
            trade->Size = quantity;
            trade->Timestamp = timeMs * 1_000_000;
            trade->Aggressor = buyerMaker ? Level.Ask : Level.Bid; // the buyer the maker: a seller hit the bid
            Pending(_seq, timeMs, false);
            Handled(_builder.Handle(_scratch, Clock.Ns()));
            return eventMs;
        }

        /// <summary>
        ///     The last accelerator a depth update reflects — a best bid and offer numbered no later than its update id,
        ///     a print no later than its transaction time — so the builder replays only the rest; those reflected forgotten.
        /// </summary>
        private long Reflected(long u, long transactionMs)
        {
            long reflected = 0;
            for (var i = 0; i < _pendingCount; i++)
            {
                var (seq, key, top) = _pending[i];
                if ((top ? key <= u : key <= transactionMs) && seq > reflected) reflected = seq;
            }

            var kept = 0;
            for (var i = 0; i < _pendingCount; i++)
                if (_pending[i].Seq > reflected)
                    _pending[kept++] = _pending[i];
            _pendingCount = kept;
            return reflected;
        }

        private void Pending(long seq, long key, bool top)
        {
            if (_pendingCount == MaxPending) // (the builder's own ring is no bigger)
            {
                Array.Copy(_pending, 1, _pending, 0, MaxPending - 1);
                _pendingCount--;
            }

            _pending[_pendingCount++] = (seq, key, top);
        }

        /// <summary>
        ///     The changes from one side's last levels to its new ones (both best first: <paramref name="sign" /> −1 for
        ///     bids, +1 for asks): a merge — sizes that moved, levels new, and levels no longer shown, gone.
        /// </summary>
        private static void Diff(Wire.DepthLevel* levels, ref int n, byte side, long[] oldPrices, long[] oldSizes,
            int oldCount, long[] newPrices, long[] newSizes, int newCount, int sign)
        {
            int i = 0, j = 0;
            while (i < oldCount || j < newCount)
            {
                if (j == newCount || (i < oldCount && (oldPrices[i] - newPrices[j]) * sign < 0)) // gone
                {
                    Put(levels, ref n, side, oldPrices[i++], 0);
                }
                else if (i == oldCount || (oldPrices[i] - newPrices[j]) * sign > 0) // new
                {
                    Put(levels, ref n, side, newPrices[j], newSizes[j]);
                    j++;
                }
                else // the same price: sent if its size moved
                {
                    if (oldSizes[i] != newSizes[j]) Put(levels, ref n, side, newPrices[j], newSizes[j]);
                    i++;
                    j++;
                }
            }
        }

        private static void Put(Wire.DepthLevel* levels, ref int n, byte side, long price, long size)
        {
            levels[n] = default;
            levels[n].Side = side;
            levels[n].Price = price;
            levels[n++].Size = size;
        }

        private int ReadLevels(ref Utf8JsonReader reader, long[] prices, long[] sizes)
        {
            reader.Read(); // [
            var n = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
            {
                reader.Read();
                var price = Decimals.Parse(reader.ValueSpan, Info.PriceScale);
                reader.Read();
                var size = Decimals.Parse(reader.ValueSpan, Info.SizeScale);
                reader.Read(); // ]
                if (n < MaxLevels && size > 0)
                {
                    prices[n] = price;
                    sizes[n++] = size;
                }
            }

            return n;
        }

        private static long ReadInt64(ref Utf8JsonReader reader)
        {
            reader.Read();
            return reader.GetInt64();
        }

        private static long ReadDecimal(ref Utf8JsonReader reader, int scale)
        {
            reader.Read();
            return Decimals.Parse(reader.ValueSpan, scale);
        }

        private static void Skip(ref Utf8JsonReader reader)
        {
            reader.Read();
            reader.Skip();
        }

        /// <summary>What the builder made of a message, posted.</summary>
        private void Handled(BookBuilder.Outcome outcome)
        {
            switch (outcome)
            {
                case BookBuilder.Outcome.Snapshot:
                    Whole(Book, _bookIngest, false, _builder.Version, ref _bookChain);
                    Whole(Accelerated, _acceleratedIngest, true, _builder.MessageSeq, ref _acceleratedChain);
                    _publishedMicro = 0;
                    PostMicro();
                    BookShapes.Post(_builder, false, _builder.Version, true);
                    AcceleratedShapes.Post(_builder, true, _builder.MessageSeq, true);
                    break;
                case BookBuilder.Outcome.Trade:
                    PostAccelerated();
                    PostMicro();
                    AcceleratedShapes.Post(_builder, true, _builder.MessageSeq, false);
                    break;
                case BookBuilder.Outcome.Depth:
                    Post(Book, _bookIngest, _builder.BookChanges, false, _builder.Version, ref _bookChain);
                    PostAccelerated();
                    PostMicro();
                    BookShapes.Post(_builder, false, _builder.Version, false);
                    AcceleratedShapes.Post(_builder, true, _builder.MessageSeq, false);
                    break;
                case BookBuilder.Outcome.Gap:
                case BookBuilder.Outcome.TooWide:
                    Failed(outcome == BookBuilder.Outcome.Gap ? "the book can't be trusted" : "the book is too wide");
                    break;
            }
        }

        private void PostAccelerated()
        {
            if (_builder.AcceleratedWhole && Feed.Producing(Accelerated, out _))
                Whole(Accelerated, _acceleratedIngest, true, _builder.MessageSeq, ref _acceleratedChain);
            else
                Post(Accelerated, _acceleratedIngest, _builder.AcceleratedChanges, true, _builder.MessageSeq,
                    ref _acceleratedChain);
        }

        /// <summary>
        ///     The micro price — the mid weighted by the size on the other side, (bid × ask size + ask × bid size) / (bid
        ///     size + ask size): nearer the ask when bids outweigh asks — from the accelerated book's top, the freshest there
        ///     is; posted when it moves.
        /// </summary>
        private void PostMicro()
        {
            if (_builder.Recovering || !Feed.Producing(Micro, out var fresh)) return;
            _builder.AcceleratedTop(out var bid, out var bidSize, out var ask, out var askSize);
            if (bid == Ladder.None || ask == Ladder.None || bidSize + askSize <= 0) return;
            var scaled = ((Int128)bid * askSize + (Int128)ask * bidSize) * 100 / (bidSize + askSize);
            var micro = (long)scaled;
            if (!fresh && micro == _publishedMicro) return;
            BinaryPrimitives.WriteInt64LittleEndian(_value, _publishedMicro = micro);
            Feed.Acknowledged(Micro, _micro.Publish(_value)); // last value: only the newest reaches each client
        }

        private void Post(Topic topic, TableIngest<Level, long, long> ingest, ChangeSet changes, bool accelerated,
            long version, ref long chain)
        {
            if (_builder.Recovering || !Feed.Producing(topic, out var fresh)) return;
            if (fresh) // (re)subscribed: the whole book first, from the feed's own copy
            {
                Whole(topic, ingest, accelerated, version, ref chain);
                return;
            }

            if (changes.Count == 0) return;
            try
            {
                var posted = ingest.Post(changes.LevelSpan, changes.DeltaSpan, chain, version);
                if (posted.Status == PublishStatus.Full)
                {
                    Whole(topic, ingest, accelerated, version, ref chain); // the router is behind: the whole book
                    return;
                }

                chain = version;
                Feed.Acknowledged(topic, posted);
            }
            catch (StreamFaultException) // faulted: the whole book recovers it
            {
                Whole(topic, ingest, accelerated, version, ref chain);
            }
        }

        private void Whole(Topic topic, TableIngest<Level, long, long> ingest, bool accelerated, long version,
            ref long chain)
        {
            var count = _builder.Fill(_snapshotLevels, _snapshotSizes, accelerated);
            Feed.Acknowledged(topic,
                ingest.PostSnapshot(_snapshotLevels.AsSpan(0, count), _snapshotSizes.AsSpan(0, count), version));
            chain = version;
        }
    }
}
