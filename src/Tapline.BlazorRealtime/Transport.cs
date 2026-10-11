using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Tapline;

namespace BlazorRealtime;

/// <summary>Per-connection state seen by the lifetime manager and the protocol.</summary>
public sealed class CircuitLink(HubConnectionContext connection)
{
    internal FrameCompressionAdvice? Advice; // the circuit socket's, when it compresses per message
    internal long Enqueued, Written; // Blazor invocations queued vs. serialized (Written only under the write lock)
    internal CircuitTransport? Transport;
    public HubConnectionContext Connection { get; } = connection;
    internal bool BlazorBehind => Volatile.Read(ref Enqueued) > Volatile.Read(ref Written);
}

/// <summary>Blazor's own invocation, tagged so the protocol can count it when it is serialized.</summary>
internal sealed class TaggedInvocation(string target, object?[] args, CircuitLink link)
    : InvocationMessage(target, args)
{
    public readonly CircuitLink Link = link;
}

/// <summary>Placeholder holding a FIFO slot; the frame is bound when the protocol serializes it. One per attachment.</summary>
internal sealed class ShimMessage(Outlet outlet, CircuitLink link) : InvocationMessage(BindRegistry.FrameTarget, [])
{
    public readonly CircuitLink Link = link;
    public readonly Outlet Outlet = outlet;
}

internal static class CircuitHubPolicy
{
    /// <summary>Which hubs carry circuits. Overridable for tests before the first lifetime manager is created.</summary>
    internal static Func<Type, bool> IsCircuitHub =
        static t => t.FullName == "Microsoft.AspNetCore.Components.Server.ComponentHub";
}

/// <summary>
///     Decorates the default lifetime manager. Blazor's circuit traffic passes through untouched (same order, same
///     allocation), merely counted; a circuit's transport binds to its connection when its secret token is seen going out
///     on it.
/// </summary>
public sealed class CircuitLifetimeManager<THub>(DefaultHubLifetimeManager<THub> inner, BindRegistry binds)
    : HubLifetimeManager<THub> where THub : Hub
{
    private static readonly bool Tapped = CircuitHubPolicy.IsCircuitHub(typeof(THub));
    private readonly ConcurrentDictionary<string, CircuitLink> _links = new();

    public override Task OnConnectedAsync(HubConnectionContext connection)
    {
        if (Tapped)
            _links[connection.ConnectionId] = new CircuitLink(connection)
                { Advice = connection.GetHttpContext()?.Features.Get<FrameCompressionAdvice>() };
        return inner.OnConnectedAsync(connection);
    }

    public override Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        if (_links.TryRemove(connection.ConnectionId, out var link)) link.Transport?.Detach(link);
        return inner.OnDisconnectedAsync(connection);
    }

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args,
        CancellationToken cancellationToken = default)
    {
        if (!_links.TryGetValue(connectionId, out var link))
            return inner.SendConnectionAsync(connectionId, methodName, args, cancellationToken);

        if (methodName == "JS.BeginInvokeJS" && binds.Match(args) is { } transport)
        {
            link.Transport = transport;
            _ = transport.AttachAsync(link);
        }

        Interlocked.Increment(ref link.Enqueued);
        return link.Connection.WriteAsync(new TaggedInvocation(methodName, args, link), cancellationToken).AsTask();
    }

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        return inner.SendAllAsync(methodName, args, cancellationToken);
    }

    public override Task SendAllExceptAsync(string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
    {
        return inner.SendAllExceptAsync(methodName, args, excludedConnectionIds, cancellationToken);
    }

    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args,
        CancellationToken cancellationToken = default)
    {
        return inner.SendConnectionsAsync(connectionIds, methodName, args, cancellationToken);
    }

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args,
        CancellationToken cancellationToken = default)
    {
        return inner.SendGroupAsync(groupName, methodName, args, cancellationToken);
    }

    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args,
        CancellationToken cancellationToken = default)
    {
        return inner.SendGroupsAsync(groupNames, methodName, args, cancellationToken);
    }

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
    {
        return inner.SendGroupExceptAsync(groupName, methodName, args, excludedConnectionIds, cancellationToken);
    }

    public override Task SendUserAsync(string userId, string methodName, object?[] args,
        CancellationToken cancellationToken = default)
    {
        return inner.SendUserAsync(userId, methodName, args, cancellationToken);
    }

    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args,
        CancellationToken cancellationToken = default)
    {
        return inner.SendUsersAsync(userIds, methodName, args, cancellationToken);
    }

    public override Task AddToGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = default)
    {
        return inner.AddToGroupAsync(connectionId, groupName, cancellationToken);
    }

    public override Task RemoveFromGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = default)
    {
        return inner.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);
    }

    public override Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object?[] args,
        CancellationToken cancellationToken)
    {
        return inner.InvokeConnectionAsync<T>(connectionId, methodName, args, cancellationToken);
    }

    public override Task SetConnectionResultAsync(string connectionId, CompletionMessage result)
    {
        return inner.SetConnectionResultAsync(connectionId, result);
    }

    public override bool TryGetReturnType(string invocationId, [NotNullWhen(true)] out Type? type)
    {
        return inner.TryGetReturnType(invocationId, out type);
    }
}

/// <summary>
///     Wraps Blazor's protocol. Outbound: fills shims at the head of the FIFO and counts Blazor's own invocations.
///     Inbound: consumes Tap.Ack / Activate / Resync / Close in place so they never reach the hub.
/// </summary>
internal sealed class RealtimeProtocol(IHubProtocol inner, OutletRegistry outlets) : IHubProtocol
{
    public string Name => inner.Name;
    public int Version => inner.Version;
    public TransferFormat TransferFormat => inner.TransferFormat;

