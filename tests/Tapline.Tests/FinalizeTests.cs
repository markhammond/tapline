using System.Runtime.InteropServices;
using Tapline;
using Tapline.Wasm;

/// <summary>
///     A fold that keeps a state of its own per key at the router, finalized into what subscribers see: a volume-weighted
///     average price from a sum of size and a sum of price × size, the sums never sent.
/// </summary>
internal static partial class T
{
    /// <summary>The state: sizes and notionals (price × size) summed.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct Sums(long Size, long Notional);

    /// <summary>The row: the size, and the price it averages to, rounded to the nearest.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct VwapRow(long Size, long Vwap);

    private readonly struct AddSums : IRowFold<Sums, Sums>
    {
        public static bool Fold(ref Sums row, bool exists, in Sums delta)
        {
            row = new Sums(row.Size + delta.Size, row.Notional + delta.Notional);
            return row.Size != 0;
        }
    }

    private readonly struct ToVwap : IRowFinalizer<Sums, VwapRow>
    {
        public static VwapRow Finalize(in Sums state)
        {
            return new VwapRow(state.Size, (2 * state.Notional + state.Size) / (2 * state.Size));
        }
    }

    /// <summary>The same fold and finish in WebAssembly: one module exporting both.</summary>
    private const string VwapWat =
        """
        (module
          (memory (export "memory") 1)
          (func (export "buffer") (param $size i32) (result i32)
            (local $short i32)
            (local.set $short (i32.sub (i32.shr_u (i32.add (local.get $size) (i32.const 65535)) (i32.const 16)) (memory.size)))
            (if (i32.gt_s (local.get $short) (i32.const 0))
              (then (if (i32.eq (memory.grow (local.get $short)) (i32.const -1)) (then unreachable))))
            (i32.const 0))
          (func (export "fold") (param $at i32) (param $rowCount i32) (param $rowSize i32) (param $deltaCount i32) (param $deltaSize i32)
            (local $d i32) (local $end i32) (local $row i32) (local $size i64)
            (if (i32.or (i32.ne (local.get $rowSize) (i32.const 16)) (i32.ne (local.get $deltaSize) (i32.const 16))) (then unreachable))
            (local.set $d (i32.add (local.get $at) (i32.mul (local.get $rowCount) (i32.const 20))))
            (local.set $end (i32.add (local.get $d) (i32.mul (local.get $deltaCount) (i32.const 20))))
            (block $done
              (loop $next
                (br_if $done (i32.ge_u (local.get $d) (local.get $end)))
                (local.set $row (i32.add (local.get $at) (i32.mul (i32.load align=1 (local.get $d)) (i32.const 20))))
                (local.set $size (i64.add (i64.load offset=4 align=1 (local.get $row)) (i64.load offset=4 align=1 (local.get $d))))
                (i64.store offset=4 align=1 (local.get $row) (local.get $size))
                (i64.store offset=12 align=1 (local.get $row)
                  (i64.add (i64.load offset=12 align=1 (local.get $row)) (i64.load offset=12 align=1 (local.get $d))))
                (i32.store align=1 (local.get $row) (i64.ne (local.get $size) (i64.const 0)))
                (local.set $d (i32.add (local.get $d) (i32.const 20)))
                (br $next))))
          (func (export "finalize") (param $at i32) (param $count i32) (param $stateSize i32) (param $rowSize i32)
            (local $e i32) (local $end i32) (local $size i64)
            (if (i32.or (i32.ne (local.get $stateSize) (i32.const 16)) (i32.ne (local.get $rowSize) (i32.const 16))) (then unreachable))
            (local.set $e (local.get $at))
            (local.set $end (i32.add (local.get $at) (i32.mul (local.get $count) (i32.const 36))))
            (block $done
              (loop $next
                (br_if $done (i32.ge_u (local.get $e) (local.get $end)))
                (if (i32.and (i32.load align=1 (local.get $e)) (i32.const 1))
                  (then
                    (local.set $size (i64.load offset=4 align=1 (local.get $e)))
                    (i64.store offset=20 align=1 (local.get $e) (local.get $size))
                    (i64.store offset=28 align=1 (local.get $e)
                      (i64.div_s (i64.add (i64.shl (i64.load offset=12 align=1 (local.get $e)) (i64.const 1)) (local.get $size))
                                 (i64.shl (local.get $size) (i64.const 1))))))
                (local.set $e (i32.add (local.get $e) (i32.const 36)))
                (br $next))))
        )
        """;

