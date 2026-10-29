namespace LiveGrid.Sample.Domain;

/// <summary>
///     A fill, as the fills tab shows it: the ChalkQL blotter's <c>Fill</c>, field for field — its order's names beside it,
///     and the mid it was priced from — made fixed-size for the wire, its names text inline (<see cref="OrderRow" />'s), its
///     time UTC to the microsecond. A fill never changes: the table <c>fills</c> holds it as the stream sent it. 200 bytes.
/// </summary>
public struct FillRow
{
    public Guid FillId; // UUIDv7 from its time: newest first by id is newest first by time
    public Guid OrderId;
    public DateTime EventTime; // UTC, to the microsecond: when the order was filled
    public Text24 ClOrdId;
    public Text24 Client;
    public Text16 Region;
    public Text16 Channel;
    public Text24 Instrument;
    public Text16 Side;
    public double Qty;
    public double Price;
    public double Amt; // its quantity at its price: the notional, in US dollars
    public double Mid; // the mid it was priced from: its price less the markup its order pays
    public double MarkupBps;

    /// <summary><paramref name="fill" /> of <paramref name="order" />: its order's names, as the order has them when filled.</summary>
    internal static FillRow Of(in OrderRow order, in Fill fill)
    {
        return new FillRow
        {
            FillId = fill.Id, OrderId = order.OrderId, EventTime = Times.Of(fill.Time), ClOrdId = order.ClOrdId, Client = order.Client,
            Region = order.Region, Channel = order.Channel, Instrument = order.Instrument, Side = order.Side, Qty = fill.Qty,
            Price = fill.Price, Amt = fill.Amount, Mid = FillRecord.MidOf(order, fill), MarkupBps = order.MarkupBps
        };
    }
}
