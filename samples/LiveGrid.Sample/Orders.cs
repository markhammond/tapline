using System.Runtime.CompilerServices;
using Tapline.Grid;

namespace LiveGrid.Sample;

internal enum OrderSide : byte
{
    Buy,
    Sell
}

internal enum OrderStatus : byte
{
    New,
    Partial,
    Filled,
    Cancelled
}

[InlineArray(22)]
internal struct ClientName
{
    private byte _0;
}

/// <summary>An order, as the grid shows it: fixed-size, its text a client's name of at most 21 bytes.</summary>
internal struct OrderRow
{
    public long Placed; // µs since 1970
    public long Number;
    public long Qty; // × 10^4
    public long Filled; // × 10^4
    public long Price; // × 10^8
    public OrderSide Side;
    public OrderStatus Status;
    public ClientName Client;
}

/// <summary>The sample's filter: the source's own model, passed through the grid (a record: equal ones are the same filter).</summary>
internal sealed record OrderFilter(OrderSide? Side = null, OrderStatus? Status = null)
{
    public bool Passes(in OrderRow row)
    {
        return (Side is null || row.Side == Side) && (Status is null || row.Status == Status);
    }
}

/// <summary>
///     Orders computed from their number — a row is a pure function of it and how often it has changed — newest first:
///     <c>rows</c> of them at the start, more arriving at the top, some filling as they go. In its own order it answers
///     any number of rows by arithmetic; sorted or filtered, it sorts a snapshot of them all, so it does that only up to
///     <see cref="MostSorted" /> rows and refuses, saying why, beyond.
/// </summary>
internal sealed class Orders : IGridSource<long, OrderRow>, IDisposable
{
    public const int MostSorted = 1_000_000;
    private static readonly string[] Places = ["Kent", "Lombard Street", "the Barbican", "Leeds", "Bath"];

    private readonly Dictionary<long, long> _arrivedAt = []; // number → when it arrived, µs since 1970 (under _gate)
    private readonly Dictionary<long, int> _changes = []; // number → times changed (under _gate)
    private readonly Lock _gate = new();
    private readonly long _initial, _started; // the orders there at the start, a second apart before it (µs since 1970)
    private readonly Timer _timer;
    private readonly List<Action> _watchers = [];
    private long _next; // orders 0 .. _next - 1
    private (string Key, long Version, long[] Order, int[] RankOf)? _snapshot;
    private long _version;

    public Orders(long rows)
    {
        (_next, _initial, _started) = (rows, rows, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(700), TimeSpan.FromMilliseconds(700));
    }

    public long Count => Volatile.Read(ref _next);

    public long Version => Volatile.Read(ref _version);

    public void Dispose()
    {
        _timer.Dispose();
    }

