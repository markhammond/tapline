using System.Buffers.Binary;
using System.Text;

namespace Tapline.Authentication;

/// <summary>
///     Just enough CBOR (RFC 8949) for WebAuthn: unsigned and negative integers (as <see cref="long" />), byte and text
///     strings, arrays, maps (as <see cref="Dictionary{TKey,TValue}" /> of object), true, false and null — definite
///     lengths only, nested at most 16 deep. Anything else, or a truncated item, is a <see cref="FormatException" />.
/// </summary>
internal static class Cbor
{
    private const int MaxDepth = 16;

    /// <summary>One item from the start of <paramref name="data" />, and how many bytes it took.</summary>
    public static object? Read(ReadOnlySpan<byte> data, out int length)
    {
        var o = 0;
        var item = Item(data, ref o, 0);
        length = o;
        return item;
    }

    private static object? Item(ReadOnlySpan<byte> b, ref int o, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("CBOR nested too deep.");
        if (o >= b.Length) throw new FormatException("CBOR truncated.");
        var initial = b[o++];
        int major = initial >> 5, info = initial & 0x1F;
        if (major == 7)
            return info switch
            {
                20 => false, 21 => true, 22 => null,
                _ => throw new FormatException($"CBOR simple value {info} isn't supported.")
            };
        var n = Argument(b, ref o, info);
        switch (major)
        {
            case 0:
                return n <= long.MaxValue ? (long)n : throw new FormatException("CBOR integer too large.");
            case 1:
                return n <= long.MaxValue ? -1 - (long)n : throw new FormatException("CBOR integer too large.");
            case 2:
                return Bytes(b, ref o, n).ToArray();
            case 3:
                return new UTF8Encoding(false, true).GetString(Bytes(b, ref o, n));
            case 4:
            {
                if (n > (ulong)(b.Length - o)) throw new FormatException("CBOR truncated.");
                var list = new List<object?>((int)n);
                for (ulong i = 0; i < n; i++) list.Add(Item(b, ref o, depth + 1));
                return list;
            }
            case 5:
            {
                if (n > (ulong)(b.Length - o)) throw new FormatException("CBOR truncated.");
                var map = new Dictionary<object, object?>();
                for (ulong i = 0; i < n; i++)
                {
                    var key = Item(b, ref o, depth + 1) ?? throw new FormatException("CBOR map key null.");
                    if (!map.TryAdd(key, Item(b, ref o, depth + 1)))
                        throw new FormatException("CBOR map key repeated.");
                }

                return map;
            }
            default:
                throw new FormatException($"CBOR major type {major} isn't supported.");
        }
    }

    private static ulong Argument(ReadOnlySpan<byte> b, ref int o, int info)
    {
        if (info < 24) return (ulong)info;
        var size = info switch
        {
            24 => 1, 25 => 2, 26 => 4, 27 => 8,
            _ => throw new FormatException("CBOR indefinite or reserved length isn't supported.")
        };
        if (o + size > b.Length) throw new FormatException("CBOR truncated.");
        var s = b.Slice(o, size);
        o += size;
        return size switch
        {
            1 => s[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(s),
            4 => BinaryPrimitives.ReadUInt32BigEndian(s),
            _ => BinaryPrimitives.ReadUInt64BigEndian(s)
        };
    }

    private static ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> b, ref int o, ulong n)
    {
        if (n > (ulong)(b.Length - o)) throw new FormatException("CBOR truncated.");
        var s = b.Slice(o, (int)n);
        o += (int)n;
        return s;
    }
}
