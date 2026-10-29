using System.Buffers.Binary;
using System.Text;

namespace Tapline.Grid;

/// <summary>
///     A grid's status value, the third member of its watch beside the rows and their order: what the page needs that
///     neither the rows nor their order say (the order says the total, its kind, and live or held), staged with the rows
///     and conflated by Tapline for each client like them. It changes only as rows arrive above a held window, a read is
///     refused, the link's overscan moves, or the source moves while the page is passive, so it goes out alone, small,
///     without the held window's keys.
///     Little-endian: u8 format (1) | u8 flags (<see cref="Refused" />, <see cref="Moved" />) | u16 the overscan the link carries (rows either
///     side of the view, <see cref="GridOptions.OverscanFill" />; <see cref="OwnOverscan" />: the page's own) | i64 above
///     (held: rows arrived above the reader hasn't seen) | why the last read was refused, UTF-8 (none: it wasn't).
/// </summary>
public static class GridStatusValue
{
    public const byte Format = 1;

    /// <summary>Bytes before the refusal.</summary>
    public const int Size = 12;

    /// <summary>Flag: the last read was refused (why follows).</summary>
    public const byte Refused = 1;

    /// <summary>
    ///     Flag: the source has moved past the rows the page holds while the page is passive (hidden, or its grid out of
    ///     view): their window isn't read till it's shown, so they may be stale.
    /// </summary>
    public const byte Moved = 2;

    /// <summary>The overscan written for none told: the page holds its own count.</summary>
    public const ushort OwnOverscan = ushort.MaxValue;

    /// <summary>The value's size with <paramref name="refusal" />.</summary>
    public static int SizeOf(string? refusal)
    {
        return Size + (refusal is null ? 0 : Encoding.UTF8.GetByteCount(refusal));
    }

    /// <summary>
    ///     Writes a status into <paramref name="into" /> (at least <see cref="SizeOf" />): its size.
    ///     <paramref name="overscan" /> below 0: none told (the page's own); <paramref name="moved" />, <see cref="Moved" />.
    /// </summary>
    public static int Write(Span<byte> into, long above, int overscan, string? refusal, bool moved = false)
    {
        into[0] = Format;
        into[1] = (byte)((refusal is not null ? Refused : 0) | (moved ? Moved : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(into[2..], overscan < 0 ? OwnOverscan : (ushort)Math.Min(overscan, OwnOverscan - 1));
        BinaryPrimitives.WriteInt64LittleEndian(into[4..], above);
        return Size + (refusal is null ? 0 : Encoding.UTF8.GetBytes(refusal, into[Size..]));
    }

    /// <summary>Reads a status (<paramref name="overscan" /> -1: none told): false if it isn't one (another format, cut short).</summary>
    public static bool TryRead(ReadOnlySpan<byte> value, out long above, out int overscan, out string? refusal)
    {
        (above, overscan, refusal) = (0, -1, null);
        if (value.Length < Size || value[0] != Format) return false;
        var told = BinaryPrimitives.ReadUInt16LittleEndian(value[2..]);
        overscan = told == OwnOverscan ? -1 : told;
        above = BinaryPrimitives.ReadInt64LittleEndian(value[4..]);
        if ((value[1] & Refused) != 0) refusal = Encoding.UTF8.GetString(value[Size..]);
        return true;
    }

    /// <summary>Whether a status says the source has moved past the page's rows (<see cref="Moved" />).</summary>
    public static bool MovedIn(ReadOnlySpan<byte> value)
    {
        return value.Length >= Size && value[0] == Format && (value[1] & Moved) != 0;
    }
}
