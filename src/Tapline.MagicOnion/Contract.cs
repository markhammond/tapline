using MagicOnion;
using MessagePack;
using Tapline;

namespace Tapline.MagicOnion;

/// <summary>
///     One client's session with a router, over MagicOnion: a duplex call per connection — the response stream its frames,
///     the request stream what the client says — and the session outliving it, for the next call to resume. The session is
///     named in headers (<see cref="TaplineStreamHeaders" />): the response's names the session the call is bound to; a
///     request's naming one claims it again. Subscribing (step 1) is the app's own call, naming the session.
/// </summary>
public interface ITaplineStream : IService<ITaplineStream>
{
    /// <summary>Opens the call: frames from the session's outlet, control messages to it.</summary>
    Task<DuplexStreamingResult<Control, Frame>> OpenAsync();
}

/// <summary>A frame: its sequence number, and its records (Tapline's wire format).</summary>
[MessagePackObject]
public struct Frame
{
    [Key(0)] public long Seq;
    [Key(1)] public ReadOnlyMemory<byte> Payload;
}

/// <summary>
///     What a client says: one of the wire's control messages (Tapline's <see cref="ControlKind" />, by its number), its
///     arguments in order.
/// </summary>
[MessagePackObject]
public struct Control
{
    [Key(0)] public ControlKind Kind;
    [Key(1)] public long A;
    [Key(2)] public long B;

    /// <summary>A sign-in's message (<see cref="ControlKind.Auth" />; <see cref="AuthMessages" />), else none.</summary>
    [Key(3)] public byte[]? Payload;
}

/// <summary>The stream's headers: the session a call is bound to, and the client's Streaming cap.</summary>
public static class TaplineStreamHeaders
{
    /// <summary>The session: <c>{outletId}.{nonce}</c> — the response's names it; a request's naming one claims it again.</summary>
    public const string Session = "tapline-session";

    /// <summary>
    ///     A request's cap on its session's Streaming, written as for rsync's <c>--bwlimit</c> (<c>64</c>: KiB/s). It can
    ///     only lower the router's.
    /// </summary>
    public const string BandwidthLimit = "tapline-bwlimit";

    public static string Format(int outletId, uint nonce)
    {
        return $"{outletId}.{nonce}";
    }

    public static bool TryParse(string? value, out int outletId, out uint nonce)
    {
        (outletId, nonce) = (0, 0);
        var dot = value?.IndexOf('.') ?? -1;
        return dot > 0 && int.TryParse(value.AsSpan(0, dot), out outletId) && outletId >= 0 &&
               uint.TryParse(value.AsSpan(dot + 1), out nonce);
    }
}