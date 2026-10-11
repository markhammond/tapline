using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using PivotTable.Contracts;

namespace PivotTable.Sample;

/// <summary>
///     What a page asks of the position viewer: rows grouped by one dimension or more (nested, in order), columns by none
///     or
///     more, the values each dimension is filtered to, the trading days — the last few, or a range of dates, as a zone and
///     a
///     cutoff make them — and the currency amounts are reported in. It becomes a statement
///     (<see cref="ToSql" />): canonical — the same criteria, however they were chosen, the same text — so pages asking
///     alike
///     share one topic.
/// </summary>
public sealed record PivotCriteria
{
    public const int MaxRows = 3, MaxColumns = 2;

    /// <summary>
    ///     The body of <c>trading_days(zone_name, cutoff_hour)</c>, a table function the provider registers (a macro: ChalkQL
    ///     inlines it, so its arguments are constants): a zone's trading days ending at a cutoff, the current one and the nine
    ///     before it — trading_day, days_ago, opens, closes — computed from <c>time_zones</c> as the page's day picker reckons
    ///     them (<see cref="Pivot.TradingDays" />). Now (<c>CURRENT_TIMESTAMP</c>: in a standing view, the engine's clock as
    ///     of
    ///     its epoch) in the zone's local time, by the offset in force (an as-of lookup); the current day's close, local: the
    ///     cutoff today, or tomorrow once it's passed; each day before it a day earlier, named by the date it ends on (the
    ///     minute before its close, its date cast to text: yyyy-MM-dd); its open
    ///     and close in UTC as <see cref="TimeZones.ToUtc" /> reckons them — by the span whose local start is the latest at or
    ///     before (an as-of lookup on <c>local_starts</c>), at its offset, or at the one before it while that one's local time
    ///     hasn't run out: a time that happens twice, the first; one that never happens, as if the clocks hadn't changed yet —
    ///     and the current day's close now. The zone is carried as a column: an as-of join keys on columns, and a macro's
    ///     parameter isn't one.
    /// </summary>
    public static readonly string TradingDaysSql = TradingDaysBody();

    public required string[] Rows { get; init; }
    public string[] Columns { get; init; } = [];

    /// <summary>Per dimension, the values kept; a dimension absent, or with all its values, isn't filtered.</summary>
    public Dictionary<string, string[]> Filters { get; init; } = [];

    /// <summary>The G10 currency amounts are reported in (<see cref="Pivot.Currencies" />).</summary>
    public string ReportingCurrency { get; init; } = "USD";

    /// <summary>
    ///     Where a trading day ends: a zone (<see cref="TimeZones.Names" />) and a cutoff, local time (
    ///     <see cref="Pivot.Cutoffs" />).
    /// </summary>
    public string Zone { get; init; } = "UTC";

    public string Cutoff { get; init; } = "00:00";

    /// <summary>
    ///     The trading days: the last n (the current one and those before it) — or, 0, the range <see cref="From" />–
    ///     <see cref="To" />.
    /// </summary>
    public int LastDays { get; init; } = 1;

    public string? From { get; init; }
    public string? To { get; init; }

    /// <summary>
    ///     The fields whose totals aren't shown (as a spreadsheet's pivot's "show totals", unticked): a field's totals are the
    ///     totals across it — the outermost row field's, the grand total row; an inner one's, each group's around it.
    /// </summary>
    public string[] NoTotals { get; init; } = [];

    /// <summary>
    ///     Whether the statement answers max gross (<see cref="Pivot.MaxGrossColumn" />): only when the page shows it — among
    ///     its
    ///     values, or a column's — as it costs aggregations of its own (about a sixth more than the statement without it).
    /// </summary>
    public bool MaxGross { get; init; }

    /// <summary>
    ///     Whether the statement answers mark price (<see cref="Pivot.MarkColumn" />): only when the page shows it — among its
    ///     values, or a column's — as it costs a grouping of its own (each instrument's mark once a day, then each cell's).
    /// </summary>
    public bool Mark { get; init; }

    /// <summary>
    ///     What its cells carry besides its values (Show as: <see cref="ShownValue.Extras" />, in order) — the provider's: the
    ///     statement answers running totals in Date, and, where a filter on a field a market's percentage runs across would
    ///     narrow it, the market's totals; the provider divides. Its values' entries (<c>values=volume.column</c>).
    /// </summary>
    public ShownValue[] Extras { get; init; } = [];

    /// <summary>What a market's percentage can be of: what adds up as a sum — Pos PnL its realized and unrealized.</summary>
    private static readonly (string Column, string Value)[] MarketFigures =
        [("notional", "notional"), ("realized", "pnl"), ("unrealized", "pnl"), ("balance", "balance"), ("volume", "volume"), ("trades", "trades")];

    /// <summary>One trading day, whichever.</summary>
    public bool OneDay => LastDays == 1 || (LastDays == 0 && From == To);

    /// <summary>The house's legs, side by side: each instrument by leg, risk and hedge across — the current trading day's.</summary>
    public static PivotCriteria Default => new() { Rows = ["instrument"], Columns = ["leg"] };

    /// <summary>
    ///     The criteria a page's address asks for (its query: <c>?rows=client&amp;columns=instrument…</c>) over
    ///     <paramref name="over" />: the fields in rows and columns, the filters, the last days or a range of dates, the
    ///     totals
    ///     hidden, the reporting currency — and max gross, if its values show it. What's malformed or unknown is left as it
    ///     was. The page reads its address so; so does a client asking for the same pivot by its address.
    /// </summary>
    public static PivotCriteria FromQuery(IReadOnlyDictionary<string, StringValues> query, PivotCriteria? over = null)
    {
        string[] Get(string key)
        {
            return query.TryGetValue(key, out var v)
                ? [.. v.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())]
                : [];
        }

        static bool IsField(string name)
        {
            return Pivot.Dimensions.Any(d => d.Name == name);
        }

