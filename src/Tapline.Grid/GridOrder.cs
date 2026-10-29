using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tapline.Grid.Provider;

namespace Tapline.Grid;

/// <summary>
///     A grid's order value, as it goes on the wire beside the rows table: what the page needs to place the table's rows.
///     Little-endian: u8 format (2) | u8 flags (<see cref="Live" />, <see cref="Stale" />, <see cref="ByKey" />,
///     <see cref="Runs" />, <see cref="AllOpen" />) | u8 the total's kind | u8 the key's size | u32 keys | i64 first (the view position of the
///     window's first row) | i64 total (rows, in view positions) | i64 let in (what the view positions have moved by so far
///     in this hold — rows let in above, runs come or gone above the page's first row: the page moves its scroll by what's
///     new of it) | u32 holds (how often the grid has gone from the live top to held: positions are this hold's) | u32 view
///     (how often its sort or filter has been set: the view its rows are of) | the keys of the view's rows in the window, in
///     order — none if <see cref="ByKey" />, the count then the view's rows the table holds | <see cref="Runs" />: the runs
///     of rows open in the window, after their open rows (<see cref="RunsOf" />).
/// </summary>
public static class GridOrder
{
    public const byte Format = 2;

    /// <summary>Bytes before the keys.</summary>
    public const int HeaderSize = 40;

    /// <summary>Flag: the window follows the live top (positions are ranks); else it's held on its rows.</summary>
    public const byte Live = 1;

    /// <summary>Flag: the last read was refused; the rows shown are as they were.</summary>
    public const byte Stale = 2;

    /// <summary>
    ///     Flag: no keys follow — the window's rows of the view are the rows the table holds (but the runs'), in their keys'
    ///     order, greatest first (as the key type compares: a <see cref="Guid" /> as <see cref="Guid.CompareTo(Guid)" />, an
    ///     integer by value). A live tail whose keys grow as rows arrive goes so: each update then carries only the rows that
    ///     changed, conflated by Tapline for each client, not every key again.
    /// </summary>
    public const byte ByKey = 4;

    /// <summary>
    ///     Flag: runs follow the keys — u32 runs, then each: its open row's key | u8 flags (<see cref="LeadRun" />) | u32 rows
    ///     | their keys | their depths (u8 each: 1, a child of the open row, to <see cref="GridRuns.MaxDepth" />). Each row of
    ///     the view open in the window has one (with no rows, if none of its run is in the window), after it in the order its
    ///     rows are drawn in.
    /// </summary>
    public const byte Runs = 8;

    /// <summary>
    ///     Flag: every row of the view is open (its run following it) but those whose run says <see cref="ClosedRun" />.
    ///     Runs follow only for rows with rows of their runs in the window, and a closed one, with none, for each row closed
    ///     in it.
    /// </summary>
    public const byte AllOpen = 16;

    /// <summary>A run's flag: its open row is above the window, and its rows are the window's first.</summary>
    public const byte LeadRun = 1;

    /// <summary>A run's flag, every row open (<see cref="AllOpen" />): its row is closed, and it has no rows.</summary>
    public const byte ClosedRun = 2;

