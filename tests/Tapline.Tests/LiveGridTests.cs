using System.Buffers.Binary;
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
        ]);
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
        Assert(GridColumns<ColumnRow>.StampOf("placed") is ({ Size: 8, Unit: "ticks" } placed, null) && placed.Offset == at["Placed"].Offset &&
               GridColumns<ColumnRow>.StampOf("Qty") is ({ Unit: null } qty, null) && qty.Offset == at["Qty"].Offset,
            "a time field (ArrivedAt): a DateTime by its ticks, a long as microseconds, each where it is");

        var (record, recordRefusal) = GridColumns<ColumnRecord>.Resolve([Col("price")]);
        var recordRow = new ColumnRecord(7, 12.5);
        var recordBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<ColumnRecord>(in recordRow)).ToArray();
        Assert(recordRefusal is null && BinaryPrimitives.ReadDoubleLittleEndian(recordBytes.AsSpan(record[0].Offset)) == 12.5,
            "a record struct's property, by its backing field");
        return Task.CompletedTask;
    }

    public static Task LiveColumnsRefused()
    {
        string? Refused(params LiveColumn[] columns) => GridColumns<ColumnRow>.Resolve(columns).Refusal;
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
        Says(Refused(Col("fill", c => (c.Kind, c.Of, c.Over) = (CellKind.Percent, "Rate", "Big"))), "Of: \"Rate\" is a Double, not an integer",
            "a percentage of a double");
        Says(Refused(Col("Qty", c => c.Width = 0)), "a Width of 0 px", "no width");
        Says(Refused(Col("Qty", c => c.Scale = 19)), "a Scale of 19", "a scale past a long's");
        Says(Refused(Col("Qty", c => c.Figures = 0)), "0 Figures", "no figures");
        Says(GridColumns<ColumnRow>.StampOf("Count").Refusal, "\"Count\" is a Int32: a time is a long of microseconds since 1970, or a DateTime",
            "a time that's an int");
        Says(GridColumns<ColumnRow>.StampOf("When").Refusal, "ColumnRow has no field \"When\"", "no such time field");
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
