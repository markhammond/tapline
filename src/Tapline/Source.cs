using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace Tapline;

/// <summary>A group's claim on a source: joined, and — once its leave is staged — leaving.</summary>
internal sealed class OwnerClaim(IngestGroup group)
{
    public readonly IngestGroup Group = group;
    public bool Leaving;
}

/// <summary>
///     Taking a source's lock on the router's own threads (building frames, draining ingests): never parked, never
///     yielding
///     its timeslice. SpinWait's own back-off would yield after a few spins and, by default, sleep a millisecond every 20
///     —
///     a tail-latency trap — so it spins only while a spin is just a spin (<see cref="SpinWait.NextSpinWillYield" />),
///     then
///     gives up: backing off is the caller's, deliberate (a tap retried 1 ms later on the outlet's wheel, a drain queued
///     again).
/// </summary>
internal static class Gates
{
    /// <summary>The lock, if it can be had now or within a few spins; else false — retry later.</summary>
    public static bool TryEnter(Lock gate)
    {
        if (gate.TryEnter()) return true;
        for (var spinner = new SpinWait(); !spinner.NextSpinWillYield;)
        {
            spinner.SpinOnce(-1);
            if (gate.TryEnter()) return true;
        }

        return false;
    }
}

/// <summary>A tap's position in a source. <see cref="Seq" /> &lt; 0 means "send a snapshot next".</summary>
public struct Cursor
{
    public long Seq;
    public long Pos;
    public static Cursor Snapshot => new() { Seq = -1 };
}

/// <summary>
///     Shared, pull-based stream state. Sources never queue per client: each <see cref="Tap" /> holds a
///     <see cref="Cursor" />
///     and the source writes "whatever brings that cursor up to date" when the client's frame is built. Squashing is
///     therefore by construction, and per-client cost is O(1) state regardless of the update rate.
///     <para>
///         A source is either healthy or <see cref="Fault">faulted</see>. A faulted source keeps its last good state; each
///         active
///         tap then receives its <see cref="ErrorPolicy" /> (degraded snapshot + error) and ends, and its client
///         resubscribes.
///     </para>
/// </summary>
public abstract class Source
{
    private const int UpstreamDepth = 64; // a power of two: an index is a mask

    /// <summary>
    ///     Returned by <see cref="Write" /> when the tap fell behind beyond recovery: its subscription ends (
    ///     <see cref="FaultCode.Overflowed" />).
    /// </summary>
    internal const int Overflowed = int.MinValue + 2;

    /// <summary>Returned by <see cref="WriteDegraded" /> when the fault it was asked about is no longer current.</summary>
    internal const int FaultCleared = int.MinValue;

    /// <summary>
    ///     Returned by <see cref="Write" /> when it couldn't take its locks without risking a deadlock: try again
    ///     shortly.
    /// </summary>
    internal const int Busy = int.MinValue + 1;

    private static long _nextId;
    private readonly Lock _tapLock = new();

    /// <summary>Process-wide identity; also the canonical order in which multi-source readers and writers take locks.</summary>
    internal readonly long SourceId = Interlocked.Increment(ref _nextId);

    private long _changedAt;
    private bool _closed; // the last acknowledgement found no route: the epoch is over
    private long _epoch = 1;
    private StreamFault? _fault;
    private Tap[] _taps = [];
    private UpstreamMark[]? _upstreams;

    /// <summary>
    ///     The last record sent out of band, reusable by any other tap that would write the same bytes — one at the same
    ///     cursor (a snapshot; the same delta), or any tap behind, for a source whose record is its whole state — while the
    ///     source can still resume from where it leaves them. Sharing without hashing.
    /// </summary>
    internal OutOfBandRecord? BlobCache;

    /// <summary>
    ///     The groups that have joined it, in order — the first its writer; each after the first waits for the one before to
    ///     have its leave applied. Under <see cref="LockDomain.Changes" />; null until first joined.
    /// </summary>
    internal List<OwnerClaim>? Claims;

    /// <summary>The sources it is written together with, or null: written alone (see <see cref="LockDomain" />).</summary>
    internal LockDomain? Domain;

    /// <summary>
    ///     Changes of <see cref="Domain" /> so far — a reader's check that it hasn't changed since (null back to null
    ///     included).
    /// </summary>
    internal long DomainEpoch;

