using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PivotTable.Contracts;
using Tapline;
using Tapline.Client;
using Tapline.MagicOnion;

namespace PivotTable.Headless;

/// <summary>
///     What the stream brings, written out — each batch's cells, a line a second — and what would fail a
///     verification.
/// </summary>
internal sealed class Watch(Options options, PivotShape shape)
{
    /// <summary>A figure's every digit, its thousands separated, no trailing zeros.</summary>
    private const string Exact = "#,0.############################";

    private readonly List<string> _failures = [];
    private readonly Lock _gate = new();

    private readonly byte[][]
        _reserved =
        [
            .. shape.Reserved.Select(shape.KeyOf)
        ]; // (the provider's cells among the pivot's: not counted as held)

    private readonly List<double> _second = [], _all = []; // post→receipt: the provider's post to this client, ms


    // The steady-state sweep time: the earliest event an answer covers to its publication to Tapline, ms — the last
    // answers' (as many as the page's vital statistics keep), and the run's.
    private readonly Lags _sweep = new(2048);
    private readonly List<double> _sweepAll = [];

    private int _batches, _snapshots, _cells, _batchesAll, _cellsAll, _connections, _errors, _moves;

    // Where the subscription's batches stand — its provider's engine (shard), and the last batch's version — and the waits
    // for a token: each completed by the first batch at or after it, on the same shard. And how long each took.
    private Guid? _shard;
    private long _at;
    private readonly List<(long Version, TaskCompletionSource<long> Reached)> _waits = [];
    private readonly List<double> _waited = [];
    private int _waitFailures;
    private long _bytesAt, _bytesBefore; // the current client's bytes a second ago; the clients' before it (moved off)
    private TaplineClient? _client;
    private long _emitted; // the last post's (its clock cell's): each post's sweep counted once

    public void Connected(int outletId, uint nonce)
    {
        lock (_gate)
        {
            _connections++;
        }

        Line($"connected: session {outletId}.{nonce}{(options.BandwidthLimit is { } cap ? $", capped at {cap}" : "")}");
    }

    public void Subscribed(PivotBinding answer)
    {
        lock (_gate)
        {
            if (answer.Shard != _shard) (_shard, _at) = (answer.Shard, 0); // (another engine: its versions its own)
        }

        Line($"subscribed: tap {answer.TapId}, topic {answer.Topic}, shard {answer.Shard}");
    }

    /// <summary>
    ///     Completes with the version of the first batch at or after <paramref name="token" /> — at once if one's in already;
    ///     refused (throws) for another shard's token.
    /// </summary>
    public Task<long> WaitForAsync(PivotToken token)
    {
        lock (_gate)
        {
            if (token.Shard != _shard)
                throw new InvalidOperationException($"token {token} is another shard's than the subscription's ({_shard})");
            if (_at >= token.Version) return Task.FromResult(_at);
            var reached = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waits.Add((token.Version, reached));
            return reached.Task;
        }
    }

    /// <summary>A wait for a token, timed: from asking to its batch in front of this client (or given up: failed).</summary>
    public void Waited(double? ms, string what)
    {
        lock (_gate)
        {
            if (ms is { } took) _waited.Add(took);
            else _waitFailures++;
        }

        if (ms is null) Fail(what);
        else Line(what);
    }

    public void Refused(string why, Guid? correlation)
    {
        Fail($"refused: {why}{(correlation is { } id ? $" (correlation {id})" : "")}");
    }

    public void Ended(TapError error)
    {
        lock (_gate)
        {
            _errors++;
        }

        Line($"subscription ended: {error.Code}{(error.Local ? " (this client's: its stream didn't check out)" : "")}");
        if (error.Code is not (FaultCode.ServiceDown or FaultCode.ServiceDrained or FaultCode.Gone))
            Fail($"subscription failed: {error.Code}");
    }

    public void Note(string what)
    {
        Line(what);
    }

    public void Moved(RouterMove move)
    {
        lock (_gate)
        {
            _moves++;
        }

        Line(
            $"moved: session {move.To.Session?.OutletId}.{move.To.Session?.Nonce} streaming, the old let go ({move.Why})");
    }

    public void Failed(string what)
    {
        Fail(what);
    }

