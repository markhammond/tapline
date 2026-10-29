using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Apache.Arrow;
using Chalk.Arrow;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Tapline.Grid.Provider;

namespace Tapline.Grid.ChalkQL;

/// <summary>
///     A row type's columns, described once (grid-sources.md §3): its public fields as the store's columns, named as
///     ChalkQL's in-process tables name them; its key, version and stamp; each view's order, written as SQL for the store
///     and compared in .NET for the tail, from the same columns; and how a statement's rows read back into it.
/// </summary>
/// <remarks>
///     A field's column is named by the <see cref="PocoNamingPolicy" /> unless <see cref="ChalkColumnAttribute" /> names it,
///     and left out if <see cref="ChalkIgnoreAttribute" /> marks it. A view sorts by columns named as SQL names them (or as
///     their fields are), then by the key, newest first; with no sort, by the key alone, newest first. Numbers, booleans,
///     times, decimals and UUIDs compare in .NET as SQL compares them; an enum, which ChalkQL's tables hold by name, isn't
///     sorted by. A <see cref="DateTime" /> is UTC, and its column a <c>TIMESTAMP</c>, as ChalkQL's tables hold one: a wall
///     clock, the UTC one; so it's bound as one (of kind Unspecified, as ChalkQL binds a <c>TIMESTAMP</c>) and read back
///     as UTC.
/// </remarks>
public class ChalkRows<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private readonly Comparison<TRow>?[] _compare; // (null: not to be sorted by)
    private readonly FieldInfo[] _fields;
    private readonly Func<TRow, TKey> _key;
    private readonly int _keyColumn, _stampColumn;
    private readonly ConditionalWeakTable<GridView, ChalkOrder<TKey, TRow>> _orders = new();
    private readonly Reader[] _read;
    private readonly Func<TRow, object?>[] _value;
    private readonly Func<TRow, long> _version;

    /// <param name="key">The key's field: of <typeparamref name="TKey" />, never reused.</param>
    /// <param name="version">
    ///     The version's field, an integer rising within a key — or none, for rows that never change (a fill, a trade): each
    ///     its key's one version, the store's and the stream's the same row.
    /// </param>
    /// <param name="stamp">When the version happened: microseconds since the Unix epoch (an integer), or a <see cref="DateTime" /> (UTC).</param>
    /// <param name="naming">How fields are named as columns, as the store's tables and ChalkQL's name them.</param>
    public ChalkRows(string key, string? version, string stamp, PocoNamingPolicy naming = PocoNamingPolicy.SnakeCase)
    {
        Naming = naming;
        _fields = [.. typeof(TRow).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.GetCustomAttribute<ChalkIgnoreAttribute>() is null && f.GetCustomAttribute<ChalkColumnAttribute>() is not { Ignore: true })
            .OrderBy(f => f.MetadataToken)];
        Columns = [.. _fields.Select(f => f.GetCustomAttribute<ChalkColumnAttribute>()?.Name ?? Name(f.Name, naming))];
        _keyColumn = Field(key, "key");
        var versionColumn = version is null ? -1 : Field(version, "version");
        _stampColumn = Field(stamp, "stamp");
        if (_fields[_keyColumn].FieldType != typeof(TKey))
            throw new ArgumentException($"The key's field {key} is a {_fields[_keyColumn].FieldType.Name}, not the grid's key, a {typeof(TKey).Name}.", nameof(key));

        var row = Expression.Parameter(typeof(TRow), "row");
        _key = Expression.Lambda<Func<TRow, TKey>>(Expression.Field(row, _fields[_keyColumn]), row).Compile();
        _version = versionColumn < 0 ? static _ => 1
            : Expression.Lambda<Func<TRow, long>>(Expression.Convert(Expression.Field(row, _fields[versionColumn]), typeof(long)), row).Compile();
        _compare = [.. _fields.Select(Comparer)];
        _value = [.. _fields.Select(f => Expression.Lambda<Func<TRow, object?>>(Expression.Convert(Bound(Expression.Field(row, f)), typeof(object)), row)
            .Compile())];
        _read = [.. _fields.Select(ReaderOf)];
        Select = string.Join(", ", Columns);
    }

    /// <summary>How fields are named as columns.</summary>
    public PocoNamingPolicy Naming { get; }

    /// <summary>The columns, in the fields' order: what a statement selects, and reads back.</summary>
    public IReadOnlyList<string> Columns { get; }

    public string KeyColumn => Columns[_keyColumn];

    /// <summary>The key's field, by its name in the row.</summary>
    internal string KeyField => _fields[_keyColumn].Name;

    public string StampColumn => Columns[_stampColumn];

    /// <summary>The select list: every column, in the fields' order.</summary>
    internal string Select { get; }

    /// <summary>The stamp's field.</summary>
    internal FieldInfo StampField => _fields[_stampColumn];

    public TKey KeyOf(in TRow row)
    {
        return _key(row);
    }

    public long VersionOf(in TRow row)
    {
        return _version(row);
    }

    public IComparer<TRow> OrderOf(GridView view)
    {
        return Order(view);
    }

    public bool NewestFirstByKey(GridView view)
    {
        return view.Sort.Count == 0;
    }

    /// <summary>The view's order: the columns it sorts by, then the key, newest first — compared, and written as SQL.</summary>
    internal ChalkOrder<TKey, TRow> Order(GridView view)
    {
        return _orders.GetValue(view, v =>
        {
            var keys = new List<(int Column, bool Descending)>();
            foreach (var (name, descending) in v.Sort)
            {
                var column = Find(name);
                if (_compare[column] is null)
                    throw new GridRefusedException($"{name} can't be sorted by: ChalkQL holds an enum by its name, so SQL would sort it otherwise.");
                if (keys.All(k => k.Column != column)) keys.Add((column, descending));
            }

            if (keys.All(k => k.Column != _keyColumn)) keys.Add((_keyColumn, true)); // (ties: the newest first)
            return new ChalkOrder<TKey, TRow>(this, [.. keys]);
        });
    }

    internal string ColumnName(int column)
    {
        return Columns[column];
    }

    internal int Compare(int column, in TRow x, in TRow y)
    {
        return _compare[column]!(x, y);
    }

    internal object? Value(int column, in TRow row)
    {
        return _value[column](row);
    }

    /// <summary>A statement's row, its columns as <see cref="Select" /> lists them.</summary>
    internal TRow Read(RecordBatch batch, int index)
    {
        var row = default(TRow);
        for (var i = 0; i < _read.Length; i++) _read[i](ref row, batch.Column(i), index);
        return row;
    }

    /// <summary>The key's field, as ChalkQL's table builder names it.</summary>
    internal Expression<Func<TRow, object?>> KeyExpression()
    {
        var row = Expression.Parameter(typeof(TRow), "row");
        return Expression.Lambda<Func<TRow, object?>>(Expression.Convert(Expression.Field(row, _fields[_keyColumn]), typeof(object)), row);
    }

    /// <summary>A standing view's key, column 0 of its row: read as <typeparamref name="TKey" />.</summary>
    internal static TKey KeyFrom(in ViewRow row, int column)
    {
        if (typeof(TKey) == typeof(long)) return (TKey)(object)row.GetInt64(column);
        if (typeof(TKey) == typeof(int)) return (TKey)(object)row.GetInt32(column);
        if (typeof(TKey) == typeof(ulong)) return (TKey)(object)(ulong)row.GetInt64(column);
        if (typeof(TKey) == typeof(uint)) return (TKey)(object)(uint)row.GetInt64(column);
        if (typeof(TKey) == typeof(short)) return (TKey)(object)(short)row.GetInt64(column);
        if (typeof(TKey) == typeof(Guid)) return (TKey)row.GetValue(column)!;
        return (TKey)Convert.ChangeType(row.GetValue(column)!, typeof(TKey), CultureInfo.InvariantCulture);
    }

    private int Field(string name, string what)
    {
        var at = System.Array.FindIndex(_fields, f => f.Name == name);
        return at >= 0 ? at : throw new ArgumentException($"{typeof(TRow).Name} has no public field {name} for its {what}.", what);
    }

    private int Find(string name)
    {
        var at = -1;
        for (var i = 0; i < Columns.Count && at < 0; i++)
            if (string.Equals(Columns[i], name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
                at = i;
        return at >= 0 ? at : throw new GridRefusedException($"{typeof(TRow).Name} has no column {name} to sort by.");
    }

    private static Comparison<TRow>? Comparer(FieldInfo field)
    {
        if (field.FieldType.IsEnum) return null;
        if (InlineText.Is(field.FieldType)) // (text inline: its UTF-8 byte by byte, as SQL's binary collation orders text)
        {
            ParameterExpression a = Expression.Parameter(typeof(TRow), "x"), b = Expression.Parameter(typeof(TRow), "y");
            return Expression.Lambda<Comparison<TRow>>(Expression.Call(typeof(InlineText).GetMethod(nameof(InlineText.Compare))!
                .MakeGenericMethod(field.FieldType), Expression.Field(a, field), Expression.Field(b, field)), a, b).Compile();
        }

        var compareTo = field.FieldType.GetMethod(nameof(IComparable<int>.CompareTo), [field.FieldType]);
        if (compareTo is null) return null;
        ParameterExpression x = Expression.Parameter(typeof(TRow), "x"), y = Expression.Parameter(typeof(TRow), "y");
        return Expression.Lambda<Comparison<TRow>>(Expression.Call(Expression.Field(x, field), compareTo, Expression.Field(y, field)), x, y).Compile();
    }

    /// <summary>
    ///     A <c>TIMESTAMP</c>'s value for a UTC time: its wall clock (ChalkQL refuses a UTC <see cref="DateTime" />, an instant,
    ///     for one).
    /// </summary>
    internal static DateTime WallClock(DateTime time)
    {
        return DateTime.SpecifyKind(time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time, DateTimeKind.Unspecified);
    }

    /// <summary>A field as it's bound: text inline as the string it holds, a time as its wall clock; else as it is.</summary>
    private static Expression Bound(MemberExpression field)
    {
        if (InlineText.Is(field.Type)) return InlineText.StringOf(field);
        if (field.Type == typeof(DateTime))
            return Expression.Call(typeof(ChalkRows<TKey, TRow>).GetMethod(nameof(WallClock), BindingFlags.NonPublic | BindingFlags.Static)!, field);
        return field;
    }

    private static Reader ReaderOf(FieldInfo field)
    {
        var type = field.FieldType;
        var row = Expression.Parameter(typeof(TRow).MakeByRefType(), "row");
        var column = Expression.Parameter(typeof(IArrowArray), "column");
        var index = Expression.Parameter(typeof(int), "index");
        var read = type switch
        {
            _ when type == typeof(long) || type == typeof(int) || type == typeof(short) || type == typeof(sbyte) || type == typeof(ulong) ||
                   type == typeof(uint) || type == typeof(ushort) || type == typeof(byte) || type == typeof(char) => Of(nameof(ArrowValues.Int64)),
            _ when type == typeof(double) || type == typeof(float) => Of(nameof(ArrowValues.Double)),
            _ when type == typeof(bool) => Of(nameof(ArrowValues.Boolean)),
            _ when type == typeof(decimal) => Of(nameof(ArrowValues.Decimal)),
            _ when type == typeof(DateTime) => Of(nameof(ArrowValues.Time)),
            _ when type == typeof(Guid) => Of(nameof(ArrowValues.Uuid)),
            _ when type.IsEnum => Expression.Call(typeof(ArrowValues), nameof(ArrowValues.Named), [type], column, index),
            _ when InlineText.Is(type) => Expression.Call(typeof(ArrowValues), nameof(ArrowValues.Text), [type], column, index),
            _ => throw new ArgumentException($"{typeof(TRow).Name}.{field.Name}, a {type.Name}, isn't read from a statement's rows.", nameof(field))
        };
        var body = Expression.Assign(Expression.Field(row, field), read.Type == type ? read : Expression.Convert(read, type));
        return Expression.Lambda<Reader>(body, row, column, index).Compile();

        Expression Of(string method)
        {
            return Expression.Call(typeof(ArrowValues).GetMethod(method)!, column, index);
        }
    }

    private static string Name(string member, PocoNamingPolicy naming)
    {
        if (naming != PocoNamingPolicy.SnakeCase) return member;
        var name = new StringBuilder(member.Length + 4); // (as ChalkQL names a member: split where a reader would)
        for (var i = 0; i < member.Length; i++)
        {
            var c = member[i];
            if (!char.IsUpper(c))
            {
                name.Append(c);
                continue;
            }

            if (i > 0 && (char.IsUpper(member[i - 1]) ? i + 1 < member.Length && char.IsLower(member[i + 1]) : member[i - 1] != '_'))
                name.Append('_');
            name.Append(char.ToLowerInvariant(c));
        }

        return name.ToString();
    }

    internal delegate void Reader(ref TRow row, IArrowArray column, int index);
}

/// <summary>
///     A view's order over a row's columns: compared in .NET (the tail's rows), and written as SQL (the store's) — its
///     <c>ORDER BY</c> both ways, and the keyset seeks from a row's place, the row's values bound as parameters.
/// </summary>
internal sealed class ChalkOrder<TKey, TRow> : IComparer<TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private readonly (int Column, bool Descending)[] _keys;
    private readonly string[] _parameters;
    private readonly ChalkRows<TKey, TRow> _rows;

    public ChalkOrder(ChalkRows<TKey, TRow> rows, (int Column, bool Descending)[] keys)
    {
        (_rows, _keys) = (rows, keys);
        _parameters = [.. keys.Select((_, i) => $"grid_k{i}")];
        (string Name, bool Descending)[] named = [.. keys.Select(k => (rows.ColumnName(k.Column), k.Descending))];
        (string Name, bool Descending)[] reversed = [.. named.Select(k => (k.Name, !k.Descending))];
        By = string.Join(", ", named.Select(k => k.Name + (k.Descending ? " DESC" : " ASC")));
        ReversedBy = string.Join(", ", reversed.Select(k => k.Name + (k.Descending ? " DESC" : " ASC")));
        From = Seek(named, true);
        Before = Seek(reversed, false);
    }

    /// <summary>The <c>ORDER BY</c> list.</summary>
    public string By { get; }

    /// <summary>The <c>ORDER BY</c> list turned round: back from a place, nearest first.</summary>
    public string ReversedBy { get; }

    /// <summary>The condition "sorts at or after the place bound" (<see cref="Bind" />).</summary>
    public string From { get; }

    /// <summary>The condition "sorts before the place bound".</summary>
    public string Before { get; }

    public int Compare(TRow x, TRow y)
    {
        foreach (var (column, descending) in _keys)
        {
            var c = _rows.Compare(column, x, y);
            if (c != 0) return descending ? -c : c;
        }

        return 0;
    }

    /// <summary>A place's values, as the seeks name them.</summary>
    public void Bind(in TRow place, Dictionary<string, object?> values)
    {
        for (var i = 0; i < _keys.Length; i++) values[_parameters[i]] = _rows.Value(_keys[i].Column, place);
    }

    /// <summary>
    ///     Written out column by column (<c>a &gt; @k0 OR (a = @k0 AND b &lt; @k1 …)</c>), the first column's bound repeated
    ///     alone in front: a plain range on the leading column, so the database starts its index there.
    /// </summary>
    private static string Seek((string Name, bool Descending)[] keys, bool inclusive)
    {
        var text = new StringBuilder();
        var closing = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0)
            {
                text.Append(" OR (").Append(keys[i - 1].Name).Append(" = @grid_k").Append(i - 1).Append(" AND (");
                closing += 2;
            }

            text.Append(keys[i].Name).Append(keys[i].Descending ? " <" : " >").Append(i == keys.Length - 1 && inclusive ? "=" : "")
                .Append(" @grid_k").Append(i);
        }

        return $"{keys[0].Name} {(keys[0].Descending ? "<=" : ">=")} @grid_k0 AND ({text.Append(')', closing)})";
    }
}

