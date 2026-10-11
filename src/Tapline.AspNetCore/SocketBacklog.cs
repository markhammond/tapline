using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Tapline.AspNetCore;

/// <summary>
///     Opt-in caps on a socket's kernel send backlog, for connections that carry frames: bytes queued there are already
///     serialized, so a shorter backlog keeps the point frames are bound close to when they leave, and a burst delays what
///     follows less. <c>SO_SNDBUF</c> caps the buffer (and throughput, at about it ÷ the round trip: size it to at least
///     the
///     bandwidth × RTT of the clients served); on Linux, <c>TCP_NOTSENT_LOWAT</c> caps the bytes queued but not yet sent,
///     leaving the window free to grow — the finer tool. Elsewhere it's ignored (on macOS it only delays writability).
/// </summary>
public static class SocketBacklog
{
    private const int IpProtoTcp = 6, TcpNotSentLowat = 25; // Linux <netinet/tcp.h>

    /// <summary>
    ///     Caps <paramref name="socket" />'s backlog: 0 leaves either as the OS has it. A socket gone meanwhile is left
    ///     be.
    /// </summary>
    public static void Cap(Socket socket, int sendBufferBytes, int notSentLowWatermarkBytes)
    {
        try
        {
            if (sendBufferBytes > 0) socket.SendBufferSize = sendBufferBytes;
            if (notSentLowWatermarkBytes > 0 && OperatingSystem.IsLinux())
            {
                Span<byte> value = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(value, notSentLowWatermarkBytes);
                socket.SetRawSocketOption(IpProtoTcp, TcpNotSentLowat, value);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    ///     Caps the backlog of every connection on this endpoint — one of its own, that carries frames (a transport's
    ///     port).
    /// </summary>
    public static ListenOptions CapSocketBacklog(this ListenOptions listen, int sendBufferBytes = 0,
        int notSentLowWatermarkBytes = 0)
    {
        listen.Use(next => connection =>
        {
            if (connection.Features.Get<IConnectionSocketFeature>()?.Socket is { } socket)
                Cap(socket, sendBufferBytes, notSentLowWatermarkBytes);
            return next(connection);
        });
        return listen;
    }
}