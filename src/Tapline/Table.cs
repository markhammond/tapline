using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Tapline;

/// <summary>
///     Folds a delta into a row on the server. Implementations are value types with a static method, so the call is
///     bound at JIT time (inlined, zero allocation). <c>exists</c> is false when the row is absent (it is
///     then
///     <c>default</c>). Return false to delete the row.
/// </summary>
public interface IRowFold<TRow, TDelta> where TRow : unmanaged where TDelta : unmanaged
{
    static abstract bool Fold(ref TRow row, bool exists, in TDelta delta);
}

/// <summary>row = delta.</summary>
public readonly struct Replace<TRow> : IRowFold<TRow, TRow> where TRow : unmanaged
{
    public static bool Fold(ref TRow row, bool exists, in TRow delta)
    {
        row = delta;
        return true;
    }
}

/// <summary>row += delta; the row is deleted when it reaches zero (e.g. relative book-level quantity changes).</summary>
public readonly struct Add<T> : IRowFold<T, T> where T : unmanaged, INumber<T>
{
    public static bool Fold(ref T row, bool exists, in T delta)
    {
        row += delta;
        return !T.IsZero(row);
    }
}

/// <summary>
///     A fold supplied at runtime — typically a sandboxed one (Tapline.Wasm's <c>WasmBatchFold</c>: a WebAssembly module
///     with a fuel budget). Called once
///     per batch with two packed, little-endian regions:
///     <code>
/// rows    rowCount × ( u32 flags | row[rowSize] )        one entry per distinct key; flags bit0 = row present
/// deltas  deltaCount × ( u32 rowIndex | delta[deltaSize] )  in publish order; several may target the same row
/// </code>
///     Apply the deltas in order, updating rows and their present bit in place (clear it to delete the row). Repeated keys
///     therefore compose inside one call. If the fold throws (trap, fuel exhausted), the whole update rolls back and the
///     table faults (<see cref="FaultCode.FoldFailed" />).
/// </summary>
public interface IBatchFold
{
    void Fold(Span<byte> rows, int rowCount, int rowSize, ReadOnlySpan<byte> deltas, int deltaCount, int deltaSize);
}

/// <summary>
///     A batch fold that meters its work: a WebAssembly module's fuel. An ingest sizes its slices by what the calls use,
///     and
///     gives a slice that runs out fewer posts (<see cref="TableIngest{TKey,TRow,TDelta}" />).
/// </summary>
public interface IMeteredFold : IBatchFold
{
    /// <summary>The most one call may use: more, and it throws <see cref="FoldBudgetExceededException" />.</summary>
    long Budget { get; }

    /// <summary>Folds as <see cref="IBatchFold.Fold" /> does; returns what the call used.</summary>
    long FoldMetered(Span<byte> rows, int rowCount, int rowSize, ReadOnlySpan<byte> deltas, int deltaCount,
        int deltaSize);
}

/// <summary>A metered fold ran out of its budget (<see cref="IMeteredFold.Budget" />): fewer deltas may fit.</summary>
public sealed class FoldBudgetExceededException(string message, Exception? inner = null) : Exception(message, inner);

public static class FoldEntry
{
    public const uint Present = 1;

    public static int RowStride(int rowSize)
    {
        return 4 + rowSize;
    }

    public static int DeltaStride(int deltaSize)
    {
        return 4 + deltaSize;
    }

    /// <summary>A finalize entry's size (<see cref="IBatchFinalizer" />): flags, state, row.</summary>
    public static int FinalizeStride(int stateSize, int rowSize)
    {
        return 4 + stateSize + rowSize;
    }
}

public static class BatchFold
{
    /// <summary>
    ///     A compile-time fold over the batch regions (e.g. for an ingest):
    ///     <c>BatchFold.Of&lt;long, long, Add&lt;long&gt;&gt;()</c>.
    /// </summary>
    public static IBatchFold Of<TRow, TDelta, TFold>() where TRow : unmanaged
        where TDelta : unmanaged
        where TFold : IRowFold<TRow, TDelta>
    {
        return Rows<TRow, TDelta, TFold>.Instance;
    }

    private sealed class Rows<TRow, TDelta, TFold> : IBatchFold where TRow : unmanaged
        where TDelta : unmanaged
        where TFold : IRowFold<TRow, TDelta>
    {
        public static readonly Rows<TRow, TDelta, TFold> Instance = new();

        public void Fold(Span<byte> rows, int rowCount, int rowSize, ReadOnlySpan<byte> deltas, int deltaCount,
            int deltaSize)
        {
            if (rowSize != Unsafe.SizeOf<TRow>() || deltaSize != Unsafe.SizeOf<TDelta>())
                throw new ArgumentException("Rows or deltas of another size.");
            int rs = FoldEntry.RowStride(rowSize), ds = FoldEntry.DeltaStride(deltaSize);
            for (var i = 0; i < deltaCount; i++)
            {
                var d = deltas.Slice(i * ds, ds);
                var e = rows.Slice((int)BinaryPrimitives.ReadUInt32LittleEndian(d) * rs, rs);
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(e);
                var exists = (flags & FoldEntry.Present) != 0;
                var row = exists ? MemoryMarshal.Read<TRow>(e[4..]) : default;
                var keep = TFold.Fold(ref row, exists, MemoryMarshal.Read<TDelta>(d[4..]));
                BinaryPrimitives.WriteUInt32LittleEndian(e,
                    keep ? flags | FoldEntry.Present : flags & ~FoldEntry.Present);
                if (!keep) row = default;
                MemoryMarshal.Write(e[4..], in row);
            }
        }
    }
}

/// <summary>
///     A batch fold's finish (<see cref="Table{TKey,TRow}.Ingest{TState,TDelta}" />): the fold keeps a state per key at
///     the router — accumulators, say, a sum of price × size and a sum of size — and this makes of each state the row
///     subscribers see (their ratio: a volume-weighted average price); the state itself is never sent. Called once per
///     batch, after the fold, with one entry per key the batch touched (and, for a snapshot, one per key of it), packed
///     little-endian:
///     <code>
/// entries  count × ( u32 flags | state[stateSize] | row[rowSize] )   flags bit0 = state present; the row zeroed
/// </code>
///     Write each present state's row; an absent state's row is absent too. Pure: the same states in, the same rows out.
///     Throwing (a trap, its fuel out) fails the batch as a fold that throws does.
/// </summary>
public interface IBatchFinalizer
{
    void Finalize(Span<byte> entries, int count, int stateSize, int rowSize);
}

/// <summary>
///     A state's row, on the server (<see cref="BatchFinalizer.Of{TState,TRow,TFinalize}" />). Implementations are value
///     types with a static method, as <see cref="IRowFold{TRow,TDelta}" />'s are.
/// </summary>
public interface IRowFinalizer<TState, TRow> where TState : unmanaged where TRow : unmanaged
{
    static abstract TRow Finalize(in TState state);
}

public static class BatchFinalizer
{
    /// <summary>A compile-time finalizer over the batch's entries: <c>BatchFinalizer.Of&lt;Sums, Vwap, ToVwap&gt;()</c>.</summary>
    public static IBatchFinalizer Of<TState, TRow, TFinalize>() where TState : unmanaged
        where TRow : unmanaged
        where TFinalize : IRowFinalizer<TState, TRow>
    {
        return Entries<TState, TRow, TFinalize>.Instance;
    }

