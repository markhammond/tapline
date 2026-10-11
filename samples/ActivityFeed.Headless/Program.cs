// ActivityFeed.Headless — the practice's feeds streamed to a console: Tapline.Client over MagicOnion (gRPC), by the
// app's IPracticeFeed. Every persona's feed on one session, each checked against the policy as it comes; then the demo
// (Jane Smith checked in and called), each command answered once its change is in front of whoever gave it; then a
// line a second.
//
//   dotnet run --project samples/ActivityFeed.Headless                        the practice spawned (service, then app)
//   dotnet run --project samples/ActivityFeed.Headless -- --verify 30         that, and exit 0 if nothing failed in 30 s
//   dotnet run -c Release --project samples/ActivityFeed.Headless -- --measure 120
//                                                                            the practice spawned quicker, and measured
//                                                                            (no demo): step 1, two minutes' staleness,
//                                                                            the provider's hop, step 1 again; each view
//                                                                            incremental alone (--twins: each checked
//                                                                            against its recomputed twin, the default's);
//                                                                            --history MINUTES: primed with that long's
//                                                                            history (90 by default, at its pace);
//                                                                            --green: green started once the feeds are
//                                                                            open, following blue through the window (its
//                                                                            replica lag: by the log's signal, or, with
//                                                                            --green-poll MS, by watching the log's file
//                                                                            every MS instead)
//   dotnet run --project samples/ActivityFeed.Headless -- --stream URL        an app already running (its stream port;
//                                                                            --app URL: its app port, if not two below)
//   --logs DIR                                                               each spawned process's output kept there
//   --quiet                                                                  no line a second

using System.Runtime.InteropServices;
using System.Text;
using ActivityFeed.Headless;
using Grpc.Core;
using Grpc.Net.Client;

string? stream = null, app = null;
TimeSpan? verify = null, measure = null;
bool quiet = false, twins = false, green = false;
double? history = null, greenPoll = null;
for (var i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--stream" when i + 1 < args.Length:
            stream = args[++i];
            break;
        case "--app" when i + 1 < args.Length:
            app = args[++i];
            break;
        case "--verify" when i + 1 < args.Length && double.TryParse(args[i + 1], out var seconds) && seconds > 0:
            verify = TimeSpan.FromSeconds(seconds);
            i++;
            break;
        case "--measure" when i + 1 < args.Length && double.TryParse(args[i + 1], out var window) && window > 0:
            measure = TimeSpan.FromSeconds(window);
            i++;
            break;
        case "--logs" when i + 1 < args.Length:
            Backend.Logs = Directory.CreateDirectory(args[++i]).FullName;
            break;
        case "--history" when i + 1 < args.Length && double.TryParse(args[i + 1], out var minutes) && minutes > 0:
            history = minutes;
            i++;
            break;
        case "--twins":
            twins = true;
            break;
        case "--green":
            green = true;
            break;
        case "--green-poll" when i + 1 < args.Length && double.TryParse(args[i + 1], out var poll) && poll > 0:
            (green, greenPoll) = (true, poll);
            i++;
            break;
        case "--quiet":
            quiet = true;
            break;
        default:
            Console.Error.WriteLine($"unknown: {args[i]}\nusage: [--stream URL [--app URL]] [--verify SECONDS | --measure SECONDS [--twins] [--history MINUTES] [--green | --green-poll MS]] [--logs DIR] [--quiet]");
            return 2;
    }

Console.OutputEncoding = Encoding.UTF8;
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16) { AutoFlush = false });
using var stop = new CancellationTokenSource();

void Stop(PosixSignalContext signal)
{
    signal.Cancel = true;
    stop.Cancel();
} // Ctrl+C, or a kill: its summary printed, its backend stopped

using var interrupted = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminated = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

Backend? backend = null;
if (stream is null)
{
    Console.Out.WriteLine("backend: starting the practice service, then the app (no --stream named)…");
    Console.Out.Flush();
    try
    {
        backend = await Backend.StartAsync(measure is not null, twins, history, stop.Token);
    }
    catch (Exception e) when (e is InvalidOperationException or OperationCanceledException)
    {
        Console.Error.WriteLine($"backend: {e.Message}");
        return 1;
    }

    Console.Out.WriteLine($"backend: the practice ({backend.Pids}) — the stream {backend.Stream}, the router's viewer " +
                          $"{new Uri(backend.App, "ops/realtime")}");
    _ = backend.Exited.ContinueWith(async _ =>
    {
        await Task.Delay(250); // (Ctrl+C reaches them too: stopping, not gone)
        if (stop.IsCancellationRequested) return;
        Console.Error.WriteLine($"backend: exited\n{backend.Tail}");
        stop.Cancel();
    }, TaskScheduler.Default);
}

await using var spawned = backend;
var streamUri = backend?.Stream ?? new Uri(stream!);
// The app's port: given, or as the app lays its ports out (two below the stream's) — its out-of-band fetches there.
var appUri = backend?.App ?? (app is not null ? new Uri(app) : new UriBuilder(streamUri) { Port = streamUri.Port - 2, Path = "/" }.Uri);
using var channel = GrpcChannel.ForAddress(streamUri);
using var http = new HttpClient(); // (out-of-band fetches: a connection of their own)
try
{
    return measure is { } measuring
        ? await new Measurer(channel, appUri, http, backend?.ServiceHttp, green ? backend : null, greenPoll).RunAsync(measuring, stop.Token)
        : await new Verifier(channel, appUri, http, backend is not null, quiet, backend?.ServiceHttp, backend).RunAsync(verify, stop.Token);
}
catch (RpcException e) // (reached, and refusing the practice's calls: another app, an older build)
{
    Console.Error.WriteLine($"{channel.Target} doesn't answer the practice's calls: {e.Status.StatusCode} — {e.Status.Detail}");
    return 1;
}
finally
{
    stop.Cancel();
    Console.Out.Flush();
}
