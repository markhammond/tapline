using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BookLevel = BlazingOrderBooks.Sample.Level; // (Level is a method here too: the ladder's row)

namespace BlazingOrderBooks.Sample;

/// <summary>
///     The market provider's views of its topics, for the router's viewer: an HTTP server of its own, in this process, on
///     a
///     port the OS allocates (<c>http://127.0.0.1:0</c>), its URLs given to the router in step 1 (
///     <c>PayloadFormat.ViewUrl</c>).
///     When a viewer page asks, the router POSTs a topic's state or a record there — JSON: the router's own rendering, by
///     the
///     formats the feed declared — and shows the page it answers, sandboxed: the book as a ladder, the mark as a price, a
///     record's changes. The router sends back the feed's own sequence numbers too (the upstream versions it posted with):
///     a record the router keeps only in part, the feed finds in its journal (<see cref="ExchangeJournal" />).
/// </summary>
internal sealed class MarketViews : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MarketViews(WebApplication app, Uri at)
    {
        _app = app;
        Book = new Uri(at, "views/book");
        Accelerated = new Uri(at, "views/book-accelerated");
        Mark = new Uri(at, "views/mark");
    }

    public Uri Book { get; }
    public Uri Accelerated { get; }
    public Uri Mark { get; }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <param name="journal">What the feed posted to the book, by exchange seq.</param>
    /// <param name="accelerated">The same for the accelerated book (its own sequence of posts).</param>
    public static async Task<MarketViews> StartAsync(ExchangeJournal journal, ExchangeJournal accelerated,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // a port the OS allocates, on the loopback
        builder.Logging.SetMinimumLevel(LogLevel.Warning); // (the feed logs where it is)
        var app = builder.Build();
        app.MapPost("/views/book",
            async (HttpRequest request) =>
                Page(await Read(request), (html, envelope) => BookPage(html, envelope, journal)));
        app.MapPost("/views/book-accelerated",
            async (HttpRequest request) =>
                Page(await Read(request), (html, envelope) => BookPage(html, envelope, accelerated)));
        app.MapPost("/views/mark", async (HttpRequest request) => Page(await Read(request), MarkPage));
        await app.StartAsync(cancellationToken);
        return new MarketViews(app, new Uri(app.Urls.First() + "/")); // as bound: http://127.0.0.1:{port}/
    }

    private static async Task<JsonElement> Read(HttpRequest request)
    {
        using var envelope =
            await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
        return envelope.RootElement.Clone();
    }

    private static IResult Page(JsonElement envelope, Action<StringBuilder, JsonElement> body)
    {
        var html = new StringBuilder("<!doctype html><html lang=en><head><meta charset=utf-8><style>")
            .Append(
                "body{font:13px ui-monospace,Menlo,Consolas,monospace;margin:8px;color:#222;background:#fff}table{border-collapse:collapse}")
            .Append(
                "td{padding:1px 8px;text-align:right}td.bar{width:240px;text-align:left}.bar div{height:10px}.bid .bar div{background:#6aa86a}.ask .bar div{background:#c96a6a}")
            .Append(
                ".bid td:first-child{color:#2e7d32}.ask td:first-child{color:#c62828}.spread td{color:#888;font-size:11px}.big{font-size:28px;margin:12px 0}p{margin:4px 0 8px;color:#666}")
            .Append(
                "@media(prefers-color-scheme:dark){body{color:#ddd;background:#161616}.bid td:first-child{color:#81c784}.ask td:first-child{color:#e57373}p{color:#999}}")
            .Append("</style></head><body>");
        var rendered = envelope.GetProperty("rendered");
        if (rendered.ValueKind == JsonValueKind.Object && rendered.TryGetProperty("busy", out _))
            html.Append("<p>busy just now: open it again</p>");
        else body(html, envelope);
        return Results.Content(html.Append("</body></html>").ToString(), "text/html; charset=utf-8");
    }

    /// <summary>
    ///     The book's state as a ladder (the ten best levels a side, the spread between); a record, its changes — as the
    ///     router kept them, or, kept only in part, as the feed's journal has them, by the exchange sequences it's between.
    /// </summary>
    private static void BookPage(StringBuilder html, JsonElement envelope, ExchangeJournal journal)
    {
        var rendered = envelope.GetProperty("rendered");
        var label = Encode(envelope.GetProperty("topic").GetProperty("label").GetString() ?? "book");
        if (envelope.GetProperty("kind").GetString() == "record")
        {
            var record = envelope.GetProperty("record");
            long? upstream = Seq(record, "upstream"), since = Seq(record, "upstreamSince");
            var seqs = upstream is not { } k ? "" :
                since is { } j ? $" · exchange seq {j:N0} → {k:N0}" : $" · as of exchange seq {k:N0}";
            if (rendered.ValueKind == JsonValueKind.Null) // not kept whole by the router: the feed's own record of it
            {
                html.Append("<p>").Append(label).Append(Encode(seqs))
                    .Append(" · the router kept part of it: from the feed's journal</p>");
                var found = new List<(BookLevel Level, long Delta)>();
                if (upstream is not { } through || since is not { } from)
                {
                    html.Append("<p>a whole book: the feed keeps changes, not books</p>");
                }
                else if (!journal.TryChanges(from, through, found, out var why))
                {
                    html.Append("<p>not found: ").Append(Encode(why ?? "")).Append("</p>");
                }
                else
                {
                    html.Append(CultureInfo.InvariantCulture, $"<p>{found.Count} levels changed</p><table>");
                    foreach (var (level, delta) in found.OrderBy(c => c.Level.Side).ThenByDescending(c => c.Level.Price)
                                 .Take(50))
                        html.Append("<tr class=").Append(level.Side == BookLevel.Ask ? "ask" : "bid").Append("><td>")
                            .Append(level.Side == BookLevel.Ask ? "ask" : "bid").Append("</td><td>")
                            .Append((level.Price / 100m).ToString("N2", CultureInfo.InvariantCulture))
                            .Append("</td><td>")
                            .Append(delta.ToString("+#,0;−#,0", CultureInfo.InvariantCulture)).Append("</td></tr>");
                    html.Append("</table>");
                }

                return;
            }

            var changes = rendered.GetProperty("changes");
            html.Append("<p>").Append(label)
                .Append(rendered.GetProperty("snapshot").GetBoolean() ? " · a snapshot" : " · a delta")
                .Append(CultureInfo.InvariantCulture,
                    $" · versions {rendered.GetProperty("fromSeq")}→{rendered.GetProperty("toSeq")}")
                .Append(Encode(seqs))
                .Append(CultureInfo.InvariantCulture, $" · {rendered.GetProperty("entries")} changes</p><table>");
            foreach (var c in changes.EnumerateArray().Take(50))
            {
                var key = c.GetProperty("key");
                var side = key.GetProperty("side").GetString() ?? "";
                html.Append("<tr class=").Append(side == "ask" ? "ask" : "bid").Append("><td>").Append(Encode(side))
                    .Append("</td><td>").Append(Price(key.GetProperty("price")))
                    .Append("</td><td>")
                    .Append(c.GetProperty("op").GetString() == "delete"
                        ? "gone"
                        : Size(c.GetProperty("row").GetProperty("size"))).Append("</td></tr>");
            }

            html.Append("</table>");
            return;
        }

        var levels = rendered.GetProperty("entries").EnumerateArray()
            .Select(e => (Side: e.GetProperty("key").GetProperty("side").GetString(),
                Price: e.GetProperty("key").GetProperty("price").GetDecimal(),
                Size: e.GetProperty("row").GetProperty("size").GetInt64()))
            .ToList();
        var asks = levels.Where(l => l.Side == "ask").OrderBy(l => l.Price).Take(10).Reverse().ToList();
        var bids = levels.Where(l => l.Side == "bid").OrderByDescending(l => l.Price).Take(10).ToList();
        var most = Math.Max(1, asks.Concat(bids).Select(l => l.Size).DefaultIfEmpty(1).Max());
        var asOf = Seq(envelope.GetProperty("topic"), "upstream") is { } at ? $" (exchange seq {at:N0})" : "";
        html.Append("<p>").Append(label).Append(CultureInfo.InvariantCulture,
            $" · version {rendered.GetProperty("version")}{asOf} · {rendered.GetProperty("rows")} levels</p><table>");
        foreach (var l in asks) Level(html, "ask", l.Price, l.Size, most);
        if (asks.Count > 0 && bids.Count > 0)
            html.Append(CultureInfo.InvariantCulture,
                $"<tr class=spread><td>spread</td><td>{asks[^1].Price - bids[0].Price:N2}</td><td class=bar>mid {(asks[^1].Price + bids[0].Price) / 2:N3}</td></tr>");
        foreach (var l in bids) Level(html, "bid", l.Price, l.Size, most);
        html.Append("</table>");
    }

    private static void Level(StringBuilder html, string side, decimal price, long size, long most)
    {
        html.Append("<tr class=").Append(side).Append("><td>")
            .Append(price.ToString("N2", CultureInfo.InvariantCulture)).Append("</td><td>")
            .Append(size.ToString("N0", CultureInfo.InvariantCulture))
            .Append(CultureInfo.InvariantCulture,
                $"</td><td class=bar><div style=\"width:{Math.Max(1, 100 * size / most)}%\"></div></td></tr>");
    }

    /// <summary>The mark: a price, large.</summary>
    private static void MarkPage(StringBuilder html, JsonElement envelope)
    {
        var rendered = envelope.GetProperty("rendered");
        var value = rendered.ValueKind == JsonValueKind.Object
            ? rendered.GetProperty("value")
            : rendered; // the state's value, or a record's
        html.Append("<p>").Append(Encode(envelope.GetProperty("topic").GetProperty("label").GetString() ?? "mark"))
            .Append(" · mid price</p><div class=big>")
            .Append(value.ValueKind == JsonValueKind.Number ? Price(value) : Encode(value.GetRawText()))
            .Append("</div>");
    }

    /// <summary>An upstream version the router sent back (the feed's exchange sequence), if any.</summary>
    private static long? Seq(JsonElement on, string name)
        => on.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static string Price(JsonElement price)
        => price.GetDecimal().ToString("N2", CultureInfo.InvariantCulture);

    private static string Size(JsonElement size)
        => size.GetInt64().ToString("N0", CultureInfo.InvariantCulture);

    private static string Encode(string text)
        => WebUtility.HtmlEncode(text);
}