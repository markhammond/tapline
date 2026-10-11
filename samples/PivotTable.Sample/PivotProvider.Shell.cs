using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Chalk;
using Chalk.Client;
using Chalk.Incremental;
using PivotTable.Contracts;
using Tapline;

namespace PivotTable.Sample;

// A pivot whose cells carry extras — Show as: running totals in Date, percentages of the market or of what's shown — has a
// stage of its own between its view and the router (its shell): the view holds its statement's rows, each the cell's
// figures and the statement's own extras (the running totals, the market's totals, how many of its rows are kept); the
// shell composes each cell — its figures, then the extras it asked for — and posts those that changed, in a table whose
// cells are as wide as they need to be (2, 4, 8 or 16 extras: a pivot asking for none keeps the plain 72-byte cell, and
// the path it always had).
internal sealed partial class PivotProvider
{
    /// <summary>
    ///     The statement's extra columns an extended pivot's view row carries (<see cref="WideRow" />), by slot: its running
    ///     totals in Date, the market's totals — a column's, ignoring the row fields' filters; a row's, the column fields';
    ///     the grand total, both — of each figure a market's percentage can be of, and how many of the group's rows are kept.
    /// </summary>
    private static readonly string[] WideColumns =
    [
        "volume_running", "trades_running",
        .. new[] { "_all_rows", "_all_columns", "_all" }.SelectMany(set =>
            new[] { "notional", "realized", "unrealized", "balance", "volume", "trades" }.Select(f => f + set)),
        "kept"
    ];

    private const int KeptSlot = 20; // (WideColumns' last)

    /// <summary>A wide slot's market total: its set (the percentage's form), then its figure's place in it.</summary>
    private static int MarketSlot(ShowAs form, int figure)
    {
        return 2 + (form switch { ShowAs.PercentOfColumn => 0, ShowAs.PercentOfRow => 1, _ => 2 }) * 6 + figure;
    }

    /// <summary>A wide slot's scale: a volume's to the cent (its sums run past eight places'), the rest to eight places.</summary>
    private static decimal WideScale(int slot)
    {
        return WideColumns[slot].StartsWith("volume", StringComparison.Ordinal) ? VolumeScale : Scale;
    }

    /// <summary>A cell's extra's scale on the wire (<see cref="ShownValue.Places" />): a running volume to the cent, the rest to eight places.</summary>
    private static int ExtraPlaces(ShownValue extra)
    {
        return extra.Places;
    }

    /// <summary>A view's row into a wide one: its cell (<see cref="Map" />), and the statement's extras it answers (none: none).</summary>
    private static void MapWide(in ViewRow row, Columns at, out PivotKey key, out WideRow wide)
    {
        Map(row, at, out key, out var cell);
        wide = new WideRow { Cell = cell };
        for (var slot = 0; slot < WideRow.Slots; slot++)
            wide.Extra[slot] = at.Wide[slot] < 0 ? PivotCell.None : Figure(row, at.Wide[slot], at.Kinds, WideColumns[slot], WideScale(slot));
    }

    /// <summary>A batch of the answer into wide rows: its cells (<see cref="Read" />), then the statement's extras of each.</summary>
    private static string? ReadWide(RecordBatch batch, Columns at, List<PivotKey> keys, List<PivotCell> rows,
        HashSet<PivotKey> seen, List<WideRow> wides)
    {
        var start = rows.Count;
        if (Read(batch, at, keys, rows, seen) is { } problem) return problem;
        for (var i = 0; i < batch.Length; i++)
        {
            var wide = new WideRow { Cell = rows[start + i] };
            for (var slot = 0; slot < WideRow.Slots; slot++)
            {
                if (!Figure(at.Wide[slot] < 0 ? null : batch.Column(at.Wide[slot]), i, out var figure, WideScale(slot)))
                    return TooLarge(WideColumns[slot], WideScale(slot));
                wide.Extra[slot] = figure;
            }

            wides.Add(wide);
        }

        return null;
    }

    /// <summary>The shell for a pivot's extras: its cells as wide as they need to be.</summary>
    private static IShell NewShell(string name, ShownValue[] extras, bool[] answered, PivotShape shape)
    {
        var format = Pivot.FormatOf(shape);
        return extras.Length switch
        {
            <= 2 => new Shell<Extras2>(name, extras, answered, format),
            <= 4 => new Shell<Extras4>(name, extras, answered, format),
            <= 8 => new Shell<Extras8>(name, extras, answered, format),
            _ => new Shell<Extras16>(name, extras, answered, format)
        };
    }

