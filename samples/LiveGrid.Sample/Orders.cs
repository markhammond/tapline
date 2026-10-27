using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Tapline.Grid;

namespace LiveGrid.Sample;

/// <summary>
///     The client orders, as the grid reads them: the <see cref="History" />'s and the <see cref="House" />'s since, each
///     read answered as a database answers a statement bound by its limit.
///     <list type="bullet">
///         <item>
///             In the order they were placed, newest or oldest first (an index: their numbers), any rows of any number of
///             orders are found by arithmetic, each read as it stands.
///         </item>
///         <item>
///             Sorted otherwise, or filtered, a read takes one pass over a copy of every order — as last taken from the
///             house, the orders changed since taken first — keeping only the rows it asks for: from a rank, the first so
///             many past it (its offset and limit); about a row, so many either side of where that sorts (a keyset).
///             Nothing is kept between reads but the copy, let go once no grid reads a sorted or filtered view.
///         </item>
///     </list>
///     Nothing estimates what a read will cost: it stops at the statement timeout, refused with the reason. It sorts and
///     filters only up to <see cref="MostSorted" /> orders (the copy's memory), and refuses beyond, saying why.
/// </summary>
internal sealed class Orders(House house, ILogger<Orders> log) : IGridSource<Guid, OrderRow>
{
    public const int MostSorted = 1_000_000;
    private const int Chunk = 16_384; // orders copied from the house in one look
    private static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowRead = TimeSpan.FromMilliseconds(25); // a read slower than this is logged

    private readonly ConditionalWeakTable<GridView, By> _by = new(); // each view's order, resolved once
    private readonly List<(long Number, OrderRow Row)> _changes = [];
    private readonly Lock _gate = new();
    private readonly Heap _first = new(true), _last = new(false); // a read's rows: from where it starts on, and before
    private readonly HashSet<GridView> _readers = []; // the sorted or filtered views read, until their grids leave them
    private bool _copied;
    private long _count, _version; // the copy's, as of the last take
    private OrderRow[] _rows = []; // every order, as last taken from the house

    public long Version => house.Version;

    /// <summary>Whether it sorts and filters: it started with no more than <see cref="MostSorted" /> orders.</summary>
    public bool Sorts => house.Initial <= MostSorted;

    public IDisposable Watch(Action changed)
    {
        return house.Watch(changed);
    }

    public ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<Guid, OrderRow> read, GridPage<Guid, OrderRow> page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var placed = Placed(view.Sort);
        if (view.Filter is null && placed != 0)
        {
            var (count, version) = house.Now();
            return new ValueTask<GridAnswer>(AsPlaced(read, page, count, version, placed < 0));
        }

