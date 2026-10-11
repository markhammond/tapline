using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using BlazorRealtime.Sample.Waveform;
using Tapline.AspNetCore;

namespace BlazorRealtime.Sample;

/// <summary>
///     The sample's operator endpoints — <c>POST /waveform/station/restart</c> (simulate station crash: every viewer
///     is told the service is down, then up), <c>POST /waveform/station/deploy?seconds=10&amp;grace=5</c> (a blue/green
///     deploy), and Tapline's viewer of the router's state at <c>GET /ops/realtime</c> (its clients, their sessions,
///     subscriptions and last records — see <c>MapTaplineViewer</c>) — on a port of their own, apart from the app's:
///     <c>Tapline:Ops:Port</c>, <see cref="DefaultPort" /> by default, listened on only on one network interface —
///     <c>WAVEFORM_OPS_ENDPOINTS</c> (or <c>Waveform:OpsEndpoints</c>): <c>localhost</c> by default (the loopback, IPv4
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
        var spec = (Environment.GetEnvironmentVariable("WAVEFORM_OPS_ENDPOINTS") ??
                    config["Waveform:OpsEndpoints"] ?? "localhost").Trim();
        if (spec.Length == 0 || spec.ToLowerInvariant() is "off" or "false" or "0" or "none")
            return new Plan(false, null, [], $"off (WAVEFORM_OPS_ENDPOINTS={spec})");
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
                      $"WAVEFORM_OPS_ENDPOINTS={spec}: no such network interface (an address, an interface's name, localhost, or off).");
        var hosts = nic.GetIPProperties().UnicastAddresses.Select(u => u.Address).Where(a => !a.IsIPv6LinkLocal)
            .Select(Host).ToList();
        return hosts.Count > 0
            ? hosts
            : throw new InvalidOperationException($"WAVEFORM_OPS_ENDPOINTS={spec}: the interface has no address now.");
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

        var ops = app.MapGroup("/waveform/station").RequireOpsPort();
        app.MapTaplineViewer(); // (an ops endpoint itself)
        app.MapTaplineDrain(); // POST /ops/tapline/drain, /ops/tapline/nodes/{id}/drain: the viewer's drain forms
        // A restart, simulated: the station on air takes a new instance id; the router deems the old one dead at its next
        // heartbeat, and viewers are told the service is down — then up — and subscribe again.
        ops.MapPost("/restart", (WaveformDeployment stations) =>
        {
            stations.Current.Restart();
            return Results.Ok();
        });
        // A blue/green deploy: the other colour starts alongside, caught up; the one on air drains, its viewers moved one at
        // a time over ?seconds= (default 10), each given ?grace= (default 5) to move — make before break — then it is retired.
        ops.MapPost("/deploy", (WaveformDeployment stations, double? seconds, double? grace) =>
        {
            _ = stations.DeployAsync(TimeSpan.FromSeconds(Math.Clamp(seconds ?? 10, 0, 600)),
                TimeSpan.FromSeconds(Math.Clamp(grace ?? 5, 0, 60)));
            return Results.Accepted();
        });
        app.Logger.LogInformation(
            "Ops endpoints (GET /ops/realtime; POST /waveform/station/restart, /deploy; POST /ops/tapline/drain, /ops/tapline/nodes/{{id}}/drain) on {Where}",
            plan.Where);
    }

    /// <summary>
    ///     Where the ops endpoints answer, decided before the app is built: off; on a shared port; or on their own (its
    ///     URLs, to listen on).
    /// </summary>
    public sealed record Plan(bool On, int? Port, IReadOnlyList<string> Urls, string Where);
}