    /// <summary>How many extras a pivot's cells hold for <paramref name="extras" />: 2, 4, 8 or 16.</summary>
    private static int Capacity(int extras)
    {
        return extras <= 2 ? 2 : extras <= 4 ? 4 : extras <= 8 ? 8 : 16;
    }

    /// <summary>
    ///     A pivot's run, its cells carrying extras: its view updated and drained as <see cref="RunViewAsync" /> does, then
    ///     its shell's — the cells composed, those changed posted. Validating: the statement run in full at this epoch, its
    ///     cells composed by the shell alike, its checksum the posted cells'.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))] // (a run each time round: its state machine pooled)
    private async ValueTask RunShellAsync(ChalkEngine engine, Topic topic, Work work, CancellationToken ct)
    {
        var shell = topic.Shell!;
        if (_validate) shell.CheckTable(topic.Check); // (the router's table, once it has the version posted before)
        var running = Stopwatch.GetTimestamp();
        string? problem = null;
        var drained = work.Wide;
        drained.Clear();
        try
        {
            var first = topic.WideView is null;
            if (first) (topic.Watermark, topic.FirstAnswer) = (null, true);
            var updating = Stopwatch.GetTimestamp();
            var allocated = GC.GetTotalAllocatedBytes();
            if (first)
                (topic.WideView, topic.Updates.Mode) = (await Materialize<WideRow>(engine, topic,
                    (in ViewRow row, out PivotKey key, out WideRow wide) => MapWide(row, topic.Columns, out key, out wide), ct), topic.Maintenance);
            await topic.WideView!.UpdateAsync(ct);
            if (topic.WideView.Statement is var now && now != topic.Query) Follow(topic, now);
            if (topic.Plans is { } plans) Track(topic, plans); // (tracked: a new plan's move logged, matched to its take-in)
            topic.Updates.Record(first, topic.Replaced, Stopwatch.GetElapsedTime(updating).TotalMilliseconds,
                GC.GetTotalAllocatedBytes() - allocated, topic.WideView.StateBytes);
            if (topic.Resync) (topic.Cursor, topic.Resync, drained.Whole) = (ViewCursor.Start, false, true);
            while (topic.WideView.Drain(ref topic.Cursor, drained, 1 << 20) == DrainOutcome.Partial)
            {
            }

            if (!first) topic.Updates.Changed(topic.Replaced, drained.Keys.Count, drained.WasReset);
            topic.Replaced = false;
        }
        catch (Exception e) when (e is ChalkException or StandingViewException or InvalidOperationException)
        {
            problem = $"ChalkQL: {e.Message}";
            if (topic.WideView is { } broken) _ = broken.DisposeAsync().AsTask();
            (topic.WideView, topic.Cursor) = (null, ViewCursor.Start);
        }

        topic.QueryMs = Stopwatch.GetElapsedTime(running).TotalMilliseconds;
        if (problem is not null)
        {
            Failed(topic, problem);
            return;
        }

        (topic.Generation, topic.Stamped) = (drained.Generation, drained.Version);
        var (clock, _) = Clock(topic);
        PublishResult? posted;
        try
        {
            posted = shell.Post(drained, clock);
        }
        catch (StreamFaultException e) // (the table faulted: the next drain the whole answer, recovering it)
        {
            _log.LogWarning("Pivot: {Name}'s table is faulted ({Fault}); its view's reader reset", topic.Name, e.Message);
            (topic.Cursor, topic.Urgent) = (ViewCursor.Start, true);
            return;
        }

        if (posted is { Status: PublishStatus.Full }) // refused (507): its next post the whole answer (the shell's), awaited
        {
            _log.LogWarning("Pivot: {Name}'s update was refused (507: its ingest's staging full); its next post whole",
                topic.Name);
            topic.Urgent = true;
            return;
        }

        (topic.Checksum, topic.CellCount) = (shell.Checksum, shell.Count);
        topic.Drains.Record(drained.Upserts, drained.Deletes, drained.WasReset, shell.Count);
        if (_validate)
        {
            var checking = Stopwatch.GetTimestamp();
            work.OtherWide.Clear();
            var other = await ComputeAsync(engine, topic, topic.Query, topic.Columns, work.OtherKeys, work.OtherRows,
                work.OtherSeen, ct, work.OtherWide);
            topic.Check.Record(topic.QueryMs, Stopwatch.GetElapsedTime(checking).TotalMilliseconds);
            if (other is not null) topic.Check.Fail($"the snapshot: {other}");
            else
            {
                var (expected, count) = shell.Expect(work.OtherKeys, work.OtherWide, posted is null ? null : drained.Version);
                topic.Check.View(expected, shell.Checksum, count, shell.Count);
            }
        }

        Answered(topic, posted, drained.Version);
    }

