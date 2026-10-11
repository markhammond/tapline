using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tapline.AspNetCore;

/// <summary>
///     Sign-ins the router offers from its configuration (<see cref="TaplineOptions.Authentication" />): passthrough
///     profiles, so far. An app registers any other authentication provider in code
///     (<see cref="Publisher.ProvideAuthentication" />).
/// </summary>
public sealed class TaplineAuthenticationOptions
{
    /// <summary>
    ///     Passthrough, by realm — each a realm whose users sign in by saying who they are
    ///     (<see cref="PassthroughAuthenticator" />): <c>"Tapline": { "Authentication": { "Passthrough": { "default": {
    ///     "Enable": "AcceptAnyCredential" } } } }</c>, or from the environment,
    ///     <c>Tapline__Authentication__Passthrough__default__Enable=AcceptAnyCredential</c>. A realm has one authentication
    ///     service (<see cref="Publisher.ProvideAuthentication" />): this one, named <c>passthrough-{realm}</c> in the viewer
    ///     and the logs.
    /// </summary>
    public Dictionary<string, PassthroughProfile> Passthrough { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
///     A realm's passthrough (<see cref="TaplineAuthenticationOptions.Passthrough" />, under the realm's name): on only
///     when <see cref="Enable" /> says <see cref="PassthroughAuthenticator.AcceptAnyCredential" />, its identities insecure
///     wherever they go.
/// </summary>
public sealed class PassthroughProfile
{
    /// <summary>
    ///     <c>AcceptAnyCredential</c>: on. Absent or empty: off (a profile kept, not in force). Anything else: the router
    ///     doesn't start — passthrough is never on by a near miss.
    /// </summary>
    public string? Enable { get; set; }

    /// <summary>How long an identity holds (default 8 hours).</summary>
    public TimeSpan? Lifetime { get; set; }

    /// <summary>How a session may be renewed (default <see cref="RenewalPolicy.Silent" />: by its ticket).</summary>
    public RenewalPolicy? Renewal { get; set; }

    /// <summary>How long before it lapses its client is warned (default: a quarter of its lifetime, at most 5 minutes).</summary>
    public TimeSpan? Warning { get; set; }

    /// <summary>Why, as its client is told with the warning (a fixed session's rule, say).</summary>
    public string? Reason { get; set; }
}

/// <summary>The configured passthrough realms, put in force on the router: checked, each logged.</summary>
internal static class PassthroughProfiles
{
    public const string Section = TaplineServiceCollectionExtensions.ConfigurationSection + ":Authentication:Passthrough";

    public static void Apply(Publisher publisher, TaplineAuthenticationOptions options, ILogger? log)
    {
        foreach (var (realm, profile) in options.Passthrough.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(profile.Enable))
            {
                log?.LogInformation("Passthrough for realm {Realm} is configured but off (no Enable)", realm);
                continue;
            }

            if (!string.Equals(profile.Enable, PassthroughAuthenticator.AcceptAnyCredential, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{Section}:{realm}:Enable is \"{profile.Enable}\": passthrough is enabled by \"{PassthroughAuthenticator.AcceptAnyCredential}\" alone (or left out, for off).");
            var policy = new SessionPolicy(profile.Renewal ?? RenewalPolicy.Silent, profile.Warning, profile.Reason);
            try
            {
                publisher.ProvidePassthrough(realm, profile.Enable, profile.Lifetime, policy);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException($"{Section}:{realm}: {e.Message}", e);
            }

            log?.LogWarning(
                "Passthrough in force for realm {Realm}: any user id is taken as given, its identities marked insecure; renewal {Renewal}",
                realm, policy.Renewal);
        }
    }
}

/// <summary>Puts the configured sign-ins in force as the app starts (the router made then): a bad one stops the start.</summary>
internal sealed class SignInStartup(IServiceProvider services, IOptions<TaplineOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Authentication.Passthrough.Count > 0)
            _ = services.GetService(typeof(Publisher)); // (its factory applies them)
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
