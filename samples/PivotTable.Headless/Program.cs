// PivotTable.Headless — the pivot sample's positions streamed to a console: Tapline.Client over MagicOnion (gRPC), by
// the sample's IPivotService. A statement — the one a page's address asks for (paste one from the browser), or one of
// your own — described first: its shape, the cells' labels and figures field by field, read from the app before
// anything streams; then its step 1. The cells are read by that shape alone: nothing of them compiled in. Prints each
// batch of cells as it's applied, and once a second what came: batches, cells, the rate, and how long the answers took
// from the provider's post to this client (its clock cell — the provider's clock and this one: the same machine's, here).
//
//   dotnet run --project samples/PivotTable.Headless                 a backend of its own, spawned; the default pivot
//   dotnet run --project samples/PivotTable.Headless -- --help       the options
//
// Its parts: Options (the command line, declared — PowerArgs — and its help), Backend (the pivot sample, spawned when no
// router is named), Endpoints (where it streams from), Described (the statement and its shape), PivotRun (the pivot
// streamed: routers, subscriptions, moves, drops, drains) and Watch (what it prints, and what fails a verification).

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using PivotTable.Headless;

var options = Options.Parse(args, out var refused);
if (options is null) return refused;
Console.OutputEncoding = Encoding.UTF8;
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16)
    { AutoFlush = false }); // written as it comes, flushed apart

using var stop = new CancellationTokenSource();

void Stop(PosixSignalContext signal)
{
    signal.Cancel = true;
    stop.Cancel();
} // Ctrl+C, or a kill: its summary printed, its backend stopped

using var interrupted = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminated = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

// Its routers: those named — or a backend of its own, spawned, where it listens echoed.
Backend? backend = null;
if (options.Stream is null)
{
    Console.Out.WriteLine("backend: starting the pivot sample (no --stream named)…");
    Console.Out.Flush();
    try
    {
        backend = await Backend.StartAsync(stop.Token);
    }
    catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or Win32Exception)
    {
        Console.Error.WriteLine($"backend: {e.Message}");
        return 1;
    }

    Console.Out.WriteLine(
        $"backend: the pivot sample (pid {backend.Pid}) — the page {backend.App}, the router's viewer {new Uri(backend.App, "ops/realtime")}, " +
        $"the stream {backend.Stream}");
    _ = backend.Exited.ContinueWith(async _ => // gone under it: said, with why — and the run over
    {
        await Task.Delay(250); // (Ctrl+C reaches it too: stopping, not gone)
        if (stop.IsCancellationRequested) return;
        Console.Error.WriteLine($"backend: exited\n{backend.Tail}");
        stop.Cancel();
    }, TaskScheduler.Default);
}

await using var spawned = backend;
var endpoints = backend is not null ? Endpoints.Of(backend) : Endpoints.Of(options);
var channels = endpoints.Streams.Select(s => GrpcChannel.ForAddress(s)).ToArray();
try
{
    if (await Described.AskAsync(options, channels[0], stop.Token) is not { } described) return 1;
    described.Print(options);
    if (options.Verify is { } run)
        stop.CancelAfter(run.Value); // (from now: a backend's start and the statement's prepare not counted)
    return await new PivotRun(options, endpoints, channels, described, stop).RunAsync();
}
catch (OperationCanceledException)
{
    return 1;
}
catch (RpcException e) // (reached, and refusing the pivot's calls: another app, an older build)
{
    Console.Error.WriteLine(
        $"{endpoints.Streams[0]} doesn't answer the pivot's calls: {e.Status.StatusCode} — {e.Status.Detail}");
    return 1;
}
finally
{
    stop.Cancel(); // (done: a backend stopping now is no news)
    foreach (var channel in channels) channel.Dispose();
    Console.Out.Flush();
}