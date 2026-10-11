using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Tapline;

// Observability: what an outlet sent, kept for a viewer (production support), at the cost of a few stores per record.
//
// Threading: each log has one writer — an outlet's frame builder (one build at a time per outlet), or, for a frame's
// ack time, its connection's receive loop — and is read by anyone, without locks: a slot carries a sequence number, odd
// while it is written (a seqlock); a reader copies the slot and keeps the copy only if the number was even and unchanged
// across the copy. A torn slot is skipped, not waited for: what a viewer shows is best effort, never a stall for the
// router. Timestamps are Stopwatch ticks, read once a frame; converted to wall-clock time only when read.

/// <summary>A record an outlet sent for a subscription (a tap), as kept for the viewer.</summary>
/// <param name="N">The tap's record number (from 1).</param>
/// <param name="Frame">The frame that carried it (its sequence number in its client's frames).</param>
/// <param name="Kind">Its record type: value, table, group, error, out-of-band handle.</param>
/// <param name="Bytes">Its size in the frame.</param>
/// <param name="Version">The topic's version it brought the subscriber to.</param>
/// <param name="ReceivedAt">When the topic last changed before it was sent — the data's arrival from the provider.</param>
/// <param name="SentAt">
///     When its frame was built — at the head of the connection's send queue, written to the socket's
///     buffer.
/// </param>
/// <param name="LagMs">Sent less received: how long the change waited at the router (conflated: the newest change's wait).</param>
/// <param name="AckedAt">When the client acknowledged its frame, if it has (and the frame is still in its client's log).</param>
/// <param name="AckedAfterMs">Acked less sent: the round trip it took, client processing included.</param>
/// <param name="Preview">Its first bytes, hex (none, its retention <see cref="RecordRetention.None" />).</param>
/// <param name="Inner">For an out-of-band handle: the kind of record its blob holds.</param>
/// <param name="Blob">
///     For an out-of-band handle: its blob's id — the content, in the blob store while it is kept (
///     <see cref="Outlet.TryLeaseBlob" />).
/// </param>
/// <param name="Whole">
///     Kept whole (<see cref="RecordRetention.Whole" />), still: its bytes, from
///     <see cref="Outlet.ReadRecord" />.
/// </param>
public sealed record MessageView(
    long N,
    long Frame,
    string Kind,
    int Bytes,
    long Version,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset SentAt,
    double? LagMs,
    DateTimeOffset? AckedAt,
    double? AckedAfterMs,
    string Preview,
    string? Inner = null,
    string? Blob = null,
    bool Whole = false,
    long? Upstream = null,
    long? UpstreamSince = null);

/// <summary>What became of a blob looked up for the viewer (<see cref="Outlet.TryLeaseBlob" />).</summary>
public enum BlobLookup
{
    /// <summary>Kept: leased — its bytes held until the lease is disposed.</summary>
    Leased,

    /// <summary>No longer kept: released, or evicted, since it was sent (or never was).</summary>
    Gone,

    /// <summary>The store was busy (another thread held it): not waited for — try again.</summary>
    Busy
}

/// <summary>A frame an outlet sent: its sequence number, when built, its size and records, and when acknowledged.</summary>
public sealed record FrameView(
    long Seq,
    DateTimeOffset SentAt,
    int Bytes,
    int Records,
    DateTimeOffset? AckedAt,
    double? AckedAfterMs);

/// <summary>
///     A client's connection, as its transport describes it: which transport (its name, e.g. "Blazor circuit"), where from
///     (as the server sees it: a proxy's address, behind one), with what, since when — and when it dropped, if it has (the
///     session may outlive it, awaiting a reconnect) — and how many connections the session has had.
/// </summary>
public sealed record OutletSession(
    string Transport,
    string? Remote,
    string? Agent,
    DateTimeOffset ConnectedAt,
    int Connections,
    DateTimeOffset? DisconnectedAt = null);

