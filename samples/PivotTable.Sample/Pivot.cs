using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tapline;

namespace PivotTable.Sample;

/// <summary>
///     A dimension a pivot can group or filter by: its name, its column — in the statement's position rows (what totals
///     group
///     by), and in the table it comes from (what filters test: the days', the positions') — and its values.
/// </summary>
public sealed record Dimension(string Name, string Column, string[] Values)
{
    /// <summary>Its name as a label shows it ("Client"), from its [Display]; the statement's, and prose's, lowercase.</summary>
    public string Title => Pivot.DisplayOf(this).GetName() ?? Name;

    /// <summary>What it is, tersely — its label's tooltip — from its [Display].</summary>
    public string Description => Pivot.DisplayOf(this).GetDescription() ?? "";
}

/// <summary>A value a cell shows: its key (the page's, the grid's), its label and its tooltip from its [Display].</summary>
public sealed record Measure(string Key)
{
    public string Title => Pivot.DisplayOf(this).GetName() ?? Key;
    public string Description => Pivot.DisplayOf(this).GetDescription() ?? "";
}

public static class Pivot
{
    public const string House = "House";

    /// <summary>A dimension's value on a leg it doesn't apply to: a hedge's channel.</summary>
    public const string None = "—";

    /// <summary>The trading days kept: the current one, and the nine before it (by any zone's and cutoff's reckoning).</summary>
    public const int DaysKept = 10;

    /// <summary>
    ///     What a pivot's statement may answer besides: <c>max_gross</c> — each client's larger side, long or short notional
    ///     (its positions netted by instrument), summed over the clients a cell spans: a measure of margin utilisation, none
    ///     where it doesn't apply (the house's hedges) or doesn't add up (across days). Absent, a cell has none.
    /// </summary>
    public const string MaxGrossColumn = "max_gross";

    /// <summary>
    ///     And <c>mark_price</c>: a rate — the instrument's quote per unit of its asset, as its venue marks it at the close —
    ///     averaged over the instruments (each venue's listing) and days a cell spans, each once; none where it spans more
    ///     than
    ///     one logical instrument (its units: one pair). Absent, a cell has none.
    /// </summary>
    public const string MarkColumn = "mark_price";

    /// <summary>A cell's figures' places: eight, the statement's DECIMAL scale (a satoshi; at most ±92,233,720,368 units).</summary>
    public const int Places = PivotCell.Places;

    /// <summary>
    ///     A cell's volume's places: two, cents. A day's trading runs past the ±92 billion eight places hold — tens of
    ///     billions
    ///     of dollars at 10M client trades a day, hundreds at 100M — and to the cent a cell holds ±92 quadrillion.
    /// </summary>
    public const int VolumePlaces = PivotCell.VolumePlaces;

    public static readonly string[] Assets = ["ADA", "AVAX", "BTC", "DOGE", "ETH", "LINK", "SOL", "XRP"];

    public static readonly string[]
        Venues =
        [
            "Binance", "Bybit", "Coinbase", "Kraken", "OKX"
        ]; // (alphabetical: the grid's columns, and the filters, in this order)

    /// <summary>
    ///     The counterparties: the clients — fictional; a handful trade much, and many assets, the rest less, and in a few
    ///     (<see cref="Brokerage" />) — and the house, which faces them all and holds the hedges.
    /// </summary>
    public static readonly string[] Clients =
    [
        "Acorn Fund", "Birch Capital", "Cedar Trust", "Damson LP", "Elder & Co", "Fern Partners",
        "Gorse Capital", "Hazel Fund", "Ivy Partners", "Juniper LP", "Kelp Trust", "Laurel & Co", House
    ];

    /// <summary>
    ///     The channels clients trade on (each trade's, in its posting's notes): each with its markup (
    ///     <see cref="Desk.MarkupBps" />).
    /// </summary>
    public static readonly string[] Channels = ["API", "FIX", "GUI", "Mobile", "Voice"];

