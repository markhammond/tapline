using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources;
using Tapline.Grid.Provider;

namespace Tapline.Grid.ChalkQL;

/// <summary>
///     The system of record read through ChalkQL (grid-sources.md §3): an append-only store's tables — each key's newest
///     version (<c>current</c>), and every version (<c>versions</c>) — in any source the engine has (PostgreSQL over
///     ADO.NET, an in-process table). Each view's filter (<see cref="ChalkFilter" />), order and limit go to the database
///     in its own SQL; each statement is prepared once a shape, its values bound, and stopped at the time limit — and, when
///     the read carries a memory budget, run in an arena of that budget (<see cref="ChalkArenas" />), refused past it.
/// </summary>
public sealed class ChalkStore<TKey, TRow, TStamp> : IGridStore<TKey, TRow, TStamp>, IDisposable
    where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged where TStamp : struct, IComparable<TStamp>
{
    private static readonly int[] LookupSizes = [16, 64, 256]; // (a lookup's keys padded to one of these: few plans serve every batch)

    private readonly ChalkArenas _arenas = new();
    private readonly string _current, _versions;
    private readonly ChalkEngine _engine;
    private readonly ConcurrentDictionary<string, Lazy<Task<PreparedQuery>>> _prepared = new(StringComparer.Ordinal);
    private readonly ChalkRows<TKey, TRow, TStamp> _rows;
    private readonly ChalkSizes _sizes, _ranks;
    private readonly ChalkSizer? _estimate;
    private readonly ChalkSettled<TRow>? _settled;
    private readonly TimeSpan _timeLimit;
    private long _executed;

    /// <param name="engine">An engine with the store's source among its sources.</param>
    /// <param name="rows">The row's columns: the store's tables have them, as named.</param>
    /// <param name="current">The table of each key's newest version, as SQL names it (<c>db.orders_current</c>).</param>
    /// <param name="versions">The table of every version, append-only (<c>db.order_versions</c>).</param>
    /// <param name="policy">What the store promises (<see cref="AppendOnlyStorePolicy.Default" /> if null).</param>
    /// <param name="timeLimit">The longest a statement runs before it's stopped (5 s if null).</param>
    /// <param name="estimate">
    ///     The provider's estimate of a view's size, when a page shows more lie past it — its own knowledge of its source
    ///     (the database's statistics with the filter's values, a counter it keeps, a sample), asked before
    ///     <paramref name="sizes" />. ChalkQL's plans don't make one: their estimates can't see a filter's values.
    /// </param>
    /// <param name="sizes">
    ///     How a view's size is told otherwise: not at all (the default — the total at least the rows seen, as a count can
    ///     be dear), or a count.
    /// </param>
    /// <param name="ranks">
    ///     How a row's place is told, when the rows read back from it show more lie before it: a count (the default: a held
    ///     grid needs its place), or not at all (a held read refused).
    /// </param>
    /// <param name="settled">
    ///     Whether a kept window's rows will change no more (<see cref="IGridStore{TKey,TRow,TStamp}.SettledAsync" />): the
    ///     provider's judgment of what writes to its store. None: never, each window read again on its refresh.
    /// </param>
    public ChalkStore(ChalkEngine engine, ChalkRows<TKey, TRow, TStamp> rows, string current, string versions,
        AppendOnlyStorePolicy? policy = null, TimeSpan? timeLimit = null, ChalkSizer? estimate = null, ChalkSizes sizes = ChalkSizes.None,
        ChalkSizes ranks = ChalkSizes.Count, ChalkSettled<TRow>? settled = null)
    {
        (_engine, _rows, _current, _versions, _estimate, _sizes, _ranks, _settled) = (engine, rows, current, versions, estimate, sizes, ranks, settled);
        Policy = policy ?? AppendOnlyStorePolicy.Default;
        _timeLimit = timeLimit ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>What its reads' arenas keep warm between reads, beside the most they may (<see cref="ChalkArenas.Memory" />).</summary>
    public GridMemory Memory => _arenas.Memory;

    /// <summary>The statements run.</summary>
    public long Executed => Interlocked.Read(ref _executed);

    public AppendOnlyStorePolicy Policy { get; }

    public async ValueTask ReadAsync(GridView view, GridSeek<TKey, TRow> seek, int limit, uint maxBytes, GridPage<TKey, TRow> page,
        CancellationToken cancellationToken)
    {
        var (filter, order) = (ChalkFilter.Of(view), _rows.Order(view));
        var values = filter.Values();
        string sql;
        if (seek.Kind == GridSeekKind.First)
        {
            sql = $"SELECT {_rows.Select} FROM {_current} WHERE ({filter.Where}) ORDER BY {order.By} LIMIT {limit}";
        }
        else
        {
            order.Bind(seek.Row, values);
            sql = seek.Kind == GridSeekKind.From
                ? $"SELECT {_rows.Select} FROM {_current} WHERE ({filter.Where}) AND ({order.From}) ORDER BY {order.By} LIMIT {limit}"
                : $"SELECT {_rows.Select} FROM {_current} WHERE ({filter.Where}) AND ({order.Before}) ORDER BY {order.ReversedBy} LIMIT {limit}";
        }

        await RowsAsync(sql, values, row => page.Add(_rows.KeyOf(row), row), "a page", maxBytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReadAtAsync(GridView view, long rank, int limit, uint maxBytes, GridPage<TKey, TRow> page,
        CancellationToken cancellationToken)
    {
        var (filter, order) = (ChalkFilter.Of(view), _rows.Order(view));
        var values = filter.Values();
        values["grid_offset"] = rank;
        await RowsAsync($"SELECT {_rows.Select} FROM {_current} WHERE ({filter.Where}) ORDER BY {order.By} LIMIT {limit} OFFSET @grid_offset",
            values, row => page.Add(_rows.KeyOf(row), row), "a jump", maxBytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<(long Rows, TotalKind Kind)?> RankAsync(GridView view, TKey key, TRow row, uint maxBytes,
        CancellationToken cancellationToken)
    {
        if (_ranks != ChalkSizes.Count) return null;
        var (filter, order) = (ChalkFilter.Of(view), _rows.Order(view));
        var where = $"({filter.Where}) AND ({order.Before})";
        var values = filter.Values();
        order.Bind(row, values);
        return (await CountAsync($"SELECT COUNT(*) AS n FROM {_current} WHERE {where}", values, "a rank", maxBytes, cancellationToken)
            .ConfigureAwait(false), TotalKind.Exact);
    }

    public async ValueTask<(long Rows, TotalKind Kind)?> SizeAsync(GridView view, uint maxBytes, CancellationToken cancellationToken)
    {
        if (_estimate is not null && await _estimate(view, cancellationToken).ConfigureAwait(false) is { } estimated) return estimated;
        if (_sizes != ChalkSizes.Count) return null;
        var filter = ChalkFilter.Of(view);
        return (await CountAsync($"SELECT COUNT(*) AS n FROM {_current} WHERE ({filter.Where})", filter.Values(), "a count",
            maxBytes, cancellationToken).ConfigureAwait(false), TotalKind.Exact);
    }

    public ValueTask<bool> SettledAsync(ReadOnlyMemory<TRow> rows, CancellationToken cancellationToken)
    {
        return _settled is { } settled ? settled(rows, cancellationToken) : ValueTask.FromResult(false);
    }

    public async ValueTask<TStamp?> NewestStampAsync(CancellationToken cancellationToken)
    {
        TStamp? newest = null;
        await RunAsync($"SELECT MAX({_rows.StampColumn}) AS s FROM {_versions}", new Dictionary<string, object?>(), "the newest stamp", 0,
            batch =>
            {
                if (batch.Length > 0 && !batch.Column(0).IsNull(0)) newest = _rows.StampFrom(batch.Column(0), 0);
            }, cancellationToken).ConfigureAwait(false);
        return newest;
    }

    public async IAsyncEnumerable<TRow> SinceAsync(TStamp stamp, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?> { ["grid_since"] = _rows.Parameter(stamp) };
        var rows = new List<TRow>();
        await RowsAsync($"SELECT {_rows.Select} FROM {_versions} WHERE {_rows.StampColumn} >= @grid_since", values, rows.Add, "the gap fill",
            0, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows) yield return row;
    }

    public async ValueTask EarlierAsync(ReadOnlyMemory<TKey> keys, ReadOnlyMemory<long> below, uint maxBytes, GridPage<TKey, TRow> page,
        CancellationToken cancellationToken)
    {
        var newest = true;
        foreach (var bound in below.Span) newest &= bound == long.MaxValue;
        var found = new List<TRow>();
        for (var start = 0; start < keys.Length; start += LookupSizes[^1])
        {
            var count = Math.Min(LookupSizes[^1], keys.Length - start);
            var size = LookupSizes.First(s => s >= count);
            var batch = new TKey[size]; // (padded with its first key, which changes nothing an IN finds)
            for (var i = 0; i < size; i++) batch[i] = keys.Span[start + (i < count ? i : 0)];
            var values = new Dictionary<string, object?> { ["grid_keys"] = batch };
            await RowsAsync($"SELECT {_rows.Select} FROM {(newest ? _current : _versions)} WHERE {_rows.KeyColumn} IN @grid_keys",
                values, found.Add, "a lookup", maxBytes, cancellationToken).ConfigureAwait(false);
        }

        if (newest)
        {
            foreach (var row in found) page.Add(_rows.KeyOf(row), row);
            return;
        }

        // Each key's newest version below its bound, of those its versions hold.
        var bounds = new Dictionary<TKey, long>();
        for (var i = 0; i < keys.Length; i++) bounds[keys.Span[i]] = below.Span[i];
        var best = new Dictionary<TKey, TRow>();
        foreach (var row in found)
        {
            var key = _rows.KeyOf(row);
            var version = _rows.VersionOf(row);
            if (version < bounds[key] && (!best.TryGetValue(key, out var was) || _rows.VersionOf(was) < version)) best[key] = row;
        }

        foreach (var (key, row) in best) page.Add(key, row);
    }

    public void Dispose()
    {
        _arenas.Dispose();
    }

    private async ValueTask RowsAsync(string sql, Dictionary<string, object?> values, Action<TRow> each, string what, uint maxBytes,
        CancellationToken cancellationToken)
    {
        await RunAsync(sql, values, what, maxBytes, batch =>
        {
            for (var i = 0; i < batch.Length; i++) each(_rows.Read(batch, i));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> CountAsync(string sql, Dictionary<string, object?> values, string what, uint maxBytes,
        CancellationToken cancellationToken)
    {
        long count = 0;
        await RunAsync(sql, values, what, maxBytes, batch =>
        {
            if (batch.Length > 0) count = ArrowValues.Int64(batch.Column(0), 0);
        }, cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>
    ///     A statement, prepared once its shape, run under the time limit and, given a budget (more than none), in an arena of
    ///     it: past either, stopped and refused (<see cref="GridStoreRefusedException" />), saying which.
    /// </summary>
    private async ValueTask RunAsync(string sql, Dictionary<string, object?> values, string what, uint maxBytes, Action<RecordBatch> each,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executed);
        var query = await PrepareAsync(sql, cancellationToken).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeLimit);
        var arena = maxBytes > 0 ? _arenas.Rent(maxBytes) : null; // (none: the engine's own)
        try
        {
            await using var execution = await _engine.ExecuteAsync(query, values, arena, limit.Token).ConfigureAwait(false);
            await foreach (var batch in execution.Batches.WithCancellation(limit.Token).ConfigureAwait(false))
                using (batch)
                {
                    each(batch);
                }
        }
        catch (OperationCanceledException stopped) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GridStoreRefusedException(
                $"{Capital(what)} took longer than the store's {_timeLimit.TotalSeconds:0.#} s limit, and was stopped.", stopped);
        }
        catch (Exception failed) when (BudgetOf(failed) is { } budget)
        {
            throw new GridStoreRefusedException($"{Capital(what)} needed more memory than the read's budget, {Size(budget)}, and was stopped.",
                failed);
        }
        finally
        {
            if (arena is not null) _arenas.Return(arena); // (its execution and batches disposed)
        }
    }

    /// <summary>The budget an execution ran out of, if that's why it failed (however ChalkQL wrapped it).</summary>
    private static long? BudgetOf(Exception? failure)
    {
        for (; failure is not null; failure = failure.InnerException)
            if (failure is ArenaBudgetExceededException exceeded)
                return exceeded.MaxBytes;
        return null;
    }

    private static string Capital(string what)
    {
        return char.ToUpperInvariant(what[0]) + what[1..];
    }

    private static string Size(long bytes)
    {
        return bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GiB"
            : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MiB"
            : $"{bytes / 1024.0:0.#} KiB";
    }

    private Task<PreparedQuery> PrepareAsync(string sql, CancellationToken cancellationToken)
    {
        var lazy = _prepared.GetOrAdd(sql, text => new Lazy<Task<PreparedQuery>>(() => _engine.PrepareAsync(text, ct: CancellationToken.None).AsTask()));
        if (lazy.Value.IsFaulted) _prepared.TryRemove(new KeyValuePair<string, Lazy<Task<PreparedQuery>>>(sql, lazy)); // (prepared again next time)
        return lazy.Value.WaitAsync(cancellationToken);
    }
}

/// <summary>How a <see cref="ChalkStore{TKey,TRow,TStamp}" /> tells a view's size, or a row's place, when a page can't.</summary>
public enum ChalkSizes : byte
{
    /// <summary>Not told: a total is at least the rows seen; a held read, which needs a place, is refused.</summary>
    None,

    /// <summary>A count: exact, as dear as the database makes it (bounded by the time limit and the read's budget).</summary>
    Count
}

/// <summary>
///     A provider's estimate of a view's size (its filter <see cref="ChalkFilter" />, its values bound): from its own
///     knowledge of its source — the database's statistics with those values (PostgreSQL's <c>EXPLAIN</c>), a counter
///     it keeps, a sample — usually <see cref="TotalKind.Estimated" />. Null if it can't say.
/// </summary>
public delegate ValueTask<(long Rows, TotalKind Kind)?> ChalkSizer(GridView view, CancellationToken cancellationToken);

/// <summary>
///     A provider's judgment that a kept window's rows will change no more (<see cref="IGridStore{TKey,TRow,TStamp}.SettledAsync" />):
///     from what it knows of what writes to its store — which rows are final, which producers may still write their keys,
///     and whether each has been seen to move on.
/// </summary>
public delegate ValueTask<bool> ChalkSettled<TRow>(ReadOnlyMemory<TRow> rows, CancellationToken cancellationToken) where TRow : unmanaged;
