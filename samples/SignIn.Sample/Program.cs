using BlazorRealtime;
using SignIn.Sample;
using SignIn.Sample.Components;

var builder = WebApplication.CreateBuilder(args);
Listening.Configure(builder); // http://localhost:5940 unless told where: this machine only by default (see Listening)
builder.WebHost
    .UseStaticWebAssets(); // blazor.web.js, tapline.js, blazor-realtime.js and tapline-opaque.mjs from the build output in any environment
builder.Configuration["EnableStaticAssetsDevelopmentCaching"] =
    "true"; // keep @Assets' fingerprinted URLs immutable when run from build output (published apps already are)
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddBlazorRealtime(); // the circuit transport, and the router it serves: after AddInteractiveServerComponents

var app = builder.Build();
Listening.WarnWhenStarted(app); // "This sample's sign-in is a demo… Listening on …"
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapBlazorRealtime();
SignInDemo.Map(app); // the demo realm: passkeys and passwords (OPAQUE), in memory; and "members", which requires a sign-in
app.Run();