    /// <summary>The regions covering the clients (<see cref="Brokerage" />): each client is one's.</summary>
    public static readonly string[] Regions = ["Americas", "EMEA", "APAC"];

    /// <summary>The house's legs: risk (its side of its clients' trades) and hedge (its positions on the venues).</summary>
    public static readonly string[] Legs = ["risk", "hedge"];

    /// <summary>The G10 currencies: what a pivot can report its amounts in (and what the FX rates cover).</summary>
    public static readonly string[] Currencies = ["USD", "EUR", "JPY", "GBP", "CHF", "CAD", "AUD", "NZD", "SEK", "NOK"];

    /// <summary>The stablecoins positions are quoted and settled in: dollar tokens, each with a rate of its own (near 1).</summary>
    public static readonly string[] Stablecoins = ["USDT", "USDC"];

    /// <summary>
    ///     What positions are in — a hedge's counter, venue by venue (<see cref="Desk.VenueQuotes" />); a client
    ///     perpetual's quote (US dollars): stablecoins, then the dollar. Dollars alone, so positions in an asset net across
    ///     its instruments with no currency left over (bar the stablecoins' rates, near 1: in the PnL).
    /// </summary>
    public static readonly string[] Counters = ["USDT", "USDC", "USD"];

    /// <summary>
    ///     The logical instruments: each asset's perpetual in each counter (BTC/USDT Perp), whichever venue lists it — the
    ///     house its own, in US dollars, and the venues theirs, each in every counter it quotes (
    ///     <see cref="Desk.Instruments" />).
    /// </summary>
    public static readonly string[] Instruments = [.. Assets.SelectMany(a => Counters.Select(q => Logical(a, q)))];

    /// <summary>What conversion_rate covers: the G10 currencies, and the stablecoins.</summary>
    public static readonly string[] Symbols = [.. Currencies, .. Stablecoins];

    /// <summary>What "last n days" offers.</summary>
    public static readonly int[] LastDays = [1, 2, 3, 5, 10];

    /// <summary>
    ///     The cutoffs a day can end at, local time: on the hour (every zone's offset is whole hours: each day closes on
    ///     an hourly sample).
    /// </summary>
    public static readonly string[] Cutoffs = [.. Enumerable.Range(0, 24).Select(h => $"{h:00}:00")];

    /// <summary>
    ///     What a pivot can group and filter by — each a column of the statement's position rows (its <c>WITH</c>), the
    ///     house's: its leg (risk or hedge), the logical instrument (an asset's perpetual in a quote: positions in one net, in
    ///     two don't), the region covering the client and the client (the house, for a hedge), the channel a client traded
    ///     on, the venue listing the instrument (the house, for a client's), the currency (a client perpetual's quote, a
    ///     hedge's counter), the trading date, and the direction at the day's close. A dimension that doesn't apply to a leg is "—" there. More
    ///     than a grid shows at once (three in rows, two in columns): the rest are filters.
    /// </summary>
    public static readonly Dimension[] Dimensions;

    /// <summary>The values a cell can show — the toolbar's, in order — and the two Pos PnL is made of (a sort's too).</summary>
    public static readonly Measure[] Measures;

    public static readonly Measure[] PnLParts;

    /// <summary>
    ///     The lists of dimensions and values, made once every static initializer has run — wherever their members are
    ///     written: an initializer runs in the file's order, and these name properties a layout may put after them.
    /// </summary>
    static Pivot()
    {
        Dimensions = [Leg, Underlier, Instrument, Region, Client, Channel, Venue, Date, Counter, Direction];
        Measures = [Position, Mark, Notional, MaxGross, PosPnL, Balance, Volume, Trades];
        PnLParts = [Realized, Unrealized];
    }

