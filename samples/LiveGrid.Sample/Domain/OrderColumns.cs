using System.Text;

namespace LiveGrid.Sample.Domain;

/// <summary>
///     The orders' columns as the computed source sorts and filters them in process, by the names the grid's columns give
///     (the blotter's SQL names): text compared byte by byte, names by the names shown, numbers and times by value. Their
///     kinds and choices are the catalog's (<see cref="Tabs" />).
/// </summary>
internal static class OrderColumns
{
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
            OrderKey.ClOrdId or OrderKey.Client or OrderKey.Region or OrderKey.Channel or OrderKey.Instrument or OrderKey.Side
                or OrderKey.Status => Text(key, a).SequenceCompareTo(Text(key, b)), // (as UTF-8, byte by byte: as a database's C collation)
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
        return Times.Micros(key == OrderKey.EventTime ? row.EventTime : row.OrderTime);
    }

    /// <summary>A text column's UTF-8: the client order id, client, region, channel, instrument, side or status.</summary>
    public static ReadOnlySpan<byte> Text(OrderKey key, in OrderRow row)
    {
        return InlineText.Bytes(key switch
        {
            OrderKey.Client => row.Client, OrderKey.Region => row.Region, OrderKey.Channel => row.Channel,
            OrderKey.Instrument => row.Instrument, OrderKey.Side => row.Side, OrderKey.Status => row.Status, _ => row.ClOrdId
        });
    }

    /// <summary>Ids as PostgreSQL orders UUIDs: byte by byte, as written.</summary>
    private static int CompareIds(Guid a, Guid b)
    {
        Span<byte> x = stackalloc byte[16], y = stackalloc byte[16];
        a.TryWriteBytes(x, true, out _);
        b.TryWriteBytes(y, true, out _);
        return x.SequenceCompareTo(y);
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

/// <summary>
///     A filter tested against orders in process, as the computed source filters them (<see cref="Orders" />): each column's
///     conditions resolved once to the order's field (<see cref="OrderColumns" />), all of which must hold.
/// </summary>
internal sealed class OrderTest
{
    private readonly Column[] _columns;

    private OrderTest(ViewFilter filter)
    {
        _columns = [.. filter.Columns.Select(c => new Column(c))];
    }

    /// <summary>The test of a filter: none, every order passing.</summary>
    public static OrderTest? Of(ViewFilter? filter)
    {
        return filter is null ? null : new OrderTest(filter);
    }

    public bool Passes(in OrderRow row)
    {
        foreach (var column in _columns)
            if (!column.Passes(row))
                return false;
        return true;
    }

/// <summary>A column's filter resolved for testing rows: its column's field, its names ticked by value, its bounds in the row's units.</summary>
private sealed class Column
{
    private readonly HashSet<Guid>? _ids;
    private readonly OrderKey _key;
    private readonly ColumnKind _kind;
    private readonly double _min = double.NegativeInfinity, _max = double.PositiveInfinity;
    private readonly long _from = long.MinValue, _to = long.MaxValue;
    private readonly bool _prefix;
    private readonly byte[][]? _texts; // a text column's values, or starts

    public Column(ColumnFilter filter)
    {
        _kind = filter.Kind;
        _key = OrderColumns.KeyOf(filter.Column) ?? OrderKey.OrderTime;
        _prefix = filter.Match == TextMatch.StartsWith && _kind == ColumnKind.Text;
        if (!filter.Values.IsEmpty)
        {
            if (_kind == ColumnKind.Id)
                _ids = [.. filter.Values.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty)]; // (not an id: matches none)
            else
                _texts = [.. filter.Values.Select(Encoding.UTF8.GetBytes)];
        }

        (_min, _max) = (filter.Min ?? double.NegativeInfinity, filter.Max ?? double.PositiveInfinity);
        if (filter.From is { } from) _from = Micros(from);
        if (filter.To is { } to) _to = Micros(to);
    }

    public bool Passes(in OrderRow row)
    {
        if (_ids is not null && !_ids.Contains(row.OrderId)) return false;
        if (_texts is not null && !Matches(OrderColumns.Text(_key, row))) return false;
        if (_kind is ColumnKind.Number or ColumnKind.Integer)
        {
            var number = OrderColumns.Number(_key, row);
            if (number < _min || number > _max) return false;
        }

        if (_kind != ColumnKind.Time) return true;
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
