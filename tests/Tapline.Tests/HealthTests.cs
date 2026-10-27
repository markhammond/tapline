using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tapline;
using Tapline.AspNetCore;
using Tapline.Client;
using Tapline.Tests;
using HealthReport = Tapline.HealthReport;
using HostHealthReport = Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport;

/// <summary>
///     The router's own state: on every client's heartbeat, damped from its host's health checks, drained as it
///     stops; a client's router check.
/// </summary>
internal static partial class T
{
    public static Task RouterStateOnHeartbeat()
    {
        var registry = new OutletRegistry();
        var changes = new List<RouterHealth>();
        registry.HealthChanged += changes.Add;
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();

        (byte[] Bytes, long Seq) Build(bool tick)
        {
            if (tick) s.Heartbeat();
            Assert(s.TryBuildFrame(owner, false, out var f, out var seq),
                tick ? "a heartbeat" : "a change: its heartbeat at once, no tick needed");
            var bytes = f.ToArray();
            s.ReleaseFrame();
            c.Decode(bytes);
            return (bytes, seq);
        }

        byte[] Next(bool tick)
        {
            var (bytes, seq) = Build(tick);
            s.Ack(seq, c.HeartbeatSeq); // echoed
            return bytes;
        }

        const int Hb = Wire.HeartbeatSize,
            Change = Wire.HeartbeatSize + Wire.RouterChangeHeader; // a heartbeat; and the router's change behind it

        var hb = Next(false); // attaching queues one
        var serving = registry.Health;
        Assert(hb.Length == Change && hb[Hb] == Wire.RouterChange && hb[Hb + 1] == (byte)RouterState.Serving &&
               Wire.RouterChangeLength(hb.AsSpan(Hb)) == Wire.RouterChangeHeader
               && c.RouterState == RouterState.Serving && c.RouterCorrelation == serving.Correlation &&
               serving.Correlation.Version == 7 && c.LastFrameRecords.SequenceEqual([Wire.Heartbeat]),
            $"serving from the start: the first heartbeat has the router's change behind it — the state, its correlation id (a UUIDv7) — {Change} bytes ({hb.Length}, {c.RouterCorrelation})");
        hb = Next(true);
        Assert(hb.Length == Hb && c.RouterChanges == 1, $"echoed: heartbeats alone, {Hb} bytes ({hb.Length})");

        const string Why = "db: Unhealthy, timed out";
        var (inFlight, inFlightSeq) = Build(true); // a heartbeat in flight as the state changes
        var degraded = registry.SetHealth(RouterState.Degraded, Why);
        Assert(inFlight.Length == Hb && !s.TryBuildFrame(owner, false, out _, out _),
            "one heartbeat in flight: the change waits for its echo");
        s.Ack(inFlightSeq, c.HeartbeatSeq);
        hb = Next(false);
        Assert(hb.Length == Change + 1 + Why.Length && Wire.RouterChangeLength(hb.AsSpan(Hb)) == hb.Length - Hb &&
               c.RouterState == RouterState.Degraded
               && c.RouterCorrelation == degraded.Correlation && degraded.Correlation != serving.Correlation
               && Encoding.UTF8.GetString(hb.AsSpan(Change + 1, hb[Change])) == Why,
            "degraded: once echoed, at once (no tick) — why, under a new correlation id");
        hb = Next(true);
        Assert(hb.Length == Hb && c.RouterChanges == 2, "echoed: heartbeats alone");
        Assert(
            ReferenceEquals(registry.SetHealth(RouterState.Degraded, Why), degraded) &&
            !s.TryBuildFrame(owner, false, out _, out _),
            "the same again: no change, no heartbeat");
        s.Attach(owner = new object()); // a new connection: what the old one carried may not have arrived
        hb = Next(false);
        Assert(
            hb.Length == Change + 1 + Why.Length && c.RouterCorrelation == degraded.Correlation && c.RouterChanges == 3,
            "reconnected: the change told again");

        var longWhy = string.Concat(Enumerable.Repeat("é∑", 200)); // 2- and 3-byte characters: 1,000 bytes
        var cut = registry.SetHealth(RouterState.Degraded, longWhy);
        hb = Next(false);
        var told = Encoding.UTF8.GetString(hb.AsSpan(Change + 1, hb[Change]));
        Assert(
            told.Length > 0 && longWhy.StartsWith(told, StringComparison.Ordinal) &&
            hb.Length <= Wire.MaxHeartbeatSize && cut.Reason == longWhy,
            $"a long reason: cut on a character's boundary, at most {Wire.MaxRouterReason} bytes on the wire ({hb[Change]}), whole in the log");

        var draining = registry.SetHealth(RouterState.Draining, grace: TimeSpan.FromSeconds(10));
        hb = Next(false);
        var graceMs = BinaryPrimitives.ReadUInt32LittleEndian(hb.AsSpan(Change));
        Assert(hb.Length == Change + 4 && Wire.RouterChangeLength(hb.AsSpan(Hb)) == hb.Length - Hb &&
               c.RouterState == RouterState.Draining && c.RouterCorrelation == draining.Correlation
               && graceMs is > 9_000 and <= 10_000 && draining.DrainsBy == draining.At + TimeSpan.FromSeconds(10),
            $"draining: the grace left ({graceMs} ms)");
        hb = Next(true);
        Assert(hb.Length == Hb, "echoed: heartbeats alone");
        Assert(
            ReferenceEquals(registry.SetHealth(RouterState.Serving), draining) &&
            ReferenceEquals(registry.SetHealth(RouterState.Degraded, "x"), draining),
            "draining is one way");

        Thread.Sleep(20);
        var later = NewOutlet(registry); // a client arriving now: told the change, with the grace left then
        var laterOwner = new object();
        later.Attach(laterOwner);
        Assert(later.TryBuildFrame(laterOwner, false, out var lf, out _) && lf[Hb] == Wire.RouterChange &&
               lf[Hb + 1] == (byte)RouterState.Draining
               && BinaryPrimitives.ReadUInt32LittleEndian(lf[Change..]) < graceMs,
            "a client arriving: told the change, the grace left as it is");
        later.ReleaseFrame();
        Assert(changes.Select(h => h.State)
                   .SequenceEqual([RouterState.Degraded, RouterState.Degraded, RouterState.Draining])
               && changes.Select(h => h.Correlation).Distinct().Count() == 3,
            "each change told once (HealthChanged), each with its own id");
        return Task.CompletedTask;
    }