    private static readonly Lazy<Dictionary<object, DisplayAttribute>> Displays = new(() => typeof(Pivot)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(p => p.GetCustomAttribute<DisplayAttribute>() is not null)
        .ToDictionary(p => p.GetValue(null)!, p => p.GetCustomAttribute<DisplayAttribute>()!,
            ReferenceEqualityComparer.Instance));

    /// <summary>
    ///     What a pivot's statement answers, whatever else it does — one row per cell, totals included: <c>row_key</c> and
    ///     <c>column_key</c> (text: the cell's labels, its levels joined by " · "; a total's, fewer — the grand total's,
    ///     none), and <c>quantity</c> (units of the asset), <c>notional</c>, <c>realized_pnl</c>, <c>unrealized_pnl</c>,
    ///     <c>balance</c> and <c>volume</c> (its reporting currency) and <c>trades</c> (a count): numbers, or null where they
    ///     don't add up. The provider puts them in a cell (<see cref="PivotCell" />), to <see cref="Places" /> places.
    /// </summary>
    public static readonly string[] Contract =
    [
        "row_key", "column_key", "quantity", "notional", "realized_pnl", "unrealized_pnl", "balance", "volume", "trades"
    ];

    /// <summary>What a viewer is told if a pivot breaks (its last state kept, an error after it).</summary>
    public static readonly ErrorPolicy Policy = new("positions interrupted"u8.ToArray());

    /// <summary>
    ///     A cell's labels, in its key's order (<see cref="PivotKey" />: UTF-8, zero-padded): each one's name, the statement's
    ///     column it's made from, and its width — a pivot's shape (<see cref="PivotShape" />) and its viewer's format are made
    ///     of these.
    /// </summary>
    public static readonly CellLabel[] Labels = Checked<PivotKey, CellLabel>(
    [
        new CellLabel("row", "row_key", PivotKey.RowBytes), new CellLabel("column", "column_key", PivotKey.ColumnBytes)
    ], l => l.Bytes);

    /// <summary>
    ///     A cell's figures, in its row's order (<see cref="PivotCell" />: each an i64, little-endian): each one's name, the
    ///     statement's column it's made from, its places, its unit and what it is — a pivot's shape (<see cref="PivotShape" />
    ///     )
    ///     and its viewer's format are made of these. Checked against the cell's layout: the same fields, in its order.
    /// </summary>
    public static readonly CellFigure[] Figures = Checked<PivotCell, CellFigure>(
    [
        new CellFigure("quantity", "quantity", Places, "units of the cell's asset",
            "the position (none across assets)"),
        new CellFigure("notional", "notional", Places, "the reporting currency", "the position at the mark"),
        new CellFigure("realized", "realized_pnl", Places, "the reporting currency", "realized PnL"),
        new CellFigure("unrealized", "unrealized_pnl", Places, "the reporting currency",
            "unrealized PnL (none across days: it's each day's close)"),
        new CellFigure("balance", "balance", Places, "the reporting currency",
            "the cash its realized PnL so far has paid"),
        new CellFigure("volume", "volume", VolumePlaces, "the reporting currency", "what it traded"),
        new CellFigure("trades", "trades", Places, "a count", "its trades"),
        new CellFigure("max_gross", MaxGrossColumn, Places, "the reporting currency",
            "each client's larger side, long or short notional, summed (none where it doesn't apply, or across days)"),
        new CellFigure("mark_price", MarkColumn, Places, "the instrument's quote per unit of its asset",
            "a rate: its mark at the close, averaged over the venues and days it spans (none across instruments)")
    ], _ => sizeof(long));

    // Each dimension, its label and tooltip annotated ([Display]: the page shows them).
    [Display(Name = "Leg", Description = "Risk (house side of client positions) or hedge (house positions on venues)")]
    public static Dimension Leg { get; } = new("leg", "leg", Legs);

    [Display(Name = "Underlier",
        Description = "The asset an instrument is on — a position's units: positions net across its instruments")]
    public static Dimension Underlier { get; } = new("underlier", "underlying", Assets);

