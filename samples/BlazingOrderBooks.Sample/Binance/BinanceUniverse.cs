using System.Globalization;
using System.Text.Json;

namespace BlazingOrderBooks.Sample.Binance;

/// <summary>The Binance grid's knobs (configuration section <c>Binance</c>).</summary>
public sealed class BinanceOptions
{
    /// <summary>Whether the grid's feed runs at all: it reaches Binance over the internet.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many perpetuals: the most traded, by 24-hour quote volume, when the sample starts.</summary>
    public int Symbols { get; set; } = 12;

    /// <summary>
    ///     Instruments a connection carries, dealt round by volume (2: the busiest pair with quieter ones). Binance cuts a
    ///     connection that carries much more than a few hundred <c>bookTicker</c> updates a second.
    /// </summary>
    public int InstrumentsPerConnection { get; set; } = 2;

    /// <summary>
    ///     How long an instrument's Binance streams stay up after its last listener has gone: a page scrolled back to it
    ///     meanwhile gets its book at once, from the feed's own copy.
    /// </summary>
    public int GraceSeconds { get; set; } = 30;

    /// <summary>The REST API (instruments and volumes, at startup).</summary>
    public string RestUrl { get; set; } = "https://fapi.binance.com";

    /// <summary>
    ///     The market-data WebSocket: a combined stream, connected to bare and subscribed by request, per instrument, as
    ///     listeners come and go. (Binance's SBE market data — binary, fixed layouts, no JSON to parse — would be the one
    ///     to prefer, but needs an API key; these public JSON streams need none.)
    /// </summary>
    public string StreamUrl { get; set; } = "wss://fstream.binance.com/public/stream";

    /// <summary>Ticks each book's ladders span (a power of two); see <see cref="MarketFeedOptions.WindowTicks" />.</summary>
    public int WindowTicks { get; set; } = 16_384;
}

/// <summary>
///     One perpetual: its symbol, and how its prices and sizes are fixed-point — prices as integers in units of
///     10<sup>−<see cref="PriceScale" /></sup>, on a grid of <see cref="Tick" /> of them; sizes in units of
///     10<sup>−<see cref="SizeScale" /></sup>.
/// </summary>
public sealed record BinanceInstrument(string Symbol, int PriceScale, long Tick, int SizeScale, double QuoteVolume)
{
    /// <summary>The symbol as it appears in the streams' JSON (<c>"s"</c>), for comparing without a string.</summary>
    public byte[] Utf8 { get; } = System.Text.Encoding.UTF8.GetBytes(Symbol);

    /// <summary>The symbol as Binance names its streams: lower case.</summary>
    public string StreamName => Symbol.ToLowerInvariant();
}

/// <summary>
///     The instruments the grid shows: Binance's USDⓈ-M perpetuals on crypto (contract type <c>PERPETUAL</c>, underlying
///     type <c>COIN</c> — not the equity, commodity or FX ones it lists beside them), quoted in USDT, trading, ranked by
///     24-hour quote volume; each with its tick and lot step from its filters. Read once, at startup, over REST.
/// </summary>
public sealed class BinanceUniverse
{
    public IReadOnlyList<BinanceInstrument> Instruments { get; private set; } = [];

    /// <summary>Why there are none, if there are none (Binance out of reach, or the feed turned off).</summary>
    public string? Unavailable { get; private set; } = "starting";

    public BinanceInstrument? Find(string symbol)
    {
        foreach (var instrument in Instruments)
            if (instrument.Symbol == symbol)
                return instrument;
        return null;
    }

    public async Task LoadAsync(BinanceOptions options, HttpClient http, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            Unavailable = "turned off (Binance:Enabled)";
            return;
        }

        try
        {
            using var info = JsonDocument.Parse(await http.GetStringAsync($"{options.RestUrl}/fapi/v1/exchangeInfo",
                cancellationToken));
            using var tickers = JsonDocument.Parse(await http.GetStringAsync($"{options.RestUrl}/fapi/v1/ticker/24hr",
                cancellationToken));
            var volume = new Dictionary<string, double>();
            foreach (var t in tickers.RootElement.EnumerateArray())
                volume[t.GetProperty("symbol").GetString()!] = double.Parse(t.GetProperty("quoteVolume").GetString()!,
                    CultureInfo.InvariantCulture);
            var found = new List<BinanceInstrument>();
            foreach (var s in info.RootElement.GetProperty("symbols").EnumerateArray())
            {
                if (s.GetProperty("contractType").GetString() != "PERPETUAL" ||
                    s.GetProperty("underlyingType").GetString() != "COIN" ||
                    s.GetProperty("quoteAsset").GetString() != "USDT" ||
                    s.GetProperty("status").GetString() != "TRADING") continue;
                string? tick = null, step = null;
                foreach (var f in s.GetProperty("filters").EnumerateArray())
                    switch (f.GetProperty("filterType").GetString())
                    {
                        case "PRICE_FILTER": tick = f.GetProperty("tickSize").GetString(); break;
                        case "LOT_SIZE": step = f.GetProperty("stepSize").GetString(); break;
                    }

                var symbol = s.GetProperty("symbol").GetString()!;
                if (tick is null || step is null || !volume.TryGetValue(symbol, out var v)) continue;
                var (priceScale, tickUnits) = FixedPoint(tick);
                var (sizeScale, _) = FixedPoint(step);
                found.Add(new BinanceInstrument(symbol, priceScale, tickUnits, sizeScale, v));
            }

            Instruments = found.OrderByDescending(i => i.QuoteVolume).Take(Math.Max(1, options.Symbols)).ToArray();
            Unavailable = Instruments.Count == 0 ? "no instruments listed" : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or KeyNotFoundException or FormatException)
        {
            Unavailable = $"Binance out of reach: {e.Message}";
        }
    }

    /// <summary>
    ///     A decimal step as fixed point: its decimal places (trailing zeros dropped: "0.00100" → 3) and the step in
    ///     those units ("0.00100" → 1; "0.25" → 25).
    /// </summary>
    public static (int Scale, long Units) FixedPoint(string step)
    {
        var value = decimal.Parse(step, CultureInfo.InvariantCulture);
        if (value <= 0) throw new FormatException($"A step of {step}.");
        var scale = 0;
        while (decimal.Truncate(value) != value)
        {
            value *= 10;
            scale++;
        }

        return (scale, (long)value);
    }
}

/// <summary>Fixed-point numbers from the decimal text Binance sends, with no allocation: digits, a point, digits.</summary>
internal static class Decimals
{
    /// <summary>
    ///     <paramref name="text" /> (UTF-8, e.g. <c>82884.10</c>) as an integer in units of 10<sup>−scale</sup>: digits
    ///     past the scale dropped (Binance never sends more than an instrument's precision), missing ones zero.
    /// </summary>
    public static long Parse(ReadOnlySpan<byte> text, int scale)
    {
        long value = 0;
        var i = 0;
        var negative = text.Length > 0 && text[0] == (byte)'-';
        if (negative) i++;
        for (; i < text.Length && text[i] != (byte)'.'; i++) value = value * 10 + (text[i] - (byte)'0');
        var decimals = 0;
        if (i < text.Length) // past the point
            for (i++; i < text.Length && decimals < scale; i++, decimals++)
                value = value * 10 + (text[i] - (byte)'0');
        for (; decimals < scale; decimals++) value *= 10;
        return negative ? -value : value;
    }
}
