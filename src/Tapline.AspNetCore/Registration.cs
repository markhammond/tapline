using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tapline.AspNetCore;

/// <summary>
///     The router's own options, whatever its transports: its name, its heartbeats, where its ops endpoints answer, and
///     injected outages for test connections. A transport has options of its own, for its clients' outlets (an
///     <see cref="OutletOptions" />: Tapline.BlazorRealtime's <c>BlazorRealtimeOptions</c>, for circuits).
/// </summary>
public sealed class TaplineOptions
{
    /// <summary>
    ///     Where the ops endpoints answer — the router's viewer, and any marked <c>RequireOpsPort()</c>: a port of their own,
    ///     or (Development only, for a stated reason) every port. Required once any is mapped (<see cref="OpsOptions" />).
    /// </summary>
    public OpsOptions Ops { get; set; } = new();

    /// <summary>
    ///     How often every client's outlet sends its heartbeat — a record in its own frames, echoed in the client's
    ///     acknowledgement — so neither direction of a live connection is ever quiet for longer, and the transport's timeouts
    ///     notice a dead one in seconds (Tapline.BlazorRealtime: SignalR's, on the circuit hub, and the browser's server timeout its
    ///     client sets).
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     This server's name as a router, stable across restarts (its id, a UUIDv7, is new each start): what data providers
    ///     know it by (<see cref="RouterInstance" />). Like every option, it can come from configuration — the
    ///     <c>Tapline</c> section: <c>"Tapline": { "RouterName": "edge-a" }</c>, or the environment,
    ///     <c>Tapline__RouterName=edge-a</c> — what's set in code winning. Null (default): this host's name and the port
    ///     it's configured to listen on (<see cref="RouterNames.Default" />). A <see cref="RouterInstance" /> registered as a
    ///     service takes precedence over all of these (name and id both: from an orchestrator, or for tests).
    /// </summary>
    public string? RouterName { get; set; }

    /// <summary>
    ///     Injected outages for test connections — those opened under <see cref="FaultInjectionOptions.Path" /> (e.g.
    ///     <c>/test</c>: for Tapline.BlazorRealtime, circuits opened on a page there) — to try error handling with full-fidelity data,
    ///     leaving production connections and the sources untouched (see <see cref="FaultInjectionService" />). Null
    ///     (default): off.
    /// </summary>
    public FaultInjectionOptions? FaultInjection { get; set; }

    /// <summary>
    ///     The router's own state, as every client's heartbeat carries it: degraded from the host's health checks — ASP.NET
    ///     Core's, reported periodically once the app calls <c>AddHealthChecks()</c> (none: always serving) — damped against
    ///     flapping; draining as the host stops, with a grace for its clients to move.
    /// </summary>
    public RouterHealthOptions Health { get; set; } = new();

    /// <summary>
    ///     Sign-ins offered from configuration: passthrough profiles, each a realm whose users say who they are — on only
    ///     with <c>"Enable": "AcceptAnyCredential"</c>, logged at startup, shown on the viewer's router page
    ///     (<see cref="TaplineAuthenticationOptions" />).
    /// </summary>
    public TaplineAuthenticationOptions Authentication { get; set; } = new();
}

public static class TaplineServiceCollectionExtensions
{
    /// <summary>
    ///     The configuration section <see cref="TaplineOptions" /> are read from (before the code's own settings, which
    ///     win).
    /// </summary>
    public const string ConfigurationSection = "Tapline";

