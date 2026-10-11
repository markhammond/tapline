using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tapline;

/// <summary>
///     A single-producer, single-consumer ring of variable-length messages, written and read in place — a socket buffer,
///     stood in for: the producer claims room (<see cref="TryClaim" />), writes the message into it and
///     <see cref="Commit" />s it; the consumer reads it where it lies (<see cref="TryRead" />) and
///     <see cref="Release" />s it. No lock, nothing copied, nothing allocated going round. As <see cref="SpscRing{T}" />:
///     each party's cursor on a cache line of its own, each caching the other's. A message never wraps: when it would, a
///     pad fills the ring's tail and it starts at the head. Each message is preceded by a record of the ring's own (8
///     bytes: its length) and starts 8-byte aligned. Full, a claim fails and the message is dropped and counted
///     (<see cref="Dropped" />) — as a socket buffer drops; the consumer sees the gap in its own sequence numbers. The
///     consumer waits, empty, on a thread of its own (<see cref="Park" />) or as a task (<see cref="WaitToReadAsync" />):
///     either way the producer wakes it, by a flag it raises first. The buffer is pinned for the ring's life (on the
///     pinned object heap) and 64-byte aligned: a span's address stays put, for a reader that casts a message in place.
/// </summary>
public sealed class SpscByteRing : IDisposable
{
    private const long Parked = 1, Awaited = 2;
    private const int RecordSize = 8; // a message's length (an int, native order; negative: a pad), then 4 spare

    private readonly byte[] _array;
    private readonly int _base, _capacity, _mask, _maxMessage;
    private readonly ManualResetEventSlim _wake = new(false, 0); // no spinning of its own: the policy is the consumer's
    private readonly RingWaiter _readable;
    private long _committing; // the producer's: the tail once the message claimed is committed
    private long _releasing; // the consumer's: the head once the message read is released
    private int _completed;
    private long _dropped;
    private CacheLine _head, _tailSeen; // the consumer's: what it read, and the tail as it last saw it
    private CacheLine _waits; // the consumer may be waiting: parked on its thread (1); has waited as a task (2, kept)
    private CacheLine _tail, _headSeen; // the producer's: what it wrote, and the head as it last saw it