    /// <summary>
    ///     Where providers accept it (<see cref="SubscribeRequest.Accept" />): null, not yet; empty, outside any partition
    ///     (any
    ///     service's); else the partition it is in, alone — <c>service/partition</c> — from its first accept.
    /// </summary>
    internal string? ServicePath;

    /// <summary>The group ingest writing it — then its only writer — or null: <see cref="Claims" />' first.</summary>
    internal IngestGroup? Writer;

    /// <summary>
    ///     How its payloads are laid out (<see cref="PayloadFormat" />), so the router's viewer can render them — on its
    ///     topic's
    ///     page and its records': its provider's, given in step 1 (<see cref="SubscribeRequest.Accept" />; the latest given
    ///     wins), or set where it is published. Without one, a value's or a message's payload is shown as its bytes; a table's
    ///     keys and rows by layouts derived from their types where code can be generated (<see cref="PayloadLayout.Of{T}" />),
    ///     else as hex.
    /// </summary>
    public PayloadFormat? Format { get; set; }

    /// <summary>
    ///     How much of its records its subscriptions' histories keep for the viewer (null: the outlet's,
    ///     <see cref="OutletOptions.RecordRetention" />) — never more than its provider allows (
    ///     <see cref="PayloadFormat.Retention" />).
    /// </summary>
    public RecordRetention? Retention { get; set; }

    /// <summary>Monotonic version; a tap has work while its cursor's Seq differs.</summary>
    public abstract long Version { get; }

    /// <summary>A name to show for it (the viewer's): a table's name, a last value's family and key.</summary>
    public string? Label { get; init; }

    /// <summary>When it last changed (Stopwatch ticks; 0: never) — its data's arrival from the provider, for the viewer's lag.</summary>
    internal virtual long ChangedAt => Volatile.Read(ref _changedAt);

    /// <summary>Non-null while the source is faulted.</summary>
    public virtual StreamFault? Fault => Volatile.Read(ref _fault);

    /// <summary>How this source's records travel (see <see cref="Tapline.Delivery" />).</summary>
    internal virtual Delivery DeliveryMode => Delivery.Auto;

    /// <summary>
    ///     Whether this source's records are worth compressing (a transport that compresses per message may skip the
    ///     rest).
    /// </summary>
    internal virtual bool IsCompressible => true;

    /// <summary>The record type this source writes (the <c>inner</c> of an out-of-band handle).</summary>
    internal abstract byte RecordType { get; }

    /// <summary>
    ///     A table's key and row sizes, in bytes (any other topic: 0, 0): what a router in another process makes its copy of
    ///     the table by.
    /// </summary>
    public virtual (int Key, int Row) Layout => default;

    /// <summary>
    ///     The lock guarding the source's state (taken, in <see cref="SourceId" /> order, by multi-source readers and
    ///     writers).
    /// </summary>
    internal virtual Lock? Gate => null;

    /// <summary>Same state for every tap (so an out-of-band snapshot can be shared), unlike a per-subscription watch.</summary>
    internal virtual bool Shared => true;

    /// <summary>Every record is the whole state (a value): any tap behind is brought up to date by the same bytes.</summary>
    internal virtual bool RecordIsWholeState => false;

    /// <summary>
    ///     A subscriber may start with the next change rather than the state (<see cref="InitialState.None" />): a value,
    ///     a message stream.
    /// </summary>
    internal virtual bool StartsAtNext => RecordIsWholeState;

    /// <summary>
    ///     A tap's cursor alone says where it is, so it can be set back to an earlier one — after a reconnect, to what its
    ///     client received (a table, a value, a message stream). False (a watch: its members' cursors are its own): a client
    ///     that missed something gets a snapshot instead.
    /// </summary>
    internal virtual bool Rewindable => false;

    /// <summary>Its routes' taps, as they are now (copy-on-write).</summary>
    internal Tap[] Taps => Volatile.Read(ref _taps);

    /// <summary>
    ///     A barrier: every subscription of this source is sent its state as it stands now, its
    ///     <see cref="Tap.MinInterval" /> notwithstanding — a day's close, say, that no subscriber may conflate past. Call it
    ///     once the barrier's state is the source's (an ingest's post applied: <c>WhenApplied</c>).
    /// </summary>
    public void Barrier()
    {
        foreach (var t in Volatile.Read(ref _taps)) t.Hurry();
    }

