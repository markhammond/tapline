using System.Text.Json;

namespace PivotTable.Sample;

// The brokerage's tables, as ChalkQL's POCO source holds them (columns snake_case) — a ledger of client perpetual trading
// facing the house: counterparties (the clients, the house, the venues it hedges on and the treasury that funds it), their
// accounts (a perpetual's, or a cash account in a deliverable), postings (a client trade, a hedge fill or a transfer of
// capital each: a UUID), their journal entries (double-entry), and each position after each entry (position_risk:
// append-only, so a day's position is the last row at or before its close) — a client's, the house's side of its clients'
// (the opposite of theirs, its own average entry), and the house's hedges on the venues (fills: what each traded).
// What's traded is an instrument — a perpetual on the venue that lists it, the house's own included — and each is
// marked by its venue (marks). JSON columns are text. The ledger keeps no daily checkpoints: a day's figures are what a
// statement derives, as of its close.

/// <summary>What a posting is (posting.code).</summary>
public static class PostingCode
{
    /// <summary>A client's trade with the house.</summary>
    public const int ClientTrade = 1;

    /// <summary>A hedge fill of the house's on a venue.</summary>
    public const int HedgeFill = 2;

    /// <summary>Working capital, from the treasury to one of the house's cash accounts.</summary>
    public const int Capital = 3;
}

/// <summary>
///     A counterparty (cpty): a client, or the house — every client trades with the house — or one the house deals with
///     itself: a venue it hedges on, and its treasury, which funds its working capital. A client's region is the one that
///     covers it (<see cref="Pivot.Regions" />); the others', none.
/// </summary>
public sealed record Cpty(
    long CptyId,
    string CptyName,
    string CptyRef,
    string Region,
    bool IsHouse,
    string Metadata,
    DateTime UpdateTime);

/// <summary>
///     An account: a counterparty's, in one ccy. A perpetual's (non-deliverable) holds a position in it; a deliverable's (a
///     currency or a stablecoin: cash), what the positions realize, as a perpetual settles its PnL in what it's quoted in.
///     A client has one for each perpetual it trades and one in US dollars; the house one in US dollars and one for each
///     of its perpetuals, facing theirs — and, at each venue, one for each perpetual listed there and one in each currency
///     the venue settles in (its metadata names the venue), each venue the other side of them. The treasury funds the
///     house's cash accounts. (Collateral, posted to an account in a deliverable, isn't modelled.)
/// </summary>
public sealed record Account(
    long AccountId,
    string AccountRef,
    long CptyId,
    short CcyId,
    string CcyName,
    string Metadata,
    DateTime UpdateTime);

/// <summary>
///     What a ccy_id names (ccy): what an account holds — a currency or a stablecoin (deliverable), or a perpetual
///     (non-deliverable): the house's (BTC-USD-PERP), or a venue's (BTC-USDT-PERP-BINANCE), its terms the instrument's
///     (<see cref="Instrument" />).
/// </summary>
public sealed record Ccy(short CcyId, string CcyName, bool IsDeliverable);

/// <summary>
///     An instrument (instrument): a perpetual on the venue that lists it — on an underlying asset, quoted (and its PnL
///     settled) in a deliverable — named as its venue lists it (BTC/USDT Perp @ Binance) and by its logical instrument
///     (BTC/USDT Perp: an asset's perpetual in a quote, whichever venue lists it). The house lists its own for its
///     clients,
///     in US dollars (BTC/USD Perp @ House); the venues list theirs, which the house hedges on. Each venue marks its own
///     (<see cref="Mark" />): the same logical instrument is a different instrument, at a different mark, on another
///     venue.
///     And different logical instruments aren't fungible: BTC/USD Perp held against BTC/USDT Perp leaves tether's rate,
///     and
///     the basis between them.
/// </summary>
public sealed record Instrument(
    short InstrumentId,
    string InstrumentName,
    string LogicalName,
    string Venue,
    string Underlying,
    string Quote);

/// <summary>
///     A posting: journal entries applied together, by a UUID (version 7: in time order) — what it is, its code
///     (<see cref="PostingCode" />): a client's trade (its notes, JSON: the channel, side, quantity, price, mid and markup),
///     a hedge fill (the venue, instrument, side, quantity, price, the PnL it realized and its fee) or working capital (the
///     amount, and the account funded).
/// </summary>
public sealed record Posting(Guid PostingId, int Code, string Notes, DateTime UpdateTime);

/// <summary>
///     A journal entry: an amount, signed, in the account's ccy, its rate in US dollars then — and the account's balance
///     after it (one balance: no layers, no credit and debit). A posting's entries sum to nothing in each ccy, and touch an
///     account once.
/// </summary>
public sealed record Journal(
    long JournalId,
    Guid PostingId,
    long AccountId,
    short CcyId,
    decimal AmtSigned,
    double UsdRate,
    decimal Balance,
    DateTime UpdateTime);

/// <summary>
///     A position after a journal entry (position_risk, append-only): by account (a perpetual's) and channel, in the
///     instrument the account holds — its quantity (long +, short −), its average entry and the mark then, and its realized
///     PnL so far, all in the instrument's quote, what it's settled in — and its entry, and the posting it shares with the
///     other side. Three kinds: a client's (by the channel it traded on: its PnL paid to its US-dollar account); the
///     house's side of its clients' trades, one in each of its perpetuals (the opposite of theirs, at its own average
///     entry; channel "—"); and the house's hedges, one in each venue's perpetual it's traded (channel "—": its PnL, less
///     its fees, settled to its cash at the venue). offset_pnl: none here. And what it's traded so far: its volume (each
///     trade's notional, in US dollars) and its trades — a day's, the difference.
/// </summary>
public sealed record PositionRisk(
    long PositionId,
    long CptyId,
    long AccountId,
    short InstrumentId,
    string Channel,
    long JournalId,
    Guid PostingId,
    decimal AmtSigned,
    double AvgPrc,
    double MarkPrc,
    DateTime UpdateTime,
    double OffsetPnl,
    double CumulativeRealizedPnl,
    double CumulativeVolume,
    long TradeCount);

