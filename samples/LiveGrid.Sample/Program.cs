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
builder.Services.AddSingleton(new Orders(builder.Configuration.GetValue("rows", 100_000L))); // --rows 5000000

var app = builder.Build();
Listening.WarnWhenStarted(app); // "This sample has no authentication. Listening on …"
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapBlazorRealtime();
app.Run();
