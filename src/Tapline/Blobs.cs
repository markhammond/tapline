using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace Tapline;

/// <summary>How a payload travels to the client.</summary>
public enum Delivery : byte
{
    /// <summary>
    ///     In-band up to <see cref="OutletOptions.OutOfBandThreshold" /> (and 64 KB for messages), otherwise
    ///     <see cref="OutOfBand" />.
    /// </summary>
    Auto = 0,

    /// <summary>Always in-band.</summary>
    Inline = 1,

    /// <summary>
    ///     A handle in-band; the client library fetches the bytes over HTTP and applies or delivers them in order. The tap
    ///     (or message lane) waits for the fetch, so nothing is buffered on the client.
    /// </summary>
    OutOfBand = 2,

    /// <summary>A handle in-band, handed to the app as is: it fetches the bytes when, or if, it wants. Nothing waits.</summary>
    Reference = 3
}

/// <summary>
///     A blob's identity. Content-addressed blobs (<see cref="Source.ContentAddressed" />, or <c>contentAddressed</c> on a
///     post) are named by the first 128 bits of their SHA-256 — identical bytes are stored once, and the client verifies
///     them (WebCrypto). Any other blob gets a UUIDv7 (RFC 9562), with no hashing at all: a millisecond timestamp, a
///     counter
///     within the millisecond and 62 bits of entropy — time-ordered and never reused, across stores, processes and
///     restarts,
///     so a URL never comes to name other bytes (a cached response stays right for as long as it is kept).
/// </summary>
public readonly record struct BlobId(UInt128 Value)
{
    public static BlobId Of(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return new BlobId(BinaryPrimitives.ReadUInt128BigEndian(hash));
    }

    /// <summary>The 16 id bytes, in hash order.</summary>
    public void WriteTo(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt128BigEndian(dst, Value);
    }

    public static BlobId Read(ReadOnlySpan<byte> src)
    {
        return new BlobId(BinaryPrimitives.ReadUInt128BigEndian(src));
    }

    public override string ToString()
    {
        return Value.ToString("x32", CultureInfo.InvariantCulture);
    }

    public static bool TryParse(ReadOnlySpan<char> hex, out BlobId id)
    {
        id = default;
        if (hex.Length != 32 ||
            !UInt128.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            return false;
        id = new BlobId(v);
        return true;
    }
}

/// <summary>
///     Ephemeral store for out-of-band payloads, allocation-free once warm. A blob is stored in fixed-size segments
///     (<see cref="SegmentBytes" />, under the large object heap's threshold) taken from the store's own pool, chained as
///     a
///     <see cref="ReadOnlySequence{T}" /> — never one large array — and returned to the pool when the last reference goes:
///     the store's own (until it evicts the blob) and one per open <see cref="BlobLease" /> (a fetch being written), so an
///     eviction during a fetch never recycles bytes still being sent. Free segments beyond <see cref="RetainBytes" /> are
///     left to the GC (small-object heap).
///     <para>
///         Each handle sent to a client carries a tag — HMAC(blob, outlet id, outlet nonce) — so a blob is fetchable only
///         by
///         outlets it was issued to, and only via a URL that doesn't reveal the nonce. A blob lives for
///         <see cref="Grace" />
///         after it was last issued; blobs pinned by queued messages live until those are sent. Bounded by
///         <see cref="MaxBytes" /> (counted in whole segments): a put that doesn't fit (after evicting expired blobs)
///         fails and the
///         caller falls back (inline, or reject).
///     </para>
///     <para>
///         Content addressing is the caller's choice (zero cost when not taken): hashed, identical bytes are found before
///         they
///         are copied and shared by every outlet issued them; otherwise each put is a new blob with a unique id, and only
///         the
///         copy into the segments is paid. Sharing the same record between taps doesn't need it either: a source keeps the
///         last
///         record it sent out of band for any tap that would write the same bytes (see
///         <see cref="Source.ContentAddressed" />).
///     </para>
/// </summary>
public sealed class BlobStore
{
    /// <summary>Default segment size: 16 KB (a blob of n bytes takes ⌈n / 16 KB⌉ of them).</summary>
    public const int DefaultSegmentBytes = 16 * 1024;

    /// <summary>A blob's bytes: one segment of the chain (its <see cref="ReadOnlySequenceSegment{T}.Memory" /> the used part).</summary>
    internal sealed class Segment(int size) : ReadOnlySequenceSegment<byte>
    {
        public readonly byte[] Data = new byte[size];
        public Segment? NextFree;
        public Blob? Owner; // the blob it holds bytes of; null while free

        public void Chain(Segment? next, long runningIndex, int length)
        {
            Memory = Data.AsMemory(0, length);
            Next = next;
            RunningIndex = runningIndex;
        }

        public void Unchain()
        {
            Memory = default;
            Next = null;
            RunningIndex = 0;
        }
    }

    /// <summary>A stored (or leased) blob. Pooled: <see cref="Generation" /> tells one use of the node from the next.</summary>
    internal sealed class Blob
    {
        public bool ContentAddressed;
        public long Expires;
        public Segment? First, Last;
        public uint Generation; // bumped each time the node is recycled
        public BlobId Id;
        public int Length, Segments;
        public bool Listed;
        public Blob? NextFree;
        public int Pins; // queued handles: not evicted while any
        public Blob? Prev, Next; // the expiry list: stored, unpinned, soonest to expire first
        public int Refs; // 1 while stored, +1 per open lease: at 0 the segments go back
        public bool Stored; // in the store's table (fetchable, issuable)

        public ReadOnlySequence<byte> Sequence => First is null
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(First, 0, Last!, Last!.Memory.Length);
    }

    private const int RetainBlobNodes = 4096;

    private readonly Lock _lock = new();
    private readonly Dictionary<BlobId, Blob> _blobs = new();
    private Blob? _head, _tail; // the expiry list
    private Segment? _freeSegments;
    private int _freeSegmentCount;
    private readonly int _retainSegments;
    private Blob? _freeBlobs;
    private int _freeBlobCount;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ulong _entropy; // UUIDv7 ids: the store's 62 random bits (or fresh per id)
    private readonly bool _entropyPerId;
    private long _idMs; // …the last id's timestamp, and its counter within that millisecond
    private int _idSeq;
    private readonly TimeProvider _time;
    private readonly long _graceTicks;
    private long _bytes, _stored, _shared;

    /// <param name="maxBytes">Most bytes held (in whole segments), leased blobs included.</param>
    /// <param name="grace">How long a blob lives after it was last issued (default 60 s).</param>
    /// <param name="time">Clock (default <see cref="TimeProvider.System" />).</param>
    /// <param name="segmentBytes">Segment size, 1–64 KB (default <see cref="DefaultSegmentBytes" />).</param>
    /// <param name="retainBytes">Free segments kept for reuse (default: the lesser of 32 MB and <paramref name="maxBytes" />).</param>
    /// <param name="entropyPerId">
    ///     Unique (UUIDv7) ids: fresh random bits for each id, so one id says nothing of the next — false (default): the
    ///     store's own random bits, drawn once (ids then differ by time and counter). Fetches are authorized by the handle's
    ///     tag either way; this only matters if ids themselves must be unguessable.
    /// </param>
    public BlobStore(long maxBytes = 256L << 20, TimeSpan? grace = null, TimeProvider? time = null,
        int segmentBytes = DefaultSegmentBytes, long? retainBytes = null,
        bool entropyPerId = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(segmentBytes,
            64 * 1024); // under the large object heap's 85,000 bytes
        MaxBytes = maxBytes;
        Grace = grace ?? TimeSpan.FromSeconds(60);
        SegmentBytes = segmentBytes;
        RetainBytes = Math.Max(0, retainBytes ?? Math.Min(maxBytes, 32L << 20));
        _retainSegments = (int)Math.Min(int.MaxValue, RetainBytes / segmentBytes);
        _time = time ?? TimeProvider.System;
        _graceTicks = (long)(Grace.TotalSeconds * _time.TimestampFrequency);
        _entropyPerId = entropyPerId;
        _entropy = Entropy();
    }

    private static ulong Entropy()
    {
        Span<byte> bits = stackalloc byte[8];
        RandomNumberGenerator.Fill(bits);
        return BinaryPrimitives.ReadUInt64LittleEndian(bits) & ((1UL << 62) - 1);
    }

    /// <summary>
    ///     A UUIDv7 (RFC 9562): unix_ts_ms (48) | version 7 (4) | a counter within the millisecond (12) | variant 0b10 (2) |
    ///     entropy (62). Monotonic: more than 4,096 in one millisecond borrow from the next. The lock held.
    /// </summary>
    private BlobId NextId()
    {
        var ms = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (ms > _idMs)
        {
            _idMs = ms;
            _idSeq = 0;
        }
        else if (++_idSeq > 0xFFF)
        {
            _idMs++;
            _idSeq = 0;
        } // also a clock that stepped back: still increasing

        var high = ((ulong)_idMs << 16) | (0x7UL << 12) | (uint)_idSeq;
        var low = (0b10UL << 62) | (_entropyPerId ? Entropy() : _entropy);
        return new BlobId(new UInt128(high, low));
    }

    public long MaxBytes { get; }
    public TimeSpan Grace { get; }
    public int SegmentBytes { get; }
    public long RetainBytes { get; }

    /// <summary>Bytes held by blobs, stored or still leased (whole segments).</summary>
    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Free segments' bytes, kept for reuse.</summary>
    public long PooledBytes
    {
        get
        {
            lock (_lock)
            {
                return (long)_freeSegmentCount * SegmentBytes;
            }
        }
    }

    /// <summary>Blobs stored (fetchable).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _blobs.Count;
            }
        }
    }

    /// <summary>Blobs stored, and content-addressed puts that found their content already stored (and so copied nothing).</summary>
    public (long Stored, long Shared) Stats
    {
        get
        {
            lock (_lock)
            {
                return (_stored, _shared);
            }
        }
    }

    /// <summary>
    ///     Stores <paramref name="data" /> and issues it: its grace period starts (or restarts). Content-addressed: hashed
    ///     first, and content already stored is issued again instead of copied. Optionally pinned (until <see cref="Unpin" />
    ///     ).
    ///     False if it doesn't fit. The copy happens outside the store's lock.
    /// </summary>
    internal bool TryPut(ReadOnlySpan<byte> data, bool pin, bool contentAddressed, out BlobId id)
    {
        id = contentAddressed ? BlobId.Of(data) : default;
        Blob blob;
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            if (contentAddressed && _blobs.TryGetValue(id, out var found))
            {
                _shared++;
                Issue(found, pin, now);
                return true;
            }

            Sweep(now);
            var segments = (int)(((long)data.Length + SegmentBytes - 1) / SegmentBytes);
            var bytes = (long)segments * SegmentBytes;
            if (_bytes + bytes > MaxBytes) return false;
            _bytes += bytes; // reserved; released by Recycle
            if (!contentAddressed) id = NextId();
            blob = RentBlob();
            blob.Id = id;
            blob.Length = data.Length;
            blob.Segments = segments;
            blob.ContentAddressed = contentAddressed;
            blob.Refs = 1;
            Segment? previous = null;
            for (var k = 0; k < segments; k++)
            {
                var s = RentSegment();
                Debug.Assert(s.Owner is null, "a pooled segment still owned by a blob");
                s.Owner = blob;
                if (previous is null) blob.First = s;
                else previous.Chain(s, (long)(k - 1) * SegmentBytes, SegmentBytes);
                previous = s;
            }

            if (previous is not null)
                previous.Chain(null, (long)(segments - 1) * SegmentBytes, data.Length - (segments - 1) * SegmentBytes);
            blob.Last = previous;
        }

        // Copy outside the lock: the blob isn't in the table yet, so nothing else can see it.
        var o = 0;
        for (var s = blob.First; s is not null; s = (Segment?)s.Next)
        {
            var n = s.Memory.Length;
            data.Slice(o, n).CopyTo(s.Data);
            o += n;
        }

        Debug.Assert(o == data.Length, "segments hold the whole blob");

        lock (_lock)
        {
            var now = _time.GetTimestamp();
            if (contentAddressed &&
                _blobs.TryGetValue(id, out var raced)) // the same content stored meanwhile: use that one
            {
                _shared++;
                Issue(raced, pin, now);
                blob.Refs = 0;
                Recycle(blob);
                return true;
            }

            _blobs.Add(id, blob);
            blob.Stored = true;
            _stored++;
            Issue(blob, pin, now);
            return true;
        }
    }

    /// <summary>Issues an existing blob again (restarts its grace period). False if it has been evicted.</summary>
    internal bool TryIssue(BlobId id, bool pin = false)
    {
        lock (_lock)
        {
            if (!_blobs.TryGetValue(id, out var blob)) return false;
            Issue(blob, pin, _time.GetTimestamp());
            return true;
        }
    }

    /// <summary>A queued handle was sent (or dropped): the blob now lives out its grace period.</summary>
    internal void Unpin(BlobId id)
    {
        lock (_lock)
        {
            if (!_blobs.TryGetValue(id, out var blob) || blob.Pins == 0) return;
            if (--blob.Pins == 0) Touch(blob, _time.GetTimestamp());
        }
    }

    /// <summary>The handle's tag for one outlet: HMAC-SHA256(key, blobId ‖ outletId ‖ nonce)[0..16].</summary>
    internal void Tag(BlobId id, int outletId, uint nonce, Span<byte> tag)
    {
        Span<byte> input = stackalloc byte[24];
        id.WriteTo(input);
        BinaryPrimitives.WriteInt32LittleEndian(input[16..], outletId);
        BinaryPrimitives.WriteUInt32LittleEndian(input[20..], nonce);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, input, mac);
        mac[..16].CopyTo(tag);
    }

    /// <summary>
    ///     For the fetch endpoint: a lease on the blob's bytes, if <paramref name="tag" /> proves it was issued to the live
    ///     outlet <paramref name="outletId" />. Dispose the lease once the bytes are written: until then they can't be reused,
    ///     even if the blob is evicted meanwhile.
    /// </summary>
    public bool TryLease(BlobId id, int outletId, ReadOnlySpan<byte> tag, OutletRegistry outlets,
        [NotNullWhen(true)] out BlobLease? lease)
    {
        lease = null;
        if (tag.Length != 16 || outlets.ById(outletId) is not { } outlet) return false;
        Span<byte> expected = stackalloc byte[16];
        Tag(id, outletId, outlet.Nonce, expected);
        return CryptographicOperations.FixedTimeEquals(expected, tag) && TryLease(id, out lease);
    }

    /// <summary>
    ///     Parses the URL segments <c>{blobId hex}/{outletId}/{tag hex}</c> and calls
    ///     <see cref="TryLease(BlobId,int,ReadOnlySpan{byte},OutletRegistry,out BlobLease)" />.
    /// </summary>
    public bool TryLease(ReadOnlySpan<char> idHex, int outletId, ReadOnlySpan<char> tagHex, OutletRegistry outlets,
        [NotNullWhen(true)] out BlobLease? lease)
    {
        lease = null;
        Span<byte> tag = stackalloc byte[16];
        return BlobId.TryParse(idHex, out var id) && tagHex.Length == 32
                                                  && Convert.FromHexString(tagHex, tag, out _, out var written) ==
                                                  OperationStatus.Done && written == 16
                                                  && TryLease(id, outletId, tag, outlets, out lease);
    }

    /// <summary>
    ///     A lease on a stored blob for a viewer, unauthenticated (server-side), and without waiting: if another thread holds
    ///     the store, <see cref="BlobLookup.Busy" /> — the viewer never makes the router's threads wait on it.
    /// </summary>
    internal BlobLookup TryLeaseNow(BlobId id, out BlobLease? lease)
    {
        lease = null;
        if (!_lock.TryEnter()) return BlobLookup.Busy;
        try
        {
            if (!_blobs.TryGetValue(id, out var blob)) return BlobLookup.Gone;
            Interlocked.Increment(ref blob.Refs);
            lease = new BlobLease(this, blob, blob.Generation);
            return BlobLookup.Leased;
        }
        finally
        {
            _lock.Exit();
        }
    }

    /// <summary>A lease on a stored blob, unauthenticated (the endpoint checks the tag first).</summary>
    internal bool TryLease(BlobId id, [NotNullWhen(true)] out BlobLease? lease)
    {
        lock (_lock)
        {
            if (!_blobs.TryGetValue(id, out var blob))
            {
                lease = null;
                return false;
            }

            Debug.Assert(blob.Stored && blob.Refs >= 1, "a blob in the table holds the store's reference");
            Interlocked.Increment(ref blob.Refs);
            lease = new BlobLease(this, blob, blob.Generation);
            return true;
        }
    }

    /// <summary>Evicts expired, unpinned blobs (also done on every put).</summary>
    public void Sweep()
    {
        lock (_lock)
        {
            Sweep(_time.GetTimestamp());
        }
    }

    private void Sweep(long now)
    {
        while (_head is { } blob && blob.Expires <= now) Evict(blob);
    }

    private void Issue(Blob blob, bool pin, long now)
    {
        if (pin)
        {
            if (blob.Pins++ == 0) Unlist(blob); // pinned: off the expiry list until unpinned
        }
        else if (blob.Pins == 0)
        {
            Touch(blob, now);
        }
    }

    /// <summary>
    ///     Restarts the blob's grace period: to the back of the expiry list (the grace is the same for all, so it stays
    ///     sorted).
    /// </summary>
    private void Touch(Blob blob, long now)
    {
        Debug.Assert(blob.Stored && blob.Pins == 0, "only stored, unpinned blobs expire");
        blob.Expires = now + _graceTicks;
        Unlist(blob);
        blob.Prev = _tail;
        blob.Next = null;
        if (_tail is null) _head = blob;
        else _tail.Next = blob;
        _tail = blob;
        blob.Listed = true;
    }

    private void Unlist(Blob blob)
    {
        if (!blob.Listed) return;
        if (blob.Prev is null) _head = blob.Next;
        else blob.Prev.Next = blob.Next;
        if (blob.Next is null) _tail = blob.Prev;
        else blob.Next.Prev = blob.Prev;
        blob.Prev = blob.Next = null;
        blob.Listed = false;
    }

    private void Evict(Blob blob)
    {
        Debug.Assert(blob.Stored && blob.Pins == 0, "evicting a blob that isn't stored, or is pinned");
        Unlist(blob);
        _blobs.Remove(blob.Id);
        blob.Stored = false;
        var refs = Interlocked.Decrement(ref blob.Refs); // the store's own
        Debug.Assert(refs >= 0, "blob reference count below zero");
        if (refs == 0) Recycle(blob);
    }

    /// <summary>A lease closed (any thread).</summary>
    internal void Release(Blob blob, uint generation)
    {
        Debug.Assert(blob.Generation == generation, "a lease released after its blob was recycled");
        var refs = Interlocked.Decrement(ref blob.Refs);
        Debug.Assert(refs >= 0, "blob reference count below zero");
        if (refs == 0)
            lock (_lock)
            {
                Recycle(blob);
            }
    }

    /// <summary>The last reference is gone: the segments, and the node, back to the pool (the lock held).</summary>
    private void Recycle(Blob blob)
    {
        Debug.Assert(blob.Refs == 0 && !blob.Stored && !blob.Listed && blob.Pins == 0, "recycling a blob still in use");
        for (var s = blob.First; s is not null;)
        {
            var next = (Segment?)s.Next;
            Debug.Assert(ReferenceEquals(s.Owner, blob), "a segment recycled by a blob that doesn't own it");
            s.Owner = null;
            s.Unchain();
#if DEBUG
            s.Data.AsSpan().Fill(0xDD);                         // a read after recycling shows as poison, not another blob's bytes
#endif
            if (_freeSegmentCount < _retainSegments)
            {
                s.NextFree = _freeSegments;
                _freeSegments = s;
                _freeSegmentCount++;
            }

            s = next;
        }

        _bytes -= (long)blob.Segments * SegmentBytes;
        blob.First = blob.Last = null;
        blob.Length = blob.Segments = 0;
        blob.Generation++;
        if (_freeBlobCount < RetainBlobNodes)
        {
            blob.NextFree = _freeBlobs;
            _freeBlobs = blob;
            _freeBlobCount++;
        }
    }

    private Segment RentSegment()
    {
        if (_freeSegments is not { } s) return new Segment(SegmentBytes);
        _freeSegments = s.NextFree;
        s.NextFree = null;
        _freeSegmentCount--;
        return s;
    }

    private Blob RentBlob()
    {
        if (_freeBlobs is not { } b) return new Blob();
        _freeBlobs = b.NextFree;
        b.NextFree = null;
        _freeBlobCount--;
        return b;
    }

