using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ActivityFeed.Contracts;
using ActivityFeed.Service;
using Grpc.Net.Client;
using MagicOnion.Client;
using Tapline;
using Tapline.Client;
using Tapline.MagicOnion;

namespace ActivityFeed.Headless;

/// <summary>
///     The practice measured (<c>--measure SECONDS</c>), its service spawned quicker and measuring itself
///     (<c>Practice:Measure</c>), every feed on one session:
///     <list type="bullet">
///         <item>
///             step 1, each feed's first: every persona's, then every other patient's, asked for one after another, each
///             timed from its call to its answer, and to its first state in front of this client;
///         </item>
///         <item>
///             staleness, for SECONDS: each activity that happens while they're open, from its happening (its time, by the
///             service's clock: this machine's) to its arrival in each feed it's in, by kind of feed — the provider's hop
///             and the router's together;
///         </item>
///         <item>step 1 again: a receptionist's and a patient's feed dropped and asked for again, over and over;</item>
///         <item>
///             the service's own figures (its <c>/measure</c>, spawned): its posts a second, its turns, and its posts'
///             lateness: the provider's hop alone;
///         </item>
///         <item>
///             with <c>--green</c>, green's replica lag: green started from the write log once the feeds are open, it
///             follows blue through the window, each turn timed from blue's append to its read on green (by the log's
///             signal, or with <c>--green-poll MS</c>, by watching the log's file) and to its run there.
///         </item>
///     </list>
///     No demo, and nothing checked: that's the verifier's (<c>--verify</c>).
/// </summary>
internal sealed class Measurer(GrpcChannel channel, Uri app, HttpClient http, Uri? service, Backend? green = null, double? greenPoll = null)
{
    private const int Rosa = 101, Jane = 1001, Again = 20;

    // A connection each — each person's signed in as them (passthrough), and the dispatcher topics' observer's — for the
    // practice serves a connection its own person's feed alone. The router's heartbeats: timed on the observer's.
    private readonly Dictionary<string, TaplineStreamConnection> _connections = [];
    private CancellationToken _stop;
    private const string Observer = "observer";

    private TaplineStreamConnection ConnectionOf(string user)
    {
        lock (_connections)
        {
            if (_connections.TryGetValue(user, out var known)) return known;
            var connection = new TaplineStreamConnection(channel, new TaplineStreamConnectionOptions
            {
                Client = new TaplineClientOptions { Http = http, BlobBase = new Uri(app, "_tapline/blob") }
            });
            _connections[user] = connection;
            _ = connection.RunAsync(_stop);
            return connection;
        }
    }

    private readonly object _gate = new(); // (taken inside the client's lock, never around it)
    private readonly Dictionary<string, Samples> _first = [], _again = [], _staleness = [];
    private int _ended;
    private ViewsReport? _views;
    private Collections? _collections; // (this client's, over the window)
    private Sample.CollectionsReport? _app; // (the app's: the router's process)
    // The router's heartbeats (its send time on them, this machine's clock): the router-to-client path on its own.
    private readonly Samples _heartbeats = new("router heartbeats: sent to received");
    private DateTimeOffset _lastHeartbeat;
    private double _heartbeatGap; // (the longest between two, ms)
    private readonly List<(DateTime At, string Kind, int Person, long Seq, double Ms)> _slowest = [];
    private const int Slowest = 8;

    private IPracticeFeed Practice(CancellationToken token)
    {
        return MagicOnionClient.Create<IPracticeFeed>(channel).WithCancellationToken(token);
    }

