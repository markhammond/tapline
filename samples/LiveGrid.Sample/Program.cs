using System.Globalization;
using BlazorRealtime;
using LiveGrid.Sample;
using LiveGrid.Sample.Components;

var builder = WebApplication.CreateBuilder(args);
Listening.Configure(builder); // http://localhost:5970 unless told where: this machine only by default (see Listening)
builder.WebHost
    .UseStaticWebAssets(); // blazor.web.js, tapline.js, blazor-realtime.js and tapline-grid.js from the build output in any environment
builder.Configuration["EnableStaticAssetsDevelopmentCaching"] =
    "true"; // keep @Assets' fingerprinted URLs immutable when run from build output (published apps already are)
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddBlazorRealtime(); // the circuit transport, and the router it serves: after AddInteractiveServerComponents

// The ChalkQL blotter's market, by default: five million orders over five days, then 200 changes a second, with a burst
// ten times as busy for 2 s every 30 s. --orders 50m, --rate 400, --burst-every 0 (no bursts), --burst-for, --burst-factor.
var config = builder.Configuration;
var started = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
var history = new History(Count(config["orders"] ?? "5m"), started);
var house = new House(history, new HouseOptions
{
    Rate = config.GetValue("rate", 200.0),
    BurstEvery = Seconds(config["burst-every"], 30),
    BurstFor = Seconds(config["burst-for"], 2),
    BurstFactor = config.GetValue("burst-factor", 10.0)
}, started);
builder.Services.AddSingleton(house);
builder.Services.AddSingleton(services => new Orders(house, services.GetRequiredService<ILogger<Orders>>()));

var app = builder.Build();
Listening.WarnWhenStarted(app); // "This sample has no authentication. Listening on …"
app.Lifetime.ApplicationStopping.Register(() => house.DisposeAsync().AsTask().Wait());
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapBlazorRealtime();
app.Run();
return;

// "5m", "500k", "200000": a count of orders.
static long Count(string text)
{
    var scale = char.ToLowerInvariant(text[^1]) switch { 'k' => 1_000L, 'm' => 1_000_000L, _ => 1L };
    var digits = scale == 1 ? text : text[..^1];
    return (long)(double.Parse(digits, CultureInfo.InvariantCulture) * scale);
}

// "30s", "2", "0": seconds.
static TimeSpan Seconds(string? text, double fallback)
{
    return TimeSpan.FromSeconds(text is null ? fallback : double.Parse(text.TrimEnd('s'), CultureInfo.InvariantCulture));
}
