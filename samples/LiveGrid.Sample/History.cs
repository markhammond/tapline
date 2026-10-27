namespace LiveGrid.Sample;

/// <summary>
///     The orders there at the start, each worked out from its number alone, the same every run and any part of it on
///     its own, spread over the days before the start (as the ChalkQL blotter's corpus). An order's one to six lots are
///     filled in up to three fills 750 ms apart, at a mid within 0.1 % of a price within 10 % of its asset's, with its
///     channel's markup; one in fifty is cancelled with some unfilled; the newest are left new, for the house to work.
/// </summary>
internal sealed class History
{
    public History(long orders, long started, int open = 2000, TimeSpan? span = null)
    {
        Orders = orders;
        Open = (int)Math.Min(open, orders);
        Spacing = Math.Max(1, (long)((span ?? TimeSpan.FromDays(5)).TotalMicroseconds / Math.Max(1, orders)));
        Origin = started - Spacing * orders;
    }

    /// <summary>Orders 0 to <c>Orders − 1</c>, oldest first.</summary>
    public long Orders { get; }

    /// <summary>The newest this many are left new.</summary>
    public int Open { get; }

    /// <summary>Microseconds between one order and the next.</summary>
    public long Spacing { get; }

    /// <summary>When order 0 was placed, µs since 1970.</summary>
    public long Origin { get; }

    /// <summary>Order <paramref name="n" /> as the start finds it: its newest version.</summary>
    public OrderRow Make(long n)
    {
        var u = (ulong)n;
        var rank = 1 + Pick(Reference.ClientWeights, Uniform(u, 1));
        var channels = Reference.ChannelsOf(rank);
        var draw = Uniform(u, 2); // (each rank's weights sum to one)
        var channel = channels[^1].Channel;
        foreach (var (c, weight) in channels)
            if ((draw -= weight) < 0)
            {
                channel = c;
                break;
            }

        var asset = Pick(Reference.AssetWeights, Uniform(u, 3));
        var (_, lot, price) = Reference.Assets[asset];
        var side = Uniform(u, 4) < 0.5 ? Side.Buy : Side.Sell;
        var lots = 1 + (int)(Uniform(u, 5) * 6);
        var open = n >= Orders - Open;
        var fills = open ? 0 : 1 + (int)(Uniform(u, 6) * 3);
        var cancelled = !open && Uniform(u, 7) < 0.02;
        var px = price * (1 + (Uniform(u, 8) - 0.5) * 0.2);
        var time = Origin + n * Spacing;
        var row = New(n, Ids.V7(time, u), time, (Client)(rank - 1), channel, (Instrument)asset, side, lots * lot);

        var fillable = lots * (cancelled ? 0.6 : 1);
        double cost = 0;
        for (var k = 1; k <= fills; k++)
        {
            var share = Math.Floor(fillable * k / fills) - Math.Floor(fillable * (k - 1) / fills);
            if (share <= 0) continue;
            var mid = Math.Round(px * (1 + (Uniform(u, 10 + (ulong)k) - 0.5) * 0.002), 6);
            var fillPrice = Math.Round(mid * (1 + (side == Side.Buy ? 1 : -1) * row.MarkupBps / 10_000), 4);
            var qty = share * lot;
            cost += qty * fillPrice;
            row.FilledQty += qty;
            row.FilledAmt = Math.Round(row.FilledAmt + Math.Round(qty * fillPrice, 2), 2);
            row.AvgPrice = Math.Round(cost / row.FilledQty, 4);
            row.Status = row.FilledQty >= row.Qty - 1e-9 ? OrderStatus.Filled : OrderStatus.Partial;
            row.EventTime = time + 750_000L * k;
            row.Version++;
        }

        if (cancelled)
        {
            row.Status = OrderStatus.Cancelled;
            row.EventTime = time + 750_000L * (fills + 1);
            row.Version++;
        }

        return row;
    }

