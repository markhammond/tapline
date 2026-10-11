using ActivityFeed.Contracts;
using ActivityFeed.Sample;
using ActivityFeed.Sample.Components;
using BlazorRealtime;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Tapline.AspNetCore;
using Tapline.MagicOnion.Server;

// ActivityFeed.Sample — the practice's app and router. Its router reaches the practice service (ActivityFeed.Service) in
// another process, over the provider link (--Practice: its address, default http://127.0.0.1:5960). The pages on
// localhost:5950 (each person's feed, live over the page's circuit), its ops endpoints (the router's viewer) on 5951, and
// the stream for clients of Tapline.MagicOnion (gRPC: HTTP/2 alone, cleartext) on 5952, with the practice's step 1 and
// commands beside it; what a stream or a circuit sends out of band is fetched from the app's port (/_tapline/blob) —
// this machine only: sign-in by passthrough (each client says who it is, taken as given: no proof). --urls names the app's,
// --OpsPort the ops port, --StreamPort the stream's; a port 0 is one the system picks. --SharedOps "reason": the ops endpoints on every port instead, for that reason (Development only, said on every
// viewer page). Run on its own (no --Practice named), it spawns the practice service and the dispatcher
// (PracticeProcesses), and they stop with it. --Spawned true: spawned itself (ActivityFeed.Headless does) — where it
// listens is printed once it is (ACTIVITY-SAMPLE app=… stream=…), and it stops once its standard input ends (the
// spawner gone). --Measure true: its garbage collections heard, at /ops/collections (the headless's --measure).
var builder = WebApplication.CreateBuilder(args);
const string App = "http://localhost:5950";
int opsPort = builder.Configuration.GetValue<int?>("OpsPort") ?? 5951,
    streamPort = builder.Configuration.GetValue<int?>("StreamPort") ?? 5952;
var sharedOps = builder.Configuration["SharedOps"] is { Length: > 0 } reason ? reason : null;

static string Url(int port)
{
    return port == 0 ? "http://127.0.0.1:0" : $"http://localhost:{port}";
} // (a port the system picks: one address — localhost is two)

var urls = builder.Configuration["urls"] is { Length: > 0 } told
    ? told.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : [App];
for (var i = 0; i < urls.Length; i++) builder.Configuration[$"Kestrel:Endpoints:App{i}:Url"] = urls[i];
if (sharedOps is null) builder.Configuration["Kestrel:Endpoints:Ops:Url"] = Url(opsPort);
builder.Configuration["Kestrel:Endpoints:Stream:Url"] = Url(streamPort);
builder.Configuration["Kestrel:Endpoints:Stream:Protocols"] = "Http2";
ListenOptions? appListen = null, streamListen = null; // (where they listen once bound: a port the system picked, say)
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    kestrel.Configure(context.Configuration.GetSection("Kestrel"))
        .Endpoint("App0", app0 => appListen = app0.ListenOptions)
        .Endpoint("Stream", stream => streamListen = stream.ListenOptions);
    kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10); // a stream whose client has gone: noticed in seconds
    kestrel.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Grpc", LogLevel.Warning);
builder.Logging.AddFilter("MagicOnion", LogLevel.Warning);
builder.WebHost.UseStaticWebAssets(); // blazor.web.js, tapline.js + blazor-realtime.js from the build output in any environment
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddTapline(o => // the router: its ops endpoints (the viewer, the drain) on their own port
{
    // Sign-in by the built-in passthrough, in the practice's realm: each client says who it is (a person's id, a
    // dispatcher's name), taken as given — insecure, marked so — and the practice service requires it.
    o.Authentication.Passthrough[PracticeSignIn.Realm] = new PassthroughProfile
        { Enable = Tapline.PassthroughAuthenticator.AcceptAnyCredential };
    if (sharedOps is not null) o.Ops.SharedPortReason = sharedOps; // (a non-production demo: on every port)
    else o.Ops.Port = opsPort;
    o.Health.DrainGrace = TimeSpan.FromSeconds(5);
});
builder.Services.AddBlazorRealtime(); // a transport, the pages' circuits: after AddInteractiveServerComponents
builder.Services.AddMagicOnion(); // another, the stream: gRPC on its own port…
builder.Services.AddTaplineMagicOnion(); // …its sessions (an outlet each, held 30 s past its call for the next)
if (builder.Configuration["Practice"] is null && !builder.Configuration.GetValue<bool>("Spawned"))
    builder.Services.AddHostedService(sp => new PracticeProcesses( // the practice's other processes (stopped after the link)
        sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<IHostEnvironment>(),
        () => (new Uri($"http://{appListen!.IPEndPoint}/"), new Uri($"http://{streamListen!.IPEndPoint}/")),
        sp.GetRequiredService<ILogger<PracticeProcesses>>()));
builder.Services.AddSingleton<Practice>(); // the practice service: its feeds (the link) and its commands
builder.Services.AddHostedService(sp => sp.GetRequiredService<Practice>());

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(); // the board, a person's page
app.MapBlazorRealtime(); // …and GET /_tapline/blob/…: what's sent out of band (a feed's snapshot of more than 16 KB), fetched
app.MapTaplineMagicOnion(); // the stream, for clients of Tapline.MagicOnion (samples/ActivityFeed.Headless)…
app.MapMagicOnionService(typeof(PracticeFeedService)); // …their step 1, by person, and the commands
app.MapTaplineViewer(); // GET /ops/realtime, on the ops port alone
app.MapTaplineDrain(); // POST /ops/tapline/drain?grace=10s: its clients moved before it stops
if (app.Configuration.GetValue<bool>("Measure")) // (measured: its collections, at /ops/collections on the ops port)
{
    var collections = new Collections();
    var measured = app.MapGet("/ops/collections", (bool? reset) => Results.Json(collections.Read(reset ?? false)));
    if (sharedOps is null) measured.RequireHost($"*:{opsPort}");
}
if (builder.Configuration.GetValue<bool>("Spawned")) // spawned: where it listens, for its parent; gone with it
{
    app.Lifetime.ApplicationStarted.Register(() =>
        Console.WriteLine($"ACTIVITY-SAMPLE app=http://{appListen!.IPEndPoint} stream=http://{streamListen!.IPEndPoint}"));
    Spawning.StopWhenParentGoes(app.Lifetime.StopApplication);
}

app.Run();