/// <summary>
///     A hedge fill of the house's (fills): in a venue's instrument; bought (+) or sold (−) at a price, and the fee
///     paid (in the instrument's quote) — and its posting.
/// </summary>
public sealed record HedgeFill(
    long FillId,
    Guid PostingId,
    DateTime UpdateTime,
    short InstrumentId,
    decimal Qty,
    double Price,
    decimal Fee);

/// <summary>
///     An instrument's mark (marks: ts, instrument_id, price), as its venue marks it: its price in its quote — the asset's
///     index (its US-dollar price) in the quote, plus the perpetual's premium there — from its time till the next. The
///     house
///     marks its own at the index.
/// </summary>
public sealed record Mark(DateTime Ts, short InstrumentId, double Price);

/// <summary>
///     A rate (conversion_rate: ts, symbol, usd_rate): what one unit of a currency or a stablecoin was worth in US
///     dollars,
///     from its time till the next — a stablecoin's near 1, not 1.
/// </summary>
public sealed record ConversionRate(DateTime Ts, string Symbol, double UsdRate);

/// <summary>
///     A trading day as a zone and a cutoff make it (a statement's days: trading_day, days_ago, opens, closes): it ends at
///     the
///     cutoff, local time — 17:00 in America/New_York, the FX roll, say — and is named by the date it ends on; its open
///     and
///     close in UTC, the current day's close now; days_ago 0 for the current day. Not a checkpoint: what a statement looks
///     the
///     changes up as of. Computed from the application's time zones (<see cref="TimeZones" />) — in the statement
///     (<c>trading_days</c>: <see cref="PivotCriteria.TradingDaysSql" />), and here for the page's day picker (
///     <see cref="Pivot.TradingDays" />).
/// </summary>
public sealed record CalendarDay(
    string Zone,
    string Cutoff,
    string TradingDay,
    int DaysAgo,
    DateTime Opens,
    DateTime Closes);

/// <summary>A position's average entry, as linear perpetuals keep it — both the clients' and the hedges'.</summary>
internal static class AverageEntry
{
    /// <summary>
    ///     A fill applied to a position: adding to a long (or a short) averages the entry; reducing realizes PnL against it;
    ///     flipping opens the rest at the fill's price. The position after it, and what it realized.
    /// </summary>
    public static (decimal Qty, decimal Avg, decimal Realized) Apply(decimal qty, decimal avg, decimal fill,
        decimal price)
    {
        if (qty == 0 || Math.Sign(qty) == Math.Sign(fill))
            return (qty + fill,
                Math.Round((avg * Math.Abs(qty) + price * Math.Abs(fill)) / (Math.Abs(qty) + Math.Abs(fill)), 8), 0);
        decimal closing = Math.Min(Math.Abs(fill), Math.Abs(qty)), after = qty + fill;
        var realized = Math.Round(closing * (price - avg) * Math.Sign(qty), 8);
        return (after, Math.Abs(fill) > closing ? price : after == 0 ? 0 : avg, realized);
    }
}

/// <summary>What the brokerage trades, and how — its clients', its hedges', their venues'.</summary>
internal static class Desk
{
    public static readonly double[]
        AssetWeight = [0.05, 0.05, 0.30, 0.06, 0.24, 0.05, 0.15, 0.10]; // in Pivot.Assets' order: how popular each is

    public static readonly decimal[] Lot = [2_000, 20, 0.05m, 10_000, 0.5m, 50, 10, 2_000]; // a lot of each, in units


    /// <summary>What each venue quotes in, and how much; its taker fee, in basis points.</summary>
    public static readonly (string Name, double Weight)[][] VenueQuotes = // in Pivot.Venues' order
    [
        [("USDT", 0.80), ("USDC", 0.20)], // Binance
        [("USDT", 0.85), ("USDC", 0.15)], // Bybit
        [("USD", 0.80), ("USDC", 0.20)], // Coinbase
        [("USD", 0.85), ("USDT", 0.15)], // Kraken
        [("USDT", 0.85), ("USDC", 0.15)] // OKX
    ];

    public static readonly double[] VenueFeeBps = [1.8, 2.0, 4.0, 2.5, 2.0];

    /// <summary>
    ///     The instruments: the house's perpetual on each asset, in US dollars (its clients': in <see cref="Pivot.Assets" />'
    ///     order, so the house's on asset a is instrument a + 1), then each venue's on each asset in each currency it quotes
    ///     (the hedges': by venue, quote, then asset).
    /// </summary>
    public static readonly Instrument[] Instruments =
    [
        .. Pivot.Assets.Select((a, i) => new Instrument((short)(i + 1), Listing(a, "USD", Pivot.House),
            Pivot.Logical(a, "USD"), Pivot.House, a, "USD")),
        .. Pivot.Venues.SelectMany((venue, v) =>
                VenueQuotes[v].SelectMany(q => Pivot.Assets.Select(a => (venue, a, q.Name))))
            .Select((x, i) => new Instrument((short)(Pivot.Assets.Length + i + 1), Listing(x.a, x.Name, x.venue),
                Pivot.Logical(x.a, x.Name), x.venue, x.a, x.Name))
    ];

    private static readonly Dictionary<(string Venue, string Asset, string Quote), Instrument> Listings =
        Instruments.ToDictionary(i => (i.Venue, i.Underlying, i.Quote));

