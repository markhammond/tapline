using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Tapline;

/// <summary>
///     How a topic's payloads are laid out, so the router's viewer can show them as JSON — described by its provider in
///     step 1
///     (<see cref="SubscribeRequest.Accept" />), before the subscriber's first record, or set where the topic is published
///     (<see cref="Source.Format" />) — and, optionally, where its provider renders them for a person (
///     <see cref="ViewUrl" />).
///     Data, not types: a provider in another process sends it as its JSON form (<see cref="WriteTo" />,
///     <see cref="Parse" />),
///     and the router reads payloads by it without the provider's code.
/// </summary>
public sealed record PayloadFormat
{
    private readonly string? _contentType;
    private readonly Uri? _viewUrl;

    /// <summary>
    ///     A media type — <c>application/json</c>, <c>text/plain</c>…: a value's or a message's payload, JSON shown as it
    ///     is, text as a string.
    /// </summary>
    public string? ContentType
    {
        get => _contentType;
        init => _contentType = value is { Length: > 256 }
            ? throw new ArgumentException("A content type is at most 256 characters.", nameof(value))
            : value;
    }

    /// <summary>
    ///     A fixed-size payload's fields: a table's rows, or a value's or a message's payload that is exactly its
    ///     <see cref="PayloadLayout.Size" />.
    /// </summary>
    public PayloadLayout? Layout { get; init; }

    /// <summary>A table's keys' fields.</summary>
    public PayloadLayout? KeyLayout { get; init; }

    /// <summary>
    ///     Where the provider renders the topic for a person: the router's viewer POSTs it a topic's state or a record, as
    ///     JSON
    ///     (the router's own rendering, by this format), when a page asks — never otherwise — and shows the HTML it answers in
    ///     a sandboxed frame. Absolute http(s), no credentials in it; given in step 1, on the provider's own host (its
    ///     endpoint's; a provider in this process, the loopback).
    /// </summary>
    public Uri? ViewUrl
    {
        get => _viewUrl;
        init => _viewUrl = value is null || (value.IsAbsoluteUri && value.Scheme is "http" or "https" &&
                                             value.UserInfo.Length == 0 && value.OriginalString.Length <= 2048)
            ? value
            : throw new ArgumentException(
                "A view URL is absolute http(s), at most 2048 characters, with no credentials in it.", nameof(value));
    }

    /// <summary>
    ///     The most of the topic's records the router may keep for its viewer (<see cref="RecordRetention" />) — a provider's
    ///     ceiling, for payloads it would rather the router didn't hold (personal data, say): it lowers the router's choice,
    ///     never raises it. Null: no ceiling.
    /// </summary>
    public RecordRetention? Retention { get; init; }

