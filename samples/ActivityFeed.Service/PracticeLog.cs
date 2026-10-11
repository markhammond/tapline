using Microsoft.Data.Sqlite;

namespace ActivityFeed.Service;

/// <summary>
///     The practice's write log: every instance of the service rebuilds the practice from it, in order. A turn is the
///     writer's alone (the holder of the log's lease: each such append is fenced by the lease's epoch, so a writer that
///     lost it can't add to the log); what only one instance knows (whose feeds it holds open) any instance appends. The store behind it is the implementation's: SQLite, for instances on one machine
///     (<see cref="SqlitePracticeLog" />), or memory, for one alone (<see cref="MemoryPracticeLog" />).
/// </summary>
public interface IPracticeLog : IAsyncDisposable
{
    /// <summary>
    ///     Appends an entry: fenced by <paramref name="lease" /> (null if it isn't held now: nothing appended), or, with no
    ///     lease, anyone's. Its number in the log.
    /// </summary>
    ValueTask<long?> AppendAsync(string kind, string origin, ReadOnlyMemory<byte> body, Lease? lease, CancellationToken ct);

    /// <summary>The entries after <paramref name="after" />, in order, at most <paramref name="max" />: added to <paramref name="into" />.</summary>
    ValueTask ReadAsync(long after, int max, List<LogEntry> into, CancellationToken ct);

    /// <summary>
    ///     Completes once there may be an entry after <paramref name="after" /> (it may wake for nothing): the store's own
    ///     word, which an instance falls back on when it doesn't hear the writer's signal.
    /// </summary>
    ValueTask WaitAsync(long after, CancellationToken ct);

    /// <summary>
    ///     The writer's lease, for <paramref name="owner" />: taken if nobody holds it or its holder's term lapsed (a new
    ///     epoch), renewed if it's theirs already; otherwise who holds it.
    /// </summary>
    ValueTask<LeaseAnswer> LeadAsync(string owner, TimeSpan term, CancellationToken ct);

    /// <summary>The lease let go (a drain): another instance may take it at once.</summary>
    ValueTask ResignAsync(Lease lease, CancellationToken ct);
}

/// <summary>The writer's lease: its holder, the epoch its appends are fenced by, and when its term ends unless renewed.</summary>
public readonly record struct Lease(string Owner, long Epoch, DateTime Until);

/// <summary>
///     What asking for the lease got: <see cref="Held" />, ours; otherwise the instance that holds it. Taken from a holder
///     whose term lapsed: that one (<see cref="TakenFrom" />).
/// </summary>
public readonly record struct LeaseAnswer(Lease? Held, string? Holder, string? TakenFrom);

/// <summary>An entry of the log: its number, its kind, the instance that appended it, its epoch (0: unfenced), when, and its body.</summary>
public sealed record LogEntry(long Seq, string Kind, string Origin, long Epoch, DateTime At, byte[] Body);

/// <summary>
///     The log in a SQLite database, in WAL mode: one writer and any number of readers at once, across the processes of
///     one machine (not over a network file system). Each append is a transaction of its own (a fenced one checks the lease
///     in it); a reader waits for more by watching the database's <c>data_version</c>, which changes when another
///     connection commits: a look every <see cref="PollEvery" />. (Looking every millisecond would find a commit sooner,
///     at a timer's wake a millisecond, all day: an instance hears the writer's signal instead, and looks only to be sure.)
/// </summary>
public sealed class SqlitePracticeLog : IPracticeLog
{
    private readonly Lock _gate = new(); // (one connection each for writing, reading and watching: SQLite's aren't shared)
    private readonly SqliteConnection _write, _read, _watch;
    private readonly SqliteCommand _version; // (the watch's look, prepared once)

