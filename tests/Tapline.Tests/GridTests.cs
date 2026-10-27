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
        public readonly LastValueSource? Status; // (the status value, asked for: staged with the rows)
        public readonly GridWindow<long, GridOrderRow> Window;
        public GridView View = GridView.Natural;

        public GridRig(IGridSource<long, GridOrderRow> source, int maxRows, int revealNear = 200, int revealAtOnce = 30,
            int log = 1024, bool status = false)
        {
            Source = source;
            Rows = new Table<long, GridOrderRow>("grid rows", log);
            Status = status ? new LastValueSource() : null;
            Window = new GridWindow<long, GridOrderRow>(Rows, Order, maxRows, maxRows, revealNear, revealAtOnce, Status);
            Page = new GridPage<long, GridOrderRow>(Window.PageCapacity);
        }

        public ComputedGrid Model => (ComputedGrid)Source;

        /// <summary>Called between a jump's two stages: its view published, the rest not yet read.</summary>
        public Action? BetweenStages;

        /// <summary>
        ///     The page's request, in the positions of the order it last applied (by default, the last published): a jump
        ///     answered in its two stages, as the session does, its view first.
        /// </summary>
        public bool Ask(long first, int count, int before, int after, int? holds = null, long? letIn = null, bool racing = false,
            bool top = false)
        {
            if (holds is null || letIn is null)
            {
                var (h, _) = Published();
                (holds, letIn) = (holds ?? h.Holds, letIn ?? h.LetIn);
            }

            var published = Answer(Window.Plan(new GridRequest(Guid.Empty, first, count, before, after, holds.Value, letIn.Value,
                Racing: racing, Top: top)));
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

        /// <summary>The order as published, and its keys — sent without them (by key), placed as the page places them.</summary>
        public (GridOrderHeader Header, long[] Keys) Published()
        {
            if (!GridOrder.TryRead(Window.PublishedOrder, out var h, out var keys)) return (default, []);
            return (h, h.ByKey ? ByKey(TableRows().Keys) : MemoryMarshal.Cast<byte, long>(keys).ToArray());
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
    ///     the live top, arrivals, a hold, arrivals above (the status alone sent), a row changed, rows let in, back to the
    ///     top — each step's frames, and the order, status and changed keys the page should then have (none: nothing
    ///     sent). Its own order is by key: at the live top the order goes without keys, the page placing the rows by theirs.
    /// </summary>
    private static object GridFixture(OutletRegistry registry, byte[] scratch)
    {
        var outlet = NewOutlet(registry);
        var source = new ComputedGrid(8, 500) { ByKey = true };
        var rig = new GridRig(source, 60, status: true);
        rig.Window.ByKey = true;
        var tap = outlet.Watch("grid", WatchMember.Of(rig.Rows), WatchMember.Of(rig.Order), WatchMember.Of(rig.Status!));
        outlet.Activate(tap.Id);
        var steps = new List<object>();
        var shown = rig.TableRows();

        void Step(string what, Action act)
        {
            var (version, order, status) = (rig.Rows.Version, rig.Window.PublishedOrder.ToArray(), rig.Status!.Version);
            act();
            var frames = new List<string>();
            for (int n; (n = outlet.Build(scratch)) > 0;) frames.Add(Convert.ToBase64String(scratch, 0, n));
            var (h, keys) = rig.Published();
            var now = rig.TableRows();
            var changed = now.Keys.Where(k => !shown.TryGetValue(k, out var was) || !SameRow(was, now[k]))
                .Concat(shown.Keys.Where(k => !now.ContainsKey(k))).OrderBy(k => k).Select(k => k.ToString()).ToArray();
            var statusOnly = rig.Rows.Version == version && rig.Window.PublishedOrder.SequenceEqual(order) &&
                             rig.Status.Version != status;
            var sent = statusOnly || rig.Rows.Version != version || !rig.Window.PublishedOrder.SequenceEqual(order);
            steps.Add(new
            {
                what, frames, sent, statusOnly,
                expect = new
                {
                    h.Live, h.ByKey, h.First, h.Total, h.LetIn, h.Holds, keys = keys.Select(k => k.ToString()).ToArray(), changed,
                    qty = keys.Select(k => now[k].Qty).ToArray(), rig.Window.Above
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
        Step("arrivals above, held: the status alone sent", () =>
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

        // By key: the header alone, its count the rows held; keys after it: not one.
        var keyless = new byte[GridOrder.SizeOf(3, 8)];
        var byKey = header with { Live = true, Stale = false, ByKey = true, KeySize = 8 };
        Assert(GridOrder.Write<long>(keyless, byKey, keys) == GridOrder.HeaderSize, "by key: 40 bytes, no keys");
        Assert(GridOrder.TryRead(keyless.AsSpan(0, GridOrder.HeaderSize), out var backByKey, out var none) &&
               backByKey == byKey with { Count = 3 } && none.IsEmpty, "by key: read back, three rows and no keys");
        Assert(!GridOrder.TryRead(keyless, out _, out _), "by key, with key bytes after it: not one");

        // The status value: rows arrived above, the overscan the link carries (none told: the page's own), and a refusal's
        // words (none: twelve bytes); another format, cut short: not one.
        var status = new byte[GridStatusValue.SizeOf("timed out: 5 s")];
        Assert(GridStatusValue.Write(status, 1_234, 64, null) == GridStatusValue.Size &&
               GridStatusValue.TryRead(status.AsSpan(0, GridStatusValue.Size), out var above, out var overscan, out var refusal) &&
               (above, overscan, refusal) == (1_234, 64, null), "no refusal: twelve bytes, read back as written");
        Assert(GridStatusValue.Write(status, 0, -1, "timed out: 5 s") == status.Length &&
               GridStatusValue.TryRead(status, out above, out overscan, out refusal) &&
               (above, overscan, refusal) == (0, -1, "timed out: 5 s"), "a refusal: its words after; no overscan told, read back");
        status[0] = 2;
        Assert(!GridStatusValue.TryRead(status, out _, out _, out _), "another format: not one");
        Assert(!GridStatusValue.TryRead(status.AsSpan(0, GridStatusValue.Size - 1), out _, out _, out _), "cut short: not one");

        // The overscan told: what the delivery rate carries in a second, both sides, at the blotter's 153 B a row, in powers
        // of two — up once the next fits; down once a quarter fewer than told fit, while the link is the limit (paced), not
        // while the application leaves it idle; no rate, as it was; at most 32,768.
        static int Overscan(long rate, int was, bool linkLimited = true) =>
            GridSession<long, GridOrderRow>.OverscanFor(rate, linkLimited, 1, 153, was);
        Assert(Overscan(0, 0) == 0 && Overscan(0, 64) == 64, "no rate yet: as it was");
        Assert(Overscan(2_000, 0) == 4, "16 Kbit/s (2,000 B/s): 6.5 rows fit, four told");
        Assert(Overscan(125_000, 4, false) == 256, "1 Mbit/s: 408 fit, 256 told, however it was measured");
        Assert(Overscan(64 * 306, 32) == 64 && Overscan(127 * 306, 64) == 64 && Overscan(128 * 306, 64) == 128,
            "up once the next power of two fits");
        Assert(Overscan(48 * 306, 64) == 64 && Overscan(47 * 306, 64) == 32, "down once a quarter fewer than told fit");
        Assert(Overscan(120, 256, false) == 256 && Overscan(24_000, 128, false) == 128,
            "the application the limit (a reader at rest, a quiet live top): not down, however little it delivers");
        Assert(Overscan(100, 8) == 0, "none fit, the link the limit: none told");
        Assert(Overscan(long.MaxValue / 2, 0) == 32_768, "at most 32,768");

        // A look's step, the reader at rest: up while it may climb — doubled (from one, if none), but by no more than four
        // fills (one row a fill before the link's measured) — to the page's most; halved once the link is the limit, never
        // below what the link carries (the fill); else as it was, the fill at least.
        static int Step(int told, int fill, int most, bool climb, bool limit) =>
            GridSession<long, GridOrderRow>.OverscanStep(told, fill, most, climb, limit);
        Assert(Step(4, 4, 150, true, false) == 8 && Step(16, 4, 150, true, false) == 32 && Step(0, 0, 150, true, false) == 1,
            "climbing: doubled, from one if none");
        Assert(Step(32, 4, 150, true, false) == 48 && Step(8, 0, 150, true, false) == 12 && Step(144, 4, 150, true, false) == 150,
            "by no more than four fills a step (four rows, none measured), to the page's most");
        Assert(Step(128, 64, 150, true, false) == 150 && Step(150, 4, 150, true, false) == 150 && Step(256, 256, 150, true, false) == 256,
            "a quick link: to the page's most at once; at it, or the fill above it: as it was");
        Assert(GridSession<long, GridOrderRow>.OverscanStep(32, 16, 150, true, false, 13) == 45 &&
               GridSession<long, GridOrderRow>.OverscanStep(0, 0, 150, true, false, 13) == 1 &&
               GridSession<long, GridOrderRow>.OverscanStep(32, 16, 150, true, false, 0) == 33,
            "by no more rows a step than go in band (one, at least)");
        Assert(Step(64, 4, 150, false, true) == 32 && Step(6, 4, 150, false, true) == 4 && Step(64, 4, 150, true, true) == 32,
            "the link the limit: halved, never below the fill, whatever else");
        Assert(Step(8, 16, 150, false, false) == 16 && Step(8, 4, 150, false, false) == 8, "neither: the fill at least");
        return Task.CompletedTask;
    }

    // ───────────── the window: live and held ─────────────

    /// <summary>
    ///     A hold from the live top on the page's row: on a slow link the page draws a live window the session has since
    ///     moved on from (rows arrived), so its positions are older than the session's; asked with the key of the row it
    ///     shows at its view's first position, the hold keeps that row there — not the row now at that rank. The row gone:
    ///     placed by rank, as an unanchored hold is.
    /// </summary>
    public static Task GridWindowAnchoredHold()
    {
        // Anchored: the page drew the live top; thirty arrived since, published, unseen; it scrolls ten rows down its own.
        var source = new ComputedGrid(3, 2_000);
        var rig = new GridRig(source, 120);
        rig.Ask(0, 22, 0, 22);
        var (_, drawn) = rig.Published();
        source.Arrive(30);
        rig.Refresh();
        var read = rig.Window.Plan(new GridRequest(Guid.Empty, 10, 22, 5, 22), drawn[10]);
        Assert(read.Kind == GridReadKind.AroundKey, "from the live top with the page's row: read about it, by key");
        rig.Answer(read);
        var (firstStage, firstKeys) = rig.Published();
        Assert(rig.Window.NeedsRest && rig.Window.PlannedFirst && firstStage.First == 10 && firstKeys.Length == 44 &&
               firstKeys[0] == drawn[10], "first the view and a view below, from the page's row, alone: the rest (the rows above it) next");
        rig.Answer(rig.Window.PlanRest());
        CheckWindow(rig, "held on the page's row");
        var (h, keys) = rig.Published();
        Assert(!h.Live && h.Holds == 1 && keys[(int)(10 - h.First)] == drawn[10] && rig.Window.Hidden == 30 && rig.Window.Above == 30,
            $"held: the page's row at its position, 10 (the window from {h.First}); the thirty arrived since above, not shown");

        // Unanchored, the same: the hold is at the rank, which the arrivals moved on — another row there.
        var plain = new GridRig(new ComputedGrid(3, 2_000), 120);
        plain.Ask(0, 22, 0, 22);
        var (_, plainDrawn) = plain.Published();
        plain.Model.Arrive(30);
        plain.Refresh();
        plain.Ask(10, 22, 5, 22, 0, 0);
        (h, keys) = plain.Published();
        var now = GridOracle(plain.Model, plain.View);
        Assert(keys[(int)(10 - h.First)] == now[10] && now[10] != plainDrawn[10],
            "unanchored: the row at 10 is the one at rank 10 now — not the one the page showed there (the shift)");

        // The page's row gone (deleted since): nothing about it — the hold placed by rank, read next, as unanchored.
        var gone = new GridRig(new ComputedGrid(3, 2_000), 120);
        gone.Ask(0, 22, 0, 22);
        var (_, goneDrawn) = gone.Published();
        gone.Model.Arrive(30);
        gone.Refresh();
        gone.Model.Delete(goneDrawn[10]);
        Assert(!gone.Answer(gone.Window.Plan(new GridRequest(Guid.Empty, 10, 22, 5, 22), goneDrawn[10])) && gone.Window.NeedsRest,
            "its row not found: nothing published, the window read next");
        gone.Answer(gone.Window.PlanRest());
        CheckWindow(gone, "held by rank, its row gone");
        (h, _) = gone.Published();
        Assert(!h.Live && h.Holds == 1 && gone.Window.Hidden == 0, "placed by rank: none hidden");
        return Task.CompletedTask;
    }

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

        // Where it now rests (its scroll moved down by the ten), asked again fetching more above: none — rows come in as
        // the reader scrolls up, not as the page's pre-fetch widens.
        rig.Ask(160, 22, 40, 22, letIn: 40);
        Assert(rig.Window.LetIn == 40 && rig.Window.Above == 10, "at rest, a wider pre-fetch: none let in");

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

        // At the first row shown with a view's worth or more above: not the live top — thirty let in, the window from the
        // top taking them, and the reader scrolls on into them; fewer than a view's worth there: the live top.
        rig.Ask(300, 22, 40, 22);
        source.Arrive(60);
        rig.Refresh();
        rig.Ask(0, 22, 0, 22);
        CheckWindow(rig, "the first row shown, sixty above");
        (h, _) = rig.Published();
        Assert(!h.Live && rig.Window.LetIn == 30 && rig.Window.Above == 30 && h.First == 0 && h.LetIn == 30,
            "the first row shown, sixty above: thirty let in, held");
        rig.Ask(30, 22, 0, 22, letIn: 30);
        Assert(rig.Window.LetIn == 30, "resting where its scroll moved to: none");
        rig.Ask(0, 22, 0, 22, letIn: 30);
        CheckWindow(rig, "the first row shown again, thirty above");
        Assert(!rig.Published().Header.Live && rig.Window.LetIn == 60 && rig.Window.Above == 0, "thirty above: let in, held");
        rig.Ask(0, 22, 0, 22, letIn: 60);
        (h, keys) = rig.Published();
        Assert(h.Live && keys.SequenceEqual(GridOracle(source, rig.View)[..44]), "none above: the live top");

        // Asked for (the pill, Home), the live top however many arrived above.
        rig.Ask(300, 22, 40, 22);
        source.Arrive(500);
        rig.Refresh();
        rig.Ask(0, 22, 0, 22, top: true);
        (h, keys) = rig.Published();
        Assert(h.Live && rig.Window.Above == 0 && keys.SequenceEqual(GridOracle(source, rig.View)[..44]),
            "the live top asked for, five hundred above: live");

        // Racing up near the top, more above than the view holds: as many as it fetches above (forty), the page's scroll
        // moved down by as many; then fewer above than the view holds: back to the live top at once, the window the top's.
        rig.Ask(300, 22, 40, 22);
        source.Arrive(52);
        rig.Refresh();
        rig.Ask(150, 22, 40, 22, racing: true);
        CheckWindow(rig, "racing up, fifty-two above");
        (h, _) = rig.Published();
        Assert(!h.Live && rig.Window.LetIn == 40 && rig.Window.Above == 12, "racing up, fifty-two above: forty let in");
        rig.Ask(190, 22, 40, 22, letIn: 40, racing: true);
        (h, keys) = rig.Published();
        Assert(h.Live && h.First == 0 && rig.Window.Above == 0 && keys.SequenceEqual(GridOracle(source, rig.View)[..44]),
            "twelve above: live");

        // Racing up at the first row shown, many above: three views at a time, the window from the top taking them.
        rig.Ask(300, 22, 40, 22);
        source.Arrive(100);
        rig.Refresh();
        rig.Ask(0, 22, 0, 22, racing: true);
        CheckWindow(rig, "racing up at the first row shown, a hundred above");
        (h, _) = rig.Published();
        Assert(!h.Live && rig.Window.LetIn == 66 && rig.Window.Above == 34 && h.First == 0 && rig.Window.Window.Count == 110,
            "racing up at the first row shown, a hundred above: sixty-six let in, the window from the top");
        rig.Ask(0, 22, 0, 22, top: true);

        // Anywhere: far from the top, with five above, racing up goes live too; with a view's worth above, it holds.
        rig.Ask(1_000, 22, 40, 22);
        source.Arrive(5);
        rig.Refresh();
        rig.Ask(950, 22, 40, 22, racing: true);
        (h, keys) = rig.Published();
        Assert(h.Live && h.First == 0 && keys.SequenceEqual(GridOracle(source, rig.View)[..44]), "racing up at 950, five above: live");
        rig.Ask(1_000, 22, 40, 22);
        source.Arrive(22);
        rig.Refresh();
        rig.Ask(950, 22, 40, 22, racing: true);
        Assert(!rig.Published().Header.Live && rig.Window.Above == 22 && rig.Window.LetIn == 0,
            "racing up at 950, twenty-two above a view of twenty-two: held, none let in");
        return Task.CompletedTask;
    }

    // ───────────── the live top by key: no keys, only the rows that changed ─────────────

    public static Task GridByKey()
    {
        var source = new ComputedGrid(6, 2_000) { ByKey = true };
        var rig = new GridRig(source, 200);
        rig.Window.ByKey = true;

        // The live top: its order the header alone; the rows held, greatest key first, the oracle's window.
        rig.Ask(0, 22, 0, 22);
        CheckWindow(rig, "the live top, by key");
        var (h, keys) = rig.Published();
        Assert(h is { ByKey: true, Live: true, Count: 44 } && rig.Window.PublishedOrder.Length == GridOrder.HeaderSize &&
               keys.SequenceEqual(GridOracle(source, rig.View)[..44]), "the live top: by key, 40 bytes, 44 rows held");

        // Arrivals: by key still, the newest on top, as many held.
        var before = rig.TableRows();
        source.Arrive(5);
        rig.Refresh();
        CheckWindow(rig, "five arrived, by key");
        (h, keys) = rig.Published();
        var now = rig.TableRows();
        Assert(h is { ByKey: true, Count: 44 } && keys.SequenceEqual(GridOracle(source, rig.View)[..44]) &&
               now.Keys.Except(before.Keys).Count() == 5 && before.Keys.Except(now.Keys).Count() == 5,
            "five arrived: by key, five rows in at the top and five out at the bottom");

        // Held: its keys (its positions are held, not the keys' order).
        rig.Ask(300, 22, 40, 22);
        CheckWindow(rig, "held");
        (h, _) = rig.Published();
        Assert(h is { ByKey: false, Live: false } && rig.Window.PublishedOrder.Length == GridOrder.SizeOf(h.Count, 8),
            "held: with its keys");

        // Back to the top from far away: its view read first, the rows before left in the table — with its keys — then
        // the rest, the rows before gone: by key.
        var between = default(GridOrderHeader);
        var lingering = 0;
        rig.BetweenStages = () => (between, lingering) = (rig.Published().Header, rig.Window.Lingering);
        rig.Ask(0, 22, 0, 22);
        rig.BetweenStages = null;
        Assert(lingering > 0 && between is { ByKey: false, Live: true }, "its view first: with its keys, rows left over");
        CheckWindow(rig, "back at the top, by key");
        (h, keys) = rig.Published();
        Assert(h.ByKey && keys.SequenceEqual(GridOracle(source, rig.View)[..44]), "its rest: by key again");

        // Sorted another way: its keys, at the top too.
        rig.View = new GridView([new SortKey("price")]);
        rig.Window.Reset(1);
        rig.Window.ByKey = source.NewestFirstByKey(rig.View);
        rig.Ask(0, 22, 0, 22);
        CheckWindow(rig, "sorted by price");
        (h, keys) = rig.Published();
        Assert(h is { ByKey: false, Live: true } && keys.SequenceEqual(GridOracle(source, rig.View)[..44]),
            "sorted by price: with its keys");
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
            var source = new ComputedGrid(seed, 2_000) { ByKey = true }; // (its own order by key: the live top without keys)
            var rig = new GridRig(source, 120);
            rig.Window.ByKey = true;
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
                    rig.Window.ByKey = source.NewestFirstByKey(rig.View); // (as the session sets it)
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

    /// <summary>
    ///     The page's replica of a grid's watch: its order, and its rows table, by key — an order sent without keys (by
    ///     key) placed as the page places it, its table's keys greatest first.
    /// </summary>
    private static (GridOrderHeader Header, long[] Keys, Dictionary<long, GridOrderRow> Rows)? ReplicaOf(GroupReplica? group)
    {
        if (group?.TableAt(0) is not { } table || !GridOrder.TryRead(group.ValueAt(1).Span, out var h, out var keys))
            return null;
        var rows = new Dictionary<long, GridOrderRow>();
        foreach (var row in table) rows[row.KeyAs<long>()] = row.RowAs<GridOrderRow>();
        return (h, h.ByKey ? ByKey(rows.Keys) : MemoryMarshal.Cast<byte, long>(keys).ToArray(), rows);
    }

    /// <summary>The page's status value, as the replica has it: rows arrived above, and a refusal.</summary>
    private static (long Above, string? Refusal)? StatusOf(GroupReplica? group)
    {
        return group is not null && GridStatusValue.TryRead(group.ValueAt(2).Span, out var above, out _, out var refusal)
            ? (above, refusal)
            : null;
    }

    /// <summary>The overscan the page's status value tells it (-1: none told), as the replica has it; null: no status.</summary>
    private static int? OverscanOf(GroupReplica? group)
    {
        return group is not null && GridStatusValue.TryRead(group.ValueAt(2).Span, out _, out var overscan, out _) ? overscan : null;
    }

    /// <summary>A window sent without its keys, placed: the keys held, greatest first.</summary>
    private static long[] ByKey(IEnumerable<long> held)
    {
        return [.. held.OrderDescending()];
    }

    /// <summary>
    ///     On a slow link a window's answers merge on their way, the first waiting for the rest: a window's first answer —
    ///     the live top's view; a hold's, the view and a view below — goes alone, the window not widened till the page has
    ///     it (its rest not read; a request whose view it holds answered as the window stands); then the rest, the last
    ///     request's window.
    /// </summary>
    public static async Task GridSessionFirstAlone()
    {
        var outlet = NewOutlet(new OutletRegistry());
        var (client, owner) = Connected(outlet);
        var source = new ComputedGrid(9, 3_000);
        var clock = new ManualClock();
        var reached = false; // (whether the page has the first answer: the test says, as a slow link's frames would)
        await using var session = new GridSession<long, GridOrderRow>(outlet, source, // (no climbing: the clock's timers the loop's looks)
            options: new GridOptions { OverscanClimb = TimeSpan.Zero }, time: clock) { Reached = _ => reached };
        GroupReplica? group = null;
        var subscription = client.Subscribe(session.Binding, new SubscriptionHandler { Group = (_, g) => group = g });
        Pump(outlet, owner, client);

        async Task Ask(long first, int count, int before, int after, long? anchor = null)
        {
            var replica = ReplicaOf(group);
            var request = new GridRequest(Guid.CreateVersion7(), first, count, before, after, replica?.Header.Holds ?? 0,
                replica?.Header.LetIn ?? 0);
            var answered = client.RendezvousAsync(CorrelationOf(request.Id), subscription.TapId);
            session.Request(request, anchor);
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return answered.IsCompleted;
            }, "the request's rendezvous");
            Pump(outlet, owner, client);
        }

        async Task Reach(Func<bool> there, string what) // (the first answer in front of the page: the loop looks again, reads the rest)
        {
            reached = true;
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return session.Settled && there();
            }, what);
            reached = false;
        }

        (long First, int Count, bool Live) Window()
        {
            var replica = ReplicaOf(group)!.Value;
            return (replica.Header.First, replica.Keys.Length, replica.Header.Live);
        }

        // The live top: its view alone; the rest once the page has it.
        await Ask(0, 22, 0, 22);
        Assert(Window() == (0, 22, true) && !session.Settled, "the live top's view first, alone: its rest not read yet");
        await Reach(() => Window() == (0, 44, true), "the page has it: the rest, a view below");

        // Held from it on the page's row, thirty arrived since: the view and a view below alone, thirty above in the status.
        source.Arrive(30);
        var drawn = ReplicaOf(group)!.Value.Keys;
        await Ask(10, 22, 5, 30, drawn[10]);
        var order = GridOracle(source, GridView.Natural);
        var held = ReplicaOf(group)!.Value;
        Assert(!held.Header.Live && held.Header.First == 10 && held.Keys.SequenceEqual(order[40..84]) && held.Keys[0] == drawn[10] &&
               StatusOf(group) == (30, null) && !session.Settled,
            "held on the page's row: the view and a view below alone (ranks 40 to 84), thirty above — the rest not read yet");

        // A request whose view the window holds, wider: answered as the window stands, not widened, till the page has it.
        await Ask(12, 22, 5, 40);
        Assert(Window() == (10, 44, false) && !session.Settled, "a view the window holds: answered as it stands, the rest waiting");
        await Reach(() => ReplicaOf(group)!.Value is var r && r.Header.First == 7 && r.Keys.SequenceEqual(order[37..104]),
            "the page has the first: the last request's window, 7 to 74");
    }

    public static async Task GridSessionWire()
    {
        var outlet = NewOutlet(new OutletRegistry());
        var (client, owner) = Connected(outlet);
        var source = new ComputedGrid(9, 3_000) { ByKey = true }; // (the live top by key: its order without keys)
        var clock = new ManualClock();
        await using var session = new GridSession<long, GridOrderRow>(outlet, source, // (no climbing: the clock's one timer is its cadence)
            options: new GridOptions { OverscanClimb = TimeSpan.Zero }, time: clock) { Reached = static _ => true }; // (each first answer in front of the page as read: pumped once settled)
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
            Assert(replica.Header.ByKey == live && (group.ValueAt(1).Length == GridOrder.HeaderSize) == live,
                $"{step}: {(live ? "live, by key: the order its header alone" : "held: the order with its keys")}");
            Assert(replica.Rows.Count == replica.Keys.Length && replica.Keys.All(k => SameRow(replica.Rows[k], source.RowOf(k))),
                $"{step}: the replica's table the window's rows alone, each as the source has it");
        }

        await Ask(0, 22, 0, 22);
        Expect(0, 44, true, "the live top");
        await Ask(400, 22, 10, 22);
        Expect(390, 444, false, "held at 400");

        // Held, rows arriving above: read at the held cadence, and one cut, the status alone (five above) — not the rows,
        // nor their order and its keys.
        var reads = source.Reads;
        var before = cuts;
        var (rows, order) = (group!.TableAt(0)!.Count, group.ValueAt(1).ToArray());
        Assert(StatusOf(group) == (0, null), "held, nothing arrived: none above");
        source.Arrive(5);
        await Until(() => clock.NextDue() is not null, "the held cadence armed");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Until(() =>
        {
            Pump(outlet, owner, client);
            return cuts == before + 1;
        }, "the status sent");
        Assert(source.Reads == reads + 1 && session.Status.Above == 5 && StatusOf(group) == (5, null),
            "read once at the held cadence: five above, in the status value");
        Assert(group.TableAt(0)!.Count == rows && group.ValueAt(1).Span.SequenceEqual(order), "the rows and their order as they were");

        // Held, a row in the window changing: one cut, that row.
        before = cuts;
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
        Assert(session.Status is { Live: true, Above: 0, Holds: 1 } && StatusOf(group) == (0, null),
            "the status: live, the one hold counted; none above in the status value");
    }

    public static async Task GridOverscan()
    {
        var rowBytes = 1 + sizeof(long) + System.Runtime.CompilerServices.Unsafe.SizeOf<GridOrderRow>();

        // A session's first window asked for (the live top), answered: its group as the page has it.
        static async Task<Func<GroupReplica?>> Open(Outlet outlet, GridSession<long, GridOrderRow> session)
        {
            var (client, owner) = Connected(outlet);
            GroupReplica? group = null;
            var subscription = client.Subscribe(session.Binding, new SubscriptionHandler { Group = (_, g) => group = g });
            var request = new GridRequest(Guid.CreateVersion7(), 0, 22, 0, 22);
            var answered = client.RendezvousAsync(CorrelationOf(request.Id), subscription.TapId);
            session.Request(request);
            await Until(() => session.Answers > 0 && session.Settled, "the first window read, both its stages (no frame acked yet)");
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return answered.IsCompleted;
            }, "its rendezvous");
            Pump(outlet, owner, client);
            return () =>
            {
                Pump(outlet, owner, client);
                return group;
            };
        }

        // Fixed flow control, or no time for the fill: none told (the page holds its own count), from the first answer.
        foreach (var (adaptive, fill, what) in new[]
                 {
                     (false, TimeSpan.FromSeconds(1), "fixed flow control"), (true, TimeSpan.Zero, "no time for the fill")
                 })
        {
            var outlet = NewOutlet(new OutletRegistry(), o => o.FlowControl = adaptive ? FlowControlMode.Adaptive : FlowControlMode.Fixed);
            await using var session = new GridSession<long, GridOrderRow>(outlet, new ComputedGrid(5, 1_000),
                options: new GridOptions { OverscanFill = fill }) { Reached = static _ => true }; // (both stages before a frame goes)
            var group = await Open(outlet, session);
            Assert(OverscanOf(group()) == -1, $"{what}: none told, the page's own count");
        }

        // Adaptive: none (0) before the link's measured — the first answer went before any frame was acked — then, answer
        // by answer at the live top, what its delivery rate carries in a second, both sides.
        {
            var outlet = NewOutlet(new OutletRegistry());
            var source = new ComputedGrid(6, 1_000);
            var clock = new ManualClock();
            await using var session = new GridSession<long, GridOrderRow>(outlet, source, // (the fill alone: no climbing)
                options: new GridOptions { OverscanClimb = TimeSpan.Zero }, time: clock) { Reached = static _ => true }; // (both stages before a frame goes)
            var group = await Open(outlet, session);
            Assert(OverscanOf(group()) == 0, "adaptive, before the link's measured: none told");

            async Task Arrival() // (an arrival at the live top read and answered; its frames left to pump)
            {
                var answers = session.Answers;
                source.Arrive(1);
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await Until(() => session.Answers > answers || clock.NextDue() is not null, "the arrival read or armed");
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await Until(() => session.Answers > answers, "the arrival read");
            }

            for (var until = Deadline(); outlet.DeliveryRate == 0;)
            {
                Assert(Stopwatch.GetTimestamp() < until, "a delivery rate measured, the frames acked");
                await Arrival();
                group(); // (pumped: acked)
            }

            var (rate, paced, was) = (outlet.DeliveryRate, outlet.PacingRate > 0, OverscanOf(group())!.Value); // (no ack till the next pump: as the session reads it)
            await Arrival();
            var told = OverscanOf(group());
            Assert(told == GridSession<long, GridOrderRow>.OverscanFor(rate, paced, 1, rowBytes, was) && told > 0,
                $"told what {rate:N0} B/s carries in a second at {rowBytes} B a row, both sides: {told}");
        }
    }

    /// <summary>
    ///     Pre-fetch while the reader rests (<see cref="GridOptions.OverscanClimb" />): held, the page having asked with the
    ///     overscan last told and rested a second since, its state sent, the overscan told climbs a step, in the status
    ///     alone — doubled, by no more than four fills (four rows, the link not measured) — to the page's most; not before
    ///     the reader has rested a second, nor again before the page has asked with it. Its state waiting longer than a
    ///     step takes (four fills: four seconds), it halves, with the rows. Back at the live top, it starts afresh, a step
    ///     each four seconds' rest.
    /// </summary>
    public static async Task GridOverscanClimb()
    {
        var outlet = NewOutlet(new OutletRegistry());
        var (client, owner) = Connected(outlet);
        var source = new ComputedGrid(8, 1_000);
        var clock = new ManualClock();
        await using var session = new GridSession<long, GridOrderRow>(outlet, source, time: clock)
            { MostOverscan = 16, Link = () => (0, false) }; // (no rate measured: the fill's none, the climb's alone)
        GroupReplica? group = null;
        var subscription = client.Subscribe(session.Binding, new SubscriptionHandler { Group = (_, g) => group = g });
        var second = TimeSpan.FromSeconds(1);

        int? Told()
        {
            Pump(outlet, owner, client);
            return OverscanOf(group);
        }

        async Task Ask(long first, string what)
        {
            var request = new GridRequest(Guid.CreateVersion7(), first, 10, 0, 0);
            var answered = client.RendezvousAsync(CorrelationOf(request.Id), subscription.TapId);
            session.Request(request);
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return answered.IsCompleted;
            }, what);
        }

        async Task Rest(TimeSpan by, int told, string what)
        {
            clock.Advance(by);
            await Until(() => Told() == told, $"{what} (told {Told()})");
        }

        async Task Stays(TimeSpan by, int told, string what)
        {
            clock.Advance(by);
            await Task.Delay(30);
            Assert(Told() == told, $"{what} (told {Told()})");
        }

        await Ask(300, "held: its window's answer");
        Assert(Told() == 0, "asked: none told");
        await Stays(second / 2, 0, "rested half a second: none");
        await Rest(second / 2, 1, "rested a second, its state sent: one");
        await Stays(second, 1, "the page not yet asking with it: as it was");
        foreach (var told in new[] { 2, 4, 8, 12, 16 })
        {
            await Ask(300, $"asked with {Told()}");
            await Stays(second / 2, Told()!.Value, "rested half a second since: as it was");
            await Rest(second / 2, told, $"a second: {told} (doubled, by no more than four rows)");
        }

        await Ask(300, "asked with sixteen");
        await Stays(second, 16, "the page's most: no more");

        // Changes the page isn't sent (not pumped), each read and looked at a second apart: sent at the first look, then
        // waiting at five — four seconds, as long as a step may take: as it was — and at the sixth, longer: halved.
        for (var look = 0; look < 7; look++)
        {
            if (look > 0) clock.Advance(second);
            var answers = session.Answers;
            source.Arrive(1); // (above the held window: its status moves)
            await Until(() => session.Answers > answers, "an arrival above read");
        }

        await Until(() => Told() == 8, $"waiting longer than a step takes: halved, with the rows (told {Told()})");

        clock.Advance(second / 10);
        await Ask(0, "the live top: its window's answer");
        Assert(Told() == 0, "back at the live top: what was climbed held gone, the fill alone (none)");
        await Stays(3 * second, 0, "rested three seconds: as it was");
        await Rest(second, 1, "four: a step");
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
                OverscanClimb = TimeSpan.Zero, // (no climbing: the clock's one timer is its cadence)
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

    /// <summary>
    ///     A page passive (hidden, or its grid out of view): the source's changes not read; its status says once that the
    ///     source moved past the rows the page holds (<see cref="GridStatusValue.Moved" />, alone: the rows and their order
    ///     as they were), and nothing more as it moves again. Back in sight it asks for its window again: read at once, its
    ///     answer the rows as they are now — the word the page waits for to show them as current — and the flag unsaid; the
    ///     request alone says it's shown, should the word of that be lost.
    /// </summary>
    public static async Task GridSessionPassive()
    {
        var outlet = NewOutlet(new OutletRegistry());
        var (client, owner) = Connected(outlet);
        var source = new ComputedGrid(4, 1_000);
        var clock = new ManualClock();
        await using var session = new GridSession<long, GridOrderRow>(outlet, source,
            options: new GridOptions { HiddenInterval = TimeSpan.FromMilliseconds(50) }, time: clock);
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
        bool Moved() => group is not null && GridStatusValue.MovedIn(group.ValueAt(2).Span);

        async Task Ask(string what)
        {
            var request = new GridRequest(Guid.CreateVersion7(), 0, 10, 0, 0);
            var answered = client.RendezvousAsync(CorrelationOf(request.Id), subscription.TapId);
            session.Request(request);
            await Until(() =>
            {
                Pump(outlet, owner, client);
                return answered.IsCompleted;
            }, what);
        }

        await Ask("the first answer");
        var (reads, total) = (source.Reads, ReplicaOf(group)!.Value.Header.Total);
        Assert(!Moved(), "in sight: nothing said moved");

        session.SetVisible(false);
        Assert(session.Tap.MinInterval == TimeSpan.FromMilliseconds(50), "passive: the subscription's interval");
        source.Arrive(3);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Until(() =>
        {
            Pump(outlet, owner, client);
            return Moved();
        }, "passive: the status says the source moved");
        Assert(source.Reads == reads && ReplicaOf(group)!.Value.Header.Total == total,
            "passive: nothing read, the rows and their order as they were");
        var said = cuts;
        source.Arrive(3);
        await Task.Delay(150);
        Pump(outlet, owner, client);
        Assert(cuts == said && source.Reads == reads, $"moved again: nothing more said, nothing read ({cuts - said} more)");

        // Back in sight, the word of it lost (a connection dropped as the page woke): its request says it's shown.
        await Ask("back in sight: its window's answer");
        Assert(source.Reads > reads && !Moved() && ReplicaOf(group)!.Value.Header.Total == total + 6,
            $"back in sight: read, the six arrivals in its answer, the flag unsaid ({ReplicaOf(group)!.Value.Header.Total - total})");
        Assert(session.Tap.MinInterval == TimeSpan.Zero, "shown by its request: no interval");
        reads = source.Reads;
        source.Arrive(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => source.Reads > reads, "shown by its request: the source's changes read again");
    }

    /// <summary>A computed source whose reads wait at a gate (counting those cancelled while they wait).</summary>
    private sealed class GatedGrid(ComputedGrid inner) : IGridSource<long, GridOrderRow>
    {
        public readonly SemaphoreSlim Gate = new(0);
        public readonly System.Collections.Concurrent.ConcurrentQueue<GridRead<long, GridOrderRow>> Asked = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<GridView> Left = new();
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

        public void Leave(GridView view)
        {
            Left.Enqueue(view);
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
        int entry = 1 + sizeof(long) + Marshal.SizeOf<GridOrderRow>(), leaving = 1 + sizeof(long); // a row in: op, key, row; one leaving: op, key
        var rows = restBytes - GridOrder.SizeOf(66, sizeof(long));
        Console.WriteLine($"  outrun scroll's rest: {restBytes:N0} B, {rows:N0} B past its order — 22 come below ({entry} B each), " +
                          $"30 gone above ({leaving} B each): {22 * entry + 30 * leaving:N0} B, none sent again");
        Assert(rows < 22 * entry + 30 * leaving + 11 * entry,
            $"the rest: 30 rows gone above, 22 come below, the 22 above the view not sent again: {rows:N0} B past its order");

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
        await Until(() => !source.Left.IsEmpty, "the view before left (just after the answer)");
        Assert(source.Left.SequenceEqual([GridView.Natural]), "the source told the grid's done with the view before, once");

        // Back to the natural order: the sorted view left once that's answered (not as it's read); and the grid closed,
        // the natural one too.
        Assert(session.SetView(GridView.Natural) == 2, "numbered on: 2");
        await Until(() => source.Started == 3, "the natural view's read");
        Assert(source.Left.SequenceEqual([GridView.Natural]), "the sorted view kept while the natural view's read");
        source.Gate.Release();
        await Until(() => source.Left.Count == 2, "the natural view answered");
        Assert(source.Left.SequenceEqual([GridView.Natural, view]), "the sorted view left");
        await session.DisposeAsync();
        Assert(source.Left.SequenceEqual([GridView.Natural, view, GridView.Natural]) && session.Fault is null,
            "closed: its last view left too");
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
            new() { RevealAtOnce = -1 }, new() { OverscanFill = TimeSpan.FromMilliseconds(-1) }
        ];
        string[] why = ["can show none", "none can open", "can't be negative", "negative number", "negative time"];
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

        /// <summary>Says its own order is by key, greatest first (as it is: the ids' bytes big-endian).</summary>
        public bool ByKey { get; set; }

        public long Version => Volatile.Read(ref _next);

        public bool NewestFirstByKey(GridView view)
        {
            return ByKey && view.Sort.Count == 0;
        }

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
            var low = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(low, id); // (big-endian: the keys compare as the ids do)
            return new Guid(0x0190_0000, 0, 0x7000, low);
        }

        private static long IdOf(Guid key)
        {
            Span<byte> bytes = stackalloc byte[16];
            key.TryWriteBytes(bytes);
            return BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
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
        // and row; one leaving, its key alone; and the order, a key each.
        var outlet = NewOutlet(new OutletRegistry());
        var (client, owner) = Connected(outlet);
        var source = new BlotterGrid(100_000);
        var clock = new ManualClock();
        await using var session = new GridSession<Guid, BlotterRow>(outlet, source, // (no climbing: the window as asked)
            options: new GridOptions { OverscanClimb = TimeSpan.Zero }, time: clock) { Reached = static _ => true }; // (its rest counted with its answer)
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

        var view = 0;

        async Task<long> Ask(long first, int count, int before, int after)
        {
            var request = new GridRequest(Guid.CreateVersion7(), first, count, before, after, session.Status.Holds,
                session.Status.LetIn, view);
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

        // The live top, a view and a view below it: each arrival a row in, a row out, the order — every key, or, its own
        // order by key, its header alone.
        async Task<double> Arrivals()
        {
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

            return arrivals / 20.0;
        }

        var perArrival = await Arrivals();
        source.ByKey = true;
        view = session.SetView(new GridView([]));
        var perArrivalByKey = await Arrivals();
        const int entry = 1 + 16 + 136, leaving = 1 + 16; // a row in: its op, key and row; one leaving: its op and key
        var order = GridOrder.SizeOf(66, 16);
        Console.WriteLine($"  grid on the wire: {perRow:F0} B a row entering, scrolling a view at a time (the row {entry} B, the one leaving it " +
                          $"{leaving} B); an arrival at the live top {perArrival:F0} B (its order {order} B), " +
                          $"by key {perArrivalByKey:F0} B (its order {GridOrder.HeaderSize} B)");
        Assert(perRow <= 400, $"S1: at most 400 B a row entering the window, uncompressed: {perRow:F0} B");
        Assert(perArrivalByKey <= entry + leaving + GridOrder.HeaderSize + 64,
            $"an arrival at the live top by key: a row in, a row out (its key) and the order's header, {perArrivalByKey:F0} B");
    }
}