    /// <summary>
    ///     Out-of-band records named by their content (SHA-256): identical bytes are stored once — found before they are
    ///     copied — and verified by the client. Worth it when the same bytes recur outside what the source shares anyway
    ///     (the same record to every tap at the same point is stored once regardless): e.g. a table whose state returns to
    ///     an earlier one, or many sources publishing the same values. Default false: a unique id per record, no hashing.
    /// </summary>
    public bool ContentAddressed { get; set; }

    /// <summary>
    ///     A cursor at the newest state: a tap there is sent the next change, not the state as it is (a whole-state
    ///     source).
    /// </summary>
    internal virtual Cursor Head => new() { Seq = Version };

    /// <summary>
    ///     Routes bound to this topic: taps bound or active — a subscriber between its two handshakes counts (an orphan
    ///     expires after <see cref="OutletOptions.BindTimeout" />).
    /// </summary>
    public int Routes => Volatile.Read(ref _taps).Length;

    /// <summary>Its process-wide identity (the viewer's topic id).</summary>
    public long Id => SourceId;

    /// <summary>The topic's current epoch (see <see cref="Acknowledge()" />).</summary>
    public long Epoch
    {
        get
        {
            lock (_tapLock)
            {
                return _epoch;
            }
        }
    }

    /// <summary>
    ///     What its records are kept as: <see cref="Retention" />, else <paramref name="fallback" />, lowered to its
    ///     provider's ceiling.
    /// </summary>
    internal RecordRetention RetentionFor(RecordRetention fallback)
    {
        var chosen = Retention ?? fallback;
        return Format?.Retention is { } ceiling && ceiling < chosen ? ceiling : chosen;
    }

    /// <summary>
    ///     The format its payloads are rendered by — <see cref="Format" />, or one made up for it (a table's layouts, derived
    ///     from its key and row types) — and why, in <paramref name="notes" />. Introspection, as <see cref="RenderState" />
    ///     and <see cref="RenderRecord" /> are: for a router's viewer.
    /// </summary>
    public virtual PayloadFormat? EffectiveFormat(ICollection<string>? notes = null) => Format;

    /// <summary>
    ///     Writes its state now as JSON, for a router's viewer — at most <paramref name="limit" /> rows or messages;
    ///     false if it has none to show.
    /// </summary>
    public virtual bool RenderState(Utf8JsonWriter w, int limit) => false;

    /// <summary>
    ///     Writes a record of it as it travelled as JSON, for a router's viewer: <paramref name="record" /> whole (a Table,
    ///     Value
    ///     or TopicMessage record), or — <paramref name="payload" /> — a value's payload alone (an out-of-band value's blob).
    ///     False if it can't be read.
    /// </summary>
    public virtual bool RenderRecord(Utf8JsonWriter w, ReadOnlySpan<byte> record, bool payload) => false;

    /// <summary>Faults it without its lock — its group's (its only writer) — first fault wins.</summary>
    internal bool SetFault(StreamFault fault)
        => TrySetFault(fault);

    internal void NotifyChanged() => Notify();

    /// <summary>
    ///     Marks <paramref name="version" />, about to be published, as the provider's <paramref name="upstream" />. Its
    ///     writer's alone (under its lock), before the version is published.
    /// </summary>
    internal void MarkUpstream(long version, long upstream)
    {
        var ring = _upstreams;
        if (ring is null) Volatile.Write(ref _upstreams, ring = new UpstreamMark[UpstreamDepth]);
        ref var slot = ref ring[(int)(version & (UpstreamDepth - 1))];
        Interlocked.Exchange(ref slot.Version, 0); // being written: a reader that already read it sees it change
        slot.Upstream = upstream;
        Volatile.Write(ref slot.Version, version);
    }

    /// <summary>
    ///     The upstream version — the provider's own numbering, as its publish gave it (<c>upstreamVersion</c>) — that
    ///     <paramref name="version" /> was published as; null if that publish gave none, or it's no longer kept (a source
    ///     keeps its last 64).
    /// </summary>
    public long? UpstreamAt(long version)
    {
        if (version <= 0 || Volatile.Read(ref _upstreams) is not { } ring) return null;
        ref var slot = ref ring[(int)(version & (UpstreamDepth - 1))];
        if (Volatile.Read(ref slot.Version) != version) return null;
        var upstream = Volatile.Read(ref slot.Upstream); // (an acquire: the check after it stays after it)
        return Volatile.Read(ref slot.Version) == version ? upstream : null;
    }