    /// <summary>
    ///     A table record applied: its cells (some), read by the shape — and, from the provider's clock cell, the answer's
    ///     latency (its post to here) and its sweep (the earliest event it covers to its publication to Tapline: the page's
    ///     steady-state sweep time; a first answer, covering none, has none — nor does an answer with nothing new).
    /// </summary>
    public void Batch(TableReplica table, TableBatch batch)
    {
        var now = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10; // µs, as the provider's clock cell
        double? latency = null, sweep = null;
        long? version = null;
        int cells = 0, shown = 0;
        var text = options.CellsShown > 0 ? new StringBuilder() : null;
        foreach (var entry in batch)
        {
            if (shape.ReservedOf(entry.Key) is { } reserved) // (the provider's clock cell: none of the pivot's)
            {
                if (!entry.Deleted && reserved.FieldMeaning("emitted") is { } emittedAt &&
                    shape.Integer(entry.Row, emittedAt) is long emitted)
                {
                    if (!batch.IsSnapshot)
                        latency = (now - emitted) / 1000.0; // (a snapshot's: the last post's, maybe long ago)
                    if (emitted != Volatile.Read(ref _emitted) && reserved.FieldMeaning("watermark") is { } markAt &&
                        shape.Integer(entry.Row, markAt) is long mark && mark > 0)
                        sweep = (emitted - mark) / 1000.0;
                    Volatile.Write(ref _emitted, emitted);
                }

                if (!entry.Deleted && reserved.FieldMeaning("version") is { } versionAt &&
                    shape.Integer(entry.Row, versionAt) is long stamped)
                    version = stamped; // (its stamp: the engine's version the answer is at)
                continue;
            }

            cells++;
            if (text is not null && shown++ < options.CellsShown)
                text.Append("\n  ").Append(Cell(entry.Key, entry.Row, entry.Deleted));
        }

        List<TaskCompletionSource<long>>? reached = null;
        lock (_gate)
        {
            if (version > _at) _at = version.Value;
            for (var i = _waits.Count - 1; i >= 0; i--)
                if (_waits[i].Version <= _at)
                {
                    (reached ??= []).Add(_waits[i].Reached);
                    _waits.RemoveAt(i);
                }

            (_batches, _batchesAll, _cells, _cellsAll) =
                (_batches + 1, _batchesAll + 1, _cells + cells, _cellsAll + cells);
            if (batch.IsSnapshot) _snapshots++;
            if (latency is { } ms)
            {
                _second.Add(ms);
                _all.Add(ms);
            }

            if (sweep is { } swept)
            {
                _sweep.Add(swept);
                _sweepAll.Add(swept);
            }
        }

        if (reached is not null)
            foreach (var r in reached) r.TrySetResult(version ?? _at);
        if (text is null) return;
        Console.Out.Write(
            $"#{_batchesAll} v{batch.FromVersion}→{batch.ToVersion} {(batch.IsSnapshot ? "snapshot" : "delta")}, {cells} cell{(cells == 1 ? "" : "s")}" +
            $"{(latency is { } l ? $", post→receipt {Ms(l)}" : "")}{(sweep is { } w ? $", sweep {Ms(w)}" : "")} · {table.Count - _reserved.Count(k => table.TryGetRow(k, out _))} held{text}{(cells > shown ? $"\n  … {cells - shown} more" : "")}\n");
    }

    /// <summary>A cell as its shape reads it: its labels, and each figure its statement answers, exactly — none: "—".</summary>
    private string Cell(ReadOnlySpan<byte> key, ReadOnlySpan<byte> row, bool deleted)
    {
        var text = new StringBuilder($"{Label(shape.Text(key, "row"))} · {Label(shape.Text(key, "column"))}:");
        if (deleted) return text.Append(" gone").ToString();
        var between = " ";
        foreach (var field in shape.Row.Fields)
        {
            if (field.From is null) continue; // (a figure the statement doesn't answer: always none)
            text.Append(between).Append(field.Name).Append(' ').Append(PivotShape.Figure(row, field) is { } figure
                ? figure.ToString(Exact, CultureInfo.InvariantCulture)
                : "—");
            between = ", ";
        }

        return text.ToString();
    }

    private static string Label(string label)
    {
        return label.Length == 0 ? "Total" : label;
    }

