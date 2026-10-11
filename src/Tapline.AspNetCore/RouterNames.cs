using Microsoft.Extensions.Configuration;

namespace Tapline.AspNetCore;

/// <summary>
///     A router's default name — when neither a <see cref="RouterInstance" /> is registered nor
///     <see cref="TaplineOptions.RouterName" /> set: this host's name and the first port the server is configured to
///     listen on, e.g. <c>my-laptop:5898</c>. A router's name should be the same across its restarts (a restart then
///     supersedes its old instance at once) and differ between routers alive together (or each passes for the other's
///     successor); host and port give both on one machine, where the host's name alone gives two servers one name. Not a
///     network address: interfaces come in no fixed order, addresses change with the network, and containers on different
///     hosts can share one. Still a guess: a container's hostname changes with each start, and a port set only in code
///     (<c>ConfigureKestrel</c>) isn't in configuration — then the host's name alone. In production, name the router: from
///     the platform (a StatefulSet pod's name), or as the service registry knows it.
/// </summary>
public static class RouterNames
{
    /// <summary>This host's name and the first configured port; the host's name alone if no port is configured.</summary>
    public static string Default(IConfiguration? configuration)
    {
        var host = Environment.MachineName.ToLowerInvariant();
        return configuration is not null && FirstPort(configuration) is int port ? $"{host}:{port}" : host;
    }

    /// <summary>
    ///     The first port in configuration: <c>urls</c> (<c>--urls</c>, <c>ASPNETCORE_URLS</c>), Kestrel's configured
    ///     endpoints, then <c>http_ports</c> / <c>https_ports</c> (<c>ASPNETCORE_HTTP_PORTS</c>, as container images set).
    ///     Null: none (Kestrel's own default, endpoints set in code, or port 0 — chosen when it binds).
    /// </summary>
    public static int? FirstPort(IConfiguration configuration)
    {
        var urls = Split(configuration["urls"]).Concat(configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(e => e["Url"]).OfType<string>());
        foreach (var url in urls)
            if (Uri.TryCreate(url.Replace("://*", "://wildcard").Replace("://+", "://wildcard"), UriKind.Absolute,
                    out var uri) && uri.Port > 0)
                return uri.Port;
        foreach (var key in (string[])["http_ports", "https_ports"])
        foreach (var value in Split(configuration[key]))
            if (int.TryParse(value, out var port) && port > 0)
                return port;
        return null;
    }

    private static string[] Split(string? list)
    {
        return (list ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}