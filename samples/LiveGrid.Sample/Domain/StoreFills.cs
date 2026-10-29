using Tapline.Grid;
using Tapline.Grid.Provider;
using Tapline.Grid.ChalkQL;

namespace LiveGrid.Sample.Domain;

/// <summary>The fills tab's source: how it's read, how a filter is said to it, and what it keeps.</summary>
public interface IFillSource : IGridSource<Guid, FillRow>, ISourceMemory
{
    /// <summary>The grid's filter for <paramref name="filter" />: the SQL it states.</summary>
    object? FilterOf(ViewFilter? filter);
}

/// <summary>
///     The fills as the ChalkQL blotter's fills tab serves them (grid-sources.md §6): the database's table <c>fills</c> —
///     PostgreSQL's, or ChalkQL's tables in process — and the house's stream of them (<see cref="Ledger.Fills" />), merged
///     by <see cref="StoreAndStream{TKey,TRow,TStamp}" /> on the orders' engine (<see cref="StoreOrders" /> makes it), a
///     filter's SQL pushed down to the table and kept over the tail. A fill never changes, so the stream's row and the
///     table's are the same one: the tail holds a fill only till the table has it. What a read brings is kept, changed by
///     the stream, as the orders' reads are; a session's share of the budget is its kept windows' and its filter's.
/// </summary>
internal sealed class StoreFills(StoreAndStream<Guid, FillRow, DateTime> source, ChalkStore<Guid, FillRow, DateTime> store,
    long stateBudget, int windowRows) : IFillSource, IDisposable
{
    public long Version => source.Version;

    /// <summary>The source, then the store: what reads are under way refused, saying the source has closed.</summary>
    public void Dispose()
    {
        source.Dispose();
        store.Dispose();
    }

    public object? FilterOf(ViewFilter? filter)
    {
        return filter?.Sql;
    }

    /// <summary>The provider's own: the fills' state tail and event log (StoreAndStream's), and their read arenas kept warm.</summary>
    public IReadOnlyList<(string Part, GridMemory Memory)> Memory
    {
        get
        {
            var (tail, history, _) = source.Memory;
            return [("fills' state tail", tail), ("fills' event log", history), ("fills' warm read arenas", store.Memory)];
        }
    }

    /// <summary>A session's: its view's kept windows and its filter's standing views, each beside its share of the session's budget.</summary>
    public IReadOnlyList<(string Part, GridMemory Memory)> MemoryOf(GridView? view)
    {
        var (windows, filter) = view is null ? default : source.MemoryOf(view);
        return
        [
            ("kept windows", new GridMemory(windows.Held, windowRows * StoreAndStream<Guid, FillRow, DateTime>.WindowRowBytes)),
            ("filter's views", new GridMemory(filter.Held, 2 * stateBudget))
        ];
    }

    public ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<Guid, FillRow> read, GridPage<Guid, FillRow> page,
        CancellationToken cancellationToken)
    {
        return source.ReadAsync(view, read, page, cancellationToken);
    }

    public IDisposable Watch(Action changed)
    {
        return source.Watch(changed);
    }

    public void Leave(GridView view)
    {
        source.Leave(view);
    }

    public bool NewestFirstByKey(GridView view)
    {
        return source.NewestFirstByKey(view);
    }
}