    /// <summary>
    ///     Whether <paramref name="view" /> is on the host of the provider at <paramref name="endpoint" />: its
    ///     endpoint's host, or — in this process — the loopback.
    /// </summary>
    internal static bool OnProviderHost(Uri view, ProviderEndpoint endpoint)
    {
        var at = endpoint.Address;
        if (at.Scheme == "inproc")
            return view.IsLoopback || view.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        return view.IdnHost.Equals(at.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Its JSON form: <c>{"contentType": …, "layout": {…}, "keyLayout": {…}, "viewUrl": …, "retention": …}</c>, each
    ///     present if set.
    /// </summary>
    public void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        if (ContentType is not null) w.WriteString("contentType", ContentType);
        if (Layout is not null)
        {
            w.WritePropertyName("layout");
            Layout.WriteTo(w);
        }

        if (KeyLayout is not null)
        {
            w.WritePropertyName("keyLayout");
            KeyLayout.WriteTo(w);
        }

        if (ViewUrl is not null) w.WriteString("viewUrl", ViewUrl.OriginalString);
        if (Retention is { } retention)
            w.WriteString("retention",
                retention switch { RecordRetention.None => "none", RecordRetention.Prefix => "prefix", _ => "whole" });
        w.WriteEndObject();
    }

    /// <summary>
    ///     A format from its JSON form (properties it doesn't know ignored); <see cref="FormatException" /> if it isn't
    ///     one.
    /// </summary>
    public static PayloadFormat Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 4 * PayloadLayout.MaxDepth + 4 });
            using var document = JsonDocument.ParseValue(ref reader);
            var e = document.RootElement;
            if (e.ValueKind != JsonValueKind.Object) throw new FormatException("A payload format is a JSON object.");
            return new PayloadFormat
            {
                ContentType = e.TryGetProperty("contentType", out var c) ? c.GetString() : null,
                Layout = e.TryGetProperty("layout", out var l) ? PayloadLayout.Read(l, 1) : null,
                KeyLayout = e.TryGetProperty("keyLayout", out var k) ? PayloadLayout.Read(k, 1) : null,
                ViewUrl = e.TryGetProperty("viewUrl", out var v)
                    ? new Uri(v.GetString()!, UriKind.RelativeOrAbsolute)
                    : null,
                Retention = e.TryGetProperty("retention", out var r)
                    ? r.GetString() switch
                    {
                        "none" => RecordRetention.None, "prefix" => RecordRetention.Prefix,
                        "whole" => RecordRetention.Whole,
                        var other => throw new ArgumentException(
                            $"No such retention: \"{other}\" (none, prefix or whole).")
                    }
                    : null
            };
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException
                                      or KeyNotFoundException or UriFormatException)
        {
            throw new FormatException($"Not a payload format: {e.Message}", e);
        }
    }
}

/// <summary>What a field of a fixed-size payload is (little-endian, as Tapline's records are).</summary>
public enum FieldKind
{
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Int128,
    UInt128,
    Float16,
    Float32,
    Float64,

    /// <summary>One byte: false if zero.</summary>
    Bool,

    /// <summary>A UTF-16 code unit.</summary>
    Char,

    /// <summary><see cref="PayloadField.Length" /> bytes, shown as hex.</summary>
    Bytes,

    /// <summary><see cref="PayloadField.Length" /> bytes of UTF-8 text, through its first zero byte.</summary>
    Utf8,

    /// <summary>Fields of its own: <see cref="PayloadField.Layout" />.</summary>
    Struct,

    /// <summary>.NET's <see cref="decimal" />, as it is in memory (16 bytes).</summary>
    Decimal,

    /// <summary>.NET's <see cref="System.Guid" />, as it is in memory (16 bytes).</summary>
    Guid,

    /// <summary>.NET's <see cref="System.DateTime" />, as it is in memory: ticks in its low 62 bits, kind in its top two.</summary>
    DateTime,

    /// <summary>.NET's <see cref="System.TimeSpan" />: ticks.</summary>
    TimeSpan
}

/// <summary>
///     A field of a fixed-size payload: <paramref name="Name" /> ("" if the payload is this field alone: shown bare, not
///     as
///     an object), where it is, and what it is.
/// </summary>
public sealed record PayloadField(string Name, int Offset, FieldKind Kind)
{
    /// <summary><see cref="FieldKind.Bytes" />, <see cref="FieldKind.Utf8" />: its length in bytes.</summary>
    public int Length { get; init; }

    /// <summary><see cref="FieldKind.Struct" />: its fields.</summary>
    public PayloadLayout? Layout { get; init; }

    /// <summary>An integer of up to 64 bits shown as a decimal with this many places (0–18): 6500000 at 2 is 65000.00.</summary>
    public int Scale { get; init; }

    /// <summary>An integer of up to 64 bits shown by name where it has one (an enum's members, say).</summary>
    public IReadOnlyDictionary<long, string>? Names { get; init; }

