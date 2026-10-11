using System.Diagnostics;
using BlazorRealtime;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using PivotTable.Sample;
using PivotTable.Sample.Components;
using Tapline.AspNetCore;
using Tapline.MagicOnion.Server;

var builder = WebApplication.CreateBuilder(args);
// The app on localhost:5900, its ops endpoints (the router's viewer) on a port of their own, 5901, and the stream for
// clients of Tapline.MagicOnion (gRPC: HTTP/2 alone, cleartext) on 5902 — this machine only: no authentication. --urls
// names the app's, --OpsPort the ops port, --StreamPort the stream's (a second instance beside the first: all three). A
// port 0 is one the system picks. --SharedOps "reason": the ops endpoints on every port instead, for that reason
// (Development only, said on every viewer page). --ParentPid: spawned (PivotTable.Headless does) — where it listens is
// printed once it is (PIVOT-SAMPLE app=… stream=…), and it stops when that process does.
const string App = "http://localhost:5900";
int OpsPort = builder.Configuration.GetValue<int?>("OpsPort") ?? 5901,
    StreamPort = builder.Configuration.GetValue<int?>("StreamPort") ?? 5902;
var sharedOps = builder.Configuration["SharedOps"] is { Length: > 0 } reason ? reason : null;

static string Url(int port)
{
    return port == 0 ? "http://127.0.0.1:0" : $"http://localhost:{port}";
} // (a port the system picks: one address — localhost is two)

var urls = builder.Configuration["urls"] is { Length: > 0 } told
    ? told.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : [App];
for (var i = 0; i < urls.Length; i++) builder.Configuration[$"Kestrel:Endpoints:App{i}:Url"] = urls[i];
if (sharedOps is null) builder.Configuration["Kestrel:Endpoints:Ops:Url"] = Url(OpsPort);
builder.Configuration["Kestrel:Endpoints:Stream:Url"] = Url(StreamPort);
builder.Configuration["Kestrel:Endpoints:Stream:Protocols"] = "Http2";
ListenOptions? appListen = null, streamListen = null; // (where they listen once bound: a port the system picked, say)
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    kestrel.Configure(context.Configuration.GetSection("Kestrel"))
        .Endpoint("App0", app0 => appListen = app0.ListenOptions)
        .Endpoint("Stream",
            stream =>
                (streamListen = stream.ListenOptions).CapSocketBacklog(
                    notSentLowWatermarkBytes: 16 * 1024)); // Linux: a short backlog in front of the wire
    kestrel.Limits.Http2.KeepAlivePingDelay =
        TimeSpan.FromSeconds(10); // a stream whose client has gone: noticed in seconds
    kestrel.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // (requests: quiet)
builder.Logging.AddFilter("Grpc", LogLevel.Warning);
builder.Logging.AddFilter("MagicOnion", LogLevel.Warning);
builder.WebHost
    .UseStaticWebAssets(); // blazor.web.js, tapline.js + blazor-realtime.js from the build output in any environment
builder.Configuration["EnableStaticAssetsDevelopmentCaching"] = "true";
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddTapline(o => // the router: its ops endpoints (the viewer, the drain) on their own port;
{
    // stopping, its stream's clients given 5 s to move to another router
    if (sharedOps is not null) o.Ops.SharedPortReason = sharedOps; // (a non-production demo: on every port)
    else o.Ops.Port = OpsPort;
    o.Health.DrainGrace = TimeSpan.FromSeconds(5);
});
builder.Services.AddBlazorRealtime(o => // a transport, the circuits: after AddInteractiveServerComponents
    o.StreamingRateQueryParameter =
        "bwlimit"); // ?bwlimit=1 (KiB/s, as rsync --bwlimit) | 0.5 | 500kb: a slower stream for this page
builder.Services.AddMagicOnion(); // another, the stream: gRPC on its own port…
builder.Services.AddTaplineMagicOnion(); // …its sessions (an outlet each, held 30 s past its call for the next)
builder.Services
    .AddSingleton<PivotProvider>(); // the "pivot" service: the trades, ChalkQL's pivot of them, posted as snapshots
builder.Services.AddHostedService(sp => sp.GetRequiredService<PivotProvider>());

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapBlazorRealtime();
app.MapTaplineMagicOnion(); // the stream, for clients of Tapline.MagicOnion (samples/PivotTable.Headless)…
app.MapMagicOnionService(typeof(PivotService)); // …and their step 1: a pivot by a page's address
app.MapTaplineViewer(); // GET /ops/realtime, on the ops port alone
app.MapTaplineDrain(); // POST /ops/tapline/drain?grace=10s: its clients moved before it stops
if (builder.Configuration.GetValue<int?>("ParentPid") is
    { } parent) // spawned: where it listens, for its parent to read; gone with it
{
    app.Lifetime.ApplicationStarted.Register(() =>
        Console.WriteLine($"PIVOT-SAMPLE app=http://{appListen!.IPEndPoint} stream=http://{streamListen!.IPEndPoint}"));
    _ = StopWithParentAsync(parent, app.Lifetime);
}

app.Run();

// Stops the app once the process that spawned it has gone (checked each second): never left behind, holding its ports.
static async Task StopWithParentAsync(int pid, IHostApplicationLifetime lifetime)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    while (await timer.WaitForNextTickAsync(lifetime.ApplicationStopping))
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) continue;
        }
        catch (ArgumentException)
        {
        } // (no such process: gone)

        lifetime.StopApplication();
        return;
    }
}