    /// <summary>
    ///     The ccys: the deliverables (currencies and stablecoins) first, then the house's perpetual on each asset, then each
    ///     venue's perpetual (its id 200 and its instrument's).
    /// </summary>
    public static readonly Ccy[] Ccys =
    [
        .. Pivot.Symbols.Select((s, i) => new Ccy((short)(i + 1), s, true)),
        .. Pivot.Assets.Select((a, i) => new Ccy((short)(101 + i), Perpetual(a, "USD"), false)),
        .. Instruments.Where(i => i.Venue != Pivot.House)
            .Select(i => new Ccy((short)(200 + i.InstrumentId), Perpetual(i), false))
    ];

    /// <summary>
    ///     The markup a client pays on its channel, in basis points: built into the price it trades at (the house's
    ///     spread).
    /// </summary>
    public static double MarkupBps(string channel)
    {
        return channel switch { "FIX" => 6, "API" => 8, "GUI" => 20, "Mobile" => 25, _ => 35 };
    }

    public static string Perpetual(string asset, string quote)
    {
        return $"{asset}-{quote}-PERP";
    }

    /// <summary>An instrument's ccy: the house's perpetual's (BTC-USD-PERP), or a venue's (BTC-USDT-PERP-BINANCE).</summary>
    public static string Perpetual(Instrument i)
    {
        return i.Venue == Pivot.House
            ? Perpetual(i.Underlying, i.Quote)
            : $"{Perpetual(i.Underlying, i.Quote)}-{i.Venue.ToUpperInvariant()}";
    }

    /// <summary>A venue's listing of a logical instrument: its name there (BTC/USDT Perp @ Binance).</summary>
    public static string Listing(string asset, string quote, string venue)
    {
        return $"{Pivot.Logical(asset, quote)} @ {venue}";
    }

    public static short CcyId(string name)
    {
        return Ccys.First(c => c.CcyName == name).CcyId;
    }

    /// <summary>The house's perpetual on an asset: its clients' instrument.</summary>
    public static Instrument HousePerpetual(int asset)
    {
        return Instruments[asset];
    }

    /// <summary>A venue's perpetual on an asset, in a currency it quotes.</summary>
    public static Instrument Listed(string venue, string asset, string quote)
    {
        return Listings[(venue, asset, quote)];
    }
}

/// <summary>
///     The brokerage: its counterparties, their accounts, and the clients' trades with the house — each a posting of
///     journal entries (the perpetual to the client's account in it and from the house's, and any PnL it realizes the other
///     way, in US dollars) and the positions after it, the client's and the house's — and its books: the house's working
///     capital from its treasury, and its hedge fills on the venues booked (<see cref="Hedger" /> makes them). Clients are
///     Zipfian twice over: in how much each trades, by its
///     rank k 1/k (the
///     busiest about twice the second, twelve times the twelfth), and in how many assets, 8/k^0.8 of them, two at least
///     (the
///     busiest every one, the second five, the next two three, the rest two) — its favourites drawn by how popular each
///     asset
///     is, from a fixed seed, weighed 1/j by their place j. Each trade comes on a channel — the bigger clients' mostly FIX
///     and
///     API, the smaller ones' GUI, mobile and voice — and pays the channel's markup, built into its price.
/// </summary>
internal sealed class Brokerage
{
    /// <summary>
    ///     The clients by how much they trade, most first, and the region covering each: the busiest spread across them, so
    ///     the Americas deal over 40% of the volume (the busiest client alone nearly that), EMEA a third and APAC — the most
    ///     clients, each smaller — about a quarter.
    /// </summary>
    private static readonly (string Name, string Ref, string Region)[] ByActivity =
    [
        ("Cedar Trust", "CEDAR", "Americas"), ("Fern Partners", "FERN", "EMEA"), ("Acorn Fund", "ACORN", "APAC"),
        ("Juniper LP", "JUNIPER", "APAC"), ("Birch Capital", "BIRCH", "EMEA"), ("Hazel Fund", "HAZEL", "APAC"),
        ("Damson LP", "DAMSON", "Americas"), ("Laurel & Co", "LAUREL", "EMEA"), ("Elder & Co", "ELDER", "Americas"),
        ("Ivy Partners", "IVY", "APAC"), ("Gorse Capital", "GORSE", "Americas"), ("Kelp Trust", "KELP", "APAC")
    ];

    /// <summary>
    ///     The house's working capital, from its treasury as the books open: its bank account's, in US dollars, and each of
    ///     its cash accounts' at the venues, in that account's currency (the venues' margin).
    /// </summary>
    public const decimal BankCapital = 25_000_000, VenueCapital = 5_000_000;

    /// <summary>The house's counterparty id: every client trades with it.</summary>
    public const long HouseId = 1;

    private readonly Dictionary<long, decimal> _balances = []; // by account
    private readonly HashSet<Guid> _capital = []; // the postings of working capital (kept whatever's trimmed)
    private readonly Client[] _clients;

    private readonly decimal[]
        _clientsHold =
            new decimal[Pivot.Assets.Length]; // the clients' net position in each asset (the house holds the opposite)

    private readonly double[] _clientWeight;

    private readonly (decimal Qty, decimal Avg, double Realized, double Volume, long Trades)[] _house =
        new (decimal, decimal, double, double, long)[Pivot.Assets.Length]; // its side of its clients', by asset

    private readonly Account _houseCash; // the house's US-dollar account, and its account in each perpetual
    private readonly Account[] _housePerpetuals;

    // By instrument: the house's account in each venue's perpetual, and the venue's facing it; by venue and currency, the
    // house's cash there and the venue's facing it; by currency, the treasury's.
    private readonly Dictionary<short, (Account House, Account Venue)> _listed = [];
    private readonly Dictionary<(string Venue, string Ccy), (Account House, Account Venue)> _venueCash = [];
    private readonly Dictionary<string, Account> _treasury = [];

