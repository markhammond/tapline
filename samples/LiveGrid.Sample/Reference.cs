namespace LiveGrid.Sample;

/// <summary>The clients, by how much they trade, most first.</summary>
public enum Client : byte
{
    Cedar,
    Fern,
    Acorn,
    Juniper,
    Birch,
    Hazel,
    Damson,
    Laurel,
    Elder,
    Ivy,
    Gorse,
    Kelp
}

public enum Region : byte
{
    Americas,
    Emea,
    Apac
}

public enum Channel : byte
{
    Fix,
    Api,
    Gui,
    Mobile,
    Voice
}

/// <summary>The house's linear perpetuals, in US dollars: one on each asset.</summary>
public enum Instrument : byte
{
    Ada,
    Avax,
    Btc,
    Doge,
    Eth,
    Link,
    Sol,
    Xrp
}

public enum Side : byte
{
    Buy,
    Sell
}

public enum OrderStatus : byte
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
    /// <summary>Each client's reference, as the grid shows it, and its region.</summary>
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

    /// <summary>The assets, in <see cref="Instrument" />'s order: how popular each is, a lot of each in units, and a price to start from.</summary>
    public static readonly (double Weight, double Lot, double Price)[] Assets =
    [
        (0.05, 2_000, 0.45), (0.05, 20, 25), (0.30, 0.05, 65_000), (0.06, 10_000, 0.12), (0.24, 0.5, 3_200),
        (0.05, 50, 14), (0.15, 10, 150), (0.10, 2_000, 0.55)
    ];

    public static readonly double[] AssetWeights = [.. Assets.Select(a => a.Weight)];

    /// <summary>The names the grid shows, by value.</summary>
    public static readonly IReadOnlyDictionary<long, string> ClientNames = Names<Client>(c => Clients[(int)c].Ref);

    public static readonly IReadOnlyDictionary<long, string> RegionNames =
        Names<Region>(r => r switch { Region.Emea => "EMEA", Region.Apac => "APAC", _ => r.ToString() });

    public static readonly IReadOnlyDictionary<long, string> ChannelNames =
        Names<Channel>(c => c switch { Channel.Fix => "FIX", Channel.Api => "API", Channel.Gui => "GUI", _ => c.ToString() });

    public static readonly IReadOnlyDictionary<long, string> InstrumentNames =
        Names<Instrument>(i => $"{i.ToString().ToUpperInvariant()}/USD Perp");

    /// <summary>An enum's names, by value, as a column's dictionary.</summary>
    private static Dictionary<long, string> Names<T>(Func<T, string> name) where T : struct, Enum
    {
        return Enum.GetValues<T>().ToDictionary(v => Convert.ToInt64(v), name);
    }
}
