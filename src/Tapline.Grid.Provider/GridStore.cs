namespace Tapline.Grid.Provider;

/// <summary>
///     What an append-only store promises, and how <see cref="StoreAndStream{TKey,TRow,TStamp}" /> keeps up with it: the
///     provider's to declare (<see cref="IGridStore{TKey,TRow,TStamp}.Policy" />), since it knows its database. Append-only
///     means: versions are only ever added, each stamped (<see cref="IGridRows{TKey,TRow,TStamp}.StampOf" />); the store can
///     say its newest stamp, its high watermark (<see cref="IGridStore{TKey,TRow,TStamp}.NewestStampAsync" />); and versions
///     land in it out of stamp order by no more than <see cref="Haircut" />. A store that can't promise that doesn't declare it: one that updates rows in
///     place would need confirmation by key, and a policy of its own. Checked once, as the source is composed.
/// </summary>
public sealed record AppendOnlyStorePolicy
{
    public static AppendOnlyStorePolicy Default { get; } = new();

    /// <summary>
    ///     How far out of stamp order versions land in the store: one stamped this long before its newest is in it. The gap
    ///     fill waits as long after the subscription; the state tail forgets a row only this far behind the store's newest
    ///     stamp (its watermark); and each recheck reads back this far from it. A minute by default, safe for an ingestion
    ///     the provider doesn't know: one that knows its store lands within seconds says so, and its tail is smaller.
    /// </summary>
    public TimeSpan Haircut { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     How far back the gap fill reaches from the subscription (its moment as a stamp:
    ///     <see cref="IGridRows{TKey,TRow,TStamp}.StampAt" />): the versions stamped since, taken as if streamed — those
    ///     sent before the subscription, which the stream couldn't, and the store didn't hold yet when the first pages were
    ///     read. Its reach covers, too, how far the host's clock and the writer's may differ.
    /// </summary>
    public TimeSpan GapFill { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How often, at most, reads have the store's recent versions read again — every version stamped from a haircut
    ///     before its newest, matching a view or not — so the merge sees each key's row as the store now holds it: a row a
    ///     view keeps because the store's last did leaves once the store has its newer version, and every key's store row
    ///     becomes known (exact counts). Asked by reads, never on a clock.
    /// </summary>
    public TimeSpan Recheck { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     The most rows the state tail holds — the versions the stream sent that the store hasn't been seen to reach. Past
    ///     it the oldest are let go first, the likeliest in the store already (the haircut is a worst case): reads go on,
    ///     those keys shown as the store has them, a version it hasn't landed yet a moment behind; once the store has passed
    ///     them by a haircut, the grids are told to read again. A memory bound, never a refusal.
    /// </summary>
    public int MaxTailRows { get; init; } = 100_000;

    /// <summary>
    ///     The furthest the event log reaches: every version the stream sent, as received, back from the newest's reception
    ///     (the host's monotonic time, so a stalled stream ages nothing). It's kept for views that need history rather than
    ///     state — an alert over a window — back the longest any of them asks for
    ///     (<see cref="StoreAndStream{TKey,TRow,TStamp}.KeepHistory" />), and not at all without one: a grid's pages read the state
    ///     tail alone. A view asking for more is refused.
    /// </summary>
    public TimeSpan History { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The most versions the event log holds: past it, the oldest go first. Nothing on a page depends on it.</summary>
    public int MaxHistoryRows { get; init; } = 100_000;

    /// <summary>
    ///     How the store's rows a read brings are kept (<see cref="StoreWindows" />): kept by the stream, by default — a run
    ///     of a view's rows, the store's merged with the tail's, changed by every change the stream sends after, so the
    ///     reads it covers ask the store nothing — or not at all, every read asking the store.
    /// </summary>
    public StoreWindows Windows { get; init; } = StoreWindows.Kept;

    /// <summary>
    ///     How long a kept window goes before a read has it read again from the store, for what the stream didn't carry (a
    ///     version it missed, a row written to the store directly) and to place it again where changes it couldn't place
    ///     moved it by a guess — unless the store says its rows are settled
    ///     (<see cref="IGridStore{TKey,TRow,TStamp}.SettledAsync" />). Off the read's path, never on a clock. Zero: never,
    ///     the stream trusted with everything.
    /// </summary>
    public TimeSpan Refresh { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The most rows the kept windows hold, every view's: past it, the least recently read go first.</summary>
    public int MaxWindowRows { get; init; } = 65_536;

    /// <summary>
    ///     The most rows one view's kept windows hold — a session's own, as each session reads a view of its own: past it, the
    ///     view's least recently read window goes, the one being read kept. Its share of a session's memory budget.
    /// </summary>
    public int MaxWindowRowsPerView { get; init; } = 65_536;

    /// <summary>Why it can't be kept to, or null.</summary>
    internal string? Broken()
    {
        if (Haircut < TimeSpan.Zero || GapFill < TimeSpan.Zero || Recheck < TimeSpan.Zero || History < TimeSpan.Zero ||
            Refresh < TimeSpan.Zero)
            return "A time can't be negative (AppendOnlyStorePolicy.Haircut, GapFill, Recheck, History, Refresh).";
        if (MaxTailRows < 1) return $"A state tail of at most {MaxTailRows} rows holds none (AppendOnlyStorePolicy.MaxTailRows).";
        if (MaxHistoryRows < 0)
            return $"An event log of at most {MaxHistoryRows} versions: a bound can't be negative (AppendOnlyStorePolicy.MaxHistoryRows).";
        if (MaxWindowRows < 0 || MaxWindowRowsPerView < 0)
            return "Kept windows of at most so many rows: a bound can't be negative (AppendOnlyStorePolicy.MaxWindowRows, MaxWindowRowsPerView).";
        return null;
    }
}

/// <summary>
///     Memory a source keeps for the grids it serves, and the most its bounds let it keep, in bytes — worked out from what it
///     holds and the sizes of its rows: its own, not the process's.
/// </summary>
public readonly record struct GridMemory(long Held, long Bound)
{
    public static GridMemory operator +(GridMemory a, GridMemory b)
    {
        return new GridMemory(a.Held + b.Held, a.Bound + b.Bound);
    }
}

/// <summary>How a view's rows read from the store are kept (<see cref="AppendOnlyStorePolicy.Windows" />).</summary>
public enum StoreWindows : byte
{
    /// <summary>
    ///     Kept by the stream: each read's run of rows changed by every change after it, the reads it covers answered from it
    ///     — the store read again for the rows past it, and on <see cref="AppendOnlyStorePolicy.Refresh" />.
    /// </summary>
    Kept = 0,

    /// <summary>Not kept: every read asks the store (an in-process store, say, whose reads cost little).</summary>
    None = 1
}

/// <summary>How a keyset read starts (<see cref="GridSeek{TKey,TRow}" />).</summary>
public enum GridSeekKind : byte
{
    /// <summary>From the view's first row, on.</summary>
    First = 0,

    /// <summary>From a row's place on: it, if the view has it, then the rows after it.</summary>
    From = 1,

    /// <summary>Back from a row's place: the rows before it, nearest first.</summary>
    Before = 2
}

/// <summary>
///     Where a keyset read starts in a view's order: its first row, or a row's place — by its values and key, where it
///     sorts, whether the view has it or not.
/// </summary>
public readonly struct GridSeek<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private GridSeek(GridSeekKind kind, in TKey key, in TRow row)
    {
        Kind = kind;
        Key = key;
        Row = row;
    }

    public GridSeekKind Kind { get; }

    /// <summary>The row's key: ties in the view's order are broken by it.</summary>
    public TKey Key { get; }

    /// <summary>The row's values: where it sorts.</summary>
    public TRow Row { get; }

    public static GridSeek<TKey, TRow> First => default;

    public static GridSeek<TKey, TRow> From(in TKey key, in TRow row)
    {
        return new GridSeek<TKey, TRow>(GridSeekKind.From, key, row);
    }

    public static GridSeek<TKey, TRow> Before(in TKey key, in TRow row)
    {
        return new GridSeek<TKey, TRow>(GridSeekKind.Before, key, row);
    }
}

/// <summary>
///     The system of record a <see cref="StoreAndStream{TKey,TRow,TStamp}" /> reads, read only: an append-only log of versions
///     (<see cref="AppendOnlyStorePolicy" />), each key shown at the newest version it holds. Its reads take the view's
///     filter and order to the database, with a keyset seek and a limit, and stop at a time limit and at the grid's
///     memory budget, <c>maxBytes</c> (<see cref="GridRead{TKey,TRow}.MaxBytes" />; none, the store's own), counted as
///     the statement takes memory, never estimated: a read past either throws <see cref="GridStoreRefusedException" />,
///     and the grid's read is refused with its message.
/// </summary>
/// <remarks>
///     A version's stamp (<c>TStamp</c>) is the implementation's measure — microseconds or nanoseconds in a long, a
///     <see cref="DateTime" />: the merge only compares stamps, and takes the policy's spans back from them
///     (<see cref="IGridRows{TKey,TRow,TStamp}.Back" />).
/// </remarks>
public interface IGridStore<TKey, TRow, TStamp>
    where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TStamp : struct, IComparable<TStamp>
{
    /// <summary>What the store promises, and how the merge keeps up with it: the provider's, since it knows its database.</summary>
    AppendOnlyStorePolicy Policy => AppendOnlyStorePolicy.Default;

    /// <summary>
    ///     Reads into <paramref name="page" /> (empty) at most <paramref name="limit" /> of the view's rows — each key at the
    ///     newest version the store holds, passing the view's filter — in its order from <paramref name="seek" /> (back
    ///     from it, nearest first, for <see cref="GridSeekKind.Before" />).
    /// </summary>
    ValueTask ReadAsync(GridView view, GridSeek<TKey, TRow> seek, int limit, uint maxBytes, GridPage<TKey, TRow> page,
        CancellationToken cancellationToken);

    /// <summary>From a rank (an OFFSET): for far jumps, approximate on a moving table.</summary>
    ValueTask ReadAtAsync(GridView view, long rank, int limit, uint maxBytes, GridPage<TKey, TRow> page,
        CancellationToken cancellationToken);

    /// <summary>
    ///     How many of the view's rows sort before a row's place — asked only when the rows read back from it reached their
    ///     limit (more lie before it; fewer, and their count is its place). An estimate is enough, one the provider can stand
    ///     behind, or a count. Null if it can't say: a held grid then can't be placed, and its read is refused.
    /// </summary>
    ValueTask<(long Rows, TotalKind Kind)?> RankAsync(GridView view, TKey key, TRow row, uint maxBytes, CancellationToken cancellationToken);

    /// <summary>
    ///     How many rows the view has — asked only when a page reached its limit (more lie past it; fewer, and the view ends
    ///     in it). An estimate is enough (<see cref="TotalKind.Estimated" />), one the provider can stand behind from its
    ///     knowledge of its source (its statistics with the filter's values, a counter, a sample) — a query planner's guess
    ///     can be orders of magnitude out — or a count (<see cref="TotalKind.Exact" />). Null if it can't say: the total is
    ///     then at least the rows seen.
    /// </summary>
    ValueTask<(long Rows, TotalKind Kind)?> SizeAsync(GridView view, uint maxBytes, CancellationToken cancellationToken);

    /// <summary>The newest stamp it holds, its high watermark (<c>SELECT MAX(stamp)</c>): how far it has caught up. None if it holds none.</summary>
    ValueTask<TStamp?> NewestStampAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Whether none of these rows — a kept window's, in its view's order — will change again: settled, the window read
    ///     again no more (<see cref="AppendOnlyStorePolicy.Refresh" />), the stream still changing it. The store's to judge,
    ///     knowing what writes to it: each row final, say, and every producer that may still write its key seen to move on
    ///     past it (its heartbeat, its newest stamp) — so a database down for maintenance, or a producer gone quiet, settles
    ///     nothing. Asked only when a refresh is due, off any read's path. False (the default), or failing, is a veto: the
    ///     window read again — a slight pessimism.
    /// </summary>
    ValueTask<bool> SettledAsync(ReadOnlyMemory<TRow> rows, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(false);
    }

    /// <summary>Every version stamped <paramref name="stamp" /> or later, in any order: the gap fill, and each recheck.</summary>
    IAsyncEnumerable<TRow> SinceAsync(TStamp stamp, CancellationToken cancellationToken);

    /// <summary>
    ///     Each key's newest version the store holds below <paramref name="below" />'s version for it (<see cref="long.MaxValue" />:
    ///     its newest), into <paramref name="page" />: a key it has none for, none. What a row was, or is.
    /// </summary>
    ValueTask EarlierAsync(ReadOnlyMemory<TKey> keys, ReadOnlyMemory<long> below, uint maxBytes, GridPage<TKey, TRow> page,
        CancellationToken cancellationToken);
}

/// <summary>
///     A store's statement stopped past one of its bounds — its time limit, or the read's memory budget — or otherwise
///     refused: the grid's read is refused, and <see cref="Exception.Message" /> says why.
/// </summary>
public sealed class GridStoreRefusedException(string reason, Exception? innerException = null) : Exception(reason, innerException);

/// <summary>A batch of versions from the stream, in the order sent.</summary>
public delegate void GridBatch<TRow>(ReadOnlySpan<TRow> rows) where TRow : unmanaged;

/// <summary>
///     The change stream a <see cref="StoreAndStream{TKey,TRow,TStamp}" /> merges with its store: every version written after
///     <see cref="Subscribe" /> returns, in batches, on the stream's thread — reliable from then on, and in the end in
///     agreement with the store, though it may run ahead of it, or behind.
/// </summary>
public interface IGridStream<TRow> where TRow : unmanaged
{
    IDisposable Subscribe(GridBatch<TRow> batch);
}

/// <summary>
///     What a row is, and a view's order as .NET applies it — from one description of the columns, as the store's ORDER
///     BY is written: a total order, the chosen columns then the key, compared as the store compares them.
/// </summary>
public interface IGridRows<TKey, TRow, TStamp>
    where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TStamp : struct, IComparable<TStamp>
{
    TKey KeyOf(in TRow row);

    /// <summary>Its version: rises within a key.</summary>
    long VersionOf(in TRow row);

    /// <summary>When it happened, as the store stamps it, in the implementation's measure.</summary>
    TStamp StampOf(in TRow row);

    /// <summary>A stamp <paramref name="span" /> before <paramref name="stamp" />: how the policy's spans are taken back (the haircut, the gap fill).</summary>
    TStamp Back(TStamp stamp, TimeSpan span);

    /// <summary>How long from <paramref name="earlier" /> to <paramref name="later" />: the store's lag, as a refusal says it.</summary>
    TimeSpan Between(TStamp earlier, TStamp later);

    /// <summary>An instant as a stamp: the subscription's, which the gap fill reaches back from.</summary>
    TStamp StampAt(DateTimeOffset time);

    /// <summary>The view's order: ties broken by key, as the store breaks them.</summary>
    IComparer<TRow> OrderOf(GridView view);

    /// <summary>Whether the view's order is its keys', greatest first (<see cref="IGridSource{TKey,TRow}.NewestFirstByKey" />).</summary>
    bool NewestFirstByKey(GridView view)
    {
        return false;
    }

}

/// <summary>A key in the state tail: the stream's newest version, and the store's row for it as last seen (none yet: unknown).</summary>
public readonly record struct GridTailRow<TKey, TRow>(TKey Key, TRow Newest, TRow Stored, bool HasStored)
    where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged;

/// <summary>
///     The views' filters applied where the store can't apply them: to the state tail. ChalkQL's (<c>Tapline.Grid.ChalkQL</c>):
///     each view's filter, the one SQL statement pushed down to the store for pages, kept as standing views over the tail,
///     so a row passes as the store would pass it. Driven from one place, the source's catch-up, one change at a time:
///     each change to the tail evaluated for every view (<see cref="EvaluateAsync" />), then published with it
///     (<see cref="Publish" />), so a read sees the tail and every view's answers as of one moment. A filter answered at
///     once completes at once, and the catch-up runs on the stream's thread.
/// </summary>
public interface IGridScopes<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    /// <summary>
    ///     A view's filter, opened over the state tail as published (<paramref name="tail" />): its answers visible from the
    ///     next <see cref="Publish" />, and evaluated with every change after. One it can't apply is refused (thrown), and
    ///     the grid's read with it.
    /// </summary>
    ValueTask<IGridScope<TKey, TRow>> OpenAsync(GridView view, ReadOnlyMemory<GridTailRow<TKey, TRow>> tail,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Changes to the state tail evaluated for every view open: keys it takes or changes (<paramref name="changed" />:
    ///     the stream's newer version, or the store's row seen) and keys it lets go (<paramref name="gone" />, as they
    ///     were). Not visible until <see cref="Publish" />. A view whose filter fails is faulted
    ///     (<see cref="IGridScope{TKey,TRow}.Fault" />), the others carry on.
    /// </summary>
    ValueTask EvaluateAsync(ReadOnlyMemory<GridTailRow<TKey, TRow>> changed, ReadOnlyMemory<GridTailRow<TKey, TRow>> gone,
        CancellationToken cancellationToken);

    /// <summary>What was evaluated, and the views opened, made visible: under the source's lock, as the tail takes the same changes.</summary>
    void Publish();
}

/// <summary>
///     A view's filter over the state tail (<see cref="IGridScopes{TKey,TRow}" />): disposed once the grid is done with the
///     view, by the source's catch-up, between evaluations.
/// </summary>
public interface IGridScope<TKey, TRow> : IAsyncDisposable where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    /// <summary>A failure that ended the filter: the view's reads are refused with it, and the next opens it again.</summary>
    Exception? Fault { get; }

    /// <summary>
    ///     Whether the key's newest version passes, and its store row as last seen did, as published: false if the scope
    ///     hasn't the key.
    /// </summary>
    bool TryMatch(in TKey key, out bool newest, out bool stored);

    /// <summary>What the view's filter keeps, beside the most it may (none, if it can't say).</summary>
    GridMemory Memory => default;
}