    public static Task RouterHealthDamping()
    {
        var time = new ManualTime();
        var registry = new OutletRegistry(new TimingWheel(time: time, autoStart: false));
        var log = new ListLogger();
        var damper = new RouterHealthDamper(registry, new RouterHealthOptions
        {
            DegradeAfter = TimeSpan.FromSeconds(10), RecoverAfter = TimeSpan.FromSeconds(30),
            MaxRecoverAfter = TimeSpan.FromSeconds(100), FlapWindow = TimeSpan.FromMinutes(10)
        }, log);
        var changes = new List<RouterHealth>();
        registry.HealthChanged += changes.Add;
        const string Db = "db=Unhealthy", Disk = "db=Unhealthy;disk=Degraded";

        RouterState Step(double after, string failing = "")
        {
            time.Advance(TimeSpan.FromSeconds(after));
            return damper.Report(failing, failing.Length > 0 ? $"reason: {failing}" : null).State;
        }

        const RouterState Serving = RouterState.Serving, Degraded = RouterState.Degraded;

        Assert(Step(0, Db) == Serving && Step(5, Db) == Serving && Step(5) == Serving && changes.Count == 0,
            "a blip, shorter than DegradeAfter: nothing told");
        Assert(
            Step(5, Db) == Serving && Step(9, Db) == Serving && Step(1, Db) == Degraded &&
            changes is [{ Reason: "reason: db=Unhealthy" }],
            "failing for DegradeAfter, report after report: degraded, at the first report past it, why");
        var episode = registry.Health.Correlation;
        Assert(
            Step(10) == Degraded && Step(20) == Degraded && Step(5, Db) == Degraded && Step(5) == Degraded &&
            Step(29) == Degraded, "passing, but not for RecoverAfter in a row");
        Assert(Step(0, Disk) == Degraded && changes.Count == 1 && registry.Health.Correlation == episode
               && log.Has(LogLevel.Warning, "still degraded", "disk=Degraded", episode.ToString()),
            "other checks failing while degraded: logged under the episode's id, not told again");
        Assert(Step(0) == Degraded && Step(29) == Degraded && Step(1) == Serving && changes.Count == 2,
            "passing for RecoverAfter: serving again");

        Assert(
            Step(0, Db) == Serving && Step(10, Db) == Degraded && Step(0) == Degraded && Step(59) == Degraded &&
            Step(1) == Serving,
            "degraded again within the flap window: recovery takes twice as long");
        Assert(
            Step(0, Db) == Serving && Step(10, Db) == Degraded && Step(0) == Degraded && Step(99) == Degraded &&
            Step(1) == Serving,
            "and again: four times — capped at MaxRecoverAfter");
        Assert(
            Step(0, Db) == Serving && Step(10, Db) == Degraded && Step(0) == Degraded && Step(99) == Degraded &&
            Step(1) == Serving, "…capped");
        Assert(
            Step(601) == Serving && Step(0, Db) == Serving && Step(10, Db) == Degraded && Step(0) == Degraded &&
            Step(30) == Serving,
            "past the flap window: RecoverAfter again");
        Assert(changes.Count == 10 && changes.Zip(changes.Skip(1)).All(p => p.First.State != p.Second.State),
            "only transitions told");

        registry.SetHealth(RouterState.Draining, grace: TimeSpan.FromSeconds(1));
        Assert(Step(0, Db) == RouterState.Draining && Step(10, Db) == RouterState.Draining && changes.Count == 11,
            "draining: reports change nothing");

        var report = new HostHealthReport(new Dictionary<string, HealthReportEntry>
        {
            ["disk"] = new(HealthStatus.Degraded, "92% full", TimeSpan.Zero, null, null, ["infra"]),
            ["db"] = new(HealthStatus.Unhealthy, null, TimeSpan.Zero, new TimeoutException("timed out"), null,
                ["tapline"]),
            ["cache"] = new(HealthStatus.Healthy, null, TimeSpan.Zero, null, null)
        }, TimeSpan.Zero);
        Assert(
            RouterHealthPublisher.Summarize(report, null) == ("db=Unhealthy;disk=Degraded",
                "db: Unhealthy, timed out; disk: Degraded, 92% full"),
            $"a report: the failing checks, in order, each with its description (or its exception's): {RouterHealthPublisher.Summarize(report, null)}");
        Assert(
            RouterHealthPublisher.Summarize(report, ["tapline"]) == ("db=Unhealthy", "db: Unhealthy, timed out") &&
            RouterHealthPublisher.Summarize(report, ["none"]) == ("", null),
            "by tag: only the checks that count");
        return Task.CompletedTask;
    }

