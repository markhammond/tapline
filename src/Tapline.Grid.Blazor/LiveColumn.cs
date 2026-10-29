using Microsoft.AspNetCore.Components;
using Tapline.Grid.Provider;

namespace Tapline.Grid.Blazor;

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

    /// <summary>
    ///     How its values are written (QuickGrid's <c>PropertyColumn.Format</c>): a .NET format string, applied as
    ///     <see cref="IFormattable.ToString(string?, IFormatProvider?)" /> would apply it on the server, in the culture current
    ///     as the grid renders — the page writes the cells to the same rules (tapline-grid.js). A number's (an integer's, a
    ///     Fixed's as the decimal it stands for, a float's or double's, a Percent's as the fraction Of is of Over), a time's
    ///     (as UTC, in the Gregorian calendar) or a Guid's. None: as <c>ToString()</c> writes it — but a Percent's
    ///     <c>"P"</c>. A format the value's type refuses is refused, with the reason.
    /// </summary>
    [Parameter]
    public string? Format { get; set; }

    /// <summary>
    ///     A number rounded to so many significant figures (half away from nought) before its <see cref="Format" />
    ///     writes it — whose places should cover them. Its tooltip, and a copy, keep the value whole.
    /// </summary>
    [Parameter]
    public int? Figures { get; set; }

    /// <summary>
    ///     The most digits a number's cell shows: one that would show more, rounded to its <see cref="Figures" />, is
    ///     written in scientific notation instead, to as many figures — 1.23457e8, 1.23456e-4 (the culture's point and
    ///     signs).
    /// </summary>
    [Parameter]
    public int? MostDigits { get; set; }

    /// <summary>
    ///     The room its cells have for their text, as a CSS length (<c>"9em"</c>, <c>"14ch"</c>, <c>"120px"</c>, a
    ///     <c>calc()</c> of them), in the cells' font (the grid's own, <c>.tg-grid</c>'s) — the cell's gutter
    ///     (<c>--tg-cell-gutter</c>) is added to it. <c>"auto"</c>: as wide as the widest of the first rows answered, then
    ///     held. None: as wide as its <see cref="Format" /> writes a value of its kind (or its label, if wider), counted in
    ///     <c>ch</c> — so the headings stand where the rows will, before any arrive.
    /// </summary>
    [Parameter]
    public string? Width { get; set; }

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

    /// <summary>Which side its cells and heading keep to (QuickGrid's <c>Align</c>; Auto: a number's end, the rest's start).</summary>
    [Parameter]
    public CellAlign Align { get; set; }

    /// <summary>
    ///     What a cell does with text wider than its column: cut at its end with an ellipsis (as CSS's
    ///     <c>text-overflow</c>), or at its start, keeping the end in view (an id's, a path's). Its tooltip says it whole.
    /// </summary>
    [Parameter]
    public CellOverflow Overflow { get; set; }

    /// <summary>A CSS class for its heading and its cells (QuickGrid's <c>Class</c>).</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary><see cref="CellKind.Enum" /> on an integer: each value's name (an enum field's names are its own).</summary>
    [Parameter]
    public IReadOnlyDictionary<long, string>? Dictionary { get; set; }

    /// <summary><see cref="CellKind.Percent" />: the number field (integer or floating point) shown as a percentage of <see cref="Over" />.</summary>
    [Parameter]
    public string? Of { get; set; }

    [Parameter]
    public string? Over { get; set; }

    /// <summary>Its cells are links: a click on one activates the row (the grid's <c>OnRowActivate</c>, naming the column).</summary>
    [Parameter]
    public bool Link { get; set; }

    /// <summary>Its cells carry their text as <c>data-value</c>, for a page's styles to go by (a side's colour).</summary>
    [Parameter]
    public bool MarkValues { get; set; }

    /// <summary>
    ///     It holds the rows' expanders: a chevron before its text on each row of the view that can open — clicked, or →
    ///     and ← on the focused row, the row opens to show its run (its descendants, depth first, from the grid's source:
    ///     <c>IGridSource.ReadRunsAsync</c>) or closes — and a run's rows indented in it by their depth. One column may.
    /// </summary>
    [Parameter]
    public bool Expander { get; set; }

    /// <summary>
    ///     The expander's column: a field of the rows saying whether a row of the view can open — a count of its descendants
    ///     (an integer, or a float), or a bool — none where it's nought. None: every row of the view can.
    /// </summary>
    [Parameter]
    public string? Expandable { get; set; }

    /// <summary>False: a run's rows leave its cells empty (a value only the view's rows have, an order's status, say).</summary>
    [Parameter]
    public bool ForChildren { get; set; } = true;

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