    /// <summary>
    ///     A line a second: what came, at what rate, how long it took — its sweep time the last answers', as the page's
    ///     vital statistics give it.
    /// </summary>
    public void Second(TaplineClient client)
    {
        string line;
        lock (_gate)
        {
            if (!ReferenceEquals(client, _client)) // moved: a new client's bytes, from zero
            {
                if (_client is not null) (_bytesBefore, _bytesAt) = (_bytesBefore + _client.BytesReceived, 0);
                _client = client;
            }

            var bytes = client.BytesReceived;
            line =
                $"{DateTime.Now:HH:mm:ss}  {_batches} batch{(_batches == 1 ? "" : "es")}{(_snapshots > 0 ? $" ({_snapshots} snapshot{(_snapshots == 1 ? "" : "s")})" : "")} · " +
                $"{_cells} cells · {(bytes - _bytesAt) / 1024.0:F1} KB/s · post→receipt {Quantiles(CollectionsMarshal.AsSpan(_second), 0.5, 0.99, 0.995)} · {SweepTime(_sweep.Times)} · frames {client.LastFrame} · resyncs {client.Resyncs}";
            (_bytesAt, _batches, _snapshots, _cells) = (bytes, 0, 0, 0);
            _second.Clear(); // (the sweep's kept: the last answers', not the second's)
        }

        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    /// <summary>The whole run, and — verifying — whether it holds: 0, or 1 with why.</summary>
    public int End(TaplineClient client, bool verifying)
    {
        lock (_gate)
        {
            if (client.Resyncs > 0)
                _failures.Add($"{client.Resyncs} resync{(client.Resyncs == 1 ? "" : "s")}: a record didn't check out");
            if (verifying && _batchesAll == 0) _failures.Add("nothing came");
            if (verifying && options.Drain is not null && _moves == 0) _failures.Add("drained, but no move made");
            var bytes = _bytesBefore + (ReferenceEquals(client, _client) || _client is null
                ? client.BytesReceived
                : _client.BytesReceived + client.BytesReceived);
            Console.Out.WriteLine(
                $"in all: {_batchesAll} batches, {_cellsAll} cells, {bytes / 1024.0:N0} KB over {_connections} connection{(_connections == 1 ? "" : "s")}" +
                $"{(_moves > 0 ? $" ({_moves} move{(_moves == 1 ? "" : "s")} to another router)" : "")}; {_errors} subscription{(_errors == 1 ? "" : "s")} ended; post→receipt {Quantiles(CollectionsMarshal.AsSpan(_all), 0.5, 0.99, 0.995)}; " +
                SweepTime(CollectionsMarshal.AsSpan(_sweepAll), $" over the run's {_sweepAll.Count:N0} answer{(_sweepAll.Count == 1 ? "" : "s")}"));
            if (_waited.Count > 0 || _waitFailures > 0)
                Console.Out.WriteLine($"tokens waited for: {_waited.Count} in front of this client, token → in front " +
                                      $"{Quantiles(CollectionsMarshal.AsSpan(_waited), 0.5, 0.99, 1)}{(_waitFailures > 0 ? $"; {_waitFailures} given up" : "")}");
            if (!verifying) return _failures.Count == 0 ? 0 : 1;
            Console.Out.WriteLine(_failures.Count == 0
                ? "verified: every record checked out, nothing failed"
                : $"not verified: {string.Join("; ", _failures)}");
            return _failures.Count == 0 ? 0 : 1;
        }
    }

    /// <summary>
    ///     The steady-state sweep time of these answers, as the page's vital statistics write it: "steady-state sweep time
    ///     (earliest event → publication to Tapline): p50 6.9 ms, p99.5 93.7 ms".
    /// </summary>
    private static string SweepTime(ReadOnlySpan<double> times, string of = "")
    {
        return $"steady-state sweep time (earliest event → publication to Tapline){of}: {Quantiles(times, 0.5, 0.995)}";
    }

    /// <summary>Times by their quantiles, each its nearest rank, as the page's: "p50 6.9 ms, p99.5 93.7 ms" — "—", none.</summary>
    private static string Quantiles(ReadOnlySpan<double> times, params ReadOnlySpan<double> quantiles)
    {
        if (times.IsEmpty) return "—";
        var sorted = times.ToArray();
        Array.Sort(sorted);
        var text = new StringBuilder();
        foreach (var q in quantiles)
            text.Append(text.Length == 0 ? "" : ", ").Append($"p{q * 100:0.#} ")
                .Append(Ms(sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)]));
        return text.ToString();
    }

    /// <summary>A time as the page writes one: to a tenth of a millisecond under 100 ms, to the millisecond from there.</summary>
    private static string Ms(double ms)
    {
        return ms < 100 ? $"{ms:F1} ms" : $"{Math.Round(ms, MidpointRounding.AwayFromZero):F0} ms";
    }

    private void Fail(string what)
    {
        lock (_gate)
        {
            _failures.Add(what);
        }

        Line(what);
    }

    private static void Line(string what)
    {
        Console.Out.WriteLine(what);
        Console.Out.Flush();
    }

    /// <summary>The last answers' times, as many as it holds (the oldest let go), for their quantiles — the page's lags.</summary>
    private sealed class Lags(int capacity)
    {
        private readonly double[] _all = new double[capacity];
        private int _count, _next;

        /// <summary>Its times, in no order (quantiles need none).</summary>
        public ReadOnlySpan<double> Times => _all.AsSpan(0, _count);

        public void Add(double time)
        {
            _all[_next] = time;
            _next = (_next + 1) % _all.Length;
            _count = Math.Min(_count + 1, _all.Length);
        }
    }
}