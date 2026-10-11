using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Tapline;

namespace ActivityFeed.Service;

/// <summary>
///     What the access log's store says of itself (<c>/log</c>): where it is, entries posted and written, how often the loop
///     waited (every batch with the writer) and for how long, and how many of its batches were with the writer at most.
/// </summary>
public sealed record AccessLogReport(string Store, long Posted, long Written, long Waits, long Failures, double WaitedMs = 0,
    int Batches = 0, int MaxInFlight = 0);

/// <summary>
///     The access log's store, off the loop. The loop hands each refresh's entries over as a batch of their own, through a
///     pipe of reusable batches (Tapline's <see cref="BatchPipe{TBatch}" />), and a writer of its own writes them to
///     SQLite, the batches waiting for it in one transaction, in the entries' order. The database is its own, apart from
///     the write log's, so neither waits on the other's lock, and ChalkQL reads it as a source of its own (the auditor's:
///     <see cref="PracticeStore.AuditSchema" />), under the practice's policy. When the practice's instances share a write
///     log's file, it's a file beside it, and every instance writes it: <c>INSERT OR IGNORE</c> by the entry's number, which
///     replay makes the same on every instance; whoever starts them removes both. When the practice is one process's alone,
///     it's a file of its own in the temporary folder, removed when the process stops.
/// </summary>
/// <remarks>
///     <para>
///         The access log is worked out from the write log, so this store is a projection of it: a writer behind, or gone,
///         loses nothing the log can't give again. So the pipe is bounded for memory, not durability: with every batch
///         still with the writer, the loop waits for one, and the report counts it; an entry is never dropped.
///     </para>
///     <para>
///         In steady state neither side allocates. Entries are structs, copied into batches that keep the room they grew
///         to; the writer's waits are the pipe's own (no token), and each row is bound with SQLite's own calls (a person's
///         name and topic as UTF-8, kept from their first entry), not ADO.NET's boxed parameters. Reads (<c>/access</c>)
///         take the same connection between transactions.
///     </para>
/// </remarks>
public sealed class AccessLogSink : IAsyncDisposable
{
    private const int MostInTransaction = 4096; // (entries: the batches waiting, written together up to about this many)

    private readonly ShownBatch[] _held; // (the writer's: the batches it's writing, returned once written)
    private readonly BatchPipe<ShownBatch> _pipe;
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new(); // (the connection's: the consumer's batches, and reads between them)
    private readonly sqlite3_stmt _insert, _begin, _commit, _rollback;
    private readonly ILogger _log;
    private readonly Dictionary<int, (byte[] User, byte[] Topic)> _utf8 = []; // (each person's, from their first entry)
    private readonly Task _writer;
    private long _posted, _written, _failures;

    private readonly bool _owned; // (a file of this process's own: removed when it stops)

    /// <param name="path">The database's file, shared with other instances; none: a file of this process's own.</param>
    /// <param name="log">Its log.</param>
    /// <param name="batches">How many refreshes' entries may wait for the writer before the loop does.</param>
    public AccessLogSink(string? path, ILogger<AccessLogSink> log, int batches = 64)
    {
        _log = log;
        _owned = path is null;
        if (_owned) Sweep();
        Store = Path.GetFullPath(path ?? Path.Combine(Path.GetTempPath(),
            $"{OwnedPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}.db"));
        _db = new SqliteConnection(Connection(SqliteOpenMode.ReadWriteCreate));
        _db.Open();
        using (var setup = _db.CreateCommand())
        {
            setup.CommandText = """
                                PRAGMA journal_mode = WAL;
                                PRAGMA synchronous = NORMAL;
                                PRAGMA busy_timeout = 5000;
                                CREATE TABLE IF NOT EXISTS access_log (
                                    seq BIGINT PRIMARY KEY, person_id INT NOT NULL, person_user TEXT NOT NULL,
                                    topic TEXT NOT NULL, part INT NOT NULL, row_seq BIGINT NOT NULL,
                                    patient_id INT NOT NULL, site_id INT NOT NULL, shown_at BIGINT NOT NULL,
                                    booking BIGINT NOT NULL DEFAULT 0, clinician_id INT NOT NULL DEFAULT 0,
                                    basis INT NOT NULL DEFAULT 0);
                                CREATE INDEX IF NOT EXISTS access_log_record ON access_log (part, row_seq);
                                """;
            setup.ExecuteNonQuery();
            // A file written before entries named their visit, its GP and why it was shown: those columns added (its old entries' 0, none).
            setup.CommandText = "SELECT name FROM pragma_table_info('access_log')";
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = setup.ExecuteReader())
                while (reader.Read()) columns.Add(reader.GetString(0));
            foreach (var (column, type) in (ReadOnlySpan<(string, string)>)[("booking", "BIGINT"), ("clinician_id", "INT"), ("basis", "INT")])
                if (!columns.Contains(column))
                {
                    setup.CommandText = $"ALTER TABLE access_log ADD COLUMN {column} {type} NOT NULL DEFAULT 0";
                    setup.ExecuteNonQuery();
                }
        }