    /// <summary>
    ///     Exact size of the record <see cref="Write" /> would produce now (0 = nothing); -1 if unsupported (can't be a
    ///     watch member).
    /// </summary>
    internal virtual int Measure(in Cursor cursor) => -1;

    /// <summary>
    ///     Whether a tap at <paramref name="cursor" /> has fallen behind what the source keeps, beyond recovery (a
    ///     message stream's overflow).
    /// </summary>
    internal virtual bool IsOverflowed(in Cursor cursor) => false;

    /// <summary>A tap at <paramref name="cursor" /> would be brought up to date by a delta (not another snapshot).</summary>
    internal virtual bool CanResumeFrom(in Cursor cursor) => cursor.Seq == Version;

    /// <summary>This topic as the viewer shows it — read without its lock (best effort).</summary>
    public TopicView Describe()
    {
        long changed = ChangedAt, version = Version;
        return new TopicView(SourceId, Label, MessageLog.KindName(RecordType), version, Volatile.Read(ref _epoch),
            Volatile.Read(ref _closed),
            Routes, changed == 0 ? null : Clock.At(changed), Fault is { } f ? $"{f.Code}: {f.Message}" : null,
            UpstreamAt(version));
    }

    /// <summary>
    ///     The answer to a publish: <see cref="PublishStatus.Delivered" /> (201) while any route is bound to this topic, else
    ///     <see cref="PublishStatus.NoListeners" /> (202) — with the epoch it answers for. The epoch ends when its last route
    ///     goes
    ///     (and a 202 finds it so), atomically with binds: the next bind (<see cref="SubscribeRequest.Accept" />) opens a new
    ///     one and hands it to the provider. So a
    ///     provider that stops producing on a 202 only if it carries the epoch it last accepted a subscriber on can't stop
    ///     just after a new subscriber arrived (a 202 from before it is stale). <see cref="LastValueSource.Publish" /> returns
    ///     it; after a table update, ask.
    /// </summary>
    public PublishResult Acknowledge()
        => Acknowledge(0);

    /// <summary>
    ///     As <see cref="Acknowledge()" />, for a publish that made <paramref name="seq" /> (
    ///     <see cref="PublishResult.Seq" />).
    /// </summary>
    private protected PublishResult Acknowledge(long seq)
    {
        lock (_tapLock)
        {
            if (_taps.Length > 0) return new PublishResult(PublishStatus.Delivered, _epoch, seq);
            _closed = true;
            return new PublishResult(PublishStatus.NoListeners, _epoch, seq);
        }
    }

    /// <summary>
    ///     The bytes that travel out of band for <paramref name="cursor" />: the whole record with tap id 0 (tables, groups)
    ///     or just the payload (values). Same convention as <see cref="Write" />.
    /// </summary>
    internal virtual int WriteBlob(ref Cursor cursor, Span<byte> dst)
        => Write(0, ref cursor, dst);

    /// <summary>
    ///     Declares the source failed (its upstream disconnected, reported a gap...). Active taps get their error policy and
    ///     end. The first fault wins until recovery: a table recovers through <see cref="Table{TKey,TRow}.BeginRecovery" />,
    ///     a last-value source with its next publish.
    /// </summary>
    public virtual void Fail(FaultCode code, string? message = null, Exception? exception = null)
    {
        if (code == FaultCode.None) throw new ArgumentOutOfRangeException(nameof(code));
        if (TrySetFault(new StreamFault(code, Version, message, exception))) Notify();
    }

    /// <summary>First fault wins. A full fence, so it can be followed by <see cref="Notify" />.</summary>
    protected bool TrySetFault(StreamFault fault)
        => Interlocked.CompareExchange(ref _fault, fault, null) is null;

    protected bool TryClearFault()
        => Volatile.Read(ref _fault) is not null && Interlocked.Exchange(ref _fault, null) is not null;

