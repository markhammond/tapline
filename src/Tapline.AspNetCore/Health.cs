using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HostHealthReport = Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport; // not core's (a provider's)

namespace Tapline.AspNetCore;

/// <summary>
///     The router's own state, as every client's heartbeat carries it (<see cref="RouterState" />): degraded from its
///     host's
///     health checks, damped against flapping; draining as the host stops.
/// </summary>
public sealed class RouterHealthOptions
{
    /// <summary>
    ///     How long the host's health checks must fail (unhealthy or degraded), report after report, before the router says
    ///     it's degraded: 10 s by default — said at the first report after that. ASP.NET Core's health check publisher
    ///     (<c>AddHealthChecks()</c>) reports every <c>HealthCheckPublisherOptions.Period</c>, 30 s by default: two failing
    ///     reports in a row, then. Zero: the first failing report.
    /// </summary>
    public TimeSpan DegradeAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How long they must pass before it says it's serving again: 30 s by default — doubled for each further time it
    ///     degraded within <see cref="FlapWindow" />, up to <see cref="MaxRecoverAfter" />. A router that keeps tipping over
    ///     settles degraded, rather than moving its clients back and forth.
    /// </summary>
    public TimeSpan RecoverAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest <see cref="RecoverAfter" /> grows to: 5 min by default.</summary>
    public TimeSpan MaxRecoverAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How far back its degradations count towards a longer <see cref="RecoverAfter" />: 10 min by default.</summary>
    public TimeSpan FlapWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The checks that count, by tag: those with any of these. Null or empty (default): every check the host reports.</summary>
    public string[]? Tags { get; set; }

    /// <summary>
    ///     How long, as the host stops, its clients have to move to another router: each told at once (draining, and the grace
    ///     left, on its heartbeat), the host held until none still connected holds a subscription here, or the grace runs
    ///     out. Zero (default): told, not waited for — they reconnect wherever they will (break before make). Keep it under
    ///     the host's <c>HostOptions.ShutdownTimeout</c> (30 s by default): the server stops only after. A transport that
    ///     closes its connections as the application stops (SignalR's, on Blazor Server: its circuits) has gone by then.
    /// </summary>
    public TimeSpan DrainGrace { get; set; }
}

/// <summary>
///     The router's state from its host's health reports, damped: degraded once they've failed for
///     <see cref="RouterHealthOptions.DegradeAfter" />; serving again once they've passed for
///     <see cref="RouterHealthOptions.RecoverAfter" />, doubled for each further degradation within the flap window. Its
///     reason
///     is the failing checks it degraded with: a change to them while degraded is logged under the same correlation id,
///     not
///     told to the clients again. Draining: nothing more.
/// </summary>
internal sealed class RouterHealthDamper(OutletRegistry registry, RouterHealthOptions options, ILogger? log = null)
{
    private readonly Queue<long> _degradations = new(); // when it degraded (timestamps), within the flap window
    private readonly Lock _gate = new();
    private string _failing = ""; // degraded: the failing checks last logged
    private long _failingSince, _passingSince; // the run of reports against its state, since (0: none)

    /// <summary>
    ///     The recovery time now: <see cref="RouterHealthOptions.RecoverAfter" />, doubled for each degradation in the
    ///     window after the first.
    /// </summary>
    internal TimeSpan RecoverAfter
    {
        get
        {
            var after = options.RecoverAfter;
            for (var i = 1; i < _degradations.Count && after < options.MaxRecoverAfter; i++) after += after;
            return after < options.MaxRecoverAfter ? after : options.MaxRecoverAfter;
        }
    }

