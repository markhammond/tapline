using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PivotTable.Contracts;

/// <summary>
///     What a statement's pivot streams, described before it does (<see cref="IPivotService.DescribeAsync" />): the
///     statement, what it binds and the columns it answers, as ChalkQL prepared it; and the cells the provider makes of
///     them — a key and a row of fixed width, field by field — and the cells it keeps for itself. A client reads the
///     stream's records with it alone, nothing of the cells compiled in. As JSON (<see cref="ToJson" />,
///     <see cref="Parse" />): the same for any client, in any language.
/// </summary>
/// <remarks>
///     The statement makes every total: a cell is a group's, its labels its levels joined (<see cref="ShapeLevels" />), a
///     total's fewer — a client shows the cells, it adds nothing up. Each cell comes keyed: a snapshot, then the cells
///     each
///     post changed or deleted (the stream's records say which, and check them).
/// </remarks>
public sealed record PivotShape
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder
            .UnsafeRelaxedJsonEscaping // (an answer's, not a page's: its quotes and its text as they are)
    };

    private static readonly decimal[] PowersOfTen =
        [.. Enumerable.Range(0, 19).Select(n => Enumerable.Repeat(10m, n).Aggregate(1m, (a, b) => a * b))];

    /// <summary>The statement, as asked for.</summary>
    public required string Sql { get; init; }

    /// <summary>What it binds, as ChalkQL inferred them: <c>@now</c> (the time it runs, each run), and the lists bound.</summary>
    public required IReadOnlyList<ShapeParameter> Parameters { get; init; }

    /// <summary>What it answers, column by column, as ChalkQL prepared it: one row per cell, totals included.</summary>
    public required IReadOnlyList<ShapeColumn> Columns { get; init; }

    /// <summary>A cell's key: its labels.</summary>
    public required ShapeLayout Key { get; init; }

    /// <summary>A cell's figures.</summary>
    public required ShapeLayout Row { get; init; }

    /// <summary>Cells the provider keeps for itself among the statement's, by their labels: none of the pivot's.</summary>
    public required IReadOnlyList<ShapeReserved> Reserved { get; init; }

    /// <summary>The shape as JSON.</summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, Json);
    }

    /// <summary>A shape from its JSON.</summary>
    public static PivotShape Parse(string json)
    {
        return JsonSerializer.Deserialize<PivotShape>(json, Json) ?? throw new FormatException("No shape.");
    }

    /// <summary>A key field's label: its UTF-8, to its first zero byte (its field zero-padded).</summary>
    public string Text(ReadOnlySpan<byte> key, string field)
    {
        var f = Key.Field(field);
        var bytes = key.Slice(f.Offset, f.Bytes);
        return Encoding.UTF8.GetString(bytes.IndexOf((byte)0) is >= 0 and var end ? bytes[..end] : bytes);
    }

    /// <summary>A row field's integer as the row holds it — or null where it means none (<see cref="ShapeField.None" />).</summary>
    public long? Integer(ReadOnlySpan<byte> row, string field)
    {
        return Integer(row, Row.Field(field));
    }

    /// <summary>The same, by its field.</summary>
    public static long? Integer(ReadOnlySpan<byte> row, ShapeField field)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(row.Slice(field.Offset, 8));
        return value == field.NoneValue ? null : value;
    }

    /// <summary>
    ///     A row field's figure: its integer at its scale (<see cref="ShapeField.Scale" />), exactly — or null where it's
    ///     none.
    /// </summary>
    public static decimal? Figure(ReadOnlySpan<byte> row, ShapeField field)
    {
        return Integer(row, field) is { } value ? value / PowersOfTen[field.Scale ?? 0] : null;
    }

    /// <summary>A reserved cell's key, as the stream's records carry it: its labels in their fields, zero-padded.</summary>
    public byte[] KeyOf(ShapeReserved reserved)
    {
        return KeyOf(reserved.Row, reserved.Column);
    }

    /// <summary>A cell's key by its labels (its row's, its column's), as the stream's records carry it: zero-padded.</summary>
    public byte[] KeyOf(string row, string column)
    {
        var key = new byte[Key.Bytes];
        foreach (var (field, label) in new[] { ("row", row), ("column", column) })
            Encoding.UTF8.GetBytes(label, key.AsSpan(Key.Field(field).Offset, Key.Field(field).Bytes));
        return key;
    }

    /// <summary>The cell the provider keeps for itself that <paramref name="key" /> labels — or null: one of the pivot's.</summary>
    public ShapeReserved? ReservedOf(ReadOnlySpan<byte> key)
    {
        if (Reserved.Count == 0) return null;
        string row = Text(key, "row"), column = Text(key, "column");
        foreach (var reserved in Reserved)
            if (reserved.Row == row && reserved.Column == column)
                return reserved;
        return null;
    }
}

