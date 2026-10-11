using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace ActivityFeed.Contracts;

/// <summary>
///     The practice's processes, one spawning another (the app its service and dispatcher; the headless all three), each
///     tied to its parent by its standard input: a child spawned (<c>--Spawned true</c>) stops once its input ends, which it
///     does when the parent closes it or dies, however it dies. Each says where it listens in a line of its own once it
///     does (<c>ACTIVITY-SERVICE grpc=… http=…</c>), which its parent waits for.
/// </summary>
public static class Spawning
{
    /// <summary>
    ///     Spawned: <paramref name="stop" /> once this process's standard input ends (its parent gone). Its input is the
    ///     parent's pipe, read on a thread of its own; nothing is ever written to it.
    /// </summary>
    public static void StopWhenParentGoes(Action stop)
    {
        new Thread(() =>
        {
            using var input = Console.OpenStandardInput();
            var buffer = new byte[64];
            try
            {
                while (input.Read(buffer) > 0)
                {
                }
            }
            catch (IOException)
            {
            } // (the pipe broken: gone all the same)

            stop();
        }) { IsBackground = true, Name = "parent" }.Start();
    }

    /// <summary>
    ///     A process of the practice's, by name, as the spawner's build has it (its project's <c>AssemblyMetadata</c>, the
    ///     process's own build output: run from there, by its own runtime configuration).
    /// </summary>
    public static string PathOf(Assembly spawner, string name)
    {
        return spawner.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == name)?.Value is { } path
            ? Path.GetFullPath(path)
            : throw new InvalidOperationException($"{spawner.GetName().Name} names no {name} to spawn.");
    }
}

/// <summary>A process spawned (<see cref="Spawning" />): its output read as it comes, the line saying where it listens once.</summary>
public sealed class SpawnedProcess : IAsyncDisposable
{
    private const int Kept = 40; // its output's last lines: for why, if it exits
    private readonly Queue<string> _tail;

    private SpawnedProcess(Process process, Dictionary<string, Uri> where, Queue<string> tail)
    {
        (Process, Where, _tail) = (process, where, tail);
    }

    public Process Process { get; }

    /// <summary>Where it listens, by name, from its ready line (<c>grpc</c>, <c>http</c>, <c>app</c>, <c>stream</c>…).</summary>
    public Dictionary<string, Uri> Where { get; }

    public string Tail
    {
        get
        {
            lock (_tail)
            {
                return string.Join('\n', _tail);
            }
        }
    }

    /// <summary>Stopped as it would be (its input closed: it stops itself), or killed if it hasn't within ten seconds.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            Process.StandardInput.Close();
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (InvalidOperationException)
        {
        } // (gone meanwhile)

        await KillAsync();
    }

    /// <summary>Killed, as a crash would: its whole tree (the service's ChalkQL planner too).</summary>
    public async Task KillAsync()
    {
        try
        {
            if (!Process.HasExited) Process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        } // (gone meanwhile)

        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    ///     <paramref name="path" /> started (<c>dotnet exec</c>, with <c>--Spawned true</c>), once it says where it listens
    ///     (its line starting <paramref name="ready" />). Each other line of its output goes to <paramref name="line" />, if
    ///     given. Or why not: it exited first, or didn't listen within two minutes (the service starts ChalkQL's planner,
    ///     and primes its history).
    /// </summary>
    public static async Task<SpawnedProcess> StartAsync(string path, string ready, IEnumerable<string> args,
        Action<string>? line, CancellationToken cancellationToken)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(path);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--Spawned");
        start.ArgumentList.Add("true");
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{name} didn't start.");
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"{name} didn't start: {e.Message}", e);
        }

        var where = new TaskCompletionSource<Dictionary<string, Uri>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new Queue<string>();
        _ = ReadAsync(process.StandardOutput, tail, ready, where, line);
        _ = ReadAsync(process.StandardError, tail, null, null, line);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        var spawned = new SpawnedProcess(process, [], tail);
        try
        {
            var first = await Task.WhenAny(where.Task, process.WaitForExitAsync(limit.Token));
            if (first != where.Task)
            {
                await first; // (cancelled, or timed out: thrown)
                throw new InvalidOperationException($"{name} exited ({process.ExitCode}) before it listened\n{spawned.Tail}");
            }

            return new SpawnedProcess(process, await where.Task, tail);
        }
        catch
        {
            await spawned.KillAsync();
            throw;
        }
    }

    private static async Task ReadAsync(StreamReader output, Queue<string> tail, string? ready,
        TaskCompletionSource<Dictionary<string, Uri>>? where, Action<string>? line)
    {
        while (await output.ReadLineAsync() is { } text)
        {
            line?.Invoke(text);
            if (ready is not null && text.StartsWith(ready, StringComparison.Ordinal))
            {
                where!.TrySetResult(text[ready.Length..].Split(' ').Select(p => p.Split('=', 2))
                    .ToDictionary(p => p[0], p => new Uri(p[1].EndsWith('/') ? p[1] : p[1] + "/")));
                continue;
            }

            lock (tail)
            {
                tail.Enqueue(text);
                while (tail.Count > Kept) tail.Dequeue();
            }
        }
    }
}