    /// <summary>
    ///     Writes the record that brings <paramref name="cursor" /> up to date and advances it. Returns bytes written,
    ///     0 when there is nothing to send, or -1 when the record does not fit in <paramref name="dst" />.
    ///     Called while the client's frame is being built: must be bounded and non-blocking.
    /// </summary>
    internal abstract int Write(uint tapId, ref Cursor cursor, Span<byte> dst);

    /// <summary>
    ///     Writes a degraded snapshot of the last good state through <paramref name="degrade" />, provided
    ///     <paramref name="fault" /> is still the current fault (checked atomically with the state it reads; otherwise
    ///     <see cref="FaultCleared" />, and the tap is served normally). Same return convention as <see cref="Write" />;
    ///     0 means no degraded form (no function, or a source without one): the client gets the error only. When not
    ///     <paramref name="first" /> in the frame and even the undegraded state might not fit, returns -1 without running
    ///     the function — so it runs once per faulted tap, in whichever frame it fits.
    /// </summary>
    internal virtual int WriteDegraded(uint tapId, StreamFault? fault, IDegradeFunction? degrade, Span<byte> dst,
        bool first)
        => ReferenceEquals(Fault, fault) ? 0 : FaultCleared;

    /// <summary>
    ///     The stale data <paramref name="policy" /> makes of this source's state for a tap — as <see cref="WriteDegraded" />
    ///     (same returns, the same check of <paramref name="fault" />); by default its function, if it has one.
    /// </summary>
    internal virtual int WriteStale(uint tapId, ErrorPolicy policy, StreamFault? fault, Span<byte> dst, bool first)
        => policy.Kind == StaleData.Function ? WriteDegraded(tapId, fault, policy.Degrade, dst, first) :
            ReferenceEquals(Fault, fault) ? 0 : FaultCleared;

    /// <summary>Binds a route; returns the topic's epoch — a new one if the last acknowledgement closed it.</summary>
    internal virtual long AddTap(Tap t)
    {
        lock (_tapLock)
        {
            _taps = [.. _taps, t];
            if (_closed)
            {
                _epoch++;
                _closed = false;
            }

            return _epoch;
        }
    }

    /// <summary>
    ///     Removes a route; returns the topic's epoch and the routes left. The last one gone ends the epoch, as a 202 does:
    ///     a subscriber binding after it gets a new one — so an unsubscribe notice saying none are left, arriving after it,
    ///     is recognisably stale.
    /// </summary>
    internal virtual (long Epoch, int Remaining) RemoveTap(Tap t)
    {
        lock (_tapLock)
        {
            var i = Array.IndexOf(_taps, t);
            if (i >= 0) _taps = [.. _taps.AsSpan(0, i), .. _taps.AsSpan(i + 1)];
            if (_taps.Length == 0) _closed = true;
            return (_epoch, _taps.Length);
        }
    }

    /// <summary>
    ///     Marks every active tap dirty: a clean tap joins the back of its outlet's ready queue (and wakes it); a dirty one
    ///     keeps its place. Callers publish with a full fence (an Interlocked op) first.
    /// </summary>
    protected void Notify()
    {
        Volatile.Write(ref _changedAt, Stopwatch.GetTimestamp());
        foreach (var t in Volatile.Read(ref _taps)) t.MarkDirty();
    }

    // The upstream version — the provider's own numbering, given with a publish — of each of its last versions: a ring,
    // made at the first one given (none given: nothing kept, nothing allocated). Read without a lock: a slot's version is
    // cleared before its value is written, written after it, and checked on both sides of reading it.
    private struct UpstreamMark
    {
        public long Version, Upstream;
    }
}

/// <summary>A record stored out of band: written for a tap at <see cref="Start" />, leaving it at <see cref="After" />.</summary>
internal sealed record OutOfBandRecord(Cursor Start, Cursor After, BlobId Id, int Length, bool ContentAddressed);

/// <summary>
///     Last-value stream: an opaque value where only the newest matters (a mark, a ticker, a position). Snapshot and delta
///     are the same thing — the current value — so a slow client simply skips intermediate values. A publish after a fault
///     is the recovery (a whole value is always consistent). Values over 64 KB travel out of band.
/// </summary>
public sealed class LastValueSource : Source, ITransactional
{
    /// <summary>Largest value (it travels out of band above 64 KB).</summary>
    public const int MaxValueBytes = 64 << 20;

