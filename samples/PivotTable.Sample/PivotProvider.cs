using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk;
using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Tapline;
using PivotTable.Contracts;
using Array = System.Array;

namespace PivotTable.Sample;

/// <summary>
///     The "pivot" service's data provider, in process: a brokerage's book — its clients' perpetuals ledger
///     (counterparties,
///     accounts, postings, journal, positions — the clients', the house's side of them and its hedges), the hedge fills,
///     the house's working capital, the instruments and their marks
///     (each venue's: the house's perpetuals at the index, the venues' basis and all) and the conversion rates, in ChalkQL
///     —
///     and a live pivot of them for each statement a page asks for — and the house's drawdown (<see cref="Drawdown" />)
///     while someone taps it; a pivot for each statement a
///     page asks for, its topic named by the hash of the statement as prepared, so pages asking alike share one: one query
///     a
///     run for all of them.
///     <para>
///         Step 1's parameter is the page's statement (JSON). The provider prepares it with ChalkQL — SQL done safely: it
///         only
///         reads, and only the brokerage's tables — and checks it answers what a pivot is (<see cref="Pivot.Contract" />).
///         Anything it
///         can't serve it refuses, saying why — ChalkQL's own message, say — with a correlation id it logs, as the router
///         does and
///         the page will: one needle, three haystacks.
///     </para>
///     <para>
///         The ledger and the hedges are append-only: each client trade leaves the client's position after it and the
///         house's, each hedge fill the hedge position after it, and nothing keeps daily checkpoints. A statement derives each day's positions from
///         those rows, as of the day's close — reckoned in the statement from time_zones: today's is now. While a pivot
///         has subscribers, the provider runs
///         its statement whenever what it reads changes (after a backoff) and posts the answer whole: a snapshot, totals
///         included, with the checksum of the pivot it materializes. The router applies the newest against the table it
///         holds,
///         and sends subscribers the cells that changed; it checks its table against the provider's checksum, and each
///         page its
///         copy against the router's. A pivot nobody subscribes to stops (a post answered 202), and is let go after a
///         minute.
///     </para>
/// </summary>
internal sealed partial class PivotProvider : BackgroundService, IDataProvider
{
    // The brokerage's pace: live, a client trade every few seconds on average (in bursts) unless a page's ?trades sets it
    // (up to 100 million a day: one brokerage, so every page's); the hedger after each burst, and each asset's hedge looked
    // at every three seconds or so besides — more often as the pace rises. Its history primed at the pace (Prime): the last
    // seven days at it, the four before them quiet; past 50,000 trades a day, each simulated trade stands for several.
    public const double DefaultTradesPerDay = 25_920, MaxTradesPerDay = 100_000_000; // (0.3 a second; at most ~1,160)
    private const double HedgesPerSecond = 0.3, QuietTradesPerHour = 8, MaxSimulatedPerDay = 50_000;
    private const int PrimedDays = 7;
    private const int LedgerKept = 100_000;

    private const int
        MaxSql = 16 * 1024, MaxTexts = 8; // (a statement with every level of totals: a dozen GROUP BYs, ~7 KB)

    // What a pivot costs, held to budgets rather than a count. Memory: its ChalkQL arena (enforced by ChalkQL: it starts
    // small and doubles when a run needs more, while the budget has room), its ingest's staging (two buffers: one filling,
    // the router's), and its cells in the router's table. Time: what running the pivots takes of each second.
    private const int Staging = 576 * 1024; // a pivot's ingest capacity: a whole snapshot staged
    private const int MaxCells = 4000; // a staged cell is 136 bytes (key 96, row 40): a snapshot always fits
    private const int CellBytes = 200; // a cell in the router's table — key, row, its slot's bookkeeping (an estimate)
    private const long ArenaStart = 1 << 20, ArenaMax = 32 << 20;
    private const double TimeShare = 0.5; // new pivots refused while running them takes more than this of the time
    private const int MaxPlans = 64;

    /// <summary>
    ///     A cell's precision: its figures to <see cref="Pivot.Places" /> places — its volume to
    ///     <see cref="Pivot.VolumePlaces" />.
    /// </summary>
    private const decimal Scale = 100_000_000, VolumeScale = 100;

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(60), Compaction = TimeSpan.FromSeconds(30);

    // The loop runs on what happens, not a clock: the market and the rates tick every 250 ms; the clients' trades land in
    // bursts, as they happen (Bursts), the hedger a moment after; ChalkQL is loaded as they do — and a pivot runs again
    // once what it reads has changed, after a backoff from its last run as long as that run (at most half a core each: a
    // heavy pivot backs off by itself), so how stale its answer gets is a multiple of what it takes to run — about three
    // runs at worst (a change landing as one starts: that run, the backoff, the next). A pivot admitted, or come back to
    // (nobody was watching: its answer's old), runs at once.
    private static readonly TimeSpan MarketTick = TimeSpan.FromMilliseconds(250),
        StatsEvery = TimeSpan.FromSeconds(1),
        CompactEvery =
            TimeSpan.FromSeconds(
                30); // (a compaction's refresh, and the updates after it, cost more whatever it removes: rare enough to fall past p99.5 — Pivot:CompactEvery)

    // What's kept, whatever the pace — ChalkQL's as-of joins read all of a table (and a statement's totals once for each
    // level): of the positions (the clients', the house's and its hedges'), the marks and the rates, each one's last row in
    // each half minute over the last seven days and two hours (Fine: the house's drawdown's points, and the hour behind
    // them), in each hour before, and the latest — what a day's open and close look up (both on the hour), a drawdown's
    // point (on the half minute), and now — compacted every 30 s (appended between); the ledger (postings and journal
    // entries) and the fills, the latest 100,000, trimmed every 30 s, when ChalkQL's tables of them are replaced — but for
    // the house's cash: its balances kept as the other logs are (the drawdown's equity), and its working capital's postings.
    private static readonly TimeSpan Sample = TimeSpan.FromHours(1), Fine = Drawdown.Step;

    private static readonly TimeSpan FineKept = Drawdown.Window + TimeSpan.FromHours(2);

    /// <summary>The clock cell's key: a label no statement answers (a control character), so it's none of the grid's.</summary>
    private static readonly PivotKey ClockKey = PivotKey.Of(PivotKey.ClockRow, "")!.Value;

    private readonly Channel<Topic> _admitted =
        Channel.CreateUnbounded<Topic>(new UnboundedChannelOptions { SingleReader = true }); // new pivots: run at once

    private readonly Waker _wake = new(); // the loop's sleep: until what's due next, a pivot admitted, or stopping

    private readonly Dictionary<string, Topic>
        _byText = []; // by statement text and the values bound: each topic's (a few)

    private readonly ILoggerFactory? _chalkLog; // ChalkQL's own logging, through the app's (Pivot:ChalkLog)
    private readonly TimeSpan _compactEvery; // how often the as-of logs are compacted (Pivot:CompactEvery)

    private readonly double
        _deadbandBps; // a mark or rate applied once it's moved this far (Pivot:DeadbandBps; 0: every tick)

    private readonly bool
        _deferPlanning; // the loop's refreshes leaving statistics and plans to ChalkQL's background (Pivot:DeferPlanning)

    private readonly Lock _gate = new();

    // Where its answers stand (ChalkQL's versions, numbered by its engine: one a refresh) and changes awaiting theirs.
    private long _version;
    private double _pace = -1; // the pace its last refresh applied (-1: none yet)
    private readonly List<(double PerSecond, TaskCompletionSource<long> Applied)> _paces = []; // (under _gate)
    private readonly TimeSpan _heartbeat; // or once the last applied is this old (Pivot:Heartbeat)
    private readonly ILogger<PivotProvider> _log;
    private readonly long _memoryBudget;

    private readonly double?
        _planMargin; // an incremental view's new plan taken only if the planner prices it this fraction cheaper (Pivot:PlanMargin, from 0 up to 1: 0 any cheaper; unset: every plan)

    private readonly Queue<string> _planned = new(); // (their texts, oldest first: at most MaxPlans kept)
    private readonly PlanningLog _planning; // ChalkQL's planning as it happens: counted, and logged when it moves

    private readonly Dictionary<string, PreparedQuery>
        _plans = []; // by statement text: prepared queries, retrieved whatever's bound (a few)

    private readonly ViewPlanSwap
        _planSwap; // how an incremental view takes a new plan, unless its page says (Low jitter): Pivot:PlanSwap, LowJitter (built beside the old) or AtNextUpdate

    private readonly bool
        _preparedCompaction; // a rewrite of ChalkQL's tables prepared off the loop, then committed (Pivot:PreparedCompaction)

    private readonly Dictionary<string, Task<PreparedQuery>>
        _preparing = []; // by statement text: prepares in flight, each awaited by every page asking meanwhile

    private readonly Publisher _publisher;

    private readonly TaskCompletionSource<ChalkEngine?>
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ConcurrentDictionary<string, Resolution> _resolutions = new();
    private readonly RouterSessions _routers;
    private readonly Dictionary<string, Topic> _topics = []; // by key: logical digest, and the values bound (exactly)

    private readonly bool
        _trackPlans; // a view's updates seen by a tracker: each new plan it runs logged (Pivot:TrackPlans)

    private readonly bool _validate;

    private readonly AllocationSampler?
        _allocations; // what the process allocates, by type, sampled — logged every 10 s (Pivot:AllocationSampling)

    private readonly Volatility
        _volatility; // how much the simulated prices move (Pivot:Volatility: Realistic, or Demo)

    private readonly Drawdown _drawdown; // the house's drawdown alert (HouseDrawdown)

    private double _pivotMs; // what running the pivots takes a second, on average (smoothed: a spike doesn't refuse)
    private long _prepared, _sharedByText, _sharedByDigest;
    private readonly LoadMeter _load = new(); // its work over the time that passes: its health reports' load
    private CancellationToken _stopping; // (the provider's: a prepare isn't any one page's to cancel)

    private double _tradesPerSecond = DefaultTradesPerDay / 86_400;

    public PivotProvider(Publisher publisher, IConfiguration configuration, ILogger<PivotProvider> log,
        ILoggerFactory loggers)
    {
        _publisher = publisher;
        _log = log;
        _memoryBudget = (configuration.GetValue<long?>("Pivot:MemoryBudgetMB") ?? 128) << 20;
        _preparedCompaction =
            configuration.GetValue<bool?>("Pivot:PreparedCompaction") ??
            true; // (false: ours rewritten on the loop, ChalkQL's replaced in a refresh)
        _deferPlanning =
            configuration.GetValue<bool?>("Pivot:DeferPlanning") ??
            true; // (false: each refresh's statistics and plans under its lock)
        _validate = configuration
            .GetValue<
                bool>("Pivot:Validate"); // each view update checked against its statement run in full at the same epoch (logged)
        _allocations = configuration.GetValue<bool>("Pivot:AllocationSampling") ? new AllocationSampler() : null;
        _compactEvery = configuration.GetValue<TimeSpan?>("Pivot:CompactEvery") is { } every && every > TimeSpan.Zero
            ? every
            : CompactEvery;
        _deadbandBps = Math.Max(0, configuration.GetValue<double?>("Pivot:DeadbandBps") ?? 1);
        _volatility = configuration.GetValue<Volatility?>("Pivot:Volatility") ?? Volatility.Realistic;
        _heartbeat = configuration.GetValue<TimeSpan?>("Pivot:Heartbeat") is { } heartbeat && heartbeat > TimeSpan.Zero
            ? heartbeat
            : TimeSpan.FromSeconds(60);
        _planSwap = configuration.GetValue<ViewPlanSwap?>("Pivot:PlanSwap") ??
                    ViewPlanSwap.LowJitter; // (low jitter, as the page's switch is by default)
        _trackPlans = configuration.GetValue<bool>("Pivot:TrackPlans");
        _planMargin = configuration.GetValue<double?>("Pivot:PlanMargin") is { } margin
            ? Math.Clamp(margin, 0, 0.99)
            : null; // (ChalkQL takes a fraction from 0 up to, not including, 1)
        _chalkLog = configuration.GetValue<bool>("Pivot:ChalkLog") ? loggers : null;
        _planning = new PlanningLog(log);
        if (configuration.GetValue<double?>("Pivot:TradesPerDay") is { } perDay)
            _tradesPerSecond =
                Math.Clamp(perDay, 0, MaxTradesPerDay) /
                86_400; // (the pace it starts at, and primes its history at)   // (a pivot over several days, its totals in nine GROUP BYs, may take a 32 MB arena: room for two, while one swaps in)
        Endpoint = ProviderEndpoint.InProcess("pivot");
        Instance = ProviderInstance.Start("pivot", Endpoint);
        Stats = new LastValueSource
            { Label = "pivot-stats", Format = new PayloadFormat { ContentType = "application/json" } };
        HouseDrawdown = new LastValueSource
            { Label = "house-drawdown", Format = new PayloadFormat { ContentType = "application/json" } };
        _drawdown = new Drawdown(log, HouseDrawdown,
            Math.Max(0, configuration.GetValue<double?>("Pivot:DrawdownHaircutUsd") ?? 0), // (below the 5th percentile, by this much)
            configuration.GetValue<TimeSpan?>("Pivot:DrawdownThresholdEvery") is { } thresholdEvery && thresholdEvery >= TimeSpan.FromSeconds(30)
                ? thresholdEvery
                : TimeSpan.FromMinutes(1), // (how often the seven days' threshold is computed again)
            configuration.GetValue<bool?>("Pivot:DrawdownIncremental") ?? true, // (false: its live view recomputed)
            _validate); // (its live view against a recomputed twin)
        _routers = new RouterSessions(() => Instance)
        {
            Lost = (router, routes) =>
                log.LogInformation("Pivot: router {Router} presumed dead, {Routes} routes forgotten", router,
                    routes.Length)
        };
    }

    public ProviderEndpoint Endpoint { get; }
    public ProviderInstance Instance { get; }

    /// <summary>
    ///     How the provider is doing, as JSON (published directly, not through the service): the tape, and each pivot it
    ///     runs.
    /// </summary>
    public LastValueSource Stats { get; }

    /// <summary>The house's drawdown alert, as JSON (published directly, while someone taps it): see <see cref="Drawdown" />.</summary>
    public LastValueSource HouseDrawdown { get; }

    /// <summary>The brokerage's live pace, in client trades a day — every page's (a page's ?trades sets it).</summary>
    public double TradesPerDay => Volatile.Read(ref _tradesPerSecond) * 86_400;

    ValueTask IDataProvider.ConnectAsync(IRouterChannel router, CancellationToken cancellationToken)
    {
        _routers.Connected(router);
        return default;
    }