/// <summary>A client (its outlet), as the viewer lists it.</summary>
public sealed record OutletView(
    int Id,
    long Serial,
    DateTimeOffset CreatedAt,
    OutletSession? Session,
    int Taps,
    long FramesSent,
    long FramesAcked,
    double RttMs,
    double MinRttMs,
    double SmoothedRttMs,
    long DeliveryBytesPerSecond,
    long InflightBytes,
    long StreamingBytesPerSecond,
    int MaxFramesPerSecond,
    long MemoryBytes = 0,
    long MemoryBudget = 0,
    double? WaitingMs = null,
    double StallTimeoutMs = 0,
    int Stalls = 0,
    long HistoryBytes = 0,
    long HistoryBudget = 0,
    long StateHolds = 0,
    bool HoldingState = false,
    ClientIdentity? Identity = null,
    SessionPolicy? SessionPolicy = null);

/// <summary>
///     A subscription (a tap) of a client: what it is, where it's at, and its last records — and, through a data provider,
///     its
///     service, node and route; in a partition, its service path (names: the router's alone) and the ids its client sees.
/// </summary>
public sealed record TapView(
    uint Id,
    string? Topic,
    long TopicId,
    string? Source,
    string State,
    string Class,
    string Initial,
    long Version,
    long TopicVersion,
    DateTimeOffset BoundAt,
    string? Service,
    ProviderInstance? Provider,
    Guid? NodeId,
    RouteId? Route,
    long? Epoch,
    bool AwaitingFetch,
    IReadOnlyList<MessageView> Messages,
    long Bytes = 0,
    string? ServicePath = null,
    string? PartitionId = null,
    bool Stale = false);

/// <summary>
///     A topic (a source), as the viewer shows it: its label and record kind, version, epoch (and whether the last publish
///     found nobody: closed), routes bound to it, when it last changed, and its fault if any. Read without its lock.
/// </summary>
public sealed record TopicView(
    long Id,
    string? Label,
    string Kind,
    long Version,
    long Epoch,
    bool EpochClosed,
    int Routes,
    DateTimeOffset? ChangedAt,
    string? Fault,
    long? Upstream = null);

/// <summary>
///     A provider instance in detail: as tracked (or as it was when it died, if recently), the services registered at its
///     endpoint, the endpoint's last health report, and when it was deemed dead.
/// </summary>
public sealed record InstanceDetail(
    InstanceView Instance,
    IReadOnlyList<string> Services,
    EndpointView? Endpoint,
    DateTimeOffset? DiedAt);

/// <summary>The router, as the viewer shows it (<see cref="Publisher.Describe" />).</summary>
public sealed record RouterView(
    RouterInstance Router,
    IReadOnlyList<ServiceView> Services,
    IReadOnlyList<EndpointView> Endpoints,
    IReadOnlyList<InstanceView> Instances,
    long InstancesDown,
    long RoutesDrained,
    long ClientsMoved,
    IReadOnlyList<RealmView>? Realms = null,
    long RoutesMoved = 0,
    long RoutesLetGo = 0);

/// <summary>
///     A realm users sign in to through this router: its authentication service's endpoints (its instances), and whether
///     it's passthrough (any user id taken as given; its identities insecure).
/// </summary>
public sealed record RealmView(string Realm, IReadOnlyList<string> Endpoints, bool Passthrough);

/// <summary>A service: its endpoints, the clients waiting for it to be up, and its up policy.</summary>
public sealed record ServiceView(
    string Name,
    IReadOnlyList<string> Endpoints,
    int Waiting,
    int Quorum,
    double GraceMs,
    double SpreadMs,
    string? Access = null);

/// <summary>
///     A provider endpoint: its readiness as last reported, whether it refuses this router, who answered there last,
///     and when; and how loaded it said it was (null: not said).
/// </summary>
public sealed record EndpointView(
    string Endpoint,
    string Status,
    bool Refused,
    ProviderInstance? Instance,
    DateTimeOffset? HeardAt,
    double? Load = null);

/// <summary>
///     A provider instance the router tracks: who, its node id (the router's transient id for it: the one clients see),
///     its
///     routes here, and its health — the service paths it has reported degraded among it.
/// </summary>
public sealed record InstanceView(
    ProviderInstance Instance,
    Guid NodeId,
    int Routes,
    bool Draining,
    bool Dead,
    DateTimeOffset HeardAt,
    bool Degraded = false,
    IReadOnlyList<string>? DegradedPaths = null);

