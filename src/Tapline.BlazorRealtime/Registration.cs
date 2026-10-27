using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Tapline;
using Tapline.AspNetCore;

namespace BlazorRealtime;

/// <summary>
///     The circuit transport's options: its circuits' outlets (an <see cref="OutletOptions" />), the circuit hub's
///     timeouts
///     and the circuit sockets. The router's own are <see cref="TaplineOptions" /> (<c>AddTapline</c>).
/// </summary>
public sealed class BlazorRealtimeOptions : OutletOptions
{
    /// <summary>
    ///     How long the server waits without hearing from a circuit's client before it drops the connection: SignalR's
    ///     <c>ClientTimeoutInterval</c>, on Blazor's circuit hub only (30 s by SignalR's default). A live client is never
    ///     quiet that long — it acknowledges every frame, a heartbeat among them every
    ///     <see cref="TaplineOptions.HeartbeatInterval" />, and pings every 3 s it sends nothing else (<c>startBlazor</c>'s
    ///     <c>keepAliveInterval</c>), however long a large message takes to come down a slow link — so a gone or frozen one
    ///     is noticed in seconds; Blazor then keeps its circuit for it to reconnect to. Applied unless the app set the hub's
    ///     own value (<c>AddInteractiveServerComponents().AddHubOptions(…)</c>).
    /// </summary>
    public TimeSpan CircuitClientTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     SignalR's keep-alive ping on the circuit hub, sent when the server has sent nothing for this long (15 s by
    ///     default):
    ///     the heartbeats keep it from firing once the circuit's outlet is bound; before that, it keeps the browser's server
    ///     timeout fed. Applied unless the app set the hub's own value.
    /// </summary>
    public TimeSpan CircuitKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    ///     Name of an optional query parameter on the page URL that caps the circuit's Streaming rate, e.g. <c>"bwlimit"</c>
    ///     — to try a slow consumer without throttling the page, its static assets or out-of-band fetches. The value follows
    ///     rsync's <c>--bwlimit</c>: KiB/s unless suffixed — <c>b</c> bytes, <c>k</c>/<c>m</c>/<c>g</c> powers of 1024,
    ///     <c>kb</c>/<c>mb</c>/<c>gb</c> powers of 1000 — e.g. <c>?bwlimit=64</c>, <c>0.5</c> (512 B/s, the minimum),
    ///     <c>500kb</c>, <c>1.5m</c>, <c>800b</c>; <c>0</c> or anything malformed leaves the cap alone. It can only lower
    ///     <see cref="OutletOptions.StreamingBytesPerSecond" />, never raise
    ///     it; the burst is one second's worth (at most <see cref="OutletOptions.StreamingBurstBytes" />). Read when the
    ///     circuit opens; a later <see cref="Outlet.SetStreamingRate" /> replaces it. Null (default): no such parameter.
    /// </summary>
    public string? StreamingRateQueryParameter { get; set; }

    /// <summary>
    ///     Circuit WebSocket messages with fewer compressible bytes than this are sent uncompressed (when Blazor's
    ///     per-message deflate is on, its default). Deflate costs a few µs per message plus ~55 ns per byte, so per byte saved
    ///     it is dearest for small messages — the high-rate frames of fast clients, for whom bandwidth matters least — while a
    ///     constrained client's conflated frames (a few hundred bytes and up) stay compressed. Content declared not
    ///     compressible (<c>compressible: false</c> on posts and publishes, <see cref="Table{TKey,TRow}.Compressible" />) and
    ///     out-of-band handles don't count. 0: only messages with nothing compressible skip deflate. Installed by
    ///     <c>MapBlazorRealtime()</c>.
    /// </summary>
    public int WebSocketCompressionThreshold { get; set; } = 256;

    /// <summary>
    ///     Opt-in cap on each circuit socket's kernel send buffer (<c>SO_SNDBUF</c>), in bytes; 0 (default) keeps the OS's
    ///     auto-tuned size, often hundreds of KB. Bytes queued there are already serialized, so a smaller buffer keeps the
    ///     backlog in front of the wire short — the last point frames are bound is closer to when they leave, and bursts
    ///     (render batches, snapshots) delay what follows less. It also caps a connection's throughput at roughly this ÷ the
    ///     round trip, so size it to at least bandwidth × RTT of the clients you serve (64 KB covers 5 Mbit/s at 100 ms).
    ///     Circuit sockets only (other connections keep the default); Linux doubles the value it is given. Installed by
    ///     <c>MapBlazorRealtime()</c>.
    /// </summary>
    public int CircuitSendBufferBytes { get; set; }

    /// <summary>
    ///     Linux only, opt-in: <c>TCP_NOTSENT_LOWAT</c> for each circuit socket, in bytes; 0 (default) leaves it unset.
    ///     The kernel then stops taking more from the server once this much is queued but not yet sent, while the data in
    ///     flight still grows to the full (auto-tuned) window — the backlog in front of the wire stays short without capping
    ///     throughput the way a small <see cref="CircuitSendBufferBytes" /> does. 16–32 KB is typical. Ignored elsewhere (on
    ///     macOS the option only delays writability, it doesn't cap writes). Circuit sockets only. Installed by
    ///     <c>MapBlazorRealtime()</c>.
    /// </summary>
    public int CircuitNotSentLowWatermarkBytes { get; set; }
}

public static class BlazorRealtimeServiceCollectionExtensions
{
    /// <summary>
    ///     The configuration section <see cref="BlazorRealtimeOptions" /> are read from (before the code's own settings,
    ///     which win).
    /// </summary>
    public const string ConfigurationSection = "Tapline.BlazorRealtime";