    /// <summary>The value's size for <paramref name="keys" /> keys of <paramref name="keySize" /> bytes, with no runs.</summary>
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
        var size = WriteHeader(into, header with
        {
            Count = keys.Length, KeySize = keyBytes.IsEmpty ? header.KeySize : keyBytes.Length / keys.Length
        });
        keyBytes.CopyTo(into[size..]);
        return size + keyBytes.Length;
    }

    /// <summary>Writes an order's header alone (its <see cref="GridOrderHeader.Count" /> as given), the keys to follow: its size.</summary>
    public static int WriteHeader(Span<byte> into, in GridOrderHeader header)
    {
        into[0] = Format;
        into[1] = (byte)((header.Live ? Live : 0) | (header.Stale ? Stale : 0) | (header.ByKey ? ByKey : 0) |
                         (header.Runs ? Runs : 0) | (header.AllOpen ? AllOpen : 0));
        into[2] = (byte)header.TotalKind;
        into[3] = (byte)header.KeySize;
        BinaryPrimitives.WriteInt32LittleEndian(into[4..], header.Count);
        BinaryPrimitives.WriteInt64LittleEndian(into[8..], header.First);
        BinaryPrimitives.WriteInt64LittleEndian(into[16..], header.Total);
        BinaryPrimitives.WriteInt64LittleEndian(into[24..], header.LetIn);
        BinaryPrimitives.WriteInt32LittleEndian(into[32..], header.Holds);
        BinaryPrimitives.WriteInt32LittleEndian(into[36..], header.View);
        return HeaderSize;
    }

    /// <summary>
    ///     Reads an order's header, and its keys' bytes (none if <see cref="GridOrderHeader.ByKey" />): false if it isn't
    ///     one (another format, cut short).
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> value, out GridOrderHeader header, out ReadOnlySpan<byte> keys)
    {
        return TryRead(value, out header, out keys, out _);
    }

    /// <summary>
    ///     Reads an order's header, its keys' bytes (none if <see cref="GridOrderHeader.ByKey" />) and its runs' (none
    ///     without <see cref="GridOrderHeader.Runs" />: <see cref="RunsOf" /> reads them): false if it isn't one.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> value, out GridOrderHeader header, out ReadOnlySpan<byte> keys,
        out ReadOnlySpan<byte> runs)
    {
        header = default;
        keys = runs = default;
        if (value.Length < HeaderSize || value[0] != Format) return false;
        var count = BinaryPrimitives.ReadInt32LittleEndian(value[4..]);
        var keySize = value[3];
        var byKey = (value[1] & ByKey) != 0;
        var nested = (value[1] & Runs) != 0;
        var keyBytes = byKey ? 0 : (long)count * keySize;
        if (count < 0 || HeaderSize + keyBytes > value.Length) return false;
        runs = value[(int)(HeaderSize + keyBytes)..];
        if (nested ? RunsSize(runs, keySize) != runs.Length : !runs.IsEmpty) return false;
        header = new GridOrderHeader
        {
            Live = (value[1] & Live) != 0,
            Stale = (value[1] & Stale) != 0,
            ByKey = byKey,
            Runs = nested,
            AllOpen = (value[1] & AllOpen) != 0,
            TotalKind = (TotalKind)value[2],
            KeySize = keySize,
            Count = count,
            First = BinaryPrimitives.ReadInt64LittleEndian(value[8..]),
            Total = BinaryPrimitives.ReadInt64LittleEndian(value[16..]),
            LetIn = BinaryPrimitives.ReadInt64LittleEndian(value[24..]),
            Holds = BinaryPrimitives.ReadInt32LittleEndian(value[32..]),
            View = BinaryPrimitives.ReadInt32LittleEndian(value[36..])
        };
        keys = value.Slice(HeaderSize, (int)keyBytes);
        return true;
    }

    /// <summary>An order's runs (<see cref="TryRead(ReadOnlySpan{byte}, out GridOrderHeader, out ReadOnlySpan{byte}, out ReadOnlySpan{byte})" />), read.</summary>
    public static List<GridOrderRun<TKey>> RunsOf<TKey>(ReadOnlySpan<byte> runs) where TKey : unmanaged
    {
        var size = Unsafe.SizeOf<TKey>();
        var read = new List<GridOrderRun<TKey>>();
        if (runs.IsEmpty) return read;
        var count = BinaryPrimitives.ReadInt32LittleEndian(runs);
        var o = 4;
        for (var r = 0; r < count; r++)
        {
            var owner = MemoryMarshal.Read<TKey>(runs[o..]);
            var flags = runs[o + size];
            var n = BinaryPrimitives.ReadInt32LittleEndian(runs[(o + size + 1)..]);
            o += size + 5;
            var keys = MemoryMarshal.Cast<byte, TKey>(runs.Slice(o, n * size)).ToArray();
            o += n * size;
            read.Add(new GridOrderRun<TKey>(owner, (flags & LeadRun) != 0, keys, runs.Slice(o, n).ToArray(),
                (flags & ClosedRun) != 0));
            o += n;
        }

        return read;
    }

    /// <summary>The bytes the runs at the start of <paramref name="runs" /> take (-1: cut short, or not runs).</summary>
    private static long RunsSize(ReadOnlySpan<byte> runs, int keySize)
    {
        if (runs.Length < 4) return -1;
        var count = BinaryPrimitives.ReadInt32LittleEndian(runs);
        if (count < 1) return -1;
        long o = 4;
        for (var r = 0; r < count; r++)
        {
            if (o + keySize + 5 > runs.Length) return -1;
            var n = BinaryPrimitives.ReadInt32LittleEndian(runs[(int)(o + keySize + 1)..]);
            if (n < 0) return -1;
            o += keySize + 5 + (long)n * (keySize + 1);
            if (o > runs.Length) return -1;
        }

        return o;
    }
}

/// <summary>
///     A run in an order: its open row, whether that's above the window, and its rows in the window and their depths; or,
///     every row open, a row closed (<see cref="GridOrder.ClosedRun" />).
/// </summary>
public sealed record GridOrderRun<TKey>(TKey Owner, bool Lead, TKey[] Keys, byte[] Depths, bool Closed = false)
    where TKey : unmanaged;

/// <summary>An order value's header (<see cref="GridOrder" />).</summary>
public readonly record struct GridOrderHeader
{
    public bool Live { get; init; }
    public bool Stale { get; init; }

    /// <summary>No keys follow: the window's rows of the view are the rows the table holds, greatest key first (<see cref="GridOrder.ByKey" />).</summary>
    public bool ByKey { get; init; }

    /// <summary>Runs follow the keys (<see cref="GridOrder.Runs" />).</summary>
    public bool Runs { get; init; }

    /// <summary>Every row of the view open but those closed (<see cref="GridOrder.AllOpen" />).</summary>
    public bool AllOpen { get; init; }

    public TotalKind TotalKind { get; init; }
    public int KeySize { get; init; }

    /// <summary>Keys that follow; <see cref="ByKey" />, the view's rows the table holds.</summary>
    public int Count { get; init; }

    /// <summary>The view position of the window's first row.</summary>
    public long First { get; init; }

    /// <summary>Rows in all, in view positions.</summary>
    public long Total { get; init; }

    /// <summary>What the view positions have moved by so far in this hold (0 live): rows let in above, runs above.</summary>
    public long LetIn { get; init; }

    /// <summary>How often the grid has gone from the live top to held: the hold its positions are of.</summary>
    public int Holds { get; init; }

    /// <summary>How often the grid's sort or filter has been set (<see cref="GridSession{TKey,TRow}.SetView" />): the view its rows are of.</summary>
    public int View { get; init; }
}