    private sealed class Entries<TState, TRow, TFinalize> : IBatchFinalizer where TState : unmanaged
        where TRow : unmanaged
        where TFinalize : IRowFinalizer<TState, TRow>
    {
        public static readonly Entries<TState, TRow, TFinalize> Instance = new();

        public void Finalize(Span<byte> entries, int count, int stateSize, int rowSize)
        {
            if (stateSize != Unsafe.SizeOf<TState>() || rowSize != Unsafe.SizeOf<TRow>())
                throw new ArgumentException("States or rows of another size.");
            var stride = FoldEntry.FinalizeStride(stateSize, rowSize);
            for (var i = 0; i < count; i++)
            {
                var e = entries.Slice(i * stride, stride);
                if ((BinaryPrimitives.ReadUInt32LittleEndian(e) & FoldEntry.Present) == 0) continue;
                var row = TFinalize.Finalize(MemoryMarshal.Read<TState>(e[4..]));
                MemoryMarshal.Write(e[(4 + stateSize)..], in row);
            }
        }
    }
}

/// <summary>
///     A replicated keyed table: rows of fixed-size unmanaged keys and values, streamed to taps as squashed deltas with
///     sequence numbers and a checksum, or as a snapshot. Domain-agnostic — an L2 order book is
///     <c>Table&lt;(side, price), qty&gt;</c>. Use <c>[StructLayout(LayoutKind.Sequential, Pack = 1)]</c> key and row
///     structs so their wire layout is compact and has no padding.
///     <para>
///         <b>Transactions.</b> Every update is all-or-nothing: <see cref="BeginUpdate" /> opens it (optionally only if
///         the
///         table is at an expected <see cref="UpstreamVersion" />), <see cref="Updater.Complete" /> publishes it
///         (optionally only
///         if the resulting checksum is as expected), and disposing without completing rolls it back. A failed
///         precondition,
///         a failed postcondition or a throwing fold rolls back and <em>faults</em> the table: it keeps its last good
///         state,
///         refuses further updates, and every active tap gets its <see cref="ErrorPolicy" /> and ends, until
///         <see cref="BeginRecovery" /> rebuilds it. So clients only ever see a version that was valid, or an explicit
///         error.
///     </para>
///     <para>
///         <b>Versions.</b> <see cref="Version" /> is the table's own wire sequence: it advances once per update that
///         changes
///         rows (net no-ops are not published) and never goes back. <see cref="UpstreamVersion" /> is whatever numbering
///         the
///         input uses (exchange sequence numbers, a provider's versions) — used for preconditions, and free to restart at
///         recovery without disturbing tap cursors.
///     </para>
///     <para>
///         <b>Change log.</b> Each published update appends one entry per changed row to a ring-buffer log. A tap's delta
///         is
///         "every row whose latest change is after my cursor", with its current contents, so any number of updates squash
///         into
///         at most one entry per row. A tap that is new, has fallen off the log, or is so far behind that walking the log
///         costs
///         more than the table gets a snapshot (= delta from the empty table).
///     </para>
///     Single writer at a time (the update holds the table's lock) — or, given an <see cref="Ingest{TDelta}">ingest</see>,
///     the
///     router alone, folding outside the lock.
/// </summary>
public sealed class Table<TKey, TRow> : Source, ITransactional
    where TKey : unmanaged, IEquatable<TKey>
    where TRow : unmanaged
{
    private static readonly int KeySize = Unsafe.SizeOf<TKey>();
    private static readonly int RowSize = Unsafe.SizeOf<TRow>();
    private static readonly int UpsertSize = 1 + KeySize + RowSize; // an entry: op, key, row
    private static readonly int DeleteSize = 1 + KeySize; // a delete's: op, key
    private readonly Stack<int> _free = new();
    private readonly Dictionary<TKey, int> _index = new();

    private readonly int[] _log;
    private readonly int _logMask;
    private readonly Queue<(int Slot, long Pos)> _tombstones = new();
    private byte[] _batchRows = new byte[1024], _batchDeltas = new byte[1024];
    private int[] _batchSlots = new int[64];
    private int[] _batchStamp = new int[64], _batchRow = new int[64];
    private uint _checksum, _txChecksum;
    private long _head, _seq, _upstream;
    private TKey[] _keys = new TKey[64];
    private bool[] _live = new bool[64];
    private long[] _modPos = new long[64];
    private long[] _bornPos = new long[64]; // where the slot's row last came into being (log position)
    private long[] _priorGonePos = new long[64]; // where the one before it in the slot went (long.MinValue: none)
    private TRow[] _rows = new TRow[64];
    private int _slots, _liveCount, _stamp, _undoCount, _txId, _txLiveCount;
    private uint? _txExpectedChecksum;
    private StreamFault? _txFault;
    private int[] _txMark = new int[64];
    private bool _txOpen, _txRecovery, _txShared;
    private Undo[] _undo = new Undo[64];

    public Table(string name, int logCapacity = 1 << 16)
    {
        if (KeySize > ushort.MaxValue || RowSize > ushort.MaxValue)
            throw new NotSupportedException("Key and row must be under 64 KB.");
        if (!BitOperations.IsPow2(logCapacity))
            throw new ArgumentException("Log capacity must be a power of two.", nameof(logCapacity));
        Name = name;
        Label = name;
        _log = new int[logCapacity];
        _logMask = logCapacity - 1;
    }

    public string Name { get; }

    public override (int Key, int Row) Layout => (KeySize, RowSize);

    /// <summary>
    ///     How records travel: Auto (default) moves a record larger than the outlet's threshold out of band — a snapshot
    ///     is then serialized once and shared by every tap that needs it. Inline never does (oversize records park their tap).
    /// </summary>
    public Delivery Delivery { get; set; } = Delivery.Auto;

    /// <summary>
    ///     False if rows are content a per-message compressor can't shrink (hashes, encrypted or random bytes): a transport
    ///     that compresses (Tapline.BlazorRealtime's circuit) then leaves frames made of such records alone. Default true.
    /// </summary>
    public bool Compressible { get; set; } = true;

    internal override Delivery DeliveryMode => Delivery;
    internal override bool IsCompressible => Compressible;
    internal override byte RecordType => Wire.Table;
    internal override Lock Gate { get; } = new();

    internal override bool Rewindable => true;
    public override long Version => Volatile.Read(ref _seq);

    /// <summary>The input's version as of the last completed update (see <see cref="Updater.Complete" />). Starts at 0.</summary>
    public long UpstreamVersion => Volatile.Read(ref _upstream);

    public uint Checksum
    {
        get
        {
            lock (Gate)
            {
                return _checksum;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (Gate)
            {
                return _liveCount;
            }
        }
    }

    // ───────────── transaction phases (one table: BeginUpdate/Complete; several: Transaction) ─────────────

    Source ITransactional.Source => this;

    StreamFault? ITransactional.EnterTx(long? expectedUpstream, bool recovery, bool shared)
    {
        Gate.Enter();
        if (_txOpen)
        {
            Gate.Exit();
            throw new InvalidOperationException("An update is already open on this thread.");
        }

        if (Volatile.Read(ref Writer) is not null)
        {
            Gate.Exit();
            throw new InvalidOperationException(
                "The table is written through an ingest: post to it (a snapshot to recover).");
        }

        if (!recovery)
        {
            if (Fault is { } fault)
            {
                Gate.Exit();
                return fault;
            }

            if (expectedUpstream is { } expected && expected != _upstream)
            {
                var mismatch = new StreamFault(FaultCode.VersionMismatch, _seq,
                    $"Update for upstream version {expected}; the table is at {_upstream}.");
                var set = TrySetFault(mismatch);
                Gate.Exit();
                if (set) Notify();
                return mismatch;
            }
        }

        OpenTx(recovery, shared);
        return null;
    }

    StreamFault? ITransactional.PrepareTx()
    {
        if (_txFault is { } fault) return fault;
        if (_txExpectedChecksum is { } expected && expected != _checksum)
            return new StreamFault(FaultCode.ChecksumMismatch, _seq,
                $"Checksum 0x{_checksum:X8} after the update; expected 0x{expected:X8}.");
        return null;
    }

    bool ITransactional.CommitTx(long? upstreamVersion)
    {
        var notify = false;
        if (AppendLog())
        {
            if (upstreamVersion is { } upstream) MarkUpstream(_seq + 1, upstream); // (before the version is published)
            Interlocked.Increment(ref _seq); // full fence before Notify
            notify = true;
        }

        if (upstreamVersion is { } up) Volatile.Write(ref _upstream, up);
        if (_txRecovery && TryClearFault()) notify = true;
        ReclaimTombstones();
        return notify;
    }

    void ITransactional.RollbackTx()
    {
        Rollback();
    }

    StreamFault? ITransactional.PendingTx => _txFault;

    bool ITransactional.FaultTx(StreamFault fault)
    {
        lock (Gate)
        {
            return TrySetFault(fault); // after any update in progress on another thread
        }
    }

    void ITransactional.ExitTx()
    {
        CloseStaged();
        Gate.Exit();
    }

    void ITransactional.NotifyTx()
    {
        Notify();
    }

    /// <summary>A shared out-of-band snapshot is reusable while a delta from it is still cheap.</summary>
    internal override bool CanResumeFrom(in Cursor cursor)
    {
        return Volatile.Read(ref _head) - cursor.Pos <= _log.Length / 2;
    }

    /// <summary>
    ///     The checksum a table holding exactly these rows has — the sum of each row's <see cref="RowHash" />, so the order
    ///     doesn't matter — for a provider to send with a snapshot (
    ///     <see cref="TableIngest{TKey, TRow, TDelta}.PostSnapshot" />'s
    ///     <c>expectedChecksum</c>): the router checks its table against it once the snapshot is applied, and every record it
    ///     sends carries the table's, which clients check against their replica. A provider in another process computes the
    ///     same over the rows' bytes (<see cref="RowHash.Of" />).
    /// </summary>
    public static uint ChecksumOf(ReadOnlySpan<TKey> keys, ReadOnlySpan<TRow> rows)
    {
        if (keys.Length != rows.Length) throw new ArgumentException("A row for every key.", nameof(rows));
        uint sum = 0;
        for (var i = 0; i < keys.Length; i++) sum += HashOf(keys[i], rows[i]);
        return sum;
    }

    public bool TryGet(in TKey key, out TRow row)
    {
        lock (Gate)
        {
            if (_index.TryGetValue(key, out var slot) && _live[slot])
            {
                row = _rows[slot];
                return true;
            }

            row = default;
            return false;
        }
    }

    /// <summary>Copies the live rows (for tests and server-side readers).</summary>
    public void CopyTo(List<KeyValuePair<TKey, TRow>> into)
    {
        lock (Gate)
        {
            for (var i = 0; i < _slots; i++)
                if (_live[i])
                    into.Add(new KeyValuePair<TKey, TRow>(_keys[i], _rows[i]));
        }
    }

    /// <summary>
    ///     Opens an atomic update. With <paramref name="expectedUpstream" />, the update applies only to that upstream
    ///     version: any other faults the table (<see cref="FaultCode.VersionMismatch" />) and throws. Throws
    ///     <see cref="StreamFaultException" /> while the table is faulted (use <see cref="BeginRecovery" />).
    /// </summary>
    public Updater BeginUpdate(long? expectedUpstream = null)
    {
        if (((ITransactional)this).EnterTx(expectedUpstream, false, false) is { } fault)
            throw new StreamFaultException(fault);
        return new Updater(this, _txId);
    }

    /// <summary>
    ///     Opens an update that may run while the table is faulted — typically <c>Clear()</c> and a full rebuild from an
    ///     upstream snapshot. Completing it clears the fault (taps bound afterwards stream normally); a failed recovery
    ///     leaves the table faulted.
    /// </summary>
    public Updater BeginRecovery()
    {
        ((ITransactional)this).EnterTx(null, true, false);
        return new Updater(this, _txId);
    }

    /// <summary>
    ///     From now on the table is written through staged posts (<see cref="TableIngest{TKey,TRow,TDelta}" />): a provider
    ///     posts deltas or a snapshot and goes on; the router folds them with <paramref name="fold" /> outside the table's
    ///     lock and applies the result as one update. The ingest is the table's only writer: direct updates
    ///     (<see cref="BeginUpdate" />, <see cref="BeginRecovery" />, a <see cref="Transaction" />) are refused, and a
    ///     snapshot
    ///     posted to it is the recovery. <paramref name="capacityBytes" /> bounds what may be staged at once;
    ///     <paramref name="maxDeltas" />, what one update folds (the router then gives its thread back before going on). For a
    ///     metered fold (<see cref="IMeteredFold" />: a WebAssembly one), <paramref name="sliceFuel" /> too: what one update's
    ///     fold
    ///     should use, as the router has measured it (default: a million, or half the fold's budget if less).
    /// </summary>
    public TableIngest<TKey, TRow, TDelta> Ingest<TDelta>(IBatchFold fold, int capacityBytes = 1 << 20,
        int maxDeltas = 4096, long? sliceFuel = null) where TDelta : unmanaged
    {
        ArgumentNullException.ThrowIfNull(fold);
        return new TableIngest<TKey, TRow, TDelta>(this, fold, capacityBytes, maxDeltas, sliceFuel);
    }

    /// <summary>
    ///     As <see cref="Ingest{TDelta}" />, the fold keeping a state of its own per key: <typeparamref name="TState" />,
    ///     held by the ingest at the router, folded from the posts' deltas — and <paramref name="finalizer" /> making of
    ///     each state the row the table holds, what subscribers see (<see cref="IBatchFinalizer" />). Only the rows are
    ///     ever sent: their changes and snapshots, checksummed, as any table's; a state whose row didn't move sends nothing.
    ///     The provider posts deltas, or a snapshot of states (<see cref="FinalizedIngest{TKey,TRow,TState,TDelta}" />),
    ///     which is the recovery. Costs a table without a finalizer nothing.
    /// </summary>
    public FinalizedIngest<TKey, TRow, TState, TDelta> Ingest<TState, TDelta>(IBatchFold fold,
        IBatchFinalizer finalizer, int capacityBytes = 1 << 20, int maxDeltas = 4096, long? sliceFuel = null)
        where TState : unmanaged where TDelta : unmanaged
    {
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(finalizer);
        return new FinalizedIngest<TKey, TRow, TState, TDelta>(this, fold, finalizer, capacityBytes, maxDeltas,
            sliceFuel);
    }

    /// <summary>
    ///     A row as it stands, read without the lock — for its group ingest alone: the only writer, between its own
    ///     updates.
    /// </summary>
    internal bool PeekRow(in TKey key, out TRow row)
    {
        if (_index.TryGetValue(key, out var slot) && _live[slot])
        {
            row = _rows[slot];
            return true;
        }

        row = default;
        return false;
    }

    // ───────────── its group ingest's update: the ingest holds the lock (Gates.TryEnter on Gate) throughout ─────────────

    internal void OpenStaged(bool recovery)
    {
        OpenTx(recovery, true);
    }

    /// <summary>
    ///     A snapshot's rows replacing the table's (<paramref name="replace" />), then the folded rows (
    ///     <paramref name="rows" />:
    ///     u32 flags | row, one per key of <paramref name="keys" />); <paramref name="expectedChecksum" />, if any, the
    ///     postcondition.
    /// </summary>
    internal void ApplyStaged(bool replace, ReadOnlySpan<TKey> snapshotKeys, ReadOnlySpan<TRow> snapshotRows,
        ReadOnlySpan<TKey> keys, ReadOnlySpan<byte> rows, uint? expectedChecksum)
    {
        if (replace)
        {
            ClearRows();
            for (var i = 0; i < snapshotKeys.Length; i++) Store(SlotFor(snapshotKeys[i]), true, snapshotRows[i]);
        }

        var stride = FoldEntry.RowStride(RowSize);
        for (var r = 0; r < keys.Length; r++)
        {
            var e = rows.Slice(r * stride, stride);
            var live = (BinaryPrimitives.ReadUInt32LittleEndian(e) & FoldEntry.Present) != 0;
            if (!live && !_index.ContainsKey(keys[r])) continue; // absent, and stays so
            Store(SlotFor(keys[r]), live, MemoryMarshal.Read<TRow>(e[4..]));
        }

        _txExpectedChecksum = expectedChecksum;
    }

    /// <summary>Ends the update (committed or rolled back); the ingest then lets go of the lock.</summary>
    internal void CloseStaged()
    {
        _txOpen = false;
        _txShared = false;
        _txFault = null;
        _txExpectedChecksum = null;
    }

    private void OpenTx(bool recovery, bool shared)
    {
        if (++_txId == int.MaxValue)
        {
            Array.Clear(_txMark);
            _txId = 1;
        }

        _txOpen = true;
        _txRecovery = recovery;
        _txShared = shared;
        _txFault = null;
        _txExpectedChecksum = null;
        _txChecksum = _checksum;
        _txLiveCount = _liveCount;
        _undoCount = 0;
    }

    /// <summary>The open update's updater, for a <see cref="Transaction" /> this table is part of.</summary>
    internal Updater SharedUpdater()
    {
        if (!_txOpen || !_txShared || !Gate.IsHeldByCurrentThread)
            throw new InvalidOperationException("The table is not part of the open transaction.");
        return new Updater(this, _txId);
    }

    /// <inheritdoc />
    /// <remarks>Called by the writer inside an open update, the update is rolled back when it ends.</remarks>
    public override void Fail(FaultCode code, string? message = null, Exception? exception = null)
    {
        if (code == FaultCode.None) throw new ArgumentOutOfRangeException(nameof(code));
        bool set;
        lock (Gate)
        {
            var fault = new StreamFault(code, _seq, message, exception);
            if (_txOpen)
            {
                _txFault ??= fault;
                return;
            } // only our own thread can hold the lock with an update open

            set = TrySetFault(fault);
        }

        if (set) Notify();
    }

    private void Check(int tx)
    {
        if (!_txOpen || _txId != tx || !Gate.IsHeldByCurrentThread)
            throw new InvalidOperationException("This update is no longer open.");
        if (_txFault is { } fault) throw new StreamFaultException(fault);
    }

    private long Finish(int tx, bool complete, long? upstream, uint? expectedChecksum)
    {
        if (!Gate.IsHeldByCurrentThread || !_txOpen || _txId != tx)
        {
            if (complete) throw new InvalidOperationException("This update is no longer open.");
            return Version;
        }

        if (_txShared)
        {
            if (complete)
                throw new InvalidOperationException("This table is part of a Transaction: complete the transaction.");
            return Version; // the transaction ends it
        }

        var self = (ITransactional)this;
        if (expectedChecksum is { } expected) _txExpectedChecksum = expected;
        var fault = complete ? self.PrepareTx() : _txFault;
        var notify = false;
        try
        {
            if (complete && fault is null)
            {
                notify = self.CommitTx(upstream);
            }
            else
            {
                Rollback();
                if (fault is not null && TrySetFault(fault)) notify = true;
            }
        }
        finally
        {
            self.ExitTx();
        }

        if (notify) Notify();
        if (complete && fault is not null) throw new StreamFaultException(fault);
        return Version;
    }

    /// <summary>Publishes the open update: one log entry per row whose final state differs from its original.</summary>
    private bool AppendLog()
    {
        var start = _head;
        for (var i = 0; i < _undoCount; i++)
        {
            ref readonly var u = ref _undo[i];
            var slot = u.Slot;
            var live = _live[slot];
            if (live == u.WasLive && (!live || Same(_rows[slot], u.Row)))
            {
                if (u.Created) Release(slot); // e.g. a fold into an absent key that ended at zero
                continue;
            }

            _log[(int)(_head & _logMask)] = slot;
            if (live && !u.WasLive) // (a row coming into being: in a slot of its own, or one whose row went before)
                (_bornPos[slot], _priorGonePos[slot]) = (_head, u.Created ? long.MinValue : _modPos[slot]);
            _modPos[slot] = _head++;
            if (!live) _tombstones.Enqueue((slot, _modPos[slot]));
        }

        _undoCount = 0;
        return _head != start;
    }

    private void Rollback()
    {
        for (var i = _undoCount - 1; i >= 0; i--)
        {
            ref readonly var u = ref _undo[i];
            _rows[u.Slot] = u.Row;
            _live[u.Slot] = u.WasLive;
            if (u.Created) Release(u.Slot);
        }

        _undoCount = 0;
        _checksum = _txChecksum;
        _liveCount = _txLiveCount;
    }

    /// <summary>
    ///     A deleted row's slot is recycled once its delete has fallen off the log: any cursor that has not seen the delete
    ///     is then forced to a snapshot, which never includes it.
    /// </summary>
    private void ReclaimTombstones()
    {
        var horizon = _head - _log.Length;
        while (_tombstones.TryPeek(out var t) && t.Pos < horizon)
        {
            _tombstones.Dequeue();
            if (_live[t.Slot] || _modPos[t.Slot] != t.Pos) continue;
            _index.Remove(_keys[t.Slot]);
            _modPos[t.Slot] = long.MinValue;
            _free.Push(t.Slot);
        }
    }

    private void Touch(int slot, bool created)
    {
        _txMark[slot] = _txId;
        if (_undoCount == _undo.Length) Array.Resize(ref _undo, _undo.Length * 2);
        _undo[_undoCount++] = new Undo { Slot = slot, WasLive = _live[slot], Created = created, Row = _rows[slot] };
    }

    private int SlotFor(in TKey key)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_index, key, out var exists);
        if (exists) return entry;
        if (!_free.TryPop(out var slot))
        {
            if (_slots == _keys.Length) Grow();
            slot = _slots++;
        }

        entry = slot;
        _keys[slot] = key;
        _rows[slot] = default;
        _live[slot] = false;
        _modPos[slot] = -1;
        Touch(slot, true);
        return slot;
    }

    /// <summary>Returns a slot that holds no row and is referenced by no log entry.</summary>
    private void Release(int slot)
    {
        _index.Remove(_keys[slot]);
        _modPos[slot] = long.MinValue;
        _free.Push(slot);
    }

    private void Grow()
    {
        var n = _keys.Length * 2;
        Array.Resize(ref _keys, n);
        Array.Resize(ref _rows, n);
        Array.Resize(ref _modPos, n);
        Array.Resize(ref _bornPos, n);
        Array.Resize(ref _priorGonePos, n);
        Array.Resize(ref _live, n);
        Array.Resize(ref _txMark, n);
        Array.Resize(ref _batchStamp, n);
        Array.Resize(ref _batchRow, n);
    }

    private void Store(int slot, bool live, in TRow row)
    {
        ref var current = ref _rows[slot];
        var wasLive = _live[slot];
        if (wasLive == live && (!live || Same(current, row))) return;
        if (_txMark[slot] != _txId) Touch(slot, false);
        if (wasLive)
        {
            _checksum -= HashOf(_keys[slot], current);
            _liveCount--;
        }

        if (live)
        {
            _checksum += HashOf(_keys[slot], row);
            _liveCount++;
        }

        current = live ? row : default;
        _live[slot] = live;
    }

    private StreamFaultException FoldFailed(Exception e)
    {
        _txFault ??= new StreamFault(FaultCode.FoldFailed, _seq, $"Fold failed: {e.Message}", e);
        return new StreamFaultException(_txFault);
    }

    private void FoldOne<TDelta, TFold>(in TKey key, in TDelta delta)
        where TDelta : unmanaged where TFold : IRowFold<TRow, TDelta>
    {
        var slot = SlotFor(key);
        var exists = _live[slot];
        var row = exists ? _rows[slot] : default;
        bool keep;
        try
        {
            keep = TFold.Fold(ref row, exists, delta);
        }
        catch (Exception e)
        {
            throw FoldFailed(e);
        }

        Store(slot, keep, row);
    }

    private void FoldBatch<TDelta>(ReadOnlySpan<TKey> keys, ReadOnlySpan<TDelta> deltas, IBatchFold fold)
        where TDelta : unmanaged
    {
        if (keys.Length != deltas.Length) throw new ArgumentException("Keys and deltas differ in length.");
        var n = keys.Length;
        if (n == 0) return;
        int deltaSize = Unsafe.SizeOf<TDelta>(),
            rowStride = FoldEntry.RowStride(RowSize),
            deltaStride = FoldEntry.DeltaStride(deltaSize);
        EnsureScratch(n * rowStride, n);
        if (_batchDeltas.Length < n * deltaStride)
            _batchDeltas = new byte[(int)BitOperations.RoundUpToPowerOf2((uint)(n * deltaStride))];
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_batchStamp);
            _stamp = 1;
        }

        var rows = _batchRows.AsSpan();
        var ds = _batchDeltas.AsSpan(0, n * deltaStride);
        var rowCount = 0;
        for (var i = 0; i < n; i++)
        {
            var slot = SlotFor(keys[i]);
            if (_batchStamp[slot] != _stamp) // first time this batch sees the key: one row entry
            {
                _batchStamp[slot] = _stamp;
                _batchRow[slot] = rowCount;
                _batchSlots[rowCount] = slot;
                var e = rows.Slice(rowCount * rowStride, rowStride);
                var exists = _live[slot];
                BinaryPrimitives.WriteUInt32LittleEndian(e, exists ? FoldEntry.Present : 0u);
                var row = exists ? _rows[slot] : default;
                MemoryMarshal.Write(e[4..], in row);
                rowCount++;
            }

            var d = ds.Slice(i * deltaStride, deltaStride);
            BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)_batchRow[slot]);
            MemoryMarshal.Write(d[4..], in deltas[i]);
        }

        rows = rows[..(rowCount * rowStride)];

        try
        {
            fold.Fold(rows, rowCount, RowSize, ds, n, deltaSize);
        }
        catch (Exception e)
        {
            throw FoldFailed(e); // nothing stored yet; the update rolls back new slots
        }

        for (var r = 0; r < rowCount; r++)
        {
            var e = rows.Slice(r * rowStride, rowStride);
            var keep = (BinaryPrimitives.ReadUInt32LittleEndian(e) & FoldEntry.Present) != 0;
            Store(_batchSlots[r], keep, MemoryMarshal.Read<TRow>(e[4..]));
        }
    }

    private void EnsureScratch(int bytes, int slots)
    {
        if (_batchRows.Length < bytes) _batchRows = new byte[(int)BitOperations.RoundUpToPowerOf2((uint)bytes)];
        if (_batchSlots.Length < slots) _batchSlots = new int[(int)BitOperations.RoundUpToPowerOf2((uint)slots)];
    }

    private void ClearRows()
    {
        for (var i = 0; i < _slots; i++)
            if (_live[i])
                Store(i, false, default);
    }

    private bool NeedsReset(in Cursor cursor)
    {
        return BeyondHorizon(cursor) || ScanCheaper(cursor);
    }

    /// <summary>A snapshot is the only way: past the log's horizon, deleted rows' slots may have been recycled.</summary>
    private bool BeyondHorizon(in Cursor cursor)
    {
        return cursor.Seq < 0 || _head - cursor.Pos > _log.Length;
    }

    /// <summary>One pass over the slots costs less than walking the log from the cursor.</summary>
    private bool ScanCheaper(in Cursor cursor)
    {
        return _head - cursor.Pos > 4L * _slots + 64;
    }

    /// <summary>
    ///     A deleted row a cursor at log position <paramref name="since" /> never held — it came into being after the
    ///     cursor, and no row before it in the slot outlived the cursor — so its delete is nothing to that client: a slow
    ///     one isn't sent the deletes of every row that came and went while it was behind. (Exact for keys used once; a
    ///     key gone and back again since the cursor keeps its delete.)
    /// </summary>
    private bool Unheld(int slot, long since)
    {
        return !_live[slot] && _bornPos[slot] >= since && _priorGonePos[slot] < since;
    }

    /// <summary>
    ///     The entries' bytes a pass over the slots writes for a delta from log position <paramref name="since" />, as
    ///     <see cref="Write" /> does: the rows changed since (or deleted, still tombstoned), less the deletes of rows it never
    ///     had — or a snapshot's, once those are no fewer.
    /// </summary>
    private long ScannedBytes(long since)
    {
        long bytes = 0, snapshot = (long)_liveCount * UpsertSize;
        for (var i = 0; i < _slots; i++)
        {
            if (_modPos[i] < since || Unheld(i, since)) continue;
            var size = _live[i] ? UpsertSize : DeleteSize;
            if (bytes + size >= snapshot) return snapshot;
            bytes += size;
        }

        return bytes;
    }

    /// <summary>The entries' bytes a delta walking the log from <paramref name="since" /> writes, as <see cref="Write" /> does.</summary>
    private long LoggedBytes(long since)
    {
        long bytes = 0;
        for (var p = since; p < _head; p++)
        {
            var slot = _log[(int)(p & _logMask)];
            if (_modPos[slot] != p || Unheld(slot, since)) continue;
            bytes += _live[slot] ? UpsertSize : DeleteSize;
        }

        return bytes;
    }

    /// <summary>
    ///     The entries' bytes <see cref="WriteRange" /> writes: a snapshot's of the range, or the rows changed in it since the
    ///     cursor, less the deletes of rows never sent this subscriber (<paramref name="state" />).
    /// </summary>
    private long RangeBytes(in Cursor cursor, bool reset, ReadOnlySpan<byte> prefix, RangeState state)
    {
        if (reset) return (long)CountLive(prefix) * UpsertSize;
        long bytes = 0;
        for (var p = cursor.Pos; p < _head; p++)
        {
            var slot = _log[(int)(p & _logMask)];
            if (_modPos[slot] != p || !Matches(slot, prefix)) continue;
            if (_live[slot]) bytes += UpsertSize;
            else if (state.Sent.ContainsKey(slot)) bytes += DeleteSize;
        }

        return bytes;
    }

    private bool Matches(int slot, ReadOnlySpan<byte> prefix)
    {
        return prefix.IsEmpty || MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in _keys[slot])).StartsWith(prefix);
    }

    /// <summary>The rows a snapshot carries: those live, optionally within a key range.</summary>
    private int CountLive(ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty) return _liveCount;
        var count = 0;
        for (var i = 0; i < _slots; i++)
            if (_live[i] && Matches(i, prefix))
                count++;
        return count;
    }

    /// <summary>
    ///     Its keys' and rows' layouts: as its provider declared them (<see cref="Source.Format" />) if they fit, else derived
    ///     from <typeparamref name="TKey" /> and <typeparamref name="TRow" /> where code can be generated (not under Native
    ///     AOT:
    ///     then none, shown as hex); how each was chosen in <paramref name="notes" />.
    /// </summary>
    public override PayloadFormat? EffectiveFormat(ICollection<string>? notes = null)
    {
        var format = Format;
        var key = Choose(format?.KeyLayout, KeySize, "key", typeof(TKey), notes);
        var row = Choose(format?.Layout, RowSize, "row", typeof(TRow), notes);
        return key is null && row is null && format?.ContentType is null && format?.ViewUrl is null &&
               format?.Retention is null
            ? null
            : new PayloadFormat
            {
                ContentType = format?.ContentType, Layout = row, KeyLayout = key, ViewUrl = format?.ViewUrl,
                Retention = format?.Retention
            };

        static PayloadLayout? Choose(PayloadLayout? declared, int size, string what, Type type,
            ICollection<string>? notes)
        {
            if (declared is not null)
            {
                if (declared.Size == size)
                {
                    notes?.Add($"{what}s: as declared");
                    return declared;
                }

                notes?.Add($"{what}s: the declared layout is {declared.Size} bytes, {what}s are {size} (not used)");
            }

            if (RuntimeFeature.IsDynamicCodeSupported && PayloadLayout.Derived(type) is { } derived)
            {
                notes?.Add($"{what}s: derived from {type.Name}");
                return derived;
            }

            notes?.Add(
                $"{what}s: hex (no layout declared{(RuntimeFeature.IsDynamicCodeSupported ? $", none derivable from {type.Name}" : "; none derived without generated code")})");
            return null;
        }
    }

    /// <summary>
    ///     Its rows now (at most <paramref name="limit" />, copied under its lock briefly), each key and row rendered by
    ///     its layout.
    /// </summary>
    public override bool RenderState(Utf8JsonWriter w, int limit)
    {
        var keys = new List<TKey>();
        var rows = new List<TRow>();
        long version;
        int count;
        if (!Gates.TryEnter(Gate))
        {
            w.WriteStartObject();
            w.WriteBoolean("busy", true);
            w.WriteEndObject();
            return true;
        }

        try
        {
            version = _seq;
            count = _liveCount;
            for (var i = 0; i < _slots && keys.Count < limit; i++)
                if (_live[i])
                {
                    keys.Add(_keys[i]);
                    rows.Add(_rows[i]);
                }
        }
        finally
        {
            Gate.Exit();
        }

        var format = EffectiveFormat();
        var keyBytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(keys));
        var rowBytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(rows));
        w.WriteStartObject();
        w.WriteNumber("version", version);
        w.WriteNumber("rows", count);
        w.WriteNumber("shown", keys.Count);
        w.WritePropertyName("entries");
        w.WriteStartArray();
        for (var i = 0; i < keys.Count; i++)
        {
            w.WriteStartObject();
            w.WritePropertyName("key");
            Rendering.Fixed(w, keyBytes.Slice(i * KeySize, KeySize), format?.KeyLayout);
            w.WritePropertyName("row");
            Rendering.Fixed(w, rowBytes.Slice(i * RowSize, RowSize), format?.Layout);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
        return true;
    }

    /// <summary>
    ///     A Table record — a snapshot or a delta — its changes rendered: each key and row by its layout, read from the
    ///     record's bytes.
    /// </summary>
    public override bool RenderRecord(Utf8JsonWriter w, ReadOnlySpan<byte> record, bool payload)
    {
        if (payload || record.Length < Wire.TableHeader || record[0] != Wire.Table) return false;
        var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[26..]);
        var deletes = BinaryPrimitives.ReadUInt32LittleEndian(record[34..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(record[30..]) != KeySize ||
            BinaryPrimitives.ReadUInt16LittleEndian(record[32..]) != RowSize || deletes > (uint)n ||
            record.Length < Wire.TableHeader + (long)n * UpsertSize - deletes * (long)RowSize)
            return false;
        var flags = record[5];
        var format = EffectiveFormat();
        w.WriteStartObject();
        w.WriteBoolean("snapshot", (flags & Wire.FlagReset) != 0);
        if ((flags & Wire.FlagDegraded) != 0) w.WriteBoolean("degraded", true);
        w.WriteNumber("fromSeq", BinaryPrimitives.ReadInt64LittleEndian(record[6..]));
        w.WriteNumber("toSeq", BinaryPrimitives.ReadInt64LittleEndian(record[14..]));
        w.WriteString("checksum", $"0x{BinaryPrimitives.ReadUInt32LittleEndian(record[22..]):X8}");
        w.WriteNumber("entries", n);
        w.WritePropertyName("changes");
        w.WriteStartArray();
        for (int i = 0, o = Wire.TableHeader; i < n && i < 1000; i++)
        {
            var delete = record[o] == Wire.OpDelete;
            if (o + (delete ? DeleteSize : UpsertSize) > record.Length) break; // (its ops not as its header says)
            w.WriteStartObject();
            w.WriteString("op", delete ? "delete" : "upsert");
            w.WritePropertyName("key");
            Rendering.Fixed(w, record.Slice(o + 1, KeySize), format?.KeyLayout);
            if (!delete)
            {
                w.WritePropertyName("row");
                Rendering.Fixed(w, record.Slice(o + 1 + KeySize, RowSize), format?.Layout);
            }

            w.WriteEndObject();
            o += delete ? DeleteSize : UpsertSize;
        }

        w.WriteEndArray();
        w.WriteEndObject();
        return true;
    }

    /// <summary>Exact size of the record <see cref="Write" /> would produce now (0 = nothing to send).</summary>
    internal override int Measure(in Cursor cursor)
    {
        if (!Gates.TryEnter(Gate)) return Busy;
        try
        {
            var reset = BeyondHorizon(cursor);
            if (!reset && cursor.Seq == _seq) return 0;
            var bytes = reset
                ? (long)_liveCount * UpsertSize
                : ScanCheaper(cursor)
                    ? ScannedBytes(cursor.Pos)
                    : LoggedBytes(cursor.Pos);
            return (int)(Wire.TableHeader + bytes);
        }
        finally
        {
            Gate.Exit();
        }
    }

    /// <summary>Exact size of the record <see cref="WriteRange" /> would produce now (0 = nothing in range changed).</summary>
    internal int MeasureRange(in Cursor cursor, ReadOnlySpan<byte> prefix, RangeState state)
    {
        if (!Gates.TryEnter(Gate)) return Busy;
        try
        {
            var reset = NeedsReset(cursor);
            if (!reset && cursor.Seq == _seq) return 0;
            var bytes = RangeBytes(cursor, reset, prefix, state);
            return !reset && bytes == 0 ? 0 : (int)(Wire.TableHeader + bytes);
        }
        finally
        {
            Gate.Exit();
        }
    }

    /// <summary>
    ///     A key range of the table (rows whose key bytes start with <paramref name="prefix" />): snapshot or delta like
    ///     <see cref="Write" />, with the checksum of the range — kept per subscriber in <paramref name="state" /> (hash of
    ///     each
    ///     row sent), so a delta costs O(changes). Changes outside the range send nothing; <c>fromSeq</c> is then the
    ///     version after the last record sent, so the client's sequence check still holds.
    /// </summary>
    internal int WriteRange(uint tapId, ref Cursor cursor, Span<byte> dst, ReadOnlySpan<byte> prefix, RangeState state)
    {
        if (!Gates.TryEnter(Gate)) return Busy;
        try
        {
            var reset = NeedsReset(cursor);
            if (!reset && cursor.Seq == _seq) return 0;
            var bytes = RangeBytes(cursor, reset, prefix, state);
            if (!reset && bytes == 0) // (nothing in range — or only rows it never had came and went: no record)
            {
                cursor.Seq = _seq;
                cursor.Pos = _head;
                return 0;
            }

            if (dst.Length < Wire.TableHeader + bytes) return -1;

            var sent = state.Sent;
            int o = Wire.TableHeader, count = 0, deletes = 0;
            if (reset)
            {
                sent.Clear();
                state.Checksum = 0;
                for (var i = 0; i < _slots; i++)
                {
                    if (!_live[i] || !Matches(i, prefix)) continue;
                    var h = HashOf(_keys[i], _rows[i]);
                    sent[i] = h;
                    state.Checksum += h;
                    o += WriteEntry(dst[o..], i);
                    count++;
                }
            }
            else
                for (var p = cursor.Pos; p < _head; p++)
                {
                    var slot = _log[(int)(p & _logMask)];
                    if (_modPos[slot] != p || !Matches(slot, prefix)) continue;
                    if (sent.Remove(slot, out var old)) state.Checksum -= old;
                    else if (!_live[slot]) continue; // (gone, and never sent this subscriber: nothing to it)
                    if (_live[slot])
                    {
                        var h = HashOf(_keys[slot], _rows[slot]);
                        sent[slot] = h;
                        state.Checksum += h;
                    }
                    else deletes++;

                    o += WriteEntry(dst[o..], slot);
                    count++;
                }

            WriteHeader(dst, tapId, reset ? Wire.FlagReset : 0, reset ? 0 : state.SentSeq + 1, state.Checksum, count,
                deletes);
            state.SentSeq = _seq;
            cursor.Seq = _seq;
            cursor.Pos = _head;
            return o;
        }
        finally
        {
            Gate.Exit();
        }
    }

    /// <summary>
    ///     The record that brings <paramref name="cursor" /> up to date, at no more CPU than a snapshot: a delta from walking
    ///     the log while that is cheaper than a pass over the slots; past that, one pass over the slots either way — writing
    ///     just the rows changed since the cursor (<c>_modPos</c>; deleted rows keep their keys while their delete is on the
    ///     log), so a slow client gets the fewer bytes, unless that is no fewer bytes than a snapshot. Either way, no delete
    ///     for a row that came and went since the cursor (<see cref="Unheld" />): the client never had it; and a delete is its
    ///     key alone. Past the log's horizon, a snapshot.
    /// </summary>
    internal override int Write(uint tapId, ref Cursor cursor, Span<byte> dst)
    {
        if (!Gates.TryEnter(Gate)) return Busy;
        try
        {
            var reset = BeyondHorizon(cursor);
            if (!reset && cursor.Seq == _seq) return 0;
            if (dst.Length < Wire.TableHeader) return -1;

            int o = Wire.TableHeader, count = 0, deletes = 0;
            var scan = !reset && ScanCheaper(cursor);
            if (scan)
            {
                var snapshot = (long)_liveCount * UpsertSize;
                for (var i = 0; i < _slots; i++)
                {
                    if (_modPos[i] < cursor.Pos || Unheld(i, cursor.Pos)) continue;
                    var size = _live[i] ? UpsertSize : DeleteSize;
                    if (o - Wire.TableHeader + size >= snapshot) // a snapshot is no bigger: write that instead (ScannedBytes)
                    {
                        reset = true;
                        o = Wire.TableHeader;
                        count = deletes = 0;
                        break;
                    }

                    if (o + size > dst.Length) return -1;
                    o += WriteEntry(dst[o..], i);
                    count++;
                    if (!_live[i]) deletes++;
                }
            }

            if (reset)
            {
                if (dst.Length < Wire.TableHeader + (long)_liveCount * UpsertSize) return -1;
                for (var i = 0; i < _slots; i++)
                {
                    if (!_live[i]) continue;
                    o += WriteEntry(dst[o..], i);
                    count++;
                }
            }
            else if (!scan)
            {
                for (var p = cursor.Pos; p < _head; p++)
                {
                    var slot = _log[(int)(p & _logMask)];
                    if (_modPos[slot] != p) continue; // superseded by a later change to the same row
                    if (Unheld(slot, cursor.Pos)) continue;
                    var size = _live[slot] ? UpsertSize : DeleteSize;
                    if (o + size > dst.Length) return -1;
                    o += WriteEntry(dst[o..], slot);
                    count++;
                    if (!_live[slot]) deletes++;
                }
            }

            WriteHeader(dst, tapId, reset ? Wire.FlagReset : 0, reset ? 0 : cursor.Seq + 1, _checksum, count, deletes);
            cursor.Seq = _seq;
            cursor.Pos = _head;
            return o;
        }
        finally
        {
            Gate.Exit();
        }
    }

    /// <summary>
    ///     A snapshot of the last good state passed through <paramref name="degrade" />, flagged reset + degraded, with the
    ///     checksum of the degraded rows (so the client still verifies what it shows). A throwing degrade function yields an
    ///     empty degraded snapshot.
    /// </summary>
    internal override int WriteDegraded(uint tapId, StreamFault? fault, IDegradeFunction? degrade, Span<byte> dst,
        bool first)
    {
        return WriteDegraded(tapId, fault, degrade, dst, first, default);
    }

    /// <summary>The stale state: its rows through the policy's function, as they are (replayed), or the provider's fixed rows.</summary>
    internal override int WriteStale(uint tapId, ErrorPolicy policy, StreamFault? fault, Span<byte> dst, bool first)
    {
        return policy.Kind switch
        {
            StaleData.ReplayLast => WriteDegraded(tapId, fault, DegradeFunction.Unchanged, dst, first),
            StaleData.Static => WriteStatic(tapId, fault, policy.State.Span, dst),
            _ => WriteDegraded(tapId, fault, policy.Degrade, dst, first)
        };
    }

    /// <summary>
    ///     Fixed rows (count × key | row) as a degraded snapshot, their checksum with them. Rows not of this table's
    ///     shape: none.
    /// </summary>
    private int WriteStatic(uint tapId, StreamFault? fault, ReadOnlySpan<byte> rows, Span<byte> dst)
    {
        if (!Gates.TryEnter(Gate)) return Busy;
        try
        {
            if (!ReferenceEquals(Fault, fault)) return FaultCleared;
            var stride = KeySize + RowSize;
            if (rows.Length % stride != 0) return 0;
            var count = rows.Length / stride;
            if (dst.Length < Wire.TableHeader + (long)count * UpsertSize) return -1;
            var o = Wire.TableHeader;
            uint checksum = 0;
            for (var r = 0; r < count; r++)
            {
                var key = rows.Slice(r * stride, KeySize);
                var row = rows.Slice(r * stride + KeySize, RowSize);
                dst[o] = Wire.OpUpsert;
                key.CopyTo(dst[(o + 1)..]);
                row.CopyTo(dst[(o + 1 + KeySize)..]);
                checksum += RowHash.Of(key, row);
                o += UpsertSize;
            }

            WriteHeader(dst, tapId, Wire.FlagReset | Wire.FlagDegraded, 0, checksum, count);
            return o;
        }
        finally
        {
            Gate.Exit();
        }
    }

    /// <summary>As <see cref="WriteDegraded(uint,StreamFault?,IDegradeFunction?,Span{byte},bool)" />, over a key range.</summary>
    internal int WriteDegraded(uint tapId, StreamFault? fault, IDegradeFunction? degrade, Span<byte> dst, bool first,
        ReadOnlySpan<byte> prefix)
    {
        if (!Gates.TryEnter(Gate)) return Busy;
        try
        {
            if (!ReferenceEquals(Fault, fault)) return FaultCleared; // recovered (or re-faulted) since the tap looked
            if (degrade is null) return 0;
            if (!first && dst.Length < Wire.TableHeader + (long)_liveCount * UpsertSize)
                return -1; // wait for a frame of its own
            int stride = 4 + KeySize + RowSize, count = CountLive(prefix);
            EnsureScratch(count * stride, count);
            var entries = _batchRows.AsSpan(0, count * stride);
            for (int i = 0, r = 0; i < _slots; i++)
            {
                if (!_live[i] || !Matches(i, prefix)) continue;
                var e = entries.Slice(r * stride, stride);
                BinaryPrimitives.WriteUInt32LittleEndian(e, FoldEntry.Present);
                MemoryMarshal.Write(e[4..], in _keys[i]);
                MemoryMarshal.Write(e[(4 + KeySize)..], in _rows[i]);
                _batchSlots[r++] = i;
            }

            try
            {
                degrade.Degrade(entries, count, KeySize, RowSize);
            }
            catch
            {
                count = 0;
            }

            var kept = 0;
            for (var r = 0; r < count; r++)
                if ((BinaryPrimitives.ReadUInt32LittleEndian(entries[(r * stride)..]) & FoldEntry.Present) != 0)
                    kept++;
            if (dst.Length < Wire.TableHeader + (long)kept * UpsertSize) return -1;

            var o = Wire.TableHeader;
            uint checksum = 0;
            for (var r = 0; r < count; r++)
            {
                var e = entries.Slice(r * stride, stride);
                if ((BinaryPrimitives.ReadUInt32LittleEndian(e) & FoldEntry.Present) == 0) continue;
                var key = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in _keys[_batchSlots[r]])); // keys are read-only
                var row = e.Slice(4 + KeySize, RowSize);
                dst[o] = Wire.OpUpsert;
                key.CopyTo(dst[(o + 1)..]);
                row.CopyTo(dst[(o + 1 + KeySize)..]);
                checksum += RowHash.Of(key, row);
                o += UpsertSize;
            }

            WriteHeader(dst, tapId, Wire.FlagReset | Wire.FlagDegraded, 0, checksum, kept);
            return o;
        }
        finally
        {
            Gate.Exit();
        }
    }

    private void WriteHeader(Span<byte> dst, uint tapId, int flags, long fromSeq, uint checksum, int count,
        int deletes = 0)
    {
        dst[0] = Wire.Table;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], tapId);
        dst[5] = (byte)flags;
        BinaryPrimitives.WriteInt64LittleEndian(dst[6..], fromSeq);
        BinaryPrimitives.WriteInt64LittleEndian(dst[14..], _seq);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[22..], checksum);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[26..], (uint)count);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[30..], (ushort)KeySize);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[32..], (ushort)RowSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[34..], (uint)deletes);
    }

    /// <summary>A slot's entry: its key and row, or, deleted, its key alone. Returns its size.</summary>
    private int WriteEntry(Span<byte> dst, int slot)
    {
        MemoryMarshal.Write(dst[1..], in _keys[slot]);
        if (!_live[slot])
        {
            dst[0] = Wire.OpDelete;
            return DeleteSize;
        }

        dst[0] = Wire.OpUpsert;
        MemoryMarshal.Write(dst[(1 + KeySize)..], in _rows[slot]);
        return UpsertSize;
    }

    private static uint HashOf(in TKey key, in TRow row)
    {
        return RowHash.Of(MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)),
            MemoryMarshal.AsBytes(new ReadOnlySpan<TRow>(in row)));
    }

    private static bool Same(in TRow a, in TRow b)
    {
        return MemoryMarshal.AsBytes(new ReadOnlySpan<TRow>(in a))
            .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<TRow>(in b)));
    }

    /// <summary>A row's state before the open update first touched it.</summary>
    private struct Undo
    {
        public int Slot;
        public bool WasLive, Created;
        public TRow Row;
    }

    /// <summary>
    ///     An open update. Changes apply to the table immediately (readers are locked out until it ends) and are recorded
    ///     for rollback; the change log is written only by <see cref="Complete" />, so rows that end where they started are
    ///     not published.
    /// </summary>
    public readonly ref struct Updater
    {
        private readonly Table<TKey, TRow> _table;
        private readonly int _tx;

        internal Updater(Table<TKey, TRow> table, int tx)
        {
            _table = table;
            _tx = tx;
        }

        public void Set(in TKey key, in TRow row)
        {
            _table.Check(_tx);
            _table.Store(_table.SlotFor(key), true, row);
        }

        public void Delete(in TKey key)
        {
            _table.Check(_tx);
            if (_table._index.TryGetValue(key, out var slot)) _table.Store(slot, false, default);
        }

        /// <summary>Folds with a compile-time function (inlined, zero allocation), e.g. <c>Fold&lt;long, Add&lt;long&gt;&gt;</c>.</summary>
        public void Fold<TDelta, TFold>(in TKey key, in TDelta delta)
            where TDelta : unmanaged where TFold : IRowFold<TRow, TDelta>
        {
            _table.Check(_tx);
            _table.FoldOne<TDelta, TFold>(key, delta);
        }

        /// <summary>Folds a batch with a runtime (e.g. sandboxed) function in a single call.</summary>
        public void Fold<TDelta>(ReadOnlySpan<TKey> keys, ReadOnlySpan<TDelta> deltas, IBatchFold fold)
            where TDelta : unmanaged
        {
            _table.Check(_tx);
            _table.FoldBatch(keys, deltas, fold);
        }

        public void Clear()
        {
            _table.Check(_tx);
            _table.ClearRows();
        }

        /// <summary>Postcondition (e.g. the provider's checksum of the result), checked when the update or transaction completes.</summary>
        public void ExpectChecksum(uint checksum)
        {
            _table.Check(_tx);
            _table._txExpectedChecksum = checksum;
        }

        /// <summary>
        ///     Publishes the update and returns the new <see cref="Version" />. <paramref name="upstreamVersion" /> becomes the
        ///     table's <see cref="UpstreamVersion" /> (the precondition for the next update). With
        ///     <paramref name="expectedChecksum" />, the resulting state must have that checksum (e.g. computed by the
        ///     provider): otherwise the update is rolled back, the table faults (<see cref="FaultCode.ChecksumMismatch" />)
        ///     and this throws — a wrong fold cannot publish a state the source never had.
        /// </summary>
        public long Complete(long? upstreamVersion = null, uint? expectedChecksum = null)
        {
            return _table.Finish(_tx, true, upstreamVersion, expectedChecksum);
        }

        /// <summary>Rolls back unless completed. A fold failure inside the update faults the table.</summary>
        public void Dispose()
        {
            _table.Finish(_tx, false, null, null);
        }
    }
}