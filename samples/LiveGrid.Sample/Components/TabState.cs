using Tapline.Grid;
using Tapline.Grid.Provider;

namespace LiveGrid.Sample.Components;

/// <summary>
///     A tab's own, on the page: its sort, filters, columns, the column whose filter is open, the rows selected, and where
///     it was held — kept while the tab is out of sight, its grid let go.
/// </summary>
public sealed class TabState(Tab tab)
{
    public Tab Tab { get; } = tab;

    public IReadOnlyList<SortKey> Sort { get; set; } = [];

    public ViewFilter? Filter { get; set; }

    /// <summary>The column whose filter is open.</summary>
    public Column? Editing { get; set; }

    public IReadOnlyCollection<Guid> Selected { get; set; } = [];

    /// <summary>Held away from the live top as it went out of sight: the row it showed first.</summary>
    public Guid? HeldAt { get; set; }

    /// <summary>The columns hidden: as its grid's chooser left them as the tab went out of sight, or a link said.</summary>
    public IReadOnlySet<string> Hidden { get; set; } = tab.HiddenAtFirst;

    /// <summary>Whether its grid shows a column at first.</summary>
    public bool Shows(string column)
    {
        return !Hidden.Contains(column);
    }
}

/// <summary>A row opened (activated): what it is, its id, and a line saying what it holds — in the domain's words.</summary>
public readonly record struct Opened(string What, string Id, string Description);
