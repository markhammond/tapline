using ActivityFeed.Contracts;

namespace ActivityFeed.Sample;

/// <summary>
///     The practice's other processes, spawned when this app is run on its own (no <c>--Practice</c> named): the
///     practice service on its own ports (5960, 5961) at once, and the dispatcher on this app's stream once the app
///     listens. Each is built with the app and run from its own build output (the project's <c>AssemblyMetadata</c> names
///     it), its output this app's, each line marked with whose. Each stops when its standard input ends: as this app
///     begins to stop (the dispatcher's stream to it closed, so the app's server isn't kept waiting for it), or dies.
/// </summary>
internal sealed class PracticeProcesses(IHostApplicationLifetime lifetime, IHostEnvironment environment,
    Func<(Uri App, Uri Stream)> listening, ILogger<PracticeProcesses> log) : BackgroundService
{
    private SpawnedProcess? _service, _dispatcher;
    private Task? _stopped;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStopping.Register(() => _ = Stopped());
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var name = "ActivityFeed.Service";
        try
        {
            _service = await SpawnedProcess.StartAsync(Spawning.PathOf(typeof(PracticeProcesses).Assembly, name),
                "ACTIVITY-SERVICE ", ["--environment", environment.EnvironmentName], Echo("service"), stop);
            log.LogInformation("Practice: the service spawned, at {Grpc} (its health at {Http})", _service.Where["grpc"],
                _service.Where["http"]);
            await Started(stop);
            name = "ActivityFeed.Dispatcher";
            var (app, stream) = listening();
            _dispatcher = await SpawnedProcess.StartAsync(Spawning.PathOf(typeof(PracticeProcesses).Assembly, name),
                "ACTIVITY-DISPATCHER ", ["--stream", stream.ToString(), "--app", app.ToString()], Echo("dispatcher"), stop);
            log.LogInformation("Practice: the dispatcher spawned, on {Stream}", stream);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException e) // (its port taken, say: a service already running, which --Practice names)
        {
            log.LogWarning("Practice: {Name} didn't start: {Why}", name, e.Message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await Stopped();
    }

    /// <summary>Both stopped, at once (each stops itself, its input closed), once: as the app begins to stop.</summary>
    private Task Stopped()
    {
        lock (this)
        {
            return _stopped ??= Task.WhenAll(Stop(_dispatcher), Stop(_service));
        }

        static Task Stop(SpawnedProcess? spawned) => spawned?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    }

    /// <summary>Once the app listens (its stream bound: the dispatcher's to reach).</summary>
    private Task Started(CancellationToken stop)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        return started.Task.WaitAsync(stop);
    }

    private static Action<string> Echo(string whose)
    {
        var mark = $"{whose,-10} | ";
        return line => Console.Out.WriteLine(mark + line);
    }
}
