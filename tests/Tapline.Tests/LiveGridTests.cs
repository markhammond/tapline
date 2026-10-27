using System.Buffers.Binary;
using CultureInfo = System.Globalization.CultureInfo;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Tapline.Grid;

/// <summary>
///     Tapline.Grid's component: LiveColumns resolved against their rows' fields — where each is in the bytes Tapline
///     sends, its kind — and the configurations refused, saying why; keys as the page holds them; text as the page reads
///     it; and a LiveGrid rendered statically refused where it would be.
/// </summary>
internal static partial class T
{
    public static Task LiveColumnsResolved()
    {
        var row = new ColumnRow
        {
            Urgent = true, Flagged = true, Count = -123_456, Side = ColumnSide.Sell, Qty = 9_876_543_210,
            Big = ulong.MaxValue - 5, Rate = 3.25, Ratio = 0.1f,
            Placed = new DateTime(2026, 10, 26, 14, 32, 5, DateTimeKind.Utc),
            Id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Position = ColumnPosition.Short, Filled = 3_000_000_000,
            Money = 1.5m, Pair = (1, 2)
        };
        GridText.Write(row.Client, "Client 42");
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<ColumnRow>(in row)).ToArray();
        var (columns, refusal) = GridColumns<ColumnRow>.Resolve([
            Col("Urgent"), Col("flagged"), Col("Count"), Col("Side"), Col("Qty"), Col("Big"), Col("Rate"), Col("Ratio"),
            Col("Placed"), Col("Id"), Col("Client"), Col("Position"), Col("Filled"), Col("qty4", c => (c.Field, c.Scale) = ("Qty", 4)),
            Col("fill", c => (c.Kind, c.Of, c.Over) = (CellKind.Percent, "Filled", "Big"))
        ], CultureInfo.InvariantCulture);
        Assert(refusal is null, $"every kind resolved: {refusal}");
        var at = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        ReadOnlySpan<byte> Field(string name) => bytes.AsSpan(at[name].Offset, at[name].Size);