    [Display(Name = "Instrument",
        Description = "Logical instrument: perpetual on an asset in a quote, whichever venue lists it")]
    public static Dimension Instrument { get; } = new("instrument", "instrument", Instruments);

    [Display(Name = "Region", Description = "Region covering the client: Americas, EMEA or APAC")]
    public static Dimension Region { get; } = new("region", "region", [.. Regions, None]);

    [Display(Name = "Client", Description = "Counterparty facing the house")]
    public static Dimension Client { get; } = new("client", "client", Clients);

    [Display(Name = "Channel", Description = "Client trading channel: API, FIX, GUI, Mobile, or Voice")]
    public static Dimension Channel { get; } = new("channel", "channel", [.. Channels, None]);

    [Display(Name = "Venue", Description = "Exchange listing an instrument, or House for client instruments")]
    public static Dimension Venue { get; } = new("venue", "venue", [.. Venues, House]);

    [Display(Name = "Date", Description = "Trading date: the day ending at the end-of-day cutoff, named by the date it ends on")]
    public static Dimension Date { get; } =
        new("date", "trading_day", []); // (its values the days': last n, or a range of dates)

    [Display(Name = "Counter", Description = "Quote currency of an instrument")]
    public static Dimension Counter { get; } = new("counter", "counter_symbol", Counters);

    [Display(Name = "Direction", Description = "Long, short, or flat from house perspective")]
    public static Dimension Direction { get; } = new("direction", "direction", ["long", "short", "flat"]);

    // Each value, its label and tooltip annotated ([Display]: the page shows them, and the grid's headings).
    [Display(Name = "Position",
        Description = "Units of the asset at the close, from house perspective: + long, − short")]
    public static Measure Position { get; } = new("position");

    [Display(Name = "Mark price",
        Description =
            "Mark at the close, a rate in the instrument's quote per unit of its asset: averaged over the venues and days a cell spans")]
    public static Measure Mark { get; } = new("mark");

    [Display(Name = "Notional",
        Description = "Position value at the mark, in reporting currency, from house perspective")]
    public static Measure Notional { get; } = new("notional");

    [Display(Name = "Max gross",
        Description = "Larger of long and short notional per client, netted by instrument: margin use")]
    public static Measure MaxGross { get; } = new("gross");

    [Display(Name = "Pos PnL",
        Description = "Realized PnL for the day, plus unrealized PnL at the close, from house perspective")]
    public static Measure PosPnL { get; } = new("pnl");

    [Display(Name = "Realized", Description = "Realized PnL for the day, from house perspective")]
    public static Measure Realized { get; } = new("realized");

    [Display(Name = "Unrealized",
        Description = "Unrealized PnL at the close, against average entry, from house perspective")]
    public static Measure Unrealized { get; } = new("unrealized");

    [Display(Name = "Balance", Description = "Cumulative realized PnL, from house perspective")]
    public static Measure Balance { get; } = new("balance");

    [Display(Name = "Volume", Description = "Notional traded in the day")]
    public static Measure Volume { get; } = new("volume");

    [Display(Name = "Trades", Description = "Number of trades in the day")]
    public static Measure Trades { get; } = new("trades");

    /// <summary>A logical instrument's name: an asset's perpetual in a quote (BTC/USDT Perp), whichever venue lists it.</summary>
    public static string Logical(string asset, string quote)
    {
        return $"{asset}/{quote} Perp";
    }