    /// <summary>
    ///     A report: the failing checks (that count; empty: none) — names and statuses, in order — and the reason to
    ///     tell.
    /// </summary>
    public RouterHealth Report(string failing, string? reason)
    {
        var time = registry.Wheel.TimeProvider;
        lock (_gate)
        {
            var health = registry.Health;
            var now = time.GetTimestamp();
            while (_degradations.TryPeek(out var at) && time.GetElapsedTime(at, now) > options.FlapWindow)
                _degradations.Dequeue();
            switch (health.State)
            {
                case RouterState.Serving when failing.Length > 0:
                    if (_failingSince == 0) _failingSince = now;
                    if (time.GetElapsedTime(_failingSince, now) < options.DegradeAfter) return health;
                    (_failingSince, _failing) = (0, failing);
                    _degradations.Enqueue(now);
                    return registry.SetHealth(RouterState.Degraded, reason);
                case RouterState.Serving:
                    _failingSince = 0;
                    return health;
                case RouterState.Degraded when failing.Length == 0:
                    if (_passingSince == 0) _passingSince = now;
                    if (time.GetElapsedTime(_passingSince, now) < RecoverAfter) return health;
                    (_passingSince, _failing) = (0, "");
                    return registry.SetHealth(RouterState.Serving);
                case RouterState.Degraded:
                    _passingSince = 0;
                    if (failing != _failing)
                    {
                        _failing = failing;
                        log?.LogWarning("Router still degraded, now: {Reason} (correlation {Correlation})", reason,
                            health.Correlation);
                    }

                    return health;
                default:
                    return health; // draining: one way
            }
        }
    }
}

