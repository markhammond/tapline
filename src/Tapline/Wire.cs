using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tapline;

/// <summary>
///     Records inside a frame payload. A frame is a concatenation of records; integers are little-endian.
///     <code>
/// Heartbeat  u8 0x01 | u32 hbSeq | i64 serverUnixMicros                                        13
/// RouterChange u8 0x0D | u8 state | u8[16] correlation | u32 controls | tail                  22 + tail
///            the router's own state (<see cref="RouterState" />: 0 serving, 1 degraded, 2 draining) and the change into
///            it: its correlation id (a GUID, RFC 4122 byte order — a UUIDv7, as the router logged it); the control
///            messages it takes (<see cref="ControlKinds" />: a bit each, 1 &lt;&lt; kind); tail by state:
///            serving none, degraded u8 len | reason (UTF-8, at most 255 bytes), draining u32 graceMs (what's left of its
///            grace). It rides behind a heartbeat, exempt from credit as the heartbeat is, until a heartbeat it rode
///            with is echoed — and again on each new connection; a change sends a heartbeat at once. Nothing otherwise:
///            the client has the router's state
/// Value      u8 0x03 | u32 tapId | u16 len | payload                                             7 + len
/// Table      u8 0x04 | u32 tapId | u8 flags | i64 fromSeq | i64 toSeq | u32 checksum | u32 n
///            | u16 keySize | u16 rowSize | n × ( u8 op | key | row )                             34 + n·(1+K+R)
///            op 0 = upsert, 1 = delete (row bytes zero); flags bit0 = reset (snapshot), bit1 = degraded
/// Error      u8 0x05 | u32 tapId | u16 code | i64 lastGoodVersion | u16 len | payload           17 + len
///            terminal: the tap is closed; the client drops it and resubscribes
/// Group      u8 0x06 | u32 tapId | u8 flags | u16 count | u32 bodyLen | count member records        12 + len
///            a coherent cut of a watch: member records (Table/Value) carry the member index as their
///            tapId; the client applies all of them, then notifies once. flags as Table
/// OobRef     u8 0x07 | u32 tapId | u8 inner | u8 mode | u8[16] blobId | u8[16] tag
///            | u32 length | u32 ticket                                                            47
///            the record's bytes travel out of band: GET {blobBase}/{blobId hex}/{outletId}/{tag hex}.
///            Blob = the payload (inner Value) or the whole record with tapId 0 (Table/Group/TopicMessage).
///            mode bits 0–6: 0 = transparent: the client applies it in order and the tap waits for
///            Tap.Fetched(0, tapId, ticket) — the ticket names this handle, so a late confirmation of an older
///            one is ignored; 1 = reference: handed to the app, nothing waits. Bit 7 = content-addressed:
///            blobId = SHA-256(blob)[0..16], and the client verifies it; clear, blobId is a unique id
/// Service    u8 0x08 | u8 status | u16 len | payload                                              4 + len
///            a data provider's health, as the router tells it — each change with its correlation id (a GUID, RFC 4122
///            byte order: a UUIDv7), the one the router logged it with. A node is a provider instance, by the router's
///            transient id for it (TapBinding.NodeId: a GUID, as the correlation); nothing of the instance itself.
///            status 0 = down, payload u8[16] node | u8[16] correlation: that node is gone — every subscription through
///            it closed: the client fails each (FaultCode.ServiceDown) and waits for the service to be up again;
///            status 1 = up, payload u8[16] correlation | the service's name (UTF-8): a service the client was waiting
///            on is up again (enough instances, or a grace period passed) — subscribe again;
///            status 2 = drained, payload as for down: that node is draining and has let this client's subscriptions
///            through it go — not down: subscribe again now (another node takes it; the drain spreads its clients out
///            over time), and wait for up only if none does;
///            status 3 = draining, payload as for down | u32 graceMs: that node is draining — move, make before break:
///            subscribe again now (another node), and drop these once the new streams; they stay open graceMs, then
///            are closed (a "drained" record follows for any still held);
///            status 4 = degraded, payload as for down | scope (UTF-8): the node — or, with a scope, the service path
///            it names there, as the client sees it (TapBinding.Service, then TapBinding.Partition's ids: those at or
///            under it) — is late, or reported unhealthy: not gone. Each subscription it covers is shown its stale data
///            (the records that follow) and held;
///            status 5 = recovered, payload as for degraded: healthy again — the subscriptions it covers stream on (a
///            table from a snapshot, a value its current one, a message stream from where it was), unless another
///            degraded scope still covers them;
///            status 6 = move, payload as for down | u32 graceMs | u32 tapId: move that one subscription, make before
///            break — the node keeps serving (a rebalance, not a drain): subscribe again now (the router places it by
///            load), and drop the old one once the new streams; it stays open graceMs, then is closed
///            (FaultCode.Moved)
/// TopicMessage u8 0x09 | u32 tapId | u8 flags | i64 seq | u32 len | payload                       18 + len
///            a message of a message topic (seq: the topic's numbering), or of the subscriber's mailbox (flags bit1;
///            seq: the mailbox's); flags bit0 = gap: messages before it were skipped. Delivered in order, never conflated
/// Ended      u8 0x0A | u16 code | u32 fromTapId | u32 toTapId                                     11
///            the router ended this client's subscriptions with tap ids from fromTapId to toTapId (a FaultCode): the
///            client fails each it still holds (an error from the server, not local). All up to an id — Stalled: it
///            acknowledged nothing for OutletOptions.StallTimeout while there was something for it (those bound since
///            stand); or one — its activation, re-sent after a reconnect, names a subscription the router no longer
///            holds: why it ended, if the router still remembers (else Gone). Kept, like a Service record, until a
///            frame carrying it is acknowledged; sent again after a reconnect
/// Rendezvous u8 0x0B | u32 tapId | u8 flags | i64 seq | u8 len | correlation                     15 + len
///            through the subscription's mailbox, strictly after the state it names: the subscriber's state is at
///            seq or past it (a table's or a value's version, a topic's message) — applied, an out-of-band record
///            fetched. The correlation is the requester's (at most 64 bytes). flags bit0 = missed: a message topic's
///            message seq wasn't delivered to this subscriber — published before it subscribed, or skipped
///            (OverflowPolicy.Skip) — or the subscriber holds no such state (no tap on that source, or it ended)
/// Refused    u8 0x0C | u8 kind | u8[16] correlation | u16 len | service | u16 len | reason    22 + lens
///            a provider — or the router, for a service that requires a sign-in — refused a subscription of this
///            client's, saying why (UTF-8; at most 256 and 1,024 bytes) and of what kind (RefusalKind: 0 refused,
///            1 unauthenticated — sign in, then subscribe again — 2 forbidden): the
///            correlation id (a GUID, RFC 4122 byte order) is the one the provider and the router logged it with — for
///            the client's log, a needle to find all three. Kept, like a Service record, until a frame carrying it is
///            acknowledged; sent again after a reconnect (the same id: logged once)
/// Auth       u8 0x0E | u8 status | u32 exchange | u16 round | u16 len | text | u32 len | payload   14 + lens
///            the router's answer to a sign-in's message (AuthMessages; exchange: the client's number for it; round:
///            the message's, 0 the Begin's, 0xFFFF a refusal before it was read): status 0 continue (payload: the
///            authentication provider's next message), 1 signed in (text: the identity as JSON — realm, userId,
///            sessionId, module, insecure, authenticatedAt, expiresAt, claims; payload: a ticket), 2 refused (text:
///            why). Kept, like a Service record, until a frame carrying it is acknowledged: a client takes each once
/// </code>
/// </summary>
public static class Wire
{
    public const byte Heartbeat = 0x01,
        Value = 0x03,
        Table = 0x04,
        Error = 0x05,
        Group = 0x06,
        OobRef = 0x07,
        Service = 0x08,
        TopicMessage = 0x09,
        Ended = 0x0A,
        Rendezvous = 0x0B,
        Refused = 0x0C,
        RouterChange = 0x0D,
        Auth = 0x0E,
        Session = 0x0F;

