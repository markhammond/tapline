using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Tapline.Grid;

/// <summary>How a <see cref="LiveGrid{TKey,TRow}" />'s rows are selected: by key, kept as they scroll away and back.</summary>
public enum GridSelection
{
    /// <summary>Not at all: a click is only a click.</summary>
    None,

    /// <summary>One row: a click (or Space) selects it alone.</summary>
    Single,

    /// <summary>Several: a click selects one alone; Ctrl or ⌘ and a click, or Space, adds or takes away one.</summary>
    Multiple
}

/// <summary>A row clicked or activated, by its key: the column, where the click was in one; the keys held.</summary>
public readonly record struct LiveGridRowEvent<TKey>(
    TKey Key,
    string? Column,
    bool CtrlKey = false,
    bool ShiftKey = false,
    bool MetaKey = false,
    bool AltKey = false);

/// <summary>A heading, for a grid's <c>HeadingTemplate</c> (its filter, a menu): the column, and how the grid sorts by it.</summary>
public sealed record LiveHeading(string Name, string Label, bool Sortable, bool Filterable, bool? Descending, int SortRank)
{
    /// <summary>Whether the grid sorts by it.</summary>
    public bool Sorted => Descending is not null;
}

/// <summary>Whether the page has all the server published for its grid (Tap.Sync), as its LiveGrid reports it.</summary>
public enum GridFreshness : byte
{
    /// <summary>Not known yet; and always, for a session with no page (<see cref="GridSession{TKey,TRow}.Status" />).</summary>
    Unknown = 0,

    /// <summary>States arriving, or the router has said the page holds all it published.</summary>
    Fresh = 1,

    /// <summary>Asked, and not answered within a second: the page may be behind.</summary>
    Behind = 2
}

/// <summary>A grid's status, as its chrome follows it.</summary>
internal interface IGridStatusSource
{
    GridStatus Status { get; }
    event Action? StatusChanged;
}

/// <summary>
///     Renders a fragment in a render of its own: after the components before it in its parent's render — a grid's
///     columns, which register as they get their parameters — so it reads them all.
/// </summary>
internal sealed class GridDeferred : ComponentBase
{
    [Parameter] public RenderFragment? Render { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.AddContent(0, Render);
    }
}

/// <summary>
///     A grid's status region: it renders itself as the status changes — not the grid around it, so headings aren't
///     rendered again for an arrival at the live top.
/// </summary>
internal sealed class GridStatusView : ComponentBase, IDisposable
{
    private IGridStatusSource? _source;

    [Parameter] public IGridStatusSource? Source { get; set; }
    [Parameter] public RenderFragment<GridStatus>? Template { get; set; }

    public void Dispose()
    {
        if (_source is not null) _source.StatusChanged -= Changed;
    }

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(_source, Source)) return;
        if (_source is not null) _source.StatusChanged -= Changed;
        _source = Source;
        if (_source is not null) _source.StatusChanged += Changed;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_source is not null && Template is not null) builder.AddContent(0, Template(_source.Status));
    }

    private void Changed()
    {
        _ = InvokeAsync(StateHasChanged);
    }
}