    /// <summary>A finalizer that fails when told to, else finalizes as <see cref="ToVwap" />.</summary>
    private sealed class FailingFinalizer : IBatchFinalizer
    {
        public bool Fail;

        public void Finalize(Span<byte> entries, int count, int stateSize, int rowSize)
        {
            if (Fail) throw new InvalidOperationException("told to fail");
            BatchFinalizer.Of<Sums, VwapRow, ToVwap>().Finalize(entries, count, stateSize, rowSize);
        }
    }

    public static Task FinalizedIngest()
    {
        var table = new Table<int, VwapRow>("VWAP");
        var ingest = table.Ingest<Sums, Sums>(Tapline.BatchFold.Of<Sums, Sums, AddSums>(),
            BatchFinalizer.Of<Sums, VwapRow, ToVwap>());
        ingest.QueueToPool = false;
        using var wasmFold = WasmBatchFold.FromText(VwapWat);
        using var wasmFinish = WasmBatchFinalizer.FromText(VwapWat);
        var wasm = new Table<int, VwapRow>("VWAP-WASM");
        var wasmIngest = wasm.Ingest<Sums, Sums>(wasmFold, wasmFinish);
        wasmIngest.QueueToPool = false;

        void Post(int[] keys, Sums[] deltas)
        {
            ingest.Post(keys, deltas);
            wasmIngest.Post(keys, deltas);
            ingest.DrainPending();
            wasmIngest.DrainPending();
        }

        VwapRow Row(int key)
        {
            return table.TryGet(key, out var row) ? row : default;
        }

        // Sizes at prices, summed: the rows are what they average to — the sums kept at the router, never in the table.
        Post([0, 0, 1], [new Sums(10, 10 * 100), new Sums(30, 30 * 104), new Sums(5, 5 * 200)]);
        Assert(Row(0) == new VwapRow(40, 103) && Row(1) == new VwapRow(5, 200) && table.Count == 2,
            $"rows finalized: 10 at 100 and 30 at 104 average 103 ({Row(0)}), 5 at 200 ({Row(1)})");
        Assert(wasm.Checksum == table.Checksum, "the WebAssembly module's the same, byte for byte");

        // A state that moves while its row doesn't: nothing published.
        var version = table.Version;
        Post([0], [new Sums(0, 1)]); // 4121 / 40: still 103
        Assert(table.Version == version && Row(0) == new VwapRow(40, 103), "the sums moved, the row didn't: nothing sent");
        Post([0], [new Sums(40, 40 * 110)]); // (4121 + 4400) / 80 = 106.5…: 107
        Assert(Row(0) == new VwapRow(80, 107) && table.Version == version + 1,
            $"the state kept the notional moved before: {Row(0)}");

        // Its state gone, its row is.
        Post([1], [new Sums(-5, -5 * 200)]);
        Assert(table.Count == 1 && !table.TryGet(1, out _) && wasm.Checksum == table.Checksum, "a state at zero: its row deleted");

        // A snapshot of states: the table replaced by their rows; deltas after it fold onto its states.
        ingest.PostSnapshot([2], [new Sums(7, 700)]);
        wasmIngest.PostSnapshot([2], [new Sums(7, 700)]);
        ingest.DrainPending();
        wasmIngest.DrainPending();
        Assert(table.Count == 1 && Row(2) == new VwapRow(7, 100), $"a snapshot's states, finalized: {Row(2)}");
        Post([2], [new Sums(7, 7 * 120)]);
        Assert(Row(2) == new VwapRow(14, 110) && wasm.Checksum == table.Checksum,
            $"a delta folded onto the snapshot's state: {Row(2)}");

        // Made over a table that has rows: their states unknown, deltas refused till a snapshot of states — then folded
        // onto its states. (An empty table's are all absent, known: the ingests above took deltas at once.)
        var written = new Table<int, VwapRow>("VWAP-WRITTEN");
        using (var u = written.BeginUpdate())
        {
            u.Set(0, new VwapRow(10, 100));
            u.Complete();
        }

        var takeover = written.Ingest<Sums, Sums>(Tapline.BatchFold.Of<Sums, Sums, AddSums>(),
            BatchFinalizer.Of<Sums, VwapRow, ToVwap>());
        takeover.QueueToPool = false;
        var refused = false;
        try
        {
            takeover.Post([0], [new Sums(10, 10 * 110)]);
        }
        catch (InvalidOperationException e) when (e.Message.Contains("snapshot of states"))
        {
            refused = true;
        }

        Assert(refused && takeover.AwaitingStates && written.TryGet(0, out var untouched) && untouched == new VwapRow(10, 100),
            "over a table with rows: deltas refused, its states unknown — the row as it was");
        takeover.PostSnapshot([0], [new Sums(10, 1000)]);
        takeover.Post([0], [new Sums(10, 10 * 110)]);
        takeover.DrainPending();
        written.TryGet(0, out var taken);
        Assert(!takeover.AwaitingStates && taken == new VwapRow(20, 105),
            $"a snapshot of states, then deltas folded onto them: {taken}");
        Assert(!ingest.AwaitingStates, "an empty table's ingest awaits nothing");

        // A finalizer that fails faults the table, as a fold that fails does; a snapshot recovers it.
        var failing = new FailingFinalizer();
        var faulty = new Table<int, VwapRow>("VWAP-FAULTY");
        var faultyIngest = faulty.Ingest<Sums, Sums>(Tapline.BatchFold.Of<Sums, Sums, AddSums>(), failing);
        faultyIngest.QueueToPool = false;
        faultyIngest.Post([0], [new Sums(1, 100)]);
        faultyIngest.DrainPending();
        failing.Fail = true;
        faultyIngest.Post([0], [new Sums(1, 100)]);
        faultyIngest.DrainPending();
        Assert(faulty.Fault?.Code == FaultCode.FoldFailed && faulty.TryGet(0, out var kept) && kept == new VwapRow(1, 100),
            $"its finalizer failed: the table faulted (FoldFailed), as it was ({faulty.Fault?.Code})");
        failing.Fail = false;
        faultyIngest.PostSnapshot([0], [new Sums(3, 300)]);
        faultyIngest.DrainPending();
        Assert(faulty.Fault is null && faulty.TryGet(0, out var back) && back == new VwapRow(3, 100),
            "a snapshot of states recovers it");
        failing.Fail = true;
        faultyIngest.PostSnapshot([0], [new Sums(3, 300)]);
        faultyIngest.DrainPending();
        Assert(faulty.Fault?.Code == FaultCode.FoldFailed, "a snapshot it fails on: faulted, nothing applied");

        // Allocation-free once warm: posting, folding, finalizing, applying. Changes that leave every row: nothing. Changes
        // deleting a row and making it again: its table queues each delete's tombstone till the delete falls off the
        // change log (64Ki entries), the queue growing meanwhile — past that, nothing either, and the states' slots, freed
        // and taken again, nothing.
        int[] key = [3];
        long Spent(Sums[] there, Sums[] back, int warm)
        {
            for (var i = 0; i < warm; i++)
            {
                ingest.Post(key, (i & 1) == 0 ? there : back);
                ingest.DrainPending();
            }

            var start = AllocationBaseline();
            for (var i = 0; i < 2_000; i++)
            {
                ingest.Post(key, (i & 1) == 0 ? there : back);
                ingest.DrainPending();
            }

            return GC.GetAllocatedBytesForCurrentThread() - start;
        }

        ingest.Post(key, [new Sums(5, 500)]);
        ingest.DrainPending();
        var steady = Spent([new Sums(1, 101)], [new Sums(-1, -101)], 200);
        Assert(steady < 8 * 1024, $"2,000 posts, the row kept: {steady:N0} B allocated");
        var remade = Spent([new Sums(-5, -500)], [new Sums(5, 500)], 140_000);
        Assert(remade < 8 * 1024, $"2,000 posts deleting the row and making it again, past the log: {remade:N0} B allocated");
        return Task.CompletedTask;
    }
}
