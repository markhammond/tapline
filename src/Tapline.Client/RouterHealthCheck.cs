using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Tapline.Client;

/// <summary>
///     The router's health as a client sees it (<see cref="TaplineClient.RouterHealthCheck" />): healthy while it serves
///     and
///     its heartbeats come; degraded when it says so (its reason), while it drains (the transport moving on), or when its
///     heartbeats are late; unhealthy when it's out of reach, or silent too long. Its data — the router's state, the
///     change's
///     correlation id (the router's log's needle), its reason or grace, how long it's been quiet — is for production
///     support.
/// </summary>
public sealed class RouterHealthCheck : IHealthCheck
{
    private readonly TaplineClient _client;
    private readonly TimeSpan _lateAfter, _silentAfter;

    internal RouterHealthCheck(TaplineClient client, TimeSpan lateAfter, TimeSpan silentAfter)
    {
        (_client, _lateAfter, _silentAfter) = (client, lateAfter, silentAfter);
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var router = _client.Router;
        var quiet = _client.SinceLastFrame;
        var reachable = _client.Reachable;
        var data = new Dictionary<string, object>
            { ["router"] = router.State.ToString(), ["correlation"] = router.Correlation, ["reachable"] = reachable };
        if (quiet != TimeSpan.MaxValue) data["quietMs"] = (long)quiet.TotalMilliseconds;
        if (router.Reason is { } reason) data["reason"] = reason;
        if (router.DrainsBy is { } by) data["drainsBy"] = by;
        var status = !reachable ? HealthStatus.Unhealthy
            : quiet > _silentAfter ? HealthStatus.Unhealthy
            : router.State != RouterState.Serving || quiet > _lateAfter ? HealthStatus.Degraded
            : HealthStatus.Healthy;
        var description = !reachable ? "the router is out of reach"
            : quiet > _silentAfter ? $"the router has been silent for {quiet.TotalSeconds:0} s"
            : router.State == RouterState.Draining ? $"the router is draining, gone by {router.DrainsBy:O}"
            : router.State == RouterState.Degraded ? $"the router is degraded: {router.Reason}"
            : quiet > _lateAfter ? $"the router's heartbeats are late ({quiet.TotalSeconds:0.#} s)"
            : "the router is serving";
        return Task.FromResult(new HealthCheckResult(status, description, data: data));
    }
}