    /// <param name="capacityBytes">Its bytes: a power of two, 4 KiB or more.</param>
    public SpscByteRing(int capacityBytes)
    {
        if (capacityBytes < 4096 || (capacityBytes & (capacityBytes - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "A power of two, 4 KiB or more.");
        (_capacity, _mask, _maxMessage) = (capacityBytes, capacityBytes - 1, capacityBytes / 2 - RecordSize);
        _array = GC.AllocateArray<byte>(capacityBytes + 64, pinned: true);
        var handle = GCHandle.Alloc(_array, GCHandleType.Pinned); // (only to learn its address: it never moves)
        var address = handle.AddrOfPinnedObject();
        handle.Free();
        _base = (int)((64 - (address & 63)) & 63);
        _readable = new RingWaiter(() => HasData || IsCompleted, () => HasData);
    }

    public int Capacity => _capacity;

    /// <summary>The longest message a claim takes: half the ring, less its record.</summary>
    public int MaxMessageLength => _maxMessage;

    /// <summary>Messages the producer dropped for want of room.</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>The producer is done (<see cref="Complete" />): once the ring's empty, an async wait answers false.</summary>
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public void Dispose()
    {
        _wake.Dispose();
    }

    // The producer's side -------------------------------------------------------------------------------------------

    /// <summary>
    ///     Room for a message of <paramref name="length" /> bytes (1 to <see cref="MaxMessageLength" />): where to write
    ///     it — then <see cref="Commit" /> — or empty when the ring is full (the message is then dropped and counted).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> TryClaim(int length)
    {
        if ((uint)(length - 1) >= (uint)_maxMessage)
            throw new ArgumentOutOfRangeException(nameof(length), $"1 to {_maxMessage} bytes.");
        var record = (RecordSize + length + 7) & ~7;
        var tail = _tail.Value;
        var at = (int)(tail & _mask);
        var toEnd = _capacity - at;
        var skip = toEnd < record ? toEnd : 0; // a pad to the end, then the message at the start
        if (skip + record > _capacity - (tail - _headSeen.Value))
        {
            _headSeen.Value = Volatile.Read(ref _head.Value);
            if (skip + record > _capacity - (tail - _headSeen.Value))
            {
                Volatile.Write(ref _dropped, _dropped + 1);
                return default;
            }
        }

        if (skip != 0)
        {
            MemoryMarshal.Write(_array.AsSpan(_base + at), -skip);
            at = 0;
        }

        _committing = tail + skip + record;
        var start = _base + at;
        MemoryMarshal.Write(_array.AsSpan(start), length);
        return _array.AsSpan(start + RecordSize, length);
    }

    /// <summary>Publishes the message last claimed; wakes the consumer if it's waiting.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit()
    {
        Volatile.Write(ref _tail.Value, _committing); // (the message's writes before: release)
        Interlocked.MemoryBarrier(); // the tail's write before the flag's read: the consumer does the reverse
        if (_waits.Value != 0) Woken(); // (one flag read, a ring parked on a thread: an async waiter's only past it)
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Woken()
    {
        if ((Volatile.Read(ref _waits.Value) & Parked) != 0) _wake.Set();
        _readable.WakeFenced();
    }

    /// <summary>The producer's done: an async wait answers false once the ring's empty; a parked consumer is woken.</summary>
    public void Complete()
    {
        Volatile.Write(ref _completed, 1);
        Interlocked.MemoryBarrier();
        _wake.Set();
        _readable.WakeFenced();
    }

    // The consumer's side -------------------------------------------------------------------------------------------

    /// <summary>The next message, in place, or empty when the ring is. Then <see cref="Release" />.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> TryRead()
    {
        while (true)
        {
            var head = _head.Value;
            if (head == _tailSeen.Value)
            {
                _tailSeen.Value = Volatile.Read(ref _tail.Value);
                if (head == _tailSeen.Value) return default;
            }

            var at = _base + (int)(head & _mask);
            var length = MemoryMarshal.Read<int>(_array.AsSpan(at));
            if (length > 0)
            {
                _releasing = head + ((RecordSize + length + 7) & ~7);
                return _array.AsSpan(at + RecordSize, length);
            }

            Volatile.Write(ref _head.Value, head - length); // a pad: skip to the ring's start
        }
    }

    /// <summary>Gives the message last read back to the producer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release()
    {
        Volatile.Write(ref _head.Value, _releasing);
    }

    /// <summary>
    ///     Parks the consumer's thread until the producer commits something (or <see cref="Wake" />,
    ///     <see cref="Complete" />), unless it did meanwhile: the flag is raised with a full fence before the tail is read
    ///     again, so a commit either sees the flag or is seen. Spinning first, if at all, is the consumer's policy.
    /// </summary>
    public void Park()
    {
        Interlocked.Or(ref _waits.Value, Parked); // (a full fence)
        if (_head.Value == Volatile.Read(ref _tail.Value) && !IsCompleted) _wake.Wait();
        _wake.Reset();
        Interlocked.And(ref _waits.Value, ~Parked);
    }

    /// <summary>Wakes a parked consumer (to stop it, say).</summary>
    public void Wake()
    {
        _wake.Set();
    }

    /// <summary>
    ///     Completes once there's a message to read (true) — at once if there is — or the producer has completed and the
    ///     ring is empty (false). Allocation-free: it spins a moment, then waits on the pool.
    /// </summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        if (HasData) return new ValueTask<bool>(true);
        if (IsCompleted) return new ValueTask<bool>(HasData);
        if ((_waits.Value & Awaited) == 0) Interlocked.Or(ref _waits.Value, Awaited); // (before the waiter's flag)
        return _readable.WaitAsync(cancellationToken);
    }

    private bool HasData => Volatile.Read(ref _head.Value) != Volatile.Read(ref _tail.Value);
}
