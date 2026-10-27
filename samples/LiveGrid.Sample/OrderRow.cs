using System.Runtime.CompilerServices;

namespace LiveGrid.Sample;

/// <summary>An order's client order id as text: up to 23 bytes of UTF-8 (GridText).</summary>
[InlineArray(24)]
public struct ClOrdIdText
{
    private byte _0;
}

/// <summary>
///     A version of a client's order, as the grid shows it: the ChalkQL blotter's order, field for field, made fixed-size
///     for the wire — its text a client order id of at most 23 bytes, its names enums (shown by the names in
///     <see cref="Reference" />). On a perpetual, in US dollars, through one of the client's channels, at the mark plus
///     the channel's markup; each fill, and a cancel, is a new version, numbered one higher. 112 bytes.
/// </summary>
public struct OrderRow
{
    public Guid OrderId;
    public long OrderTime; // µs since 1970: when the client placed it
    public long EventTime; // µs since 1970: when this version happened
    public double Qty;
    public double FilledQty;
    public double AvgPrice; // its fills' prices, weighted by their quantities
    public double FilledAmt; // its fills' amounts: the notional filled, in US dollars
    public double MarkupBps;
    public int Version;
    public ClOrdIdText ClOrdId; // "CEDAR-4999999": the client's reference, then the order's number
    public Client Client;
    public Region Region;
    public Channel Channel;
    public Instrument Instrument;
    public Side Side;
    public OrderStatus Status;
}
