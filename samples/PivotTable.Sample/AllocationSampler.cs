using System.Diagnostics.Tracing;

namespace PivotTable.Sample;

/// <summary>
///     What the process allocates, by type (Pivot:AllocationSampling): the runtime's allocation ticks — one each time a
///     heap has allocated about 100 KB more, naming the type it was allocating then — summed by type. A sample by bytes: a
///     type's share of the ticks is about its share of what's allocated. Off (the default), nothing listens.
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    private readonly Dictionary<string, (long Ticks, long Bytes)> _byType = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1); // (the GC's keyword: its allocation tick is verbose)
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is not { } name || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal) ||
            e.Payload is not { } payload || e.PayloadNames is not { } names) return;
        var (type, bytes) = ("?", 0L);
        for (var i = 0; i < names.Count; i++)
            switch (names[i])
            {
                case "TypeName":
                    type = payload[i] as string ?? "?";
                    break;
                case "AllocationAmount64":
                    bytes = Convert.ToInt64(payload[i]);
                    break;
            }

        lock (_gate)
        {
            _byType.TryGetValue(type, out var was);
            _byType[type] = (was.Ticks + 1, was.Bytes + bytes);
        }
    }

    /// <summary>The types that took the most since the last call, each its share; then forgotten.</summary>
    public string Take(int top)
    {
        KeyValuePair<string, (long Ticks, long Bytes)>[] all;
        lock (_gate)
        {
            all = [.. _byType];
            _byType.Clear();
        }

        var total = Math.Max(1, all.Sum(p => p.Value.Bytes));
        return string.Join(", ", all.OrderByDescending(p => p.Value.Bytes).Take(top)
                   .Select(p => $"{p.Key} {p.Value.Bytes * 100.0 / total:F0}%")) +
               $" ({all.Sum(p => p.Value.Ticks):N0} ticks, {total >> 20:N0} MB)";
    }
}