    public static async Task RouterDrainOnStop()
    {
        foreach (var (grace, move) in new[]
                 {
                     (TimeSpan.Zero, false), (TimeSpan.FromSeconds(10), true), (TimeSpan.FromMilliseconds(300), false)
                 })
        {
            var log = new ListLogger();
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders().AddProvider(log);
            builder.Services.AddTapline(o => o.Health.DrainGrace = grace);
            using var host = builder.Build();
            await host.StartAsync();
            var registry = host.Services.GetRequiredService<OutletRegistry>();
            Assert(log.Has(LogLevel.Information, "Router serving", registry.Health.Correlation.ToString()),
                "its first state logged, with the id its clients see");
            var values = new LastValues<int>("drain");
            var here = NewOutlet(registry);
            here.Attach(new object());
            var tap = here.Tap(values[0], activate: true);
            var away = NewOutlet(registry);
            away.Attach(new object());
            away.Tap(values[1], activate: true);
            away.Detach(); // gone: can't be told, nor move through this router — not waited for
            var idle = NewOutlet(registry); // connected, nothing subscribed: nothing to move
            idle.Attach(new object());

            var start = Stopwatch.GetTimestamp();
            var stopping = host.StopAsync();
            for (var i = 0; i < 200 && registry.Health.State != RouterState.Draining; i++) await Task.Delay(10);
            var health = registry.Health;
            Assert(health.State == RouterState.Draining && health.DrainsBy == health.At + grace
                                                        && log.Has(LogLevel.Information, "Router draining",
                                                            health.Correlation.ToString()),
                $"stopping: draining at once, the grace {grace} told and logged");
            if (move)
            {
                await Task.Delay(100);
                Assert(!stopping.IsCompleted, "held while a connected client holds a subscription here");
                tap.Dispose(); // it moved: subscribed elsewhere, it lets this one go
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(
                    Stopwatch.GetElapsedTime(start) < grace && log.Has(LogLevel.Information, "every client moved",
                        health.Correlation.ToString()),
                    "…stopped once it moved, well within the grace");
            }
            else
            {
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                var took = Stopwatch.GetElapsedTime(start);
                Assert(
                    grace == TimeSpan.Zero
                        ? took < TimeSpan.FromSeconds(1)
                        : took >= grace && log.Has(LogLevel.Warning, "ran out", "still subscribed here: 1 ",
                            health.Correlation.ToString()),
                    grace == TimeSpan.Zero
                        ? "no grace: told, not waited for"
                        : $"the grace ran out: stopped anyway, those left logged ({took.TotalMilliseconds:0} ms)");
            }
        }
    }

