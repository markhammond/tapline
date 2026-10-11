using System.Buffers.Binary;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Tapline.AspNetCore;

namespace BlazorRealtime;

/// <summary>
///     The circuit sockets' handling. Blazor negotiates permessage-deflate (RFC 7692) for its circuit WebSocket and
///     compresses
///     every message; deflate's per-message overhead makes that the most expensive CPU per byte saved for small messages —
///     the high-rate frames of unconstrained clients — and pure waste for content that doesn't compress, while a
///     constrained
///     client's conflated frames are larger and stay compressed. RFC 7692 lets a sender leave any message uncompressed
///     (RSV1
///     clear), which also leaves both sides' compression history untouched; .NET does this per message with
///     <see cref="WebSocketMessageFlags.DisableCompression" />. Also caps the socket's kernel send backlog when asked to
///     (<c>SO_SNDBUF</c>; on Linux, <c>TCP_NOTSENT_LOWAT</c>).
/// </summary>
internal static class CircuitSockets
{
    /// <summary>
    ///     For circuit WebSocket requests (<c>…/_blazor</c>) only: ensures the WebSocket feature exists — the circuit
    ///     endpoint's own <c>UseWebSockets()</c>, from the app's <c>WebSocketOptions</c>, unless the app's pipeline already
    ///     added one — and decorates it. Blazor's own decorator (which turns compression on) wraps this one, so its settings
    ///     still apply.
    /// </summary>
    public static void Install(IApplicationBuilder app, BlazorRealtimeOptions options)
    {
        app.UseWhen(static ctx => ctx.Request.Path.Value?.EndsWith("/_blazor", StringComparison.Ordinal) == true,
            branch =>
            {
                branch.UseWebSockets();
                branch.Use((ctx, next) =>
                {
                    if (ctx.Features.Get<IHttpWebSocketFeature>() is { IsWebSocketRequest: true } feature)
                        ctx.Features.Set<IHttpWebSocketFeature>(new CircuitSocketFeature(feature, ctx, options));
                    return next(ctx);
                });
            });
    }

    private sealed class CircuitSocketFeature(
        IHttpWebSocketFeature inner,
        HttpContext http,
        BlazorRealtimeOptions options) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => inner.IsWebSocketRequest;

        public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            FrameCompressionAdvice? advice = null;
            if (context.DangerousEnableCompression)
                http.Features.Set(advice = new FrameCompressionAdvice()); // the circuit's link picks it up
            var socket = await inner.AcceptAsync(context).ConfigureAwait(false);
            if (http.Features.Get<IConnectionSocketFeature>()?.Socket is { } tcp)
                SocketBacklog.Cap(tcp, options.CircuitSendBufferBytes, options.CircuitNotSentLowWatermarkBytes);
            return advice is null
                ? socket
                : new ThresholdWebSocket(socket, options.WebSocketCompressionThreshold, advice);
        }
    }
}

/// <summary>
///     Per circuit socket: the frames written with content not worth compressing, by sequence number, so the socket can
///     discount those bytes when deciding whether a message is worth compressing. The protocol records a frame before its
///     bytes enter the pipe; the socket's sender reads frames in the same (sequence) order. Single producer, single
///     consumer.
/// </summary>
internal sealed class FrameCompressionAdvice
{
    // A ring, not a ConcurrentQueue: its TryPeek preserves the segment for observation, so the next enqueue allocates a
    // new one — every frame. Recorded under the connection's write lock, read by its (serialized) sender.
    private const int Capacity = 256; // frames recorded, not yet seen by the socket
    private readonly int[] _bytes = new int[Capacity];
    private readonly long[] _seqs = new long[Capacity];
    private long _head, _tail; // written by the producer and the consumer respectively

    public bool HasPending => Volatile.Read(ref _head) != Volatile.Read(ref _tail);