        // Each column's bytes, where the resolver placed them, hold the value written by name: the oracle is the row itself.
        Assert(at["Urgent"] is { Kind: "bool", Size: 1 } && Field("Urgent")[0] == 1 &&
               at["flagged"] is { Kind: "bool", Offset: 1 } && Field("flagged")[0] == 1,
            "bools side by side (not four bytes each, as marshalled); a name in any case");
        Assert(at["Count"] is { Kind: "int", Size: 4, Unsigned: false } && BinaryPrimitives.ReadInt32LittleEndian(Field("Count")) == -123_456,
            "an int");
        Assert(at["Side"] is { Kind: "enum", Size: 1, Unsigned: true } && Field("Side")[0] == 1 &&
               at["Side"].Dictionary!["0"] == "Buy" && at["Side"].Dictionary!["1"] == "Sell", "a byte enum, its names");
        Assert(at["Qty"] is { Kind: "int", Size: 8 } && BinaryPrimitives.ReadInt64LittleEndian(Field("Qty")) == 9_876_543_210, "a long");
        Assert(at["Big"] is { Kind: "int", Unsigned: true } && BinaryPrimitives.ReadUInt64LittleEndian(Field("Big")) == ulong.MaxValue - 5,
            "a ulong, unsigned");
        Assert(at["Rate"] is { Kind: "float", Size: 8 } && BinaryPrimitives.ReadDoubleLittleEndian(Field("Rate")) == 3.25, "a double");
        Assert(at["Ratio"] is { Kind: "float", Size: 4 } && BinaryPrimitives.ReadSingleLittleEndian(Field("Ratio")) == 0.1f, "a float");
        Assert(at["Placed"] is { Kind: "time", Size: 8, Unit: "ticks" } &&
               (BinaryPrimitives.ReadInt64LittleEndian(Field("Placed")) & ((1L << 62) - 1)) == row.Placed.Ticks, "a DateTime, in ticks");
        Assert(at["Id"] is { Kind: "uuid", Size: 16 } && new Guid(Field("Id")) == row.Id, "a Guid");
        Assert(at["Client"] is { Kind: "text", Size: 12 } && Field("Client")[0] == 9 &&
               Encoding.UTF8.GetString(Field("Client").Slice(1, 9)) == "Client 42", "an inline array of bytes: text");
        Assert(at["Position"] is { Kind: "enum", Size: 2, Unsigned: false } && BinaryPrimitives.ReadInt16LittleEndian(Field("Position")) == -1 &&
               at["Position"].Dictionary!["-1"] == "Short", "a signed enum, its names by value");
        Assert(at["Filled"] is { Kind: "int", Size: 4, Unsigned: true } &&
               BinaryPrimitives.ReadUInt32LittleEndian(Field("Filled")) == 3_000_000_000, "a uint, unsigned");
        Assert(at["Qty"] is { Scale: null } && at["qty4"] is { Kind: "fixed", Scale: 4 } && at["qty4"].Offset == at["Qty"].Offset,
            "a field shown twice, by a column of another name (its Field): given a scale, fixed");
        var fill = at["fill"];
        Assert(fill is { Kind: "percent", Sortable: false } && fill.Of == new GridField(at["Filled"].Offset, 4, true) &&
               fill.Over == new GridField(at["Big"].Offset, 8, true), "a percentage: its fields by place, never sorted by");
        Assert(columns.Where(c => c.Kind != "percent").All(c => c.Sortable), "the rest sortable");
        Assert(at["Count"] is { Format: null, Width: "8ch" } && at["Rate"] is { Format: null, Width: "12ch" } &&
               at["Placed"] is { Format: "MM/dd/yyyy HH:mm:ss", Invariant: false, Width: "19ch" } && at["Id"] is { Format: null, Width: "36ch" } &&
               at["Side"].Width == "6ch" && at["Client"].Width == "11ch" && at["Urgent"].Width == "8ch" && at["fill"] is { Format: null, Width: "8ch" },
            "no Format: .NET's default (a time's, its culture's G spelt out), and each as wide, in ch, as it writes a wide value of its " +
            "kind — or its label, and room for its arrow: " + string.Join(", ", columns.Select(c => $"{c.Name} {c.Width}")));
        var (blotter, blotterRefusal) = GridColumns<ColumnRow>.Resolve([
            Col("at", c => (c.Field, c.Format, c.MarkValues, c.Overflow) = ("Placed", "'… 'HH:mm:ss", true, CellOverflow.EllipsisStart)),
            Col("px", c => (c.Field, c.Format, c.Align, c.Class) = ("Rate", "#,###.####;-#,###.####;–", CellAlign.Center, "price")),
            Col("share", c => (c.Kind, c.Of, c.Over, c.Format, c.Width) = (CellKind.Percent, "Ratio", "Rate", "0.0%", "auto")),
            Col("when", c => (c.Field, c.Format, c.Width) = ("Placed", "u", "calc(10ch + 4px)")),
            Col("size", c => (c.Field, c.Format, c.Figures, c.MostDigits) = ("Rate", "#,###.########;-#,###.########;–", 6, 8))
        ], CultureInfo.InvariantCulture);
        Assert(blotterRefusal is null && blotter[0] is { Kind: "time", Format: "'… 'HH:mm:ss", Mark: true, Overflow: "ellipsis-start", Width: "10ch" } &&
               blotter[1] is { Kind: "float", Format: "#,###.####;-#,###.####;–", Align: "center", Class: "price", Width: "14ch" } &&
               blotter[2] is { Format: "0.0%", Auto: true, Width: "7ch" } &&
               blotter[2].Of == new GridField(at["Ratio"].Offset, 4, false, true) && blotter[2].Over == new GridField(at["Rate"].Offset, 8, false, true) &&
               blotter[3] is { Format: "yyyy'-'MM'-'dd HH':'mm':'ss'Z'", Invariant: true, Width: "calc(10ch + 4px)", Auto: false } &&
            blotter[4] is { Figures: 6, MostDigits: 8, Width: "12ch" },
            "a blotter's columns: a time by its custom pattern, cut at its start, its cells marked with their value; a double by a " +
            "custom format, centred, its class; a percentage of floating point fields, by a format, measured; a standard time " +
            "format spelt out, in the invariant culture's names, its width given; a quantity to six figures, scientific past " +
            "eight digits, as wide as that writes (-1.23457e-12): " + string.Join(", ", blotter.Select(c => $"{c.Name} {c.Width}")));
        Assert(GridColumns<ColumnRow>.StampOf("placed") is ({ Size: 8, Unit: "ticks" } placed, null) && placed.Offset == at["Placed"].Offset &&
               GridColumns<ColumnRow>.StampOf("Qty") is ({ Unit: null } qty, null) && qty.Offset == at["Qty"].Offset,
            "a time field (ArrivedAt): a DateTime by its ticks, a long as microseconds, each where it is");