/// <summary>Converts the logs' Stopwatch ticks to wall-clock time (from one pairing of the two, made once).</summary>
internal static class Clock
{
    private static readonly long Ticks = Stopwatch.GetTimestamp();
    private static readonly DateTimeOffset Wall = DateTimeOffset.UtcNow;

    public static DateTimeOffset At(long ticks)
    {
        return Wall + Stopwatch.GetElapsedTime(Ticks, ticks);
    }

    public static double Ms(long from, long to)
    {
        return Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
    }
}

/// <summary>
///     How much of a subscription's records the router keeps for its viewer — each record as it was sent: conflated, the
///     subscriber's own. Ordered: a provider's ceiling (<see cref="PayloadFormat.Retention" />) lowers what's chosen.
/// </summary>
public enum RecordRetention
{
    /// <summary>
    ///     Its metadata alone — when, how big, what version — no payload bytes (an out-of-band record's handle kept: it
    ///     is metadata).
    /// </summary>
    None,

    /// <summary>Its first 32 bytes too.</summary>
    Prefix,

    /// <summary>
    ///     All of it, while the subscription's ring holds it (<see cref="OutletOptions.HistoryBytes" />); its first 32
    ///     bytes after.
    /// </summary>
    Whole
}

/// <summary>
///     A tap's last records: a ring written by its outlet's builder, read lock-free (see the notes above) — and, kept
///     whole,
///     their bytes in a ring of their own: a record's at its absolute position, intact while the writer hasn't reserved
///     past
///     it by more than the ring's size (a reader checks after it copies; a writer reserves before it writes).
/// </summary>
internal sealed class MessageLog
{
    public const int PreviewBytes = 32;

    /// <summary>The builder's, only.</summary>
    /// <summary>An upstream version not given (its publish gave none, or it isn't a state's record).</summary>
    public const long None = long.MinValue;

    private readonly byte[]? _bytes; // the records kept whole, a ring
    private readonly int _mask;

    private readonly Slot[] _slots;
    private long _count;
    private long _reserved; // the ring of bytes written (or being written) up to here