    public static Task ClientRouterHealth()
    {
        var time = new ManualTime();
        var registry = new OutletRegistry();
        var outlet = NewOutlet(registry);
        var owner = new object();
        outlet.Attach(owner);
        var client = new TaplineClient(new LoopConnection(outlet), new TaplineClientOptions { Time = time });
        var check = client.RouterHealthCheck();

        HealthCheckResult Check()
        {
            return check.CheckHealthAsync(new HealthCheckContext()).GetAwaiter().GetResult();
        }

        Assert(Check().Status == HealthStatus.Unhealthy && client.Router.Correlation == Guid.Empty,
            "not bound yet: out of reach");

        var seen = new List<RouterStatus>();
        client.RouterStatusChanged += seen.Add;
        client.Bind(outlet.Id, outlet.Nonce);
        Pump(outlet, owner, client);
        Assert(
            client.Router == new RouterStatus(RouterState.Serving, registry.Health.Correlation, null, null) &&
            seen.Count == 1 && Check().Status == HealthStatus.Healthy,
            $"serving: healthy, the router's correlation id ({client.Router})");
        outlet.Heartbeat();
        Pump(outlet, owner, client);
        Assert(seen.Count == 1, "a heartbeat, no change: nothing told");

        time.Advance(TimeSpan.FromSeconds(4));
        var late = Check();
        Assert(
            late.Status == HealthStatus.Degraded && late.Description!.Contains("late") &&
            (long)late.Data["quietMs"] == 4000, $"heartbeats late: degraded ({late.Description})");
        time.Advance(TimeSpan.FromSeconds(7));
        Assert(Check().Status == HealthStatus.Unhealthy, "silent: unhealthy");
        outlet.Heartbeat();
        Pump(outlet, owner, client);
        Assert(Check().Status == HealthStatus.Healthy, "heard again: healthy");

        var degraded = registry.SetHealth(RouterState.Degraded, "db: Unhealthy");
        Pump(outlet, owner, client);
        var result = Check();
        Assert(client.Router is { State: RouterState.Degraded, Reason: "db: Unhealthy" } &&
               client.Router.Correlation == degraded.Correlation && seen.Count == 2
               && result.Status == HealthStatus.Degraded && (Guid)result.Data["correlation"] == degraded.Correlation &&
               (string)result.Data["reason"] == "db: Unhealthy",
            $"degraded: told once, why, the router's correlation id in the check's data ({result.Description})");

        registry.SetHealth(RouterState.Draining, grace: TimeSpan.FromSeconds(10));
        Pump(outlet, owner, client);
        var drainsBy = client.Router.DrainsBy;
        Assert(client.Router.State == RouterState.Draining && drainsBy is { } by &&
               Math.Abs((by - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10)).TotalSeconds) < 2
               && Check() is { Status: HealthStatus.Degraded } draining && draining.Data.ContainsKey("drainsBy") &&
               seen.Count == 3, $"draining: gone by {drainsBy:O}, by this client's clock");

        client.Disconnected();
        Assert(Check() is { Status: HealthStatus.Unhealthy, Description: var d } && d!.Contains("out of reach"),
            "the connection gone: unhealthy");
        return Task.CompletedTask;
    }

    /// <summary>Log lines, as written: for what a test needs to find in them (a correlation id).</summary>
    private sealed class ListLogger : ILogger, ILoggerProvider
    {
        public readonly List<(LogLevel Level, string Message)> Lines = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add((logLevel, formatter(state, exception)));
            }
        }

        public ILogger CreateLogger(string categoryName)
        {
            return this;
        }

        public void Dispose()
        {
        }

        public bool Has(LogLevel level, params string[] parts)
        {
            lock (Lines)
            {
                return Lines.Any(l => l.Level == level && parts.All(l.Message.Contains));
            }
        }
    }
}

internal static partial class T
{
    private static (Publisher Publisher, PartitionedProvider Provider, Dictionary<string, LastValueSource> Topics)
        InPartitions(OutletRegistry registry, string service = "svc", string at = "parts")
    {
        var topics = new[] { "eu/fx", "eu/rates", "us" }.ToDictionary(p => p, _ => new LastValueSource());
        foreach (var t in topics.Values) t.Publish([1]);
        var publisher = new Publisher(registry)
            { ProviderHeartbeatInterval = TimeSpan.Zero, ProviderRecoverHold = TimeSpan.FromMilliseconds(150) };
        var provider = new PartitionedProvider(ProviderInstance.Start(at, At(at)), topics);
        publisher.Provide(service, At(at), provider);
        return (publisher, provider, topics);
    }

