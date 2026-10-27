using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace LiveGrid.Sample;

/// <summary>
///     The orders' columns as the source sorts and filters them, by the names the grid's columns give (the blotter's
///     SQL names): text compared byte by byte, names by the names shown, numbers and times by value.
/// </summary>
internal static class OrderColumns
{
    private static readonly int[] ClientRanks = Ranks(Reference.ClientNames);
    private static readonly int[] RegionRanks = Ranks(Reference.RegionNames);
    private static readonly int[] ChannelRanks = Ranks(Reference.ChannelNames);
    private static readonly int[] InstrumentRanks = Ranks(Reference.InstrumentNames);
    private static readonly int[] StatusRanks = Ranks(Enum.GetValues<OrderStatus>().ToDictionary(s => (long)s, s => s.ToString()));

    /// <summary>The columns whose values are names, and their names by value.</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<long, string>> Names =
        new Dictionary<string, IReadOnlyDictionary<long, string>>
        {
            ["client"] = Reference.ClientNames, ["region"] = Reference.RegionNames, ["channel"] = Reference.ChannelNames,
            ["instrument"] = Reference.InstrumentNames,
            ["side"] = Enum.GetValues<Side>().ToDictionary(s => (long)s, s => s.ToString()),
            ["status"] = Enum.GetValues<OrderStatus>().ToDictionary(s => (long)s, s => s.ToString())
        };

    /// <summary>How a column's filter is edited, as the blotter's columns are: by the kind of its values.</summary>
    public static FilterKind KindOf(string column)
    {
        return column switch
        {
            "order_id" => FilterKind.Id, "order_time" or "event_time" => FilterKind.Time, "version" => FilterKind.Integer,
            "cl_ord_id" or "client" or "instrument" => FilterKind.Text,
            "region" or "channel" or "side" or "status" => FilterKind.Choice, _ => FilterKind.Number
        };
    }

    /// <summary>A column's names, by value, if its values are names (client, instrument, and those ticked): else null.</summary>
    public static IReadOnlyDictionary<long, string>? NamesOf(string column)
    {
        return Names.GetValueOrDefault(column);
    }

    /// <summary>The column a sort names, once, for comparing by: null for none of these.</summary>
    public static OrderKey? KeyOf(string column)
    {
        return column switch
        {
            "order_time" => OrderKey.OrderTime, "event_time" => OrderKey.EventTime, "order_id" => OrderKey.OrderId,
            "version" => OrderKey.Version, "cl_ord_id" => OrderKey.ClOrdId, "client" => OrderKey.Client,
            "region" => OrderKey.Region, "channel" => OrderKey.Channel, "instrument" => OrderKey.Instrument,
            "side" => OrderKey.Side, "status" => OrderKey.Status, "qty" => OrderKey.Qty, "filled_qty" => OrderKey.FilledQty,
            "avg_price" => OrderKey.AvgPrice, "filled_amt" => OrderKey.FilledAmt, "markup_bps" => OrderKey.MarkupBps,
            _ => null
        };
    }

    public static int Compare(OrderKey key, in OrderRow a, in OrderRow b)
    {
        return key switch
        {
            OrderKey.OrderTime => a.OrderTime.CompareTo(b.OrderTime),
            OrderKey.EventTime => a.EventTime.CompareTo(b.EventTime),
            OrderKey.OrderId => CompareIds(a.OrderId, b.OrderId),
            OrderKey.Version => a.Version.CompareTo(b.Version),
            OrderKey.ClOrdId => Text(a).SequenceCompareTo(Text(b)),
            OrderKey.Client => ClientRanks[(int)a.Client].CompareTo(ClientRanks[(int)b.Client]),
            OrderKey.Region => RegionRanks[(int)a.Region].CompareTo(RegionRanks[(int)b.Region]),
            OrderKey.Channel => ChannelRanks[(int)a.Channel].CompareTo(ChannelRanks[(int)b.Channel]),
            OrderKey.Instrument => InstrumentRanks[(int)a.Instrument].CompareTo(InstrumentRanks[(int)b.Instrument]),
            OrderKey.Side => a.Side.CompareTo(b.Side), // (Buy before Sell: as the names are)
            OrderKey.Status => StatusRanks[(int)a.Status].CompareTo(StatusRanks[(int)b.Status]),
            OrderKey.Qty => a.Qty.CompareTo(b.Qty),
            OrderKey.FilledQty => a.FilledQty.CompareTo(b.FilledQty),
            OrderKey.AvgPrice => a.AvgPrice.CompareTo(b.AvgPrice),
            OrderKey.FilledAmt => a.FilledAmt.CompareTo(b.FilledAmt),
            _ => a.MarkupBps.CompareTo(b.MarkupBps)
        };
    }