        var (record, recordRefusal) = GridColumns<ColumnRecord>.Resolve([Col("price")], CultureInfo.InvariantCulture);
        var recordRow = new ColumnRecord(7, 12.5);
        var recordBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<ColumnRecord>(in recordRow)).ToArray();
        Assert(recordRefusal is null && BinaryPrimitives.ReadDoubleLittleEndian(recordBytes.AsSpan(record[0].Offset)) == 12.5,
            "a record struct's property, by its backing field");
        return Task.CompletedTask;
    }

    public static Task LiveColumnsRefused()
    {
        string? Refused(params LiveColumn[] columns) => GridColumns<ColumnRow>.Resolve(columns, CultureInfo.InvariantCulture).Refusal;
        void Says(string? refusal, string fragment, string what) =>
            Assert(refusal?.Contains(fragment, StringComparison.Ordinal) == true, $"{what}: \"{refusal}\"");

        Says(Refused(), "has no columns", "none");
        Says(Refused(Col("")), "has no Name", "a column with no name");
        Says(Refused(Col("Qty"), Col("qty")), "two columns are named \"qty\"", "a name twice");
        Says(Refused(Col("Qty"), Col("Rate", c => c.Frozen = true)), "\"Rate\" is frozen but follows \"Qty\"", "frozen after not");
        Says(Refused(Col("Qtty")), "ColumnRow has no field \"Qtty\" (its fields: Urgent, Flagged, Count,", "no such field");
        Says(Refused(Col("qty", c => c.Field = "Quantity")), "ColumnRow has no field \"Quantity\"", "no such Field");
        Says(Refused(Col("Money")), "a decimal", "a decimal");
        Says(Refused(Col("Pair")), "can't show", "a struct of its own");
        Says(Refused(Col("Qty", c => c.Kind = CellKind.Float)), "Kind Float can't read its field, a Int64", "a kind that can't read it");
        Says(Refused(Col("Qty", c => c.Kind = CellKind.Fixed)), "needs its Scale", "fixed with no scale");
        Says(Refused(Col("Count", c => c.Kind = CellKind.Enum)), "needs a Dictionary", "an int as an enum, no names");
        Says(Refused(Col("Qty", c => c.Dictionary = new Dictionary<long, string>())), "a Kind Enum column's", "names for no enum");
        Says(Refused(Col("Qty", c => c.Of = "Filled")), "Of and Over are a Percent column's", "Of not a percentage's");
        Says(Refused(Col("fill", c => c.Kind = CellKind.Percent)), "needs Of and Over", "a percentage of nothing");
        Says(Refused(Col("fill", c => (c.Kind, c.Of, c.Over, c.Sortable) = (CellKind.Percent, "Filled", "Big", true))),
            "can't be sorted by", "a percentage sorted by");
        Says(Refused(Col("Qty", c => c.Width = "120")), "a Width of \"120\": a CSS length", "a width with no unit");
        Says(Refused(Col("Qty", c => c.Width = "20%")), "a Width of \"20%\"", "a percentage (the widths add up in the cells' font)");
        Says(Refused(Col("Qty", c => c.Width = "9em; color: red")), "a CSS length", "a width that would end the declaration");
        Says(Refused(Col("Qty", c => c.Width = "calc(10ch + (2px)")), "a CSS length", "a calc() not closed");
        Says(Refused(Col("Qty", c => c.Scale = 19)), "a Scale of 19", "a scale past a long's");
        Says(Refused(Col("Rate", c => c.Format = "D")), "Format \"D\" can't write a Double", "a format a double refuses");
        Says(Refused(Col("qty4", c => (c.Field, c.Scale, c.Format) = ("Qty", 4, "X"))), "Format \"X\" can't write a Decimal",
            "a format a Fixed's decimal refuses");
        Says(Refused(Col("Qty", c => c.Format = "N200")), "asks for 200 digits: at most 100", "a format of too many digits");
        Says(Refused(Col("Placed", c => c.Format = "%")), "Format \"%\" can't write a time", "a time's pattern .NET refuses");
        Says(Refused(Col("Id", c => c.Format = "Q")), "Format \"Q\" can't write a Guid", "a Guid's format .NET refuses");
        Says(Refused(Col("Side", c => c.Format = "D")), "Format is a number's, a time's or a Guid's: a Kind Enum column writes its values' names",
            "a format for names");
        Says(Refused(Col("Client", c => c.Format = "0")), "a Kind Text column writes its text as it is", "a format for text");
        Says(Refused(Col("Side", c => c.Figures = 3)), "Figures and MostDigits are a number's", "figures of names");
        Says(Refused(Col("Rate", c => c.Figures = 0)), "0 Figures: from 1 to 17", "no figures");
        Says(Refused(Col("Rate", c => c.MostDigits = 0)), "a MostDigits of 0: from 1 to 28", "no digits");
        Says(Refused(Col("Count", c => (c.Format, c.Figures) = ("X8", 3))), "Figures round a number written as a decimal",
            "figures of a hexadecimal");
        Says(Refused([.. Enumerable.Range(0, 65).Select(i => Col($"q{i}", c => c.Field = "Qty"))]), "it has 65 columns: at most 64",
            "more columns than the stylesheet places");
        Says(GridColumns<ColumnRow>.StampOf("Count").Refusal, "\"Count\" is a Int32: a time is a long of microseconds since 1970, or a DateTime",
            "a time that's an int");
        Says(GridColumns<ColumnRow>.StampOf("When").Refusal, "ColumnRow has no field \"When\"", "no such time field");
        Says(GridColumns<ColumnRow>.ArrivalOf<long>("When", false).Refusal, "ArrivedAt: ColumnRow has no field \"When\"",
            "ArrivedAt's field, said whose");
        Says(GridColumns<ColumnRow>.ArrivalOf<Guid>("Qty", true).Refusal, "not both", "a field and the key: one or the other");
        Says(GridColumns<ColumnRow>.ArrivalOf<long>(null, true).Refusal, "the keys are Int64s", "a key's time from a long");
        Assert(GridColumns<ColumnRow>.ArrivalOf<Guid>(null, true) == (GridStamp.Key, null) &&
               GridColumns<ColumnRow>.ArrivalOf<Guid>(null, false) == (null, null), "a Guid key's time, or none");
        Says(Refused(Col("share", c => (c.Kind, c.Of, c.Over) = (CellKind.Percent, "Client", "Rate"))), "Of: \"Client\" is a ColumnText, not a number",
            "a percentage of text");
        Assert(Refused(Col("Qty"), Col("Rate")) is null, "and two good ones: none");
        return Task.CompletedTask;
    }

    public static Task LiveGridKeysAndText()
    {
        var key = GridKeys<long>.ToPage(0x0102_0304_0506_07F8L);
        Assert(key == "ø\u0007\u0006\u0005\u0004\u0003\u0002\u0001", "a long's bytes, a character each (as tapline.js keys its replica)");
        Assert(GridKeys<long>.TryFromPage(key, out var back) && back == 0x0102_0304_0506_07F8L, "and back");
        var id = Guid.CreateVersion7();
        Assert(GridKeys<Guid>.TryFromPage(GridKeys<Guid>.ToPage(id), out var idBack) && idBack == id, "a Guid there and back");
        var made = DateTimeOffset.FromUnixTimeMilliseconds(1_730_000_123_456);
        var page = GridKeys<Guid>.ToPage(Guid.CreateVersion7(made));
        long Byte(int i) => page[i];
        var ms = ((Byte(0) | (Byte(1) << 8) | (Byte(2) << 16)) + Byte(3) * 0x1000000) * 0x10000 + (Byte(4) | (Byte(5) << 8));
        Assert(ms == made.ToUnixTimeMilliseconds() && Byte(7) >> 4 == 7,
            $"a UUIDv7's time in its page key where keyTime reads it (Data1 and Data2 little-endian): {ms}");
        Assert(!GridKeys<long>.TryFromPage("short", out _) && !GridKeys<long>.TryFromPage(null, out _) &&
               !GridKeys<long>.TryFromPage("Ā\0\0\0\0\0\0\0", out _), "another size, none, a character past a byte: no key");

        var field = new byte[8];
        GridText.Write(field, "Zoë Ångström");
        Assert(field[0] == (0x80 | 7) && Encoding.UTF8.GetString(field, 1, 7) == "Zoë Å", "cut at a character's end, flagged");
        GridText.Write(field.AsSpan(0, 7), "Zoë Ångström");
        Assert(field[0] == (0x80 | 5) && Encoding.UTF8.GetString(field, 1, 5) == "Zoë ", "not through one (Å's two bytes)");
        GridText.Write(field, "Kent");
        Assert(field[0] == 4 && Encoding.UTF8.GetString(field, 1, 4) == "Kent" && field[5..].All(b => b == 0), "fits: its length, the rest clear");
        Throws<ArgumentException>(() => GridText.Write(new byte[1], "x"), "a field of one byte");
        Throws<ArgumentException>(() => GridText.Write(new byte[129], "x"), "a field past 128");
        return Task.CompletedTask;
    }

    public static Task LiveGridSorts()
    {
        static string Show(IReadOnlyList<SortKey> sort) =>
            sort.Count == 0 ? "own" : string.Join(",", sort.Select(k => k.Column + (k.Descending ? "↓" : "↑")));
        var a = new SortKey("a");
        string Next(IReadOnlyList<SortKey> sort, string column, bool add, bool descendingFirst) =>
            Show(LiveGrid<long, ColumnRow>.NextSort(sort, column, add, descendingFirst));

        // Ascending first (the default): a click sorts by it alone, then reverses it, then reverses it again.
        Assert(Next([], "a", false, false) == "a↑" && Next([a], "a", false, false) == "a↓" &&
               Next([a with { Descending = true }], "a", false, false) == "a↑", "ascending first: ↑, ↓, ↑");
        Assert(Next([a, new SortKey("b")], "a", false, false) == "a↑", "a click on one of several: it alone, the first way");
        Assert(Next([new SortKey("A")], "a", false, false) == "a↓", "a column by its name in any case");
        Assert(Next([], "a", true, false) == "a↑" && Next([a], "b", true, false) == "a↑,b↑" &&
               Next([a, new SortKey("b")], "b", true, false) == "a↑,b↓" && Next([a, new SortKey("b", true)], "b", true, false) == "a↑",
            "shift-clicked: added ↑, reversed, taken out");

        // Descending first (as a blotter sorts): ↓, ↑, then back to the source's own order; shift-click alike.
        Assert(Next([], "a", false, true) == "a↓" && Next([a with { Descending = true }], "a", false, true) == "a↑" &&
               Next([a], "a", false, true) == "own", "descending first: ↓, ↑, the source's own order");
        Assert(Next([a with { Descending = true }], "b", true, true) == "a↓,b↓" &&
               Next([a with { Descending = true }, new SortKey("b", true)], "b", true, true) == "a↓,b↑" &&
               Next([a with { Descending = true }, new SortKey("b")], "b", true, true) == "a↓", "shift-clicked: added ↓, reversed, taken out");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The window LiveGrid reads before the page asks, from the body's height: as the page's module asks first (its
    ///     view, a quarter of it ahead; tapline-grid.js's test holds windowFor to the same rows), within the session's bound.
    /// </summary>
    public static Task LiveGridFirstWindow()
    {
        static string Show(double height, int maxRows = 1_000) =>
            LiveGrid<long, ColumnRow>.FirstWindow(height, 30, maxRows, 2) is { } w
                ? $"{w.First}+{w.Count}+{w.After}/{w.Before}@{w.View}"
                : "none";
        Assert(Show(780) == "0+26+7/0@2" && Show(790) == "0+27+7/0@2" && Show(100) == "0+4+1/0@2",
            "the rows in view, a part one rounded up, and a quarter of them ahead, rounded up; in the view asked in");
        Assert(Show(29_000) == "0+967+33/0@2" && Show(100_000) == "0+1000+0/0@2", "within the window's bound: the rows ahead first to go");
        Assert(Show(0) == "none" && Show(double.NaN) == "none" && Show(780, 0) == "none", "no height (or no window): none read");
        return Task.CompletedTask;
    }

    public static async Task LiveGridStaticRefused()
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime, NoScript>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<LiveGrid<long, ColumnRow>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(LiveGrid<long, ColumnRow>.Source)] = new NaturalGrid<ColumnRow>(100, _ => default),
                [nameof(LiveGrid<long, ColumnRow>.Columns)] = (RenderFragment)(b =>
                {
                    b.OpenComponent<LiveColumn>(0);
                    b.AddComponentParameter(1, nameof(LiveColumn.Name), "Qty");
                    b.CloseComponent();
                })
            }));
            return output.ToHtmlString();
        });
        Assert(html.Contains("role=\"alert\"") && html.Contains("show its rows") && html.Contains("rendered statically") &&
               html.Contains("@rendermode InteractiveServer"), $"rendered statically: refused in its place, saying why: {html}");
        Assert(!html.Contains("role=\"grid\""), "and no grid");
    }

    private static LiveColumn Col(string name, Action<LiveColumn>? set = null)
    {
        var column = new LiveColumn { Name = name };
        set?.Invoke(column);
        return column;
    }

    private enum ColumnSide : byte
    {
        Buy,
        Sell
    }

    private enum ColumnPosition : short
    {
        Short = -1,
        Flat = 0,
        Long = 1
    }

    [InlineArray(12)]
    private struct ColumnText
    {
        private byte _0;
    }

    /// <summary>A row of every kind the page reads, laid out by the runtime: two bools side by side, not as marshalled.</summary>
    private struct ColumnRow
    {
        public bool Urgent;
        public bool Flagged;
        public int Count;
        public ColumnSide Side;
        public long Qty;
        public ulong Big;
        public double Rate;
        public float Ratio;
        public DateTime Placed;
        public Guid Id;
        public ColumnText Client;
        public ColumnPosition Position;
        public uint Filled;
        public decimal Money;
        public (int A, int B) Pair;
    }

    private readonly record struct ColumnRecord(long Placed, double Price);

    /// <summary>No page: a call to it fails (a static render makes none).</summary>
    private sealed class NoScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            throw new InvalidOperationException("No page here.");
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            throw new InvalidOperationException("No page here.");
        }
    }
}