    public SqlitePracticeLog(string path, TimeSpan? pollEvery = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        PollEvery = pollEvery ?? TimeSpan.FromMilliseconds(25);
        var connection = new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }
            .ToString();
        (_write, _read, _watch) = (new SqliteConnection(connection), new SqliteConnection(connection), new SqliteConnection(connection));
        foreach (var c in (ReadOnlySpan<SqliteConnection>)[_write, _read, _watch]) c.Open();
        Execute(_write, """
                        PRAGMA journal_mode = WAL;
                        PRAGMA synchronous = NORMAL;
                        PRAGMA busy_timeout = 5000;
                        CREATE TABLE IF NOT EXISTS log (
                            seq INTEGER PRIMARY KEY, kind TEXT NOT NULL, origin TEXT NOT NULL, epoch INTEGER NOT NULL,
                            at INTEGER NOT NULL, body BLOB NOT NULL);
                        CREATE TABLE IF NOT EXISTS lease (
                            id INTEGER PRIMARY KEY CHECK (id = 1), owner TEXT, epoch INTEGER NOT NULL, until INTEGER NOT NULL);
                        INSERT OR IGNORE INTO lease (id, owner, epoch, until) VALUES (1, NULL, 0, 0);
                        """);
        foreach (var c in (ReadOnlySpan<SqliteConnection>)[_read, _watch]) Execute(c, "PRAGMA busy_timeout = 5000;");
        _version = Command(_watch, null, "PRAGMA data_version");
        _version.Prepare();
    }

    /// <summary>The database's file.</summary>
    public string Path { get; }

    /// <summary>How often a reader waiting for more looks.</summary>
    public TimeSpan PollEvery { get; }

    public ValueTask<long?> AppendAsync(string kind, string origin, ReadOnlyMemory<byte> body, Lease? lease, CancellationToken ct)
    {
        lock (_gate)
        {
            using var tx = _write.BeginTransaction(deferred: false); // (BEGIN IMMEDIATE: the lease can't change under it)
            if (lease is { } held)
            {
                using var check = Command(_write, tx, "SELECT owner, epoch FROM lease WHERE id = 1");
                using var row = check.ExecuteReader();
                if (!row.Read() || row.IsDBNull(0) || row.GetString(0) != held.Owner || row.GetInt64(1) != held.Epoch)
                    return ValueTask.FromResult<long?>(null); // (another holds it now: nothing of ours goes in)
            }

            using var insert = Command(_write, tx,
                "INSERT INTO log (kind, origin, epoch, at, body) VALUES ($kind, $origin, $epoch, $at, $body); SELECT last_insert_rowid();");
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$origin", origin);
            insert.Parameters.AddWithValue("$epoch", lease?.Epoch ?? 0);
            insert.Parameters.AddWithValue("$at", Micros(DateTime.UtcNow));
            insert.Parameters.AddWithValue("$body", body.ToArray());
            var seq = (long)insert.ExecuteScalar()!;
            tx.Commit();
            return ValueTask.FromResult<long?>(seq);
        }
    }

    public ValueTask ReadAsync(long after, int max, List<LogEntry> into, CancellationToken ct)
    {
        lock (_read)
        {
            using var read = Command(_read, null,
                "SELECT seq, kind, origin, epoch, at, body FROM log WHERE seq > $after ORDER BY seq LIMIT $max");
            read.Parameters.AddWithValue("$after", after);
            read.Parameters.AddWithValue("$max", max);
            using var row = read.ExecuteReader();
            while (row.Read())
                into.Add(new LogEntry(row.GetInt64(0), row.GetString(1), row.GetString(2), row.GetInt64(3),
                    DateTime.UnixEpoch.AddTicks(row.GetInt64(4) * 10), (byte[])row.GetValue(5)));
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask WaitAsync(long after, CancellationToken ct)
    {
        long version;
        lock (_watch)
        {
            if (Head() > after) return; // (there already)
            version = DataVersion();
        }

        while (true)
        {
            await Task.Delay(PollEvery, ct);
            lock (_watch)
            {
                if (DataVersion() != version) return; // (someone committed: perhaps an entry after it)
            }
        }
    }

    public ValueTask<LeaseAnswer> LeadAsync(string owner, TimeSpan term, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            using (var look = Command(_write, null, "SELECT owner, until FROM lease WHERE id = 1")) // (a read: another's, still)
            using (var row = look.ExecuteReader())
                if (row.Read() && !row.IsDBNull(0) && row.GetString(0) != owner && row.GetInt64(1) >= Millis(now))
                    return ValueTask.FromResult(new LeaseAnswer(null, row.GetString(0), null));

            using var tx = _write.BeginTransaction(deferred: false); // (taken, or renewed: the write lock, briefly)
            string? holder;
            long epoch, until;
            using (var read = Command(_write, tx, "SELECT owner, epoch, until FROM lease WHERE id = 1"))
            using (var row = read.ExecuteReader())
            {
                row.Read();
                (holder, epoch, until) = (row.IsDBNull(0) ? null : row.GetString(0), row.GetInt64(1), row.GetInt64(2));
            }

            var lapsed = holder is null || until < Millis(now);
            if (holder != owner && !lapsed) return ValueTask.FromResult(new LeaseAnswer(null, holder, null));
            if (holder != owner) epoch++; // (taken: a new epoch fences out the one before)
            var ends = now + term;
            using (var take = Command(_write, tx, "UPDATE lease SET owner = $owner, epoch = $epoch, until = $until WHERE id = 1"))
            {
                take.Parameters.AddWithValue("$owner", owner);
                take.Parameters.AddWithValue("$epoch", epoch);
                take.Parameters.AddWithValue("$until", Millis(ends));
                take.ExecuteNonQuery();
            }

            tx.Commit();
            return ValueTask.FromResult(new LeaseAnswer(new Lease(owner, epoch, ends), owner,
                holder is not null && holder != owner ? holder : null));
        }
    }

    public ValueTask ResignAsync(Lease lease, CancellationToken ct)
    {
        lock (_gate)
        {
            using var resign = Command(_write, null, "UPDATE lease SET owner = NULL, until = 0 WHERE id = 1 AND owner = $owner AND epoch = $epoch");
            resign.Parameters.AddWithValue("$owner", lease.Owner);
            resign.Parameters.AddWithValue("$epoch", lease.Epoch);
            resign.ExecuteNonQuery();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        lock (_read)
        lock (_watch)
        {
            _version.Dispose();
            foreach (var c in (ReadOnlySpan<SqliteConnection>)[_write, _read, _watch]) c.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private long Head()
    {
        using var head = Command(_watch, null, "SELECT coalesce(max(seq), 0) FROM log");
        return (long)head.ExecuteScalar()!;
    }

    private long DataVersion()
    {
        return (long)_version.ExecuteScalar()!;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        return command;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, null, sql);
        command.ExecuteNonQuery();
    }

    private static long Micros(DateTime utc)
    {
        return (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
    }

    private static long Millis(DateTime utc)
    {
        return (utc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
    }
}

/// <summary>
///     The log in memory, for an instance alone: nothing outlives the process (started again, it primes a new day), and no
///     other instance can read it. The lease is the first asker's for good.
/// </summary>
public sealed class MemoryPracticeLog : IPracticeLog
{
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _gate = new();
    private Lease? _lease;
    private long _epoch;
    private TaskCompletionSource _more = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<long?> AppendAsync(string kind, string origin, ReadOnlyMemory<byte> body, Lease? lease, CancellationToken ct)
    {
        TaskCompletionSource more;
        long seq;
        lock (_gate)
        {
            if (lease is { } asked && (_lease is not { } current || current.Owner != asked.Owner || current.Epoch != asked.Epoch))
                return ValueTask.FromResult<long?>(null);
            seq = _entries.Count + 1;
            _entries.Add(new LogEntry(seq, kind, origin, lease?.Epoch ?? 0, DateTime.UtcNow, body.ToArray()));
            (more, _more) = (_more, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        more.TrySetResult();
        return ValueTask.FromResult<long?>(seq);
    }

    public ValueTask ReadAsync(long after, int max, List<LogEntry> into, CancellationToken ct)
    {
        lock (_gate)
        {
            for (var i = (int)Math.Max(0, after); i < _entries.Count && max-- > 0; i++) into.Add(_entries[i]);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask WaitAsync(long after, CancellationToken ct)
    {
        Task more;
        lock (_gate)
        {
            if (_entries.Count > after) return;
            more = _more.Task;
        }

        await more.WaitAsync(ct);
    }

    public ValueTask<LeaseAnswer> LeadAsync(string owner, TimeSpan term, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_lease is { } held && held.Owner != owner) return ValueTask.FromResult(new LeaseAnswer(null, held.Owner, null));
            _lease = new Lease(owner, _lease?.Epoch ?? ++_epoch, DateTime.MaxValue);
            return ValueTask.FromResult(new LeaseAnswer(_lease, owner, null));
        }
    }

    public ValueTask ResignAsync(Lease lease, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_lease is { } held && held.Owner == lease.Owner) _lease = null;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