    private readonly
        Dictionary<(long Account, string Channel), (decimal Qty, decimal Avg, double Realized, double Volume, long
            Trades)> _positions = [];

    private readonly Random _random;
    private long _positionId;

    public Brokerage(int seed, DateTime opened)
    {
        _random = new Random(seed);
        var house = new Cpty(HouseId, "House", "HOUSE", Pivot.None, true, """{"role":"principal"}""", opened);
        var cptys = new List<Cpty> { house };
        var accounts = new List<Account>();

        Account Open(Cpty cpty, string ccy, string? venue = null)
        {
            var holds = Desk.Ccys.First(c => c.CcyName == ccy).IsDeliverable ? "pnl" : "position";
            var account = new Account(accounts.Count + 1,
                venue is null || ccy.EndsWith(venue.ToUpperInvariant(), StringComparison.Ordinal)
                    ? $"{cpty.CptyRef}-{ccy}"
                    : $"{cpty.CptyRef}-{venue.ToUpperInvariant()}-{ccy}",
                cpty.CptyId, Desk.CcyId(ccy), ccy,
                venue is null
                    ? $$"""{"holds":"{{holds}}"}"""
                    : $$"""{"holds":"{{holds}}","venue":"{{venue}}"}""", opened);
            accounts.Add(account);
            return account;
        }

        _houseCash = Open(house, "USD");
        _housePerpetuals = [.. Pivot.Assets.Select(a => Open(house, Desk.Perpetual(a, "USD")))];
        var favourites = new Random(23);
        var clients = new List<Client>();
        foreach (var (name, reference, region) in ByActivity)
        {
            int rank = clients.Count + 1,
                breadth = Math.Max(2, (int)Math.Round(Pivot.Assets.Length / Math.Pow(rank, 0.8)));
            var cpty = new Cpty(1 + rank, name, reference, region, false,
                $$"""{"tier":{{(rank <= 2 ? 1 : rank <= 6 ? 2 : 3)}}}""", opened);
            cptys.Add(cpty);
            var assets = Favourites(favourites, breadth);
            var cash = Open(cpty, "USD");
            var perpetuals = assets.Order()
                .ToDictionary(a => a, a => Open(cpty, Desk.Perpetual(Pivot.Assets[a], "USD")));
            clients.Add(new Client(cpty, cash, perpetuals, 1.0 / rank, [.. assets.Select((a, j) => (a, 1.0 / (j + 1)))],
                Channels(rank)));
        }

        // The treasury, in each currency positions settle in; and each venue — the house's cash there, in each currency
        // it settles in, and its account in each perpetual listed there — each facing the venue's own.
        var treasury = new Cpty(cptys.Count + 1, "Treasury", "TREASURY", Pivot.None, false, """{"role":"treasury"}""",
            opened);
        cptys.Add(treasury);
        foreach (var ccy in Pivot.Counters) _treasury[ccy] = Open(treasury, ccy);
        for (var v = 0; v < Pivot.Venues.Length; v++)
        {
            var name = Pivot.Venues[v];
            var venue = new Cpty(cptys.Count + 1, name, name.ToUpperInvariant(), Pivot.None, false,
                """{"role":"venue"}""", opened);
            cptys.Add(venue);
            foreach (var (ccy, _) in Desk.VenueQuotes[v]) _venueCash[(name, ccy)] = (Open(house, ccy, name), Open(venue, ccy, name));
            foreach (var i in Desk.Instruments.Where(i => i.Venue == name))
                _listed[i.InstrumentId] = (Open(house, Desk.Perpetual(i), name), Open(venue, Desk.Perpetual(i), name));
        }

        _clients = [.. clients];
        _clientWeight = [.. _clients.Select(c => c.Weight)];
        Cptys = [.. cptys.OrderBy(c => c.CptyId)];
        Accounts = [.. accounts];
    }

    public Cpty[] Cptys { get; }
    public Account[] Accounts { get; }

    /// <summary>The client trades so far, and their journal entries.</summary>
    public long Trades { get; private set; }

    public int Clients => _clients.Length;
    public long Entries { get; private set; }

    /// <summary>
    ///     A client's channels, by its rank: the busiest on FIX and API, the middle on API and GUI, the rest on GUI,
    ///     mobile and voice.
    /// </summary>
    private static (string Name, double Weight)[] Channels(int rank)
    {
        return rank switch
        {
            <= 2 => [("FIX", 0.6), ("API", 0.3), ("Voice", 0.1)],
            <= 6 => [("API", 0.5), ("FIX", 0.2), ("GUI", 0.3)],
            _ => [("GUI", 0.5), ("Mobile", 0.3), ("Voice", 0.2)]
        };
    }

    /// <summary>A client's assets, its favourites first: drawn without replacement by how popular each is.</summary>
    private static List<int> Favourites(Random random, int breadth)
    {
        var pool = Enumerable.Range(0, Pivot.Assets.Length).ToList();
        var picked = new List<int>();
        while (picked.Count < breadth)
        {
            var r = random.NextDouble() * pool.Sum(a => Desk.AssetWeight[a]);
            var at = 0;
            while (at < pool.Count - 1 && (r -= Desk.AssetWeight[pool[at]]) >= 0) at++;
            picked.Add(pool[at]);
            pool.RemoveAt(at);
        }

        return picked;
    }

    /// <summary>The house's position in an asset, facing its clients: the opposite of theirs.</summary>
    public decimal HouseHolds(int asset)
    {
        return -_clientsHold[asset];
    }

