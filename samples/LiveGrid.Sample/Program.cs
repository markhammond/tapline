using System.Globalization;
using BlazorRealtime;
using Microsoft.AspNetCore.DataProtection;
using LiveGrid.Sample;
using LiveGrid.Sample.Components;
using LiveGrid.Sample.Domain;
using LiveGrid.Sample.Postgres;
using Tapline.Alerts;
using Tapline.Grid;

var builder = WebApplication.CreateBuilder(args);
Listening.Configure(builder); // http://localhost:5970 unless told where: this machine only by default (see Listening)
builder.WebHost
    .UseStaticWebAssets(); // blazor.web.js, tapline.js, blazor-realtime.js and tapline-grid.js from the build output in any environment
builder.Configuration["EnableStaticAssetsDevelopmentCaching"] =
    "true"; // keep @Assets' fingerprinted URLs immutable when run from build output (published apps already are)
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddBlazorRealtime(); // the circuit transport, and the router it serves: after AddInteractiveServerComponents

// The ChalkQL blotter's market, by default: a million orders over five days, then 200 changes a second, with a burst
// ten times as busy for 2 s every 30 s. --orders 5m, --rate 400, --burst-every 0 (no bursts), --burst-for, --burst-factor.
// Served as the blotter serves it: a database, in the blotter's schema, written behind the house, and the house's
// stream, merged. The database is PostgreSQL's (--store postgres, the default): a server the sample starts, in a
// directory of its own, from the PostgreSQL found installed (--pg-bin <its bin directory> to say which), and deletes
// when it stops; or the one --postgres <a connection string> names, the sample's schema made in it and dropped after.
// --store memory: ChalkQL's tables in process. --store computed: the house's orders read where they stand, sorted and
// filtered from a copy (five million by default; up to 50m).
var config = builder.Configuration;
var kind = (config["store"] ?? "postgres").ToLowerInvariant();
if (kind is not ("postgres" or "memory" or "computed"))
{
    Console.Error.WriteLine($"--store {kind}: postgres (the default), memory or computed");
    return 1;
}

var computed = kind == "computed";
PgServer? server = null;
if (kind == "postgres")
    try
    {
        server = await PgServer.StartAsync(config["postgres"], config["pg-bin"], CancellationToken.None);
    }
    catch (Exception failure) when (failure is InvalidOperationException or Npgsql.NpgsqlException or IOException)
    {
        Console.Error.WriteLine($"The sample's database: {failure.Message}. Or --store memory, for ChalkQL's tables in process.");
        return 1;
    }

var started = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
var history = new History(Count(config["orders"] ?? (computed ? "5m" : "1m")), started);
var ledger = computed ? null : new Ledger(); // (told every version and fill from the first, to stream them and land them)
if (ledger is not null) ledger.Failed = failure => Console.Error.WriteLine($"The ledger's landing failed, to be tried again: {failure}");
var house = new House(history, new HouseOptions
{
    Rate = config.GetValue("rate", 200.0),
    BurstEvery = Seconds(config["burst-every"], 30),
    BurstFor = Seconds(config["burst-for"], 2),
    BurstFactor = config.GetValue("burst-factor", 10.0)
}, started, ledger);
builder.Services.AddSingleton(house);
if (ledger is not null) builder.Services.AddSingleton(ledger); // (the page's toggles: a stall, the streams out of order)

// The keys that shroud the values in a view's link (LinkShroud), kept in a folder so a link opens after a restart: rotated
// every 90 days, an old one kept to open what it shrouded. --link-keys <folder>.
builder.Services.AddDataProtection()
    .SetApplicationName("LiveGrid.Sample")
    .PersistKeysToFileSystem(new DirectoryInfo(config["link-keys"] ??
                                               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveGrid.Sample", "link-keys")));
builder.Services.AddSingleton<LinkShroud>();
StoreOrders? store = null;
try
{
    if (server is not null) store = await StoreOrders.StartPostgresAsync(history, house, ledger!, server, Console.WriteLine);
    else if (ledger is not null) store = await StoreOrders.StartAsync(history, house, ledger, Console.WriteLine);
}
catch when (server is not null)
{
    await server.DisposeAsync(); // (the sample's own server stopped and deleted, a given one's schema dropped)
    throw;
}

// The start's garbage collected now, before the first reads, and the memory it took handed back.
GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);

if (store is not null) builder.Services.AddSingleton<IOrderSource>(store).AddSingleton(store.Fills); // (the fills tab: none computed)
else builder.Services.AddSingleton<IOrderSource>(services => services.GetRequiredService<Orders>());

// The page's tabs, the domain's (Domain/Tabs.cs): the client orders, and their fills unless computed. Outside Domain, the page
// knows the rows by these alone; a text column's values for its choosers are the house's (Orders), whatever the store.
builder.Services.AddSingleton(new Catalog(store is null ? [Tabs.Orders] : Tabs.All));
builder.Services.AddSingleton<IColumnValues>(services => services.GetRequiredService<Orders>());

// The alerts (virtual-grid.md §15): each page's rules, kept in its browser, evaluated as the ChalkQL blotter evaluates them
// (StandingAlerts, the store's), each shape once however many pages watch it, and each rule's alerts sent no oftener than its
// throttle (Tapline.Alerts). None computed: no ledger to hear.
var desk = store is null ? null : new AlertDesk(store.Alerts);
if (desk is not null) builder.Services.AddSingleton(desk);

// What a grid's read may take at the source (GridOptions.MaxReadBytes): a host would budget each user; this one, every
// reader alike. --read-memory 32m (the default), 2g: a read of the database's pages needs a few MB; --store computed,
// which copies and sorts the orders within the read's budget, needs far more to sort.
builder.Services.AddSingleton(new GridOptions { MaxReadBytes = (uint)Math.Min(uint.MaxValue, Bytes(config["read-memory"] ?? "32m")) });
builder.Services.AddSingleton(services => new Orders(house, services.GetRequiredService<ILogger<Orders>>()));

var app = builder.Build();
Listening.WarnWhenStarted(app); // "This sample has no authentication. Listening on …"
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapBlazorRealtime();
await app.RunAsync(); // (awaited, not blocked on: the thread the start finished on unwinds, letting go of what it loaded)
// The web server stopped and its circuits gone, so no grid reads what's let go under it: the house, then the source.
await house.DisposeAsync();
if (desk is not null) await desk.DisposeAsync(); // (its evaluator with it: the store's, on its planner)
if (store is not null) await store.DisposeAsync();
return 0;

// "5m", "500k", "200000": a count of orders.
static long Count(string text)
{
    var scale = char.ToLowerInvariant(text[^1]) switch { 'k' => 1_000L, 'm' => 1_000_000L, _ => 1L };
    var digits = scale == 1 ? text : text[..^1];
    return (long)(double.Parse(digits, CultureInfo.InvariantCulture) * scale);
}

// "256m", "2g", "512k": bytes, binary (a k 1,024 of them).
static long Bytes(string text)
{
    var scale = char.ToLowerInvariant(text[^1]) switch { 'k' => 1L << 10, 'm' => 1L << 20, 'g' => 1L << 30, _ => 1L };
    var digits = scale == 1 ? text : text[..^1];
    return (long)(double.Parse(digits, CultureInfo.InvariantCulture) * scale);
}

// "30s", "2", "0": seconds.
static TimeSpan Seconds(string? text, double fallback)
{
    return TimeSpan.FromSeconds(text is null ? fallback : double.Parse(text.TrimEnd('s'), CultureInfo.InvariantCulture));
}
