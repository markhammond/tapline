using MessagePack;

namespace Tapline.MagicOnion.Providers;

/// <summary>
///     What a router says to a provider in another process, on the link's control stream (<see cref="ITaplineProviderLink" />):
///     who it is, each step 1 and what becomes of its route, health checks, and the answers to the provider's own asks.
/// </summary>
[Union(0, typeof(RouterHello))]
[Union(1, typeof(SubscribeAsked))]
[Union(2, typeof(StepAbandoned))]
[Union(3, typeof(RouteActivated))]
[Union(4, typeof(RouteRemoved))]
[Union(5, typeof(HealthAsked))]
[Union(6, typeof(BeatAnswered))]
[Union(7, typeof(DrainAnswered))]
[Union(8, typeof(AccessAsked))]
[Union(9, typeof(AuthAsked))]
[Union(10, typeof(RouteIdentity))]
[Union(11, typeof(MoveAnswered))]
public abstract class RouterMessage;

/// <summary>
///     What a provider says back: each step 1's answer, its subscribers' mailboxes, its health, and its own heartbeats and
///     drains.
/// </summary>
[Union(0, typeof(StepAnswered))]
[Union(1, typeof(MailRelayed))]
[Union(2, typeof(HealthAnswered))]
[Union(3, typeof(BeatAsked))]
[Union(4, typeof(DrainAsked))]
[Union(5, typeof(AccessAnswered))]
[Union(6, typeof(AuthAnswered))]
[Union(7, typeof(MoveAsked))]
public abstract class ProviderMessage;

/// <summary>
///     The router, first on each control stream: its name and time-ordered id, and the session it streams the provider's
///     topics on (the provider's router's outlet for it).
/// </summary>
[MessagePackObject]
public sealed class RouterHello : RouterMessage
{
    [Key(0)] public string Name = "";
    [Key(1)] public Guid Id;
    [Key(2)] public int Outlet;
    [Key(3)] public uint Nonce;
}

/// <summary>
///     Step 1, on a subscriber's behalf: the request's id at the router (the link's name for its route), and what it asks —
///     with what the provider declared of the service, as the router acted on it, and the subscriber's identity if it is
///     signed in to the service's realm (the router shows a provider no other).
/// </summary>
[MessagePackObject]
public sealed class SubscribeAsked : RouterMessage
{
    [Key(0)] public long Request;
    [Key(1)] public string Service = "";
    [Key(2)] public string? Parameters;
    [Key(3)] public string? AccessRealm;
    [Key(4)] public bool AccessRequired;
    [Key(5)] public IdentityCarried? Identity;
    [Key(6)] public LimitsCarried? Limits; // (none: null; a link that doesn't know the key skips it)
}

/// <summary>
///     The limits a subscriber is under, as step 1 carries them (Tapline's <c>SubscriberLimits</c>). Keys are only ever
///     added: one a side doesn't know is skipped, one it doesn't get reads as zero ("none").
/// </summary>
[MessagePackObject]
public sealed class LimitsCarried
{
    [Key(0)] public long MinIntervalTicks;
    [Key(1)] public long BytesPerSecond;

    public static LimitsCarried? Of(SubscriberLimits limits)
    {
        return limits.IsNone
            ? null
            : new LimitsCarried { MinIntervalTicks = limits.MinInterval.Ticks, BytesPerSecond = limits.BytesPerSecond };
    }

    public SubscriberLimits ToLimits()
    {
        return new SubscriberLimits
        {
            MinInterval = TimeSpan.FromTicks(Math.Max(0, MinIntervalTicks)), BytesPerSecond = Math.Max(0, BytesPerSecond)
        };
    }
}

/// <summary>A subscriber's identity, as step 1 carries it (Tapline's <c>ClientIdentity</c>).</summary>
[MessagePackObject]
public sealed class IdentityCarried
{
    [Key(0)] public string Realm = "";
    [Key(1)] public string UserId = "";
    [Key(2)] public Guid SessionId;
    [Key(3)] public string Module = "";
    [Key(4)] public bool Insecure;
    [Key(5)] public DateTimeOffset AuthenticatedAt;
    [Key(6)] public DateTimeOffset? ExpiresAt;
    [Key(7)] public Dictionary<string, string>? Claims;

    public static IdentityCarried? Of(ClientIdentity? identity)
    {
        return identity is null
            ? null
            : new IdentityCarried
            {
                Realm = identity.Realm, UserId = identity.UserId, SessionId = identity.SessionId,
                Module = identity.Module, Insecure = identity.Insecure, AuthenticatedAt = identity.AuthenticatedAt,
                ExpiresAt = identity.ExpiresAt,
                Claims = identity.Claims is { Count: > 0 } claims ? new Dictionary<string, string>(claims) : null
            };
    }

