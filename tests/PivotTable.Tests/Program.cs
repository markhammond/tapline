// The pivot sample's tests: the percentages its data provider derives for the cells a pivot streams (PivotTable.Contracts,
// ShowAs.cs: Show as), against a reference — a fixture made by make-fixture.mjs, with the derivation the page had before
// the provider took it over (of what's shown). Run: dotnet run --project tests/PivotTable.Tests

using System.Buffers.Binary;
using System.Text.Json;
using PivotTable.Contracts;

var tests = new (string Name, Action Run)[]
{
    ("Show as: entries read as a page's address has them, and written back — a market's only where it has a total", Entries),
    ("Show as: a figure exactly, as a page's copy has it", Exactly),
    ("Show as: a cell's figures read by the shape — volume's cents scaled up, none where none or unanswered", Figures),
    ("Show as: the extras a pivot asks for, in the order its cells carry them", Extras),
    ("Show as: every percentage of what's shown in the fixture's layouts, derived as the reference does", Fixture),
    ("Show as: a percentage of the market, of the market's total; of what's shown, of the pivot's own", Market),
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"✓ {name}");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"✗ {name}\n  {e.Message.Replace("\n", "\n  ")}");
    }
}

Console.WriteLine(failed == 0 ? $"All {tests.Length} passed." : $"{failed} of {tests.Length} failed.");
return failed == 0 ? 0 : 1;

static void Entries()
{
    foreach (var (entry, value, form) in new[]
             {
                 ("volume", "volume", ShowAs.Value), ("volume.row", "volume", ShowAs.PercentOfRow),
                 ("volume.column", "volume", ShowAs.PercentOfColumn), ("pnl.total", "pnl", ShowAs.PercentOfGrandTotal),
                 ("volume.columnShown", "volume", ShowAs.PercentOfColumnShown), ("pnl.totalShown", "pnl", ShowAs.PercentOfGrandTotalShown),
                 ("notional.rowShown", "notional", ShowAs.PercentOfRowShown),
                 ("trades.running", "trades", ShowAs.RunningTotalInDate), ("mark", "mark", ShowAs.Value)
             })
    {
        Check(ShownValue.TryParse(entry, out var shown) && shown == new ShownValue(value, form), $"{entry} reads as {value}, {form}");
        Check(shown.ToString() == entry, $"{entry} written back as {shown}");
    }

    foreach (var entry in new[] { "mark.column", "mark.columnShown", "pnl.running", "notional.running", "volume.", "volume.sum", "share", "", "volume.column.row", "volume.ColumnShown", "position.column", "gross.total", "gross.row" })
        Check(!ShownValue.TryParse(entry, out _), $"{entry} refused");
    foreach (var entry in new[] { "position.columnShown", "gross.totalShown", "gross.rowShown" })
        Check(ShownValue.TryParse(entry, out _), $"{entry} read: of what's shown, though it has no market total");
    Check(new ShownValue("volume", ShowAs.RunningTotalInDate).Places == 2 && new ShownValue("trades", ShowAs.RunningTotalInDate).Places == 8
          && new ShownValue("volume", ShowAs.PercentOfColumn).Places == 8, "places: a running volume to the cent, the rest to eight");
}

static void Exactly()
{
    foreach (var (figure, text) in new (Int128, string)[]
             {
                 (0, "0"), (1, "0.00000001"), (-1, "-0.00000001"), (100_000_000, "1"), (-150_000_000, "-1.5"),
                 (8_328_044_854_000_000, "83280448.54"), (Int128.Parse("12345678901234567890123"), "123456789012345.67890123")
             })
        Check(Derived.Exactly(figure) == text, $"{figure} exactly: {Derived.Exactly(figure)}, not {text}");
    Check(Derived.Ratio(1, 200_000_000) == 1 && Derived.Ratio(-1, 200_000_000) == -1 && Derived.Ratio(1, -200_000_000) == -1,
        "half a hundred-millionth: away from zero, either way");
    Check(Derived.Ratio(1, 200_000_001) == 0, "under half: toward zero");
}

static void Figures()
{
    // A row as the sample's cells lay it out: nine i64 figures, little-endian — volume to two places, the rest to eight.
    string[] names = ["quantity", "notional", "realized", "unrealized", "balance", "volume", "trades", "max_gross", "mark_price"];
    const long none = long.MinValue;
    var shape = new PivotShape
    {
        Sql = "", Parameters = [], Columns = [], Reserved = [],
        Key = new ShapeLayout(0, []),
        Row = new ShapeLayout(names.Length * 8, [
            .. names.Select((n, i) => new ShapeField
            {
                Name = n, Offset = i * 8, Type = "int64", Bytes = 8, Scale = n == "volume" ? 2 : 8,
                None = none.ToString(), From = n == "mark_price" ? null : n
            })
        ])
    };
    long[] values = [3 * 100_000_000L, -2, none, 5, 0, 12_345, 7 * 100_000_000L, none, 1];
    var row = new byte[values.Length * 8];
    for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(i * 8), values[i]);
    var figures = PivotFigures.Read(shape, row);
    Check(figures.Quantity == 300_000_000 && figures.Notional == -2, "eight places as they are");
    Check(figures.Volume == 12_345 * (Int128)1_000_000, $"volume's cents scaled up to eight places: {figures.Volume}");
    Check(figures.Realized is null && figures.MaxGross is null, "none where the row holds its none");
    Check(figures.MarkPrice is null, "none where the statement doesn't answer it");
    Check(figures.Of("pnl") is null && figures with { Realized = 7 } is var r && r.Of("pnl") == 12, "Pos PnL: none without realized; realized and unrealized");
}

