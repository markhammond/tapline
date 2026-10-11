using System.Threading.Tasks.Sources;

namespace Tapline;

/// <summary>
///     Auto-reset, single-waiter async event. Zero allocation: no TaskCompletionSource, and the wake-up hops to the
///     thread pool as an <see cref="IThreadPoolWorkItem" /> so a publisher thread never runs the waiter's continuation.
/// </summary>
internal sealed class Signal : IValueTaskSource, IThreadPoolWorkItem
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

    public ValueTask WaitAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 0, 1) == 1) return default; // consume a pending set
        _core.Reset();
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) return new ValueTask(this, _core.Version);
        Volatile.Write(ref _state, 0); // raced with Set: consume it
        return default;
    }

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