    /// <summary>A pivot's shell: its cells' table, and what it posts there.</summary>
    private interface IShell
    {
        Source Source { get; }

        /// <summary><paramref name="then" /> once everything it posted so far is applied by the router.</summary>
        void WhenApplied(Action<long> then);
        long Superseded { get; }

        /// <summary>The cells it posted (the clock cell aside), and their checksum.</summary>
        int Count { get; }

        uint Checksum { get; }

        /// <summary>A cell's bytes in the router's table — its key, its row — and what an ingest stages for a snapshot.</summary>
        int RowBytes { get; }

        int Staging { get; }

        bool TryRendezvous(Mailbox mailbox, ReadOnlySpan<byte> correlation);

        /// <summary>
        ///     A drained update applied — its rows kept, or let go — every cell composed, and those that changed posted with
        ///     the clock cell (the whole answer after a reset); throws as a faulted table's ingest does.
        /// </summary>
        PublishResult? Post(WideDrained drained, in PivotCell clock);

        /// <summary>
        ///     The cells a whole answer's rows make, as it would post them: their checksum, and how many — and, at a version
        ///     posted, what the router's table should hold once it has it (<see cref="CheckTable" />).
        /// </summary>
        (uint Checksum, int Count) Expect(List<PivotKey> keys, List<WideRow> rows, long? version);

        /// <summary>Validating: the router's table, once it has the version expected, cell by cell — figures and extras.</summary>
        void CheckTable(Checks checks);
    }

    /// <summary>
    ///     A shell, its cells carrying <typeparamref name="TExtras" />' extras (an inline array of i64s). It holds the view's
    ///     rows (those that keep any row: a client filtered out isn't a cell) and the cells it posted; each update it composes
    ///     every cell — its figures, then each extra: a running total as the statement answers it; a percentage, the cell's
    ///     figure over its total's (<see cref="Derived" />), of what's shown by the total cell's own figure, of the market by
    ///     the total cell's market total (where the statement made one — a filter moved — else its own: they're the same)
    ///     — posts the cells that changed and those gone, and keeps their checksum, a sum of each cell's.
    /// </summary>
    private sealed class Shell<TExtras> : IShell where TExtras : unmanaged
    {
        private static readonly int RowSize = Unsafe.SizeOf<PivotRow<TExtras>>();
        private readonly bool[] _answered; // (by wide slot: the statement answers it)
        private readonly List<RowChange<PivotRow<TExtras>>> _changes = [];
        private readonly ShownValue[] _extras;
        private readonly TableIngest<PivotKey, PivotRow<TExtras>, RowChange<PivotRow<TExtras>>> _ingest;

        public void WhenApplied(Action<long> then)
        {
            _ingest.WhenApplied(then);
        }
        private readonly List<PivotKey> _keys = [];
        private readonly Dictionary<PivotKey, PivotRow<TExtras>> _posted = [], _next = [];
        private readonly Dictionary<PivotKey, WideRow> _rows = [];
        private readonly List<PivotRow<TExtras>> _snapshot = [];
        private readonly Table<PivotKey, PivotRow<TExtras>> _table;
        private uint _checksum;
        private (long Version, Dictionary<PivotKey, PivotRow<TExtras>> Cells)? _pending; // (validating)
        private bool _started;

