using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Tapline.AspNetCore;

namespace BlazingOrderBooks.Sample;

/// <summary>
///     The sample's operator endpoints — Tapline's viewer of the router's state at <c>GET /ops/realtime</c> (its
///     clients, their sessions, subscriptions and last records — see <c>MapTaplineViewer</c>) — on a port of their own,
///     apart from the app's:
///     <c>Tapline:Ops:Port</c>, <see cref="DefaultPort" /> by default, listened on only on one network interface —
///     <c>ORDERBOOKS_OPS_ENDPOINTS</c> (or <c>Market:OpsEndpoints</c>): <c>localhost</c> by default (the loopback, IPv4
///     and
///     IPv6: from this machine alone), an address, or an interface's name (<c>en0</c>, <c>eth0</c>…: its addresses);
///     <c>off</c> turns them off. Asked for on the app's port, they aren't there (404): Tapline answers ops endpoints
///     on the ops port alone — the port a request's connection arrived on, not its Host header.
///     <para>
///         They have no authentication: what keeps them in is that the ops port is listened on where only an operator
///         reaches.
///         For a demo on one port (one URL to open), the <c>shared-port</c> launch profile sets
///         <c>Tapline:Ops:SharedPortReason</c> ("non-production demo") in Development: the ops endpoints answer on the
///         app's port too, and every viewer page says so, and why. In production these are a deploy pipeline's or an
///         orchestrator's to do, not an app's endpoints.
///     </para>
/// </summary>
internal static class OpsEndpoints
{
    public const int DefaultPort = 5899;

    public static Plan Configure(WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var spec = (Environment.GetEnvironmentVariable("ORDERBOOKS_OPS_ENDPOINTS") ??
                    config["Market:OpsEndpoints"] ?? "localhost").Trim();
        if (spec.Length == 0 || spec.ToLowerInvariant() is "off" or "false" or "0" or "none")
            return new Plan(false, null, [], $"off (ORDERBOOKS_OPS_ENDPOINTS={spec})");
        if (config["Tapline:Ops:SharedPortReason"] is { Length: > 0 } reason)
            return new Plan(true, null, [], $"every port the app listens on ({reason})");
        var port = int.TryParse(config["Tapline:Ops:Port"], out var p) ? p : DefaultPort;
        var urls = Hosts(spec).Select(host => $"http://{host}:{port}").ToList();
        return new Plan(true, port, urls, string.Join(", ", urls));
    }

    /// <summary>
    ///     The hosts to listen on for <paramref name="spec" />: localhost, an address, or an interface's addresses (not
    ///     link-local).
    /// </summary>
    private static IEnumerable<string> Hosts(string spec)
    {
        if (spec.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return ["localhost"];
        if (IPAddress.TryParse(spec, out var address)) return [Host(address)];
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == spec)
                  ?? throw new InvalidOperationException(
                      $"ORDERBOOKS_OPS_ENDPOINTS={spec}: no such network interface (an address, an interface's name, localhost, or off).");
        var hosts = nic.GetIPProperties().UnicastAddresses.Select(u => u.Address).Where(a => !a.IsIPv6LinkLocal)
            .Select(Host).ToList();
        return hosts.Count > 0
            ? hosts
            : throw new InvalidOperationException($"ORDERBOOKS_OPS_ENDPOINTS={spec}: the interface has no address now.");
    }

    private static string Host(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
    }

    public static void Map(WebApplication app, Plan plan)
    {
        if (!plan.On)
        {
            app.Logger.LogInformation("Ops endpoints {Where}", plan.Where);
            return;
        }

        app.MapTaplineViewer(); // (an ops endpoint itself)
        app.MapTaplineDrain(); // POST /ops/tapline/drain, /ops/tapline/nodes/{id}/drain: the viewer's drain forms
    }

    /// <summary>
    ///     Where the ops endpoints answer, decided before the app is built: off; on a shared port; or on their own (its
    ///     URLs, to listen on).
    /// </summary>
    public sealed record Plan(bool On, int? Port, IReadOnlyList<string> Urls, string Where);
}