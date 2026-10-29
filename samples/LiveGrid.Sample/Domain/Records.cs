namespace LiveGrid.Sample.Domain;

/// <summary>
///     An order's version as the database holds it: the ChalkQL blotter's <c>Order</c>, field for field — its names text,
///     its times UTC. The tables <c>orders_current</c> and <c>order_versions</c> are of these, and so is the grid's tail
///     (ChalkTail.Of), so a filter reads the same columns in either.
/// </summary>
public sealed record OrderRecord(
    Guid OrderId,
    int Version,
    DateTime EventTime,
    DateTime OrderTime,
    string ClOrdId,
    string Client,
    string Region,
    string Channel,
    string Instrument,
    string Side,
    double Qty,
    double FilledQty,
    double AvgPrice,
    double FilledAmt,
    double MarkupBps,
    string Status)
{
    /// <summary>A row as the database holds it, its names the house's own strings (one of each, not one a row).</summary>
    public static OrderRecord Of(in OrderRow row)
    {
        return new OrderRecord(row.OrderId, row.Version, row.EventTime, row.OrderTime, row.ClOrdId.ToString(), Names.Of(row.Client),
            Names.Of(row.Region), Names.Of(row.Channel), Names.Of(row.Instrument), Names.Of(row.Side), row.Qty, row.FilledQty,
            row.AvgPrice, row.FilledAmt, row.MarkupBps, Names.Of(row.Status));
    }
}

/// <summary>
///     A fill as the database holds it: the ChalkQL blotter's <c>Fill</c>, field for field — its order's names beside it,
///     and the mid it was priced from. The table <c>fills</c> is of these.
/// </summary>
public sealed record FillRecord(
    Guid FillId,
    Guid OrderId,
    DateTime EventTime,
    string ClOrdId,
    string Client,
    string Region,
    string Channel,
    string Instrument,
    string Side,
    double Qty,
    double Price,
    double Amt,
    double Mid,
    double MarkupBps)
{
    /// <summary>
    ///     <paramref name="fill" /> of <paramref name="order" />, its client order id <paramref name="clOrdId" /> (the order's
    ///     record's): its mid the price less the markup the order pays.
    /// </summary>
    internal static FillRecord Of(in OrderRow order, in Fill fill, string clOrdId)
    {
        return new FillRecord(fill.Id, order.OrderId, Times.Of(fill.Time), clOrdId, Names.Of(order.Client), Names.Of(order.Region),
            Names.Of(order.Channel), Names.Of(order.Instrument), Names.Of(order.Side), fill.Qty, fill.Price, fill.Amount,
            MidOf(order, fill), order.MarkupBps);
    }

    /// <summary>A fill as the stream sends it, its names the house's own strings.</summary>
    internal static FillRecord Of(in FillRow row)
    {
        return new FillRecord(row.FillId, row.OrderId, row.EventTime, row.ClOrdId.ToString(), Names.Of(row.Client), Names.Of(row.Region),
            Names.Of(row.Channel), Names.Of(row.Instrument), Names.Of(row.Side), row.Qty, row.Price, row.Amt, row.Mid, row.MarkupBps);
    }

    /// <summary>The mid <paramref name="fill" /> was priced from: its price less the markup <paramref name="order" /> pays.</summary>
    internal static double MidOf(in OrderRow order, in Fill fill)
    {
        var sign = InlineText.Is(order.Side, "Buy"u8) ? 1 : -1;
        return Math.Round(fill.Price / (1 + sign * order.MarkupBps / 10_000), 6);
    }
}