        var db = _db.Handle!;
        _insert = Prepare(db, "INSERT OR IGNORE INTO access_log (seq, person_id, person_user, topic, part, row_seq, " +
                              "patient_id, site_id, shown_at, booking, clinician_id, basis) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)");
        _begin = Prepare(db, "BEGIN IMMEDIATE");
        _commit = Prepare(db, "COMMIT");
        _rollback = Prepare(db, "ROLLBACK");
        _pipe = new BatchPipe<ShownBatch>(batches, static () => new ShownBatch());
        _held = new ShownBatch[batches];
        _writer = Task.Run(WriteAsync);
    }

    private const string OwnedPrefix = "activity-feed-access-";

    /// <summary>
    ///     Files of its own a process killed before it could remove them (their names carry its id): removed, so a
    ///     practice stopped hard leaves its access log behind only until the next starts.
    /// </summary>
    private void Sweep()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), OwnedPrefix + "*"))
        {
            var name = Path.GetFileName(file).AsSpan(OwnedPrefix.Length);
            if (name.IndexOf('-') is var dash and > 0 && int.TryParse(name[..dash], out var pid) && !Running(pid))
                try
                {
                    File.Delete(file);
                    _log.LogInformation("Practice: removed {File}, an access log its process ({Pid}) left behind", file, pid);
                }
                catch (IOException)
                {
                }
        }

        static bool Running(int pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>Its database's file (full path).</summary>
    public string Store { get; }

    /// <summary>A connection string for its database: ChalkQL's reads open their own.</summary>
    public string Connection(SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        return new SqliteConnectionStringBuilder { DataSource = Store, Mode = mode, Pooling = false }.ToString();
    }

    /// <summary>Its file beside a write log's (<c>practice.db</c>: <c>practice.access.db</c>), for instances that share it.</summary>
    public static string Beside(string writeLog)
    {
        var full = Path.GetFullPath(writeLog);
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + ".access.db");
    }

    public AccessLogReport Report()
    {
        return new AccessLogReport(Store, Interlocked.Read(ref _posted), Interlocked.Read(ref _written), _pipe.Waits,
            Interlocked.Read(ref _failures), _pipe.WaitedMs, _pipe.Capacity, _pipe.MaxInFlight);
    }

    /// <summary>
    ///     A refresh's entries, in order, handed to the writer as a batch of their own: at once, unless every batch is still
    ///     with the writer, when the loop waits for one (counted).
    /// </summary>
    public ValueTask PostAsync(List<Shown> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return ValueTask.CompletedTask;
        var rent = _pipe.RentAsync(ct);
        if (!rent.IsCompletedSuccessfully) return PostingAsync(rent, entries);
        Post(rent.Result, entries);
        return ValueTask.CompletedTask;
    }

    private async ValueTask PostingAsync(ValueTask<ShownBatch> rent, List<Shown> entries)
    {
        Post(await rent, entries);
    }

    private void Post(ShownBatch batch, List<Shown> entries)
    {
        batch.Fill(entries);
        Interlocked.Add(ref _posted, entries.Count);
        _pipe.Publish(batch);
    }

    /// <summary>
    ///     Once what was posted when asked is written (or a second has passed): a read then has every entry the loop had
    ///     worked out by then.
    /// </summary>
    public async Task FlushedAsync(CancellationToken ct)
    {
        var posted = Interlocked.Read(ref _posted);
        for (var waited = 0; Interlocked.Read(ref _written) + Interlocked.Read(ref _failures) < posted && waited < 1000; waited += 10)
            await Task.Delay(10, ct);
    }

    /// <summary>
    ///     The writer: each batch as it's handed over, with those waiting behind it (up to about
    ///     <see cref="MostInTransaction" /> entries), written in one transaction and returned, until the loop's posting is over.
    /// </summary>
    private async Task WriteAsync()
    {
        await foreach (var first in _pipe.ReadAllAsync().ConfigureAwait(false)) // (its waits the pipe's own: nothing allocated)
        {
            _held[0] = first;
            var (count, entries) = (1, first.Count);
            while (count < _held.Length && entries < MostInTransaction && _pipe.TryRead(out var next))
            {
                _held[count++] = next;
                entries += next.Count;
            }

            try
            {
                Write(count);
                Interlocked.Add(ref _written, entries);
            }
            catch (Exception e) // (a projection: what isn't written here, the write log can give again)
            {
                Interlocked.Add(ref _failures, entries);
                _log.LogWarning(e, "Practice: the access log's store didn't take entries {First} to {Last}",
                    _held[0].Entries[0].Seq, _held[count - 1].Entries[^1].Seq);
            }
            finally
            {
                for (var i = 0; i < count; i++)
                {
                    _pipe.Return(_held[i]);
                    _held[i] = null!;
                }
            }
        }
    }

    /// <summary>The first <paramref name="count" /> held batches' entries, in one transaction: each bound with SQLite's own calls.</summary>
    private void Write(int count)
    {
        lock (_gate)
        {
            Step(_begin);
            try
            {
                for (var b = 0; b < count; b++)
                foreach (ref readonly var entry in _held[b].Entries)
                {
                    if (!_utf8.TryGetValue(entry.PersonId, out var names)) // (once a person: their name and topic, as UTF-8)
                        _utf8[entry.PersonId] = names = (Encoding.UTF8.GetBytes(entry.PersonUser), Encoding.UTF8.GetBytes(entry.Topic));
                    Bind(raw.sqlite3_bind_int64(_insert, 1, entry.Seq));
                    Bind(raw.sqlite3_bind_int64(_insert, 2, entry.PersonId));
                    Bind(raw.sqlite3_bind_text(_insert, 3, names.User));
                    Bind(raw.sqlite3_bind_text(_insert, 4, names.Topic));
                    Bind(raw.sqlite3_bind_int64(_insert, 5, entry.Part));
                    Bind(raw.sqlite3_bind_int64(_insert, 6, entry.RowSeq));
                    Bind(raw.sqlite3_bind_int64(_insert, 7, entry.PatientId));
                    Bind(raw.sqlite3_bind_int64(_insert, 8, entry.SiteId));
                    Bind(raw.sqlite3_bind_int64(_insert, 9, entry.ShownAt.Ticks));
                    Bind(raw.sqlite3_bind_int64(_insert, 10, entry.Booking));
                    Bind(raw.sqlite3_bind_int64(_insert, 11, entry.ClinicianId));
                    Bind(raw.sqlite3_bind_int64(_insert, 12, (long)entry.Basis));
                    Step(_insert);
                }

                Step(_commit);
            }
            catch
            {
                raw.sqlite3_reset(_rollback);
                raw.sqlite3_step(_rollback);
                raw.sqlite3_reset(_rollback);
                throw;
            }
        }
    }

    private void Step(sqlite3_stmt statement)
    {
        var result = raw.sqlite3_step(statement);
        raw.sqlite3_reset(statement);
        if (result != raw.SQLITE_DONE) throw new SqliteException(raw.sqlite3_errmsg(_db.Handle).utf8_to_string(), result);
    }

    private void Bind(int result)
    {
        if (result != raw.SQLITE_OK) throw new SqliteException(raw.sqlite3_errmsg(_db.Handle).utf8_to_string(), result);
    }

    private static sqlite3_stmt Prepare(sqlite3 db, string sql)
    {
        var result = raw.sqlite3_prepare_v2(db, sql, out var statement);
        if (result != raw.SQLITE_OK) throw new SqliteException(raw.sqlite3_errmsg(db).utf8_to_string(), result);
        return statement;
    }

    /// <summary>The loop's posting over: what's posted, written, then the connection closed, and a file of its own removed.</summary>
    public async ValueTask DisposeAsync()
    {
        _pipe.Complete();
        await _writer;
        lock (_gate)
        {
            foreach (var statement in (ReadOnlySpan<sqlite3_stmt>)[_insert, _begin, _commit, _rollback]) statement.Dispose();
            _db.Dispose();
        }

        if (!_owned) return; // (shared: whoever started the instances removes it, with the write log)
        foreach (var file in (ReadOnlySpan<string>)[Store, Store + "-wal", Store + "-shm"])
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
    }
}

/// <summary>A refresh's access log entries, in order: a batch of the access log's pipe, keeping the room it grew to.</summary>
internal sealed class ShownBatch
{
    private Shown[] _entries = new Shown[64];

    public int Count { get; private set; }

    public ReadOnlySpan<Shown> Entries => _entries.AsSpan(0, Count);

    /// <summary>Its entries now <paramref name="entries" />, copied (whatever it held before let go).</summary>
    public void Fill(List<Shown> entries)
    {
        if (_entries.Length < entries.Count) _entries = new Shown[Math.Max(entries.Count, _entries.Length * 2)];
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries).CopyTo(_entries);
        Count = entries.Count;
    }
}
