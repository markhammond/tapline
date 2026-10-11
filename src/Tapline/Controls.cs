namespace Tapline;

/// <summary>
///     The wire's control messages, client to router: named <c>Tap.{Kind}</c> on a Blazor circuit, by this number over
///     MagicOnion. A router says the kinds it takes on every new connection (its RouterChange record's <c>controls</c>,
///     <see cref="ControlKinds" />), so a client never waits on a kind its router doesn't know.
/// </summary>
public enum ControlKind : byte
{
    /// <summary>Step 2 of a subscription: <c>A</c> its tap.</summary>
    Activate,

    /// <summary>Frames received, cumulatively: <c>A</c> the last frame's sequence, <c>B</c> the last heartbeat's echo.</summary>
    Ack,

    /// <summary>Bound again after a reconnect: <c>A</c> the last frame received, <c>B</c> the last heartbeat's echo.</summary>
    Resume,

    /// <summary>A delta failed (a sequence gap, a checksum): <c>A</c> the tap, to send a snapshot of.</summary>
    Resync,

    /// <summary>Ends a subscription: <c>A</c> its tap, <c>B</c> a <see cref="FaultCode" /> (none: unsubscribed).</summary>
    Close,

    /// <summary>An out-of-band record applied: <c>A</c> its tap, <c>B</c> its ticket.</summary>
    Fetched,

    /// <summary>A sign-in's message, its payload (<see cref="AuthMessages" />, at most 16 KB).</summary>
    Auth,

    /// <summary>Is this client up to date on a subscription? <c>A</c> its tap, <c>B</c> the sync id its rendezvous names.</summary>
    Sync
}

/// <summary>Control kinds as a set: a bit each, <c>1 &lt;&lt; kind</c>, as a RouterChange record carries them.</summary>
public static class ControlKinds
{
    /// <summary>Every kind this router takes (<see cref="Outlet.Apply" />).</summary>
    public const uint Supported = (1u << ((int)ControlKind.Sync + 1)) - 1;

    /// <summary>Whether <paramref name="kinds" /> includes <paramref name="kind" />.</summary>
    public static bool Includes(uint kinds, ControlKind kind)
    {
        return (kinds & (1u << (int)kind)) != 0;
    }
}

/// <summary>
///     One control message (<see cref="ControlKind" />), its arguments in order: what a client's connection sends, and
///     what a router applies (<see cref="Outlet.Apply" />). A payload is the sender's, valid only for the call it's
///     passed to: copy it to keep it.
/// </summary>
public readonly struct ControlMessage(ControlKind kind, long a = 0, long b = 0, ReadOnlyMemory<byte> payload = default)
{
    public ControlKind Kind { get; } = kind;
    public long A { get; } = a;
    public long B { get; } = b;
    public ReadOnlyMemory<byte> Payload { get; } = payload;

    public static ControlMessage Activate(uint tapId) => new(ControlKind.Activate, tapId);
    public static ControlMessage Ack(long frameSeq, long heartbeatEcho) => new(ControlKind.Ack, frameSeq, heartbeatEcho);
    public static ControlMessage Resume(long lastFrame, long heartbeatEcho) => new(ControlKind.Resume, lastFrame, heartbeatEcho);
    public static ControlMessage Resync(uint tapId) => new(ControlKind.Resync, tapId);
    public static ControlMessage Close(uint tapId, FaultCode code) => new(ControlKind.Close, tapId, (ushort)code);
    public static ControlMessage Fetched(uint tapId, uint ticket) => new(ControlKind.Fetched, tapId, ticket);
    public static ControlMessage Auth(ReadOnlyMemory<byte> message) => new(ControlKind.Auth, payload: message);
    public static ControlMessage Sync(uint tapId, uint syncId) => new(ControlKind.Sync, tapId, syncId);
}
