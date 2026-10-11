using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Tapline.AspNetCore;

namespace Tapline.MagicOnion.Server;

/// <summary>
///     The stream's sessions' outlets (an <see cref="OutletOptions" />). <see cref="OutletOptions.DisconnectedHold" /> is
///     how
///     long a session outlives its last call, for the next to resume it — 30 s by default: no circuit bounds it here.
/// </summary>
public sealed class TaplineMagicOnionOptions : OutletOptions
{
    internal static readonly TimeSpan DefaultHold = TimeSpan.FromSeconds(30);

    public TaplineMagicOnionOptions()
    {
        DisconnectedHold = DefaultHold;
    }
}

public static class TaplineMagicOnionExtensions
{
    /// <summary>
    ///     The configuration section <see cref="TaplineMagicOnionOptions" /> are read from (before the code's own
    ///     settings, which win).
    /// </summary>
    public const string ConfigurationSection = "TaplineMagicOnion";

    /// <summary>
    ///     Registers the stream — its sessions — and the router it serves (
    ///     <see cref="TaplineServiceCollectionExtensions.AddTapline" />).
    ///     MagicOnion itself is the app's to add (<c>AddMagicOnion()</c>), as is a port for it: gRPC is HTTP/2 (over TLS, or
    ///     a cleartext endpoint of HTTP/2 alone). Options come from configuration (<see cref="ConfigurationSection" />), then
    ///     <paramref name="configure" />.
    /// </summary>
    public static IServiceCollection AddTaplineMagicOnion(this IServiceCollection services,
        Action<TaplineMagicOnionOptions>? configure = null)
    {
        services.AddTapline();
        var options = services.AddOptions<TaplineMagicOnionOptions>();
        services.AddSingleton<IConfigureOptions<TaplineMagicOnionOptions>>(sp =>
            new ConfigureNamedOptions<TaplineMagicOnionOptions>(Options.DefaultName,
                o => sp.GetService<IConfiguration>()?.GetSection(ConfigurationSection)
                    .Bind(o))); // configuration first (if there is any)…
        if (configure is not null) options.Configure(configure); // …then the code's: it wins
        services.TryAddSingleton<TaplineStreamSessions>();
        return services;
    }

    /// <summary>
    ///     Maps the stream (<see cref="ITaplineStream" />). The app's step 1 — its own service — finds a session's outlet
    ///     with <see cref="TaplineStreamSessions.Find" />.
    /// </summary>
    public static IEndpointConventionBuilder MapTaplineMagicOnion(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapMagicOnionService(typeof(TaplineStreamService));
    }
}