/// <summary>A statement's values, from ChalkQL's Arrow arrays, as a row's fields hold them.</summary>
internal static class ArrowValues
{
    public static long Int64(IArrowArray column, int index)
    {
        return column switch
        {
            Int64Array a => a.GetValue(index) ?? 0,
            Int32Array a => a.GetValue(index) ?? 0,
            Int16Array a => a.GetValue(index) ?? 0,
            Int8Array a => a.GetValue(index) ?? 0,
            UInt64Array a => (long)(a.GetValue(index) ?? 0),
            UInt32Array a => a.GetValue(index) ?? 0,
            UInt16Array a => a.GetValue(index) ?? 0,
            UInt8Array a => a.GetValue(index) ?? 0,
            BooleanArray a => a.GetValue(index) == true ? 1 : 0,
            TimestampArray a => a.GetTimestamp(index) is { } t ? (t.UtcTicks - DateTime.UnixEpoch.Ticks) / 10 : 0,
            DoubleArray a => (long)(a.GetValue(index) ?? 0),
            Decimal128Array a => (long)(a.GetValue(index) ?? 0),
            _ => Convert.ToInt64(RecordBatchExtensions.ValueAt(column, index) ?? 0L, CultureInfo.InvariantCulture)
        };
    }

    public static double Double(IArrowArray column, int index)
    {
        return column switch
        {
            DoubleArray a => a.GetValue(index) ?? 0,
            FloatArray a => a.GetValue(index) ?? 0,
            Decimal128Array a => (double)(a.GetValue(index) ?? 0),
            _ => Int64(column, index)
        };
    }

