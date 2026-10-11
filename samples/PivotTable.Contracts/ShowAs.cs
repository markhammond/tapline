namespace PivotTable.Contracts;

/// <summary>
///     How a value shows — as a spreadsheet pivot's "Show as" — beside its own figure, or instead of it: a percentage of a
///     total, or a running total in Date (Excel's "Running Total In"). A percentage is of its row's total (the Total
///     column's figure on its row), its column's (the Total row's in its column) or the grand total — of the market, the
///     total ignoring the filters on the fields it runs across (filter two clients, and each is still a share of all of
///     them: Power BI's ALL), or of what's shown, the filtered pivot's own total (as a spreadsheet's: the two add up to
///     100%). Each is the data provider's: a pivot asks for it, and its cells carry it.
/// </summary>
public enum ShowAs
{
    Value,
    PercentOfRow,
    PercentOfColumn,
    PercentOfGrandTotal,
    PercentOfRowShown,
    PercentOfColumnShown,
    PercentOfGrandTotalShown,
    RunningTotalInDate
}

/// <summary>
///     A value and how it shows: an entry of a page's address, as its Values have it — <c>volume</c>,
///     <c>volume.column</c> (% of column, the market's), <c>volume.columnShown</c> (of what's shown), <c>pnl.total</c>,
///     <c>volume.running</c>. A mark price (a rate, averaged) shows as itself alone; a percentage of the market is of what
///     the market has a total of (<see cref="HasMarket" />); a running total is of what adds up across dates: volume and
///     trades.
/// </summary>
public readonly record struct ShownValue(string Value, ShowAs Form)
{
    /// <summary>The most a pivot asks for besides its values: its cells' extra figures.</summary>
    public const int MaxExtras = 16;

    private static readonly string[] Order = ["position", "mark", "notional", "gross", "pnl", "balance", "volume", "trades"];

    /// <summary>The values a pivot shows, as a page's address names them, in their order.</summary>
    public static IReadOnlyList<string> Values => Order;

    private static readonly string[] Suffixes = ["", "row", "column", "total", "rowShown", "columnShown", "totalShown", "running"];

    /// <summary>How each form is named in a heading, after its value's name ("Volume % col").</summary>
    private static readonly string[] Marks = ["", "% row", "% col", "% total", "% row shown", "% col shown", "% total shown", "cum."];

    /// <summary>
    ///     Whether a value has a market total — what its percentages of the market are of: notional, Pos PnL, balance,
    ///     volume and trades (summed over every row the other filters keep). A position (each in its own asset's units) and
    ///     max gross (each client's larger side) show as percentages of what's shown alone.
    /// </summary>
    public static bool HasMarket(string value)
    {
        return value is "notional" or "pnl" or "balance" or "volume" or "trades";
    }

    /// <summary>
    ///     Its figure's places in a cell: a running volume to the cent, as volume's own is; the rest — a percentage's
    ///     fraction, a running count — eight.
    /// </summary>
    public int Places => Form == ShowAs.RunningTotalInDate && Value == "volume" ? 2 : Derived.Places;

    /// <summary>A percentage's total: its row's, its column's, or the grand total — of the market or of what's shown.</summary>
    public bool IsPercent => Form is not (ShowAs.Value or ShowAs.RunningTotalInDate);

    /// <summary>Of the market: the total ignoring the filters on the fields the percentage runs across.</summary>
    public bool OfMarket => Form is ShowAs.PercentOfRow or ShowAs.PercentOfColumn or ShowAs.PercentOfGrandTotal;

    /// <summary>Whether its total runs across the rows (its column's, or the grand total) and across the columns (its row's, or the grand total).</summary>
    public (bool AcrossRows, bool AcrossColumns) Across => Form switch
    {
        ShowAs.PercentOfColumn or ShowAs.PercentOfColumnShown => (true, false),
        ShowAs.PercentOfRow or ShowAs.PercentOfRowShown => (false, true),
        ShowAs.PercentOfGrandTotal or ShowAs.PercentOfGrandTotalShown => (true, true),
        _ => (false, false)
    };

    /// <summary>Its heading's mark, after its value's name: <c>% col</c>, <c>% col shown</c>, <c>cum.</c>.</summary>
    public string Mark => Marks[(int)Form];

    /// <summary>An entry read — or false: neither a value nor a form a value has.</summary>
    public static bool TryParse(string entry, out ShownValue shown)
    {
        shown = default;
        var (value, suffix) = entry.Split('.') switch { [var v] => (v, ""), [var v, var s] when s.Length > 0 => (v, s), _ => ("", "") };
        var form = Array.IndexOf(Suffixes, suffix);
        if (!Values.Contains(value) || form < 0 || (form > 0 && value == "mark") ||
            ((ShowAs)form is ShowAs.PercentOfRow or ShowAs.PercentOfColumn or ShowAs.PercentOfGrandTotal && !HasMarket(value)) ||
            ((ShowAs)form == ShowAs.RunningTotalInDate && value is not ("volume" or "trades"))) return false;
        shown = new ShownValue(value, (ShowAs)form);
        return true;
    }

    /// <summary>
    ///     What a pivot asks for besides its values, in the order its cells carry them: each entry read (what isn't one
    ///     dropped, and values themselves), once, by its value's order and then its form's — the same for any order asked
    ///     in, so pages asking alike share a pivot — at most <see cref="MaxExtras" />.
    /// </summary>
    public static ShownValue[] Extras(IEnumerable<string> entries)
    {
        return
        [
            .. entries.Select(e => TryParse(e, out var s) ? s : default).Where(s => s.Value is not null && s.Form != ShowAs.Value)
                .Distinct().OrderBy(s => Array.IndexOf(Order, s.Value)).ThenBy(s => s.Form).Take(MaxExtras)
        ];
    }

    /// <summary>The entry: its value's name, then its form's.</summary>
    public override string ToString()
    {
        return Form == ShowAs.Value ? Value : $"{Value}.{Suffixes[(int)Form]}";
    }
}

