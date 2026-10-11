using BlazorRealtime;
using BlazorRealtime.Sample;
using BlazorRealtime.Sample.Components;
using BlazorRealtime.Sample.Waveform;
using Tapline;
using Tapline.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var ops = OpsEndpoints
    .Configure(builder); // the ops endpoints' own port, 5899, on localhost — or off, or shared (see OpsEndpoints)
Listening.Configure(builder,
    ops.Urls); // http://localhost:5898 unless told where, and the ops port: no authentication (see Listening)
builder.WebHost
    .UseStaticWebAssets(); // blazor.web.js, tapline.js + blazor-realtime.js from the build output in any environment (a no-op once published)
builder.Configuration["EnableStaticAssetsDevelopmentCaching"] =
    "true"; // keep @Assets' fingerprinted URLs immutable when run from build output (published apps already are)
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o =>
    o.DisconnectedCircuitRetentionPeriod =
        TimeSpan.FromSeconds(30)); // a dropped client may come back within 30 s (not Blazor's 3 min): its subscriptions held till then, resumed exactly — after, it needs a new circuit
builder.Services.AddTapline(o => // the router
{
    o.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
    o.Ops.Port = ops.Port; // the viewer and the operator endpoints answer there alone (null: shared, or off)
});
builder.Services.AddBlazorRealtime(o => // its transport, the circuits: after AddInteractiveServerComponents
{
    // Streaming uncapped: each circuit gets what its path carries (flow control keeps the queue short). To cap it:
    // o.StreamingBytesPerSecond = 64 * 1024, or per user Outlet.SetStreamingRate(...).
    o.StreamingRateQueryParameter =
        "bwlimit"; // ?bwlimit=64 (KiB/s, as rsync --bwlimit) | 0.5 | 500kb: a slower stream for this page
    o.CircuitSendBufferBytes =
        64 * 1024; // opt-in: a short kernel backlog per circuit (≥ bandwidth × RTT of your clients)
});
builder.Services
    .AddSingleton<
        WaveformDeployment>(); // the waveform: Resonance + Unknown Pleasures, streamed (see Waveform/) — blue/green stations
builder.Services.AddHostedService(sp => sp.GetRequiredService<WaveformDeployment>());
builder.Services.AddSingleton(new GcStats()); // the process's garbage collection, in the waveform's stats (i)
builder.Services
    .AddSingleton<
        TaglineFont>(); // the waveform's caption in a font of your own (WAVEFORM_TAGLINE_FONT: not bundled; see TaglineFont)

var app = builder.Build();
Listening.WarnWhenStarted(app); // "This sample has no authentication. Listening on …"
app.Logger.LogInformation("Router {Router}", app.Services.GetRequiredService<Publisher>().Router);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapBlazorRealtime(); // out-of-band fetches: GET /_tapline/blob/...
TaglineFont.Map(app); // GET /tagline-font/{fingerprint}: that font, to this machine only
OpsEndpoints.Map(app, ops); // the viewer, restart, deploy: on http://localhost:5899 by default (see OpsEndpoints)
app.Run();