    public static bool Boolean(IArrowArray column, int index)
    {
        return column is BooleanArray a ? a.GetValue(index) ?? false : Int64(column, index) != 0;
    }

    public static decimal Decimal(IArrowArray column, int index)
    {
        return column switch
        {
            Decimal128Array a => a.GetValue(index) ?? 0,
            DoubleArray a => (decimal)(a.GetValue(index) ?? 0),
            _ => Int64(column, index)
        };
    }

    public static DateTime Time(IArrowArray column, int index)
    {
        return column switch
        {
            TimestampArray a => a.GetTimestamp(index)?.UtcDateTime ?? default,
            Date32Array a => a.GetDateTime(index) ?? default,
            Date64Array a => a.GetDateTime(index) ?? default,
            _ => DateTime.UnixEpoch.AddTicks(Int64(column, index) * 10)
        };
    }

    public static Guid Uuid(IArrowArray column, int index)
    {
        return RecordBatchExtensions.ValueAt(column, index) is Guid uuid ? uuid : default;
    }

    /// <summary>A text column's value into a field of text inline (GridText's layout), its UTF-8 copied: cut short if it doesn't fit.</summary>
    public static T Text<T>(IArrowArray column, int index) where T : struct
    {
        var field = default(T);
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref field, 1));
        if (column is StringArray strings)
        {
            if (!strings.IsNull(index)) GridText.Write(bytes, strings.GetBytes(index));
        }
        else if (RecordBatchExtensions.ValueAt(column, index) is string text)
        {
            GridText.Write(bytes, text.AsSpan());
        }

        return field;
    }

    public static T Named<T>(IArrowArray column, int index) where T : struct, Enum
    {
        return RecordBatchExtensions.ValueAt(column, index) switch
        {
            string name => Enum.Parse<T>(name),
            { } value => (T)Enum.ToObject(typeof(T), value),
            null => default
        };
    }
}