    /// <summary>An order's number from its id, if it's one of these (its id's time places it): else −1.</summary>
    public long NumberOf(Guid id)
    {
        var micros = Ids.Milliseconds(id) * 1000;
        for (var n = Math.Max(0, (micros - Origin) / Spacing - 1); n <= (micros + 999 - Origin) / Spacing && n < Orders; n++)
            if (Ids.V7(Origin + n * Spacing, (ulong)n) == id)
                return n;
        return -1;
    }

    /// <summary>A new order, number <paramref name="n" />, placed at <paramref name="time" />.</summary>
    public static OrderRow New(long n, Guid id, long time, Client client, Channel channel, Instrument instrument, Side side,
        double qty)
    {
        var row = new OrderRow
        {
            OrderId = id, OrderTime = time, EventTime = time, Version = 1, Client = client,
            Region = Reference.Clients[(int)client].Region, Channel = channel, Instrument = instrument, Side = side,
            Qty = qty, MarkupBps = Reference.MarkupBps(channel), Status = OrderStatus.New
        };
        WriteClOrdId(ref row, Reference.Clients[(int)client].Ref, n);
        return row;
    }

    /// <summary>"CEDAR-4999999": the client's reference and the order's number, as the grid's text (GridText's layout).</summary>
    private static void WriteClOrdId(ref OrderRow row, string reference, long n)
    {
        Span<byte> text = row.ClOrdId;
        var length = 0;
        foreach (var c in reference) text[1 + length++] = (byte)c;
        text[1 + length++] = (byte)'-';
        n.TryFormat(text[(1 + length)..], out var digits);
        text[0] = (byte)(length + digits);
    }

    /// <summary>A number in [0, 1) drawn from <paramref name="n" /> and a salt: the same every time.</summary>
    private static double Uniform(ulong n, ulong salt)
    {
        return (Ids.Mix(Ids.Mix(n) ^ (salt * 0x3C6EF372FE94F82B)) >> 11) * (1.0 / (1UL << 53));
    }

    private static int Pick(double[] weights, double uniform)
    {
        var r = uniform * weights.Sum();
        for (var i = 0; i < weights.Length - 1; i++)
            if ((r -= weights[i]) < 0)
                return i;
        return weights.Length - 1;
    }
}

/// <summary>
///     Orders' ids: version 7 UUIDs (RFC 9562), the millisecond they were made, then their number's low 32 bits as a
///     counter (its method 1: monotonic within a millisecond), then bits hashed from it. Orders are made in time order, so
///     their ids grow as they're placed: the newest first is the greatest id first.
/// </summary>
internal static class Ids
{
    public static Guid V7(long micros, ulong n)
    {
        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = (ulong)(micros / 1000);
        for (var i = 0; i < 6; i++) bytes[i] = (byte)(milliseconds >> (8 * (5 - i)));
        var counter = (uint)n;
        var hashed = Mix(n ^ 0x6A09E667F3BCC908);
        bytes[6] = (byte)(0x70 | (int)(counter >> 28)); // version 7, then the counter's top 4 bits (rand_a)
        bytes[7] = (byte)(counter >> 20); // its next 8 (rand_a)
        bytes[8] = (byte)(0x80 | (int)((counter >> 14) & 0x3F)); // the variant, then its next 6 (rand_b)
        bytes[9] = (byte)(counter >> 6); // its next 8
        bytes[10] = (byte)(((counter & 0x3F) << 2) | (hashed & 0x03)); // its last 6, then hashed bits
        for (var i = 11; i < 16; i++) bytes[i] = (byte)(hashed >> (8 * (i - 9)));
        return new Guid(bytes, true);
    }

    /// <summary>A version 7 UUID's time, in ms since 1970.</summary>
    public static long Milliseconds(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, true, out _);
        long milliseconds = 0;
        for (var i = 0; i < 6; i++) milliseconds = (milliseconds << 8) | bytes[i];
        return milliseconds;
    }

    /// <summary>SplitMix64's finaliser: a hash good enough to stand in for a random draw.</summary>
    public static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EB;
        return x ^ (x >> 31);
    }
}