    public async Task<int> RunAsync(TimeSpan window, CancellationToken token)
    {
        _stop = token;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = ConnectionOf(Observer);
        observer.Connected += (_, _) => connected.TrySetResult();
        if (observer.Session is not null) connected.TrySetResult();
        observer.Client.HeartbeatReceived += heartbeat =>
        {
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                _heartbeats.Add((now - heartbeat.SentAt).TotalMilliseconds);
                if (_lastHeartbeat != default) _heartbeatGap = Math.Max(_heartbeatGap, (now - _lastHeartbeat).TotalMilliseconds);
                _lastHeartbeat = now;
            }
        };
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            var personas = (await Practice(token).PersonasAsync()).Where(p => p.Role != "auditor").ToArray(); // (no feed: the audit page)
            List<Feed> feeds = [];
            var opening = Stopwatch.StartNew();
            foreach (var persona in personas)
            {
                feeds.Add(await OpenAsync(persona.Id, KindOf(persona.Role), _first, token));
                if (persona.Role == "manager") feeds.Add(await OpenAsync(persona.Id, "managers", _first, token)); // (their statistics)
            }
            foreach (var patient in People.Patients.Where(p => personas.All(q => q.Id != p.Id)))
                feeds.Add(await OpenAsync(patient.Id, "patients", _first, token));
            for (var shard = 0; shard < 2; shard++) feeds.Add(await OpenAsync(shard, "dispatchers", _first, token));
            Console.Out.WriteLine($"step 1: {feeds.Count} feeds open, one after another, in {opening.Elapsed.TotalSeconds:F1} s");
            Console.Out.Flush();
            if (green is not null) await FollowingAsync(green, token);

            // Staleness, from here: the service's figures and this client's started again together.
            var started = await MeasuredAsync(true, token);
            lock (_gate)
            {
                foreach (var samples in _staleness.Values) samples.Clear();
                _heartbeats.Clear();
                (_heartbeatGap, _lastHeartbeat) = (0, default);
                _slowest.Clear();
            }

            var collected = Collections.Now();
            await AppCollectionsAsync(true, token);