    public ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<long, OrderRow> read, GridPage<long, OrderRow> page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long next, version;
        lock (_gate) (next, version) = (_next, _version);
        if (view.Sort.Count == 0 && view.Filter is null) return new ValueTask<GridAnswer>(Natural(read, page, next, version));
        if (next > MostSorted)
            return new ValueTask<GridAnswer>(GridAnswer.Refused(
                $"This sample sorts and filters a snapshot of its orders, and has {next:N0}: it does so up to {MostSorted:N0}."));
        return new ValueTask<GridAnswer>(Sorted(view, read, page, next, version));
    }

    public IDisposable Watch(Action changed)
    {
        lock (_watchers) _watchers.Add(changed);
        return new Unwatch(this, changed);
    }

    /// <summary>The order <paramref name="number" /> as it stands now (null: none such).</summary>
    public OrderRow? Find(long number)
    {
        lock (_gate) return number >= 0 && number < _next ? RowOf(number) : null;
    }

    /// <summary>Newest first: rank r is order next − 1 − r.</summary>
    private GridAnswer Natural(in GridRead<long, OrderRow> read, GridPage<long, OrderRow> page, long next, long version)
    {
        long first, anchor;
        if (read.Kind == GridReadKind.AtRank)
        {
            first = Math.Min(read.Rank, next);
            anchor = 0;
        }
        else
        {
            if (read.Key < 0 || read.Key >= next)
                return new GridAnswer { Anchor = GridAnswer.NotFound, Total = next, Version = version };
            var at = next - 1 - read.Key;
            first = Math.Max(0, at - read.Before);
            anchor = at - first;
        }

        var to = Math.Min(next, first + anchor + read.Count);
        lock (_gate)
            for (var rank = first; rank < to && page.Add(next - 1 - rank, RowOf(next - 1 - rank)); rank++)
            {
            }

        return new GridAnswer
            { FirstRank = first, Anchor = (int)anchor, Total = next, TotalKind = TotalKind.Exact, Version = version };
    }

    private GridAnswer Sorted(GridView view, in GridRead<long, OrderRow> read, GridPage<long, OrderRow> page, long next,
        long version)
    {
        var (order, rankOf) = Snapshot(view, next, version);
        int at;
        switch (read.Kind)
        {
            case GridReadKind.AtRank:
                at = (int)Math.Min(read.Rank, order.Length);
                break;
            case GridReadKind.AroundKey:
                if (read.Key < 0 || read.Key >= next || rankOf[read.Key] < 0)
                    return new GridAnswer { Anchor = GridAnswer.NotFound, Total = order.Length, Version = version };
                at = rankOf[read.Key];
                break;
            default: // (about a row: where it is, or, gone from the view, where it would sort as last read)
                at = read.Key >= 0 && read.Key < next && rankOf[read.Key] >= 0
                    ? rankOf[read.Key]
                    : LowerBound(order, view, read.Row, read.Key);
                break;
        }

        var first = read.Kind == GridReadKind.AtRank ? at : Math.Max(0, at - read.Before);
        var to = Math.Min(order.Length, at + read.Count);
        lock (_gate)
            for (var rank = first; rank < to && page.Add(order[rank], RowOf(order[rank])); rank++)
            {
            }

        return new GridAnswer
            { FirstRank = first, Anchor = at - first, Total = order.Length, TotalKind = TotalKind.Exact, Version = version };
    }

    /// <summary>The view's orders in its order, as of <paramref name="version" />: sorted once, kept for the next read.</summary>
    private (long[] Order, int[] RankOf) Snapshot(GridView view, long next, long version)
    {
        var key = $"{string.Join(",", view.Sort.Select(s => s.Column + (s.Descending ? "↓" : "↑")))}|{view.Filter}";
        lock (_gate)
            if (_snapshot is { } s && s.Key == key && s.Version == version)
                return (s.Order, s.RankOf);
        var rows = new OrderRow[next];
        lock (_gate)
            for (long i = 0; i < next; i++)
                rows[i] = RowOf(i);
        var filter = view.Filter as OrderFilter;
        var ids = new List<long>((int)next);
        for (long i = next - 1; i >= 0; i--)
            if (filter?.Passes(rows[i]) ?? true)
                ids.Add(i);
        var order = ids.ToArray();
        Array.Sort(order, (a, b) => Compare(view, rows[a], a, rows[b], b));
        var rankOf = new int[next];
        Array.Fill(rankOf, -1);
        for (var r = 0; r < order.Length; r++) rankOf[order[r]] = r;
        lock (_gate) _snapshot = (key, version, order, rankOf);
        return (order, rankOf);
    }

    private int LowerBound(long[] order, GridView view, in OrderRow row, long number)
    {
        int lo = 0, hi = order.Length;
        lock (_gate)
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (Compare(view, RowOf(order[mid]), order[mid], row, number) < 0) lo = mid + 1;
                else hi = mid;
            }

        return lo;
    }

    /// <summary>By the view's sort, then newest first.</summary>
    private static int Compare(GridView view, in OrderRow a, long aNumber, in OrderRow b, long bNumber)
    {
        foreach (var (column, descending) in view.Sort)
        {
            var c = column.ToLowerInvariant() switch
            {
                "placed" => a.Placed.CompareTo(b.Placed),
                "number" => aNumber.CompareTo(bNumber),
                "qty" => a.Qty.CompareTo(b.Qty),
                "filled" => a.Filled.CompareTo(b.Filled),
                "price" => a.Price.CompareTo(b.Price),
                "side" => a.Side.CompareTo(b.Side),
                "status" => a.Status.CompareTo(b.Status),
                "client" => ((ReadOnlySpan<byte>)a.Client).SequenceCompareTo(b.Client),
                _ => 0
            };
            if (c != 0) return descending ? -c : c;
        }

        return bNumber.CompareTo(aNumber);
    }

    /// <summary>An order as it stands: a pure function of its number and how often it has changed (under _gate).</summary>
    private OrderRow RowOf(long number)
    {
        var h = Hash(number);
        var changed = _changes.GetValueOrDefault(number);
        var row = new OrderRow
        {
            Placed = number < _initial ? _started - (_initial - number) * 1_000_000 : _arrivedAt[number],
            Number = number,
            Qty = (long)(h % 1000 + 1) * 10_000,
            Price = 10_000_000_000 + (long)(h >> 20) % 5_000_000_000,
            Side = (OrderSide)((h >> 33) & 1),
            Status = (OrderStatus)Math.Min(3, (int)((h >> 37) % 2) + changed)
        };
        row.Filled = row.Qty * Math.Min(4, (int)((h >> 12) % 3) + changed) / 4;
        GridText.Write(row.Client, $"Client {(h >> 40) % 997} of {Places[h % (ulong)Places.Length]}");
        return row;
    }

    private static ulong Hash(long number)
    {
        var h = unchecked((ulong)number * 0x9E3779B97F4A7C15UL);
        h ^= h >> 31;
        h = unchecked(h * 0xD6E8FEB86659FD93UL);
        return h ^ (h >> 29);
    }

    /// <summary>One or two orders arrive at the top; one of the last thousand fills a little.</summary>
    private void Tick()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            for (var arrivals = 1 + Random.Shared.Next(2); arrivals > 0; arrivals--) _arrivedAt[_next++] = now;
            var number = _next - 1 - Random.Shared.Next((int)Math.Min(_next, 1000));
            _changes[number] = _changes.GetValueOrDefault(number) + 1;
            _version++;
        }

        Action[] watchers;
        lock (_watchers) watchers = [.. _watchers];
        foreach (var watcher in watchers) watcher();
    }

    private sealed class Unwatch(Orders orders, Action changed) : IDisposable
    {
        public void Dispose()
        {
            lock (orders._watchers) orders._watchers.Remove(changed);
        }
    }
}
