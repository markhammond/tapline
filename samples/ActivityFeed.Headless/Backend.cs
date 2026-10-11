using System.Globalization;
using ActivityFeed.Contracts;
using System.Net;
using System.Net.Sockets;

namespace ActivityFeed.Headless;

/// <summary>
///     The practice, spawned when no router is named (<c>--stream</c>): its service (the provider, in a process of its
///     own), its app (the router) and its dispatcher (the app's client, for the notifications), each referenced, so built
///     with this client, and run from its own build output (the project's <c>AssemblyMetadata</c> names it). The service
///     on ports this client picks (the app must be told its address), its write log a file of its own (a temporary SQLite
///     database, gone with this client): blue at first, and green, on ports picked as well (the app is told both), started
///     from the same log on demand, for blue/green. The app's stream on a port picked too (each instance hears the log's
///     signal there), its app port the system's pick, its ops endpoints on every port for a stated reason (a
///     non-production demo: Development only, said on every viewer page). Where each listens is read from what it prints
///     once it does. Stopped with this client, and each stops itself if this client dies (its standard input ended:
///     <see cref="Spawning" />).
/// </summary>
internal sealed class Backend : IAsyncDisposable
{
    private const string Reason = "non-production demo: spawned by ActivityFeed.Headless";

    /// <summary>Where each process's whole output is kept (<c>--logs DIR</c>: a file each, appended to), if anywhere.</summary>
    public static string? Logs { get; set; }

    private static readonly string ServicePath = Spawning.PathOf(typeof(Backend).Assembly, "ActivityFeed.Service"),
        AppPath = Spawning.PathOf(typeof(Backend).Assembly, "ActivityFeed.Sample"),
        DispatcherPath = Spawning.PathOf(typeof(Backend).Assembly, "ActivityFeed.Dispatcher");

    private const int Blue = 0, Green = 1;
    private readonly SpawnedProcess _app;
    private readonly string[][] _serviceArgs; // (each instance's, blue's and green's: to start it again as it was)
    private readonly Uri[] _serviceHttp;
    private readonly SpawnedProcess?[] _services = new SpawnedProcess?[2];
    private readonly bool[] _retiring = new bool[2]; // (drained, on purpose: its exit expected)
    private readonly string _log; // (the write log's file: every instance's)
    private SpawnedProcess? _dispatcher;
    private int _current; // (the instance that writes, as the verifier has it)

    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _restarting; // (the service killed on purpose: not the backend gone)

    private Backend(SpawnedProcess service, string[][] serviceArgs, Uri[] serviceHttp, string log, SpawnedProcess app, Uri stream, Uri ops)
    {
        (_services[Blue], _serviceArgs, _serviceHttp, _log, _app, Stream, App) = (service, serviceArgs, serviceHttp, log, app, stream, ops);
        Watch(Blue);
        _ = app.Process.WaitForExitAsync().ContinueWith(_ => _exited.TrySetResult(), TaskScheduler.Default);
    }

    private void Watch(int slot)
    {
        var spawned = _services[slot]!;
        _ = spawned.Process.WaitForExitAsync().ContinueWith(_ =>
        {
            if (!_retiring[slot] && !(slot == _current && _restarting) && ReferenceEquals(_services[slot], spawned))
                _exited.TrySetResult();
        }, TaskScheduler.Default);
    }

    /// <summary>The dispatcher's process id, while it runs.</summary>
    public int? DispatcherPid => _dispatcher?.Process is { HasExited: false } process ? process.Id : null;

    /// <summary>The health port of the instance that writes (blue, until green takes over): its <c>/livez</c>, <c>/readyz</c>, <c>/views</c>…</summary>
    public Uri ServiceHttp => _serviceHttp[_current];

    /// <summary>Blue's health port, and green's.</summary>
    public Uri BlueHttp => _serviceHttp[Blue];

    public Uri GreenHttp => _serviceHttp[Green];

    /// <summary>Whether blue runs (it stops once drained).</summary>
    public bool BlueRuns => _services[Blue]?.Process is { HasExited: false };

    /// <summary>Whether green runs.</summary>
    public bool GreenRuns => _services[Green]?.Process is { HasExited: false };