    private byte[] _buffer = new byte[64];
    private bool _compressible = true, _stagedCompressible = true;
    private Delivery _delivery;
    private int _length;
    private byte[] _staged = [];
    private Delivery _stagedDelivery;
    private int _stagedLength = -1;
    private bool _txOpen;
    private StreamFault? _txPending;
    private long _version;
    internal override bool Rewindable => true;

    public override long Version => Volatile.Read(ref _version);
    internal override bool IsCompressible => Volatile.Read(ref _compressible);
    internal override Delivery DeliveryMode => _delivery; // (a word: read whole without the lock)
    internal override byte RecordType => Wire.Value;
    internal override Lock Gate { get; } = new();

    internal override bool RecordIsWholeState => true;

    // ───────────── as part of a Transaction: the value is staged and published on commit ─────────────

    Source ITransactional.Source => this;

    StreamFault? ITransactional.EnterTx(long? expectedUpstream, bool recovery, bool shared)
    {
        Gate.Enter();
        if (_txOpen)
        {
            Gate.Exit();
            throw new InvalidOperationException("A transaction is already open on this source.");
        }

        if (Volatile.Read(ref Writer) is not null)
        {
            Gate.Exit();
            throw new InvalidOperationException("This source is written through an ingest: publish in its units.");
        }

        OpenStaged(); // no upstream version: a whole value always applies
        return null;
    }

    StreamFault? ITransactional.PrepareTx() => _txPending;
    

    bool ITransactional.CommitTx(long? upstreamVersion)
    {
        if (_stagedLength < 0) return false;
        (_buffer, _staged) = (_staged, _buffer);
        _length = _stagedLength;
        _delivery = _stagedDelivery;
        Volatile.Write(ref _compressible, _stagedCompressible);
        if (upstreamVersion is { } upstream) MarkUpstream(_version + 1, upstream); // (before the version is published)
        Interlocked.Increment(ref _version); // full fence before Notify
        TryClearFault();
        return true;
    }

    void ITransactional.RollbackTx() => _stagedLength = -1;
    

    StreamFault? ITransactional.PendingTx => _txPending;

    bool ITransactional.FaultTx(StreamFault fault)
    {
        lock (Gate)
        {
            return TrySetFault(fault);
        }
    }

    void ITransactional.ExitTx()
    {
        CloseStaged();
        Gate.Exit();
    }

    void ITransactional.NotifyTx()
        => Notify();

    /// <param name="value">The new value.</param>
    /// <param name="delivery">Auto: in-band up to the outlet's threshold. Reference: clients get a handle to fetch.</param>
    /// <param name="compressible">
    ///     False for a value a per-message compressor can't shrink (already compressed, encrypted,
    ///     random): a transport that compresses (Tapline.BlazorRealtime's circuit) then leaves frames made of such content alone.
    /// </param>
    /// <returns>
    ///     201/202: whether anyone is subscribed to it (see <see cref="Source.Acknowledge()" />); and the version it
    ///     made.
    /// </returns>
    public PublishResult Publish(ReadOnlySpan<byte> value, Delivery delivery = Delivery.Auto, bool compressible = true)
    {
        long version;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, MaxValueBytes);
        if (value.Length > ushort.MaxValue && delivery == Delivery.Inline)
            throw new ArgumentOutOfRangeException(nameof(value), "Values over 64 KB can't be inline.");
        lock (Gate)
        {
            if (_txOpen)
                throw new InvalidOperationException(
                    "This source is part of an open Transaction: publish through tx.Of(source).");
            if (Volatile.Read(ref Writer) is not null)
                throw new InvalidOperationException("This source is written through an ingest: publish in its units.");
            if (value.Length > _buffer.Length)
                _buffer = new byte[(int)BitOperations.RoundUpToPowerOf2((uint)value.Length)];
            value.CopyTo(_buffer);
            _length = value.Length;
            _delivery = delivery;
            Volatile.Write(ref _compressible, compressible);
            version = Interlocked.Increment(ref _version);
            TryClearFault();
        }