        public Shell(string name, ShownValue[] extras, bool[] answered, PayloadFormat format)
        {
            (_extras, _answered) = (extras, answered);
            var table = _table = new Table<PivotKey, PivotRow<TExtras>>(name) { Format = format };
            Source = table;
            Staging = MaxCells * (1 + Unsafe.SizeOf<PivotKey>() + RowSize) + 64 * 1024; // (a whole snapshot staged)
            _ingest = table.Ingest<RowChange<PivotRow<TExtras>>>(
                BatchFold.Of<PivotRow<TExtras>, RowChange<PivotRow<TExtras>>, ReplaceOrDeleteRow<PivotRow<TExtras>>>(), Staging);
        }

        public Source Source { get; }
        public long Superseded => _ingest.Superseded;
        public int Count => _posted.Count;
        public uint Checksum => _checksum;
        public int RowBytes => Unsafe.SizeOf<PivotKey>() + RowSize;
        public int Staging { get; }

        public bool TryRendezvous(Mailbox mailbox, ReadOnlySpan<byte> correlation)
        {
            return _ingest.TryRendezvous(mailbox, correlation);
        }

        public PublishResult? Post(WideDrained drained, in PivotCell clock)
        {
            if (drained.WasReset) _rows.Clear();
            for (var i = 0; i < drained.Keys.Count; i++)
                if (drained.Deleted[i] || !Kept(drained.Rows[i])) _rows.Remove(drained.Keys[i]);
                else _rows[drained.Keys[i]] = drained.Rows[i];
            Compose(_rows, _next);
            var clockRow = new PivotRow<TExtras> { Cell = clock };
            Extras(ref clockRow).Fill(PivotCell.None);
            var clockHash = Hash(ClockKey, clockRow);
            _keys.Clear();
            PublishResult posted;
            if (!_started || drained.WasReset || drained.Whole) // the whole answer
            {
                _snapshot.Clear();
                _checksum = 0;
                foreach (var (key, row) in _next)
                {
                    _keys.Add(key);
                    _snapshot.Add(row);
                    _checksum += Hash(key, row);
                }

                _keys.Add(ClockKey);
                _snapshot.Add(clockRow);
                posted = _ingest.PostSnapshot(CollectionsMarshal.AsSpan(_keys), CollectionsMarshal.AsSpan(_snapshot),
                    drained.Version, _checksum + clockHash);
                _posted.Clear();
                foreach (var (key, row) in _next) _posted[key] = row;
                _started = true;
                return posted;
            }

            _changes.Clear();
            foreach (var (key, row) in _next)
                if (!_posted.TryGetValue(key, out var was) || !Same(was, row))
                {
                    _keys.Add(key);
                    _changes.Add(new RowChange<PivotRow<TExtras>>(row, 0));
                    _checksum += Hash(key, row) - (_posted.ContainsKey(key) ? Hash(key, was) : 0);
                    _posted[key] = row;
                }

            foreach (var key in _posted.Keys)
                if (!_next.ContainsKey(key))
                {
                    _keys.Add(key);
                    _changes.Add(new RowChange<PivotRow<TExtras>>(default, 1));
                    _checksum -= Hash(key, _posted[key]);
                }

            foreach (var key in _keys)
                if (!_next.ContainsKey(key))
                    _posted.Remove(key);
            _keys.Add(ClockKey);
            _changes.Add(new RowChange<PivotRow<TExtras>>(clockRow, 0));
            posted = _ingest.Post(CollectionsMarshal.AsSpan(_keys), CollectionsMarshal.AsSpan(_changes),
                upstreamVersion: drained.Version, expectedChecksum: _checksum + clockHash);
            if (posted.Status == PublishStatus.Full) _started = false; // (refused: the table hasn't what this diff recorded as posted)
            return posted;
        }

        public (uint Checksum, int Count) Expect(List<PivotKey> keys, List<WideRow> rows, long? version)
        {
            var whole = new Dictionary<PivotKey, WideRow>();
            for (var i = 0; i < keys.Count; i++)
                if (Kept(rows[i]))
                    whole[keys[i]] = rows[i];
            var cells = new Dictionary<PivotKey, PivotRow<TExtras>>();
            Compose(whole, cells);
            uint sum = 0;
            foreach (var (key, row) in cells) sum += Hash(key, row);
            if (version is { } v) _pending = (v, cells);
            return (sum, cells.Count);
        }

