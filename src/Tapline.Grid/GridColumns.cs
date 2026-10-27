using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tapline.Grid;

/// <summary>
///     What a <see cref="LiveGrid{TKey,TRow}" />'s column reads from its field, and so how its page writes the cells
///     (tapline-grid.js's cell kinds) — by the column's <see cref="LiveColumn.Format" />, as .NET would.
/// </summary>
public enum CellKind
{
    /// <summary>
    ///     From the field's type: an integer <see cref="Int" /> (<see cref="Fixed" /> with a scale), floating point
    ///     <see cref="Float" />, bool <see cref="Bool" />, an enum <see cref="Enum" />, a <see cref="DateTime" />
    ///     <see cref="Time" />, a <see cref="Guid" /> <see cref="Uuid" />, an inline array of bytes <see cref="Text" />.
    /// </summary>
    Auto,

    /// <summary>An integer, written as its type's <c>ToString(Format)</c>; signed, its least value is no value (blank).</summary>
    Int,

    /// <summary>
    ///     An integer scaled by 10^Scale: written as the decimal it stands for (<c>ToString()</c> keeps its places); its
    ///     tooltip, the decimal whole. Signed, its least value is no value.
    /// </summary>
    Fixed,

    /// <summary>A float or double (NaN: no value); its tooltip, the value whole (its shortest round-trip text).</summary>
    Float,

    /// <summary>
    ///     A long of microseconds since 1970 (its least value: none), or a <see cref="DateTime" /> (MinValue: none),
    ///     written as UTC in the Gregorian calendar (<c>G</c>, the culture's date and time, if no Format); its tooltip, the
    ///     instant to its last digit.
    /// </summary>
    Time,

    /// <summary>A bool (or a byte): a tick for true.</summary>
    Bool,

    /// <summary>An enum's names, or a small integer's entries in the column's Dictionary.</summary>
    Enum,

    /// <summary>
    ///     UTF-8 in an inline array of bytes (<see cref="GridText" /> writes it): its first byte the length, with 0x80 in
    ///     it if the text was cut short.
    /// </summary>
    Text,

    /// <summary>
    ///     Worked out on the page: the fraction one number field is of another (Of over Over; none where Over is 0), written
    ///     as a double (<c>"P"</c> if no Format). Never sorted by.
    /// </summary>
    Percent,

    /// <summary>A <see cref="Guid" />, as its text (Guid.Empty: none).</summary>
    Uuid
}

/// <summary>Which side a column's cells and heading keep to (QuickGrid's <c>Align</c>, and an Auto of its own).</summary>
public enum CellAlign
{
    /// <summary>A number's end (a sum's digits line up), the rest's start.</summary>
    Auto,

    /// <summary>The side a line starts at: the left, but in a right-to-left page.</summary>
    Start,

    Center,

    /// <summary>The side a line ends at: the right, but in a right-to-left page.</summary>
    End,
    Left,
    Right
}

/// <summary>What a column's cell does with text wider than the column: its tooltip says it whole.</summary>
public enum CellOverflow
{
    /// <summary>Cut at its end, an ellipsis in its place (CSS's <c>text-overflow: ellipsis</c>).</summary>
    Ellipsis,

    /// <summary>Cut at its end.</summary>
    Clip,

    /// <summary>Cut at its start, an ellipsis in its place: its end kept in view (an id's, a path's).</summary>
    EllipsisStart,

    /// <summary>Cut at its start.</summary>
    ClipStart
}

/// <summary>Text for a <see cref="CellKind.Text" /> field: an inline array of bytes, written as the page reads it.</summary>
public static class GridText
{
    /// <summary>
    ///     Writes <paramref name="text" /> into <paramref name="field" /> (an inline array's bytes, at most 128): its first
    ///     byte the length, then UTF-8 — cut short at a character's boundary if it doesn't fit, and flagged (0x80).
    /// </summary>
    public static void Write(Span<byte> field, ReadOnlySpan<char> text)
    {
        if (field.Length is < 2 or > 128)
            throw new ArgumentException($"A text field of {field.Length} bytes: from 2 to 128.", nameof(field));
        field.Clear();
        var room = field.Length - 1;
        var cut = false;
        var written = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (written + rune.Utf8SequenceLength > room)
            {
                cut = true;
                break;
            }

            written += rune.EncodeToUtf8(field[(1 + written)..]);
        }