    /// <summary>
    ///     A ring of <paramref name="depth" /> records at least — a power of two (an index is a mask) — and
    ///     <paramref name="recordBytes" /> of their bytes (0: none whole).
    /// </summary>
    public MessageLog(int depth, int recordBytes = 0)
    {
        _slots = new Slot[(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, depth))];
        _mask = _slots.Length - 1;
        _bytes = recordBytes > 0 ? new byte[recordBytes] : null;
    }

    /// <summary>Whether it keeps records whole (it has a ring of bytes).</summary>
    public bool KeepsWhole => _bytes is not null;

    /// <summary>
    ///     The memory a ring of <paramref name="depth" /> records takes, with <paramref name="recordBytes" /> of them
    ///     kept whole (what a subscription is charged for it).
    /// </summary>
    public static long BytesFor(int depth, int recordBytes = 0)
    {
        return depth <= 0
            ? 0
            : 64 + BitOperations.RoundUpToPowerOf2((uint)depth) * Unsafe.SizeOf<Slot>() +
              Math.Max(0, recordBytes);
    }

    /// <summary>
    ///     The builder's, only. <paramref name="upstream" />: the provider's version this record is as of — and
    ///     <paramref name="upstreamSince" />, the one its changes are since (<see cref="None" />: not given, or a whole
    ///     state).
    /// </summary>
    public void Write(long frame, int bytes, long version, long receivedAt, long sentAt, ReadOnlySpan<byte> record,
        RecordRetention retention,
        long upstream = None, long upstreamSince = None)
    {
        var n = _count + 1;
        ref var s = ref _slots[(int)_count & _mask];
        var kind = record.IsEmpty ? (byte)0 : record[0];
        var whole = retention == RecordRetention.Whole && _bytes is { } ring && record.Length <= ring.Length;
        var at = _reserved;
        var seq = s.Seq + 1;
        Volatile.Write(ref s.Seq, seq); // odd: being written…
        if (whole) Volatile.Write(ref _reserved, at + record.Length); // …and the bytes it will overwrite, reserved…
        Interlocked.MemoryBarrier(); // …before any field or byte (weakly ordered CPUs)
        s.N = n;
        s.Frame = frame;
        s.Version = version;
        s.ReceivedAt = receivedAt;
        s.SentAt = sentAt;
        s.Upstream = upstream;
        s.UpstreamSince = upstreamSince;
        s.Bytes = bytes;
        s.Kind = kind;
        s.Whole = whole;
        s.At = at;
        if (whole)
        {
            int start = (int)(at % _bytes!.Length), first = Math.Min(record.Length, _bytes.Length - start);
            record[..first].CopyTo(_bytes.AsSpan(start));
            record[first..].CopyTo(_bytes); // (wrapped)
        }

        var p = retention == RecordRetention.None && kind != Wire.OobRef ? 0 : Math.Min(record.Length, PreviewBytes);
        record[..p].CopyTo(s.Preview);
        s.PreviewLength = (byte)p;
        Volatile.Write(ref s.Seq, seq + 1); // even: done (a release: the fields before it)
        Volatile.Write(ref _count, n);
    }

    /// <summary>Any thread: the records kept, newest first; one being overwritten meanwhile is left out.</summary>
    public List<MessageView> Read(FrameLog frames)
    {
        var views = new List<MessageView>(_slots.Length);
        var count = Volatile.Read(ref _count);
        for (var i = count - 1; i >= 0 && i >= count - _slots.Length; i--)
        {
            ref var s = ref _slots[(int)i & _mask];
            var before = Volatile.Read(ref s.Seq);
            var copy = s;
            Interlocked.MemoryBarrier();
            if ((before & 1) != 0 || Volatile.Read(ref s.Seq) != before || copy.N != i + 1)
                continue; // torn, or overwritten
            var acked = frames.AckedAt(copy.Frame);
            var preview = ((ReadOnlySpan<byte>)copy.Preview)[..copy.PreviewLength];
            var handle =
                copy.Kind == Wire.OobRef && preview.Length >= 23; // its inner kind and blob id are in its first bytes
            views.Add(new MessageView(copy.N, copy.Frame, KindName(copy.Kind), copy.Bytes, copy.Version,
                copy.ReceivedAt == 0 ? null : Clock.At(copy.ReceivedAt), Clock.At(copy.SentAt),
                copy.ReceivedAt == 0 ? null : Clock.Ms(copy.ReceivedAt, copy.SentAt),
                acked is { } a ? Clock.At(a) : null, acked is { } b ? Clock.Ms(copy.SentAt, b) : null,
                Convert.ToHexString(preview), handle ? KindName(preview[5]) : null,
                handle ? BlobId.Read(preview.Slice(7, 16)).ToString() : null,
                copy.Whole && Intact(copy.At, copy.Bytes, Volatile.Read(ref _reserved)),
                copy.Upstream == None ? null : copy.Upstream, copy.UpstreamSince == None ? null : copy.UpstreamSince));
        }

        return views;
    }

    /// <summary>
    ///     Any thread: record <paramref name="n" />'s bytes, if it is kept whole still; null if not (or it was
    ///     overwritten as it was read).
    /// </summary>
    public byte[]? ReadWhole(long n)
    {
        if (_bytes is not { } ring || n < 1) return null;
        ref var s = ref _slots[(int)(n - 1) & _mask];
        var before = Volatile.Read(ref s.Seq);
        var copy = s;
        Interlocked.MemoryBarrier();
        if ((before & 1) != 0 || Volatile.Read(ref s.Seq) != before || copy.N != n || !copy.Whole) return null;
        var bytes = new byte[copy.Bytes];
        int start = (int)(copy.At % ring.Length), first = Math.Min(bytes.Length, ring.Length - start);
        ring.AsSpan(start, first).CopyTo(bytes);
        ring.AsSpan(0, bytes.Length - first).CopyTo(bytes.AsSpan(first));
        Interlocked.MemoryBarrier(); // the copy, before checking nothing overwrote it meanwhile
        return Intact(copy.At, copy.Bytes, Volatile.Read(ref _reserved)) ? bytes : null;
    }

    /// <summary>Bytes at [at, at + length) are intact if nothing reserved since reaches a ring's length past their start.</summary>
    private bool Intact(long at, int length, long reserved)
    {
        return _bytes is { } ring && reserved - at <= ring.Length && length <= ring.Length;
    }

    internal static string KindName(byte kind)
    {
        return kind switch
        {
            Wire.Value => "value", Wire.Table => "table", Wire.Group => "group", Wire.Error => "error",
            Wire.OobRef => "out-of-band",
            Wire.TopicMessage => "message", Wire.Heartbeat => "heartbeat", Wire.Service => "service",
            _ => $"0x{kind:X2}"
        };
    }

    [InlineArray(PreviewBytes)]
    private struct Preview
    {
        private byte _first;
    }

    private struct Slot
    {
        public int Seq; // odd while written
        public long N, Frame, Version, ReceivedAt, SentAt;
        public long Upstream, UpstreamSince; // the provider's numbering (None: not given)
        public long At; // kept whole: its bytes' absolute position in the ring of bytes
        public int Bytes;
        public byte Kind, PreviewLength;
        public bool Whole;
        public Preview Preview;
    }
}

