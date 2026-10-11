using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tapline.AspNetCore;

/// <summary>
///     Where the ops endpoints answer — the router's viewer (<c>MapTaplineViewer</c>) and any endpoint marked
///     <see cref="OpsPortEndpointExtensions.RequireOpsPort{TBuilder}" />: on a port of their own (<see cref="Port" />), or
///     —
///     in Development, for a stated reason — on every port the server listens on (<see cref="SharedPortReason" />). One of
///     the two is required once any is mapped: without it the app doesn't start. Neither is access control: who reaches
///     the
///     port is the network's to decide, and what they may see the app's (<c>RequireAuthorization</c> on the endpoints).
/// </summary>
public sealed class OpsOptions
{
    /// <summary>
    ///     The port the ops endpoints answer on — their own. A request for one arriving on any other port finds nothing there
    ///     (404, as if they weren't): the port its connection arrived on, not its Host header. Checked at startup: the server
    ///     listens on it (the app adds it to its URLs: <c>http://127.0.0.1:5899</c>, say), and on another port besides (the
    ///     ops port alone would be sharing it). Reachable from other machines without authorization: a warning.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    ///     Instead of a port of their own: the ops endpoints answer on every port the server listens on, the client-facing one
    ///     too, for this reason — "non-production demo", say. Logged at startup, shown on every viewer page (and in its JSON:
    ///     <c>sharedPort</c>). Development only: in any other environment the app doesn't start.
    /// </summary>
    public string? SharedPortReason { get; set; }
}

public static class OpsPortEndpointExtensions
{
    /// <summary>
    ///     Makes these ops endpoints, answered only where <see cref="OpsOptions" /> say: on the ops port, or — shared — on
    ///     any.
    ///     One that changes things (a POST, PUT, PATCH or DELETE: a drain, a restart) refuses a browser's cross-site request,
    ///     403 — a page elsewhere posting a form here needs no preflight, so the browser's own say is checked: its
    ///     <c>Sec-Fetch-Site</c> (another site, or another origin of this one — the app's own port), or an <c>Origin</c> not
    ///     this one. This origin's pages and scripts (curl, a container's hook) send neither: they pass. (Minimal API
    ///     endpoints:
    ///     the check is an endpoint filter.)
    /// </summary>
    public static TBuilder RequireOpsPort<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(OpsEndpoint.Instance));
        builder.AddEndpointFilter(SameOriginChanges);
        return builder;
    }

    private static ValueTask<object?> SameOriginChanges(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method)) return next(context);
        string site = request.Headers["Sec-Fetch-Site"].ToString(), origin = request.Headers.Origin.ToString();
        var elsewhere = site is "cross-site" or "same-site"
                        || (origin.Length > 0 && !string.Equals(origin, $"{request.Scheme}://{request.Host}",
                            StringComparison.OrdinalIgnoreCase));
        return elsewhere
            ? ValueTask.FromResult<object?>(Results.Problem(
                "An ops endpoint changes things for this origin's own pages, or scripts: not for a page elsewhere.",
                statusCode: StatusCodes.Status403Forbidden))
            : next(context);
    }
}

/// <summary>An ops endpoint's mark (<see cref="OpsPortEndpointExtensions.RequireOpsPort{TBuilder}" />).</summary>
internal sealed class OpsEndpoint
{
    public static readonly OpsEndpoint Instance = new();
}

/// <summary>
///     Routing's rule for ops endpoints: where a request's connection didn't arrive on the ops port (any, shared), they
///     are
///     no route at all — routing goes on as if they weren't there (the app's own 404). It branches routing's tree before
///     the
///     HTTP method does, so an ops path asked for elsewhere isn't a route of another method either (405 would say it's
///     there). Costs only the requests whose paths an ops endpoint shares.
/// </summary>
internal sealed class OpsPortPolicy(IOptions<TaplineOptions> options)
    : MatcherPolicy, INodeBuilderPolicy, IEndpointSelectorPolicy
{
    public override int Order => -2000; // before the HTTP method's (-1000)

    bool IEndpointSelectorPolicy.AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        return ContainsDynamicEndpoints(endpoints) && endpoints.Any(IsOps);
    }

    /// <summary>The same rule where routing can't branch ahead of time (dynamic endpoints among the candidates).</summary>
    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        if (Answers(httpContext)) return Task.CompletedTask;
        for (var i = 0; i < candidates.Count; i++)
            if (candidates.IsValidCandidate(i) && IsOps(candidates[i].Endpoint))
                candidates.SetValidity(i, false);
        return Task.CompletedTask;
    }

    bool INodeBuilderPolicy.AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        return !ContainsDynamicEndpoints(endpoints) && endpoints.Any(IsOps);
    }

    /// <summary>
    ///     Two branches: on the ops port, every endpoint; elsewhere, all but the ops endpoints (none left: no branch, no
    ///     match).
    /// </summary>
    public IReadOnlyList<PolicyNodeEdge> GetEdges(IReadOnlyList<Endpoint> endpoints)
    {
        var others = endpoints.Where(e => !IsOps(e)).ToArray();
        return others.Length == 0
            ? [new PolicyNodeEdge(true, endpoints)]
            : [new PolicyNodeEdge(true, endpoints), new PolicyNodeEdge(false, others)];
    }

    public PolicyJumpTable BuildJumpTable(int exitDestination, IReadOnlyList<PolicyJumpTableEdge> edges)
    {
        int ops = exitDestination, others = exitDestination;
        foreach (var edge in edges)
            if (edge.State is true) ops = edge.Destination;
            else others = edge.Destination;
        return new Jumps(this, ops, others);
    }

    private static bool IsOps(Endpoint endpoint)
    {
        return endpoint.Metadata.GetMetadata<OpsEndpoint>() is not null;
    }

    /// <summary>Whether ops endpoints answer on this request's connection: it arrived on the ops port — or, shared, on any.</summary>
    private bool Answers(HttpContext context)
    {
        var ops = options.Value.Ops;
        return ops.Port is { } port ? context.Connection.LocalPort == port : ops.SharedPortReason is not null;
    }

    private sealed class Jumps(OpsPortPolicy policy, int ops, int others) : PolicyJumpTable
    {
        public override int GetDestination(HttpContext httpContext)
        {
            return policy.Answers(httpContext) ? ops : others;
        }
    }
}