        field[0] = (byte)(written | (cut ? 0x80 : 0));
    }
}

/// <summary>
///     A column as the page formats it (tapline-grid.js): a <see cref="LiveColumn" /> resolved against its row. Sent
///     without its nulls and falses, which the page reads as absent (they were half of what a grid's start sends the page).
/// </summary>
internal sealed record GridColumn
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public int Offset { get; init; }
    public int Size { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Unsigned { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Scale { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Unit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyDictionary<string, string>? Dictionary { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public GridField? Of { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public GridField? Over { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Frozen { get; init; }

    /// <summary>"start", "center", "end", "left" or "right"; null: Auto.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Align { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Link { get; init; }

    /// <summary>
    ///     How the page writes the values: a .NET numeric format (an integer's, a decimal's, a double's), a time's custom
    ///     pattern (its standard format spelt out), or a Guid's. Null for kinds that have none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    /// <summary>A number rounded to so many significant figures before its format writes it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Figures { get; init; }

    /// <summary>A number that would show more digits written in scientific notation, to its figures.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MostDigits { get; init; }

    /// <summary>A time's pattern is written in the invariant culture's names and separators (o, r, s, u).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Invariant { get; init; }

    /// <summary>"clip", "ellipsis-start" or "clip-start"; null: an ellipsis at the end.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Overflow { get; init; }

    /// <summary>The column's class, on its heading and its cells.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Class { get; init; }

    /// <summary>Its width is measured by the page, from the first rows answered (Width "auto").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Auto { get; init; }

    /// <summary>Its cells carry their text as <c>data-value</c>, for styling by value.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Mark { get; init; }

    /// <summary>The room its text has, a CSS length (the grid's style declares it, --tg-w&lt;i&gt;; not sent).</summary>
    [JsonIgnore]
    public string Width { get; init; } = "";

    /// <summary>The column it came from (not sent).</summary>
    [JsonIgnore]
    public LiveColumn? Source { get; init; }

    [JsonIgnore]
    public bool Sortable { get; init; }
}

/// <summary>A number field a computed column reads: where it is in the row, and whether it's floating point.</summary>
internal sealed record GridField(int Offset, int Size, bool Unsigned, bool Float = false);

/// <summary>
///     A time field the page reads from each row (µs since 1970; <c>Unit</c> "ticks": a DateTime): where it is. Or the
///     row's key's time (<see cref="Key" />).
/// </summary>
internal sealed record GridStamp(int Offset, int Size, string? Unit)
{
    /// <summary>A UUIDv7 key's time: its first 48 bits, ms since 1970, read by the page from the key's bytes.</summary>
    public static GridStamp Key { get; } = new(0, 16, "uuid7");
}

/// <summary>
///     Resolves <see cref="LiveColumn" />s against <typeparamref name="TRow" />'s fields: where each is in the row's bytes
///     (as Tapline sends them), its kind, and why a column can't work. A field's place is found as laid out in memory —
///     the row's fields set one at a time in a boxed row, the bytes that change seen — not as marshalled.
/// </summary>
internal static partial class GridColumns<TRow> where TRow : unmanaged
{
    private static readonly FieldInfo[] Fields =
        typeof(TRow).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>The row's size in bytes, as it goes on the wire.</summary>
    public static readonly int RowSize = MemoryMarshal.AsBytes(new ReadOnlySpan<TRow>(new TRow[1])).Length;

    /// <summary>The columns as the page formats them in <paramref name="culture" />, or why they can't work.</summary>
    public static (GridColumn[] Columns, string? Refusal) Resolve(IReadOnlyList<LiveColumn> columns, CultureInfo culture)
    {
        if (columns.Count == 0)
            return ([], "it has no columns: give it LiveColumn children, each naming a field of its rows.");
        if (columns.Count > GridFormats.MostColumns)
            return ([], $"it has {columns.Count} columns: at most {GridFormats.MostColumns}.");
        var resolved = new GridColumn[columns.Count];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unfrozen = (string?)null;
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (string.IsNullOrWhiteSpace(column.Name)) return ([], $"its column {i + 1} has no Name.");
            if (!names.Add(column.Name)) return ([], $"two columns are named \"{column.Name}\".");
            if (column.Frozen && unfrozen is not null)
                return ([], $"column \"{column.Name}\" is frozen but follows \"{unfrozen}\", which isn't: frozen columns come first.");
            if (!column.Frozen) unfrozen ??= column.Name;
            var (one, refusal) = Resolve(column, culture);
            if (refusal is not null) return ([], $"column \"{column.Name}\": {refusal}");
            resolved[i] = one!;
        }

        return (resolved, null);
    }

    private static (GridColumn? Column, string? Refusal) Resolve(LiveColumn c, CultureInfo culture)
    {
        if (WidthRefusal(c.Width) is { } widthRefusal) return (null, widthRefusal);
        if (c.Scale is < 0 or > 18) return (null, $"a Scale of {c.Scale}: from 0 to 18.");
        if (c.Kind == CellKind.Percent) return Percent(c, culture);
        if (c.Of is not null || c.Over is not null) return (null, "Of and Over are a Percent column's.");

        var field = FieldOf(c.Field ?? c.Name);
        if (field is null)
            return (null, $"{typeof(TRow).Name} has no field \"{c.Field ?? c.Name}\" (its fields: {string.Join(", ", Fields.Select(NameOf))}).");
        var type = field.FieldType;
        var offset = OffsetOf(field);
        var size = SizeOf(type);
        if (size == 0 || offset < 0)
            return (null, $"its field is a {type.Name}, which the page can't show: give it a field of a kind it can " +
                          "(an integer, a float or double, a bool, an enum, a DateTime, a Guid, an inline array of bytes).");
        if (type == typeof(decimal))
            return (null, "its field is a decimal, which the page can't read: carry it as a long scaled by 10^Scale (Kind Fixed).");

        var integer = IsInteger(type);
        var kind = c.Kind == CellKind.Auto ? Infer(type, c) : c.Kind;
        var fits = kind switch
        {
            CellKind.Int or CellKind.Fixed => integer,
            CellKind.Float => type == typeof(float) || type == typeof(double),
            CellKind.Time => type == typeof(long) || type == typeof(DateTime),
            CellKind.Bool => type == typeof(bool) || type == typeof(byte),
            CellKind.Enum => type.IsEnum || integer,
            CellKind.Text => IsText(type),
            CellKind.Uuid => type == typeof(Guid),
            _ => false
        };
        if (!fits) return (null, $"Kind {kind} can't read its field, a {type.Name}.");
        if (kind == CellKind.Fixed && c.Scale is null) return (null, "Kind Fixed needs its Scale (the value is scaled by 10^Scale).");
        if (kind == CellKind.Enum && !type.IsEnum && c.Dictionary is null)
            return (null, $"Kind Enum on a {type.Name} needs a Dictionary of its values' names.");
        if (c.Dictionary is not null && kind != CellKind.Enum) return (null, "a Dictionary is a Kind Enum column's.");
        var (format, invariant, dates, formatRefusal) = FormatOf(kind, type, c, culture);
        if (formatRefusal is not null) return (null, formatRefusal);
        if ((c.Figures ?? c.MostDigits) is not null && kind is not (CellKind.Int or CellKind.Fixed or CellKind.Float))
            return (null, "Figures and MostDigits are a number's: Kind Int, Fixed or Float.");
        if (c.Figures is < 1 or > 17) return (null, $"{c.Figures} Figures: from 1 to 17.");
        if (c.MostDigits is < 1 or > 28) return (null, $"a MostDigits of {c.MostDigits}: from 1 to 28.");
        if (c.Figures is not null && format is { Length: >= 1 } f && "DdXxBb".Contains(f[0]) && f.Skip(1).All(char.IsAsciiDigit))
            return (null, $"Figures round a number written as a decimal: Format \"{format}\" writes its digits as they are.");

        return (Sized(new GridColumn
        {
            Name = c.Name,
            Label = c.Label ?? c.Name,
            Kind = KindName(kind),
            Offset = offset,
            Size = size,
            Unsigned = integer || type.IsEnum ? IsUnsigned(type.IsEnum ? Enum.GetUnderlyingType(type) : type) : null,
            Scale = kind == CellKind.Fixed ? c.Scale : null,
            Unit = type == typeof(DateTime) ? "ticks" : null,
            Dictionary = kind == CellKind.Enum ? DictionaryOf(type, c.Dictionary) : null,
            Frozen = c.Frozen,
            Align = AlignOf(c.Align),
            Link = c.Link,
            Format = format,
            Figures = c.Figures,
            MostDigits = c.MostDigits,
            Invariant = invariant,
            Overflow = OverflowOf(c.Overflow),
            Class = string.IsNullOrWhiteSpace(c.Class) ? null : c.Class,
            Auto = c.Width == "auto",
            Mark = c.MarkValues,
            Source = c,
            Sortable = c.Sortable ?? true
        }, type, culture, dates), null);
    }

    /// <summary>
    ///     A column's format as the page applies it — null where the page's own default is .NET's (G; a Guid's D), a time's
    ///     pattern spelt out — with the Gregorian dates a time's is written in; or why the value's type refuses it.
    /// </summary>
    private static (string? Format, bool Invariant, DateTimeFormatInfo? Dates, string? Refusal) FormatOf(CellKind kind, Type type,
        LiveColumn c, CultureInfo culture)
    {
        var format = string.IsNullOrEmpty(c.Format) ? null : c.Format;
        switch (kind)
        {
            case CellKind.Int:
                return (format, false, null, format is null ? null : GridFormats.Refusal(Sample(type), format, culture));
            case CellKind.Fixed:
                return (format, false, null,
                    format is null ? null : GridFormats.Refusal(new decimal(12345, 0, 0, true, (byte)c.Scale!.Value), format, culture));
            case CellKind.Float:
                return (format, false, null,
                    format is null ? null : GridFormats.Refusal(type == typeof(float) ? -1.25f : -1.25, format, culture));
            case CellKind.Time:
                if (GridFormats.DatesOf(culture) is not { } dates)
                    return (null, false, null, $"the culture, {culture.Name}, has no Gregorian calendar to write its times in.");
                var (pattern, invariant, refusal) = GridFormats.Expand(format, dates);
                return (pattern, invariant, dates, refusal);
            case CellKind.Uuid:
                if (format is null or "D" or "d") return (null, false, null, null);
                return (format, false, null, GridFormats.Refusal(Guid.Empty, format, culture));
            default:
                return format is null
                    ? (null, false, null, null)
                    : (null, false, null, $"Format is a number's, a time's or a Guid's: a Kind {kind} column writes its {kind switch
                    {
                        CellKind.Bool => "values as ticks",
                        CellKind.Enum => "values' names",
                        _ => "text"
                    }} as it is.");
        }
    }

    /// <summary>A value of an integer type, to try a format on.</summary>
    private static IFormattable Sample(Type type)
    {
        return (IFormattable)Convert.ChangeType(-12, type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) ||
                                                      type == typeof(ulong) ? typeof(long) : type, CultureInfo.InvariantCulture) is var signed &&
               IsUnsigned(type)
            ? (IFormattable)Convert.ChangeType(12, type, CultureInfo.InvariantCulture)
            : signed;
    }

    /// <summary>
    ///     The column with the room its text has (its Width's, or what its format writes, or its label, as many <c>ch</c> as
    ///     the wider of them has characters — and, measured, that till the first rows are answered).
    /// </summary>
    private static GridColumn Sized(GridColumn column, Type type, CultureInfo culture, DateTimeFormatInfo? dates)
    {
        var given = column.Source?.Width;
        if (given is not null && given != "auto") return column with { Width = given };
        var room = Math.Max(GridFormats.Room(column, type, culture, dates, column.Dictionary), column.Label.Length + 2);
        return column with { Width = $"{room}ch" };
    }

    /// <summary>Why a Width can't be one: "auto", or a CSS length (no percentage: the widths add up, in the cells' font).</summary>
    private static string? WidthRefusal(string? width)
    {
        if (width is null or "auto") return null;
        if (Length().IsMatch(width) || (Measure().IsMatch(width) && Balanced(width))) return null;
        return $"a Width of \"{width}\": a CSS length, such as \"9em\", \"14ch\" or \"120px\" (or a calc() of them), or \"auto\".";

        static bool Balanced(string text)
        {
            var depth = 0;
            foreach (var c in text)
                if ((depth += c == '(' ? 1 : c == ')' ? -1 : 0) < 0)
                    return false;
            return depth == 0;
        }
    }

    [GeneratedRegex(@"^(\d+\.?\d*|\.\d+)(px|em|rem|ch|ex|cap|ic|lh|rlh|vw|vh|vi|vb|vmin|vmax|cm|mm|q|in|pt|pc)$", RegexOptions.IgnoreCase)]
    private static partial Regex Length();

    /// <summary>A calc(), min(), max() or clamp() of lengths, numbers and custom properties: nothing that could end the declaration.</summary>
    [GeneratedRegex(@"^(calc|min|max|clamp)\([-+*/ .,()0-9a-z]*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex Measure();

    /// <summary>A time field — a long of µs since 1970, or a DateTime — where it is in the row, or why it can't be one.</summary>
    public static (GridStamp? Stamp, string? Refusal) StampOf(string name)
    {
        var field = FieldOf(name);
        if (field is null)
            return (null, $"{typeof(TRow).Name} has no field \"{name}\" (its fields: {string.Join(", ", Fields.Select(NameOf))}).");
        if (field.FieldType != typeof(long) && field.FieldType != typeof(DateTime))
            return (null, $"\"{name}\" is a {field.FieldType.Name}: a time is a long of microseconds since 1970, or a DateTime.");
        return (new GridStamp(OffsetOf(field), 8, field.FieldType == typeof(DateTime) ? "ticks" : null), null);
    }

    /// <summary>
    ///     Where the page reads when a row arrived: <paramref name="field" /> (LiveGrid's ArrivedAt), or the key's time
    ///     (<paramref name="fromKey" />: ArrivedFromKey, the keys UUIDv7s); none, or why neither can.
    /// </summary>
    public static (GridStamp? Stamp, string? Refusal) ArrivalOf<TKey>(string? field, bool fromKey)
    {
        if (fromKey && field is not null) return (null, "ArrivedAt or ArrivedFromKey, not both: each says when the rows arrived.");
        if (fromKey)
            return typeof(TKey) == typeof(Guid)
                ? (GridStamp.Key, null)
                : (null, $"ArrivedFromKey reads the time in a UUIDv7, and the keys are {typeof(TKey).Name}s.");
        if (field is null) return (null, null);
        var (stamp, refusal) = StampOf(field);
        return (stamp, refusal is null ? null : $"ArrivedAt: {refusal}");
    }

    private static (GridColumn?, string?) Percent(LiveColumn c, CultureInfo culture)
    {
        if (c.Sortable == true) return (null, "a Percent column is worked out on the page: it can't be sorted by.");
        if (c.Of is null || c.Over is null) return (null, "a Percent column needs Of and Over, the fields it divides.");
        var (of, ofRefusal) = NumberField(c.Of);
        if (ofRefusal is not null) return (null, $"Of: {ofRefusal}");
        var (over, overRefusal) = NumberField(c.Over);
        if (overRefusal is not null) return (null, $"Over: {overRefusal}");
        var format = string.IsNullOrEmpty(c.Format) ? null : c.Format;
        if (format is not null && GridFormats.Refusal(-0.25, format, culture) is { } refusal) return (null, refusal);
        return (Sized(new GridColumn
        {
            Name = c.Name, Label = c.Label ?? c.Name, Kind = "percent", Of = of, Over = over, Format = format, Frozen = c.Frozen,
            Align = AlignOf(c.Align), Link = c.Link, Overflow = OverflowOf(c.Overflow),
            Class = string.IsNullOrWhiteSpace(c.Class) ? null : c.Class, Auto = c.Width == "auto", Mark = c.MarkValues, Source = c,
            Sortable = false
        }, typeof(double), culture, null), null);
    }

    /// <summary>A field a percentage reads: an integer, a float or a double.</summary>
    private static (GridField?, string?) NumberField(string name)
    {
        var field = FieldOf(name);
        if (field is null) return (null, $"{typeof(TRow).Name} has no field \"{name}\".");
        var type = field.FieldType;
        var floating = type == typeof(float) || type == typeof(double);
        if (!IsInteger(type) && !floating) return (null, $"\"{name}\" is a {type.Name}, not a number.");
        return (new GridField(OffsetOf(field), SizeOf(type), IsUnsigned(type), floating), null);
    }

    private static CellKind Infer(Type type, LiveColumn c)
    {
        if (IsInteger(type)) return c.Scale is null ? CellKind.Int : CellKind.Fixed;
        if (type == typeof(float) || type == typeof(double)) return CellKind.Float;
        if (type == typeof(bool)) return CellKind.Bool;
        if (type.IsEnum) return CellKind.Enum;
        if (type == typeof(DateTime)) return CellKind.Time;
        if (type == typeof(Guid)) return CellKind.Uuid;
        return IsText(type) ? CellKind.Text : CellKind.Auto;
    }

    /// <summary>A field by name: as written, else ignoring case; a property's (a record struct's) by its backing field.</summary>
    private static FieldInfo? FieldOf(string name)
    {
        return Fields.FirstOrDefault(f => NameOf(f) == name) ??
               Fields.SingleOrDefault(f => string.Equals(NameOf(f), name, StringComparison.OrdinalIgnoreCase));
    }

    private static string NameOf(FieldInfo field)
    {
        var name = field.Name;
        return name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal)
            ? name[1..name.IndexOf('>')]
            : name;
    }

    /// <summary>Where a field starts in the row's bytes: the first that changes as it's set to a value with no zero byte.</summary>
    private static int OffsetOf(FieldInfo field)
    {
        if (Probe(field.FieldType) is not { } probe) return -1;
        object boxed = default(TRow);
        field.SetValue(boxed, probe);
        var row = (TRow)boxed;
        return MemoryMarshal.AsBytes(new ReadOnlySpan<TRow>(in row)).IndexOfAnyExcept((byte)0);
    }

    /// <summary>A value of a field's type whose first byte in memory isn't zero (each byte, for those it can).</summary>
    private static object? Probe(Type type)
    {
        if (type.IsEnum) return Enum.ToObject(type, -1L); // (all ones, in its underlying type's size)
        if (type == typeof(bool)) return true;
        if (type == typeof(byte)) return byte.MaxValue;
        if (type == typeof(sbyte)) return (sbyte)-1;
        if (type == typeof(short)) return (short)-1;
        if (type == typeof(ushort)) return ushort.MaxValue;
        if (type == typeof(char)) return char.MaxValue;
        if (type == typeof(int)) return -1;
        if (type == typeof(uint)) return uint.MaxValue;
        if (type == typeof(long)) return -1L;
        if (type == typeof(ulong)) return ulong.MaxValue;
        if (type == typeof(float)) return BitConverter.Int32BitsToSingle(-1);
        if (type == typeof(double)) return BitConverter.Int64BitsToDouble(-1);
        if (type == typeof(DateTime)) return DateTime.MaxValue; // (ticks with no zero byte)
        if (type == typeof(Guid)) return new Guid(Enumerable.Repeat((byte)0xFF, 16).ToArray());
        if (type == typeof(decimal)) return decimal.MinusOne; // (refused, but placed)
        if (!IsText(type)) return null;
        var text = RuntimeHelpers.GetUninitializedObject(type); // (an inline array: its first element set)
        type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0].SetValue(text, byte.MaxValue);
        return text;
    }

    private static int SizeOf(Type type)
    {
        if (type.IsEnum) return SizeOf(Enum.GetUnderlyingType(type));
        if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte)) return 1;
        if (type == typeof(short) || type == typeof(ushort) || type == typeof(char)) return 2;
        if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
        if (type == typeof(long) || type == typeof(ulong) || type == typeof(double) || type == typeof(DateTime)) return 8;
        if (type == typeof(Guid) || type == typeof(decimal)) return 16;
        return IsText(type) ? type.GetCustomAttribute<InlineArrayAttribute>()!.Length : 0;
    }

    private static bool IsInteger(Type type)
    {
        return type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);
    }

    private static bool IsUnsigned(Type type)
    {
        return type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong);
    }

    /// <summary>An inline array of bytes, 2 to 128 of them (<see cref="GridText" />).</summary>
    private static bool IsText(Type type)
    {
        var inline = type.GetCustomAttribute<InlineArrayAttribute>();
        var element = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return inline is { Length: >= 2 and <= 128 } && element.Length == 1 && element[0].FieldType == typeof(byte);
    }

    private static IReadOnlyDictionary<string, string> DictionaryOf(Type type, IReadOnlyDictionary<long, string>? given)
    {
        if (given is not null) return given.ToDictionary(e => e.Key.ToString(), e => e.Value);
        var names = new Dictionary<string, string>();
        foreach (var value in Enum.GetValuesAsUnderlyingType(type))
            names.TryAdd(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
                Enum.GetName(type, value) ?? "");
        return names;
    }

    private static string KindName(CellKind kind)
    {
        return kind switch
        {
            CellKind.Int => "int", CellKind.Fixed => "fixed", CellKind.Float => "float", CellKind.Time => "time",
            CellKind.Bool => "bool", CellKind.Enum => "enum", CellKind.Text => "text", CellKind.Percent => "percent",
            _ => "uuid"
        };
    }

    private static string? AlignOf(CellAlign align)
    {
        return align switch
        {
            CellAlign.Start => "start", CellAlign.Center => "center", CellAlign.End => "end", CellAlign.Left => "left",
            CellAlign.Right => "right", _ => null
        };
    }

    private static string? OverflowOf(CellOverflow overflow)
    {
        return overflow switch
        {
            CellOverflow.Clip => "clip", CellOverflow.EllipsisStart => "ellipsis-start", CellOverflow.ClipStart => "clip-start", _ => null
        };
    }
}