        Notify();
        return Acknowledge(version);
    }

    public override bool RenderState(Utf8JsonWriter w, int limit)
    {
        byte[] copy;
        long version;
        if (!Gates.TryEnter(Gate))
        {
            w.WriteStartObject();
            w.WriteBoolean("busy", true);
            w.WriteEndObject();
            return true;
        }

        try
        {
            if (_version == 0) return false;
            version = _version;
            copy = _buffer.AsSpan(0, Math.Min(_length, Rendering.MaxPayload + 1)).ToArray();
        }
        finally
        {
            Gate.Exit();
        }

        w.WriteStartObject();
        w.WriteNumber("version", version);
        w.WritePropertyName("value");
        Rendering.PayloadOrBytes(w, copy, Format);
        w.WriteEndObject();
        return true;
    }

    public override bool RenderRecord(Utf8JsonWriter w, ReadOnlySpan<byte> record, bool payload)
    {
        if (!payload)
        {
            if (record.Length < Wire.ValueHeader || record[0] != Wire.Value) return false;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(record[5..]);
            if (record.Length < Wire.ValueHeader + length) return false;
            record = record.Slice(Wire.ValueHeader, length);
        }

        Rendering.PayloadOrBytes(w, record, Format);
        return true;
    }

    /// <summary>-1 for a value over 64 KB: a Value record can't carry it (it can't be part of a watch).</summary>
    internal override int Measure(in Cursor cursor)
    {
        if (!Gates.TryEnter(Gate)) return Busy;
        try
        {
            return _version == 0 || cursor.Seq == _version ? 0 :
                _length > ushort.MaxValue ? -1 : Wire.ValueHeader + _length;
        }
        finally
        {
            Gate.Exit();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Called inside an open <see cref="Transaction" /> this source is part of, the transaction rolls back and
    ///     faults.
    /// </remarks>
    public override void Fail(FaultCode code, string? message = null, Exception? exception = null)
    {
        if (code == FaultCode.None) throw new ArgumentOutOfRangeException(nameof(code));
        bool set;
        lock (Gate)
        {
            var fault = new StreamFault(code, _version, message, exception);
            if (_txOpen)
            {
                _txPending ??= fault;
                return;
            }

            set = TrySetFault(fault);
        }

        if (set) Notify();
    }

    // ───────────── its group ingest's update: the ingest holds the lock (Gates.TryEnter on Gate) throughout ─────────────

    internal void OpenStaged()
    {
        _txOpen = true;
        _stagedLength = -1;
        _txPending = null;
    }

    internal void CloseStaged()
    {
        _txOpen = false;
        _txPending = null;
        _stagedLength = -1;
    }

    internal void Stage(ReadOnlySpan<byte> value, Delivery delivery, bool compressible)
    {
        if (!_txOpen || !Gate.IsHeldByCurrentThread)
            throw new InvalidOperationException("The source is not part of the open transaction.");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, MaxValueBytes);
        if (value.Length > ushort.MaxValue && delivery == Delivery.Inline)
            throw new ArgumentOutOfRangeException(nameof(value), "Values over 64 KB can't be inline.");
        if (value.Length > _staged.Length) _staged = new byte[(int)BitOperations.RoundUpToPowerOf2((uint)value.Length)];
        value.CopyTo(_staged);
        _stagedLength = value.Length;
        _stagedDelivery = delivery;
        _stagedCompressible = compressible;
    }

    internal override int WriteBlob(ref Cursor cursor, Span<byte> dst)
    {
        if (!Gates.TryEnter(Gate)) return Busy;
        try
        {
            if (_version == 0)
            {
                cursor.Seq = 0;
                return 0;
            }

            if (_length > dst.Length) return -1;
            _buffer.AsSpan(0, _length).CopyTo(dst);
            cursor.Seq = _version;
            return _length;
        }
        finally
        {
            Gate.Exit();
        }
    }

    /// <summary>
    ///     The stale value: the policy's fixed one, the last again, or the last through its function (one entry: no key,
    ///     the value its row).
    /// </summary>
    internal override int WriteStale(uint tapId, ErrorPolicy policy, StreamFault? fault, Span<byte> dst, bool first)
    {
        byte[]? scratch = null;
        if (!Gates.TryEnter(Gate)) return Busy;
        try
        {
            if (!ReferenceEquals(Fault, fault)) return FaultCleared;
            try
            {
                ReadOnlySpan<byte> value;
                if (policy.Kind == StaleData.Static)
                {
                    value = policy.State.Span;
                }
                else if (_version == 0)
                {
                    return 0; // nothing published: nothing to replay
                }
                else if (policy.Kind == StaleData.ReplayLast)
                {
                    value = _buffer.AsSpan(0, _length);
                }
                else
                {
                    if (policy.Degrade is not { } degrade) return 0;
                    scratch = ArrayPool<byte>.Shared.Rent(4 + _length);
                    BinaryPrimitives.WriteUInt32LittleEndian(scratch, FoldEntry.Present);
                    _buffer.AsSpan(0, _length).CopyTo(scratch.AsSpan(4));
                    try
                    {
                        degrade.Degrade(scratch.AsSpan(0, 4 + _length), 1, 0, _length);
                    }
                    catch
                    {
                        return 0;
                    }

                    if ((BinaryPrimitives.ReadUInt32LittleEndian(scratch) & FoldEntry.Present) == 0) return 0;
                    value = scratch.AsSpan(4, _length);
                }

                if (value.Length > ushort.MaxValue) return 0; // too large for a Value record: no stale value
                var n = Wire.ValueHeader + value.Length;
                if (n > dst.Length) return -1;
                dst[0] = Wire.Value;
                BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], tapId);
                BinaryPrimitives.WriteUInt16LittleEndian(dst[5..], (ushort)value.Length);
                value.CopyTo(dst[Wire.ValueHeader..]);
                return n;
            }
            finally
            {
                if (scratch is not null) ArrayPool<byte>.Shared.Return(scratch);
            }
        }
        finally
        {
            Gate.Exit();
        }
    }

    internal override int Write(uint tapId, ref Cursor cursor, Span<byte> dst)
    {
        if (!Gates.TryEnter(Gate)) return Busy;
        try
        {
            if (_version == 0)
            {
                cursor.Seq = 0;
                return 0;
            }

            var n = Wire.ValueHeader + _length;
            if (n > dst.Length || _length > ushort.MaxValue) return -1;
            dst[0] = Wire.Value;
            BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], tapId);
            BinaryPrimitives.WriteUInt16LittleEndian(dst[5..], (ushort)_length);
            _buffer.AsSpan(0, _length).CopyTo(dst[Wire.ValueHeader..]);
            cursor.Seq = _version;
            return n;
        }
        finally
        {
            Gate.Exit();
        }
    }
}