    /// <summary>
    ///     A client's trade at <paramref name="ts" />: a posting, its journal entries and the positions after it, the
    ///     client's and the house's (in the perpetual, across its clients) — in lots, leaning against the client's position
    ///     as it grows (so positions turn over, long and short), at the mark plus (bought) or minus (sold) its channel's
    ///     markup. The asset it traded.
    /// </summary>
    /// <param name="weight">
    ///     How many trades it stands for (a primed history's, past 50,000 a day): its volume and trades
    ///     counted so.
    /// </param>
    public int Trade(DateTime ts, MarketTape market, List<Posting> postings, List<Journal> journal,
        List<PositionRisk> risks, int[]? inPlay = null, long weight = 1)
    {
        // In a burst, the assets in play: a client trading one of them, in one of them (any client, any asset, if none does).
        var eligible = inPlay is null
            ? null
            : _clients.Where(c => c.Assets.Any(a => inPlay.Contains(a.Asset))).ToArray();
        var client = eligible is { Length: > 0 }
            ? eligible[Pick([.. eligible.Select(c => c.Weight)])]
            : _clients[Pick(_clientWeight)];
        var assets = eligible is { Length: > 0 }
            ? client.Assets.Where(a => inPlay!.Contains(a.Asset)).ToArray()
            : client.Assets;
        var asset = assets[Pick([.. assets.Select(a => a.Weight)])].Asset;
        var channel = Pick(client.Channels);
        var account = client.Perpetuals[asset];
        var key = (account.AccountId, channel);
        var (held, avg, cumulative, volume, trades) = _positions.GetValueOrDefault(key);
        var limit = Desk.Lot[asset] * 30;
        var buy = 0.5 - 0.35 * Math.Tanh((double)(held / limit));
        var qty = Desk.Lot[asset] * (1 + _random.Next(6)) * (_random.NextDouble() < buy ? 1 : -1);
        double mid = market.UsdPrice(asset), markup = Desk.MarkupBps(channel);
        var price = Math.Round((decimal)(mid * (1 + Math.Sign(qty) * markup / 10_000)),
            mid < 1 ? 6 : mid < 100 ? 4 : 2);
        var (after, avgAfter, realized) =
            AverageEntry.Apply(held, avg, qty, price); // (in US dollars, the perpetual's quote)

        var posting = Guid.CreateVersion7(new DateTimeOffset(ts));
        postings.Add(new Posting(posting, PostingCode.ClientTrade, JsonSerializer.Serialize(new
        {
            channel, side = qty > 0 ? "buy" : "sell", qty, price_usd = price, mid_usd = Math.Round(mid, 8),
            markup_bps = markup, cl_ord_id = $"{client.Cpty.CptyRef}-{Entries + 1}"
        }), ts));

        var first = Entry(posting, ts, account, qty, (double)price, journal); // the perpetual: to the client, from the house
        var houses = Entry(posting, ts, _housePerpetuals[asset], -qty, (double)price, journal);
        if (realized != 0) // what it realized, settled in US dollars: to the client, from the house
        {
            Entry(posting, ts, client.Cash, realized, 1, journal);
            Entry(posting, ts, _houseCash, -realized, 1, journal);
        }

        cumulative += (double)realized;
        var notional = weight * (double)Math.Abs(qty * price); // (its notional, in US dollars)
        volume += notional;
        trades += weight;
        Trades += weight;
        _positions[key] = (after, avgAfter, cumulative, volume, trades);
        _clientsHold[asset] += qty;
        var instrument = Desk.HousePerpetual(asset).InstrumentId;
        risks.Add(new PositionRisk(++_positionId, client.Cpty.CptyId, account.AccountId, instrument, channel, first,
            posting, after, (double)avgAfter, mid, ts, 0, cumulative, volume, trades));
        // The house's side: the opposite, at its own average entry — what it realizes as its clients' trades net.
        var (houseHeld, houseAvg, houseRealized, houseVolume, houseTrades) = _house[asset];
        var (houseAfter, houseAvgAfter, houseGained) = AverageEntry.Apply(houseHeld, houseAvg, -qty, price);
        _house[asset] = (houseAfter, houseAvgAfter, houseRealized += (double)houseGained, houseVolume += notional,
            houseTrades += weight);
        risks.Add(new PositionRisk(++_positionId, HouseId, _housePerpetuals[asset].AccountId, instrument, Pivot.None,
            houses, posting, houseAfter, (double)houseAvgAfter, mid, ts, 0, houseRealized, houseVolume, houseTrades));
        return asset;
    }

    /// <summary>The postings of working capital (<see cref="Fund" />): the books' opening, kept whatever's trimmed.</summary>
    public IReadOnlySet<Guid> Capital => _capital;

    /// <summary>
    ///     The books opened at <paramref name="ts" />: the house's working capital, from the treasury — its bank account's,
    ///     and each of its cash accounts' at the venues (<see cref="BankCapital" />, <see cref="VenueCapital" />), a posting
    ///     each, at the currency's rate then.
    /// </summary>
    public void Fund(DateTime ts, FxTape fx, List<Posting> postings, List<Journal> journal)
    {
        Transfer(_houseCash, BankCapital);
        foreach (var (house, _) in _venueCash.Values) Transfer(house, VenueCapital);

        void Transfer(Account to, decimal amount)
        {
            var posting = Guid.CreateVersion7(new DateTimeOffset(ts));
            _capital.Add(posting);
            postings.Add(new Posting(posting, PostingCode.Capital,
                JsonSerializer.Serialize(new { amount, ccy = to.CcyName, account = to.AccountRef }), ts));
            var rate = fx.UsdRate(to.CcyName);
            Entry(posting, ts, to, amount, rate, journal);
            Entry(posting, ts, _treasury[to.CcyName], -amount, rate, journal);
        }
    }

    /// <summary>The next position row's id: every row's in position_risk, the hedges' too.</summary>
    public long NextPositionId()
    {
        return ++_positionId;
    }

    /// <summary>The house's account in a venue's perpetual (its hedges' there).</summary>
    public Account Listed(Instrument instrument)
    {
        return _listed[instrument.InstrumentId].House;
    }

