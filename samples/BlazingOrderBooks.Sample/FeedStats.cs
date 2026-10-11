using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BlazingOrderBooks.Sample;

/// <summary>
///     A log-linear histogram of non-negative longs (nanoseconds, mostly): 16 buckets per power of two, exact under 32,
///     the maximum kept exactly. Recording is one array increment; percentiles are read off the buckets (a bucket's
///     lower bound: at most 6% under). One writer; readers see a slightly stale count — it's for stats.
/// </summary>
public sealed class LatencyHistogram
{
    private const int Exact = 32, SubBuckets = 16, Buckets = Exact + (62 - 5) * SubBuckets;
    private readonly long[] _counts = new long[Buckets];
    private long _count, _max;

    public long Count => Volatile.Read(ref _count);
    public long Max => Volatile.Read(ref _max);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Record(long value)
    {
        if (value < 0) value = 0;
        int index;
        if (value < Exact)
        {
            index = (int)value;
        }
        else
        {
            var e = BitOperations.Log2((ulong)value); // ≥ 5
            index = Exact + (e - 5) * SubBuckets + (int)((value >> (e - 4)) & (SubBuckets - 1));
        }

        _counts[index]++;
        if (value > _max) _max = value;
        Volatile.Write(ref _count, _count + 1);
    }

    /// <summary>The value at or below which <paramref name="p" /> (0–1) of the records fall: its bucket's lower bound.</summary>
    public long Percentile(double p)
    {
        var total = Count;
        if (total == 0) return 0;
        var target = Math.Max(1, (long)Math.Ceiling(p * total));
        long seen = 0;
        for (var i = 0; i < Buckets; i++)
        {
            seen += Volatile.Read(ref _counts[i]);
            if (seen >= target) return LowerBound(i);
        }

        return Max;
    }

    public void Reset()
    {
        Array.Clear(_counts);
        _max = 0;
        Volatile.Write(ref _count, 0);
    }

    private static long LowerBound(int index)
    {
        if (index < Exact) return index;
        int e = 5 + (index - Exact) / SubBuckets, sub = (index - Exact) % SubBuckets;
        return (1L << e) | ((long)sub << (e - 4));
    }
}

/// <summary>
///     What the feed handler measures, allocation-free, as it runs — read by the page and the bench:
///     <list type="bullet">
///         <item>
///             messages by kind, gaps and recoveries, and messages dropped on the wire;
///         </item>
///         <item>
///             <see cref="Decode" />: from a message's read off the wire to the books updated, ns; <see cref="Posted" />:
///             the
///             same through its post to Tapline; <see cref="Handoff" />: from the exchange's timestamp on it to its read
///             (the wire and the handler's wake-up);
///         </item>
///         <item>
///             acceleration: predictions of the top of the book made from prints, how many the next depth update
///             confirmed (price and size; price alone), and the lead — how much earlier the accelerated top changed than
///             the authoritative one, µs (<see cref="Lead" />);
///         </item>
///         <item>
///             bytes the handler thread allocated after its warm-up (<see cref="AllocatedSinceBaseline" />) — 0 is the
///             goal; the counter drifts by up to ~8 KB from the background GC's own bookkeeping, so it's read over many
///             messages.
///         </item>
///     </list>
/// </summary>
public sealed class FeedStats
{
    public const int WarmUpMessages = 2_000;
    public readonly LatencyHistogram Decode = new(), Posted = new(), Handoff = new(), Lead = new();
    public long AllocatedSinceBaseline, MessagesSinceBaseline;
    public long Confirmed, PriceConfirmed, Missed; // predictions, as the next depth update found them

    /// <summary>Depth updates per second the exchange is configured for, for the stats line.</summary>
    public int DepthIntervalMs;

    public long Moves, MovesConfirmed; // of them, the ones that put the top at a new price (a level swept)
    public long Trades, Depths, Snapshots, Gaps, Dropped, Resyncs;

    /// <summary>Best bid and offer updates (a venue's <c>bookTicker</c>), accelerating the book as prints do.</summary>
    public long Tops;

