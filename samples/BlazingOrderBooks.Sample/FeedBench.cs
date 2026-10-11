using System.Diagnostics;
using System.Globalization;

namespace BlazingOrderBooks.Sample;

/// <summary>
///     <c>--bench-feed [seconds] [depthIntervalMs] [jumpEveryMs]</c>: the exchange, the wire and the book builder alone, on one
///     thread, the exchange stepped a millisecond at a time without waiting (so a second of the bench is minutes of
///     trading) and each step's messages handled as they land. Prints what the handler measures — decode→book latency
///     per message, bytes allocated after warm-up, the acceleration's accuracy — without Tapline, the web host, or
///     the hand-off between threads (whose figures are the live page's). Lead times aren't meaningful here: time is
///     compressed. With <c>jumpEveryMs</c>, the price jumps 2,000 ticks that often (in the exchange's time): the
///     handler's way through instantaneous gaps, timed with the rest.
/// </summary>
internal static class FeedBench
{
    public static bool TryRun(string[] args)
    {
        var i = Array.IndexOf(args, "--bench-feed");
        if (i < 0) return false;
        var seconds = i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float,
            CultureInfo.InvariantCulture, out var s)
            ? s
            : 5;
        var interval = i + 2 < args.Length && int.TryParse(args[i + 2], out var d) ? d : 20;
        var jumpEvery = i + 3 < args.Length && int.TryParse(args[i + 3], out var j) ? j : 0;
        Run(seconds, interval, jumpEvery);
        return true;
    }

    private static unsafe void Run(double seconds, int depthIntervalMs, int jumpEveryMs)
    {
        var window = new MarketFeedOptions().WindowTicks;
        const long mid = 6_500_000;
        var stats = new FeedStats { DepthIntervalMs = depthIntervalMs, Waiting = "none: one thread" };
        using var ring = new Tapline.SpscByteRing(1 << 20);
        using var exchange = new Exchange(mid, new Random(42), ring, window, depthIntervalMs, 0,
            jumpEveryMs: jumpEveryMs, jumpTicks: 2_000);
        using var builder = new BookBuilder(window, mid, stats);
        Console.WriteLine(
            $"Feed bench: {seconds:F0} s, depth every {depthIntervalMs} ms, {(jumpEveryMs > 0 ? $"a 2,000-tick jump every {jumpEveryMs:N0} ms" : "no jumps")}, a {window:N0}-tick window, one thread; load average {LoadAverage()}");
        long messages = 0, baseline = 0, baselineAt = 0;
        var stop = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        double simulatedMs = 0;
        var ladderChanges = 0L;
        while (Stopwatch.GetTimestamp() < stop)
        {
            simulatedMs += 1;
            exchange.Step(simulatedMs, 1);
            while (true)
            {
                var message = ring.TryRead();
                if (message.IsEmpty) break;
                var read = Stopwatch.GetTimestamp();
                var outcome = builder.Handle(Wire.Address(message), Clock.Ns(read));
                var built = Stopwatch.GetTimestamp();
                ring.Release();
                stats.Decode.Record(Clock.Ns(built) - Clock.Ns(read));
                ladderChanges += builder.BookChanges.Count + builder.AcceleratedChanges.Count;
                if (outcome is BookBuilder.Outcome.Gap or BookBuilder.Outcome.TooWide) exchange.RequestSnapshot();
                messages++;
                if (messages == FeedStats.WarmUpMessages)
                {
                    baseline = GC.GetAllocatedBytesForCurrentThread();
                    baselineAt = messages;
                }
            }
        }

        var allocated = messages > baselineAt ? GC.GetAllocatedBytesForCurrentThread() - baseline : 0;
        var decode = stats.Decode;
        Console.WriteLine(
            $"{messages:N0} messages ({stats.Trades:N0} prints, {stats.Depths:N0} depth updates, {stats.Snapshots:N0} snapshots, {stats.Gaps:N0} gaps) over {simulatedMs / 1000:F0} s of trading; {ladderChanges:N0} level changes posted");
        Console.WriteLine(
            $"decode→book: p50 {FeedStats.Ns(decode.Percentile(0.5))}, p90 {FeedStats.Ns(decode.Percentile(0.9))}, p99 {FeedStats.Ns(decode.Percentile(0.99))}, p99.5 {FeedStats.Ns(decode.Percentile(0.995))}, p99.9 {FeedStats.Ns(decode.Percentile(0.999))}, max {FeedStats.Ns(decode.Max)}");
        Console.WriteLine(
            $"allocated after warm-up: {allocated:N0} B over {messages - baselineAt:N0} messages ({(messages > baselineAt ? (double)allocated / (messages - baselineAt) : 0):F3} B/message; the counter drifts by up to ~8 KB from the background GC)");
        Console.WriteLine(
            $"prices: {exchange.Jumps:N0} jumps, windows moved {stats.Recentres:N0}×, {stats.Lost:N0} levels lost, {stats.OffGrid:N0} off the grid, {stats.TooWide:N0} snapshots too wide, {stats.Resyncs + stats.Gaps:N0} resyncs");
        var predictions = stats.PriceConfirmed + stats.Missed;
        Console.WriteLine(predictions == 0
            ? "acceleration: no predictions"
            : $"acceleration: {predictions:N0} top-of-book predictions, price confirmed {100.0 * stats.PriceConfirmed / predictions:F1}% (size too {100.0 * stats.Confirmed / predictions:F1}%); {stats.Moves:N0} price moves, {(stats.Moves == 0 ? 0 : 100.0 * stats.MovesConfirmed / stats.Moves):F1}% confirmed");
    }

    private static string LoadAverage()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("uptime") { RedirectStandardOutput = true });
            var line = p!.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var at = line.IndexOf("load average", StringComparison.OrdinalIgnoreCase);
            return at < 0 ? "?" : line[(at + 13)..].Trim().TrimStart(':').Trim();
        }
        catch
        {
            return "?";
        }
    }
}