    /// <summary>
    ///     A hedge fill of the house's at <paramref name="ts" />, booked: a posting — the perpetual to the house's account at
    ///     the venue and from the venue's, and what it realized less its fee the other way, in what the instrument settles
    ///     in (its quote: the house's cash at the venue in it). Its posting, and the house's entry in the perpetual.
    /// </summary>
    public (Guid Posting, long Entry) Hedged(DateTime ts, Instrument instrument, decimal qty, double price,
        decimal realized, decimal fee, double quoteRate, List<Posting> postings, List<Journal> journal)
    {
        var posting = Guid.CreateVersion7(new DateTimeOffset(ts));
        postings.Add(new Posting(posting, PostingCode.HedgeFill, JsonSerializer.Serialize(new
        {
            venue = instrument.Venue, instrument = instrument.InstrumentName, side = qty > 0 ? "buy" : "sell", qty,
            price, realized, fee, ccy = instrument.Quote
        }), ts));
        var (house, venue) = _listed[instrument.InstrumentId];
        var entry = Entry(posting, ts, house, qty, price * quoteRate, journal);
        Entry(posting, ts, venue, -qty, price * quoteRate, journal);
        if (realized - fee is var settled && settled != 0) // (one entry: an account's balance once a posting)
        {
            var (houseCash, venueCash) = _venueCash[(instrument.Venue, instrument.Quote)];
            Entry(posting, ts, houseCash, settled, quoteRate, journal);
            Entry(posting, ts, venueCash, -settled, quoteRate, journal);
        }

        return (posting, entry);
    }

    private long Entry(Guid posting, DateTime ts, Account to, decimal amount, double usdRate, List<Journal> journal)
    {
        var balance = _balances.GetValueOrDefault(to.AccountId) + amount;
        _balances[to.AccountId] = balance;
        journal.Add(new Journal(++Entries, posting, to.AccountId, to.CcyId, amount, usdRate, balance, ts));
        return Entries;
    }

    /// <summary>The client positions there are (by account — a perpetual's — and channel), and how many are open.</summary>
    public (int Positions, int Open) Count()
    {
        return (_positions.Count, _positions.Values.Count(p => p.Qty != 0));
    }

    private int Pick(double[] weights)
    {
        var r = _random.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length - 1; i++)
            if ((r -= weights[i]) < 0)
                return i;
        return weights.Length - 1;
    }

    private string Pick((string Name, double Weight)[] names)
    {
        return names[Pick([.. names.Select(n => n.Weight)])].Name;
    }

    /// <summary>
    ///     A client: its accounts (in US dollars, and in each perpetual it trades, by asset), how much it trades, what
    ///     and how.
    /// </summary>
    private sealed record Client(
        Cpty Cpty,
        Account Cash,
        Dictionary<int, Account> Perpetuals,
        double Weight,
        (int Asset, double Weight)[] Assets,
        (string Name, double Weight)[] Channels);
}

/// <summary>
///     The clients' trading, bursty: flurries — a few assets in play at once, a handful of trades within tens of
///     milliseconds
///     (now and then dozens: the size heavy-tailed, lognormal) — and quiet between them, at the pace asked for on average.
/// </summary>
internal sealed class Bursts(int seed)
{
    public const double MeanSize = 6;
    private const double Sigma = 0.9; // (the size's spread: median ~4, a few in a hundred over 25)
    private readonly Random _random = new(seed);

    /// <summary>
    ///     The next burst after <paramref name="after" /> at <paramref name="perSecond" /> trades a second, on average: when
    ///     it
    ///     starts, how many trades, over how long, and the assets in play — or none (DateTime.MaxValue), at no pace.
    /// </summary>
    public (DateTime Start, int Size, TimeSpan Span, int[] Assets) Next(DateTime after, double perSecond)
    {
        if (perSecond <= 0) return (DateTime.MaxValue, 0, TimeSpan.Zero, []);
        var gap = -Math.Log(1 - _random.NextDouble()) * MeanSize / perSecond; // (exponential: bursts a Poisson process)
        var size = (int)Math.Clamp(Math.Round(Math.Exp(Math.Log(MeanSize) - Sigma * Sigma / 2 + Sigma * Normal())), 1,
            200);
        var span = TimeSpan.FromMilliseconds(Math.Min(250, size * (2 + 13 * _random.NextDouble())));
        var inPlay = Math.Min(size, 1 + _random.Next(4));
        var pool = Enumerable.Range(0, Pivot.Assets.Length).ToList();
        var assets = new int[inPlay];
        for (var i = 0; i < inPlay; i++) // (drawn by how popular each is)
        {
            var r = _random.NextDouble() * pool.Sum(a => Desk.AssetWeight[a]);
            var at = 0;
            while (at < pool.Count - 1 && (r -= Desk.AssetWeight[pool[at]]) >= 0) at++;
            assets[i] = pool[at];
            pool.RemoveAt(at);
        }

        return (after + TimeSpan.FromSeconds(gap), size, span, assets);
    }

    private double Normal()
    {
        return Math.Sqrt(-2 * Math.Log(1 - _random.NextDouble())) * Math.Cos(2 * Math.PI * _random.NextDouble());
    }
}

