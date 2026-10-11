using BlazorRealtime;
using BlazingOrderBooks.Sample;
using BlazingOrderBooks.Sample.Components;
using Tapline;
using Tapline.AspNetCore;

if (FeedBench.TryRun(args)) return; // --bench-feed [seconds]: the feed handler's path alone, timed, no web host

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
    o.FaultInjection = new FaultInjectionOptions
        { Path = "/test" }; // circuits opened on /test (the simulated book): an outage every 15 s for 2 s, all together; elsewhere never
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
builder.Services.AddSingleton<BookFeed>(); // the order books' ingests: the feed posts, the router folds (WebAssembly)
builder.Services.Configure<MarketFeedOptions>(builder.Configuration.GetSection("Market")); // depth interval, spin…
builder.Services.AddSingleton<FeedStats>(); // what the feed handler measures, for the page
builder.Services
    .AddHostedService<MarketFeedHandler>(); // the "market" service's data provider: the books and their mark
builder.Services.Configure<BlazingOrderBooks.Sample.Binance.BinanceOptions>(builder.Configuration.GetSection("Binance"));
builder.Services.AddHttpClient(); // (the Binance feed's REST calls, at startup)
builder.Services.AddSingleton<BlazingOrderBooks.Sample.Binance.BinanceUniverse>(); // its instruments, read at startup
builder.Services.AddSingleton<BlazingOrderBooks.Sample.Binance.BinanceFeed>(); // the "binance" service's provider: /binance
builder.Services.AddHostedService(sp => sp.GetRequiredService<BlazingOrderBooks.Sample.Binance.BinanceFeed>());

var app = builder.Build();
Listening.WarnWhenStarted(app); // "This sample has no authentication. Listening on …"
app.Logger.LogInformation("Router {Router}", app.Services.GetRequiredService<Publisher>().Router);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapBlazorRealtime(); // out-of-band fetches: GET /_tapline/blob/...
OpsEndpoints.Map(app, ops); // the viewer, restart, deploy: on http://localhost:5899 by default (see OpsEndpoints)
app.Run();