    /// <summary>
    ///     Registers the router: its <see cref="OutletRegistry" /> and <see cref="Publisher" />, its identity, the heartbeats
    ///     of its clients' outlets and its data providers, its own state (<see cref="TaplineOptions.Health" />: from the
    ///     host's
    ///     health reports, logged as it changes, draining as it stops), injected outages
    ///     (<see cref="TaplineOptions.FaultInjection" />) and the ops port's rule. A transport calls it itself
    ///     (Tapline.BlazorRealtime's <c>AddBlazorRealtime</c>); the app calls it for the
    ///     router's options — before the transport's or after, as often as it likes: registered once, each call's
    ///     <paramref name="configure" /> applied in turn. Options come from configuration (<see cref="ConfigurationSection" />
    ///     ),
    ///     then the code's. The router's identity is a <see cref="RouterInstance" /> service: registered by the app, it is
    ///     used
    ///     as is; else made from <see cref="TaplineOptions.RouterName" />, or <see cref="RouterNames.Default" />.
    /// </summary>
    public static IServiceCollection AddTapline(this IServiceCollection services,
        Action<TaplineOptions>? configure = null)
    {
        var options = services.AddOptions<TaplineOptions>();
        if (!services.Any(d => d.ServiceType == typeof(Registered)))
        {
            services.AddSingleton<Registered>();
            services.AddSingleton<IConfigureOptions<TaplineOptions>>(sp => new ConfigureNamedOptions<TaplineOptions>(
                Options.DefaultName,
                o => sp.GetService<IConfiguration>()?.GetSection(ConfigurationSection)
                    .Bind(o))); // configuration first (if there is any)…

            services.AddSingleton<OutletRegistry>();
            services.TryAddSingleton(sp => RouterInstance.Start(
                sp.GetRequiredService<IOptions<TaplineOptions>>().Value.RouterName ??
                RouterNames.Default(sp.GetService<IConfiguration>())));
            services.AddSingleton(sp =>
            {
                var publisher = new Publisher(sp.GetRequiredService<OutletRegistry>())
                    { Router = sp.GetRequiredService<RouterInstance>() };
                PassthroughProfiles.Apply(publisher,
                    sp.GetRequiredService<IOptions<TaplineOptions>>().Value.Authentication,
                    sp.GetService<ILogger<Publisher>>());
                if (sp.GetService<ILogger<Publisher>>() is
                    { } log) // the router's diagnostic lines (each with its correlation id: the needle)
                {
                    publisher.SubscriptionRefused += r =>
                        log.LogWarning(
                            "Subscription refused by {Service} at {Endpoint}: {Reason} (correlation {CorrelationId})",
                            r.Service, r.Endpoint, r.Reason, r.CorrelationId);
                    publisher.ServiceHealthChanged += change => ServiceHealthLog.Write(log, change);
                    publisher.SignIn += (outlet, realm, identity, reason) =>
                    {
                        if (identity is null)
                            log.LogInformation("Sign-in to {Realm} refused for client {Client}: {Reason}", realm,
                                outlet.Serial, reason);
                        else
                            log.LogInformation(
                                "Client {Client} signed in to {Realm} as {User} by {Module}{Insecure} (session {Session})",
                                outlet.Serial, realm, identity.UserId, identity.Module,
                                identity.Insecure ? ", insecure" : "", identity.SessionId);
                    };
                }

                return publisher;
            });
            services.AddHostedService<HeartbeatService>();
            services.AddHostedService<SignInStartup>(); // (a profile misconfigured: the app doesn't start)
            services.AddSingleton(sp => new RouterHealthDamper(sp.GetRequiredService<OutletRegistry>(),
                sp.GetRequiredService<IOptions<TaplineOptions>>().Value.Health,
                sp.GetService<ILogger<RouterHealthDamper>>()));
            services.TryAddEnumerable(ServiceDescriptor
                .Singleton<IHealthCheckPublisher,
                    RouterHealthPublisher>()); // the host's health reports, if it makes any
            services.AddSingleton<RouterDrain>();
            services.AddHostedService<RouterHealthService>(); // logs its changes; drains as the host stops
            services.AddSingleton<FaultInjectionService>();
            services.AddHostedService(sp => sp.GetRequiredService<FaultInjectionService>());
            services.TryAddEnumerable(ServiceDescriptor
                .Singleton<MatcherPolicy, OpsPortPolicy>()); // ops endpoints: on their port only
            services.AddHostedService<OpsPortCheck>(); // …checked once listening
        }

        if (configure is not null) options.Configure(configure); // …then the code's: it wins
        return services;
    }

    /// <summary>Marks the router registered (<see cref="AddTapline" /> registers it once, however often called).</summary>
    private sealed class Registered;
}

/// <summary>Heartbeats every outlet (clients) and, through the publisher, every data provider instance holding routes.</summary>
internal sealed class HeartbeatService(OutletRegistry outlets, Publisher publisher, IOptions<TaplineOptions> options)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                outlets.HeartbeatAll();
                publisher.HeartbeatProviders();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}