        var c = over ?? Default;
        if (Get("rows") is { Length: > 0 } given && given.Where(IsField).Distinct().Take(MaxRows).ToArray() is
                { Length: > 0 } rows)
            c = c with
            {
                Rows = rows,
                Columns =
                [
                    .. Get("columns").Where(f => IsField(f) && !rows.Contains(f)).Distinct().Take(MaxColumns)
                ]
            };
        var filters = new Dictionary<string, string[]>(c.Filters);
        foreach (var d in Pivot.Dimensions.Where(d => d.Name != "date"))
            if (Get(d.Name) is { Length: > 0 } kept && d.Values.Where(kept.Contains).ToArray() is { Length: > 0 } known)
                filters[d.Name] = known;
        c = c with { Filters = filters };
        if (int.TryParse(Get("days").FirstOrDefault(), out var days) && Pivot.LastDays.Contains(days))
            c = c with { LastDays = days, From = null, To = null };
        else if (Get("from").FirstOrDefault() is { } from && Get("to").FirstOrDefault() is { } to
                                                          && DateOnly.TryParseExact(from, "yyyy-MM-dd", out _) &&
                                                          DateOnly.TryParseExact(to, "yyyy-MM-dd", out _))
            c = c with { LastDays = 0, From = from, To = to };
        if (Get("noTotals") is { Length: > 0 } hidden)
            c = c with { NoTotals = [.. hidden.Where(f => c.Rows.Contains(f) || c.Columns.Contains(f)).Distinct()] };
        if (Get("currency").FirstOrDefault() is { } currency && Pivot.Currencies.Contains(currency))
            c = c with { ReportingCurrency = currency };
        var shown = Get("values").Select(v => v.Split('.')[0]).ToArray(); // (a value shown as a percentage too: "gross.total")
        if (shown.Contains("gross")) c = c with { MaxGross = true };
        if (shown.Contains("mark")) c = c with { Mark = true };
        return c with { Extras = ShownValue.Extras(Get("values")) };
    }

    private static Dimension Find(string name)
    {
        return Pivot.Dimensions.First(d => d.Name == name);
    }

    /// <summary>
    ///     The values kept of a dimension: all of them, unless filtered (in the dimension's order; unknown values
    ///     dropped). Days: <see cref="LastDays" />.
    /// </summary>
    public string[] Kept(Dimension d)
    {
        return Filters.TryGetValue(d.Name, out var kept) ? [.. d.Values.Where(kept.Contains)] : d.Values;
    }

    /// <summary>
    ///     The statement, as ChalkQL takes it. Its <c>WITH</c> is the house's book, one row for each position on each day
    ///     asked for, as of the day's close, in two legs. Risk: the house's side of each client position — the opposite of
    ///     the client's — looked up in position_risk (the last row at or before the close; today's close is now), by
    ///     account (a perpetual's) and channel, the client's rows alone; its realized PnL the day's (the difference from the last row before the
    ///     open), in its instrument's quote (q, its rate at the close); its unrealized PnL against its average entry at its
    ///     instrument's mark then (m: the house's perpetual, marked at the index); its balance the cash its realized PnL has
    ///     paid so far. Hedge: each hedge position, the house's in a venue's perpetual, looked up in position_risk the same
    ///     way (its own rows: the house's, in a venue's instrument), at its
    ///     instrument's mark (m: its venue's, basis and all). The positions and marks are looked up by the position's own
    ///     columns, before anything is joined to them — so ChalkQL seeks each by its index (above a join, it may scan) — and
    ///     then named: its asset, logical instrument, venue and quote (instrument), its client (cpty), the quote's rate.
    ///     Amounts in the reporting currency — reporting in another than the US dollar, at its rate at the close (r). The
    ///     filters narrow what each as-of join reads, and so which positions find a row. Then one
    ///     <c>GROUP BY ROLLUP</c> of the rows by <c>ROLLUP</c> of the columns — or, some totals hidden, the
    ///     <c>GROUPING SETS</c>
    ///     they leave (<see cref="NoTotals" />): its rows' and columns' prefixes, every subtotal and the grand total, their
    ///     labels shorter (from <c>GROUPING</c>). A figure that doesn't add up across what a total
    ///     spans is null: a position across assets (an instrument is on one); a position, its notional, its unrealized PnL
    ///     and its balance across days
    ///     (realized PnL adds up anywhere). The filters' values are bound, as lists (<see cref="Bindings" />), not written in:
    ///     the same layout is the same statement — one prepared query — whatever they keep. ChalkQL is the sandbox — a
    ///     statement only ever reads.
    /// </summary>
    public string ToSql()
    {
        var rows = Rows.Select(Find).ToArray();
        var columns = Columns.Select(Find).ToArray();
        var currency = Pivot.Currencies.Contains(ReportingCurrency) ? ReportingCurrency : "USD";
        // The figures are DOUBLE, with no casts: prices and rates are (FLOAT, as the schema has them), and a quantity or an
        // amount (DECIMAL) by one is DOUBLE.
        var to = currency == "USD" ? "" : " / r.usd_rate";
        // A mark, asked for, is the instrument's own as its venue marks it at the close: a rate, its quote per unit of its
        // asset — not an amount, so not converted to the reporting currency, nor summed (each leg's Book writes it).
        // The days: trading_days' (the provider's function: TradingDaysSql), for the zone and cutoff asked for — the last n
        // of them (renumbered as each day ends, so the statement stays the same: its topic too), or a range of dates.
        var zone = TimeZones.Names.Contains(Zone) ? Zone : "UTC";
        var hour = Pivot.Cutoffs.Contains(Cutoff) ? int.Parse(Cutoff[..2]) : 0;
        var days = $"SELECT * FROM trading_days({Quote(zone)}, {hour}) WHERE " + (LastDays > 0
            ? $"days_ago < {Math.Min(LastDays, Pivot.DaysKept)}"
            : $"trading_day BETWEEN {Quote(From ?? "")} AND {Quote(To ?? "")}");
        // Running totals in Date, asked for (Date in the rows or columns): each date's figure since the first date's open —
        // its cumulative figure then, less as of that open (an as-of lookup more: the day's figure's own, anchored at the
        // window's start) — so no day's summed by a client, nor is the last a cent off the Total column.
        var runs = Rows.Contains("date") || Columns.Contains("date")
            ? Extras.Where(e => e.Form == ShowAs.RunningTotalInDate).Select(e => e.Value).Distinct().ToArray()
            : [];
        if (runs.Length > 0)
            days = $"SELECT d.trading_day, d.days_ago, d.opens, d.closes, s.opens AS since FROM trading_days({Quote(zone)}, {hour}) d " +
                   $"CROSS JOIN (SELECT opens FROM trading_days({Quote(zone)}, {hour}) WHERE " + (LastDays > 0
                       ? $"days_ago = {Math.Min(LastDays, Pivot.DaysKept) - 1}) s WHERE d.days_ago < {Math.Min(LastDays, Pivot.DaysKept)}"
                       : $"trading_day = {Quote(From ?? "")}) s WHERE d.trading_day BETWEEN {Quote(From ?? "")} AND {Quote(To ?? "")}");
        var reporting = currency == "USD"
            ? "" // (one currency's rates: a condition on the as-of side alone)
            : $"  ASOF JOIN conversion_rate r\n    MATCH_CONDITION r.ts <= d.closes ON r.symbol = {Quote(currency)}\n";

        // A filtered dimension's values are a list bound to it (@instrument …). Each leg's keys are filtered by what's on
        // its positions (a client's channel) and what the leg is constant in (a hedge's client is the house, its region
        // and channel none; a client trade's venue the house's: a leg filtered out has no keys, and looks nothing up);
        // its instruments by logical instrument and quote (instruments, the statement's), a hedge's by venue too (a
        // client trade's is the house's own perpetual), and its clients by region and name — joined after the lookups,
        // which read the tables they look up unnarrowed, each sought by its index; a direction once it is.
        var bound = Bindings();

        string? Condition(string dimension, string expression)
        {
            return bound.ContainsKey(dimension) ? $"{expression} IN @{dimension}" : null;
        }

        // The market's percentages asked for (Extras): each of a total ignoring the filters on the fields it runs across —
        // a column's, the row fields' (filter clients, and it's still all of them); a row's, the column fields'; the grand
        // total, both. Those filters move from where they prune (the keys, the instruments, the joined rows) into flags the
        // rows are kept by: the pivot's figures are its kept rows' (none on a row filtered out), the market's totals every
        // row its other filters keep, summed beside them — one pass, no join. A group with no row kept (a client filtered
        // out) is the provider's to leave out (kept: 0). None moved — no such filter — the market's are the pivot's own.
        var markets = Extras.Where(e => e.OfMarket && MarketFigures.Any(f => f.Value == e.Value)).ToArray();
        var rowsKept = markets.Any(e => e.Form is ShowAs.PercentOfColumn or ShowAs.PercentOfGrandTotal)
            ? Rows.Where(bound.ContainsKey).ToArray()
            : [];
        var columnsKept = markets.Any(e => e.Form is ShowAs.PercentOfRow or ShowAs.PercentOfGrandTotal)
            ? Columns.Where(bound.ContainsKey).ToArray()
            : [];
        var keeping = rowsKept.Length + columnsKept.Length > 0;
        // Each market total asked: its suffix, the filters it ignores, and the book's columns it sums (each value's asking).
        var sets = new List<(string Name, bool IgnoreRows, bool IgnoreColumns, string[] Columns)>();
        foreach (var (form, name, ignoreRows, ignoreColumns) in new[]
                 {
                     (ShowAs.PercentOfColumn, "_all_rows", true, false), (ShowAs.PercentOfRow, "_all_columns", false, true),
                     (ShowAs.PercentOfGrandTotal, "_all", true, true)
                 })
            if ((ignoreRows && rowsKept.Length > 0) || (ignoreColumns && columnsKept.Length > 0))
                if (MarketFigures.Where(f => markets.Any(e => e.Value == f.Value && e.Form == form)).Select(f => f.Column).Distinct().ToArray()
                    is { Length: > 0 } summed)
                    sets.Add((name, ignoreRows, ignoreColumns, summed));

        // The levels: every prefix of the rows by every prefix of the columns — a prefix shorter than the fields is the totals
        // across the field it stops at, made if that field's are shown.
        var levels = new List<(int Rows, int Columns)>();
        for (var i = rows.Length; i >= 0; i--)
        for (var j = columns.Length; j >= 0; j--)
            if (!(i < rows.Length && NoTotals.Contains(rows[i].Name)) &&
                !(j < columns.Length && NoTotals.Contains(columns[j].Name)))
                levels.Add((i, j));
        // A figure is given where it adds up: one asset (a position: units of its underlying — however many instruments
        // on it), one day (a position, its notional, unrealized PnL, balance, max gross) — fixed by every level's fields
        // (its underlier, or an instrument: on one), or by the days asked for, or checked by each group (however many
        // the filter keeps: its values are bound, not the statement's).
        var asset = levels.All(l => rows[..l.Rows].Concat(columns[..l.Columns]).Any(g => g.Name is "underlier" or "instrument"));
        var day = OneDay || levels.All(l => rows[..l.Rows].Concat(columns[..l.Columns]).Any(g => g.Name == "date"));

        string? Placed(string dimension, string expression)
        {
            return rowsKept.Contains(dimension) || columnsKept.Contains(dimension) ? null : Condition(dimension, expression);
        }

        // A leg's flag for some of the moved filters: each dimension's, as the leg has it (none moved: none).
        static string? Kept(Dictionary<string, string> at, IEnumerable<string> dimensions)
        {
            return dimensions.Select(d => $"{at[d]} IN @{d}").ToArray() is { Length: > 0 } kept ? string.Join(" AND ", kept) : null;
        }

        static string? Both(string? a, string? b)
        {
            return a is null ? b : b is null ? a : $"{a} AND {b}";
        }

        static string Figure(string expression, string column, string? keep)
        {
            return keep is null ? $"{expression} AS {column}" : $"CASE WHEN {keep} THEN {expression} END AS {column}";
        }

        // A leg's figures — kept, filters moved (none on a row filtered out) — then its mark (asked for: a kept row's, and
        // its instrument, which the mark's one-instrument test counts), its running totals' (cumulative since the first
        // open), each market total's figures (every row the other filters keep), and whether the row's kept.
        void Book(StringBuilder leg, (string Expression, string Column)[] figures, (string Expression, string Column)[] since,
            string markExpression, string? keepRows, string? keepColumns)
        {
            var keep = Both(keepRows, keepColumns);
            for (var f = 0; f < figures.Length; f++)
                leg.Append("         ").Append(Figure(figures[f].Expression, figures[f].Column, keep)).Append(f < figures.Length - 1 ? ",\n" : "");
            if (Mark)
            {
                leg.Append(",\n         ").Append(Figure(markExpression, "mark", keep));
                if (keep is not null) leg.Append(",\n         ").Append(Figure("i.logical_name", "mark_instrument", keep));
            }

            foreach (var (expression, column) in since.Where(s => runs.Contains(s.Column[..s.Column.IndexOf('_')])))
                leg.Append(",\n         ").Append(Figure(expression, column, keep));
            foreach (var (name, ignoreRows, ignoreColumns, summed) in sets)
            foreach (var (expression, column) in figures.Where(f => summed.Contains(f.Column)))
                leg.Append(",\n         ").Append(Figure(expression, column + name, Both(ignoreRows ? null : keepRows, ignoreColumns ? null : keepColumns)));
            if (keep is not null) leg.Append(",\n         CASE WHEN ").Append(keep).Append(" THEN 1 ELSE 0 END AS keep");
            leg.Append('\n');
        }

        static string Where(params string?[] conditions)
        {
            return conditions.OfType<string>().ToArray() is { Length: > 0 } kept
                ? $" WHERE {string.Join(" AND ", kept)}"
                : "";
        }

        static string And(string? condition)
        {
            return condition is null ? "" : $"\n    AND {condition}";
        }

        static string Text(string value)
        {
            return $"CAST({Quote(value)} AS VARCHAR(32))";
        } // (a constant's type: VARCHAR — no CHAR padding across the legs, nor in a list)

        var instruments = "SELECT instrument_id, logical_name, venue, underlying, quote FROM instrument"
                          + Where(Placed("underlier", "underlying"), Placed("instrument", "logical_name"), Placed("counter", "quote"));

        var legs = new List<string>();
        {
            var direction =
                "CASE WHEN c.amt_signed < 0 THEN 'long' WHEN c.amt_signed > 0 THEN 'short' ELSE 'flat' END"; // (the house's: the client's opposite)
            var at = new Dictionary<string, string>
            {
                ["leg"] = Text("risk"), ["venue"] = Text(Pivot.House), ["channel"] = "k.channel", ["region"] = "x.region",
                ["client"] = "x.cpty_name", ["direction"] = direction, ["underlier"] = "i.underlying",
                ["instrument"] = "i.logical_name", ["counter"] = "i.quote"
            };
            string? keepRows = Kept(at, rowsKept), keepColumns = Kept(at, columnsKept);
            var leg = new StringBuilder();
            leg.Append("  SELECT d.trading_day, ").Append(Text("risk")).Append(" AS leg, i.logical_name AS instrument, i.underlying")
                .Append(", x.region, x.cpty_name AS client, k.channel, ")
                .Append(Text(Pivot.House)).Append(" AS venue, i.quote AS counter_symbol, c.instrument_id,\n");
            leg.Append("         ").Append(direction).Append(" AS direction,\n");
            Book(leg,
            [
                ("-c.amt_signed", "qty"), ($"-c.amt_signed * m.price * q.usd_rate{to}", "notional"),
                ($"-(c.cumulative_realized_pnl - COALESCE(o.cumulative_realized_pnl, 0)) * q.usd_rate{to}", "realized"),
                ($"-c.amt_signed * (m.price - c.avg_prc) * q.usd_rate{to}", "unrealized"),
                ($"-c.cumulative_realized_pnl * q.usd_rate{to}", "balance"),
                ($"(c.cumulative_volume - COALESCE(o.cumulative_volume, 0)){to}", "volume"), // (the day's trading: its volume, in US dollars, and its trades)
                ("c.trade_count - COALESCE(o.trade_count, 0)", "trades")
            ], [($"(c.cumulative_volume - COALESCE(w.cumulative_volume, 0)){to}", "volume_since"), ("c.trade_count - COALESCE(w.trade_count, 0)", "trades_since")],
                "m.price", keepRows, keepColumns);
            leg.Append("  FROM days d\n");
            // Each client position there's been — by account (a perpetual's) and channel; its client and instrument, its
            // rows' (an account's: one of each — grouped by them too, the planner would reckon on ten times the keys). The
            // house's own rows (its side of them, its hedges) aren't a client's.
            string?[] constants = [Placed("leg", Text("risk")), Placed("venue", Text(Pivot.House))];
            leg.Append("  CROSS JOIN (SELECT account_id, channel FROM position_risk")
                .Append(Where([.. constants, Placed("channel", "channel"), $"cpty_id <> {Brokerage.HouseId}"]))
                .Append(" GROUP BY account_id, channel) k\n");
            leg.Append(
                "  ASOF JOIN position_risk c\n    MATCH_CONDITION c.update_time <= d.closes\n    ON c.account_id = k.account_id AND c.channel = k.channel\n");
            leg.Append(
                "  LEFT ASOF JOIN position_risk o\n    MATCH_CONDITION o.update_time < d.opens\n    ON o.account_id = k.account_id AND o.channel = k.channel\n");
            if (runs.Length > 0)
                leg.Append(
                    "  LEFT ASOF JOIN position_risk w\n    MATCH_CONDITION w.update_time < d.since\n    ON w.account_id = k.account_id AND w.channel = k.channel\n");
            leg.Append(
                "  ASOF JOIN marks m\n    MATCH_CONDITION m.ts <= d.closes ON m.instrument_id = c.instrument_id\n");
            leg.Append(
                "  JOIN instruments i ON i.instrument_id = c.instrument_id\n  JOIN cpty x ON x.cpty_id = c.cpty_id\n");
            leg.Append("  ASOF JOIN conversion_rate q\n    MATCH_CONDITION q.ts <= d.closes ON q.symbol = i.quote\n");
            leg.Append(reporting);
            leg.Append(
                    $"  WHERE NOT (c.amt_signed = 0 AND c.cumulative_realized_pnl = 0 AND c.trade_count = COALESCE({(runs.Length > 0 ? "w" : "o")}.trade_count, 0))") // (nothing held, nothing ever realized, nothing traded that day — or, running totals asked, since the first open: still in them)
                .Append(And(Placed("region", "x.region"))).Append(And(Placed("client", "x.cpty_name")))
                .Append(And(Placed("direction", direction)));
            legs.Add(leg.ToString());
        }
        {
            var direction = "CASE WHEN h.amt_signed > 0 THEN 'long' WHEN h.amt_signed < 0 THEN 'short' ELSE 'flat' END";
            var at = new Dictionary<string, string>
            {
                ["leg"] = Text("hedge"), ["venue"] = "i.venue", ["channel"] = Text(Pivot.None), ["region"] = Text(Pivot.None),
                ["client"] = Text(Pivot.House), ["direction"] = direction, ["underlier"] = "i.underlying",
                ["instrument"] = "i.logical_name", ["counter"] = "i.quote"
            };
            string? keepRows = Kept(at, rowsKept), keepColumns = Kept(at, columnsKept);
            var leg = new StringBuilder();
            leg.Append("  SELECT d.trading_day, ").Append(Text("hedge"))
                .Append(" AS leg, i.logical_name AS instrument, i.underlying, ")
                .Append(Text(Pivot.None))
                .Append(" AS region, ").Append(Text(Pivot.House)).Append(" AS client, ")
                .Append(Text(Pivot.None)).Append(" AS channel, i.venue, i.quote AS counter_symbol, k.instrument_id,\n");
            leg.Append("         ").Append(direction).Append(" AS direction,\n");
            Book(leg,
            [
                ("h.amt_signed", "qty"), ($"h.amt_signed * m.price * q.usd_rate{to}", "notional"),
                ($"(h.cumulative_realized_pnl - COALESCE(o.cumulative_realized_pnl, 0)) * q.usd_rate{to}", "realized"),
                ($"h.amt_signed * (m.price - h.avg_prc) * q.usd_rate{to}", "unrealized"),
                ($"h.cumulative_realized_pnl * q.usd_rate{to}", "balance"),
                ($"(h.cumulative_volume - COALESCE(o.cumulative_volume, 0)){to}", "volume"),
                ("h.trade_count - COALESCE(o.trade_count, 0)", "trades")
            ], [($"(h.cumulative_volume - COALESCE(w.cumulative_volume, 0)){to}", "volume_since"), ("h.trade_count - COALESCE(w.trade_count, 0)", "trades_since")],
                "m.price", keepRows, keepColumns);
            leg.Append("  FROM days d\n");
            // Each hedge position there's been — the house's, in a venue's perpetual: by account (one an instrument) and
            // channel (none), as a client's is looked up; its instrument, its rows'.
            string?[] constants =
            [
                Placed("leg", Text("hedge")), Placed("region", Text(Pivot.None)),
                Placed("client", Text(Pivot.House)), Placed("channel", Text(Pivot.None)),
                $"cpty_id = {Brokerage.HouseId}",
                $"instrument_id IN (SELECT instrument_id FROM instruments WHERE venue <> {Text(Pivot.House)})"
            ];
            leg.Append("  CROSS JOIN (SELECT account_id, channel, instrument_id FROM position_risk").Append(Where(constants))
                .Append(" GROUP BY account_id, channel, instrument_id) k\n");
            leg.Append(
                "  ASOF JOIN position_risk h\n    MATCH_CONDITION h.update_time <= d.closes\n    ON h.account_id = k.account_id AND h.channel = k.channel\n");
            leg.Append(
                "  LEFT ASOF JOIN position_risk o\n    MATCH_CONDITION o.update_time < d.opens\n    ON o.account_id = k.account_id AND o.channel = k.channel\n");
            if (runs.Length > 0)
                leg.Append(
                    "  LEFT ASOF JOIN position_risk w\n    MATCH_CONDITION w.update_time < d.since\n    ON w.account_id = k.account_id AND w.channel = k.channel\n");
            leg.Append(
                "  ASOF JOIN marks m\n    MATCH_CONDITION m.ts <= d.closes ON m.instrument_id = k.instrument_id\n");
            leg.Append("  JOIN instruments i ON i.instrument_id = k.instrument_id\n");
            leg.Append("  ASOF JOIN conversion_rate q\n    MATCH_CONDITION q.ts <= d.closes ON q.symbol = i.quote\n");
            leg.Append(reporting);
            leg.Append(
                    $"  WHERE NOT (h.amt_signed = 0 AND h.cumulative_realized_pnl = 0 AND h.trade_count = COALESCE({(runs.Length > 0 ? "w" : "o")}.trade_count, 0))")
                .Append(And(Placed("venue", "i.venue"))).Append(And(Placed("direction", direction)));
            legs.Add(leg.ToString());
        }
        var sql = new StringBuilder();
        sql.Append("WITH days AS (").Append(days).Append("),\n");
        sql.Append("instruments AS (").Append(instruments).Append("),\n");
        sql.Append("p AS (\n").Append(string.Join("\n  UNION ALL\n", legs)).Append("\n),\n");

        string When(bool fixedHere, string condition, string figure)
        {
            return fixedHere ? figure : $"CASE WHEN {condition} THEN {figure} END";
        }

        // (Filters moved, a group's figures are its kept rows': one day, one asset of those — every row's for the market's
        // totals. The underlier itself is every row's: a field to group by.)
        string dayCondition = keeping
                ? "MIN(trading_day) FILTER (WHERE keep > 0) = MAX(trading_day) FILTER (WHERE keep > 0)"
                : "MIN(trading_day) = MAX(trading_day)",
            marketDayCondition = "MIN(trading_day) = MAX(trading_day)",
            assetCondition = keeping
                ? "MIN(underlying) FILTER (WHERE keep > 0) = MAX(underlying) FILTER (WHERE keep > 0)"
                : "MIN(underlying) = MAX(underlying)",
            lots = keeping // (the assets a grouping's kept rows span: for the cells over it)
                ? "MIN(underlying) FILTER (WHERE keep > 0) AS lo, MAX(underlying) FILTER (WHERE keep > 0) AS hi"
                : "MIN(underlying) AS lo, MAX(underlying) AS hi";
        var quantityCondition = asset ? dayCondition : day ? assetCondition : $"{assetCondition} AND {dayCondition}";

        // What the cells carry besides, summed as their figures are: each running total — within a date, since the first
        // open; across the dates, the window's own figure, its last — each market total (none across days, as its
        // figure is), and how many of a group's rows are kept (none: the provider leaves it out).
        string ExtraTotals()
        {
            var extra = new StringBuilder();
            foreach (var v in runs)
                extra.Append(",\n       ").Append(day ? $"SUM({v}_since)" : $"CASE WHEN {dayCondition} THEN SUM({v}_since) ELSE SUM({v}) END")
                    .Append($" AS {v}_running");
            foreach (var (name, _, _, summed) in sets)
            foreach (var column in summed)
                extra.Append(",\n       ").Append(column is "notional" or "unrealized" or "balance"
                    ? When(day, marketDayCondition, $"SUM({column}{name})")
                    : $"SUM({column}{name})").Append($" AS {column}{name}");
            if (keeping) extra.Append(",\n       SUM(keep) AS kept");
            return extra.ToString();
        }

        // The book's extra columns a level's rows carry into a grouping, and each grouping's sums of them.
        IEnumerable<string> ExtraColumns()
        {
            foreach (var v in runs) yield return $"{v}_since";
            foreach (var (name, _, _, summed) in sets)
            foreach (var column in summed)
                yield return column + name;
            if (keeping) yield return "keep";
        }

        string ExtraSums()
        {
            return string.Concat(ExtraColumns().Select(c => $", SUM({c}) AS {c}"));
        }

        string ExtraNames()
        {
            return string.Concat(runs.Select(v => $", {v}_running")
                .Concat(sets.SelectMany(s => s.Columns.Select(c => $", {c}{s.Name}")))
                .Concat(keeping ? [", kept"] : []));
        }

        // The mark's one-instrument test, filters moved: the kept rows' instruments alone.
        var markInstrument = keeping ? "mark_instrument" : "instrument";

        // Each row of the book once for each level, labelled as that level's cell (its label: the level's prefix).
        static string LevelLabels(string level, int[] lengths, Dimension[] fields)
        {
            return lengths.Distinct().ToArray() is [var only]
                ? Label(fields[..only])
                : "CASE " + level +
                  string.Concat(lengths.Distinct().Select(n => $" WHEN {n} THEN {Label(fields[..n])}")) + " END";
        }

        var levelLabels =
            $"{LevelLabels("l.r", [.. levels.Select(l => l.Rows)], rows)} AS row_key, {LevelLabels("l.c", [.. levels.Select(l => l.Columns)], columns)} AS column_key";
        var levelValues = string.Join(", ", levels.Select(l => $"({l.Rows}, {l.Columns})"));

        // The cells: the book's figures summed at every level.
        var cells = new StringBuilder();
        cells.Append("SELECT row_key, column_key,\n");
        cells.Append("       ").Append(When(asset && day, quantityCondition, "SUM(qty)")).Append(" AS quantity,\n");
        cells.Append("       ").Append(When(day, dayCondition, "SUM(notional)")).Append(" AS notional,\n");
        cells.Append("       SUM(realized) AS realized_pnl,\n");
        cells.Append("       ").Append(When(day, dayCondition, "SUM(unrealized)")).Append(" AS unrealized_pnl,\n");
        cells.Append("       ").Append(When(day, dayCondition, "SUM(balance)")).Append(" AS balance,\n");
        cells.Append(
            "       SUM(volume) AS volume,\n"); // (what was traded adds up anywhere: across assets, across days)
        cells.Append("       SUM(trades) AS trades").Append(ExtraTotals()).Append('\n');

        // ROLLUP: each level a grouping of the rows' prefix and the columns' — every prefix of each (no totals hidden),
        // or the levels listed (GROUPING SETS). A total's label is the prefix it's grouped by: GROUPING says which fields
        // a row leaves out (1), and only prefixes occur, so the first field left out says how long the prefix is.
        static string Labels(int[] lengths, Dimension[] fields)
        {
            var present = lengths.Distinct().Order().ToArray();
            return present is [var only]
                ? Label(fields[..only])
                : "CASE" + string.Concat(present[..^1]
                      .Select(n => $" WHEN GROUPING({fields[n].Column}) = 1 THEN {Label(fields[..n])}")) +
                  $" ELSE {Label(fields[..present[^1]])} END";
        }

        cells.Remove(0, "SELECT row_key, column_key,\n".Length)
            .Insert(0,
                $"SELECT {Labels([.. levels.Select(l => l.Rows)], rows)} AS row_key, {Labels([.. levels.Select(l => l.Columns)], columns)} AS column_key,\n");
        cells.Append("FROM p\n");

        static string Fields(Dimension[] fields)
        {
            return string.Join(", ", fields.Select(f => f.Column));
        }

        if (levels.Count == 1) cells.Append("GROUP BY ").Append(Fields([.. rows, .. columns]));
        else if (NoTotals.Length == 0 || !rows.Concat(columns).Any(d => NoTotals.Contains(d.Name)))
            cells.Append("GROUP BY ROLLUP(").Append(Fields(rows)).Append(')')
                .Append(columns.Length > 0 ? $", ROLLUP({Fields(columns)})" : "");
        else
            cells.Append("GROUP BY GROUPING SETS (").Append(string.Join(", ",
                levels.Select(l => $"({Fields([.. rows[..l.Rows], .. columns[..l.Columns]])})"))).Append(')');
        if (!MaxGross && !Mark) // (not shown: not asked for, nor paid for)
        {
            sql.Remove(sql.Length - 2, 2).Append('\n').Append(cells);
            return sql.ToString();
        }

        var lotCondition = "MIN(lo) = MAX(hi)"; // (one asset: the assets each group spans)
        var oneLotCondition = asset ? dayCondition : day ? lotCondition : $"{lotCondition} AND {dayCondition}";

        // The figures summed again from a group of them (by client, or by instrument and day): its sums summed.
        void Figures()
        {
            sql.Append("       ").Append(When(asset && day, oneLotCondition, "SUM(qty)")).Append(" AS quantity,\n");
            sql.Append("       ").Append(When(day, dayCondition, "SUM(notional)")).Append(" AS notional,\n");
            sql.Append("       SUM(realized) AS realized_pnl,\n");
            sql.Append("       ").Append(When(day, dayCondition, "SUM(unrealized)")).Append(" AS unrealized_pnl,\n");
            sql.Append("       ").Append(When(day, dayCondition, "SUM(balance)")).Append(" AS balance,\n");
            sql.Append("       SUM(volume) AS volume,\n");
            sql.Append("       SUM(trades) AS trades");
        }

        // The mark price: each listing's mark once a day — grouped by instrument and day first, as the book has a row for
        // each of its positions in it — averaged over those the cell spans; where they're of one logical instrument (one
        // pair: its rate's units), otherwise none. A sum and a count, which a maintained view keeps (a median it doesn't).
        var markPrice = When(asset, "MIN(instrument) = MAX(instrument)", "SUM(mark) / COUNT(mark)");
        if (!MaxGross)
        {
            // Mark price with the cells in one pass: the book once for each level, labelled as its cell (as max gross does,
            // below), grouped twice — by instrument and day (its mark once), by cell (the sums, and the marks averaged).
            sql.Append("u AS (\n");
            sql.Append("SELECT r, c, row_key, column_key, trading_day, instrument_id,\n");
            sql.Append(
                "       SUM(qty) AS qty, SUM(notional) AS notional, SUM(realized) AS realized, SUM(unrealized) AS unrealized, SUM(balance) AS balance,\n");
            sql.Append(
                    $"       SUM(volume) AS volume, SUM(trades) AS trades, {lots}, MIN({markInstrument}) AS instrument, MIN(mark) AS mark")
                .Append(ExtraSums()).Append('\n');
            sql.Append("FROM (SELECT l.r, l.c, ").Append(levelLabels).Append(",\n");
            sql.Append(
                    "             p.trading_day, p.instrument_id, p.instrument, p.underlying, p.qty, p.notional, p.realized, p.unrealized, p.balance, p.volume, p.trades, p.mark")
                .Append(keeping ? ", p.mark_instrument" : "").Append(string.Concat(ExtraColumns().Select(c => $", p.{c}"))).Append('\n');
            sql.Append("      FROM p CROSS JOIN (VALUES ").Append(levelValues).Append(") AS l (r, c)) x\n");
            sql.Append("GROUP BY r, c, row_key, column_key, trading_day, instrument_id\n)\n");
            sql.Append("SELECT row_key, column_key,\n");
            Figures();
            sql.Append(",\n       ").Append(markPrice).Append(" AS mark_price").Append(ExtraTotals()).Append('\n');
            sql.Append("FROM u\n");
            sql.Append("GROUP BY r, c, row_key, column_key");
            return sql.ToString();
        }

        // Max gross with the cells in one pass: the book once for each level, labelled as its cell (the levels are the
        // ROLLUP's grouping sets), grouped three times — by client and instrument (its risk netted), by client (its two
        // sides), by cell (the sums, and the larger side summed) — so p is read once. (Two WITH tables over p, the cells'
        // and max gross's, would each need columns of their own, and ChalkQL plans a WITH table once for each such use.)
        sql.Append("t AS (\n");
        sql.Append("SELECT r, c, row_key, column_key, trading_day, client, instrument_id,\n");
        sql.Append(
            "       SUM(qty) AS qty, SUM(notional) AS notional, SUM(realized) AS realized, SUM(unrealized) AS unrealized, SUM(balance) AS balance,\n");
        sql.Append(
            $"       SUM(volume) AS volume, SUM(trades) AS trades, {lots},\n");
        sql.Append("       SUM(CASE WHEN leg = ").Append(Text("risk"))
            .Append(" THEN notional END) AS net, SUM(CASE WHEN leg = ").Append(Text("risk"))
            .Append(keeping ? " THEN keep ELSE 0 END) AS risk_rows" : " THEN 1 ELSE 0 END) AS risk_rows")
            .Append(Mark ? $", MIN({markInstrument}) AS instrument, MIN(mark) AS mark" : "").Append(ExtraSums()).Append('\n');
        sql.Append("FROM (SELECT l.r, l.c, ").Append(levelLabels).Append(",\n");
        sql.Append(
                "             p.trading_day, p.client, p.instrument_id, p.leg, p.underlying, p.qty, p.notional, p.realized, p.unrealized, p.balance, p.volume, p.trades")
            .Append(Mark ? ", p.instrument, p.mark" : "").Append(Mark && keeping ? ", p.mark_instrument" : "")
            .Append(string.Concat(ExtraColumns().Select(c => $", p.{c}"))).Append('\n');
        sql.Append("      FROM p CROSS JOIN (VALUES ").Append(levelValues).Append(") AS l (r, c)) x\n");
        sql.Append("GROUP BY r, c, row_key, column_key, trading_day, client, instrument_id\n),\n");
        sql.Append("s AS (\n");
        sql.Append("SELECT r, c, row_key, column_key, trading_day, client,\n");
        sql.Append(
            "       SUM(qty) AS qty, SUM(notional) AS notional, SUM(realized) AS realized, SUM(unrealized) AS unrealized, SUM(balance) AS balance,\n");
        sql.Append("       SUM(volume) AS volume, SUM(trades) AS trades, MIN(lo) AS lo, MAX(hi) AS hi,\n");
        sql.Append(
                "       SUM(CASE WHEN net > 0 THEN net ELSE 0 END) AS long_side, SUM(CASE WHEN net < 0 THEN -net ELSE 0 END) AS short_side, SUM(risk_rows) AS risk_rows")
            .Append(ExtraSums()).Append('\n');
        sql.Append("FROM t\n");
        sql.Append("GROUP BY r, c, row_key, column_key, trading_day, client\n)").Append(Mark ? ",\n" : "\n");
        if (Mark) sql.Append("cells AS (\n");
        sql.Append(Mark ? "SELECT r, c, row_key, column_key,\n" : "SELECT row_key, column_key,\n");
        Figures();
        sql.Append(",\n       CASE WHEN SUM(risk_rows) > 0").Append(day ? "" : $" AND {dayCondition}").Append(
            " THEN SUM(CASE WHEN long_side > short_side THEN long_side ELSE short_side END) END AS max_gross").Append(ExtraTotals()).Append('\n'); // (no client in the cell: none)
        sql.Append("FROM s\n");
        sql.Append("GROUP BY r, c, row_key, column_key");
        if (!Mark) return sql.ToString();
        // And the mark price beside: t's groups by instrument and day (its mark once — t has one for each client), by cell
        // (averaged), joined to the cells by their level and labels.
        sql.Append("\n),\nk AS (\n");
        sql.Append("SELECT r, c, row_key, column_key, ").Append(markPrice).Append(" AS mark_price\n");
        sql.Append(
            "FROM (SELECT r, c, row_key, column_key, trading_day, instrument_id, MIN(instrument) AS instrument, MIN(mark) AS mark\n");
        sql.Append("      FROM t\n");
        sql.Append("      GROUP BY r, c, row_key, column_key, trading_day, instrument_id) u\n");
        sql.Append("GROUP BY r, c, row_key, column_key\n)\n");
        sql.Append(
            "SELECT cells.row_key, cells.column_key, quantity, notional, realized_pnl, unrealized_pnl, balance, volume, trades, max_gross, mark_price")
            .Append(ExtraNames()).Append('\n');
        sql.Append(
            "FROM cells JOIN k ON k.r = cells.r AND k.c = cells.c AND k.row_key = cells.row_key AND k.column_key = cells.column_key");
        return sql.ToString();
    }

    private static string TradingDaysBody()
    {
        const string cut = "cutoff_hour * INTERVAL '1' HOUR",
            midnight = "CAST(CAST(t.local_now AS DATE) AS TIMESTAMP(9))";

        // A local time in UTC, by the span looked up at it (alias): its offset, or the previous span's in an overlap.
        static string Utc(string local, string span)
        {
            return
                $"{local} - CASE WHEN {local} < {span}.starts + {span}.previous_offset_minutes * INTERVAL '1' MINUTE THEN {span}.previous_offset_minutes ELSE {span}.utc_offset_minutes END * INTERVAL '1' MINUTE";
        }

        var sql = new StringBuilder();
        sql.Append("SELECT x.trading_day, x.days_ago,\n");
        sql.Append($"       {Utc("x.open_local", "zo")} AS opens,\n");
        sql.Append($"       CASE WHEN x.days_ago = 0 THEN x.now ELSE {Utc("x.close_local", "zc")} END AS closes\n");
        sql.Append("FROM (SELECT n.now, n.zone, k.days_ago,\n");
        sql.Append("             n.close_local - k.days_ago * INTERVAL '1' DAY AS close_local,\n");
        sql.Append("             n.close_local - (k.days_ago + 1) * INTERVAL '1' DAY AS open_local,\n");
        sql.Append(
            "             CAST(CAST(n.close_local - k.days_ago * INTERVAL '1' DAY - INTERVAL '1' MINUTE AS DATE) AS VARCHAR) AS trading_day\n"); // (named by the minute before its close: its date, as text)
        sql.Append(
            $"      FROM (SELECT t.now, t.zone, CASE WHEN t.local_now < {midnight} + {cut} THEN {midnight} + {cut}\n");
        sql.Append(
            $"                                       ELSE {midnight} + INTERVAL '1' DAY + {cut} END AS close_local\n");
        sql.Append(
            "            FROM (SELECT s.now, s.zone, s.now + z.utc_offset_minutes * INTERVAL '1' MINUTE AS local_now\n");
        sql.Append("                  FROM (SELECT CURRENT_TIMESTAMP(9) AS now, zone_name AS zone) s\n");
        sql.Append(
            "                  ASOF JOIN time_zones z MATCH_CONDITION z.starts <= s.now ON z.zone = s.zone) t) n\n");
        sql.Append(
            $"      CROSS JOIN (VALUES {string.Join(", ", Enumerable.Range(0, Pivot.DaysKept).Select(k => $"({k})"))}) AS k (days_ago)) x\n");
        sql.Append("ASOF JOIN time_zones zc MATCH_CONDITION zc.local_starts <= x.close_local ON zc.zone = x.zone\n");
        sql.Append("ASOF JOIN time_zones zo MATCH_CONDITION zo.local_starts <= x.open_local ON zo.zone = x.zone");
        return sql.ToString();
    }

    /// <summary>A cell's label: its levels' values joined by " · " — a total's, its prefix; the grand total's, empty.</summary>
    private static string Label(Dimension[] levels)
    {
        return levels.Length == 0 ? "''" : string.Join(" || ' · ' || ", levels.Select(l => l.Column));
    }

    private static string Quote(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }

    /// <summary>
    ///     The values bound to the statement: each filtered dimension's kept values, as a list named for it (@instrument,
    ///     @client …), by name — the statement names them, so the same layout is the same statement whatever's kept. The
    ///     days aren't among them: they're the statement's window.
    /// </summary>
    public SortedDictionary<string, string[]> Bindings()
    {
        return new SortedDictionary<string, string[]>(
            Pivot.Dimensions.Where(d => d.Name != "date" && Kept(d).Length != d.Values.Length)
                .ToDictionary(d => d.Name, Kept), StringComparer.Ordinal);
    }

    /// <summary>
    ///     A subscription's parameters, as JSON — what the page sends the provider: the statement, the values it binds (each a
    ///     list of text, by name), and — if the page awaits it — a rendezvous id (hex): handed back once the page has the
    ///     pivot's first answer — and whether its view is to be maintained incrementally (experimental), not recomputed, and
    ///     if so with low jitter (the default: a new plan built beside the one it runs, not computed at an update) or not.
    ///     And, a client holding a token, its shard: served by that provider's engine, or refused.
    /// </summary>
    public static string Parameters(string sql, IReadOnlyDictionary<string, string[]> bindings,
        string? rendezvous = null, bool incremental = false, bool lowJitter = true, IReadOnlyList<string>? extras = null,
        Guid? shard = null)
    {
        var parameters = new Dictionary<string, object?> { ["sql"] = sql, ["bindings"] = bindings };
        if (shard is { } s) parameters["shard"] = s.ToString(); // (a token's: served by that provider's engine alone)
        if (extras is { Count: > 0 }) parameters["extras"] = extras; // (what its cells carry besides: Show as — said only when any)
        if (rendezvous is not null) parameters["rendezvous"] = rendezvous;
        if (incremental) parameters["incremental"] = true;
        if (incremental && !lowJitter)
            parameters["lowJitter"] = false; // (said only when off: the default's parameters as they were)
        return JsonSerializer.Serialize(parameters);
    }
}