    public static async Task Partitions()
    {
        var registry = new OutletRegistry();
        var (publisher, provider, topics) = InPartitions(registry);
        var outlet = NewOutlet(registry);
        var fx = (await publisher.SubscribeAsync(outlet, "svc", "eu/fx"))!.Binding;
        var rates = (await publisher.SubscribeAsync(outlet, "svc", "eu/rates"))!.Binding;
        var us = (await publisher.SubscribeAsync(outlet, "svc", "us"))!.Binding;
        var segments = fx.Partition!.Split('/');
        Assert(
            fx.Service == "svc" && segments.Length == 2 &&
            segments.All(s => s.Length == 16 && s.All(char.IsAsciiHexDigitLower)) &&
            fx.Partition == publisher.PartitionId("svc", "eu/fx"),
            $"a binding in a partition: its service by name, the partition as ids — 16 hex digits a segment ({fx.Service}, {fx.Partition})");
        Assert(
            rates.Partition!.StartsWith(segments[0] + "/", StringComparison.Ordinal) &&
            rates.Partition != fx.Partition && publisher.PartitionId("svc", "eu") == segments[0],
            "chained: a partition's ids begin with its parent's");
        Assert(
            us.Partition!.Length == 16 && !fx.Partition.Contains(us.Partition) && fx.NodeId == us.NodeId &&
            fx.NodeId is { Version: 7 },
            $"another partition, other ids; one node, its id a UUIDv7 ({fx.NodeId})");
        Assert(
            publisher.PartitionId("other", "eu") != segments[0] &&
            publisher.PartitionId("svc", "eu") == publisher.PartitionId("svc", "eu"),
            "the same partition, the same id, through the run; another service's of the same name, another");
        Assert(new Publisher(new OutletRegistry()).PartitionId("svc", "eu/fx") != fx.Partition,
            "another router (or this one restarted): other ids — its key drawn at random as it starts");
        Assert(
            outlet.DescribeTaps().First(t => t.Id == fx.TapId) is { ServicePath: "svc/eu/fx" } view &&
            view.PartitionId == fx.Partition, "the viewer: its partition by name, and the ids its client sees");

        foreach (var bad in new[] { "", "eu//fx", "eu/", "/eu", "e\nu", new string('x', 256) })
        {
            var request = new LastValueSource();
            try
            {
                var answered = new PartitionedProvider(ProviderInstance.Start("bad", At("bad")),
                    new Dictionary<string, LastValueSource> { [bad] = request });
                publisher.Provide("bad" + Math.Abs(bad.GetHashCode()), At("bad" + Math.Abs(bad.GetHashCode())),
                    answered);
                answered.Instance = ProviderInstance.Start("bad", At("bad" + Math.Abs(bad.GetHashCode())));
                await publisher.SubscribeAsync(NewOutlet(registry), "bad" + Math.Abs(bad.GetHashCode()), bad);
                Assert(false, $"a malformed partition accepted: '{bad}'");
            }
            catch (ArgumentException)
            {
            }
        }

        try
        {
            publisher.Provide("a/b", At("slash"), provider);
            Assert(false, "a service's name with '/' registered");
        }
        catch (ArgumentException)
        {
        }

        var reuse = new PartitionedProvider(ProviderInstance.Start("reuse", At("reuse")),
            new Dictionary<string, LastValueSource> { ["eu"] = topics["us"], ["none"] = topics["us"] });
        publisher.Provide("svc2", At("reuse"), reuse);
        try
        {
            await publisher.SubscribeAsync(NewOutlet(registry), "svc2", "eu");
            Assert(false, "a topic in two partitions");
        }
        catch (InvalidOperationException e)
        {
            Assert(e.Message.Contains("svc/us"), e.Message);
        }

        var shared = new LastValueSource();
        var outside = new TestProvider(shared);
        Provide(publisher, "plain-a", "plain", outside);
        publisher.Provide("plain-b", At("plain"), outside);
        Assert(
            await publisher.SubscribeAsync(NewOutlet(registry), "plain-a") is not null &&
            await publisher.SubscribeAsync(NewOutlet(registry), "plain-b") is not null,
            "a topic in no partition: under several services");
    }