/// <summary>
///     An outlet's last frames: built (by its builder) and acknowledged (by its connection's receive loop) — two writers,
///     each of its own fields; read lock-free.
/// </summary>
internal sealed class FrameLog
{
    private const int Depth = 64, Mask = Depth - 1; // a power of two: an index is a mask

    private readonly Slot[] _slots = new Slot[Depth];
    private long _acked; // the receive loop's: frames up to here are stamped

    /// <summary>The builder's, only.</summary>
    public void Built(long seq, long sentAt, int bytes, int records)
    {
        ref var s = ref _slots[(int)seq & Mask];
        var v = s.Version + 1;
        Volatile.Write(ref s.Version, v);
        Interlocked.MemoryBarrier();
        Volatile.Write(ref s.AckedAt, 0);
        s.Seq = seq;
        s.SentAt = sentAt;
        s.Bytes = bytes;
        s.Records = records;
        Volatile.Write(ref s.Version, v + 1);
    }

    /// <summary>The receive loop's, only: frames up to <paramref name="seq" /> acknowledged now (a cumulative ack).</summary>
    public void Acked(long seq, long now)
    {
        var from = Math.Max(_acked + 1, seq - Depth + 1);
        for (var f = from; f <= seq; f++)
        {
            ref var s = ref _slots[(int)f & Mask];
            if (Volatile.Read(ref s.Seq) == f && Volatile.Read(ref s.AckedAt) == 0) Volatile.Write(ref s.AckedAt, now);
        }

        if (seq > _acked) _acked = seq;
    }

    /// <summary>A new connection: frames sent on the old one and never acked won't be (left unacked).</summary>
    public void Reset(long sent)
    {
        _acked = Math.Max(_acked, sent);
    }

    /// <summary>Any thread: when frame <paramref name="seq" /> was acked, if it was and is still kept.</summary>
    public long? AckedAt(long seq)
    {
        ref var s = ref _slots[(int)seq & Mask];
        var acked = Volatile.Read(ref s.AckedAt);
        return Volatile.Read(ref s.Seq) == seq && acked != 0 ? acked : null;
    }

    /// <summary>Any thread: the frames kept, newest first; one being overwritten meanwhile is left out.</summary>
    public List<FrameView> Read(long newest)
    {
        var views = new List<FrameView>(Depth);
        for (var f = newest; f > 0 && f > newest - Depth; f--)
        {
            ref var s = ref _slots[(int)f & Mask];
            var before = Volatile.Read(ref s.Version);
            var copy = s;
            Interlocked.MemoryBarrier();
            if ((before & 1) != 0 || Volatile.Read(ref s.Version) != before || copy.Seq != f) continue;
            var acked = Volatile.Read(ref s.AckedAt);
            views.Add(new FrameView(copy.Seq, Clock.At(copy.SentAt), copy.Bytes, copy.Records,
                acked == 0 ? null : Clock.At(acked), acked == 0 ? null : Clock.Ms(copy.SentAt, acked)));
        }

        return views;
    }

    private struct Slot
    {
        public int Version; // odd while the builder writes
        public long Seq, SentAt;
        public int Bytes, Records;
        public long AckedAt; // the receive loop's: 0 until acked
    }
}