static void Extras()
{
    var extras = ShownValue.Extras(["trades.running", "volume", "pnl.totalShown", "volume.column", "bogus", "volume.column", "mark.row", "notional.rowShown"]);
    Check(string.Join(",", extras.Select(e => e.ToString())) == "notional.rowShown,pnl.totalShown,volume.column,trades.running",
        $"by value, then form, once, values and the unknown left out: {string.Join(",", extras.Select(e => e.ToString()))}");
    Check(ShownValue.Extras(Enumerable.Range(0, 40).Select(i => $"{ShownValue.Values[i % 8]}.{(i / 8) switch { 0 => "row", 1 => "column", 2 => "total", 3 => "rowShown", _ => "columnShown" }}")).Length == ShownValue.MaxExtras,
        $"at most {ShownValue.MaxExtras}");
    Check(new ShownValue("volume", ShowAs.PercentOfColumn).Mark == "% col" && new ShownValue("volume", ShowAs.PercentOfColumnShown).Mark == "% col shown"
          && new ShownValue("volume", ShowAs.RunningTotalInDate).Mark == "cum.", "marks");
}

static void Fixture()
{
    var path = Path.Combine(AppContext.BaseDirectory, "show-as.fixture.json");
    using var json = JsonDocument.Parse(File.ReadAllText(path));
    var (checks, misses) = (0, new List<string>());
    foreach (var layout in json.RootElement.GetProperty("layouts").EnumerateArray())
    {
        var cells = Cells(layout.GetProperty("cells"));
        foreach (var derived in layout.GetProperty("derived").EnumerateArray())
        {
            var (row, column, entry) = (derived[0].GetString()!, derived[1].GetString()!, derived[2].GetString()!);
            if (entry.EndsWith(".running", StringComparison.Ordinal)) continue; // (the statement's now: no client sums a day)
            var expected = derived[3].ValueKind == JsonValueKind.Null ? null : derived[3].GetString();
            // The reference's percentages were of what's shown: the pivot's own totals.
            if (!ShownValue.TryParse(entry + "Shown", out var shown)) throw new InvalidOperationException($"the fixture's {entry} isn't an entry");
            var figure = cells.TryGetValue((row, column), out var own)
                ? Derived.Percent(own.Of(shown.Value), cells.TryGetValue(Derived.TotalOf(shown, row, column), out var total) ? total.Of(shown.Value) : null)
                : null;
            var actual = figure is { } f ? Derived.Exactly(f) : null;
            checks++;
            if (actual != expected)
                misses.Add($"{layout.GetProperty("name").GetString()}: ({row}, {column}) {shown}: {actual ?? "none"}, the reference's {expected ?? "none"}");
        }
    }

    Check(checks > 800, $"only {checks} figures checked");
    Check(misses.Count == 0, $"{misses.Count} of {checks} differ from the reference's:\n{string.Join("\n", misses.Take(10))}");
}

static void Market()
{
    // Two clients kept of three: their volume, and the market's — every client's — on the Total row.
    const long unit = 100_000_000;
    Int128 a = 30 * unit, b = 10 * unit, market = 200 * unit;
    var column = new ShownValue("volume", ShowAs.PercentOfColumn);
    var shown = new ShownValue("volume", ShowAs.PercentOfColumnShown);
    Check(Derived.TotalOf(column, "Acorn Fund", "2026-10-15") == ("", "2026-10-15"), "% of column: the Total row's, in its column");
    Check(Derived.TotalOf(new ShownValue("volume", ShowAs.PercentOfRow), "Acorn Fund", "2026-10-15") == ("Acorn Fund", ""), "% of row: the Total column's, on its row");
    Check(Derived.TotalOf(new ShownValue("volume", ShowAs.PercentOfGrandTotalShown), "Acorn Fund", "2026-10-15") == ("", ""), "% of grand total: the grand total's");
    Check(Derived.Exactly(Derived.Percent(a, market)!.Value) == "0.15" && Derived.Exactly(Derived.Percent(a, a + b)!.Value) == "0.75",
        "of the market: 15% of all clients; of what's shown: 75% of the two");
    Check(Derived.Percent(a, 0) is null && Derived.Percent(null, market) is null && Derived.Percent(a, null) is null, "none: a total of zero, a part or a total none");
    Check(column.OfMarket && !shown.OfMarket && shown.IsPercent && !new ShownValue("volume", ShowAs.RunningTotalInDate).IsPercent, "kinds");
}

/// <summary>The fixture's cells: by their labels, their figures (q, n, r, u, b, v, t, g, m, as the page named them).</summary>
static Dictionary<(string, string), PivotFigures> Cells(JsonElement cells)
{
    var held = new Dictionary<(string, string), PivotFigures>();
    foreach (var cell in cells.EnumerateArray())
    {
        var f = cell.GetProperty("figures");
        Int128? Get(string k) => f.GetProperty(k).ValueKind == JsonValueKind.Null ? null : Int128.Parse(f.GetProperty(k).GetString()!);
        held[(cell.GetProperty("row").GetString()!, cell.GetProperty("column").GetString()!)] =
            new PivotFigures(Get("q"), Get("n"), Get("r"), Get("u"), Get("b"), Get("v"), Get("t"), Get("g"), Get("m"));
    }

    return held;
}

static void Check(bool ok, string what)
{
    if (!ok) throw new InvalidOperationException(what);
}