/// <summary>
///     Once the server listens, and only if ops endpoints are mapped: checks <see cref="OpsOptions" /> against the
///     environment
///     and the server's addresses — throwing (the app doesn't start) where they don't hold, warning where the endpoints
///     are
///     open wider than they should be.
/// </summary>
internal sealed class OpsPortCheck(
    IServiceProvider services,
    IOptions<TaplineOptions> options,
    IHostEnvironment environment,
    ILogger<OpsPortCheck> log) : IHostedLifecycleService
{
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<EndpointDataSource>() is not { } source) return Task.CompletedTask;
        var endpoints = source.Endpoints.Where(e => e.Metadata.GetMetadata<OpsEndpoint>() is not null).ToList();
        if (endpoints.Count == 0) return Task.CompletedTask;
        var ops = options.Value.Ops;
        const string Section = TaplineServiceCollectionExtensions.ConfigurationSection + ":Ops";
        if (ops.Port is not null && ops.SharedPortReason is not null)
            throw new InvalidOperationException(
                $"{Section}: a port of their own (Port) or a shared one (SharedPortReason) — not both.");
        if (ops.SharedPortReason is { } reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException($"{Section}:SharedPortReason: a reason, not a blank.");
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    $"The ops endpoints share the client-facing port (\"{reason}\") in Development only, and this is {environment.EnvironmentName}: " +
                    $"give them a port of their own ({Section}:Port).");
            log.LogWarning(
                "Ops endpoints answer on every port the server listens on, the client-facing one too: {Reason}",
                reason);
            return Task.CompletedTask;
        }

        if (ops.Port is not { } port)
            throw new InvalidOperationException(
                $"The ops endpoints ({endpoints[0].DisplayName}, …) have no port of their own: set {Section}:Port, and listen there " +
                $"(http://127.0.0.1:5899, say) — or, in Development, {Section}:SharedPortReason.");
        var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ??
                        [];
        var on = addresses.Where(a => PortOf(a) == port).ToList();
        if (on.Count == 0)
            throw new InvalidOperationException(
                $"The ops port is {port}, but the server doesn't listen there (it listens on {string.Join(", ", addresses)}): add http://127.0.0.1:{port} to its URLs.");
        if (on.Count == addresses.Count)
            throw new InvalidOperationException(
                $"The server listens on the ops port ({port}) alone, so the app shares it: give the app a port of its own " +
                $"— or, in Development, set {Section}:SharedPortReason instead.");
        var beyond = on.Any(a => !IsLoopback(a));
        var authorized = endpoints.All(e =>
            e.Metadata.GetMetadata<IAuthorizeData>() is not null && e.Metadata.GetMetadata<IAllowAnonymous>() is null);
        if (beyond && !authorized)
            log.LogWarning(
                "Ops endpoints on {Addresses}: reachable from other machines, and they require no authorization — restrict who reaches port {Port}, " +
                "or add RequireAuthorization to them", string.Join(", ", on), port);
        else log.LogInformation("Ops endpoints on {Addresses}", string.Join(", ", on));
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static int PortOf(string address)
    {
        return Uri.TryCreate(Wildcard(address), UriKind.Absolute, out var uri) ? uri.Port : -1;
    }

    /// <summary>
    ///     An address only this machine can reach: localhost, 127.0.0.0/8, ::1 (not *, +, 0.0.0.0, [::], or a host's
    ///     address).
    /// </summary>
    private static bool IsLoopback(string address)
    {
        return Uri.TryCreate(Wildcard(address), UriKind.Absolute, out var uri)
               && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static string Wildcard(string address)
    {
        return address.Replace("://*", "://wildcard").Replace("://+", "://wildcard");
    }
}