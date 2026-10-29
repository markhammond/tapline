using Tapline;
using Tapline.Client;
using Tapline.Grid;
using Tapline.Grid.Provider;

/// <summary>
///     Tapline.Grid's nested rows: a row opened shows its run — its descendants, depth first, to thirty-two deep — after
///     it, read a slice at a time as the window crosses it; held, rows of runs come or gone above the page's first row
///     move every position, and the page's scroll, by as many. Against an oracle worked out apart from the source.
/// </summary>
internal static partial class T
{
    /// <summary>The view as the page draws it, worked out apart: each order in the view's order, an open one followed by its run.</summary>
    private static List<(long Key, byte Depth)> NestedView(TreeGrid model, GridView view, IReadOnlySet<long> open)
    {
        return NestedView(model, view, open.Contains);
    }

    private static List<(long Key, byte Depth)> NestedView(TreeGrid model, GridView view, Func<long, bool> isOpen)
    {
        var rows = new List<(long, byte)>();
        foreach (var id in GridOracle(model.Top, view))
        {
            rows.Add((id, 0));
            if (isOpen(id)) rows.AddRange(model.Run(id));
        }

        return rows;
    }

    /// <summary>
    ///     The window as published, against the oracle: its rows placed from the wire as the page places them are the
    ///     window's; the table holds them and no others (but rows a view read first left), each as the source has it; they
    ///     are a run of the view as drawn, each at its depth, the view's rows shown open as they are; and its first row —
    ///     or the open row above whose run it starts in — stands where the view has it, less the rows arrived above and not
    ///     shown, and less the rows of runs above it the window doesn't know of (opened elsewhere and not read, or grown
    ///     since): exactly.
    /// </summary>
    private static void CheckNested(GridRig rig, TreeGrid model, IReadOnlySet<long> open, string step)
    {
        CheckNested(rig, model, open.Contains, step);
    }

    private static void CheckNested(GridRig rig, TreeGrid model, Func<long, bool> isOpen, string step)
    {
        var (h, keys, depths) = rig.PublishedNested();
        var table = rig.TableRows();
        if (rig.Window.PublishedOrder.IsEmpty)
        {
            Assert(table.Count == 0, $"{step}: nothing published, no rows");
            return;
        }

        Assert(keys.SequenceEqual(rig.Window.Keys.ToArray()), $"{step}: the rows placed from the wire, as the page places them, the window's");
        Assert(table.Count == keys.Length + rig.Window.Lingering && keys.All(table.ContainsKey),
            $"{step}: the table holds the window's rows and no others but those left by a view read first " +
            $"({table.Count} rows, {keys.Length} keys, {rig.Window.Lingering} left)");
        foreach (var key in keys)
            Assert(SameRow(table[key], model.OwnerOf(key) < 0 ? model.Top.RowOf(key) : TreeGrid.RowOf(key)),
                $"{step}: row {key} as the source has it now");
        if (keys.Length == 0) return;
        var all = NestedView(model, rig.View, isOpen);
        var at = all.FindIndex(r => r.Key == keys[0]);
        Assert(at >= 0, $"{step}: the window's first row, {keys[0]}, in the view as drawn");
        for (var i = 0; i < keys.Length; i++)
            Assert(at + i < all.Count && all[at + i] == (keys[i], depths[i]),
                $"{step}: the window's row {i} ({keys[i]}, {depths[i]} deep) the view's next ({(at + i < all.Count ? all[at + i] : "none")})");
        for (var i = 0; i < keys.Length; i++)
            if (depths[i] == 0)
                Assert((rig.Window.Depths[i] & 0x80) != 0 == isOpen(keys[i]), $"{step}: {keys[i]} shown open as it is");

        // On the wire, as the page reads it: every row of the view open (AllOpen) but those whose run says it's closed, or
        // the runs' rows open.
        GridOrder.TryRead(rig.Window.PublishedOrder, out var wire, out _, out var runBytes);
        var runs = GridOrder.RunsOf<long>(runBytes);
        var closedRuns = runs.Where(r => r.Closed).Select(r => r.Owner).ToHashSet();
        var openRuns = runs.Where(r => !r.Closed).Select(r => r.Owner).ToHashSet();
        Assert(runs.All(r => !r.Closed || (wire.AllOpen && r.Keys.Length == 0)), $"{step}: closed runs only with every row open, and empty");
        for (var i = 0; i < keys.Length; i++)
            if (depths[i] == 0)
                Assert((wire.AllOpen ? !closedRuns.Contains(keys[i]) : openRuns.Contains(keys[i])) == isOpen(keys[i]),
                    $"{step}: {keys[i]} open on the wire as it is ({(wire.AllOpen ? "every row open" : "rows opened")})");

        var owner = rig.Window.FirstOwner!.Value;
        var ownerKey = depths[0] == 0 ? keys[0] : model.OwnerOf(keys[0]);
        var ownerAt = all.FindIndex(r => r == (ownerKey, 0));
        var ownerRank = Array.IndexOf(GridOracle(model.Top, rig.View), ownerKey);
        var hidden = h.Live ? 0 : rig.Window.Hidden;
        Assert(ownerAt >= 0 && owner.At + hidden + (ownerAt - ownerRank - owner.Runs) == ownerAt,
            $"{step}: {ownerKey} at {owner.At} (top {owner.Top}) with {hidden} not shown above and {owner.Runs} rows of runs " +
            $"counted above it, where the view has it, {ownerAt} (rank {ownerRank}), {ownerAt - ownerRank} rows of runs above it " +
            $"({(h.Live ? "live" : $"held {h.Holds}")}, let in {h.LetIn}, first {h.First}, {keys.Length} rows)");
        var offset = depths[0] == 0 ? -1 : model.OffsetOf(ownerKey, keys[0]);
        Assert(h.First == owner.At + 1 + offset, $"{step}: the window's first row at {h.First}, {offset + 1} after {owner.At}");
    }