    private static readonly TimeSpan SignalRClientTimeout = TimeSpan.FromSeconds(30),
        SignalRKeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     The circuit hub's (Blazor's <c>ComponentHub</c>, internal: found by name) timeouts — the app's own settings
    ///     kept.
    /// </summary>
    private static void ConfigureCircuitHub(IServiceCollection services, Assembly components)
    {
        if (components.GetType("Microsoft.AspNetCore.Components.Server.ComponentHub") is not { } hub ||
            !typeof(Hub).IsAssignableFrom(hub)) return;
        typeof(BlazorRealtimeServiceCollectionExtensions).GetMethod(nameof(ConfigureHub),
                BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(hub).Invoke(null, [services]);
    }

    private static void ConfigureHub<THub>(IServiceCollection services) where THub : Hub
    {
        services.AddSingleton<IPostConfigureOptions<HubOptions<THub>>>(sp => new PostConfigureOptions<HubOptions<THub>>(
            Options.DefaultName, o =>
            {
                var ours = sp.GetRequiredService<IOptions<BlazorRealtimeOptions>>().Value;
                if (o.ClientTimeoutInterval is null || o.ClientTimeoutInterval == SignalRClientTimeout)
                    o.ClientTimeoutInterval = ours.CircuitClientTimeout;
                if (o.KeepAliveInterval is null || o.KeepAliveInterval == SignalRKeepAlive)
                    o.KeepAliveInterval = ours.CircuitKeepAliveInterval;
            }));
    }

    /// <summary>
    ///     Registers the circuit transport — a per-circuit <see cref="Outlet" /> and the hooks that carry it over the
    ///     circuit's
    ///     connection — and the router it serves (<see cref="TaplineServiceCollectionExtensions.AddTapline" />: the app calls
    ///     that too, for the router's own options). Call after <c>AddInteractiveServerComponents()</c> (it wraps Blazor's hub
    ///     protocol). Options come from configuration (<see cref="ConfigurationSection" />), then
    ///     <paramref name="configure" />.
    /// </summary>
    public static IServiceCollection AddBlazorRealtime(this IServiceCollection services,
        Action<BlazorRealtimeOptions>? configure = null)
    {
        services.AddTapline();
        var options = services.AddOptions<BlazorRealtimeOptions>();
        services.AddSingleton<IConfigureOptions<BlazorRealtimeOptions>>(sp =>
            new ConfigureNamedOptions<BlazorRealtimeOptions>(Options.DefaultName,
                o => sp.GetService<IConfiguration>()?.GetSection(ConfigurationSection)
                    .Bind(o))); // configuration first (if there is any)…
        if (configure is not null) options.Configure(configure); // …then the code's: it wins

        services.AddSingleton<BindRegistry>();
        services.AddScoped(sp => new Outlet(sp.GetRequiredService<OutletRegistry>(),
            sp.GetRequiredService<IOptions<BlazorRealtimeOptions>>().Value));
        services.AddScoped<CircuitTransport>();
        services.AddScoped<CircuitHandler, RealtimeCircuitHandler>();

        services.AddSingleton(typeof(DefaultHubLifetimeManager<>));
        services.AddSingleton(typeof(HubLifetimeManager<>), typeof(CircuitLifetimeManager<>));

        var blazorPack = services.SingleOrDefault(d => d.ServiceType == typeof(IHubProtocol)
                                                       && d.ImplementationType?.FullName ==
                                                       "Microsoft.AspNetCore.Components.Server.BlazorPack.BlazorPackHubProtocol")
                         ?? throw new InvalidOperationException(
                             "Call AddBlazorRealtime() after AddInteractiveServerComponents().");
        services.Remove(blazorPack);
        ConfigureCircuitHub(services, blazorPack.ImplementationType!.Assembly);
        services.AddSingleton<IHubProtocol>(sp => new RealtimeProtocol(
            (IHubProtocol)ActivatorUtilities.CreateInstance(sp, blazorPack.ImplementationType!),
            sp.GetRequiredService<OutletRegistry>()));
        return services;
    }
}

/// <summary>
///     Binds the circuit's outlet to its connection on first connect and on every reconnect. When the circuit opens:
///     applies
///     a Streaming cap from the page URL (<see cref="BlazorRealtimeOptions.StreamingRateQueryParameter" />), and on a test
///     page makes the outlet a test connection (<see cref="TaplineOptions.FaultInjection" />).
/// </summary>
internal sealed class RealtimeCircuitHandler(
    CircuitTransport transport,
    IJSRuntime js,
    NavigationManager navigation,
    IOptions<BlazorRealtimeOptions> options,
    FaultInjectionService faults) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var o = options.Value;
        faults.Enlist(transport.Outlet, navigation.ToBaseRelativePath(navigation.Uri));
        if (o.StreamingRateQueryParameter is { } name
            && QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query).TryGetValue(name, out var values)
            && BandwidthLimits.Parse(values.LastOrDefault()) is { } rate)
        {
            if (o.StreamingBytesPerSecond > 0) rate = Math.Min(rate, o.StreamingBytesPerSecond); // lower only
            transport.Outlet.SetStreamingRate(rate, Math.Min(rate, o.StreamingBurstBytes));
        }

        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _ = BindAsync();
        return Task.CompletedTask;
    }

    private async Task BindAsync()
    {
        try
        {
            await js.InvokeVoidAsync("taplineBind", transport.BindToken, transport.Outlet.Id, transport.Outlet.Nonce);
        } // the send itself binds
        catch (Exception e) when (e is JSException or JSDisconnectedException or TaskCanceledException)
        {
        }
    }
}