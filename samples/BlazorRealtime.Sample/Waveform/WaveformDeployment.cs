using System.Diagnostics;
using Tapline;

namespace BlazorRealtime.Sample.Waveform;

/// <summary>
///     The "waveform" service's stations, blue/green: one on air (<see cref="Current" />); a deploy (
///     <see cref="DeployAsync" />)
///     starts the other colour alongside — caught up to the same carrier clock, so it plays on from where the first is,
///     row
///     for row — and, once it takes subscribers, drains the first: its viewers told to move one at a time, each
///     subscribing
///     again onto the new station and letting the old subscription go once the new one streams (make-before-break), the
///     rest closed when their grace runs out. Then the old one is retired: stopped, and its endpoint withdrawn — with
///     nobody left on it to tell. (The sample's <c>POST /waveform/station/deploy</c>, when enabled.)
/// </summary>
public sealed class WaveformDeployment(Publisher publisher, ILoggerFactory logs, IConfiguration config) : IHostedService
{
    private readonly ILogger _log = logs.CreateLogger<WaveformDeployment>();
    private readonly long _origin = Stopwatch.GetTimestamp(); // carrier zero, for every station of the deployment
    private readonly CancellationTokenSource _stopping = new();
    private WaveformStation? _current, _previous;
    private int _deploying, _deploys;

    /// <summary>The station on air, taking subscribers.</summary>
    public WaveformStation Current => Volatile.Read(ref _current) ??
                                      throw new InvalidOperationException("The deployment hasn't started.");

    /// <summary>The station being drained, while a deploy runs (null otherwise).</summary>
    public WaveformStation? Previous => Volatile.Read(ref _previous);

    /// <summary>Deploys completed.</summary>
    public int Deploys => Volatile.Read(ref _deploys);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var blue = Create("waveform-blue");
        await blue.StartAsync(cancellationToken);
        await blue.Ready;
        Volatile.Write(ref _current, blue);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (Previous is { } previous) await previous.StopAsync(cancellationToken);
        if (Volatile.Read(ref _current) is { } current) await current.StopAsync(cancellationToken);
    }

    private WaveformStation Create(string name)
    {
        return new WaveformStation(publisher, logs.CreateLogger<WaveformStation>(), config, name, _origin);
    }

    /// <summary>
    ///     Deploys the other colour: started alongside and caught up, taking subscribers; then the current one drained — its
    ///     viewers moved over <paramref name="over" />, each given <paramref name="grace" /> to move — and retired. False if a
    ///     deploy is already running.
    /// </summary>
    public async Task<bool> DeployAsync(TimeSpan over, TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _deploying, 1) != 0) return false;
        try
        {
            var old = Current;
            var next = Create(old.Instance.Name == "waveform-blue" ? "waveform-green" : "waveform-blue");
            await next.StartAsync(_stopping.Token);
            await next.Ready; // caught up and registered: the router sends step 1s there
            Volatile.Write(ref _previous, old);
            Volatile.Write(ref _current, next);
            _log.LogInformation("Deploying {Next}: draining {Old} over {Over} s, {Grace} s grace", next.Instance.Name,
                old.Instance.Name, over.TotalSeconds, grace.TotalSeconds);
            await old.DrainAsync(over, grace);
            await Task.Delay(over + grace + TimeSpan.FromSeconds(1), _stopping.Token); // its viewers moved (or let go)
            await old.StopAsync(CancellationToken.None);
            publisher.Withdraw(old.Endpoint); // retired: nobody left on it
            old.Dispose();
            Volatile.Write(ref _previous, null);
            Interlocked.Increment(ref _deploys);
            _log.LogInformation("Deployed {Next}; {Old} retired", next.Instance.Name, old.Instance.Name);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        } // shutting down
        finally
        {
            Volatile.Write(ref _deploying, 0);
        }
    }
}