/// <summary>
///     Hands the host's health reports (ASP.NET Core's health check publisher: <c>AddHealthChecks()</c>) to the
///     router's damper.
/// </summary>
internal sealed class RouterHealthPublisher(RouterHealthDamper damper, IOptions<TaplineOptions> options)
    : IHealthCheckPublisher
{
    public Task PublishAsync(HostHealthReport report, CancellationToken cancellationToken)
    {
        var (failing, reason) = Summarize(report, options.Value.Health.Tags);
        damper.Report(failing, reason);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The failing checks that count: a key (names and statuses, in order) and the reason told (each with its
    ///     description).
    /// </summary>
    internal static (string Failing, string? Reason) Summarize(HostHealthReport report, string[]? tags)
    {
        List<KeyValuePair<string, HealthReportEntry>>? failing = null;
        foreach (var e in report.Entries)
            if (e.Value.Status != HealthStatus.Healthy &&
                (tags is not { Length: > 0 } || e.Value.Tags.Any(tags.Contains)))
                (failing ??= []).Add(e);
        if (failing is null) return ("", null);
        failing.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return (string.Join(";", failing.Select(e => $"{e.Key}={e.Value.Status}")),
            string.Join("; ",
                failing.Select(e =>
                    (e.Value.Description ?? e.Value.Exception?.Message) is { Length: > 0 } d
                        ? $"{e.Key}: {e.Value.Status}, {d}"
                        : $"{e.Key}: {e.Value.Status}")));
    }
}

/// <summary>
///     How a drain went: its correlation id, the clients connected and subscribed as it began, those still so as it
///     ended, and how long it took.
/// </summary>
public sealed record DrainResult(Guid Correlation, int Clients, int Left, TimeSpan Took);

/// <summary>
///     Drains the router — as the host stops (<see cref="RouterHealthOptions.DrainGrace" />), or on request, before it
///     does
///     (<see cref="TaplineDrainExtensions.MapTaplineDrain" />: a container's pre-stop hook, say — so the clients of a
///     transport
///     that closes its connections as the host stops, SignalR's circuits, have the grace to move too). Every client is
///     told
///     at once, draining and the grace; then it waits until no client still connected holds a subscription here, or the
///     grace
///     runs out — logged either way. Draining is one way: the router serves on, telling its clients to move, till it
///     stops.
/// </summary>
public sealed class RouterDrain(OutletRegistry outlets, ILogger<RouterDrain> log)
{
    private DrainResult? _last;
    private int _waiting;

    /// <summary>The last drain's outcome, once over (null: none yet).</summary>
    public DrainResult? Last => Volatile.Read(ref _last);

    /// <summary>A drain is waiting for its clients to move.</summary>
    public bool Waiting => Volatile.Read(ref _waiting) > 0;

    /// <summary>
    ///     Clients still here, and subscribed: those away (their connection gone) can't be told, nor move through this
    ///     router.
    /// </summary>
    public int Subscribed
    {
        get
        {
            var n = 0;
            foreach (var o in outlets.Outlets)
                if (o is { IsAway: false, Taps.Count: > 0 })
                    n++;
            return n;
        }
    }

    /// <summary>
    ///     Drains with <paramref name="grace" /> (already draining: its own grace, as it was set), waiting for the
    ///     clients to move.
    /// </summary>
    public async Task<DrainResult> DrainAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        var health = outlets.SetHealth(RouterState.Draining, grace: grace);
        var by = health.DrainsBy ?? health.At;
        var start = Stopwatch.GetTimestamp();
        int clients = Subscribed, left = clients;
        Interlocked.Increment(ref _waiting);
        try
        {
            while ((left = Subscribed) > 0 && DateTimeOffset.UtcNow < by)
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log.LogWarning(
                "Router drain cut short, with clients still subscribed here: {Clients} (correlation {Correlation})",
                Subscribed, health.Correlation);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        var took = Stopwatch.GetElapsedTime(start);
        if (by <= health.At)
        {
        } // no grace: told, not waited for
        else if (left > 0)
        {
            log.LogWarning(
                "Router drained: its grace ({Grace} s) ran out with clients still subscribed here: {Clients} (correlation {Correlation})",
                (by - health.At).TotalSeconds, left, health.Correlation);
        }
        else if (clients > 0)
        {
            log.LogInformation("Router drained: every client moved, in {Elapsed} ms (correlation {Correlation})",
                (long)took.TotalMilliseconds, health.Correlation);
        }

        var result = new DrainResult(health.Correlation, clients, left, took);
        Volatile.Write(ref _last, result);
        return result;
    }
}

/// <summary>
///     Where the drains answer (<see cref="TaplineDrainExtensions.MapTaplineDrain" />): the viewer offers them, as
///     forms, when they're mapped.
/// </summary>
internal sealed record DrainEndpoints(string Prefix);

public static class TaplineDrainExtensions
{
    /// <summary>
    ///     Maps the drains, on the ops port alone, under <paramref name="prefix" />. <c>POST {prefix}/drain?grace=10s</c>
    ///     drains the router (<see cref="RouterDrain" />), answering once every client has moved, or the grace has run out
    ///     (<see cref="DrainResult" />) — then stop the host. <c>POST {prefix}/nodes/{nodeId}/drain?over=0s&amp;grace=5s</c>
    ///     drains a provider's node on this router's side (<see cref="Publisher.DrainNode" />): its clients moved to the
    ///     service's other nodes. Both are one way. <c>POST {prefix}/nodes/{nodeId}/move?partition=&amp;over=1s&amp;grace=5s&amp;reason=</c>
    ///     moves some of a node's subscriptions (a partition's; none named, all of them) to the service's other nodes —
    ///     a rebalance: the node keeps serving (<see cref="Publisher.MoveNode" />); refused, 409 with why. Durations:
    ///     <c>500ms</c>, <c>10s</c>, <c>1m</c>. As ops endpoints that
    ///     change things, they refuse a browser's cross-site request (
    ///     <see cref="OpsPortEndpointExtensions.RequireOpsPort{TBuilder}" />):
    ///     only this origin's pages (the viewer's forms, which mapping these lights up) and scripts (curl, a container's
    ///     pre-stop hook) drain. A form post naming the page to go back to (<c>back</c>: a path here) starts the drain and
    ///     goes back there, 303.
    /// </summary>
    public static RouteGroupBuilder MapTaplineDrain(this IEndpointRouteBuilder endpoints,
        string prefix = "/ops/tapline")
    {
        var root = prefix.TrimEnd('/');
        var group = endpoints.MapGroup(root);
        group.RequireOpsPort().ExcludeFromDescription().WithMetadata(new DrainEndpoints(root));
        group.MapPost("/drain", async (HttpContext context, RouterDrain drain) =>
        {
            var (form, back) = await FormAsync(context);
            if (Duration(Field(context, form, "grace"), TimeSpan.FromSeconds(10)) is not { } grace)
                return Results.BadRequest("grace: a duration (500ms, 10s, 1m)");
            if (back is null) return Results.Json(await drain.DrainAsync(grace, context.RequestAborted));
            _ = drain.DrainAsync(grace); // its page shows how it goes
            return SeeOther(context, back);
        });
        group.MapPost("/nodes/{nodeId:guid}/drain", async (Guid nodeId, HttpContext context, Publisher router) =>
        {
            var (form, back) = await FormAsync(context);
            if (Duration(Field(context, form, "over"), TimeSpan.Zero) is not { } over ||
                Duration(Field(context, form, "grace"), TimeSpan.FromSeconds(5)) is not { } grace)
                return Results.BadRequest("over, grace: durations (500ms, 10s, 1m)");
            if (router.DrainNode(nodeId, over, grace) is not { } correlation)
                return Results.NotFound($"No live node {nodeId} on this router.");
            return back is null ? Results.Json(new { nodeId, correlation, over, grace }) : SeeOther(context, back);
        });
        group.MapPost("/nodes/{nodeId:guid}/move", async (Guid nodeId, HttpContext context, Publisher router) =>
        {
            var (form, back) = await FormAsync(context);
            if (Duration(Field(context, form, "over"), TimeSpan.FromSeconds(1)) is not { } over ||
                Duration(Field(context, form, "grace"), TimeSpan.FromSeconds(5)) is not { } grace)
                return Results.BadRequest("over, grace: durations (500ms, 10s, 1m)");
            var partition = Field(context, form, "partition") is { Length: > 0 } p ? p : null;
            var reason = Field(context, form, "reason") is { Length: > 0 } r ? r : "by hand, from the ops port";
            var answer = router.MoveNode(nodeId, partition, null, over, grace, reason);
            if (!answer.Accepted)
                return answer.Refused!.StartsWith("No live node", StringComparison.Ordinal)
                    ? Results.NotFound(answer.Refused)
                    : Results.Conflict(answer.Refused);
            return back is null
                ? Results.Json(new { nodeId, answer.Routes, answer.Correlation, partition, over, grace })
                : SeeOther(context, back);
        });
        return group;
    }

    /// <summary>A form's fields, if one was posted, and the page it names to go back to — a path here, never another site.</summary>
    private static async Task<(IFormCollection? Form, string? Back)> FormAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType) return (null, null);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var back = form["back"].ToString();
        return (form,
            back.StartsWith('/') && !back.StartsWith("//", StringComparison.Ordinal) &&
            !back.StartsWith("/\\", StringComparison.Ordinal)
                ? back
                : null);
    }

    private static string? Field(HttpContext context, IFormCollection? form, string name)
    {
        return form?[name].ToString() is { Length: > 0 } posted ? posted : context.Request.Query[name].ToString();
    }

    private static IResult SeeOther(HttpContext context, string back)
    {
        context.Response.Headers.Location = back;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    /// <summary>
    ///     A duration — <c>500ms</c>, <c>10s</c>, <c>1m</c>, or seconds — of at most an hour; none given,
    ///     <paramref name="fallback" />.
    /// </summary>
    private static TimeSpan? Duration(string? text, TimeSpan fallback)
    {
        if (string.IsNullOrEmpty(text)) return fallback;
        var (number, unit) = text.EndsWith("ms", StringComparison.Ordinal)
            ? (text[..^2], 0.001)
            : text[^1] switch
            {
                's' => (text[..^1], 1.0), 'm' => (text[..^1], 60.0), _ => (text, 1.0)
            };
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0 &&
               n * unit <= 3600
            ? TimeSpan.FromSeconds(n * unit)
            : null;
    }
}

/// <summary>
///     Logs the router's state as it changes, each change with its correlation id — the needle its clients carry too; its
///     first state as it starts. And drains the router as the host stops (<see cref="RouterHealthOptions.DrainGrace" />).
/// </summary>
internal sealed class RouterHealthService(
    OutletRegistry outlets,
    RouterDrain drain,
    IOptions<TaplineOptions> options,
    ILogger<RouterHealthService> log) : IHostedLifecycleService
{
    private readonly Lock _gate = new();
    private RouterState _last;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            outlets.HealthChanged += Changed;
            Log(outlets.Health, true);
        }

        return Task.CompletedTask;
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await drain.DrainAsync(options.Value.Health.DrainGrace, cancellationToken);
        } // (drained on request before: as it was)
        catch (OperationCanceledException)
        {
        } // the host's shutdown timeout: logged
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        outlets.HealthChanged -= Changed;
        return Task.CompletedTask;
    }

    private void Changed(RouterHealth health)
    {
        lock (_gate)
        {
            Log(health, false);
        }
    }

    private void Log(RouterHealth health, bool first)
    {
        switch (health.State)
        {
            case RouterState.Degraded:
                log.LogWarning("Router degraded: {Reason} (correlation {Correlation})", health.Reason,
                    health.Correlation);
                break;
            case RouterState.Draining when health.DrainsBy > health.At:
                log.LogInformation("Router draining: its clients have {Grace} s to move (correlation {Correlation})",
                    (health.DrainsBy.Value - health.At).TotalSeconds, health.Correlation);
                break;
            case RouterState.Draining:
                log.LogInformation(
                    "Router draining as it stops: its clients told, not waited for (correlation {Correlation})",
                    health.Correlation);
                break;
            default:
                log.LogInformation(
                    first || _last == RouterState.Serving
                        ? "Router serving (correlation {Correlation})"
                        : "Router serving again (correlation {Correlation})",
                    health.Correlation);
                break;
        }

        _last = health.State;
    }
}

