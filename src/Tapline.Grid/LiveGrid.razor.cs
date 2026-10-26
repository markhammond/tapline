using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Tapline.Grid;

/// <summary>
///     A live virtual grid over a source of any number of rows: the server keeps each reader's window
///     (<see cref="GridSession{TKey,TRow}" />), Tapline carries it, and the page draws the rows (tapline-grid.js) under
///     headings, filters and status that are Blazor's. Its columns are <see cref="LiveColumn" />s in its
///     <see cref="Columns" />, each a field of <typeparamref name="TRow" />.
/// </summary>
/// <remarks>
///     Interactive server rendering only. Rendered statically, in WebAssembly or with InteractiveAuto, it's refused where
///     it would be, with the reason — as are columns that can't work, options that can't, and a client with its most
///     grids open. Prerendered, it shows its headings until the circuit starts. The page loads
///     <c>_content/Tapline/tapline-grid.css</c> and runs Tapline's Blazor transport (its outlet is the circuit's).
/// </remarks>
public partial class LiveGrid<TKey, TRow> : IAsyncDisposable, IGridStatusSource
    where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private readonly Dictionary<string, bool> _chosen = new(StringComparer.OrdinalIgnoreCase); // the column chooser's
    private readonly LiveColumnRegistry _registry = new();
    private readonly HashSet<TKey> _selected = [];
    private GridColumn[] _columns = [], _shown = [];
    private int _columnsVersion = -1;
    private GridStamp? _stamp; // (ArrivedAt, where it is in the row)
    private string? _stampSeen;
    private string? _columnsRefusal, _refusal;
    private DotNetObjectReference<LiveGrid<TKey, TRow>>? _dotnet;
    private object? _filter;
    private bool _filterSeen, _starting;
    private GridFreshness _freshness;
    private IJSObjectReference? _handle, _module;
    private ElementReference _host, _head, _body;
    private GridRequest _last = new(Guid.Empty, 0, 22, 0, 22); // the page's last request: its view's size
    private string? _pageColumns; // the columns as the page has them
    private IReadOnlyCollection<TKey>? _selectedSeen;
    private GridSession<TKey, TRow>? _session;
    private IReadOnlyList<SortKey> _sort = [];
    private IReadOnlyList<SortKey>? _sortSeen;
    private int _view;

    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    /// <summary>Where the rows come from.</summary>
    [Parameter]
    [EditorRequired]
    public IGridSource<TKey, TRow>? Source { get; set; }

    /// <summary>Every row's height, px.</summary>
    [Parameter]
    public int RowHeight { get; set; } = 30;

    /// <summary>The columns it sorts by, first first (none: the source's order). The headings change it (<c>@bind-Sort</c>).</summary>
    [Parameter]
    public IReadOnlyList<SortKey>? Sort { get; set; }

    [Parameter] public EventCallback<IReadOnlyList<SortKey>> SortChanged { get; set; }

    /// <summary>The source's own filter, passed through to it: a new one (not equal to the last) reads from the top.</summary>
    [Parameter]
    public object? Filter { get; set; }

    /// <summary>The session's bounds and cadences (<see cref="GridOptions.Default" /> if none).</summary>
    [Parameter]
    public GridOptions? Options { get; set; }

    [Parameter] public GridSelection Selection { get; set; } = GridSelection.Single;

    /// <summary>The keys selected (<c>@bind-Selected</c>), kept as their rows scroll away and back.</summary>
    [Parameter]
    public IReadOnlyCollection<TKey>? Selected { get; set; }

    [Parameter] public EventCallback<IReadOnlyCollection<TKey>> SelectedChanged { get; set; }

    /// <summary>A row clicked.</summary>
    [Parameter]
    public EventCallback<LiveGridRowEvent<TKey>> OnRowClick { get; set; }

    /// <summary>A row activated: double-clicked, Enter on it, or a click on a <see cref="LiveColumn.Link" /> column's cell.</summary>
    [Parameter]
    public EventCallback<LiveGridRowEvent<TKey>> OnRowActivate { get; set; }

    /// <summary>Its <see cref="LiveColumn" />s (<c>&lt;Columns&gt;</c>, beside <c>&lt;HeadingTemplate&gt;</c> and <c>&lt;StatusTemplate&gt;</c>).</summary>
    [Parameter]
    public RenderFragment? Columns { get; set; }

    /// <summary>Drawn in each heading after its label: a filter, a menu.</summary>
    [Parameter]
    public RenderFragment<LiveHeading>? HeadingTemplate { get; set; }

    /// <summary>Drawn below the rows as the status changes.</summary>
    [Parameter]
    public RenderFragment<GridStatus>? StatusTemplate { get; set; }

    /// <summary>A list below the rows for showing and hiding columns.</summary>
    [Parameter]
    public bool ColumnChooser { get; set; } = true;

    /// <summary>
    ///     A field of the rows holding when each arrived (a long of microseconds since 1970, or a DateTime): a row's
    ///     arrival flash runs from it wherever the row is drawn, so pages side by side, or one reloaded, flash alike.
    ///     None: from when the page hears of the row. Read as the grid starts.
    /// </summary>
    [Parameter]
    public string? ArrivedAt { get; set; }

    [Parameter] public string? AriaLabel { get; set; }

    /// <summary>Its class, beside <c>live-grid</c>: size it here or by <see cref="Style" /> (it fills its height).</summary>
    [Parameter]
    public string? Class { get; set; }

    [Parameter] public string? Style { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>Why it can't show its rows (shown in its place), or null.</summary>
    public string? Refusal => _refusal ?? _columnsRefusal;

    /// <summary>What the chrome shows: the session's last answer, and whether the page holds all it published.</summary>
    public GridStatus Status => (_session?.Status ?? new GridStatus { Live = true }) with { Freshness = _freshness };

    /// <summary>The status changed (on any thread: <c>InvokeAsync</c> to render).</summary>
    public event Action? StatusChanged;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Brings <paramref name="key" />'s row into view, wherever it is in the view's order, and focuses it: false if the
    ///     view hasn't it, or the grid isn't showing rows.
    /// </summary>
    public async Task<bool> RevealAsync(TKey key)
    {
        if (_session is null || _handle is null) return false;
        var above = _last.Count / 3; // (placed a third of the way down)
        var position = await _session.LocateAsync(key, _last.Before + above, Math.Max(1, _last.Count - above + _last.After));
        if (position is null || _handle is null) return false;
        await _handle.InvokeVoidAsync("reveal", position.Value, above, GridKeys<TKey>.ToPage(key));
        return true;
    }

    protected override void OnParametersSet()
    {
        _refusal = RenderModeRefusal() ?? Bounds();
        var view = false;
        if (!ReferenceEquals(Sort, _sortSeen))
        {
            _sortSeen = Sort;
            if (Sort is not null && !Sort.SequenceEqual(_sort))
            {
                _sort = Sort.ToArray();
                view = true;
            }
        }

        if (!_filterSeen || !Equals(Filter, _filter))
        {
            view |= _filterSeen;
            (_filter, _filterSeen) = (Filter, true);
        }

        if (!ReferenceEquals(Selected, _selectedSeen))
        {
            _selectedSeen = Selected;
            _selected.Clear();
            if (Selected is not null) _selected.UnionWith(Selected);
            _ = PushSelectionAsync();
        }

        if (view) _ = ApplyViewAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Refusal is not null)
        {
            if (_session is not null) await StopAsync(); // (its body gone from the page)
            return;
        }

        if (!RendererInfo.IsInteractive) return; // (prerendered: the circuit starts it)
        if (_session is null)
        {
            if (!_starting) await StartAsync();
            return;
        }

        var columns = JsonSerializer.Serialize(_shown);
        if (_handle is not null && columns != _pageColumns)
        {
            _pageColumns = columns;
            await _handle.InvokeVoidAsync("setColumns", (object)_shown);
        }
    }

    private string? RenderModeRefusal()
    {
        if (RendererInfo.Name == "WebAssembly" || AssignedRenderMode is InteractiveWebAssemblyRenderMode)
            return "it runs on the server, beside the session that holds its rows, and here it's rendered in the browser " +
                   "(WebAssembly): give it @rendermode InteractiveServer.";
        if (AssignedRenderMode is InteractiveAutoRenderMode)
            return "@rendermode InteractiveAuto may move it into the browser (WebAssembly), away from the session that " +
                   "holds its rows: give it InteractiveServer.";
        if (AssignedRenderMode is null && !RendererInfo.IsInteractive)
            return "it's rendered statically, so its page can't ask for rows: give it, or a component above it, " +
                   "@rendermode InteractiveServer.";
        if (RendererInfo.IsInteractive && RendererInfo.Name != "Server")
            return $"it runs in Blazor Server's interactive rendering, and here it's rendered by \"{RendererInfo.Name}\".";
        return null;
    }

    private string? Bounds()
    {
        if (Source is null) return "it has no Source.";
        if (RowHeight < 1) return $"a RowHeight of {RowHeight} px.";
        return Options?.Broken();
    }

    /// <summary>The columns, read afresh once they've changed: those shown, and why they can't work.</summary>
    private void ResolveColumns()
    {
        if (_registry.Version == _columnsVersion && ArrivedAt == _stampSeen) return;
        (_columnsVersion, _stampSeen) = (_registry.Version, ArrivedAt);
        (_columns, _columnsRefusal) = GridColumns<TRow>.Resolve(_registry.Columns);
        (_stamp, var stampRefusal) = ArrivedAt is null ? (null, null) : GridColumns<TRow>.StampOf(ArrivedAt);
        if (stampRefusal is not null) _columnsRefusal ??= $"ArrivedAt: {stampRefusal}";
        Choose();
    }

    private void Choose()
    {
        _shown = _columns.Where(Shown).ToArray();
    }

    private bool Shown(GridColumn column)
    {
        return _chosen.TryGetValue(column.Name, out var shown) ? shown : column.Source?.Visible ?? true;
    }

    private async Task StartAsync()
    {
        _starting = true;
        try
        {
            var outlet = Services.GetService<Outlet>();
            if (outlet is null)
            {
                Refuse("this circuit has no Tapline outlet: register Tapline's Blazor transport (builder.Services.AddBlazorRealtime()).");
                return;
            }

            try
            {
                _session = new GridSession<TKey, TRow>(outlet, Source!, new GridView(_sort, _filter), Options);
            }
            catch (Exception e) when (e is GridRefusedException or OutletFullException)
            {
                Refuse(e.Message);
                return;
            }

            _view = 0;
            _session.StatusChanged += Changed;
            _ = Watch(_session);
            _dotnet = DotNetObjectReference.Create(this);
            _module = await Js.InvokeAsync<IJSObjectReference>("import", "./" + Assets["_content/Tapline/tapline-grid.js"]);
            _pageColumns = JsonSerializer.Serialize(_shown);
            _handle = await _module.InvokeAsync<IJSObjectReference>("createGrid", _host, _body, _head, _dotnet, new
            {
                binding = _session.Binding,
                columns = _shown,
                rowHeight = RowHeight,
                maxRows = (Options ?? GridOptions.Default).MaxWindowRows,
                headerRows = 1,
                view = _view,
                arrivedAt = _stamp,
                selection = Selection switch
                {
                    GridSelection.None => "none", GridSelection.Multiple => "multiple", _ => "single"
                },
                selected = _selected.Select(k => GridKeys<TKey>.ToPage(k)).ToArray()
            });
        }
        catch (JSDisconnectedException)
        {
        }
        catch (TaskCanceledException)
        {
        }
        catch (JSException e) // (the page's module missing, or failing to start)
        {
            Refuse($"its page module, _content/Tapline/tapline-grid.js, failed: {e.Message.Split('\n')[0]}");
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>The session over by its own end (its subscription gone, its source failed): shown in the grid's place.</summary>
    private async Task Watch(GridSession<TKey, TRow> session)
    {
        await session.Completion;
        if (_session != session) return; // (stopped: not an end of its own)
        try
        {
            await InvokeAsync(() =>
                Refuse(session.Fault is { } fault ? $"its session failed: {fault.Message}" : "its session ended (its subscription closed)."));
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            // (its circuit gone too: nothing to show it on)
        }
    }

    private void Refuse(string reason)
    {
        _refusal = reason;
        StateHasChanged();
    }

    private async Task StopAsync()
    {
        var (session, handle, module, dotnet) = (_session, _handle, _module, _dotnet);
        (_session, _handle, _module, _dotnet, _pageColumns) = (null, null, null, null, null);
        if (session is not null) session.StatusChanged -= Changed;
        try
        {
            if (handle is not null) await handle.InvokeVoidAsync("dispose");
            if (handle is not null) await handle.DisposeAsync();
            if (module is not null) await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
        catch (TaskCanceledException)
        {
        }

        if (session is not null) await session.DisposeAsync();
        dotnet?.Dispose();
    }

    /// <summary>A new sort or filter: the session reads from the top, and the page, told the view's number, goes there.</summary>
    private async Task ApplyViewAsync()
    {
        if (_session is null) return;
        _view = _session.SetView(new GridView(_sort, _filter));
        try
        {
            if (_handle is not null) await _handle.InvokeVoidAsync("reset", _view);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private async Task PushSelectionAsync()
    {
        if (_handle is null) return;
        try
        {
            await _handle.InvokeVoidAsync("setSelection", (object)_selected.Select(k => GridKeys<TKey>.ToPage(k)).ToArray());
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private void Changed()
    {
        StatusChanged?.Invoke();
    }

    // ── the headings ──

    private int SortRank(string column)
    {
        for (var i = 0; i < _sort.Count; i++)
            if (string.Equals(_sort[i].Column, column, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        return 0;
    }

    private LiveHeading HeadingOf(GridColumn column)
    {
        var rank = SortRank(column.Name);
        return new LiveHeading(column.Name, column.Label, column.Sortable, column.Source?.Filterable ?? false,
            rank > 0 ? _sort[rank - 1].Descending : null, rank);
    }

    private string? AriaSort(GridColumn column)
    {
        return SortRank(column.Name) == 1 ? _sort[0].Descending ? "descending" : "ascending" : null;
    }

    /// <summary>
    ///     A heading clicked: sorted by it alone (ascending, then descending, then ascending); shift-clicked, it's added to
    ///     the sort (ascending, then descending, then taken out).
    /// </summary>
    private async Task SortByAsync(GridColumn column, bool add)
    {
        var rank = SortRank(column.Name);
        List<SortKey> sort;
        if (add)
        {
            sort = [.. _sort];
            if (rank == 0) sort.Add(new SortKey(column.Name));
            else if (!sort[rank - 1].Descending) sort[rank - 1] = sort[rank - 1] with { Descending = true };
            else sort.RemoveAt(rank - 1);
        }
        else
        {
            sort = [new SortKey(column.Name, rank == 1 && _sort.Count == 1 && !_sort[0].Descending)];
        }

        _sort = sort;
        await SortChanged.InvokeAsync(sort);
        await ApplyViewAsync();
    }

    /// <summary>The column chooser: a column shown or hidden (its width kept for when it's back).</summary>
    private void ShowColumn(GridColumn column, bool shown)
    {
        _chosen[column.Name] = shown;
        Choose();
    }

    // ── the page's ──

    /// <summary>The page asks for a window (tapline-grid.js, its Requests).</summary>
    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public void Request(string id, long first, int count, int before, int after, int holds, long letIn, int view)
    {
        if (_session is null || !Guid.TryParseExact(id, "N", out var guid)) return;
        _last = new GridRequest(guid, first, count, before, after, holds, letIn, view);
        _session.Request(_last);
    }

    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public void Visible(bool visible)
    {
        _session?.SetVisible(visible);
    }

    /// <summary>Whether the page holds all the server published: 'fresh', 'behind' or 'unknown'.</summary>
    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public void Fresh(string fresh)
    {
        _freshness = fresh switch { "fresh" => GridFreshness.Fresh, "behind" => GridFreshness.Behind, _ => GridFreshness.Unknown };
        Changed();
    }

    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public async Task RowClick(string key, string? column, bool ctrl, bool shift, bool meta, bool alt)
    {
        if (!GridKeys<TKey>.TryFromPage(key, out var k)) return;
        if (Selection != GridSelection.None) await SelectAsync(k, Selection == GridSelection.Multiple && (ctrl || meta));
        await OnRowClick.InvokeAsync(new LiveGridRowEvent<TKey>(k, column, ctrl, shift, meta, alt));
    }

    /// <summary>Space on the focused row: selected alone, or toggled among several.</summary>
    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public async Task RowSelect(string key)
    {
        if (Selection != GridSelection.None && GridKeys<TKey>.TryFromPage(key, out var k))
            await SelectAsync(k, Selection == GridSelection.Multiple);
    }

    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public async Task RowActivate(string key, string? column)
    {
        if (GridKeys<TKey>.TryFromPage(key, out var k)) await OnRowActivate.InvokeAsync(new LiveGridRowEvent<TKey>(k, column));
    }

    private async Task SelectAsync(TKey key, bool toggle)
    {
        if (toggle)
        {
            if (!_selected.Remove(key)) _selected.Add(key);
        }
        else
        {
            _selected.Clear();
            _selected.Add(key);
        }

        var selected = _selected.ToArray();
        _selectedSeen = selected; // (handed back as Selected: not taken as a new one)
        await SelectedChanged.InvokeAsync(selected);
        await PushSelectionAsync();
    }
}
