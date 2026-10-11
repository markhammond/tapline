using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using ActivityFeed.Contracts;
using ActivityFeed.Service;
using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using Tapline;
using Tapline.Client;
using Tapline.MagicOnion;
using ActivityKind = ActivityFeed.Contracts.ActivityKind;

namespace ActivityFeed.Headless;

/// <summary>
///     The practice's personas, each one's feed streamed on one session, and what each is shown checked against the
///     policy as it arrives:
///     <list type="bullet">
///         <item>reception sees who, and the schedule's notes, never a visit's clinical section;</item>
///         <item>
///             the manager sees who is booked and with whom (the appointments), never a note or a section; each row's
///             basis checked: a GP's own visits for care, the rest of the site's for scheduling (as reception's), a
///             patient's own record and a carer's;
///         </item>
///         <item>
///             a GP reads a section only where they attended the visit or the patient shares it with them (a patient's
///             registration shares everything with their GP), and then in full;
///         </item>
///         <item>
///             the manager's statistics only counts and means, by the hour and by reason, every count by reason at least
///             five or withheld;
///             a patient only their own rows, in full; a screen only tickets;
///         </item>
///         <item>each replica's checksum is the router's, after every record;</item>
///         <item>
///             each dispatcher topic's entry (watched as an observer) carries nothing personal or clinical, and a token
///             only on a send.
///         </item>
///     </list>
///     Then the demo: reception checks Jane Smith in and her GP calls her in, each command answered once its change is
///     in front of whoever gave it (its rendezvous), and the screen and Jane's own feed follow. A line a second; at the
///     end, what failed, and, the service spawned (its health port known), whether any view of the feeds gave way to a
///     recomputed one.
/// </summary>
internal sealed class Verifier(GrpcChannel channel, Uri app, HttpClient http, bool viewer, bool quiet, Uri? serviceAtStart,
    Backend? backend)
{
    /// <summary>The service's health port: the instance that writes (green, once it has taken over from blue).</summary>
    private Uri? service => backend?.ServiceHttp ?? serviceAtStart;

    private readonly Dictionary<int, (uint TapId, Guid? NodeId)> _observers = []; // (each shard's observer: its binding)
    private Guid? _drainingNode; // (blue's, as the router named it draining)
    private int _endedMoving; // (feeds that ended before they moved off it)

    /// <summary>What no push's text or reason may say: anyone's name.</summary>
    private static readonly string[] Names = [.. Service.People.Patients.Concat(Service.People.Carers).Select(p => p.Name)];

    /// <summary>
    ///     What a push about someone's own care (transactional) may not say: what anyone came for, or was tested for. A
    ///     notice's text is the practice's, the same for everyone it's sent to ("Flu vaccinations are available").
    /// </summary>
    private static readonly string[] Clinical =
        [.. StatsRow.Reasons, .. Service.Simulator.Problems, "Blood test", "Kidney function", "Screening", "Antenatal bloods"];

    private readonly Dictionary<int, Dictionary<long, NotifyRow>> _notify = []; // (the observer's: each shard's topic, as it is)
    private DateTime? _tomRevoked;
    private bool _killed; // (the service killed, on purpose: its feeds' ends expected)
    private const int Jane = 1001, Rosa = 101, Priya = 104, Sam = 102, Patel = 201, Okafor = 202, Lee = 203, Tom = 3001, Max = 301;

    /// <summary>What a sensitive visit or result is for: never in the feed of a carer without the sensitive scope.</summary>
    private static readonly HashSet<string> Sensitive =
        ["Sexual health", "Mental health review", "Pregnancy", "Screening", "Antenatal bloods"];

    private readonly HashSet<int> _caredFor = [Jane]; // (Tom's designation, while it stands)
    private const int ScreenNorth = 901;

    // A connection each — each person's signed in as them (passthrough), as their own app's would be, and the observer's as
    // "observer" — for the practice serves a connection its own person's feed alone.
    private readonly Dictionary<string, TaplineStreamConnection> _connections = [];
    private readonly HashSet<(string Change, Guid Node, Guid Correlation)> _told = []; // (noted once: every connection hears it)
    private CancellationToken _stop;
    private const string Observer = "observer";

    /// <summary>The connection that signs in as <paramref name="user" />: made, and run, the first time.</summary>
    private TaplineStreamConnection ConnectionOf(string user)
    {
        lock (_connections)
        {
            if (_connections.TryGetValue(user, out var known)) return known;
            var connection = new TaplineStreamConnection(channel, new TaplineStreamConnectionOptions
            {
                Client = new TaplineClientOptions { Http = http, BlobBase = new Uri(app, "_tapline/blob") }
            });
            var client = connection.Client;
            client.Rendezvous += Rendezvous;
            client.Refused += r => Note($"refused by {r.Service} ({user}'s): {r.Reason} (correlation {r.CorrelationId})");
            client.ServiceDown += (node, correlation) =>
            {
                if (Told("down", node, correlation)) Note($"the practice's provider node is gone ({node}, correlation {correlation})");
            };
            client.ServiceDraining += (node, grace, correlation) => _ = DrainingAsync(user, node, grace, correlation, _stop);
            client.ServiceDrained += (node, correlation) =>
            {
                if (Told("drained", node, correlation))
                    Note($"the practice's provider node {node} let this client's subscriptions go (correlation {correlation})");
            };
            _connections[user] = connection;
            _ = connection.RunAsync(_stop);
            return connection;
        }
    }

    private bool Told(string change, Guid node, Guid correlation)
    {
        lock (_told)
        {
            return _told.Add((change, node, correlation));
        }
    }

    private void DropAll()
    {
        lock (_connections)
        {
            foreach (var connection in _connections.Values) connection.Drop();
        }
    }
    private readonly HashSet<(int Gp, int Patient)> _emergency = []; // (the demo's: broken glass, allowed while it stands)
    private readonly HashSet<string> _failures = [];
    private readonly HashSet<(int Patient, int Gp, int Problem)> _shared = []; // (the demo's: what's shared, allowed)
    private readonly Dictionary<int, Feed> _feeds = [];
    private readonly object _gate = new(); // (a monitor: entered again by a check that fails)
    private readonly byte[] _sender = Guid.CreateVersion7().ToByteArray(); // (this verifier, as its commands' sender)
    private readonly Dictionary<string, long> _numbered = []; // (its commands' last number, by ordering key)
    private readonly List<string> _notes = [];
    private readonly Dictionary<string, Pending> _pending = []; // rendezvous awaited, by correlation (hex)
    private int _batches, _printed = -1, _quietSeconds;

    private IPracticeFeed Practice(CancellationToken token)
    {
        return MagicOnionClient.Create<IPracticeFeed>(channel).WithCancellationToken(token);
    }

    public async Task<int> RunAsync(TimeSpan? verify, CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        _stop = token;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_connections) // (the observer's connection first: the stream's, heard connecting)
        {
            var observer = ConnectionOf(Observer);
            observer.Connected += (_, _) => connected.TrySetResult();
            if (observer.Session is not null) connected.TrySetResult();
        }

        var completed = false; // (ran its time: not stopped, and nothing threw)
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            var personas = (await Practice(token).PersonasAsync()).Where(p => p.Role != "auditor").ToArray(); // (no feed: the audit page)
            Console.Out.WriteLine($"personas: {string.Join(", ", personas.Select(p => $"{p.Name} ({p.Role})"))}");
            foreach (var persona in personas)
            {
                await SubscribeAsync(persona, null, token);
                if (persona.Role == "manager") await SubscribeAsync(persona, "stats", token); // (and their statistics)
            }

            for (var shard = 0; shard < 2; shard++) await ObserveAsync(shard, token);
            await Task.WhenAll(_feeds.Values.Select(f => f.First.Task)).WaitAsync(TimeSpan.FromSeconds(30), token);
            Console.Out.WriteLine($"every feed's first state in {started.Elapsed.TotalSeconds:F1} s:");
            lock (_gate)
            {
                foreach (var feed in _feeds.Values) Console.Out.WriteLine(feed.Summary());
            }

            Console.Out.Flush();
            DispatchOrderChecks();
            await SignInChecksAsync(token);
            await DemoAsync(token);
            Console.Out.Flush();
            var ends = verify is { } run ? started.Elapsed + run : TimeSpan.MaxValue;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (started.Elapsed < ends && await timer.WaitForNextTickAsync(token)) Second();
            await BlueGreenAsync(token);
            completed = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (TimeoutException e)
        {
            Fail($"timed out: {e.Message}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Fail($"{e.GetType().Name}: {e.Message}");
        }

        lock (_gate)
        {
            foreach (var feed in _feeds.Values.Where(f => !f.First.Task.IsCompleted))
                Fail($"{feed.Name}'s feed: no first state");
        }

        await EndChecksAsync();
        await AccessChecksAsync();
        await ExplainChecksAsync();
        await ViewChecksAsync();
        await DispatchChecksAsync();
        await OracleChecksAsync();
        if (completed)
            try
            {
                await KilledAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (TimeoutException e)
            {
                Fail($"the service killed: timed out ({e.Message})");
            }
        DropAll();
        return End(verify is not null, completed);
    }

    /// <summary>
    ///     The oracle, at the end (the service's <c>/oracle</c>): every open topic (each person's, the screens', the
    ///     statistics', the dispatchers') holds exactly its statements run whole as its principal, its own rows beside them:
    ///     the same rows, by count and checksum. With the replicas' own checks (each the router's, at every record), that's
    ///     every client holding what the policy says, end to end.
    /// </summary>
    private async Task OracleChecksAsync()
    {
        if (service is null) return;
        OracleReport[]? reports;
        try
        {
            reports = await http.GetFromJsonAsync<OracleReport[]>(new Uri(service, "oracle"), System.Text.Json.JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            Fail($"the service's oracle: {e.Message}");
            return;
        }

        foreach (var report in (reports ?? []).Where(r => !r.Ok))
            Fail($"the oracle: {report.Topic} holds {report.Rows} rows ({report.Held:x8}) at version {report.Version}, " +
                 $"its statements run whole {report.ExpectedRows} ({report.Expected:x8})");
        var checkedTopics = (reports ?? []).Where(r => r.Note is null).ToList();
        Console.Out.WriteLine($"oracle: {checkedTopics.Count} open topics, each its statements run whole: " +
                              $"{checkedTopics.Count(r => r.Ok)} the same rows by count and checksum " +
                              $"({checkedTopics.Sum(r => r.Rows)} rows in all){((reports ?? []).Length > checkedTopics.Count ? $", {(reports ?? []).Length - checkedTopics.Count} held" : "")}");
    }

    /// <summary>
    ///     Blue/green (spawned): green started from blue's write log (it runs every turn logged, follows blue, and serves once
    ///     caught up); then blue drained: it resigns the lease (green writes from its next look), and the router moves its
    ///     clients over, each subscribing again on green and letting blue go once green streams (make before break); blue
    ///     stops once its drain is over. Checked: green followed blue (no turn's writes differed); took the lease; every feed
    ///     and observer moved, none ended, each feed's newest activity in its first state on green; a command after, green's,
    ///     at its rendezvous; blue gone. The end's checks (the oracle, the access and dispatch logs) are then green's.
    /// </summary>
    private async Task BlueGreenAsync(CancellationToken token)
    {
        if (backend is null) return;
        var timer = Stopwatch.StartNew();
        await backend.StartGreenAsync(token);
        LogStatus? blue = null, green = null;
        await UntilAsync(async () =>
        {
            (blue, green) = (await LogOfAsync(backend.BlueHttp, token), await LogOfAsync(backend.GreenHttp, token));
            return green is { Role: "following" } g && blue is { } b && g.Read >= b.LastTurn && await ServesAsync(backend.GreenHttp, token);
        }, TimeSpan.FromSeconds(90), "green caught up from the log, following blue", token);
        var (caughtUp, replayed) = (timer.Elapsed, green?.Turns ?? 0);
        await Task.Delay(TimeSpan.FromSeconds(6), token); // (green following blue a while: the turns blue writes, run on green)
        var following = await LogOfAsync(backend.GreenHttp, token);
        if (following is not { Signals: > 0 }) Fail($"green didn't hear blue's log signal while it followed: {following?.Signal}");
        var (newest, feeds, messaged) = Locked(() => (
            _feeds.Values.Where(f => f is { IsScreen: false, IsStats: false, Rows.Count: > 0 }).ToDictionary(f => f.Persona.Id, f => f.Rows.Keys.Max()),
            _feeds.Count, _feeds[Jane].Rows.Where(r => r.Value.Kind == ActivityKind.Messaged).Select(r => r.Key).DefaultIfEmpty().Max()));
        timer.Restart();
        backend.Retire();
        using (var drained = await http.PostAsync(new Uri(backend.BlueHttp, "drain?over=3&grace=10"), null, token))
            if (!drained.IsSuccessStatusCode) Fail($"blue's drain: {(int)drained.StatusCode}");
        await UntilAsync(async () => (green = await LogOfAsync(backend.GreenHttp, token)) is { Role: "writing" },
            TimeSpan.FromSeconds(10), "green writing once blue drained", token);
        var tookOver = timer.Elapsed;
        await Until(() => _drainingNode is { } node && _feeds.Values.All(f => f.NodeId != node) && _observers.Values.All(o => o.NodeId != node),
            TimeSpan.FromSeconds(30), "every feed and observer moved off blue", token);
        var moved = timer.Elapsed;
        await Until(() => newest.All(n => _feeds[n.Key].Rows.ContainsKey(n.Value)), TimeSpan.FromSeconds(5),
            "each feed's newest activity in its first state on green", token);
        var kept = Locked(() => newest.Count(n => _feeds[n.Key].Rows.ContainsKey(n.Value)));
        var wrote = await CommandAsync(Jane,
            correlation => Practice(token).MessageAsync(Jane, "Thank you: see you on Thursday.", correlation, Order($"thread:{Jane}")),
            () => _feeds[Jane].Rows.Any(r => r.Key > messaged && r.Value.Kind == ActivityKind.Messaged) ? null : "Jane's feed hasn't her message",
            "Jane writes again, green writing", token);
        await Until(() => !backend.BlueRuns, TimeSpan.FromSeconds(30), "blue stopped once its drain was over", token);
        green = await LogOfAsync(backend.GreenHttp, token);
        foreach (var divergence in green?.Divergences ?? []) Fail($"green's writes differed from blue's at {divergence}");
        Console.Out.WriteLine($"blue/green: green caught up from the write log in {caughtUp.TotalSeconds:F1} s ({replayed} turns run), " +
                              $"then followed blue by its log signal ({following?.Signals} heard): {green?.Lag?.Samples} turns, each read " +
                              $"{green?.Heard?.P50:F1} ms after blue appended it at p50 (max {green?.Heard?.Max:F1}), run by " +
                              $"{green?.Lag?.P50:F1} (max {green?.Lag?.Max:F1}); " +
                              $"blue drained: green wrote {tookOver.TotalMilliseconds:F0} ms later; every feed ({feeds}) and both observers " +
                              $"moved make-before-break in {moved.TotalSeconds:F1} s, {Locked(() => _endedMoving)} ended, {kept} of " +
                              $"{newest.Count} with their newest activity on green; " +
                              $"{(wrote ? "a command after, green's, at its rendezvous" : "the command after FAILED")}; blue stopped; " +
                              $"{(green?.Divergences.Length > 0 ? "writes DIFFERED" : "no turn's writes differed")}");
    }

    private async Task<LogStatus?> LogOfAsync(Uri health, CancellationToken token)
    {
        try
        {
            return await http.GetFromJsonAsync<LogStatus>(new Uri(health, "log"), System.Text.Json.JsonSerializerOptions.Web, token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null; // (not answering yet)
        }
    }

    private async Task<bool> ServesAsync(Uri health, CancellationToken token)
    {
        try
        {
            using var ready = await http.GetAsync(new Uri(health, "readyz"), token);
            return ready.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task UntilAsync(Func<Task<bool>> done, TimeSpan within, string what, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (!await done())
        {
            if (timer.Elapsed > within)
            {
                Fail($"not within {within.TotalSeconds:F0} s: {what}");
                return;
            }

            await Task.Delay(100, token);
        }
    }

    /// <summary>
    ///     The service killed, checked (spawned), last of all: every feed keeps its last state (shown stale) until the router
    ///     presumes the provider dead and the feed ends; the service started again on its ports (a new instance), it replays
    ///     the write log to its last entry and carries on from there: past the last turn the killed one had run, none of the
    ///     replayed turns' writes differing from what the killed one logged; every feed is asked for again and has its first
    ///     state, checked as ever: each still with its newest activity of before.
    /// </summary>
    private async Task KilledAsync(CancellationToken token)
    {
        if (backend is null) return;
        Dictionary<int, int> held;
        Dictionary<int, long> newest;
        lock (_gate)
        {
            _killed = true;
            held = _feeds.ToDictionary(f => f.Key, f => f.Value.Rows.Count + f.Value.Screen.Count + f.Value.Stats.Count);
            newest = _feeds.Values.Where(f => f is { IsScreen: false, IsStats: false, Rows.Count: > 0 })
                .ToDictionary(f => f.Key, f => f.Rows.Keys.Max());
        }

        var before = await LogOfAsync(backend.ServiceHttp, token); // (a turn it had run: the log has it, and every one before)
        var timer = Stopwatch.StartNew();
        await backend.KillServiceAsync();
        await Until(() => _feeds.Values.All(f => f.Ended is not null), TimeSpan.FromSeconds(60),
            "every feed ended, the service presumed dead", token);
        var (down, ends) = (timer.Elapsed, Locked(() => string.Join(", ", _feeds.Values.Select(f => f.Ended?.Code).Distinct())));
        var kept = Locked(() => _feeds.Values.Count(f => f.Rows.Count + f.Screen.Count + f.Stats.Count == held[f.Key]));
        timer.Restart();
        await backend.StartServiceAsync(token); // (the same practice, from its log: what was shared or revoked, still so)
        var feeds = Locked(() => _feeds.Values.Select(f => (f.Persona, f.Of)).ToList());
        foreach (var (persona, of) in feeds) await SubscribeAsync(persona, of, token);
        for (var shard = 0; shard < 2; shard++) await ObserveAsync(shard, token);
        await Task.WhenAll(Locked(() => _feeds.Values.Select(f => f.First.Task).ToList())).WaitAsync(TimeSpan.FromSeconds(60), token);
        lock (_gate) _killed = false;
        var same = Locked(() => newest.Count(n => _feeds[n.Key].Rows.ContainsKey(n.Value)));
        var after = await LogOfAsync(backend.ServiceHttp, token);
        Console.Out.WriteLine($"demo: the service killed: every feed kept its last state ({kept} of {held.Count}) until it ended " +
                              $"({ends}, {down.TotalSeconds:F1} s on); started again, a new instance replayed the write log past " +
                              $"the killed one's last turn ({before?.LastTurn}: {after?.Turns} turns run, " +
                              $"{(after?.Divergences.Length > 0 ? "some DIFFERING" : "none differing")}) and carried on; every feed " +
                              $"subscribed again and had its first state in {timer.Elapsed.TotalSeconds:F1} s, {same} of " +
                              $"{newest.Count} with their newest activity of before");
        if (kept != held.Count) Fail($"{held.Count - kept} feeds didn't keep their last state while the service was down");
        if (before is null || after is null || after.LastTurn <= before.LastTurn)
            Fail($"the new instance didn't carry on past the killed one's last turn ({before?.LastTurn}; it's at {after?.LastTurn})");
        foreach (var divergence in after?.Divergences ?? []) Fail($"the replayed log's writes differed from the killed instance's at {divergence}");
        if (same != newest.Count) Fail($"{newest.Count - same} feeds lost their newest activity across the restart");
    }

    /// <summary>
    ///     The service's views (<c>/views</c> on its health port), at the end: none gave way to a recomputed view, and each
    ///     incremental one validated was checked against its twin. Those recomputed from the start, though the policy asks
    ///     for incremental maintenance (ChalkQL can't maintain their statements), are named with ChalkQL's reason. Classes of
    ///     one shape share a statement (the GPs' schedules one, their sections one, the receptions' one, the screens' one),
    ///     each class's views binding its own grants.
    /// </summary>
    private async Task ViewChecksAsync()
    {
        if (service is null) return;
        ViewsReport? views;
        try
        {
            views = await http.GetFromJsonAsync<ViewsReport>(new Uri(service, "views"), System.Text.Json.JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            Fail($"the service's views: {e.Message}");
            return;
        }

        if (views is null) return;
        foreach (var gave in views.GaveWay) Fail($"{gave.View} gave way to a recomputed view at {gave.At:HH:mm:ss}: {gave.Why}");
        var validated = views.Views.Where(v => v.Kept.Contains("validated", StringComparison.Ordinal)).ToList();
        Console.Out.WriteLine($"views: {views.Views.Length} ({views.Maintenance}{(views.Validate ? ", validated" : "")}" +
                              $"{(views.PlanSwap is { } swap ? $", new plans {swap}" : "")}): " +
                              string.Join("; ", views.Views.Select(v => $"{v.View} {v.Kept}, {v.Updates} updates, {v.Rows} rows")));
        if (validated.Count > 0)
            Console.Out.WriteLine($"views: {validated.Sum(v => v.Checks)} checks against recomputed twins, " +
                                  $"{(views.GaveWay.Length == 0 ? "none" : views.GaveWay.Length.ToString())} gave way");
        List<string> shared = [];
        foreach (var (what, of) in new (string, Func<ViewReport, bool>)[]
                 {
                     ("the GPs' schedules", v => v.View.StartsWith("class gp:") && !v.View.Contains("+emergency") && v.View.EndsWith(": schedule")),
                     ("their sections", v => v.View.StartsWith("class gp:") && !v.View.Contains("+emergency") && v.View.EndsWith(": sections")),
                     ("the receptions'", v => v.View.StartsWith("class reception@")),
                     ("the screens'", v => v.View.StartsWith("screen "))
                 })
        {
            var group = views.Views.Where(of).ToList();
            if (group.Count < 2) continue;
            if (group.Select(v => v.Plan).Distinct().Count() is var statements and not 1)
                Fail($"{what} ran on {statements} statements, not one: {string.Join(", ", group.Select(v => $"{v.View} {v.Plan}"))}");
            if (group.Select(v => v.Grants).Distinct().Count() is var bound && bound != group.Count)
                Fail($"{what}: {group.Count} classes bound {bound} sets of grants, not one each");
            shared.Add($"{what} ({group.Count}) one");
        }

        static string Owner(ViewReport v) => v.View.LastIndexOf(": ", StringComparison.Ordinal) is var i and > 0 ? v.View[..i] : v.View;
        foreach (var owner in views.Views.GroupBy(Owner).Where(g => g.Select(v => v.Grants).Distinct().Count() > 1))
            Fail($"{owner.Key}'s views bound different grants");
        Console.Out.WriteLine($"views: {views.Views.Length} on {views.Views.Select(v => v.Plan).Distinct().Count()} statements, " +
                              $"each class's binding its own grants: {string.Join(", ", shared)}");
        foreach (var why in views.Views.Where(v => v.Why is not null).GroupBy(v => v.Why))
            Console.Out.WriteLine($"views recomputed, ChalkQL unable to maintain their statement ({string.Join(", ", why.Select(v => v.View))}): {why.Key}");
        foreach (var mismatch in views.OracleMismatches ?? [])
            Fail($"the sampled oracle found {mismatch.Topic} wrong at {mismatch.At:HH:mm:ss}: {mismatch.Rows} rows ({mismatch.Held:x8}), " +
                 $"its statements {mismatch.ExpectedRows} ({mismatch.Expected:x8})");
        Console.Out.WriteLine($"oracle, sampled through the run: {views.OracleChecks} topics checked against their statements run whole, " +
                              $"{(views.OracleMismatches is { Length: > 0 } m ? m.Length.ToString() : "none")} wrong");
    }

    /// <summary>A shard's dispatcher topic, watched as an observer: it reports nothing, but sees every entry it's sent.</summary>
    private async Task ObserveAsync(int shard, CancellationToken token, bool moving = false)
    {
        lock (_gate)
        {
            if (!moving) _notify[shard] = [];
        }

        var answer = await StepOneAsync(Observer, (p, outlet, nonce) => p.DispatchAsync(outlet, nonce, shard, Observer), token);
        if (answer.Refused is { } why)
        {
            Fail($"notify:{shard} refused the observer: {why}");
            return;
        }

        uint old;
        lock (_gate)
        {
            old = _observers.GetValueOrDefault(shard).TapId;
            _observers[shard] = (answer.TapId, answer.NodeId);
        }

        var handler = new SubscriptionHandler
        {
            Table = (subscription, table, _) =>
            {
                lock (_gate)
                {
                    if (subscription.TapId == _observers[shard].TapId) Observed(shard, table);
                }
            },
            Error = (subscription, error) =>
            {
                lock (_gate)
                {
                    if (subscription.TapId == _observers[shard].TapId && !_killed) Fail($"notify:{shard} ended: {error.Code}");
                }
            }
        };
        var client = ConnectionOf(Observer).Client;
        if (moving) client.Replace(old, Binding(answer), handler);
        else client.Subscribe(Binding(answer), handler);
    }

    /// <summary>A dispatcher topic's record (under the client's lock, then this one's): each entry checked.</summary>
    private void Observed(int shard, TableReplica table)
    {
        var rows = _notify[shard];
        rows.Clear();
        foreach (var row in table)
        {
            var (seq, entry) = (row.KeyAs<FeedKey>().Seq, row.RowAs<NotifyRow>());
            rows[seq] = entry;
            var (title, reason, token) = (entry.Title.ToString(), entry.Reason.ToString(), entry.Token.ToString());
            var wrong = entry switch
            {
                _ when entry.Shard != shard => $"shard {entry.Shard}'s entry",
                { Action: DispatchAction.Send } when token.Length == 0 || entry.TokenDisclosed != Disclosed.Full => "a send without its token",
                { Action: DispatchAction.NoOp } when token.Length > 0 => "a no-op carrying a token",
                { Action: DispatchAction.NoOp } when reason.Length == 0 => "a no-op that doesn't say why",
                _ when title.Length == 0 || entry.TitleDisclosed != Disclosed.Full => "no push text",
                _ when Names.FirstOrDefault(w => title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                                 reason.Contains(w, StringComparison.OrdinalIgnoreCase)) is { } named =>
                    $"\"{named}\" in its text: personal",
                _ when Clinical.FirstOrDefault(w => reason.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                                    entry.Class == NotificationClass.Transactional &&
                                                    title.Contains(w, StringComparison.OrdinalIgnoreCase)) is { } clinical =>
                    $"\"{clinical}\" in its text: clinical",
                _ => null
            };
            if (wrong is not null) Fail($"notify:{shard} #{seq} is {wrong}: {entry}");
        }

        if (!table.Verified) Fail($"notify:{shard}'s replica didn't check out at version {table.Version}");
    }

    /// <summary>
    ///     The dispatch log (the service's <c>/dispatches</c>), at the end: each shard's numbers gapless, one entry per
    ///     notification and device, each reported but the last few seconds', an urgent send handed off by its deadline or
    ///     expired, a notification seen in the app never sent to whoever saw it, a carer's only for whom they care for, and
    ///     nothing sent to a carer once their designation's revoked. A bad token failed once, its device then let go.
    /// </summary>
    private async Task DispatchChecksAsync()
    {
        if (service is null) return;
        DispatchLog? log;
        try
        {
            log = await http.GetFromJsonAsync<DispatchLog>(new Uri(service, "dispatches"), System.Text.Json.JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            Fail($"the service's dispatch log: {e.Message}");
            return;
        }

        if (log is null) return;
        var end = DateTime.UtcNow;
        var notifications = log.Notifications.ToDictionary(n => n.Id);
        foreach (var shard in log.Entries.GroupBy(e => e.Shard))
        {
            var seqs = shard.Select(e => e.Seq).Order().ToList();
            if (seqs[0] != 1 || seqs[^1] != seqs.Count || seqs.Distinct().Count() != seqs.Count)
                Fail($"shard {shard.Key}'s log isn't gapless: {seqs.Count} entries numbered {seqs[0]}–{seqs[^1]}");
        }

        foreach (var twice in log.Entries.GroupBy(e => (e.NotificationId, e.DeviceId)).Where(g => g.Count() > 1))
            Fail($"notification {twice.Key.NotificationId} has {twice.Count()} entries for device {twice.Key.DeviceId}");
        foreach (var entry in log.Entries)
        {
            var notification = notifications.GetValueOrDefault(entry.NotificationId);
            var wrong = entry switch
            {
                _ when notification is null => "for no notification",
                { Outcome: "" } when entry.WrittenAt < end.AddSeconds(-10) => "unreported, ten seconds on",
                { Action: "send", Outcome: "handed_off" } when notification.Urgent && entry.ReportedAt > notification.DeliverBy =>
                    $"an urgent send handed off after its deadline ({notification.DeliverBy:HH:mm:ss})",
                { Action: "send" } when entry.Reason.StartsWith("seen in the app", StringComparison.Ordinal) => "a send seen in the app",
                _ when entry.RecipientId == Tom && (notification.PatientId != Jane || !entry.ForCarer) => "a carer's, not for whom he cares for",
                { Action: "send" } when entry.RecipientId == Tom && entry.WrittenAt > _tomRevoked => "a send to a carer whose designation's revoked",
                _ => null
            };
            if (wrong is not null) Fail($"dispatch {entry.Shard}/{entry.Seq} is {wrong}: {entry}");
        }

        // A device's entries in their order (its ordering key): each a dispatcher reported, no sooner than the one before it.
        var outOfOrder = 0;
        foreach (var device in log.Entries.Where(e => e.Dispatcher.Length > 0).GroupBy(e => e.DeviceId))
        {
            var ordered = device.OrderBy(e => (e.Shard, e.Seq)).ToList();
            foreach (var (before, after) in ordered.Zip(ordered.Skip(1)).Where(p => p.Second.ReportedAt < p.First.ReportedAt))
            {
                outOfOrder++;
                Fail($"device {device.Key}: entry {after.Shard}/{after.Seq} reported before {before.Shard}/{before.Seq}, written before it");
            }
        }

        // Budgets: a recipient's counted pushes (a notice's sends) at most three urgent and two normal a day, the normal
        // four hours apart; a transactional one never counted.
        foreach (var recipient in log.Entries.Where(e => e.Action == "send").GroupBy(e => e.RecipientId))
        {
            var pushes = recipient.Select(e => notifications[e.NotificationId]).Where(n => n.Class != "transactional")
                .DistinctBy(n => n.Id).ToList();
            var normal = pushes.Where(n => !n.Urgent).OrderBy(n => n.CreatedAt).ToList();
            if (pushes.Count(n => n.Urgent) > 3 || normal.Count > 2 ||
                normal.Zip(normal.Skip(1)).Any(p => p.Second.CreatedAt - p.First.CreatedAt < TimeSpan.FromHours(4)))
                Fail($"recipient {recipient.Key}'s budgets: {pushes.Count(n => n.Urgent)} urgent and {normal.Count} normal pushes");
        }

        // Seen in the app, never pushed: no send to whoever saw it, of a notification they saw.
        foreach (var seen in log.Entries.Where(e => e.Reason.StartsWith("seen in the app", StringComparison.Ordinal)))
            if (log.Entries.Any(e => e.NotificationId == seen.NotificationId && e.RecipientId == seen.RecipientId && e.Action == "send"))
                Fail($"notification {seen.NotificationId} was seen in the app by {seen.RecipientId}, and sent to them too");
        // A bad token: failed, its device let go (nothing written for it after its failure).
        var bad = Service.People.Devices.Where(d => d.ApnToken.StartsWith("dead", StringComparison.Ordinal)).Select(d => d.Id).ToHashSet();
        foreach (var failed in log.Entries.Where(e => bad.Contains(e.DeviceId) && e.Outcome == "failed"))
            if (log.Entries.Any(e => e.DeviceId == failed.DeviceId && e.WrittenAt > failed.ReportedAt))
                Fail($"device {failed.DeviceId}'s token failed at {failed.ReportedAt:HH:mm:ss}, yet entries followed");
        if (backend is not null && !log.Entries.Any(e => bad.Contains(e.DeviceId) && e.Outcome == "failed"))
            Fail("Ann Lowe's old iPad: no send of hers failed (its token is one the last mile no longer knows)");
        var byKind = log.Notifications.GroupBy(n => n.Kind).Select(k =>
            $"{k.Key} {k.Count()} ({string.Join(", ", k.GroupBy(n => n.State).Select(g => $"{g.Count()} {g.Key}"))})");
        Console.Out.WriteLine($"dispatches: {log.Notifications.Length} notifications: {string.Join("; ", byKind)}. " +
                              $"{log.Entries.Length} entries over {log.Entries.Select(e => e.Shard).Distinct().Count()} shards, " +
                              $"{log.Entries.Count(e => e.Outcome == "handed_off")} handed off, {log.Entries.Count(e => e.Outcome == "failed")} failed, " +
                              $"{log.Entries.Count(e => e.Outcome == "recorded")} recorded, {log.Entries.Count(e => e.Outcome == "")} pending; " +
                              $"each device's in order ({log.Entries.Where(e => e.Dispatcher.Length > 0).Select(e => e.DeviceId).Distinct().Count()} " +
                              $"devices{(outOfOrder > 0 ? $", {outOfOrder} OUT OF ORDER" : "")})");
    }

    /// <summary>
    ///     The dispatcher's order, checked on its own (<see cref="Dispatcher.Dispatcher.Pick" />, as its workers choose): an
    ///     urgent transactional push ahead of a site's burst of notices; among those as pressing, the sites in turn, so one
    ///     site's burst (fifty notices) doesn't hold the other's (ten) back; within a site, the earliest deadline first.
    /// </summary>
    private void DispatchOrderChecks()
    {
        static (int Shard, long Seq, NotifyRow Row) Entry(long seq, byte site, NotificationClass cls, bool urgent, long deliverBy)
        {
            return (0, seq, new NotifyRow
                { Site = site, Class = cls, Urgent = urgent ? (byte)1 : (byte)0, DeliverByMicros = deliverBy, DeviceId = seq });
        }

        var north = Enumerable.Range(1, 50).Select(i => Entry(i, 1, NotificationClass.Operational, false, 10_000 + i)).ToList();
        var south = Enumerable.Range(51, 10).Select(i => Entry(i, 2, NotificationClass.Operational, false, 20_000 + i)).ToList();
        var called = Entry(99, 2, NotificationClass.Transactional, true, 30_000);
        if (Dispatcher.Dispatcher.Pick([.. north, .. south, called], 0)?.Seq != 99)
            Fail("dispatcher's order: an urgent transactional push isn't first, ahead of a burst of notices");

        List<(int Shard, long Seq, NotifyRow Row)> waiting = [.. north, .. south];
        var (sites, last) = (new List<byte>(), (byte)0);
        while (Dispatcher.Dispatcher.Pick(waiting, last) is { } next)
        {
            waiting.RemoveAll(w => w.Seq == next.Seq); // (by its number: a packed row's default equality reads it unaligned)
            sites.Add(last = next.Row.Site);
        }

        if (!sites.Take(20).Select((s, i) => s == (i % 2 == 0 ? 1 : 2)).All(x => x) || sites.Count != 60)
            Fail($"dispatcher's order: the sites not in turn ({string.Concat(sites.Take(24))}…)");
        var (later, sooner) = (Entry(1, 1, NotificationClass.Transactional, false, 900), Entry(2, 1, NotificationClass.Transactional, false, 100));
        if (Dispatcher.Dispatcher.Pick([later, sooner], 0)?.Seq != 2)
            Fail("dispatcher's order: within a site, not the earliest deadline first");
        Console.Out.WriteLine($"dispatcher's order: an urgent call-in ahead of fifty notices; two sites' notices in turn " +
                              $"({string.Concat(sites.Take(24))}…), the other's ten not held back; within a site, the earliest deadline first");
    }

    /// <summary>
    ///     The practice's feeds require a sign-in, and serve a connection its own person's alone: an anonymous connection
    ///     refused Rosa's (by the router: unauthenticated), Jane's connection refused hers (by the service: forbidden).
    /// </summary>
    private async Task SignInChecksAsync(CancellationToken token)
    {
        var anonymous = new TaplineStreamConnection(channel);
        _ = anonymous.RunAsync(token);
        try
        {
            for (var i = 0; i < 200 && anonymous.Session is null; i++) await Task.Delay(50, token);
            if (anonymous.Session is not { } session)
            {
                Fail("sign-in: an anonymous connection didn't connect");
                return;
            }

            var refused = await Practice(token).SubscribeAsync(session.OutletId, session.Nonce, Rosa, null);
            var forbidden = await StepOneAsync(PracticeSignIn.UserOf(Jane), (p, outlet, nonce) => p.SubscribeAsync(outlet, nonce, Rosa, null), token);
            if (refused.Refused is null) Fail("sign-in: an anonymous connection was given Rosa's feed");
            else if (forbidden.Refused?.Contains("theirs alone") != true)
                Fail($"sign-in: Jane's connection was given Rosa's feed ({forbidden.Refused ?? "bound"})");
            else
                Console.Out.WriteLine($"sign-in: an anonymous connection refused Rosa's feed ({refused.Refused}); " +
                                      $"Jane's, signed in as her, refused it too ({forbidden.Refused})");
        }
        finally
        {
            anonymous.Drop();
        }
    }

    /// <summary>
    ///     A persona's step 1 (the person in its parameters, and which of their feeds: <paramref name="of" />), asked again
    ///     while the service starts.
    /// </summary>
    private async Task SubscribeAsync(Persona persona, string? of, CancellationToken token)
    {
        var feed = new Feed(persona, of);
        lock (_gate)
        {
            _feeds[feed.Key] = feed;
        }

        var answer = await StepOneAsync(PracticeSignIn.UserOf(persona.Id), (p, outlet, nonce) => p.SubscribeAsync(outlet, nonce, persona.Id, of), token);
        if (answer.Refused is { } why)
        {
            Fail($"{persona.Name}'s feed refused: {why} (correlation {answer.CorrelationId})");
            feed.First.TrySetResult();
            return;
        }

        lock (_gate)
        {
            Bind(feed, answer);
        }

        var subscription = ConnectionOf(PracticeSignIn.UserOf(persona.Id)).Client.Subscribe(Binding(answer), Handler(feed));
        lock (_gate)
        {
            feed.Subscription = subscription;
        }
    }

    /// <summary>
    ///     Step 1 on <paramref name="user" />'s session — signed in as them first — asked again while the service starts (or
    ///     moves), until it's answered: bound, or refused.
    /// </summary>
    private async Task<FeedBinding> StepOneAsync(string user, Func<IPracticeFeed, int, uint, UnaryResult<FeedBinding>> ask,
        CancellationToken token)
    {
        var connection = ConnectionOf(user);
        for (var delay = TimeSpan.FromMilliseconds(250);; delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.TicksPerSecond * 2)))
        {
            if (connection.Session is not { } session || !await connection.Client.SignedInAsync(user, token))
            {
                await Task.Delay(delay, token);
                continue;
            }

            var answer = await ask(Practice(token), session.OutletId, session.Nonce);
            if (!answer.Unavailable && !answer.UnknownSession) return answer;
            await Task.Delay(delay, token);
        }
    }

    private static TapBinding Binding(FeedBinding answer)
    {
        return new TapBinding(answer.TapId, answer.Topic, null, answer.Service, answer.NodeId, answer.Partition);
    }

    private static void Bind(Feed feed, FeedBinding answer)
    {
        (feed.Topic, feed.Partition, feed.TapId, feed.NodeId) = (answer.Topic, answer.Partition, answer.TapId, answer.NodeId);
    }

    /// <summary>
    ///     A feed's handler: each record applied and checked. A subscription it has since replaced (moving off a draining
    ///     node) streams on until the new one does: what it sends then, and its end, are the move's, not the feed's.
    /// </summary>
    private SubscriptionHandler Handler(Feed feed)
    {
        return new SubscriptionHandler
        {
            Table = (subscription, table, batch) =>
            {
                lock (_gate)
                {
                    if (subscription.TapId == feed.TapId) Applied(feed, table, batch.IsSnapshot);
                }
            },
            Error = (subscription, error) =>
            {
                lock (_gate)
                {
                    if (subscription.TapId != feed.TapId) return;
                    if (_killed) feed.Ended = error; // (the service killed: expected)
                    else Fail($"{feed.Name}'s feed ended: {error.Code}");
                    if (_drainingNode is not null) _endedMoving++;
                }
            }
        };
    }

    /// <summary>
    ///     A node draining (blue, for blue/green): each of its feeds and observers moved, make before break — step 1 again
    ///     (the router sends it elsewhere), and the old subscription replaced, let go once the new one streams.
    /// </summary>
    private async Task DrainingAsync(string user, Guid node, TimeSpan grace, Guid correlation, CancellationToken token)
    {
        List<Feed> feeds;
        List<int> shards;
        lock (_gate)
        {
            _drainingNode = node;
            feeds = [.. _feeds.Values.Where(f => f.NodeId == node && PracticeSignIn.UserOf(f.Persona.Id) == user)];
            shards = user == Observer ? [.. _observers.Where(o => o.Value.NodeId == node).Select(o => o.Key)] : [];
        }

        if (Told("draining", node, correlation))
            Note($"node {node} draining ({grace.TotalSeconds:F0} s grace, correlation {correlation}): each connection's feeds and observers moving");
        try // (all at once: each must move within the grace)
        {
            await Task.WhenAll([.. feeds.Select(f => MoveAsync(f, token)), .. shards.Select(shard => ObserveAsync(shard, token, true))]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task MoveAsync(Feed feed, CancellationToken token)
    {
        var answer = await StepOneAsync(PracticeSignIn.UserOf(feed.Persona.Id),
            (p, outlet, nonce) => p.SubscribeAsync(outlet, nonce, feed.Persona.Id, feed.Of), token);
        if (answer.Refused is { } why)
        {
            Fail($"{feed.Name}'s feed, moving: refused: {why}");
            return;
        }

        uint old;
        lock (_gate)
        {
            old = feed.TapId;
            Bind(feed, answer);
            feed.Moves++;
        }

        var subscription = ConnectionOf(PracticeSignIn.UserOf(feed.Persona.Id)).Client.Replace(old, Binding(answer), Handler(feed));
        lock (_gate)
        {
            feed.Subscription = subscription;
        }
    }

    /// <summary>A feed's record applied (under the client's lock, then this one's): its rows read again, and checked.</summary>
    private void Applied(Feed feed, TableReplica table, bool snapshot)
    {
        _batches++;
        feed.Batches++;
        if (snapshot) feed.Snapshots++;
        feed.Rows.Clear();
        feed.Sections.Clear();
        feed.Tasks.Clear();
        feed.Access.Clear();
        feed.Outbox.Clear();
        feed.Screen.Clear();
        feed.Stats.Clear();
        feed.Status = null;
        foreach (var row in table)
        {
            var key = row.KeyAs<FeedKey>();
            if (feed.IsStats) feed.Stats[key] = row.RowAs<StatsRow>();
            else if (feed.IsScreen) feed.Screen[key.Seq] = row.RowAs<ScreenRow>();
            else if (key.Section == FeedKey.Clinical) feed.Sections[key.Seq] = row.RowAs<FeedRow>();
            else if (key.Section == FeedKey.Tasks) feed.Tasks[key.Seq] = row.RowAs<FeedRow>();
            else if (key.Section == FeedKey.Status) feed.Status = row.RowAs<FeedRow>();
            else if (key.Section == FeedKey.Access) feed.Access[key.Seq] = row.RowAs<FeedRow>();
            else if (key.Section == FeedKey.Outbox) feed.Outbox[key.Seq] = row.RowAs<FeedRow>();
            else
            {
                feed.Rows[key.Seq] = row.RowAs<FeedRow>();
                feed.Ever.Add(key.Seq); // (an activity this feed has held: the access log must name its person)
            }
        }

        (feed.Version, feed.Checksum) = (table.Version, table.Checksum);
        if (!table.Verified) Fail($"{feed.Name}'s replica didn't check out at version {table.Version}");
        Check(feed);
        feed.First.TrySetResult();
    }

    /// <summary>What a persona may be shown, by role: every row of their feed checked.</summary>
    private void Check(Feed feed)
    {
        var p = feed.Persona;
        var site = p.Site is "North" ? (byte)1 : p.Site is "South" ? (byte)2 : (byte)0;
        if (feed.IsScreen)
        {
            foreach (var (seq, row) in feed.Screen)
                if (row.Kind is not (ActivityKind.Arrived or ActivityKind.Called))
                    Fail($"{feed.Name} shows activity {seq}, a {row.Kind}: only tickets waiting or called");
            if (feed.Screen.Values.GroupBy(r => r.Ticket).FirstOrDefault(g => g.Count() > 1) is { } twice)
                Fail($"{feed.Name} shows ticket {twice.Key} twice: a visit's latest step only");
            return;
        }

        if (feed.IsStats) // (counts and means only: by the hour, and by reason, each count at least the floor, or withheld)
        {
            foreach (var (key, row) in feed.Stats)
            {
                var wrong = key.Section switch
                {
                    FeedKey.Hours when row.Hourly != 1 || key != FeedKey.Hour(row.AtMicros, row.Site) => "an hour's row out of place",
                    FeedKey.Hours when row.Arrivals < 0 || row.Called < 0 || row.Seen < 0 ||
                                       (row.Called == 0) != (row.MeanWaitSeconds < 0) => "an hour's figures that don't add up",
                    FeedKey.Reasons when row.Hourly != 0 || key.Seq != row.Site => "a count by reason out of place",
                    FeedKey.Hours or FeedKey.Reasons when row.Site is not (1 or 2) => $"site {row.Site}, not the manager's",
                    FeedKey.Hours or FeedKey.Reasons => null,
                    _ => $"a row of part {key.Section}: only figures by the hour and by reason"
                };
                for (var i = 0; wrong is null && key.Section == FeedKey.Reasons && i < StatsRow.ReasonCount; i++)
                    wrong = row.ReasonsDisclosed[i] switch
                    {
                        Disclosed.Aggregate when row.ByReason[i] < Service.PracticeStore.StatsFloor =>
                            $"{StatsRow.Reasons[i]} counted {row.ByReason[i]}, under the floor",
                        Disclosed.Redacted when row.ByReason[i] != 0 => $"{StatsRow.Reasons[i]} withheld, yet counted {row.ByReason[i]}",
                        Disclosed.Aggregate or Disclosed.Redacted => null,
                        var other => $"{StatsRow.Reasons[i]} disclosed {other}: a count is an aggregate, or withheld"
                    };
                if (wrong is not null) Fail($"{feed.Name} was shown {wrong}: {row}");
            }

            return;
        }

        foreach (var (seq, row) in feed.Rows)
        {
            string? wrong = p.Role switch
            {
                "reception" when row.Site != site => $"a row of site {row.Site}",
                "reception" when row.PatientDisclosed != Disclosed.Full || row.PatientName.ToString().Length == 0 =>
                    "the patient withheld (reception sees who)",
                "reception" when row.DetailDisclosed != Disclosed.Full => "the schedule's note withheld",
                "manager" when row.PatientDisclosed != Disclosed.Full || row.PatientName.ToString().Length == 0 =>
                    "the patient withheld (the manager sees who's booked)",
                "manager" when row.Kind is not (ActivityKind.Booked or ActivityKind.Reminded or ActivityKind.Arrived or
                    ActivityKind.Called or ActivityKind.Seen) => $"a {row.Kind}: the manager's schedule is appointments",
                "manager" when row.DetailDisclosed == Disclosed.Full && row.Detail.ToString().Length > 0 =>
                    "the schedule's note (the manager isn't granted it)",
                "reception" or "manager" when row.Basis != DisclosureBasis.Scheduling => $"the basis {row.Basis}, not scheduling",
                "clinician" when row.Site != site && row.Clinician != p.Id => $"a row of site {row.Site}, not their visit",
                "clinician" when row.PatientDisclosed != Disclosed.Full => "the patient withheld (GPs see who)",
                "clinician" when row.DetailDisclosed != Disclosed.Full => "the schedule's note withheld",
                "clinician" when row.Clinician == p.Id && row.Basis != DisclosureBasis.Care => $"their own visit, the basis {row.Basis}",
                "clinician" when row.Clinician != p.Id && row.Basis is not (DisclosureBasis.Scheduling or DisclosureBasis.Emergency) =>
                    $"another GP's visit, the basis {row.Basis}",
                "patient" when row.Patient != p.Id => $"patient {row.Patient}'s row",
                "patient" when row.PatientDisclosed != Disclosed.Full || row.DetailDisclosed != Disclosed.Full =>
                    "their own row, withheld",
                "patient" when row.Basis != DisclosureBasis.OwnRecord => $"their own row, the basis {row.Basis}",
                "carer" when !_caredFor.Contains(row.Patient) => $"patient {row.Patient}'s row, whom they don't care for",
                "carer" when row.PatientDisclosed != Disclosed.Full || row.DetailDisclosed != Disclosed.Full =>
                    "their patient's row, withheld",
                "carer" when row.Patient != p.Id && row.Basis != DisclosureBasis.Carer => $"their patient's row, the basis {row.Basis}",
                _ => null
            };
            if (wrong is not null) Fail($"{feed.Name} was shown activity {seq} with {wrong}: {row}");
        }

        // Acknowledgements: who acknowledged or read is the practice's staff names, which a patient never reads.
        foreach (var (seq, row) in feed.Rows)
            if (p.Role is "patient" or "carer" && (row.AckedMicros > 0 || row.ReadMicros > 0) &&
                (row.StaffDisclosed == Disclosed.Full || row.AckedBy.ToString().Length > 0 || row.ReadBy.ToString().Length > 0))
                Fail($"{feed.Name} was shown who at the practice acknowledged or read activity {seq}: {row}");

        // Their own: tasks (reception's, the messages each has yet to read) and the status row (the badge).
        foreach (var (seq, task) in feed.Tasks)
            if (p.Role == "reception" ? task.Kind is not (ActivityKind.Messaged or ActivityKind.Replied) // (to read; undelivered)
                    : p.Role != "clinician" || task.Kind != ActivityKind.Result)
                Fail($"{feed.Name} has a task ({seq}) that isn't theirs: {task}");
        foreach (var (seq, access) in feed.Access)
            if (p.Role switch
                {
                    "patient" => access.Count != 2 && access.Patient != p.Id, // (a consent of theirs, or a carer of theirs)
                    "carer" => access.Count != 2 || !_caredFor.Contains(access.Patient),
                    "clinician" => access.Reason.ToString() != "Emergency access",
                    _ => true
                })
                Fail($"{feed.Name} has access row {seq} that isn't theirs: {access}");
        if (feed.Status is not { } status) Fail($"{feed.Name}'s feed has no status row");
        else if (p.Role is "patient" or "carer"
                     ? status.Count != feed.Rows.Values.Count(r => r is { Kind: ActivityKind.Reminded, AckedMicros: 0 })
                     : status.Count != feed.Tasks.Count)
            Fail($"{feed.Name}'s badge says {status.Count}, against {(p.Role == "patient" ? "their reminders to acknowledge" : "their tasks")}");

        foreach (var (seq, section) in feed.Sections)
        {
            string? wrong = p.Role switch
            {
                "reception" or "manager" => "a clinical section (the policy shows them none)",
                "clinician" when section.Clinician != p.Id && !_shared.Contains((section.Patient, p.Id, section.Problem)) &&
                                 !_emergency.Contains((p.Id, section.Patient)) &&
                                 Service.People.Find(section.Patient)?.Gp != p.Id =>
                    "a section neither theirs (attended) nor shared with them, nor theirs in an emergency",
                "patient" when section.Patient != p.Id => $"patient {section.Patient}'s section",
                "patient" when section is { Kind: ActivityKind.Result, Released: 0 } &&
                               (section.DetailDisclosed != Disclosed.Redacted || section.Detail.ToString() != "Awaiting review by your GP") =>
                    "a result's finding before their GP released it",
                "carer" when !_caredFor.Contains(section.Patient) => $"patient {section.Patient}'s section, whom they don't care for",
                "carer" when section.Released == 0 => "a result not yet released",
                "carer" when Sensitive.Contains(section.Reason.ToString()) => "a sensitive section (hidden from their scope)",
                "clinician" or "carer" when section.ReasonDisclosed != Disclosed.Full || section.DetailDisclosed != Disclosed.Full =>
                    "a section they read, withheld",
                "patient" when section.ReasonDisclosed != Disclosed.Full ||
                               section is not { Kind: ActivityKind.Result, Released: 0 } && section.DetailDisclosed != Disclosed.Full =>
                    "a section of their own, withheld",
                "clinician" when section.Basis is not (DisclosureBasis.Care or DisclosureBasis.Emergency) =>
                    $"a section, the basis {section.Basis}",
                "patient" when section.Patient == p.Id && section.Basis != DisclosureBasis.OwnRecord => $"their own section, the basis {section.Basis}",
                "carer" when section.Patient != p.Id && section.Basis != DisclosureBasis.Carer => $"their patient's section, the basis {section.Basis}",
                _ => null
            };
            if (wrong is not null) Fail($"{feed.Name} was shown section {seq}, {wrong}: {section}");
        }
    }

    /// <summary>
    ///     The demo, checked: reception checks Jane Smith in (South's reception may not, nor may a clinician), her GP calls
    ///     her in (a South clinician may not). Each command's rendezvous checked against the giver's feed as it lands.
    /// </summary>
    private async Task DemoAsync(CancellationToken token)
    {
        var booking = 0L;
        await Until(() => (booking = JaneBooking()) != 0, TimeSpan.FromSeconds(30), "Jane's booking, in Rosa's feed", token);
        Console.Out.WriteLine($"demo: Jane Smith's booking {booking}, as Rosa sees it: " +
                              $"{Locked(() => _feeds[Rosa].Rows[booking].ToString())}");

        // Refused: by who may act, and by what they can see.
        await RefusedAsync(Practice(token).ArriveAsync(Sam, booking, Correlation(), Order($"visit:{booking}")), "can't see",
            "South's reception checking a North patient in");
        await RefusedAsync(Practice(token).ArriveAsync(Patel, booking, Correlation(), Order($"visit:{booking}")), "isn't reception",
            "a clinician checking a patient in");
        await RefusedAsync(Practice(token).CallAsync(Lee, booking, "Room 1", Correlation(), Order($"visit:{booking}")), "can't see",
            "a South clinician calling a North patient in");

        // Jane acknowledges her reminder (sent once she's booked): reception's rows say who, and when.
        await ReminderAsync(booking, token);

        // Rosa checks Jane in: her feed has the arrival, with a ticket, when the id comes back.
        short ticket = 0;
        var hour = FeedKey.Hour(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, 1);
        var counted = Locked(() => _feeds.GetValueOrDefault(StatsOf(Max))?.Stats.GetValueOrDefault(hour).Arrivals ?? 0);
        var arrived = await CommandAsync(Rosa, correlation => Practice(token).ArriveAsync(Rosa, booking, correlation, Order($"visit:{booking}")),
            () => _feeds[Rosa].Rows.Values.FirstOrDefault(r => r.Booking == booking && r.Kind == ActivityKind.Arrived) is
                { Ticket: > 0 } row
                ? Ok(ticket = row.Ticket)
                : "Rosa's feed hasn't the arrival", "Rosa checks Jane in", token);
        if (arrived)
        {
            await Until(() => _feeds[Jane].Rows.Values.Any(r => r.Booking == booking && r.Kind == ActivityKind.Arrived),
                TimeSpan.FromSeconds(5), "Jane's own feed: she's checked in", token);
            await Until(() => _feeds[Priya].Rows.Values.Any(r => r.Booking == booking && r.Kind == ActivityKind.Arrived),
                TimeSpan.FromSeconds(5), "Priya's feed (Rosa's class): the arrival", token);
            await Until(() => _feeds[ScreenNorth].Screen.Values.Any(r => r.Ticket == ticket && r.Kind == ActivityKind.Arrived),
                TimeSpan.FromSeconds(5), $"the North screen: ticket {ticket}, waiting", token);
            Console.Out.WriteLine($"demo: the screen says \"{Locked(() => _feeds[ScreenNorth].Screen.Values.First(r => r.Ticket == ticket).ToString())}\"");
            // Max's figures follow: North's hour counts the check-in, nobody named.
            await Until(() => _feeds[StatsOf(Max)].Stats.GetValueOrDefault(hour).Arrivals > counted, TimeSpan.FromSeconds(5),
                $"Max's figures: North's hour counts the check-in (from {counted})", token);
            Console.Out.WriteLine($"demo: Max's figures: {Locked(() => _feeds[StatsOf(Max)].Stats.GetValueOrDefault(hour).ToString())}");
        }

        // Dr Patel calls her in: his feed has the call, and what she came for in full (she's his patient: her registration
        // shares everything with him).
        var called = await CommandAsync(Patel,
            correlation => Practice(token).CallAsync(Patel, booking, "Room 4", correlation, Order($"visit:{booking}")),
            () => _feeds[Patel].Rows.Values.FirstOrDefault(r => r.Booking == booking && r.Kind == ActivityKind.Called) is
                { Kind: ActivityKind.Called } row
                ? row.Room.ToString() != "Room 4" ? $"called to {row.Room}"
                : !_feeds[Patel].Sections.Values.Any(s => s.Booking == booking && s.ReasonDisclosed == Disclosed.Full)
                    ? "his own patient's section missing, or withheld"
                    : null
                : "Dr Patel's feed hasn't the call", "Dr Patel calls Jane in", token);
        if (called)
        {
            await Until(() => _feeds[ScreenNorth].Screen.Values.Any(r => r.Ticket == ticket && r.Kind == ActivityKind.Called),
                TimeSpan.FromSeconds(5), $"the North screen: ticket {ticket}, called", token);
            await Until(() => _feeds[Jane].Rows.Values.Any(r => r.Booking == booking && r.Kind == ActivityKind.Called),
                TimeSpan.FromSeconds(5), "Jane's own feed: she's called in", token);
            await Until(() => _feeds[Okafor].Rows.Values.Any(r => r.Booking == booking && r.Kind == ActivityKind.Called),
                TimeSpan.FromSeconds(5), "Dr Okafor's feed: the call", token);
            await CalledInAsync(booking, token);
            await ResultAsync(token);
            await MessageAsync(token);
            await SharingAsync(booking, token);
            await BreakGlassAsync(booking, token);
            await RevokeAsync(token);
            await AwayAsync(token);
            await NotesAsync(token);
            await NoticesAsync(token);
            await LagAsync(token);
            await OrderAsync(token);
            Console.Out.WriteLine($"demo: the screen says \"{Locked(() => _feeds[ScreenNorth].Screen.Values.First(r => r.Ticket == ticket).ToString())}\"; " +
                                  $"Dr Patel reads \"{Locked(() => _feeds[Patel].Sections.Values.First(s => s.Booking == booking).Reason.ToString())}\"; " +
                                  $"Dr Okafor sees the call and {Locked(() => _feeds[Okafor].Sections.Values.Count(s => s.Booking == booking))} " +
                                  "sections of it: Jane hasn't shared it with him");
            if (Locked(() => _feeds[Okafor].Sections.Values.Any(s => s.Booking == booking)))
                Fail("Dr Okafor reads Jane's section, which she hasn't shared with him");
        }
    }

    /// <summary>
    ///     The call's notification, checked: it's urgent, and Jane's and Tom's feeds are open, so it waits for the app. Jane's
    ///     page reports she's seen the call: her devices get no-ops ("seen in the app"). Tom's doesn't: once the grace is
    ///     over, his phone gets a send. The dispatcher was killed as the call was made, so the send waits in its topic;
    ///     started again, it's sent the topic's whole state, and hands the send off.
    /// </summary>
    private async Task CalledInAsync(long booking, CancellationToken token)
    {
        if (backend is null) return; // (an app already running: its dispatcher isn't this client's to stop)
        await backend.KillDispatcherAsync();
        var call = Locked(() => _feeds[Jane].Rows.FirstOrDefault(r => r.Value.Booking == booking && r.Value.Kind == ActivityKind.Called).Key);
        var (janes, toms) = (Service.People.Devices.Where(d => d.PersonId == Jane).Select(d => d.Id).ToHashSet(),
            Service.People.Devices.Where(d => d.PersonId == Tom).Select(d => d.Id).ToHashSet());
        var seen = await Practice(token).SeenAsync(Jane, [call], Correlation());
        if (!seen.Done) Fail($"Jane's page reporting she's seen her call: refused ({seen.Refused})");
        await Until(() => _notify[1].Values.Count(e => janes.Contains(e.DeviceId) && e.Reason.ToString().StartsWith("seen in the app")) == janes.Count,
            TimeSpan.FromSeconds(5), "Jane's devices: no-ops for the call, seen in the app", token);
        var notification = Locked(() => _notify[1].Values.FirstOrDefault(e => janes.Contains(e.DeviceId) &&
                                                                             e.Reason.ToString().StartsWith("seen in the app")).NotificationId.ToString());
        await Until(() => _notify[1].Values.Any(e => toms.Contains(e.DeviceId) && e.NotificationId.ToString() == notification &&
                                                      e.Action == DispatchAction.Send),
            TimeSpan.FromSeconds(15), "Tom's phone: a send of the call, once the grace is over", token);
        if (Locked(() => _notify[1].Values.Any(e => janes.Contains(e.DeviceId) && e.NotificationId.ToString() == notification &&
                                                    e.Action == DispatchAction.Send)))
            Fail("Jane's devices got a send of the call she'd seen in the app");
        Console.Out.WriteLine($"demo: called in: Jane saw it in the app (no-ops for her {janes.Count} devices); Tom's phone waits: " +
                              $"{Locked(() => _notify[1].Values.First(e => toms.Contains(e.DeviceId) && e.NotificationId.ToString() == notification).ToString())}");
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        await backend.StartDispatcherAsync(token);
        await Until(() => !_notify[1].Values.Any(e => e.NotificationId.ToString() == notification), TimeSpan.FromSeconds(15),
            "the dispatcher, started again: the call's entries reported (handed off, recorded) and gone", token);
        Console.Out.WriteLine("demo: the dispatcher, started again, handed Tom's send off and recorded Jane's no-ops: nothing lost");
    }

    /// <summary>
    ///     A full note, checked: the visit Dr Okafor attended (primed), its diabetes note longer than a row's 120 bytes,
    ///     read whole by him, by Dr Patel (her registration shares everything) and by Jane; refused to reception and to
    ///     South's GP, who can't see the section; the manager reads the section, but its note redacted.
    /// </summary>
    private async Task NotesAsync(CancellationToken token)
    {
        var (seq, row) = Locked(() => _feeds[Okafor].Sections.FirstOrDefault(s => s.Value is { Patient: Jane, Clinician: Okafor, DetailCut: 1 }));
        if (seq == 0)
        {
            Fail("Dr Okafor's feed: no note of Jane's cut at 120 bytes (the visit he attended)");
            return;
        }

        foreach (var (who, name) in new[] { (Okafor, "Dr Okafor"), (Patel, "Dr Patel"), (Jane, "Jane") })
        {
            var note = await Practice(token).NoteAsync(who, seq);
            if (note.Refused is { } why) Fail($"{name}'s full note of section {seq}: refused ({why})");
            else if (note.Disclosed != "FULL" || System.Text.Encoding.UTF8.GetByteCount(note.Text) <= Text120.Bytes ||
                     !note.Text.StartsWith(row.Detail.ToString(), StringComparison.Ordinal))
                Fail($"{name}'s full note of section {seq}: {note.Disclosed}, {note.Text.Length} chars");
        }

        foreach (var (who, name) in new[] { (Rosa, "reception"), (Lee, "South's GP") })
            if ((await Practice(token).NoteAsync(who, seq)).Refused is null)
                Fail($"{name} read the full note of Jane's section {seq}");
        if (await Practice(token).NoteAsync(Max, seq) is { Refused: null, Disclosed: "FULL" })
            Fail($"the manager read the note of Jane's section {seq} in full");
        Console.Out.WriteLine($"demo: Jane's note, {(await Practice(token).NoteAsync(Okafor, seq)).Text.Length} characters, read whole " +
                              "by Dr Okafor, Dr Patel and Jane; refused to reception and South's GP; redacted for the manager");
    }

    /// <summary>
    ///     The access log (the service's <c>/access</c>, as its auditor reads it), at the end: who was shown Jane's check-in
    ///     names exactly the people whose feeds held it, each with its own topic. The audit page's read says the same as the
    ///     practice's privacy officer (the auditor role), and nothing as reception; the log's own database has written all
    ///     it was given.
    /// </summary>
    private async Task AccessChecksAsync()
    {
        if (service is null) return;
        var arrival = Locked(() => _feeds[Rosa].Ever.Where(seq => _feeds[Jane].Ever.Contains(seq) &&
                                                                  _feeds[Rosa].Rows.GetValueOrDefault(seq) is { Kind: ActivityKind.Arrived, Patient: Jane })
            .DefaultIfEmpty().Max());
        if (arrival == 0) return; // (no check-in of hers: the demo didn't get that far)
        await Task.Delay(TimeSpan.FromSeconds(6)); // (the log is written with the next refresh: the clock's, at the latest)
        ShownTo[]? shown;
        try
        {
            shown = await http.GetFromJsonAsync<ShownTo[]>(new Uri(service, $"access?activity={arrival}"),
                System.Text.Json.JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            Fail($"the service's access log: {e.Message}");
            return;
        }

        var logged = (shown ?? []).Select(s => s.Person).ToHashSet();
        var held = Locked(() => _feeds.Values.Where(f => !f.IsScreen && !f.IsStats && f.Ever.Contains(arrival)).Select(f => f.Persona.Id)
            .ToHashSet());
        if (!logged.SetEquals(held))
            Fail($"the access log of Jane's check-in (activity {arrival}) names {string.Join(", ", logged.Order())}, " +
                 $"but it was in the feeds of {string.Join(", ", held.Order())}");
        var attending = Locked(() => _feeds[Rosa].Rows[arrival].Clinician);
        foreach (var entry in shown ?? [])
        {
            if (entry.Topic != $"home:{entry.Person}")
                Fail($"the access log says {entry.Person} was shown Jane's check-in on {entry.Topic}, not their own topic");
            var basis = Service.People.Find(entry.Person)?.Role switch // (why each was shown it: the need to know, as derived)
            {
                Service.Role.Reception or Service.Role.Manager => DisclosureBasis.Scheduling,
                Service.Role.Clinician => entry.Person == attending ? DisclosureBasis.Care : DisclosureBasis.Scheduling,
                Service.Role.Patient => DisclosureBasis.OwnRecord,
                Service.Role.Carer => DisclosureBasis.Carer,
                _ => DisclosureBasis.None
            };
            if (entry.Basis != basis && !(basis == DisclosureBasis.Scheduling && entry.Basis == DisclosureBasis.Emergency))
                Fail($"the access log says {entry.Person} was shown Jane's check-in for {entry.Basis.Label()}, not {basis.Label()}");
        }
        Console.Out.WriteLine($"access: Jane's check-in (activity {arrival}) was shown to " +
                              string.Join(", ", (shown ?? []).Select(s => $"{Service.People.Find(s.Person)?.Name} ({s.Basis.Label()}, {s.Posts} posts, from {s.FirstShown:HH:mm:ss})")));

        var auditor = Service.People.Auditors[0];
        var audited = await Practice(CancellationToken.None).AuditAsync(auditor.Id, 0, arrival);
        var newest = await Practice(CancellationToken.None).AuditAsync(auditor.Id, 0, 0);
        var reception = await Practice(CancellationToken.None).AuditAsync(Rosa, 0, arrival);
        if (audited.Refused is { } refused) Fail($"{auditor.Name}'s audit of Jane's check-in: refused ({refused})");
        else if (!audited.Rows.Select(r => r.Person).ToHashSet().SetEquals(logged))
            Fail($"{auditor.Name}'s audit of Jane's check-in names {string.Join(", ", audited.Rows.Select(r => r.Person).Order())}, " +
                 $"the access log {string.Join(", ", logged.Order())}");
        foreach (var row in audited.Rows)
            if ((shown ?? []).FirstOrDefault(e => e.Person == row.Person) is { } entry && entry.Basis != row.Basis)
                Fail($"{auditor.Name}'s audit says {row.Person} was shown Jane's check-in for {row.Basis.Label()}, the log {entry.Basis.Label()}");
        if (newest.Rows.Length == 0) Fail($"the access log's newest entries: none, as {auditor.Name} reads them");
        if (reception.Rows.Length > 0) Fail($"Rosa Diaz (reception) read {reception.Rows.Length} lines of the access log");
        var store = (await http.GetFromJsonAsync<LogStatus>(new Uri(service, "log"), System.Text.Json.JsonSerializerOptions.Web))?.AccessLog;
        if (store is { Failures: > 0 }) Fail($"the access log's database didn't take {store.Failures} entries");
        Console.Out.WriteLine($"audit: as {auditor.Name} (auditor, for {audited.Purpose}), the same {audited.Rows.Length} people, " +
                              $"and the log's newest {newest.Rows.Length} entries; as Rosa Diaz (reception), {reception.Rows.Length} lines; " +
                              $"its database {store?.Written} entries written of {store?.Posted} posted, the loop waiting {store?.Waits} times");
    }

    /// <summary>
    ///     Each persona's explanation (ChalkQL's, of their statements: nothing run), at the end: there for every one; the
    ///     manager's reason aggregate only; reception's statements reading no section; and where a statement's reason is
    ///     shown in full, every section of theirs reads it so.
    /// </summary>
    private async Task ExplainChecksAsync()
    {
        List<string> lines = [];
        foreach (var feed in Locked(() => _feeds.Values.Where(f => !f.IsStats).ToList())) // (a person's: their statements, all their feeds')
        {
            Explanation explained;
            try
            {
                explained = await Practice(CancellationToken.None).ExplainAsync(feed.Persona.Id);
            }
            catch (Exception e) when (e is Grpc.Core.RpcException)
            {
                Fail($"{feed.Name}'s explanation: {e.Message}");
                continue;
            }

            if (explained.Refused is { } why)
            {
                Fail($"{feed.Name}'s explanation: refused ({why})");
                continue;
            }

            var columns = explained.Statements.SelectMany(st => st.Tables).SelectMany(t => t.Columns.Select(c => (t.Table, c))).ToList();
            var reason = columns.Where(c => c.Table == "sections" && c.c.Column == "reason").Select(c => c.c.Disclosure).Distinct().ToList();
            var wrong = feed.Persona.Role switch
            {
                "manager" when reason is not ["AGGREGATE"] => $"the reason {string.Join(" / ", reason)}, not aggregate only",
                "reception" when explained.Statements.Any(st => st.Tables.Any(t => t.Table == "sections")) => "a statement reading sections",
                _ when reason is ["FULL"] && Locked(() => feed.Sections.Values.Any(s => s.ReasonDisclosed != Disclosed.Full)) =>
                    "a section's reason withheld, though its statement shows every reason in full",
                _ => null
            };
            if (wrong is not null) Fail($"{feed.Name}'s explanation: {wrong}");
            lines.Add($"{feed.Short} {string.Join("; ", explained.Statements.Select(st => $"{st.Name}: {string.Join(", ", st.Tables.Select(t => t.Table))}"))}");
        }

        Console.Out.WriteLine($"explained: {lines.Count} personas' statements, as ChalkQL folds their policy — {string.Join(" | ", lines)}");
    }

    /// <summary>
    ///     Jane away, checked: her page closed (her feed dropped), Rosa replies to her message. It's urgent, and her feed
    ///     isn't open, so each of her devices gets a send at once; the dispatcher hands both off, and Rosa's own row of the
    ///     reply says "Delivered to 2 devices". Jane's page open again, the reply's in her feed.
    /// </summary>
    private async Task AwayAsync(CancellationToken token)
    {
        var message = Locked(() => _feeds[Rosa].Rows.Where(r => r.Value is { Kind: ActivityKind.Messaged, Patient: Jane })
            .Select(r => r.Key).DefaultIfEmpty().Max());
        if (message == 0 || backend is null) return; // (no message of hers, or no dispatcher of this client's)
        var jane = Locked(() => _feeds[Jane]);
        ConnectionOf(PracticeSignIn.UserOf(Jane)).Client.Drop(jane.TapId);
        await Task.Delay(TimeSpan.FromSeconds(1.5), token); // (her topic let go: nobody's watching it)
        long reply = 0;
        var replied = await CommandAsync(Rosa,
            correlation => Practice(token).ReplyAsync(Rosa, message, "Yes: Thursday at 10:20 is free, see you then.", correlation,
                Order($"thread:{Jane}")),
            () => _feeds[Rosa].Rows.Values.Any(r => r is { Kind: ActivityKind.Replied, Booking: var thread } && thread == message &&
                                                   (reply = _feeds[Rosa].Rows.First(x => x.Value.Kind == ActivityKind.Replied && x.Value.Booking == message).Key) > 0)
                ? null
                : "Rosa's feed hasn't the reply", "Rosa replies to Jane's message, Jane away", token);
        if (!replied) return;
        await Until(() => _feeds[Rosa].Outbox.GetValueOrDefault(reply) is { Count: 2 }, TimeSpan.FromSeconds(15),
            "Rosa's row of her reply: delivered to Jane's 2 devices", token);
        Console.Out.WriteLine($"demo: Jane away: Rosa's reply \"{Locked(() => _feeds[Rosa].Outbox.GetValueOrDefault(reply).Reason.ToString())}\"");
        if (Locked(() => _feeds[Priya].Outbox.ContainsKey(reply))) Fail("Priya's feed has Rosa's reply's delivery: it's Rosa's");
        await SubscribeAsync(jane.Persona, null, token);
        await Until(() => _feeds[Jane].Rows.ContainsKey(reply), TimeSpan.FromSeconds(10), "Jane's page, open again: the reply", token);
    }

    /// <summary>
    ///     Notices, checked: Max posts three to North's patients (South's manager he isn't, so not there: refused). The
    ///     release job gives each patient their own notification of each: the urgent one a push of its own; the opening
    ///     hours (operational) and flu vaccinations (informational) one push, the hours first, flu bundled into it, counted
    ///     once; flu to nobody who opted out of news.
    /// </summary>
    private async Task NoticesAsync(CancellationToken token)
    {
        if (service is null) return; // (the dispatch log is the service's to tell)
        await RefusedAsync(Practice(token).NoticeAsync(Rosa, 1, "phones", Correlation()), "isn't manager", "reception posting a notice");
        foreach (var notice in new[] { "phones", "hours", "flu" })
            await CommandAsync(Max, correlation => Practice(token).NoticeAsync(Max, 1, notice, correlation), () => null,
                $"Max posts the \"{notice}\" notice to North's patients", token);
        DispatchLog? log = null;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            log = await http.GetFromJsonAsync<DispatchLog>(new Uri(service, "dispatches"), System.Text.Json.JsonSerializerOptions.Web, token);
            if (log is not null && log.Notices.Length >= 3 &&
                log.Notices.All(n => log.Notifications.Any(m => m.NoticeId == n.Id))) break;
            await Task.Delay(500, token);
        }

        if (log is null || log.Notices.Length < 3 || !log.Notices.All(n => log.Notifications.Any(m => m.NoticeId == n.Id)))
        {
            Fail("not within 20 s: the notices released to North's patients");
            return;
        }

        var notices = log.Notices.ToDictionary(n => n.Id);
        var kind = (Notification n) => notices[n.NoticeId].Kind;
        var byPatient = log.Notifications.Where(n => n.Kind == "notice").GroupBy(n => n.PatientId).ToList();
        foreach (var patient in byPatient)
        {
            var person = Service.People.Find(patient.Key)!;
            var mine = patient.ToDictionary(kind);
            var wrong = mine.Count != 3 ? $"{mine.Count} of the 3 notices"
                : person.OptedOutOfNews && mine["flu"].State != "opted_out" ? "flu, though they opted out of news"
                : !person.OptedOutOfNews && mine["flu"].State != "bundled" && mine["hours"].State is not "undeliverable"
                    ? $"flu {mine["flu"].State}, not bundled into the hours' push"
                : mine["hours"].State is "bundled" or "opted_out" ? $"the opening hours {mine["hours"].State}: ranked after flu"
                : mine["phones"].State is "bundled" ? "the urgent notice bundled into a normal push"
                : null;
            if (wrong is not null) Fail($"{person.Name}'s notices: {wrong}");
        }

        var pushed = byPatient.Count(p => p.Any(n => n.State is "ready" or "handed_off" && notices[n.NoticeId].Kind == "hours"));
        Console.Out.WriteLine($"demo: notices released to {byPatient.Count} of North's patients: each an urgent push, and " +
                              $"one normal push ({pushed} so far) of the opening hours with flu bundled into it; " +
                              $"{byPatient.Count(p => Service.People.Find(p.Key)!.OptedOutOfNews)} opted out of news");
    }

    /// <summary>
    ///     A site falling behind, checked (fault injection, the service spawned): South's system lags for a few seconds. Its
    ///     partition is reported degraded, so South's feeds are held and shown stale while North's stream on; once it's
    ///     over, South's are serving again.
    /// </summary>
    private async Task LagAsync(CancellationToken token)
    {
        if (service is null) return;
        int[] south = [Sam, Lee, 2001, 902], north = [Rosa, Patel, Jane, ScreenNorth];
        var lagged = await http.PostAsync(new Uri(service, "faults/lag?site=2&seconds=8"), null, token);
        if (!lagged.IsSuccessStatusCode)
        {
            Fail($"South's system falling behind: {(int)lagged.StatusCode} {await lagged.Content.ReadAsStringAsync(token)}");
            return;
        }

        // (a subscription's provider state is the client's, under its lock: read outside this one's, which the client's
        // handlers take inside theirs)
        var subscriptions = Locked(() => south.Concat(north).ToDictionary(p => p, p => (_feeds[p].Short, _feeds[p].Subscription)));
        ProviderState? State(int person) => subscriptions[person].Subscription?.Provider;
        async Task<bool> Until(Func<bool> done, TimeSpan within)
        {
            for (var timer = Stopwatch.StartNew(); timer.Elapsed < within; await Task.Delay(50, token))
                if (done()) return true;
            return false;
        }

        if (!await Until(() => south.All(p => State(p) == ProviderState.Degraded), TimeSpan.FromSeconds(10)))
            Fail($"not within 10 s: South's feeds held, shown stale (its partition degraded): {string.Join(", ", south.Select(p => $"{subscriptions[p].Short} {State(p)}"))}");
        if (north.FirstOrDefault(p => State(p) != ProviderState.Serving) is var notServing and > 0)
            Fail($"{subscriptions[notServing].Short}'s feed isn't serving while South lags: {State(notServing)}");
        Console.Out.WriteLine($"demo: South lags: {string.Join(", ", south.Select(p => $"{subscriptions[p].Short} {State(p)}"))}; " +
                              $"North streams on: {string.Join(", ", north.Select(p => $"{subscriptions[p].Short} {State(p)}"))}");
        if (!await Until(() => south.All(p => State(p) == ProviderState.Serving), TimeSpan.FromSeconds(20)))
            Fail("not within 20 s: South caught up, its feeds serving again");
    }

    /// <summary>Jane's booking still to come, as Rosa sees it: booked (and reminded), nothing after.</summary>
    private long JaneBooking()
    {
        return _feeds[Rosa].Rows.Values.Where(r => r.Patient == Jane).GroupBy(r => r.Booking)
            .Where(g => g.All(r => r.Kind is ActivityKind.Booked or ActivityKind.Reminded)).Select(g => g.Key)
            .DefaultIfEmpty().Max();
    }

    /// <summary>
    ///     An acknowledgement, checked: Jane acknowledges her reminder (reception can't for her), and reception's rows say
    ///     who and when, while her own says when, not who (a staff realm).
    /// </summary>
    private async Task ReminderAsync(long booking, CancellationToken token)
    {
        long reminder = 0;
        await Until(() => (reminder = _feeds[Jane].Rows.Where(r => r.Value.Booking == booking && r.Value.Kind == ActivityKind.Reminded)
                .Select(r => r.Key).DefaultIfEmpty().Max()) != 0,
            TimeSpan.FromSeconds(20), "Jane's reminder of her visit, in her feed", token);
        if (reminder == 0) return;
        if (Locked(() => _feeds[Jane].Status?.Count) is not > 0) Fail("Jane's badge doesn't count her reminder");

        await RefusedAsync(Practice(token).AcknowledgeAsync(Rosa, reminder, Correlation()), "isn't patient",
            "reception acknowledging a patient's reminder");
        // Tom, her carer, acknowledges it for her: a carer acts on behalf of the patient they care for.
        var acknowledged = await CommandAsync(Tom,
            correlation => Practice(token).AcknowledgeAsync(Tom, reminder, correlation),
            () => _feeds[Tom].Rows.GetValueOrDefault(reminder) is { AckedMicros: > 0 } row
                ? row.StaffDisclosed == Disclosed.Full ? "his feed names who acknowledged (a staff realm)" : null
                : "Tom's feed hasn't his acknowledgement", "Tom acknowledges Jane's reminder, for her", token);
        if (acknowledged)
        {
            await Until(() => _feeds[Rosa].Rows.GetValueOrDefault(reminder) is { AckedMicros: > 0 } &&
                              _feeds[Jane].Rows.GetValueOrDefault(reminder) is { AckedMicros: > 0 },
                TimeSpan.FromSeconds(5), "Rosa's row of Jane's reminder, and Jane's: acknowledged", token);
            Console.Out.WriteLine($"demo: Rosa's row of the reminder: {Locked(() => _feeds[Rosa].Rows[reminder].ToString())}, for Jane Smith");
            if (Locked(() => _feeds[Rosa].Rows[reminder].AckedBy.ToString()) != "Tom Smith")
                Fail($"Rosa's row of the reminder says it was acknowledged by \"{Locked(() => _feeds[Rosa].Rows[reminder].AckedBy.ToString())}\"");
            await RefusedAsync(Practice(token).AcknowledgeAsync(Jane, reminder, Correlation()), "acknowledged already",
                "Jane acknowledging it again");
        }
    }

    /// <summary>
    ///     A result, checked: Jane's blood test comes back as she's called in, for Dr Patel to review (his task); her feed
    ///     says it's awaiting review, Tom's hasn't it. He releases it: her feed has its finding, and Tom's has it too.
    /// </summary>
    private async Task ResultAsync(CancellationToken token)
    {
        long result = 0;
        await Until(() => (result = _feeds[Patel].Tasks.Where(t => t.Value is { Kind: ActivityKind.Result, Patient: Jane })
                .Select(t => t.Key).DefaultIfEmpty().Max()) != 0,
            TimeSpan.FromSeconds(20), "Jane's result, a task for Dr Patel", token);
        if (result == 0) return;
        await Until(() => _feeds[Jane].Sections.ContainsKey(result), TimeSpan.FromSeconds(5), "Jane's result, in her feed", token);
        Console.Out.WriteLine($"demo: a result comes back: Dr Patel's task {Locked(() => _feeds[Patel].Tasks[result].ToString())}; " +
                              $"Jane's feed: {Locked(() => _feeds[Jane].Sections.GetValueOrDefault(result).ToString())}");
        if (Locked(() => _feeds[Tom].Sections.ContainsKey(result))) Fail("Tom's feed has Jane's result before it's released");
        await RefusedAsync(Practice(token).ReleaseAsync(Okafor, result, Correlation()), "isn't the GP reviewing",
            "a GP not reviewing it releasing Jane's result");
        var released = await CommandAsync(Patel, correlation => Practice(token).ReleaseAsync(Patel, result, correlation),
            () => _feeds[Patel].Tasks.ContainsKey(result) ? "Dr Patel's task to review it is still open" : null,
            "Dr Patel releases Jane's result", token);
        if (!released) return;
        await Until(() => _feeds[Jane].Sections.GetValueOrDefault(result) is { Released: 1, DetailDisclosed: Disclosed.Full } &&
                          _feeds[Tom].Sections.ContainsKey(result),
            TimeSpan.FromSeconds(5), "the result, released: its finding in Jane's feed, and in Tom's", token);
        Console.Out.WriteLine($"demo: released: Jane reads \"{Locked(() => _feeds[Jane].Sections[result].Detail.ToString())}\", and so does Tom");
    }

    /// <summary>A carer's designation revoked, checked: Jane revokes Tom's, and her rows leave his feed within a second.</summary>
    private async Task RevokeAsync(CancellationToken token)
    {
        var revoked = await CommandAsync(Jane, correlation => Practice(token).RevokeCarerAsync(Jane, Tom, correlation),
            () => _feeds[Jane].Access.Values.Any(a => a is { Count: 2, Patient: Tom }) ? "Jane's feed still says Tom cares for her" : null,
            "Jane revokes Tom's designation", token);
        if (!revoked) return;
        await Until(() => !_feeds[Tom].Rows.Values.Any(r => r.Patient == Jane) && !_feeds[Tom].Sections.Values.Any(r => r.Patient == Jane),
            TimeSpan.FromSeconds(2), "Tom's feed: none of Jane's rows", token);
        lock (_gate)
        {
            _caredFor.Remove(Jane);
            _tomRevoked = DateTime.UtcNow;
        }

        Console.Out.WriteLine($"demo: revoked: Tom's feed holds {Locked(() => _feeds[Tom].Rows.Count + _feeds[Tom].Sections.Count)} rows, none of Jane's");
    }

    /// <summary>
    ///     Sharing, checked: Jane shares her tag "My diabetes" with Dr Okafor, and his feed gains her visit's diabetes
    ///     section; she withdraws it, and it leaves his feed, but for the visit he attended (a quarter of an hour before).
    /// </summary>
    private async Task SharingAsync(long booking, CancellationToken token)
    {
        var diabetes = Jane * 100 + Array.IndexOf(Service.Simulator.Problems, "Type 2 diabetes");
        if (Locked(() => _feeds[Okafor].Sections.Values.Any(s => s.Booking == booking)))
            Fail("Dr Okafor reads Jane's visit before she shares it with him");
        var attended = Locked(() => _feeds[Okafor].Sections.Values.Count(s => s.Patient == Jane && s.Clinician == Okafor));
        lock (_gate) _shared.Add((Jane, Okafor, diabetes)); // (allowed from the moment it's sent)
        var sharing = Stopwatch.StartNew();
        var shared = await CommandAsync(Jane, correlation => Practice(token).ShareAsync(Jane, Okafor, "My diabetes", correlation),
            () => _feeds[Jane].Access.Values.Any(a => a.Clinician == Okafor && a.Reason.ToString() == "My diabetes")
                ? null
                : "Jane's feed doesn't say Dr Okafor reads \"My diabetes\"", "Jane shares \"My diabetes\" with Dr Okafor", token);
        if (!shared) return;
        await Until(() => _feeds[Okafor].Sections.Values.Any(s => s.Booking == booking && s.ReasonDisclosed == Disclosed.Full),
            TimeSpan.FromSeconds(5), "Dr Okafor's feed: Jane's diabetes section, shared", token);
        var gained = sharing.Elapsed;
        Console.Out.WriteLine($"demo: Dr Okafor now reads Jane's visit: \"{Locked(() => _feeds[Okafor].Sections.Values.FirstOrDefault(s => s.Booking == booking).Reason.ToString())}\" " +
                              $"(and {attended} section of the visit he attended), in his feed {gained.TotalMilliseconds:F0} ms after she shared it");
        var consent = Locked(() => _feeds[Jane].Access.FirstOrDefault(a => a.Value.Clinician == Okafor && a.Value.Reason.ToString() == "My diabetes").Key);
        var withdrawing = Stopwatch.StartNew();
        var withdrawn = await CommandAsync(Jane, correlation => Practice(token).WithdrawAsync(Jane, consent, correlation),
            () => _feeds[Jane].Access.ContainsKey(consent) ? "Jane's feed still says Dr Okafor reads \"My diabetes\"" : null,
            "Jane withdraws it", token);
        if (!withdrawn) return;
        await Until(() => !_feeds[Okafor].Sections.Values.Any(s => s.Booking == booking),
            TimeSpan.FromSeconds(5), "Dr Okafor's feed: Jane's shared section gone", token);
        var lost = withdrawing.Elapsed;
        lock (_gate) _shared.Remove((Jane, Okafor, diabetes));
        var kept = Locked(() => _feeds[Okafor].Sections.Values.Count(s => s.Patient == Jane && s.Clinician == Okafor));
        Console.Out.WriteLine($"demo: withdrawn: Dr Okafor's feed no longer holds it ({lost.TotalMilliseconds:F0} ms after she withdrew it), " +
                              $"and keeps the {kept} section of the visit he attended");
        if (kept != attended) Fail($"Dr Okafor kept {kept} of the {attended} sections of the visit he attended");
    }

    /// <summary>
    ///     Break glass, checked: Dr Okafor asks for emergency access to Jane (who, now, shares nothing with him): his feed
    ///     holds her sections while it lasts, and once it's over they go.
    /// </summary>
    private async Task BreakGlassAsync(long booking, CancellationToken token)
    {
        lock (_gate) _emergency.Add((Okafor, Jane));
        var broke = await CommandAsync(Okafor, correlation => Practice(token).BreakGlassAsync(Okafor, Jane, correlation),
            () => !_feeds[Okafor].Access.ContainsKey(Jane) ? "Dr Okafor's feed doesn't say he has emergency access to Jane"
                : !_feeds[Okafor].Sections.Values.Any(s => s.Booking == booking) ? "Dr Okafor's feed hasn't Jane's sections"
                : null, "Dr Okafor breaks the glass for Jane", token);
        if (!broke) return;
        var until = Locked(() => _feeds[Okafor].Access[Jane].At);
        Console.Out.WriteLine($"demo: Dr Okafor reads Jane's whole record until {until:HH:mm:ss}, the reason recorded");
        await Until(() => !_feeds[Okafor].Access.ContainsKey(Jane) && !_feeds[Okafor].Sections.Values.Any(s => s.Booking == booking),
            TimeSpan.FromSeconds(30), "Dr Okafor's emergency access over: Jane's sections gone from his feed", token);
        lock (_gate) _emergency.Remove((Okafor, Jane));
        Console.Out.WriteLine("demo: the emergency access is over, and Jane's sections have left Dr Okafor's feed");
    }

    /// <summary>
    ///     A message, checked: Jane writes to the practice, and it's a task for each receptionist; Rosa reads it, and
    ///     Jane's page says the practice has, not who, while Priya's task stays open.
    /// </summary>
    private async Task MessageAsync(CancellationToken token)
    {
        long message = 0;
        var wrote = await CommandAsync(Jane,
            correlation => Practice(token).MessageAsync(Jane, "Could I move my appointment to later in the week?", correlation,
                Order($"thread:{Jane}")),
            () => _feeds[Jane].Rows.Values.Any(r => r.Kind == ActivityKind.Messaged &&
                                                   (message = _feeds[Jane].Rows.First(m => m.Value.Kind == ActivityKind.Messaged).Key) > 0)
                ? null
                : "Jane's feed hasn't her message", "Jane writes to the practice", token);
        if (!wrote) return;
        message = Locked(() => _feeds[Jane].Rows.Where(r => r.Value.Kind == ActivityKind.Messaged).Max(r => r.Key));
        await Until(() => _feeds[Rosa].Tasks.ContainsKey(message) && _feeds[Priya].Tasks.ContainsKey(message),
            TimeSpan.FromSeconds(5), "the message, a task for Rosa and for Priya", token);
        var read = await Practice(token).SeenAsync(Rosa, [message], Correlation());
        if (!read.Done) Fail($"Rosa reading Jane's message: refused ({read.Refused})");
        await Until(() => !_feeds[Rosa].Tasks.ContainsKey(message) && _feeds[Jane].Rows.GetValueOrDefault(message) is { ReadMicros: > 0 },
            TimeSpan.FromSeconds(5), "Rosa's task closed, and Jane's message read by the practice", token);
        Console.Out.WriteLine($"demo: Rosa reads Jane's message: Rosa's row {Locked(() => _feeds[Rosa].Rows[message].ToString())}; " +
                              $"Jane's {Locked(() => _feeds[Jane].Rows[message].ToString())}; " +
                              $"Priya's task {(Locked(() => _feeds[Priya].Tasks.ContainsKey(message)) ? "still open" : "CLOSED")}");
        if (!Locked(() => _feeds[Priya].Tasks.ContainsKey(message))) Fail("Priya's task closed when Rosa read the message");
        await Until(() => _feeds[Tom].Rows.ContainsKey(message), TimeSpan.FromSeconds(5), "Tom's feed: Jane's message (his scope shows it)", token);
        var rosas = Locked(() => _feeds[Rosa].Rows[message]);
        if (rosas.Readers != 1 || rosas.ReadBy.ToString() != "Rosa Diaz")
            Fail("Rosa's row of the message doesn't say she read it, first");
    }

    /// <summary>
    ///     A command, its rendezvous awaited (registered before it's sent): checked against the giver's feed as it lands —
    ///     null if right, else what's wrong.
    /// </summary>
    private async Task<bool> CommandAsync(int giver, Func<byte[], MagicOnion.UnaryResult<CommandAnswer>> send,
        Func<string?> check, string what, CancellationToken token)
    {
        var correlation = Correlation();
        var pending = new Pending(check);
        lock (_gate)
        {
            _pending[Convert.ToHexString(correlation)] = pending;
        }

        var timer = Stopwatch.StartNew();
        var answer = await send(correlation);
        if (!answer.Done)
        {
            Fail($"{what}: refused ({answer.Refused})");
            return false;
        }

        try
        {
            var wrong = await pending.Done.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            if (wrong is not null)
            {
                Fail($"{what}: at its rendezvous, {wrong}");
                return false;
            }

            Console.Out.WriteLine(
                $"demo: {what} — activity {answer.Seq}, in {_feeds[giver].Name}'s feed at its rendezvous ({timer.Elapsed.TotalMilliseconds:F0} ms)");
            return true;
        }
        catch (TimeoutException)
        {
            Fail($"{what}: no rendezvous within 10 s");
            return false;
        }
    }

    private async Task RefusedAsync(MagicOnion.UnaryResult<CommandAnswer> sent, string because, string what)
    {
        var answer = await sent;
        if (answer.Done || answer.Refused?.Contains(because, StringComparison.Ordinal) != true)
            Fail($"{what}: {(answer.Done ? "done" : $"refused, but \"{answer.Refused}\"")}, not refused ({because})");
        else Console.Out.WriteLine($"demo: {what}: refused — {answer.Refused}");
    }

    /// <summary>A rendezvous delivered (under the client's lock): the command's check, against the feed as it is now.</summary>
    private void Rendezvous(ReadOnlyMemory<byte> correlation, RendezvousInfo info)
    {
        lock (_gate)
        {
            if (_pending.Remove(Convert.ToHexString(correlation.Span), out var pending))
                pending.Done.TrySetResult(pending.Check());
        }
    }

    /// <summary>
    ///     At the end: Rosa's and Priya's feeds the same (one class, one view: the same rows, so the same checksum); every
    ///     activity the same in every feed that has it, but for what the policy withholds; and a section the same in every
    ///     feed that reads it (a result's finding, where both read it).
    /// </summary>
    private async Task EndChecksAsync()
    {
        Feed? rosa, priya, patel, okafor;
        lock (_gate)
        {
            (rosa, priya, patel, okafor) = (_feeds.GetValueOrDefault(Rosa), _feeds.GetValueOrDefault(Priya),
                _feeds.GetValueOrDefault(Patel), _feeds.GetValueOrDefault(Okafor));
        }

        if (rosa is null || priya is null || patel is null || okafor is null) return; // (not every feed subscribed)
        await Until(() => Same(rosa, priya), TimeSpan.FromSeconds(3),
            "Rosa's and Priya's feeds (one class): the same rows", CancellationToken.None, false);
        lock (_gate)
        {
            if (!Same(rosa, priya))
                Fail($"Rosa's and Priya's feeds differ: {rosa.Rows.Count} and {priya.Rows.Count} rows");
            var bySeq = new Dictionary<long, (Feed Feed, FeedRow Row)>();
            foreach (var feed in _feeds.Values.Where(f => !f.IsScreen))
            foreach (var (seq, row) in feed.Rows)
                if (!bySeq.TryAdd(seq, (feed, row)) && bySeq[seq] is var (other, seen) && !Shared(row, seen))
                    Fail($"activity {seq} differs between {feed.Name}'s feed and {other.Name}'s: {row} | {seen}");
            var sections = new Dictionary<long, (Feed Feed, FeedRow Row)>();
            foreach (var feed in _feeds.Values.Where(f => !f.IsScreen))
            foreach (var (seq, section) in feed.Sections)
                if (!sections.TryAdd(seq, (feed, section)) && sections[seq] is var (other, seen) &&
                    (section.Reason.ToString() != seen.Reason.ToString() ||
                     section.DetailDisclosed == Disclosed.Full && seen.DetailDisclosed == Disclosed.Full &&
                     section.Detail.ToString() != seen.Detail.ToString())) // (a result's finding: withheld from its patient till released)
                    Fail($"section {seq} differs between {feed.Name}'s feed and {other.Name}'s: {section} | {seen}");
        }
    }

    /// <summary>Two members of one class: the same class rows, byte for byte (their own tasks and badge aside).</summary>
    private static bool Same(Feed a, Feed b)
    {
        return a.Rows.Count == b.Rows.Count && a.Sections.Count == b.Sections.Count &&
               a.Rows.All(r => b.Rows.TryGetValue(r.Key, out var other) && Bytes(r.Value, other)) &&
               a.Sections.All(r => b.Sections.TryGetValue(r.Key, out var other) && Bytes(r.Value, other));
    }

    private static bool Bytes(FeedRow a, FeedRow b)
    {
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<FeedRow>(in a))
            .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<FeedRow>(in b)));
    }

    /// <summary>What every feed that has an activity agrees on: what no realm protects.</summary>
    private static bool Shared(FeedRow a, FeedRow b)
    {
        return a.Kind == b.Kind && a.Site == b.Site && a.Clinician == b.Clinician && a.Booking == b.Booking &&
               a.SlotMicros == b.SlotMicros && a.AtMicros == b.AtMicros && a.Ticket == b.Ticket &&
               a.Room.ToString() == b.Room.ToString();
    }

    /// <summary>A line a second while batches come (else every ten): batches, and each feed's rows.</summary>
    private void Second()
    {
        if (quiet) return;
        lock (_gate)
        {
            if (_batches == _printed && _notes.Count == 0 && ++_quietSeconds < 10) return;
            (_printed, _quietSeconds) = (_batches, 0);
            var line = new StringBuilder($"{_batches} batches · ");
            line.AppendJoin(", ", _feeds.Values.Select(f => $"{f.Short} {(f.IsStats ? f.Stats.Count : f.IsScreen ? f.Screen.Count : f.Rows.Count)}"));
            foreach (var note in _notes) line.Append("\n  ").Append(note);
            _notes.Clear();
            Console.Out.WriteLine(line);
        }

        Console.Out.Flush();
    }

    private int End(bool verifying, bool completed)
    {
        lock (_gate)
        {
            if (verifying && !completed && _failures.Count == 0)
                _failures.Add("stopped before the verification's time was up: not verified");
            Console.Out.WriteLine(
                $"end: {_batches} batches over {_feeds.Count} feeds; {_feeds.Values.Sum(f => f.Snapshots)} snapshots" +
                (viewer ? $"; the router's viewer: {new Uri(app, "ops/realtime")}" : ""));
            if (_failures.Count == 0)
            {
                Console.Out.WriteLine(verifying ? "verified: every feed as the policy says, every command in one record" : "nothing failed");
                Console.Out.Flush();
                return 0;
            }

            Console.Out.WriteLine($"FAILED ({_failures.Count}):");
            foreach (var failure in _failures.Take(40)) Console.Out.WriteLine($"  {failure}");
            Console.Out.Flush();
            return 1;
        }
    }

    private async Task Until(Func<bool> done, TimeSpan within, string what, CancellationToken token, bool fail = true)
    {
        var timer = Stopwatch.StartNew();
        while (!Locked(done))
        {
            if (timer.Elapsed > within)
            {
                if (fail) Fail($"not within {within.TotalSeconds:F0} s: {what}");
                return;
            }

            await Task.Delay(20, token);
        }
    }

    private T Locked<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }

    private void Fail(string what)
    {
        lock (_gate)
        {
            if (_failures.Count < 200 && _failures.Add(what)) _notes.Add($"FAIL {what}");
        }
    }

    private void Note(string what)
    {
        lock (_gate)
        {
            _notes.Add(what);
        }
    }

    private static string? Ok<T>(T _)
    {
        return null;
    }

    /// <summary>
    ///     This verifier's next command with <paramref name="key" />, numbered: it's one sender (<see cref="CommandOrder" />),
    ///     whoever it acts as.
    /// </summary>
    private CommandOrder Order(string key)
    {
        lock (_numbered)
        {
            return new CommandOrder { Key = key, Sender = _sender, Seq = _numbered[key] = _numbered.GetValueOrDefault(key) + 1 };
        }
    }

    /// <summary>
    ///     Order, checked: Jane's next two messages, numbered in her thread with the practice (<c>thread:1001</c>), the second
    ///     sent first. The writer holds it until the first has come, so her thread has them in her order. Then one whose
    ///     predecessor hasn't come is refused once its hold is over, saying which it waited for; its predecessor sent, it's
    ///     sent again, and goes in after it.
    /// </summary>
    private async Task OrderAsync(CancellationToken token)
    {
        var key = $"thread:{Jane}";
        var (first, second) = (Order(key), Order(key));
        const string earlier = "First: is Thursday at 10:20 still free?", later = "Second: or Friday morning, if not.";
        var secondSent = Practice(token).MessageAsync(Jane, later, Correlation(), second).ResponseAsync;
        await Task.Delay(300, token); // (the second in first: held for the first)
        var firstAnswer = await Practice(token).MessageAsync(Jane, earlier, Correlation(), first);
        var secondAnswer = await secondSent;
        if (!firstAnswer.Done || !secondAnswer.Done)
        {
            Fail($"order: Jane's two messages, the second sent first: refused ({firstAnswer.Refused ?? secondAnswer.Refused})");
            return;
        }

        await Until(() => _feeds[Jane].Rows.ContainsKey(firstAnswer.Seq) && _feeds[Jane].Rows.ContainsKey(secondAnswer.Seq),
            TimeSpan.FromSeconds(5), "both of Jane's messages in her feed", token);
        if (firstAnswer.Seq > secondAnswer.Seq)
            Fail($"order: Jane's second message went in before her first ({secondAnswer.Seq} before {firstAnswer.Seq})");

        var (skipped, after) = (Order(key), Order(key));
        var timer = Stopwatch.StartNew();
        var gap = await Practice(token).MessageAsync(Jane, "Third: before the one before me.", Correlation(), after);
        var held = timer.Elapsed;
        if (gap.Done || gap.Refused?.Contains($"#{skipped.Seq}") != true)
            Fail($"order: a message whose predecessor never came wasn't refused for it ({gap.Refused ?? "done"})");
        var filled = await Practice(token).MessageAsync(Jane, "Third, after all: is Friday at 9:00 free?", Correlation(), skipped);
        var resent = await Practice(token).MessageAsync(Jane, "Fourth: thank you.", Correlation(), after);
        if (!filled.Done || !resent.Done || filled.Seq > resent.Seq)
            Fail($"order: the missing one sent, then the refused one again: not in order ({filled.Refused ?? resent.Refused})");
        Console.Out.WriteLine($"demo: order — Jane's two messages, the second sent first, went in in her order (activities " +
                              $"{firstAnswer.Seq}, {secondAnswer.Seq}); one whose predecessor hadn't come refused after " +
                              $"{held.TotalSeconds:F1} s, sent again after it, in order");
    }

    private static byte[] Correlation()
    {
        return TaplineClient.NewCorrelation();
    }

    private sealed class Pending(Func<string?> check)
    {
        public Func<string?> Check { get; } = check;

        public TaskCompletionSource<string?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A manager's statistics, among the feeds: keyed apart from their own topic.</summary>
    private static int StatsOf(int person) => -person;

    /// <summary>A persona's feed as this client holds it: its rows, read again at each record.</summary>
    private sealed class Feed(Persona persona, string? of = null)
    {
        public readonly Dictionary<long, FeedRow> Rows = []; // (activities)
        public readonly Dictionary<long, FeedRow> Tasks = []; // (their own)
        public readonly Dictionary<long, FeedRow> Access = []; // (their own: a patient's consents, a GP's emergency access)
        public readonly Dictionary<long, FeedRow> Outbox = []; // (their own: a receptionist's replies, as each went)
        public readonly HashSet<long> Ever = []; // (every activity it has held)
        public FeedRow? Status;
        public readonly Dictionary<long, ScreenRow> Screen = [];
        public readonly Dictionary<FeedKey, StatsRow> Stats = []; // (a manager's: their figures)
        public readonly Dictionary<long, FeedRow> Sections = []; // (clinical)
        public int Batches, Snapshots;
        public uint Checksum;
        public uint TapId;
        public Guid? NodeId; // (the router's id of the provider instance it's bound through)
        public int Moves; // (moved off a draining node)
        public Subscription? Subscription;
        public TapError? Ended;
        public string? Partition;
        public string? Topic;
        public long Version;
        public Persona Persona { get; } = persona;
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsScreen => Persona.Role == "screen";
        public string? Of { get; } = of; // (which of their feeds: none, their own topic; "stats", a manager's statistics)
        public int Key => IsStats ? StatsOf(Persona.Id) : Persona.Id;
        public bool IsStats => Of == "stats";
        public string Name => Persona.Name;
        public string Short => Persona.Role switch
        {
            "clinician" => Persona.Name.Split(' ')[^1],
            "screen" => $"screen:{Persona.Site?.ToLowerInvariant()}",
            _ => Persona.Name.Split(' ')[0] + (IsStats ? ":stats" : "")
        };

        public string Summary()
        {
            var rows = IsStats ? Stats.Count : IsScreen ? Screen.Count : Rows.Count;
            var example = IsStats
                ? string.Join("; ", Stats.Where(r => r.Key.Section == FeedKey.Reasons).Select(r => r.Value.ToString()))
                : IsScreen
                    ? Screen.Values.LastOrDefault().ToString()
                    : Rows.Count > 0
                        ? Rows.MaxBy(r => r.Key).Value.ToString()
                        : "";
            return $"  {Name} ({Persona.Role}{(IsStats ? ", statistics" : "")}{(Persona.Site is { } s ? $", {s}" : "")}): {Topic}, partition {Partition ?? "none"}, " +
                   $"{rows} rows{(IsScreen || IsStats ? "" : $" and {Sections.Count} sections")}{(rows > 0 ? $"; the latest: {example}" : "")}";
        }
    }
}