    public static Task GridNestedRules()
    {
        // The rows open, where their runs put the view's rows: placed where a read found them, their runs as last read.
        var open = new GridOpenRows<long>();
        Assert(open.Open(10) && open.Open(20) && open.Open(30) && !open.Open(10), "opened (one open already: false)");
        open.Learn([5, 10, 15, 20], 100);
        open.SetLength(10, 4);
        open.SetLength(20, 2);
        Assert(open.AtOf(10) == 101 && open.AtOf(20) == 103 && open.AtOf(30) == -1, "placed where read; one not read unplaced");
        Assert(!open.TryLength(30, out _) && open.TryLength(10, out var four) && four == 4, "its run unread: not known");
        Assert(open.Display(100) == 100 && open.Display(101) == 101 && open.Display(102) == 106 && open.Display(103) == 107 &&
               open.Display(104) == 110, "a row of the view after the runs open above it");
        Assert(open.Locate(101) == (101, -1) && open.Locate(102) == (101, 0) && open.Locate(105) == (101, 3) &&
               open.Locate(106) == (102, -1) && open.Locate(108) == (103, 0) && open.Locate(109) == (103, 1) &&
               open.Locate(110) == (104, -1) && open.Locate(99) == (99, -1), "a view position: a row of the view, or of a run");
        Assert(open.Before(103) == 4 && open.RunRows(1_000) == 6 && open.RunRows(102) == 4, "the rows of runs above");
        open.Shift(10);
        Assert(open.AtOf(10) == 111 && open.Display(112) == 116, "every one moved by ten");
        open.Learn([1, 2, 3], 110);
        Assert(open.AtOf(10) == -1 && open.Display(200) == 202, "read elsewhere than found: unplaced, its run not counted");
        open.Learn([10], 150);
        Assert(open.AtOf(10) == 150 && open.Display(200) == 206, "found again: placed, its run as last read");
        Assert(open.Close(20) && !open.Close(20) && open.Count == 2 && open.Display(200) == 204, "closed: its run gone");
        open.Unplace();
        Assert(open.AtOf(10) == -1 && !open.TryLength(10, out _) && open.IsOpen(10) && open.Display(500) == 500,
            "another view: unplaced, unread, still open");

        // The rows open as their session remembers them: three at most, one opened forgetting the least recently used —
        // opened, or held by the window published — never one the window holds.
        var remembered = new GridRecentSet<long>(3);
        var forgotten = new List<long>();
        Assert(remembered.Open(10, forgotten) && remembered.Open(20, forgotten) && remembered.Open(30, forgotten) &&
               !remembered.Open(10, forgotten) && forgotten.Count == 0 && remembered.Count == 3, "three; 10 used again");
        Assert(remembered.Open(40, forgotten) && forgotten.SequenceEqual([20L]) && !remembered.Contains(20) && remembered.Count == 3,
            "a fourth: 20 forgotten, used least recently (10 used again since)");
        forgotten.Clear();
        remembered.Shown([30]);
        Assert(remembered.Open(50, forgotten) && remembered.Open(60, forgotten) && forgotten.SequenceEqual([10L, 40L]),
            "30 held by the window: 10 and 40 forgotten before it, though used since");
        forgotten.Clear();
        remembered.Shown([30, 50, 60]);
        Assert(remembered.Open(70, forgotten) && forgotten.Count == 0 && remembered.Count == 4,
            "every one held: none forgotten, four while the window holds three");
        remembered.Shown([70]);
        Assert(remembered.Open(80, forgotten) && forgotten.SequenceEqual([30L, 50L]) && remembered.Count == 3,
            "the window moved on: those it held used then, forgotten first of those it no longer holds, down to three");
        forgotten.Clear();
        Assert(remembered.Close(70) && !remembered.Close(70) && remembered.Count == 2, "closed (held): gone");
        remembered.Shown([70, 80]);
        Assert(remembered.Open(90, forgotten) && !remembered.Open(60, forgotten) && remembered.Open(100, forgotten) &&
               forgotten.SequenceEqual([90L]) && remembered.Count == 3,
            "a closed row shown open (its close on its way) not held; 60 used again: 90 forgotten, 80 held");
        forgotten.Clear();
        var none = new GridRecentSet<long>(0);
        Assert(none.Open(1, forgotten) && forgotten.Count == 0, "none remembered: one opened all the same");
        none.Shown([1]);
        Assert(none.Open(2, forgotten) && forgotten.Count == 0 && none.Count == 2, "held by the window: kept");
        none.Shown([]);
        Assert(none.Open(3, forgotten) && forgotten.SequenceEqual([2L, 1L]) && none.Count == 1,
            "the window holding neither: both forgotten, the one it held last");
        forgotten.Clear();
        remembered.Clear();
        Assert(remembered.Count == 0 && !remembered.Contains(60) && remembered.Open(60, forgotten) && remembered.Open(70, forgotten) &&
               remembered.Open(80, forgotten) && remembered.Open(90, forgotten) && forgotten.SequenceEqual([60L]),
            "cleared: none, then as new — the window's held rows forgotten with the rest");

        // A run's rows: one to thirty-two deep, no deeper.
        var page = new GridRunPage<long, GridOrderRow>(4);
        Assert(page.Add(1, default, 1) && page.Add(2, default, GridRuns.MaxDepth), "1 and 32 deep");
        Throws<ArgumentOutOfRangeException>(() => page.Add(3, default, 0), "none deep: a row of the view's, not a run's");
        Throws<ArgumentOutOfRangeException>(() => page.Add(3, default, GridRuns.MaxDepth + 1), "thirty-three deep");
        Assert(page.Add(3, default, 2) && page.Add(4, default, 3) && !page.Add(5, default, 1), "full at its capacity");
        return Task.CompletedTask;
    }