    /// <summary>Its size in bytes.</summary>
    public int Size => Kind switch
    {
        FieldKind.Int8 or FieldKind.UInt8 or FieldKind.Bool => 1,
        FieldKind.Int16 or FieldKind.UInt16 or FieldKind.Float16 or FieldKind.Char => 2,
        FieldKind.Int32 or FieldKind.UInt32 or FieldKind.Float32 => 4,
        FieldKind.Int64 or FieldKind.UInt64 or FieldKind.Float64 or FieldKind.DateTime or FieldKind.TimeSpan => 8,
        FieldKind.Int128 or FieldKind.UInt128 or FieldKind.Decimal or FieldKind.Guid => 16,
        FieldKind.Bytes or FieldKind.Utf8 => Length,
        FieldKind.Struct => Layout?.Size ?? 0,
        _ => 0
    };

    internal bool IsInteger => Kind <= FieldKind.UInt64;

    internal void Validate(int size)
    {
        if (Name is null || Name.Length > 256) throw new ArgumentException("A field's name is at most 256 characters.");
        if ((uint)Kind > (uint)FieldKind.TimeSpan)
            throw new ArgumentException($"Field \"{Name}\": no such kind ({(int)Kind}).");
        if (Kind is FieldKind.Bytes or FieldKind.Utf8 != (Length != 0) || Length < 0)
            throw new ArgumentException(
                $"Field \"{Name}\": a length is given for bytes and UTF-8 text (positive), and for nothing else.");
        if (Kind == FieldKind.Struct != Layout is not null)
            throw new ArgumentException($"Field \"{Name}\": a struct has a layout, and nothing else does.");
        if (Scale is < 0 or > 18 || (Scale != 0 && !IsInteger))
            throw new ArgumentException($"Field \"{Name}\": a scale (0–18) is for an integer of up to 64 bits.");
        if (Names is not null && (!IsInteger || Names.Count > PayloadLayout.MaxFields ||
                                  Names.Values.Any(n => n is null || n.Length > 256)))
            throw new ArgumentException(
                $"Field \"{Name}\": names (at most {PayloadLayout.MaxFields}, each at most 256 characters) are for an integer of up to 64 bits.");
        if (Offset < 0 || Size <= 0 || (long)Offset + Size > size)
            throw new ArgumentException($"Field \"{Name}\": {Size} bytes at {Offset} don't fit a payload of {size}.");
    }

    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("name", Name);
        w.WriteNumber("offset", Offset);
        w.WriteString("kind", PayloadLayout.KindName(Kind));
        if (Length != 0) w.WriteNumber("length", Length);
        if (Scale != 0) w.WriteNumber("scale", Scale);
        if (Names is not null)
        {
            w.WriteStartObject("names");
            foreach (var (value, name) in Names.OrderBy(n => n.Key))
                w.WriteString(value.ToString(CultureInfo.InvariantCulture), name);
            w.WriteEndObject();
        }

        if (Layout is not null)
        {
            w.WritePropertyName("layout");
            Layout.WriteTo(w);
        }