/// <summary>A family of last-value streams discriminated by a conflation key (tickers, marks, positions...).</summary>
public sealed class LastValues<TKey>(string name) where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, LastValueSource> _sources = new();

    public string Name { get; } = name;

    public LastValueSource this[TKey key] =>
        _sources.GetOrAdd(key, static (k, name) => new LastValueSource { Label = $"{name}[{k}]" }, Name);

    public PublishResult Publish(TKey key, ReadOnlySpan<byte> value, Delivery delivery = Delivery.Auto,
        bool compressible = true)
    {
        return this[key].Publish(value, delivery, compressible);
    }
}

/// <summary>
///     The answer to a publish, as an HTTP publisher's (Nchan's): 201, subscribers got it; 202, accepted — none are
///     left.
/// </summary>
public enum PublishStatus
{
    /// <summary>201: routes are bound to the topic.</summary>
    Delivered = 201,

    /// <summary>
    ///     202: no route is bound: stop producing for the topic — if <see cref="PublishResult.Epoch" /> is the one you
    ///     last accepted a subscriber on.
    /// </summary>
    NoListeners = 202,

    /// <summary>
    ///     507: refused — a message topic under <see cref="OverflowPolicy.Refuse" /> would have evicted a message a
    ///     subscriber hasn't been sent.
    /// </summary>
    Full = 507
}

/// <summary>
///     A publish's answer and the topic epoch it is for (see <see cref="Source.Acknowledge()" />) — and <see cref="Seq" />
///     , what
///     it made: a value's new version, a message's number in its topic (0: not kept — nobody subscribed, or refused). The
///     router's, for this source as long as it lives here: to hand a requester's rendezvous (
///     <see cref="Tap.TryRendezvous(ReadOnlySpan{byte}, long)" />),
///     not an identity — correlate end to end in the payload. (A table's update returns its version: <c>Complete()</c>.)
/// </summary>
public readonly record struct PublishResult(PublishStatus Status, long Epoch, long Seq = 0)
{
    public bool HasListeners => Status == PublishStatus.Delivered;
}