    /// <summary>
    ///     Prices and windows: times a ladder's window moved, levels lost for want of room beyond it (each a resync),
    ///     prices off the venue's tick grid, snapshots too wide to hold, and the venue's jumps (<c>Market:JumpEveryMs</c>).
    /// </summary>
    public long Recentres, Lost, OffGrid, TooWide, Jumps;
    public long TradesOverflowed; // prints the overlay could not keep (its ring full): the accelerated book missed them

    /// <summary>How the handler waits for the wire, for the stats line.</summary>
    public string Waiting = "";

    public long Messages => Volatile.Read(ref Trades) + Volatile.Read(ref Depths) + Volatile.Read(ref Snapshots);

    /// <summary>The stats as one line (allocates: for the page).</summary>
    public string Line()
    {
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture,
            $"Feed handler: {Volatile.Read(ref Trades):N0} prints, {Volatile.Read(ref Depths):N0} depth updates ({DepthIntervalMs} ms), {Volatile.Read(ref Snapshots):N0} snapshots");
        if (Volatile.Read(ref Gaps) + Volatile.Read(ref Dropped) + Volatile.Read(ref Resyncs) > 0)
            s.Append(CultureInfo.InvariantCulture,
                $", {Volatile.Read(ref Gaps):N0} gaps ({Volatile.Read(ref Dropped):N0} dropped on the wire), {Volatile.Read(ref Resyncs):N0} resyncs");
        if (Volatile.Read(ref Jumps) + Volatile.Read(ref Recentres) + Volatile.Read(ref Lost) +
            Volatile.Read(ref OffGrid) + Volatile.Read(ref TooWide) > 0)
            s.Append(CultureInfo.InvariantCulture,
                $" · {Volatile.Read(ref Jumps):N0} jumps, windows moved {Volatile.Read(ref Recentres):N0}×, {Volatile.Read(ref Lost):N0} levels lost, {Volatile.Read(ref OffGrid):N0} off the tick grid, {Volatile.Read(ref TooWide):N0} snapshots too wide");
        s.Append(" · decode→book ").Append(Ns(Decode.Percentile(0.5))).Append(" p50, ")
            .Append(Ns(Decode.Percentile(0.99))).Append(" p99, ").Append(Ns(Decode.Percentile(0.995)))
            .Append(" p99.5, ").Append(Ns(Decode.Max)).Append(" max")
            .Append(" · →posted ").Append(Ns(Posted.Percentile(0.995))).Append(" p99.5")
            .Append(" · hand-off ").Append(Ns(Handoff.Percentile(0.5))).Append(" p50, ")
            .Append(Ns(Handoff.Percentile(0.995))).Append(" p99.5 (").Append(Waiting).Append(')');
        var messages = Volatile.Read(ref MessagesSinceBaseline);
        if (messages > 0)
            s.Append(CultureInfo.InvariantCulture,
                $" · {Volatile.Read(ref AllocatedSinceBaseline):N0} B allocated over {messages:N0} messages");
        long confirmed = Volatile.Read(ref Confirmed),
            price = Volatile.Read(ref PriceConfirmed),
            missed = Volatile.Read(ref Missed),
            predictions = price + missed;
        long moves = Volatile.Read(ref Moves), movesConfirmed = Volatile.Read(ref MovesConfirmed);
        s.Append(" · acceleration: ");
        if (predictions == 0) s.Append("no predictions yet");
        else
            s.Append(CultureInfo.InvariantCulture,
                    $"top of book led by {Lead.Percentile(0.5) / 1000.0:F1} ms p50, {Lead.Percentile(0.9) / 1000.0:F1} ms p90; {predictions:N0} predictions, price confirmed {100.0 * price / predictions:F0}% (size too {100.0 * confirmed / predictions:F0}%); {moves:N0} price moves, {(moves == 0 ? 0 : 100.0 * movesConfirmed / moves):F0}% confirmed")
                .Append(Volatile.Read(ref TradesOverflowed) > 0
                    ? $", {Volatile.Read(ref TradesOverflowed):N0} prints not kept"
                    : "");
        return s.ToString();
    }

    public static string Ns(long ns)
    {
        return ns < 1_000 ? $"{ns:N0} ns" :
            ns < 1_000_000 ? $"{ns / 1000.0:F1} µs" : $"{ns / 1_000_000.0:F2} ms";
    }
}