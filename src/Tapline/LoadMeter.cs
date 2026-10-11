using System.Diagnostics;

namespace Tapline;

/// <summary>
///     A data provider's load, measured: the time it spends working over the time that passes, on as many workers as it
///     has, smoothed — what its health reports say (<see cref="HealthReport.Load" />: 0 idle, 1 working all the time,
///     more when work outruns it). Time a unit of work with <see cref="Measure" /> (a scope: <c>using var busy =
///     meter.Measure();</c>), or add a span timed already (<see cref="AddTicks" />), and read <see cref="Load" /> as
///     health checks ask. Each read past the window takes a new sample — the work since the last over the time since —
///     and moves the load halfway to it. Allocation-free; work added from any thread.
/// </summary>
/// <param name="workers">How many work at once (a loop: 1): the load is per worker.</param>
/// <param name="window">How long a sample spans at least (1 s): reads within it answer the load as it was.</param>
public sealed class LoadMeter(int workers = 1, TimeSpan? window = null)
{
    private readonly Lock _lock = new();
    private readonly long _window = (long)((window ?? TimeSpan.FromSeconds(1)).TotalSeconds * Stopwatch.Frequency);
    private readonly double _workers = Math.Max(1, workers);
    private long _busy; // Stopwatch ticks worked since the sample began
    private double _load;
    private bool _sampled;
    private long _since = Stopwatch.GetTimestamp();

    /// <summary>
    ///     The load: the work over the time, per worker, smoothed — 0 idle, 1 working all the time, more when work outruns
    ///     the workers.
    /// </summary>
    public double Load
    {
        get
        {
            lock (_lock)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = now - _since;
                if (elapsed < _window) return _load;
                var sample = Interlocked.Exchange(ref _busy, 0) / (elapsed * _workers);
                (_load, _sampled, _since) = (_sampled ? (_load + sample) / 2 : sample, true, now);
                return _load;
            }
        }
    }

    /// <summary>Times a unit of work, from now until the scope is disposed.</summary>
    public Scope Measure()
    {
        return new Scope(this, Stopwatch.GetTimestamp());
    }

    /// <summary>Adds work timed already, in <see cref="Stopwatch" /> ticks.</summary>
    public void AddTicks(long ticks)
    {
        if (ticks > 0) Interlocked.Add(ref _busy, ticks);
    }

    /// <summary>A unit of work being timed: its time added to the meter when disposed.</summary>
    public readonly struct Scope(LoadMeter meter, long start) : IDisposable
    {
        public void Dispose()
        {
            meter.AddTicks(Stopwatch.GetTimestamp() - start);
        }
    }
}