        w.WriteEndObject();
    }

    /// <summary>The field as JSON, read from <paramref name="payload" /> (all of it: its offset counts from its start).</summary>
    internal void Render(Utf8JsonWriter w, ReadOnlySpan<byte> payload)
    {
        var b = payload.Slice(Offset, Size);
        switch (Kind)
        {
            case FieldKind.Int8: Integer(w, (sbyte)b[0]); break;
            case FieldKind.UInt8: Integer(w, b[0]); break;
            case FieldKind.Int16: Integer(w, BinaryPrimitives.ReadInt16LittleEndian(b)); break;
            case FieldKind.UInt16: Integer(w, BinaryPrimitives.ReadUInt16LittleEndian(b)); break;
            case FieldKind.Int32: Integer(w, BinaryPrimitives.ReadInt32LittleEndian(b)); break;
            case FieldKind.UInt32: Integer(w, BinaryPrimitives.ReadUInt32LittleEndian(b)); break;
            case FieldKind.Int64: Integer(w, BinaryPrimitives.ReadInt64LittleEndian(b)); break;
            case FieldKind.UInt64: Unsigned(w, BinaryPrimitives.ReadUInt64LittleEndian(b)); break;
            case FieldKind.Int128:
                w.WriteRawValue(BinaryPrimitives.ReadInt128LittleEndian(b)
                    .ToString(CultureInfo.InvariantCulture)); break;
            case FieldKind.UInt128:
                w.WriteRawValue(BinaryPrimitives.ReadUInt128LittleEndian(b)
                    .ToString(CultureInfo.InvariantCulture)); break;
            case FieldKind.Float16: Real(w, (double)BinaryPrimitives.ReadHalfLittleEndian(b)); break;
            case FieldKind.Float32: Real(w, BinaryPrimitives.ReadSingleLittleEndian(b)); break;
            case FieldKind.Float64: Real(w, BinaryPrimitives.ReadDoubleLittleEndian(b)); break;
            case FieldKind.Bool: w.WriteBooleanValue(b[0] != 0); break;
            case FieldKind.Char:
                var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(b);
                if (char.IsSurrogate(c)) w.WriteNumberValue(c); // half a pair: its code, as a number
                else w.WriteStringValue(c.ToString());
                break;
            case FieldKind.Bytes: w.WriteStringValue(Convert.ToHexString(b)); break;
            case FieldKind.Utf8:
                w.WriteStringValue(Encoding.UTF8.GetString(b.IndexOf((byte)0) is >= 0 and var end ? b[..end] : b));
                break;
            case FieldKind.Struct: Layout!.Render(w, b); break;
            case FieldKind.Decimal:
                var flags = BinaryPrimitives.ReadInt32LittleEndian(b);
                var hi = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
                var lo = BinaryPrimitives.ReadUInt64LittleEndian(b[8..]);
                var places = (flags >> 16) & 0xFF;
                if ((flags & 0x7F00FFFF) != 0 || places > 28)
                    w.WriteStringValue(Convert.ToHexString(b)); // not a decimal
                else w.WriteNumberValue(new decimal((int)lo, (int)(lo >> 32), (int)hi, flags < 0, (byte)places));
                break;
            case FieldKind.Guid: w.WriteStringValue(new Guid(b).ToString()); break;
            case FieldKind.DateTime:
                var data = BinaryPrimitives.ReadUInt64LittleEndian(b);
                var ticks = (long)(data & 0x3FFF_FFFF_FFFF_FFFF);
                if (ticks > DateTime.MaxValue.Ticks)
                {
                    w.WriteStringValue(Convert.ToHexString(b));
                    break;
                }

                var kind = (data >> 62) switch
                {
                    0 => DateTimeKind.Unspecified, 1 => DateTimeKind.Utc, _ => DateTimeKind.Local
                };
                w.WriteStringValue(new DateTime(ticks, kind).ToString("O", CultureInfo.InvariantCulture));
                break;
            case FieldKind.TimeSpan:
                w.WriteStringValue(TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(b))
                    .ToString("c", CultureInfo.InvariantCulture)); break;
        }
    }

    private void Integer(Utf8JsonWriter w, long value)
    {
        if (Names is not null && Names.TryGetValue(value, out var name))
        {
            w.WriteStringValue(name);
        }
        else if (Scale == 0)
        {
            w.WriteNumberValue(value);
        }
        else
        {
            var magnitude = value < 0 ? (ulong)-(value + 1) + 1 : (ulong)value;
            w.WriteNumberValue(new decimal((int)magnitude, (int)(magnitude >> 32), 0, value < 0, (byte)Scale));
        }
    }

    private void Unsigned(Utf8JsonWriter w, ulong value)
    {
        if (Names is not null && Names.TryGetValue(unchecked((long)value), out var name)) w.WriteStringValue(name);
        else if (Scale == 0) w.WriteNumberValue(value);
        else w.WriteNumberValue(new decimal((int)value, (int)(value >> 32), 0, false, (byte)Scale));
    }

    private static void Real(Utf8JsonWriter w, double value)
    {
        if (double.IsFinite(value)) w.WriteNumberValue(value);
        else w.WriteStringValue(value.ToString(CultureInfo.InvariantCulture)); // NaN, ∞: not JSON numbers
    }
}

