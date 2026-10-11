using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace BlazorRealtime.Sample;

/// <summary>
///     Where the sample listens. It has no authentication, so by default only on this machine: <see cref="Default" />
///     (<c>localhost</c> — the loopback, IPv4 and IPv6 — on 5898, clear of macOS's AirPlay Receiver on 5000), unless told
///     otherwise — <c>--urls</c>, <c>ASPNETCORE_URLS</c>, <c>ASPNETCORE_HTTP_PORTS</c> (as container images set), or
///     Kestrel
///     endpoints in configuration — and on the ops endpoints' own port (see <see cref="OpsEndpoints" />), added to the
///     URLs
///     (told where some other way, those must name it too: Tapline.BlazorRealtime checks at startup). Listening, it says
///     so, and
///     where
///     — warning when that reaches beyond this machine. (Set in configuration, the default URL also names the router:
///     host:5898 — see Tapline.BlazorRealtime's <c>RouterNames</c>.)
/// </summary>
internal static class Listening
{
    public const string Default = "http://localhost:5898";

    /// <summary>
    ///     Listens on the configured URLs, or <see cref="Default" />, and on <paramref name="opsUrls" /> — unless the
    ///     configuration names where some other way.
    /// </summary>
    public static void Configure(WebApplicationBuilder builder, IReadOnlyList<string> opsUrls)
    {
        var config = builder.Configuration;
        var urls = config["urls"];
        if (string.IsNullOrWhiteSpace(urls) && (!string.IsNullOrWhiteSpace(config["http_ports"]) ||
                                                !string.IsNullOrWhiteSpace(config["https_ports"])
                                                || config.GetSection("Kestrel:Endpoints").Exists())) return;
        var app = string.IsNullOrWhiteSpace(urls)
            ? [Default]
            : urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        builder.WebHost.UseUrls([.. app, .. opsUrls]);
    }

    /// <summary>Once listening: a warning naming the addresses — this sample has no authentication.</summary>
    public static void WarnWhenStarted(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?.Addresses ?? [];
            var beyond = addresses.Any(a => !IsLoopback(a));
            app.Logger.LogWarning("This sample has no authentication. Listening on {Addresses}{Reach}",
                string.Join(", ", addresses),
                beyond ? " — reachable from other machines" : " (this machine only)");
        });
    }

    /// <summary>
    ///     An address only this machine can reach: localhost, 127.0.0.0/8, ::1 (not *, +, 0.0.0.0, [::], or a host's
    ///     address).
    /// </summary>
    private static bool IsLoopback(string address)
    {
        var host = address.Replace("://*", "://wildcard").Replace("://+", "://wildcard");
        return Uri.TryCreate(host, UriKind.Absolute, out var uri)
               && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }
}