    public ClientIdentity ToIdentity()
    {
        return new ClientIdentity(Realm, UserId, SessionId, Module, Insecure, AuthenticatedAt, ExpiresAt, Claims);
    }
}

/// <summary>What the provider declares of a service (its realm, whether it requires a sign-in): asked once, cached till the link reconnects.</summary>
[MessagePackObject]
public sealed class AccessAsked : RouterMessage
{
    [Key(0)] public long Ask;
    [Key(1)] public string Service = "";
}

/// <summary>
///     One message of a client's sign-in, relayed to an authentication provider (<see cref="IAuthenticator" />): the
///     router's <see cref="AuthStep" />, the router itself being the link's.
/// </summary>
[MessagePackObject]
public sealed class AuthAsked : RouterMessage
{
    [Key(0)] public long Ask;
    [Key(1)] public Guid Exchange;
    [Key(2)] public string Realm = "";
    [Key(3)] public string Module = "";
    [Key(4)] public int Round;
    [Key(5)] public byte[]? Payload;
    [Key(6)] public byte[]? Context;
    [Key(7)] public IdentityCarried? Current;
    [Key(8)] public bool Renewal;
}

/// <summary>The router's step 1 ended without keeping the request (timed out, cancelled, refused there): a route accepted for it goes.</summary>
[MessagePackObject]
public sealed class StepAbandoned : RouterMessage
{
    [Key(0)] public long Request;
}

/// <summary>Step 2: the subscriber completed its route.</summary>
[MessagePackObject]
public sealed class RouteActivated : RouterMessage
{
    [Key(0)] public long Request;
}

/// <summary>
///     The subscriber's route is gone at the router. <see cref="Quiet" />: gone as the provider isn't told in process —
///     let go by its own drain, or with its instance deemed dead — so its tap goes, and no notice is relayed.
/// </summary>
[MessagePackObject]
public sealed class RouteRemoved : RouterMessage
{
    [Key(0)] public long Request;
    [Key(1)] public bool Quiet;
}

/// <summary>The subscriber's identity changed and its route carries on: renewed, or lapsed (anonymous now).</summary>
[MessagePackObject]
public sealed class RouteIdentity : RouterMessage
{
    [Key(0)] public long Request;
    [Key(1)] public IdentityCarried? Identity;
    [Key(2)] public bool Lapsed;
}

/// <summary>A health check: the instance the router expects, if it knows one.</summary>
[MessagePackObject]
public sealed class HealthAsked : RouterMessage
{
    [Key(0)] public long Probe;
    [Key(1)] public string? ExpectedName;
    [Key(2)] public string? ExpectedEndpoint;
    [Key(3)] public Guid? ExpectedId;
}

/// <summary>The router's answer to the provider's heartbeat — the router as it is now, and whether it knows the instance — or why there's none.</summary>
[MessagePackObject]
public sealed class BeatAnswered : RouterMessage
{
    [Key(0)] public long Beat;
    [Key(1)] public string RouterName = "";
    [Key(2)] public Guid RouterId;
    [Key(3)] public bool KnowsInstance;
    [Key(4)] public string? Error;
}

/// <summary>The router's answer to the provider's drain; or why it failed.</summary>
[MessagePackObject]
public sealed class DrainAnswered : RouterMessage
{
    [Key(0)] public long Drain;
    [Key(1)] public string? Error;
}

/// <summary>The router's answer to the provider's move: how many it moves and the move's correlation id, or why not.</summary>
[MessagePackObject]
public sealed class MoveAnswered : RouterMessage
{
    [Key(0)] public long Move;
    [Key(1)] public int Routes;
    [Key(2)] public Guid Correlation;
    [Key(3)] public string? Refused;
    [Key(4)] public string? Error;
}

/// <summary>How a step 1 ended at the provider.</summary>
public enum StepOutcome : byte
{
    /// <summary>Neither accepted nor refused with a reason: as a provider that doesn't accept, here.</summary>
    None,
    Accepted,
    Refused,
    Nacked,

    /// <summary>The provider threw.</summary>
    Failed
}