/// <summary>
///     The house's hedger: its position in each asset — the opposite of its clients', plus its hedges — offset on external
///     venues, most of it, after a short while: in a perpetual on one of the venues, quoted in one of the venue's
///     counters,
///     in lots, at its mark (a little worse: the market's spread), paying the venue's taker fee. It hedges the asset, not
///     the clients' instrument: their BTC/USD Perp @ House, by a mix of the
///     venues' perpetuals — mostly BTC/USDT Perp and BTC/USDC Perp — routed where it's cheapest, each venue's price
///     normalised for its basis (its premium over the index, in its quote) and its fee: buying the cheaper and selling the
///     richer, venue against venue and quote against quote. So it holds logical instruments its clients don't trade, which
///     don't net against theirs: what basis it's left holding, and the stablecoins' rates, stay in the PnL. Ideally the
///     hedges offset the clients' trades exactly, and what's left is PnL: the markup earned, less the fees, and whatever
///     the
///     market did to what wasn't hedged yet.
/// </summary>
internal sealed class Hedger(int seed)
{
    // Where a hedge goes, leaning: each venue's perpetuals on the asset (one in each counter) weighed by e^(edge/2 bp), its
    // edge the basis it buys under (or sells over) the others', less its venue's fee — 4 bp is ~7× likelier — and 6 bp
    // more if it reduces a position held the other way there (the margin and funding that frees), so buys and sells net
    // rather than pile up.
    private const double LeanBps = 2, ReduceBps = 6;
    private readonly decimal[] _hedged = new decimal[Pivot.Assets.Length];

    private readonly Dictionary<short, (decimal Qty, decimal Avg, decimal Realized, double Volume, long Fills)>
        _positions = []; // by instrument

    private readonly Random _random = new(seed);

    /// <summary>The hedge fills so far.</summary>
    public long Fills { get; private set; }

    /// <summary>What the house's hedges hold in an asset.</summary>
    public decimal Holds(int asset)
    {
        return _hedged[asset];
    }

    /// <summary>
    ///     The house's exposure in an asset — what it holds facing its clients, and its hedges — offset, 70% to all of it in
    ///     lots, if it's a lot or more: a fill, booked by <paramref name="books" /> (its posting and journal entries), and
    ///     the hedge position after it.
    /// </summary>
    public void Hedge(DateTime ts, int asset, decimal houseHolds, MarketTape market, FxTape fx, Brokerage books,
        List<Posting> postings, List<Journal> journal, List<HedgeFill> fills, List<PositionRisk> risks, long weight = 1)
    {
        decimal exposure = houseHolds + _hedged[asset], lot = Desk.Lot[asset];
        var qty = -Math.Round(exposure * (decimal)(0.7 + 0.3 * _random.NextDouble()) / lot) * lot;
        if (qty == 0) return;
        var name = Pivot.Assets[asset];
        var perpetuals = Pivot.Venues.SelectMany((at, v) =>
            Desk.VenueQuotes[v].Select(q => (Instrument: Desk.Listed(at, name, q.Name), q.Weight))).ToArray();

        double Edge(Instrument i)
        {
            return -Math.Sign(qty) * market.PremiumBps(i) - Desk.VenueFeeBps[Array.IndexOf(Pivot.Venues, i.Venue)]
                   + (Math.Sign(_positions.GetValueOrDefault(i.InstrumentId).Qty) == -Math.Sign(qty) ? ReduceBps : 0);
        }

        var (instrument, _) =
            perpetuals[
                Pick([
                    .. perpetuals.Select(p => p.Weight / Pivot.Venues.Length * Math.Exp(Edge(p.Instrument) / LeanBps))
                ])];
        var v = Array.IndexOf(Pivot.Venues, instrument.Venue);
        var mark = market.Price(instrument, fx);
        var inQuote = mark * (1 + Math.Sign(qty) * (0.5 + _random.NextDouble()) / 10_000); // (the market's spread: a little worse)
        var price = Math.Round(inQuote,
            inQuote < 1 ? 6 : inQuote < 100 ? 4 : 2); // (a price: FLOAT, as the schema has it)
        var fee = Math.Round(Math.Abs(qty * (decimal)price) * (decimal)Desk.VenueFeeBps[v] / 10_000, 8);
        var key = instrument.InstrumentId;
        var (held, avg, realized, volume, filled) = _positions.GetValueOrDefault(key);
        var (after, avgAfter, gained) =
            AverageEntry.Apply(held, avg, qty, (decimal)price); // (the keeper's average: exact; written as a price)
        volume += weight * Math.Abs((double)qty * price) *
                  fx.UsdRate(instrument.Quote); // (its notional, in US dollars at the fill)
        _positions[key] = (after, avgAfter, realized += gained - fee, volume, filled += weight);
        _hedged[asset] += qty;
        // Booked: the posting, the perpetual from the venue, what it realized less the fee settled to the house's cash
        // there — and the position after it (the house's, in the venue's perpetual).
        var (posting, entry) = books.Hedged(ts, instrument, qty, price, gained, fee, fx.UsdRate(instrument.Quote),
            postings, journal);
        fills.Add(new HedgeFill(++Fills, posting, ts, instrument.InstrumentId, qty, price, fee));
        risks.Add(new PositionRisk(books.NextPositionId(), Brokerage.HouseId, books.Listed(instrument).AccountId,
            instrument.InstrumentId, Pivot.None, entry, posting, after, (double)avgAfter, mark, ts, 0, (double)realized,
            volume, filled));
    }

    private int Pick(double[] weights)
    {
        var r = _random.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length - 1; i++)
            if ((r -= weights[i]) < 0)
                return i;
        return weights.Length - 1;
    }
}

/// <summary>How much the simulated prices move: as markets do (realistic, the default), or for something to see (demo).</summary>
public enum Volatility
{
    Demo,
    Realistic
}

/// <summary>
///     Simulated marks: each asset's US-dollar price walking (its index); and each venue perpetual's premium over the
///     index
///     in its quote, mean-reverting — one part shared by the venues quoting the asset in that currency (about 5 bp either
///     way,
///     its half-life 15 minutes), one its venue's own (about 2 bp, half-life 5 minutes). Each instrument marked at the
///     index,
///     in its quote, plus its premium; the house's own at the index.
/// </summary>
internal sealed class MarketTape(int seed, Volatility volatility = Volatility.Realistic)
{
    private const double BasisBps = 5, BasisHalfLife = 15 * 60, VenueBps = 2, VenueHalfLife = 5 * 60;
    private static readonly double[] Start = [0.40, 30, 65_000, 0.12, 3_400, 14, 150, 0.55]; // in Pivot.Assets' order

