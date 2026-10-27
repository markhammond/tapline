using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Tapline.Grid;

/// <summary>
///     A grid's order value, as it goes on the wire beside the rows table: what the page needs to place the table's rows.
///     Little-endian: u8 format (1) | u8 flags (<see cref="Live" />, <see cref="Stale" />, <see cref="ByKey" />) | u8 the
///     total's kind | u8 the key's size | u32 keys | i64 first (the view position of the first key) | i64 total (rows, in
///     view positions) | i64 let in (rows let in above so far in this hold: the page moves its scroll by what's new of
///     it) | u32 holds (how often the grid has gone from the live top to held: positions are this hold's) | u32 view (how
///     often its sort or filter has been set: the view its rows are of) | the keys, in order — none if
///     <see cref="ByKey" />, the count then the rows the table holds.
/// </summary>
public static class GridOrder
{
    public const byte Format = 1;

    /// <summary>Bytes before the keys.</summary>
    public const int HeaderSize = 40;

    /// <summary>Flag: the window follows the live top (positions are ranks); else it's held on its rows.</summary>
    public const byte Live = 1;

    /// <summary>Flag: the last read was refused; the rows shown are as they were.</summary>
    public const byte Stale = 2;

    /// <summary>
    ///     Flag: no keys follow — the window is the rows the table holds, in their keys' order, greatest first (as the key
    ///     type compares: a <see cref="Guid" /> as <see cref="Guid.CompareTo(Guid)" />, an integer by value). A live tail
    ///     whose keys grow as rows arrive goes so: each update then carries only the rows that changed, conflated by
    ///     Tapline for each client, not every key again.
    /// </summary>
    public const byte ByKey = 4;

    /// <summary>The value's size for <paramref name="keys" /> keys of <paramref name="keySize" /> bytes.</summary>
    public static int SizeOf(int keys, int keySize)
    {
        return HeaderSize + keys * keySize;
    }

    /// <summary>
    ///     Writes an order into <paramref name="into" />: its size. <see cref="GridOrderHeader.ByKey" />: no keys, and the
    ///     count is <paramref name="keys" />' — the rows the table holds.
    /// </summary>
    public static int Write<TKey>(Span<byte> into, in GridOrderHeader header, ReadOnlySpan<TKey> keys)
        where TKey : unmanaged
    {
        var keyBytes = header.ByKey ? default : MemoryMarshal.AsBytes(keys);
        var size = HeaderSize + keyBytes.Length;
        into[0] = Format;
        into[1] = (byte)((header.Live ? Live : 0) | (header.Stale ? Stale : 0) | (header.ByKey ? ByKey : 0));
        into[2] = (byte)header.TotalKind;
        into[3] = (byte)(keyBytes.IsEmpty ? header.KeySize : keyBytes.Length / keys.Length);
        BinaryPrimitives.WriteInt32LittleEndian(into[4..], keys.Length);
        BinaryPrimitives.WriteInt64LittleEndian(into[8..], header.First);
        BinaryPrimitives.WriteInt64LittleEndian(into[16..], header.Total);
        BinaryPrimitives.WriteInt64LittleEndian(into[24..], header.LetIn);
        BinaryPrimitives.WriteInt32LittleEndian(into[32..], header.Holds);
        BinaryPrimitives.WriteInt32LittleEndian(into[36..], header.View);
        keyBytes.CopyTo(into[HeaderSize..]);
        return size;
    }

    /// <summary>
    ///     Reads an order's header, and its keys' bytes (none if <see cref="GridOrderHeader.ByKey" />): false if it isn't
    ///     one (another format, cut short).
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> value, out GridOrderHeader header, out ReadOnlySpan<byte> keys)
    {
        header = default;
        keys = default;
        if (value.Length < HeaderSize || value[0] != Format) return false;
        var count = BinaryPrimitives.ReadInt32LittleEndian(value[4..]);
        var keySize = value[3];
        var byKey = (value[1] & ByKey) != 0;
        if (count < 0 || (byKey ? 0 : (long)count * keySize) != value.Length - HeaderSize) return false;
        header = new GridOrderHeader
        {
            Live = (value[1] & Live) != 0,
            Stale = (value[1] & Stale) != 0,
            ByKey = byKey,
            TotalKind = (TotalKind)value[2],
            KeySize = keySize,
            Count = count,
            First = BinaryPrimitives.ReadInt64LittleEndian(value[8..]),
            Total = BinaryPrimitives.ReadInt64LittleEndian(value[16..]),
            LetIn = BinaryPrimitives.ReadInt64LittleEndian(value[24..]),
            Holds = BinaryPrimitives.ReadInt32LittleEndian(value[32..]),
            View = BinaryPrimitives.ReadInt32LittleEndian(value[36..])
        };
        keys = value[HeaderSize..];
        return true;
    }
}

/// <summary>An order value's header (<see cref="GridOrder" />).</summary>
public readonly record struct GridOrderHeader
{
    public bool Live { get; init; }
    public bool Stale { get; init; }

    /// <summary>No keys follow: the window is the rows the table holds, greatest key first (<see cref="GridOrder.ByKey" />).</summary>
    public bool ByKey { get; init; }

    public TotalKind TotalKind { get; init; }
    public int KeySize { get; init; }

    /// <summary>Keys that follow; <see cref="ByKey" />, the rows the table holds.</summary>
    public int Count { get; init; }

    /// <summary>The view position of the first key.</summary>
    public long First { get; init; }

    /// <summary>Rows in all, in view positions.</summary>
    public long Total { get; init; }

    /// <summary>Rows let in above so far in this hold (0 live).</summary>
    public long LetIn { get; init; }

    /// <summary>How often the grid has gone from the live top to held: the hold its positions are of.</summary>
    public int Holds { get; init; }

    /// <summary>How often the grid's sort or filter has been set (<see cref="GridSession{TKey,TRow}.SetView" />): the view its rows are of.</summary>
    public int View { get; init; }
}
