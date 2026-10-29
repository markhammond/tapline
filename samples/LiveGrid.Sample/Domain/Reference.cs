using System.Text;

namespace LiveGrid.Sample.Domain;

/// <summary>The house's own vocabulary, for making orders: a row carries each as text (<see cref="Reference" />'s texts).</summary>
internal enum Region : byte
{
    Americas,
    Emea,
    Apac
}

internal enum Channel : byte
{
    Fix,
    Api,
    Gui,
    Mobile,
    Voice
}

internal enum Side : byte
{
    Buy,
    Sell
}

internal enum OrderStatus : byte
{
    New,
    Partial,
    Filled,
    Cancelled
}

/// <summary>
///     The brokerage's reference data, as the PivotTable sample's (Brokerage, Desk), which the ChalkQL blotter's follows:
///     twelve clients, each in its region and trading through its channels at their markups, in the house's perpetuals
///     on eight assets.
/// </summary>
internal static class Reference
{
    /// <summary>Each client's reference, as the grid shows it, and its region: by how much they trade, most first.</summary>
    public static readonly (string Ref, Region Region)[] Clients =
    [
        ("CEDAR", Region.Americas), ("FERN", Region.Emea), ("ACORN", Region.Apac), ("JUNIPER", Region.Apac),
        ("BIRCH", Region.Emea), ("HAZEL", Region.Apac), ("DAMSON", Region.Americas), ("LAUREL", Region.Emea),
        ("ELDER", Region.Americas), ("IVY", Region.Apac), ("GORSE", Region.Americas), ("KELP", Region.Apac)
    ];

    /// <summary>How much each client trades: the first twice the second's, three times the third's, and on.</summary>
    public static readonly double[] ClientWeights = [.. Clients.Select((_, i) => 1.0 / (i + 1))];

    private static readonly (Channel Channel, double Weight)[] Busiest = [(Channel.Fix, 0.6), (Channel.Api, 0.3), (Channel.Voice, 0.1)];
    private static readonly (Channel Channel, double Weight)[] Middle = [(Channel.Api, 0.5), (Channel.Fix, 0.2), (Channel.Gui, 0.3)];
    private static readonly (Channel Channel, double Weight)[] Rest = [(Channel.Gui, 0.5), (Channel.Mobile, 0.3), (Channel.Voice, 0.2)];

    /// <summary>A client's channels, by its rank from 1: the busiest on FIX and API, the middle on API and GUI, the rest on GUI, mobile and voice.</summary>
    public static (Channel Channel, double Weight)[] ChannelsOf(int rank)
    {
        return rank switch { <= 2 => Busiest, <= 6 => Middle, _ => Rest };
    }

    /// <summary>The markup a client pays on its channel, in basis points: built into the price it trades at.</summary>
    public static double MarkupBps(Channel channel)
    {
        return channel switch { Channel.Fix => 6, Channel.Api => 8, Channel.Gui => 20, Channel.Mobile => 25, _ => 35 };
    }

    /// <summary>
    ///     The assets the house's linear perpetuals are on, in US dollars: each one's symbol, how popular it is, a lot of it
    ///     in units, and a price to start from.
    /// </summary>
    public static readonly (string Symbol, double Weight, double Lot, double Price)[] Assets =
    [
        ("ADA", 0.05, 2_000, 0.45), ("AVAX", 0.05, 20, 25), ("BTC", 0.30, 0.05, 65_000), ("DOGE", 0.06, 10_000, 0.12),
        ("ETH", 0.24, 0.5, 3_200), ("LINK", 0.05, 50, 14), ("SOL", 0.15, 10, 150), ("XRP", 0.10, 2_000, 0.55)
    ];

    public static readonly double[] AssetWeights = [.. Assets.Select(a => a.Weight)];

    /// <summary>Each asset's perpetual, as the grid shows it ("BTC/USD Perp"), in <see cref="Assets" />' order.</summary>
    public static readonly string[] Instruments = [.. Assets.Select(a => $"{a.Symbol}/USD Perp")];

    private static readonly byte[][] InstrumentBytes = [.. Instruments.Select(Encoding.UTF8.GetBytes)];

    /// <summary>The asset an instrument is on, by its name's UTF-8: its place in <see cref="Assets" />, or −1 for none of the house's.</summary>
    public static int AssetOf(ReadOnlySpan<byte> instrument)
    {
        for (var i = 0; i < InstrumentBytes.Length; i++)
            if (instrument.SequenceEqual(InstrumentBytes[i]))
                return i;
        return -1;
    }

    /// <summary>The names the house writes, as the blotter's: each a ticked column's choices.</summary>
    public static readonly string[] RegionNames = ["Americas", "EMEA", "APAC"];

    public static readonly string[] ChannelNames = ["FIX", "API", "GUI", "Mobile", "Voice"];

    public static readonly string[] SideNames = ["Buy", "Sell"];

    public static readonly string[] StatusNames = ["New", "Partial", "Filled", "Cancelled"];

    /// <summary>The texts the house writes into its rows, made once (by the enums' values, or the lists' places).</summary>
    public static readonly Text24[] ClientTexts = [.. Clients.Select(c => Text24.Of(c.Ref))];

    public static readonly Text24[] InstrumentTexts = [.. Instruments.Select(Text24.Of)];

    public static readonly Text16[] RegionTexts = [.. RegionNames.Select(Text16.Of)];

    public static readonly Text16[] ChannelTexts = [.. ChannelNames.Select(Text16.Of)];

    public static readonly Text16[] SideTexts = [.. SideNames.Select(Text16.Of)];

    public static readonly Text16[] StatusTexts = [.. StatusNames.Select(Text16.Of)];
}
