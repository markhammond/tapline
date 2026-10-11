using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace Tapline;

/// <summary>
///     What a message topic does when a subscriber falls behind its buffer — when a message it hasn't been sent is
///     evicted.
/// </summary>
public enum OverflowPolicy : byte
{
    /// <summary>
    ///     End the subscription with an error (<see cref="FaultCode.Overflowed" />): it never silently misses a message — its
    ///     client resubscribes (and recovers what it needs from a state topic). The default.
    /// </summary>
    Fault = 0,

    /// <summary>
    ///     Skip to the oldest message still buffered, flagging the gap (<see cref="Wire.FlagGap" />): the client decides
    ///     whether it matters.
    /// </summary>
    Skip = 1,

    /// <summary>
    ///     Refuse the publish (<see cref="PublishStatus.Full" />) rather than evict a message a subscriber hasn't been sent:
    ///     back-pressure on the provider — from subscribers whose clients are connected. One whose client is away (its
    ///     connection gone, or not yet resumed) doesn't hold the topic back: it falls behind, and overflows as under
    ///     <see cref="Fault" />.
    /// </summary>
    Refuse = 2
}

/// <summary>
///     An ordering constraint on a message: its subscriber gets it only once it has <see cref="Source" />'s state at
///     <see cref="Version" /> or later — a fill after the position it changed, a reply after the order row it refers to.
///     If
///     the subscriber's tap on that source is behind when the message's turn comes, the tap's record is pulled forward
///     into
///     the same frame, ahead of the message. On a topic's message the constraint names a source, and holds for each
///     subscriber that has a tap on it (one that hasn't, isn't held); on a mailbox's, a source or one tap of the same
///     client.
///     A tap that closes, or can't be sent (oversize), no longer holds the message: it goes.
/// </summary>
public readonly record struct After
{
    private After(object target, long version)
    {
        (Target, Version) = (target, version);
    }

    /// <summary>After <paramref name="source" />'s state at <paramref name="version" /> — each subscriber's tap on it.</summary>
    public After(Source source, long version) : this((object)source, version)
    {
    }

    /// <summary>
    ///     After <paramref name="tap" />'s source at <paramref name="version" />, as that tap has it (a mailbox post, the
    ///     same client's tap).
    /// </summary>
    public After(Tap tap, long version) : this((object)tap, version)
    {
    }

    /// <summary>A <see cref="Tapline.Source" /> or a <see cref="Tapline.Tap" />; null: no constraint.</summary>
    internal object? Target { get; }

    public long Version { get; }
    public Source? Source => Target as Source;
    public Tap? Tap => Target as Tap;

    /// <summary>After the source as it is now (typically right after the publish the message is about).</summary>
    public static After Current(Source source)
    {
        return new After(source, source.Version);
    }

    /// <summary>After the tap's source as it is now.</summary>
    public static After Current(Tap tap)
    {
        return new After(tap, tap.Source.Version);
    }
}

/// <summary>
///     A bounded run of messages, numbered on from a given one: an arena of bytes, written circularly, and an index of
///     where
///     each message lies (both sized once and rented from the shared pools — <see cref="Release" /> returns them; a power
///     of
///     two of messages), and, once a constrained message is appended, each one's ordering constraint. Appending evicts the
///     oldest as needed. Not thread-safe: its owner locks.
/// </summary>
internal sealed class MessageRing
{
    private readonly byte[] _arena;
    private readonly long[] _index; // offset << 32 | length
    private readonly int _mask;
    private object?[]? _after; // per message: its constraint's target (made at the first)
    private long[]? _afterVersion;
    private int _head; // the arena's next free byte
    private byte[]? _kinds; // per message: what it is, if not a message (made at the first)

