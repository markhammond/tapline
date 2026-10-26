using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tapline.Grid;

/// <summary>How a <see cref="LiveGrid{TKey,TRow}" />'s page formats a column's cells (tapline-grid.js's cell kinds).</summary>
public enum CellKind
{
    /// <summary>
    ///     From the field's type: an integer <see cref="Int" /> (<see cref="Fixed" /> with a scale), floating point
    ///     <see cref="Float" />, bool <see cref="Bool" />, an enum <see cref="Enum" />, a <see cref="DateTime" />
    ///     <see cref="Time" />, a <see cref="Guid" /> <see cref="Uuid" />, an inline array of bytes <see cref="Text" />.
    /// </summary>
    Auto,

    /// <summary>An integer: grouped in thousands; signed, its least value is no value (shown blank).</summary>
    Int,

    /// <summary>An integer scaled by 10^Scale: to Decimals places, or Figures significant; exact on hover.</summary>
    Fixed,

    /// <summary>A float or double, to Decimals places (2); whole on hover.</summary>
    Float,

    /// <summary>
    ///     A long of microseconds since 1970 (its least value: none), or a <see cref="DateTime" /> (MinValue: none),
    ///     shown as UTC: today's an ellipsis and the time, another day's its date too; to the microsecond on hover.
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

    /// <summary>Worked out on the page: one integer field over another (Of, Over), as a percentage. Never sorted by.</summary>
    Percent,

    /// <summary>A <see cref="Guid" />, as its text (Guid.Empty: none).</summary>
    Uuid
}

/// <summary>Which side a column's cells and heading keep to.</summary>
public enum CellAlign
{
    /// <summary>Numbers right, the rest left.</summary>
    Auto,
    Left,
    Right
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

/// <summary>A column as the page formats it (tapline-grid.js): a <see cref="LiveColumn" /> resolved against its row.</summary>
internal sealed record GridColumn
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public int Offset { get; init; }
    public int Size { get; init; }
    public bool? Unsigned { get; init; }
    public int? Scale { get; init; }
    public int? Decimals { get; init; }
    public int? Figures { get; init; }
    public string? Unit { get; init; }
    public IReadOnlyDictionary<string, string>? Dictionary { get; init; }
    public GridField? Of { get; init; }
    public GridField? Over { get; init; }
    public int? Width { get; init; }
    public bool Frozen { get; init; }
    public string? Align { get; init; }
    public bool Link { get; init; }

    /// <summary>The column it came from (not sent).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LiveColumn? Source { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Sortable { get; init; }
}

/// <summary>An integer field a computed column reads: where it is in the row.</summary>
internal sealed record GridField(int Offset, int Size, bool Unsigned);

/// <summary>A time field the page reads from each row (µs since 1970; <c>Unit</c> "ticks": a DateTime): where it is.</summary>
internal sealed record GridStamp(int Offset, int Size, string? Unit);

/// <summary>
///     Resolves <see cref="LiveColumn" />s against <typeparamref name="TRow" />'s fields: where each is in the row's bytes
///     (as Tapline sends them), its kind, and why a column can't work. A field's place is found as laid out in memory —
///     the row's fields set one at a time in a boxed row, the bytes that change seen — not as marshalled.
/// </summary>
internal static class GridColumns<TRow> where TRow : unmanaged
{
    private static readonly FieldInfo[] Fields =
        typeof(TRow).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>The row's size in bytes, as it goes on the wire.</summary>
    public static readonly int RowSize = MemoryMarshal.AsBytes(new ReadOnlySpan<TRow>(new TRow[1])).Length;

    /// <summary>The columns as the page formats them, or why they can't work.</summary>
    public static (GridColumn[] Columns, string? Refusal) Resolve(IReadOnlyList<LiveColumn> columns)
    {
        if (columns.Count == 0)
            return ([], "it has no columns: give it LiveColumn children, each naming a field of its rows.");
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
            var (one, refusal) = Resolve(column);
            if (refusal is not null) return ([], $"column \"{column.Name}\": {refusal}");
            resolved[i] = one!;
        }

        return (resolved, null);
    }

    private static (GridColumn? Column, string? Refusal) Resolve(LiveColumn c)
    {
        if (c.Width is < 1) return (null, $"a Width of {c.Width} px.");
        if (c.Scale is < 0 or > 18) return (null, $"a Scale of {c.Scale}: from 0 to 18.");
        if (c.Decimals is < 0 or > 18) return (null, $"{c.Decimals} Decimals: from 0 to 18.");
        if (c.Figures is < 1 or > 18) return (null, $"{c.Figures} Figures: from 1 to 18.");
        if (c.Kind == CellKind.Percent) return Percent(c);
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

        return (new GridColumn
        {
            Name = c.Name,
            Label = c.Label ?? c.Name,
            Kind = KindName(kind),
            Offset = offset,
            Size = size,
            Unsigned = integer || type.IsEnum ? IsUnsigned(type.IsEnum ? Enum.GetUnderlyingType(type) : type) : null,
            Scale = kind == CellKind.Fixed ? c.Scale : null,
            Decimals = c.Decimals,
            Figures = c.Figures,
            Unit = type == typeof(DateTime) ? "ticks" : null,
            Dictionary = kind == CellKind.Enum ? DictionaryOf(type, c.Dictionary) : null,
            Width = c.Width,
            Frozen = c.Frozen,
            Align = AlignOf(c.Align),
            Link = c.Link,
            Source = c,
            Sortable = c.Sortable ?? true
        }, null);
    }

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

    private static (GridColumn?, string?) Percent(LiveColumn c)
    {
        if (c.Sortable == true) return (null, "a Percent column is worked out on the page: it can't be sorted by.");
        if (c.Of is null || c.Over is null) return (null, "a Percent column needs Of and Over, the fields it divides.");
        var (of, ofRefusal) = IntegerField(c.Of);
        if (ofRefusal is not null) return (null, $"Of: {ofRefusal}");
        var (over, overRefusal) = IntegerField(c.Over);
        if (overRefusal is not null) return (null, $"Over: {overRefusal}");
        return (new GridColumn
        {
            Name = c.Name, Label = c.Label ?? c.Name, Kind = "percent", Of = of, Over = over, Decimals = c.Decimals,
            Width = c.Width, Frozen = c.Frozen, Align = AlignOf(c.Align), Link = c.Link, Source = c, Sortable = false
        }, null);
    }

    private static (GridField?, string?) IntegerField(string name)
    {
        var field = FieldOf(name);
        if (field is null) return (null, $"{typeof(TRow).Name} has no field \"{name}\".");
        if (!IsInteger(field.FieldType)) return (null, $"\"{name}\" is a {field.FieldType.Name}, not an integer.");
        return (new GridField(OffsetOf(field), SizeOf(field.FieldType), IsUnsigned(field.FieldType)), null);
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
        return align switch { CellAlign.Left => "left", CellAlign.Right => "right", _ => null };
    }
}

/// <summary>A grid's keys as the page holds them: their bytes, one character each (latin1), as tapline.js keys its replica.</summary>
internal static class GridKeys<TKey> where TKey : unmanaged
{
    private static readonly int Size = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(new TKey[1])).Length;

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