    /// <summary>
    ///     A rendezvous's flags: the message it names wasn't delivered to this subscriber (see <see cref="Rendezvous" />
    ///     ).
    /// </summary>
    public const byte FlagMissed = 0x01;

    public const int RendezvousHeader = 15, MaxCorrelation = 64;

    /// <summary>
    ///     A topic message's flags: messages before it were skipped (lost: <see cref="OverflowPolicy.Skip" />); it came
    ///     through
    ///     the subscriber's mailbox; it is stale data (its provider degraded or gone: <see cref="ErrorPolicy" />), not a new
    ///     message.
    /// </summary>
    public const byte FlagGap = 0x01, FlagMailbox = 0x02, FlagStale = 0x04;

    public const int TopicMessageHeader = 18;

    public const byte ServiceDown = 0,
        ServiceUp = 1,
        ServiceDrained = 2,
        ServiceDraining = 3,
        ServiceDegraded = 4,
        ServiceRecovered = 5,
        ServiceMove = 6;

    public const byte FlagReset = 0x01, FlagDegraded = 0x02, OpUpsert = 0, OpDelete = 1;
    public const byte OobTransparent = 0, OobReference = 1, OobContentAddressed = 0x80, OobModeMask = 0x7F;

    public const int HeartbeatSize = 13,
        RouterChangeHeader = 22,
        RouterChangeControls = 18,
        ValueHeader = 7,
        TableHeader = 34,
        ErrorHeader = 17,
        GroupHeader = 12,
        OobRefSize = 47,
        ServiceHeader = 4,
        EndedSize = 11;

