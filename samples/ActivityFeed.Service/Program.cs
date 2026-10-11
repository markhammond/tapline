using ActivityFeed.Contracts;
using ActivityFeed.Service;
using Tapline;
using Tapline.MagicOnion.Providers;

// ActivityFeed.Service — a medical practice's activity feed, as a data provider in a process of its own: routers reach
// it over MagicOnion (gRPC: HTTP/2, cleartext) on 5960 — its topics on a Tapline stream, the rest of the provider
// protocol on its control stream, and the practice's commands — and its health on 5961 (/livez, /readyz), with how its
// feeds' views are kept (/views), the dispatch log (/dispatches), the access log (/access), the oracle (/oracle) and the
// provider's view (/view, which the router's viewer posts to), the write log as it has it (/log), and a drain (POST
// /drain). This machine only: its feeds require a sign-in, by passthrough (each client says who it is, taken as given),
// and the app vouches for who its commands act as. --GrpcPort and --HttpPort name the
// ports (a second instance beside the first: both); --Practice:Log names the write log's SQLite file (every instance given
// the same one replays the same practice: one writes, the others follow, hearing each turn by the writer's signal through
// the app's stream, --Practice:Stream, http://127.0.0.1:5952/ by default, and failing that by watching the file, every
// --Practice:LogPollMs, 25; none named, the log's in memory and the practice this process's alone); --Practice:AccessLog
// names the access log's own SQLite file (by default beside the write log's: practice.access.db; with no write log's file,
// one of this process's own, removed when it stops); --Practice:Name names
// the instance (practice-blue, say); --Spawned true: spawned (the app does, and ActivityFeed.Headless) — where it
// listens is printed once it does (ACTIVITY-SERVICE grpc=… http=…), and it stops once its standard input ends (the
// spawner gone). The data is synthetic.
var builder = WebApplication.CreateBuilder(args);
int grpcPort = builder.Configuration.GetValue<int?>("GrpcPort") ?? 5960,
    httpPort = builder.Configuration.GetValue<int?>("HttpPort") ?? 5961;
var endpoint = new ProviderEndpoint(new Uri($"http://127.0.0.1:{grpcPort}")); // (the address routers reach it at)
builder.Configuration["Kestrel:Endpoints:Grpc:Url"] = endpoint.Address.ToString();
builder.Configuration["Kestrel:Endpoints:Grpc:Protocols"] = "Http2";
builder.Configuration["Kestrel:Endpoints:Http:Url"] = $"http://127.0.0.1:{httpPort}";
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10); // a router gone: noticed in seconds
    kestrel.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Grpc", LogLevel.Warning);
builder.Logging.AddFilter("MagicOnion", LogLevel.Warning);
builder.Services.AddMagicOnion();
// Stopping (a drain over, say): what's still open is the routers' links, its clients moved: five seconds, not thirty.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton(endpoint);
// The write log: SQLite, a file every instance of the service on this machine shares (Practice:Log), or memory, for one alone.
builder.Services.AddSingleton<IPracticeLog>(_ => builder.Configuration["Practice:Log"] is { Length: > 0 } path
    ? new SqlitePracticeLog(path, TimeSpan.FromMilliseconds(Math.Clamp(builder.Configuration.GetValue<double?>("Practice:LogPollMs") ?? 25, 1, 1000)))
    : new MemoryPracticeLog());
// The access log at the posts: where intents and outcomes are kept, for an access log that can't be worked out by replay.
// The practice's is worked out from the write log instead, so its own keeps nothing (ReplayedAccessLog).
builder.Services.AddSingleton<IAccessLog, ReplayedAccessLog>();
// The access log's own database, written off the loop and read by ChalkQL as the audit source: Practice:AccessLog, or beside
// the write log's file when instances share one (whoever starts them removes both), or a file of this process's own.
builder.Services.AddSingleton(sp => new AccessLogSink(builder.Configuration["Practice:AccessLog"] is { Length: > 0 } access
    ? access
    : builder.Configuration["Practice:Log"] is { Length: > 0 } log ? AccessLogSink.Beside(log) : null,
    sp.GetRequiredService<ILogger<AccessLogSink>>()));
builder.Services.AddSingleton<PracticeProvider>(); // the "practice" service: ChalkQL's records, the feeds made of them
builder.Services.AddHostedService(sp => sp.GetRequiredService<PracticeProvider>());
builder.Services.AddTaplineProviderLink(endpoint, sp => sp.GetRequiredService<PracticeProvider>());

