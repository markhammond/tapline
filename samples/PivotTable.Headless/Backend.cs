using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using PivotTable.Sample;

namespace PivotTable.Headless;

/// <summary>
///     The pivot sample, spawned when no router is named (<c>--stream</c>): referenced, so built with this client and
///     copied beside it, and found by a type of its own (<see cref="Sample" />) — run from there, on ports the system
///     picks: the page, the router's viewer and the out-of-band fetches on one, the stream (gRPC: HTTP/2 alone) on
///     another, as a port without TLS can't take both; its ops endpoints on both, for a stated reason (a non-production
///     demo: Development only, said on every viewer page). Where it listens is read from what it prints once it does.
///     Stopped with this client — and it stops itself if this client dies.
/// </summary>
internal sealed class Backend : IAsyncDisposable
{
    private const string Ready = "PIVOT-SAMPLE ", Reason = "non-production demo: spawned by PivotTable.Headless";
    private const int Kept = 40; // its output's last lines: for why, if it exits

    /// <summary>The pivot sample's assembly, beside this client's: found by one of its types, nothing of it used.</summary>
    private static readonly Assembly Sample = typeof(Pivot).Assembly;

    private readonly Process _process;
    private readonly Queue<string> _tail; // its output's last lines, read as it comes all along

    private Backend(Process process, Uri app, Uri stream, Queue<string> tail)
    {
        (_process, App, Stream, _tail) = (process, app, stream, tail);
    }

    /// <summary>Its app's port: the page, the router's viewer (<c>/ops/realtime</c>), the out-of-band fetches.</summary>
    public Uri App { get; }

    /// <summary>Its stream's: Tapline.MagicOnion's, and the pivot's step 1.</summary>
    public Uri Stream { get; }

    public int Pid => _process.Id;

    /// <summary>Completes when it exits.</summary>
    public Task Exited => _process.WaitForExitAsync();

    /// <summary>Its output's last lines.</summary>
    public string Tail => Lines(_tail);

    /// <summary>Stops it — its whole tree: the app, and what it started (ChalkQL's planner).</summary>
    public async ValueTask DisposeAsync()
    {
        Stop(_process);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _process.Dispose();
    }

    /// <summary>The pivot sample started — once it says where it listens (within a minute) — or why not.</summary>
    public static async Task<Backend> StartAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false
        };
        foreach (var arg in (string[])
                 [
                     "exec", Sample.Location,
                     "--urls", "http://127.0.0.1:0", "--StreamPort", "0", "--SharedOps", Reason, "--ParentPid",
                     Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                     "--environment", "Development"
                 ])
            start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException($"{Sample.GetName().Name} didn't start.");
        var where = new TaskCompletionSource<(Uri App, Uri Stream)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new Queue<string>();
        _ = ReadAsync(process.StandardOutput, lines,
            where); // (read as it comes, all along: a full pipe would hold it up)
        _ = ReadAsync(process.StandardError, lines, null);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            var first = await Task.WhenAny(where.Task, process.WaitForExitAsync(limit.Token));
            if (first != where.Task)
            {
                await first; // (cancelled, or timed out: thrown)
                throw new InvalidOperationException(
                    $"{Sample.GetName().Name} exited ({process.ExitCode}) before it listened ({Sample.Location})\n{Lines(lines)}");
            }

            var (app, stream) = await where.Task;
            return new Backend(process, app, stream, lines);
        }
        catch
        {
            Stop(process);
            throw;
        }
    }

    /// <summary>Its output, line by line, kept (the last few) — and the line saying where it listens, once.</summary>
    private static async Task ReadAsync(StreamReader output, Queue<string> lines,
        TaskCompletionSource<(Uri, Uri)>? where)
    {
        while (await output.ReadLineAsync() is { } line)
        {
            if (where is not null && line.StartsWith(Ready, StringComparison.Ordinal))
            {
                var at = line[Ready.Length..].Split(' ').Select(p => p.Split('=', 2))
                    .ToDictionary(p => p[0], p => new Uri(p[1] + "/"));
                where.TrySetResult((at["app"], at["stream"]));
                continue;
            }

            lock (lines)
            {
                lines.Enqueue(line);
                while (lines.Count > Kept) lines.Dequeue();
            }
        }
    }

    private static string Lines(Queue<string> lines)
    {
        lock (lines)
        {
            return string.Join('\n', lines);
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        } // (gone meanwhile)
    }
}