    /// <summary>
    ///     The trading days a zone and a cutoff make, as of <paramref name="now" /> (UTC): the current one first (its close
    ///     now), then the days before it. A day ends at the cutoff, local time, and is named by the date it ends on — the
    ///     minute before its close — so a 17:00 cutoff's Tuesday runs from Monday 17:00 to Tuesday 17:00, and a 00:00
    ///     cutoff's Tuesday is the calendar day.
    /// </summary>
    public static IEnumerable<CalendarDay> TradingDays(string zone, string cutoff, DateTime now)
    {
        var at = TimeSpan.ParseExact(cutoff, @"hh\:mm", null);
        var local = TimeZones.ToLocal(zone, now);
        var close = local.TimeOfDay < at
            ? local.Date + at
            : local.Date.AddDays(1) + at; // the current day's close, local
        for (var ago = 0; ago < DaysKept; ago++, close = close.AddDays(-1))
        {
            var open = close.AddDays(-1);
            yield return new CalendarDay(zone, cutoff, close.AddMinutes(-1).ToString("yyyy-MM-dd"), ago,
                TimeZones.ToUtc(zone, open), ago == 0 ? now : TimeZones.ToUtc(zone, close));
        }
    }

    /// <summary>A dimension's or a value's [Display] — its label and tooltip — by the member it's declared on.</summary>
    internal static DisplayAttribute DisplayOf(object annotated)
    {
        return Displays.Value[annotated];
    }

    /// <summary>
    ///     The fields of <typeparamref name="TCell" /> as declared — their names (each its member's, in snake case) and
    ///     widths, in its order, filling it — or the type initializer throws: the declaration and the struct can't drift.
    /// </summary>
    private static T[] Checked<TCell, T>(T[] fields, Func<T, int> width) where TCell : unmanaged where T : ICellField
    {
        var members = typeof(TCell).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(f =>
                Regex.Replace(f.Name.TrimStart('_').Replace("k__BackingField", "").Trim('<', '>'), "(?<=[a-z])([A-Z])",
                    "_$1").ToLowerInvariant())
            .ToArray();
        var bytes = fields.Sum(width);
        if (bytes != Unsafe.SizeOf<TCell>() || !members.SequenceEqual(fields.Select(f => f.Name)))
            throw new InvalidOperationException(
                $"{typeof(TCell).Name}'s fields ({string.Join(", ", members)}: {Unsafe.SizeOf<TCell>()} bytes) " +
                $"aren't those declared ({string.Join(", ", fields.Select(f => f.Name))}: {bytes} bytes).");
        return fields;
    }

    /// <summary>
    ///     The router viewer's format of a pivot's cells, made of its shape: the labels as text, the figures at their
    ///     places.
    /// </summary>
    public static PayloadFormat FormatOf(PivotShape shape)
    {
        return new PayloadFormat
        {
            KeyLayout = new PayloadLayout(shape.Key.Bytes,
            [
                .. shape.Key.Fields.Select(f => new PayloadField(f.Name, f.Offset, FieldKind.Utf8) { Length = f.Bytes })
            ]),
            Layout = new PayloadLayout(shape.Row.Bytes,
            [
                .. shape.Row.Fields.Select(f => new PayloadField(f.Name, f.Offset, FieldKind.Int64)
                    { Scale = f.Scale ?? 0 })
            ])
        };
    }
}

/// <summary>A cell field's declaration: its name, as a pivot's shape names it.</summary>
public interface ICellField
{
    string Name { get; }
}

/// <summary>A cell's label: its name, the statement's column it's made from, and its width (UTF-8, zero-padded).</summary>
public sealed record CellLabel(string Name, string Column, int Bytes) : ICellField;

/// <summary>A cell's figure: its name, the statement's column it's made from, its places, its unit and what it is.</summary>
public sealed record CellFigure(string Name, string Column, int Places, string Unit, string About) : ICellField;

/// <summary>
///     A change to a pivot's cell, as a standing view drains it: its new figures, or its deletion (the view's answer no
///     longer has the cell).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct CellChange(PivotCell Row, byte Deleted);

/// <summary>A cell's change folded in: replaced by its new figures, or deleted (false).</summary>
public readonly struct ReplaceOrDelete : IRowFold<PivotCell, CellChange>
{
    public static bool Fold(ref PivotCell row, bool exists, in CellChange change)
    {
        if (change.Deleted != 0) return false;
        row = change.Row;
        return true;
    }
}