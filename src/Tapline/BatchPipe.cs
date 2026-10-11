using System.Diagnostics;

namespace Tapline;

/// <summary>
///     A fixed set of reusable batches going round between a producer that mustn't lose or conflate anything — a standing
///     view publishing every version, each drained into a batch of its own — and a reader at its own pace: posting each to
///     an ingest, sending it, logging it. The producer rents a free batch (<see cref="RentAsync" />), fills it and
///     <see cref="Publish" />es it (or, nothing to send, <see cref="Unrent" />s it); the reader reads them in order
///     (<see cref="ReadAsync" />) and <see cref="Return" />s each once done — over two <see cref="SpscRing{T}" />s, no
///     lock. The producer waits only when every batch is still with the reader: so the pipe absorbs a reader's
///     jitter, as many batches deep as it has, and past that holds the producer back (back-pressure — counted:
///     <see cref="Waits" />, <see cref="WaitedMs" />) rather than buffering without end. A batch keeps whatever room it
///     grew to: nothing is allocated going round. One producer and one reader.
/// </summary>
public sealed class BatchPipe<TBatch> where TBatch : class
{
    // Two single-producer, single-consumer rings, no lock: the reader returns free batches on one, the producer publishes
    // full ones on the other. A batch the producer rented and didn't publish it keeps (its own side's: SPSC holds).
    private readonly SpscRing<TBatch> _free, _full;
    private TBatch? _spare;
    private long _waits, _waitedTicks;
    private int _inFlight, _maxInFlight;

    /// <param name="batches">How many go round (at least one): how far the producer may run ahead of the reader.</param>
    /// <param name="make">A new batch, empty: made <paramref name="batches" /> times, at once.</param>
    public BatchPipe(int batches, Func<TBatch> make)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batches, 1);
        ArgumentNullException.ThrowIfNull(make);
        Capacity = batches;
        var slots = (int)Math.Max(2, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)batches));
        (_free, _full) = (new SpscRing<TBatch>(slots), new SpscRing<TBatch>(slots));
        for (var i = 0; i < batches; i++) _free.TryWrite(make());
    }

    /// <summary>How many batches go round.</summary>
    public int Capacity { get; }

    /// <summary>Batches published and not yet returned: how far the reader is behind, now.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>The most ever in flight at once.</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Times the producer waited for a free batch: every one with the reader.</summary>
    public long Waits => Volatile.Read(ref _waits);

    /// <summary>How long the producer waited for free batches, in all (ms).</summary>
    public double WaitedMs => (double)Volatile.Read(ref _waitedTicks) * 1000 / Stopwatch.Frequency;

    /// <summary>
    ///     A free batch, as the reader returned it (to be cleared, or filled over, by the producer) — at once if one's free,
    ///     else once the reader returns one. The producer's.
    /// </summary>
    public ValueTask<TBatch> RentAsync(CancellationToken cancellationToken = default)
    {
        if (_spare is { } spare)
        {
            _spare = null;
            return new ValueTask<TBatch>(spare);
        }

        return _free.TryRead(out var batch) ? new ValueTask<TBatch>(batch) : WaitAsync(cancellationToken);
    }

    [System.Runtime.CompilerServices.AsyncMethodBuilder(
        typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<TBatch> WaitAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waits);
        var started = Stopwatch.GetTimestamp();
        try
        {
            TBatch? batch;
            while (!_free.TryRead(out batch)) // (the free ring is never completed: a wait ends with a batch, or cancelled)
                await _free.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
            return batch;
        }
        finally
        {
            Interlocked.Add(ref _waitedTicks, Stopwatch.GetTimestamp() - started);
        }
    }

    /// <summary>A rented batch, filled: to the reader, after those published before it. The producer's.</summary>
    public void Publish(TBatch batch)
    {
        var inFlight = Interlocked.Increment(ref _inFlight);
        for (int max; inFlight > (max = Volatile.Read(ref _maxInFlight)) &&
                      Interlocked.CompareExchange(ref _maxInFlight, inFlight, max) != max;)
        {
        }

        if (!_full.TryWrite(batch)) // (never full: there are no more batches than room for them)
            throw new InvalidOperationException("Published a batch the pipe doesn't hold.");
    }

    /// <summary>A rented batch the producer didn't publish (nothing to send): kept for its next rent. The producer's.</summary>
    public void Unrent(TBatch batch)
    {
        if (_spare is not null) throw new InvalidOperationException("One rented batch unpublished at a time.");
        _spare = batch;
    }

    /// <summary>A published batch, done with: free for the producer again. The reader's.</summary>
    public void Return(TBatch batch)
    {
        Interlocked.Decrement(ref _inFlight);
        _free.TryWrite(batch);
    }

    /// <summary>The next published batch, in order: at once if one's waiting, else when the producer publishes one.</summary>
    public ValueTask<TBatch> ReadAsync(CancellationToken cancellationToken = default)
    {
        return _full.ReadAsync(cancellationToken);
    }

    /// <summary>The next published batch if one's waiting.</summary>
    public bool TryRead(out TBatch batch)
    {
        return _full.TryRead(out batch!);
    }

    /// <summary>The published batches, in order, until the producer completes (<see cref="Complete" />).</summary>
    public IAsyncEnumerable<TBatch> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        return _full.ReadAllAsync(cancellationToken);
    }

    /// <summary>The producer's done: the reader reads what's published, then its reading ends.</summary>
    public void Complete()
    {
        _full.Complete();
    }
}
