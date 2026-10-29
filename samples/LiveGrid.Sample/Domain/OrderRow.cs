namespace LiveGrid.Sample.Domain;

/// <summary>
///     A version of a client's order, as the grid shows it: the ChalkQL blotter's <c>Order</c>, field for field, made
///     fixed-size for the wire — its names text inline, any of at most 23 bytes (15 for region, channel, side and status),
///     its times UTC to the microsecond. On a perpetual, in US dollars, through one of the client's channels, at the mark
///     plus the channel's markup; each fill, and a cancel, is a new version, numbered one higher. 216 bytes.
/// </summary>
public struct OrderRow
{
    public Guid OrderId;
    public DateTime OrderTime; // UTC, to the microsecond: when the client placed it
    public DateTime EventTime; // UTC, to the microsecond: when this version happened
    public double Qty;
    public double FilledQty;
    public double AvgPrice; // its fills' prices, weighted by their quantities
    public double FilledAmt; // its fills' amounts: the notional filled, in US dollars
    public double MarkupBps;
    public int Version;
    public Text24 ClOrdId; // "CEDAR-4999999": the client's reference, then the order's number
    public Text24 Client; // "CEDAR"
    public Text16 Region; // "EMEA"
    public Text16 Channel; // "FIX"
    public Text24 Instrument; // "BTC/USD Perp"
    public Text16 Side; // "Buy", "Sell"
    public Text16 Status; // "New", "Partial", "Filled", "Cancelled"
}
