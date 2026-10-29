using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace LiveGrid.Sample.Postgres;

/// <summary>
///     The database server — lifted from the ChalkQL blotter (<c>samples/Chalk.Sample.Blotter/Postgres/PgServer.cs</c>,
///     ChalkQL's repository), its names this sample's: one the sample starts for itself, in a directory of its own, from a
///     PostgreSQL installation on this machine; or one it is given a connection string to. Started, it
///     is a throwaway: its data never outlives the sample, so it runs without the durability a real one
///     keeps, and is deleted when the sample stops.
/// </summary>
public sealed class PgServer : IAsyncDisposable
{
    private const string Prefix = "livegrid-pg-";
    private const string Owner = "owner.pid";
    private readonly string? _binaries;
    private readonly string? _directory;
    private bool _madeSchema;

    private PgServer(string connectionString, string? directory, string? binaries)
    {
        ConnectionString = connectionString;
        _directory = directory;
        _binaries = binaries;
    }

    /// <summary>The <c>livegrid</c> database's connection string, its tables found in the <c>livegrid</c> schema.</summary>
    public string ConnectionString { get; }

    /// <summary>Whether the sample started this server, and will stop it.</summary>
    public bool Owned => _directory is not null;

    /// <summary>
    ///     Stops a server the sample started, and deletes it. On a server it was given, drops the schema it
    ///     made there, and nothing else.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_madeSchema)
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var drop = new NpgsqlCommand("DROP SCHEMA livegrid CASCADE", connection);
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
            return;
        }

        if (_directory is null || _binaries is null) return;

        NpgsqlConnection.ClearAllPools();
        try
        {
            if (File.Exists(Path.Combine(_directory, "postmaster.pid")))
                await RunAsync(Path.Combine(_binaries, "pg_ctl"), CancellationToken.None, "-D", _directory, "-m",
                        "immediate", "-w", "stop")
                    .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(_directory, true);
            }
            catch (IOException)
            {
                // Best effort: it is under the machine's temp root, which is swept anyway.
            }
        }
    }

    /// <summary>Whether a server can be started here: a PostgreSQL installation is found, or named and there.</summary>
    public static bool CanStart(string? binaries = null) => binaries is null ? FindBinaries() is not null : File.Exists(Path.Combine(binaries, "pg_ctl"));

    /// <summary>
    ///     A server: the one <paramref name="connectionString" /> names, if given; otherwise one started from
    ///     <paramref name="binaries" />, or from the first PostgreSQL 16 or later found on this machine.
    /// </summary>
    public static async Task<PgServer> StartAsync(string? connectionString, string? binaries, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var given = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = "livegrid" };
            await PrepareAsync(given, ct).ConfigureAwait(false);
            return new PgServer(given.ConnectionString, null, null) { _madeSchema = true };
        }

        var bin = binaries ?? FindBinaries()
            ?? throw new InvalidOperationException(
                "no PostgreSQL to start: install PostgreSQL 16 or later (brew install postgresql@16), "
                + "pass --pg-bin <its bin directory>, or --postgres <a connection string>");
        await SweepAsync(bin).ConfigureAwait(false);
        var directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")[..8]);
        var server = new PgServer("", directory, bin);
        try
        {
            return await server.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<PgServer> StartAsync(CancellationToken ct)
    {
        var (bin, directory) = (_binaries!, _directory!);
        await RunAsync(Path.Combine(bin, "initdb"), ct,
                "-D", directory, "-U", "livegrid", "--auth=trust", "--encoding=UTF8", "--locale=C", "--no-sync")
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, Owner),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture), ct)
            .ConfigureAwait(false);

        // A throwaway: nothing is fsynced, and the settings favour bulk loading and building indexes.
        var port = FreePort();
        var settings = string.Create(CultureInfo.InvariantCulture,
                           $"-p {port} -k {directory} -c listen_addresses=localhost ")
                       + "-c fsync=off -c synchronous_commit=off -c full_page_writes=off -c shared_buffers=1GB "
                       + "-c maintenance_work_mem=1GB -c work_mem=64MB -c max_wal_size=8GB -c max_connections=200 "
                       + "-c max_parallel_maintenance_workers=4 -c autovacuum_naptime=10s";
        await RunAsync(Path.Combine(bin, "pg_ctl"), ct,
                "-D", directory, "-l", Path.Combine(directory, "server.log"), "-w", "-o", settings, "start")
            .ConfigureAwait(false);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = port,
            Username = "livegrid",
            Database = "livegrid",
            SearchPath = "livegrid",
            MaxPoolSize = 100,
            IncludeErrorDetail = true
        };
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(builder.ConnectionString)
                     {
                         Database = "postgres",
                         SearchPath = null
                     }.ConnectionString))
        {
            await admin.OpenAsync(ct).ConfigureAwait(false);
            await using var create =
                new NpgsqlCommand("CREATE DATABASE livegrid TEMPLATE template0 LC_COLLATE 'C' LC_CTYPE 'C'", admin);
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await PrepareAsync(builder, ct).ConfigureAwait(false);
        return new PgServer(builder.ConnectionString, directory, bin);
    }

    /// <summary>
    ///     The schema the sample's tables live in. On a server it was given, the sample drops only what it
    ///     made: a schema of that name already there is refused, for its owner to drop.
    /// </summary>
    private static async Task PrepareAsync(NpgsqlConnectionStringBuilder builder, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var exists =
            new NpgsqlCommand("SELECT count(*) FROM pg_namespace WHERE nspname = 'livegrid'", connection);
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) >
            0)
            throw new InvalidOperationException(
                $"the database {builder.Database} on {builder.Host} already has a schema named livegrid; the sample "
                + "creates its own and drops nothing it did not make: drop that schema, or point at another database");

        await using var schema = new NpgsqlCommand("CREATE SCHEMA livegrid", connection);
        await schema.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Stops and deletes the servers of samples that were killed rather than stopped: each server's
    ///     directory names the process that started it, and one whose process has gone is left over.
    /// </summary>
    private static async Task SweepAsync(string bin)
    {
        foreach (var directory in Directory.EnumerateDirectories(Path.GetTempPath(), Prefix + "*"))
        {
            var owner = Path.Combine(directory, Owner);
            if (!File.Exists(owner) ||
                !int.TryParse(await File.ReadAllTextAsync(owner).ConfigureAwait(false), out var pid) ||
                Alive(pid)) continue;

            await new PgServer("", directory, bin).DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? FindBinaries()
    {
        string[] candidates =
        [
            .. Enumerable.Range(16, 5).Reverse().Select(v => $"/opt/homebrew/opt/postgresql@{v}/bin"),
            .. Enumerable.Range(16, 5).Reverse().Select(v => $"/usr/local/opt/postgresql@{v}/bin"),
            .. Enumerable.Range(16, 5).Reverse().Select(v => $"/usr/lib/postgresql/{v}/bin"),
            "/opt/homebrew/bin",
            "/usr/local/bin"
        ];
        return candidates.FirstOrDefault(d =>
            File.Exists(Path.Combine(d, "initdb")) && File.Exists(Path.Combine(d, "pg_ctl")));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task RunAsync(string program, CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };

        // On macOS the server refuses to start without a locale it can load in the environment.
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {program}");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(program)} failed ({process.ExitCode}): {await error.ConfigureAwait(false)}{await output.ConfigureAwait(false)}");
    }
}