    /// <summary>The app's stream: Tapline.MagicOnion's, and the practice's step 1 and commands.</summary>
    public Uri Stream { get; }

    /// <summary>The app's port: its out-of-band fetches, and its ops endpoints (the router's viewer, its drain), shared.</summary>
    public Uri App { get; }

    /// <summary>Completes when either exits, but for the service killed on purpose (<see cref="KillServiceAsync" />).</summary>
    public Task Exited => _exited.Task;

    public string Tail => $"service:\n{_services[_current]?.Tail}\napp:\n{_app.Tail}";

    public string Pids => $"service {_services[_current]?.Process.Id}, app {_app.Process.Id}, dispatcher {DispatcherPid}";

    public async ValueTask DisposeAsync()
    {
        if (_dispatcher is not null) await _dispatcher.DisposeAsync();
        await _app.DisposeAsync();
        foreach (var service in _services)
            if (service is not null)
                await service.DisposeAsync();
        var access = ActivityFeed.Service.AccessLogSink.Beside(_log); // (the access log's own database, beside it: the instances' alike)
        foreach (var file in (string[])[_log, _log + "-wal", _log + "-shm", access, access + "-wal", access + "-shm"]) // (this run's alone)
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
    }

    /// <summary>
    ///     Green started, from the same log: it catches up, follows blue, and serves once it has. <paramref name="watchEvery" />:
    ///     hearing no signal (its stream a port nobody listens on), it watches the log's file that often instead.
    /// </summary>
    public async Task StartGreenAsync(CancellationToken cancellationToken, double? watchEvery = null)
    {
        string[] args = watchEvery is { } ms
            ? [.. _serviceArgs[Green], "--Practice:Stream", "http://127.0.0.1:9/", "--Practice:LogPollMs", ms.ToString(CultureInfo.InvariantCulture)]
            : _serviceArgs[Green];
        _services[Green] = await SpawnedProcess.StartAsync(ServicePath, "ACTIVITY-SERVICE ", args, Log(ServicePath), cancellationToken);
        Watch(Green);
    }

    /// <summary>Blue, draining on purpose: its exit expected once it has (green writes from here, as the verifier has it).</summary>
    public void Retire()
    {
        _retiring[Blue] = true;
        _current = Green;
    }

    /// <summary>The dispatcher started (both shards, as dispatcher-1), once it's bound: the app's client.</summary>
    public async Task StartDispatcherAsync(CancellationToken cancellationToken)
    {
        _dispatcher = await SpawnedProcess.StartAsync(DispatcherPath, "ACTIVITY-DISPATCHER ",
            ["--stream", Stream.ToString(), "--app", App.ToString(), "--name", "dispatcher-1"], Log(DispatcherPath), cancellationToken);
    }

    /// <summary>The instance that writes killed, as a crash would (its whole tree: ChalkQL's planner too).</summary>
    public async Task KillServiceAsync()
    {
        _restarting = true;
        await _services[_current]!.KillAsync();
    }

    /// <summary>It started again on its ports: a new instance, replaying the write log (the same practice, carried on).</summary>
    public async Task StartServiceAsync(CancellationToken cancellationToken)
    {
        _services[_current] = await SpawnedProcess.StartAsync(ServicePath, "ACTIVITY-SERVICE ", _serviceArgs[_current], Log(ServicePath),
            cancellationToken);
        Watch(_current);
        _restarting = false;
    }

    /// <summary>The dispatcher killed, as a crash would: what it had in hand is in its topics still.</summary>
    public async Task KillDispatcherAsync()
    {
        if (_dispatcher is null) return;
        await _dispatcher.KillAsync();
        _dispatcher = null;
    }

    /// <summary>The demo's settings, for the verifier.</summary>
    private static readonly string[] Demo =
    [
        "--Practice:BreakGlassSeconds", "12", // (the demo's emergency access: long enough to see, short enough to end)
        "--Practice:SeenGraceSeconds", "5", // (how long a notification waits for the app: the demo's, short)
        "--Practice:ReleaseSeconds", "5", // (the release job: every few seconds, for the demo)
        "--Practice:QuietHours", "none", // (so the demo runs the same at any hour)
        "--Practice:Faults", Reason, // (fault injection: a site falling behind, for the demo)
        "--Practice:OracleSeconds", "2", // (the sampled oracle: a topic every two seconds, through the demo)
        "--Practice:OrderHoldSeconds", "3" // (a command come before its turn: held three seconds for those before it, not eight)
    ];

