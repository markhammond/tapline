namespace LiveGrid.Sample.Domain;

/// <summary>
///     The sample's tabs, client orders and fills (the page's <see cref="Catalog" />): their columns as the database's tables
///     and the alerts' hold them — each named as SQL names it, its heading, its kind, a choice's names — which the grid hides
///     at first, and when each row happened.
/// </summary>
public static class Tabs
{
    public static readonly Tab Orders = MakeOrders();

    public static readonly Tab Fills = MakeFills();

    public static readonly IReadOnlyList<Tab> All = [Orders, Fills];

    public static Tab? Of(string name)
    {
        return All.FirstOrDefault(t => t.Name == name);
    }

    private static Tab MakeOrders()
    {
        var key = new Column("order_id", "Order", ColumnKind.Id);
        return new Tab("orders", "Client orders", key,
        [
            new Column("order_time", "Time", ColumnKind.Time, Description: "When the client placed the order"), key,
            new Column("version", "Ver", ColumnKind.Integer, Hidden: true), new Column("cl_ord_id", "ClOrdId", ColumnKind.Text),
            new Column("client", "Client", ColumnKind.Text),
            new Column("region", "Region", ColumnKind.Choice, Reference.RegionNames, Hidden: true),
            new Column("channel", "Channel", ColumnKind.Choice, Reference.ChannelNames, Hidden: true),
            new Column("instrument", "Instrument", ColumnKind.Text), new Column("side", "Side", ColumnKind.Choice, Reference.SideNames),
            new Column("qty", "Qty", ColumnKind.Number), new Column("filled_qty", "Filled", ColumnKind.Number, Hidden: true),
            new Column("avg_price", "Avg px", ColumnKind.Number), new Column("filled_amt", "Filled amt", ColumnKind.Number),
            new Column("markup_bps", "Markup", ColumnKind.Number), new Column("status", "Status", ColumnKind.Choice, Reference.StatusNames),
            new Column("event_time", "Updated", ColumnKind.Time, Description: "When this version happened")
        ], true, "order_time", ["filled_pct"]);
    }

    private static Tab MakeFills()
    {
        var key = new Column("fill_id", "Fill", ColumnKind.Id);
        var fills = new Tab("fills", "Fills", key,
        [
            new Column("event_time", "Time", ColumnKind.Time, Description: "When the order was filled"), key,
            new Column("order_id", "Order", ColumnKind.Id), new Column("cl_ord_id", "ClOrdId", ColumnKind.Text),
            new Column("client", "Client", ColumnKind.Text),
            new Column("region", "Region", ColumnKind.Choice, Reference.RegionNames, Hidden: true),
            new Column("channel", "Channel", ColumnKind.Choice, Reference.ChannelNames, Hidden: true),
            new Column("instrument", "Instrument", ColumnKind.Text), new Column("side", "Side", ColumnKind.Choice, Reference.SideNames),
            new Column("qty", "Qty", ColumnKind.Number), new Column("price", "Price", ColumnKind.Number),
            new Column("amt", "Amt", ColumnKind.Number), new Column("mid", "Mid", ColumnKind.Number),
            new Column("markup_bps", "Markup", ColumnKind.Number)
        ], false, "event_time");
        fills.Paths = [new TabPath("order", Orders, "order_id", "The fill's order, at the newest version the stream sent")];
        return fills;
    }
}
