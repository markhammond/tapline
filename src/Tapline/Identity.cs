using System.Text;

namespace Tapline;

/// <summary>
///     Who a connection is: a user of a realm, as an authentication module asserted it (docs/provider.md, signing in).
///     A connection (<see cref="Outlet.Identity" />) starts anonymous and has at most one; every subscription it makes
///     through a provider carries it to services of its realm (<see cref="SubscribeRequest.Identity" />), and to no others.
///     The router passes on what the module asserted — <see cref="Module" /> and <see cref="Insecure" /> with it — and
///     each provider governs its own security posture.
/// </summary>
/// <param name="Realm">The identity domain (a tenant, a practice): shown only to services declared in it.</param>
/// <param name="UserId">Who, as the realm knows them.</param>
/// <param name="SessionId">This sign-in (a UUIDv7): logged with what it does, and what a renewal renews.</param>
/// <param name="Module">
///     The authentication module that asserted it — <c>passthrough</c>, <c>passkey</c>, <c>opaque</c>, a
///     host's own.
/// </param>
/// <param name="Insecure">The module says its assertion proves nothing (passthrough): carried wherever the identity goes.</param>
/// <param name="AuthenticatedAt">When it was proved.</param>
/// <param name="ExpiresAt">When it lapses (null: it doesn't); lapsed, the connection is anonymous to services.</param>
/// <param name="Claims">What else the module asserts (roles, a display name), as name–value pairs.</param>
public sealed record ClientIdentity(
    string Realm,
    string UserId,
    Guid SessionId,
    string Module,
    bool Insecure,
    DateTimeOffset AuthenticatedAt,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyDictionary<string, string>? Claims = null)
{
    /// <summary>The most a realm, user id or module may be, in bytes of UTF-8 (they travel to providers in other processes).</summary>
    public const int MaxName = 256;

    public string Realm { get; } = Checked(Realm, nameof(Realm));
    public string UserId { get; } = Checked(UserId, nameof(UserId));
    public string Module { get; } = Checked(Module, nameof(Module));

    /// <summary>The same user of the same realm — a renewal of this identity, not another sign-in.</summary>
    public bool SameUser(ClientIdentity other)
    {
        return string.Equals(Realm, other.Realm, StringComparison.Ordinal) &&
               string.Equals(UserId, other.UserId, StringComparison.Ordinal);
    }

    /// <summary>Lapsed at <paramref name="now" />.</summary>
    public bool ExpiredAt(DateTimeOffset now)
    {
        return ExpiresAt is { } at && at <= now;
    }

    public override string ToString()
    {
        return $"{UserId}@{Realm} ({Module}{(Insecure ? ", insecure" : "")})";
    }

    private static string Checked(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"A {name} can't be empty.", name);
        if (Encoding.UTF8.GetByteCount(value) > MaxName)
            throw new ArgumentException($"A {name} is at most {MaxName} bytes of UTF-8.", name);
        return value;
    }
}