        public void CheckTable(Checks checks)
        {
            if (_pending is not { } pending || _table.UpstreamVersion != pending.Version) return;
            var table = new List<KeyValuePair<PivotKey, PivotRow<TExtras>>>();
            _table.CopyTo(table);
            table.RemoveAll(cell => cell.Key.Equals(ClockKey)); // (its clock cell: no cell of the statement's)
            var mismatches = Math.Abs(table.Count - pending.Cells.Count);
            foreach (var (key, row) in table)
            {
                if (!pending.Cells.TryGetValue(key, out var want))
                {
                    mismatches++;
                    continue;
                }

                var close = checks.Close(key, row.Cell, want.Cell, pending.Version);
                var (a, b) = (row, want);
                var x = Extras(ref a);
                var y = Extras(ref b);
                for (var i = 0; i < _extras.Length; i++)
                    close &= checks.Near(key, _extras[i].ToString(), x[i], y[i], pending.Version);
                if (!close) mismatches++;
            }

            checks.Tabled(mismatches, table.Count, pending.Cells.Count, pending.Version);
            _pending = null;
        }

        /// <summary>Whether a wide row keeps any of the rows it groups (none moved: all).</summary>
        private bool Kept(in WideRow row)
        {
            return !_answered[KeptSlot] || (row.Extra[KeptSlot] is var kept && kept != PivotCell.None && kept != 0);
        }

        /// <summary>Every cell of <paramref name="rows" /> composed: its figures, then its extras.</summary>
        private void Compose(Dictionary<PivotKey, WideRow> rows, Dictionary<PivotKey, PivotRow<TExtras>> cells)
        {
            cells.Clear();
            foreach (var (key, wide) in rows)
            {
                var cell = new PivotRow<TExtras> { Cell = wide.Cell };
                var extras = Extras(ref cell);
                extras.Fill(PivotCell.None);
                for (var i = 0; i < _extras.Length; i++)
                    extras[i] = Extra(_extras[i], key, wide, rows);
                cells[key] = cell;
            }
        }

        /// <summary>
        ///     A cell's extra: a running total as its statement answers it; a percentage of a total — the cell's figure over
        ///     the total cell's (of what's shown), or over its market total (the statement's, where it made one) — as a
        ///     fraction to eight places (none where either is none, or the total is zero).
        /// </summary>
        private long Extra(ShownValue extra, PivotKey key, in WideRow wide, Dictionary<PivotKey, WideRow> rows)
        {
            if (extra.Form == ShowAs.RunningTotalInDate) return wide.Extra[extra.Value == "volume" ? 0 : 1];
            var (acrossRows, acrossColumns) = extra.Across;
            var at = key;
            if (acrossRows) at = at.AcrossRows();
            if (acrossColumns) at = at.AcrossColumns();
            if (!rows.TryGetValue(at, out var total)) return PivotCell.None; // (its total not made: that field's totals hidden)
            var whole = extra.OfMarket && Market(extra, total) is { } market ? market : Figures(total.Cell).Of(extra.Value);
            return Derived.Percent(Figures(wide.Cell).Of(extra.Value), whole) is { } fraction && fraction > long.MinValue && fraction <= long.MaxValue
                ? (long)fraction
                : PivotCell.None;
        }

        /// <summary>The market's total of a value in a total cell, where its statement made one (its figures' slots); else none.</summary>
        private Int128? Market(ShownValue extra, in WideRow total)
        {
            int[] figures = extra.Value switch
            {
                "notional" => [0], "pnl" => [1, 2], "balance" => [3], "volume" => [4], "trades" => [5], _ => []
            };
            if (figures.Length == 0 || !_answered[MarketSlot(extra.Form, figures[0])]) return null;
            Int128? sum = null;
            foreach (var f in figures)
            {
                var slot = MarketSlot(extra.Form, f);
                var figure = total.Extra[slot];
                if (figure == PivotCell.None)
                {
                    if (f == 2) continue; // (Pos PnL's unrealized none, across days: its realized alone, as a cell's)
                    return null;
                }

                sum = (sum ?? 0) + Derived.Scaled(figure, WideColumns[slot].StartsWith("volume", StringComparison.Ordinal) ? Pivot.VolumePlaces : Pivot.Places);
            }

            return sum;
        }