    public static Task GridNested()
    {
        var model = new TreeGrid(3, 2_000);
        var open = new HashSet<long>();
        var rig = new GridRig(model, 200);
        rig.Ask(0, 20, 0, 20);

        // Live: a row opened near the top, its run after it, the rows below moved down by as many.
        var a = Enumerable.Range(0, 10).Select(i => 1_999L - i).First(id => model.LengthOf(id) > 2);
        var aRun = model.LengthOf(a);
        Assert(rig.Window.OpenRow(a) && open.Add(a), "opened");
        rig.Refresh();
        CheckNested(rig, model, open, "live, a row opened");
        var (h, keys, depths) = rig.PublishedNested();
        var aAt = Array.IndexOf(keys, a);
        Assert(h.Live && h.Runs && h.Total == 2_000 + aRun && keys.Length == 40 && depths[aAt + 1] == 1 &&
               keys[aAt + 1] == model.RunAt(a, 0).Key, $"live: its run after it, the total {h.Total} counting it");

        // Rows arriving at the live top push it, and its run, down.
        model.Top.Arrive(2);
        rig.Refresh();
        CheckNested(rig, model, open, "live, two arrived");
        Assert(Array.IndexOf(rig.Published().Keys, a) == aAt + 2, "two arrived: two down");

        // ByKey: the order's header alone, the view's rows placed by their keys, the run after its open row.
        rig.Window.ByKey = true;
        rig.Refresh();
        Assert(rig.Published().Header is { ByKey: true, Runs: true }, "by key, with runs");
        CheckNested(rig, model, open, "live, by key");
        rig.Window.ByKey = false;

        // Thirty-two deep: the chain under Deep, far below.
        Assert(rig.Window.OpenRow(TreeGrid.Deep) && open.Add(TreeGrid.Deep), "Deep opened");
        var all = NestedView(model, rig.View, open);
        var deepAt = all.FindIndex(r => r == (TreeGrid.Deep, 0));
        rig.Ask(deepAt - 5, 20, 5, 40);
        CheckNested(rig, model, open, "held at Deep");
        Assert(rig.Published().Header.Live == false && rig.PublishedNested().Depths.Max() == GridRuns.MaxDepth,
            "held, its chain to thirty-two deep");

        // A million under Big: the window anywhere in it, a slice read, no more.
        Assert(rig.Window.OpenRow(TreeGrid.Big) && open.Add(TreeGrid.Big), "Big opened");
        rig.Refresh(); // (Big is above Deep: Deep's place moves down a million, and the page with it)
        CheckNested(rig, model, open, "Big opened above");
        all = NestedView(model, rig.View, open);
        var bigAt = all.FindIndex(r => r == (TreeGrid.Big, 0));
        var rows = model.RunRows;
        rig.Ask(bigAt + 1 + 500_000, 20, 10, 40);
        CheckNested(rig, model, open, "in the middle of the million");
        (h, keys, _) = rig.PublishedNested();
        Assert(model.OffsetOf(TreeGrid.Big, keys[0]) == 499_990 && keys.Length == 70, "the window's rows the run's, from 499,990");
        Assert(model.RunRows - rows <= 2 * (70 + 16 + 40), $"a slice read, no more ({model.RunRows - rows} run rows read)");
        Assert(h.Total == 2_002 + aRun + model.LengthOf(TreeGrid.Deep) + TreeGrid.Million, $"the total, {h.Total}, every run counted");

        // Held in the run, it grows at its head: every position moves down by as many, the page's first row kept.
        var (before, _, _) = rig.PublishedNested();
        var viewKey = rig.Window.Keys[10];
        model.Grow(TreeGrid.Big, 5);
        rig.Refresh();
        CheckNested(rig, model, open, "the run grown above the page's row");
        h = rig.Published().Header;
        Assert(h.LetIn == before.LetIn + 5 && rig.Window.Keys[(int)(before.First + 10 + 5 - h.First)] == viewKey,
            $"moved five: the page's first row still drawn where it was (let in {h.LetIn})");

        // Shrunk at its head: up as many.
        model.Shrink(TreeGrid.Big, 3);
        rig.Refresh();
        CheckNested(rig, model, open, "the run shrunk above the page's row");
        Assert(rig.Published().Header.LetIn == before.LetIn + 2, "moved back three");

        // Held below Deep's chain, Big closed above: a million and two rows go above, every position with them.
        all = NestedView(model, rig.View, open);
        deepAt = all.FindIndex(r => r == (TreeGrid.Deep, 0));
        var (hd, _) = rig.Published();
        rig.Ask(deepAt + 40 - (h.LetIn - hd.LetIn), 20, 30, 30, hd.Holds, hd.LetIn);
        CheckNested(rig, model, open, "held below Deep's chain");
        (before, _, _) = rig.PublishedNested();
        viewKey = rig.Window.Keys[(int)(deepAt + 40 - before.First)];
        Assert(rig.Window.CloseRow(TreeGrid.Big) && open.Remove(TreeGrid.Big), "Big closed");
        rig.Refresh();
        CheckNested(rig, model, open, "Big closed above");
        h = rig.Published().Header;
        Assert(h.LetIn == before.LetIn - TreeGrid.Million - 2 &&
               rig.Window.Keys[(int)(deepAt + 40 - TreeGrid.Million - 2 - h.First)] == viewKey,
            $"moved up a million and two: the page's row still drawn where it was (let in {h.LetIn})");

        // From the live top, held on a run's row (the page's first): the hold set on its open row, the row kept there.
        rig.Ask(0, 20, 0, 20, h.Holds, h.LetIn, top: true);
        (h, keys, depths) = rig.PublishedNested();
        Assert(h.Live, "back at the live top");
        var runRow = Array.FindIndex(depths, d => d > 0);
        var anchor = keys[runRow];
        model.Top.Arrive(4); // (the page drew the live window before these)
        Assert(rig.Answer(rig.Window.Plan(new GridRequest(Guid.Empty, h.First + runRow, 10, 0, 10, h.Holds, h.LetIn), anchor)) |
               (rig.Window.NeedsRest && rig.Answer(rig.Window.PlanRest())), "held on the run's row");
        CheckNested(rig, model, open, "held on a run's row");
        (h, keys, _) = rig.PublishedNested();
        Assert(!h.Live && rig.Window.Hidden == 4 && keys[(int)(runRow - h.First)] == anchor,
            "the run's row kept where the page drew it, four arrived above and not shown");

        // Its open row closed, the page's first row in its run: the open row drawn where that row was.
        var anchorOwner = model.OwnerOf(anchor);
        Assert(rig.Window.CloseRow(anchorOwner) && open.Remove(anchorOwner), "its open row closed");
        rig.Refresh();
        CheckNested(rig, model, open, "the open row of the page's first row closed");
        var (hc, kc) = rig.Published();
        Assert(kc[(int)(runRow + hc.LetIn - h.LetIn - hc.First)] == anchorOwner, $"{anchorOwner} drawn where {anchor} was");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Every row open: the view's rows read open, their runs after them, counted as read — the total at least so many,
    ///     growing as the reader goes; held, runs coming to be counted above the page's first row move every position, and
    ///     the page's scroll, by as many; a row closed — on the wire, a closed run — and opened again; a million rows' run
    ///     read a few rows first, then by slice; the runs counted bounded, the least recently read forgotten; every row
    ///     closed again, the page's rows where they're drawn.
    /// </summary>
    public static Task GridNestedAll()
    {
        var model = new TreeGrid(4, 2_000);
        var rig = new GridRig(model, 200, counted: 0); // (the runs counted: a read's, at the least)
        var closed = new HashSet<long>();
        bool IsOpen(long id) => !closed.Contains(id);
        long Counted() => model.Top.Ids().Sum(id => rig.Window.KnownLength(id) ?? 0); // (the runs read: the window's, and below it)

        // Live, every row opened: each row of the window open, its run after it; the total the rows and the runs read.
        rig.Ask(0, 20, 0, 20);
        rig.Window.SetAllOpen(true);
        rig.Refresh();
        CheckNested(rig, model, IsOpen, "every row open, live");
        var (h, keys, depths) = rig.PublishedNested();
        var tops = keys.Where((_, i) => depths[i] == 0).ToArray();
        Assert(h is { AllOpen: true, Live: true, TotalKind: TotalKind.AtLeast } && h.Total == 2_000 + Counted() &&
               tops.Count(k => rig.Window.KnownLength(k) is not null) >= tops.Length - 1 &&
               tops.All(k => rig.Window.KnownLength(k) is not { } n || n == model.LengthOf(k)),
            $"every row open: the window's runs read, the total {h.Total} at least the rows and the runs read " +
            $"({h.AllOpen}, {h.Live}, {h.TotalKind}; counted {Counted()}; {tops.Count(k => rig.Window.KnownLength(k) is not null)} " +
            $"of {tops.Length} known; wrong: {string.Join(", ", tops.Where(k => rig.Window.KnownLength(k) is { } n && n != model.LengthOf(k)))})");

        // Held further down: more runs counted, the total growing.
        var total = h.Total;
        rig.Ask(400, 20, 10, 10);
        CheckNested(rig, model, IsOpen, "every row open, held at 400");
        (h, _) = rig.Published();
        Assert(!h.Live && h.Total > total && h.TotalKind == TotalKind.AtLeast, $"held, more counted: {total} → {h.Total}");

        // The page asking from a row it shows with rows above it not counted yet: counted now, every position down by their
        // runs, the page's row where it's drawn.
        var (hb, kb) = rig.Published();
        var at = hb.First + 5;
        var pageRow = kb[5];
        rig.Ask(at, 20, 120, 10, hb.Holds, hb.LetIn); // (a hundred and twenty above: into rows not read yet)
        CheckNested(rig, model, IsOpen, "every row open, rows above the page's counted");
        (h, keys, _) = rig.PublishedNested();
        var moved = h.LetIn - hb.LetIn;
        Assert(moved > 0 && keys[(int)(at + moved - h.First)] == pageRow,
            $"runs counted above the page's first row: every position down {moved}, {pageRow} where it's drawn");

        // A row closed, every row open: a closed run on the wire, its run gone; opened again.
        (h, keys, depths) = rig.PublishedNested();
        var k = keys.Where((_, i) => depths[i] == 0).First(id => model.LengthOf(id) > 0);
        Assert(rig.Window.CloseRow(k) && closed.Add(k), "closed");
        rig.Refresh();
        CheckNested(rig, model, IsOpen, "every row open, one closed");
        GridOrder.TryRead(rig.Window.PublishedOrder, out _, out _, out var runBytes);
        Assert(GridOrder.RunsOf<long>(runBytes).Any(r => r.Owner == k && r.Closed && r.Keys.Length == 0) &&
               !rig.PublishedNested().Keys.Any(key => model.OwnerOf(key) == k), $"{k} closed on the wire, its run gone");
        Assert(rig.Window.OpenRow(k) && closed.Remove(k), "opened again");
        rig.Refresh();
        CheckNested(rig, model, IsOpen, "every row open, opened again");

        // A million under Big, every row open: read a few rows first, then by slice — never whole.
        var rows = model.RunRows;
        (h, _) = rig.Published();
        rig.Ask(h.Total - 25, 20, 10, 10, h.Holds, h.LetIn);
        CheckNested(rig, model, IsOpen, "every row open, at the bottom, Big's run");
        Assert(rig.Window.KnownLength(TreeGrid.Big) == TreeGrid.Million && model.RunRows - rows <= 4 * 200,
            $"Big's million counted, a slice read ({model.RunRows - rows} run rows read)");
        Assert(rig.Window.CloseRow(TreeGrid.Big) && closed.Add(TreeGrid.Big), "Big closed");
        rig.Refresh();

        // The runs counted bounded (a read's at the least): jumps here and there, the least recently read forgotten.
        foreach (var to in new[] { 900L, 100, 1_500, 600, 1_200, 300, 1_800 })
        {
            (h, _) = rig.Published();
            rig.Ask(Math.Min(to, h.Total - 1), 20, 60, 60, h.Holds, h.LetIn);
            CheckNested(rig, model, IsOpen, $"every row open, jumped to {to}");
            Assert(rig.Window.OpenRows <= rig.Window.PageCapacity, $"the runs counted {rig.Window.OpenRows}, at most a read's");
        }

        // Every row closed, held: the runs counted above the page's first row gone, the page's row where it's drawn.
        (h, _) = rig.Published();
        at = 700;
        rig.Ask(at, 20, 60, 60, h.Holds, h.LetIn);
        (hb, kb) = rig.Published();
        pageRow = kb[(int)(at - hb.First)];
        rig.Window.SetAllOpen(false);
        rig.Refresh();
        CheckNested(rig, model, _ => false, "every row closed again");
        (h, keys, depths) = rig.PublishedNested();
        moved = h.LetIn - hb.LetIn;
        var drawn = model.OwnerOf(pageRow) is >= 0 and var owner ? owner : pageRow; // (a run's row: its open row, where it was)
        Assert(h is { AllOpen: false, Runs: false } && depths.All(d => d == 0) && moved < 0 &&
               keys[(int)(at + moved - h.First)] == drawn,
            $"every row closed: no runs, every position up {-moved}, {drawn} drawn where {pageRow} was");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Rows opened and closed through the session, on the wire: each read at once, the page's replica (verified) the
    ///     view as drawn — the run after its open row, at its depths; a jump into a million rows' run; three remembered at
    ///     most, a fourth forgetting the row open least recently used (above the page's rows: they stay where they're
    ///     drawn), never the window's own; closed, its run gone; every row open, one closed on the wire, then every row
    ///     closed again.
    /// </summary>
    public static async Task GridSessionNested()
    {
        var outlet = NewOutlet(new OutletRegistry());
        var (client, owner) = Connected(outlet);
        var model = new TreeGrid(5, 2_000);
        var open = new HashSet<long>();
        await using var session = new GridSession<long, GridOrderRow>(outlet, model,
            options: new GridOptions { OverscanClimb = TimeSpan.Zero, MaxOpenRows = 3, MaxReadBytes = 48u << 20 }) { Reached = static _ => true };
        GroupReplica? group = null;
        var subscription = client.Subscribe(session.Binding, new SubscriptionHandler { Group = (_, g) => group = g });
        Pump(outlet, owner, client);

        async Task Ask(long first, int count, int before, int after)
        {
            var replica = ReplicaOf(group);
            var request = new GridRequest(Guid.CreateVersion7(), first, count, before, after, replica?.Header.Holds ?? 0,
                replica?.Header.LetIn ?? 0);
            var answered = client.RendezvousAsync(CorrelationOf(request.Id), subscription.TapId);
            session.Request(request);
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return answered.IsCompleted && session.Settled;
            }, "the request answered, its rest too");
            Pump(outlet, owner, client);
        }

        async Task Toggle(long id, bool shown)
        {
            var answers = session.Answers;
            Assert(session.SetOpen(id, shown) && session.IsOpen(id) == shown, $"{id} {(shown ? "opened" : "closed")}");
            if (shown) open.Add(id);
            else open.Remove(id);
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return session.Answers > answers && session.Settled;
            }, "read at once");
            Pump(outlet, owner, client);
        }

        Func<long, bool> isOpen = open.Contains;

        void Expect(long from, string step)
        {
            var replica = NestedReplicaOf(group) ?? throw new Exception($"{step}: no replica");
            var all = NestedView(model, GridView.Natural, isOpen);
            Assert(group!.TableAt(0)!.Verified, $"{step}: the rows replica verified (its checksum the server's)");
            var expected = all.Skip((int)from).Take(replica.Keys.Length).ToArray();
            Assert(replica.Header.First == from && replica.Keys.Length > 0 &&
                   replica.Keys.Zip(replica.Depths).SequenceEqual(expected.Select(r => (r.Key, r.Depth))),
                $"{step}: the view as drawn from {from} ({replica.Keys.Length} rows, first {replica.Header.First})");
            Assert(replica.Rows.Count == replica.Keys.Length, $"{step}: the replica's table the window's rows alone");
        }

        await Ask(0, 20, 0, 20);
        var a = Enumerable.Range(0, 10).Select(i => 1_999L - i).First(id => model.LengthOf(id) > 2);
        var aRun = model.LengthOf(a);
        await Toggle(a, true);
        Expect(0, "live, a row opened");
        Assert(NestedReplicaOf(group)!.Value.Header is { Live: true, Runs: true }, "live, with its run on the wire");

        await Toggle(TreeGrid.Deep, true);
        await Toggle(TreeGrid.Big, true);
        var bigAt = NestedView(model, GridView.Natural, open).FindIndex(r => r == (TreeGrid.Big, 0));
        await Ask(bigAt - 5, 20, 5, 20); // (its run known once read: the session's positions count it from then)
        Expect(bigAt - 10, "held at Big, its run read");
        var middle = bigAt + 1 + 600_000 - 10;
        await Ask(middle + 10, 20, 10, 20);
        Expect(middle, "held in the middle of the million");

        // Three remembered at most: a fourth forgets the row open least recently used, out of the window — a, last held at
        // the top (Deep, opened after it, used again now). Its run above the page's rows goes, the rows kept where drawn.
        var before = NestedReplicaOf(group)!.Value.Header;
        Assert(session.SetOpen(TreeGrid.Deep, true) && session.IsOpen(TreeGrid.Deep), "Deep used again (open already)");
        await Toggle(3, true);
        open.Remove(a);
        Assert(!session.IsOpen(a) && session.IsOpen(TreeGrid.Deep) && session.IsOpen(TreeGrid.Big), "a forgotten; Deep and Big kept");
        Expect(middle - aRun, "a forgotten above: its run gone");
        Assert(NestedReplicaOf(group)!.Value.Header.LetIn == before.LetIn - aRun,
            $"every position up by its run, {aRun}: the page's rows where they're drawn");

        // Big, the window's (the window's rows its run's), outlives rows used since: two more forget Deep, then 3, below.
        await Toggle(2, true);
        await Toggle(1, true);
        open.Remove(TreeGrid.Deep);
        open.Remove(3);
        Assert(session.IsOpen(TreeGrid.Big) && session.IsOpen(1) && session.IsOpen(2) && !session.IsOpen(TreeGrid.Deep) &&
               !session.IsOpen(3), "Deep and 3 forgotten, Big kept");
        Expect(middle - aRun, "rows forgotten below: the window as it was");

        await Toggle(TreeGrid.Big, false);
        var replica = NestedReplicaOf(group)!.Value;
        Assert(replica.Keys.All(k => model.OwnerOf(k) != TreeGrid.Big) && replica.Header.Total < 3_000,
            "closed: its run gone, the window back among the view's rows, the total without it");

        // Every row open, at the live top: the replica's rows each followed by its run (all above read: the top's); one
        // closed (Big too: its million), on the wire; every row closed again.
        await Ask(0, 20, 0, 20);
        var closed = new HashSet<long>();
        isOpen = id => !closed.Contains(id);
        async Task All(bool shown)
        {
            var answers = session.Answers;
            Assert(session.SetAllOpen(shown) && session.AllOpen == shown && session.IsOpen(1_500) == shown,
                $"every row {(shown ? "opened" : "closed")}");
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return session.Answers > answers && session.Settled;
            }, "read at once");
            Pump(outlet, owner, client);
        }

        await All(true);
        closed.Add(TreeGrid.Big);
        Assert(session.SetOpen(TreeGrid.Big, false) && !session.IsOpen(TreeGrid.Big), "Big closed, every row open");
        Expect(0, "every row open, live");
        Assert(NestedReplicaOf(group)!.Value.Header is { AllOpen: true, Live: true, TotalKind: TotalKind.AtLeast },
            "every row open on the wire, the total at least so many");
        var shownRows = NestedReplicaOf(group)!.Value;
        var c = shownRows.Keys.Where((_, i) => shownRows.Depths[i] == 0).First(id => model.LengthOf(id) > 0);
        await Toggle(c, false); // (closed: as the rest no more — remembered)
        closed.Add(c);
        Expect(0, "every row open, one closed");
        Assert(!session.IsOpen(c) && session.IsOpen(1_500), $"{c} closed, the rest open");

        await All(false);
        isOpen = _ => false;
        Expect(0, "every row closed");
        Assert(NestedReplicaOf(group)!.Value.Header is { AllOpen: false, Runs: false } && !session.IsOpen(c), "every row closed on the wire");
        Assert(model.Top.Budget == 48u << 20 && model.RunBudget == 48u << 20,
            $"every read, of the view and of runs, carries the host's budget (GridOptions.MaxReadBytes: {model.Top.Budget}, {model.RunBudget})");
    }

    public static Task GridNestedOracle()
    {
        string[] columns = ["placed", "price", "qty", "status"];
        int checks = 0, everyOpen = 0, kept = 0;
        for (var seed = 1; seed <= 6; seed++)
        {
            var rnd = new Random(seed);
            var model = new TreeGrid(seed, 600);
            var open = new HashSet<long>(); // (the rows apart from the rest: opened — every row open, closed)
            var allOpen = false;
            bool IsOpen(long id) => open.Contains(id) != allOpen;
            var remembered = seed > 4 ? new GridRecentSet<long>(3) : null; // (the last two: three at most, as a session has them)
            var forgotten = new List<long>();
            var rig = new GridRig(model, 160, counted: seed > 4 ? 0 : 4096); // (the last two: the runs counted, a read's at most)
            rig.Window.ByKey = true;
            var stage = "";
            rig.BetweenStages = () => CheckNested(rig, model, IsOpen, $"seed {seed}, {stage}: a jump's view, alone");
            long first = 0;
            int holds = 0, views = 0;
            long letIn = 0;
            for (var step = 0; step < 300; step++)
            {
                stage = $"step {step}";
                var roll = rnd.Next(100);
                if (roll < 40)
                {
                    var total = rig.Published().Header.Total;
                    first = roll < 30 ? Math.Max(0, first + rnd.Next(-30, 31)) // a short scroll
                        : roll < 37 ? rnd.Next(0, (int)Math.Max(1, total + 50)) // a jump, past the end too
                        : 0; // back to the top
                    rig.Ask(first, 20, rnd.Next(0, 21), rnd.Next(0, 40), holds, letIn);
                }
                else if (roll < 60)
                {
                    // A row of the view in the window opened or closed (as a reader would), or one anywhere (as .NET might).
                    var (_, keys, depths) = rig.PublishedNested();
                    var mine = keys.Where((_, i) => depths[i] == 0).ToArray();
                    var id = mine.Length > 0 && rnd.Next(4) > 0 ? mine[rnd.Next(mine.Length)] : rnd.Next(0, 600);
                    if (id == TreeGrid.Big) id = TreeGrid.Deep;
                    var shown = IsOpen(id);
                    if (open.Remove(id)) // (apart: as the rest again)
                    {
                        remembered?.Close(id);
                        if (shown) rig.Window.CloseRow(id);
                        else rig.Window.OpenRow(id);
                    }
                    else if (remembered is null || remembered.Open(id, forgotten))
                    {
                        var held = rig.Window.ToggledHeld().ToArray();
                        foreach (var gone in forgotten) // (as the rest again, first: as a session tells its window)
                        {
                            Assert(!held.Contains(gone), $"seed {seed}, step {step}: {gone} forgotten, not one the window holds");
                            if (allOpen) rig.Window.OpenRow(gone);
                            else rig.Window.CloseRow(gone);
                            open.Remove(gone);
                        }

                        forgotten.Clear();
                        if (shown) rig.Window.CloseRow(id);
                        else rig.Window.OpenRow(id);
                        open.Add(id);
                        var big = allOpen ? 1 : 0; // (every row open: Big closed, and not remembered)
                        Assert(remembered is null || (remembered.Count <= Math.Max(3, held.Length + 1) &&
                                                      remembered.Count == open.Count - big &&
                                                      open.All(k => (allOpen && k == TreeGrid.Big) || remembered.Contains(k))),
                            $"seed {seed}, step {step}: three remembered, or as many as the window holds and one");
                    }

                    rig.Refresh();
                }
                else if (roll < 82)
                {
                    // The source moving: orders arriving, changing, going; open runs growing and shrinking.
                    var window = rig.Window.Keys.ToArray().Where(k => model.OwnerOf(k) < 0).ToArray();
                    model.Top.Arrive(rnd.Next(0, 3));
                    for (var n = rnd.Next(0, 3); n > 0; n--)
                        model.Top.Update(window.Length > 0 && rnd.Next(2) == 0 ? window[rnd.Next(window.Length)] : rnd.Next(0, 600),
                            rnd.Next(0, 1_000));
                    if (rnd.Next(4) == 0) model.Top.Delete(window.Length > 0 ? window[rnd.Next(window.Length)] : rnd.Next(0, 600));
                    var growing = allOpen ? window.Where(k => IsOpen(k) && k != TreeGrid.Big).ToArray() : open.ToArray();
                    foreach (var id in growing.Where(_ => rnd.Next(3) == 0).ToArray())
                        if (rnd.Next(3) > 0) model.Grow(id, rnd.Next(1, 4));
                        else model.Shrink(id, rnd.Next(1, 3));
                    rig.Refresh();
                }
                else if (roll < 86)
                {
                    // Every row opened, or closed: the rows apart from the rest forgotten, as a session forgets them.
                    allOpen = !allOpen;
                    rig.Window.SetAllOpen(allOpen);
                    open.Clear();
                    remembered?.Clear();
                    if (allOpen) // (Big's million rows closed: the oracle's own draw is too slow with them)
                    {
                        rig.Window.CloseRow(TreeGrid.Big);
                        open.Add(TreeGrid.Big);
                    }

                    rig.Refresh();
                }
                else if (roll < 90)
                {
                    var keep = !rig.Published().Header.Live && rnd.Next(2) == 0; // (held: the place kept, half the time)
                    var sort = Enumerable.Range(0, rnd.Next(0, 3))
                        .Select(_ => new SortKey(columns[rnd.Next(columns.Length)], rnd.Next(2) == 0)).ToArray();
                    var conditions = Enumerable.Range(0, rnd.Next(0, 2)).Select(_ => rnd.Next(2) switch
                    {
                        0 => ("status", '=', (long)rnd.Next(4)),
                        _ => ("qty", '<', (long)rnd.Next(100, 1_000))
                    }).ToArray();
                    rig.View = new GridView(sort, conditions.Length == 0 ? null : new GridFilter(conditions));
                    rig.Window.Reset(++views, keep ? 20 : 0);
                    rig.Window.ByKey = model.NewestFirstByKey(rig.View);
                    rig.Refresh();
                    var (placed, _) = rig.Published();
                    first = placed.Live ? 0 : placed.First; // (the page goes where the new view's first order puts it)
                    kept += keep && !placed.Live ? 1 : 0;
                }
                else
                {
                    rig.Refresh();
                }

                CheckNested(rig, model, IsOpen, $"seed {seed}, step {step}");
                remembered?.Shown(rig.Window.ToggledHeld());
                checks++;
                everyOpen += allOpen ? 1 : 0;

                // The page applies the order: its scroll moves by what the positions moved by in this hold.
                var (h, _) = rig.Published();
                if (h.Holds == holds && h.LetIn != letIn) first = Math.Max(0, first + h.LetIn - letIn);
                (holds, letIn) = (h.Holds, h.LetIn);
            }
        }

        Console.WriteLine($"  nested grid oracle: {checks:N0} windows checked, {everyOpen:N0} with every row open; {kept} views kept in place");
        return Task.CompletedTask;
    }
}