    /// <summary>
    ///     Measuring's: the practice quicker, measuring itself, and nothing run beside it (no sampled oracle); each view
    ///     incremental alone, or (twins) checked against its recomputed twin at every update, as the sample's default.
    /// </summary>
    private static readonly string[] Measuring =
    [
        "--Practice:Measure", "true",
        "--Practice:PerMinute", "120", // (an activity every half second, or none: both sites together)
        "--Practice:QuietHours", "none", // (its notifications sent at any hour)
        "--Practice:OracleSeconds", "0"
    ];

    /// <summary>The service, then the app (once the service listens), started — or why not. Measuring: as it's measured.</summary>
    public static async Task<Backend> StartAsync(bool measure, bool twins, double? history, CancellationToken cancellationToken)
    {
        int[] grpc = [FreePort(), FreePort()], http = [FreePort(), FreePort()]; // (blue's, and green's, should it start)
        var stream = FreePort(); // (the app's stream: the services hear the log's signal there, so it's picked here too)
        var log = Path.Combine(Path.GetTempPath(), $"activity-feed-{Environment.ProcessId}-{Guid.NewGuid():N}.db");
        string[] Args(int slot) =>
        [
            "--GrpcPort", grpc[slot].ToString(CultureInfo.InvariantCulture), "--HttpPort",
            http[slot].ToString(CultureInfo.InvariantCulture), "--environment", "Development",
            "--Practice:Log", log, "--Practice:Name", slot == Blue ? "practice-blue" : "practice-green",
            "--Practice:Stream", $"http://127.0.0.1:{stream}/",
            .. measure ? [.. Measuring, "--Practice:Validate", twins ? "true" : "false"] : Demo,
            .. history is { } minutes ? ["--Practice:HistoryMinutes", minutes.ToString(CultureInfo.InvariantCulture)] : (string[])[]
        ];
        string[][] serviceArgs = [Args(Blue), Args(Green)];
        Uri[] serviceHttp = [new($"http://127.0.0.1:{http[Blue]}/"), new($"http://127.0.0.1:{http[Green]}/")];
        var service = await SpawnedProcess.StartAsync(ServicePath, "ACTIVITY-SERVICE ", serviceArgs[Blue], Log(ServicePath), cancellationToken);
        try
        {
            var app = await SpawnedProcess.StartAsync(AppPath, "ACTIVITY-SAMPLE ",
            [
                "--urls", "http://127.0.0.1:0", "--StreamPort", stream.ToString(CultureInfo.InvariantCulture), "--SharedOps", Reason,
                "--Practice", $"{service.Where["grpc"]},http://127.0.0.1:{grpc[Green]}/", "--environment", "Development",
                .. measure ? ["--Measure", "true"] : (string[])[] // (its collections heard, for the measurer)
            ], Log(AppPath), cancellationToken);
            var backend = new Backend(service, serviceArgs, serviceHttp, log, app, app.Where["stream"], app.Where["app"]);
            try
            {
                await backend.StartDispatcherAsync(cancellationToken);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }

            return backend;
        }
        catch
        {
            await service.DisposeAsync();
            throw;
        }
    }

    /// <summary>Where a process's whole output goes (<c>--logs DIR</c>: a file of its own, appended to), if anywhere.</summary>
    private static Action<string>? Log(string path)
    {
        if (Logs is not { } logs) return null;
        var log = TextWriter.Synchronized(new StreamWriter(Path.Combine(logs, $"{Path.GetFileNameWithoutExtension(path)}.log"), true)
            { AutoFlush = true });
        log.WriteLine($"--- {DateTime.Now:O}: started");
        return log.WriteLine;
    }

    /// <summary>A port free now (the system's pick, let go): the service is told it.</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
