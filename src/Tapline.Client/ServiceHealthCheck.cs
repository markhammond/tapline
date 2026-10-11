using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Tapline.Client;

/// <summary>
///     A service's health as a client sees it (<see cref="TaplineClient.ServiceHealthCheck" />), over its subscriptions to
///     the
///     service: healthy while each streams from a node serving it; degraded while one is shown stale data (its node, or a
///     partition there, degraded), its node drains, or a drain let one go; unhealthy while the service is down for it, or
///     the
///     router is out of reach. Its data — the subscriptions, the nodes and degraded scopes (ids of the router's run), the
///     last
///     notice's correlation id (the router's log's needle) — is for production support.
/// </summary>
public sealed class ServiceHealthCheck : IHealthCheck
{
    private readonly TaplineClient _client;

    internal ServiceHealthCheck(TaplineClient client, string service)
    {
        (_client, Service) = (client, service);
    }

    /// <summary>The service it checks, by name.</summary>
    public string Service { get; }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var (status, description, data) = _client.ServiceHealth(Service);
        return Task.FromResult(new HealthCheckResult(status, description, data: data));
    }
}