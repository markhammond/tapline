using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace SignIn.Sample;

/// <summary>
///     Where the sample listens: by default only on this machine, <see cref="Default" /> (<c>localhost</c>, the loopback,
///     IPv4 and IPv6), which passkeys need anyway — they belong to <c>localhost</c>. Unless told otherwise: <c>--urls</c>,
///     <c>ASPNETCORE_URLS</c>, <c>ASPNETCORE_HTTP_PORTS</c>, or Kestrel endpoints in configuration. Listening, it says so,
///     and where, warning when that reaches beyond this machine.
/// </summary>
internal static class Listening
{
    public const string Default = "http://localhost:5940";

    /// <summary>Listens on the configured URLs, or <see cref="Default" />, unless the configuration names where some other way.</summary>
    public static void Configure(WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        if (!string.IsNullOrWhiteSpace(config["urls"]) || !string.IsNullOrWhiteSpace(config["http_ports"]) ||
            !string.IsNullOrWhiteSpace(config["https_ports"]) || config.GetSection("Kestrel:Endpoints").Exists()) return;
        builder.WebHost.UseUrls(Default);
    }

    /// <summary>Once listening: a warning naming the addresses — its users are a demo's, kept in memory.</summary>
    public static void WarnWhenStarted(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?.Addresses ?? [];
            var beyond = addresses.Any(a => !IsLoopback(a));
            app.Logger.LogWarning("This sample's sign-in is a demo, its users kept in memory. Listening on {Addresses}{Reach}",
                string.Join(", ", addresses), beyond ? " — reachable from other machines" : " (this machine only)");
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
