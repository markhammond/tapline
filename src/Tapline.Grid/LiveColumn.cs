using Microsoft.AspNetCore.Components;

namespace Tapline.Grid;

/// <summary>
///     A <see cref="LiveGrid{TKey,TRow}" />'s column: a field of its rows (<see cref="Name" />), how the page formats its
///     cells, and its heading. It renders nothing itself; its grid reads it. Columns stand in the order they're first
///     rendered. A column that can't work (no such field, a kind that can't read it) is refused, with the reason, in the
///     grid's place.
/// </summary>
public sealed class LiveColumn : IComponent, IDisposable
{
    [CascadingParameter] internal LiveColumnRegistry? Registry { get; set; }

    /// <summary>
    ///     What a sort by it names, to the grid's source, and the field of the grid's rows it shows unless
    ///     <see cref="Field" /> says otherwise. Unique in its grid.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public string Name { get; set; } = "";

    /// <summary>
    ///     The field of the grid's rows it shows, if not its <see cref="Name" />: as written, else ignoring case; a record
    ///     struct's property by its name.
    /// </summary>
    [Parameter]
    public string? Field { get; set; }

    /// <summary>Its heading (the <see cref="Name" /> if none).</summary>
    [Parameter]
    public string? Label { get; set; }

    /// <summary>How its cells are formatted (<see cref="CellKind.Auto" />: from the field's type).</summary>
    [Parameter]
    public CellKind Kind { get; set; }

    /// <summary><see cref="CellKind.Fixed" />: the value is scaled by 10^Scale (given on an integer, its kind is Fixed).</summary>
    [Parameter]
    public int? Scale { get; set; }

    /// <summary>Fixed, Float, Percent: places shown (rounded half away from nought; nought a dash).</summary>
    [Parameter]
    public int? Decimals { get; set; }

    /// <summary>Fixed: significant figures shown, instead of places.</summary>
    [Parameter]
    public int? Figures { get; set; }

    /// <summary>Its width in px; none: measured from the first rows answered, then held.</summary>
    [Parameter]
    public int? Width { get; set; }

    /// <summary>Stays put at the left as the grid scrolls sideways. Frozen columns come first.</summary>
    [Parameter]
    public bool Frozen { get; set; }

    /// <summary>Its heading sorts by it (click; shift-click adds it to the sort). Null: yes, unless worked out on the page.</summary>
    [Parameter]
    public bool? Sortable { get; set; }

    /// <summary>Its heading has a filter: for the grid's <c>HeadingTemplate</c>, which draws it (the grid's filter is the app's).</summary>
    [Parameter]
    public bool Filterable { get; set; }

    /// <summary>Shown at first (the column chooser shows or hides it after).</summary>
    [Parameter]
    public bool Visible { get; set; } = true;

    [Parameter]
    public CellAlign Align { get; set; }

    /// <summary><see cref="CellKind.Enum" /> on an integer: each value's name (an enum field's names are its own).</summary>
    [Parameter]
    public IReadOnlyDictionary<long, string>? Dictionary { get; set; }

    /// <summary><see cref="CellKind.Percent" />: the integer field shown as a percentage of <see cref="Over" />.</summary>
    [Parameter]
    public string? Of { get; set; }

    [Parameter]
    public string? Over { get; set; }

    /// <summary>Its cells are links: a click on one activates the row (the grid's <c>OnRowActivate</c>, naming the column).</summary>
    [Parameter]
    public bool Link { get; set; }

    public void Attach(RenderHandle renderHandle)
    {
    }

    public Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this);
        if (Registry is null)
            throw new InvalidOperationException($"LiveColumn \"{Name}\" must be in a LiveGrid's Columns.");
        Registry.Set(this);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Registry?.Remove(this);
    }
}

/// <summary>
///     A grid's columns, as they register: in the order first rendered. They change only as the grid renders (its child
///     content is theirs), so the grid reads them afresh after, when <see cref="Version" /> has moved.
/// </summary>
internal sealed class LiveColumnRegistry
{
    private readonly List<LiveColumn> _columns = [];

    public IReadOnlyList<LiveColumn> Columns => _columns;

    /// <summary>Bumped as a column comes, changes or goes.</summary>
    public int Version { get; private set; }

    public void Set(LiveColumn column)
    {
        if (!_columns.Contains(column)) _columns.Add(column);
        Version++;
    }

    public void Remove(LiveColumn column)
    {
        if (!_columns.Remove(column)) return;
        Version++;
    }
}