        /// <summary>A cell's figures as Show as reads them: each in hundred-millionths, its volume's cents scaled up; none, null.</summary>
        private static PivotFigures Figures(in PivotCell c)
        {
            static Int128? Of(long figure, int places = Pivot.Places)
            {
                return figure == PivotCell.None ? null : Derived.Scaled(figure, places);
            }

            return new PivotFigures(Of(c.Quantity), Of(c.Notional), Of(c.Realized), Of(c.Unrealized), Of(c.Balance),
                Of(c.Volume, Pivot.VolumePlaces), Of(c.Trades), Of(c.MaxGross), Of(c.MarkPrice));
        }

        private static Span<long> Extras(ref PivotRow<TExtras> row)
        {
            return MemoryMarshal.Cast<TExtras, long>(MemoryMarshal.CreateSpan(ref row.Extras, 1));
        }

        private static bool Same(in PivotRow<TExtras> a, in PivotRow<TExtras> b)
        {
            return MemoryMarshal.AsBytes(new ReadOnlySpan<PivotRow<TExtras>>(in a))
                .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<PivotRow<TExtras>>(in b)));
        }

        private static uint Hash(in PivotKey key, in PivotRow<TExtras> row)
        {
            return Table<PivotKey, PivotRow<TExtras>>.ChecksumOf([key], [row]);
        }
    }

    /// <summary>What an extended pivot's view drained: each key upserted (its wide row) or deleted, in order — the whole after a reset.</summary>
    private sealed class WideDrained : IViewSink<PivotKey, WideRow>
    {
        public readonly List<bool> Deleted = [];
        public readonly List<PivotKey> Keys = [];
        public readonly List<WideRow> Rows = [];
        public int Upserts, Deletes;
        public long Version;
        public bool WasReset, Whole; // (whole: a resync asked for the whole answer, though the view's reader goes on)

        public void Reset()
        {
            Keys.Clear();
            Rows.Clear();
            Deleted.Clear();
            (WasReset, Upserts, Deletes) = (true, 0, 0);
        }

        public void Upsert(in PivotKey key, in WideRow row)
        {
            Keys.Add(key);
            Rows.Add(row);
            Deleted.Add(false);
            Upserts++;
        }

        public void Delete(in PivotKey key)
        {
            Keys.Add(key);
            Rows.Add(default);
            Deleted.Add(true);
            Deletes++;
        }

        public long Generation;

        public void Commit(in ViewStamp stamp, uint checksum)
        {
            (Version, Generation) = (stamp.Version, stamp.Generation);
        }

        public void Commit(long version, uint checksum)
        {
            Version = version;
        }

        public void Clear()
        {
            Keys.Clear();
            Rows.Clear();
            Deleted.Clear();
            (Upserts, Deletes, Version, WasReset, Whole) = (0, 0, 0, false, false);
        }
    }
}

/// <summary>The row an extended pivot's view holds: its cell, then the statement's extras (<c>PivotProvider.WideColumns</c>).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WideRow
{
    public const int Slots = 21;
    public PivotCell Cell;
    public WideSlots Extra;
}

[InlineArray(WideRow.Slots)]
internal struct WideSlots
{
    private long _first;
}

/// <summary>A cell carrying extras on the wire: its figures (<see cref="PivotCell" />), then its extras, each an i64.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct PivotRow<TExtras> where TExtras : unmanaged
{
    public PivotCell Cell;
    public TExtras Extras;
}

[InlineArray(2)]
internal struct Extras2
{
    private long _first;
}

[InlineArray(4)]
internal struct Extras4
{
    private long _first;
}

[InlineArray(8)]
internal struct Extras8
{
    private long _first;
}

[InlineArray(16)]
internal struct Extras16
{
    private long _first;
}

/// <summary>A wide cell's change: replaced, or deleted.</summary>
internal readonly record struct RowChange<TRow>(TRow Row, byte Deleted) where TRow : unmanaged;

/// <summary>A wide cell's change folded in: replaced by its new figures, or deleted (false).</summary>
internal readonly struct ReplaceOrDeleteRow<TRow> : IRowFold<TRow, RowChange<TRow>> where TRow : unmanaged
{
    public static bool Fold(ref TRow row, bool exists, in RowChange<TRow> change)
    {
        if (change.Deleted != 0) return false;
        row = change.Row;
        return true;
    }
}
