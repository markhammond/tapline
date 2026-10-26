using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Tapline;
using Tapline.Client;
using Tapline.Grid;

/// <summary>
///     Tapline.Grid: a grid's window kept by the server — live top and held, rows let in, refusals — against an oracle
///     worked out apart from the source; its session on the wire, its cadences on a clock moved by hand, its bounds,
///     cancellation and shutdown; what it allocates, holds and costs on the wire.
/// </summary>
internal static partial class T
{
    // ───────────── the oracle: a view worked out by brute force, apart from the source's own sorting ─────────────

    private static long[] GridOracle(ComputedGrid model, GridView view)
    {
        var filter = view.Filter as GridFilter;
        var rows = model.Ids().Select(id => (Id: id, Row: model.RowOf(id))).Where(r => OraclePasses(filter, r.Id, r.Row));
        IOrderedEnumerable<(long Id, GridOrderRow Row)>? ordered = null;
        foreach (var (column, descending) in view.Sort)
        {
            Func<(long Id, GridOrderRow Row), long> by = r => OracleColumn(r.Row, r.Id, column);
            ordered = ordered is null
                ? descending ? rows.OrderByDescending(by) : rows.OrderBy(by)
                : descending ? ordered.ThenByDescending(by) : ordered.ThenBy(by);
        }

        return (ordered is null ? rows.OrderByDescending(r => r.Id) : ordered.ThenByDescending(r => r.Id))
            .Select(r => r.Id).ToArray();
    }

    private static long OracleColumn(GridOrderRow row, long id, string column)
    {
        if (column == "placed") return row.Placed;
        if (column == "price") return row.Price;
        if (column == "qty") return row.Qty;
        if (column == "version") return row.Version;
        if (column == "side") return row.Side;
        return column == "status" ? row.Status : id;
    }

    private static bool OraclePasses(GridFilter? filter, long id, GridOrderRow row)
    {
        return filter is null || filter.All.All(c =>
        {
            var v = OracleColumn(row, id, c.Column);
            return c.Op == '<' ? v < c.Value : c.Op == '>' ? v > c.Value : v == c.Value;
        });
    }