    /// <summary>
    ///     The longest a router's degraded reason is on the wire (UTF-8 bytes) — and so the most a heartbeat takes, with the
    ///     router's change riding behind it.
    /// </summary>
    public const int MaxRouterReason = 255, MaxHeartbeatSize = HeartbeatSize + RouterChangeHeader + 1 + MaxRouterReason;

    internal static void WriteRendezvous(Span<byte> dst, uint tapId, byte flags, long seq,
        ReadOnlySpan<byte> correlation)
    {
        dst[0] = Rendezvous;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], tapId);
        dst[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(dst[6..], seq);
        dst[14] = (byte)correlation.Length;
        correlation.CopyTo(dst[RendezvousHeader..]);
    }

    internal static void WriteTopicMessage(Span<byte> dst, uint tapId, byte flags, long seq, ReadOnlySpan<byte> message)
    {
        dst[0] = TopicMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], tapId);
        dst[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(dst[6..], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[14..], (uint)message.Length);
        message.CopyTo(dst[TopicMessageHeader..]);
    }

    /// <summary>A RouterChange record's length, read from its state — or −1 if <paramref name="record" /> is too short to say.</summary>
    public static int RouterChangeLength(ReadOnlySpan<byte> record)
    {
        if (record.Length < RouterChangeHeader) return -1;
        var length = record[1] switch
        {
            (byte)RouterState.Draining => RouterChangeHeader + 4,
            (byte)RouterState.Degraded when record.Length > RouterChangeHeader => RouterChangeHeader + 1 +
                record[RouterChangeHeader],
            (byte)RouterState.Degraded => -1,
            _ => RouterChangeHeader
        };
        return length <= record.Length ? length : -1;
    }

    internal static void WriteOobRef(Span<byte> dst, uint tapId, byte inner, byte mode, BlobId id,
        ReadOnlySpan<byte> tag, int length, uint ticket)
    {
        dst[0] = OobRef;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], tapId);
        dst[5] = inner;
        dst[6] = mode;
        id.WriteTo(dst.Slice(7, 16));
        tag.CopyTo(dst.Slice(23, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(dst[39..], (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[43..], ticket);
    }
}

/// <summary>
///     Order-independent multiset hash of a table's live rows: checksum = Σ hash(key bytes, row bytes) mod 2³².
///     O(1) to maintain on both ends; 32-bit operations only, so a JS client matches it with Math.imul.
/// </summary>
public static class RowHash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Mix(uint h)
    {
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return h;
    }

    public static uint Of(ReadOnlySpan<byte> key, ReadOnlySpan<byte> row)
    {
        var h = Mix(0x9E3779B9u + (uint)(key.Length + row.Length));
        h = Absorb(h, key);
        return Absorb(h, row);
    }

    /// <summary>Folds bytes in as little-endian 32-bit words; a trailing partial word is zero-padded.</summary>
    private static uint Absorb(uint h, ReadOnlySpan<byte> b)
    {
        var i = 0;
        for (; i + 4 <= b.Length; i += 4) h = Mix(h ^ BinaryPrimitives.ReadUInt32LittleEndian(b[i..]));
        if (i < b.Length)
        {
            uint w = 0;
            for (var k = 0; i + k < b.Length; k++) w |= (uint)b[i + k] << (8 * k);
            h = Mix(h ^ w);
        }

        return h;
    }
}

/// <summary>
///     A <see cref="Wire.Refused" /> record: a refusal's kind, correlation id, service and reason, each kept within its
///     bound.
/// </summary>
internal static class RefusedRecord
{
    public const int MaxService = 256, MaxReason = 1024;