/// <summary>
///     The fields of a fixed-size payload of <see cref="Size" /> bytes — a table's key or row, say — each at its offset,
///     in
///     the order shown. Checked when made: every field within the payload, names distinct, nesting at most
///     <see cref="MaxDepth" /> deep, at most <see cref="MaxFields" /> fields in all. A payload whose only field is named
///     ""
///     is shown as that field, bare.
/// </summary>
public sealed class PayloadLayout
{
    public const int MaxDepth = 8, MaxFields = 1024, MaxSize = 1 << 20;

    private static readonly ConcurrentDictionary<Type, PayloadLayout?> s_derived = new();

    private static readonly string[] s_kindNames =
    [
        "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "int128", "uint128",
        "float16", "float32", "float64", "bool", "char", "bytes", "utf8", "struct", "decimal", "guid", "datetime",
        "timespan"
    ];

    private readonly PayloadField[] _fields;

    public PayloadLayout(int size, IEnumerable<PayloadField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (size is <= 0 or > MaxSize)
            throw new ArgumentOutOfRangeException(nameof(size), size, $"A layout is 1 to {MaxSize} bytes.");
        _fields = fields.ToArray();
        Size = size;
        var names = new HashSet<string>(StringComparer.Ordinal);
        int count = _fields.Length, depth = 1;
        foreach (var f in _fields)
        {
            ArgumentNullException.ThrowIfNull(f, nameof(fields));
            f.Validate(size);
            if (!names.Add(f.Name)) throw new ArgumentException($"Two fields are named \"{f.Name}\".", nameof(fields));
            if (f.Name.Length == 0 && _fields.Length > 1)
                throw new ArgumentException("A field is unnamed only if it is the payload's only one.", nameof(fields));
            if (f.Layout is { } nested)
            {
                count += nested.Count;
                depth = Math.Max(depth, nested.Depth + 1);
            }
        }

        if (depth > MaxDepth) throw new ArgumentException($"Fields nest at most {MaxDepth} deep.", nameof(fields));
        if (count > MaxFields)
            throw new ArgumentException($"A layout has at most {MaxFields} fields in all.", nameof(fields));
        (Count, Depth) = (count, depth);
    }

    /// <summary>The payload's size in bytes.</summary>
    public int Size { get; }

    public IReadOnlyList<PayloadField> Fields => _fields;

    internal int Count { get; }
    internal int Depth { get; }

    /// <summary>
    ///     This layout with the field named <paramref name="name" /> changed — a scale or names added to a derived one,
    ///     say.
    /// </summary>
    public PayloadLayout With(string name, Func<PayloadField, PayloadField> change)
    {
        var i = Array.FindIndex(_fields, f => f.Name == name);
        if (i < 0) throw new ArgumentException($"No field is named \"{name}\".", nameof(name));
        var fields = (PayloadField[])_fields.Clone();
        fields[i] = change(fields[i]);
        return new PayloadLayout(Size, fields);
    }

    /// <summary>
    ///     The layout of <typeparamref name="T" /> as it is in memory: its fields, offsets found by generated code (exact,
    ///     padding and packing included), named as declared (a record struct's by its properties' names); a primitive, an enum
    ///     (by its members' names) or a .NET value the kinds cover, bare; an inline array or a fixed buffer as bytes. For a
    ///     provider describing its payloads — in this process, or sending it from another. Not under Native AOT (no code is
    ///     generated there): declare the layout instead.
    /// </summary>
    [RequiresDynamicCode("Finds fields' offsets with generated code: under Native AOT, declare the layout instead.")]
    public static PayloadLayout Of<T>() where T : unmanaged
    {
        return Derived(typeof(T)) ?? throw new NotSupportedException(
            $"{typeof(T)}: no layout can be derived (nested more than {MaxDepth} deep, or more than {MaxFields} fields).");
    }