            var measuring = Stopwatch.StartNew();
            while (measuring.Elapsed < window)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, (window - measuring.Elapsed).TotalSeconds + 0.01)), token);
                lock (_gate)
                {
                    Console.Out.WriteLine($"{measuring.Elapsed.TotalSeconds,5:F0} s: {string.Join(", ", _staleness.Select(s => $"{s.Key} {s.Value.Read().Samples}"))} arrivals measured");
                }

                Console.Out.Flush();
            }

            var measured = await MeasuredAsync(false, token);
            var replica = green is null ? null : await LogOfAsync(green.GreenHttp, token);
            _collections = Collections.Now().Since(collected);
            _app = await AppCollectionsAsync(false, token);
            _views = service is null ? null : await http.GetFromJsonAsync<ViewsReport>(new Uri(service, "views"), JsonSerializerOptions.Web, token);
            Percentiles[] staleness;
            lock (_gate)
            {
                staleness = [.. _staleness.Values.Select(s => s.Read())];
            }

            // Step 1 again: a receptionist's feed and a patient's, each dropped and asked for again.
            foreach (var (person, kind) in new[] { (Rosa, "staff"), (Jane, "patients") })
            {
                var feed = feeds.First(f => f.Person == person && f.Kind == kind);
                for (var i = 0; i < Again; i++)
                {
                    ConnectionOf(feed.User).Client.Drop(feed.TapId);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), token); // (its route let go: the router's, the provider's)
                    feed = await OpenAsync(person, kind, _again, token);
                }
            }

            Report(window, started is not null, measured, staleness);
            if (replica is not null) ReportReplica(replica);
            return Volatile.Read(ref _ended) == 0 ? 0 : 1;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return 1;
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine($"measure: {e.Message}");
            return 1;
        }
        finally
        {
            lock (_connections)
            {
                foreach (var connection in _connections.Values) connection.Drop();
            }
        }
    }

    private static string KindOf(string role)
    {
        return role switch
        {
            "reception" or "clinician" or "manager" => "staff", // (a manager's schedule; their statistics, "managers")
            "screen" => "screens",
            _ => "patients" // (and carers: a topic of their own, as a patient's)
        };
    }

    /// <summary>
    ///     A feed's step 1 (its person, or a shard's dispatcher topic, in its parameters), asked again while the service
    ///     starts, then timed: from its call to its answer, and to its first state in front of this client.
    /// </summary>
    private async Task<Feed> OpenAsync(int id, string kind, Dictionary<string, Samples> into, CancellationToken token)
    {
        var feed = new Feed(id, kind);
        var connection = ConnectionOf(feed.User);
        for (var delay = TimeSpan.FromMilliseconds(250);; delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.TicksPerSecond * 2)))
        {
            if (connection.Session is not { } session || !await connection.Client.SignedInAsync(feed.User, token))
            {
                await Task.Delay(delay, token);
                continue;
            }

            var asked = Stopwatch.GetTimestamp();
            var answer = kind == "dispatchers"
                ? await Practice(token).DispatchAsync(session.OutletId, session.Nonce, id, Observer)
                : await Practice(token).SubscribeAsync(session.OutletId, session.Nonce, id, kind == "managers" ? "stats" : null);
            if (answer.Unavailable || answer.UnknownSession)
            {
                await Task.Delay(delay, token);
                continue;
            }

            if (answer.Refused is { } why) throw new InvalidOperationException($"{kind} {id}: refused: {why}");
            var answered = Stopwatch.GetElapsedTime(asked);
            feed.TapId = answer.TapId;
            connection.Client.Subscribe(
                new TapBinding(answer.TapId, answer.Topic, null, answer.Service, answer.NodeId, answer.Partition),
                new SubscriptionHandler
                {
                    Table = (_, _, batch) => Applied(feed, batch),
                    Error = (_, error) =>
                    {
                        Interlocked.Increment(ref _ended);
                        Console.Error.WriteLine($"{answer.Topic} ended: {error.Code}");
                    }
                });
            await feed.First.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            var first = Stopwatch.GetElapsedTime(asked);
            lock (_gate)
            {
                Sample(into, $"{kind}: answered", answered.TotalMilliseconds);
                Sample(into, $"{kind}: first state", first.TotalMilliseconds);
            }

            return feed;
        }
    }

    /// <summary>
    ///     A feed's record applied (under the client's lock): its first state noted, what it holds and when it came;
    ///     after it, each activity new to it that happened since, its staleness. A row it had (changed), or one that
    ///     happened before it opened (newly shown to it), measures nothing.
    /// </summary>
    private void Applied(Feed feed, TableBatch batch)
    {
        var now = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10; // (micros, as a row's time)
        lock (_gate)
        {
            foreach (var entry in batch)
            {
                if (entry.Deleted) continue;
                var key = entry.KeyAs<FeedKey>();
                if (!feed.Held.Add((key.Section, key.Seq)) || feed.Opened == 0) continue;
                var at = feed.Kind switch
                {
                    "screens" => entry.RowAs<ScreenRow>().AtMicros,
                    "dispatchers" => entry.RowAs<NotifyRow>().AtMicros,
                    "staff" or "patients" when key.Section == FeedKey.Activity => entry.RowAs<FeedRow>().AtMicros,
                    _ => 0 // (the manager's figures: an hour's, not an activity's)
                };
                if (at < feed.Opened) continue;
                var ms = (now - at) / 1000.0;
                Sample(_staleness, feed.Kind, ms);
                if (_slowest.Count < Slowest || ms > _slowest[^1].Ms)
                {
                    _slowest.Add((DateTime.Now, feed.Kind, feed.Person, key.Seq, ms));
                    _slowest.Sort((a, b) => b.Ms.CompareTo(a.Ms));
                    if (_slowest.Count > Slowest) _slowest.RemoveAt(Slowest);
                }
            }

            if (feed.Opened != 0) return;
            feed.Opened = now;
            feed.First.TrySetResult();
        }
    }

    private static void Sample(Dictionary<string, Samples> into, string of, double milliseconds)
    {
        if (!into.TryGetValue(of, out var samples)) into[of] = samples = new Samples(of);
        samples.Add(milliseconds);
    }

    /// <summary>The app's collections (its <c>/ops/collections</c>, measured), and, asked to, started again.</summary>
    private async Task<Sample.CollectionsReport?> AppCollectionsAsync(bool reset, CancellationToken token)
    {
        if (service is null) return null; // (spawned only: it's measured then)
        try
        {
            return await http.GetFromJsonAsync<Sample.CollectionsReport>(
                new Uri(app, $"ops/collections?reset={(reset ? "true" : "false")}"), JsonSerializerOptions.Web, token);
        }
        catch (HttpRequestException e)
        {
            Console.Error.WriteLine($"the app's collections: {e.Message}");
            return null;
        }
    }

    /// <summary>The service's figures (spawned: its <c>/measure</c>), and, asked to, started again.</summary>
    private async Task<MeasureReport?> MeasuredAsync(bool reset, CancellationToken token)
    {
        if (service is null) return null;
        try
        {
            return await http.GetFromJsonAsync<MeasureReport>(new Uri(service, $"measure?reset={(reset ? "true" : "false")}"),
                JsonSerializerOptions.Web, token);
        }
        catch (HttpRequestException e)
        {
            Console.Error.WriteLine($"the service's figures: {e.Message}");
            return null;
        }
    }

    private void Report(TimeSpan window, bool measuring, MeasureReport? measured, Percentiles[] staleness)
    {
        var o = Console.Out;
        o.WriteLine();
        o.WriteLine("step 1, each feed's first (one after another), ms — answered, and its first state here:");
        Print(o, _first.Values);
        o.WriteLine($"staleness over {window.TotalSeconds:F0} s, ms — an activity's happening (the service's clock) to its arrival here:");
        Print(o, staleness.OrderBy(p => p.Of));
        lock (_gate)
        {
            Print(o, [_heartbeats.Read()]);
            o.WriteLine($"  the longest between two heartbeats: {_heartbeatGap:F0} ms");
            o.WriteLine($"  the slowest arrivals: {string.Join("; ", _slowest.Select(s => $"{s.At:HH:mm:ss.fff} {s.Kind} {s.Person} #{s.Seq} {s.Ms:F0} ms"))}");
        }

        if (_collections is { } c) o.WriteLine($"  this client: {c}");
        if (_app is { } a)
            o.WriteLine($"  the app (the router): {a.Gen0} gen0, {a.Gen1} gen1, {a.Gen2} gen2 collections, paused {a.PausedMs:F1} ms in all; " +
                        $"the longest: {string.Join("; ", a.Longest.Select(p => $"{p.At:HH:mm:ss.fff} gen{p.Generation} {p.Ms:F1} ms"))}");
        if (measured is { } m)
        {
            o.WriteLine($"the provider's hop: {m.Activities} activities in {m.Seconds:F0} s; {m.Posts} posts, " +
                        $"{m.PostsPerSecond:F0} a second, {m.PostMicros:F0} µs each; {m.Collections}");
            o.WriteLine("  its turns, ms:");
            Print(o, m.Turns, "    ");
            o.WriteLine("  its posts by kind of topic (whole: snapshots), and their lateness, ms — a row's happening to its post:");
            foreach (var t in m.Topics)
                o.WriteLine($"    {t.Of,-20} {t.Posts,6} posts, {t.Snapshots,5} whole   " +
                            $"lateness n {t.Lateness.Samples,6}   p50 {t.Lateness.P50,8:F2}   p99 {t.Lateness.P99,8:F2}   " +
                            $"p99.5 {t.Lateness.P995,8:F2}   max {t.Lateness.Max,8:F2}");
        }
        else if (measuring)
        {
            o.WriteLine("the provider's hop: its figures didn't come");
        }

        if (measured is { } u)
        {
            o.WriteLine("  each view's update, ms (the busiest first, by its total):");
            Print(o, u.Updates.OrderByDescending(p => p.P50 * p.Samples), "    ");
        }

        if (_views is { } views)
        {
            o.WriteLine($"  its views ({views.Maintenance}{(views.Validate ? ", each checked against its recomputed twin" : "")}):");
            foreach (var v in views.Views.OrderBy(v => v.View))
                o.WriteLine($"    {v.View,-36} {v.Kept,-12} {v.Updates,6} updates {v.Checks,6} checks {v.Rows,6} rows {v.StateBytes / 1024.0,9:F1} KiB state");
            foreach (var g in views.GaveWay) o.WriteLine($"    gave way: {g.View} at {g.At:HH:mm:ss}: {g.Why}");
            foreach (var why in views.Views.Where(v => v.Why is not null).GroupBy(v => v.Why))
                o.WriteLine($"    recomputed ({string.Join(", ", why.Select(v => v.View))}): {why.Key}");
        }

        o.WriteLine($"step 1 again ({Again} times each: dropped, then asked for 250 ms later), ms:");
        Print(o, _again.Values);
        o.Flush();
    }

    /// <summary>Green started from the log (the feeds open on blue), and caught up: following blue from here.</summary>
    private async Task FollowingAsync(Backend backend, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        await backend.StartGreenAsync(token, greenPoll);
        while (timer.Elapsed < TimeSpan.FromMinutes(3))
        {
            if (await LogOfAsync(backend.GreenHttp, token) is { Role: "following" } g &&
                await LogOfAsync(backend.BlueHttp, token) is { } b && g.Read >= b.LastTurn)
            {
                Console.Out.WriteLine($"green: caught up from the write log in {timer.Elapsed.TotalSeconds:F1} s ({g.Turns} turns run), following blue " +
                                      (greenPoll is { } ms ? $"by watching the log's file every {ms} ms" : "by the log's signal"));
                Console.Out.Flush();
                return;
            }

            await Task.Delay(250, token);
        }

        throw new TimeoutException("green didn't catch up from the write log within three minutes");
    }

    private async Task<LogStatus?> LogOfAsync(Uri health, CancellationToken token)
    {
        try
        {
            return await http.GetFromJsonAsync<LogStatus>(new Uri(health, "log"), JsonSerializerOptions.Web, token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private void ReportReplica(LogStatus replica)
    {
        var o = Console.Out;
        o.WriteLine($"green's replica lag, ms — a turn's append by blue to its read on green, and to its run there ({replica.Signal}; " +
                    $"{replica.Signals} signals heard, {replica.Divergences.Length} turns differing):");
        Print(o, [replica.Heard ?? new Percentiles("read", 0, 0, 0, 0, 0), replica.Lag ?? new Percentiles("run", 0, 0, 0, 0, 0)]);
        o.Flush();
    }

    private static void Print(TextWriter o, IEnumerable<Samples> samples, string indent = "  ")
    {
        Print(o, samples.Select(s => s.Read()), indent);
    }

    private static void Print(TextWriter o, IEnumerable<Percentiles> rows, string indent = "  ")
    {
        foreach (var p in rows)
            o.WriteLine($"{indent}{p.Of,-36} n {p.Samples,6}   p50 {p.P50,8:F2}   p99 {p.P99,8:F2}   p99.5 {p.P995,8:F2}   max {p.Max,8:F2}");
    }

    /// <summary>A feed open on the session: what it holds (by part and sequence), and when its first state came.</summary>
    private sealed class Feed(int person, string kind)
    {
        public int Person => person;
        public string Kind => kind;

        /// <summary>Whom its connection signs in as: its person, or the dispatcher topics' observer.</summary>
        public string User => kind == "dispatchers" ? "observer" : PracticeSignIn.UserOf(person);
        public uint TapId { get; set; }
        public long Opened { get; set; } // (its first state's arrival, in micros: 0 before)
        public HashSet<(byte, long)> Held { get; } = [];
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
