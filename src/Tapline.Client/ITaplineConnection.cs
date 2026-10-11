namespace Tapline.Client;

/// <summary>
///     What a <see cref="TaplineClient" /> says to the router, as the transport that carries its frames sends it: over a
///     Blazor circuit, a gRPC stream, anything. The transport calls the client back with what arrives
///     (<see cref="TaplineClient.Bind" />, <see cref="TaplineClient.Receive" />, <see cref="TaplineClient.Disconnected" />)
///     and knows the outlet it is bound to: these messages name none. Each is one of the wire's control messages
///     (<see cref="ControlKind" />), which a router applies by <see cref="Outlet.Apply" />.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             <b>Every kind, passed on.</b> Send each message whatever its kind, one this transport doesn't know too:
///             the router says the kinds it takes, and the client sends no other. So a kind added later reaches the
///             router through a transport written before it.
///         </item>
///         <item>
///             <b>Never blocks, and keeps order.</b> Queue messages and send them in the order they came. An
///             acknowledgement (<see cref="ControlKind.Ack" />) is cumulative: only the newest of those waiting needs
///             sending.
///         </item>
///         <item><b>From any thread, at once.</b> The client calls it from its receiving thread and from its callers'.</item>
///         <item>
///             <b>A payload is the caller's.</b> It's valid only during the call (an <see cref="ControlKind.Auth" />
///             message's buffer may be reused once it returns): copy it to queue it.
///         </item>
///     </list>
/// </remarks>
public interface ITaplineConnection
{
    /// <summary>Sends one control message (<see cref="ControlMessage" />): queued, in order, never blocking.</summary>
    void Send(in ControlMessage message);
}
