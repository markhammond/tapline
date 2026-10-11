namespace BlazingOrderBooks.Sample;

/// <summary>
///     What the feed posted, by the exchange's sequence numbers: its last 16,384 level changes, and where it posted the
///     whole book. Each post gives its sequence number to the router as the upstream version, and the router sends those
///     back to the feed's views: so a view can show a record the router keeps only in part — the changes from the version
///     its subscriber was at to the one it's as of — from here. Written by the feed's loop, read by its views: under a
///     lock
///     (a write is a copy into a ring: nothing allocated).
/// </summary>
internal sealed class ExchangeJournal
{
    private const int Capacity = 16_384;
    private readonly (long Seq, Level Level, long Delta)[] _changes = new (long, Level, long)[Capacity];
    private readonly Lock _gate = new();
    private readonly Queue<long> _snapshots = new(); // the sequences the whole book was posted as of (its last 64)
    private long _first = long.MaxValue; // the first sequence it was told of (a change or a snapshot)
    private long _written; // changes written in all (the ring's next is this, mod its capacity)

    /// <summary>A message's changes, as posted: its sequence number, each level's size change.</summary>
    public void Posted(long seq, ReadOnlySpan<Level> levels, ReadOnlySpan<long> deltas)
    {
        lock (_gate)
        {
            _first = Math.Min(_first, seq);
            for (var i = 0; i < levels.Length; i++) _changes[_written++ % Capacity] = (seq, levels[i], deltas[i]);
        }
    }

    /// <summary>The whole book posted, as of <paramref name="seq" />.</summary>
    public void Snapshot(long seq)
    {
        lock (_gate)
        {
            _first = Math.Min(_first, seq);
            if (_snapshots.Count == 64) _snapshots.Dequeue();
            _snapshots.Enqueue(seq);
        }
    }

    /// <summary>
    ///     The net change to each level after <paramref name="since" /> through <paramref name="through" /> — or why not: a
    ///     whole book posted in between (they aren't changes then), or changes no longer held.
    /// </summary>
    public bool TryChanges(long since, long through, List<(Level Level, long Delta)> into, out string? why)
    {
        lock (_gate)
        {
            // Every change after this is held: the oldest held change's sequence (its message may be partly overwritten), or —
            // not wrapped yet — the one before the first.
            var complete = _written > Capacity ? _changes[_written % Capacity].Seq : _first - 1;
            if (since < complete)
            {
                why = $"its journal holds the changes after exchange seq {complete:N0} alone";
                return false;
            }

            if (_snapshots.Where(s => s > since && s <= through).DefaultIfEmpty(-1).Max() is var whole and >= 0)
            {
                why = $"the feed posted the whole book at exchange seq {whole:N0}, in between: not changes";
                return false;
            }

            var net = new Dictionary<Level, long>();
            for (var i = Math.Max(0, _written - Capacity); i < _written; i++)
            {
                var (seq, level, delta) = _changes[i % Capacity];
                if (seq > since && seq <= through) net[level] = net.GetValueOrDefault(level) + delta;
            }

            into.AddRange(net.Where(c => c.Value != 0).Select(c => (c.Key, c.Value)));
            why = null;
            return true;
        }
    }
}