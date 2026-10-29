using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Tapline.Grid.Provider;
using SortKey = Tapline.Grid.Provider.SortKey;

namespace Tapline.Grid.Blazor;

/// <summary>
///     A live virtual grid over a source of any number of rows: the server keeps each reader's window
///     (<see cref="GridSession{TKey,TRow}" />), Tapline carries it and its status, and the page draws the rows and the
///     status line (tapline-grid.js) under headings and filters that are Blazor's. Its columns are
///     <see cref="LiveColumn" />s in its <see cref="Columns" />, each a field of <typeparamref name="TRow" />.
/// </summary>
/// <remarks>
///     Interactive server rendering only. Rendered statically, in WebAssembly or with InteractiveAuto, it's refused where
///     it would be, with the reason — as are columns that can't work, options that can't, and a client with its most
///     grids open. Prerendered, it shows its headings until the circuit starts. The page loads
///     <c>_content/Tapline/tapline-grid.css</c> and runs Tapline's Blazor transport (its outlet is the circuit's).
/// </remarks>
public partial class LiveGrid<TKey, TRow> : IAsyncDisposable, IGridStatusSource, ILiveGridColumns
    where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private const int TellEveryMs = 250; // StatusChanged told at most this often

    private readonly Dictionary<string, bool> _chosen = new(StringComparer.OrdinalIgnoreCase); // the column chooser's
    private LiveColumnChoice[]? _choices; // (as a chooser lists them: made again once they change)
    private int _choicesVersion, _choicesTold; // (changed, and told: ColumnsChanged)
    private readonly LiveColumnRegistry _registry = new();
    private readonly Dictionary<string, Func<MouseEventArgs, Task>> _sortClicks = new(StringComparer.OrdinalIgnoreCase);
    private Action? _toggleAllClick;
    private readonly HashSet<TKey> _selected = [];
    private readonly Lock _told = new(); // (StatusChanged's telling: _toldAt, _tellDue)
    private GridColumn[] _columns = [], _shown = [];
    private int _columnsVersion = -1;
    private CultureInfo? _culture; // the culture the columns' formats were resolved in: the page writes cells in it
    private GridStamp? _stamp, _changeStamp; // (ArrivedAt and ChangedAt, where each is in the row)
    private string? _stampSeen, _changeStampSeen;
    private bool _fromKeySeen;
    private TimeSpan? _lag; // the page's, as it last said
    private string? _columnsRefusal, _refusal;
    private DotNetObjectReference<LiveGrid<TKey, TRow>>? _dotnet;
    private object? _filter;
    private bool _disposed, _filterSeen, _starting;
    private GridFreshness _freshness;
    private IJSObjectReference? _handle, _module;
    private ElementReference _host, _head, _body, _statusLine;
    private long _toldAt = long.MinValue / 2; // StatusChanged last told (ms, Environment.TickCount64)
    private bool _tellDue; // and a telling due (_tell set to tell it)
    private Timer? _tell;
    private GridRequest _last = new(Guid.Empty, 0, 22, 0, 22); // the page's last request: its view's size
    private string? _pageColumns; // the columns as the page has them
    private IReadOnlyCollection<TKey>? _selectedSeen;
    private (bool Click, bool Activate) _rowEvents; // the row events the page sends: those handled here
    private GridSession<TKey, TRow>? _session;
    private bool _startedAt; // (StartAt revealed: a restart, after a reconnect, starts where the reader is)
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

    /// <summary>
    ///     The most rows the page holds either side of those in view (QuickGrid's term): fetched once and kept as the reader
    ///     scrolls, so a fling, or a turn back the way they came, finds its rows already there. It holds as many as its link
    ///     carries in time (<see cref="GridOptions.OverscanFill" />), as the session measures it: none at first, so the first
    ///     rows aren't kept waiting for rows no one is looking at, then a view at the least, and more as the link shows it
    ///     carries them — and more while the reader rests and the link has room, up to this
    ///     (<see cref="GridOptions.OverscanClimb" />). A window's
    ///     publish re-sends its keys (but at the live top of a source ordered by its keys), so they cost bytes as the window
    ///     changes, and none while it rests. A window is at most <see cref="GridOptions.MaxWindowRows" />, the side behind
    ///     giving way first. Read as the grid starts.
    /// </summary>
    [Parameter]
    public int OverscanCount { get; set; } = 150;

    /// <summary>
    ///     How long a jump to the live top (<see cref="ToTopAsync" />, the Home key) whose rows aren't on the page yet keeps
    ///     the rows in view while it asks for them: they come in that time, the grid goes straight to them; not, it goes
    ///     then, their places drawn as placeholders till they come. Zero: at once. The reader's wheel, drag or keys give the
    ///     jump up. Read as the grid starts.
    /// </summary>
    [Parameter]
    public TimeSpan JumpHold { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The columns it sorts by, first first (none: the source's order). The headings change it (<c>@bind-Sort</c>).</summary>
    [Parameter]
    public IReadOnlyList<SortKey>? Sort { get; set; }

    [Parameter] public EventCallback<IReadOnlyList<SortKey>> SortChanged { get; set; }

    /// <summary>
    ///     The source's own filter, passed through to it: a new one (not equal to the last) runs the query again, the
    ///     reader's place kept, as a new <see cref="Sort" /> does — held, about the row the page shows first; at the live
    ///     top, its top.
    /// </summary>
    [Parameter]
    public object? Filter { get; set; }

    /// <summary>The session's bounds and cadences (<see cref="GridOptions.Default" /> if none).</summary>
    [Parameter]
    public GridOptions? Options { get; set; }

    /// <summary>
    ///     Whether a click (or Space) selects a row, by key: not at all (the default — QuickGrid's rows aren't selected),
    ///     one, or several. A click is sent to the server only for what hears it there: the selection, or
    ///     <see cref="OnRowClick" />.
    /// </summary>
    [Parameter]
    public GridSelection Selection { get; set; }

    /// <summary>The keys selected (<c>@bind-Selected</c>), kept as their rows scroll away and back.</summary>
    [Parameter]
    public IReadOnlyCollection<TKey>? Selected { get; set; }

    [Parameter] public EventCallback<IReadOnlyCollection<TKey>> SelectedChanged { get; set; }

    /// <summary>
    ///     A row clicked, by its key, the column and the keys held (QuickGrid's <c>OnRowClick</c>): given, the rows show
    ///     they can be (a pointer: their body's <c>tg-clickable</c>) and the page sends their clicks; none, a click costs
    ///     the circuit nothing (a selection's aside).
    /// </summary>
    [Parameter]
    public EventCallback<LiveGridRowEvent<TKey>> OnRowClick { get; set; }

    /// <summary>
    ///     A row activated: double-clicked, Enter on it, or a click on a <see cref="LiveColumn.Link" /> column's cell. None:
    ///     nothing is sent, and link columns are plain.
    /// </summary>
    [Parameter]
    public EventCallback<LiveGridRowEvent<TKey>> OnRowActivate { get; set; }

    /// <summary>Its <see cref="LiveColumn" />s (<c>&lt;Columns&gt;</c>, beside <c>&lt;HeadingTemplate&gt;</c> and <c>&lt;StatusTemplate&gt;</c>).</summary>
    [Parameter]
    public RenderFragment? Columns { get; set; }

    /// <summary>Drawn in each heading after its label: a filter, a menu.</summary>
    [Parameter]
    public RenderFragment<LiveHeading>? HeadingTemplate { get; set; }

    /// <summary>
    ///     A line below the rows saying how many there are, whether it follows the live top or holds its place (and how
    ///     many arrived above), whether the page has all the server published, why a read was refused, and how many are
    ///     selected. The page draws it as the session's status streams to it, conflated by Tapline like the rows, and not
    ///     Blazor, whose every render a slow link would carry. A <see cref="StatusTemplate" /> is drawn instead. Read as the
    ///     grid starts.
    /// </summary>
    [Parameter]
    public bool StatusLine { get; set; } = true;

    /// <summary>
    ///     Out of sight two seconds — its page hidden, or the grid more than a view from the viewport — the grid goes
    ///     passive: its session reads none of the source's changes, and its status says only whether the source has moved.
    ///     Back in sight, it asks for its window again, its rows greyed if the answer is late (or, more arrived at the live
    ///     top than it holds, drawn as placeholders) till it comes. False keeps it live out of sight: current the moment
    ///     it's back, at the cost of reads no one sees; its page holds a shared Web Lock, which keeps a browser from putting
    ///     a hidden tab to sleep. (A hidden tab's timers and frames are still the browser's to throttle: it draws once
    ///     shown.) Read as the grid starts.
    /// </summary>
    [Parameter]
    public bool PassiveWhenHidden { get; set; } = true;

    /// <summary>
    ///     A pill over the rows while the grid holds its place with rows arrived above the reader hasn't seen: how many
    ///     ("12 new above" — those let in above the view counted till it's scrolled to them), and a click back to the live top (as <see cref="ToTopAsync" />) — drawn by the page as the status streams to it, so
    ///     the click goes at once, no round trip to the server. Style it as <c>.tg-new-above</c>. Read as the grid starts.
    /// </summary>
    [Parameter]
    public bool NewAbove { get; set; }

    /// <summary>What the status line calls a row (<see cref="StatusLine" />).</summary>
    [Parameter]
    public string RowName { get; set; } = "row";

    /// <summary>And rows: <see cref="RowName" /> and an s, if not given.</summary>
    [Parameter]
    public string? RowsName { get; set; }

    /// <summary>
    ///     Drawn below the rows instead of the status line, rendered by Blazor as the status changes (at most four times a
    ///     second: <see cref="StatusChanged" />).
    /// </summary>
    [Parameter]
    public RenderFragment<GridStatus>? StatusTemplate { get; set; }

    /// <summary>
    ///     A column chooser below the rows, beside the status line, for showing and hiding columns. False, none — or the
    ///     page's own, where it likes: a <see cref="LiveColumnChooser" /> given the grid.
    /// </summary>
    [Parameter]
    public bool ColumnChooser { get; set; } = true;

    /// <summary>
    ///     A field of the rows holding when each arrived (a long of microseconds since 1970, or a DateTime): a row's
    ///     arrival flash runs from it wherever the row is drawn, so pages side by side, or one reloaded, flash alike.
    ///     None: from when the page hears of the row. Read as the grid starts.
    /// </summary>
    [Parameter]
    public string? ArrivedAt { get; set; }

    /// <summary>
    ///     Each row arrived when its key says: the keys are UUIDv7s (RFC 9562), whose first 48 bits are the millisecond each
    ///     was made, so no field of the row need say it (instead of <see cref="ArrivedAt" />). A key of another version
    ///     flashes from when the page hears of it. Read as the grid starts.
    /// </summary>
    [Parameter]
    public bool ArrivedFromKey { get; set; }

    /// <summary>
    ///     A field of the rows holding when each last changed (a long of microseconds since 1970, or a DateTime): a changed
    ///     cell's flash runs from it, so pages side by side flash alike, and the page reports its lag from it
    ///     (<see cref="GridStatus.Lag" />). None: flashes run from when the page hears of the change, and no lag is told.
    ///     Read as the grid starts.
    /// </summary>
    [Parameter]
    public string? ChangedAt { get; set; }

    /// <summary>
    ///     A heading's first click sorts by it descending, the next ascending, the next goes back to the source's own
    ///     order (shift-click alike, within a sort by several columns), as a blotter sorts; otherwise ascending first.
    /// </summary>
    [Parameter]
    public bool SortDescendingFirst { get; set; }

    /// <summary>The source's own order, given no sort: its column's heading shows it with a muted arrow.</summary>
    [Parameter]
    public SortKey? NaturalSort { get; set; }

    [Parameter] public string? AriaLabel { get; set; }

    /// <summary>Its class, beside <c>live-grid</c>: size it here or by <see cref="Style" /> (it fills its height).</summary>
    [Parameter]
    public string? Class { get; set; }

    [Parameter] public string? Style { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>Why it can't show its rows (shown in its place), or null.</summary>
    public string? Refusal => _refusal ?? _columnsRefusal;

    /// <summary>
    ///     The view its session reads, as the source knows it — by reference, each session's its own: what the source keeps
    ///     for this grid (null till it starts).
    /// </summary>
    public GridView? View => _session?.View;

    /// <summary>
    ///     Held away from the live top, the key of the row it shows first (a row of a run: its open row's); none at the live
    ///     top, or before it starts. Given back as <see cref="StartAt" />, a grid made again (a tab come back to, say)
    ///     starts about where this one was.
    /// </summary>
    public TKey? HeldAt => _session?.HeldAt;

    /// <summary>
    ///     A row to start at, held there, rather than at the live top: brought into view as it starts (as
    ///     <see cref="RevealAsync" /> does), if the view has it. Read as the grid starts.
    /// </summary>
    [Parameter]
    public TKey? StartAt { get; set; }

    /// <summary>What the chrome shows: the session's last answer, and whether the page holds all it published.</summary>
    public GridStatus Status => (_session?.Status ?? new GridStatus { Live = true }) with { Freshness = _freshness, Lag = _lag };

    /// <summary>
    ///     The status changed (on any thread: <c>InvokeAsync</c> to render). Told at most four times a second: a change
    ///     within a quarter of a second of the last told is told once that's up, with any others since — a render a
    ///     change would cost a slow link more than the rows.
    /// </summary>
    public event Action? StatusChanged;

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await StopAsync();
        lock (_told)
        {
            _tellDue = true; // (nothing told after)
            _tell?.Dispose();
        }

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

    /// <summary>
    ///     Opens a row of the view, so that its run — its descendants, depth first (<c>IGridSource.ReadRunsAsync</c>) — shows
    ///     after it, as its expander does: read at once. False if the grid isn't showing rows. A row stays open as the
    ///     reader scrolls away and back, and as the sort or filter changes; past <see cref="GridOptions.MaxOpenRows" />, the
    ///     row open least recently used (opened, or shown) is closed to make room — never one in the window.
    /// </summary>
    public Task<bool> ExpandAsync(TKey key)
    {
        return Task.FromResult(_session?.SetOpen(key, true) ?? false);
    }

    /// <summary>Closes a row, its run gone: false if the grid isn't showing rows.</summary>
    public Task<bool> CollapseAsync(TKey key)
    {
        return Task.FromResult(_session?.SetOpen(key, false) ?? false);
    }

    /// <summary>Whether a row is open, its run shown (<see cref="ExpandAsync" />).</summary>
    public bool IsExpanded(TKey key)
    {
        return _session?.IsOpen(key) ?? false;
    }

    /// <summary>
    ///     Opens every row of the view — rows arriving too, till <see cref="CollapseAllAsync" /> — as the expander's heading
    ///     does, or * on a row: read at once. A row's place counts the runs above it read so far, so the total is at least
    ///     so many and the scrollbar settles as the reader goes. Rows closed meanwhile (<see cref="CollapseAsync" />) stay
    ///     closed, as many as <see cref="GridOptions.MaxOpenRows" /> remembered. False if the grid isn't showing rows.
    /// </summary>
    public Task<bool> ExpandAllAsync()
    {
        return Task.FromResult(SetAllExpanded(true));
    }

    /// <summary>Closes every row, those opened one by one too: false if the grid isn't showing rows.</summary>
    public Task<bool> CollapseAllAsync()
    {
        return Task.FromResult(SetAllExpanded(false));
    }

    /// <summary>Whether every row is open (<see cref="ExpandAllAsync" />), but those closed since.</summary>
    public bool AllExpanded => _session?.AllOpen ?? false;

    private bool SetAllExpanded(bool open)
    {
        if (_session?.SetAllOpen(open) is not true) return false;
        _ = InvokeAsync(StateHasChanged); // (the expander's heading says which)
        return true;
    }

    /// <summary>
    ///     The expander's heading clicked: every row opened, or, open, closed. One delegate, kept (as
    ///     <see cref="SortClick" />): a render that changes nothing sends nothing.
    /// </summary>
    private Action ToggleAllClick => _toggleAllClick ??= () => SetAllExpanded(!AllExpanded);

    /// <summary>Back to the live top: the page scrolls to the first row, and the grid follows the top again.</summary>
    public async Task ToTopAsync()
    {
        try
        {
            if (_handle is not null) await _handle.InvokeVoidAsync("top");
        }
        catch (JSDisconnectedException)
        {
        }
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
        if (_handle is not null && RowEvents() != _rowEvents) _ = PushRowEventsAsync();
    }

    /// <summary>The row events heard here, which the page sends: a click (OnRowClick), an activation (OnRowActivate).</summary>
    private (bool Click, bool Activate) RowEvents()
    {
        return (OnRowClick.HasDelegate, OnRowActivate.HasDelegate);
    }

    private async Task PushRowEventsAsync()
    {
        _rowEvents = RowEvents();
        try
        {
            if (_handle is not null) await _handle.InvokeVoidAsync("setRowEvents", _rowEvents.Click, _rowEvents.Activate);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_choicesTold != _choicesVersion) // (the columns, or which are shown, changed in this render: its choosers told)
        {
            _choicesTold = _choicesVersion;
            ColumnsChanged?.Invoke();
        }

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
        if (OverscanCount < 0) return $"an OverscanCount of {OverscanCount} rows.";
        if (JumpHold < TimeSpan.Zero || JumpHold > TimeSpan.FromSeconds(10)) return $"a JumpHold of {JumpHold}: from 0 to 10 s.";
        return Options?.Broken();
    }

    /// <summary>
    ///     The columns, read afresh once they've changed: those shown, and why they can't work. Their formats are the
    ///     culture's current as the grid renders (QuickGrid's, a <c>PropertyColumn</c>'s): the page writes the cells in it.
    /// </summary>
    private void ResolveColumns()
    {
        var culture = CultureInfo.CurrentCulture;
        if (_registry.Version == _columnsVersion && ArrivedAt == _stampSeen && ArrivedFromKey == _fromKeySeen &&
            ChangedAt == _changeStampSeen && Equals(culture, _culture)) return;
        (_columnsVersion, _stampSeen, _fromKeySeen, _changeStampSeen, _culture) =
            (_registry.Version, ArrivedAt, ArrivedFromKey, ChangedAt, culture);
        (_columns, _columnsRefusal) = GridColumns<TRow>.Resolve(_registry.Columns, culture);
        (_stamp, var stampRefusal) = GridColumns<TRow>.ArrivalOf<TKey>(ArrivedAt, ArrivedFromKey);
        if (stampRefusal is not null) _columnsRefusal ??= stampRefusal;
        (_changeStamp, var changeRefusal) = ChangedAt is null ? (null, null) : GridColumns<TRow>.StampOf(ChangedAt);
        if (changeRefusal is not null) _columnsRefusal ??= $"ChangedAt: {changeRefusal}";
        Choose();
    }

    private void Choose()
    {
        _shown = _columns.Where(Shown).ToArray();
        _choices = null;
        _choicesVersion++;
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

            // The page's module first, however long a slow link takes to bring it (up to StartTimeout, not interop's default
            // minute); the session after, once the page can take its subscription up at once: Tapline closes one not taken
            // up within its bind timeout (OutletOptions.BindTimeout, 30 s — at 16 Kbit/s, about 60 KB of module, uncompressed).
            // The body's height comes back with it, in the same round trip.
            using var importing = new CancellationTokenSource(StartTimeout);
            var import = Js.InvokeAsync<IJSObjectReference>("import", importing.Token,
                ["./" + Assets["_content/Tapline/tapline-grid.js"]]);
            var height = HeightAsync(importing.Token);
            var module = await import;
            if (_disposed || Refusal is not null)
            {
                await module.DisposeAsync();
                return;
            }

            GridSession<TKey, TRow> session;
            try
            {
                session = new GridSession<TKey, TRow>(outlet, Source!, new GridView(_sort, _filter), Options)
                    { MostOverscan = OverscanCount };
            }
            catch (Exception e) when (e is GridRefusedException or OutletFullException)
            {
                await module.DisposeAsync();
                Refuse(e.Message);
                return;
            }

            (_session, _module, _view) = (session, module, 0);
            if (FirstWindow(await height) is { } first) // (read as createGrid goes: the page's first state has rows)
                session.Request(_last = first);
            session.StatusChanged += Changed;
            _ = Watch(session);
            _dotnet = DotNetObjectReference.Create(this);
            _pageColumns = JsonSerializer.Serialize(_shown);
            var created = _view;
            using var creating = new CancellationTokenSource(StartTimeout);
            var handle = await module.InvokeAsync<IJSObjectReference>("createGrid", creating.Token, [_host, _body, _head, _dotnet, new
            {
                binding = session.Binding,
                columns = _shown,
                culture = PageCulture(),
                rowHeight = RowHeight,
                overscan = OverscanCount,
                jumpHold = JumpHold.TotalMilliseconds,
                newAbove = NewAbove,
                keyOrder = GridKeys<TKey>.Order,
                statusLine = StatusTemplate is null && StatusLine ? (object)_statusLine : null,
                rowName = RowName,
                rowsName = RowsName ?? RowName + "s",
                passive = PassiveWhenHidden,
                maxRows = (Options ?? GridOptions.Default).MaxWindowRows,
                headerRows = 1,
                view = _view,
                arrivedAt = _stamp,
                changedAt = _changeStamp,
                selection = Selection switch
                {
                    GridSelection.None => "none", GridSelection.Multiple => "multiple", _ => "single"
                },
                clickable = (_rowEvents = RowEvents()).Click,
                activatable = _rowEvents.Activate,
                selected = _selected.Select(k => GridKeys<TKey>.ToPage(k)).ToArray()
            }]);
            if (_session == session)
            {
                _handle = handle;
                if (_view != created) await handle.InvokeVoidAsync("reset", _view); // (sorted or filtered meanwhile: told now)
                if (StartAt is { } at && !_startedAt) // (held there, if the view has it: else the live top; once, not on a restart)
                {
                    _startedAt = true;
                    await RevealAsync(at);
                }
            }
            else
            {
                await handle.InvokeVoidAsync("dispose"); // (stopped meanwhile: its body goes too)
                await handle.DisposeAsync();
            }
        }
        catch (Exception e) when (e is JSDisconnectedException or TaskCanceledException or OperationCanceledException)
        {
            // The circuit gone, or the page's answer lost with a connection (the start's interop calls never answered): a
            // half-made start undone, and started again on the next render — which, the circuit back, comes at once.
            if (_handle is null && _session is not null) await StopAsync();
            if (!_disposed) _ = InvokeAsync(StateHasChanged);
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

    /// <summary>
    ///     The culture as the page writes cells in it: what of its symbols, and of its names the columns' patterns write,
    ///     differ from the invariant culture's (none for a page in it).
    /// </summary>
    private Dictionary<string, object>? PageCulture()
    {
        var culture = _culture ?? CultureInfo.CurrentCulture;
        return GridFormats.PageCulture(culture, GridFormats.DatesOf(culture),
            _columns.Where(c => c is { Kind: "time", Invariant: false, Format: not null }).Select(c => c.Format!));
    }

    /// <summary>The most the page's module and its start may take (a slow link's): past it, started again.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     How tall the body is on the page, px, laid out before the module draws in it — or, not laid out (hidden, in a
    ///     container not shown), the page's viewport, the most of it a reader could see. Asked with the module's import,
    ///     by interop alone (no script of the grid's yet), so it's back in the same round trip. 0 if neither could be read.
    /// </summary>
    private async Task<double> HeightAsync(CancellationToken token)
    {
        try
        {
            var body = Js.InvokeAsync<double>("Reflect.get", token, _body, "clientHeight");
            var page = Js.GetValueAsync<double>("innerHeight", token);
            var height = await body;
            return height > 0 ? height : await page;
        }
        catch (Exception) // (only a head start: whatever fails — the page, the circuit, a runtime without property reads — none)
        {
            return 0;
        }
    }

    /// <summary>
    ///     The window the page asks for first, from the body's height (<see cref="HeightAsync" />): the rows in view and a
    ///     quarter of them ahead, as its module asks before any overscan is told (tapline-grid.js, windowFor), within the
    ///     session's bound. The session reads it as soon as it's made, so the page's first state, as it subscribes, has its
    ///     rows — and its own first request finds them read. Null for no height.
    /// </summary>
    private GridRequest? FirstWindow(double height)
    {
        return FirstWindow(height, RowHeight, (Options ?? GridOptions.Default).MaxWindowRows, _view);
    }

    /// <inheritdoc cref="FirstWindow(double)" />
    internal static GridRequest? FirstWindow(double height, int rowHeight, int maxRows, int view)
    {
        if (!(height > 0) || rowHeight < 1 || maxRows < 1) return null;
        var rows = (int)Math.Min(maxRows, Math.Ceiling(height / rowHeight));
        return new GridRequest(Guid.Empty, 0, rows, 0, Math.Min((rows + 3) / 4, maxRows - rows), View: view);
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

    /// <summary>
    ///     A new sort or filter, the query run again, the reader's place kept: held, the session reads the new view about
    ///     the row the page shows first — where it sorts in it, whether it's there or not, its view's rows from there — and
    ///     the page goes there, told the view's number; at the live top, its top.
    /// </summary>
    private async Task ApplyViewAsync()
    {
        if (_session is null) return;
        _view = _session.SetView(new GridView(_sort, _filter), true);
        try
        {
            if (_handle is not null) await _handle.InvokeVoidAsync("reset", _view, true);
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

    /// <summary>The status changed: told now, or once a quarter of a second from the last telling is up (<see cref="StatusChanged" />).</summary>
    private void Changed()
    {
        lock (_told)
        {
            if (_tellDue) return; // (told when it's up, with the others)
            var wait = _toldAt + TellEveryMs - Environment.TickCount64;
            if (wait > 0)
            {
                _tellDue = true;
                (_tell ??= new Timer(_ => Tell())).Change(wait, Timeout.Infinite);
                return;
            }

            _toldAt = Environment.TickCount64;
        }

        StatusChanged?.Invoke();
    }

    private void Tell()
    {
        lock (_told)
        {
            _tellDue = false;
            _toldAt = Environment.TickCount64;
        }

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
        if (Natural(column) is { } natural) return natural.Descending ? "descending" : "ascending";
        return SortRank(column.Name) == 1 ? _sort[0].Descending ? "descending" : "ascending" : null;
    }

    /// <summary>The source's own order, if it's this column's and no sort is chosen.</summary>
    private SortKey? Natural(GridColumn column)
    {
        return _sort.Count == 0 && NaturalSort is { } natural &&
               string.Equals(natural.Column, column.Name, StringComparison.OrdinalIgnoreCase)
            ? natural
            : null;
    }

    /// <summary>
    ///     A heading clicked: sorted by it alone, the first way (ascending, or descending given
    ///     <see cref="SortDescendingFirst" />), then the other; then, descending first, back to the source's own order,
    ///     else the first way again. Shift-clicked, it's added to the sort the first way, then reversed, then taken out.
    /// </summary>
    private async Task SortByAsync(string column, bool add)
    {
        _sort = NextSort(_sort, column, add, SortDescendingFirst);
        var sort = _sort;
        await SortChanged.InvokeAsync(sort);
        await ApplyViewAsync();
    }

    /// <summary>The sort after a heading's click (<see cref="SortByAsync" />).</summary>
    internal static IReadOnlyList<SortKey> NextSort(IReadOnlyList<SortKey> sort, string column, bool add, bool descendingFirst)
    {
        var rank = 0;
        for (var i = 0; i < sort.Count && rank == 0; i++)
            if (string.Equals(sort[i].Column, column, StringComparison.OrdinalIgnoreCase))
                rank = i + 1;
        if (add)
        {
            List<SortKey> more = [.. sort];
            if (rank == 0) more.Add(new SortKey(column, descendingFirst));
            else if (more[rank - 1].Descending == descendingFirst) more[rank - 1] = more[rank - 1] with { Descending = !descendingFirst };
            else more.RemoveAt(rank - 1);
            return more;
        }

        if (rank != 1 || sort.Count != 1) return [new SortKey(column, descendingFirst)];
        if (sort[0].Descending == descendingFirst) return [new SortKey(column, !descendingFirst)];
        return descendingFirst ? [] : [new SortKey(column, descendingFirst)];
    }

    /// <summary>Every column, in order, and whether it's shown: as a column chooser lists them (<see cref="LiveColumnChooser" />).</summary>
    public IReadOnlyList<LiveColumnChoice> ColumnChoices =>
        _choices ??= Array.ConvertAll(_columns, c => new LiveColumnChoice(c.Name, c.Label, Shown(c)));

    /// <summary>
    ///     A column shown or hidden, by name (in any case), as a chooser's tick does: its width kept for when it's back. The
    ///     last column shown stays; a name not yet a column's is kept for when it is.
    /// </summary>
    public Task ShowColumnAsync(string name, bool shown)
    {
        return InvokeAsync(() =>
        {
            if (!shown && _shown.Length == 1 && string.Equals(_shown[0].Name, name, StringComparison.OrdinalIgnoreCase)) return;
            _chosen[name] = shown;
            Choose();
            StateHasChanged();
        });
    }

    /// <summary>The columns, or which of them are shown, changed: told once the grid has rendered them, for its choosers.</summary>
    public event Action? ColumnsChanged;

    /// <summary>
    ///     A heading's click, one delegate a column, kept: a render that changes nothing sends nothing. (A new one each
    ///     render is a new event handler to Blazor, sent with the old one's disposal: some 75 bytes a column on the circuit
    ///     each time the grid renders again unchanged.)
    /// </summary>
    private Func<MouseEventArgs, Task> SortClick(GridColumn column)
    {
        var name = column.Name;
        return _sortClicks.TryGetValue(name, out var click) ? click : _sortClicks[name] = e => SortByAsync(name, e.ShiftKey);
    }

    // ── the page's ──

    /// <summary>
    ///     The page asks for a window (tapline-grid.js, its Requests); from the live top, with the key of the row it shows
    ///     at the view's first position (<paramref name="anchor" />), which a hold keeps there.
    /// </summary>
    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public void Request(string id, long first, int count, int before, int after, int holds, long letIn, int view, bool racing,
        bool top, string? anchor)
    {
        if (_session is null || !Guid.TryParseExact(id, "N", out var guid)) return;
        _last = new GridRequest(guid, first, count, before, after, holds, letIn, view, racing, top);
        _session.Request(_last, GridKeys<TKey>.TryFromPage(anchor, out var key) ? key : null);
    }

    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public void Visible(bool visible)
    {
        _session?.SetVisible(visible);
    }

    /// <summary>
    ///     The page's lag (ms): the longest a change took from its stamp to being drawn, over the last second it drew one;
    ///     null, its stamps not agreeing with the page's clock.
    /// </summary>
    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public void Lag(double? ms)
    {
        _lag = ms is { } lag ? TimeSpan.FromMilliseconds(lag) : null;
        Changed();
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

    /// <summary>* on a row: every row opened — false if refused (no rows shown).</summary>
    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public bool ToggleAll(bool open)
    {
        return SetAllExpanded(open);
    }

    /// <summary>A row's expander, or → and ← on it: opened or closed — false if refused (no rows shown), the page's chevron back.</summary>
    [JSInvokable, EditorBrowsable(EditorBrowsableState.Never)]
    public bool Toggle(string key, bool open)
    {
        return GridKeys<TKey>.TryFromPage(key, out var k) && (_session?.SetOpen(k, open) ?? false);
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