/// <summary>
///     What a provider declares of a service (<see cref="IDataProvider.AccessAsync" />): the realm it serves, and whether
///     it requires a signed-in user of that realm. The router acts on it before step 1: a required service is refused to a
///     connection not signed in to its realm (<see cref="RefusalKind.Unauthenticated" />), the provider not asked; a
///     service in a realm receives the identity of a connection signed in to it (<see cref="SubscribeRequest.Identity" />)
///     and none other; an open service (<see cref="Open" />: no realm) receives no identity at all.
/// </summary>
public readonly record struct ServiceAccess
{
    private ServiceAccess(string? realm, bool required)
    {
        if (realm is not null && (string.IsNullOrWhiteSpace(realm) || Encoding.UTF8.GetByteCount(realm) > ClientIdentity.MaxName))
            throw new ArgumentException($"A realm is 1 to {ClientIdentity.MaxName} bytes of UTF-8.", nameof(realm));
        Realm = realm;
        Required = required;
    }

    /// <summary>No realm: anyone may subscribe, and no identity is shown to it (the default).</summary>
    public static ServiceAccess Open => default;

    /// <summary>The realm it serves (null: none).</summary>
    public string? Realm { get; }

    /// <summary>Only a connection signed in to <see cref="Realm" /> may subscribe.</summary>
    public bool Required { get; }

    /// <summary>
    ///     A service of <paramref name="realm" /> that anyone may subscribe to: a signed-in user's identity is shown to it
    ///     (to give them more, say), an anonymous subscriber is served as such.
    /// </summary>
    public static ServiceAccess In(string realm)
    {
        return new ServiceAccess(realm, false);
    }

    /// <summary>A service of <paramref name="realm" /> for its signed-in users alone.</summary>
    public static ServiceAccess RequiredIn(string realm)
    {
        return new ServiceAccess(realm, true);
    }

    /// <summary>The identity this service is shown for <paramref name="identity" /> at <paramref name="now" />: its own realm's, unlapsed.</summary>
    public ClientIdentity? Shown(ClientIdentity? identity, DateTimeOffset now)
    {
        return Realm is { } realm && identity is { } id && string.Equals(id.Realm, realm, StringComparison.Ordinal) &&
               !id.ExpiredAt(now)
            ? id
            : null;
    }

    public override string ToString()
    {
        return Realm is null ? "open" : Required ? $"{Realm}, required" : $"{Realm}, optional";
    }
}

/// <summary>Why a subscription was refused, as its client is told (<see cref="SubscriptionRefusal.Kind" />).</summary>
public enum RefusalKind : byte
{
    /// <summary>The provider's reason (its criteria aren't valid, no room…).</summary>
    Refused = 0,

    /// <summary>Not signed in — to the service's realm, or at all: sign in, then subscribe again.</summary>
    Unauthenticated = 1,

    /// <summary>Signed in, and not allowed.</summary>
    Forbidden = 2,

    /// <summary>
    ///     Not this instance: another of the service's may serve it (a writer's alone, say). The router asks the next one it
    ///     hasn't, within the same step 1 — clients never see it — and tells this refusal only if every one says so.
    /// </summary>
    Elsewhere = 3
}

/// <summary>How a session may be renewed before it lapses (<see cref="SessionPolicy" />).</summary>
public enum RenewalPolicy : byte
{
    /// <summary>The client renews it with its ticket when warned; no one is asked.</summary>
    Silent = 0,

    /// <summary>A fresh proof by the same user renews it (a password, a passkey).</summary>
    Reauthenticate = 1,

    /// <summary>It ends when it lapses, whatever happens (a compliance rule): a sign-in after is a new session.</summary>
    Fixed = 2
}

/// <summary>
///     What an authentication provider says of a session it signs in (<see cref="AuthAnswer.Accept" />), or a host of one
///     it sets (<see cref="Outlet.SetIdentity" />): how it may be renewed, how long before it lapses its client is warned,
///     and why (a fixed session's reason, told with the warning).
/// </summary>
/// <param name="Renewal">How it may be renewed (default: by a fresh proof).</param>
/// <param name="Warning">
///     How long before <see cref="ClientIdentity.ExpiresAt" /> its client is warned (null: a quarter of its lifetime,
///     at most 5 minutes).
/// </param>
/// <param name="Reason">Why, as its client is told (a fixed session's compliance rule, say).</param>
public sealed record SessionPolicy(
    RenewalPolicy Renewal = RenewalPolicy.Reauthenticate,
    TimeSpan? Warning = null,
    string? Reason = null)
{
    /// <summary>Renewed by a fresh proof; warned a quarter of its lifetime ahead, at most 5 minutes.</summary>
    public static readonly SessionPolicy Default = new();

    /// <summary>When <paramref name="identity" />'s client is warned (null: it doesn't lapse).</summary>
    public DateTimeOffset? WarnAt(ClientIdentity identity)
    {
        if (identity.ExpiresAt is not { } at) return null;
        var lead = Warning ?? TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(5).Ticks,
            Math.Max(0, (at - identity.AuthenticatedAt).Ticks / 4)));
        return at - (lead < TimeSpan.Zero ? TimeSpan.Zero : lead);
    }
}