/// <summary>
///     A value a statement binds: its name, the type ChalkQL inferred (a list's, its values'), and whether it's a
///     list.
/// </summary>
public sealed record ShapeParameter(string Name, string Type, bool List, string About);

/// <summary>
///     A column the statement answers: its name, its type as ChalkQL prepared it (<c>utf8</c>, <c>decimal128(38, 8)</c>,
///     <c>float64</c>, <c>int64</c>…), whether it may be null — and the cell field it's carried in (<c>key.row</c>,
///     <c>row.notional</c>…), if any: none, a column the provider reads nothing of.
/// </summary>
public sealed record ShapeColumn(string Name, string Type, bool Nullable, string? Carried);

/// <summary>A cell's key or row: its width in bytes, and its fields — at their offsets, nothing between or after them.</summary>
public sealed record ShapeLayout(int Bytes, IReadOnlyList<ShapeField> Fields)
{
    /// <summary>A field, by its name.</summary>
    public ShapeField Field(string name)
    {
        foreach (var field in Fields)
            if (field.Name == name)
                return field;
        throw new KeyNotFoundException(
            $"No field {name} (the fields: {string.Join(", ", Fields.Select(f => f.Name))}).");
    }
}

/// <summary>
///     A field of a cell's key or row: where it is and what it holds. <see cref="Type" />: <c>utf8</c> — text, zero-padded
///     to <see cref="Bytes" /> — or <c>int64</c>, little-endian: a fixed-point figure, its value the integer ÷
///     10^<see cref="Scale" />; <see cref="None" /> (the integer, as text: past what a double holds) where the figure
///     doesn't add
///     up across what the cell spans. <see cref="From" />: the statement's column it's made from — none: a figure the
///     statement
///     doesn't answer, always none.
/// </summary>
public sealed record ShapeField
{
    public required string Name { get; init; }
    public required int Offset { get; init; }
    public required string Type { get; init; }
    public required int Bytes { get; init; }
    public int? Scale { get; init; }

    public string? None
    {
        get => field;
        init => (field, NoneValue) = (value,
            value is null ? null : long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
    }

    /// <summary><see cref="None" /> as an integer.</summary>
    [JsonIgnore]
    public long? NoneValue { get; private init; }

    public string? From { get; init; }
    public string? Unit { get; init; }
    public string? About { get; init; }

    /// <summary>A key's label: its levels, joined.</summary>
    public ShapeLevels? Levels { get; init; }
}

/// <summary>
///     How a label holds a cell's levels: joined by <see cref="Separator" />, outermost first — and what a total's
///     is.
/// </summary>
public sealed record ShapeLevels(string Separator, string Totals);

/// <summary>
///     A cell the provider keeps for itself, by its labels: what it's for, and what its figures say instead — by field
///     (<see cref="ShapeReservedFigure" />). Its figures are integers as they are: no scale.
/// </summary>
public sealed record ShapeReserved(
    string Row,
    string Column,
    string About,
    IReadOnlyDictionary<string, ShapeReservedFigure> Figures)
{
    /// <summary>The row field holding what <paramref name="means" /> (<c>watermark</c>, <c>emitted</c>) — or null.</summary>
    public string? FieldMeaning(string means)
    {
        foreach (var (field, figure) in Figures)
            if (figure.Means == means)
                return field;
        return null;
    }
}

/// <summary>What a reserved cell's figure says: a name for it, its unit and what it is.</summary>
public sealed record ShapeReservedFigure(string Means, string Unit, string About);