    /// <summary>
    ///     Full (a socket that hasn't sent whole messages for a while): dropped — the frame compresses as it would
    ///     without advice.
    /// </summary>
    public void Record(long seq, int incompressibleBytes)
    {
        var head = _head;
        if (head - Volatile.Read(ref _tail) >= Capacity) return;
        var i = (int)(head & (Capacity - 1));
        _seqs[i] = seq;
        _bytes[i] = incompressibleBytes;
        Volatile.Write(ref _head, head + 1);
    }

    /// <summary>
    ///     Incompressible bytes of the <c>Tap.Frame</c> messages in <paramref name="message" /> (BlazorPack: varint
    ///     length + body, each).
    /// </summary>
    public int IncompressibleIn(ReadOnlySpan<byte> message)
    {
        var total = 0;
        for (var o = 0; o < message.Length;)
        {
            int length = 0, shift = 0;
            byte b;
            do
            {
                if (o >= message.Length || shift > 28) return total;
                b = message[o++];
                length |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);

            if (length > message.Length - o) return total;
            var body = message.Slice(o, length);
            if (body.Length >= FrameWriter.Prefix.Length + 8 && body.StartsWith(FrameWriter.Prefix))
                total += Take(BinaryPrimitives.ReadInt64BigEndian(body[FrameWriter.Prefix.Length..]));
            o += length;
        }

        return total;
    }

    private int Take(long seq)
    {
        long tail = _tail, head = Volatile.Read(ref _head);
        var bytes = 0;
        for (; tail < head; tail++)
        {
            var i = (int)(tail & (Capacity - 1));
            if (_seqs[i] > seq) break;
            if (_seqs[i] == seq)
            {
                bytes = _bytes[i];
                tail++;
                break;
            } // earlier ones went out in fragmented (compressed) messages
        }

        Volatile.Write(ref _tail, tail);
        return bytes;
    }
}

/// <summary>
///     Delegates to the accepted socket, adding <see cref="WebSocketMessageFlags.DisableCompression" /> to every message
///     sent
///     whole (in one frame) whose compressible bytes — its length less the frames' content declared not compressible — are
///     fewer than the threshold (and at least to one with none). A fragmented message keeps compression throughout (its
///     size
///     isn't known at its first frame, and a message's frames must agree). Sends are serialized by SignalR's transport.
/// </summary>
internal sealed class ThresholdWebSocket(WebSocket inner, int threshold, FrameCompressionAdvice advice) : WebSocket
{
    private readonly int _threshold = Math.Max(threshold, 1);
    private bool _midMessage; // frames of a fragmented message are being sent

    internal WebSocket Inner => inner;

    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
    public override string? CloseStatusDescription => inner.CloseStatusDescription;
    public override WebSocketState State => inner.State;
    public override string? SubProtocol => inner.SubProtocol;

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
        WebSocketMessageFlags messageFlags, CancellationToken cancellationToken)
    {
        var end = (messageFlags & WebSocketMessageFlags.EndOfMessage) != 0;
        if (!_midMessage && end)
        {
            var compressible = buffer.Length - (advice.HasPending ? advice.IncompressibleIn(buffer.Span) : 0);
            if (compressible < _threshold) messageFlags |= WebSocketMessageFlags.DisableCompression;
        }

        _midMessage = !end;
        return inner.SendAsync(buffer, messageType, messageFlags, cancellationToken);
    }

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken)
    {
        return SendAsync(buffer, messageType,
            endOfMessage ? WebSocketMessageFlags.EndOfMessage : WebSocketMessageFlags.None, cancellationToken);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken)
    {
        return SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();
    }

    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        return inner.ReceiveAsync(buffer, cancellationToken);
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        return inner.ReceiveAsync(buffer, cancellationToken);
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        return inner.CloseAsync(closeStatus, statusDescription, cancellationToken);
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        return inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    }

    public override void Abort()
    {
        inner.Abort();
    }

    public override void Dispose()
    {
        inner.Dispose();
    }
}