    public static double Number(OrderKey key, in OrderRow row)
    {
        return key switch
        {
            OrderKey.Qty => row.Qty, OrderKey.FilledQty => row.FilledQty, OrderKey.AvgPrice => row.AvgPrice,
            OrderKey.FilledAmt => row.FilledAmt, OrderKey.MarkupBps => row.MarkupBps, OrderKey.Version => row.Version, _ => 0
        };
    }

    public static long Time(OrderKey key, in OrderRow row)
    {
        return key == OrderKey.EventTime ? row.EventTime : row.OrderTime;
    }

    /// <summary>A named value's number (its enum's): client, instrument, region, channel, side, status.</summary>
    public static int Choice(OrderKey key, in OrderRow row)
    {
        return key switch
        {
            OrderKey.Client => (int)row.Client, OrderKey.Region => (int)row.Region, OrderKey.Channel => (int)row.Channel,
            OrderKey.Instrument => (int)row.Instrument, OrderKey.Side => (int)row.Side, _ => (int)row.Status
        };
    }

    /// <summary>The client order id's bytes.</summary>
    public static ReadOnlySpan<byte> Text(in OrderRow row)
    {
        ReadOnlySpan<byte> text = row.ClOrdId;
        return text.Slice(1, text[0] & 0x7F);
    }

    /// <summary>Ids as PostgreSQL orders UUIDs: byte by byte, as written.</summary>
    private static int CompareIds(Guid a, Guid b)
    {
        Span<byte> x = stackalloc byte[16], y = stackalloc byte[16];
        a.TryWriteBytes(x, true, out _);
        b.TryWriteBytes(y, true, out _);
        return x.SequenceCompareTo(y);
    }

    private static int[] Ranks(IReadOnlyDictionary<long, string> names)
    {
        var ranks = new int[names.Count];
        var rank = 0;
        foreach (var (value, _) in names.OrderBy(n => n.Value, StringComparer.Ordinal)) ranks[value] = rank++;
        return ranks;
    }
}

/// <summary>The orders' sortable columns, resolved from their names (<see cref="OrderColumns.KeyOf" />).</summary>
internal enum OrderKey : byte
{
    OrderTime,
    EventTime,
    OrderId,
    Version,
    ClOrdId,
    Client,
    Region,
    Channel,
    Instrument,
    Side,
    Status,
    Qty,
    FilledQty,
    AvgPrice,
    FilledAmt,
    MarkupBps
}

/// <summary>How a column's filter is edited, by the kind of its values (the blotter's column kinds).</summary>
public enum FilterKind
{
    /// <summary>An id: values typed or pasted, matched exactly.</summary>
    Id,

    /// <summary>A whole number: at least, at most.</summary>
    Integer,

    /// <summary>A number with a fraction: at least, at most.</summary>
    Number,

    /// <summary>Text of open-ended values: values picked or typed, matched exactly or by what they start with.</summary>
    Text,

    /// <summary>Text from a short fixed list: the values ticked.</summary>
    Choice,

    /// <summary>A time: from (at or after), to (before), UTC.</summary>
    Time
}

/// <summary>How a text column's values are matched.</summary>
public enum TextMatch
{
    /// <summary>The value is one of those given.</summary>
    AnyOf,

    /// <summary>The value starts with one of those given.</summary>
    StartsWith
}

/// <summary>
///     One column's filter, as the blotter's: values to match for ids, text and choices, a range for numbers and times
///     (from at or after, to before). An empty filter filters nothing.
/// </summary>
public sealed record ColumnFilter
{
    private Test? _test;

