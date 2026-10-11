using System.Buffers.Binary;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tapline.Client;

/// <summary>
///     A replica of a router's table (<c>Table&lt;TKey, TRow&gt;</c>): its rows, keyed by their key bytes, as of
///     <see cref="Version" /> — every record applied in sequence, its checksum verified (the router's,
///     <see cref="RowHash" />).
///     Domain-agnostic: the app reads keys and rows by their layouts. A row is updated in place: copy what you keep.
/// </summary>
public sealed class TableReplica : IEnumerable<TableRow>
{
    private readonly Dictionary<byte[], byte[]> _rows = new(KeyBytes.Comparer);

    internal bool AwaitingReset = true, ResyncSent, LastWasReset;

    /// <summary>Its keys' size in bytes (every key's), as of its last snapshot.</summary>
    public int KeySize { get; private set; }

    /// <summary>Its rows' size in bytes (every row's), as of its last snapshot.</summary>
    public int RowSize { get; private set; }

    /// <summary>The table's version this replica is at: the last record's (−1 before its first snapshot).</summary>
    public long Version { get; private set; } = -1;

    /// <summary>The checksum of its rows now: Σ <see cref="RowHash.Of" /> over them, mod 2³².</summary>
    public uint Checksum { get; private set; }

    /// <summary>Its last snapshot was the router's degraded form of the table: its error follows.</summary>
    public bool Degraded { get; private set; }

    /// <summary>Its last record applied checked out: in sequence, and its checksum the router's.</summary>
    public bool Verified { get; private set; }

    /// <summary>Its rows now.</summary>
    public int Count => _rows.Count;

    IEnumerator<TableRow> IEnumerable<TableRow>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>The row held under <paramref name="key" />, if any (in place: copy it to keep it).</summary>
    public bool TryGetRow(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> row)
    {
        if (_rows.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(key, out var held))
        {
            row = held;
            return true;
        }

        row = default;
        return false;
    }

    /// <summary>
    ///     The row held under <paramref name="key" />, read as the app's own struct of the table's row layout — the router's
    ///     <c>Table&lt;TKey, TRow&gt;</c> types, or any of the same layout (the wire is little-endian, as is .NET on every
    ///     platform it ships on). False if there's none, or the sizes don't match the table's.
    /// </summary>
    public bool TryGetRow<TKey, TRow>(in TKey key, out TRow row) where TKey : unmanaged where TRow : unmanaged
    {
        if (TryGetRow(MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)), out var bytes) &&
            bytes.Length == Unsafe.SizeOf<TRow>())
        {
            row = MemoryMarshal.Read<TRow>(bytes);
            return true;
        }

        row = default;
        return false;
    }

    public Enumerator GetEnumerator()
    {
        return new Enumerator(_rows.GetEnumerator());
    }

    /// <summary>
    ///     Applies a Table record (<paramref name="record" />: the record whole). False on a sequence gap, a record that
    ///     doesn't
    ///     fit, or a checksum that isn't the router's: the replica then awaits a snapshot.
    /// </summary>
    internal bool Apply(ReadOnlySpan<byte> record)
    {
        var flags = record[5];
        var reset = (flags & Wire.FlagReset) != 0;
        LastWasReset = reset;
        long from = BinaryPrimitives.ReadInt64LittleEndian(record[6..]),
            to = BinaryPrimitives.ReadInt64LittleEndian(record[14..]);
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(record[22..]);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[26..]);
        int keySize = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]),
            rowSize = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);

        if (reset)
        {
            _rows.Clear();
            (Checksum, KeySize, RowSize, AwaitingReset, ResyncSent, Degraded) = (0, keySize, rowSize, false, false,
                (flags & Wire.FlagDegraded) != 0);
        }
        else if (AwaitingReset || from != Version + 1 || keySize != KeySize || rowSize != RowSize)
        {
            (AwaitingReset, Verified) = (true, false);
            return false;
        }

        var sum = Checksum;
        var rows = _rows.GetAlternateLookup<ReadOnlySpan<byte>>();
        var stride = 1 + keySize + rowSize;
        var entries = record[Wire.TableHeader..];
        for (var i = 0; i < count; i++)
        {
            var entry = entries.Slice(i * stride, stride);
            var key = entry.Slice(1, keySize);
            var row = entry.Slice(1 + keySize, rowSize);
            var held = rows.TryGetValue(key, out var heldKey, out var heldRow);
            if (held) sum -= RowHash.Of(heldKey, heldRow);
            if (entry[0] == Wire.OpDelete)
            {
                if (held) rows.Remove(key);
            }
            else
            {
                if (held) row.CopyTo(heldRow);
                else rows[key] = row.ToArray();
                sum += RowHash.Of(key, row);
            }
        }

        (Checksum, Version) = (sum, to);
        Verified = sum == expected;
        if (!Verified) AwaitingReset = true;
        return Verified;
    }

    /// <summary>A Table record's length: its header and entries — or −1 if <paramref name="record" /> is too short to say.</summary>
    internal static int LengthOf(ReadOnlySpan<byte> record)
    {
        if (record.Length < Wire.TableHeader) return -1;
        long count = BinaryPrimitives.ReadUInt32LittleEndian(record[26..]);
        long stride = 1 + BinaryPrimitives.ReadUInt16LittleEndian(record[30..]) +
                      BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
        var length = Wire.TableHeader + count * stride;
        return length <= record.Length ? (int)length : -1;
    }

    /// <summary>Its rows, in no order: each its key and row (in place).</summary>
    public struct Enumerator(Dictionary<byte[], byte[]>.Enumerator inner) : IEnumerator<TableRow>
    {
        private Dictionary<byte[], byte[]>.Enumerator _inner = inner;
        public readonly TableRow Current => new(_inner.Current.Key, _inner.Current.Value);
        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            return _inner.MoveNext();
        }

        public readonly void Reset()
        {
            throw new NotSupportedException();
        }

        public readonly void Dispose()
        {
        }
    }
}

