using System.Diagnostics.Tracing;

namespace ActivityFeed.Sample;

/// <summary>
///     The app's garbage collections, while it's measured (<c>--Measure true</c>: nothing heard otherwise), at
///     <c>/ops/collections</c>: how many by generation, the time paused for them, and the longest pauses, each when it
///     began and of what generation, so a late arrival can be laid beside them. A pause runs from the runtime suspending
///     the app's threads to its restarting them, as its own GC events say (heard in process).
/// </summary>
internal sealed class Collections : EventListener
{
    private const int Kept = 8;
    private readonly object _gate = new();
    private readonly List<Pause> _longest = [];
    private int _gen0, _gen1, _gen2, _generation;
    private double _pausedMs;
    private DateTime _suspended;

    public CollectionsReport Read(bool reset)
    {
        lock (_gate)
        {
            var report = new CollectionsReport(GC.CollectionCount(0) - _gen0, GC.CollectionCount(1) - _gen1,
                GC.CollectionCount(2) - _gen2, GC.GetTotalPauseDuration().TotalMilliseconds - _pausedMs, [.. _longest]);
            if (!reset) return report;
            (_gen0, _gen1, _gen2, _pausedMs) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                GC.GetTotalPauseDuration().TotalMilliseconds);
            _longest.Clear();
            return report;
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Informational, (EventKeywords)0x1); // (its GC events)
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        lock (_gate)
        {
            switch (e.EventName)
            {
                case "GCStart_V2" when e.Payload is [_, var depth, ..]:
                    _generation = Convert.ToInt32(depth);
                    break;
                case "GCSuspendEEBegin_V1":
                    _suspended = e.TimeStamp;
                    break;
                case "GCRestartEEEnd_V1" when _suspended != default:
                    var pause = new Pause(_suspended.ToLocalTime(), _generation, (e.TimeStamp - _suspended).TotalMilliseconds);
                    _suspended = default;
                    if (_longest.Count == Kept && pause.Ms <= _longest[^1].Ms) break;
                    _longest.Add(pause);
                    _longest.Sort((a, b) => b.Ms.CompareTo(a.Ms));
                    if (_longest.Count > Kept) _longest.RemoveAt(Kept);
                    break;
            }
        }
    }
}

/// <summary>The app's collections since it was last asked to start again: by generation, paused in all, the longest.</summary>
public sealed record CollectionsReport(int Gen0, int Gen1, int Gen2, double PausedMs, Pause[] Longest);

/// <summary>A pause for a collection: when it began (this machine's time), of what generation, and how long.</summary>
public sealed record Pause(DateTime At, int Generation, double Ms);
