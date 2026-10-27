namespace Tapline.Grid;

/// <summary>A column a grid sorts by, and which way.</summary>
public readonly record struct SortKey(string Column, bool Descending = false);

/// <summary>
///     What a grid shows: its sort, and its filter in the source's own terms (null: none). Set whole, a new one each
///     time (<see cref="GridSession{TKey,TRow}.SetView" />): the grid then reads from the top again.
/// </summary>
public sealed class GridView(IReadOnlyList<SortKey> sort, object? filter = null)
{
    /// <summary>The source's own order, and no filter.</summary>
    public static GridView Natural { get; } = new([]);

    /// <summary>The columns it sorts by, first first (none: the source's own order).</summary>
    public IReadOnlyList<SortKey> Sort { get; } = sort;

    /// <summary>The source's own filter, passed through: null for none.</summary>
    public object? Filter { get; } = filter;
}

/// <summary>How sure a total is: the page shows each differently, and scrolls by the best it has.</summary>
public enum TotalKind : byte
{
    /// <summary>Counted.</summary>
    Exact = 0,

    /// <summary>Estimated (from a query plan, a sample).</summary>
    Estimated = 1,

    /// <summary>At least so many: a count that stopped at its time limit.</summary>
    AtLeast = 2
}

/// <summary>How a read finds its rows.</summary>
public enum GridReadKind : byte
{
    /// <summary>From a rank: the first row wanted is the <see cref="GridRead{TKey,TRow}.Rank" />th of the view's order.</summary>
    AtRank = 0,

    /// <summary>
    ///     About a row read before: rows before it, then it and the rows after — where it sorts, by its row as last read,
    ///     should it be gone.
    /// </summary>
    AroundRow = 1,

    /// <summary>About a key alone, its row the source's to find: none, and the answer is empty (<see cref="GridAnswer.NotFound" />).</summary>
    AroundKey = 2
}

/// <summary>What a grid's session asks of its source: a run of rows in the view's order.</summary>
public readonly struct GridRead<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private GridRead(GridReadKind kind, long rank, in TKey key, in TRow row, int before, int count)
    {
        Kind = kind;
        Rank = rank;
        Key = key;
        Row = row;
        Before = before;
        Count = count;
    }

    public GridReadKind Kind { get; }

    /// <summary><see cref="GridReadKind.AtRank" />: the rank of the first row wanted (0: the first of the order).</summary>
    public long Rank { get; }

    /// <summary>About a row or a key: its key.</summary>
    public TKey Key { get; }

    /// <summary><see cref="GridReadKind.AroundRow" />: the row as last read, where it sorts should it be gone.</summary>
    public TRow Row { get; }

    /// <summary>About a row or a key: at most this many rows before it.</summary>
    public int Before { get; }

    /// <summary>Rows wanted from the rank, or from the row or key on (it first, if it's still there).</summary>
    public int Count { get; }

    /// <summary><paramref name="count" /> rows from the <paramref name="rank" />th on.</summary>
    public static GridRead<TKey, TRow> AtRank(long rank, int count)
    {
        return new GridRead<TKey, TRow>(GridReadKind.AtRank, rank, default, default, 0, count);
    }

    /// <summary>At most <paramref name="before" /> rows before a row read before, then <paramref name="count" /> from it on.</summary>
    public static GridRead<TKey, TRow> AroundRow(in TKey key, in TRow row, int before, int count)
    {
        return new GridRead<TKey, TRow>(GridReadKind.AroundRow, 0, key, row, before, count);
    }

    /// <summary>At most <paramref name="before" /> rows before a key's row, then <paramref name="count" /> from it on.</summary>
    public static GridRead<TKey, TRow> AroundKey(in TKey key, int before, int count)
    {
        return new GridRead<TKey, TRow>(GridReadKind.AroundKey, 0, key, default, before, count);
    }
}