var app = builder.Build();
app.MapTaplineProviderLink(); // the topics' stream, and the control stream (the provider protocol)
app.MapMagicOnionService(typeof(CommandService)); // the commands, as the app forwards them
// Health over HTTP, for a service registry (IProviderHealth's contract): live whenever it answers, with its report; ready
// once it serves (ChalkQL up, the history primed), 503 with the report until then.
app.MapGet("/livez", (PracticeProvider practice) => Results.Json(practice.Report()));
app.MapGet("/readyz", (PracticeProvider practice) => practice.Report() is { Serves: true } report
    ? Results.Json(report)
    : Results.Json(practice.Report(), statusCode: StatusCodes.Status503ServiceUnavailable));
// The feeds' views: how each is kept (incremental, validated against a recomputed twin, by default), its updates and
// checks, and any that gave way to a recomputed view, and why.
app.MapGet("/views", (PracticeProvider practice) => Results.Json(practice.Views));
// The write log, as this instance has it: writing, following or draining; who holds the lease; how far it has read and
// run; how far behind the writer it runs (following); and any turn whose writes differed from the writer's.
app.MapGet("/log", (PracticeProvider practice) => Results.Json(practice.Log));
// A drain, for blue/green: the lease resigned (another instance writes from its next look), its clients moved by the
// routers over `over` seconds, each given `grace`; then it stops. On the service's own host, beside its health.
app.MapPost("/drain", async (double? over, double? grace, PracticeProvider practice, CancellationToken ct) =>
{
    await practice.DrainAsync(TimeSpan.FromSeconds(Math.Clamp(over ?? 10, 0, 300)), TimeSpan.FromSeconds(Math.Clamp(grace ?? 5, 0, 60)), ct);
    return Results.Accepted(value: practice.Log);
});
// The provider's view, for the router's viewer (a person's topic's view URL): what the router posts, rendered: the full
// record of the newest activity it holds, as that person may see it, and who was shown it.
app.MapPost("/view", async (HttpRequest request, PracticeProvider practice, CancellationToken ct) =>
{
    using var envelope = await System.Text.Json.JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
    return Results.Content(await practice.ViewAsync(envelope.RootElement, ct), "text/html; charset=utf-8");
});
// Fault injection, for a demo: a site's system falling behind for a while (?site=2&seconds=20; 0, caught up again). In
// Development only, opted into with a reason (--Practice:Faults "…"), and said so as it starts.
var faults = builder.Environment.IsDevelopment() ? builder.Configuration["Practice:Faults"] : null;
app.MapPost("/faults/lag", (int site, double seconds, PracticeProvider practice) =>
    faults is not { Length: > 0 }
        ? Results.Problem("Fault injection is off: Development only, opted into with --Practice:Faults naming why.", statusCode: 403)
        : practice.Lag(site, TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 600))) is { } why
            ? Results.BadRequest(why)
            : Results.Ok(new { site, seconds, practice.Report().DegradedPaths }));
if (faults is { Length: > 0 })
    app.Logger.LogWarning("Fault injection is on (/faults/lag), for a stated reason: {Reason}", faults);
// What's measured, opted into (Practice:Measure): posts and their time, each turn's write and fan-out, and each post's
// lateness by kind of topic; ?reset=true starts it again.
app.MapGet("/measure", async (PracticeProvider practice, bool? reset, CancellationToken ct) =>
    await practice.MeasureAsync(reset ?? false, ct) is { } measured
        ? Results.Json(measured)
        : Results.NotFound("Not measuring: --Practice:Measure true."));
// The oracle: every open topic checked against its statements run whole, by row count and checksum (one that differs is
// sent its whole state again). Development samples one a turn on its own (Practice:OracleSeconds).
app.MapGet("/oracle", async (PracticeProvider practice, CancellationToken ct) => Results.Json(await practice.OracleAsync(ct)));
// The access log: who was shown a record (?activity=N, or ?section=N), as the practice's auditor reads it.
app.MapGet("/access", async (long? activity, long? section, PracticeProvider practice, CancellationToken ct) =>
    activity is null && section is null
        ? Results.BadRequest("Name a record: ?activity=N or ?section=N.")
        : Results.Json(await practice.ShownToAsync(activity is not null ? (byte)0 : (byte)3, activity ?? section!.Value, ct)));
// The dispatch log: every notification, and every entry of it (a device's send or no-op, and what came of it).
app.MapGet("/dispatches", async (PracticeProvider practice, CancellationToken ct) => Results.Json(await practice.DispatchLogAsync(ct)));
if (builder.Configuration.GetValue<bool>("Spawned")) // spawned: where it listens, for its parent; gone with it
{
    app.Lifetime.ApplicationStarted.Register(() =>
        Console.WriteLine($"ACTIVITY-SERVICE grpc={endpoint.Address} http=http://127.0.0.1:{httpPort}/"));
    Spawning.StopWhenParentGoes(app.Lifetime.StopApplication);
}

app.Run();
