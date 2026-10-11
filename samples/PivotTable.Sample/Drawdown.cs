using System.Diagnostics;
using System.Text.Json;
using Apache.Arrow;
using Chalk;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources;
using Tapline;

namespace PivotTable.Sample;

/// <summary>
///     The house's drawdown, live: its equity — its cash at each currency's rate, and what its open positions would
///     settle for at the marks — every 30 seconds over the last seven days, and now; its drawdown, from its high over the
///     hour before; and an alert when that's deeper than its seven days' 5th percentile, less a haircut, for five minutes
///     on end — with how close it is. Two statements over the brokerage's own tables (no table of their own):
///     <list type="bullet">
///         <item>
///             the threshold (<see cref="ThresholdSql" />): every minute (Pivot:DrawdownThresholdEvery), a query off the
///             provider's loop, reading the snapshots its scans start on — the seven days' drawdowns, their 5th percentile
///             (it moves slowly: a minute adds two points to 20,000);
///         </item>
///         <item>
///             live (<see cref="LiveSql" />): a standing view, kept incrementally (recomputed if ChalkQL refuses to, saying
///             why; Pivot:DrawdownIncremental false: recomputed), updated after the loop's refreshes and rebound when the
///             threshold moves, off the loop, one at a time — the last hour's points, the breach now and the hour's
///             episodes. With Pivot:Validate, a recomputed twin beside it, updated on its own and compared wherever the
///             view stood at the same version and trigger.
///         </item>
///     </list>
///     Published as JSON (a last value, the page's to tap), while someone taps it; nobody tapping, the view's let go.
/// </summary>
internal sealed class Drawdown(
    ILogger log,
    LastValueSource source,
    double haircutUsd,
    TimeSpan thresholdEvery,
    bool incremental,
    bool validate)
{
    /// <summary>The equity's points: every 30 seconds, as the market was marked (each instrument and rate on the half minute).</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>A point's drawdown: from the equity's high over the hour before it.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(1);

    /// <summary>The drawdowns the threshold is a percentile of: the last seven days'.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>The live points, and the episodes shown: the last hour's.</summary>
    public static readonly TimeSpan Live = TimeSpan.FromHours(1);

    /// <summary>How long a breach lasts before it's confirmed: five minutes on end.</summary>
    public static readonly TimeSpan Confirm = TimeSpan.FromMinutes(5);

    public const double Percentile = 0.05;

    private const long ThresholdRetainBytes = 512L << 20; // (the threshold's arena: the most it keeps between runs)

    // The house's equity at each point: the end of each half minute (from @from, its marks before @to; the current one's,
    // now) — its cash (each of its cash accounts' balance, at its currency's rate) and its positions at the marks: its side
    // of its clients' (the opposite of theirs: what it'd pay or be paid, were they all closed — their realized PnL is paid
    // already, in cash) and its hedges, each its quantity at the mark less its cost (its quantity at its average entry),
    // at its quote's rate. A point's mark and rate are each instrument's and currency's last in the half minute (every one
    // is marked as each begins and ends, and the logs keep each one's last in it: as an as-of lookup at its end finds). A
    // position's quantity and cost, and a balance, are running sums of their changes (each row's from the one before it,
    // the points among them: what's held at each, a change at its very time included — its peers, in a RANGE frame) — a window, not a lookup for each point: 20,000 points and 88
    // instruments are 1.8 million valuations, and a sort. Marks and rates are read through their time indexes: a range,
    // not every row kept.
    // The house's cash accounts: its own, in a deliverable.
    private const string CashAccounts = """
        cash_accounts AS (
          SELECT a.account_id, a.ccy_name FROM account a JOIN ccy c ON c.ccy_id = a.ccy_id
          WHERE a.cpty_id = 1 AND c.is_deliverable),
        """;

    private const string Equity = """
        marks_at AS (
          SELECT instrument_id, CASE WHEN t > @now THEN @now ELSE t END AS ts, price
          FROM (SELECT instrument_id, price, TIME_BUCKET(INTERVAL '30' SECOND, ts) + INTERVAL '30' SECOND AS t,
                       ROW_NUMBER() OVER (PARTITION BY instrument_id, TIME_BUCKET(INTERVAL '30' SECOND, ts)
                                          ORDER BY ts DESC) AS latest
                FROM marks WHERE ts >= @from AND ts < @to) x
          WHERE latest = 1),
        rates_at AS (
          SELECT symbol, CASE WHEN t > @now THEN @now ELSE t END AS ts, usd_rate
          FROM (SELECT symbol, usd_rate, TIME_BUCKET(INTERVAL '30' SECOND, ts) + INTERVAL '30' SECOND AS t,
                       ROW_NUMBER() OVER (PARTITION BY symbol, TIME_BUCKET(INTERVAL '30' SECOND, ts)
                                          ORDER BY ts DESC) AS latest
                FROM conversion_rate WHERE ts >= @from AND ts < @to) x
          WHERE latest = 1),
        book AS (
          SELECT instrument_id, update_time AS ts,
                 CAST(CASE WHEN cpty_id = 1 THEN 1 ELSE -1 END
                      * (amt_signed - LAG(amt_signed, 1, 0)
                           OVER (PARTITION BY account_id, channel ORDER BY update_time)) AS DOUBLE) AS dq,
                 CASE WHEN cpty_id = 1 THEN 1 ELSE -1 END
                   * (amt_signed * avg_prc - LAG(amt_signed * avg_prc, 1, 0)
                        OVER (PARTITION BY account_id, channel ORDER BY update_time)) AS dc
          FROM position_risk
          WHERE cpty_id <> 1 OR instrument_id IN (SELECT instrument_id FROM instrument WHERE venue <> 'House')),
        timeline AS (
          SELECT instrument_id, ts, 0 AS point, dq, dc, CAST(NULL AS DOUBLE) AS price FROM book
          UNION ALL
          SELECT instrument_id, ts, 1 AS point, CAST(0 AS DOUBLE), CAST(0 AS DOUBLE), price FROM marks_at),
        held AS (
          SELECT instrument_id, ts, point, price,
                 SUM(dq) OVER (PARTITION BY instrument_id ORDER BY ts RANGE UNBOUNDED PRECEDING) AS q,
                 SUM(dc) OVER (PARTITION BY instrument_id ORDER BY ts RANGE UNBOUNDED PRECEDING) AS c
          FROM timeline),
        positions AS (
          SELECT h.ts, SUM((h.q * h.price - h.c) * r.usd_rate) AS usd
          FROM held h JOIN instrument i ON i.instrument_id = h.instrument_id
          JOIN rates_at r ON r.symbol = i.quote AND r.ts = h.ts
          WHERE h.point = 1 GROUP BY h.ts),
        cash_moves AS (
          SELECT a.ccy_name AS symbol, j.update_time AS ts,
                 CAST(j.balance - LAG(j.balance, 1, 0) OVER (PARTITION BY j.account_id ORDER BY j.journal_id) AS DOUBLE) AS d
          FROM journal j JOIN cash_accounts a ON a.account_id = j.account_id),
        cash_timeline AS (
          SELECT symbol, ts, 0 AS point, d, CAST(NULL AS DOUBLE) AS usd_rate FROM cash_moves
          UNION ALL
          SELECT symbol, ts, 1 AS point, CAST(0 AS DOUBLE), usd_rate FROM rates_at
          WHERE symbol IN (SELECT ccy_name FROM cash_accounts)),
        cash AS (
          SELECT ts, SUM(balance * usd_rate) AS usd
          FROM (SELECT symbol, ts, point, usd_rate,
                       SUM(d) OVER (PARTITION BY symbol ORDER BY ts RANGE UNBOUNDED PRECEDING) AS balance
                FROM cash_timeline) b
          WHERE point = 1 GROUP BY ts),
        equity AS (
          SELECT p.ts, p.usd + COALESCE(c.usd, 0) AS equity FROM positions p LEFT JOIN cash c ON c.ts = p.ts)
        """;

    /// <summary>
    ///     The threshold: the 5th percentile of the drawdowns over the last seven days (from <c>@since</c>; the points from
    ///     <c>@from</c>, an hour before, are each point's hour behind it), how many, from and to when.
    /// </summary>
    public const string ThresholdSql = "WITH " + CashAccounts + Equity + ",\n" + """
        drawdowns AS (
          SELECT ts, equity - MAX(equity) OVER (ORDER BY ts RANGE BETWEEN INTERVAL '1' HOUR PRECEDING AND CURRENT ROW)
                   AS drawdown
          FROM equity)
        SELECT PERCENTILE_DISC(0.05) WITHIN GROUP (ORDER BY drawdown) AS p05, COUNT(*) AS points, MIN(ts) AS since,
               MAX(ts) AS until
        FROM drawdowns WHERE ts >= @since
        """;

    /// <summary>
    ///     Live, as a view: the last hour's points (their marks and rates from the clock's two hours back: each point's
    ///     hour behind it), each its drawdown, breached where it's below <c>@trigger_level</c> (the threshold, less the
    ///     haircut: rebindable, bound in a <c>WITH</c> and its one row cross-joined); an episode the breached points in a
    ///     row, from the first (or the hour's start). The equity is the threshold's, written for a view: the clock's
    ///     cut-offs (<c>CURRENT_TIMESTAMP</c>, the refresh's clock) where the threshold binds times; each half minute's last
    ///     mark and rate by <c>GROUP BY … MAX(ts)</c> and a join back; the current half minute's point at its end (what's
    ///     marked of it so far). Two kinds of row, keyed by kind and a time: now (the latest point) — the equity, its
    ///     capital (the treasury's transfers, at their rates then), its high, drawdown and breach (since when) — and each
    ///     episode in the hour: when it started and was last breached, its deepest drawdown.
    /// </summary>
    public const string LiveSql = """
        WITH level AS (SELECT CAST(@trigger_level AS DOUBLE) AS trigger_level),
        """ + CashAccounts + """
        recent_marks AS (SELECT instrument_id, ts, price FROM marks WHERE ts >= CURRENT_TIMESTAMP - INTERVAL '2' HOUR),
        marks_last AS (
          SELECT instrument_id, TIME_BUCKET(INTERVAL '30' SECOND, ts) + INTERVAL '30' SECOND AS t, MAX(ts) AS last
          FROM recent_marks GROUP BY instrument_id, TIME_BUCKET(INTERVAL '30' SECOND, ts)),
        marks_at AS (
          SELECT x.instrument_id, x.t AS ts, m.price
          FROM marks_last x JOIN recent_marks m ON m.instrument_id = x.instrument_id AND m.ts = x.last),
        recent_rates AS (SELECT symbol, ts, usd_rate FROM conversion_rate WHERE ts >= CURRENT_TIMESTAMP - INTERVAL '2' HOUR),
        rates_last AS (
          SELECT symbol, TIME_BUCKET(INTERVAL '30' SECOND, ts) + INTERVAL '30' SECOND AS t, MAX(ts) AS last
          FROM recent_rates GROUP BY symbol, TIME_BUCKET(INTERVAL '30' SECOND, ts)),
        rates_at AS (
          SELECT x.symbol, x.t AS ts, r.usd_rate
          FROM rates_last x JOIN recent_rates r ON r.symbol = x.symbol AND r.ts = x.last),
        book AS (
          SELECT instrument_id, update_time AS ts,
                 CAST(CASE WHEN cpty_id = 1 THEN 1 ELSE -1 END
                      * (amt_signed - LAG(amt_signed, 1, 0)
                           OVER (PARTITION BY account_id, channel ORDER BY update_time)) AS DOUBLE) AS dq,
                 CASE WHEN cpty_id = 1 THEN 1 ELSE -1 END
                   * (amt_signed * avg_prc - LAG(amt_signed * avg_prc, 1, 0)
                        OVER (PARTITION BY account_id, channel ORDER BY update_time)) AS dc
          FROM position_risk
          WHERE cpty_id <> 1 OR instrument_id IN (SELECT instrument_id FROM instrument WHERE venue <> 'House')),
        timeline AS (
          SELECT instrument_id, ts, 0 AS point, dq, dc, CAST(NULL AS DOUBLE) AS price FROM book
          UNION ALL
          SELECT instrument_id, ts, 1 AS point, CAST(0 AS DOUBLE), CAST(0 AS DOUBLE), price FROM marks_at),
        held AS (
          SELECT instrument_id, ts, point, price,
                 SUM(dq) OVER (PARTITION BY instrument_id ORDER BY ts RANGE UNBOUNDED PRECEDING) AS q,
                 SUM(dc) OVER (PARTITION BY instrument_id ORDER BY ts RANGE UNBOUNDED PRECEDING) AS c
          FROM timeline),
        valued AS (
          SELECT h.ts, (h.q * h.price - h.c) * r.usd_rate AS usd
          FROM held h JOIN instrument i ON i.instrument_id = h.instrument_id
          JOIN rates_at r ON r.symbol = i.quote AND r.ts = h.ts
          WHERE h.point = 1),
        positions AS (SELECT ts, SUM(usd) AS usd FROM valued GROUP BY ts),
        cash_moves AS (
          SELECT a.ccy_name AS symbol, j.update_time AS ts,
                 CAST(j.balance - LAG(j.balance, 1, 0) OVER (PARTITION BY j.account_id ORDER BY j.journal_id) AS DOUBLE) AS d
          FROM journal j JOIN cash_accounts a ON a.account_id = j.account_id),
        cash_timeline AS (
          SELECT symbol, ts, 0 AS point, d, CAST(NULL AS DOUBLE) AS usd_rate FROM cash_moves
          UNION ALL
          SELECT symbol, ts, 1 AS point, CAST(0 AS DOUBLE), usd_rate FROM rates_at
          WHERE symbol IN (SELECT ccy_name FROM cash_accounts)),
        cash_held AS (
          SELECT symbol, ts, point, usd_rate,
                 SUM(d) OVER (PARTITION BY symbol ORDER BY ts RANGE UNBOUNDED PRECEDING) AS balance
          FROM cash_timeline),
        cash AS (SELECT ts, SUM(balance * usd_rate) AS usd FROM cash_held WHERE point = 1 GROUP BY ts),
        equity AS (
          SELECT p.ts, p.usd + COALESCE(c.usd, 0) AS equity FROM positions p LEFT JOIN cash c ON c.ts = p.ts),
        capital AS (
          SELECT SUM(j.amt_signed * j.usd_rate) AS usd
          FROM journal j JOIN posting p ON p.posting_id = j.posting_id JOIN cash_accounts a ON a.account_id = j.account_id
          WHERE p.code = 3),
        peaks AS (
          SELECT ts, equity,
                 MAX(equity) OVER (ORDER BY ts RANGE BETWEEN INTERVAL '1' HOUR PRECEDING AND CURRENT ROW) AS peak
          FROM equity),
        points AS (
          SELECT p.ts, p.equity, p.peak, p.equity - p.peak AS drawdown, p.equity - p.peak < l.trigger_level AS breached
          FROM peaks p CROSS JOIN level l WHERE p.ts >= CURRENT_TIMESTAMP - INTERVAL '1' HOUR),
        turns AS (
          SELECT ts, equity, peak, drawdown, breached, LAG(breached, 1, FALSE) OVER (ORDER BY ts) AS was FROM points),
        episodes AS (
          SELECT ts, equity, peak, drawdown, breached,
                 MAX(CASE WHEN breached AND NOT was THEN ts END) OVER (ORDER BY ts RANGE UNBOUNDED PRECEDING) AS started
          FROM turns),
        latest AS (SELECT MAX(ts) AS ts FROM episodes)
        SELECT CAST('now' AS VARCHAR(8)) AS kind, TIMESTAMP '2000-01-01 00:00:00' AS key_ts, e.ts,
               CASE WHEN e.breached THEN e.started END AS started, e.equity, c.usd AS capital, e.peak, e.drawdown,
               e.breached
        FROM episodes e JOIN latest l ON e.ts = l.ts CROSS JOIN capital c
        UNION ALL
        SELECT CAST('episode' AS VARCHAR(8)), started, MAX(ts), started, CAST(NULL AS DOUBLE), CAST(NULL AS DOUBLE),
               CAST(NULL AS DOUBLE), MIN(drawdown), TRUE
        FROM episodes WHERE breached GROUP BY started
        """;

    private static readonly ViewMapper<LiveKey, LiveRow> MapLive = static (in ViewRow row, out LiveKey key, out LiveRow value) =>
    {
        key = default; // (written whole, then its fields: the view compares and hashes their bytes)
        key.Kind = row.GetUtf8(0).SequenceEqual("now"u8) ? 0 : 1;
        key.Ts = row.GetDateTime(1).Ticks;
        value = default;
        value.Ts = row.GetDateTime(2).Ticks;
        value.Started = row.IsNull(3) ? 0 : row.GetDateTime(3).Ticks;
        value.Equity = Cents(row, 4);
        value.Capital = Cents(row, 5);
        value.Peak = Cents(row, 6);
        value.Drawdown = Cents(row, 7);
        value.Breached = row.GetBoolean(8) ? (byte)1 : (byte)0;
    };

    private readonly HashSet<string> _failures = []; // (each logged in full once)
    private readonly Dictionary<string, object?> _thresholdValues = new();
    private readonly Replica _replica = new(); // (the advance's: the view's rows as its drains left them)
    private ExecutionArena? _thresholdArena;
    private PreparedQuery? _threshold;
    private DateTime _thresholdDue = DateTime.MinValue;
    private Task<Threshold?>? _thresholdRun;
    private Threshold? _known;
    private StandingView<LiveKey, LiveRow>? _view; // (the advance's, or the loop's while none runs)
    private ViewCursor _cursor = ViewCursor.Start;
    private double? _bound; // (the trigger the view holds: none, nothing's breached)
    private string? _refusal; // (why it's recomputed, if ChalkQL refused to keep it)
    private Task<Advanced>? _advance;
    private StandingView<LiveKey, LiveRow>? _twin; // (validating: the check's, or the loop's while none runs)
    private double? _twinBound;
    private Task<Checked>? _check;
    private bool _dirty = true, _trimmed;
    private int _updates, _rebinds, _compared, _mismatches; // (since the last report; mismatches since the start)
    private double _updateMs, _maxMs, _rebindMs;
    private string _made = "";

    /// <summary>
    ///     What it's done since the last report: the live view's updates, their mean and longest, its rebinds, what it
    ///     keeps; the threshold's last run. None: empty.
    /// </summary>
    public string Report()
    {
        if (_updates == 0 && _rebinds == 0 && _known is null) return "";
        var view = Volatile.Read(ref _view);
        var report = $"live {(view is null ? "no view" : Kept(view))}: {_updates} updates, mean {(_updates == 0 ? 0 : _updateMs / _updates):F1} ms, max {_maxMs:F1} ms"
                     + (_rebinds > 0 ? $", {_rebinds} rebinds, mean {_rebindMs / _rebinds:F1} ms" : "")
                     + (view is null ? "" : $", {view.Count} rows, state {view.StateBytes >> 20} MB")
                     + (_made.Length > 0 ? $" ({_made})" : "")
                     + (validate ? $"; against its recomputed twin {_compared} compared, {_mismatches} differing since the start" : "")
                     + (_known is { } t
                         ? $"; threshold {t.P05:N0} USD ({t.Points:N0} points, {t.Ms:N0} ms; its arena: peak {_thresholdArena?.PeakBytes >> 20} MB, working set {_thresholdArena?.WorkingSetBytes >> 20} MB, retained {_thresholdArena?.RetainedBytes >> 20} MB, heap fallbacks {_thresholdArena?.HeapFallbackBytes >> 20} MB)"
                         : "");
        (_updates, _updateMs, _maxMs, _rebinds, _rebindMs, _compared, _made) = (0, 0, 0, 0, 0, 0, "");
        return report;
    }

    /// <summary>The tables changed: the view's updated, at once if none's running.</summary>
    public void Changed()
    {
        _dirty = true;
    }

    /// <summary>
    ///     The loop's turn (after its refresh, if any): an advance that's done published; the threshold run when due, and
    ///     the view advanced after a change or a new trigger — each on the thread pool, one of each at a time — while
    ///     someone taps it.
    /// </summary>
    public void Tick(ChalkEngine engine, DateTime now, CancellationToken ct)
    {
        if (_thresholdRun is { IsCompleted: true } thresholdDone)
        {
            _thresholdRun = null;
            if (thresholdDone.IsCompletedSuccessfully && thresholdDone.Result is { } known) _known = known; // (rebound at once)
            else if (thresholdDone.Exception is { } e) Failed("the threshold's run", e.GetBaseException());
        }

        if (_advance is { IsCompleted: true } advanced)
        {
            _advance = null;
            if (advanced.IsCompletedSuccessfully)
                Took(advanced.Result);
            else if (advanced.Exception is { } e)
            {
                Failed("the live view", e.GetBaseException());
                LetGo(); // (made again at the next turn: its readers sent it whole)
            }
        }

        if (_check is { IsCompleted: true } checkDone)
        {
            _check = null;
            if (checkDone.IsCompletedSuccessfully) Compared(checkDone.Result);
            else if (checkDone.Exception is { } e)
            {
                Failed("the live view's twin", e.GetBaseException());
                LetGoTwin();
            }
        }

        if (source.Routes == 0) // (nobody tapping: nothing run, the view let go — made again, and the threshold run, when someone does)
        {
            if (_thresholdRun is null && _advance is null && _check is null && !_trimmed)
            {
                LetGo();
                LetGoTwin();
                _thresholdArena?.Dispose();
                (_thresholdArena, _trimmed, _thresholdDue) = (null, true, DateTime.MinValue);
            }

            return;
        }

        _trimmed = false;
        if (_thresholdRun is null && now >= _thresholdDue)
        {
            _thresholdDue = now + thresholdEvery;
            _thresholdRun = Task.Run(() => RunThreshold(engine, now, ct), ct);
        }

        var trigger = _known is { } t ? t.P05 - haircutUsd : (double?)null;
        if (_advance is null && (_dirty || _view is null || trigger != _bound))
        {
            _dirty = false;
            var threshold = _known;
            _advance = Task.Run(() => Advance(engine, trigger, threshold, ct), ct);
        }

        if (validate && _check is null && _view is not null)
            _check = Task.Run(() => Check(engine, trigger, ct), ct);
    }

    /// <summary>An advance's figures counted, and its reading published.</summary>
    private void Took(Advanced a)
    {
        if (a.Made is { } made) _made = made;
        else if (a.Rebound) (_rebinds, _rebindMs) = (_rebinds + 1, _rebindMs + a.Ms);
        else (_updates, _updateMs, _maxMs) = (_updates + 1, _updateMs + a.Ms, Math.Max(_maxMs, a.Ms));
        if (a.Reading is { } reading) Publish(reading, a.Threshold);
    }

    /// <summary>A check's outcome counted: a difference logged (the first five).</summary>
    private void Compared(Checked c)
    {
        if (c.Matched is not { } matched) return; // (the view never stood where the twin did)
        _compared++;
        if (!matched && ++_mismatches <= 5)
            log.LogWarning("Drawdown: the live view differs from its recomputed twin at version {Version}: {Why}", c.Version, c.Mismatch);
    }

    /// <summary>The view let go (the loop's, while no advance runs): its reader from the start.</summary>
    private void LetGo()
    {
        if (_view is { } view) _ = view.DisposeAsync().AsTask();
        (_view, _cursor, _bound) = (null, ViewCursor.Start, null);
        _replica.Rows.Clear();
    }

    /// <summary>The twin let go (the loop's, while no check runs).</summary>
    private void LetGoTwin()
    {
        if (_twin is { } twin) _ = twin.DisposeAsync().AsTask();
        (_twin, _twinBound) = (null, null);
    }

    /// <summary>A run failed: logged — its stack the first time it fails so, its message after (it runs again when due).</summary>
    private void Failed(string run, Exception e)
    {
        if (_failures.Add($"{run}: {e.Message}")) log.LogWarning(e, "Drawdown: {Run} failed", run);
        else log.LogDebug("Drawdown: {Run} failed again — {Message}", run, e.Message);
    }

    /// <summary>The last whole half minute's end at or before <paramref name="at" />.</summary>
    private static DateTime Floor(DateTime at)
    {
        return new DateTime(at.Ticks - at.Ticks % Step.Ticks, DateTimeKind.Unspecified);
    }

    private async Task<Threshold?> RunThreshold(ChalkEngine engine, DateTime now, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        _threshold ??= await engine.PrepareAsync(ThresholdSql, new PrepareOptions(), ct);
        // (it keeps its working set — what its pools must hold for a run to rent nothing from the heap: ~434 MB, though
        // no more than ~255 MB is out at once — and a margin, up to RetainBytes, a ceiling; kept less, a run's end would
        // let go of what the next rents again, whole arrays, from the heap. Logged each run)
        _thresholdArena ??= new ExecutionArena(new ArenaOptions { MaxBytes = ThresholdRetainBytes, RetainBytes = ThresholdRetainBytes });
        var to = Floor(now);
        var since = to - Window;
        // (ts is a wall clock, TIMESTAMP: bound as one — the time in UTC, of kind Unspecified)
        _thresholdValues["from"] = since - Horizon - Step;
        _thresholdValues["to"] = _thresholdValues["now"] = to; // (whole half minutes: the last ends at to)
        _thresholdValues["since"] = since;
        Threshold? found = null;
        await using (var execution = await engine.ExecuteAsync(_threshold, _thresholdValues, _thresholdArena, ct))
        {
            await foreach (var batch in execution.Batches.WithCancellation(ct))
                using (batch)
                {
                    if (batch.Length == 0 || Number(batch.Column(0), 0) is not { } p05) continue;
                    found = new Threshold(p05, (long)(Number(batch.Column(1), 0) ?? 0), Time(batch.Column(2), 0),
                        Time(batch.Column(3), 0), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
        }

        var a = _thresholdArena; // (once the execution's disposed: what it rented and didn't hand back is outstanding)
        if (found is { } f)
            log.LogInformation(
                "Drawdown: threshold {P05:N0} USD (the 5th percentile of {Points:N0} drawdowns, {Since:u} to {Until:u}) in {Ms:N0} ms; its arena: outstanding {Outstanding:N0} B, retained {Retained:N0} B, rented {Rented:N0} B in {Rentals:N0}, heap fallbacks {Fallbacks:N0} ({FallbackBytes:N0} B), unpooled returns {Unpooled:N0} B, trimmed {Trimmed:N0} B, working set {WorkingSet:N0} B of {Retain:N0} B kept",
                f.P05, f.Points, f.Since, f.Until, f.Ms, a.OutstandingBytes, a.RetainedBytes, a.RentedBytes, a.Rentals,
                a.HeapFallbacks, a.HeapFallbackBytes, a.UnpooledReturnBytes, a.TrimmedBytes, a.WorkingSetBytes, ThresholdRetainBytes);

        return found;
    }

    /// <summary>
    ///     The view made (the first time, or after it's let go), rebound to a new trigger, or updated, and drained: its
    ///     rows read as they stand now, and where they stood kept for a check.
    /// </summary>
    private async Task<Advanced> Advance(ChalkEngine engine, double? trigger, Threshold? threshold, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        string? made = null;
        var rebound = false;
        if (_view is null)
        {
            var view = await Make(engine, trigger, incremental, ct);
            (_bound, _cursor) = (trigger, ViewCursor.Start);
            _replica.Rows.Clear();
            Volatile.Write(ref _view, view);
            made = $"made in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:N0} ms, {Kept(view)}";
            log.LogInformation("Drawdown: the live view {Made}, {Rows} rows, state {State:N0} B", made, view.Count, view.StateBytes);
        }
        else if (trigger != _bound)
        {
            await _view.RebindAsync(Level(trigger), ct);
            (_bound, rebound) = (trigger, true);
        }
        else
        {
            await _view.UpdateAsync(ct);
        }

        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        (_replica.Changed, _replica.Bound) = (false, trigger);
        while (_view.Drain(ref _cursor, _replica, 1 << 16) == DrainOutcome.Partial)
        {
        }

        var reading = _replica.Changed || rebound || made is not null ? Read(trigger, ms) : null;
        return new Advanced(reading, threshold, made, rebound, ms);
    }

    /// <summary>
    ///     Validating: the recomputed twin made, rebound or updated, on its own (a run of the statement takes longer
    ///     than refreshes come), and compared with the view where the view stood at the same version and trigger.
    /// </summary>
    private async Task<Checked> Check(ChalkEngine engine, double? trigger, CancellationToken ct)
    {
        if (_twin is null) (_twin, _twinBound) = (await Make(engine, trigger, false, ct), trigger);
        else if (trigger != _twinBound)
        {
            await _twin.RebindAsync(Level(trigger), ct);
            _twinBound = trigger;
        }
        else
        {
            await _twin.UpdateAsync(ct);
        }

        var version = _twin.Version;
        if (!_replica.Stood(version, trigger, out var checksum, out var count)) return new Checked(null, null, version);
        var matched = checksum == _twin.Checksum && count == _twin.Count;
        return new Checked(matched, matched ? null
            : $"{count} rows (0x{checksum:X8}) against {_twin.Count} (0x{_twin.Checksum:X8}), the trigger {trigger?.ToString("N0") ?? "none"}", version);
    }

    /// <summary>The view: kept incrementally if asked and ChalkQL can (else recomputed, saying why), or recomputed.</summary>
    private async Task<StandingView<LiveKey, LiveRow>> Make(ChalkEngine engine, double? trigger, bool kept, CancellationToken ct)
    {
        if (kept)
            try
            {
                return await engine.MaterializeAsync(LiveSql, Options(ViewMaintenance.Incremental, trigger), ct);
            }
            catch (StandingViewException e) when (e.Code == ChalkErrorCodes.UnsupportedInView)
            {
                if (_refusal is null) log.LogWarning("Drawdown: the live view can't be kept incrementally, so it's recomputed: {Refusal}", e.Message);
                _refusal = e.Message;
            }

        return await engine.MaterializeAsync(LiveSql, Options(ViewMaintenance.Recompute, trigger), ct);
    }

    private static ViewOptions<LiveKey, LiveRow> Options(ViewMaintenance maintenance, double? trigger)
    {
        return new ViewOptions<LiveKey, LiveRow>
        {
            Key = ["kind", "key_ts"],
            Map = MapLive,
            RowHash = RowHash.Of,
            Exactness = ViewExactness.EqualUpToRounding, // (its figures summed as DOUBLE: kept to the cent)
            Maintenance = maintenance,
            PlanSwap = maintenance == ViewMaintenance.Incremental ? ViewPlanSwap.LowJitter : ViewPlanSwap.AtNextUpdate,
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows, // (every table in process)
            StateBudget = 1L << 30, // (~300 MB kept incrementally; a new plan built beside it isn't counted)
            Parameters = Level(trigger),
            Rebindable = [new RebindableParameter("trigger_level")]
        };
    }

    /// <summary>The trigger as bound: none, nothing's breached.</summary>
    private static Dictionary<string, object?> Level(double? trigger)
    {
        return new Dictionary<string, object?> { ["trigger_level"] = trigger ?? double.MinValue / 4 };
    }

    private string Kept(StandingView<LiveKey, LiveRow> view)
    {
        return view.Maintenance == ViewMaintenance.Incremental ? "kept incrementally"
            : _refusal is { } why && incremental ? $"recomputed (ChalkQL can't keep it incrementally: {why})"
            : "recomputed";
    }

    /// <summary>The view's rows as they stand: now (its time no later than the clock), and the hour's episodes, latest first.</summary>
    private Reading? Read(double? trigger, double ms)
    {
        var clock = DateTime.UtcNow;
        Point? now = null;
        var episodes = new List<Episode>();
        foreach (var (key, row) in _replica.Rows)
        {
            var at = At(row.Ts, clock); // (the current half minute's point is at its end: shown as of now)
            var started = row.Started == 0 ? (DateTime?)null : new DateTime(row.Started);
            if (key.Kind == 0)
            {
                var drawdown = row.Drawdown / 100.0;
                now = new Point(at, started, row.Equity / 100.0, row.Capital / 100.0, row.Peak / 100.0, drawdown,
                    trigger is { } h ? drawdown - h : null, trigger is { } x && x < 0 ? drawdown / x : null,
                    started is { } s && at - s >= Confirm);
            }
            else
            {
                episodes.Add(new Episode(started ?? default, at, row.Drawdown / 100.0, started is { } s && at - s >= Confirm));
            }
        }

        episodes.Sort(static (a, b) => b.Started.CompareTo(a.Started));
        return now is { } p ? new Reading(p, [.. episodes], trigger, ms) : null;
    }

    private static DateTime At(long ticks, DateTime clock)
    {
        return ticks > clock.Ticks ? clock : new DateTime(ticks);
    }

    private void Publish(Reading live, Threshold? threshold)
    {
        var p = live.Now;
        source.Publish(JsonSerializer.SerializeToUtf8Bytes(new
        {
            asOf = Utc(p.At),
            equity = Math.Round(p.Equity, 2), capital = Math.Round(p.Capital, 2), pnl = Math.Round(p.Equity - p.Capital, 2),
            peak = Math.Round(p.Peak, 2), drawdown = Math.Round(p.Drawdown, 2),
            headroom = p.Headroom is { } h ? Math.Round(h, 2) : (double?)null,
            proximity = p.Proximity is { } x ? Math.Round(x, 4) : (double?)null,
            threshold = threshold is { } t
                ? new
                {
                    p05 = Math.Round(t.P05, 2), haircut = haircutUsd, trigger = Math.Round(t.P05 - haircutUsd, 2),
                    t.Points, since = Utc(t.Since), until = Utc(t.Until), ms = Math.Round(t.Ms, 1)
                }
                : null,
            breach = p.Started is { } s
                ? new { since = Utc(s), seconds = Math.Round((p.At - s).TotalSeconds), confirmed = p.Confirmed }
                : null,
            episodes = live.Episodes.Select(e => new
            {
                started = Utc(e.Started), last = Utc(e.Last), worst = Math.Round(e.Worst, 2), e.Confirmed
            }),
            horizonMinutes = Horizon.TotalMinutes, windowDays = Window.TotalDays, liveMinutes = Live.TotalMinutes,
            confirmMinutes = Confirm.TotalMinutes, stepSeconds = Step.TotalSeconds, percentile = Percentile,
            ms = Math.Round(live.Ms, 1),
            maintained = Volatile.Read(ref _view)?.Maintenance == ViewMaintenance.Incremental ? "incrementally" : "recomputed"
        }));
    }

    private static long Cents(in ViewRow row, int column)
    {
        return row.IsNull(column) ? 0 : (long)Math.Round(row.GetDouble(column) * 100, MidpointRounding.AwayFromZero);
    }

    private static string Utc(DateTime at)
    {
        return DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("O");
    }

    private static double? Number(IArrowArray array, int row)
    {
        if (array.IsNull(row)) return null;
        return array switch
        {
            DoubleArray a => a.GetValue(row),
            Decimal128Array a => (double?)a.GetValue(row),
            Int64Array a => a.GetValue(row),
            Int32Array a => a.GetValue(row),
            _ => null
        };
    }

    private static DateTime Time(IArrowArray array, int row)
    {
        return array is TimestampArray t && t.GetTimestamp(row) is { } at ? at.UtcDateTime : default;
    }

    /// <summary>
    ///     The view's rows, as its drains leave them: a reader's replica (the now row and the hour's episodes) — and,
    ///     for a check, where the view last stood: each version it committed, at the trigger it held, its checksum and
    ///     count.
    /// </summary>
    private sealed class Replica : IViewSink<LiveKey, LiveRow>
    {
        public readonly Dictionary<LiveKey, LiveRow> Rows = [];
        public bool Changed;
        public double? Bound;
        private readonly (long Version, double? Bound, uint Checksum, int Count)[] _stood = new (long, double?, uint, int)[256];
        private int _next;

        public void Reset()
        {
            Rows.Clear();
            Changed = true;
        }

        public void Upsert(in LiveKey key, in LiveRow row)
        {
            Rows[key] = row;
            Changed = true;
        }

        public void Delete(in LiveKey key)
        {
            Changed |= Rows.Remove(key);
        }

        public void Commit(long version, uint checksum)
        {
            lock (_stood) _stood[_next++ & (_stood.Length - 1)] = (version, Bound, checksum, Rows.Count);
        }

        /// <summary>Whether the view stood at <paramref name="version" /> with <paramref name="bound" />, and how.</summary>
        public bool Stood(long version, double? bound, out uint checksum, out int count)
        {
            lock (_stood)
                foreach (var (v, b, c, n) in _stood)
                    if (v == version && b == bound && (v != 0 || n != 0))
                    {
                        (checksum, count) = (c, n);
                        return true;
                    }

            (checksum, count) = (0, 0);
            return false;
        }
    }

    /// <summary>A row's key: its kind (0 now, 1 an episode) and, for an episode, when it started.</summary>
    private struct LiveKey : IEquatable<LiveKey>
    {
        public long Kind, Ts;

        public readonly bool Equals(LiveKey other)
        {
            return Kind == other.Kind && Ts == other.Ts;
        }

        public override readonly bool Equals(object? obj)
        {
            return obj is LiveKey other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return HashCode.Combine(Kind, Ts);
        }
    }

    /// <summary>A row: its point's time (an episode's last), its breach's start, its figures in cents, breached or not.</summary>
    private struct LiveRow
    {
        public long Ts, Started, Equity, Capital, Peak, Drawdown;
        public byte Breached;
    }

    private readonly record struct Threshold(double P05, long Points, DateTime Since, DateTime Until, double Ms);

    private readonly record struct Point(
        DateTime At,
        DateTime? Started,
        double Equity,
        double Capital,
        double Peak,
        double Drawdown,
        double? Headroom,
        double? Proximity,
        bool Confirmed);

    private readonly record struct Episode(DateTime Started, DateTime Last, double Worst, bool Confirmed);

    private sealed record Reading(Point Now, Episode[] Episodes, double? Trigger, double Ms);

    private sealed record Advanced(Reading? Reading, Threshold? Threshold, string? Made, bool Rebound, double Ms);

    private sealed record Checked(bool? Matched, string? Mismatch, long Version);
}
