using System.Buffers;
using System.Buffers.Binary;
using Tapline;

namespace BlazorRealtime;

/// <summary>Writes a SignalR MessagePack invocation <c>Tap.Frame(seq, bin)</c> straight into the pipe.</summary>
internal static class FrameWriter
{
    // varint(len) [1, {}, nil, "Tap.Frame", [int64 seq, bin32 payload], []]
    private const int Header = 29;

    /// <summary>A frame message's body up to its sequence number: array(6), Invocation, {}, nil, "Tap.Frame", array(2), int64.</summary>
    internal static ReadOnlySpan<byte> Prefix =>
    [
        0x96, 0x01, 0x80, 0xC0, 0xA9, (byte)'T', (byte)'a', (byte)'p', (byte)'.', (byte)'F', (byte)'r', (byte)'a',
        (byte)'m', (byte)'e', 0x92, 0xD3
    ];

    public static void Write(IBufferWriter<byte> w, long seq, ReadOnlySpan<byte> payload)
    {
        var body = Header + payload.Length + 1;
        var s = w.GetSpan(5 + body);
        var o = 0;
        var v = (uint)body;
        while (v >= 0x80)
        {
            s[o++] = (byte)(v | 0x80);
            v >>= 7;
        }

        s[o++] = (byte)v;
        Prefix.CopyTo(s[o..]);
        o += Prefix.Length; // array(6), Invocation, {}, nil, "Tap.Frame", arguments: array(2), int64
        BinaryPrimitives.WriteInt64BigEndian(s[o..], seq);
        o += 8;
        s[o++] = 0xC6;
        BinaryPrimitives.WriteUInt32BigEndian(s[o..], (uint)payload.Length);
        o += 4;
        payload.CopyTo(s[o..]);
        o += payload.Length;
        s[o++] = 0x90; // streamIds: []
        w.Advance(o);
    }
}

/// <summary>
///     Parses client control invocations in place (zero allocation) before Blazor's hub sees them:
///     <c>Tap.Ack(outletId, nonce, frameSeq, hbSeq)</c>, <c>Tap.Resume(outletId, nonce, lastFrame, hbSeq)</c>,
///     <c>Tap.Activate(outletId, nonce, tapId)</c>, <c>Tap.Resync(outletId, nonce, tapId)</c>,
///     <c>Tap.Close(outletId, nonce, tapId, faultCode)</c>, <c>Tap.Fetched(outletId, nonce, kind, id, ticket)</c> and
///     <c>Tap.Sync(outletId, nonce, tapId, syncId)</c> —
///     and a sign-in's message, <c>Tap.Auth(outletId, nonce, bin)</c> (<see cref="AuthMessages" />; larger, and rare).
/// </summary>
internal static class ControlParser
{
    private const int MaxControl = 96, MaxAuth = AuthMessages.MaxMessage + 32;

    public static bool TryConsume(ref ReadOnlySequence<byte> input, OutletRegistry outlets)
    {
        var r = new SequenceReader<byte>(input);
        int len = 0, shift = 0;
        byte b;
        do
        {
            if (shift > 28 || !r.TryRead(out b)) return false;
            len |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (len < 8 || r.Remaining < len) return false;
        if (len > MaxControl)
        {
            if (len > MaxAuth) return false;
            var auth = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                r.TryCopyTo(auth.AsSpan(0, len));
                if (!TryAuth(auth.AsSpan(0, len), outlets)) return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(auth);
            }

            r.Advance(len);
            input = input.Slice(r.Position);
            return true;
        }

        Span<byte> s = stackalloc byte[MaxControl];
        s = s[..len];
        r.TryCopyTo(s);

        // [1, {}, nil, target, [ints...]] — the JS client's 5-element form; a 6th empty streamIds array is tolerated.
        var arr = s[0];
        if ((arr != 0x95 && arr != 0x96) || s[1] != 0x01 || s[2] != 0x80 || s[3] != 0xC0) return false;
        var tl = s[4] - 0xA0;
        if (tl is < 0 or > 31 || 6 + tl > len) return false;
        var target = s.Slice(5, tl);
        if (target.SequenceEqual("Tap.Auth"u8))
        {
            if (!TryAuth(s, outlets)) return false;
            r.Advance(len);
            input = input.Slice(r.Position);
            return true;
        }

        var o = 5 + tl;
        var argc = s[o++] - 0x90;
        Span<long> a = stackalloc long[5];
        if (argc < 0 || argc > a.Length) return false;
        for (var i = 0; i < argc; i++)
            if (!TryInt(s, ref o, out a[i]))
                return false;
        if (arr == 0x96 && (o >= len || s[o++] != 0x90)) return false;
        if (o != len) return false;

        if (argc == 4 && target.SequenceEqual("Tap.Ack"u8)) outlets.Find(a[0], a[1])?.Ack(a[2], a[3]);
        else if (argc == 4 && target.SequenceEqual("Tap.Resume"u8))
            outlets.Find(a[0], a[1])?.Resume(a[2], a[3]); // binding again: the last frame it received
        else if (argc == 3 && target.SequenceEqual("Tap.Activate"u8)) outlets.Find(a[0], a[1])?.Activate((uint)a[2]);
        else if (argc == 3 && target.SequenceEqual("Tap.Resync"u8)) outlets.Find(a[0], a[1])?.Resync((uint)a[2]);
        else if (argc == 4 && target.SequenceEqual("Tap.Close"u8))
            outlets.Find(a[0], a[1])?.Close((uint)a[2], (FaultCode)(ushort)a[3]);
        else if (argc == 5 && target.SequenceEqual("Tap.Fetched"u8))
            outlets.Find(a[0], a[1])?.Fetched((int)a[2], (uint)a[3], (uint)a[4]);
        else if (argc == 4 && target.SequenceEqual("Tap.Sync"u8)) outlets.Find(a[0], a[1])?.Sync((uint)a[2], (uint)a[3]);
        else return false;

        r.Advance(len);
        input = input.Slice(r.Position);
        return true;
    }