    public required string Column { get; init; }

    /// <summary>The column's heading, as the filter's pill names it.</summary>
    public required string Label { get; init; }

    /// <summary>For ids, text and choices: the values, or the starts of values, to match.</summary>
    public ImmutableArray<string> Values { get; init; } = [];

    public TextMatch Match { get; init; } = TextMatch.AnyOf;

    /// <summary>For numbers: at least, and at most.</summary>
    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>For times (UTC): at or after, and before.</summary>
    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public bool IsEmpty => Values.IsEmpty && Min is null && Max is null && From is null && To is null;

    /// <summary>Canonical: its values distinct and in order, its times in UTC to the microsecond.</summary>
    public ColumnFilter Canonical()
    {
        return this with
        {
            Values = [.. Values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().Order(StringComparer.Ordinal)],
            From = From is { } from ? Microsecond(from) : null, To = To is { } to ? Microsecond(to) : null
        };
    }

    /// <summary>Whether an order passes: its values matched byte for byte (names by the names shown), numbers and times by value.</summary>
    public bool Passes(in OrderRow row)
    {
        return (_test ??= new Test(this)).Passes(row);
    }

    /// <summary>What its pill says, as the blotter's do: a column's values (three, and how many more), its range.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!Values.IsEmpty)
        {
            var shown = string.Join(", ", Values.Take(3)) + (Values.Length > 3 ? $" +{Values.Length - 3}" : "");
            parts.Add(Match == TextMatch.StartsWith ? $"starts {shown}" : shown);
        }