    private static bool SameRow<TRow>(in TRow a, in TRow b) where TRow : unmanaged
    {
        return MemoryMarshal.AsBytes(new ReadOnlySpan<TRow>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<TRow>(in b)));
    }

    /// <summary>A window with its own table and order, and a source: asked and answered on this thread, no loop, no clock.</summary>
    private sealed class GridRig
    {
        public readonly LastValueSource Order = new();
        public readonly GridPage<long, GridOrderRow> Page;
        public readonly Table<long, GridOrderRow> Rows;
        public readonly IGridSource<long, GridOrderRow> Source;
        public readonly GridWindow<long, GridOrderRow> Window;
        public GridView View = GridView.Natural;

        public GridRig(IGridSource<long, GridOrderRow> source, int maxRows, int revealNear = 200, int revealAtOnce = 30,
            int log = 1024)
        {
            Source = source;
            Rows = new Table<long, GridOrderRow>("grid rows", log);
            Window = new GridWindow<long, GridOrderRow>(Rows, Order, maxRows, maxRows, revealNear, revealAtOnce);
            Page = new GridPage<long, GridOrderRow>(Window.PageCapacity);
        }

        public ComputedGrid Model => (ComputedGrid)Source;

        /// <summary>Called between a jump's two stages: its view published, the rest not yet read.</summary>
        public Action? BetweenStages;

        /// <summary>
        ///     The page's request, in the positions of the order it last applied (by default, the last published): a jump
        ///     answered in its two stages, as the session does, its view first.
        /// </summary>
        public bool Ask(long first, int count, int before, int after, int? holds = null, long? letIn = null)
        {
            if (holds is null || letIn is null)
            {
                var (h, _) = Published();
                (holds, letIn) = (holds ?? h.Holds, letIn ?? h.LetIn);
            }

            var published = Answer(Window.Plan(new GridRequest(Guid.Empty, first, count, before, after, holds.Value, letIn.Value)));
            if (!Window.NeedsRest) return published;
            BetweenStages?.Invoke();
            return Answer(Window.PlanRest()) | published;
        }

        public bool Refresh()
        {
            return Window.PlanRefresh(out var read) && Answer(read);
        }

        public bool Answer(GridRead<long, GridOrderRow> read)
        {
            Page.Clear();
            var answer = Source.ReadAsync(View, read, Page, CancellationToken.None).GetAwaiter().GetResult();
            return Window.Apply(answer, Page);
        }

        public (GridOrderHeader Header, long[] Keys) Published()
        {
            return GridOrder.TryRead(Window.PublishedOrder, out var h, out var keys)
                ? (h, MemoryMarshal.Cast<byte, long>(keys).ToArray())
                : (default, []);
        }

        public Dictionary<long, GridOrderRow> TableRows()
        {
            var rows = new List<KeyValuePair<long, GridOrderRow>>();
            Rows.CopyTo(rows);
            return rows.ToDictionary();
        }
    }

    /// <summary>
    ///     The window as published, against the oracle: the table holds its rows and no others, each as the source has it
    ///     now; its keys a run of the view's order (none twice, none skipped, each passing the filter), each at its rank —
    ///     its view position plus the rows above not shown; its total the view's (held: within 1 %).
    /// </summary>
    private static void CheckWindow(GridRig rig, string step)
    {
        var (h, keys) = rig.Published();
        var table = rig.TableRows();
        if (rig.Window.PublishedOrder.IsEmpty)
        {
            Assert(table.Count == 0, $"{step}: nothing published, no rows");
            return;
        }

        Assert(table.Count == keys.Length + rig.Window.Lingering && keys.All(table.ContainsKey),
            $"{step}: the table holds the window's rows and no others but those left by a view read first " +
            $"({table.Count} rows, {keys.Length} keys, {rig.Window.Lingering} left)");
        foreach (var key in keys)
            Assert(SameRow(table[key], rig.Model.RowOf(key)), $"{step}: row {key} as the source has it now");
        var order = GridOracle(rig.Model, rig.View);
        var rank = new Dictionary<long, int>(order.Length);
        for (var i = 0; i < order.Length; i++) rank[order[i]] = i;
        var hidden = h.Live ? 0 : rig.Window.Hidden;
        for (var i = 0; i < keys.Length; i++)
        {
            Assert(rank.TryGetValue(keys[i], out var r), $"{step}: {keys[i]} is in the view (passes its filter)");
            Assert(r == h.First + i + hidden,
                $"{step}: {keys[i]} at rank {r}, not its view position {h.First + i} plus {hidden} not shown");
        }

        var shown = order.Length - hidden;
        Assert(h.Live ? h.Total == order.Length : Math.Abs(h.Total - shown) * 100 <= h.Total,
            $"{step}: a total of {h.Total} for {shown} rows in view positions");
    }

    /// <summary>
    ///     For the JS tests (tapline-grid.js against frames as the server writes them): a grid's watch, step by step —
    ///     the live top, arrivals, a hold, arrivals above (nothing sent), a row changed, rows let in, back to the top —
    ///     each step's frames, and the order and changed keys the page should then have (none: nothing sent).
    /// </summary>
    private static object GridFixture(OutletRegistry registry, byte[] scratch)
    {
        var outlet = NewOutlet(registry);
        var source = new ComputedGrid(8, 500);
        var rig = new GridRig(source, 60);
        var tap = outlet.Watch("grid", WatchMember.Of(rig.Rows), WatchMember.Of(rig.Order));
        outlet.Activate(tap.Id);
        var steps = new List<object>();
        var shown = rig.TableRows();

        void Step(string what, Action act)
        {
            var (version, order) = (rig.Rows.Version, rig.Window.PublishedOrder.ToArray());
            act();
            var frames = new List<string>();
            for (int n; (n = outlet.Build(scratch)) > 0;) frames.Add(Convert.ToBase64String(scratch, 0, n));
            var (h, keys) = rig.Published();
            var now = rig.TableRows();
            var changed = now.Keys.Where(k => !shown.TryGetValue(k, out var was) || !SameRow(was, now[k]))
                .Concat(shown.Keys.Where(k => !now.ContainsKey(k))).OrderBy(k => k).Select(k => k.ToString()).ToArray();
            var sent = rig.Rows.Version != version || !rig.Window.PublishedOrder.SequenceEqual(order);
            steps.Add(new
            {
                what, frames, sent,
                expect = new
                {
                    h.Live, h.First, h.Total, h.LetIn, h.Holds, keys = keys.Select(k => k.ToString()).ToArray(), changed,
                    qty = keys.Select(k => now[k].Qty).ToArray()
                }
            });
            shown = now;
        }

        Step("the live top", () => rig.Ask(0, 10, 0, 10, 0, 0));
        Step("arrivals at the top", () =>
        {
            source.Arrive(2);
            rig.Refresh();
        });
        Step("held", () => rig.Ask(40, 10, 5, 10, 0, 0));
        Step("arrivals above, held: nothing sent", () =>
        {
            source.Arrive(3);
            rig.Refresh();
        });
        Step("a row in view changed", () =>
        {
            source.Update(rig.Window.Keys[4], 4_321);
            rig.Refresh();
        });
        Step("near the top: the three let in", () => rig.Ask(20, 10, 10, 10, 1, 0));
        Step("back at the top", () => rig.Ask(0, 10, 0, 10, 1, 3));
        return new { watchId = tap.Id, steps };
    }

    // ───────────── rules ─────────────

    public static Task GridRules()
    {
        // A held total: kept while within 1 % of the one held, taken afresh past it, at each boundary.
        var held = new HeldTotal();
        Assert(held.Hold(1_000) == 1_000, "the first total taken");
        Assert(held.Hold(1_010) == 1_000 && held.Hold(990) == 1_000, "1 % either way: held");
        Assert(held.Hold(1_011) == 1_011, "a row past 1 % up: taken afresh");
        held.Clear();
        held.Hold(1_000);
        Assert(held.Hold(989) == 989, "a row past 1 % down: taken afresh");
        held.Set(5_000);
        Assert(held.Hold(5_040) == 5_000, "set where the rows ended: held from there");
        held.Shift(30);
        Assert(held.Hold(5_030) == 5_030, "rows let in: the total grows by as many");
        held.Clear();
        Assert(held.Hold(7) == 7, "cleared: the next taken afresh");

        // A read for the source's changes may begin its cadence after the last read began: a microsecond before, not;
        // on it, and a microsecond after, yes.
        const long every = 100_000_000, readAt = 5_000_000_000; // (nanoseconds)
        Assert(!GridSession<long, GridOrderRow>.IsDue(readAt + every - 1_000, readAt, every), "a microsecond before: not due");
        Assert(GridSession<long, GridOrderRow>.IsDue(readAt + every, readAt, every), "on it: due");
        Assert(GridSession<long, GridOrderRow>.IsDue(readAt + every + 1_000, readAt, every), "a microsecond after: due");

        // The order value: written and read back; another format, a count that doesn't fit, cut short: not one.
        var keys = new long[] { 7, 5, 3 };
        var bytes = new byte[GridOrder.SizeOf(3, 8)];
        var header = new GridOrderHeader
        {
            Live = false, Stale = true, TotalKind = TotalKind.AtLeast, First = 1_234_567_890_123, Total = 9_876_543_210,
            LetIn = 30, Holds = 4
        };
        Assert(GridOrder.Write<long>(bytes, header, keys) == 64, "40 bytes of header and 8 a key");
        Assert(GridOrder.TryRead(bytes, out var back, out var keyBytes) &&
               back == header with { KeySize = 8, Count = 3 } &&
               MemoryMarshal.Cast<byte, long>(keyBytes).SequenceEqual(keys), "read back as written");
        var other = (byte[])bytes.Clone();
        other[0] = 2;
        Assert(!GridOrder.TryRead(other, out _, out _), "another format: not one");
        Assert(!GridOrder.TryRead(bytes.AsSpan(0, 63), out _, out _), "cut short: not one");
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 4);
        Assert(!GridOrder.TryRead(bytes, out _, out _), "more keys than it holds: not one");
        return Task.CompletedTask;
    }

    // ───────────── the window: live and held ─────────────

    public static Task GridWindowLiveHeld()
    {
        var source = new ComputedGrid(3, 2_000);
        var rig = new GridRig(source, 120);

        // The live top: the first rows, at their ranks.
        rig.Ask(0, 22, 0, 22);
        CheckWindow(rig, "the live top");
        var (h, keys) = rig.Published();
        var order = GridOracle(source, rig.View);
        Assert(h.Live && h.First == 0 && keys.SequenceEqual(order[..44]) && h.Total == 2_000,
            "the live top: the first 44 rows, every row counted");

        // Rows arriving at the live top push the rest down: three in at the top, three out at the bottom.
        source.Arrive(3);
        Assert(rig.Refresh(), "arrivals at the live top: published");
        CheckWindow(rig, "arrivals at the live top");
        (h, keys) = rig.Published();
        order = GridOracle(source, rig.View);
        Assert(keys.SequenceEqual(order[..44]) && h.Total == 2_003, "three in at the top, three gone at the bottom");

        // Scrolled away: held; positions as they were.
        rig.Ask(100, 22, 5, 22);
        CheckWindow(rig, "held");
        (h, keys) = rig.Published();
        Assert(!h.Live && h.Holds == 1 && h.First == 95 && keys.SequenceEqual(order[95..144]),
            "held: rows 95 on, at their ranks, the first hold");

        // Held, rows arriving above: counted, not shown; nothing goes out.
        var orderBytes = rig.Window.PublishedOrder.ToArray();
        var version = rig.Rows.Version;
        source.Arrive(7);
        Assert(!rig.Refresh(), "held, rows arriving above: nothing published");
        CheckWindow(rig, "held, arrivals above");
        Assert(rig.Window.Above == 7 && rig.Rows.Version == version && rig.Window.PublishedOrder.SequenceEqual(orderBytes),
            "seven above, counted; the rows and the order as they were");

        // Held, a row in the window changing: that row goes out, and the order stays.
        source.Update(keys[10], 4_242);
        Assert(rig.Refresh(), "a row in the window changed: published");
        CheckWindow(rig, "held, a row changed");
        Assert(rig.Rows.Version == version + 1 && rig.Window.PublishedOrder.SequenceEqual(orderBytes) &&
               rig.TableRows()[keys[10]].Qty == 4_242, "the row alone: the order as it was");

        // Held, the window's first row deleted, and two rows above it: the rows in view keep their places.
        var firstKey = keys[0];
        var view = rig.Window.Keys.ToArray();
        source.Delete(firstKey);
        order = GridOracle(source, rig.View);
        source.Delete(order[3]);
        source.Delete(order[4]);
        rig.Refresh();
        CheckWindow(rig, "held, rows deleted in the window and above");
        (h, keys) = rig.Published();
        Assert(keys[0] == view[1] && h.First == 95 && rig.Window.Above == 5,
            "the row below took the deleted one's place; five above (seven arrived, two gone)");

        // Far from the window: placed by rank, past the rows not shown — its view first, alone, then the rest.
        (GridOrderHeader Header, long[] Keys) viewFirst = (default, []);
        rig.BetweenStages = () =>
        {
            CheckWindow(rig, "held, far: its view");
            viewFirst = rig.Published();
        };
        rig.Ask(1_500, 22, 0, 22);
        rig.BetweenStages = null;
        CheckWindow(rig, "held, far");
        (h, keys) = rig.Published();
        order = GridOracle(source, rig.View);
        Assert(viewFirst.Header.First == 1_500 && viewFirst.Keys.Length == 22 && viewFirst.Keys.SequenceEqual(order[1_505..1_527]),
            "a jump: its view published first, alone (22 rows)");
        Assert(!h.Live && h.First == 1_500 && keys.Length == 44 && Array.IndexOf(order, keys[0]) == 1_505,
            "then the rest: rank 1,505 at position 1,500, past five above, the 22 below too");

        // Back to the top: live, what arrived shown.
        rig.Ask(0, 22, 0, 22);
        CheckWindow(rig, "back at the top");
        (h, keys) = rig.Published();
        Assert(h.Live && rig.Window.Above == 0 && keys.SequenceEqual(order[..44]), "live again: the top rows, arrivals shown");

        // Refused: the rows as they were, marked stale, and why; the next answer clears it.
        var read = rig.Window.Plan(new GridRequest(Guid.Empty, 0, 22, 0, 22, h.Holds, h.LetIn));
        Assert(rig.Window.Apply(GridAnswer.Refused("Busy: try again."), rig.Page), "a refusal: the order goes out, stale");
        var (stale, staleKeys) = rig.Published();
        Assert(stale.Stale && staleKeys.SequenceEqual(keys) && rig.Window.Refusal == "Busy: try again.",
            "stale, the rows as they were, the reason kept");
        Assert(!rig.Window.Apply(GridAnswer.Refused("Busy: try again."), rig.Page), "refused again: nothing more goes out");
        rig.Answer(read);
        Assert(!rig.Published().Header.Stale && rig.Window.Refusal is null, "answered: no longer stale");

        // A source answering a key twice: refused, the table as it was.
        version = rig.Rows.Version;
        rig.Window.Plan(new GridRequest(Guid.Empty, 0, 4, 0, 0, h.Holds, h.LetIn));
        rig.Page.Clear();
        rig.Page.Add(keys[0], source.RowOf(keys[0]));
        rig.Page.Add(keys[0], source.RowOf(keys[0]));
        rig.Window.Apply(new GridAnswer { Total = 2, TotalKind = TotalKind.Exact }, rig.Page);
        Assert(rig.Window.Refusal == "The source answered a key twice." && rig.Rows.Version == version &&
               rig.Published().Header.Stale, "a key twice: refused, the rows as they were");
        rig.Refresh();

        // A new sort and filter: the top of it, the old window's rows gone from the table.
        rig.View = new GridView([new SortKey("price", true), new SortKey("qty")], new GridFilter(("status", '=', 1)));
        rig.Window.Reset(1);
        Assert(rig.Refresh(), "the new view's top: published");
        CheckWindow(rig, "a new sort and filter");
        (h, keys) = rig.Published();
        order = GridOracle(source, rig.View);
        Assert(h.Live && h.View == 1 && keys.SequenceEqual(order[..44]), "live, at the top of the new order, of view 1");

        // A key brought into view, wherever it is: a place of its own, rows about it.
        var holds = h.Holds;
        var target = order[300];
        rig.Answer(rig.Window.PlanLocate(target, 5, 10));
        CheckWindow(rig, "a key located");
        (h, keys) = rig.Published();
        Assert(rig.Window.Located == 300 && !h.Live && h.Holds == holds + 1 && h.First == 295 &&
               keys.SequenceEqual(order[295..310]), "found at 300: held there, five above it and ten on");
        source.Delete(target);
        var before = rig.Window.PublishedOrder.ToArray();
        Assert(!rig.Answer(rig.Window.PlanLocate(target, 5, 10)) && rig.Window.Located is null &&
               rig.Window.PublishedOrder.SequenceEqual(before), "a key the view hasn't: not found, the window as it was");
        return Task.CompletedTask;
    }

    // ───────────── letting rows in, at each boundary ─────────────

    public static Task GridLetIn()
    {
        var source = new ComputedGrid(5, 2_000);
        var rig = new GridRig(source, 200);
        rig.Ask(0, 22, 0, 22);
        rig.Ask(300, 22, 40, 22);
        source.Arrive(50);
        rig.Refresh();
        CheckWindow(rig, "fifty above");
        Assert(rig.Window.Above == 50 && rig.Window.LetIn == 0, "fifty arrived above: counted, none let in");

        // The view's first row at 200: not near enough. At 199: at most thirty at a time.
        rig.Ask(200, 22, 40, 22);
        Assert(rig.Window.LetIn == 0, "at 200: none let in");
        rig.Ask(199, 22, 40, 22, letIn: 0);
        CheckWindow(rig, "199: let in");
        var (h, _) = rig.Published();
        Assert(rig.Window.LetIn == 30 && rig.Window.Above == 20 && h.LetIn == 30 && h.First == 159 + 30,
            "at 199: thirty let in, twenty above still; positions moved down by thirty");

        // Asked again by a page that hasn't applied that: no more, and its positions read as before the thirty.
        rig.Ask(199, 22, 40, 22, letIn: 0);
        CheckWindow(rig, "asked again, the thirty not applied");
        Assert(rig.Window.LetIn == 30 && rig.Published().Header.First == 189, "no more until the page has moved its scroll");

        // Applied (its scroll moved down by thirty, to 229): no longer near the top.
        rig.Ask(229, 22, 40, 22, letIn: 30);
        Assert(rig.Window.LetIn == 30, "at 229: none");

        // Up to 150, fetching ten above: no more than it fetches above.
        rig.Ask(150, 22, 10, 22, letIn: 30);
        CheckWindow(rig, "150, ten above");
        Assert(rig.Window.LetIn == 40 && rig.Window.Above == 10, "ten let in: as many as it fetches above");

        // Fetching none above: none.
        rig.Ask(150, 22, 0, 22, letIn: 40);
        Assert(rig.Window.LetIn == 40, "none fetched above: none let in");

        // A window reaching the top takes the rows let in.
        rig.Ask(20, 22, 20, 22, letIn: 40);
        CheckWindow(rig, "a window reaching the top");
        (h, var keys) = rig.Published();
        Assert(rig.Window.LetIn == 50 && rig.Window.Above == 0 && h.First == 0,
            "the last ten let in, the window from the top: they're in it");

        // None to let in: none.
        rig.Ask(20, 22, 20, 22, letIn: 50);
        Assert(rig.Window.LetIn == 50, "nothing above: nothing let in");

        // Back to the top: live, positions ranks again.
        rig.Ask(0, 22, 0, 22, letIn: 50);
        (h, keys) = rig.Published();
        Assert(h.Live && h.LetIn == 0 && keys.SequenceEqual(GridOracle(source, rig.View)[..44]), "live: let in reset");
        return Task.CompletedTask;
    }

    // ───────────── against the oracle: random scrolling, changes, sorts and filters ─────────────

    public static Task GridOracleRuns()
    {
        string[] columns = ["placed", "price", "qty", "status", "side", "version"];
        var checks = 0;
        for (var seed = 1; seed <= 5; seed++)
        {
            var rnd = new Random(seed);
            var source = new ComputedGrid(seed, 2_000);
            var rig = new GridRig(source, 120);
            var stage = "";
            rig.BetweenStages = () => CheckWindow(rig, $"seed {seed}, {stage}: a jump's view, alone");
            long first = 0;
            int holds = 0, views = 0;
            long letIn = 0;
            for (var step = 0; step < 400; step++)
            {
                stage = $"step {step}";
                var roll = rnd.Next(100);
                if (roll < 60)
                {
                    first = roll < 45 ? Math.Max(0, first + rnd.Next(-30, 31)) // a short scroll
                        : roll < 55 ? rnd.Next(0, 2_600) // a jump, past the end too
                        : 0; // back to the top
                    rig.Ask(first, 22, rnd.Next(0, 23), rnd.Next(0, 45), holds, letIn);
                }
                else if (roll < 85)
                {
                    // The source moving: rows arriving, changing, going — some in the window.
                    var window = rig.Window.Keys.ToArray();
                    source.Arrive(rnd.Next(0, 4));
                    for (var n = rnd.Next(0, 4); n > 0; n--)
                        source.Update(window.Length > 0 && rnd.Next(2) == 0 ? window[rnd.Next(window.Length)] : rnd.Next(0, 2_000),
                            rnd.Next(0, 1_000));
                    for (var n = rnd.Next(0, 3); n > 0; n--)
                        source.Delete(window.Length > 0 && rnd.Next(2) == 0 ? window[rnd.Next(window.Length)] : rnd.Next(0, 2_000));
                    rig.Refresh();
                }
                else if (roll < 90)
                {
                    var sort = Enumerable.Range(0, rnd.Next(0, 4))
                        .Select(_ => new SortKey(columns[rnd.Next(columns.Length)], rnd.Next(2) == 0)).ToArray();
                    var conditions = Enumerable.Range(0, rnd.Next(0, 3)).Select(_ => rnd.Next(4) switch
                    {
                        0 => ("status", '=', (long)rnd.Next(4)),
                        1 => ("qty", '<', (long)rnd.Next(100, 1_000)),
                        2 => ("side", '=', (long)rnd.Next(2)),
                        _ => ("price", '>', (long)rnd.Next(10_000, 14_000))
                    }).ToArray();
                    rig.View = new GridView(sort, conditions.Length == 0 ? null : new GridFilter(conditions));
                    rig.Window.Reset(++views);
                    rig.Refresh();
                    Assert(rig.Published().Header.View == views, $"seed {seed}, step {step}: the order of view {views}");
                    first = 0;
                }
                else
                {
                    rig.Refresh();
                }

                CheckWindow(rig, $"seed {seed}, step {step}");
                checks++;

                // The page applies the order: its scroll moves down by what was let in.
                var (h, _) = rig.Published();
                if (h.Holds == holds && h.LetIn > letIn) first += h.LetIn - letIn;
                (holds, letIn) = (h.Holds, h.LetIn);
            }

            // Held still, nothing changing: every window anywhere the oracle's, exactly (from the top: a hold afresh).
            var still = GridOracle(source, rig.View);
            rig.Ask(0, 22, 0, 22, holds, letIn);
            (holds, letIn) = (rig.Published().Header.Holds, rig.Published().Header.LetIn);
            for (var i = 0; i < 60 && still.Length > 1; i++)
            {
                var at = rnd.Next(1, still.Length);
                int count = rnd.Next(1, 23), above = rnd.Next(0, 23), below = rnd.Next(0, 45);
                rig.Ask(at, count, above, below, holds, letIn);
                var (h, keys) = rig.Published();
                (holds, letIn) = (h.Holds, h.LetIn);
                if (rig.Window.Hidden != 0) continue; // (a hold begun before the last changes: positions not ranks)
                var from = Math.Max(0, at - above);
                Assert(keys.SequenceEqual(still[(int)Math.Min(from, still.Length)..(int)Math.Min(at + count + below, still.Length)]),
                    $"seed {seed}, held still: the window at {at} the oracle's");
                checks++;
            }
        }

        Console.WriteLine($"  grid oracle: {checks:N0} windows checked");
        return Task.CompletedTask;
    }

    // ───────────── the session: on the wire, its cadence, cancelled, bounded ─────────────

    private static async Task Until(Func<bool> done, string what, int ms = 10_000)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            if (timer.ElapsedMilliseconds > ms) throw new TimeoutException($"Waited {ms} ms for {what}.");
            await Task.Delay(1);
        }
    }

    private static (TaplineClient Client, object Owner) Connected(Outlet outlet)
    {
        var owner = new object();
        outlet.Attach(owner);
        var client = new TaplineClient(new LoopConnection(outlet));
        client.Bind(outlet.Id, outlet.Nonce);
        return (client, owner);
    }

    private static byte[] CorrelationOf(Guid id)
    {
        var bytes = new byte[16];
        id.TryWriteBytes(bytes, true, out _);
        return bytes;
    }

    /// <summary>The page's replica of a grid's watch: its order, and its rows table, by key.</summary>
    private static (GridOrderHeader Header, long[] Keys, Dictionary<long, GridOrderRow> Rows)? ReplicaOf(GroupReplica? group)
    {
        if (group?.TableAt(0) is not { } table || !GridOrder.TryRead(group.ValueAt(1).Span, out var h, out var keys))
            return null;
        var rows = new Dictionary<long, GridOrderRow>();
        foreach (var row in table) rows[row.KeyAs<long>()] = row.RowAs<GridOrderRow>();
        return (h, MemoryMarshal.Cast<byte, long>(keys).ToArray(), rows);
    }

    public static async Task GridSessionWire()
    {
        var outlet = NewOutlet(new OutletRegistry());
        var (client, owner) = Connected(outlet);
        var source = new ComputedGrid(9, 3_000);
        var clock = new ManualClock();
        await using var session = new GridSession<long, GridOrderRow>(outlet, source, time: clock);
        GroupReplica? group = null;
        var cuts = 0;
        var subscription = client.Subscribe(session.Binding, new SubscriptionHandler
        {
            Group = (_, g) =>
            {
                group = g;
                cuts++;
            }
        });
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
                return answered.IsCompleted;
            }, "the request's rendezvous");
            await answered;
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return session.Settled;
            }, "a jump's rest");
            Pump(outlet, owner, client);
        }

        // Each answer in front of the page by its rendezvous: the replica's rows and order the window the oracle's.
        void Expect(int from, int to, bool live, string step)
        {
            var replica = ReplicaOf(group) ?? throw new Exception($"{step}: no replica");
            var order = GridOracle(source, GridView.Natural);
            Assert(group!.TableAt(0)!.Verified, $"{step}: the rows replica verified (its checksum the server's)");
            Assert(replica.Header.Live == live && replica.Keys.SequenceEqual(order[from..to]),
                $"{step}: rows {from} to {to}, in order ({replica.Keys.Length} keys)");
            Assert(replica.Rows.Count == replica.Keys.Length && replica.Keys.All(k => SameRow(replica.Rows[k], source.RowOf(k))),
                $"{step}: the replica's table the window's rows alone, each as the source has it");
        }

        await Ask(0, 22, 0, 22);
        Expect(0, 44, true, "the live top");
        await Ask(400, 22, 10, 22);
        Expect(390, 444, false, "held at 400");

        // Held, rows arriving above: read at the held cadence, and nothing goes out.
        var reads = source.Reads;
        var before = cuts;
        source.Arrive(5);
        await Until(() => clock.NextDue() is not null, "the held cadence armed");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Until(() => session.Status.Above == 5, "five counted above");
        Pump(outlet, owner, client);
        Assert(source.Reads == reads + 1 && cuts == before, "read once at the held cadence; nothing sent");

        // Held, a row in the window changing: one cut, that row.
        var replica = ReplicaOf(group)!.Value;
        source.Update(replica.Keys[3], 77);
        await Until(() => clock.NextDue() is not null, "the held cadence armed again");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Until(() =>
        {
            Pump(outlet, owner, client);
            return cuts == before + 1;
        }, "the changed row sent");
        Assert(ReplicaOf(group)!.Value.Rows[replica.Keys[3]].Qty == 77, "the row as changed, in the replica");

        // Back to the top: live, the five shown.
        await Ask(0, 22, 0, 22);
        Expect(0, 44, true, "back at the top");
        Assert(session.Status is { Live: true, Above: 0, Holds: 1 }, "the status: live, the one hold counted");
    }

    public static async Task GridSessionCadence()
    {
        var outlet = NewOutlet(new OutletRegistry());
        var source = new ComputedGrid(4, 1_000);
        var clock = new ManualClock();
        await using var session = new GridSession<long, GridOrderRow>(outlet, source,
            options: new GridOptions
            {
                LiveEvery = TimeSpan.FromMilliseconds(100), HeldEvery = TimeSpan.FromMilliseconds(500),
                HiddenInterval = TimeSpan.FromSeconds(10)
            }, time: clock);
        outlet.Activate(session.Tap.Id);
        var microsecond = TimeSpan.FromTicks(10);

        session.Request(new GridRequest(Guid.Empty, 0, 10, 0, 0));
        await Until(() => session.Answers == 1, "the first answer");
        var readAt = clock.GetTimestamp();

        // Live: a change read 100 ms after the last read began — not a microsecond before.
        source.Arrive(1);
        await Until(() => clock.NextDue() == readAt + 100_000_000, "armed for 100 ms after the read began");
        clock.Advance(TimeSpan.FromMilliseconds(100) - microsecond);
        await Task.Delay(30);
        Assert(source.Reads == 1, "a microsecond before: not read");
        clock.Advance(microsecond);
        await Until(() => source.Reads == 2, "on it: read");

        // A change a microsecond past the cadence since the last read: read at once.
        await Until(() => session.Answers == 2, "its answer");
        clock.Advance(TimeSpan.FromMilliseconds(100) + microsecond);
        source.Arrive(1);
        await Until(() => source.Reads == 3, "past it: read at once");

        // Held: 500 ms.
        await Until(() => session.Answers == 3, "its answer");
        session.Request(new GridRequest(Guid.Empty, 300, 10, 0, 0));
        await Until(() => session.Answers == 4, "held");
        readAt = clock.GetTimestamp();
        source.Arrive(1);
        await Until(() => clock.NextDue() == readAt + 500_000_000, "held: armed for 500 ms after the read began");
        clock.Advance(TimeSpan.FromMilliseconds(500) - microsecond);
        await Task.Delay(30);
        Assert(source.Reads == 4, "held, a microsecond before: not read");
        clock.Advance(microsecond);
        await Until(() => source.Reads == 5, "held, on it: read");
        await Until(() => session.Answers == 5, "its answer");

        // Hidden: the source's changes not read, the subscription's interval long; shown, read at once.
        session.SetVisible(false);
        Assert(session.Tap.MinInterval == TimeSpan.FromSeconds(10), "hidden: the subscription's interval ten seconds");
        source.Arrive(1);
        await Task.Delay(30);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(30);
        Assert(source.Reads == 5, "hidden: a minute on, nothing read");
        session.SetVisible(true);
        await Until(() => source.Reads == 6, "shown: what changed read at once");
        Assert(session.Tap.MinInterval == TimeSpan.Zero, "shown: no interval");

        // A request is read at once, whatever the cadence.
        await Until(() => session.Answers == 6, "its answer");
        source.Arrive(1);
        session.Request(new GridRequest(Guid.Empty, 310, 10, 0, 0));
        await Until(() => source.Reads == 7, "a request: read at once");
    }

    /// <summary>A computed source whose reads wait at a gate (counting those cancelled while they wait).</summary>
    private sealed class GatedGrid(ComputedGrid inner) : IGridSource<long, GridOrderRow>
    {
        public readonly SemaphoreSlim Gate = new(0);
        public readonly System.Collections.Concurrent.ConcurrentQueue<GridRead<long, GridOrderRow>> Asked = new();
        public int Started, Cancelled;

        public long Version => inner.Version;

        public async ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<long, GridOrderRow> read,
            GridPage<long, GridOrderRow> page, CancellationToken cancellationToken)
        {
            Asked.Enqueue(read);
            Interlocked.Increment(ref Started);
            try
            {
                await Gate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }

            return await inner.ReadAsync(view, read, page, cancellationToken);
        }

        public IDisposable Watch(Action changed)
        {
            return inner.Watch(changed);
        }
    }

    public static async Task GridSessionCancel()
    {
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> count = (_, _) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += count;
        try
        {
            var outlet = NewOutlet(new OutletRegistry());
            var (client, owner) = Connected(outlet);
            var source = new GatedGrid(new ComputedGrid(2, 1_000));
            var session = new GridSession<long, GridOrderRow>(outlet, source);
            var subscription = client.Subscribe(session.Binding, new SubscriptionHandler());
            Pump(outlet, owner, client);

            // A jump (a window not touching the one being read) cancels the read in flight, its token reaching the
            // source; only the jump is answered.
            var a = new GridRequest(Guid.CreateVersion7(), 0, 10, 0, 0);
            var b = new GridRequest(Guid.CreateVersion7(), 50, 10, 0, 0);
            using var abandon = new CancellationTokenSource();
            var aAnswered = client.RendezvousAsync(CorrelationOf(a.Id), subscription.TapId, abandon.Token);
            var bAnswered = client.RendezvousAsync(CorrelationOf(b.Id), subscription.TapId);
            session.Request(a);
            await Until(() => source.Started == 1, "a's read begun");
            session.Request(b);
            await Until(() => source.Cancelled == 1, "a's read cancelled, in the source");
            await Until(() => source.Started == 2, "b's read begun");
            source.Gate.Release();
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return bAnswered.IsCompleted;
            }, "b answered");
            Assert(!aAnswered.IsCompleted, "a, superseded, never answered");
            abandon.Cancel();
            try
            {
                await aAnswered;
            }
            catch (OperationCanceledException)
            {
            }

            // A newer request touching the window being read: that read goes on and is answered; the newer, read next.
            var c = new GridRequest(Guid.CreateVersion7(), 100, 10, 0, 0);
            var d = new GridRequest(Guid.CreateVersion7(), 105, 10, 0, 0);
            var cAnswered = client.RendezvousAsync(CorrelationOf(c.Id), subscription.TapId);
            var dAnswered = client.RendezvousAsync(CorrelationOf(d.Id), subscription.TapId);
            session.Request(c);
            await Until(() => source.Started == 3, "c's read begun");
            session.Request(d);
            await Task.Delay(30);
            Assert(source.Cancelled == 1 && source.Started == 3, "c's read not cancelled by d, which touches it");
            source.Gate.Release();
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return cAnswered.IsCompleted;
            }, "c answered");
            await Until(() => source.Started == 4, "d's read begun, after c's");
            source.Gate.Release();
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return dAnswered.IsCompleted;
            }, "d answered");

            // Disposed mid-read: the read's token cancelled; the session over, its subscription closed.
            session.Request(new GridRequest(Guid.Empty, 300, 10, 0, 0));
            await Until(() => source.Started == 5, "a fifth read begun");
            await session.DisposeAsync();
            await Until(() => source.Cancelled == 2, "the read in flight cancelled by the dispose");
            Assert(session.Completion.IsCompletedSuccessfully && session.Fault is null && session.Tap.IsClosed,
                "ended cleanly: its loop done, no fault, its subscription closed");
            await session.DisposeAsync(); // (twice: nothing more)
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            TaskScheduler.UnobservedTaskException -= count;
        }

        Assert(unobserved == 0, $"no unobserved exception ({unobserved})");
    }

    public static async Task GridSessionJumps()
    {
        var outlet = NewOutlet(new OutletRegistry());
        var (client, owner) = Connected(outlet);
        var model = new ComputedGrid(6, 20_000);
        var source = new GatedGrid(model);
        await using var session = new GridSession<long, GridOrderRow>(outlet, source);
        GroupReplica? group = null;
        var subscription = client.Subscribe(session.Binding, new SubscriptionHandler { Group = (_, g) => group = g });
        Pump(outlet, owner, client);
        var order = GridOracle(model, GridView.Natural);

        // A jump: its view read and sent first, alone, with its rendezvous; then the rows round it, read from the view's.
        var j = new GridRequest(Guid.CreateVersion7(), 1_000, 22, 22, 22);
        var jAnswered = client.RendezvousAsync(CorrelationOf(j.Id), subscription.TapId);
        session.Request(j);
        await Until(() => source.Started == 1, "the view's read");
        Assert(source.Asked.TryDequeue(out var view) && view.Kind == GridReadKind.AtRank && view.Rank == 1_000 && view.Count == 22,
            "first, the view alone: 22 rows from rank 1,000");
        source.Gate.Release();
        await Until(() =>
        {
            Pump(outlet, owner, client);
            return jAnswered.IsCompleted;
        }, "its rendezvous, with the view");
        var replica = ReplicaOf(group)!.Value;
        Assert(replica.Header.First == 1_000 && replica.Keys.SequenceEqual(order[1_000..1_022]),
            "in front of the page with its rendezvous: the view's 22 rows, alone");
        await Until(() => source.Started == 2, "the rest's read");
        Assert(source.Asked.TryDequeue(out var rest) && rest.Kind == GridReadKind.AroundRow && rest.Key == order[1_000] &&
               rest.Before == 22 && rest.Count == 44, "then the rest: 22 above the view's first row and 44 from it");
        source.Gate.Release();
        await Until(() =>
        {
            Pump(outlet, owner, client);
            return session.Settled && ReplicaOf(group)!.Value.Keys.Length == 66;
        }, "the rest in front of the page");
        Assert(ReplicaOf(group)!.Value.Keys.SequenceEqual(order[978..1_044]), "the whole window: 978 to 1,044");

        // Scrolling, the view in the window published: read whole, in one.
        session.Request(new GridRequest(Guid.Empty, 1_010, 22, 22, 22, 1, 0));
        await Until(() => source.Started == 3, "a scroll's read");
        Assert(source.Asked.TryDequeue(out var scroll) && scroll.Count + scroll.Before == 66, "a scroll: the whole window at once");
        source.Gate.Release();
        await Until(() =>
        {
            Pump(outlet, owner, client);
            return session.Settled && ReplicaOf(group)!.Value.Header.First == 988;
        }, "the scroll answered: 988 to 1,054");

        // Scrolling past the window published (outrunning the pre-fetch): the view first; the rows round it left on the
        // page, not deleted ahead of it, and those the rest still holds not sent again.
        var o = new GridRequest(Guid.CreateVersion7(), 1_040, 22, 22, 22, 1, 0);
        var oAnswered = client.RendezvousAsync(CorrelationOf(o.Id), subscription.TapId);
        session.Request(o);
        await Until(() => source.Started == 4, "the outrun view's read");
        Assert(source.Asked.TryDequeue(out var oView) && oView.Kind == GridReadKind.AroundRow && oView.Key == order[1_040] &&
               oView.Before == 0 && oView.Count == 22, "first, the view alone: 22 rows from the window's row at 1,040");
        source.Gate.Release();
        await Until(() =>
        {
            Pump(outlet, owner, client);
            return oAnswered.IsCompleted;
        }, "its rendezvous, with the view");
        replica = ReplicaOf(group)!.Value;
        Assert(replica.Keys.SequenceEqual(order[1_040..1_062]) && replica.Rows.Count == 74 &&
               order[988..1_062].All(replica.Rows.ContainsKey),
            $"the view's 22 rows in front of the page, the window's 52 above it still there: {replica.Rows.Count} rows");
        await Until(() => source.Started == 5, "the outrun view's rest read");
        Assert(source.Asked.TryDequeue(out var oRest) && oRest.Kind == GridReadKind.AroundRow && oRest.Key == order[1_040] &&
               oRest.Before == 22 && oRest.Count == 44, "then the rest: 22 above the view's first row and 44 from it");
        long restBytes = 0;
        source.Gate.Release();
        await Until(() =>
        {
            Pump(outlet, owner, client, frame =>
            {
                restBytes += frame.Length - HeartbeatBytes(frame);
                return frame;
            });
            return session.Settled && ReplicaOf(group)!.Value.Header.First == 1_018;
        }, "the rest in front of the page");
        replica = ReplicaOf(group)!.Value;
        Assert(replica.Keys.SequenceEqual(order[1_018..1_084]) && replica.Rows.Count == 66 &&
               replica.Keys.All(replica.Rows.ContainsKey), $"the whole window, 1,018 to 1,084, and no other rows: {replica.Rows.Count}");
        var entry = 1 + sizeof(long) + Marshal.SizeOf<GridOrderRow>(); // a row in, or one leaving: its key and its row's width
        var rows = restBytes - GridOrder.SizeOf(66, sizeof(long));
        Console.WriteLine($"  outrun scroll's rest: {restBytes:N0} B, {rows:N0} B past its order — {(double)rows / entry:F1} rows' worth " +
                          $"({entry} B each): 30 gone above, 22 come below, none sent again");
        Assert(rows < (30 + 22 + 11) * entry,
            $"the rest: 30 rows gone above, 22 come below, the 22 above the view not sent again: {(double)rows / entry:F1} rows' worth");

        // A newer jump between a jump's stages: the rest of the first given up (its read cancelled), the newer's view read.
        var k = new GridRequest(Guid.CreateVersion7(), 5_000, 22, 22, 22, 1, 0);
        var kAnswered = client.RendezvousAsync(CorrelationOf(k.Id), subscription.TapId);
        session.Request(k);
        await Until(() => source.Started == 6, "k's view read");
        source.Gate.Release();
        await Until(() =>
        {
            Pump(outlet, owner, client);
            return kAnswered.IsCompleted;
        }, "k's view in front of the page");
        await Until(() => source.Started == 7, "k's rest read");
        session.Request(new GridRequest(Guid.Empty, 9_000, 22, 0, 0, 1, 0));
        await Until(() => source.Cancelled == 1 && source.Started == 8, "k's rest cancelled by the jump to 9,000; its view read");
        source.Gate.Release();
        await Until(() =>
        {
            Pump(outlet, owner, client);
            return session.Settled && ReplicaOf(group)!.Value.Header.First == 9_000;
        }, "the jump to 9,000 in front of the page");
        replica = ReplicaOf(group)!.Value;
        Assert(replica.Keys.SequenceEqual(order[9_000..9_022]) && source.Started == 8,
            "its view alone (no pads asked for), and k's rest never read again");
        Assert(replica.Rows.Count == 22, $"the rows left by k's view gone with the jump's: {replica.Rows.Count} rows");
    }

    public static async Task GridSessionViews()
    {
        var outlet = NewOutlet(new OutletRegistry());
        var (client, owner) = Connected(outlet);
        var model = new ComputedGrid(7, 5_000);
        var source = new GatedGrid(model);
        await using var session = new GridSession<long, GridOrderRow>(outlet, source);
        GroupReplica? group = null;
        var subscription = client.Subscribe(session.Binding, new SubscriptionHandler { Group = (_, g) => group = g });
        Pump(outlet, owner, client);

        // The page asks (view 0), and again while that's read (touching it: it waits); then the sort changes.
        var a = new GridRequest(Guid.CreateVersion7(), 0, 20, 0, 0);
        var aAnswered = client.RendezvousAsync(CorrelationOf(a.Id), subscription.TapId);
        session.Request(a);
        await Until(() => source.Started == 1, "a's read");
        var waiting = new GridRequest(Guid.CreateVersion7(), 10, 20, 0, 0);
        var waitingAnswered = client.RendezvousAsync(CorrelationOf(waiting.Id), subscription.TapId);
        session.Request(waiting);
        var view = new GridView([new SortKey("price", true)]);
        var number = session.SetView(view);
        Assert(number == 1, $"the view's number: {number}");
        await Until(() => source.Cancelled == 1 && session.Settled, "a's read cancelled by the new view, the request waiting dropped");

        // Asked in the view before (crossing the new one on the way): dropped, unread.
        var stale = new GridRequest(Guid.CreateVersion7(), 300, 20, 0, 0);
        var staleAnswered = client.RendezvousAsync(CorrelationOf(stale.Id), subscription.TapId);
        session.Request(stale);
        Assert(session.Settled, "the late request dropped: nothing asked of the loop");

        // The page, told of view 1, asks in it: read, answered, its order of view 1.
        var b = new GridRequest(Guid.CreateVersion7(), 0, 20, 0, 0, View: 1);
        var bAnswered = client.RendezvousAsync(CorrelationOf(b.Id), subscription.TapId);
        session.Request(b);
        await Until(() => source.Started == 2, "b's read");
        source.Gate.Release();
        await Until(() =>
        {
            Pump(outlet, owner, client);
            return bAnswered.IsCompleted && session.Settled;
        }, "b answered");
        var replica = ReplicaOf(group)!.Value;
        var order = GridOracle(model, view);
        Assert(replica.Header.View == 1 && replica.Keys.SequenceEqual(order[..20]), "the new view's top, its order of view 1");
        Assert(source.Started == 2 && !aAnswered.IsCompleted && !waitingAnswered.IsCompleted && !staleAnswered.IsCompleted,
            "the old view's requests: the one read cancelled, the one waiting and the late one dropped, none answered");
        Assert(session.SetView(GridView.Natural) == 2, "numbered on: 2");
    }

    private static TException Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException e)
        {
            return e;
        }

        throw new Exception($"{what}: not refused");
    }

    public static async Task GridSessionBounds()
    {
        var outlet = NewOutlet(new OutletRegistry());
        var source = new ComputedGrid(1, 100);

        // Options that can't work: refused when the session is made, saying why.
        GridOptions[] broken =
        [
            new() { MaxWindowRows = 0 }, new() { MaxSessionsPerClient = 0 }, new() { LiveEvery = TimeSpan.FromMilliseconds(-1) },
            new() { RevealAtOnce = -1 }
        ];
        string[] why = ["can show none", "none can open", "can't be negative", "negative number"];
        for (var i = 0; i < broken.Length; i++)
        {
            var refused = Throws<GridRefusedException>(() => _ = new GridSession<long, GridOrderRow>(outlet, source, options: broken[i]),
                why[i]);
            Assert(refused.Message.Contains(why[i]), $"refused, saying why: {refused.Message}");
        }

        // A client's grids, at most so many: one more refused, saying why; one closed, another opens. Another client's
        // are its own.
        var two = new GridOptions { MaxSessionsPerClient = 2 };
        var a = new GridSession<long, GridOrderRow>(outlet, source, options: two);
        var b = new GridSession<long, GridOrderRow>(outlet, source, options: two);
        var full = Throws<GridRefusedException>(() => _ = new GridSession<long, GridOrderRow>(outlet, source, options: two),
            "a third grid");
        Assert(full.Message.Contains("2 grids open, its most"), $"refused, saying why: {full.Message}");
        await using (new GridSession<long, GridOrderRow>(NewOutlet(new OutletRegistry()), source, options: two))
        {
        }

        await a.DisposeAsync();
        var c = new GridSession<long, GridOrderRow>(outlet, source, options: two);

        // A window wider than its most, or a count below none: refused, saying why.
        var wide = Throws<ArgumentOutOfRangeException>(() => b.Request(new GridRequest(Guid.Empty, 0, 900, 50, 51)), "1,001 rows");
        Assert(wide.Message.Contains("1001 rows: more than its most, 1000"), $"refused, saying why: {wide.Message}");
        Throws<ArgumentOutOfRangeException>(() => b.Request(new GridRequest(Guid.Empty, -1, 10, 0, 0)), "a position below none");
        Throws<ArgumentOutOfRangeException>(() => c.LocateAsync(5, 0, 0), "locating with no rows");

        // Its subscription over — the client let it go: the session ends.
        var (client, owner) = Connected(outlet);
        var subscription = client.Subscribe(c.Binding, new SubscriptionHandler());
        Pump(outlet, owner, client);
        subscription.Dispose();
        Pump(outlet, owner, client);
        await Until(() => c.Completion.IsCompleted, "the session ended with its subscription");
        Assert(c.Fault is null, "ended, not failed");
        await b.DisposeAsync();
        await c.DisposeAsync();
    }

    // ───────────── what it allocates, holds and costs on the wire ─────────────

    public static async Task GridAllocation()
    {
        // The way to the wire, on this thread: a held window sliding a row at a time through 100,000 rows — a row in, a
        // row out and the order republished — 10,000 answers: nothing allocated.
        var natural = new NaturalGrid<GridOrderRow>(100_000, id => ComputedGrid.Computed(id, 1));
        var rig = new GridRig(natural, 120);
        rig.Ask(1_000, 22, 22, 22);
        for (var i = 1; i < 2_000; i++) rig.Ask(1_000 + i, 22, 22, 22);
        var start = AllocationBaseline();
        for (var i = 2_000; i < 12_000; i++) rig.Ask(1_000 + i, 22, 22, 22, 1, 0);
        var spent = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert(rig.Published().Header.First == 12_000 - 22 + 1_000 - 1,
            $"the window where the last request put it ({rig.Published().Header.First})");
        Assert(spent < 8 * 1024, $"10,000 answers, a row in and out and the order each: {spent:N0} B allocated");

        // Its session's loop, from request to answer, 20,000 times (the process's: other threads may add a little, once).
        var outlet = NewOutlet(new OutletRegistry());
        var source = new NaturalGrid<GridOrderRow>(100_000, id => ComputedGrid.Computed(id, 1));
        var session = new GridSession<long, GridOrderRow>(outlet, source, options: new GridOptions { MaxWindowRows = 200 });
        outlet.Activate(session.Tap.Id);
        for (var i = 0; i < 4_000; i++) Answered(i % 400);
        await Task.Delay(500); // (tiered compilation settles: its rejits aren't the loop's)
        var before = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < 20_000; i++) Answered(i % 400);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Console.WriteLine($"  grid: 10,000 answers on the thread, {spent:N0} B; 20,000 through the session's loop, {allocated:N0} B (the process's)");
        Assert(allocated < 64 * 1024, $"20,000 requests answered by the session: {allocated:N0} B allocated (the process's)");
        await session.DisposeAsync();

        void Answered(int i)
        {
            var answers = session.Answers;
            session.Request(new GridRequest(Guid.Empty, 1_000 + i, 22, 22, 22, 1, 0));
            var spin = new SpinWait();
            while (Volatile.Read(ref session.Answers) == answers) spin.SpinOnce(-1);
        }
    }

    public static Task GridMemory()
    {
        // A window holds its rows, not its view's: its table, order and buffers allocate as much over 50,000,000 rows as
        // over 5,000 — the same windows read over each, on this thread (what's allocated here is kept, but for the buffers
        // each outgrew), its options the defaults (a window of 1,000 rows at most).
        Footprint(5_000);
        var small = Footprint(5_000);
        var large = Footprint(50_000_000);
        Console.WriteLine($"  grid window: {small:N0} B over 5,000 rows, {large:N0} B over 50,000,000 (windows of 88 rows: " +
                          "its table and change log, order, rows and page)");
        Assert(small == large, $"as much over 50,000,000 rows as over 5,000: {small:N0} B, {large:N0} B");
        return Task.CompletedTask;

        static long Footprint(long rows)
        {
            var source = new NaturalGrid<GridOrderRow>(rows, id => ComputedGrid.Computed(id, 2));
            var start = AllocationBaseline();
            var rig = new GridRig(source, GridOptions.Default.MaxWindowRows,
                log: GridSession<long, GridOrderRow>.LogCapacity(GridOptions.Default));
            rig.Ask(0, 22, 0, 66, 0, 0);
            for (var i = 0; i < 8; i++) rig.Ask(rows / 2 + 22 * i, 22, 22, 44, 1, 0);
            var held = GC.GetAllocatedBytesForCurrentThread() - start;
            GC.KeepAlive(rig);
            return held;
        }
    }

    /// <summary>The blotter's orders, newest first, keyed by UUID: rows of its 136 bytes, computed from their place.</summary>
    private sealed class BlotterGrid(long rows) : IGridSource<Guid, BlotterRow>
    {
        private long _next = rows;
        private readonly List<Action> _watchers = [];
        public int Reads;

        public long Version => Volatile.Read(ref _next);

        public ValueTask<GridAnswer> ReadAsync(GridView view, GridRead<Guid, BlotterRow> read, GridPage<Guid, BlotterRow> page,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            var next = Volatile.Read(ref _next);
            var at = read.Kind == GridReadKind.AtRank ? Math.Min(read.Rank, next) : next - 1 - IdOf(read.Key);
            var start = read.Kind == GridReadKind.AtRank ? at : Math.Max(0, at - read.Before);
            for (var rank = start; rank < Math.Min(next, at + read.Count); rank++) page.Add(KeyOf(next - 1 - rank), RowOf(next - 1 - rank));
            return new ValueTask<GridAnswer>(new GridAnswer
                { FirstRank = start, Anchor = (int)(at - start), Total = next, TotalKind = TotalKind.Exact, Version = next });
        }

        public IDisposable Watch(Action changed)
        {
            lock (_watchers) _watchers.Add(changed);
            return new Unwatching(() =>
            {
                lock (_watchers) _watchers.Remove(changed);
            });
        }

        public void Arrive(int n)
        {
            Interlocked.Add(ref _next, n);
            Action[] watchers;
            lock (_watchers) watchers = [.. _watchers];
            foreach (var w in watchers) w();
        }

        private static Guid KeyOf(long id)
        {
            return new Guid(0x0190_0000, 0, 0x7000, BitConverter.GetBytes(id));
        }

        private static long IdOf(Guid key)
        {
            Span<byte> bytes = stackalloc byte[16];
            key.TryWriteBytes(bytes);
            return BitConverter.ToInt64(bytes[8..]);
        }

        private static BlotterRow RowOf(long id)
        {
            var o = ComputedGrid.Computed(id, 3);
            var row = new BlotterRow
            {
                OrderTime = o.Placed, Version = 1, Region = o.Status, Channel = o.Side, Side = o.Side, Qty = o.Qty * 10_000L,
                FilledQty = o.Qty * 5_000L, AvgPrice = o.Price * 100, FilledAmt = o.Qty * o.Price, MarkupBps = 25,
                Status = o.Status, EventTime = o.Placed + 1_000, Reconciled = o.Placed + 2_000
            };
            Encoding.ASCII.GetBytes($"CL-{id:D12}", row.ClOrdId);
            Encoding.ASCII.GetBytes($"Client {id % 997:D4}", row.Client);
            Encoding.ASCII.GetBytes(id % 2 == 0 ? "BTC-PERP" : "ETH-PERP", row.Instrument);
            return row;
        }

        private sealed class Unwatching(Action dispose) : IDisposable
        {
            public void Dispose()
            {
                dispose();
            }
        }
    }

    public static async Task GridWireCost()
    {
        // On the wire, uncompressed, with the blotter's rows (136 B) and UUID keys: a row coming into the window is its key
        // and row; one leaving, its key and its row's width of zeros (decision (k)); and the order, a key each.
        var outlet = NewOutlet(new OutletRegistry());
        var (client, owner) = Connected(outlet);
        var source = new BlotterGrid(100_000);
        var clock = new ManualClock();
        await using var session = new GridSession<Guid, BlotterRow>(outlet, source, time: clock);
        var subscription = client.Subscribe(session.Binding, new SubscriptionHandler());
        Pump(outlet, owner, client);

        long Bytes()
        {
            long bytes = 0;
            for (var idle = 0; idle < 3;)
            {
                if (!outlet.TryBuildFrame(owner, false, out var frame, out var seq))
                {
                    idle++;
                    continue;
                }

                idle = 0;
                var copy = frame.ToArray();
                outlet.ReleaseFrame();
                bytes += copy.Length - HeartbeatBytes(copy);
                client.Receive(seq, copy);
            }

            return bytes;
        }

        async Task<long> Ask(long first, int count, int before, int after)
        {
            var request = new GridRequest(Guid.CreateVersion7(), first, count, before, after, session.Status.Holds, session.Status.LetIn);
            var answered = client.RendezvousAsync(CorrelationOf(request.Id), subscription.TapId);
            session.Request(request);
            long bytes = 0;
            await Until(() =>
            {
                bytes += Bytes();
                return answered.IsCompleted && session.Settled;
            }, "the answer, and the rest of its window"); // (counted with it, not the next)
            return bytes + Bytes();
        }

        // Scrolling a view at a time (22 rows), held: 22 rows in, 22 out, the order.
        await Ask(0, 22, 0, 44);
        await Ask(1_000, 22, 22, 22);
        long paged = 0;
        for (var i = 1; i <= 20; i++) paged += await Ask(1_000 + 22 * i, 22, 22, 22);
        var perRow = paged / (20.0 * 22);

        // The live top, a view and a view below it: each arrival a row in, a row out, the order.
        await Ask(0, 22, 0, 44);
        long arrivals = 0;
        for (var i = 0; i < 20; i++)
        {
            var answers = session.Answers;
            source.Arrive(1);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Until(() => session.Answers > answers || clock.NextDue() is not null, "the arrival read or armed");
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Until(() => session.Answers > answers, "the arrival read");
            arrivals += Bytes();
        }

        var perArrival = arrivals / 20.0;
        const int entry = 1 + 16 + 136; // a row in, or one leaving: its key and its row's width
        var order = GridOrder.SizeOf(66, 16);
        Console.WriteLine($"  grid on the wire: {perRow:F0} B a row entering, scrolling a view at a time (the row {entry} B, the one leaving it " +
                          $"{entry} B, a key-only delete would be {1 + 16} B); an arrival at the live top {perArrival:F0} B (its order {order} B)");
        Assert(perRow <= 400, $"S1: at most 400 B a row entering the window, uncompressed: {perRow:F0} B");
    }
}