/// <summary>
///     Text inline in a row, as Tapline writes it (<see cref="GridText" />): an inline array of bytes, its first the length
///     (0x80 set if cut), then the UTF-8. As a column it's text: compared byte by byte, bound as a string.
/// </summary>
internal static class InlineText
{
    /// <summary>Whether a field's type is text inline: an inline array of bytes.</summary>
    public static bool Is(Type type)
    {
        return type.IsValueType && type.GetCustomAttribute<InlineArrayAttribute>() is { Length: >= 2 and <= 128 } &&
               type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is [{ FieldType: var element }] &&
               element == typeof(byte);
    }

    public static ReadOnlySpan<byte> Bytes<T>(in T field) where T : struct
    {
        var all = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in field, 1));
        return all.Slice(1, Math.Min(all[0] & 0x7F, all.Length - 1));
    }

    public static int Compare<T>(T a, T b) where T : struct
    {
        return Bytes(in a).SequenceCompareTo(Bytes(in b));
    }

    public static string String<T>(T field) where T : struct
    {
        return Encoding.UTF8.GetString(Bytes(in field));
    }

    /// <summary>An expression of the string a field of text inline holds.</summary>
    public static Expression StringOf(Expression field)
    {
        return Expression.Call(typeof(InlineText).GetMethod(nameof(String))!.MakeGenericMethod(field.Type), field);
    }
}