#if DEBUG
    /// <summary>DEBUG: the store's invariants — accounting, the expiry list, the pool, segment ownership.</summary>
    internal void AssertConsistent()
    {
        lock (_lock)
        {
            long storedBytes = 0;
            foreach (var blob in _blobs.Values)
            {
                Debug.Assert(blob.Stored && blob.Refs >= 1 && blob.Listed == (blob.Pins == 0), "a stored blob's state");
                int n = 0;
                for (var s = blob.First; s is not null; s =
 (Segment?)s.Next, n++) Debug.Assert(ReferenceEquals(s.Owner, blob), "segment ownership");
                Debug.Assert(n == blob.Segments, "segment count");
                storedBytes += (long)blob.Segments * SegmentBytes;
            }
            Debug.Assert(storedBytes <= _bytes, "stored bytes exceed the accounted bytes (leased blobs make up the rest)");
            long last = long.MinValue;
            for (var b = _head; b is not null; b = b.Next)
            {
                Debug.Assert(b.Listed && b.Stored && b.Pins == 0 && b.Expires >= last, "the expiry list is sorted and holds only stored, unpinned blobs");
                last = b.Expires;
            }
            int free = 0;
            for (var s = _freeSegments; s is not null; s =
 s.NextFree, free++) Debug.Assert(s.Owner is null && s.Next is null, "a free segment is unowned");
            Debug.Assert(free == _freeSegmentCount, "free segment count");
        }
    }