    public static async Task ScopedHealth()
    {
        var registry = new OutletRegistry();
        var (publisher, provider, topics) = InPartitions(registry);
        var changes = new List<ServiceHealthChange>();
        publisher.ServiceHealthChanged += c =>
        {
            lock (changes)
            {
                changes.Add(c);
            }
        };
        var outlet = NewOutlet(registry);
        var owner = new object();
        outlet.Attach(owner);
        var c = new RefClient();
        var taps = new Dictionary<string, Tap>();
        foreach (var p in topics.Keys) (taps[p] = (await publisher.SubscribeAsync(outlet, "svc", p))!).Activate();
        var other = NewOutlet(registry); // a client in "us" alone: told nothing of "eu"
        var otherOwner = new object();
        other.Attach(otherOwner);
        var oc = new RefClient();
        (await publisher.SubscribeAsync(other, "svc", "us"))!.Activate();
        DrainFrames(outlet, owner, c);
        DrainFrames(other, otherOwner, oc);

        bool Stale(string p)
        {
            return outlet.DescribeTaps().First(t => t.Id == taps[p].Id).Stale;
        }

        void Beat()
        {
            publisher.HeartbeatProviders();
            DrainFrames(outlet, owner, c);
            DrainFrames(other, otherOwner, oc);
        }

        var eu = "svc/" + publisher.PartitionId("svc", "eu");

        provider.Degraded = ["svc/eu"];
        Beat();
        var degraded = changes.Single();
        Assert(c.ProviderStates.SequenceEqual([(taps["us"].Binding.NodeId!.Value, "degraded")]) &&
               c.ProviderScopes.SequenceEqual([eu])
               && c.ServiceCorrelations.SequenceEqual([degraded.Correlation]) && degraded is
                   { Event: ServiceHealthEvent.Degraded, Path: "svc/eu", Clients: 1 } && degraded.Scope == eu
               && degraded.Correlation.Version == 7 && oc.ProviderStates.Count == 0,
            $"a partition degraded: its clients told, the scope as they see it, the change's id — logged with it ({degraded})");
        Assert(Stale("eu/fx") && Stale("eu/rates") && !Stale("us"),
            "the routes it covers held, shown their stale data; the rest stream on");
        topics["us"].Publish([2]);
        topics["eu/fx"].Publish([2]);
        DrainFrames(outlet, owner, c);
        Assert(c.Values[taps["us"].Id][0] == 2 && c.Values[taps["eu/fx"].Id][0] != 2, "us streams; eu/fx held");
        Beat();
        Assert(changes.Count == 1, "reported again: no change");

        var joined = NewOutlet(registry); // subscribing into it now: told at once
        var joinedOwner = new object();
        joined.Attach(joinedOwner);
        var jc = new RefClient();
        (await publisher.SubscribeAsync(joined, "svc", "eu/fx"))!.Activate();
        DrainFrames(joined, joinedOwner, jc);
        Assert(jc.ProviderScopes.SequenceEqual([eu]) && jc.ServiceCorrelations.SequenceEqual([degraded.Correlation]),
            "a client arriving: told the scope degraded, under the change's id");

        provider.Degraded = null;
        Beat();
        var recovered = changes[^1];
        Assert(recovered is { Event: ServiceHealthEvent.Recovered, Path: "svc/eu" } &&
               recovered.Correlation != degraded.Correlation && !Stale("eu/fx") && !Stale("eu/rates")
               && c.ProviderStates[^1].State == "serving" && c.ProviderScopes[^1] == eu &&
               c.Values[taps["eu/fx"].Id][0] == 2,
            "no longer reported: recovered at once (its first degradation), told under its own id; the held streams go on");

        provider.Degraded = ["svc/eu"]; // flapping: the next recovery held
        Beat();
        provider.Degraded = null;
        Beat();
        Assert(Stale("eu/fx") && changes[^1].Event == ServiceHealthEvent.Degraded,
            "degraded again within the window: healthy, but held");
        await Task.Delay(200);
        Beat();
        Assert(!Stale("eu/fx") && changes[^1].Event == ServiceHealthEvent.Recovered,
            "healthy for the hold (150 ms): recovered");
        provider.Degraded = ["svc/eu"];
        Beat();
        provider.Degraded = null;
        Beat();
        await Task.Delay(200);
        Beat();
        Assert(Stale("eu/fx"), "and again: the hold doubled (300 ms)");
        await Task.Delay(150);
        Beat();
        Assert(!Stale("eu/fx"), "…then recovered");

        provider.Degraded = ["svc/eu/rates"]; // a partition degraded, then the node too
        Beat();
        provider.Status = ProviderStatus.NotServing;
        Beat();
        Assert(Stale("eu/fx") && Stale("eu/rates") && Stale("us"), "the node degraded: every route held");
        provider.Status = ProviderStatus.Serving;
        Beat();
        await Task.Delay(700); // (its recoveries held: it has flapped — the node's own first, at once)
        Beat();
        Assert(!Stale("eu/fx") && Stale("eu/rates") && !Stale("us"),
            "the node recovered: the routes degraded by nothing else go on; eu/rates still held");
        Assert(c.ProviderScopes.Count(s => s is null) == 2 && changes.Count(ch => ch.Path is null) == 2,
            "the node's own changes: no scope");
    }

