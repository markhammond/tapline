using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PivotTable.Contracts;

/// <summary>
///     A cell of a pivot: its row's label and its column's — the values its statement grouped by, as text (UTF-8, at most
///     <see cref="RowBytes" /> and <see cref="ColumnBytes" />, zero-padded): any grouping, one key type.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PivotKey : IEquatable<PivotKey>
{
    public const int RowBytes = 64, ColumnBytes = 32;

    [InlineArray(RowBytes)]
    private struct RowText
    {
        private byte _first;
    }

    [InlineArray(ColumnBytes)]
    private struct ColumnText
    {
        private byte _first;
    }

    private RowText _row;
    private ColumnText _column;

    /// <summary>A key, or null if a label doesn't fit (it would be cut: two labels could become one cell).</summary>
    public static PivotKey? Of(string row, string column)
    {
        var key = new PivotKey();
        if (!Encoding.UTF8.TryGetBytes(row, key._row, out _) ||
            !Encoding.UTF8.TryGetBytes(column, key._column, out _)) return null;
        return key;
    }

    /// <summary>A key from its labels' UTF-8 — copied, nothing decoded — or null if one doesn't fit.</summary>
    public static PivotKey? Of(ReadOnlySpan<byte> row, ReadOnlySpan<byte> column)
    {
        var key = new PivotKey();
        if (!row.TryCopyTo(key._row) || !column.TryCopyTo(key._column)) return null;
        return key;
    }

    public bool Equals(PivotKey other)
    {
        return Bytes(this).SequenceEqual(Bytes(other));
    }

    public override bool Equals(object? obj)
    {
        return obj is PivotKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes(this));
        return hash.ToHashCode();
    }

    private static ReadOnlySpan<byte> Bytes(in PivotKey key)
    {
        return MemoryMarshal.AsBytes(new ReadOnlySpan<PivotKey>(in key));
    }

    /// <summary>
    ///     The row label of the provider's clock cell, in each post with the cells it describes: its
    ///     <see cref="PivotCell.Quantity" /> the answer's watermark, its <see cref="PivotCell.Notional" /> when the provider
    ///     posted it — microseconds since the Unix epoch, the provider's clock. No cell of the statement's (a control
    ///     character).
    /// </summary>
    public const string ClockRow = "\u0001clock";

    /// <summary>Its row's label.</summary>
    public string Row => Text(Bytes(this)[..RowBytes]);

    /// <summary>Its column's label.</summary>
    public string Column => Text(Bytes(this)[RowBytes..]);

    /// <summary>The provider's clock cell (<see cref="ClockRow" />).</summary>
    public bool IsClock => Row == ClockRow;

    /// <summary>Its column's total across the rows (the Total row's cell in its column): its row label let go.</summary>
    public PivotKey AcrossRows()
    {
        var key = this;
        ((Span<byte>)key._row).Clear();
        return key;
    }

    /// <summary>Its row's total across the columns (the Total column's cell on its row): its column label let go.</summary>
    public PivotKey AcrossColumns()
    {
        var key = this;
        ((Span<byte>)key._column).Clear();
        return key;
    }

    /// <summary>Its labels, as text: the row's and the column's (for a log).</summary>
    public override string ToString()
    {
        return $"row '{Row}', column '{Column}'";
    }

    private static string Text(ReadOnlySpan<byte> b)
    {
        return Encoding.UTF8.GetString(b.IndexOf((byte)0) is >= 0 and var end ? b[..end] : b);
    }
}

/// <summary>
///     What a cell holds: a position (in units of its asset), its notional at the mark, its realized and unrealized PnL,
///     its
///     balance — the cash its realized PnL so far has paid — and the volume it traded (in the reporting currency), its
///     trades (a count), its max gross (each client's larger side, long or short notional, summed), and its mark price (a
///     rate: its instrument's quote per unit of its asset, averaged over the venues it spans) — each in hundred-millionths
///     (<see cref="Places" />), its volume in cents (<see cref="VolumePlaces" />); <see cref="None" /> where the figure
///     doesn't add up (a position across assets, a level
///     across days, a mark across instruments). Fixed-point integers, not decimals: a row of fixed size, exact, its
///     checksum the same wherever it's
///     computed, read by the page as 64-bit integers. The statement answers plain numbers; the provider scales them in.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct PivotCell(
    long Quantity,
    long Notional,
    long Realized,
    long Unrealized,
    long Balance,
    long Volume,
    long Trades,
    long MaxGross,
    long MarkPrice)
{
    /// <summary>No figure: one that doesn't add up across what the cell spans (the statement said null).</summary>
    public const long None = long.MinValue;

    /// <summary>Its figures' places: eight, hundred-millionths — but its volume's.</summary>
    public const int Places = 8;

    /// <summary>Its volume's places: two, cents (a day's trading runs past what eight places hold).</summary>
    public const int VolumePlaces = 2;
}