    /// <summary><c>[1, {}, nil, "Tap.Auth", [outletId, nonce, bin]]</c> (a 6th empty array tolerated): relayed to the outlet.</summary>
    private static bool TryAuth(ReadOnlySpan<byte> s, OutletRegistry outlets)
    {
        var arr = s[0];
        if ((arr != 0x95 && arr != 0x96) || s[1] != 0x01 || s[2] != 0x80 || s[3] != 0xC0) return false;
        if (s[4] != 0xA0 + 8 || s.Length < 14 || !s.Slice(5, 8).SequenceEqual("Tap.Auth"u8) || s[13] != 0x93)
            return false;
        var o = 14;
        if (!TryInt(s, ref o, out var outletId) || !TryInt(s, ref o, out var nonce) || o >= s.Length) return false;
        var t = s[o++];
        var n = t switch { 0xC4 => 1, 0xC5 => 2, 0xC6 => 4, _ => 0 };
        if (n == 0 || o + n > s.Length) return false;
        var length = n switch
        {
            1 => s[o],
            2 => BinaryPrimitives.ReadUInt16BigEndian(s[o..]),
            _ => (long)BinaryPrimitives.ReadUInt32BigEndian(s[o..])
        };
        o += n;
        if (length > s.Length - o) return false;
        var message = s.Slice(o, (int)length);
        o += (int)length;
        if (arr == 0x96 && (o >= s.Length || s[o++] != 0x90)) return false;
        if (o != s.Length) return false;
        outlets.Find(outletId, nonce)?.Auth(message);
        return true;
    }

    private static bool TryInt(ReadOnlySpan<byte> s, ref int o, out long v)
    {
        v = 0;
        if (o >= s.Length) return false;
        var t = s[o++];
        if (t <= 0x7F)
        {
            v = t;
            return true;
        }

        if (t >= 0xE0)
        {
            v = (sbyte)t;
            return true;
        } // a negative fixint (−32 … −1)

        var n = t switch { 0xCC or 0xD0 => 1, 0xCD or 0xD1 => 2, 0xCE or 0xD2 => 4, 0xCF or 0xD3 => 8, _ => 0 };
        if (n == 0 || o + n > s.Length) return false;
        var p = s.Slice(o, n);
        o += n;
        v = t switch
        {
            0xCC => p[0],
            0xD0 => (sbyte)p[0],
            0xCD => BinaryPrimitives.ReadUInt16BigEndian(p),
            0xD1 => BinaryPrimitives.ReadInt16BigEndian(p),
            0xCE => BinaryPrimitives.ReadUInt32BigEndian(p),
            0xD2 => BinaryPrimitives.ReadInt32BigEndian(p),
            0xCF => (long)BinaryPrimitives.ReadUInt64BigEndian(p),
            _ => BinaryPrimitives.ReadInt64BigEndian(p)
        };
        return true;
    }
}