/// <summary>
///     A cell's figures, each in hundred-millionths — eight places, volume's cents scaled up — or none (null): the figure
///     doesn't add up across what the cell spans, or the statement doesn't answer it.
/// </summary>
public readonly record struct PivotFigures(
    Int128? Quantity, Int128? Notional, Int128? Realized, Int128? Unrealized, Int128? Balance,
    Int128? Volume, Int128? Trades, Int128? MaxGross, Int128? MarkPrice)
{
    /// <summary>A value's figure, as a page's grid has it: Pos PnL realized and unrealized (realized alone where unrealized is none).</summary>
    public Int128? Of(string value)
    {
        return value switch
        {
            "position" => Quantity, "mark" => MarkPrice, "notional" => Notional, "gross" => MaxGross,
            "pnl" => Realized is { } r ? Unrealized is { } u ? r + u : r : null,
            "realized" => Realized, "unrealized" => Unrealized, "balance" => Balance, "volume" => Volume, "trades" => Trades,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Not a value a pivot shows.")
        };
    }

    /// <summary>A cell's figures, read from its row by the pivot's shape (<see cref="PivotShape" />).</summary>
    public static PivotFigures Read(PivotShape shape, ReadOnlySpan<byte> row)
    {
        return new PivotFigures(Get(shape, row, "quantity"), Get(shape, row, "notional"), Get(shape, row, "realized"),
            Get(shape, row, "unrealized"), Get(shape, row, "balance"), Get(shape, row, "volume"), Get(shape, row, "trades"),
            Get(shape, row, "max_gross"), Get(shape, row, "mark_price"));

        static Int128? Get(PivotShape shape, ReadOnlySpan<byte> row, string name)
        {
            var field = shape.Row.Field(name);
            if (field.From is null || PivotShape.Integer(row, field) is not { } value) return null; // (unanswered, or none)
            return Derived.Scaled(value, field.Scale ?? 0);
        }
    }
}

/// <summary>
///     The arithmetic of a pivot's percentages, the data provider's (its cells carry them): a part of a total as a
///     fraction to eight places, rounded half away from zero — none where either is none, or the total is zero; the cell
///     whose total a percentage is of; a figure exactly, as a copy has it.
/// </summary>
public static class Derived
{
    /// <summary>A figure's places: eight, hundred-millionths.</summary>
    public const int Places = 8;

    private static readonly Int128 Unit = Pow10(Places);

    /// <summary>A part of a total, as a fraction: to eight places, half away from zero — none where either is none, or the total is zero.</summary>
    public static Int128? Percent(Int128? part, Int128? total)
    {
        return part is { } x && total is { } y && y != 0 ? Ratio(x, y) : null;
    }

    /// <summary>A part of a total, as a fraction: to eight places, half away from zero (truncated, then rounded on what's left).</summary>
    public static Int128 Ratio(Int128 part, Int128 total)
    {
        Int128 n = part * Unit, q = n / total, rest = n % total; // (truncated, toward zero)
        return 2 * Int128.Abs(rest) >= Int128.Abs(total) ? q + ((n < 0) != (total < 0) ? -1 : 1) : q;
    }

    /// <summary>
    ///     The cell whose figure a percentage of the cell (<paramref name="row" />, <paramref name="column" />) is of: its
    ///     row's total (the Total column, on its row), its column's (the Total row, in its column), or the grand total.
    /// </summary>
    public static (string Row, string Column) TotalOf(ShownValue shown, string row, string column)
    {
        var (acrossRows, acrossColumns) = shown.Across;
        return (acrossRows ? "" : row, acrossColumns ? "" : column);
    }

    /// <summary>A figure at its scale in hundred-millionths: a volume's cents scaled up.</summary>
    public static Int128 Scaled(long value, int scale)
    {
        return scale <= Places ? value * Pow10(Places - scale) : value / Pow10(scale - Places);
    }

    /// <summary>A figure exactly, as a page's copy has it: its units, no trailing zeros — <c>-0.00000001</c>, <c>83280448.54</c>.</summary>
    public static string Exactly(Int128 figure)
    {
        var magnitude = Int128.Abs(figure);
        var fraction = (magnitude % Unit).ToString().PadLeft(Places, '0').TrimEnd('0');
        return $"{(figure < 0 ? "-" : "")}{magnitude / Unit}{(fraction.Length > 0 ? $".{fraction}" : "")}";
    }

    /// <summary>Ten to the <paramref name="n" />.</summary>
    public static Int128 Pow10(int n)
    {
        Int128 power = 1;
        for (var i = 0; i < n; i++) power *= 10;
        return power;
    }
}