    /// <summary>
    ///     Step 1: the page's statement — onto the pivot's topic if one is running (the same statement: shared), or
    ///     a new one, prepared and checked; refused, saying why, if it can't be served.
    /// </summary>
    async ValueTask IDataProvider.SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
    {
        if (!_routers.Admits(request.Router))
        {
            request.Nack();
            return;
        }

        var (sql, bindings, rendezvous, wrong) = Parse(request.Parameters);
        if (ShardOf(request.Parameters) is { } shard && shard != Shard)
        {
            Refuse(request, $"Shard {shard} isn't this provider's ({Shard}): its tokens mean nothing here — subscribe without one, or there.");
            return;
        }

        // How its view is maintained, as the page asks: Recompute — its statement run in full each update — or, asked for,
        // Incremental (experimental): what changed since its last update computed, its joins, as-of joins and aggregates
        // keeping state between them. Part of a pivot's identity: pages asking for each share a view of their own.
        var maintenance = Incremental(request.Parameters) ? ViewMaintenance.Incremental : ViewMaintenance.Recompute;
        // And, maintained incrementally, how its view takes a plan ChalkQL makes anew: with low jitter (the page's switch, on by
        // default) built beside the plan it runs and swapped in, its state kept (blue/green); without, computed from nothing
        // at the next update. Part of its identity too.
        var swap = maintenance != ViewMaintenance.Incremental ? ViewPlanSwap.AtNextUpdate
            : LowJitter(request.Parameters) is { } low ? low ? ViewPlanSwap.LowJitter : ViewPlanSwap.AtNextUpdate
            : _planSwap;
        var mode = maintenance != ViewMaintenance.Incremental ? "" :
            swap == ViewPlanSwap.LowJitter ? "\u0001incremental" : "\u0001incremental\u0001atnext";
        // And what its cells carry besides its values (Show as), in their order: computed here — part of its identity too,
        // as one statement's percentages may be of what's shown or of the market. None: the plain cell, and its path.
        var extras = Extras(request.Parameters);
        if (extras.Length > 0) mode += $"\u0001show:{string.Join(",", extras)}";
        if (wrong is not null || sql is null)
        {
            Refuse(request, wrong ?? "No statement.");
            return;
        }

        if (await _ready.Task.WaitAsync(cancellationToken) is not { } engine)
        {
            Refuse(request, "ChalkQL's planner isn't running (it needs a Java 21 runtime).");
            return;
        }

        // A pivot is found by either of two keys; each only an optimisation (a miss is another pivot, never a wrong one):
        // - Its statement's text, exactly: looked up first, so a page sending a statement already running joins
        //   it without the planner.
        // - ChalkQL's logical digest (its plan's, before anything is chosen by cost), its topic's name: the same for the
        //   statement however it's spaced or cased, its IN lists ordered, or its window written (INTERVAL '1' MINUTE, or
        //   '60' SECOND); a GROUP BY reordered too, unless its measures are computed (the plan's columns move). Different
        //   for other values, windows, aliases or tables; stable as the data changes — planned again as its tables grow,
        //   the statement keeps it (the plan digest doesn't) — and from one process to the next.
        //   The texts that found it this way are kept too (a few), as keys of their own.
        // Text, not ChalkQL's redacted SQL: that needs the planner, and its literals' 32-bit fingerprints could collide,
        // sharing statements that differ. The same text means the same pivot while its catalog holds: ChalkQL plans a
        // statement again in place as its tables drift (stale meanwhile, past tenfold, and still correct), and a change of
        // shape is prepared anew — by a view itself (followed), or by the provider for a snapshot (see Unfit).
        // The values a statement binds are part of both keys, exactly (its text, its digest: the same for any values):
        // they're what tells two pivots of one statement apart. Its prepared query is kept by its text alone — retrieved,
        // not planned again, whatever's bound. (One run in a caller's context — entitlements folded into its plan — would
        // add that to the keys too.)
        var canonical = JsonSerializer.Serialize(bindings);
        var text = (bindings!.Count == 0 ? sql : $"{sql}\u0001{canonical}") + mode;
        Topic? topic = null;
        var answered = false;
        var resolving = Stopwatch.GetTimestamp(); // (its prepared query: built, or retrieved — for the page)
        lock (_gate)
        {
            if (_byText.TryGetValue(text, out var known))
                if (Unfit(known.Query))
                {
                    Forget(known,
                        text); // (a table it reads changed shape: prepared again — a drift's planned again in place)
                }
                else
                {
                    Resolved(rendezvous, resolving, "retrieved");
                    (topic, answered) = (known, Join(known, request, rendezvous));
                    _sharedByText++;
                }
        }

        if (topic is null)
        {
            PreparedQuery? query;
            Task<PreparedQuery>? preparing = null;
            var planned = false; // (this page's prepare — or another's, in flight, awaited)
            lock (_gate)
            {
                if (_plans.TryGetValue(sql, out query) && Unfit(query)) (query, _) = (null, _plans.Remove(sql));
                if (query is null &&
                    !_preparing.TryGetValue(sql, out preparing)) // (none in flight: this page's — on the pool, so it's
                    (_preparing[sql], planned) =
                        (preparing = Task.Run(() => Prepare(engine, sql)), true); // registered before it can finish)
            }

            if (query is null)
                try
                {
                    query = await preparing!.WaitAsync(cancellationToken);
                }
                catch (ChalkException e)
                {
                    Refuse(request, $"ChalkQL: {e.Message}");
                    return;
                }

            if (Contract(query, bindings) is { Problem: { } problem })
            {
                Refuse(request, problem);
                return;
            }

            if (extras.Length > 0 && query.Parameters.Any(p => IsNow(p.Name)))
            {
                Refuse(request,
                    "Show as needs a pivot maintained as a view: this statement binds @now, so it runs as a snapshot each time.");
                return;
            }

            var key = (bindings.Count == 0
                ? $"{query.LogicalDigest:x16}"
                : $"{query.LogicalDigest:x16}\u0001{canonical}") + mode;
            lock (_gate)
            {
                Resolved(rendezvous, resolving, preparing is null ? "retrieved"
                    : !planned ? "planned for another page meanwhile"
                    : _topics.ContainsKey(key) ? "planned, then found running" : "planned");
                if (_topics.TryGetValue(key, out topic))
                {
                    _sharedByDigest++;
                }
                else // (the first to ask: a new pivot)
                {
                    // Admitted if what it needs to start fits the memory budget — idle pivots let go first, the longest idle
                    // first — and running the pivots leaves the time for another.
                    var need = ArenaStart + 2L * Staging;
                    while (Reserved() + need > _memoryBudget &&
                           _topics.Values.Where(t => !t.Live).MinBy(t => t.IdleSince) is { } idle) LetGo(idle);
                    if (Reserved() + need > _memoryBudget)
                    {
                        Refuse(request,
                            $"The pivot provider's memory is spoken for: {_topics.Count} pivots reserve {MB(Reserved())} of its {MB(_memoryBudget)}, and a new one needs {MB(need)}. " +
                            "Ask for one of those (the same criteria share it), or try again when one is let go.");
                        return;
                    }

                    if (_pivotMs > 1000 * TimeShare)
                    {
                        Refuse(request,
                            $"The pivot provider spends {_pivotMs:F0} ms of each second on {_topics.Count(t => t.Value.Live)} pivots already. " +
                            "Ask for one of those (the same criteria share it), or try again when one is let go.");
                        return;
                    }

                    // Its name: the logical digest — and, binding values, a hash of them; maintained incrementally, "-incremental"
                    // (the key, not the name, tells pivots apart).
                    var name = (bindings.Count == 0
                                   ? $"pivot:{query.LogicalDigest:x16}"
                                   : $"pivot:{query.LogicalDigest:x16}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..8]}")
                               + (maintenance != ViewMaintenance.Incremental ? "" :
                                   swap == ViewPlanSwap.LowJitter ? "-incremental" : "-incremental-atnext")
                               + (extras.Length == 0 ? "" : $"-x{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", extras))))[..6]}");
                    for (var n = 2; _topics.Values.Any(t => t.Name == name); n++)
                        name = $"{name.Split('+')[0]}+{n}"; // (a name taken: another's)
                    var columns = Contract(query, bindings).Columns!;
                    var shape = Shape(sql, query, bindings, extras); // (the viewer's format: made of it)
                    var values = query.Parameters.Select(p => (p.Name ?? "").TrimStart('@', ':')).Where(p => !IsNow(p))
                        .ToDictionary(p => p, p => (object?)bindings[p]);
                    Table<PivotKey, PivotCell>? cells = null;
                    IShell? shell = null;
                    if (extras.Length == 0) cells = new Table<PivotKey, PivotCell>(name) { Format = Pivot.FormatOf(shape) };
                    else shell = NewShell(name, extras, [.. columns.Wide.Select(at => at >= 0)], shape);
                    _topics[key] = topic = new Topic
                    {
                        Key = key, Name = (cells as Source ?? shell!.Source).Label!, Sql = sql, Query = query,
                        BindsNow = query.Parameters.Any(p => IsNow(p.Name)), Values = values,
                        Columns = columns, Cells = cells, Shell = shell, Extras = extras,
                        Ingest = cells?.Ingest<CellChange>(BatchFold.Of<PivotCell, CellChange, ReplaceOrDelete>(), Staging),
                        Arena = NewArena(ArenaStart), ArenaBudget = ArenaStart, Maintenance = maintenance,
                        PlanSwap = swap
                    };
                    _log.LogInformation("Pivot: {Name} {How} (plan {Plan:x16}), its view {Maintenance}:\n{Sql}{Bound}",
                        topic.Name, planned ? "prepared" : "from its prepared query", query.PlanDigest,
                        topic.BindsNow
                            ? "none (it binds @now: a snapshot each run)"
                            : $"maintained by {maintenance}" + (maintenance != ViewMaintenance.Incremental ? ""
                                : swap == ViewPlanSwap.LowJitter ? ", new plans built beside (low jitter)"
                                : ", new plans at the next update"), sql,
                        bindings.Count == 0 ? "" : $"\n  bound: {canonical}");
                }

                if (topic.Texts.Count < MaxTexts && _byText.TryAdd(text, topic)) topic.Texts.Add(text);
                answered = Join(topic, request, rendezvous);
            }
        }

        // The page awaits its pivot's first answer: running already, after what's been posted (applied, perhaps already).
        if (rendezvous is not null && answered) topic.TryRendezvous(request.Mailbox, rendezvous);
        _routers.Accepted(request.Router, request.Route);
    }

    ValueTask IDataProvider.UnsubscribeAsync(Unsubscription notice, CancellationToken cancellationToken)
    {
        _routers.Ended(notice.Router, notice.Route);
        return default;
    }

    ValueTask<HealthReport> IProviderHealth.CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
    {
        return new ValueTask<HealthReport>(new HealthReport(Instance, ProviderStatus.Serving,
            _routers.Refuses(probe.Router), Load: _load.Load)); // (a struct, from memory: nothing allocated)
    }

    /// <summary>
    ///     The plan ChalkQL prepared for a pivot (by its topic's name), if it's held: physical (what runs: its operators, and
    ///     what the planner estimated of each) and logical.
    /// </summary>
    public PreparedPlan? PlanOf(string topic)
    {
        PreparedQuery query;
        lock (_gate)
        {
            if (_topics.Values.FirstOrDefault(t => t.Name == topic) is { } held) query = held.Query;
            else return null;
        }

        var text = query.PlanText ?? "";
        int logical = text.IndexOf("-- logical\n", StringComparison.Ordinal),
            physical = text.IndexOf("-- physical\n", StringComparison.Ordinal);
        if (logical < 0 || physical < logical) return new PreparedPlan(text, "");
        return new PreparedPlan(text[(physical + "-- physical\n".Length)..].TrimEnd(),
            text[(logical + "-- logical\n".Length)..physical].TrimEnd());
    }

    /// <summary>
    ///     This provider's engine, as a token names it: ChalkQL numbers its versions per engine, so a version means
    ///     something only on the engine that made it. Drawn as it starts (a UUIDv7): another instance, another shard.
    /// </summary>
    public Guid Shard { get; } = Guid.CreateVersion7();

    /// <summary>The version its engine stands at: its last refresh's (0 before the first).</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>
    ///     Sets the pace (<see cref="SetTradesPerDay" />), and completes with the version of the refresh that applies it —
    ///     the history primed again at it, ChalkQL's tables replaced — or at once with the current one if it's the pace
    ///     already: a token to wait on, as a client waits for its change.
    /// </summary>
    public Task<long> SetTradesPerDayAsync(double perDay)
    {
        var perSecond = Math.Clamp(perDay, 0, MaxTradesPerDay) / 86_400;
        if (Volatile.Read(ref _pace) == perSecond) return Task.FromResult(Version); // (applied already)
        var applied = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _paces.Add((perSecond, applied));
        }

        SetTradesPerDay(perDay);
        _wake.Signal();
        return applied.Task;
    }

    /// <summary>Its engine refreshed: where it stands now; a pace asked for, applied by it, told.</summary>
    private void Refreshed(long version, double pace)
    {
        Volatile.Write(ref _version, version);
        Volatile.Write(ref _pace, pace);
        List<TaskCompletionSource<long>>? done = null;
        lock (_gate)
        {
            for (var i = _paces.Count - 1; i >= 0; i--)
                if (_paces[i].PerSecond == pace)
                {
                    (done ??= []).Add(_paces[i].Applied);
                    _paces.RemoveAt(i);
                }
        }

        if (done is not null)
            foreach (var applied in done) applied.TrySetResult(version);
    }

    /// <summary>
    ///     Completes once the pivot whose cells <paramref name="source" /> holds has posted an answer at or after
    ///     <paramref name="version" /> — run at once, not after its backoff — and the router has applied it, with the version
    ///     it posted at: what a client
    ///     waits for to have its change in front of it. Null: no pivot of this provider's holds that source.
    /// </summary>
    public Task<long>? WaitAsync(Source source, long version, CancellationToken ct)
    {
        Topic? topic;
        lock (_gate)
        {
            topic = _topics.Values.FirstOrDefault(t => ReferenceEquals(t.Source, source));
        }

        if (topic is null) return null;
        var reached = topic.Reached(version);
        if (!reached.IsCompleted && _admitted.Writer.TryWrite(topic)) _wake.Signal(); // (run at once)
        return Applied(topic, reached).WaitAsync(ct);

        // (posted, then applied by the router: the subscription has it to send — its MinInterval aside, its caller's)
        static async Task<long> Applied(Topic topic, Task<long> reached)
        {
            var version = await reached.ConfigureAwait(false);
            var applied = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            topic.WhenApplied(_ => applied.TrySetResult(version));
            return await applied.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Sets the pace, from the next burst (0 to <see cref="MaxTradesPerDay" />): the history primed again at it.</summary>
    public void SetTradesPerDay(double perDay)
    {
        var perSecond = Math.Clamp(perDay, 0, MaxTradesPerDay) / 86_400;
        if (Interlocked.Exchange(ref _tradesPerSecond, perSecond) != perSecond)
            _log.LogInformation("Pivot: the brokerage's pace set to {PerDay:N0} client trades a day",
                perSecond * 86_400);
    }

    /// <summary>
    ///     A statement's text prepared for the pages asking for it — once, however many ask while it's prepared (each awaits
    ///     this), under the provider's token, not a page's: one going away doesn't cancel it for the others. Kept by its text
    ///     (the next page retrieves it), then let go from the prepares in flight — a page asking in between finds one or the
    ///     other.
    /// </summary>
    private async Task<PreparedQuery> Prepare(ChalkEngine engine, string sql)
    {
        try
        {
            var query = await engine.PrepareAsync(sql, new PrepareOptions { IncludePlanText = true },
                _stopping); // (its plan's text: the page shows it)
            lock (_gate)
            {
                if (_plans.TryAdd(sql, query))
                {
                    _planned.Enqueue(sql);
                    while (_planned.Count > MaxPlans) _plans.Remove(_planned.Dequeue());
                }
            }

            return query;
        }
        finally
        {
            Interlocked.Increment(ref _prepared);
            lock (_gate)
            {
                _preparing.Remove(sql);
            }
        }
    }

    private void Resolved(byte[]? rendezvous, long since, string how)
    {
        if (rendezvous is null) return;
        if (_resolutions.Count > 256) _resolutions.Clear(); // (unclaimed — their pages gone: bounded)
        _resolutions[Convert.ToHexStringLower(rendezvous)] = new Resolution(Stopwatch.GetElapsedTime(since), how);
    }

    /// <summary>How long resolving the statement a page asked for with this rendezvous id took — once, then forgotten.</summary>
    public Resolution? TakeResolution(string rendezvous)
    {
        return _resolutions.TryRemove(rendezvous.ToLowerInvariant(), out var resolution) ? resolution : null;
    }

    /// <summary>
    ///     A subscription onto a pivot (under _gate, with finding it: never one let go meanwhile): it runs; a rendezvous
    ///     waits for its first answer — or, answered already, the caller hands it back.
    /// </summary>
    private bool Join(Topic topic, SubscribeRequest request, byte[]? rendezvous)
    {
        var idle = !topic.Live;
        var epoch = request.Accept(topic.Source, Instance, topic.Name, InitialState.Snapshot, Pivot.Policy,
            topic.Source.Format);
        // A new epoch: every route gone since it last posted (a 202 found it so), then one bound — its answer, next, the
        // whole of it (a snapshot), so its table starts the epoch resynchronised with its view, whatever came before.
        // Another page joining the same epoch: none.
        if (epoch != topic.Epoch && topic.Posted > 0) topic.Resync = true;
        topic.Epoch = epoch;
        topic.Live = true;
        // Answered, and running: its answer's fresh. New, or come back to — nobody watched it, so it stopped, its last answer
        // as old as that — it runs at once, and a rendezvous waits for that answer.
        var answered = topic.Posted > 0 && !idle;
        if (rendezvous is not null && !answered) topic.Waiting.Add((request.Mailbox, rendezvous));
        if (!answered && _admitted.Writer.TryWrite(topic)) _wake.Signal();
        return answered;
    }

    /// <summary>A text no longer a key of its pivot's. Under _gate.</summary>
    private void Forget(Topic topic, string text)
    {
        _byText.Remove(text);
        topic.Texts.Remove(text);
    }

    /// <summary>
    ///     A pivot's arena: at most its budget, and keeping it between runs (it's reserved anyway: no allocating each
    ///     run).
    /// </summary>
    private static ExecutionArena NewArena(long budget)
    {
        return new ExecutionArena(new ArenaOptions { MaxBytes = budget, RetainBytes = budget });
    }

    private static string MB(long bytes)
    {
        return $"{bytes / 1048576.0:0.#} MB";
    }

    /// <summary>What the pivots hold against the memory budget. Under _gate.</summary>
    private long Reserved()
    {
        return _topics.Values.Sum(t => t.Reserved);
    }

    /// <summary>A pivot let go: its topic, its arena. Under _gate; never one running (only idle ones are).</summary>
    private void LetGo(Topic topic)
    {
        _log.LogInformation("Pivot: {Name} let go, idle {Idle:F0} s ({Reserved} freed)", topic.Name,
            (DateTime.UtcNow - topic.IdleSince).TotalSeconds, MB(topic.Reserved));
        _topics.Remove(topic.Key);
        foreach (var text in topic.Texts) _byText.Remove(text);
        topic.Arena.Dispose();
        if (topic.View is { } view) _ = view.DisposeAsync().AsTask();
        if (topic.WideView is { } wide) _ = wide.DisposeAsync().AsTask();
    }

    /// <summary>
    ///     Whether a run failed for want of arena — ChalkQL reports it as the execution's failure, the arena's own
    ///     exception within — and what it needed in all, if it says (else 0).
    /// </summary>
    private static long? Exceeded(ChalkException e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            if (x is ArenaBudgetExceededException arena)
                return arena.OutstandingBytes + arena.RequestedBytes;
        return e.Code == "MemoryBudgetExceeded" ? 0 : null;
    }

    /// <summary>
    ///     A bigger arena for a pivot whose run needed more (double, or what it needed, at most 32 MB) — if the memory
    ///     budget has room, idle pivots let go for it.
    /// </summary>
    private bool Grow(Topic topic, long needed)
    {
        lock (_gate)
        {
            var want = Math.Min(ArenaMax,
                Math.Max(topic.ArenaBudget * 2, (long)BitOperations.RoundUpToPowerOf2((ulong)needed)));
            if (want <= topic.ArenaBudget) return false;
            // Room made as admission makes it: idle pivots let go, the longest idle first (never one running).
            while (Reserved() - topic.ArenaBudget + want > _memoryBudget &&
                   _topics.Values.Where(t => !t.Live && t != topic).MinBy(t => t.IdleSince) is { } idle) LetGo(idle);
            if (Reserved() - topic.ArenaBudget + want > _memoryBudget) return false;
            topic.Arena.Dispose();
            (topic.Arena, topic.ArenaBudget) = (NewArena(want), want);
        }

        _log.LogInformation("Pivot: {Name}'s arena grown to {Budget}", topic.Name, MB(topic.ArenaBudget));
        return true;
    }

    /// <summary>
    ///     A refusal, saying why: the provider's log line, with the correlation id the router and the page log it with
    ///     too.
    /// </summary>
    private void Refuse(SubscribeRequest request, string reason)
    {
        var id = request.Refuse(reason);
        _log.LogInformation("Pivot: refused a subscription: {Reason} (correlation {CorrelationId})", reason, id);
    }

    /// <summary>The parameters: <c>{"sql": "…", "rendezvous": "hex"}</c> (the last optional) — or what's wrong with them.</summary>
    /// <summary>
    ///     Step 1's parameters: the statement, the values it binds — each a list of text, by name; sorted, without repeats,
    ///     so the same values are the same key — and a rendezvous id, if the page awaits one.
    /// </summary>
    private static (string? Sql, SortedDictionary<string, string[]>? Bindings, byte[]? Rendezvous, string? Problem)
        Parse(string? parameters)
    {
        const int MaxBindings = 16, MaxValues = 256, MaxValue = 128;
        try
        {
            using var json = JsonDocument.Parse(parameters ?? "");
            var root = json.RootElement;
            var sql = root.GetProperty("sql").GetString() ?? "";
            var rendezvous = root.TryGetProperty("rendezvous", out var r) && r.GetString() is { Length: > 0 } hex
                ? Convert.FromHexString(hex)
                : null;
            if (sql.Length is 0 or > MaxSql)
                return (null, null, null, $"A statement is 1 to {MaxSql} characters (this is {sql.Length}).");
            if (rendezvous is { Length: > Wire.MaxCorrelation })
                return (null, null, null, $"A rendezvous id is at most {Wire.MaxCorrelation} bytes.");
            var bindings = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
            if (root.TryGetProperty("bindings", out var bound) && bound.ValueKind != JsonValueKind.Null)
                foreach (var binding in bound.EnumerateObject())
                {
                    if (!Regex.IsMatch(binding.Name, "^[a-z][a-z0-9_]{0,31}$") || IsNow(binding.Name))
                        return (null, null, null,
                            $"A bound value's name is a word, lower case, at most 32 characters, and not now ({binding.Name} isn't).");
                    string[] values =
                    [
                        .. binding.Value.EnumerateArray()
                            .Select(v =>
                                v.GetString() ?? throw new FormatException($"@{binding.Name}'s values are text."))
                            .Distinct().Order(StringComparer.Ordinal)
                    ];
                    if (values.Length > MaxValues || values.Any(v => v.Length > MaxValue))
                        return (null, null, null,
                            $"A bound list is at most {MaxValues} values, each at most {MaxValue} characters (@{binding.Name} isn't).");
                    bindings[binding.Name] = values;
                }

            if (bindings.Count > MaxBindings)
                return (null, null, null,
                    $"A statement binds at most {MaxBindings} lists (this binds {bindings.Count}).");
            return (sql, bindings, rendezvous, null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException
                                      or FormatException)
        {
            return (null, null, null,
                $"The parameters are JSON — {{\"sql\": \"…\", \"bindings\": {{\"name\": [\"…\"]}}, \"rendezvous\": \"hex\"}}: {e.Message}");
        }
    }

    /// <summary>Whether a page asks for its pivot's view to be maintained incrementally (its "incremental": true).</summary>
    /// <summary>What a subscription asks its cells to carry besides its values (Show as), in their order: none, none.</summary>
    private static ShownValue[] Extras(string? parameters)
    {
        try
        {
            using var json = JsonDocument.Parse(parameters ?? "");
            return json.RootElement.TryGetProperty("extras", out var extras) && extras.ValueKind == JsonValueKind.Array
                ? ShownValue.Extras(extras.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!))
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The shard a client asks for (its "shard": a token's), or none.</summary>
    private static Guid? ShardOf(string? parameters)
    {
        try
        {
            using var json = JsonDocument.Parse(parameters ?? "");
            return json.RootElement.TryGetProperty("shard", out var shard) && shard.ValueKind == JsonValueKind.String &&
                   Guid.TryParse(shard.GetString(), out var id)
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Incremental(string? parameters)
    {
        try
        {
            using var json = JsonDocument.Parse(parameters ?? "");
            return json.RootElement.TryGetProperty("incremental", out var incremental) &&
                   incremental.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Whether a page asks for its incremental view's low jitter (its "lowJitter": true or false) — or none, saying
    ///     nothing.
    /// </summary>
    private static bool? LowJitter(string? parameters)
    {
        try
        {
            using var json = JsonDocument.Parse(parameters ?? "");
            return json.RootElement.TryGetProperty("lowJitter", out var low) &&
                   low.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? low.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsNow(string? name)
    {
        return string.Equals((name ?? "").TrimStart('@', ':'), "now", StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>
    ///     Whether a statement answers what a pivot is (<see cref="Pivot.Contract" />) and binds nothing but <c>@now</c> and
    ///     the
    ///     lists the page bound: where its columns are, or what's wrong.
    /// </summary>
    private static (Columns? Columns, string? Problem) Contract(PreparedQuery query,
        IReadOnlyDictionary<string, string[]> bindings)
    {
        var fields = query.OutputSchema.FieldsList;

        int Index(string name)
        {
            return fields.Select((f, i) => (f.Name, i))
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase), (Name: "", i: -1))
                .i;
        }

        var at = Pivot.Contract.Select(Index).ToArray();
        var answers = string.Join(", ", fields.Select(f => f.Name));
        if (at.Any(i => i < 0))
            return (default,
                $"A pivot's statement answers {string.Join(", ", Pivot.Contract)} — one row per cell, totals included (this one answers {answers}).");

        bool Text(int i)
        {
            return fields[i].DataType.TypeId is ArrowTypeId.String or ArrowTypeId.StringView or ArrowTypeId.LargeString;
        }

        bool Number(int i)
        {
            return fields[i].DataType.TypeId is ArrowTypeId.Int64 or ArrowTypeId.Int32 or ArrowTypeId.Int16
                or ArrowTypeId.Decimal128 or ArrowTypeId.Double or ArrowTypeId.Float;
        }

        if (!Text(at[0]) || !Text(at[1]))
            return (default,
                $"row_key and column_key are text (this statement's are {fields[at[0]].DataType.Name} and {fields[at[1]].DataType.Name}).");
        if (!at[2..].All(Number))
            return (default,
                "quantity (units of the asset), notional, realized_pnl, unrealized_pnl, balance and volume (the reporting currency) and trades (a count) are numbers, or null.");
        var maxGross = Index(Pivot.MaxGrossColumn); // (may be answered: each client's larger side, summed)
        if (maxGross >= 0 && !Number(maxGross))
            return (default, "max_gross (the reporting currency) is a number, or null.");
        var mark = Index(Pivot.MarkColumn); // (may be answered: a rate, the instrument's quote per unit of its asset)
        if (mark >= 0 && !Number(mark))
            return (default, "mark_price (a rate: the instrument's quote per unit of its asset) is a number, or null.");
        var unbound = query.Parameters
            .Where(p => !IsNow(p.Name) && !bindings.ContainsKey((p.Name ?? "").TrimStart('@', ':'))).ToList();
        if (unbound.Count > 0)
            return (default,
                $"A pivot's statement may bind @now — the time it runs, each run — and the lists the page binds ({(bindings.Count == 0 ? "none" : string.Join(", ", bindings.Keys.Select(k => "@" + k)))}); " +
                $"this one binds {string.Join(", ", unbound.Select(p => string.IsNullOrEmpty(p.Name) ? "?" : "@" + p.Name!.TrimStart('@', ':')))} too.");
        var wide = WideColumns.Select(Index).ToArray(); // (an extended pivot's: what its statement answers besides)
        if (wide.FirstOrDefault(i => i >= 0 && !Number(i), -1) is var notNumber and >= 0)
            return (default, $"{fields[notNumber].Name} is a number, or null.");
        FigureKind[] kinds =
        [
            .. fields.Select(f => f.DataType.TypeId switch
            {
                ArrowTypeId.Float => FigureKind.Real, ArrowTypeId.Double => FigureKind.Double,
                ArrowTypeId.Decimal128 => FigureKind.Decimal, _ => FigureKind.Integer
            })
        ];
        return (new Columns(at[0], at[1], at[2], at[3], at[4], at[5], at[6], at[7], at[8], maxGross, mark, kinds, wide),
            null);
    }

    /// <summary>
    ///     What a statement's pivot would stream, without subscribing: its shape (<see cref="Shape" />) — or why it can't be a
    ///     pivot. Checked as step 1 checks it, and prepared as step 1 prepares it (its prepared query kept, or one in flight
    ///     awaited): the page subscribing next isn't planned again.
    /// </summary>
    public async Task<(PivotShape? Shape, string? Problem)> DescribeAsync(string sql,
        IReadOnlyDictionary<string, string[]> bindings, IReadOnlyList<string> extras, CancellationToken cancellationToken)
    {
        var (text, bound, _, wrong) = Parse(PivotCriteria.Parameters(sql, bindings, extras: extras));
        if (wrong is not null || text is null) return (null, wrong ?? "No statement.");
        if (await _ready.Task.WaitAsync(cancellationToken) is not { } engine)
            return (null, "ChalkQL's planner isn't running (it needs a Java 21 runtime).");
        PreparedQuery? query;
        Task<PreparedQuery>? preparing = null;
        lock (_gate)
        {
            if (_plans.TryGetValue(text, out query) && Unfit(query)) (query, _) = (null, _plans.Remove(text));
            if (query is null && !_preparing.TryGetValue(text, out preparing))
                _preparing[text] = preparing = Task.Run(() => Prepare(engine, text));
        }

        if (query is null)
            try
            {
                query = await preparing!.WaitAsync(cancellationToken);
            }
            catch (ChalkException e)
            {
                return (null, $"ChalkQL: {e.Message}");
            }

        return Contract(query, bound!) is { Problem: { } problem }
            ? (null, problem)
            : (Shape(text, query, bound!, ShownValue.Extras(extras)), null);
    }

    /// <summary>
    ///     A pivot's shape, for a client to read its cells with nothing compiled in (<see cref="PivotShape" />): what its
    ///     statement binds and answers, as ChalkQL prepared it; the cells made of it — their labels and figures, field by
    ///     field
    ///     (<see cref="Pivot.Labels" />, <see cref="Pivot.Figures" />), each from the column it's made of; and the clock cell
    ///     among
    ///     them. The statement answers what a pivot is (<see cref="Contract" />).
    /// </summary>
    private static PivotShape Shape(string sql, PreparedQuery query, IReadOnlyDictionary<string, string[]> bindings,
        ShownValue[] extras)
    {
        var fields = query.OutputSchema.FieldsList;

        string? Answered(string column)
        {
            return fields.FirstOrDefault(f => string.Equals(f.Name, column, StringComparison.OrdinalIgnoreCase))?.Name;
        }

        ShapeParameter Parameter(string name, string type, bool list)
        {
            return new ShapeParameter(name, type, list, IsNow(name)
                ? "the time it runs, each run: the provider binds it"
                : $"bound: {(bindings.TryGetValue(name, out var values) ? values.Length : 0)} values");
        }

        var carried = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var key = new List<ShapeField>();
        foreach (var label in Pivot.Labels)
        {
            var from = Answered(label.Column)!;
            carried[from] = $"key.{label.Name}";
            key.Add(new ShapeField
            {
                Name = label.Name, Offset = key.Sum(f => f.Bytes), Type = "utf8", Bytes = label.Bytes, From = from,
                About = $"the cell's {label.Name} label; \"(none)\" where a level's value is null",
                Levels = new ShapeLevels(" · ",
                    "a total's label has fewer levels than its cells' — the grand total's, none (empty)")
            });
        }

        var row = new List<ShapeField>();
        foreach (var figure in Pivot.Figures)
        {
            var from = Answered(figure.Column);
            if (from is not null) carried[from] = $"row.{figure.Name}";
            row.Add(new ShapeField
            {
                Name = figure.Name, Offset = row.Count * sizeof(long), Type = "int64", Bytes = sizeof(long),
                Scale = figure.Places,
                None = PivotCell.None.ToString(CultureInfo.InvariantCulture), From = from, Unit = figure.Unit,
                About = from is null
                    ? $"{figure.About} — the statement answers no {figure.Column}: always none"
                    : figure.About
            });
        }

        // Its extras, after its figures (a cell carrying any is as wide as they need: 2, 4, 8 or 16 of them, the rest none):
        // each the provider's — a running total as the statement answers it, a percentage as a fraction it divides.
        if (extras.Length > 0)
        {
            for (var i = 0; i < Capacity(extras.Length); i++)
            {
                var extra = i < extras.Length ? extras[i] : (ShownValue?)null;
                var running = extra is { Form: ShowAs.RunningTotalInDate } r ? Answered($"{r.Value}_running") : null;
                if (running is not null) carried[running] = $"row.{extra}";
                row.Add(new ShapeField
                {
                    Name = extra?.ToString() ?? $"extra{i}", Offset = row.Count * sizeof(long), Type = "int64",
                    Bytes = sizeof(long), Scale = extra is { } e ? ExtraPlaces(e) : Pivot.Places,
                    None = PivotCell.None.ToString(CultureInfo.InvariantCulture),
                    From = extra is not { } x ? null : x.Form == ShowAs.RunningTotalInDate ? running : "the provider",
                    Unit = extra is not { } u ? null : u.IsPercent ? "a fraction (1: the whole)" : Pivot.Figures.First(f => f.Name == u.Value).Unit,
                    About = extra is not { } a ? "no extra here: always none"
                        : a.Form == ShowAs.RunningTotalInDate ? $"{a.Value} since the first date's open: a running total in Date"
                        : $"{a.Value} as a part of {(a.Across switch { (true, false) => "its column's", (false, true) => "its row's", _ => "the grand" })} total — {(a.OfMarket ? "the market's: the filters on the fields it runs across ignored" : "what's shown: the pivot's own")}"
                });
            }
        }

        return new PivotShape
        {
            Sql = sql,
            Parameters =
            [
                .. query.Parameters.Select(p =>
                    Parameter((p.Name ?? $"${p.Ordinal}").TrimStart('@', ':'), p.Type.ToString(), p.AcceptsList))
            ],
            Columns =
            [
                .. fields.Select(f =>
                    new ShapeColumn(f.Name, TypeName(f.DataType), f.IsNullable, carried.GetValueOrDefault(f.Name)))
            ],
            Key = new ShapeLayout(key.Sum(f => f.Bytes), key),
            Row = new ShapeLayout(row.Count * sizeof(long), row),
            Reserved =
            [
                new ShapeReserved(PivotKey.ClockRow, "",
                    "the provider's clock cell, in each post with the cells it describes — none of the pivot's",
                    new Dictionary<string, ShapeReservedFigure>
                    {
                        ["quantity"] = new("watermark", "µs since 1970, UTC",
                            "the earliest change the answer answers, as the provider received it (0: nothing new)"),
                        ["notional"] = new("emitted", "µs since 1970, UTC", "when the provider posted it, by its clock"),
                        ["realized"] = new("generation", "", "its view's generation (its rebinds)"),
                        ["unrealized"] = new("version", "",
                            "the engine's version the answer is at (ChalkQL's, its shard's): with the generation, its stamp — what a token waits for")
                    })
            ]
        };
    }

    /// <summary>A column's type, as a shape names it: <c>utf8</c>, <c>decimal128(38, 8)</c>, <c>float64</c>, <c>int64</c>…</summary>
    private static string TypeName(IArrowType type)
    {
        return type switch
        {
            StringType or StringViewType or LargeStringType => "utf8",
            Decimal128Type d => $"decimal128({d.Precision}, {d.Scale})",
            DoubleType => "float64",
            FloatType => "float32",
            Int64Type => "int64",
            Int32Type => "int32",
            Int16Type => "int16",
            Int8Type => "int8",
            BooleanType => "bool",
            TimestampType t => t.Timezone is { Length: > 0 } zone
                ? $"timestamp({t.Unit}, {zone})"
                : $"timestamp({t.Unit})",
            Date32Type => "date32",
            var other => other.Name
        };
    }

    public override void Dispose()
    {
        _allocations?.Dispose();
        _wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _publisher.Provide("pivot", Endpoint, this); // serving at once: step 1 waits for ChalkQL, briefly
        PublishStats(new { starting = "ChalkQL's planner starting" });
        PlannerProcess planner;
        try
        {
            planner = await PlannerProcess.StartAsync(new PlannerProcessOptions(), stoppingToken);
        } // the Calcite sidecar (Java 21+), over a Unix socket
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e,
                "ChalkQL's planner didn't start (it needs a Java 21 runtime: JAVA_HOME, or Homebrew's openjdk@21)");
            PublishStats(new
            {
                error =
                    $"ChalkQL's planner didn't start: {e.Message} — it needs a Java 21 runtime (JAVA_HOME, or Homebrew's openjdk@21)."
            });
            _ready.TrySetResult(null);
            return;
        }

        await using var sidecar = planner; // (the planner's process: stopped when the loop ends)
        var pools = new Pools(); // (each table's row arrays: ChalkQL's for it, and its rewrites', handed over)
        var source = new PocoSourceBuilder("brokerage")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .DefaultDecimalScale(
                8) // quantities, balances, a hedge's PnL: DECIMAL(28,8); prices, rates, a client's PnL: DOUBLE
            .AddTable("time_zones", TimeZones.Offsets, out var zones,
                t => t.OrderedBy(x => x.Zone).ThenBy(x => x.Starts)) // the application's: fixed
            .AddTable("ccy", Desk.Ccys, out var ccys, t => t.OrderedBy(x => x.CcyId))
            .AddTable("instrument", Desk.Instruments, out var instruments, t => t.OrderedBy(x => x.InstrumentId))
            .AddTable("cpty", Array.Empty<Cpty>(), out var cptys, t => t.OrderedBy(x => x.CptyId))
            .AddTable("account", Array.Empty<Account>(), out var accounts, t => t.OrderedBy(x => x.AccountId))
            // The ledger and the hedges, append-only: each row as it happened.
            .AddTable("posting", Array.Empty<Posting>(), out var postings,
                t => t.OrderedBy(x => x.UpdateTime).ArrayPool(pools.Postings.Pool))
            // The journal, by account then time too: an account's balance as of a time is an index lookup.
            .AddTable("journal", Array.Empty<Journal>(), out var journal,
                t => t.OrderedBy(x => x.JournalId).UniqueKey(x => x.JournalId).Index(x => x.AccountId, x => x.UpdateTime)
                    .ArrayPool(pools.Journal.Pool))
            // A position's rows, by account, channel and time — unique: a position changes once at a time (a window over a
            // position's rows, LAG say, is maintained over a unique order).
            .AddTable("position_risk", Array.Empty<PositionRisk>(), out var positionRisk,
                t => t.OrderedBy(x => x.UpdateTime).UniqueIndex(x => x.AccountId, x => x.Channel, x => x.UpdateTime)
                    .ArrayPool(pools.Risk.Pool))
            .AddTable("fills", Array.Empty<HedgeFill>(), out var fills,
                t => t.OrderedBy(x => x.UpdateTime).ArrayPool(pools.Fills.Pool))
            // The marks, by time; and by instrument, then time — and indexed by time: the house's drawdown reads the last
            // hours' marks, a range, not every day kept.
            .AddTable("marks", Array.Empty<Mark>(), out var marks,
                t => t.OrderedBy(x => x.Ts).ThenBy(x => x.InstrumentId).Index(x => x.InstrumentId, x => x.Ts)
                    .Index(x => x.Ts).ArrayPool(pools.Marks.Pool))
            // The rates, by time; and by symbol, then time: a reporting currency's rates are an index lookup (and indexed by
            // time, as the marks are).
            .AddTable("conversion_rate", Array.Empty<ConversionRate>(), out var rates,
                t => t.OrderedBy(x => x.Ts).ThenBy(x => x.Symbol).Index(x => x.Symbol, x => x.Ts).Index(x => x.Ts)
                    .ArrayPool(pools.Rates.Pool))
            // A zone's trading days ending at a cutoff (on the hour), as of now — in SQL, from time_zones: a macro, inlined.
            .AddFunction("trading_days", f => f.TableFunction()
                .Parameter<string>("zone_name").Parameter<int>("cutoff_hour")
                .Column<string>("trading_day").Column<int>("days_ago").Column("opens", ChalkType.Timestamp())
                .Column("closes", ChalkType.Timestamp())
                .Sql(PivotCriteria.TradingDaysSql))
            .Build();
        var tables = new Tables(postings, journal, fills, positionRisk, marks, rates); // (the loop's refreshes')
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "pivot", Sources = [source], Planner = planner.CreatePlanner(), LoggerFactory = _chalkLog,
            PlanningObserver = _planning,
            Clock = "now" // (in a view, CURRENT_TIMESTAMP reads it as of its epoch: each refresh advances it)
        });
        _log.LogInformation("Pivot: ChalkQL ready — planner {Planner} on Calcite {Calcite}",
            engine.PlannerInfo.PlannerVersion, engine.PlannerInfo.CalciteVersion);

        // The brokerage, its history primed (Prime) — the marks and rates sampled each hour, each half minute over the last
        // seven days — so a day's positions, PnL, volume and trades, and the house's equity, have rows to be derived from:
        // client trades, each hedged a little after, most of the time.
        var started = DateTime.UtcNow;
        MarketTape market = null!;
        FxTape fx = null!;
        Brokerage brokerage = null!;
        Hedger hedger = null!;
        Random random = null!;
        Bursts bursts = null!;
        var keptPostings = new List<Posting>();
        var keptJournal = new List<Journal>();
        var keptRisk = new List<PositionRisk>();
        var keptFills = new List<HedgeFill>();
        var keptMarks = new List<Mark>();
        var keptRates = new List<ConversionRate>();

        // The as-of logs' thinning, its scratch kept: a rewrite prepared off the loop (one at a time) uses one set, a
        // compaction on the loop (nobody watching, or Pivot:PreparedCompaction=false) the other.
        Thinning offLoop = new(), onLoop = new();

        // Its history, until now, at a pace: eleven days back, on the hour (the oldest day kept opens up to a day before the
        // tenth day back, whatever its zone and cutoff) — the last seven at the pace, the four before them quiet (eight
        // trades an hour: a ten-day view isn't empty) — each hour, then each half minute over the last seven days and two
        // hours (Fine: the drawdown's points); the books opened with the house's working capital. Past 50,000 trades a day,
        // each trade simulated stands for several, its volume and count weighted: priming takes seconds at any pace, a
        // day's volume and trades the pace's, its positions and PnL the simulated trades'. Each hedge booked as it's due,
        // among the trades (every position's rows in time order). Primed again when a page sets another pace.
        void Prime(DateTime until, double perDay)
        {
            var first =
                new DateTime(until.Ticks - until.Ticks % Sample.Ticks, DateTimeKind.Utc).AddDays(-(Pivot.DaysKept + 1));
            var fine = until - FineKept;
            fine = new DateTime(fine.Ticks - fine.Ticks % Sample.Ticks, DateTimeKind.Utc); // (on the hour)
            (market, fx, brokerage, hedger, random, bursts) = (new MarketTape(7, _volatility),
                new FxTape(11, _volatility), new Brokerage(13, first), new Hedger(19), new Random(17), new Bursts(23));
            keptPostings.Clear();
            keptJournal.Clear();
            keptRisk.Clear();
            keptFills.Clear();
            keptMarks.Clear();
            keptRates.Clear();
            var books = (Cash: (IReadOnlySet<long>)brokerage.Accounts
                .Where(a => a.CptyId == Brokerage.HouseId && Desk.Ccys.First(c => c.CcyId == a.CcyId).IsDeliverable)
                .Select(a => a.AccountId).ToHashSet(), Capital: brokerage.Capital);
            (onLoop.Ledger.Books, offLoop.Ledger.Books) = (books, books);
            onLoop.From(until);
            brokerage.Fund(first, fx, keptPostings, keptJournal);
            var weight = Math.Max(1, (long)Math.Ceiling(perDay / MaxSimulatedPerDay));
            var hedges = new PriorityQueue<(int Asset, long Weight), DateTime>(); // (due, each after a burst)
            var booked = first; // (the last position row's time: every row later)

            void HedgeBefore(DateTime before)
            {
                while (hedges.TryPeek(out var due, out var at) && at < before)
                {
                    hedges.Dequeue();
                    booked = at > booked ? at : booked.AddTicks(10);
                    hedger.Hedge(booked, due.Asset, brokerage.HouseHolds(due.Asset), market, fx, brokerage, keptPostings,
                        keptJournal, keptFills, keptRisk, due.Weight);
                }
            }

            var last = first - Sample;
            for (var t = first - Sample; t < until;)
            {
                market.Step((t - last).TotalSeconds);
                fx.Next(t, keptRates, true);
                market.Marks(t, fx, keptMarks);
                var next = t + (t >= fine ? Fine : Sample);
                last = t;
                if (t < first) // (a sample before the first day: its rates and marks as it opens)
                {
                    t = next;
                    continue;
                }

                // The step's trading, in bursts (at its pace, on average), each asset traded hedged a little after, most of
                // the time.
                var paced = until - t <= TimeSpan.FromDays(PrimedDays);
                var w = paced ? weight : 1;
                var perSecond = paced ? perDay / weight / 86_400 : QuietTradesPerHour / 3600;
                for (var burst = bursts.Next(t, perSecond);
                     burst.Start + burst.Span < next && burst.Start + burst.Span < until;
                     burst = bursts.Next(burst.Start + burst.Span, perSecond))
                {
                    HedgeBefore(burst.Start);
                    var traded = new SortedSet<int>();
                    for (var i = 0; i < burst.Size; i++)
                    {
                        var at = burst.Start + burst.Span * ((i + 1.0) / (burst.Size + 1));
                        traded.Add(brokerage.Trade(at, market, keptPostings, keptJournal, keptRisk, burst.Assets, w));
                        booked = at;
                    }

                    var hedgeAt = burst.Start + burst.Span + TimeSpan.FromSeconds(20);
                    foreach (var asset in traded)
                        if (random.NextDouble() < 0.7 && hedgeAt < until)
                            hedges.Enqueue((asset, w), hedgeAt = hedgeAt.AddTicks(10));
                }

                HedgeBefore(next < until ? next : until);
                t = next;
                if (t.TimeOfDay == TimeSpan.Zero) Compact(until); // (kept small as it goes: each day)
            }

            Compact(until);
            Trim();
            _log.LogInformation(
                "Pivot: the brokerage primed — {Trades:N0} client trades over {Days} days at {PerDay:N0} a day ({Weight}), {Quiet} quiet days before them",
                brokerage.Trades, PrimedDays, perDay,
                weight == 1 ? "each simulated" : $"each simulated trade standing for {weight}",
                Pivot.DaysKept + 1 - PrimedDays);
        }

        var newPostings = new List<Posting>();
        var newJournal = new List<Journal>();
        var newRisk = new List<PositionRisk>();
        var newFills = new List<HedgeFill>();
        var ticked = new List<ConversionRate>();
        var marked = new List<Mark>();
        var stale = true; // ChalkQL's tables behind ours: replaced whole at the next work
        // ChalkQL's rows of each table we rewrite now and then: the first of ours (the rest: not loaded yet). A rewrite of
        // them prepared off the loop (Pivot:PreparedCompaction) — one at a time; committed when it's ready, unless ChalkQL's
        // tables have since been replaced whole, or ours rewritten on the loop (then let go).
        var held = new Dictionary<string, int>
        {
            ["posting"] = 0, ["journal"] = 0, ["position_risk"] = 0, ["fills"] = 0, ["marks"] = 0,
            ["conversion_rate"] = 0
        };
        Rewriting? rewrite = null;
        var replacedWhole = 0; // (each time: a rewrite prepared before it let go)
        var refreshes = new Refreshes();
        var markBand =
            new Deadband(Desk.Instruments.Length, _deadbandBps,
                _heartbeat); // each instrument's mark (Pivot:DeadbandBps, Pivot:Heartbeat)
        var rateBand = new Deadband(Pivot.Symbols.Length, _deadbandBps, _heartbeat); // each symbol's rate
        var lastMinute =
            new Queue<(DateTime At, int Trades)>(); // the live trades, burst by burst: their pace over the last minute
        var lastMinuteTrades = 0;
        var work = new Work();
        var nextReport = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var (gcThen, pausedThen, allocatedThen) = (
            (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)), GC.GetTotalPauseDuration(),
            GC.GetTotalAllocatedBytes()); // (the report's: what the GC did since the last)

        // ChalkQL's tables replaced whole with what's kept (stale: left behind, nobody watching) — and the statements it
        // holds planned for them at once (PlanNow): a run ChalkQL starts as the refresh publishes, its plans in effect the
        // moment it ends. Nothing waits for it — each pivot runs the plan it has meanwhile, which answers the same — and
        // what fails in it ChalkQL logs and tells the planning observer. A view moves to a new plan at its next update (under
        // low jitter, starts building it beside as soon as it's in). At the start it holds none.
        async Task ReplaceAll(DateTimeOffset clock)
        {
            (Posting[] allPostings, Journal[] allJournal, PositionRisk[] allRisk, HedgeFill[] allFills,
                    Mark[] allMarks, ConversionRate[] allRates) =
                ([.. keptPostings], [.. keptJournal], [.. keptRisk], [.. keptFills], [.. keptMarks],
                    [.. keptRates]); // (ChalkQL holds them: never changed)
            await engine.RefreshAsync(refresh => refresh.AdvanceClock(clock).Replace(ccys, Desk.Ccys)
                .Replace(instruments, Desk.Instruments).Replace(cptys, brokerage.Cptys)
                .Replace(accounts, brokerage.Accounts).Replace(postings, allPostings).Replace(journal, allJournal)
                .Replace(positionRisk, allRisk).Replace(fills, allFills)
                .Replace(marks, allMarks).Replace(rates, allRates)
                .Replace(zones, TimeZones.Offsets).PlanNow());
            stale = false;
            (held["posting"], held["journal"], held["position_risk"], held["fills"]) = (allPostings.Length,
                allJournal.Length, allRisk.Length, allFills.Length);
            (held["marks"], held["conversion_rate"]) = (allMarks.Length, allRates.Length);
            replacedWhole++;
        }

        // A rewrite of ChalkQL's tables prepared off the event loop — the as-of logs compacted, the ledger trimmed, as
        // ours are: the rows each holds (up to the cut) copied here; then, on a worker, rewritten and prepared (indexed, its
        // order checked) holding no lock, the loop appending meanwhile; spliced into ours once committed. None to do: none.
        Rewriting? Rewrite(bool compacting, bool trimming)
        {
            var copying = Stopwatch.GetTimestamp();
            offLoop.From(DateTime.UtcNow); // (its scratch idle: one rewrite at a time)
            var parts = new List<Func<Task<(PreparedReplacement, Action)>>>();
            if (compacting)
            {
                parts.Add(Part("position_risk", keptRisk, positionRisk, pools.Risk, 0, offLoop.Risk.Keep));
                parts.Add(Part("marks", keptMarks, marks, pools.Marks, 0, offLoop.Marks.Keep));
                parts.Add(Part("conversion_rate", keptRates, rates, pools.Rates, 0, offLoop.Rates.Keep));
            }

            if (trimming && keptJournal.Count > LedgerKept)
            {
                var from = keptJournal[^LedgerKept].UpdateTime; // (a posting's entries share its time: kept whole)
                var ledger = offLoop.Ledger; // (the journal's first: the postings kept are its entries')
                parts.Add(Part("journal", keptJournal, journal, pools.Journal, 0, rows => ledger.KeepJournal(rows, from)));
                parts.Add(Part("posting", keptPostings, postings, pools.Postings, 0,
                    rows => ledger.KeepPostings(rows, from)));
            }

            if (trimming && keptFills.Count > LedgerKept)
            {
                var drop = keptFills.Count - LedgerKept;
                parts.Add(Part("fills", keptFills, fills, pools.Fills, drop,
                    static rows => rows.Length)); // (the first dropped: not even copied)
            }

            if (parts.Count == 0) return null;
            var copyMs = Stopwatch.GetElapsedTime(copying).TotalMilliseconds;
            return new Rewriting(Task.Run(async () =>
            {
                var preparing = Stopwatch.GetTimestamp();
                var done = new List<(PreparedReplacement Prepared, Action Splice)>();
                try
                {
                    foreach (var part in parts) done.Add(await part());
                }
                catch
                {
                    foreach (var (prepared, _) in done) prepared.Dispose();
                    throw;
                }

                return (done.ToArray(), Stopwatch.GetElapsedTime(preparing).TotalMilliseconds);
            }), replacedWhole, compacting, copyMs);
        }

        // One table's part: its rows ChalkQL holds — ours up to the cut, but for those it drops first — copied into an
        // array from the table's pool (ours change on the loop meanwhile); then, on a worker, rewritten there in place, kept
        // here too (ours, spliced in once committed), and handed over whole: ChalkQL's till no snapshot reads it, then back
        // in the pool. The array's room past the rewrite takes what's appended meanwhile, and after.
        Func<Task<(PreparedReplacement, Action)>> Part<T>(string table, List<T> rows, PocoTable<T> handle,
            Pooled<T> pooled, int dropped, KeepRows<T> rewriting)
        {
            var cut = held[table];
            dropped = Math.Min(dropped, cut);
            var count = cut - dropped;
            var array = pooled.Pool.Rent(count + Math.Max(1024, count / 8));
            rows.CopyTo(dropped, array, 0, count);
            return async () =>
            {
                var kept = rewriting(array.AsSpan(0, count));
                array.AsSpan(kept, count - kept).Clear(); // (those it let go: not held by the array's room)
                pooled.Rewritten.Clear();
                pooled.Rewritten.AddRange(array.AsSpan(0, kept));
                PreparedReplacement prepared;
                try
                {
                    prepared = await engine.PrepareReplaceAsync(handle, array, kept, cut, stoppingToken);
                }
                catch
                {
                    pooled.Pool.Return(array, clearArray: true); // (refused: it took nothing, the array's ours again)
                    throw;
                }

                return (prepared, () =>
                {
                    var rewritten = pooled.Rewritten; // (ours: the array's ChalkQL's)
                    CollectionsMarshal.AsSpan(rewritten).CopyTo(CollectionsMarshal.AsSpan(rows));
                    rows.RemoveRange(rewritten.Count, cut - rewritten.Count);
                    held[table] += rewritten.Count - cut;
                });
            };
        }

        // The ledger kept to its latest entries and fills (and the house's cash: Ledger).
        void Trim()
        {
            if (keptJournal.Count > LedgerKept)
            {
                var from = keptJournal[^LedgerKept].UpdateTime; // (a posting's entries share its time: kept whole)
                var kept = onLoop.Ledger.KeepJournal(CollectionsMarshal.AsSpan(keptJournal), from);
                keptJournal.RemoveRange(kept, keptJournal.Count - kept);
                kept = onLoop.Ledger.KeepPostings(CollectionsMarshal.AsSpan(keptPostings), from);
                keptPostings.RemoveRange(kept, keptPostings.Count - kept);
            }

            if (keptFills.Count > LedgerKept) keptFills.RemoveRange(0, keptFills.Count - LedgerKept);
        }

        // The as-of logs kept as an as-of lookup needs them: each key's last row in each half minute (over the last seven
        // days and two hours, from now) or hour, and the latest.
        void Compact(DateTime now)
        {
            onLoop.From(now);
            onLoop.Risk.Keep(keptRisk);
            onLoop.Marks.Keep(keptMarks);
            onLoop.Rates.Keep(keptRates);
        }

        var priming = Stopwatch.GetTimestamp();
        var primed =
            Volatile.Read(
                ref _tradesPerSecond); // (the pace its history is primed at: a page setting another meanwhile primes it again)
        Prime(started, primed * 86_400);
        _log.LogInformation("Pivot: primed in {Ms:N0} ms", Stopwatch.GetElapsedTime(priming).TotalMilliseconds);
        // ChalkQL's tables loaded before any statement is prepared (step 1 waits for this): no pivot answers before its
        // history is in, and a statement's first plan is made for the tables as they are (prepared on empty ones, it would
        // run a plan for empty ones until ChalkQL planned it again as they filled). The load leaves its statistics to a
        // planning run (PlanNow); nothing is served yet, so that's awaited here (PlanAsync), or a first prepare could plan
        // on the numbers from before the load.
        try
        {
            await ReplaceAll(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
            Volatile.Write(ref _version, engine.Version);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "Pivot: ChalkQL's tables didn't load; loading them whole when a pivot first runs");
        }

        try
        {
            await engine.PlanAsync(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "Pivot: ChalkQL couldn't plan for the tables loaded; its background will");
        }

        _ready.TrySetResult(engine);

        // What's due: the market's tick, the next burst landing, a hedge, the stats, a compaction, the ledger's trim — and
        // each pivot changed since it last ran, once its backoff is over (a pivot admitted, or come back to: at once).
        var now = DateTime.UtcNow;
        DateTime lastMarket = now,
            nextMarket = now + MarketTick,
            nextStats = now + StatsEvery,
            nextCompact = now + _compactEvery,
            nextTrim = now + Compaction;
        // (the last position row's time — a trade's or a fill's: every row later — or now)
        DateTime statsFrom = now,
            lastBooked = keptRisk.Count > 0 && keptRisk[^1].UpdateTime > now ? keptRisk[^1].UpdateTime : now;
        double pace = primed, refreshMs = 0, runMs = 0;
        var burstNext = bursts.Next(now, pace);
        var hedgeChecks = new PriorityQueue<int, DateTime>();
        List<Topic> live = [], idle = []; // (the pivots someone's watching this time round, and those let go: refilled)
        await using var stopping = stoppingToken.Register(static wake => ((Waker)wake!).Signal(), _wake);

        // The pivots someone's watching, into a list kept from round to round.
        void Live(List<Topic> into)
        {
            into.Clear();
            lock (_gate)
            {
                foreach (var t in _topics.Values)
                    if (t.Live)
                        into.Add(t);
            }
        }

        static DateTime Sooner(DateTime a, DateTime b)
        {
            return a <= b ? a : b;
        }

        static TimeSpan Gap(Topic topic)
        {
            return TimeSpan.FromMilliseconds(topic.GapMs);
        } // (its backoff: as long as its last run — a first answer's, none)

        static DateTime? Earliest(DateTime? a, DateTime b)
        {
            return a is { } x && x <= b ? x : b;
        }

        refreshes.Started();
        while (true)
        {
            Live(live);
            var wakeAt = Sooner(Sooner(nextMarket, burstNext.Start + burstNext.Span),
                Sooner(nextStats, Sooner(nextCompact, nextTrim))); // (what's due next — a hedge, or a pivot past its backoff, if sooner)
            if (hedgeChecks.TryPeek(out _, out var check) && check < wakeAt) wakeAt = check;
            foreach (var t in live)
                if (t.Dirty && t.RanAt + Gap(t) is var due && due < wakeAt)
                    wakeAt = due;
            if (wakeAt - DateTime.UtcNow is { Ticks: > 0 } wait) await _wake.WaitAsync(wait);
            stoppingToken.ThrowIfCancellationRequested();
            using var busy = _load.Measure(); // (the rest of the turn: the loop's load, its health reports')

            while (_admitted.Reader.TryRead(out var admitted)) admitted.Urgent = true;
            Live(live);

            now = DateTime.UtcNow;
            DateTime?
                changedAt = null; // (the earliest change loaded this time round, as it was due: a pivot's latency from it)

            // The market and the rates: their tick — and each asset's hedge looked at now and then, more often as the pace
            // rises (by its square root).
            if (now >= nextMarket)
            {
                var seconds = (now - lastMarket).TotalSeconds;
                market.Step(seconds);
                // The marks and rates applied — kept, loaded into ChalkQL — those moved past the deadband or a heartbeat
                // old; and on the first tick in a half minute and the last before the next, every one (a day's close is
                // on the hour, a drawdown's point on the half minute: valued exactly — every instrument and currency with
                // a mark and a rate in each half minute, its last in it what an as-of lookup at its end finds).
                var full = (now + MarketTick).Ticks / Fine.Ticks != now.Ticks / Fine.Ticks ||
                           now.Ticks / Fine.Ticks != lastMarket.Ticks / Fine.Ticks;
                fx.Next(now, ticked, full);
                foreach (var i in Desk.Instruments) // (a mark made only where the deadband applies it: most aren't)
                    if (market.Price(i, fx) is var price && markBand.Applies(i.InstrumentId - 1, price, now, full))
                        marked.Add(new Mark(now, i.InstrumentId, price));
                rateBand.Apply(ticked, r => Array.IndexOf(Pivot.Symbols, r.Symbol), r => r.UsdRate, now, full);
                var hedgesPerSecond = Math.Min(1 / seconds,
                    HedgesPerSecond * Math.Sqrt(Math.Max(1, pace * 86_400 / DefaultTradesPerDay)));
                for (var a = 0; a < Pivot.Assets.Length; a++)
                    if (random.NextDouble() < hedgesPerSecond * seconds)
                        hedgeChecks.Enqueue(a, now);
                if (marked.Count + ticked.Count > 0)
                    changedAt = Earliest(changedAt, nextMarket); // (the tick, as it was due — if any of it applied)
                (lastMarket, nextMarket) = (now, now + MarketTick);
            }

            // The clients' bursts that have landed (their trades spread over each, in order) — and the hedger reacting, a
            // moment after, in each asset traded. A new pace: the next burst drawn again.
            if (Volatile.Read(ref _tradesPerSecond) is var asked && asked != pace)
            {
                var repriming = Stopwatch.GetTimestamp();
                Prime(now,
                    asked * 86_400); // (its history at the new pace: ChalkQL's tables replaced whole, the pivots run again)
                (markBand, rateBand) = (new Deadband(Desk.Instruments.Length, _deadbandBps, _heartbeat),
                    new Deadband(Pivot.Symbols.Length, _deadbandBps, _heartbeat)); // (applied anew)
                (pace, burstNext, lastBooked, stale) = (asked, bursts.Next(now, asked),
                    keptRisk.Count > 0 && keptRisk[^1].UpdateTime > now ? keptRisk[^1].UpdateTime : now, true);
                hedgeChecks.Clear();
                lastMinute.Clear();
                lastMinuteTrades = 0;
                _log.LogInformation("Pivot: primed again in {Ms:N0} ms", Stopwatch.GetElapsedTime(repriming).TotalMilliseconds);
            }

            while (burstNext.Start + burstNext.Span <= now)
            {
                var traded = new SortedSet<int>();
                for (var i = 0; i < burstNext.Size; i++)
                    traded.Add(brokerage.Trade(burstNext.Start + burstNext.Span * ((i + 1.0) / (burstNext.Size + 1)),
                        market, newPostings, newJournal, newRisk, burstNext.Assets));
                var end = burstNext.Start + burstNext.Span;
                if (newRisk.Count > 0 && newRisk[^1].UpdateTime > lastBooked) lastBooked = newRisk[^1].UpdateTime;
                changedAt = Earliest(changedAt, end); // (the burst, as it landed)
                foreach (var asset in traded)
                    hedgeChecks.Enqueue(asset, end + TimeSpan.FromMilliseconds(100 + 400 * random.NextDouble()));
                lastMinute.Enqueue((end, burstNext.Size));
                lastMinuteTrades += burstNext.Size;
                burstNext = bursts.Next(end, pace);
            }

            // (in time order — a fill's after the last row, a trade's or a fill's — and before the next burst, whose trades
            // come after it: the house's positions, its hedges among them, each in time order)
            while (hedgeChecks.TryPeek(out var asset, out var at) && at <= now && at < burstNext.Start)
            {
                hedgeChecks.Dequeue();
                lastBooked = at > lastBooked ? at : lastBooked.AddTicks(10);
                var filled = newFills.Count;
                hedger.Hedge(lastBooked, asset, brokerage.HouseHolds(asset), market, fx, brokerage, newPostings,
                    newJournal, newFills, newRisk);
                if (newFills.Count > filled) changedAt = Earliest(changedAt, at); // (a fill, as its check was due)
            }

            // What's kept: the new rows; every 30 s the as-of logs compacted and the ledger trimmed — whether anyone's
            // watching or not. Someone watching, ChalkQL's tables are rewritten as ours are by a rewrite prepared off the loop
            // (Pivot:PreparedCompaction), committed once it's ready; otherwise (or nobody watching) ours are rewritten here,
            // and ChalkQL's replaced in this refresh (or whole, when someone is).
            keptPostings.AddRange(newPostings);
            keptJournal.AddRange(newJournal);
            keptRisk.AddRange(newRisk);
            keptFills.AddRange(newFills);
            // A tick's marks and rates: each instrument's and symbol's once, at the tick's time, later than the last — so an
            // as-of lookup meets no ties, whatever ChalkQL plans it as. Asserted here (a Debug build's check, none in Release),
            // not declared as a unique key: ChalkQL would check every row it holds against one at each refresh.
            Debug.Assert(Unique(marked, keptMarks, m => m.Ts, m => m.InstrumentId),
                "a tick's marks: one an instrument, later than the last");
            Debug.Assert(Unique(ticked, keptRates, r => r.Ts, r => r.Symbol),
                "a tick's rates: one a symbol, later than the last");
            keptMarks.AddRange(marked);
            keptRates.AddRange(ticked);
            bool compacting = now >= nextCompact, trimmed = now >= nextTrim;
            if ((compacting || trimmed) && _preparedCompaction && !stale && live.Count > 0)
            {
                if (rewrite is null)
                {
                    rewrite = Rewrite(compacting, trimmed);
                    if (compacting) nextCompact = now + _compactEvery;
                    if (trimmed) nextTrim = now + Compaction;
                }

                (compacting, trimmed) =
                    (false, false); // (none replaced in this refresh — one prepared already: still due)
            }
            else if (compacting || trimmed)
            {
                var rewriting = Stopwatch.GetTimestamp();
                if (compacting)
                {
                    Compact(now);
                    nextCompact = now + _compactEvery;
                }

                if (trimmed)
                {
                    Trim(); // (ChalkQL's ledger tables replaced with what's kept, next)
                    nextTrim = now + Compaction;
                }

                refreshes.OnLoop(Stopwatch.GetElapsedTime(rewriting).TotalMilliseconds);
                replacedWhole++; // (ours rewritten here: a rewrite prepared from them let go)
            }

            // A rewrite prepared: committed in this refresh, if ChalkQL's tables are still those it was prepared from.
            (PreparedReplacement Prepared, Action Splice)[]? commit = null;
            var commitCompacts = false;
            if (rewrite is { Preparing.IsCompleted: true } ready)
            {
                rewrite = null;
                if (ready.Preparing.IsCompletedSuccessfully && ready.Generation == replacedWhole && !stale &&
                    live.Count > 0)
                {
                    (commit, commitCompacts) = (ready.Preparing.Result.Parts, ready.Compacts);
                    refreshes.Prepared(ready.Preparing.Result.Ms, ready.CopyMs);
                }
                else
                {
                    if (ready.Preparing.IsCompletedSuccessfully)
                        foreach (var (prepared, _) in ready.Preparing.Result.Parts)
                            prepared.Dispose();
                    else
                        _log.LogWarning(ready.Preparing.Exception,
                            "Pivot: a rewrite of ChalkQL's tables didn't prepare; another when next due");
                    refreshes.LetGo++;
                }
            }

            // ChalkQL loaded with what's changed — the new rows appended (in order; statistics deferred: an append costs what
            // it adds), the compacted logs and the trimmed ledger replaced, the engine's clock advanced (a view's now, the
            // current day's close) — and
            // each pivot reading them marked to run again. Nobody watching: left behind, replaced whole when someone is.
            var changed = newPostings.Count + newFills.Count + marked.Count + ticked.Count > 0 || compacting ||
                          trimmed || commit is not null;
            var allocated = GC.GetTotalAllocatedBytes(true); // (an append's cost to the process, from here: its batch, its refresh)
            if (live.Count == 0 && HouseDrawdown.Routes == 0) // (nobody watching a pivot, nor the house's drawdown)
            {
                stale = true;
            }
            else if (changed || stale)
            {
                var refreshing = Stopwatch.GetTimestamp();
                var replacing =
                    stale || compacting || commitCompacts; // (tables a pivot reads replaced, not appended to)
                // The clock (every query's now): now — or the last fill's time, a few ticks on (fills at one instant are
                // spaced apart), so nothing loaded is later than the clock and every row is seen as it lands.
                var clock = new DateTimeOffset(lastBooked > now ? lastBooked : now, TimeSpan.Zero);
                try
                {
                    if (stale)
                    {
                        await ReplaceAll(clock);
                    }
                    else
                    {
                        if (commit is not null) // (first: its tables' rows since the cut carried over — this time round's appended after)
                        {
                            var committing = Stopwatch.GetTimestamp();
                            try
                            {
                                await engine.RefreshAsync((Commit: commit, Defer: _deferPlanning), static (refresh, s) =>
                                {
                                    foreach (var (prepared, _) in s.Commit) refresh.Commit(prepared);
                                    if (s.Defer) refresh.DeferPlanning();
                                }); // (a static plan: nothing of the loop's captured)
                            }
                            finally
                            {
                                foreach (var (prepared, _) in commit) prepared.Dispose();
                            } // (committed: nothing; refused: let go)

                            foreach (var (_, splice) in commit) splice();
                            refreshes.Rewritten(Stopwatch.GetElapsedTime(committing).TotalMilliseconds);
                        }

                        var appending = Stopwatch.GetTimestamp();
                        // What it brings: appended, the loop's own lists (ChalkQL copies them as the refresh runs, which the
                        // loop awaits, adding to none meanwhile); replaced, copies (a table keeps the rows it's given) — the
                        // ledger's tables whole once trimmed, the as-of logs once compacted.
                        IReadOnlyList<Posting> batchPostings = trimmed ? keptPostings.ToArray() : newPostings;
                        IReadOnlyList<Journal> batchJournal = trimmed ? keptJournal.ToArray() : newJournal;
                        IReadOnlyList<HedgeFill> batchFills = trimmed ? keptFills.ToArray() : newFills;
                        IReadOnlyList<PositionRisk> risk = compacting ? keptRisk.ToArray() : newRisk;
                        IReadOnlyList<Mark> markRows = compacting ? keptMarks.ToArray() : marked;
                        IReadOnlyList<ConversionRate> rateRows = compacting ? keptRates.ToArray() : ticked;
                        await engine.RefreshAsync(
                            new Batch(tables, clock, trimmed, compacting, _deferPlanning, batchPostings, batchJournal,
                                batchFills, risk, markRows, rateRows),
                            static (refresh, batch) => batch.Plan(refresh)); // (a static plan: no closure, no delegate)
                        if (trimmed)
                        {
                            (held["posting"], held["journal"], held["fills"]) = (keptPostings.Count, keptJournal.Count,
                                keptFills.Count);
                        }
                        else
                        {
                            if (batchPostings.Count > 0)
                                (held["posting"], held["journal"]) = (held["posting"] + batchPostings.Count,
                                    held["journal"] + batchJournal.Count);
                            held["fills"] += batchFills.Count;
                        }

                        if (compacting)
                        {
                            (held["position_risk"], held["marks"], held["conversion_rate"]) = (
                                keptRisk.Count, keptMarks.Count, keptRates.Count);
                        }
                        else
                        {
                            held["position_risk"] += risk.Count;
                            (held["marks"], held["conversion_rate"]) = (held["marks"] + markRows.Count,
                                held["conversion_rate"] + rateRows.Count);
                        }

                        if (compacting || trimmed) refreshes.Rewritten(Stopwatch.GetElapsedTime(appending).TotalMilliseconds);
                        else
                            refreshes.Appended(Stopwatch.GetElapsedTime(appending).TotalMilliseconds,
                                rewrite is not null || commit is not null, GC.GetTotalAllocatedBytes(true) - allocated);
                    }

                    refreshMs = Stopwatch.GetElapsedTime(refreshing).TotalMilliseconds;
                    Refreshed(engine.Version, pace); // (where it stands now: a pace asked for, applied, told)
                    _drawdown.Changed();
                    foreach (var topic in live)
                    {
                        topic.Dirty = true;
                        topic.Replaced |= replacing;
                        if (changedAt is { } at)
                            topic.ChangedAt = Earliest(topic.ChangedAt, at); // (the earliest it hasn't answered yet)
                    }
                }
                catch (Exception e) when
                    (e is not OperationCanceledException) // a refresh refused (a table not as declared, say): logged, not fatal —
                {
                    // the tables replaced whole next time
                    _log.LogError(e, "Pivot: ChalkQL's refresh failed; replacing its tables whole next time");
                    stale = true;
                }
            }

            newPostings.Clear();
            newJournal.Clear();
            newRisk.Clear();
            newFills.Clear();
            marked.Clear();
            ticked.Clear();

            // The pivots due: one admitted, or come back to, at once; one whose tables changed, once its backoff is over
            // (as long as its last run) — on this loop, so a run never overlaps a refresh.
            live.Sort(static (a, b) => a.RanAt.CompareTo(b.RanAt)); // (the longest waiting first)
            foreach (var topic in live)
            {
                if (!topic.Urgent && !(topic.Dirty && DateTime.UtcNow >= topic.RanAt + Gap(topic))) continue;
                try
                {
                    if (stale)
                    {
                        await ReplaceAll(new DateTimeOffset(DateTime.UtcNow)); // (left behind: nobody was watching)
                        Refreshed(engine.Version, pace);
                    }
                    (topic.Urgent, topic.Dirty) = (false, false);
                    var since = topic.ChangedAt; // (what this run answers: everything loaded so far)
                    if (since is { } earliest && topic.Floor is { } floor && earliest < floor)
                        since = floor; // (just after a first answer: what waited on its view being made, from its post)
                    topic.Floor = null;
                    (topic.ChangedAt, topic.Watermark) = (null, since);
                    if (topic.BindsNow && Unfit(topic.Query))
                    {
                        await Replan(engine, topic,
                            stoppingToken); // (a snapshot's: a table it reads changed shape — a drift's planned again in place)
                    }
                    else if (topic.Plan is { } planned &&
                             planned != topic.Query
                                 .PlanDigest) // (planned again by ChalkQL: swapped in at a refresh, or by PlanAsync after a whole replacement)
                    {
                        refreshes.Swaps++;
                        _log.LogInformation(
                            "Pivot: {Name}'s plan swapped in, planned again as its tables moved: {Old:x16} → {New:x16} (logical digest {Logical:x16})",
                            topic.Name, planned, topic.Query.PlanDigest, topic.Query.LogicalDigest);
                    }

                    topic.Plan = topic.Query.PlanDigest;
                    if (topic.Shell is not null)
                        await RunShellAsync(engine, topic, work, stoppingToken); // (its cells carrying extras: its shell's)
                    else if (!topic.BindsNow)
                        await RunViewAsync(engine, topic, work,
                            stoppingToken); // (@now: a view reads the engine's clock — not named: by snapshot)
                    else await RunAsync(engine, topic, work, stoppingToken);
                    topic.RanAt = DateTime.UtcNow;
                    if (topic.Watermark is { } from)
                        topic.Latency.Add((topic.RanAt - from)
                            .TotalMilliseconds); // (from the earliest change it answers to its post — a first answer: none)
                    if (topic.FirstAnswer)
                        (topic.GapMs, topic.Floor, topic.FirstAnswer) =
                            (0, topic.RanAt, false); // (a view just made: its next update at once)
                    else (topic.GapMs, topic.Floor) = (topic.QueryMs, null);
                    runMs += topic.QueryMs;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogError(e, "Pivot: ChalkQL's refresh failed; replacing its tables whole next time");
                    stale = true;
                }
            }

            // The house's drawdown: a run done published, the next begun when due (off the loop: it reads its snapshots).
            _drawdown.Tick(engine, DateTime.UtcNow, stoppingToken);

            // Each second: pivots nobody's watching stopped (and let go after a minute), and the stats.
            if (now < nextStats) continue;
            lock (_gate)
            {
                idle.Clear();
                foreach (var t in _topics.Values)
                    if (t.Live && t.Source.Routes == 0)
                        (t.Live, t.IdleSince) =
                            (false, DateTime.UtcNow); // nobody left (one failing posts nothing: no 202 tells it)
                    else if (!t.Live && DateTime.UtcNow - t.IdleSince > Idle)
                        idle.Add(t);
                foreach (var t in idle) LetGo(t);
            }

            _pivotMs += (runMs * 1000 / Math.Max(1, (now - statsFrom).TotalMilliseconds) - _pivotMs) /
                        4; // (ms a second, smoothed)
            if (now >= nextReport) // (every 10 s: what each pivot's view drained, and its checks)
            {
                nextReport = now + TimeSpan.FromSeconds(10);
                foreach (var t in live)
                    _log.LogInformation("Pivot: {Name} — {Latency}{Drains}{Checks}{Updates}{Plans}{Planning}", t.Name,
                        t.Latency, t.Drains.ToString() is { Length: > 0 } drains ? $" · {drains}" : "",
                        _validate && t.Check.ToString() is { Length: > 0 } checks ? $" · validating: {checks}" : "",
                        t.Updates.ToString() is { Length: > 0 } updates ? $" · {updates}" : "",
                        t.Plans is { } plans ? $" · {plans}" : "",
                        _planning.Of(t.Query.LogicalDigest) is { } judged ? $" · its statement {judged}" : "");
                var (gcNow, pausedNow) = ((GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)),
                    GC.GetTotalPauseDuration());
                _log.LogInformation(
                    "Pivot: GC in the last 10 s — {Gen0} gen0, {Gen1} gen1, {Gen2} gen2, paused {Paused:F0} ms; {Allocated:N0} MB allocated; heap {Heap:N0} MB at the last",
                    gcNow.Item1 - gcThen.Item1, gcNow.Item2 - gcThen.Item2, gcNow.Item3 - gcThen.Item3,
                    (pausedNow - pausedThen).TotalMilliseconds,
                    (GC.GetTotalAllocatedBytes() - allocatedThen) >> 20,
                    GC.GetGCMemoryInfo().HeapSizeBytes >> 20); // (what the heap held after the last collection: the pivots' views, ChalkQL's tables)
                (gcThen, pausedThen, allocatedThen) = (gcNow, pausedNow, GC.GetTotalAllocatedBytes());
                if (_allocations is { } sampled)
                    _log.LogInformation("Pivot: allocated in the last 10 s, by type (sampled) — {Types}", sampled.Take(16));
                _log.LogInformation("Pivot: ChalkQL's refreshes — {Refreshes}", refreshes);
                if (_drawdown.Report() is { Length: > 0 } drawdown) _log.LogInformation("Pivot: the house's drawdown — {Drawdown}", drawdown);
                _log.LogInformation("Pivot: ChalkQL's planning — {Planning}", _planning);
                _log.LogInformation(
                    "Pivot: deadband ({Bps} bp, {Heartbeat:N0} s heartbeat) — marks applied {Marks}, rates {Rates}",
                    _deadbandBps, _heartbeat.TotalSeconds, markBand, rateBand);
            }

            (runMs, statsFrom, nextStats) = (0, now, now + StatsEvery);
            while (lastMinute.TryPeek(out var oldest) && oldest.At < now - TimeSpan.FromMinutes(1))
                lastMinuteTrades -= lastMinute.Dequeue().Trades;
            Topic[] alive;
            long reserved;
            lock (_gate)
            {
                (alive, reserved) = ([.. _topics.Values], Reserved());
            }

            var (positionsHeld, open) = brokerage.Count();
            var unhedged = Enumerable.Range(0, Pivot.Assets.Length).Sum(a =>
                Math.Abs((double)(brokerage.HouseHolds(a) + hedger.Holds(a)) * market.UsdPrice(a)));
            PublishStats(new
            {
                trades = brokerage.Trades, clients = brokerage.Clients, journal = brokerage.Entries,
                positions = positionsHeld, open, hedges = hedger.Fills, unhedgedUsd = Math.Round(unhedged),
                tradesPerDay = Math.Round(TradesPerDay),
                rows = keptPostings.Count + keptJournal.Count + keptRisk.Count + keptFills.Count +
                       keptMarks.Count + keptRates.Count,
                tradesPerMinute = Math.Round(lastMinuteTrades * 60 / Math.Clamp((now - started).TotalSeconds, 1, 60)),
                refreshMs = Math.Round(refreshMs, 2),
                usdRates = Pivot.Currencies.ToDictionary(c => c,
                    c => Math.Round(fx.UsdRate(c),
                        8)), // (what a US dollar is in each: the page's rounding, a dollar's worth)
                prepared = Interlocked.Read(ref _prepared), sharedByText = _sharedByText,
                sharedByDigest = _sharedByDigest,
                memoryBudget = _memoryBudget, memoryReserved = reserved, pivotMs = Math.Round(_pivotMs, 1),
                pivotBudgetMs = 1000 * TimeShare,
                topics = alive.Select(t => new
                {
                    name = t.Name, live = t.Live, subscribers = t.Source.Routes, cells = t.CellCount, posted = t.Posted,
                    checksum = $"0x{t.Checksum:X8}", queryMs = Math.Round(t.QueryMs, 2),
                    superseded = t.Superseded, error = t.Error,
                    reserved = t.Reserved, arena = t.ArenaBudget, arenaPeak = t.ArenaPeak, texts = t.Texts.Count,
                    maintenance = t.Maintenance.ToString(),
                    refusal = t.Refusal // (asked to be maintained incrementally, and refused: why)
                })
            });
        }
    }

    /// <summary>
    ///     A pivot's statement run in full, as of now, into cells — or what's wrong: in the pivot's own arena, at most its
    ///     budget (grown, while the provider's has room, and run again).
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))] // (a snapshot pivot's every run: pooled)
    private async ValueTask<string?> ComputeAsync(ChalkEngine engine, Topic topic, PreparedQuery query, Columns columns,
        List<PivotKey> keys, List<PivotCell> rows, HashSet<PivotKey> seen, CancellationToken ct,
        List<WideRow>? wides = null) // (an extended pivot's: its rows wide)
    {
        keys.Clear();
        rows.Clear();
        seen.Clear();
        wides?.Clear();
        string? problem = null;
        for (;;)
            try
            {
                // ts is a wall clock (TIMESTAMP): @now is bound as one — the time in UTC, of kind Unspecified.
                if (topic.BindsNow)
                    topic.Values["now"] = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
                await using var execution = topic.Values.Count > 0
                    ? await engine.ExecuteAsync(query, topic.Values, topic.Arena,
                        ct) // (a list: expanded, one placeholder each)
                    : await engine.ExecuteAsync(query, (IReadOnlyList<object?>?)null, topic.Arena, ct);
                await foreach (var batch in execution.Batches.WithCancellation(ct))
                    using (batch)
                    {
                        problem ??= wides is null ? Read(batch, columns, keys, rows, seen) : ReadWide(batch, columns, keys, rows, seen, wides);
                    }

                break;
            }
            catch (ChalkException e) when (Exceeded(e) is { } needed)
            {
                if (Grow(topic,
                        needed)) // a bigger arena: once more (the run let go — not in a filter, which runs before it is)
                {
                    problem = null;
                    keys.Clear();
                    rows.Clear();
                    seen.Clear();
                    wides?.Clear();
                    continue;
                }

                long free;
                lock (_gate)
                {
                    free = _memoryBudget - Reserved();
                }

                problem = topic.ArenaBudget >= ArenaMax
                    ? $"This pivot needs more than {MB(ArenaMax)} to compute, the most a pivot is given: group by fewer dimensions, or coarser ones, or a shorter window."
                    : $"This pivot needs more than its {MB(topic.ArenaBudget)} to compute, and the provider's memory budget has {MB(Math.Max(0, free))} free.";
                break;
            }
            catch (ChalkException e)
            {
                problem = $"ChalkQL: {e.Message}";
                break;
            }

        topic.ArenaPeak = Math.Max(topic.ArenaPeak, topic.Arena.PeakBytes);
        if (problem is null && wides is null) // (an extended pivot's rows, validated, aren't its cells: its shell keeps those it keeps)
            lock (_gate)
            {
                if (keys.Count > topic.CellCount &&
                    Reserved() + (long)(keys.Count - topic.CellCount) * CellBytes > _memoryBudget)
                    problem =
                        $"This pivot's {keys.Count:N0} cells need {MB((long)keys.Count * CellBytes)} in the router, and the provider's memory budget hasn't the room.";
            }

        return problem;
    }

    /// <summary>A pivot failed: its subscribers told (its last state kept); the next answer recovers it.</summary>
    private void Failed(Topic topic, string problem)
    {
        if (topic.Error != problem) _log.LogWarning("Pivot: {Name} failed: {Problem}", topic.Name, problem);
        topic.Error = problem;
        topic.Source.Fail(FaultCode.SourceFailed, problem);
    }

    /// <summary>
    ///     A pivot answered (or current already): the rendezvous awaiting it handed back once the page has it; nobody left,
    ///     stopped.
    /// </summary>
    private void Answered(Topic topic, PublishResult? posted, long version)
    {
        topic.Error = null;
        if (posted is not null) topic.PostedAt(version);
        List<(Mailbox Mailbox, byte[] Correlation)>? waiting = null;
        lock (_gate)
        {
            if (posted is not null) topic.Posted++;
            if (topic.Waiting.Count > 0)
                (waiting, topic.Waiting) = (topic.Waiting, []); // (a list made anew only for those waiting: rarely)
        }

        if (waiting is not null)
            foreach (var (mailbox, correlation) in waiting)
                topic.TryRendezvous(mailbox, correlation); // its answer: theirs once applied
        if (posted is { Status: PublishStatus.NoListeners } result)
            lock (_gate)
            {
                if (topic.Epoch == result.Epoch)
                    (topic.Live, topic.IdleSince) = (false, DateTime.UtcNow); // nobody left: stopped
            }
    }

    /// <summary>
    ///     A pivot's run, by snapshot (a statement binding @now: a view reads the engine's clock instead): its statement as of
    ///     now, the answer posted whole — totals included, with its checksum.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))] // (a run each time round: its state machine pooled)
    private async ValueTask RunAsync(ChalkEngine engine, Topic topic, Work work, CancellationToken ct)
    {
        if (topic.Posted == 0)
            (topic.Watermark, topic.FirstAnswer) = (null, true); // (a first answer: in no sweep time, nor latency)
        var querying = Stopwatch.GetTimestamp();
        var problem =
            await ComputeAsync(engine, topic, topic.Query, topic.Columns, work.Keys, work.Rows, work.Seen, ct);
        topic.QueryMs = Stopwatch.GetElapsedTime(querying).TotalMilliseconds;
        if (problem is not null)
        {
            Failed(topic, problem);
            return;
        }

        var cells = Table<PivotKey, PivotCell>.ChecksumOf(CollectionsMarshal.AsSpan(work.Keys),
            CollectionsMarshal.AsSpan(work.Rows));
        topic.CellCount = work.Keys.Count;
        var version = engine.Version; // (what it ran at: the engine's last refresh — a snapshot has no view's stamp)
        topic.Stamped = version;
        var (clock, clockHash) = Clock(topic); // (its clock cell, in the same post)
        work.Keys.Add(ClockKey);
        work.Rows.Add(clock);
        topic.Checksum = cells + clockHash;
        Answered(topic,
            topic.Ingest!.PostSnapshot(CollectionsMarshal.AsSpan(work.Keys), CollectionsMarshal.AsSpan(work.Rows),
                version, topic.Checksum), version);
    }

    /// <summary>
    ///     Whether a statement must be prepared again: its plan no longer fits the catalog, a table it reads having changed
    ///     shape. One that drifted keeps running correctly, stale past tenfold, and ChalkQL plans it again in place, in its
    ///     background — as the loop's refreshes defer their planning (Pivot:DeferPlanning); without that, a statement stale
    ///     for drift is prepared again too.
    /// </summary>
    private bool Unfit(PreparedQuery query)
    {
        return _deferPlanning ? query.ChangedShape : query.IsStale;
    }

    /// <summary>
    ///     A snapshot pivot's statement prepared again, its plan no longer fitting the catalog (<see cref="Unfit" />): the
    ///     plan for
    ///     the tables as they are. (A view prepares its own again, which <see cref="Follow" /> takes up.) It keeps its topic:
    ///     named by its logical digest, which the planner's choices don't move.
    /// </summary>
    private async Task Replan(ChalkEngine engine, Topic topic, CancellationToken ct)
    {
        var stale = topic.Query;
        var fresh = await engine.PrepareAsync(topic.Sql, new PrepareOptions { IncludePlanText = true }, ct);
        Interlocked.Increment(ref _prepared);
        lock (_gate)
        {
            if (_plans.TryGetValue(topic.Sql, out var cached) && cached == stale)
                _plans[topic.Sql] = fresh; // (the next page asking: this plan)
        }

        topic.Query = fresh;
        _log.LogInformation(
            "Pivot: {Name}'s statement prepared again, stale: plan {Old:x16} → {New:x16}, logical digest {OldLogical:x16} → {NewLogical:x16}",
            topic.Name, stale.PlanDigest, fresh.PlanDigest, stale.LogicalDigest, fresh.LogicalDigest);
    }

    /// <summary>
    ///     A view pivot onto the statement its view prepared anew (a table it reads changed shape: a drift is planned again in
    ///     place, the statement kept), found after the update: the one its view runs, so a run of it still gives the view's
    ///     cells bit for bit, its plan is the one shown and handed to the next page asking, and the one it held is let go.
    /// </summary>
    private void Follow(Topic topic, PreparedQuery now)
    {
        var held = topic.Query;
        lock (_gate)
        {
            if (_plans.TryGetValue(topic.Sql, out var cached) && cached == held)
                _plans[topic.Sql] = now; // (the next page asking: this plan)
        }

        (topic.Query, topic.Plan) = (now, now.PlanDigest); // (no swap at a refresh, this)
        _log.LogInformation(
            "Pivot: {Name}'s statement prepared anew by its view: plan {Old:x16} → {New:x16}, logical digest {OldLogical:x16} → {NewLogical:x16}",
            topic.Name, held.PlanDigest, now.PlanDigest, held.LogicalDigest, now.LogicalDigest);
    }

    /// <summary>
    ///     A pivot's run, by standing view: the view brought to ChalkQL's latest epoch, and what changed since the pivot's
    ///     cursor
    ///     drained into its table — the whole answer after a reset (a snapshot), else the cells that changed and those gone —
    ///     with the view's version and checksum (which the router checks its table against). Validating: the same statement
    ///     run in full at this epoch, its checksum the view's; and the router's table, once it has the version posted before,
    ///     cell by cell what that run gave.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))] // (a run each time round: its state machine pooled)
    private async ValueTask RunViewAsync(ChalkEngine engine, Topic topic, Work work, CancellationToken ct)
    {
        if (_validate && topic.Check.Pending is { } pending && topic.Cells!.UpstreamVersion == pending.Version)
        {
            work.Table.Clear();
            topic.Cells.CopyTo(work.Table);
            work.Table.RemoveAll(cell => cell.Key.Equals(ClockKey)); // (its clock cell: no cell of the statement's)
            topic.Check.Table(work.Table, pending.Cells, pending.Version);
            topic.Check.Pending = null;
        }

        var running = Stopwatch.GetTimestamp();
        string? problem = null;
        var drained = work.Drained;
        drained.Clear();
        try
        {
            var first = topic.View is null;
            if (first)
                (topic.Watermark, topic.FirstAnswer) =
                    (null, true); // (its view made: a first answer — in no sweep time, nor latency: its time is "first update")
            var updating = Stopwatch.GetTimestamp(); // (the first: making the view too — it builds what it keeps)
            var allocated = GC.GetTotalAllocatedBytes(); // (the process's: the update's, and whatever ran beside it)
            if (first)
                (topic.View, topic.Updates.Mode) = (await Materialize<PivotCell>(engine, topic,
                    (in ViewRow row, out PivotKey key, out PivotCell cell) => Map(row, topic.Columns, out key, out cell), ct), topic.Maintenance);
            await topic.View!.UpdateAsync(ct);
            if (topic.View.Statement is var now && now != topic.Query)
                Follow(topic, now); // (prepared anew by its view: no longer the one it was made over)
            if (topic.Plans is { } plans) Track(topic, plans);
            topic.Updates.Record(first, topic.Replaced, Stopwatch.GetElapsedTime(updating).TotalMilliseconds,
                GC.GetTotalAllocatedBytes() - allocated, topic.View.StateBytes);
            if (topic.Resync)
                (topic.Cursor, topic.Resync) =
                    (ViewCursor.Start, false); // (a new epoch: the whole answer — its reader from the start)
            while (topic.View.Drain(ref topic.Cursor, drained, 1 << 20) == DrainOutcome.Partial)
            {
            }

            if (!first) topic.Updates.Changed(topic.Replaced, drained.Keys.Count, drained.WasReset);
            topic.Replaced = false;
        }
        catch (Exception e) when (e is ChalkException or StandingViewException or InvalidOperationException)
        {
            problem = $"ChalkQL: {e.Message}";
            if (topic.View is { } broken) _ = broken.DisposeAsync().AsTask();
            (topic.View, topic.Cursor) = (null, ViewCursor.Start); // (made again next run: a reset, a snapshot)
        }

        topic.QueryMs = Stopwatch.GetElapsedTime(running).TotalMilliseconds;
        if (problem is not null)
        {
            Failed(topic, problem);
            return;
        }

        var view = topic.View!;
        (topic.Generation, topic.Stamped) = (drained.Generation, drained.Version);
        var (clock, clockHash) =
            Clock(topic); // (its clock cell, in the same post — every run's: one changing no cell too)
        drained.Keys.Add(ClockKey);
        if (drained.WasReset) drained.Rows.Add(clock);
        else drained.Changes.Add(new CellChange(clock, 0));
        var k = CollectionsMarshal.AsSpan(drained.Keys);
        PublishResult? posted;
        try
        {
            posted = drained.WasReset
                ? topic.Ingest!.PostSnapshot(k, CollectionsMarshal.AsSpan(drained.Rows), drained.Version,
                    drained.Checksum + clockHash)
                : topic.Ingest!.Post(k, CollectionsMarshal.AsSpan(drained.Changes), upstreamVersion: drained.Version,
                    expectedChecksum: drained.Checksum + clockHash);
            // (a snapshot's rows hashed as posted: what the router's table should hold after it — the fault's evidence)
            topic.Remember(new PostRecord(drained.Version, drained.WasReset, k.Length, drained.Checksum + clockHash,
                drained.WasReset ? Table<PivotKey, PivotCell>.ChecksumOf(k, CollectionsMarshal.AsSpan(drained.Rows)) : 0,
                DateTime.UtcNow));
        }
        catch (StreamFaultException e) // the table faulted (its checksum, say): changes can't follow —
        {
            // the reader reset, so the next drain is the whole answer, recovering it
            _log.LogWarning("Pivot: {Name}'s table is faulted ({Fault}); its view's reader reset", topic.Name,
                e.Message);
            _log.LogWarning("Pivot: {Name}'s fault, its post: {Post}", topic.Name, FaultingPost(topic, e.Message));
            if (!drained.WasReset)
                _log.LogWarning("Pivot: {Name}'s refused update, against its view: {Diagnosis}", topic.Name,
                    Diagnose(topic, drained));
            (topic.Cursor, topic.Urgent) = (ViewCursor.Start, true);
            return;
        }

        if (posted is { Status: PublishStatus.Full }) // refused (507: its staging full) — a delta, never a snapshot —
        {
            // so the table hasn't these changes, though the view's reader has passed them: read from the start, the next
            // post the whole answer (always staged), and a page awaiting this pivot's answer waits for that one.
            _log.LogWarning("Pivot: {Name}'s update was refused (507: its ingest's staging full); its view's reader reset",
                topic.Name);
            (topic.Cursor, topic.Urgent) = (ViewCursor.Start, true);
            return;
        }

        (topic.Checksum, topic.CellCount) = (view.Checksum + clockHash, view.Count);
        topic.Drains.Record(drained, view.Count);
        if (_validate)
        {
            var checking = Stopwatch.GetTimestamp();
            var other = await ComputeAsync(engine, topic, topic.Query, topic.Columns, work.OtherKeys, work.OtherRows,
                work.OtherSeen, ct);
            topic.Check.Record(topic.QueryMs, Stopwatch.GetElapsedTime(checking).TotalMilliseconds);
            if (other is not null)
            {
                topic.Check.Fail($"the snapshot: {other}");
            }
            else
            {
                var expected = Table<PivotKey, PivotCell>.ChecksumOf(CollectionsMarshal.AsSpan(work.OtherKeys),
                    CollectionsMarshal.AsSpan(work.OtherRows));
                topic.Check.View(expected, view.Checksum, work.OtherKeys.Count, view.Count);
                if (posted is not null)
                    topic.Check.Pending = (drained.Version,
                        work.OtherKeys.Zip(work.OtherRows).ToDictionary(c => c.First, c => c.Second));
            }
        }

        Answered(topic, posted, drained.Version);
    }

    /// <summary>
    ///     What a refused update got wrong (its table faulted: a checksum, say). The router's table — as it was before the
    ///     update, which it refused — against the view's whole state now, drained afresh by a reader of its own (the pivot's
    ///     left where it is); and the update's changes against that difference. Every update before was checked against its
    ///     checksum, so the table held the view's previous state: a cell that differs and isn't in the update is one the
    ///     update missed, one in it that doesn't match is one it got wrong. And the view's checksum against its own cells':
    ///     if they differ, the cells may be right and only its checksum wrong. Only ever on a fault: it allocates freely.
    /// </summary>
    /// <summary>
    ///     The post a fault was the router's answer to — the one whose expected checksum the fault names, among the topic's
    ///     last — and the router's table now (rolled back: as it was before it): its running checksum, against its rows'
    ///     own, hashed as it holds them. A snapshot whose rows' own checksum is its expected one was posted whole and right.
    /// </summary>
    private static string FaultingPost(Topic topic, string fault)
    {
        var table = new List<KeyValuePair<PivotKey, PivotCell>>();
        topic.Cells!.CopyTo(table);
        var held = Table<PivotKey, PivotCell>.ChecksumOf([.. table.Select(c => c.Key)], [.. table.Select(c => c.Value)]);
        var now = $"the table's running checksum 0x{topic.Cells.Checksum:X8}, its {table.Count} rows' own 0x{held:X8}";
        var at = fault.IndexOf("expected 0x", StringComparison.Ordinal);
        if (at < 0 || !uint.TryParse(fault.AsSpan(at + 11, 8), NumberStyles.HexNumber, null, out var expected))
            return $"not a checksum fault; {now}";
        var posts = topic.Posts.Where(p => p.At != default).OrderBy(p => p.At).ToList();
        var post = posts.FindLast(p => p.Expected == expected);
        var recent = string.Join(", ", posts.Select(p => $"{(p.Snapshot ? "snapshot" : "delta")} v{p.Version} 0x{p.Expected:X8}"));
        if (post.At == default) return $"none of its last posts expected 0x{expected:X8} ({recent}); {now}";
        var own = post.Snapshot
            ? $"; its rows' own checksum 0x{post.Own:X8}{(post.Own == post.Expected ? ", as expected: posted whole and right" : ", NOT its expected")}"
            : "";
        return $"a {(post.Snapshot ? "snapshot" : "delta")} at version {post.Version}, {post.Count} keys (the clock's among them), posted {post.At:HH:mm:ss.fff}{own}; {now}; its last posts: {recent}";
    }

    private static string Diagnose(Topic topic, Drained update)
    {
        var table = new List<KeyValuePair<PivotKey, PivotCell>>();
        topic.Cells!.CopyTo(table);
        var before = table.Where(c => !c.Key.Equals(ClockKey)).ToDictionary(c => c.Key, c => c.Value);
        var whole = new Drained();
        var cursor = ViewCursor.Start;
        while (topic.View!.Drain(ref cursor, whole, 1 << 20) == DrainOutcome.Partial)
        {
        }

        var now = new Dictionary<PivotKey, PivotCell>();
        for (var i = 0; i < whole.Keys.Count; i++) now[whole.Keys[i]] = whole.Rows[i];
        var cellsChecksum = Table<PivotKey, PivotCell>.ChecksumOf(CollectionsMarshal.AsSpan(whole.Keys),
            CollectionsMarshal.AsSpan(whole.Rows));
        // The update's changes by key, in order (its clock cell aside) — a key changed twice in it noted: the last one stands.
        var changes = new Dictionary<PivotKey, CellChange>();
        var twice = 0;
        for (var i = 0; i < update.Keys.Count; i++)
            if (!update.Keys[i].Equals(ClockKey) && !changes.TryAdd(update.Keys[i], update.Changes[i]))
                (twice, changes[update.Keys[i]]) = (twice + 1, update.Changes[i]);

        static string Cell(bool has, PivotCell cell)
        {
            return has ? cell.ToString() : "none";
        }

        var findings = new List<string>();
        int changed = 0, wrong = 0, missed = 0, idle = 0;
        foreach (var key in before.Keys.Union(now.Keys))
        {
            bool had = before.TryGetValue(key, out var was), has = now.TryGetValue(key, out var held);
            var differs = had != has || (had && !was.Equals(held));
            if (differs) changed++;
            if (changes.TryGetValue(key, out var change))
            {
                var right = change.Deleted == 1 ? !has : has && change.Row.Equals(held);
                if (!right)
                {
                    wrong++;
                    if (findings.Count < 8)
                        findings.Add(
                            $"{key}: the update {(change.Deleted == 1 ? "deletes it" : $"sets {change.Row}")}, the view holds {Cell(has, held)}, the table had {Cell(had, was)}");
                }
                else if (!differs)
                {
                    idle++; // (a change to what the table had already: harmless)
                }
            }
            else if (differs)
            {
                missed++;
                if (findings.Count < 8)
                    findings.Add(
                        $"{key}: not in the update — the table had {Cell(had, was)}, the view holds {Cell(has, held)}");
            }
        }

        return
            $"version {update.Version} (the view's now {whole.Version}): the table held {before.Count} cells, the view {now.Count}; the view's checksum 0x{whole.Checksum:X8}, " +
            $"its cells' 0x{cellsChecksum:X8}, the update's 0x{update.Checksum:X8}; the update {update.Upserts} upserts, {update.Deletes} deletes" +
            (twice > 0 ? $", {twice} keys twice" : "") +
            $"; {changed} cells the view changed, {wrong} the update got wrong, {missed} it missed, {idle} it changed to what they were" +
            (findings.Count > 0 ? ":\n  " + string.Join("\n  ", findings) : "");
    }

    /// <summary>
    ///     A tracked view's update (Pivot:TrackPlans): one that ran a new plan logged — matched to the refresh that took the
    ///     plan
    ///     in, by the version it published (the view's first update at it or after runs the plan; blue/green, the first after
    ///     the build) — with what it cost, and the view's state before it (blue/green: both states held while the new plan's
    ///     built), after it and at most — and its state kept.
    /// </summary>
    private void Track(Topic topic, PlanTracker plans)
    {
        var state = topic.StateBytes;
        topic.StateMost = Math.Max(topic.StateMost, state);
        if (plans.Swapped)
        {
            var u = plans.LastSwap;
            var at = _planning.TakenAt(u.PlanDigest);
            if (at <= u.Version) plans.Matched++; // (taken in by then: the move is its take-in's)
            var taken = at is not { } v ? "no take-in of it seen"
                : v == u.Version ? $"taken in at version {v}: this update's"
                : v < u.Version ? $"taken in at version {v}, {u.Version - v} before"
                : $"taken in at version {v}, after this update's";
            _log.LogInformation(
                "Pivot: {Name} ran a new plan from version {Version} ({Taken}): {From:x16} → {To:x16}; the update {Ms:F1} ms, {KB:N0} KB allocated (the process's), {Changes} changes, {How}; its state {Before} before, {After} after, {Most} at most",
                topic.Name, u.Version, taken, plans.From, u.PlanDigest, u.Elapsed.TotalMilliseconds,
                u.AllocatedBytes >> 10, u.Changes,
                u.Whole ? "computed from nothing" : "its state kept", MB(topic.StateBefore), MB(state),
                MB(topic.StateMost));
        }

        topic.StateBefore = state;
    }

    /// <summary>
    ///     A pivot's clock cell, posted with each of its answers — in the same post as the cells it describes, so a page
    ///     reads it with them: the answer's watermark, the earliest change it answers as the provider received it (a burst of
    ///     trades, a fill, a tick of the marks and rates; 0: nothing new — a page joining — or a pivot's first answer, its
    ///     view
    ///     made, which no sweep time counts), and when it was emitted (each in
    ///     microseconds since 1970, UTC, the provider's clock; not scaled, as the figures are). Emission less watermark is
    ///     the pivot's lag, which the page reports: the backoff, ChalkQL's refresh (a compaction too) and the run. Its stamp:
    ///     the engine's version it's at, and its view's generation. And its checksum, which the post's expected one adds (a
    ///     table's is the sum of its cells').
    /// </summary>
    private static (PivotCell Cell, uint Hash) Clock(Topic topic)
    {
        static long Micros(DateTime utc)
        {
            return (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        }

        var cell = new PivotCell(topic.Watermark is { } at ? Micros(at) : 0, Micros(DateTime.UtcNow), topic.Generation,
            topic.Stamped, 0, 0, 0, 0, 0);
        return (cell, Table<PivotKey, PivotCell>.ChecksumOf([ClockKey], [cell]));
    }

    /// <summary>
    ///     A pivot's view, made from its statement's prepared plan (a run of it gives the view's cells, bit for bit),
    ///     maintained
    ///     as its pages asked — or, its statement refused incremental maintenance (UnsupportedInView,
    ///     naming the operator and its place in the plan: logged), recomputed.
    /// </summary>
    private async Task<StandingView<PivotKey, TRow>> Materialize<TRow>(ChalkEngine engine, Topic topic,
        ViewMapper<PivotKey, TRow> map, CancellationToken ct) where TRow : unmanaged
    {
        // A margin is a statement's, from one view: the first incremental view made over it carries it, and the others over
        // the same statement (its text, other values) run the plans it takes — every plan, once that view's let go.
        bool carries;
        lock (_gate)
        {
            carries = _planMargin is not null &&
                      !_topics.Values.Any(t => t != topic && t.Margined && t.Query == topic.Query);
        }

        ViewOptions<PivotKey, TRow> Options(ViewMaintenance maintenance)
        {
            return new ViewOptions<PivotKey, TRow>
            {
                Key =
                [
                    topic.Query.OutputSchema.FieldsList[topic.Columns.Row].Name,
                    topic.Query.OutputSchema.FieldsList[topic.Columns.Column].Name
                ],
                Map = map,
                RowHash = RowHash.Of, // (the view's checksum: Tapline's, of the cells it holds)
                Exactness = ViewExactness.EqualUpToRounding, // (its figures are summed as DOUBLE)
                Parameters = topic.Values.Count > 0 ? topic.Values : null,
                LocalJoinMaxRows =
                    StandingViews
                        .DefaultLocalJoinMaxRows, // (a cross-source join's cap: none here, every table in process)
                Maintenance = maintenance,
                Tracker = _trackPlans ? topic.Plans ??= new PlanTracker() : null,
                PlanSwap = maintenance == ViewMaintenance.Incremental
                    ? topic.PlanSwap
                    : ViewPlanSwap.AtNextUpdate, // (blue/green: incremental views only — its pages' Low jitter)
                PlanImprovementMargin =
                    maintenance == ViewMaintenance.Incremental && carries
                        ? _planMargin
                        : null // (a recomputing view moves at no cost)
            };
        }

        var maintenance = topic.Maintenance;
        StandingView<PivotKey, TRow> view;
        try
        {
            view = await engine.MaterializeAsync(topic.Query, Options(maintenance), ct);
        }
        catch (StandingViewException e) when (e.Code == ChalkErrorCodes.UnsupportedInView &&
                                              maintenance == ViewMaintenance.Incremental)
        {
            _log.LogWarning("Pivot: {Name} can't be maintained incrementally, so it's recomputed: {Refusal}",
                topic.Name, e.Message);
            (topic.Maintenance, topic.Refusal) = (ViewMaintenance.Recompute, e.Message);
            view = await engine.MaterializeAsync(topic.Query, Options(ViewMaintenance.Recompute), ct);
        }

        topic.Margined = carries && view.Maintenance == ViewMaintenance.Incremental;
        return view;
    }

    /// <summary>
    ///     A view's row into a cell: its labels (their UTF-8, copied), and its figures to the cell's precision (none where
    ///     null) —
    ///     each by its column's typed getter: nothing decoded, boxed or allocated.
    /// </summary>
    private static void Map(in ViewRow row, Columns at, out PivotKey key, out PivotCell cell)
    {
        ReadOnlySpan<byte> rowLabel = row.IsNull(at.Row) ? "(none)"u8 : row.GetUtf8(at.Row),
            columnLabel = row.IsNull(at.Column) ? "(none)"u8 : row.GetUtf8(at.Column);
        key = PivotKey.Of(rowLabel, columnLabel) ?? throw new InvalidOperationException(TooLong(rowLabel, columnLabel));
        var kinds = at.Kinds;
        cell = new PivotCell(Figure(row, at.Quantity, kinds, "quantity"), Figure(row, at.Notional, kinds, "notional"),
            Figure(row, at.Realized, kinds, "realized PnL"),
            Figure(row, at.Unrealized, kinds, "unrealized PnL"), Figure(row, at.Balance, kinds, "balance"),
            Figure(row, at.Volume, kinds, "volume", VolumeScale),
            Figure(row, at.Trades, kinds, "trades"),
            at.MaxGross < 0 ? PivotCell.None : Figure(row, at.MaxGross, kinds, "max gross"),
            at.MarkPrice < 0 ? PivotCell.None : Figure(row, at.MarkPrice, kinds, "mark price"));
    }

    /// <summary>
    ///     A view's figure into a cell, as <see cref="Figure(IArrowArray?, int, out long, decimal)" /> reads a batch's — by
    ///     its
    ///     column's getter (<paramref name="kinds" />): an integer scaled exactly, a REAL, DOUBLE or DECIMAL rounded half away
    ///     from zero.
    /// </summary>
    private static long Figure(in ViewRow row, int at, FigureKind[] kinds, string name, decimal scale = Scale)
    {
        if (row.IsNull(at)) return PivotCell.None;
        try
        {
            var figure = kinds[at] switch
            {
                FigureKind.Integer => checked(row.GetInt64(at) * (long)scale),
                FigureKind.Real => (long)Math.Round((decimal)(float)row.GetDouble(at) * scale,
                    MidpointRounding.AwayFromZero),
                FigureKind.Double => (long)Math.Round((decimal)row.GetDouble(at) * scale,
                    MidpointRounding.AwayFromZero),
                _ => (long)Math.Round(row.GetDecimal(at) * scale, MidpointRounding.AwayFromZero)
            };
            if (figure != PivotCell.None) return figure; // (the one value that means none: too large too)
        }
        catch (OverflowException)
        {
        }

        throw new InvalidOperationException(TooLarge(name, scale));
    }

    /// <summary>What's wrong with labels too long for a cell: the two, decoded for the message.</summary>
    private static string TooLong(ReadOnlySpan<byte> row, ReadOnlySpan<byte> column)
    {
        return
            $"A cell's labels are at most {PivotKey.RowBytes} and {PivotKey.ColumnBytes} bytes (\"{Encoding.UTF8.GetString(row)}\", \"{Encoding.UTF8.GetString(column)}\" aren't).";
    }

    /// <summary>What's wrong with a figure too large for a cell: which, and what a cell holds of it.</summary>
    private static string TooLarge(string name, decimal scale)
    {
        return
            $"A cell's {name} is too large for it: at most ±{long.MaxValue / scale:N0}, to {(scale == VolumeScale ? Pivot.VolumePlaces : Pivot.Places)} places.";
    }

    /// <summary>
    ///     A batch of the answer into cells — or what's wrong with it: a label too long for a cell, or two rows for one
    ///     cell.
    /// </summary>
    private static string? Read(RecordBatch batch, Columns at, List<PivotKey> keys, List<PivotCell> rows,
        HashSet<PivotKey> seen)
    {
        for (var i = 0; i < batch.Length; i++)
        {
            ReadOnlySpan<byte> row = Label(batch.Column(at.Row), i), column = Label(batch.Column(at.Column), i);
            if (PivotKey.Of(row, column) is not { } key) return TooLong(row, column);
            if (!seen.Add(key))
                return
                    $"Two rows for the cell (\"{Encoding.UTF8.GetString(row)}\", \"{Encoding.UTF8.GetString(column)}\"): a pivot's statement groups by what its labels name.";
            if (keys.Count == MaxCells)
                return
                    $"A pivot is at most {MaxCells:N0} cells, totals included — its snapshot fits the {Staging / 1024} KB staged for it (this one has more): group by fewer dimensions, or coarser ones.";
            if (!Figure(batch.Column(at.Quantity), i, out var quantity)) return TooLarge("quantity", Scale);
            if (!Figure(batch.Column(at.Notional), i, out var notional)) return TooLarge("notional", Scale);
            if (!Figure(batch.Column(at.Realized), i, out var realized)) return TooLarge("realized PnL", Scale);
            if (!Figure(batch.Column(at.Unrealized), i, out var unrealized)) return TooLarge("unrealized PnL", Scale);
            if (!Figure(batch.Column(at.Balance), i, out var balance)) return TooLarge("balance", Scale);
            if (!Figure(batch.Column(at.Volume), i, out var volume, VolumeScale))
                return TooLarge("volume", VolumeScale);
            if (!Figure(batch.Column(at.Trades), i, out var trades)) return TooLarge("trades", Scale);
            if (!Figure(at.MaxGross < 0 ? null : batch.Column(at.MaxGross), i, out var maxGross))
                return TooLarge("max gross", Scale);
            if (!Figure(at.MarkPrice < 0 ? null : batch.Column(at.MarkPrice), i, out var markPrice))
                return TooLarge("mark price", Scale);
            keys.Add(key);
            rows.Add(new PivotCell(quantity, notional, realized, unrealized, balance, volume, trades, maxGross,
                markPrice));
        }

        return null;
    }

    /// <summary>A label's UTF-8, over the batch's own buffer (whichever string layout) — "(none)" for a NULL.</summary>
    private static ReadOnlySpan<byte> Label(IArrowArray array, int row)
    {
        return RecordBatchExtensions.TryGetUtf8(array, row, out var utf8) ? utf8 : "(none)"u8;
    }

    /// <summary>
    ///     A figure into a cell: the statement's number to the cell's precision (its fixed-point integer, rounded half away
    ///     from zero) — or, null (it doesn't add up across what the cell spans), <see cref="PivotCell.None" />. False if it's
    ///     too large for a cell.
    /// </summary>
    private static bool
        Figure(IArrowArray? array, int row, out long figure,
            decimal scale = Scale) // (no column: a figure the statement doesn't answer — none)
    {
        figure = PivotCell.None;
        if (array is null || array.IsNull(row)) return true;
        try
        {
            var value = array switch
            {
                Decimal128Array a => a.GetValue(row),
                Int64Array a => a.GetValue(row),
                Int32Array a => a.GetValue(row),
                Int16Array a => a.GetValue(row),
                DoubleArray a => a.GetValue(row) is { } d ? (decimal)d : null,
                FloatArray a => a.GetValue(row) is { } f ? (decimal)f : null,
                _ => null
            };
            if (value is not { } v) return true; // (a type the contract refused: none)
            figure = (long)Math.Round(v * scale, MidpointRounding.AwayFromZero);
            return figure != PivotCell.None; // (the one value that means none: too large too)
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void PublishStats(object stats)
    {
        Stats.Publish(JsonSerializer.SerializeToUtf8Bytes(stats));
    }

    /// <summary>Whether a tick's rows are each key's once, and all later than the last row kept (Debug.Assert's).</summary>
    private static bool Unique<T, TKey>(List<T> tick, List<T> kept, Func<T, DateTime> at, Func<T, TKey> key)
    {
        return (kept.Count == 0 || tick.All(row => at(row) > at(kept[^1]))) &&
               tick.Select(key).Distinct().Count() == tick.Count;
    }

    /// <summary>
    ///     Which rows of an append-only log (in time order) a lookup as of a point needs, read back to front: each key's
    ///     last row in each bucket — a half minute from <see cref="FineFrom" /> on (the drawdown's points), an hour before
    ///     (a day's open and close) — and its last exactly on a bucket's start (a close looks up at or before it, an open
    ///     before it). Read back to front, a key's rows come latest first, so its last in a bucket is the first met there:
    ///     one entry a key, the scratch kept from one compaction to the next.
    /// </summary>
    private sealed class Buckets<TKey> where TKey : notnull
    {
        private readonly Dictionary<TKey, (long Bucket, long Exact, long At)> _last = new();

        /// <summary>From when (ticks, on the hour) the buckets are half minutes.</summary>
        public long FineFrom;

        public void Clear()
        {
            _last.Clear();
        }

        /// <summary>Whether a key's row at <paramref name="ticks" /> is kept — its rows met latest first.</summary>
        public bool Keeps(TKey key, long ticks)
        {
            var width = ticks >= FineFrom ? Fine.Ticks : Sample.Ticks;
            var bucket = ticks - ticks % width;
            ref var last = ref CollectionsMarshal.GetValueRefOrAddDefault(_last, key, out var met);
            Debug.Assert(!met || ticks <= last.At, "a key's rows in time order");
            if (!met) last = (long.MinValue, long.MinValue, ticks);
            last.At = ticks;
            if (last.Bucket != bucket) // its key's last in that bucket
            {
                last.Bucket = bucket;
                return true;
            }

            if (ticks % width != 0 || last.Exact == ticks) return false;
            last.Exact = ticks; // its key's last on the bucket's start
            return true;
        }
    }

    /// <summary>An as-of log kept as a lookup as of a point needs it (<see cref="Buckets{TKey}" />).</summary>
    private sealed class AsOfLog<T, TKey>(Func<T, DateTime> at, Func<T, TKey> key) where TKey : notnull
    {
        public readonly Buckets<TKey> Buckets = new();
        private bool[] _keep = [];

        public void Keep(List<T> rows)
        {
            var kept = Keep(CollectionsMarshal.AsSpan(rows));
            rows.RemoveRange(kept, rows.Count - kept);
        }

        /// <summary>The rows kept moved to the front, in order: how many.</summary>
        public int Keep(Span<T> rows)
        {
            Buckets.Clear();
            if (_keep.Length < rows.Length) _keep = new bool[rows.Length + rows.Length / 4];
            for (var i = rows.Length - 1; i >= 0; i--) _keep[i] = Buckets.Keeps(key(rows[i]), at(rows[i]).Ticks);
            var kept = 0;
            for (var i = 0; i < rows.Length; i++)
                if (_keep[i])
                    rows[kept++] = rows[i];
            return kept;
        }
    }

    /// <summary>
    ///     The ledger kept: its latest entries whole (from a time: a posting's entries share it) — and of the older, the
    ///     house's cash accounts' as an as-of log is kept (<see cref="Buckets{TKey}" />: the drawdown's equity looks their
    ///     balances up), and its working capital's (the books' opening: the drawdown's capital). The postings with them:
    ///     the latest, and those the older entries kept are of.
    /// </summary>
    private sealed class Ledger
    {
        public readonly Buckets<long> Buckets = new();
        private readonly HashSet<Guid> _postings = [];
        private bool[] _keep = [];

        /// <summary>The house's cash accounts, and its working capital's postings — the brokerage's, as it's primed.</summary>
        public (IReadOnlySet<long> Cash, IReadOnlySet<Guid> Capital) Books = (new HashSet<long>(), new HashSet<Guid>());

        /// <summary>The journal's entries kept, moved to the front, in order: how many (their postings noted).</summary>
        public int KeepJournal(Span<Journal> rows, DateTime from)
        {
            var (cash, capital) = Books;
            Buckets.Clear();
            _postings.Clear();
            if (_keep.Length < rows.Length) _keep = new bool[rows.Length + rows.Length / 4];
            for (var i = rows.Length - 1; i >= 0; i--)
            {
                var entry = rows[i];
                if (entry.UpdateTime >= from) _keep[i] = true;
                else if ((_keep[i] = capital.Contains(entry.PostingId) ||
                                     (cash.Contains(entry.AccountId) &&
                                      Buckets.Keeps(entry.AccountId, entry.UpdateTime.Ticks))))
                    _postings.Add(entry.PostingId);
            }

            var kept = 0;
            for (var i = 0; i < rows.Length; i++)
                if (_keep[i])
                    rows[kept++] = rows[i];
            return kept;
        }

        /// <summary>The postings kept, after the journal's (<see cref="KeepJournal" />): the latest, and the older entries'.</summary>
        public int KeepPostings(Span<Posting> rows, DateTime from)
        {
            var kept = 0;
            for (var i = 0; i < rows.Length; i++)
                if (rows[i].UpdateTime >= from || _postings.Contains(rows[i].PostingId))
                    rows[kept++] = rows[i];
            return kept;
        }
    }

    /// <summary>A rewrite in place: the rows it keeps moved to the front, in order — how many.</summary>
    private delegate int KeepRows<T>(Span<T> rows);

    /// <summary>The rows <paramref name="keep" /> keeps moved to the front, in order: how many (a RemoveAll, in place).</summary>
    private static int Where<T>(Span<T> rows, Func<T, bool> keep)
    {
        var kept = 0;
        for (var i = 0; i < rows.Length; i++)
            if (keep(rows[i]))
                rows[kept++] = rows[i];
        return kept;
    }

    /// <summary>
    ///     A table's row arrays: its pool — ChalkQL's arrays for the table come from it and go back to it, its appends'
    ///     and the rewrites handed over — and the rows of its last rewrite, kept: ours to splice in once it's committed.
    /// </summary>
    private sealed class Pooled<T>
    {
        public readonly ArrayPool<T> Pool = ArrayPool<T>.Create(1 << 22, 4); // (an array past 4M rows isn't kept)
        public readonly List<T> Rewritten = [];
    }

    /// <summary>The tables' row arrays, each its pool (<see cref="Pooled{T}" />).</summary>
    private sealed class Pools
    {
        public readonly Pooled<HedgeFill> Fills = new();
        public readonly Pooled<Journal> Journal = new();
        public readonly Pooled<Mark> Marks = new();
        public readonly Pooled<Posting> Postings = new();
        public readonly Pooled<ConversionRate> Rates = new();
        public readonly Pooled<PositionRisk> Risk = new();
    }

    /// <summary>
    ///     The as-of logs' thinning (<see cref="AsOfLog{T,TKey}" />) and the ledger's (<see cref="Ledger" />), each its
    ///     scratch: one set for each thread compacting.
    /// </summary>
    private sealed class Thinning
    {
        public readonly Ledger Ledger = new();
        public readonly AsOfLog<Mark, short> Marks = new(m => m.Ts, m => m.InstrumentId);
        public readonly AsOfLog<ConversionRate, string> Rates = new(r => r.Ts, r => r.Symbol);
        public readonly AsOfLog<PositionRisk, (long, string)> Risk = new(r => r.UpdateTime, r => (r.AccountId, r.Channel));

        /// <summary>Kept by the half minute from <see cref="FineKept" /> before <paramref name="now" />, on the hour.</summary>
        public void From(DateTime now)
        {
            var ticks = (now - FineKept).Ticks;
            ticks -= ticks % Sample.Ticks;
            Marks.Buckets.FineFrom = Rates.Buckets.FineFrom = Risk.Buckets.FineFrom = Ledger.Buckets.FineFrom = ticks;
        }
    }

    /// <summary>A pivot: its statement as prepared, the table the router holds it in, and how it's doing.</summary>
    private sealed class Topic
    {
        public readonly Checks Check = new();

        /// <summary>
        ///     Its last posts, a ring: a fault the router finds as it applies one — later, on its own thread — is matched to
        ///     it by the checksum it expected (the fault names it), and logged with it.
        /// </summary>
        public readonly PostRecord[] Posts = new PostRecord[8];

        public int PostAt;
        public readonly DrainCounts Drains = new();
        public readonly Latencies Latency = new(); // from a change to its run's post, its last runs'
        public readonly List<string> Texts = []; // its keys in _byText: at most MaxTexts
        public readonly Maintained Updates = new(); // its view's updates: their times, state and allocations
        public required ExecutionArena Arena; // its executions' memory (one at a time): at most ArenaBudget
        public required long ArenaBudget;
        public long ArenaPeak;
        public required bool BindsNow;
        public int CellCount;
        public Table<PivotKey, PivotCell>? Cells; // (its cells' table — or, its cells carrying extras, its shell's)
        public DateTime? ChangedAt; // the earliest change loaded since it last ran (as it was due)
        public uint Checksum;
        public required Columns Columns;
        public ViewCursor Cursor = ViewCursor.Start;

        public bool
            Dirty, Urgent; // what it reads changed since it ran; to run at once (new, or come back to) — the loop's

        public long Epoch, Posted;
        public string? Error;

        // Where its answers stand — the version its last was posted at, and its view's generation — and the waits on it.
        private readonly Lock _reaching = new();
        private List<(long Version, TaskCompletionSource<long> Done)>? _waits;
        public long PostedVersion, Generation;
        public long Stamped; // the version the answer in hand is posted at (its clock cell says it)

        /// <summary>Completes once an answer at or after <paramref name="version" /> is posted, with its version.</summary>
        public Task<long> Reached(long version)
        {
            lock (_reaching)
            {
                if (PostedVersion >= version) return Task.FromResult(PostedVersion);
                var done = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                (_waits ??= []).Add((version, done));
                return done.Task;
            }
        }

        /// <summary>An answer posted at <paramref name="version" />: the waits it reaches, completed.</summary>
        public void PostedAt(long version)
        {
            List<TaskCompletionSource<long>>? done = null;
            lock (_reaching)
            {
                if (version > PostedVersion) PostedVersion = version;
                if (_waits is null) return;
                for (var i = _waits.Count - 1; i >= 0; i--)
                    if (_waits[i].Version <= PostedVersion)
                    {
                        (done ??= []).Add(_waits[i].Done);
                        _waits.RemoveAt(i);
                    }
            }

            if (done is not null)
                foreach (var d in done) d.TrySetResult(version);
        }

        public bool
            FirstAnswer; // the run in hand made its view (or, by snapshot, is its first): its answer in no latency

        public DateTime? Floor; // after a first answer, when it was posted: the next answer's lag counts from there
        public double GapMs; // its backoff before running again: its last run's time (a first answer's: none)
        public DateTime IdleSince = DateTime.UtcNow;

        public TableIngest<PivotKey, PivotCell, CellChange>?
            Ingest; // (snapshots, or a view's changes: cells replaced or deleted)

        public ShownValue[] Extras = []; // what its cells carry besides its values (Show as): none, the plain cell
        public IShell? Shell; // its cells carrying extras: the stage between its view and its table
        public StandingView<PivotKey, WideRow>? WideView; // (its view, its cells carrying extras: the statement's rows wide)

        /// <summary>Its cells' table, either kind.</summary>
        public Source Source => (Source?)Cells ?? Shell!.Source;

        /// <summary><paramref name="then" /> once everything it posted so far is applied by the router.</summary>
        public void WhenApplied(Action<long> then)
        {
            if (Ingest is { } ingest) ingest.WhenApplied(then);
            else Shell!.WhenApplied(then);
        }

        public long Superseded => Ingest?.Superseded ?? Shell!.Superseded;

        /// <summary>What its view holds, either kind.</summary>
        public long StateBytes => View?.StateBytes ?? WideView?.StateBytes ?? 0;

        public bool TryRendezvous(Mailbox mailbox, ReadOnlySpan<byte> correlation)
        {
            return Ingest?.TryRendezvous(mailbox, correlation) ?? Shell!.TryRendezvous(mailbox, correlation);
        }

        public required string Key, Name, Sql;
        public bool Live;

        public required ViewMaintenance
            Maintenance; // how its view is maintained: as its pages asked (Recompute, too, once Incremental's refused)

        public bool Margined; // its view carries its statement's plan margin (Pivot:PlanMargin): one view's a statement
        public ulong? Plan; // its plan's digest as it last ran (another since: swapped in)
        public PlanTracker? Plans; // its view's updates as its tracker sees them (Pivot:TrackPlans)

        public required ViewPlanSwap
            PlanSwap; // how its incremental view takes a new plan: blue/green (low jitter), or at the next update

        public required PreparedQuery Query;
        public double QueryMs;
        public DateTime RanAt;
        public string? Refusal; // why ChalkQL refused to maintain it incrementally, if it did (its pages are told)

        public bool
            Replaced; // a table it reads replaced since its last update (a compaction): that update reads it whole

        public bool
            Resync; // a new epoch bound (its routes all gone since it last posted): its next answer whole, its table resynchronised

        public long StateBefore, StateMost; // (tracked: its view's state after its last update, and the most it's held)

        public void Remember(in PostRecord post)
        {
            Posts[PostAt++ % Posts.Length] = post;
        }

        public required Dictionary<string, object?>
            Values; // what it binds, by name: the page's lists (and @now, set each run)

        public StandingView<PivotKey, PivotCell>?
            View; // its standing view, made at its first run (a statement binding @now: none — a snapshot each run)

        public List<(Mailbox Mailbox, byte[] Correlation)>
            Waiting = []; // rendezvous asked for before its first answer (under _gate)

        public DateTime?
            Watermark; // the run in hand's: the earliest change it answers (none: a page joining, or its first answer) — its clock cell's

        /// <summary>What it holds against the provider's memory budget: its arena's limit, its staging, its cells in the router.</summary>
        public long Reserved => ArenaBudget + 2L * (Shell?.Staging ?? Staging) + (long)CellCount * (Shell is { } s ? s.RowBytes + 32 : CellBytes) +
                                MaxTexts * 2L * MaxSql + (View?.StateBytes ?? 0) + (WideView?.StateBytes ?? 0);
    }

    /// <summary>
    ///     How long resolving a page's statement took — its prepared query built (planned) or retrieved (a pivot running has
    ///     it) — by the page's rendezvous id, taken once (<see cref="TakeResolution" />).
    /// </summary>
    public sealed record Resolution(TimeSpan Took, string How);

    /// <summary>
    ///     Where a pivot's statement answers what a pivot is (max gross, mark price: -1 where it doesn't), and how each
    ///     column's
    ///     figures read (<see cref="Kinds" />, by ordinal): a view's rows by their typed getters, nothing boxed.
    /// </summary>
    private sealed record Columns(
        int Row,
        int Column,
        int Quantity,
        int Notional,
        int Realized,
        int Unrealized,
        int Balance,
        int Volume,
        int Trades,
        int MaxGross,
        int MarkPrice,
        FigureKind[] Kinds,
        int[] Wide); // (an extended pivot's: each WideColumns' ordinal, -1 where its statement doesn't answer it)

    /// <summary>A figure's column: an integer of any width, a REAL, a DOUBLE or a DECIMAL.</summary>
    private enum FigureKind : byte
    {
        Integer,
        Real,
        Double,
        Decimal
    }

    /// <summary>
    ///     A view's updates as its tracker sees them (Pivot:TrackPlans): the plan each ran — one
    ///     differing from the last update's, a new plan taken (blue/green: swapped in, its state kept; at the next update:
    ///     computed from nothing) — and how many.
    /// </summary>
    private sealed class PlanTracker : IViewCostTracker
    {
        public ulong Digest, From; // the plan the last update ran, and the one before a new one
        public ViewUpdate LastSwap;
        public bool Swapped; // the last update ran a new plan
        public int Swaps, Whole, Matched; // (matched: to the refresh that took its plan in, by version)
        public long Updates;

        public void Updated(in ViewUpdate update)
        {
            Updates++;
            Swapped = Digest != 0 && update.PlanDigest != Digest;
            if (Swapped) (Swaps, Whole, LastSwap, From) = (Swaps + 1, Whole + (update.Whole ? 1 : 0), update, Digest);
            Digest = update.PlanDigest;
        }

        public override string ToString()
        {
            return
                $"new plans {Swaps} ({Whole} computed from nothing, {Matched} matched to their take-ins) in {Updates:N0} updates";
        }
    }

    /// <summary>
    ///     ChalkQL's planning as it happens (its planning observer, told on ChalkQL's threads): each statement a background
    ///     run
    ///     found drifted, counted by its logical digest (a pivot's name carries it) and logged unless it came back to the plan
    ///     it
    ///     runs; each refresh that took a run's plans in, logged when it swapped any; each prepare, and why. Its counts go
    ///     into
    ///     the 10 s lines, so ChalkQL's own log can stay quiet (Pivot:ChalkLog).
    /// </summary>
    private sealed class PlanningLog(ILogger log) : IPlanningObserver
    {
        private readonly Verdicts _all = new();

        private readonly ConcurrentDictionary<ulong, Verdicts> _byStatement = new();

        // A plan planned anew, by its digest: judged by a run (its runs follow one another, on one thread), staged once that
        // run's over, then taken in by a refresh — at the version it published, which a view's move to it is matched by.
        private readonly List<ulong> _judging = [];

        private readonly int[]
            _prepared = new int[3]; // by PrepareReason: the provider's, a view's for a change of shape, for a drift

        private readonly ConcurrentDictionary<ulong, byte> _staged = new();
        private readonly ConcurrentDictionary<ulong, long> _takenAt = new();

        private long _runs, _runTicks, _mostTicks, _takenIn, _swappedIn, _unchangedIn;

        public void Judged(in StatementJudged judged)
        {
            _byStatement.GetOrAdd(judged.LogicalDigest, static _ => new Verdicts()).Count(judged.Verdict);
            _all.Count(judged.Verdict);
            if (judged.Verdict == PlanVerdict.PlannedAnew)
                lock (_judging)
                {
                    _judging.Add(judged.NewPlan);
                }

            if (judged.Verdict == PlanVerdict.Unchanged) return;
            var costs = judged.NewCost is { } next
                ? judged.RunningCost is { } running
                    ? $", priced {next:N0} against {running:N0}"
                    : $", priced {next:N0} (the plan it runs unpriced)"
                : "";
            var came = judged.Verdict switch
            {
                PlanVerdict.PlannedAnew =>
                    $"planned anew, plan {judged.RunningPlan:x16} → {judged.NewPlan:x16} at the next take-in (a refresh, or PlanAsync)",
                PlanVerdict.HeldByMargin => $"held back by its view's margin, plan {judged.RunningPlan:x16} kept",
                PlanVerdict.Vetoed => $"vetoed by its view's host, plan {judged.RunningPlan:x16} kept",
                PlanVerdict.NotTheSame => $"planned to another statement, plan {judged.RunningPlan:x16} kept",
                _ => $"not planned (the planner failed), plan {judged.RunningPlan:x16} kept"
            };
            log.LogInformation("Pivot: ChalkQL planned statement {Logical:x16} again, {Drift}: {Came}{Costs}",
                judged.LogicalDigest,
                judged.Stale ? "stale (its tables moved tenfold)" : "its tables having moved twofold", came, costs);
        }

        public void Ran(in PlanningRun run)
        {
            lock (_judging) // (its plans staged: the next refresh, or PlanAsync, takes them in)
            {
                foreach (var plan in _judging) _staged[plan] = 0;
                _judging.Clear();
            }

            Interlocked.Increment(ref _runs);
            Interlocked.Add(ref _runTicks, run.Elapsed.Ticks);
            for (var most = Volatile.Read(ref _mostTicks);
                 run.Elapsed.Ticks > most;
                 most = Volatile.Read(ref _mostTicks))
                if (Interlocked.CompareExchange(ref _mostTicks, run.Elapsed.Ticks, most) == most)
                    break;
        }

        public void TookIn(in PlansTakenIn takenIn)
        {
            Interlocked.Increment(ref _takenIn);
            Interlocked.Add(ref _swappedIn, takenIn.PlansSwappedIn);
            Interlocked.Add(ref _unchangedIn, takenIn.PlansUnchanged);
            int plans = takenIn.PlansSwappedIn + takenIn.PlansUnchanged, staged = 0;
            if (plans == 0) return;
            foreach (var plan in
                     _staged.Keys) // (the plans staged so far: this refresh's — a view's move to one is matched by its version)
                if (_staged.TryRemove(plan, out _))
                    (_takenAt[plan], staged) = (takenIn.Version, staged + 1);
            log.LogInformation(
                "Pivot: ChalkQL took plans in at version {Version} (epoch {Epoch}): {Swapped} swapped in, {Unchanged} changing nothing{Staged}",
                takenIn.Version, takenIn.Epoch, takenIn.PlansSwappedIn, takenIn.PlansUnchanged,
                staged == plans ? "" : $" — {staged} planned anew were staged for it");
        }

        public void Prepared(in StatementPrepared prepared)
        {
            Interlocked.Increment(ref _prepared[Math.Min((int)prepared.Reason, 2)]);
            log.LogInformation("Pivot: ChalkQL prepared statement {Logical:x16} {Why} in {Ms:F0} ms: plan {Plan:x16}",
                prepared.LogicalDigest,
                prepared.Reason switch
                {
                    PrepareReason.Host => "for the provider",
                    PrepareReason.ChangedShape => "again, by its view (a table it reads changed shape)",
                    _ => "again, by its view (a tenfold drift it can't plan again in place)"
                }, prepared.Elapsed.TotalMilliseconds, prepared.PlanDigest);
        }

        /// <summary>A statement's verdicts, if a background run has found it drifted.</summary>
        public string? Of(ulong logical)
        {
            return _byStatement.TryGetValue(logical, out var verdicts) ? verdicts.ToString() : null;
        }

        /// <summary>The version the refresh that took a plan in published, its latest — none if no take-in was seen for it.</summary>
        public long? TakenAt(ulong plan)
        {
            return _takenAt.TryGetValue(plan, out var version) ? version : null;
        }

        public override string ToString()
        {
            var runs = Volatile.Read(ref _runs);
            return $"{runs:N0} background runs" + (runs > 0
                       ? $" ({TimeSpan.FromTicks(Volatile.Read(ref _runTicks) / runs).TotalMilliseconds:F1} ms on average, at most {TimeSpan.FromTicks(Volatile.Read(ref _mostTicks)).TotalMilliseconds:F1})"
                       : "") +
                   $"; statements {_all}; plans taken in at {Volatile.Read(ref _takenIn):N0} refreshes: {Volatile.Read(ref _swappedIn)} swapped in, {Volatile.Read(ref _unchangedIn)} changing nothing" +
                   $"; prepared {Volatile.Read(ref _prepared[0])} for the provider, {Volatile.Read(ref _prepared[1])} by views for a change of shape, {Volatile.Read(ref _prepared[2])} for a drift";
        }

        /// <summary>What background runs came to for drifted statements: one's, or all of them.</summary>
        private sealed class Verdicts
        {
            public int Drifted, Anew, Unchanged, Held, Vetoed, Other;

            public void Count(PlanVerdict verdict)
            {
                Interlocked.Increment(ref Drifted);
                switch (verdict)
                {
                    case PlanVerdict.PlannedAnew: Interlocked.Increment(ref Anew); break;
                    case PlanVerdict.Unchanged: Interlocked.Increment(ref Unchanged); break;
                    case PlanVerdict.HeldByMargin: Interlocked.Increment(ref Held); break;
                    case PlanVerdict.Vetoed: Interlocked.Increment(ref Vetoed); break;
                    default: Interlocked.Increment(ref Other); break;
                }
            }

            public override string ToString()
            {
                return
                    $"drifted {Volatile.Read(ref Drifted)}: {Volatile.Read(ref Anew)} planned anew, {Volatile.Read(ref Unchanged)} unchanged" +
                    (Volatile.Read(ref Held) is > 0 and var held ? $", {held} held back by a margin" : "") +
                    (Volatile.Read(ref Vetoed) is > 0 and var vetoed ? $", {vetoed} vetoed" : "") +
                    (Volatile.Read(ref Other) is > 0 and var other
                        ? $", {other} not planned (another statement, or failed)"
                        : "");
            }
        }
    }

    /// <summary>
    ///     A pivot's latency, its last runs': from the earliest change each run answered (a tick, a burst, a fill, as it was
    ///     due) to its post — the backoff, ChalkQL's refresh and the run; the router's delivery to each page on top.
    /// </summary>
    private sealed class Latencies
    {
        private readonly double[] _ms = new double[2048];
        private int _count, _next;

        public void Add(double ms)
        {
            (_ms[_next], _next, _count) = (ms, (_next + 1) % _ms.Length, Math.Min(_count + 1, _ms.Length));
        }

        public override string ToString()
        {
            if (_count == 0) return "latency: no runs answering a change yet";
            var sorted = _ms.AsSpan(0, _count).ToArray();
            Array.Sort(sorted);

            double At(double q)
            {
                return sorted[Math.Min(_count - 1, (int)Math.Ceiling(q * _count) - 1)];
            }

            return
                $"latency (a change to its post, last {_count:N0} runs): p50 {At(0.5):F1} ms, p99.5 {At(0.995):F1} ms, max {sorted[^1]:F1} ms";
        }
    }

    /// <summary>
    ///     A pivot's view's updates, how it's maintained: the first's time (the view made and updated: it builds what an
    ///     incremental view keeps), the
    ///     warm ones' — those after a compaction (a table replaced, read whole) apart — the last 2,048 of each, as p50, p90,
    ///     p99 and max; what the view holds (its rows, and what maintenance keeps); and what the process allocated during
    ///     each.
    /// </summary>
    private sealed class Maintained
    {
        private readonly Ring _warm = new(), _replaced = new(), _warmChanged = new(), _replacedChanged = new();
        private double _first = double.NaN;
        private long _state, _allocated, _updates;

        private int _warmResets, _replacedResets;
        public ViewMaintenance? Mode;

        public void Record(bool first, bool replaced, double ms, long allocated, long state)
        {
            if (first) _first = ms;
            else (replaced ? _replaced : _warm).Add(ms);
            (_state, _allocated, _updates) = (state, _allocated + allocated, _updates + 1);
        }

        /// <summary>The cells an update changed (a reset: every cell), warm or after a compaction.</summary>
        public void Changed(bool replaced, int cells, bool reset)
        {
            (replaced ? _replacedChanged : _warmChanged).Add(cells);
            if (reset) _ = replaced ? _replacedResets++ : _warmResets++;
        }

        public override string ToString()
        {
            return _updates == 0
                ? ""
                : $"{Mode}: first update {_first:F0} ms; warm {_warm}; after a compaction {_replaced}; " +
                  $"cells changed warm {_warmChanged.Counts()} ({_warmResets:N0} resets), after a compaction {_replacedChanged.Counts()} ({_replacedResets:N0} resets); state {_state / 1048576.0:F1} MB; " +
                  $"{_allocated / _updates / 1024:N0} KB allocated an update (the process's)";
        }

        private sealed class Ring
        {
            private readonly double[] _ms = new double[2048];
            private int _count, _next;

            public void Add(double ms)
            {
                (_ms[_next], _next, _count) = (ms, (_next + 1) % _ms.Length, Math.Min(_count + 1, _ms.Length));
            }

            public override string ToString()
            {
                if (_count == 0) return "none yet";
                var sorted = _ms.AsSpan(0, _count).ToArray();
                Array.Sort(sorted);

                double At(double q)
                {
                    return sorted[Math.Min(_count - 1, (int)Math.Ceiling(q * _count) - 1)];
                }

                return $"p50 {At(0.5):F1}, p90 {At(0.9):F1}, p99 {At(0.99):F1}, max {sorted[^1]:F1} ms ({_count:N0})";
            }

            /// <summary>The same, as counts.</summary>
            public string Counts()
            {
                if (_count == 0) return "none yet";
                var sorted = _ms.AsSpan(0, _count).ToArray();
                Array.Sort(sorted);

                double At(double q)
                {
                    return sorted[Math.Min(_count - 1, (int)Math.Ceiling(q * _count) - 1)];
                }

                return $"p50 {At(0.5):F0}, p90 {At(0.9):F0}, max {sorted[^1]:F0} ({_count:N0})";
            }
        }
    }

    /// <summary>The loop's buffers, reused run to run.</summary>
    private sealed class Work
    {
        public readonly Drained Drained = new();
        public readonly List<PivotKey> Keys = [], OtherKeys = [];
        public readonly List<PivotCell> Rows = [], OtherRows = [];
        public readonly HashSet<PivotKey> Seen = [], OtherSeen = [];
        public readonly List<KeyValuePair<PivotKey, PivotCell>> Table = [];
        public readonly WideDrained Wide = new(); // (an extended pivot's drain)
        public readonly List<WideRow> OtherWide = []; // (and its validation's rows, wide)
    }

    /// <summary>
    ///     A view's changes, drained: the whole answer after a reset, else the cells that changed and those gone — then
    ///     its version and checksum.
    /// </summary>
    /// <summary>
    ///     A deadband over each key's values — a mark by instrument, a rate by symbol: one applied (kept, and loaded into
    ///     ChalkQL) once it's moved the band from the last applied, in basis points, or the last is a heartbeat old — or with
    ///     every one (<c>all</c>: the hour's last tick). The rest let go, in place: the simulation keeps its own.
    /// </summary>
    private sealed class Deadband(int keys, double bps, TimeSpan heartbeat)
    {
        private readonly DateTime[] _at = new DateTime[keys]; // (default: none applied yet)
        private readonly double[] _value = new double[keys];
        private long _seen, _applied;

        public void Apply<T>(List<T> rows, Func<T, int> key, Func<T, double> value, DateTime now, bool all)
        {
            var kept = 0;
            for (var i = 0; i < rows.Count; i++)
                if (Applies(key(rows[i]), value(rows[i]), now, all))
                    rows[kept++] = rows[i];
            rows.RemoveRange(kept, rows.Count - kept);
        }

        /// <summary>Whether a key's value applies — then the last applied — asked before a row's made for it.</summary>
        public bool Applies(int k, double v, DateTime now, bool all)
        {
            _seen++;
            if (!(all || _at[k] == default || now - _at[k] >= heartbeat ||
                  Math.Abs(v / _value[k] - 1) * 10_000 >= bps)) return false;
            (_value[k], _at[k], _applied) = (v, now, _applied + 1);
            return true;
        }

        public override string ToString()
        {
            return _seen == 0 ? "none yet" : $"{_applied:N0} of {_seen:N0} ({100.0 * _applied / _seen:F0}%)";
        }
    }

    /// <summary>
    ///     The loop's sleep, reused: until a time (one timer, re-armed), or a signal — a pivot admitted, the provider
    ///     stopping. One given while the loop's awake isn't lost: its next sleep returns at once. A sleep allocates nothing.
    /// </summary>
    private sealed class Waker : IValueTaskSource, IDisposable
    {
        private const int Awake = 0, Asleep = 1, Woken = 2; // (woken: signaled while awake)
        private readonly Timer _timer;
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };
        private int _state;

        public Waker()
        {
            _timer = new Timer(static waker => ((Waker)waker!).Signal(), this, Timeout.Infinite, Timeout.Infinite);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }

        /// <summary>Asleep until <paramref name="due" /> from now, or a signal (one since the last sleep: at once).</summary>
        public ValueTask WaitAsync(TimeSpan due)
        {
            _core.Reset();
            if (Interlocked.CompareExchange(ref _state, Asleep, Awake) != Awake)
            {
                Volatile.Write(ref _state, Awake); // (woken meanwhile)
                return ValueTask.CompletedTask;
            }

            _timer.Change(due, Timeout.InfiniteTimeSpan);
            return new ValueTask(this, _core.Version);
        }

        public void Signal()
        {
            if (Interlocked.CompareExchange(ref _state, Awake, Asleep) == Asleep) _core.SetResult(true);
            else Interlocked.CompareExchange(ref _state, Woken, Awake);
        }

        void IValueTaskSource.GetResult(short token)
        {
            _core.GetResult(token);
        }

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
        {
            return _core.GetStatus(token);
        }

        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _core.OnCompleted(continuation, state, token, flags);
        }
    }

    /// <summary>
    ///     A rewrite of ChalkQL's tables prepared off the loop (design 69): each table's replacement, and how ours follow it
    ///     once it's committed; what preparing took (on a worker), and the loop's copying; whether it compacts the as-of logs.
    /// </summary>
    private sealed record Rewriting(
        Task<((PreparedReplacement Prepared, Action Splice)[] Parts, double Ms)> Preparing,
        int Generation,
        bool Compacts,
        double CopyMs);

    /// <summary>
    ///     ChalkQL's refreshes as the loop waits on them (each holding ChalkQL's refresh lock as it applies): the appends —
    ///     those while a rewrite's prepared off the loop, or just committed, apart — and the rewrites' (the as-of logs
    ///     compacted, the ledger trimmed): their refresh, replacing or committing; prepared off the loop, what that took; the
    ///     loop's own share (its copying — or, rewritten on the loop, the rewriting); the plans ChalkQL swapped in (planned
    ///     again in the background, Pivot:DeferPlanning, and taken in at a refresh; or by PlanAsync after a whole
    ///     replacement); and the GC's gen2 collections since the loop started.
    /// </summary>
    private sealed class Refreshes
    {
        private readonly Ring _appends = new(), _during = new(), _rewrites = new(), _prepares = new(), _onLoop = new(),
            _appendBytes = new();
        private int _gen2;
        private TimeSpan _paused;
        public int Swaps, LetGo;

        public void Started()
        {
            (_gen2, _paused) = (GC.CollectionCount(2), GC.GetTotalPauseDuration());
        }

        public void Appended(double ms, bool during, long allocated)
        {
            (during ? _during : _appends).Add(ms);
            if (!during) _appendBytes.Add(allocated);
        }

        public void Rewritten(double ms)
        {
            _rewrites.Add(ms);
        }

        public void Prepared(double ms, double copyMs)
        {
            _prepares.Add(ms);
            _onLoop.Add(copyMs);
        }

        public void OnLoop(double ms)
        {
            _onLoop.Add(ms);
        }

        public override string ToString()
        {
            return
                $"appends {_appends}, {_appendBytes.Bytes()} allocated each (the process's); those while a rewrite's prepared or committed {_during}; rewrites' refreshes {_rewrites}, prepared off the loop {_prepares}, " +
                $"the loop's share {_onLoop}; {LetGo} let go; {Swaps} plans swapped in; {GC.CollectionCount(2) - _gen2} gen2 collections since the start, " +
                $"{(GC.GetTotalPauseDuration() - _paused).TotalMilliseconds:N0} ms paused in all";
        }

        private sealed class Ring
        {
            private readonly double[] _ms = new double[4096];
            private int _count, _next;

            public void Add(double ms)
            {
                (_ms[_next], _next, _count) = (ms, (_next + 1) % _ms.Length, Math.Min(_count + 1, _ms.Length));
            }

            public override string ToString()
            {
                if (_count == 0) return "none yet";
                var sorted = _ms.AsSpan(0, _count).ToArray();
                Array.Sort(sorted);

                double At(double q)
                {
                    return sorted[Math.Min(_count - 1, (int)Math.Ceiling(q * _count) - 1)];
                }

                return $"p50 {At(0.5):F1}, p99 {At(0.99):F1}, max {sorted[^1]:F1} ms ({_count:N0})";
            }

            public string Bytes() // (its entries bytes, not ms)
            {
                if (_count == 0) return "none yet";
                var sorted = _ms.AsSpan(0, _count).ToArray();
                Array.Sort(sorted);
                return $"p50 {sorted[(_count - 1) / 2]:N0} B, mean {sorted.Average():N0} B";
            }
        }
    }

    /// <summary>A post a topic made: its view's version, whether a snapshot, its keys, the checksum it expected, a snapshot's rows' own, when.</summary>
    private readonly record struct PostRecord(long Version, bool Snapshot, int Count, uint Expected, uint Own, DateTime At);

    /// <summary>The tables the loop's refreshes write, by their handles.</summary>
    private sealed record Tables(
        PocoTable<Posting> Postings,
        PocoTable<Journal> Journal,
        PocoTable<HedgeFill> Fills,
        PocoTable<PositionRisk> Risk,
        PocoTable<Mark> Marks,
        PocoTable<ConversionRate> Rates);

    /// <summary>
    ///     A refresh of the loop's, all it needs in one value: given to ChalkQL with a static plan, the call allocates no
    ///     closure and no delegate (the loop's locals aren't captured). The clock advanced; the ledger's rows appended, or
    ///     its tables replaced whole once trimmed; the as-of logs' appended, or replaced once compacted; planning deferred.
    /// </summary>
    private readonly record struct Batch(
        Tables Tables,
        DateTimeOffset Clock,
        bool Trimmed,
        bool Compacting,
        bool DeferPlanning,
        IReadOnlyList<Posting> Postings,
        IReadOnlyList<Journal> Journal,
        IReadOnlyList<HedgeFill> Fills,
        IReadOnlyList<PositionRisk> Risk,
        IReadOnlyList<Mark> Marks,
        IReadOnlyList<ConversionRate> Rates)
    {
        public void Plan(RefreshBuilder refresh)
        {
            refresh.AdvanceClock(Clock);
            if (Trimmed)
            {
                refresh.Replace(Tables.Postings, Postings).Replace(Tables.Journal, Journal).Replace(Tables.Fills, Fills);
            }
            else
            {
                if (Postings.Count > 0)
                    refresh.Append(Tables.Postings, Postings, StatisticsRefresh.Defer)
                        .Append(Tables.Journal, Journal, StatisticsRefresh.Defer);
                if (Fills.Count > 0) refresh.Append(Tables.Fills, Fills, StatisticsRefresh.Defer);
            }

            if (Compacting)
            {
                refresh.Replace(Tables.Risk, Risk).Replace(Tables.Marks, Marks).Replace(Tables.Rates, Rates);
            }
            else
            {
                if (Risk.Count > 0) refresh.Append(Tables.Risk, Risk, StatisticsRefresh.Defer);
                if (Marks.Count > 0) refresh.Append(Tables.Marks, Marks, StatisticsRefresh.Defer);
                if (Rates.Count > 0) refresh.Append(Tables.Rates, Rates, StatisticsRefresh.Defer);
            }

            if (DeferPlanning) refresh.DeferPlanning();
        }
    }

    private sealed class Drained : IViewSink<PivotKey, PivotCell>
    {
        public readonly List<CellChange> Changes = []; // (otherwise)
        public readonly List<PivotKey> Keys = [];
        public readonly List<PivotCell> Rows = []; // (after a reset)
        public uint Checksum;
        public int Upserts, Deletes;
        public long Version;
        public bool WasReset;

        public void Reset()
        {
            Keys.Clear();
            Rows.Clear();
            Changes.Clear();
            (WasReset, Upserts, Deletes) = (true, 0, 0);
        }

        public void Upsert(in PivotKey key, in PivotCell row)
        {
            Keys.Add(key);
            if (WasReset) Rows.Add(row);
            else Changes.Add(new CellChange(row, 0));
            Upserts++;
        }

        public void Delete(in PivotKey key)
        {
            Keys.Add(key);
            Changes.Add(new CellChange(default, 1));
            Deletes++;
        }

        public long Generation; // (its view's rebinds: with the version, where it stands)

        public void Commit(long version, uint checksum)
        {
            (Version, Checksum) = (version, checksum);
        }

        public void Commit(in ViewStamp stamp, uint checksum)
        {
            (Version, Generation, Checksum) = (stamp.Version, stamp.Generation, checksum);
        }

        public void Clear()
        {
            Keys.Clear();
            Rows.Clear();
            Changes.Clear();
            (WasReset, Upserts, Deletes) = (false, 0, 0);
        }
    }

    /// <summary>What a pivot's view has drained: runs, resets, cells upserted and deleted, against the cells it holds.</summary>
    private sealed class DrainCounts
    {
        public long Runs, Resets, Upserts, Deletes, Cells;

        public void Record(Drained drained, int cells)
        {
            Record(drained.Upserts, drained.Deletes, drained.WasReset, cells);
        }

        public void Record(int upserts, int deletes, bool reset, int cells)
        {
            Runs++;
            if (reset) Resets++;
            (Upserts, Deletes, Cells) = (Upserts + upserts, Deletes + deletes, Cells + cells);
        }

        public override string ToString()
        {
            return Runs == 0
                ? ""
                : $"{Runs} drains ({Resets} resets): {Upserts:N0} upserts, {Deletes:N0} deletes, against {Cells:N0} cells held";
        }
    }

    /// <summary>
    ///     A pivot's checks, validating: its view against its statement run in full at the same epoch, and the router's table
    ///     against the answer it was posted.
    /// </summary>
    private sealed class Checks
    {
        public string? LastMismatch;
        public string? LastProblem;
        public double MaxRelative; // the largest relative difference in one, and where it was
        public long MaxUnits; // the largest difference in a double-summed figure (to 8 places)
        public double Ms, OtherMs; // smoothed: this pivot's run, the other's
        public (long Version, Dictionary<PivotKey, PivotCell> Cells)? Pending;
        public long Runs, ViewChecks, ViewMismatches, TableChecks, TableMismatches, Failures;
        public string? Worst;

        public void Record(double ms, double otherMs)
        {
            Runs++;
            (Ms, OtherMs) = Runs == 1 ? (ms, otherMs) : (Ms + (ms - Ms) / 8, OtherMs + (otherMs - OtherMs) / 8);
        }

        public void Fail(string problem)
        {
            (Failures, LastProblem) = (Failures + 1, problem);
        }

        /// <summary>
        ///     The view's checksum against its statement's, run in full at the same epoch. An incremental view keeps its DOUBLE
        ///     sums exactly and rounds them once, and a run adds in input order, so the last bits of a figure — and so the
        ///     checksums — may differ where the cells agree (<see cref="Table" /> says whether they do).
        /// </summary>
        public void View(uint expected, uint actual, int expectedCells, int actualCells)
        {
            ViewChecks++;
            if (expected != actual) ViewMismatches++;
            if (expectedCells != actualCells)
                LastProblem = $"the view holds {actualCells} cells, its statement answers {expectedCells}";
        }

        public void Table(List<KeyValuePair<PivotKey, PivotCell>> table, Dictionary<PivotKey, PivotCell> expected,
            long version)
        {
            var mismatches = Math.Abs(table.Count - expected.Count);
            foreach (var (key, row) in table)
                if (!expected.TryGetValue(key, out var want) || !Close(key, row, want, version))
                    mismatches++;
            Tabled(mismatches, table.Count, expected.Count, version);
        }

        /// <summary>A router's table checked: how many of its cells differ from what it was posted.</summary>
        public void Tabled(int mismatches, int count, int expected, long version)
        {
            TableChecks++;
            if (mismatches > 0)
                (TableMismatches, LastProblem) = (TableMismatches + 1,
                    $"the router's table at version {version}: {mismatches} cells differ ({count} against {expected})" +
                    (LastMismatch is { } m ? $"; {m}" : ""));
        }

        /// <summary>
        ///     A cell against its statement's: quantity and trades exact (DECIMAL, a count); the others — DOUBLE sums, added in
        ///     another order or kept exactly — within a unit (10⁻⁸) or 10⁻⁹ of the figure. The largest difference, absolute and
        ///     relative, kept, and where the relative one was.
        /// </summary>
        public bool Close(PivotKey key, PivotCell a, PivotCell b, long version)
        {
            if (a.Quantity != b.Quantity || a.Trades != b.Trades)
            {
                LastMismatch =
                    $"{key} at version {version}: quantity {a.Quantity} against {b.Quantity}, trades {a.Trades} against {b.Trades}";
                return false;
            }

            var close = true;
            foreach (var (name, x, y) in new[]
                     {
                         ("notional", a.Notional, b.Notional), ("realized", a.Realized, b.Realized),
                         ("unrealized", a.Unrealized, b.Unrealized),
                         ("balance", a.Balance, b.Balance), ("volume", a.Volume, b.Volume),
                         ("max gross", a.MaxGross, b.MaxGross),
                         ("mark price", a.MarkPrice, b.MarkPrice)
                     })
                close &= Near(key, name, x, y, version);

            return close;
        }

        /// <summary>A figure against its statement's: within a unit (10⁻⁸; a cent, volume's) or 10⁻⁹ of it — its difference kept.</summary>
        public bool Near(PivotKey key, string name, long x, long y, long version)
        {
            if (x == y) return true;
            if (x == PivotCell.None || y == PivotCell.None)
            {
                LastMismatch = $"{key} {name} at version {version}: {x} against {y}";
                return false;
            }

            var units = Math.Abs(x - y);
            var relative = units / (double)Math.Max(Math.Abs(x), Math.Abs(y));
            MaxUnits = Math.Max(MaxUnits, units);
            if (relative > MaxRelative)
                (MaxRelative, Worst) = (relative, $"{key} {name} at version {version}: {x} against {y} units");
            if (units <= 1 || relative <= 1e-9) return true;
            LastMismatch = $"{key} {name} at version {version}: {x} against {y} units ({relative:E1} of it)";
            return false;
        }

        public override string ToString()
        {
            return Runs == 0
                ? ""
                : $"{Runs} runs, {Ms:F1} ms against {OtherMs:F1} ms" +
                  (ViewChecks > 0 ? $"; view checksums {ViewChecks}: {ViewMismatches} differ" : "") +
                  (TableChecks > 0 ? $"; router tables {TableChecks}: {TableMismatches} differ" : "") +
                  $"; largest difference {MaxUnits} units, {MaxRelative:E1} of a figure" +
                  (Worst is { } worst ? $" ({worst})" : "") +
                  (Failures > 0 ? $"; {Failures} failed" : "") +
                  (LastProblem is { } last ? $" — last problem: {last}" : "");
        }
    }
}

/// <summary>A pivot's plan, as ChalkQL prepared it: physical and logical.</summary>
public sealed record PreparedPlan(string Physical, string Logical);