    [RequiresDynamicCode("Finds fields' offsets with generated code.")]
    internal static PayloadLayout? Derived(Type type)
    {
        return s_derived.GetOrAdd(type, static t =>
        {
            try
            {
                return Derive(t, 1);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidProgramException)
            {
                return null;
            }
        });
    }

    [RequiresDynamicCode("Finds fields' offsets with generated code.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "A value type's instance fields are never trimmed: its layout depends on them.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "A value type's instance fields are never trimmed; an enum's members are its own constants.")]
    private static PayloadLayout Derive(Type type, int depth)
    {
        var size = RuntimeHelpers.SizeOf(type.TypeHandle);
        if (Bare(type, size) is { } bare) return new PayloadLayout(size, [bare]);
        var fields = new List<PayloadField>();
        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .OrderBy(f => f.MetadataToken))
        {
            var name = f.Name is ['<', .. var rest] && rest.IndexOf('>') is > 0 and var end ? rest[..end] : f.Name;
            var offset = OffsetOf(type, f);
            var t = f.FieldType;
            var length = RuntimeHelpers.SizeOf(t.TypeHandle);
            fields.Add(Bare(t, length) is { } field
                ? field with { Name = name, Offset = offset }
                : f.IsDefined(typeof(FixedBufferAttribute)) || depth >= MaxDepth
                    ? new PayloadField(name, offset, FieldKind.Bytes) { Length = length }
                    : new PayloadField(name, offset, FieldKind.Struct) { Layout = Derive(t, depth + 1) });
        }

        return new PayloadLayout(size, fields);
    }

    /// <summary>
    ///     A type shown as one value — a primitive, an enum, a .NET value the kinds cover, a pointer, an inline array —
    ///     or null (a struct).
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "An enum's members are its own constants.")]
    private static PayloadField? Bare(Type t, int size)
    {
        if (t.IsEnum)
        {
            var names = new Dictionary<long, string>();
            foreach (var m in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                names.TryAdd(
                    m.GetRawConstantValue() switch
                    {
                        ulong u => unchecked((long)u), var v => Convert.ToInt64(v, CultureInfo.InvariantCulture)
                    }, m.Name);
            return Bare(Enum.GetUnderlyingType(t), size)! with { Names = names };
        }

        FieldKind? kind = Type.GetTypeCode(t) switch
        {
            TypeCode.SByte => FieldKind.Int8, TypeCode.Byte => FieldKind.UInt8, TypeCode.Int16 => FieldKind.Int16,
            TypeCode.UInt16 => FieldKind.UInt16,
            TypeCode.Int32 => FieldKind.Int32, TypeCode.UInt32 => FieldKind.UInt32, TypeCode.Int64 => FieldKind.Int64,
            TypeCode.UInt64 => FieldKind.UInt64,
            TypeCode.Single => FieldKind.Float32, TypeCode.Double => FieldKind.Float64,
            TypeCode.Boolean => FieldKind.Bool, TypeCode.Char => FieldKind.Char,
            TypeCode.Decimal => FieldKind.Decimal, TypeCode.DateTime => FieldKind.DateTime,
            _ => t == typeof(Int128)
                ? FieldKind.Int128
                : t == typeof(UInt128)
                    ? FieldKind.UInt128
                    : t == typeof(Half)
                        ? FieldKind.Float16
                        : t == typeof(Guid)
                            ? FieldKind.Guid
                            : t == typeof(TimeSpan)
                                ? FieldKind.TimeSpan
                                : t == typeof(nint)
                                    ? size == 8 ? FieldKind.Int64 : FieldKind.Int32
                                    : t == typeof(nuint) || t.IsPointer || t.IsFunctionPointer
                                        ? size == 8 ? FieldKind.UInt64 : FieldKind.UInt32
                                        : null
        };
        if (kind is { } k) return new PayloadField("", 0, k);
        return t.IsDefined(typeof(InlineArrayAttribute), false)
            ? new PayloadField("", 0, FieldKind.Bytes) { Length = size }
            : null;
    }

    /// <summary>A field's offset in its struct, as the runtime laid it out: the distance of its address from a local's.</summary>
    [RequiresDynamicCode("Generates code.")]
    private static int OffsetOf(Type owner, FieldInfo field)
    {
        var method = new DynamicMethod("OffsetOf", typeof(nint), Type.EmptyTypes, typeof(PayloadLayout).Module, true);
        var il = method.GetILGenerator();
        il.DeclareLocal(owner);
        il.Emit(OpCodes.Ldloca_S, (byte)0);
        il.Emit(OpCodes.Ldflda, field);
        il.Emit(OpCodes.Ldloca_S, (byte)0);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ret);
        return (int)method.CreateDelegate<Func<nint>>()();
    }

    /// <summary>
    ///     The payload as JSON: an object of its fields, or its one unnamed field bare. <paramref name="payload" /> is at
    ///     least <see cref="Size" />.
    /// </summary>
    internal void Render(Utf8JsonWriter w, ReadOnlySpan<byte> payload)
    {
        if (_fields is [{ Name: "" } bare])
        {
            bare.Render(w, payload);
            return;
        }

        w.WriteStartObject();
        foreach (var f in _fields)
        {
            w.WritePropertyName(f.Name);
            f.Render(w, payload);
        }

        w.WriteEndObject();
    }

    /// <summary>Its JSON form: <c>{"size": 9, "fields": [{"name": "price", "offset": 1, "kind": "int64", "scale": 2}, …]}</c>.</summary>
    public void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("size", Size);
        w.WriteStartArray("fields");
        foreach (var f in _fields) f.WriteTo(w);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static PayloadLayout Read(JsonElement e, int depth)
    {
        if (depth > MaxDepth) throw new ArgumentException($"Fields nest at most {MaxDepth} deep.");
        var fields = new List<PayloadField>();
        foreach (var f in e.GetProperty("fields").EnumerateArray())
        {
            if (fields.Count >= MaxFields)
                throw new ArgumentException($"A layout has at most {MaxFields} fields in all.");
            var kindName = f.GetProperty("kind").GetString() ?? "";
            var kind = KindOf(kindName) ?? throw new ArgumentException($"No such kind of field: \"{kindName}\".");
            Dictionary<long, string>? names = null;
            if (f.TryGetProperty("names", out var n))
            {
                names = [];
                foreach (var p in n.EnumerateObject())
                    names[long.TryParse(p.Name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
                            ? v
                            : throw new ArgumentException($"A name's value is an integer: \"{p.Name}\".")] =
                        p.Value.GetString()!;
            }

            fields.Add(new PayloadField(f.GetProperty("name").GetString()!, f.GetProperty("offset").GetInt32(), kind)
            {
                Length = f.TryGetProperty("length", out var l) ? l.GetInt32() : 0,
                Scale = f.TryGetProperty("scale", out var s) ? s.GetInt32() : 0,
                Names = names,
                Layout = f.TryGetProperty("layout", out var nested) ? Read(nested, depth + 1) : null
            });
        }

        return new PayloadLayout(e.GetProperty("size").GetInt32(), fields);
    }

    internal static string KindName(FieldKind kind)
    {
        return s_kindNames[(int)kind];
    }

    private static FieldKind? KindOf(string name)
    {
        return Array.IndexOf(s_kindNames, name) is >= 0 and var i ? (FieldKind)i : null;
    }
}