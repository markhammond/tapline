using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;

namespace Tapline;

/// <summary>
///     One party's wait on a single-producer, single-consumer ring (<see cref="SpscRing{T}" />,
///     <see cref="SpscByteRing" />), allocation-free (the source itself, reset each time): it spins a little first — the
///     other party is often only a moment away — then raises the flag, fences, and looks again (<c>ready</c>); the other
///     party, having moved its cursor, fences and takes the flag down. Whoever takes it down completes the wait: the
///     waiter at once, the other party by queuing the waiter to the pool. A wait at a time; it answers <c>answer</c>.
///     <para>
///         A wake says only that the other party moved since the waiter last looked, not that the ring is ready now: the
///         waiter may have taken what was moved, on a look of its own, before raising the flag the wake then found (a
///         wake late for the wait before). So a wake looks first, and finding the ring not ready raises the flag again —
///         the wait goes on, unseen.
///     </para>
/// </summary>
internal sealed class RingWaiter(Func<bool> ready, Func<bool> answer) : IValueTaskSource<bool>, IThreadPoolWorkItem
{
    // Its continuation run off the waker's thread — on the pool, by this very object queued (reused: no work item
    // allocated a wake, as RunContinuationsAsynchronously would): the writer never runs the reader's code, nor back.
    private ManualResetValueTaskSourceCore<bool> _core;
    private CancellationTokenRegistration _cancel;
    private CancellationToken _cancelled;
    private int _waiting;

    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken)
    {
        // A spin first (no yield, no sleep: SpinOnce(-1), stopping where it would yield) — a wake costs a hop
        // through the pool, both ways, when both parties keep pace with each other.
        var spinner = new SpinWait();
        while (!spinner.NextSpinWillYield)
        {
            if (ready()) return new ValueTask<bool>(answer());
            spinner.SpinOnce(-1);
        }

        _core.Reset();
        Interlocked.Exchange(ref _waiting, 1); // (raised, with a full fence, before looking again)
        if (ready())
        {
            if (Interlocked.Exchange(ref _waiting, 0) == 1) return new ValueTask<bool>(answer()); // (no wake owed)
        }
        else if (cancellationToken.CanBeCanceled)
        {
            // (Assigned before the wait's returned, so before any GetResult: a callback at once only queues.)
            _cancel = cancellationToken.UnsafeRegister(static (state, token) =>
            {
                var waiter = (RingWaiter)state!;
                if (Interlocked.Exchange(ref waiter._waiting, 0) != 1) return; // (woken already)
                waiter._cancelled = token;
                ThreadPool.UnsafeQueueUserWorkItem(waiter, false);
            }, this);
        }

        return new ValueTask<bool>(this, _core.Version);
    }

    /// <summary>The other party moved its cursor: wakes the wait, if one's raised (a fence first: see above).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Wake()
    {
        Interlocked.MemoryBarrier();
        WakeFenced();
    }

    /// <summary><see cref="Wake" />, the caller having fenced since it moved its cursor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WakeFenced()
    {
        if (Volatile.Read(ref _waiting) != 0 && Interlocked.Exchange(ref _waiting, 0) == 1)
            ThreadPool.UnsafeQueueUserWorkItem(this, false);
    }

    void IThreadPoolWorkItem.Execute()
    {
        if (!_cancelled.CanBeCanceled)
        {
            if (!ready())
            {
                // A wake for nothing (see above): the flag raised again, and a look again, as the wait itself does.
                Interlocked.Exchange(ref _waiting, 1);
                if (!ready() || Interlocked.Exchange(ref _waiting, 0) != 1) return; // (a wake will come, or has)
            }

            _core.SetResult(true);
            return;
        }

        var token = _cancelled;
        _cancelled = default;
        _core.SetException(new OperationCanceledException(token));
    }

    public bool GetResult(short token)
    {
        try
        {
            _core.GetResult(token);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = default;
        }

        return answer();
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        return _core.GetStatus(token);
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _core.OnCompleted(continuation, state, token, flags);
    }
}

/// <summary>A long on a cache line of its own: 64 bytes of padding either side (a ring's cursor, apart from the other's).</summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
internal struct CacheLine
{
    [FieldOffset(64)] public long Value;
}