/// <summary>
///     A row of a <see cref="TableReplica" />: its key and row bytes, in place — or read as the app's own structs of
///     their layouts.
/// </summary>
public readonly record struct TableRow(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Row)
{
    public TKey KeyAs<TKey>() where TKey : unmanaged
    {
        return MemoryMarshal.Read<TKey>(Key.Span);
    }

    public TRow RowAs<TRow>() where TRow : unmanaged
    {
        return MemoryMarshal.Read<TRow>(Row.Span);
    }
}

/// <summary>
///     A table record as applied: a snapshot (every row the table has) or a delta (the rows it changed), read from the
///     frame
///     that carried it — valid during the handler's call only.
/// </summary>
public readonly ref struct TableBatch
{
    private readonly ReadOnlySpan<byte> _record;

    internal TableBatch(ReadOnlySpan<byte> record)
    {
        _record = record;
    }

    /// <summary>A snapshot: the table's rows whole (the replica was reset to them).</summary>
    public bool IsSnapshot => (_record[5] & Wire.FlagReset) != 0;

    /// <summary>The router's degraded form of the table (a snapshot): its error follows.</summary>
    public bool Degraded => (_record[5] & Wire.FlagDegraded) != 0;

    /// <summary>The first version it covers.</summary>
    public long FromVersion => BinaryPrimitives.ReadInt64LittleEndian(_record[6..]);

    /// <summary>The version it brings the replica to.</summary>
    public long ToVersion => BinaryPrimitives.ReadInt64LittleEndian(_record[14..]);

    /// <summary>Its entries: rows set, or deleted.</summary>
    public int Count => (int)BinaryPrimitives.ReadUInt32LittleEndian(_record[26..]);

    public Enumerator GetEnumerator()
    {
        return new Enumerator(_record);
    }

    /// <summary>Its entries, in the order the router wrote them.</summary>
    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<byte> _entries;
        private readonly int _keySize, _stride, _count;
        private int _index;

        internal Enumerator(ReadOnlySpan<byte> record)
        {
            _keySize = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
            _stride = 1 + _keySize + BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            _count = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[26..]);
            _entries = record[Wire.TableHeader..];
            _index = -1;
        }

        public readonly TableEntry Current
        {
            get
            {
                var entry = _entries.Slice(_index * _stride, _stride);
                return new TableEntry(entry[0] == Wire.OpDelete, entry.Slice(1, _keySize), entry[(1 + _keySize)..]);
            }
        }

        public bool MoveNext()
        {
            return ++_index < _count;
        }
    }
}

/// <summary>
///     An entry of a <see cref="TableBatch" />: a key, and its row set — or <see cref="Deleted" /> (its row bytes
///     zero).
/// </summary>
public readonly ref struct TableEntry(bool deleted, ReadOnlySpan<byte> key, ReadOnlySpan<byte> row)
{
    public bool Deleted { get; } = deleted;
    public ReadOnlySpan<byte> Key { get; } = key;
    public ReadOnlySpan<byte> Row { get; } = row;

    /// <summary>Its key, read as the app's own struct of the table's key layout.</summary>
    public TKey KeyAs<TKey>() where TKey : unmanaged
    {
        return MemoryMarshal.Read<TKey>(Key);
    }

    /// <summary>Its row, read as the app's own struct of the table's row layout (zeros when deleted).</summary>
    public TRow RowAs<TRow>() where TRow : unmanaged
    {
        return MemoryMarshal.Read<TRow>(Row);
    }
}

/// <summary>Keys by their bytes — looked up by a span (a frame's) without copying it.</summary>
internal sealed class KeyBytes : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
{
    public static readonly KeyBytes Comparer = new();

    public bool Equals(ReadOnlySpan<byte> alternate, byte[] other)
    {
        return alternate.SequenceEqual(other);
    }

    public int GetHashCode(ReadOnlySpan<byte> alternate)
    {
        return Hash(alternate);
    }

    public byte[] Create(ReadOnlySpan<byte> alternate)
    {
        return alternate.ToArray();
    }

    public bool Equals(byte[]? x, byte[]? y)
    {
        return x.AsSpan().SequenceEqual(y);
    }

    public int GetHashCode(byte[] key)
    {
        return Hash(key);
    }

    private static int Hash(ReadOnlySpan<byte> bytes)
    {
        var hash = new HashCode();
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }
}