    private static readonly int[] AssetOf =
        [.. Desk.Instruments.Select(i => Array.IndexOf(Pivot.Assets, i.Underlying))];

    private static readonly int[] QuoteOf = [.. Desk.Instruments.Select(i => Array.IndexOf(Pivot.Counters, i.Quote))];
    private readonly double[] _ownBps = new double[Desk.Instruments.Length];
    private readonly double[] _prices = (double[])Start.Clone();
    private readonly Random _random = new(seed), _basis = new(seed + 1), _venues = new(seed + 2);
    private readonly double[,] _sharedBps = new double[Start.Length, Pivot.Counters.Length];

    public double UsdPrice(int asset)
    {
        return _prices[asset];
    }

    /// <summary>
    ///     An instrument's premium over the index, in basis points: the basis its venue shares with the others quoting
    ///     the asset in its currency, and its own (the house's: none).
    /// </summary>
    public double PremiumBps(Instrument i)
    {
        var k = i.InstrumentId - 1;
        return i.Venue == Pivot.House ? 0 : _sharedBps[AssetOf[k], QuoteOf[k]] + _ownBps[k];
    }

    /// <summary>An instrument's mark, in its quote: the index at the quote's rate, plus its premium.</summary>
    public double Price(Instrument i, FxTape fx)
    {
        return _prices[AssetOf[i.InstrumentId - 1]] / fx.UsdRate(i.Quote) * (1 + PremiumBps(i) / 10_000);
    }

    /// <summary>
    ///     <paramref name="seconds" /> of the market: each price walks — demo, 6 bp a √second (about 2% a day's worth of
    ///     volatility an hour, for something to see); realistic, 1 bp (about 3% a day, 57% a year: a crypto asset's).
    /// </summary>
    public void Step(double seconds)
    {
        var sd = (volatility == Volatility.Realistic ? 0.0001 : 0.0006) * Math.Sqrt(seconds);
        for (var i = 0; i < _prices.Length; i++) _prices[i] *= Math.Exp(sd * Normal(_random));
        double keep = Math.Pow(0.5, seconds / BasisHalfLife),
            shock = BasisBps * Math.Sqrt(1 - keep * keep); // (exact for any step: the history's hours too)
        for (var i = 0; i < _prices.Length; i++)
        for (var c = 0; c < Pivot.Counters.Length; c++)
            _sharedBps[i, c] = _sharedBps[i, c] * keep + shock * Normal(_basis);
        double keepOwn = Math.Pow(0.5, seconds / VenueHalfLife), shockOwn = VenueBps * Math.Sqrt(1 - keepOwn * keepOwn);
        for (var k = 0; k < _ownBps.Length; k++) _ownBps[k] = _ownBps[k] * keepOwn + shockOwn * Normal(_venues);
    }

    /// <summary>Each instrument's mark, at <paramref name="ts" /> — in marks' order: by time, then instrument.</summary>
    public void Marks(DateTime ts, FxTape fx, List<Mark> into)
    {
        foreach (var i in Desk.Instruments) into.Add(new Mark(ts, i.InstrumentId, Price(i, fx)));
    }

    private static double Normal(Random random)
    {
        return Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
    }
}

/// <summary>
///     Simulated rates: each G10 currency's US-dollar rate walking, and each stablecoin's held near 1 (pulled back to its
///     peg as it wanders), each ticking every second or three — the US dollar's own, 1, too: every position finds a rate
///     as
///     of its time, whatever its currency.
/// </summary>
internal sealed class FxTape(int seed, Volatility volatility = Volatility.Realistic)
{
    private static readonly double[]
        Start =
        [
            1, 1.08, 0.0067, 1.27, 1.12, 0.73, 0.66, 0.60, 0.095, 0.093, 1, 1
        ]; // USD per unit, in Pivot.Symbols' order

    private static readonly int[] ByName =
        [.. Enumerable.Range(0, Start.Length).OrderBy(i => Pivot.Symbols[i], StringComparer.Ordinal)];

    private readonly DateTime[] _due = new DateTime[Start.Length];
    private readonly Random _random = new(seed);
    private readonly double[] _rates = (double[])Start.Clone();

    /// <summary>What one unit of <paramref name="symbol" /> is worth in US dollars, now.</summary>
    public double UsdRate(string symbol)
    {
        return _rates[Array.IndexOf(Pivot.Symbols, symbol)];
    }

    /// <summary>
    ///     The rates that ticked by <paramref name="now" /> (every rate the first time, and with <paramref name="all" />) — in
    ///     conversion_rate's order: by time, then symbol.
    /// </summary>
    public void Next(DateTime now, List<ConversionRate> into, bool all = false)
    {
        foreach (var i in ByName)
        {
            if (now < _due[i] && !all) continue;
            var symbol = Pivot.Symbols[i];
            // A stablecoin near its peg; a G10 currency walking — demo, about 3.6% a day; realistic, about 0.5% (its noise a seventh).
            var realistic = volatility == Volatility.Realistic;
            if (Pivot.Stablecoins.Contains(symbol))
                _rates[i] = 1 + (_rates[i] - 1) * 0.8 + (_random.NextDouble() - 0.5) * (realistic ? 0.0001 : 0.0004);
            else if (symbol != "USD") _rates[i] *= 1 + (_random.NextDouble() - 0.5) * (realistic ? 0.00008 : 0.0006);
            into.Add(new ConversionRate(now, symbol, _rates[i]));
            _due[i] = now.AddSeconds(1 + 2 * _random.NextDouble());
        }
    }
}