using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;

namespace Tapline;

/// <summary>
///     A single-producer, single-consumer ring of <typeparamref name="T" /> — one thread (or task) writes, one reads — with
///     no lock: each party's cursor sits on a cache line of its own (sharing one would make every write a transfer between
///     cores), and each caches the other's, reading it afresh only when the ring looks full (the writer) or empty (the
///     reader). Full, <see cref="TryWrite" /> says so and the writer chooses: drop and count, or wait for room
///     (<see cref="WriteAsync" />: back-pressure). Empty, the reader waits (<see cref="ReadAsync" />,
///     <see cref="WaitToReadAsync" />). Each side's wait is allocation-free and its own: it spins a moment (never yielding),
///     then raises a flag, fences, and looks again, and the other side, having moved its cursor, fences and looks at the flag — so a wake is never missed. A slot
///     is cleared as it's read (a reference held no longer than needed).
/// </summary>
public sealed class SpscRing<T>
{
    private readonly int _mask;
    private readonly T[] _slots;
    private readonly RingWaiter _readable, _writable;
    private int _completed;
    private CacheLine _head, _tailSeen; // the reader's: what it read, and the tail as it last saw it
    private CacheLine _tail, _headSeen; // the writer's: what it wrote, and the head as it last saw it

    /// <param name="capacity">Its slots: a power of two, at least two.</param>
    public SpscRing(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "A power of two, at least two.");
        (_slots, _mask) = (new T[capacity], capacity - 1);
        _readable = new RingWaiter(() => HasItem || IsCompleted, () => HasItem);
        _writable = new RingWaiter(() => HasRoom, () => HasRoom);
    }

    public int Capacity => _slots.Length;

    /// <summary>Items written and not yet read (a moment's view: either side may be moving).</summary>
    public int Count => (int)(Volatile.Read(ref _tail.Value) - Volatile.Read(ref _head.Value));

    /// <summary>The writer is done (<see cref="Complete" />): once the ring's empty, reading ends.</summary>
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    // The writer's side ---------------------------------------------------------------------------------------------

    /// <summary>Writes <paramref name="item" /> if there's room: false if the ring is full.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(T item)
    {
        var tail = _tail.Value;
        if (tail - _headSeen.Value >= _slots.Length)
        {
            _headSeen.Value = Volatile.Read(ref _head.Value);
            if (tail - _headSeen.Value >= _slots.Length) return false;
        }

        _slots[(int)(tail & _mask)] = item;
        Volatile.Write(ref _tail.Value, tail + 1); // (the slot's write before the cursor's: release)
        _readable.Wake(); // (a fence, then the flag: the reader's wait raises it, fences, then looks)
        return true;
    }

    /// <summary>Writes <paramref name="item" />, waiting for room while the ring is full: the reader holds the writer back.</summary>
    public ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        return TryWrite(item) ? default : WriteSlowAsync(item, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))] // (a wait's state machine pooled: nothing allocated)
    private async ValueTask WriteSlowAsync(T item, CancellationToken cancellationToken)
    {
        while (!TryWrite(item))
        {
            if (Volatile.Read(ref _completed) != 0)
                throw new InvalidOperationException("The ring is completed: nothing more is written.");
            await _writable.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The writer's done: the reader reads what's written, then its reading ends (a wait answers false).</summary>
    public void Complete()
    {
        Volatile.Write(ref _completed, 1);
        _readable.Wake();
    }

    private bool HasRoom => Volatile.Read(ref _tail.Value) - Volatile.Read(ref _head.Value) < _slots.Length;

    // The reader's side ---------------------------------------------------------------------------------------------

    /// <summary>The next item if there is one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out T item)
    {
        var head = _head.Value;
        if (head == _tailSeen.Value)
        {
            _tailSeen.Value = Volatile.Read(ref _tail.Value);
            if (head == _tailSeen.Value)
            {
                item = default!;
                return false;
            }
        }

        ref var slot = ref _slots[(int)(head & _mask)];
        item = slot;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) slot = default!; // (held no longer than needed)
        Volatile.Write(ref _head.Value, head + 1);
        _writable.Wake();
        return true;
    }

    /// <summary>
    ///     Completes once there's an item to read (true) — at once if there is — or the writer has completed and the ring
    ///     is empty (false).
    /// </summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        if (HasItem) return new ValueTask<bool>(true);
        if (Volatile.Read(ref _completed) != 0) return new ValueTask<bool>(HasItem);
        return _readable.WaitAsync(cancellationToken);
    }

    /// <summary>The next item, waiting while the ring is empty; throws once the writer's completed and it's empty.</summary>
    public ValueTask<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        return TryRead(out var item) ? new ValueTask<T>(item) : ReadSlowAsync(cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))] // (a wait's state machine pooled: nothing allocated)
    private async ValueTask<T> ReadSlowAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryRead(out var item)) return item;
            if (!await WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The ring is completed and empty.");
        }
    }

    /// <summary>Every item, in order, until the writer completes and the ring is empty.</summary>
    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            while (TryRead(out var item))
                yield return item;
    }

    private bool HasItem => Volatile.Read(ref _head.Value) != Volatile.Read(ref _tail.Value);
}