    public static async Task ClientServiceHealth()
    {
        var registry = new OutletRegistry();
        var (publisher, provider, topics) = InPartitions(registry);
        var outlet = NewOutlet(registry);
        var owner = new object();
        outlet.Attach(owner);
        var client = new TaplineClient(new LoopConnection(outlet));
        client.Bind(outlet.Id, outlet.Nonce);
        var check = client.ServiceHealthCheck("svc");

        HealthCheckResult Check()
        {
            return check.CheckHealthAsync(new HealthCheckContext()).GetAwaiter().GetResult();
        }

        var changes = new List<ProviderChange>();
        client.ProviderStateChanged += changes.Add;
        var subs = new Dictionary<string, Subscription>();
        foreach (var p in topics.Keys)
            subs[p] = client.Subscribe((await publisher.SubscribeAsync(outlet, "svc", p))!.Binding,
                new SubscriptionHandler());
        Pump(outlet, owner, client);
        var healthy = Check();
        Assert(healthy.Status == HealthStatus.Healthy && (int)healthy.Data["subscriptions"] == 3,
            $"streaming: healthy ({healthy.Description})");

        Guid? told = null;
        publisher.ServiceHealthChanged += c => told = c.Correlation;
        provider.Degraded = ["svc/eu"];
        publisher.HeartbeatProviders();
        Pump(outlet, owner, client);
        var degraded = Check();
        var eu = "svc/" + publisher.PartitionId("svc", "eu");
        Assert(
            subs["eu/fx"].Provider == ProviderState.Degraded && subs["eu/rates"].Provider == ProviderState.Degraded &&
            subs["us"].Provider == ProviderState.Serving,
            "a partition degraded: the subscriptions it covers degraded, the rest serving");
        Assert(
            changes is [{ State: ProviderState.Degraded, Scope: var scope, Correlation: var id }] && scope == eu &&
            id == told, $"told once: its scope, the router's correlation id ({string.Join(", ", changes)})");
        Assert(degraded.Status == HealthStatus.Degraded && (int)degraded.Data["degraded"] == 2 &&
               ((string[])degraded.Data["degradedScopes"]).SequenceEqual([eu])
               && (Guid)degraded.Data["correlation"] == told,
            $"the service's check: degraded, which scopes, the needle ({degraded.Description})");

        provider.Degraded = null;
        publisher.HeartbeatProviders();
        Pump(outlet, owner, client);
        Assert(
            Check().Status == HealthStatus.Healthy && subs["eu/fx"].Provider == ProviderState.Serving &&
            changes[^1] is { State: ProviderState.Serving } && changes[^1].Scope == eu,
            "recovered: healthy again");

        Guid? down = null;
        client.ServiceDown += (node, correlation) => down = correlation;
        publisher.Withdraw(At("parts"));
        Pump(outlet, owner, client);
        var gone = Check();
        Assert(
            gone.Status == HealthStatus.Unhealthy && (bool)gone.Data["waitingForUp"] && down == told &&
            subs.Values.All(s => s.IsEnded),
            $"its node gone: unhealthy — waiting for it to be up, told with the change's id ({gone.Description})");
        string? up = null;
        client.ServiceUp += (service, correlation) => up = service;
        publisher.ServiceUp = new ServiceUpPolicy();
        var back = new PartitionedProvider(ProviderInstance.Start("parts-2", At("parts-2")),
            topics.ToDictionary(kv => kv.Key, _ => new LastValueSource()));
        publisher.Provide("svc", At("parts-2"), back);
        publisher.HeartbeatProviders();
        publisher.HeartbeatProviders();
        Pump(outlet, owner, client);
        Assert(up == "svc" && Check().Status == HealthStatus.Healthy, "up again: no longer waiting");
        client.Disconnected();
        Assert(Check() is { Status: HealthStatus.Unhealthy, Description: var d } && d!.Contains("out of reach"),
            "the router out of reach: unhealthy");
    }