/// <summary>A grid's keys as the page holds them: their bytes, one character each (latin1), as tapline.js keys its replica.</summary>
internal static class GridKeys<TKey> where TKey : unmanaged
{
    private static readonly int Size = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(new TKey[1])).Length;

    /// <summary>
    ///     How the page compares the keys, to order a window sent without them (<see cref="GridOrder.ByKey" />): "guid" (a
    ///     <see cref="Guid" />'s bytes as .NET lays them out, compared as <see cref="Guid.CompareTo(Guid)" />), "int" or
    ///     "uint" (a little-endian integer). Null: it can't, so every order goes with its keys.
    /// </summary>
    public static readonly string? Order =
        typeof(TKey) == typeof(Guid) ? "guid"
        : typeof(TKey) == typeof(long) || typeof(TKey) == typeof(int) || typeof(TKey) == typeof(short) ||
          typeof(TKey) == typeof(sbyte) ? "int"
        : typeof(TKey) == typeof(ulong) || typeof(TKey) == typeof(uint) || typeof(TKey) == typeof(ushort) ||
          typeof(TKey) == typeof(byte) ? "uint"
        : null;

    public static string ToPage(in TKey key)
    {
        return string.Create(Size, key, static (chars, k) =>
        {
            var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in k));
            for (var i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
        });
    }

    /// <summary>A key from the page's text of it: false if it isn't one (another size, a character past 255).</summary>
    public static bool TryFromPage(string? text, out TKey key)
    {
        key = default;
        if (text is null || text.Length != Size) return false;
        Span<byte> bytes = stackalloc byte[Size];
        for (var i = 0; i < Size; i++)
        {
            if (text[i] > 0xFF) return false;
            bytes[i] = (byte)text[i];
        }

        key = MemoryMarshal.Read<TKey>(bytes);
        return true;
    }
}