    public static byte[] Of(SubscriptionRefusal refusal)
    {
        var service = Utf8(refusal.Service, MaxService);
        var reason = Utf8(refusal.Reason, MaxReason);
        var record = new byte[22 + service.Length + reason.Length];
        record[0] = Wire.Refused;
        record[1] = (byte)refusal.Kind;
        refusal.CorrelationId.TryWriteBytes(record.AsSpan(2, 16), true, out _);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), (ushort)service.Length);
        service.CopyTo(record, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20 + service.Length), (ushort)reason.Length);
        reason.CopyTo(record, 22 + service.Length);
        return record;
    }

    /// <summary>Its UTF-8, at most <paramref name="max" /> bytes — cut at a character, never within one.</summary>
    internal static byte[] Utf8(string text, int max)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= max) return bytes;
        var end = max;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80) end--; // (a continuation byte: back to the character's start)
        return bytes[..end];
    }
}

/// <summary>
///     A service notice for one client, with its change's correlation id: a node down, draining (and the grace), drained,
///     degraded or recovered (a scope: a service path there, or the whole node), a service up (its name), or one of the
///     client's subscriptions to move (its tap, in <see cref="From" />, and the grace).
/// </summary>
internal readonly record struct ServiceNotice(
    byte Status,
    Guid NodeId,
    string? Service,
    uint GraceMs = 0,
    ushort Code = 0,
    uint From = 0,
    Guid Correlation = default,
    string? Scope = null)
{
    /// <summary>
    ///     Not a Service record: an <see cref="Wire.Ended" /> one — <see cref="Code" />, the tap ids from
    ///     <see cref="From" /> to the last (in <see cref="GraceMs" />'s place).
    /// </summary>
    public const byte EndedStatus = 0xFF;

    /// <summary>
    ///     Not a Service record: records written ahead of time (<see cref="Records" />) — a tap's degraded state and
    ///     error, sent as a notice is (in order, until acknowledged).
    /// </summary>
    public const byte RecordsStatus = 0xFE;

    public byte[]? Records { get; init; }

    private int PayloadSize => Status switch
    {
        Wire.ServiceUp => 16 + Encoding.UTF8.GetByteCount(Service!),
        Wire.ServiceDraining => 36,
        Wire.ServiceMove => 40,
        _ => 32 + (Scope is null ? 0 : Encoding.UTF8.GetByteCount(Scope))
    };

    public int Size => Status == EndedStatus ? Wire.EndedSize :
        Status == RecordsStatus ? Records!.Length : Wire.ServiceHeader + PayloadSize;

    public static ServiceNotice OfRecords(byte[] records)
    {
        return new ServiceNotice(RecordsStatus, default, null) { Records = records };
    }

    public static ServiceNotice Ended(FaultCode code, uint fromTapId, uint toTapId)
    {
        return new ServiceNotice(EndedStatus, default, null, toTapId, (ushort)code, fromTapId);
    }

    public int Write(Span<byte> dst)
    {
        if (Status == RecordsStatus)
        {
            Records.CopyTo(dst);
            return Records!.Length;
        }

        if (Status == EndedStatus)
        {
            dst[0] = Wire.Ended;
            BinaryPrimitives.WriteUInt16LittleEndian(dst[1..], Code);
            BinaryPrimitives.WriteUInt32LittleEndian(dst[3..], From);
            BinaryPrimitives.WriteUInt32LittleEndian(dst[7..], GraceMs);
            return Wire.EndedSize;
        }

        dst[0] = Wire.Service;
        dst[1] = Status;
        var payload = dst[Wire.ServiceHeader..];
        int len;
        if (Status == Wire.ServiceUp)
        {
            Correlation.TryWriteBytes(payload[..16], true, out _);
            len = 16 + Encoding.UTF8.GetBytes(Service!, payload[16..]);
        }
        else
        {
            NodeId.TryWriteBytes(payload[..16], true, out _);
            Correlation.TryWriteBytes(payload.Slice(16, 16), true, out _);
            if (Status == Wire.ServiceDraining)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload[32..], GraceMs);
                len = 36;
            }
            else if (Status == Wire.ServiceMove)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload[32..], GraceMs);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[36..], From); // (the tap to move)
                len = 40;
            }
            else
            {
                len = 32 + (Scope is null ? 0 : Encoding.UTF8.GetBytes(Scope, payload[32..]));
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dst[2..], (ushort)len);
        return Wire.ServiceHeader + len;
    }
}