/// <summary>What a source answers, beside the rows it put in the page.</summary>
public readonly record struct GridAnswer
{
    /// <summary>A refused read: why (a time limit, a budget). The rows shown stay, marked stale.</summary>
    public static GridAnswer Refused(string reason)
    {
        return new GridAnswer { Refusal = reason, Anchor = NotFound };
    }

    /// <summary><see cref="Anchor" /> for a key the source doesn't have, in the view.</summary>
    public const int NotFound = -1;

    /// <summary>The rank of the page's first row in the view's order (as sure as the total is).</summary>
    public long FirstRank { get; init; }

    /// <summary>
    ///     About a row or a key: how many of the page's rows sort before it, so the row (or the place it sorts) is the
    ///     page's <c>Anchor</c>th. <see cref="NotFound" />: a key the view doesn't have.
    /// </summary>
    public int Anchor { get; init; }

    /// <summary>How many rows the view has in all.</summary>
    public long Total { get; init; }

    public TotalKind TotalKind { get; init; }

    /// <summary>The source's version the answer is as of (<see cref="IGridSource{TKey,TRow}.Version" />, or its watermark).</summary>
    public long Version { get; init; }

    /// <summary>Why the read was refused, or null.</summary>
    public string? Refusal { get; init; }
}

/// <summary>
///     The rows a read answers, in the view's order: a buffer the grid's session owns and reuses, and its source fills,
///     at most <see cref="Capacity" /> rows. It grows to the largest page read (no further), and keeps that size.
/// </summary>
public sealed class GridPage<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private TKey[] _keys;
    private TRow[] _rows;

    internal GridPage(int capacity)
    {
        Capacity = capacity;
        _keys = new TKey[Math.Min(capacity, 64)];
        _rows = new TRow[_keys.Length];
    }

    /// <summary>Rows it takes at most.</summary>
    public int Capacity { get; }

    public int Count { get; private set; }

    public ReadOnlySpan<TKey> Keys => _keys.AsSpan(0, Count);

    public ReadOnlySpan<TRow> Rows => _rows.AsSpan(0, Count);

    /// <summary>Adds the next row, in the view's order: false, and nothing added, once the page is full.</summary>
    public bool Add(in TKey key, in TRow row)
    {
        if (Count == _keys.Length)
        {
            if (Count == Capacity) return false;
            var size = Math.Min(Capacity, 2 * Count);
            Array.Resize(ref _keys, size);
            Array.Resize(ref _rows, size);
        }

        _keys[Count] = key;
        _rows[Count++] = row;
        return true;
    }

    internal void Clear()
    {
        Count = 0;
    }
}

/// <summary>
///     Where a grid's rows come from: the adopter's. It answers runs of rows in a view's order and says when its answers
///     may have changed. It never sees Tapline: the grid's session owns cadence, cancellation and what goes on the wire.
/// </summary>
public interface IGridSource<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    /// <summary>A number that moves whenever an answer may have changed.</summary>
    long Version { get; }

    /// <summary>
    ///     Reads into <paramref name="page" /> (empty, at most its capacity) the rows <paramref name="read" /> asks for, in
    ///     <paramref name="view" />'s order. Cancelled, it stops: the grid has a newer read. A read refused for a reason
    ///     is an answer (<see cref="GridAnswer.Refused" />), not an exception.
    /// </summary>
    ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<TKey, TRow> read, GridPage<TKey, TRow> page,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Calls <paramref name="changed" /> whenever <see cref="Version" /> moves — on the source's thread, which it
    ///     doesn't hold — until the result is disposed.
    /// </summary>
    IDisposable Watch(Action changed);

    /// <summary>
    ///     The grid is done with <paramref name="view" />: a newer view it read has answered (rows, or a refusal), or the
    ///     grid closed. What the source keeps for it alone, it may let go. Once for each view the grid read, after its
    ///     last read returned: no read of it follows.
    /// </summary>
    void Leave(GridView view)
    {
    }

    /// <summary>
    ///     Whether <paramref name="view" />'s order is its rows' keys' — greatest first, as the key type compares (a
    ///     <see cref="Guid" /> as <see cref="Guid.CompareTo(Guid)" />, an integer by value) — keys that only grow as rows
    ///     arrive: monotonic UUIDv7s, a sequence. Then at the live top no list of keys goes on the wire: the page orders the
    ///     rows by their keys, and each update carries only the rows that changed, conflated by Tapline for each client
    ///     (<see cref="GridOrder.ByKey" />). Asked as the view is set, on the caller's thread.
    /// </summary>
    bool NewestFirstByKey(GridView view)
    {
        return false;
    }
}