/// <summary>
///     The router's log line for each change in a provider's health its clients are told (
///     <see cref="Publisher.ServiceHealthChanged" />),
///     with the change's correlation id: down and degraded as warnings, recovered, draining and up as information, a
///     drain's
///     let-go as debug (one a client).
/// </summary>
internal static class ServiceHealthLog
{
    public static void Write(ILogger log, ServiceHealthChange c)
    {
        var where = c.Path is null ? "" :
            c.Scope is { } scope && scope != c.Path ? $", {c.Path} (to its clients {scope})" : $", {c.Path}";
        switch (c.Event)
        {
            case ServiceHealthEvent.Up:
                log.LogInformation(
                    "Service {Service} up: {Reason}; {Clients} waiting clients told (correlation {Correlation})",
                    c.Path, c.Reason, c.Clients, c.Correlation);
                break;
            case ServiceHealthEvent.Down:
                log.LogWarning(
                    "Provider node {NodeId} ({Instance}) down: {Reason}; {Clients} clients told (correlation {Correlation})",
                    c.NodeId, c.Instance, c.Reason, c.Clients, c.Correlation);
                break;
            case ServiceHealthEvent.Degraded:
                log.LogWarning(
                    "Provider node {NodeId} ({Instance}) degraded{Where}: {Reason}; {Clients} clients told (correlation {Correlation})",
                    c.NodeId, c.Instance, where, c.Reason, c.Clients, c.Correlation);
                break;
            case ServiceHealthEvent.Recovered:
                log.LogInformation(
                    "Provider node {NodeId} ({Instance}) recovered{Where}: {Reason}; {Clients} clients told (correlation {Correlation})",
                    c.NodeId, c.Instance, where, c.Reason, c.Clients, c.Correlation);
                break;
            case ServiceHealthEvent.Draining:
                log.LogInformation(
                    "Provider node {NodeId} ({Instance}) draining: {Reason}, each given {Grace} s; {Clients} clients (correlation {Correlation})",
                    c.NodeId, c.Instance, c.Reason, c.Grace.TotalSeconds, c.Clients, c.Correlation);
                break;
            default:
                log.LogDebug(
                    "Provider node {NodeId} ({Instance}) drained a client: {Reason} (correlation {Correlation})",
                    c.NodeId, c.Instance, c.Reason, c.Correlation);
                break;
        }
    }
}