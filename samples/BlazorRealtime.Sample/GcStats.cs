using System.Diagnostics;
using System.Runtime;

namespace BlazorRealtime.Sample;

/// <summary>
///     The process's garbage collection, for the stats overlay (i): the GC in use, the allocation rate and collections
///     over
///     the last 10–20 s, time paused in collections, and the same since start, with the heap. Process-wide — every
///     circuit,
///     the stations and ASP.NET Core itself. The allocation rate is the measure that matters: a server GC's gen-0 budget
///     (up to 200 MB a heap) can hide a steady rate for minutes, then collect it all at once.
/// </summary>
public sealed class GcStats
{
    private static readonly string Mode = GCSettings.IsServerGC
        ? GC.GetConfigurationVariables().TryGetValue("GCDynamicAdaptationMode", out var datas) &&
          Convert.ToInt64(datas) == 1
            ? "server GC (DATAS)"
            : "server GC"
        : GCSettings.LatencyMode == GCLatencyMode.Interactive
            ? "workstation GC (concurrent)"
            : "workstation GC";

    private readonly Lock _lock = new();
    private readonly Sample _start = Take();
    private Sample _from, _next; // the window: from _from (10–20 s ago) to now; _next starts the following one

    public GcStats()
    {
        _from = _next = _start;
    }

    private static Sample Take()
    {
        return new Sample(Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(), GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2), GC.GetTotalPauseDuration());
    }

    /// <summary>Allocation rate (bytes/s) and collections per generation over the window, and the share of it spent paused.</summary>
    public (double BytesPerSecond, int Gen0, int Gen1, int Gen2, double PausedPercent, TimeSpan Window) Recent()
    {
        var now = Take();
        Sample from;
        lock (_lock)
        {
            if (Stopwatch.GetElapsedTime(_next.At, now.At) >= TimeSpan.FromSeconds(10)) (_from, _next) = (_next, now);
            from = _from;
        }

        var window = Stopwatch.GetElapsedTime(from.At, now.At);
        var seconds = Math.Max(window.TotalSeconds, 1e-3);
        return ((now.Allocated - from.Allocated) / seconds, now.Gen0 - from.Gen0, now.Gen1 - from.Gen1,
            now.Gen2 - from.Gen2,
            (now.Paused - from.Paused).TotalSeconds / seconds * 100, window);
    }

    public string Line()
    {
        var (rate, g0, g1, g2, paused, window) = Recent();
        var total = Take();
        var uptime = Stopwatch.GetElapsedTime(_start.At, total.At);
        var heap = GC.GetTotalMemory(false) / 1048576.0; // garbage not yet collected included
        return
            $"gc       {Mode} · {rate / 1024:F1} KiB/s allocated, collections {g0} / {g1} / {g2} (gen 0 / 1 / 2), {paused:F3}% paused, " +
            $"over the last {window.TotalSeconds:F0} s · since start ({uptime.TotalMinutes:F0} min) {(total.Allocated - _start.Allocated) / 1048576.0:F0} MiB, " +
            $"{total.Gen0 - _start.Gen0} / {total.Gen1 - _start.Gen1} / {total.Gen2 - _start.Gen2}, {(total.Paused - _start.Paused).TotalMilliseconds:F0} ms paused · heap {heap:F0} MiB";
    }

    private readonly record struct Sample(long At, long Allocated, int Gen0, int Gen1, int Gen2, TimeSpan Paused);
}