/// <summary>
///     A step 1's answer. Accepted: the topic (its id at the provider) and the tap the router streams it on there, how it's
///     laid out, and what the provider's accept said. Refused: why, with the correlation id the provider minted.
/// </summary>
[MessagePackObject]
public sealed class StepAnswered : ProviderMessage
{
    [Key(0)] public long Request;
    [Key(1)] public StepOutcome Outcome;
    [Key(2)] public long Topic;
    [Key(3)] public uint Tap;
    [Key(4)] public string? Kind;
    [Key(5)] public int KeySize;
    [Key(6)] public int RowSize;
    [Key(7)] public string? Name;
    [Key(8)] public byte Initial;
    [Key(9)] public byte? Stale;
    [Key(10)] public byte[]? StalePayload;
    [Key(11)] public byte[]? StaleState;
    [Key(12)] public byte[]? Format;
    [Key(13)] public string? Partition;
    [Key(14)] public string? InstanceName;
    [Key(15)] public string? InstanceEndpoint;
    [Key(16)] public Guid InstanceId;
    [Key(17)] public Guid Correlation;
    [Key(18)] public string? Reason;
    [Key(19)] public byte RefusalKind;
}

/// <summary>What the provider declared of a service, or why it couldn't say.</summary>
[MessagePackObject]
public sealed class AccessAnswered : ProviderMessage
{
    [Key(0)] public long Ask;
    [Key(1)] public string? Realm;
    [Key(2)] public bool Required;
    [Key(3)] public string? Error;
}

/// <summary>
///     An authentication provider's answer to one message of a sign-in (<see cref="AuthAnswer" />): the next message, an
///     identity asserted and its ticket, a refusal — or why it couldn't answer.
/// </summary>
[MessagePackObject]
public sealed class AuthAnswered : ProviderMessage
{
    [Key(0)] public long Ask;
    [Key(1)] public AuthOutcome Outcome;
    [Key(2)] public byte[]? Payload;
    [Key(3)] public IdentityCarried? Identity;
    [Key(4)] public string? Reason;
    [Key(5)] public string? Error;
    [Key(6)] public RenewalPolicy Renewal = RenewalPolicy.Reauthenticate;
    [Key(7)] public TimeSpan? Warning;
    [Key(8)] public string? PolicyReason;
}

/// <summary>What a provider does with a subscriber's mailbox.</summary>
public enum MailOp : byte
{
    Post,
    Rendezvous,
    Send,
    Ready
}

/// <summary>
///     A mailbox operation for a subscriber, after its topic's state at <see cref="Version" /> — the topic's version at the
///     provider, as it was when the operation was called (a rendezvous: the version it names; a snapshot: its as-of; the end
///     of an initial state: its through).
/// </summary>
[MessagePackObject]
public sealed class MailRelayed : ProviderMessage
{
    [Key(0)] public long Request;
    [Key(1)] public MailOp Op;
    [Key(2)] public byte[]? Bytes;
    [Key(3)] public long Version;
}

/// <summary>A health check's answer: the provider's report, or why there's none.</summary>
[MessagePackObject]
public sealed class HealthAnswered : ProviderMessage
{
    [Key(0)] public long Probe;
    [Key(1)] public string? InstanceName;
    [Key(2)] public string? InstanceEndpoint;
    [Key(3)] public Guid InstanceId;
    [Key(4)] public byte Status;
    [Key(5)] public bool RouterRefused;
    [Key(6)] public DateTimeOffset? DrainUntil;
    [Key(7)] public string[]? DegradedPaths;
    [Key(8)] public string? Error;
    [Key(9)] public double? Load;
}

/// <summary>The provider's heartbeat: the instance it is, and the router it expects.</summary>
[MessagePackObject]
public sealed class BeatAsked : ProviderMessage
{
    [Key(0)] public long Beat;
    [Key(1)] public string InstanceName = "";
    [Key(2)] public string InstanceEndpoint = "";
    [Key(3)] public Guid InstanceId;
    [Key(4)] public string ExpectedName = "";
    [Key(5)] public Guid ExpectedId;
}

/// <summary>The provider draining itself, one way: over that long, each client given that grace.</summary>
[MessagePackObject]
public sealed class DrainAsked : ProviderMessage
{
    [Key(0)] public long Drain;
    [Key(1)] public string InstanceName = "";
    [Key(2)] public string InstanceEndpoint = "";
    [Key(3)] public Guid InstanceId;
    [Key(4)] public long OverTicks;
    [Key(5)] public long GraceTicks;
}

/// <summary>
///     The provider's move: some of its subscriptions at the router to its service's other instances — a partition's, or
///     one route's (its outlet and tap there) — over a time, each with a grace, and why.
/// </summary>
[MessagePackObject]
public sealed class MoveAsked : ProviderMessage
{
    [Key(0)] public long Move;
    [Key(1)] public string InstanceName = "";
    [Key(2)] public string InstanceEndpoint = "";
    [Key(3)] public Guid InstanceId;
    [Key(4)] public string? Partition;
    [Key(5)] public int? RouteOutlet;
    [Key(6)] public uint RouteTap;
    [Key(7)] public long OverTicks;
    [Key(8)] public long GraceTicks;
    [Key(9)] public string Reason = "";
}