    public MessageRing(int capacity, int capacityBytes, long numberedAfter = 0)
    {
        var slots = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, capacity));
        _index = ArrayPool<long>.Shared.Rent(slots);
        _mask = slots - 1;
        MaxMessage = Math.Max(1, capacityBytes);
        _arena = ArrayPool<byte>.Shared.Rent(MaxMessage);
        First = numberedAfter + 1;
        Last = numberedAfter;
    }

    public long First { get; private set; }

    public long Last { get; private set; }

    public int MaxMessage { get; }

    private int Slots => _mask + 1;

    /// <summary>
    ///     The newest message appending <paramref name="length" /> bytes would evict (0: none) — every one from the oldest up
    ///     to
    ///     it goes: one large message can take several.
    /// </summary>
    public long WouldEvict(int length)
    {
        Place(length, out var at, out var wasted);
        var first = First;
        while (first <= Last && (Last - first + 1 == Slots || Overlaps(_index[(int)first & _mask], at, length, wasted)))
            first++;
        return first == First ? 0 : first - 1;
    }

    /// <summary>Appends a message (evicting the oldest as needed), with its ordering constraint if any; returns its number.</summary>
    public long Append(ReadOnlySpan<byte> message, in After after = default, byte kind = 0)
    {
        if (message.Length > MaxMessage)
            throw new ArgumentOutOfRangeException(nameof(message), $"A message is at most {MaxMessage:N0} bytes here.");
        Place(message.Length, out var at, out var wasted);
        while (Last >= First && (Last - First + 1 == Slots ||
                                 Overlaps(_index[(int)First & _mask], at, message.Length, wasted)))
            First++; // evicted: the oldest, in order — the kept stay a contiguous run
        message.CopyTo(_arena.AsSpan(at));
        var seq = ++Last;
        if (Last < First) First = seq;
        var i = (int)seq & _mask;
        _index[i] = ((long)at << 32) | (uint)message.Length;
        if (after.Target is { } target)
        {
            if (_after is null)
            {
                _after = ArrayPool<object?>.Shared.Rent(Slots);
                Array.Clear(_after); // (a rented array may hold what another left)
                _afterVersion = ArrayPool<long>.Shared.Rent(Slots);
            }

            _after[i] = target;
            _afterVersion![i] = after.Version;
        }
        else if (_after is not null)
        {
            _after[i] = null;
        }

        if (kind != 0)
        {
            if (_kinds is null)
            {
                _kinds = ArrayPool<byte>.Shared.Rent(Slots);
                Array.Clear(_kinds);
            }

            _kinds[i] = kind;
        }
        else if (_kinds is not null)
        {
            _kinds[i] = 0;
        }

        _head = at + message.Length;
        return seq;
    }

    /// <summary>What entry <paramref name="seq" /> is: 0, a message, unless appended as another kind.</summary>
    public byte KindOf(long seq)
    {
        return _kinds is null || seq < First || seq > Last ? (byte)0 : _kinds[(int)seq & _mask];
    }

    /// <summary>Message <paramref name="seq" />, if kept.</summary>
    public bool TryGet(long seq, out ReadOnlySpan<byte> message)
    {
        message = default;
        if (seq < First || seq > Last) return false;
        var e = _index[(int)seq & _mask];
        message = _arena.AsSpan((int)(e >> 32), (int)e);
        return true;
    }

    /// <summary>Message <paramref name="seq" />'s ordering constraint, if it is kept and has one.</summary>
    public bool TryGetConstraint(long seq, out object? target, out long version)
    {
        target = null;
        version = 0;
        if (_after is null || seq < First || seq > Last) return false;
        var i = (int)seq & _mask;
        target = _after[i];
        version = _afterVersion![i];
        return target is not null;
    }

    /// <summary>Its arrays back to the pools (the ring unusable after).</summary>
    public void Release()
    {
        ArrayPool<long>.Shared.Return(_index);
        ArrayPool<byte>.Shared.Return(_arena);
        if (_after is not null) ArrayPool<object?>.Shared.Return(_after, true);
        if (_afterVersion is not null) ArrayPool<long>.Shared.Return(_afterVersion);
        if (_kinds is not null) ArrayPool<byte>.Shared.Return(_kinds);
        _after = null;
        _afterVersion = null;
        _kinds = null;
    }

    /// <summary>
    ///     Where <paramref name="length" /> bytes go: at the head, or — if they don't fit before the arena's end — at 0,
    ///     the tail wasted.
    /// </summary>
    private void Place(int length, out int at, out int wasted)
    {
        if (_head + length <= MaxMessage)
        {
            at = _head;
            wasted = 0;
        }
        else
        {
            at = 0;
            wasted = MaxMessage - _head;
        }
    }

    /// <summary>Whether an entry lies in the bytes about to be (re)used: the new message's, and the wasted tail.</summary>
    private bool Overlaps(long e, int at, int length, int wasted)
    {
        int offset = (int)(e >> 32), size = Math.Max(1, (int)e);
        var overNew = offset < at + length && at < offset + size;
        var overTail = wasted > 0 && offset + size > MaxMessage - wasted;
        return overNew || overTail;
    }
}

