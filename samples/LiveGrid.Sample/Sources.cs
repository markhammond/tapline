using Tapline.Grid;
using Tapline.Grid.Provider;

namespace LiveGrid.Sample;

/// <summary>What a grid's source keeps, for the page's chip: its own, and a session's.</summary>
public interface ISourceMemory
{
    /// <summary>
    ///     What it keeps for every grid, part by part, beside the most each part's bounds allow — the provider's own, no
    ///     session's (none: it can't say).
    /// </summary>
    IReadOnlyList<(string Part, GridMemory Memory)> Memory { get; }

    /// <summary>
    ///     What it keeps for one grid's session — the session reading <paramref name="view" /> (null: one not started) — part
    ///     by part, beside the most each may: a session's budget, shared out (none: it can't say).
    /// </summary>
    IReadOnlyList<(string Part, GridMemory Memory)> MemoryOf(GridView? view);
}

/// <summary>A text column's values, for a filter's chooser and a condition's completions: what the rows hold, the most frequent first.</summary>
public interface IColumnValues
{
    /// <summary>
    ///     A text column's values among the rows from <paramref name="from" /> until <paramref name="to" /> (UTC), the most
    ///     frequent first, then in order — those starting <paramref name="start" />, if it's given — at most
    ///     <paramref name="most" />, each with how many rows have it.
    /// </summary>
    List<(string Value, long Rows)> ValuesOf(string column, DateTime from, DateTime to, string? start, int most, CancellationToken cancellationToken);
}