#endif
}

/// <summary>
///     A blob's bytes held for reading (one fetch): they can't be reused until it is disposed, even if the blob is evicted
///     meanwhile. <see cref="Data" /> is valid only until then. Not pooled — a recycled lease could otherwise read another
///     blob — and safe to dispose twice.
/// </summary>
public sealed class BlobLease : IDisposable
{
    private readonly BlobStore.Blob _blob;
    private readonly uint _generation;
    private BlobStore? _store;

    internal BlobLease(BlobStore store, BlobStore.Blob blob, uint generation)
    {
        _store = store;
        _blob = blob;
        _generation = generation;
        Length = blob.Length;
        ContentAddressed = blob.ContentAddressed;
    }

    public int Length { get; }

    /// <summary>Its id is the content's SHA-256 (see <see cref="BlobId" />).</summary>
    public bool ContentAddressed { get; }

    /// <summary>The bytes, as a sequence of segments. Valid until <see cref="Dispose" />.</summary>
    public ReadOnlySequence<byte> Data
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _store) is null, this);
            Debug.Assert(_blob.Generation == _generation && Volatile.Read(ref _blob.Refs) > 0,
                "a lease on a recycled blob");
#if DEBUG
            for (var s = _blob.First; s is not null; s =
 (BlobStore.Segment?)s.Next) Debug.Assert(ReferenceEquals(s.Owner, _blob), "a leased segment owned by another blob");
#endif
            return _blob.Sequence;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _store, null) is { } store) store.Release(_blob, _generation);
    }

    public byte[] ToArray()
    {
        return Data.ToArray();
    }
}