/// <summary>
///     The row's columns (<see cref="ChalkRows{TKey,TRow}" />) and its stamp, of its field's own type: a
///     <see cref="DateTime" /> (UTC), or a long of <c>stampsPerSecond</c> since the Unix epoch — microseconds unless said,
///     nanoseconds, say. The stamp goes to SQL as its column holds it: a long as it is, a time as a <c>TIMESTAMP</c>'s wall
///     clock.
/// </summary>
public sealed class ChalkRows<TKey, TRow, TStamp> : ChalkRows<TKey, TRow>, IGridRows<TKey, TRow, TStamp>
    where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TStamp : struct, IComparable<TStamp>
{
    private readonly long _perSecond;
    private readonly Func<TRow, TStamp> _stamp;

    /// <param name="key">The key's field: of <typeparamref name="TKey" />, never reused.</param>
    /// <param name="version">The version's field, an integer rising within a key; none for rows that never change.</param>
    /// <param name="stamp">When the version happened: a field of <typeparamref name="TStamp" />, a <see cref="DateTime" /> or a long.</param>
    /// <param name="naming">How fields are named as columns, as the store's tables and ChalkQL's name them.</param>
    /// <param name="stampsPerSecond">A long stamp's unit, as many to a second: a million (microseconds) unless said.</param>
    public ChalkRows(string key, string? version, string stamp, PocoNamingPolicy naming = PocoNamingPolicy.SnakeCase,
        long stampsPerSecond = 1_000_000)
        : base(key, version, stamp, naming)
    {
        if (StampField.FieldType != typeof(TStamp))
            throw new ArgumentException($"The stamp's field {stamp} is a {StampField.FieldType.Name}, not a {typeof(TStamp).Name}.", nameof(stamp));
        if (typeof(TStamp) != typeof(DateTime) && typeof(TStamp) != typeof(long))
            throw new ArgumentException($"A stamp is a DateTime or a long, not a {typeof(TStamp).Name}.", nameof(stamp));
        if (stampsPerSecond < 1)
            throw new ArgumentOutOfRangeException(nameof(stampsPerSecond), stampsPerSecond, "A stamp's unit is at least one a second.");
        _perSecond = stampsPerSecond;
        var row = Expression.Parameter(typeof(TRow), "row");
        _stamp = Expression.Lambda<Func<TRow, TStamp>>(Expression.Field(row, StampField), row).Compile();
    }

    public TStamp StampOf(in TRow row)
    {
        return _stamp(row);
    }


    public TStamp Back(TStamp stamp, TimeSpan span)
    {
        if (typeof(TStamp) == typeof(DateTime))
        {
            var time = (DateTime)(object)stamp;
            return (TStamp)(object)(time.Ticks < span.Ticks ? DateTime.MinValue : time - span);
        }

        var units = (long)(object)stamp;
        var back = (long)((decimal)span.Ticks * _perSecond / TimeSpan.TicksPerSecond);
        return (TStamp)(object)(units < long.MinValue + back ? long.MinValue : units - back);
    }

    public TimeSpan Between(TStamp earlier, TStamp later)
    {
        if (typeof(TStamp) == typeof(DateTime)) return (DateTime)(object)later - (DateTime)(object)earlier;
        return TimeSpan.FromTicks((long)((decimal)((long)(object)later - (long)(object)earlier) * TimeSpan.TicksPerSecond / _perSecond));
    }

    public TStamp StampAt(DateTimeOffset time)
    {
        if (typeof(TStamp) == typeof(DateTime)) return (TStamp)(object)time.UtcDateTime;
        return (TStamp)(object)(long)((decimal)(time - DateTimeOffset.UnixEpoch).Ticks * _perSecond / TimeSpan.TicksPerSecond);
    }

    /// <summary>A stamp as a statement binds it: a long as it is, a time as its wall clock.</summary>
    internal object Parameter(TStamp stamp)
    {
        return typeof(TStamp) == typeof(DateTime) ? WallClock((DateTime)(object)stamp) : stamp;
    }

    /// <summary>A stamp a statement answered (the newest).</summary>
    internal TStamp StampFrom(IArrowArray column, int index)
    {
        return typeof(TStamp) == typeof(DateTime)
            ? (TStamp)(object)DateTime.SpecifyKind(ArrowValues.Time(column, index), DateTimeKind.Utc)
            : (TStamp)(object)ArrowValues.Int64(column, index);
    }
}