        if (Min is { } min) parts.Add("≥ " + min.ToString("#,0.####", CultureInfo.InvariantCulture));
        if (Max is { } max) parts.Add("≤ " + max.ToString("#,0.####", CultureInfo.InvariantCulture));
        if (From is { } from) parts.Add("from " + from.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
        if (To is { } to) parts.Add("to " + to.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
        return $"{Label}: {string.Join(" ", parts)}";
    }

    /// <summary>The same filter, written out: its column, values and bounds.</summary>
    public override string ToString()
    {
        var name = new StringBuilder(Column);
        if (!Values.IsEmpty) name.Append(Match == TextMatch.StartsWith ? " starts " : " in ").AppendJoin('|', Values);
        if (Min is { } min) name.Append(" >= ").Append(min.ToString("R", CultureInfo.InvariantCulture));
        if (Max is { } max) name.Append(" <= ").Append(max.ToString("R", CultureInfo.InvariantCulture));
        if (From is { } from) name.Append(" from ").Append(from.ToString("O", CultureInfo.InvariantCulture));
        if (To is { } to) name.Append(" to ").Append(to.ToString("O", CultureInfo.InvariantCulture));
        return name.ToString();
    }

    /// <summary>Equal by what it says: its values compared in order (a record would compare the array they're in).</summary>
    public bool Equals(ColumnFilter? other)
    {
        return other is not null && Column == other.Column && Match == other.Match && Min == other.Min && Max == other.Max &&
               From == other.From && To == other.To && Values.AsSpan().SequenceEqual(other.Values.AsSpan());
    }

    public override int GetHashCode()
    {
        return ToString().GetHashCode(StringComparison.Ordinal);
    }

    private static DateTime Microsecond(DateTime time)
    {
        var utc = DateTime.SpecifyKind(time, DateTimeKind.Utc);
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    /// <summary>A filter resolved for testing rows: its column's field, its names ticked by value, its bounds in the row's units.</summary>
    private sealed class Test
    {
        private readonly HashSet<Guid>? _ids;
        private readonly OrderKey _key;
        private readonly FilterKind _kind;
        private readonly double _min = double.NegativeInfinity, _max = double.PositiveInfinity;
        private readonly long _from = long.MinValue, _to = long.MaxValue;
        private readonly bool[]? _named; // a named column's values that pass, by number
        private readonly bool _prefix;
        private readonly byte[][]? _texts; // the client order id's values, or starts

        public Test(ColumnFilter filter)
        {
            _kind = OrderColumns.KindOf(filter.Column);
            _key = OrderColumns.KeyOf(filter.Column) ?? OrderKey.OrderTime;
            _prefix = filter.Match == TextMatch.StartsWith && _kind == FilterKind.Text;
            if (!filter.Values.IsEmpty)
            {
                if (_kind == FilterKind.Id)
                    _ids = [.. filter.Values.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty)]; // (not an id: matches none)
                else if (OrderColumns.NamesOf(filter.Column) is { } names)
                {
                    _named = new bool[names.Keys.Max() + 1];
                    foreach (var (value, name) in names)
                        _named[value] = filter.Values.Any(v => _prefix ? name.StartsWith(v, StringComparison.Ordinal) : name == v);
                }
                else
                {
                    _texts = [.. filter.Values.Select(Encoding.UTF8.GetBytes)];
                }
            }

            (_min, _max) = (filter.Min ?? double.NegativeInfinity, filter.Max ?? double.PositiveInfinity);
            if (filter.From is { } from) _from = Micros(from);
            if (filter.To is { } to) _to = Micros(to);
        }

        public bool Passes(in OrderRow row)
        {
            if (_ids is not null && !_ids.Contains(row.OrderId)) return false;
            if (_named is not null)
            {
                var value = OrderColumns.Choice(_key, row);
                if ((uint)value >= (uint)_named.Length || !_named[value]) return false;
            }

            if (_texts is not null && !Matches(OrderColumns.Text(row))) return false;
            if (_kind is FilterKind.Number or FilterKind.Integer)
            {
                var number = OrderColumns.Number(_key, row);
                if (number < _min || number > _max) return false;
            }

            if (_kind != FilterKind.Time) return true;
            var time = OrderColumns.Time(_key, row);
            return time >= _from && time < _to;
        }

        private bool Matches(ReadOnlySpan<byte> text)
        {
            foreach (var value in _texts!)
                if (_prefix ? text.StartsWith(value) : text.SequenceEqual(value))
                    return true;
            return false;
        }

        private static long Micros(DateTime time)
        {
            return (DateTime.SpecifyKind(time, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks) / 10;
        }
    }
}

/// <summary>
///     The sample's filter, the blotter's way: a filter on any of its columns, all of which must hold — the source's own
///     model, passed through the grid. Equal when it says the same, so setting an equal one again reads nothing afresh.
/// </summary>
public sealed class OrderFilter
{
    private OrderFilter(ImmutableArray<ColumnFilter> columns)
    {
        Columns = columns;
        Name = string.Join("; ", columns.Select(c => c.ToString()));
    }

    /// <summary>The columns filtered, by name.</summary>
    public ImmutableArray<ColumnFilter> Columns { get; }

    /// <summary>Canonical: the same for filters that say the same.</summary>
    public string Name { get; }

    /// <summary>The filter of these, each canonical, the empty ones left out: none if none's left.</summary>
    public static OrderFilter? Of(IEnumerable<ColumnFilter> filters)
    {
        ImmutableArray<ColumnFilter> columns =
            [.. filters.Select(f => f.Canonical()).Where(f => !f.IsEmpty).OrderBy(f => f.Column, StringComparer.Ordinal)];
        return columns.IsEmpty ? null : new OrderFilter(columns);
    }

    public ColumnFilter? For(string column)
    {
        return Columns.FirstOrDefault(c => c.Column == column);
    }

    /// <summary>This filter with <paramref name="filter" /> in place of its column's (or without it, empty): none if none's left.</summary>
    public OrderFilter? With(ColumnFilter filter)
    {
        return Of([.. Columns.Where(c => c.Column != filter.Column), filter]);
    }

    public OrderFilter? Without(string column)
    {
        return Of(Columns.Where(c => c.Column != column));
    }

    public bool Passes(in OrderRow row)
    {
        foreach (var column in Columns)
            if (!column.Passes(row))
                return false;
        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is OrderFilter other && Name == other.Name;
    }

    public override int GetHashCode()
    {
        return Name.GetHashCode(StringComparison.Ordinal);
    }

    public override string ToString()
    {
        return string.Join(" · ", Columns.Select(c => c.Describe()));
    }
}
