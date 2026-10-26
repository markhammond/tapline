using System.Threading.Tasks.Sources;

namespace Tapline.Grid;

/// <summary>
///     A session loop's wake: auto-reset, one waiter, nothing allocated (no TaskCompletionSource), the waiter's
///     continuation queued to the pool as this very object, so whoever wakes it never runs the loop. (As Tapline's own
///     Signal, which is internal to its core.)
/// </summary>
internal sealed class Wake : IValueTaskSource, IThreadPoolWorkItem
{
    private ManualResetValueTaskSourceCore<bool> _core;
    private int _state; // 0 idle, 1 set, 2 waiting

    void IThreadPoolWorkItem.Execute()
    {
        _core.SetResult(true);
    }

    void IValueTaskSource.GetResult(short token)
    {
        _core.GetResult(token);
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
    {
        return _core.GetStatus(token);
    }

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _core.OnCompleted(continuation, state, token, flags);
    }

    /// <summary>Completes once set — at once, if it has been since the last wait.</summary>
    public ValueTask WaitAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 0, 1) == 1) return default; // (a set waiting: taken)
        _core.Reset();
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) return new ValueTask(this, _core.Version);
        Volatile.Write(ref _state, 0); // (raced with a set: taken)
        return default;
    }

    /// <summary>Wakes the waiter, or the next wait.</summary>
    public void Set()
    {
        while (true)
        {
            var s = Volatile.Read(ref _state);
            if (s == 1) return;
            if (s == 0)
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) == 0) return;
                continue;
            }

            if (Interlocked.CompareExchange(ref _state, 0, 2) == 2)
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, false);
                return;
            }
        }
    }
}