/// <summary>
///     A message topic — Nchan's channel: an ordered stream of messages (events, alerts, trades), not state — nothing
///     conflates, each subscriber is sent every one, in order, from the next message published after it subscribed (step
///     1):
///     a message topic has no snapshot. Each message is kept once, in a bounded buffer (a power of two of messages, and a
///     byte budget), for every subscriber to read at its own pace from where it is; a publish is answered 201/202 like any
///     topic's (epochs apply). With nobody subscribed a message isn't kept at all (202), and the buffer itself is released
///     until someone subscribes. One that falls behind the buffer — a message it hasn't been sent is evicted — meets the
///     topic's <see cref="OverflowPolicy" />. After a reconnect a subscriber resumes from the next message its client
///     didn't
///     get (<see cref="Outlet.Resume" />). Large messages travel out of band, stored once for every subscriber. A message
///     may
///     be ordered after the state it refers to (<see cref="After" />).
/// </summary>
public sealed class MessageTopic : Source
{
    private readonly int _capacity, _capacityBytes;
    private long _last; // the last message's number (kept across the ring's releases)
    private MessageRing? _ring; // while anyone is subscribed

    /// <param name="capacity">Messages kept (rounded up to a power of two).</param>
    /// <param name="capacityBytes">Bytes kept: the largest message, too.</param>
    /// <param name="overflow">What a subscriber falling behind the buffer meets.</param>
    public MessageTopic(int capacity = 1024, int capacityBytes = 1 << 20,
        OverflowPolicy overflow = OverflowPolicy.Fault)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 1);
        _capacity = capacity;
        _capacityBytes = capacityBytes;
        Overflow = overflow;
    }

    public OverflowPolicy Overflow { get; }

    /// <summary>
    ///     How its messages travel: Auto (default) — in-band up to the outlet's threshold, out of band above; Inline —
    ///     always in-band.
    /// </summary>
    public Delivery Delivery { get; set; } = Delivery.Auto;

    /// <summary>False for messages a per-message compressor can't shrink (see <see cref="LastValueSource.Publish" />).</summary>
    public bool Compressible { get; set; } = true;

    public override long Version
    {
        get
        {
            lock (Gate)
            {
                return _last;
            }
        }
    }

    internal override Delivery DeliveryMode => Delivery;
    internal override bool IsCompressible => Compressible;
    internal override byte RecordType => Wire.TopicMessage;
    internal override Lock Gate { get; } = new();

    internal override bool StartsAtNext => true;
    internal override bool Rewindable => true;

    /// <summary>The messages it keeps, newest first (at most <paramref name="limit" />), rendered by its format.</summary>
    public override bool RenderState(Utf8JsonWriter w, int limit)
    {
        var kept = new List<(long Seq, byte[] Payload)>();
        long first, last;
        if (!Gates.TryEnter(Gate))
        {
            w.WriteStartObject();
            w.WriteBoolean("busy", true);
            w.WriteEndObject();
            return true;
        }

        try
        {
            if (_ring is not { } ring || ring.Last < ring.First)
            {
                first = last = 0;
            }
            else
            {
                (first, last) = (ring.First, ring.Last);
                for (var seq = last; seq >= first && kept.Count < limit; seq--)
                    if (ring.TryGet(seq, out var message))
                        kept.Add((seq, message[..Math.Min(message.Length, Rendering.MaxPayload + 1)].ToArray()));
            }
        }
        finally
        {
            Gate.Exit();
        }

        w.WriteStartObject();
        w.WriteNumber("first", first);
        w.WriteNumber("last", last);
        w.WriteNumber("shown", kept.Count);
        w.WritePropertyName("messages");
        w.WriteStartArray();
        foreach (var (seq, payload) in kept)
        {
            w.WriteStartObject();
            w.WriteNumber("seq", seq);
            w.WritePropertyName("payload");
            Rendering.PayloadOrBytes(w, payload, Format);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
        return true;
    }

    public override bool RenderRecord(Utf8JsonWriter w, ReadOnlySpan<byte> record, bool payload)
    {
        if (record.Length < Wire.TopicMessageHeader || record[0] != Wire.TopicMessage) return false;
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[14..]);
        if (record.Length < Wire.TopicMessageHeader + length) return false;
        w.WriteStartObject();
        w.WriteNumber("seq", BinaryPrimitives.ReadInt64LittleEndian(record[6..]));
        if ((record[5] & 1) != 0) w.WriteBoolean("gap", true);
        if ((record[5] & 2) != 0) w.WriteBoolean("mailbox", true);
        w.WritePropertyName("payload");
        Rendering.PayloadOrBytes(w, record.Slice(Wire.TopicMessageHeader, length), Format);
        w.WriteEndObject();
        return true;
    }

    internal override bool CanResumeFrom(in Cursor cursor)
    {
        lock (Gate)
        {
            return _ring is { } r && cursor.Seq >= r.First - 1;
        }
    }

    /// <summary>
    ///     A subscriber starts at the next message: its cursor set here, with the route, so none published meanwhile is
    ///     missed.
    /// </summary>
    internal override long AddTap(Tap t)
    {
        lock (Gate)
        {
            _ring ??= new MessageRing(_capacity, _capacityBytes, _last);
            t.Cursor = new Cursor { Seq = _last };
            return base.AddTap(t);
        }
    }

    /// <summary>The last subscriber gone: what was kept goes too, and the buffer back to the pools.</summary>
    internal override (long Epoch, int Remaining) RemoveTap(Tap t)
    {
        lock (Gate)
        {
            var left = base.RemoveTap(t);
            if (left.Remaining == 0 && _ring is { } ring)
            {
                _ring = null;
                ring.Release();
            }

            return left;
        }
    }

    /// <summary>
    ///     Appends a message (evicting the oldest as needed — or, under <see cref="OverflowPolicy.Refuse" />, refusing rather
    ///     than evict one a subscriber hasn't been sent: <see cref="PublishStatus.Full" />); with nobody subscribed, keeps
    ///     nothing. 201/202 as for any topic; <see cref="PublishResult.Seq" />: its number (0 if not kept).
    ///     <paramref name="after" />:
    ///     each subscriber gets it only once it has that source's state at that version (see <see cref="After" />).
    /// </summary>
    public PublishResult Publish(ReadOnlySpan<byte> message, After after = default)
    {
        if (after.Tap is not null)
            throw new ArgumentException(
                "A topic's message is ordered after a source (each subscriber's tap on it), not one subscriber's tap.",
                nameof(after));
        long seq;
        lock (Gate)
        {
            if (_ring is not { } ring) return Acknowledge(); // nobody subscribed: nothing kept
            if (Overflow == OverflowPolicy.Refuse && ring.WouldEvict(message.Length) is long evicts and > 0)
                foreach (var t in Taps) // one not yet sent up to the newest it would evict, its client here: refused
                    if (t.IsActive && !t.IsAway && t.Cursor.Seq < evicts)
                        return new PublishResult(PublishStatus.Full, Epoch);
            seq = _last = ring.Append(message, after);
        }

        Notify();
        return Acknowledge(seq);
    }

    internal override bool IsOverflowed(in Cursor cursor)
    {
        if (Overflow == OverflowPolicy.Skip) return false;
        lock (Gate)
        {
            return _ring is { } r && cursor.Seq >= 0 && cursor.Seq < r.First - 1;
        }
    }

    /// <summary>
    ///     The stale message: the policy's fixed one, the last again, or the last through its function (one entry: no key, the
    ///     message its row) — flagged stale (<see cref="Wire.FlagStale" />), numbered as the last: not a new message.
    /// </summary>
    internal override int WriteStale(uint tapId, ErrorPolicy policy, StreamFault? fault, Span<byte> dst, bool first)
    {
        byte[]? scratch = null;
        lock (Gate)
        {
            if (!ReferenceEquals(Fault, fault)) return FaultCleared;
            try
            {
                ReadOnlySpan<byte> message;
                if (policy.Kind == StaleData.Static)
                {
                    message = policy.State.Span;
                }
                else if (_ring is not { } ring || !ring.TryGet(_last, out message))
                {
                    return 0; // nothing kept: nothing to replay
                }
                else if (policy.Kind == StaleData.Function)
                {
                    if (policy.Degrade is not { } degrade) return 0;
                    scratch = ArrayPool<byte>.Shared.Rent(4 + message.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(scratch, FoldEntry.Present);
                    message.CopyTo(scratch.AsSpan(4));
                    var length = message.Length;
                    try
                    {
                        degrade.Degrade(scratch.AsSpan(0, 4 + length), 1, 0, length);
                    }
                    catch
                    {
                        return 0;
                    }

                    if ((BinaryPrimitives.ReadUInt32LittleEndian(scratch) & FoldEntry.Present) == 0) return 0;
                    message = scratch.AsSpan(4, length);
                }

                var n = Wire.TopicMessageHeader + message.Length;
                if (n > dst.Length) return -1;
                Wire.WriteTopicMessage(dst, tapId, Wire.FlagStale, _last, message);
                return n;
            }
            finally
            {
                if (scratch is not null) ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }

    /// <summary>The ordering constraint of the next message a tap at <paramref name="cursor" /> would be sent, if it has one.</summary>
    internal bool TryPeekConstraint(in Cursor cursor, out object? target, out long version)
    {
        lock (Gate)
        {
            target = null;
            version = 0;
            if (_ring is not { } r || cursor.Seq < 0) return false;
            var next = Math.Max(cursor.Seq + 1, Overflow == OverflowPolicy.Skip ? r.First : 0);
            return r.TryGetConstraint(next, out target, out version);
        }
    }

    internal override int Write(uint tapId, ref Cursor cursor, Span<byte> dst)
    {
        lock (Gate)
        {
            if (_ring is not { } ring || cursor.Seq < 0)
            {
                cursor.Seq = _last;
                return 0;
            } // (no snapshot: from the next)

            var next = cursor.Seq + 1;
            if (next > ring.Last)
            {
                cursor.Seq = ring.Last;
                return 0;
            }

            byte flags = 0;
            if (next < ring.First)
            {
                if (Overflow != OverflowPolicy.Skip) return Overflowed; // (Refuse: one that was away)
                next = ring.First; // skipped: the gap flagged
                flags |= Wire.FlagGap;
            }

            ring.TryGet(next, out var message);
            var n = Wire.TopicMessageHeader + message.Length;
            if (n > dst.Length) return -1;
            Wire.WriteTopicMessage(dst, tapId, flags, next, message);
            cursor.Seq = next;
            return n;
        }
    }
}

/// <summary>
///     A subscription's mailbox messages (<see cref="Tap.TryPost" />) and rendezvous (
///     <see cref="Tap.TryRendezvous(ReadOnlySpan{byte}, long)" />):
///     its own ring, numbered apart from its topic's, kept after they're sent too — so a reconnect can resume from the
///     next
///     one its client didn't get.
/// </summary>
internal sealed class MailRing(int capacity, int capacityBytes)
{
    /// <summary>An entry's kind: a message, or a rendezvous (its bytes the correlation).</summary>
    public const byte Message = 0, Rendezvous = 1;

    private readonly Lock _lock = new();
    private MessageRing? _ring = new(capacity, capacityBytes); // null once released (its subscription closed)
    private long _sent; // the last sent

    public long Last
    {
        get
        {
            lock (_lock)
            {
                return _ring?.Last ?? 0;
            }
        }
    }

    public long Sent
    {
        get
        {
            lock (_lock)
            {
                return _sent;
            }
        }
    }

    public bool HasNext
    {
        get
        {
            lock (_lock)
            {
                return _ring is { } r && _sent < r.Last;
            }
        }
    }

    /// <summary>The next unsent entry's kind (a message if there is none).</summary>
    public byte NextKind
    {
        get
        {
            lock (_lock)
            {
                return _ring is { } r ? r.KindOf(_sent + 1) : Message;
            }
        }
    }

    /// <summary>The memory a mailbox takes (what its subscription is charged for it): its arena and index.</summary>
    public static long BytesFor(int capacity, int capacityBytes)
    {
        return 128 + Math.Max(1, capacityBytes) +
               8L * BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, capacity));
    }

    /// <summary>
    ///     Appends, evicting only messages already sent: false if it would have to evict one not yet sent (full), or it's
    ///     released.
    /// </summary>
    public bool TryAppend(ReadOnlySpan<byte> message, in After after, byte kind = Message)
    {
        lock (_lock)
        {
            if (_ring is not { } r || message.Length > r.MaxMessage) return false;
            if (r.WouldEvict(message.Length) is long evicts and > 0 && evicts > _sent) return false;
            r.Append(message, after, kind);
            return true;
        }
    }

    /// <summary>Resumes after a reconnect: after the last message its client got.</summary>
    public void ResumeAfter(long received)
    {
        lock (_lock)
        {
            _sent = Math.Min(Math.Max(0, received), _ring?.Last ?? 0);
        }
    }

    /// <summary>The ordering constraint of the next unsent message, if it has one.</summary>
    public bool TryPeekConstraint(out object? target, out long version)
    {
        lock (_lock)
        {
            target = null;
            version = 0;
            return _ring is { } r && r.TryGetConstraint(_sent + 1, out target, out version);
        }
    }

    /// <summary>
    ///     Writes the next unsent entry — a message, or a rendezvous (with <paramref name="rendezvousFlags" />, naming the
    ///     version its constraint names): bytes, 0 for none, −1 if it doesn't fit, or <see cref="Source.Overflowed" />
    ///     (evicted before it was sent).
    /// </summary>
    public int WriteNext(uint tapId, Span<byte> dst, byte rendezvousFlags = 0)
    {
        lock (_lock)
        {
            if (_ring is not { } r) return 0;
            var next = _sent + 1;
            if (next > r.Last) return 0;
            if (next < r.First) return Source.Overflowed;
            r.TryGet(next, out var message);
            int n;
            if (r.KindOf(next) == Rendezvous)
            {
                n = Wire.RendezvousHeader + message.Length;
                if (n > dst.Length) return -1;
                r.TryGetConstraint(next, out _, out var seq);
                Wire.WriteRendezvous(dst, tapId, rendezvousFlags, seq, message);
            }
            else
            {
                n = Wire.TopicMessageHeader + message.Length;
                if (n > dst.Length) return -1;
                Wire.WriteTopicMessage(dst, tapId, Wire.FlagMailbox, next, message);
            }

            _sent = next;
            return n;
        }
    }

    /// <summary>Its subscription closed: the ring back to the pools; posts refused from now on.</summary>
    public void Release()
    {
        lock (_lock)
        {
            _ring?.Release();
            _ring = null;
        }
    }
}