        if (!Sorts)
            return new ValueTask<GridAnswer>(GridAnswer.Refused(
                $"This sample sorts and filters a copy of its orders, and started with {house.Initial:N0}: it does so up to {MostSorted:N0} (--orders 1m or fewer)."));
        return new ValueTask<GridAnswer>(Sorted(view, read, page, cancellationToken));
    }

    /// <summary>
    ///     As placed, newest first — no sort, or by time descending, filtered or not — is by id, greatest first: the ids grow
    ///     as orders are placed (<see cref="Ids.V7" />). So at the live top the grid sends no list of ids, only the orders
    ///     that changed.
    /// </summary>
    public bool NewestFirstByKey(GridView view)
    {
        return Placed(view.Sort) == 1;
    }

    public void Leave(GridView view)
    {
        lock (_gate)
        {
            _by.Remove(view);
            if (!_readers.Remove(view) || _readers.Count > 0 || !_copied) return;
            (_rows, _copied) = ([], false);
            log.LogInformation("Let go of the copy: no grid reads a sorted or filtered view");
        }
    }

    /// <summary>
    ///     A text column's values among the orders placed from <paramref name="from" /> until <paramref name="to" /> (UTC),
    ///     the most frequent first, then in order — those starting <paramref name="start" />, if it's given — at most
    ///     <paramref name="most" />, each with how many orders have it: what the blotter's filter offers to pick from.
    /// </summary>
    public List<(string Value, long Rows)> ValuesOf(string column, DateTime from, DateTime to, string? start, int most,
        CancellationToken cancellationToken)
    {
        var (count, _) = house.Now();
        long first = PlacedFrom(Micros(from), count), last = PlacedFrom(Micros(to), count);
        var names = OrderColumns.NamesOf(column);
        var key = OrderColumns.KeyOf(column) ?? OrderKey.ClOrdId;
        var named = names is null ? null : new long[names.Keys.Max() + 1];
        var texts = new Dictionary<string, long>(StringComparer.Ordinal);
        var numbers = ArrayPool<long>.Shared.Rent(Chunk);
        var rows = ArrayPool<OrderRow>.Shared.Rent(Chunk);
        try
        {
            for (var n = first; n < last; n += Chunk) // (a look at a time: the house goes on between)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var k = (int)Math.Min(Chunk, last - n);
                for (var i = 0; i < k; i++) numbers[i] = n + i;
                house.RowsOf(numbers.AsSpan(0, k), rows.AsSpan(0, k));
                for (var i = 0; i < k; i++)
                    if (named is not null)
                    {
                        named[OrderColumns.Choice(key, rows[i])]++;
                    }
                    else
                    {
                        var text = Encoding.UTF8.GetString(OrderColumns.Text(rows[i]));
                        if (start is null || text.StartsWith(start, StringComparison.Ordinal))
                            texts[text] = texts.GetValueOrDefault(text) + 1;
                    }
            }
        }
        finally
        {
            ArrayPool<OrderRow>.Shared.Return(rows);
            ArrayPool<long>.Shared.Return(numbers);
        }

        if (names is not null)
            foreach (var (value, name) in names)
                if (named![value] > 0 && (start is null || name.StartsWith(start, StringComparison.Ordinal)))
                    texts[name] = named[value];
        return [.. texts.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Take(most).Select(t => (t.Key, t.Value))];
    }

    /// <summary>The first order placed at or after <paramref name="micros" />: orders are numbered as they're placed.</summary>
    private long PlacedFrom(long micros, long count)
    {
        long lo = 0, hi = count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (house.RowOf(mid).OrderTime < micros) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static long Micros(DateTime time)
    {
        return (DateTime.SpecifyKind(time, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks) / 10;
    }

    /// <summary>1: in the order placed, newest first (no sort, or by time first); −1: oldest first; 0: sorted otherwise.</summary>
    private static int Placed(IReadOnlyList<SortKey> sort)
    {
        if (sort.Count == 0) return 1;
        return sort[0].Column == "order_time" ? sort[0].Descending ? 1 : -1 : 0;
    }

    /// <summary>As placed: newest first, rank r is order count − 1 − r; oldest first, order r.</summary>
    private GridAnswer AsPlaced(in GridRead<Guid, OrderRow> read, GridPage<Guid, OrderRow> page, long count, long version,
        bool oldest)
    {
        long at, first;
        if (read.Kind == GridReadKind.AtRank)
        {
            at = first = Math.Min(read.Rank, count);
        }
        else
        {
            var n = house.NumberOf(read.Key);
            if (n < 0 || n >= count) return new GridAnswer { Anchor = GridAnswer.NotFound, Total = count, Version = version };
            at = oldest ? n : count - 1 - n;
            first = Math.Max(0, at - read.Before);
        }

        var to = Math.Min(count, at + read.Count);
        var numbers = ArrayPool<long>.Shared.Rent((int)Math.Max(1, to - first));
        for (var r = first; r < to; r++) numbers[r - first] = oldest ? r : count - 1 - r;
        var rows = ArrayPool<OrderRow>.Shared.Rent((int)Math.Max(1, to - first));
        house.RowsOf(numbers.AsSpan(0, (int)(to - first)), rows); // (in one look at the house)
        for (var i = 0; i < to - first && page.Add(rows[i].OrderId, rows[i]); i++)
        {
        }

        ArrayPool<OrderRow>.Shared.Return(rows);
        ArrayPool<long>.Shared.Return(numbers);
        return new GridAnswer
            { FirstRank = first, Anchor = (int)(at - first), Total = count, TotalKind = TotalKind.Exact, Version = version };
    }

    private GridAnswer Sorted(GridView view, in GridRead<Guid, OrderRow> read, GridPage<Guid, OrderRow> page,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var limit = new Limit(started + (long)(StatementTimeout.TotalSeconds * Stopwatch.Frequency), cancellationToken);
        lock (_gate)
        {
            var by = _by.GetValue(view, static v => new By(v.Sort, v.Filter as OrderFilter));
            try
            {
                _readers.Add(view);
                Take(limit);
                by.Use(_rows, limit);
                var answer = read.Kind == GridReadKind.AtRank ? FromRank(by, read, page, limit) : About(by, read, page, limit);
                var took = Stopwatch.GetElapsedTime(started);
                if (took > SlowRead)
                    log.LogInformation("Read {View} {What} in {Ms:N0} ms: {Total:N0} orders in it", by, Describe(read),
                        took.TotalMilliseconds, answer.Total);
                return answer;
            }
            catch (TimeoutException)
            {
                log.LogWarning("Refused a read of {View} {What}: past the statement timeout, {Seconds:0} s", by,
                    Describe(read), StatementTimeout.TotalSeconds);
                return GridAnswer.Refused(
                    $"This read took longer than the sample's statement timeout, {StatementTimeout.TotalSeconds:0} s.");
            }
            finally
            {
                _first.Done();
                _last.Done();
            }
        }
    }

    /// <summary>From a rank: ORDER BY … LIMIT count OFFSET rank — the view's first rank + count kept as the pass goes.</summary>
    private GridAnswer FromRank(By by, in GridRead<Guid, OrderRow> read, GridPage<Guid, OrderRow> page, in Limit limit)
    {
        _first.Start(by, (int)Math.Min(read.Rank + read.Count, _count));
        long total = 0;
        for (var i = 0L; i < _count; i++)
        {
            if ((i & 0xFFFF) == 0) limit.Check();
            var n = by.Placed >= 0 ? _count - 1 - i : i; // (as the view runs, if it does: the first come first)
            if (!by.Passes(_rows[n])) continue;
            total++;
            _first.Offer(n);
        }

        var rows = _first.Sorted();
        var first = (int)Math.Min(read.Rank, rows.Length);
        foreach (var n in rows[first..])
            if (!page.Add(_rows[n].OrderId, _rows[n]))
                break;
        return new GridAnswer { FirstRank = first, Total = total, TotalKind = TotalKind.Exact, Version = _version };
    }

    /// <summary>
    ///     About a row (a keyset): so many before where it sorts, then it and so many after, and its rank — where the view
    ///     has it now, else where it sorts as last read (gone, or new); about a key alone, none if the view hasn't it.
    /// </summary>
    private GridAnswer About(By by, in GridRead<Guid, OrderRow> read, GridPage<Guid, OrderRow> page, in Limit limit)
    {
        var n = house.NumberOf(read.Key);
        var there = n >= 0 && n < _count && by.Passes(_rows[n]);
        if (!there && read.Kind == GridReadKind.AroundKey)
            return new GridAnswer { Anchor = GridAnswer.NotFound, Version = _version };
        var anchor = there ? _rows[n] : read.Row;
        _last.Start(by, read.Before);
        _first.Start(by, read.Count);
        long total = 0, rank = 0;
        for (var i = 0L; i < _count; i++)
        {
            if ((i & 0xFFFF) == 0) limit.Check();
            var m = by.Placed >= 0 ? _count - 1 - i : i;
            ref readonly var row = ref _rows[m];
            if (!by.Passes(row)) continue;
            total++;
            if (by.Compare(row, m, anchor, n) < 0)
            {
                rank++;
                _last.Offer(m);
            }
            else
            {
                _first.Offer(m);
            }
        }

        var before = _last.Sorted();
        foreach (var m in before) page.Add(_rows[m].OrderId, _rows[m]);
        foreach (var m in _first.Sorted())
            if (!page.Add(_rows[m].OrderId, _rows[m]))
                break;
        return new GridAnswer
        {
            FirstRank = rank - before.Length, Anchor = before.Length, Total = total, TotalKind = TotalKind.Exact,
            Version = _version
        };
    }

    /// <summary>
    ///     The orders changed since the last take, into the copy. The first time, when the copy was let go, or when the
    ///     house let the changes go, the copy is taken whole.
    /// </summary>
    private void Take(in Limit limit)
    {
        var (count, version, whole) = house.Changes(_changes);
        if (_rows.Length < count) Array.Resize(ref _rows, (int)(count + Math.Max(count / 8, Chunk)));
        if (whole || !_copied)
        {
            var started = Stopwatch.GetTimestamp();
            _copied = false; // (until it's whole)
            var numbers = ArrayPool<long>.Shared.Rent(Chunk);
            try
            {
                for (long from = 0; from < count; from += Chunk) // (a look at a time: the house goes on between)
                {
                    limit.Check();
                    var n = (int)Math.Min(Chunk, count - from);
                    for (var i = 0; i < n; i++) numbers[i] = from + i;
                    house.RowsOf(numbers.AsSpan(0, n), _rows.AsSpan((int)from, n));
                }
            }
            finally
            {
                ArrayPool<long>.Shared.Return(numbers);
            }

            _copied = true;
            log.LogInformation("Copied {Count:N0} orders from the house in {Ms:N0} ms", count,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        else
        {
            foreach (var (n, row) in _changes) _rows[n] = row;
        }

        (_count, _version) = (count, version);
    }

    private static string Describe(in GridRead<Guid, OrderRow> read)
    {
        return read.Kind == GridReadKind.AtRank
            ? $"from rank {read.Rank:N0}, {read.Count:N0} rows"
            : $"about a {(read.Kind == GridReadKind.AroundKey ? "key" : "row")}, {read.Before:N0} before and {read.Count:N0} from it";
    }

    /// <summary>A read's statement timeout, and the grid's cancellation: checked now and then by what takes long.</summary>
    private readonly struct Limit(long deadline, CancellationToken cancellation)
    {
        public void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() > deadline) throw new TimeoutException();
        }
    }

    /// <summary>A view over the copy, resolved once: its filter, and its order — by its columns, then newest first.</summary>
    private sealed class By(IReadOnlyList<SortKey> sort, OrderFilter? filter) : IComparer<long>
    {
        private readonly (OrderKey Key, bool Descending)[] _keys =
            [.. sort.Where(s => OrderColumns.KeyOf(s.Column) is not null).Select(s => (OrderColumns.KeyOf(s.Column)!.Value, s.Descending))];

        private int _calls;
        private Limit _limit;
        private OrderRow[] _rows = [];

        /// <summary>1: it runs as the orders were placed, newest first; −1: oldest first; 0: otherwise.</summary>
        public int Placed { get; } = Orders.Placed(sort);

        /// <summary>The copy compared, and the read's limit, checked now and then while it sorts.</summary>
        public void Use(OrderRow[] rows, in Limit limit)
        {
            (_rows, _limit) = (rows, limit);
        }

        public bool Passes(in OrderRow row)
        {
            return filter?.Passes(row) ?? true;
        }

        public int Compare(long a, long b)
        {
            if ((++_calls & 0xFFFF) == 0) _limit.Check();
            return Compare(_rows[a], a, _rows[b], b);
        }

        public int Compare(in OrderRow a, long aNumber, in OrderRow b, long bNumber)
        {
            foreach (var (key, descending) in _keys)
            {
                var c = OrderColumns.Compare(key, a, b);
                if (c != 0) return descending ? -c : c;
            }

            return bNumber.CompareTo(aNumber);
        }

        public override string ToString()
        {
            var by = sort.Count == 0 ? "as placed" : "by " + string.Join(", ", sort.Select(s => s.Column + (s.Descending ? " desc" : "")));
            return filter is null ? by : $"{by} where {filter}";
        }
    }

    /// <summary>
    ///     The best so many orders a pass offers, in a view's order: the first of them, or the last — a heap whose root
    ///     is the one to give way next. Its room is the pool's, for one read.
    /// </summary>
    private sealed class Heap(bool first)
    {
        private By _by = null!;
        private int _capacity, _count;
        private long[] _items = [];

        public void Start(By by, int capacity)
        {
            (_by, _capacity, _count) = (by, Math.Max(0, capacity), 0);
            _items = ArrayPool<long>.Shared.Rent(Math.Max(1, _capacity));
        }

        public void Offer(long n)
        {
            if (_count < _capacity) Up(_count++, n);
            else if (_count > 0 && Yields(_items[0], n) > 0) Down(n);
        }

        /// <summary>Those kept, in the view's order.</summary>
        public ReadOnlySpan<long> Sorted()
        {
            try
            {
                Array.Sort(_items, 0, _count, _by);
            }
            catch (InvalidOperationException e) when (e.InnerException is TimeoutException or OperationCanceledException)
            {
                ExceptionDispatchInfo.Throw(e.InnerException); // (the comparison's: the sort's own wrapping undone)
            }

            return _items.AsSpan(0, _count);
        }

        public void Done()
        {
            if (_items.Length > 0) ArrayPool<long>.Shared.Return(_items);
            (_items, _count) = ([], 0);
        }

        /// <summary>Above zero: <paramref name="a" /> gives way before <paramref name="b" />.</summary>
        private int Yields(long a, long b)
        {
            return first ? _by.Compare(a, b) : _by.Compare(b, a);
        }

        private void Up(int at, long n)
        {
            while (at > 0)
            {
                var parent = (at - 1) >> 1;
                if (Yields(_items[parent], n) >= 0) break;
                _items[at] = _items[parent];
                at = parent;
            }

            _items[at] = n;
        }

        private void Down(long n)
        {
            var at = 0;
            while (true)
            {
                var child = 2 * at + 1;
                if (child >= _count) break;
                if (child + 1 < _count && Yields(_items[child + 1], _items[child]) > 0) child++;
                if (Yields(_items[child], n) <= 0) break;
                _items[at] = _items[child];
                at = child;
            }

            _items[at] = n;
        }
    }
}