    /// <summary>
    ///     A provider's health as a host paces it — a check at most once per <c>ProviderHeartbeatInterval</c>, degraded past
    ///     twice that, as by default (1 s, 2 s; scaled here) — its sweeps a timer's, a little early or late: a sweep before
    ///     the
    ///     interval sends no check, the next sends it. An instance answering every check at once is never late, however the
    ///     sweeps fall: late is a check it was sent going unanswered past <c>ProviderDegradedAfter</c> — one it never answers,
    ///     or checks failing — not the router's own pacing.
    /// </summary>
    public static async Task ProviderHealthPacing()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry)
        {
            ProviderHeartbeatInterval = TimeSpan.FromMilliseconds(200),
            ProviderDegradedAfter = TimeSpan.FromMilliseconds(400), ProviderTimeout = TimeSpan.FromSeconds(5)
        };
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var c = new RefClient();
        var station = new TestProvider(new LastValueSource());
        Provide(publisher, "paced", "paced-station", station);
        var tap = (await publisher.SubscribeAsync(s, "paced"))!;
        tap.Activate();
        var node = tap.Binding.NodeId!.Value;

        static void Until(Stopwatch clock, int ms)
        {
            while (clock.ElapsedMilliseconds < ms) Thread.SpinWait(64);
        }

        // A sweep sends a check, answered at once; the next comes a little early (before the interval: no check); the next
        // a little past twice the interval since that answer — and sends the check. Three times over.
        for (var round = 0; round < 3; round++)
        {
            var clock = Stopwatch.StartNew();
            publisher.HeartbeatProviders();
            Until(clock, 150);
            publisher.HeartbeatProviders();
            Until(clock, 450);
            publisher.HeartbeatProviders();
            DrainFrames(s, owner, c);
        }

        Assert(c.ProviderStates.Count == 0,
            $"answering every check at once: never late, however the sweeps fall (told {string.Join(", ", c.ProviderStates)})");

        // A check it never answers: late once it has gone unanswered past ProviderDegradedAfter, not before.
        var never = new TaskCompletionSource<HealthReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        station.Health = (_, _) => new ValueTask<HealthReport>(never.Task);
        var asked = Stopwatch.StartNew();
        while (c.ProviderStates.Count == 0 && asked.ElapsedMilliseconds < 10_000)
        {
            publisher.HeartbeatProviders();
            Thread.Sleep(20);
            DrainFrames(s, owner, c);
        }

        var lateAfter = asked.ElapsedMilliseconds;
        Assert(c.ProviderStates.SequenceEqual([(node, "degraded")]) && lateAfter >= 400,
            $"a check unanswered: late, past ProviderDegradedAfter ({lateAfter} ms)");
        never.SetResult(new HealthReport(station.Instance!, ProviderStatus.Serving));
        await Eventually(() => c.ProviderStates.Count >= 2, () =>
        {
            publisher.HeartbeatProviders();
            DrainFrames(s, owner, c);
        }, every: 20);

        Assert(c.ProviderStates.Count == 2 && c.ProviderStates[1] == (node, "serving"),
            "answered: recovered (its first degradation: at once)");

        // Checks failing at once: unanswered too — late past ProviderDegradedAfter from the first that failed.
        station.Health = (_, _) => ValueTask.FromException<HealthReport>(new InvalidOperationException("broken"));
        asked.Restart();
        while (c.ProviderStates.Count == 2 && asked.ElapsedMilliseconds < 10_000)
        {
            publisher.HeartbeatProviders();
            Thread.Sleep(20);
            DrainFrames(s, owner, c);
        }

        lateAfter = asked.ElapsedMilliseconds;
        Assert(c.ProviderStates.Count == 3 && c.ProviderStates[2] == (node, "degraded") && lateAfter >= 400,
            $"checks failing: late, past ProviderDegradedAfter ({lateAfter} ms)");
        Assert(!tap.IsClosed, "late, not gone: the route held");
    }

    /// <summary>
    ///     A provider of one service in partitions: a topic for each, the one a subscription's parameters name; its
    ///     health reports name the paths it says are degraded.
    /// </summary>
    private sealed class PartitionedProvider(ProviderInstance instance, Dictionary<string, LastValueSource> topics)
        : IDataProvider
    {
        public string[]? Degraded; // the service paths its next reports name
        public ProviderInstance Instance = instance;
        public ProviderStatus Status = ProviderStatus.Serving;

        public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
        {
            return new ValueTask<HealthReport>(new HealthReport(Instance, Status, DegradedPaths: Degraded));
        }

        public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
        {
            var partition = request.Parameters!;
            request.Accept(topics[partition], Instance, partition: partition,
                errorPolicy: new ErrorPolicy("stale"u8.ToArray()));
            return default;
        }
    }
}