    public bool IsVersionSupported(int version)
    {
        return inner.IsVersionSupported(version);
    }

    public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
    {
        switch (message)
        {
            case ShimMessage shim:
                if (shim.Outlet.TryBuildFrame(shim, shim.Link.BlazorBehind, out var frame, out var seq,
                        out var incompressible))
                    try
                    {
                        if (incompressible > 0)
                            shim.Link.Advice?.Record(seq, incompressible); // before the bytes can reach the socket
                        FrameWriter.Write(output, seq, frame); // late-bound; otherwise zero bytes on the wire
                    }
                    finally
                    {
                        shim.Outlet.ReleaseFrame(); // copied: its buffer may go back (now, if the circuit has ended)
                    }

                return;
            case TaggedInvocation tagged:
                Volatile.Write(ref tagged.Link.Written, tagged.Link.Written + 1);
                break;
        }

        inner.WriteMessage(message, output);
    }

    public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
    {
        return message is ShimMessage
            ? throw new InvalidOperationException(
                "Shims are per-connection only; pre-serialized paths would fill them eagerly.")
            : inner.GetMessageBytes(message);
    }

    public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder,
        [NotNullWhen(true)] out HubMessage? message)
    {
        var consumed = false;
        while (ControlParser.TryConsume(ref input, outlets)) consumed = true;
        if (inner.TryParseMessage(ref input, binder, out message)) return true;
        message = consumed ? PingMessage.Instance : null; // makes the connection handler commit the consumed bytes
        return consumed;
    }
}

/// <summary>Circuit transports by secret bind token (server-verified binding of a circuit to its connection).</summary>
public sealed class BindRegistry
{
    public const string FrameTarget = "Tap.Frame";
    internal const string Marker = "tap-bind:";
    private static readonly int TokenLength = Marker.Length + 32;

    private readonly ConcurrentDictionary<string, CircuitTransport> _byToken = new();

    internal string Register(CircuitTransport transport)
    {
        var token = Marker + Guid.NewGuid().ToString("N");
        _byToken[token] = transport;
        return token;
    }

    internal void Unregister(string token)
    {
        _byToken.TryRemove(token, out _);
    }

    internal CircuitTransport? Match(object?[] args)
    {
        foreach (var arg in args)
        {
            if (arg is not string s) continue;
            var i = s.IndexOf(Marker, StringComparison.Ordinal);
            if (i >= 0 && s.Length >= i + TokenLength &&
                _byToken.TryGetValue(s.Substring(i, TokenLength), out var transport))
                return transport;
        }

        return null;
    }
}

/// <summary>
///     Per-circuit glue between the circuit's <see cref="Outlet" /> and its current SignalR connection: keeps at most one
///     shim staged in the connection's FIFO, re-staged after each flush while the outlet is ready.
/// </summary>
public sealed class CircuitTransport : IDisposable
{
    private readonly SemaphoreSlim _attachGate = new(1, 1);
    private readonly BindRegistry _binds;
    private CancellationTokenSource? _cts;
    private ShimMessage? _current;
    private Task _pump = Task.CompletedTask;

    public CircuitTransport(Outlet outlet, BindRegistry binds)
    {
        Outlet = outlet;
        _binds = binds;
        BindToken = binds.Register(this);
    }

    public Outlet Outlet { get; }
    internal string BindToken { get; }

    public void Dispose()
    {
        _cts?.Cancel();
        _binds.Unregister(BindToken);
    }

    internal async Task AttachAsync(CircuitLink link)
    {
        await _attachGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _cts?.Cancel();
            await _pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var shim = new ShimMessage(Outlet, link);
            Outlet.Attach(shim); // every tap restarts from a snapshot; old shims are refused
            var http = link.Connection
                .GetHttpContext(); // the session, for the viewer: where from, with what, since when
            var remote = http?.Connection.RemoteIpAddress is { } ip
                ? new IPEndPoint(ip, http.Connection.RemotePort).ToString()
                : null;
            Outlet.Session = new OutletSession("Blazor circuit", remote, http?.Request.Headers.UserAgent.ToString(),
                DateTimeOffset.UtcNow, (Outlet.Session?.Connections ?? 0) + 1);
            Outlet.IdentifyFrom(http?.User); // who it is, if the host says (OutletOptions.Identify)
            _current = shim;
            _cts = new CancellationTokenSource();
            _pump = PumpAsync(link, shim, _cts.Token);
        }
        finally
        {
            _attachGate.Release();
        }
    }

    internal void Detach(CircuitLink link)
    {
        if (!ReferenceEquals(_current?.Link, link)) return;
        _cts?.Cancel();
        Outlet.Detach();
        if (Outlet.Session is { } session)
            Outlet.Session = session with { DisconnectedAt = DateTimeOffset.UtcNow }; // (it may reconnect)
    }

    /// <summary>
    ///     Off the context that attached it: the attach starts inside the circuit's renderer (the bind token goes out on a JS
    ///     interop call), and a pump resuming there would post every wake-up through Blazor's synchronization context — an
    ///     allocation each, and frames queued behind rendering. So it hops to the thread pool and never captures a context.
    /// </summary>
    private async Task PumpAsync(CircuitLink link, ShimMessage shim, CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        using var registration = ct.UnsafeRegister(static o => ((Outlet)o!).Interrupt(), Outlet);
        try
        {
            while (!ct.IsCancellationRequested && !link.Connection.ConnectionAborted.IsCancellationRequested)
            {
                if (!Outlet.IsReady)
                {
                    await Outlet.WaitAsync().ConfigureAwait(false);
                    continue;
                }

                await link.Connection.WriteAsync(shim, ct)
                    .ConfigureAwait(false); // reserve a FIFO slot; the frame is bound at the head
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}