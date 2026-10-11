using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using ActivityFeed.Contracts;
using Chalk;
using Chalk.Entitlements;
using Chalk.Incremental;
using Tapline;

namespace ActivityFeed.Service;

/// <summary>
///     The practice's feeds, as a data provider. Every person's feed is a topic of their own (<c>home:{person}</c>), so
///     the provider decides, per person, what it holds: the schedule's activities they may see, and the visits' clinical
///     sections they may read. The work is shared where the policy allows:
///     <list type="bullet">
///         <item>
///             Staff: two standing views per entitlement class (reception at a site; each GP), its schedule and its
///             sections, prepared with the class's grants folded in: incremental, each update costing what changed, and
///             validated against a recomputed twin at every update (<see cref="FeedView{TRow}" />). Each member's topic
///             reads both with cursors of its own: the views' changes, fanned out.
///         </item>
///         <item>
///             Patients: too many for views each. The statements, prepared once from a context's shape, run for a patient
///             when they open their feed (its snapshot), and once for each change that concerns them (that row, or its
///             deletion).
///         </item>
///         <item>A screen: its site's topic (<c>screen:{site}</c>), shared, from its own view, kept alike. Nothing personal.</item>
///         <item>
///             A manager: their class's statistics (<c>stats:{sites}</c>), shared by every manager of those sites, from
///             two views of its own, kept alike: by the hour, and visits by reason. Counts and means only: nothing
///             personal, and nothing clinical but how many came for what.
///         </item>
///         <item>
///             A dispatcher: a shard's pending entries of the dispatch log (<c>notify:{shard}</c>), from a view of its own
///             under the dispatcher's grants: a push token, but nothing personal or clinical. What it reports takes the
///             entry out (<see cref="Notifications" />).
///         </item>
///     </list>
///     What's shared with a GP is data: the service writes a section's grant rows as the section is written, from each
///     patient's consents (<see cref="Consents" />). One loop owns ChalkQL and every topic, so each topic has one writer:
///     the simulated practice's steps and the commands are written, the views updated, their changes posted; then step
///     1's opens are answered, and the commands' rendezvous asked for.
/// </summary>
public sealed class PracticeProvider : BackgroundService, IDataProvider
{
    private const int StaffStaging = 2 << 20, PatientStaging = 128 << 10, ScreenStaging = 256 << 10, HomeLog = 4096;

    private static readonly TimeSpan ClockEvery = TimeSpan.FromSeconds(5), Idle = TimeSpan.FromSeconds(60);

    // A dispatcher topic's pipe: how many versions of its view may wait for its reader (past that, the view waits, the
    // engine holding its versions), and how many the engine holds before the view is made again, its reader sent it whole.
    private const int NotifyBatches = 16, NotifyBacklog = 1024;

    private static readonly ErrorPolicy Interrupted = ErrorPolicy.ReplayLast("the practice's feed is interrupted"u8.ToArray());

    private readonly HashSet<long> _acknowledged = []; // reminders acknowledged (each once)
    private readonly List<RowChange<FeedRow>> _changes = [];
    private readonly ILoggerFactory? _chalkLog;
    private readonly Dictionary<string, StaffClass> _classes = []; // by class: the loop's
    private readonly Dictionary<(string Sql, string Shape), EntitledQuery> _shaped = []; // (classes' statements, by shape: the loop's)
    private readonly Consents _consents = new();
    private TimeSpan _breakGlass; // (how long an emergency's access lasts: the log's first turn says)
    private readonly Carers _carers = new();
    private readonly Dictionary<long, Section> _results = []; // results awaiting their GP's review and release, by section
    private readonly Drained<FeedRow>[] _drained = [new(), new()]; // (a staff topic's parts: its schedule, its sections)
    private readonly Drained<StatsRow>[] _drainedStats = [new(), new()]; // (a stats topic's: by the hour, by reason)
    private readonly Dictionary<int, Dictionary<int, DateTime>> _emergencies = []; // by GP: the patients they broke the glass for, until when
    private readonly int _feedRows;
    private readonly List<GaveWay> _gaveWay = []; // (the loop's: the last few, for /views)
    private readonly Dictionary<int, Home> _homes = []; // by person: each feed open on any instance (the log's), the loop's
    private readonly List<FeedKey> _keys = [];
    private readonly Dictionary<long, int> _patientOf = []; // an activity's patient, by its number
    private readonly HashSet<long> _read = []; // messages the practice has read (its first reader marked so)
    private readonly Dictionary<long, int> _replies = []; // the practice's replies, by activity: who at reception wrote it
    private readonly Dictionary<long, int> _sectionPatient = []; // a section's patient, by its number
    private readonly Dictionary<long, (long Booking, int Clinician)> _activityVisit = [], _sectionVisit = []; // (whose visit, its GP)
    private long _shownSeq; // (the access log's rows, numbered as every instance works them out: the log's order)
    private readonly List<(byte Part, long Seq, DisclosureBasis Basis)> _showing = []; // (a feed's rows shown at once, in order)
    private readonly Drained<FeedRow> _whole = new(); // (a class's view drained whole: a feed's first state, for the access log)
    private EntitledQuery? _shownTo; // (who was shown a record: the auditor's statement)
    private readonly ILogger<PracticeProvider> _log;
    private readonly TaskCompletionSource<PracticeStore?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RouterSessions _routers;
    private readonly LoadMeter _load = new(); // its loop's work over the time that passes: its health reports' load
    private readonly List<FeedKey> _ownKeys = [];
    private readonly List<RowChange<FeedRow>> _ownChanges = [];
    private readonly List<FeedRow> _ownRows = [];
    private readonly List<FeedRow> _rows = [];
    private readonly Drained<ScreenRow> _screenDrained = new();
    private readonly HashSet<(long Activity, int Person)> _seenBy = []; // who has read which message
    private readonly Dictionary<int, ScreenFeed> _screens = []; // by site: the loop's
    private readonly Dictionary<string, StatsFeed> _stats = []; // by class: the loop's
    private readonly Dictionary<int, NotifyFeed> _notify = []; // by shard: the loop's
    private Notifications _notifications;
    private readonly List<(Activity? Activity, Section? Section)> _raise = []; // (what to notify of, once written)
    private readonly Writes _notifyWrites = new();
    private bool _live; // (past priming: what happens now is notified)
    private readonly TimeSpan? _oracleEvery;
    // Measuring (Practice:Measure: off by default, and nothing kept then): posts and their time, each turn's write and
    // fan-out, and each post's lateness behind the newest activity of its turn it carries, by kind of topic.
    private readonly bool _measure;
    private readonly Samples? _write, _fanOut;
    private readonly Samples[] _lateness = [];
    private readonly long[] _postsOf = [], _snapshotsOf = [];
    private readonly Dictionary<object, long> _sampledIn = new(ReferenceEqualityComparer.Instance); // (a topic's turn: once)
    private readonly Dictionary<string, Samples> _updates = []; // (each view's update, by name)
    private readonly System.Diagnostics.Stopwatch _measured = new();
    private long _postTicks, _activities, _turnMicros;
    private readonly Lock _measureGate = new(); // (posts measured on the loop, and by the dispatcher topics' readers)
    private Collections _collected = Collections.Now();
    private const int StaffTopics = 0, PatientTopics = 1, ScreenTopics = 2, StatsTopics = 3, DispatcherTopics = 4;
    private DateTime _nextOracle;
    private long _oracleChecks, _oracleTurn;
    private readonly List<OracleReport> _oracleMismatches = []; // (the last few, for /views)
    private ImmutableDictionary<int, DateTime> _lagging = ImmutableDictionary<int, DateTime>.Empty; // (sites falling behind, until)
    private ImmutableHashSet<string> _services = ImmutableHashSet<string>.Empty; // (the names the routers ask for it by)
    // A person's topic: its rows decoded for the router's viewer, its records kept as metadata only (personal data: the
    // router holds its state to route it, and no more), and the full record of what it holds on this service's own host.
    private readonly PayloadFormat _homeFormat;
    private TimeSpan _releaseEvery;
    private DateTime _nextRelease;
    private EntitledQuery? _release; // (the release job's statement, as the notifier: prepared once)
    private EntitledQuery? _note; // (a section's note in full: one plan, every principal's)
    private readonly List<StatsRow> _statsRows = [];
    private readonly List<RowChange<StatsRow>> _statsChanges = [];
    private readonly Simulator _simulator = new();
    private TimeSpan _stepEvery, _history;
    private readonly ViewPolicy _viewPolicy;
    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private Shaped? _oneActivity, _oneSection, _patientSchedule, _patientSections; // (from a context's shape: every principal's)
    private Shaped? _carerActivity, _carerSection, _carerSchedule, _carerSections; // (a carer's twins: their basis, carer)
    private ViewsReport _views = new("", false, [], []); // (the loop's, published: /views reads it)

    // The write log: every instance replays it in order; the writer (its lease's holder) logs each turn's inputs, then runs
    // them, as every other instance will. What only this instance knows (whose feeds it holds open), it appends; every
    // instance applies it where the next turn says, and works out what each feed open anywhere is shown (the access log).
    private static readonly TimeSpan LeaseTerm = TimeSpan.FromSeconds(3), LeaseEvery = TimeSpan.FromMilliseconds(250);
    private const int LogBatch = 256;
    private readonly IPracticeLog _practiceLog;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _name, _origin; // (this instance, as named, and in the log: its id)
    private readonly PracticeSettings _settings; // (this instance's own, should it prime the practice)
    private Lease? _lease; // (held: this instance writes)
    private string? _holder; // (who does, as last asked)
    private bool _draining;
    private DateTime _drainUntil, _leaseCheck, _nextStep, _clocked;
    private long _logRead, _upTo, _lastTurn, _turns; // (the last entry read; the last other instances' applied; the last turn run)
    private bool _caughtUp; // (the log's last read found nothing new: a follower's ready only then, not a batch behind)
    private string? _lastDigest; // (what it wrote)
    private readonly List<LogEntry> _entries = []; // (a read's)
    private readonly List<LogEntry> _unfenced = []; // (read: applied where the turn that includes them says)
    private readonly HashSet<long> _ownTurns = []; // (logged by this instance, run as they were: skipped when read)
    private readonly Dictionary<long, string> _ran = []; // (turns run, by number: what each wrote, digested)
    private readonly Queue<long> _ranOrder = new();
    private readonly List<string> _divergences = []; // (turns another instance's writes differed from this one's)
    private readonly Dictionary<int, HashSet<string>> _openOn = []; // (whose feed is open, and on which instances: the log's)
    private readonly List<int> _opened = [], _closed = []; // (this instance's, since its last presence entry)
    private readonly HashSet<int> _openHere = []; // (whose feeds this instance has said it holds open)
    private readonly List<Open> _waiting = []; // (step 1s for a person's feed here, until the log opens it here)
    private readonly List<Shown> _shownStash = []; // (the access log's, as worked out: written with the next refresh)
    private readonly Dictionary<string, CommandAnswer> _answers = []; // (commands run, by correlation: a retry answered so)
    private readonly Queue<string> _answersOrder = new();
    private readonly List<Command> _commandsIn = []; // (the writer's, for its next turn)
    // The commands' order (CommandOrder): each sender's last number run, per key (from the log: every instance's alike); and
    // the writer's commands that came before their turn, held until those before them do, or their hold is over.
    private readonly Dictionary<(string Sender, string Key), long> _sequenced = [];
    private readonly List<(Command Command, DateTime Until)> _held = [];
    private readonly TimeSpan _orderHold;
    private readonly List<DispatchReport> _reportsIn = [];
    private readonly List<(Command Command, Person Actor, CommandAnswer Answer)> _answered = [];
    private readonly List<DispatchReport> _reported = [];
    private readonly Writes _turnWrites = new();
    private readonly TurnDigest _digest = new();
    private readonly Samples _lag = new("replica lag", 4096); // (a turn appended to its run here: a follower's)
    private readonly Samples _heard = new("replica read", 4096); // (a turn appended to its read here)
    // The log's signal, over Tapline, where other instances share the log: writing, this instance posts each turn it appends
    // on its log topic; following, it hears the writer's through the app (the store looked at every so often, failing that).
    private readonly LogSignal? _signal;
    private readonly IAccessLog _accessLog; // (the access log at the posts: the practice's keeps nothing, its log replayed)
    private readonly AccessLogSink _accessStore; // (the access log's own database, written off the loop: ChalkQL's audit source)
    private LastValueSource? _logTopic; // (writing, a shared log: its signal)
    private volatile bool _leaseNow; // (the writer let the lease go, or its signal ended: the lease asked for at once)
    private LogStatus _status = new("", "", "starting", null, 0, 0, 0, 0, "", "", 0, null, null, []); // (the loop's, published: /log)

    public PracticeProvider(ProviderEndpoint endpoint, IConfiguration configuration, ILogger<PracticeProvider> log,
        ILoggerFactory loggers, IHostEnvironment environment, IPracticeLog practiceLog, IHostApplicationLifetime lifetime,
        IAccessLog accessLog, AccessLogSink accessStore)
    {
        _log = log;
        (_practiceLog, _lifetime, _accessLog, _accessStore) = (practiceLog, lifetime, accessLog, accessStore);
        // (the sampled oracle: an open topic checked against its statements run whole, each so often; Development's)
        var oracle = configuration.GetValue<double?>("Practice:OracleSeconds") ?? (environment.IsDevelopment() ? 30 : 0);
        _oracleEvery = oracle > 0 ? TimeSpan.FromSeconds(Math.Clamp(oracle, 1, 3600)) : null;
        if (_measure = configuration.GetValue<bool>("Practice:Measure"))
        {
            (_write, _fanOut) = (new Samples("write and refresh"), new Samples("fan-out"));
            _lateness = [new("staff"), new("patients and carers"), new("screens"), new("statistics"), new("dispatchers")];
            (_postsOf, _snapshotsOf) = (new long[_lateness.Length], new long[_lateness.Length]);
            _measured.Start();
        }
        _feedRows = Math.Clamp(configuration.GetValue<int?>("Practice:FeedRows") ?? 300, 10, 5000); // (a class's newest)
        // (how long a command that came before its turn waits for those before it: the app's retries' eight seconds, by default)
        _orderHold = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<double?>("Practice:OrderHoldSeconds") ?? 8, 0.5, 60));
        _viewPolicy = ViewPolicy.From(configuration);
        _breakGlass = configuration.GetValue<double?>("Practice:BreakGlassSeconds") is { } seconds and > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue<double?>("Practice:BreakGlassMinutes") ?? 30, 1, 240));
        // The practice's settings, should this instance prime it; following a log, its first turn's. How long a notification
        // waits for its recipient to see what it's about in the app (their feed open); how often the release job runs (the
        // notices, as each recipient's budgets allow); the pace (activities a minute, each step one or none, both sites
        // together); and the history it's primed with, at that pace (the last hour and a half by default).
        _settings = new PracticeSettings(
            Math.Clamp(configuration.GetValue<double?>("Practice:HistoryMinutes") ?? 90, 1, 720),
            60 / Math.Clamp(configuration.GetValue<double?>("Practice:PerMinute") ?? 12, 0.5, 600), _breakGlass.TotalSeconds,
            Math.Clamp(configuration.GetValue<double?>("Practice:SeenGraceSeconds") ?? 30, 1, 600),
            Math.Clamp(configuration.GetValue<double?>("Practice:ReleaseSeconds") ?? 60, 1, 3600),
            configuration["Practice:QuietHours"] ?? "22-8", configuration["Practice:TimeZone"] ?? "");
        _notifications = Adopt(_settings);
        _chalkLog = configuration.GetValue<bool>("Practice:ChalkLog") ? loggers : null;
        _homeFormat = Formats.Feed with
        {
            Retention = RecordRetention.None,
            ViewUrl = new Uri($"http://127.0.0.1:{configuration.GetValue<int?>("HttpPort") ?? 5961}/view")
        };
        Endpoint = endpoint;
        _name = configuration["Practice:Name"] is { Length: > 0 } name ? name : "practice"; // (blue/green: practice-blue, say)
        Instance = ProviderInstance.Start(_name, endpoint);
        _origin = Instance.Id.ToString("N");
        if (practiceLog is not MemoryPracticeLog) // (a log other instances share: the writer's signal, heard through the app)
            _signal = new LogSignal(new Uri(configuration["Practice:Stream"] ?? "http://127.0.0.1:5952/"), _name, Heard,
                SignalEnded, log);
        _routers = new RouterSessions(() => Instance)
        {
            Lost = (router, routes) =>
                log.LogInformation("Practice: router {Router} presumed dead, {Routes} routes forgotten", router,
                    routes.Length)
        };
    }

    public ProviderEndpoint Endpoint { get; }
    public ProviderInstance Instance { get; }

    /// <summary>The write log as this instance has it (<c>/log</c>): its role, the lease, how far it's read and run, its lag.</summary>
    public LogStatus Log => Volatile.Read(ref _status);

    /// <summary>
    ///     A drain (<c>POST /drain</c>): this instance resigns the lease (another writes), reports draining, and has the
    ///     routers move its clients over <paramref name="over" />, each given <paramref name="grace" />; then it stops.
    /// </summary>
    public async Task DrainAsync(TimeSpan over, TimeSpan grace, CancellationToken ct)
    {
        if (await _ready.Task.WaitAsync(ct) is null) return;
        var drain = new DrainRequest(over, grace);
        _work.Writer.TryWrite(drain);
        await drain.Done.Task.WaitAsync(ct);
    }

    /// <summary>ChalkQL is up and the history primed: step 1 is answered.</summary>
    public bool Ready => _ready.Task is { IsCompletedSuccessfully: true, Result: not null };

    /// <summary>The feeds' views, as the loop last left them: how each is kept, and any that gave way to a recomputed one.</summary>
    public ViewsReport Views => Volatile.Read(ref _views);

    /// <summary>The practice's people, as a persona picker shows them.</summary>
    public static Persona[] Personas => People.Personas;

    ValueTask IDataProvider.ConnectAsync(IRouterChannel router, CancellationToken cancellationToken)
    {
        _routers.Connected(router);
        return default;
    }

    /// <summary>
    ///     The practice's feeds require a sign-in to its realm (<see cref="PracticeSignIn" />): the router refuses anyone
    ///     else before step 1 reaches here.
    /// </summary>
    ValueTask<ServiceAccess> IDataProvider.AccessAsync(string service, CancellationToken cancellationToken)
    {
        return new ValueTask<ServiceAccess>(ServiceAccess.RequiredIn(PracticeSignIn.Realm));
    }

    /// <summary>
    ///     Step 1: the person signed in (<c>{"person":101}</c>, signed in as <c>101</c>), onto their own topic — opened, its
    ///     first state posted and applied, if it isn't open — or their site's screen. Refused, saying why, if there's no
    ///     such person or ChalkQL refuses their statement; forbidden if the connection is signed in as someone else (a
    ///     dispatcher's or a follower's topic: as its own name).
    /// </summary>
    async ValueTask IDataProvider.SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
    {
        if (!_routers.Admits(request.Router)) // a router presumed dead
        {
            request.Nack();
            return;
        }

        if (!Volatile.Read(ref _services).Contains(request.Service)) // (what a degraded site's paths are under)
            ImmutableInterlocked.Update(ref _services, s => s.Add(request.Service));
        var parameters = ParametersOf(request.Parameters);
        var signedIn = request.Identity?.UserId; // (passthrough's: insecure — this sample's, on this machine)
        Person? person = null;
        if (parameters is { Follower: { Length: > 0 } follower }) // (the log's signal: the writer's; another refuses it as served elsewhere)
        {
            if (signedIn != follower)
            {
                request.Forbid($"Signed in as {signedIn}: the log is followed as its follower, {follower}.");
                return;
            }

            if (Volatile.Read(ref _logTopic) is null)
            {
                request.Refuse(RefusalKind.Elsewhere, $"{_name} doesn't write the practice's records now.", null); // (the router asks the next instance)
                return;
            }
        }
        else if (parameters is { Dispatcher: { Length: > 0 } dispatcher, Shard: var shard })
        {
            if (signedIn != dispatcher)
            {
                request.Forbid($"Signed in as {signedIn}: a dispatcher's topic is its own, {dispatcher}'s.");
                return;
            }

            if (shard is < 0 or >= Notifications.Shards)
            {
                request.Refuse($"No such shard: {shard} (there are {Notifications.Shards}).");
                return;
            }
        }
        else if ((person = parameters is null ? null : People.Find(parameters.Person)) is null)
        {
            request.Refuse($"No such person: {request.Parameters}.");
            return;
        }

        if (person is not null && signedIn != PracticeSignIn.UserOf(person.Id))
        {
            request.Forbid($"Signed in as {signedIn}: {person.Name}'s feed is theirs alone.");
            return;
        }

        if (person is { Role: Role.Auditor })
        {
            request.Refuse($"{person.Name} audits: no feed of their own (the access log is theirs, on the audit page).");
            return;
        }

        if (await _ready.Task.WaitAsync(cancellationToken) is null)
        {
            request.Refuse("The practice's records are unavailable: ChalkQL didn't start.");
            return;
        }

        if (parameters is { Feed: { } feed } && (feed != "stats" || person is not { Role: Role.Manager }))
        {
            request.Refuse($"No such feed: {feed} (a manager's statistics are \"stats\").");
            return;
        }

        var open = new Open(request, person)
            { Dispatcher = parameters!.Dispatcher, Shard = parameters.Shard, Follower = parameters.Follower, Feed = parameters.Feed };
        _work.Writer.TryWrite(open);
        try
        {
            await open.Done.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            open.Abandoned = true; // (the router gave up on it: not accepted later, onto a topic nobody would close)
            throw;
        }
    }

    ValueTask IDataProvider.ActivateAsync(Activation notice, CancellationToken cancellationToken)
    {
        return default;
    }

    ValueTask IDataProvider.UnsubscribeAsync(Unsubscription notice, CancellationToken cancellationToken)
    {
        _routers.Ended(notice.Router, notice.Route);
        _work.Writer.TryWrite(new Close(notice));
        return default;
    }

    ValueTask<HealthReport> IProviderHealth.CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
    {
        return new ValueTask<HealthReport>(Report() with { RouterRefused = _routers.Refuses(probe.Router) });
    }

    /// <summary>How it is: serving once ChalkQL is up and the history primed; not serving (starting) until then.</summary>
    public HealthReport Report()
    {
        var (lagging, services) = (Volatile.Read(ref _lagging), Volatile.Read(ref _services));
        var load = _load.Load; // (its loop's: the time it works over the time that passes)
        if (_draining) return new HealthReport(Instance, ProviderStatus.Draining, DrainUntil: _drainUntil, Load: load);
        return new HealthReport(Instance, Ready ? ProviderStatus.Serving : ProviderStatus.NotServing,
            DegradedPaths: lagging.Count == 0
                ? null
                : [.. services.SelectMany(service => lagging.Keys.Order().Select(s => $"{service}/{People.SiteOf(s).Partition}"))],
            Load: load);
    }

    /// <summary>
    ///     Fault injection (Development only, opted into): a site's system falling behind for a while (or caught up again,
    ///     at zero). Its partition is reported degraded, so the router holds only the routes under it and shows them stale;
    ///     the site's topics are fed nothing meanwhile, and catch up once it's over. Why not, if it's no site.
    /// </summary>
    public string? Lag(int site, TimeSpan duration)
    {
        if (People.Sites.All(s => s.Id != site)) return $"No such site: {site}.";
        ImmutableDictionary<int, DateTime> was, now;
        do
        {
            was = Volatile.Read(ref _lagging);
            now = duration > TimeSpan.Zero ? was.SetItem(site, DateTime.UtcNow + duration) : was.Remove(site);
        } while (Interlocked.CompareExchange(ref _lagging, now, was) != was);

        _log.LogWarning("Practice: fault injected: {Site}'s system {What}", People.SiteOf(site).Name,
            duration > TimeSpan.Zero ? $"falls behind for {duration.TotalSeconds:0} s" : "is caught up");
        return null;
    }

    /// <summary>Whether a person's topic is held: its site is falling behind (fault injection).</summary>
    private bool Held(Person person)
    {
        return person.Site is { } site && Volatile.Read(ref _lagging).ContainsKey(site.Id);
    }

    /// <summary>Reception checks a patient in: answered once it's written and posted, its rendezvous asked for.</summary>
    public Task<CommandAnswer> ArriveAsync(int person, long booking, byte[] correlation, CommandOrder? order, CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.Arrive, person, booking, "", correlation) { Order = order }, ct);
    }

    /// <summary>A clinician calls a patient in, to a room.</summary>
    public Task<CommandAnswer> CallAsync(int person, long booking, string room, byte[] correlation, CommandOrder? order,
        CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.Call, person, booking, room, correlation) { Order = order }, ct);
    }

    /// <summary>A patient acknowledges a reminder of theirs.</summary>
    public Task<CommandAnswer> AcknowledgeAsync(int person, long activity, byte[] correlation, CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.Acknowledge, person, activity, "", correlation), ct);
    }

    /// <summary>What a person's page has shown them: the messages they've read (reception's, recorded once each).</summary>
    public Task<CommandAnswer> SeenAsync(int person, long[] activities, byte[] correlation, CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.Seen, person, 0, "", correlation) { Activities = activities }, ct);
    }

    /// <summary>A patient writes to the practice.</summary>
    public Task<CommandAnswer> MessageAsync(int person, string text, byte[] correlation, CommandOrder? order, CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.Message, person, 0, text, correlation) { Order = order }, ct);
    }

    /// <summary>Reception replies to a patient's message.</summary>
    public Task<CommandAnswer> ReplyAsync(int person, long message, string text, byte[] correlation, CommandOrder? order,
        CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.Reply, person, message, text, correlation) { Order = order }, ct);
    }

    /// <summary>A patient shares a tag of theirs with a GP.</summary>
    public Task<CommandAnswer> ShareAsync(int person, int gp, string tag, byte[] correlation, CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.Share, person, gp, tag, correlation), ct);
    }

    /// <summary>A patient withdraws a consent of theirs.</summary>
    public Task<CommandAnswer> WithdrawAsync(int person, long consent, byte[] correlation, CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.Withdraw, person, consent, "", correlation), ct);
    }

    /// <summary>A GP breaks the glass for a patient.</summary>
    public Task<CommandAnswer> BreakGlassAsync(int person, int patient, byte[] correlation, CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.BreakGlass, person, patient, "", correlation), ct);
    }

    /// <summary>A GP releases a result of their patient's, reviewed.</summary>
    public Task<CommandAnswer> ReleaseAsync(int person, long section, byte[] correlation, CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.Release, person, section, "", correlation), ct);
    }

    /// <summary>The manager posts one of the practice's notices to a site's cohort.</summary>
    public Task<CommandAnswer> NoticeAsync(int person, int site, string notice, byte[] correlation, CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.Notice, person, site, notice, correlation), ct);
    }

    /// <summary>A patient revokes a carer's designation.</summary>
    public Task<CommandAnswer> RevokeCarerAsync(int person, int carer, byte[] correlation, CancellationToken ct)
    {
        return CommandAsync(new Command(CommandKind.RevokeCarer, person, carer, "", correlation), ct);
    }

    /// <summary>A dispatcher reports a send handed off: the entry its shard's number names leaves its topic.</summary>
    public Task<CommandAnswer> HandedOffAsync(string dispatcher, int shard, long seq, byte[] correlation, CancellationToken ct)
    {
        return ReportAsync(new DispatchReport(dispatcher, shard, seq, "handed_off", "", correlation), ct);
    }

    /// <summary>A dispatcher reports a send the last mile refused, saying why.</summary>
    public Task<CommandAnswer> FailedAsync(string dispatcher, int shard, long seq, string reason, byte[] correlation,
        CancellationToken ct)
    {
        return ReportAsync(new DispatchReport(dispatcher, shard, seq, "failed", reason, correlation), ct);
    }

    /// <summary>A dispatcher reports a no-op recorded in its log.</summary>
    public Task<CommandAnswer> RecordedAsync(string dispatcher, int shard, long seq, byte[] correlation, CancellationToken ct)
    {
        return ReportAsync(new DispatchReport(dispatcher, shard, seq, "recorded", "", correlation), ct);
    }

    /// <summary>What the policy shows a person, and why: ChalkQL's explanation of each of their statements.</summary>
    public async Task<Explanation> ExplainAsync(int person, CancellationToken ct)
    {
        if (await _ready.Task.WaitAsync(ct) is null) return new Explanation { Refused = "ChalkQL didn't start." };
        if (People.Find(person) is not { } who) return new Explanation { Refused = $"No such person: {person}." };
        var asked = new ExplainRequest(who);
        _work.Writer.TryWrite(asked);
        return await asked.Done.Task.WaitAsync(ct);
    }

    /// <summary>A section's note in full, as the person may read it.</summary>
    public async Task<NoteAnswer> NoteAsync(int person, long section, CancellationToken ct)
    {
        if (await _ready.Task.WaitAsync(ct) is null) return new NoteAnswer { Refused = "ChalkQL didn't start." };
        if (People.Find(person) is not { } who) return new NoteAnswer { Refused = $"No such person: {person}." };
        var asked = new NoteRequest(who, section);
        _work.Writer.TryWrite(asked);
        return await asked.Done.Task.WaitAsync(ct);
    }

    /// <summary>
    ///     The access log as a person may read it (the audit page): who was shown a record (<paramref name="part" />, 0 an
    ///     activity or 3 a section; <paramref name="seq" />), or with none (0) its newest entries. Read once every entry
    ///     worked out so far is in its database.
    /// </summary>
    public async Task<AuditAnswer> AuditAsync(int person, byte part, long seq, CancellationToken ct)
    {
        if (await _ready.Task.WaitAsync(ct) is null) return new AuditAnswer { Refused = "ChalkQL didn't start." };
        if (People.Find(person) is not { } who) return new AuditAnswer { Refused = $"No such person: {person}." };
        await _accessStore.FlushedAsync(ct);
        var asked = new AuditRequest(who, part, seq);
        _work.Writer.TryWrite(asked);
        return await asked.Done.Task.WaitAsync(ct);
    }

    /// <summary>
    ///     Who was shown a record (<c>/access</c>): an activity, or a section, from the access log, as the auditor reads it.
    /// </summary>
    public async Task<ShownTo[]> ShownToAsync(byte part, long seq, CancellationToken ct)
    {
        if (await _ready.Task.WaitAsync(ct) is null) return [];
        await _accessStore.FlushedAsync(ct); // (every entry worked out so far, in its database)
        var asked = new AccessRequest(part, seq);
        _work.Writer.TryWrite(asked);
        return await asked.Done.Task.WaitAsync(ct);
    }

    /// <summary>
    ///     The provider's view (<c>/view</c>, posted by the router's viewer): a person's topic as the router holds it, its
    ///     newest activity's full record from this service's records, as that person may see it, and who was shown it.
    /// </summary>
    public async Task<string> ViewAsync(JsonElement envelope, CancellationToken ct)
    {
        var label = envelope.TryGetProperty("topic", out var topic) && topic.TryGetProperty("label", out var l) ? l.GetString() : null;
        if (label is null || !label.StartsWith("home:", StringComparison.Ordinal) || !int.TryParse(label.AsSpan(5), out var id) ||
            People.Find(id) is not { } person)
            return ViewPages.Page("Not a person's topic", $"<p>{ViewPages.Esc(label ?? "(no topic)")} holds nothing personal: nothing more to show.</p>");
        long focus = 0, held = 0;
        if (envelope.TryGetProperty("rendered", out var rendered) && rendered.ValueKind == JsonValueKind.Object &&
            rendered.TryGetProperty("entries", out var entries))
        {
            held = rendered.TryGetProperty("rows", out var rows) ? rows.GetInt64() : 0; // (all of its rows, not just those rendered)
            foreach (var entry in entries.EnumerateArray())
                if (entry.TryGetProperty("key", out var key) && key.TryGetProperty("section", out var part) &&
                    part.GetString() == "activity" && key.TryGetProperty("seq", out var seq))
                    focus = Math.Max(focus, seq.GetInt64());
        }
        if (await _ready.Task.WaitAsync(ct) is null) return ViewPages.Page("Unavailable", "<p>ChalkQL didn't start.</p>");
        var asked = new ViewRequest(person, focus, held, label);
        _work.Writer.TryWrite(asked);
        return await asked.Done.Task.WaitAsync(ct);
    }

    /// <summary>
    ///     Every open topic checked against its statements run whole (<c>/oracle</c>), at the loop's epoch: a person's topic
    ///     against their class's statements (or, a patient's, their own) and their own rows, a screen's, the statistics'
    ///     and a dispatcher's against theirs. One that differs is sent its whole state again, and the log says so.
    /// </summary>
    public async Task<OracleReport[]> OracleAsync(CancellationToken ct)
    {
        if (await _ready.Task.WaitAsync(ct) is null) return [];
        var asked = new OracleRequest();
        _work.Writer.TryWrite(asked);
        return await asked.Done.Task.WaitAsync(ct);
    }

    /// <summary>The dispatch log as the loop has it now (<c>/dispatches</c>): every notification, and every entry.</summary>
    public async Task<DispatchLog> DispatchLogAsync(CancellationToken ct)
    {
        if (await _ready.Task.WaitAsync(ct) is null) return new DispatchLog([], [], []);
        var asked = new LogRequest();
        _work.Writer.TryWrite(asked);
        return await asked.Done.Task.WaitAsync(ct);
    }

    private async Task<CommandAnswer> ReportAsync(DispatchReport report, CancellationToken ct)
    {
        if (await _ready.Task.WaitAsync(ct) is null)
            return new CommandAnswer { Refused = "The practice's records are unavailable: ChalkQL didn't start." };
        _work.Writer.TryWrite(report);
        return await report.Done.Task.WaitAsync(ct);
    }

    private async Task<CommandAnswer> CommandAsync(Command command, CancellationToken ct)
    {
        if (await _ready.Task.WaitAsync(ct) is null)
            return new CommandAnswer { Refused = "The practice's records are unavailable: ChalkQL didn't start." };
        _work.Writer.TryWrite(command);
        return await command.Done.Task.WaitAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        PracticeStore store;
        try
        {
            await _accessLog.ReconcileAsync(stop); // (intents an instance before this one left without an outcome)
            store = await PracticeStore.StartAsync(_feedRows, _accessStore, _chalkLog, stop);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e,
                "Practice: ChalkQL didn't start (its planner needs a Java 21 runtime: JAVA_HOME, or Homebrew's openjdk@21)");
            _ready.TrySetResult(null);
            return;
        }

        await using var _ = store;
        try
        {
            await StartAsync(store, stop);
            await RunAsync(store, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _log.LogCritical(e, "Practice: the loop failed");
            _ready.TrySetResult(null);
            throw;
        }
        finally
        {
            if (_lease is { } lease) // (stopping: the next instance writes at once, told so, not once the lease lapses)
            {
                await _practiceLog.ResignAsync(lease, CancellationToken.None);
                StepDown("stopping");
            }

            if (_openHere.Count > 0 && _signal is not null) // (what it held open, closed on every instance: it's gone)
                await _practiceLog.AppendAsync(Entries.Instance, _origin, Entries.Body(new InstanceEntry(_name, _origin)), null,
                    CancellationToken.None);
            if (_signal is not null) await _signal.DisposeAsync();
            foreach (var notify in _notify.Values) notify.Pipe.Complete(); // (their readers post what they hold, then close)
        }
    }

    /// <summary>
    ///     The patients' statements prepared (one plan each, from a shape), the classes' (one plan for every class of a
    ///     shape), and the last hour and a half primed (its seeding plans them all again, on its rows). One
    ///     activity is every person's, a command's check among them: its shape leaves every name open. A patient's
    ///     statements are only patients' and carers': theirs leaves open what one of them may hold (who they are, and a
    ///     carer's grants), every staff role's grants folded as none. A shape serves every principal it could bind, and
    ///     the manager may only count a section's reason: a plan that also served a manager could read none.
    /// </summary>
    private async Task StartAsync(PracticeStore store, CancellationToken stop)
    {
        var (jane, tom) = (People.Find(1001)!, People.Find(3001)!);
        var everyone = store.Entitlements.Bind(store.Policy.For(jane)).Shape();
        var personal = store.Entitlements.Bind(store.Policy.For(jane)).Shape([
            "user", .. store.Entitlements.Explain(store.Policy.For(tom)).Where(g => g.Role == "carer").Select(g => g.List)
        ]);
        _patientSchedule = await Shaped.PrepareAsync(store, PracticeStore.PatientSchedule, personal, stop);
        _patientSections = await Shaped.PrepareAsync(store, PracticeStore.PatientSections, personal, stop);
        _oneActivity = await Shaped.PrepareAsync(store, PracticeStore.OneActivity, everyone, stop);
        _oneSection = await Shaped.PrepareAsync(store, PracticeStore.OneSection, personal, stop);
        _carerSchedule = await Shaped.PrepareAsync(store, PracticeStore.CarerSchedule, personal, stop);
        _carerSections = await Shaped.PrepareAsync(store, PracticeStore.CarerSections, personal, stop);
        _carerActivity = await Shaped.PrepareAsync(store, PracticeStore.CarerActivity, everyone, stop);
        _carerSection = await Shaped.PrepareAsync(store, PracticeStore.CarerSection, personal, stop);
        _release = await store.Entitled.PrepareAsync(PracticeStore.Release,
            store.Entitlements.Bind(store.Policy.ForNotifier()), ct: stop);
        _note = await store.Entitled.PrepareAsync(PracticeStore.Note, everyone, ct: stop); // (no reason in it: every principal's)
        _shownTo = await store.Entitled.PrepareAsync(PracticeStore.ShownTo, store.Entitlements.Bind(store.Policy.ForAuditor()),
            ct: stop);
        await ShapesAsync(store, stop);
        // This instance, in the log: started (whatever was open on an instance of its id before, forgotten). The log's run
        // from its start in the loop, its lease asked for; the writer of an empty log primes the practice in its first turn.
        await _practiceLog.AppendAsync(Entries.Instance, _origin, Entries.Body(new InstanceEntry(_name, null)), null, stop);
    }

    /// <summary>What the practice did, to be written: its activity, its sections, and the sections' grants as consented.</summary>
    private void Written(Happened happened, Writes writes)
    {
        if (happened.Activity is { } activity) Written(activity, writes);
        foreach (var section in happened.Sections) Written(section, writes);
        foreach (var ack in happened.Acks) Written(ack, writes);
    }

    private void Written(Activity activity, Writes writes)
    {
        writes.Activities.Add(activity);
        _patientOf[activity.Seq] = activity.PatientId;
        _activityVisit[activity.Seq] = (activity.Booking, activity.ClinicianId);
        var sensitive = _simulator.Visits.TryGetValue(activity.Booking, out var visit) && visit.Reason.Sensitive;
        _carers.Written(activity, sensitive, writes);
        if (_live && activity.Kind is "called" or "reminded" or "replied")
            _raise.Add((activity, null)); // (its patient told, and their carers)
    }

    /// <summary>A section written, with its grants (as consented, as carers' scopes show it); a result to review, kept.</summary>
    private void Written(Section section, Writes writes)
    {
        writes.Write(section);
        _sectionPatient[section.Seq] = section.PatientId;
        _sectionVisit[section.Seq] = (section.Booking, section.AttendingId);
        _consents.Written(section, writes);
        _carers.Written(section, writes);
        if (section is { Kind: "result", Released: false }) _results[section.Seq] = section;
        else if (_results.Remove(section.Seq) && _live) _raise.Add((null, section)); // (a result released: its patient told)
    }

    private void Written(Ack ack, Writes writes)
    {
        writes.Acks.Add(ack);
        if (ack.Kind == "acknowledged") _acknowledged.Add(ack.ActivitySeq);
        else if (ack.PersonRole is not ("patient" or "carer"))
            (_, _) = (_read.Add(ack.ActivitySeq), _seenBy.Add((ack.ActivitySeq, ack.PersonId)));
    }

    private async Task RunAsync(PracticeStore store, CancellationToken stop)
    {
        List<Open> opens = [];
        List<Close> closes = [];
        List<OracleRequest> oracles = [];
        _ = WatchAsync(stop);
        while (!stop.IsCancellationRequested)
        {
            // Woken by work, by the log (another instance appended), or, writing, for the practice's next step; and every
            // so often for the lease.
            var wait = Writing && _nextStep - DateTime.UtcNow < LeaseEvery ? _nextStep - DateTime.UtcNow : LeaseEvery;
            if (wait > TimeSpan.Zero && !_work.Reader.TryPeek(out _))
            {
                using var due = CancellationTokenSource.CreateLinkedTokenSource(stop);
                due.CancelAfter(wait);
                try
                {
                    await _work.Reader.WaitToReadAsync(due.Token);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                }
            }

            using var busy = _load.Measure(); // (from here to the iteration's end: the loop's load, its health reports')
            opens.Clear();
            closes.Clear();
            oracles.Clear();
            var now = DateTime.UtcNow;
            while (_work.Reader.TryRead(out var work))
                switch (work)
                {
                    case Open open:
                        opens.Add(open);
                        break;
                    case Close close:
                        closes.Add(close);
                        break;
                    case Command command: // (the writer's to take in, in its sender's order: another instance writes, otherwise)
                        if (Writing) Admit(command, now);
                        else command.Done.TrySetResult(NotWriter());
                        break;
                    case DispatchReport report:
                        if (Writing) _reportsIn.Add(report);
                        else report.Done.TrySetResult(NotWriter());
                        break;
                    case DrainRequest drain:
                        await DrainAsync(drain, stop);
                        break;
                    case LogAdvanced: // (read below)
                        break;
                    case ExplainRequest explain: // (nothing run: the policy, folded for this person's statements)
                        explain.Done.TrySetResult(await ExplainedAsync(store, explain.Person, stop));
                        break;
                    case NoteRequest note:
                        note.Done.TrySetResult(await NoteOfAsync(store, note.Person, note.Section, stop));
                        break;
                    case AuditRequest audit:
                        audit.Done.TrySetResult(await AuditedAsync(store, audit, stop));
                        break;
                    case MeasureRequest measure:
                        measure.Done.TrySetResult(Measurement(measure.Reset));
                        break;
                    case OracleRequest oracle: // (after this turn's writes and posts: every open topic, whole)
                        oracles.Add(oracle);
                        break;
                    case ViewRequest view:
                        view.Done.TrySetResult(await RenderViewAsync(store, view, stop));
                        break;
                    case AccessRequest access: // (as the auditor: written up to the last refresh)
                        try
                        {
                            access.Done.TrySetResult(await ShownToAsync(store, access.Part, access.Seq, stop));
                        }
                        catch (Exception e) when (e is not OperationCanceledException) // (a read's failure the caller's, not the loop's)
                        {
                            _log.LogWarning(e, "Practice: reading the access log failed");
                            access.Done.TrySetException(e);
                        }

                        break;
                    case LogRequest asked:
                        asked.Done.TrySetResult(new DispatchLog([.. _notifications.All],
                            [.. _notifications.Entries.OrderBy(e => e.Id)], [.. _notifications.Notices]));
                        break;
                }

            await FollowAsync(store, stop);
            await LeadAsync(store, now, stop);
            Unhold(now);
            _signal?.Want(!Writing); // (following: the writer's signal heard; writing, let go)
            if (Writing && (!_live || _commandsIn.Count > 0 || _reportsIn.Count > 0 || _unfenced.Count > 0 || now >= _nextStep ||
                            now - _clocked >= ClockEvery)) // (another instance's entries: placed by a turn at once)
                await TurnAsync(store, now, stop);
            if (_live && !_ready.Task.IsCompleted && (Writing || _caughtUp)) // (primed, and caught up: step 1 answered from here)
            {
                _ready.TrySetResult(store);
                _log.LogInformation("Practice: {Name} ready, {Role}: {Turns} turns of its log run ({Store})", _name,
                    Writing ? "writing" : $"following {_holder}", _turns, StoreName);
            }

            foreach (var close in closes) Closed(close.Notice);
            foreach (var open in opens) await OpenAsync(store, open, stop);
            await AppendLocalAsync(store, stop);
            await CatchUpAsync(store, stop);
            foreach (var oracle in oracles)
            {
                List<OracleReport> reports = [];
                foreach (var topic in Topics()) reports.Add(await CheckAsync(store, topic, stop));
                oracle.Done.TrySetResult([.. reports]);
            }

            if (_oracleEvery is { } every && DateTime.UtcNow >= _nextOracle) // (one topic a turn, round the open ones)
            {
                _nextOracle = DateTime.UtcNow + every;
                var topics = Topics();
                if (topics.Count > 0) await CheckAsync(store, topics[(int)(_oracleTurn++ % topics.Count)], stop);
            }

            LetIdleGo(DateTime.UtcNow);
            Volatile.Write(ref _views, new ViewsReport(_viewPolicy.Maintenance.ToString(), _viewPolicy.Validate,
            [
                .. _classes.Values.SelectMany(c => c.Views).Select(v => v.Report()),
                .. _screens.Values.Select(s => s.View.Report()),
                .. _stats.Values.SelectMany(s => s.Views).Select(v => v.Report()),
                .. _notify.Values.Select(n => n.View.Report())
            ], [.. _gaveWay], _oracleChecks, [.. _oracleMismatches], _viewPolicy.PlanSwap.ToString(),
            [.. _notify.Values.Select(n => new PipeReport(n.Name, n.Pipe.Capacity, n.Pipe.MaxInFlight, n.HandedOn, n.Deferred))]));
            Volatile.Write(ref _status, Status());
        }
    }

    /// <summary>Whether this instance writes the practice's records: it holds the log's lease, and isn't draining.</summary>
    private bool Writing => _lease is not null && !_draining;

    private string StoreName => _practiceLog is SqlitePracticeLog sqlite ? sqlite.Path : "memory";

    /// <summary>
    ///     A turn of the writer's: its inputs logged (fenced by the lease: a writer that lost it adds nothing, and steps
    ///     down), then the other entries read before it applied, where it says (only once it's in the log: as every instance
    ///     applies them), then run, as every other instance will run it; its commands and reports answered as it runs. Logged
    ///     before it runs: a writer that stops mid-turn is replayed from it. The practice's first turn primes it, with this
    ///     instance's settings.
    /// </summary>
    private async Task TurnAsync(PracticeStore store, DateTime now, CancellationToken stop)
    {
        await FollowAsync(store, stop); // (anything appended since)
        var upTo = _unfenced.Count > 0 ? _unfenced[^1].Seq : _upTo; // (presence read so far: this turn places it)
        var stepped = _live && now >= _nextStep;
        var turn = new TurnEntry
        {
            Now = now, Stepped = stepped, Commands = [.. _commandsIn.Select(Input)], Reports = [.. _reportsIn.Select(Input)],
            UpTo = upTo, PreviousSeq = _lastTurn, PreviousDigest = _lastDigest, Prime = _live ? null : _settings
        };
        if (await _practiceLog.AppendAsync(Entries.Turn, _origin, Entries.Body(turn), _lease, stop) is not { } seq)
        {
            StepDown("another instance holds the lease now"); // (what it read, placed by that one's turns: not applied here)
            return;
        }

        Signal(seq); // (the instances that follow read it while this one runs it)
        await ApplyUnfencedAsync(store, upTo, stop); // (where the turn says, as every instance applies it)
        if (stepped) _nextStep = now - _nextStep > _stepEvery ? now + _stepEvery : _nextStep + _stepEvery; // (behind: no catching up)
        Command[] commands = [.. _commandsIn];
        DispatchReport[] reports = [.. _reportsIn];
        _commandsIn.Clear();
        _reportsIn.Clear();
        _ownTurns.Add(seq);
        await RunTurnAsync(store, turn, seq, commands, reports, stop);
    }

    /// <summary>
    ///     A turn run, the writer's own (its commands and reports to answer, as taken in) or, following, another's (rebuilt
    ///     from the log): the commands checked and written, the reports, the practice's step, the refresh and its posts, the
    ///     notifications, emergencies over; then each command's rendezvous on its actor's routes here. What it wrote is
    ///     digested: the writer's next turn says its own, for every other instance to check against.
    /// </summary>
    private async Task RunTurnAsync(PracticeStore store, TurnEntry turn, long seq, Command[]? commands,
        DispatchReport[]? reports, CancellationToken stop)
    {
        var now = turn.Now;
        if (turn.Prime is { } settings)
        {
            await PrimeAsync(store, settings, now, stop);
            Ran(seq);
            return;
        }

        var writes = _turnWrites;
        writes.Clear();
        _answered.Clear();
        _reported.Clear();
        long began = System.Diagnostics.Stopwatch.GetTimestamp(), checkedAt, steppedAt, refreshedAt, fannedAt, notifiedAt, expiredAt;
        var consent = false; // (a consent shared or withdrawn: the turn's times logged, slow or not)
        if (_measure) _turnMicros = Rows.Micros(now) - 1000; // (this turn's activities: at or after it, a millisecond's give)
        for (var i = 0; i < turn.Commands.Length; i++)
        {
            if (turn.Commands[i] is { OrderKey.Length: > 0 } ordered) // (its sender's order: the last number run, every instance's alike)
                _sequenced[(ordered.Sender, ordered.OrderKey)] =
                    Math.Max(_sequenced.GetValueOrDefault((ordered.Sender, ordered.OrderKey)), ordered.Seq);
            var command = commands?[i] ?? Command.From(turn.Commands[i]);
            consent |= command.Kind is CommandKind.Share or CommandKind.Withdraw;
            if (_answers.TryGetValue(turn.Commands[i].Correlation, out var already)) // (a retry: run once, answered so again)
            {
                command.Done.TrySetResult(already);
                continue;
            }

            var (actor, answer, activity, acks) = await CheckAsync(store, command, Unspecified(now), writes, stop);
            if (activity is not null) Written(activity, writes);
            foreach (var ack in acks) Written(ack, writes);
            Answered(turn.Commands[i].Correlation, answer);
            if (actor is null) command.Done.TrySetResult(answer);
            else _answered.Add((command, actor, answer));
        }

        for (var i = 0; i < turn.Reports.Length; i++)
        {
            var report = reports?[i] ?? DispatchReport.From(turn.Reports[i]);
            if (_notifications.Report(report.Dispatcher, report.Shard, report.Seq, report.Outcome, report.Reason,
                    Unspecified(now), writes) is { } why)
                report.Done.TrySetResult(new CommandAnswer { Refused = why });
            else _reported.Add(report);
        }

        checkedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (turn.Stepped)
        {
            if (_simulator.Step(Unspecified(now)) is { } happened)
            {
                Written(happened, writes);
                if (_measure) _activities++;
            }

            // The practice's other GPs review their results too: one a step, eight minutes on (the demo's are Dr Patel's to release).
            if (_results.Values.FirstOrDefault(r => !Simulator.Demo.Contains(r.PatientId) &&
                                                    r.WrittenAt < Unspecified(now).AddMinutes(-8)) is { } due)
                Written(due with { Released = true }, writes);
        }

        steppedAt = refreshedAt = fannedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (writes.Any || now - _clocked >= ClockEvery)
        {
            writes.Shown.AddRange(_shownStash); // (the access log: what every instance posted since, digested with this refresh)
            _shownStash.Clear();
            await RefreshAsync(store, writes, Clock(now), stop);
            if (writes.Shown.Count > 0) await _accessStore.PostAsync(writes.Shown, stop); // (and to its own database, off the loop)
            _clocked = now;
            refreshedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            await FanOutAsync(store, writes, now, stop);
            fannedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_measure) Turned(steppedAt, refreshedAt);
        }

        await NotifyAsync(store, Unspecified(now), stop);
        notifiedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        await ExpireAsync(store, now, stop);
        expiredAt = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var (command, actor, answer) in _answered)
            {
                if (answer.Done && command.Kind == CommandKind.RevokeCarer && _homes.TryGetValue((int)command.Booking, out var carer))
                    await SnapshotPatientAsync(store, carer, now, stop); // (what the designation showed, gone from their feed)
                if (answer.Done && _homes.TryGetValue(actor.Id, out var mine)) // (its own rows, or its class, changed)
                    if (command.Kind == CommandKind.BreakGlass) await ReclassAsync(store, mine, now, stop);
                    else if (command.Kind is CommandKind.Share or CommandKind.Withdraw or CommandKind.RevokeCarer) PostOwn(mine);
                // Read your writes: the actor's feed has the change when the id comes back — on each of their routes. Not
                // while their site falls behind: their topic is held, the change not in it yet.
                if (answer.Done && !Held(actor) && _homes.TryGetValue(actor.Id, out var home) && home.Ingest is { } ingest)
                    foreach (var mailbox in home.Routes.Values)
                        ingest.TryRendezvous(mailbox, command.Correlation);
                if (answer.Done && actor.Role == Role.Manager && _stats.TryGetValue(PracticePolicy.ClassOf(actor), out var stats))
                    foreach (var (mailbox, person) in stats.Routes.Values) // (the manager's own routes of their statistics)
                        if (person == actor.Id)
                            stats.Ingest.TryRendezvous(mailbox, command.Correlation);
                command.Done.TrySetResult(answer);
            }

            foreach (var report in _reported) // (once its removal is in front of the dispatcher: a reconnect can't act on it again)
            {
                if (_notify.TryGetValue(report.Shard, out var notify))
                    foreach (var (mailbox, dispatcher) in notify.Routes.Values)
                        if (dispatcher == report.Dispatcher) // (its topic's reader's, after the version that removed it)
                            notify.Owed.Add((store.Engine.Version, mailbox, report.Correlation));
                report.Done.TrySetResult(new CommandAnswer { Done = true, Seq = report.Seq });
            }

            if (_reported.Count > 0)
                foreach (var notify in _notify.Values)
                    if (notify.Owed.Count > 0)
                        await AdvanceAsync(store, notify, stop);

        Ran(seq);
        var took = System.Diagnostics.Stopwatch.GetElapsedTime(began);
        if (took > TimeSpan.FromMilliseconds(250) || consent) // (a slow turn: where it went, for a writer late with its turns or a replica behind)
        {
            static double Ms(long from, long to) => System.Diagnostics.Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
            _log.LogInformation(
                "Practice: {Name} ran turn {Seq} ({Role}{Consent}) in {Took:F0} ms: {Commands} commands and {Reports} reports {Checks:F0}, " +
                "step {Step:F0}, refresh {Refresh:F0}, fan-out {FanOut:F0}, notifications {Notify:F0}, emergencies {Expire:F0}, " +
                "rendezvous {After:F0}", _name, seq, commands is null ? "following" : "writing", consent ? ", a consent changed" : "",
                took.TotalMilliseconds,
                turn.Commands.Length, turn.Reports.Length, Ms(began, checkedAt), Ms(checkedAt, steppedAt), Ms(steppedAt, refreshedAt),
                Ms(refreshedAt, fannedAt), Ms(fannedAt, notifiedAt), Ms(notifiedAt, expiredAt),
                Ms(expiredAt, System.Diagnostics.Stopwatch.GetTimestamp()));
        }
    }

    /// <summary>A refresh of ChalkQL's records, digested: what the turn wrote, in order. Its planning deferred, as every refresh's.</summary>
    private ValueTask<long> RefreshAsync(PracticeStore store, Writes writes, DateTimeOffset clock, CancellationToken stop)
    {
        _digest.Add(writes, clock);
        return store.WriteAsync(writes, clock, stop);
    }

    /// <summary>A turn run: what it wrote, digested and kept, for the writer's next turn to be checked against.</summary>
    private void Ran(long seq)
    {
        _lastTurn = seq;
        _lastDigest = _digest.Finish();
        _turns++;
        _ran[seq] = _lastDigest;
        _ranOrder.Enqueue(seq);
        if (_ranOrder.Count > 4096) _ran.Remove(_ranOrder.Dequeue());
    }

    /// <summary>A command's answer, kept by its correlation id: a retry (the writer gone mid-turn, say) is answered so, not run again.</summary>
    private void Answered(string correlation, CommandAnswer answer)
    {
        if (!_answers.TryAdd(correlation, answer)) return;
        _answersOrder.Enqueue(correlation);
        if (_answersOrder.Count > 4096) _answers.Remove(_answersOrder.Dequeue());
    }

    /// <summary>
    ///     The practice's first turn: its settings taken, its devices written, its history primed (each step as the pace says,
    ///     back from now), Jane's visit with Dr Okafor, in one refresh, its planning deferred, then run and awaited
    ///     (ChalkQL's <c>PlanAsync</c>): every statement prepared before the practice's rows were in (the patients', the
    ///     classes') is planned on them before any feed opens, and no view is built on a plan made on empty tables. From
    ///     here, what happens is notified.
    /// </summary>
    private async Task PrimeAsync(PracticeStore store, PracticeSettings settings, DateTime now, CancellationToken stop)
    {
        Adopt(settings);
        var writes = _turnWrites;
        writes.Clear();
        foreach (var device in People.Devices) writes.Write(device);
        for (var at = now - _history; at < now; at += _stepEvery)
            if (_simulator.Step(Unspecified(at), false) is { } happened)
                Written(happened, writes);
        // Jane also sees Dr Okafor, for her diabetes: a visit with him a quarter of an hour ago, which he attended.
        foreach (var happened in _simulator.Visited(People.Find(1001)!, People.Find(202)!, Unspecified(now.AddMinutes(-16))))
            Written(happened, writes);
        await RefreshAsync(store, writes, Clock(now), stop);
        _clocked = now;
        var planned = await PlannedAsync(store, stop);
        _log.LogInformation(
            "Practice: primed — {Activities} activities and {Sections} sections over the last {Minutes} minutes, " +
            "{Grants} sections shared; {Planned}; patients' plans {Schedule:x16} and {Sections2:x16}, from one shape " +
            "(patients' and carers')", writes.Activities.Count, writes.Sections.Count, _history.TotalMinutes,
            writes.Granted.Count, planned, _patientSchedule!.Query.PlanDigest, _patientSections!.Query.PlanDigest);
        _live = true;
    }

    /// <summary>
    ///     What deferred planning left, planned now and awaited (ChalkQL's <c>PlanAsync</c>), in words: how long it took, and
    ///     whether a plan changed. A failure leaves the statements it couldn't plan on the plans they have, and is said.
    /// </summary>
    private async Task<string> PlannedAsync(PracticeStore store, CancellationToken stop)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var changed = await store.Engine.PlanAsync(stop);
            return $"planned in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms " +
                   (changed ? "(new plans in effect)" : "(no plan changed)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "Practice: planning the seeded practice failed: what it couldn't plan keeps the plan it has");
            return $"planning failed after {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms ({e.Message})";
        }
    }

    /// <summary>The practice's settings taken: its history and pace, emergencies' length, the grace, the release job's, quiet hours.</summary>
    private Notifications Adopt(PracticeSettings settings)
    {
        _history = TimeSpan.FromMinutes(settings.HistoryMinutes);
        _stepEvery = TimeSpan.FromSeconds(settings.StepSeconds);
        _breakGlass = TimeSpan.FromSeconds(settings.BreakGlassSeconds);
        _releaseEvery = TimeSpan.FromSeconds(settings.ReleaseSeconds);
        return _notifications = new Notifications(TimeSpan.FromSeconds(settings.SeenGraceSeconds),
            Budgets.Of(settings.QuietHours, settings.TimeZone));
    }

    /// <summary>
    ///     The log, read on from the last entry read: another writer's turns run (each after the entries of other instances
    ///     it says it had applied, its predecessor's digest checked against this instance's), this instance's own skipped
    ///     (run as they were logged), and the rest (presence, posts, instances) kept until the turn that includes them.
    /// </summary>
    private async Task FollowAsync(PracticeStore store, CancellationToken stop)
    {
        while (true)
        {
            _entries.Clear();
            await _practiceLog.ReadAsync(_logRead, LogBatch, _entries, stop);
            foreach (var entry in _entries)
            {
                Volatile.Write(ref _logRead, entry.Seq);
                if (entry.Kind != Entries.Turn)
                {
                    _unfenced.Add(entry);
                    continue;
                }

                if (_ownTurns.Remove(entry.Seq)) continue;
                var read = DateTime.UtcNow - entry.At;
                var turn = Entries.Of<TurnEntry>(entry.Body);
                await ApplyUnfencedAsync(store, turn.UpTo, stop);
                if (turn.PreviousDigest is { } theirs && _ran.TryGetValue(turn.PreviousSeq, out var ours) && ours != theirs)
                    Diverged(turn.PreviousSeq, theirs, ours);
                await RunTurnAsync(store, turn, entry.Seq, null, null, stop);
                if (!_ready.Task.IsCompleted) continue; // (catching up: not lag)
                var lag = DateTime.UtcNow - entry.At;
                _lag.Add(lag.TotalMilliseconds);
                _heard.Add(read.TotalMilliseconds);
                if (read > TimeSpan.FromMilliseconds(100)) // (late to this instance: the log's to explain, not the turn)
                    _log.LogInformation("Practice: {Name} read turn {Seq} {Read:F0} ms after it was appended, ran it in {Run:F0} ms",
                        _name, entry.Seq, read.TotalMilliseconds, (lag - read).TotalMilliseconds);
            }

            if (_entries.Count >= LogBatch) continue;
            _caughtUp = _entries.Count == 0; // (a read that found nothing new: everything logged till then run here)
            return;
        }
    }

    /// <summary>
    ///     The entries other than turns read so far, up to <paramref name="upTo" />, applied in order, where every instance
    ///     applies them: whose feeds are open where (what a notification's grace asks; a feed open on any instance is worked
    ///     out on every one, what it's shown with it), and instances started or gone.
    /// </summary>
    private async Task ApplyUnfencedAsync(PracticeStore store, long upTo, CancellationToken stop)
    {
        var applied = 0;
        try
        {
            for (; applied < _unfenced.Count && _unfenced[applied].Seq <= upTo; applied++)
            {
                var entry = _unfenced[applied];
                _upTo = entry.Seq;
                switch (entry.Kind)
                {
                    case Entries.Presence: // (its closes first: an instance's closes in a pass come before its opens)
                        var presence = Entries.Of<PresenceEntry>(entry.Body);
                        foreach (var person in presence.Closed) ClosedOn(person, entry.Origin);
                        foreach (var person in presence.Opened) await OpenedOnAsync(store, person, entry.Origin, entry.At, stop);
                        break;
                    case Entries.Instance: // (started: nothing open there yet; gone: what it held open, closed)
                        var instance = Entries.Of<InstanceEntry>(entry.Body);
                        var forgotten = instance.Gone ?? entry.Origin;
                        foreach (var person in _openOn.Where(o => o.Value.Contains(forgotten)).Select(o => o.Key).ToList())
                            ClosedOn(person, forgotten);
                        break;
                }
            }
        }
        finally
        {
            _unfenced.RemoveRange(0, applied);
        }
    }

    /// <summary>
    ///     A person's feed opened on an instance, where the log has it: made here too if it's open on no other instance
    ///     (every instance works out what it's shown from here: its first state, now); this instance's own, its topic made and
    ///     the step 1s waiting for it accepted. ChalkQL refusing the person's statements refuses them, on every instance alike.
    /// </summary>
    private async Task OpenedOnAsync(PracticeStore store, int person, string origin, DateTime at, CancellationToken stop)
    {
        if (!_openOn.TryGetValue(person, out var on)) _openOn[person] = on = [];
        on.Add(origin);
        if (People.Find(person) is not { } who) return;
        if (!_homes.TryGetValue(person, out var home))
            try
            {
                _homes[person] = home = await HomeOfAsync(store, who, at, stop);
            }
            catch (Exception e) when (e is ChalkException or EntitlementException or StandingViewException)
            {
                _log.LogWarning("Practice: {Person}'s feed refused: ChalkQL: {Reason}", who.Name, e.Message);
                if (origin != _origin) return;
                foreach (var open in TakeWaiting(person))
                {
                    var correlation = open.Request.Refuse($"ChalkQL: {e.Message}");
                    _log.LogWarning("Practice: {Person}'s step 1 refused (correlation {Correlation})", who.Name, correlation);
                    open.Done.TrySetResult();
                }

                if (_openHere.Remove(person)) _closed.Add(person); // (open here no longer: the log's to say)
                return;
            }

        if (origin == _origin) await HereAsync(store, home, stop);
    }

    /// <summary>A person's feed closed on an instance, where the log has it: once it's open on none, let go here too.</summary>
    private void ClosedOn(int person, string origin)
    {
        if (!_openOn.TryGetValue(person, out var on) || !on.Remove(origin) || on.Count > 0) return;
        if (!_homes.Remove(person, out var home)) return;
        if (home.Class is { } cls && cls.Members.Remove(home) && cls.Members.Count == 0) cls.IdleSince = DateTime.UtcNow;
        home.Ingest?.Close(); // (none: closed here first, as the log has it)
    }

    /// <summary>The step 1s waiting for a person's feed here, taken from the queue in the order they came.</summary>
    private List<Open> TakeWaiting(int person)
    {
        var taken = _waiting.FindAll(o => o.Person?.Id == person);
        _waiting.RemoveAll(o => o.Person?.Id == person);
        return taken;
    }

    /// <summary>
    ///     The writer's lease, every so often: renewed while held (a third of its term gone); otherwise taken if it's free or
    ///     lapsed (a drain resigned it, or its holder went), after every entry already logged has run here.
    /// </summary>
    private async Task LeadAsync(PracticeStore store, DateTime now, CancellationToken stop)
    {
        if (_draining || (now < _leaseCheck && !_leaseNow)) return;
        (_leaseCheck, _leaseNow) = (now + LeaseEvery, false);
        if (_lease is { } held && held.Until - now > LeaseTerm * 2 / 3) return;
        var answer = await _practiceLog.LeadAsync(_origin, LeaseTerm, stop);
        _holder = answer.Holder;
        if (answer.Held is not { } lease)
        {
            if (_lease is not null) StepDown($"{answer.Holder} holds the lease now");
            return;
        }

        if (_lease is null) // (taken: every turn already logged runs here before this instance's own)
        {
            await FollowAsync(store, stop);
            _nextStep = now + _stepEvery;
            _log.LogWarning("Practice: {Name} writes the practice's records now (epoch {Epoch}){From}", _name, lease.Epoch,
                answer.TakenFrom is { } from ? $", taking over from {from}, whose lease lapsed" : "");
            if (answer.TakenFrom is { } gone)
                await _practiceLog.AppendAsync(Entries.Instance, _origin, Entries.Body(new InstanceEntry(_name, gone)), null, stop);
            _lease = lease;
            if (_signal is not null) // (the others' signal: from here, this instance's log topic)
            {
                Volatile.Write(ref _logTopic, new LastValueSource { Label = "log", Format = Formats.Log });
                Signal(_logRead);
            }

            return;
        }

        _lease = lease;
    }

    /// <summary>This instance writes no more (the lease lost, or resigned): what it had taken in, answered so: ask the writer.</summary>
    private void StepDown(string why)
    {
        if (_lease is null) return;
        if (_logTopic is { } topic) // (its followers told the lease is let go, then the topic ended: the next writer's to bind)
        {
            Signal(_logRead, true);
            topic.Fail(FaultCode.SourceFailed, $"{_name} doesn't write the practice's records now");
            Volatile.Write(ref _logTopic, null);
        }

        _lease = null;
        _log.LogWarning("Practice: {Name} doesn't write the practice's records now: {Why}", _name, why);
        foreach (var command in _commandsIn) command.Done.TrySetResult(NotWriter());
        foreach (var (command, _) in _held) command.Done.TrySetResult(NotWriter());
        foreach (var report in _reportsIn) report.Done.TrySetResult(NotWriter());
        _commandsIn.Clear();
        _held.Clear();
        _reportsIn.Clear();
    }

    private CommandAnswer NotWriter()
    {
        return new CommandAnswer
        {
            NotWriter = true,
            Refused = $"{_name} doesn't write the practice's records now{(_holder is { } holder && holder != _origin ? $" ({holder} does)" : "")}."
        };
    }

    /// <summary>
    ///     Draining: the lease resigned at once (another instance writes from its next look, a quarter of a second at most),
    ///     reported draining, the routers told to move this instance's clients over <c>over</c>; it follows the log meanwhile,
    ///     so what they see carries on, and stops once the drain and its grace are over.
    /// </summary>
    private async Task DrainAsync(DrainRequest drain, CancellationToken stop)
    {
        if (!_draining)
        {
            _draining = true;
            _drainUntil = DateTime.UtcNow + drain.Over;
            if (_lease is { } lease)
            {
                await _practiceLog.ResignAsync(lease, stop);
                StepDown("draining: another instance writes");
            }

            _log.LogWarning("Practice: {Name} draining over {Over} s, {Grace} s grace", _name, drain.Over.TotalSeconds,
                drain.Grace.TotalSeconds);
            _ = LeaveAsync(drain.Over, drain.Grace);
        }

        drain.Done.TrySetResult();
    }

    private async Task LeaveAsync(TimeSpan over, TimeSpan grace)
    {
        try
        {
            await _routers.DrainAsync(over, grace);
            await Task.Delay(over + grace + TimeSpan.FromSeconds(1));
        }
        finally
        {
            _log.LogWarning("Practice: {Name} drained: stopping", _name);
            _lifetime.StopApplication();
        }
    }

    /// <summary>
    ///     Whose feeds this instance opened or closed, appended: every instance opens and closes its feed of theirs where the
    ///     log has it, and works out what it's shown from there. Writing, this instance places it with a turn at once;
    ///     following, the writer's next turn does.
    /// </summary>
    private async Task AppendLocalAsync(PracticeStore store, CancellationToken stop)
    {
        if (_opened.Count == 0 && _closed.Count == 0) return;
        await _practiceLog.AppendAsync(Entries.Presence, _origin, Entries.Body(new PresenceEntry([.. _opened], [.. _closed])),
            null, stop);
        _opened.Clear();
        _closed.Clear();
        if (Writing) await TurnAsync(store, DateTime.UtcNow, stop); // (a turn of its own places it: its step 1s answered)
    }

    /// <summary>
    ///     The loop woken when the store says the log moved on (another instance appended, or this one did): what an instance
    ///     following the writer falls back on, its signal not heard (no writer bound yet, or the app gone).
    /// </summary>
    private async Task WatchAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await _practiceLog.WaitAsync(Volatile.Read(ref _logRead), stop);
                _work.Writer.TryWrite(LogAdvanced.Instance);
                await Task.Delay(5, stop); // (read by then, as a rule: not woken again for the same entry)
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The log's signal posted (writing, a shared log): its last turn, for the instances that follow, through the router.</summary>
    private void Signal(long seq, bool resigned = false)
    {
        if (_logTopic is not { } topic) return;
        var at = new LogAt { Seq = seq, Epoch = _lease?.Epoch ?? 0, AtMicros = Rows.Micros(DateTime.UtcNow), Resigned = resigned ? (byte)1 : (byte)0 };
        topic.Publish(MemoryMarshal.AsBytes(new ReadOnlySpan<LogAt>(in at)));
    }

    /// <summary>The writer's signal heard (on its client's thread): the loop woken to read the log on; the lease let go, asked for.</summary>
    private void Heard(LogAt at)
    {
        if (at.Resigned != 0) _leaseNow = true;
        _work.Writer.TryWrite(LogAdvanced.Instance);
    }

    /// <summary>The writer's signal ended, or its node drains: the writer gone or going, so the lease asked for at once.</summary>
    private void SignalEnded()
    {
        _leaseNow = true;
        _work.Writer.TryWrite(LogAdvanced.Instance);
    }

    private void Diverged(long seq, string theirs, string ours)
    {
        var what = $"turn {seq}: the writer wrote {theirs}, {_name} {ours}";
        _log.LogError("Practice: {Name} diverged from the writer at {What}", _name, what);
        if (_divergences.Count == 20) _divergences.RemoveAt(0);
        _divergences.Add(what);
    }

    private LogStatus Status()
    {
        var signal = _logTopic is { } topic ? $"posting its own: {topic.Routes} following"
            : _signal is { Node: { } node } ? $"hearing the writer's through the router (node {node})"
            : _signal is not null ? "not hearing the writer's: the store watched"
            : "none: a log of its own";
        return new LogStatus(_name, _origin, _draining ? "draining" : Writing ? "writing" : "following", _holder,
            _lease?.Epoch ?? 0, _logRead, _lastTurn, _turns, StoreName, signal, _signal?.Values ?? 0, _heard.Read(), _lag.Read(),
            [.. _divergences], _accessStore.Report());
    }

    private static CommandInput Input(Command command)
    {
        return new CommandInput(command.Kind.ToString(), command.Person, command.Booking, command.Text,
            Convert.ToHexString(command.Correlation), command.Activities, command.Ordered?.Key ?? "",
            command.Ordered?.Sender ?? "", command.Order?.Seq ?? 0);
    }

    /// <summary>
    ///     A command taken in for the writer's next turn, in its sender's order for its key (<see cref="CommandOrder" />): one
    ///     numbered next goes in, and any held for it after it, in turn; one ahead of the next is held, until those before it
    ///     come, or its hold is over (refused then, saying which it waited for); one numbered before the next is a retry, in
    ///     to be answered as it was. One with no order goes in as it comes.
    /// </summary>
    private void Admit(Command command, DateTime now)
    {
        if (command.Ordered is not { } key)
        {
            _commandsIn.Add(command);
            return;
        }

        if (command.Order!.Seq > Next(key))
        {
            _held.Add((command, now + _orderHold));
            return;
        }

        _commandsIn.Add(command);
        for (var i = 0; i < _held.Count; i++) // (held for it: next now, each in, in turn)
            if (_held[i].Command.Ordered == key && _held[i].Command.Order!.Seq <= Next(key))
            {
                _commandsIn.Add(_held[i].Command);
                _held.RemoveAt(i);
                i = -1;
            }
    }

    /// <summary>The number a sender's next command with a key must have: one after the last run, or taken in for the next turn.</summary>
    private long Next((string Sender, string Key) key)
    {
        var last = _sequenced.GetValueOrDefault(key);
        foreach (var taken in _commandsIn)
            if (taken.Ordered == key)
                last = Math.Max(last, taken.Order!.Seq);
        return last + 1;
    }

    /// <summary>Held commands whose hold is over: refused, saying which they waited for (that one may come yet, and go in).</summary>
    private void Unhold(DateTime now)
    {
        for (var i = _held.Count - 1; i >= 0; i--)
        {
            var (command, until) = _held[i];
            if (until > now) continue;
            _held.RemoveAt(i);
            command.Done.TrySetResult(new CommandAnswer
            {
                Refused = $"Out of order: {command.Ordered!.Value.Key} #{command.Order!.Seq} came, but #{Next(command.Ordered.Value)} " +
                          $"of its sender's didn't, within {_orderHold.TotalSeconds:0.#} s."
            });
        }
    }

    private static ReportInput Input(DispatchReport report)
    {
        return new ReportInput(report.Dispatcher, report.Shard, report.Seq, report.Outcome, report.Reason,
            Convert.ToHexString(report.Correlation));
    }

    /// <summary>
    ///     A command checked: the actor exists and may act so; what it's about is one they can see (an entitled read, as
    ///     themselves: one activity, their policy's); and it's at the right step. Then written: its activity, or its
    ///     acknowledgements.
    /// </summary>
    private async Task<(Person? Actor, CommandAnswer Answer, Activity? Activity, List<Ack> Acks)> CheckAsync(
        PracticeStore store, Command command, DateTime now, Writes writes, CancellationToken stop)
    {
        static (Person?, CommandAnswer, Activity?, List<Ack>) No(string why)
        {
            return (null, new CommandAnswer { Refused = why }, null, []);
        }

        if (People.Find(command.Person) is not { } actor) return No($"No such person: {command.Person}.");
        var role = command.Kind switch
        {
            CommandKind.Seen when actor.Role is Role.Patient or Role.Carer => actor.Role, // (what a notification waits for)
            CommandKind.Arrive or CommandKind.Seen or CommandKind.Reply => Role.Reception,
            CommandKind.Call or CommandKind.BreakGlass or CommandKind.Release => Role.Clinician,
            CommandKind.Notice => Role.Manager,
            CommandKind.Acknowledge when actor.Role == Role.Carer => Role.Carer, // (for the patient they care for)
            _ => Role.Patient
        };
        if (actor.Role != role)
            return No($"{actor.Name} isn't {(command.Kind == CommandKind.Acknowledge ? "patient, or their carer" : role.ToString().ToLowerInvariant())}.");
        switch (command.Kind)
        {
            case CommandKind.Share:
                if (People.Find((int)command.Booking) is not { Role: Role.Clinician } gp) return No($"No such GP: {command.Booking}.");
                if (!actor.Tags.ContainsKey(command.Text)) return No($"{actor.Name} has no tag \"{command.Text}\".");
                if (_consents.Of(actor.Id).Any(c => c.Gp == gp.Id && (c.Everything || c.Label == command.Text)))
                    return No($"{gp.Name} reads \"{command.Text}\" already.");
                var shared = _consents.Share(actor, gp.Id, command.Text, now, writes);
                _log.LogInformation("Practice: {Actor} shared \"{Tag}\" with {Gp}: consent {Consent}", actor.Name,
                    command.Text, gp.Name, shared.Id);
                return (actor, new CommandAnswer { Done = true, Seq = shared.Id }, null, []);
            case CommandKind.Withdraw:
                if (_consents.Withdraw(actor.Id, command.Booking, now, writes) is not { } withdrawn)
                    return No($"{actor.Name} has no consent {command.Booking} to withdraw.");
                _log.LogInformation("Practice: {Actor} withdrew consent {Consent} (\"{Label}\", {Gp})", actor.Name,
                    withdrawn.Id, withdrawn.Label, People.Find(withdrawn.Gp)?.Name);
                return (actor, new CommandAnswer { Done = true, Seq = withdrawn.Id }, null, []);
            case CommandKind.BreakGlass:
                if (People.Find((int)command.Booking) is not { Role: Role.Patient } patient)
                    return No($"No such patient: {command.Booking}.");
                if (!_emergencies.TryGetValue(actor.Id, out var broken)) _emergencies[actor.Id] = broken = [];
                if (broken.ContainsKey(patient.Id)) return No($"{actor.Name}'s emergency access to {patient.Name} stands already.");
                broken[patient.Id] = now + _breakGlass;
                _log.LogWarning("Practice: {Actor} broke the glass for {Patient}, until {Until:HH:mm:ss}: an emergency", actor.Name,
                    patient.Name, broken[patient.Id]);
                return (actor, new CommandAnswer { Done = true, Seq = patient.Id }, null, []);
            case CommandKind.Reply:
                var reply = command.Text.Trim();
                if (reply.Length == 0) return No("A reply says something.");
                if (System.Text.Encoding.UTF8.GetByteCount(reply) > Text120.Bytes || reply.Any(char.IsControl))
                    return No($"A reply is one line of at most {Text120.Bytes} bytes.");
                if (await VisibleAsync(store, actor, command.Booking, stop) is not { Kind: ActivityKind.Messaged } thread)
                    return No($"{actor.Name} can't see a patient's message {command.Booking}.");
                if (People.Find(thread.Patient) is not { } writer) return No($"No such patient: {thread.Patient}.");
                var replied = _simulator.Reply(writer, command.Booking, reply, now);
                _replies[replied.Seq] = actor.Id;
                _log.LogInformation("Practice: {Actor} replied to {Patient}'s message {Thread}: activity {Seq}", actor.Name,
                    writer.Name, command.Booking, replied.Seq);
                return (actor, new CommandAnswer { Done = true, Seq = replied.Seq }, replied, []);
            case CommandKind.Message:
                var text = command.Text.Trim();
                if (text.Length == 0) return No("A message says something.");
                if (System.Text.Encoding.UTF8.GetByteCount(text) > Text120.Bytes || text.Any(char.IsControl))
                    return No($"A message is one line of at most {Text120.Bytes} bytes.");
                var message = _simulator.Message(actor, text, now);
                _log.LogInformation("Practice: {Actor} wrote to the practice: activity {Seq}", actor.Name, message.Seq);
                return (actor, new CommandAnswer { Done = true, Seq = message.Seq }, message, []);
            case CommandKind.Acknowledge:
                if (await VisibleAsync(store, actor, command.Booking, stop) is not { } reminder)
                    return No($"{actor.Name} can't see activity {command.Booking}.");
                if (reminder.Kind != ActivityKind.Reminded) return No($"Activity {command.Booking} isn't a reminder.");
                if (_acknowledged.Contains(command.Booking)) return No($"Reminder {command.Booking} is acknowledged already.");
                if (_simulator.Visits.Values.FirstOrDefault(v => v.Reminder == command.Booking) is { } remindedVisit)
                    remindedVisit.Acknowledged = true;
                _log.LogInformation("Practice: {Actor} acknowledged reminder {Seq}{For}", actor.Name, command.Booking,
                    actor.Role == Role.Carer ? $", for {reminder.PatientName}" : "");
                _notifications.Acknowledged(command.Booking, actor, now, writes); // (nobody pushed it, now)
                return (actor, new CommandAnswer { Done = true, Seq = command.Booking }, null,
                    [_simulator.Ack(command.Booking, actor, actor.Role == Role.Carer ? "carer" : "patient", "acknowledged", false, now)]);
            case CommandKind.Release:
                if (!_results.TryGetValue(command.Booking, out var result)) return No($"Section {command.Booking} isn't a result to release.");
                if (result.AttendingId != actor.Id) return No($"{actor.Name} isn't the GP reviewing result {command.Booking}.");
                Written(result with { Released = true }, writes);
                _log.LogInformation("Practice: {Actor} released result {Seq} to {Patient}", actor.Name, result.Seq,
                    People.Find(result.PatientId)?.Name);
                return (actor, new CommandAnswer { Done = true, Seq = result.Seq }, null, []);
            case CommandKind.Notice:
                if (!actor.Sites.Contains((int)command.Booking)) return No($"{actor.Name} doesn't manage site {command.Booking}.");
                if (_notifications.Post(command.Text, actor, (int)command.Booking, now, writes) is not { } posted)
                    return No($"No such notice: \"{command.Text}\".");
                _log.LogInformation("Practice: {Actor} posted notice {Id} (\"{Title}\", {Class}{Urgent}) to {Site}'s patients, " +
                                    "deliver by {By:HH:mm:ss}", actor.Name, posted.Id, posted.Title, posted.Class,
                    posted.Urgent ? ", urgent" : "", People.SiteOf(posted.SiteId).Name, posted.DeliverBy);
                return (actor, new CommandAnswer { Done = true }, null, []);
            case CommandKind.RevokeCarer:
                if (_carers.Revoke(actor.Id, (int)command.Booking, now, writes) is not { } revoked)
                    return No($"{People.Find((int)command.Booking)?.Name ?? $"{command.Booking}"} isn't {actor.Name}'s carer.");
                _log.LogInformation("Practice: {Actor} revoked {Carer}'s designation ({Scope})", actor.Name,
                    People.Find(revoked.Carer)?.Name, revoked.Scope);
                _notifications.Revoked(actor.Id, revoked.Carer, now, writes); // (nothing more of the patient's for them)
                return (actor, new CommandAnswer { Done = true, Seq = revoked.Carer }, null, []);
            case CommandKind.Seen:
                List<Ack> acks = [];
                foreach (var seq in command.Activities.Distinct().Take(50))
                {
                    if (actor.Role is Role.Patient or Role.Carer) // (recorded where a notification waits for it: ended unsent)
                    {
                        if (!_notifications.Awaits(actor, seq) || acks.Any(a => a.ActivitySeq == seq)) continue;
                        if (await VisibleAsync(store, actor, seq, stop) is null) continue;
                        acks.Add(_simulator.Ack(seq, actor, actor.Role == Role.Carer ? "carer" : "patient", "seen", false, now));
                        _notifications.Seen(actor, seq, now, writes);
                        continue;
                    }

                    if (_seenBy.Contains((seq, actor.Id)) || acks.Any(a => a.ActivitySeq == seq)) continue;
                    if (await VisibleAsync(store, actor, seq, stop) is not { Kind: ActivityKind.Messaged }) continue;
                    acks.Add(_simulator.Ack(seq, actor, "reception", "seen", !_read.Contains(seq), now));
                }

                if (acks.Count > 0)
                    _log.LogInformation("Practice: {Actor} read messages {Seqs}", actor.Name,
                        string.Join(',', acks.Select(a => a.ActivitySeq)));
                return (acks.Count == 0 ? null : actor, new CommandAnswer { Done = true }, null, acks);
        }

        var step = command.Kind == CommandKind.Arrive ? ActivityKind.Booked : ActivityKind.Arrived;
        if (await VisibleAsync(store, actor, command.Booking, stop) is null)
            return No($"{actor.Name} can't see booking {command.Booking}.");
        if (!_simulator.Visits.TryGetValue(command.Booking, out var visit))
            return No($"Booking {command.Booking} isn't a visit to come.");
        if (visit.State != step)
            return No($"Booking {command.Booking} is {visit.State.ToString().ToLowerInvariant()}, not {step.ToString().ToLowerInvariant()}.");
        if (command.Kind == CommandKind.Call && command.Room is not { Length: > 0 and <= Text8.Bytes })
            return No($"A room is named in at most {Text8.Bytes} bytes.");
        var activity = command.Kind == CommandKind.Arrive
            ? _simulator.Arrive(visit, now)
            : _simulator.Call(visit, command.Room, now);
        _log.LogInformation("Practice: {Actor} {Did} booking {Booking} ({Patient}): activity {Seq}", actor.Name,
            command.Kind == CommandKind.Arrive ? "checked in" : $"called in to {command.Room}", visit.Booking,
            visit.Patient.Name, activity.Seq);
        return (actor, new CommandAnswer { Done = true, Seq = activity.Seq }, activity, []);
    }

    /// <summary>An activity as a person may see it (an entitled read, as themselves), or nothing if they can't.</summary>
    private async Task<FeedRow?> VisibleAsync(PracticeStore store, Person person, long seq, CancellationToken stop)
    {
        List<FeedKey> keys = [];
        List<FeedRow> rows = [];
        await using var read = await store.Engine.ExecuteAsync(_oneActivity!.Query.Query,
            store.Entitlements.Bind(store.Policy.For(person, EmergenciesOf(person))), [seq], ct: stop);
        await foreach (var batch in read.Batches.WithCancellation(stop))
            using (batch)
            {
                Rows.Read(batch, _oneActivity.Columns, keys, rows);
            }

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    ///     Notifications: each raised for what's just been written (a call in, a reminder, a released result), to its patient
    ///     and to each carer who can see it, and time's passing (graces over, deadlines passed), written in a refresh of
    ///     their own, and posted.
    /// </summary>
    private async Task NotifyAsync(PracticeStore store, DateTime now, CancellationToken stop)
    {
        var writes = _notifyWrites;
        writes.Clear();
        foreach (var (activity, section) in _raise) await RaiseAsync(store, activity, section, now, writes, stop);
        _raise.Clear();
        _notifications.Tick(now, writes);
        if (now >= _nextRelease)
        {
            _nextRelease = now + _releaseEvery;
            if (_notifications.Notices.Any()) _notifications.Release(await WaitingAsync(store, stop), now, writes);
        }
        if (!writes.Any) return;
        await RefreshAsync(store, writes, Clock(DateTime.SpecifyKind(now, DateTimeKind.Utc)), stop);
        await FanOutAsync(store, writes, now, stop);
    }

    /// <summary>
    ///     A notification raised: its recipients the patient, and each carer who can see what it's about (an entitled read,
    ///     as the carer: a sensitive visit's reaches no carer without the sensitive scope); the app's grace for those whose
    ///     feed is open, but for a result's (a page reports activities it's shown, not sections).
    /// </summary>
    private async Task RaiseAsync(PracticeStore store, Activity? activity, Section? section, DateTime now, Writes writes,
        CancellationToken stop)
    {
        if (People.Find(activity?.PatientId ?? section!.PatientId) is not { } patient) return;
        var grace = activity is not null;
        List<(Person, bool, bool)> recipients = [(patient, false, grace && IsOpen(patient))];
        foreach (var designation in _carers.Of(patient.Id))
            if (People.Find(designation.Carer) is { } carer &&
                (activity is not null
                    ? await VisibleAsync(store, carer, activity.Seq, stop) is not null
                    : await SectionVisibleAsync(store, carer, section!.Seq, stop)))
                recipients.Add((carer, true, grace && IsOpen(carer)));
        var validUntil = activity?.Kind switch
        {
            "called" => now.AddMinutes(10), // (a call in is no use for long)
            "reminded" => activity.Slot, // (a reminder, until the appointment)
            "replied" => now.AddDays(2), // (a reply, while it's of use)
            _ => now.AddDays(3)
        };
        var row = _notifications.Raise(activity?.Kind ?? "result", patient, activity?.Seq ?? 0, section?.Seq ?? 0, validUntil,
            recipients, now, writes);
        _log.LogInformation("Practice: notification {Id} ({Kind}{Urgent}) for {Patient}, to {Recipients}; deliver by {By:HH:mm:ss}",
            row.Id, row.Kind, row.Urgent ? ", urgent" : "", patient.Name,
            string.Join(", ", recipients.Select(r => r.Item1.Name + (r.Item3 ? " (in the app's grace)" : ""))), row.DeliverBy);
    }

    /// <summary>
    ///     ChalkQL's explanation of a person's statements, as their principal (their class's, for staff): what each table's
    ///     rows come to, and each column's disclosure. A statement it refuses says why.
    /// </summary>
    private async Task<Explanation> ExplainedAsync(PracticeStore store, Person person, CancellationToken stop)
    {
        var emergencies = EmergenciesOf(person);
        (Chalk.Entitlements.Tenancy.TenancyPrincipal Principal, (string Name, string Sql)[] Statements) chosen = person.Role switch
        {
            Role.Patient or Role.Carer => (store.Policy.For(person),
                new[] { ("Visits", PracticeStore.PatientSchedule), ("Clinical sections", PracticeStore.PatientSections) }),
            Role.Manager => (store.Policy.ForClass(person),
                new[]
                {
                    ("Schedule", store.ManagerSchedule), ("By the hour", PracticeStore.StatsHours),
                    ("By reason", PracticeStore.StatsReasons)
                }),
            Role.Screen => (store.Policy.ForClass(person), new[] { ("Tickets", PracticeStore.ScreenFeed) }),
            Role.Clinician => (store.Policy.ForClass(person, emergencies),
                new[]
                {
                    ("Schedule", store.StatementsOf(person, emergencies).Schedule.Sql),
                    ("Clinical sections", store.StatementsOf(person, emergencies).Sections!.Sql)
                }),
            _ => (store.Policy.ForClass(person), new[] { ("Schedule", store.ReceptionSchedule) })
        };
        var (principal, statements) = chosen;
        var context = store.Entitlements.Bind(principal);
        var explained = new Explanation { Principal = principal.User?.ToString() ?? principal.Actor, Purpose = principal.Purpose };
        try
        {
            List<ExplainedStatement> all = [];
            foreach (var (name, sql) in statements)
                all.Add(Explained.Of(name, 0, await store.Entitled.ExplainAsync(sql, context, ct: stop)));
            explained.Statements = [.. all];
        }
        catch (Exception e) when (e is Chalk.ChalkException or EntitlementException)
        {
            explained.Refused = e.Message.Split('\n')[0];
        }

        return explained;
    }

    /// <summary>
    ///     A section's note in full, as the person may read it now (an entitled read, as them, their emergencies too): its
    ///     text, or its placeholder, and what the policy disclosed. Refused if the section isn't theirs to see.
    /// </summary>
    /// <summary>
    ///     The access log as a person may read it (the audit page): who was shown a record, or with none named its newest
    ///     entries. Their own principal's statement over the audit source, prepared and run: the auditor role reads the log,
    ///     and to anyone else it holds no line. ChalkQL audits the read itself, as theirs.
    /// </summary>
    private async Task<AuditAnswer> AuditedAsync(PracticeStore store, AuditRequest asked, CancellationToken stop)
    {
        var principal = store.Policy.For(asked.Person);
        var answer = new AuditAnswer { Principal = principal.User?.ToString() ?? principal.Actor, Purpose = principal.Purpose };
        try
        {
            var record = asked.Seq > 0;
            var query = await store.Entitled.PrepareAsync(record ? PracticeStore.ShownTo : PracticeStore.RecentAccess,
                store.Entitlements.Bind(principal), ct: stop);
            await using var run = record
                ? await store.Engine.ExecuteAsync(query.Query, [(int)asked.Part, asked.Seq], ct: stop)
                : await store.Engine.ExecuteAsync(query.Query, ct: stop);
            List<AuditRow> rows = [];
            await foreach (var batch in run.Batches.WithCancellation(stop))
                using (batch)
                {
                    int At(string name) => batch.Schema.GetFieldIndex(name);
                    for (var i = 0; i < batch.Length; i++)
                    {
                        var cells = new BatchCells(batch, i);
                        string Text(string name) => System.Text.Encoding.UTF8.GetString(cells.Utf8(At(name)));
                        rows.Add(record
                            ? new AuditRow
                            {
                                Person = (int)cells.Int64(At("person_id")), User = Text("person_user"), Topic = Text("topic"),
                                Part = asked.Part, Record = asked.Seq, First = new DateTime(cells.Int64(At("first_shown"))),
                                Last = new DateTime(cells.Int64(At("last_shown"))), Posts = cells.Int64(At("posts")),
                                Patient = (int)cells.Int64(At("patient_id")), Booking = cells.Int64(At("booking")),
                                Clinician = (int)cells.Int64(At("clinician_id")), Basis = (DisclosureBasis)cells.Int64(At("basis"))
                            }
                            : new AuditRow
                            {
                                Seq = cells.Int64(At("seq")), Person = (int)cells.Int64(At("person_id")), User = Text("person_user"),
                                Topic = Text("topic"), Part = (byte)cells.Int64(At("part")), Record = cells.Int64(At("row_seq")),
                                Patient = (int)cells.Int64(At("patient_id")), First = new DateTime(cells.Int64(At("shown_at"))),
                                Last = new DateTime(cells.Int64(At("shown_at"))), Posts = 1, Booking = cells.Int64(At("booking")),
                                Clinician = (int)cells.Int64(At("clinician_id")), Basis = (DisclosureBasis)cells.Int64(At("basis"))
                            });
                    }
                }

            answer.Rows = record ? [.. rows.OrderBy(r => r.First)] : [.. rows];
        }
        catch (Exception e) when (e is not OperationCanceledException) // (refused, or its source failed: the caller's, not the loop's)
        {
            _log.LogWarning(e, "Practice: {Person}'s read of the access log failed", asked.Person.Name);
            answer.Refused = e.Message.Split('\n')[0];
        }

        return answer;
    }

    private async Task<NoteAnswer> NoteOfAsync(PracticeStore store, Person person, long section, CancellationToken stop)
    {
        await using var read = await store.Engine.ExecuteAsync(_note!.Query,
            store.Entitlements.Bind(store.Policy.For(person, EmergenciesOf(person))), [section], ct: stop);
        await foreach (var batch in read.Batches.WithCancellation(stop))
            using (batch)
            {
                if (batch.Length == 0) continue;
                var cells = new BatchCells(batch, 0);
                var (detail, disclosure) = (batch.Schema.GetFieldIndex("detail"), batch.Schema.GetFieldIndex("detail__disclosure"));
                return new NoteAnswer
                {
                    Text = System.Text.Encoding.UTF8.GetString(cells.Utf8(detail)),
                    Disclosed = disclosure < 0 ? "FULL" : System.Text.Encoding.UTF8.GetString(cells.Utf8(disclosure))
                };
            }

        return new NoteAnswer { Refused = $"{person.Name} can't see section {section}." };
    }

    /// <summary>
    ///     The provider's view of a person's topic: its newest activity's visit, every activity and section of it as the
    ///     person may see them (their own principal's statements, prepared for this view), and who was shown the activity.
    /// </summary>
    private async Task<string> RenderViewAsync(PracticeStore store, ViewRequest view, CancellationToken stop)
    {
        var person = view.Person;
        var context = store.Entitlements.Bind(store.Policy.For(person, EmergenciesOf(person)));
        var title = $"{view.Topic}: {person.Name}, {person.Role.ToString().ToLowerInvariant()}";
        if (view.Focus == 0)
            return ViewPages.Page(title, $"<p>The router holds {view.Held} rows of this topic: no activity to show the record of.</p>");
        var focus = await VisibleAsync(store, person, view.Focus, stop);
        if (focus is not { } row)
            return ViewPages.Page(title, $"<p>Activity {view.Focus} is no longer theirs to see: the router's state is behind, or it left.</p>");
        async Task<string> Run(string sql, Func<List<FeedKey>, List<FeedRow>, string> render)
        {
            try
            {
                var query = await store.Entitled.PrepareAsync(sql, context, ct: stop);
                var columns = new FeedColumns(query.OutputSchema);
                List<FeedKey> keys = [];
                List<FeedRow> rows = [];
                await using var run = await store.Engine.ExecuteAsync(query.Query, [row.Booking], ct: stop);
                await foreach (var batch in run.Batches.WithCancellation(stop))
                    using (batch)
                    {
                        Rows.Read(batch, columns, keys, rows);
                    }

                return render(keys, rows);
            }
            catch (Exception e) when (e is EntitlementException or Chalk.ChalkException)
            {
                return $"<p class=\"refused\">Refused by ChalkQL for {ViewPages.Esc(person.Name)}: {ViewPages.Esc(e.Message.Split('\n')[0])}</p>";
            }
        }

        var visit = await Run(PracticeStore.VisitActivities, ViewPages.Activities);
        var sections = await Run(PracticeStore.VisitSections, ViewPages.Sections);
        ShownTo[] shown;
        try
        {
            shown = await ShownToAsync(store, FeedKey.Activity, view.Focus, stop);
        }
        catch (Exception e) when (e is not OperationCanceledException) // (the page says so; the loop goes on)
        {
            _log.LogWarning(e, "Practice: reading the access log for the provider's view failed");
            shown = [];
        }
        return ViewPages.Page(title,
            $"<p>The router holds {view.Held} rows of <code>{ViewPages.Esc(view.Topic)}</code>, the masked bytes of {ViewPages.Esc(person.Name)}'s " +
            $"policy. Here is the newest, activity {view.Focus}, from the practice service's own records, as that policy shows " +
            "them: its visit, and who was shown it.</p>" +
            $"<h2>Visit {row.Booking}</h2>{visit}<h2>Its sections</h2>{sections}<h2>Who was shown activity {view.Focus}</h2>" +
            ViewPages.ShownTo(shown) + "<p class=\"note\">From the access log, as the practice's auditor reads it: made available " +
            "to their topic (the router may conflate a version away), up to the last refresh.</p>");
    }

    /// <summary>Every open topic, for an oracle: people's, screens', the statistics' and the dispatchers'.</summary>
    private List<object> Topics()
    {
        return
        [
            .. _homes.Values.Where(h => h.Routes.Count > 0), .. _screens.Values.Where(s => s.Routes.Count > 0),
            .. _stats.Values.Where(s => s.Routes.Count > 0), .. _notify.Values.Where(n => n.Routes.Count > 0)
        ];
    }

    /// <summary>
    ///     One topic checked: what it holds (its posts applied) against its statements run whole as its principal, by row
    ///     count and checksum, its own rows beside them. Differing, it's sent its whole state again (a staff topic's, by its
    ///     cursors from the start; a patient's, its statements run as them), and the log says so.
    /// </summary>
    private async Task<OracleReport> CheckAsync(PracticeStore store, object topic, CancellationToken stop)
    {
        var at = DateTime.UtcNow;
        _oracleChecks++;
        OracleReport report;
        switch (topic)
        {
            case Home home when Held(home.Person):
                return new OracleReport(home.Name, home.Table!.Version, home.Table.Count, home.Table.Checksum, 0, 0, true, at,
                    "held: its site is falling behind");
            case Home home:
            {
                await AppliedAsync(home.Ingest!, stop);
                var (keys, rows) = (new List<FeedKey>(), new List<FeedRow>());
                if (home.Class is { } cls)
                {
                    var principal = store.Policy.ForClass(home.Person, EmergenciesOf(home.Person));
                    foreach (var view in cls.Views)
                        await WholeAsync(store, view.Sql, store.Entitlements.Bind(principal), s => new FeedColumns(s),
                            Rows.Feed<BatchCells>, keys, rows, stop, [.. view.Parameters.Select(p => p.Value)]);
                }
                else
                {
                    var context = store.Entitlements.Bind(store.Policy.For(home.Person));
                    foreach (var statement in WholeOf(home.Person))
                    {
                        await using var run = await store.Engine.ExecuteAsync(statement.Query.Query, context, ct: stop);
                        await foreach (var batch in run.Batches.WithCancellation(stop))
                            using (batch)
                            {
                                Rows.Read(batch, statement.Columns, keys, rows);
                            }
                    }
                }

                var expected = Table<FeedKey, FeedRow>.ChecksumOf(CollectionsMarshal.AsSpan(keys), CollectionsMarshal.AsSpan(rows)) +
                               home.OwnChecksum;
                report = new OracleReport(home.Name, home.Table!.Version, home.Table.Count, home.Table.Checksum,
                    keys.Count + home.Own.Count, expected, home.Table.Checksum == expected && home.Table.Count == keys.Count + home.Own.Count, at);
                if (!report.Ok)
                    if (home.Class is null) await SnapshotPatientAsync(store, home, at, stop);
                    else home.Restart(); // (its whole state, with the next posts)
                break;
            }
            case ScreenFeed screen when Volatile.Read(ref _lagging).ContainsKey(screen.Site.Id):
                return new OracleReport(screen.Name, screen.Table.Version, screen.Table.Count, screen.Table.Checksum, 0, 0, true, at,
                    "held: its site is falling behind");
            case ScreenFeed screen:
            {
                await AppliedAsync(screen.Ingest, stop);
                var (keys, rows) = (new List<FeedKey>(), new List<ScreenRow>());
                var person = People.Screens.First(p => p.Sites[0] == screen.Site.Id);
                await WholeAsync(store, PracticeStore.ScreenFeed, store.Entitlements.Bind(store.Policy.ForClass(person)),
                    s => new FeedColumns(s), Rows.Screen<BatchCells>, keys, rows, stop);
                report = Report(screen.Name, screen.Table, keys, rows, at);
                if (!report.Ok) screen.Cursor = ViewCursor.Start;
                break;
            }
            case StatsFeed stats:
            {
                await AppliedAsync(stats.Ingest, stop);
                var (keys, rows) = (new List<FeedKey>(), new List<StatsRow>());
                var manager = People.Staff.First(p => p.Role == Role.Manager && PracticePolicy.ClassOf(p) == stats.Key);
                foreach (var sql in new[] { PracticeStore.StatsHours, PracticeStore.StatsReasons })
                    await WholeAsync(store, sql, store.Entitlements.Bind(store.Policy.ForClass(manager)), s => new StatsColumns(s),
                        Rows.Stats<BatchCells>, keys, rows, stop);
                report = Report(stats.Name, stats.Table, keys, rows, at);
                if (!report.Ok) stats.Restart();
                break;
            }
            case NotifyFeed notify:
            {
                await AdvanceAsync(store, notify, stop);
                if (notify.View.Version < store.Engine.Version) // (its reader behind: the view waits, so the topic isn't at the engine's)
                    return new OracleReport(notify.Name, notify.Table.Version, notify.Table.Count, notify.Table.Checksum, 0, 0, true,
                        at, "waiting: its reader is behind");
                await PostedAsync(notify, stop);
                var (keys, rows) = (new List<FeedKey>(), new List<NotifyRow>());
                await WholeAsync(store, PracticeStore.NotifyFeed(notify.Shard), store.Entitlements.Bind(store.Policy.ForDispatcher()),
                    s => new NotifyColumns(s), Rows.Notify<BatchCells>, keys, rows, stop);
                report = Report(notify.Name, notify.Table, keys, rows, at);
                if (!report.Ok) Volatile.Write(ref notify.Restart, true); // (its whole state at the next advance)
                break;
            }
            default:
                throw new ArgumentException($"Not a topic: {topic}", nameof(topic));
        }

        if (!report.Ok)
        {
            _log.LogError("Practice: oracle: {Topic} holds {Rows} rows ({Held:x8}) at version {Version}, its statements run whole " +
                          "{Expected} ({ExpectedSum:x8}): sent its whole state again", report.Topic, report.Rows, report.Held,
                report.Version, report.ExpectedRows, report.Expected);
            if (_oracleMismatches.Count == 20) _oracleMismatches.RemoveAt(0);
            _oracleMismatches.Add(report);
        }

        return report;
    }

    private static OracleReport Report<TRow>(string name, Table<FeedKey, TRow> table, List<FeedKey> keys, List<TRow> rows, DateTime at)
        where TRow : unmanaged
    {
        var expected = Table<FeedKey, TRow>.ChecksumOf(CollectionsMarshal.AsSpan(keys), CollectionsMarshal.AsSpan(rows));
        return new OracleReport(name, table.Version, table.Count, table.Checksum, keys.Count, expected,
            table.Checksum == expected && table.Count == keys.Count, at);
    }

    private delegate void CellsMapper<in TColumns, TRow>(BatchCells cells, TColumns at, out FeedKey key, out TRow row);

    /// <summary>
    ///     A statement run whole as a principal (prepared afresh for it: an oracle's own plan), its own parameters bound by
    ///     position (a view's: a GP's id, say), its rows mapped as a topic's.
    /// </summary>
    private static async Task WholeAsync<TColumns, TRow>(PracticeStore store, string sql, Chalk.Client.RequestContext context,
        Func<Apache.Arrow.Schema, TColumns> columns, CellsMapper<TColumns, TRow> map, List<FeedKey> keys, List<TRow> rows,
        CancellationToken stop, IReadOnlyList<object?>? parameters = null)
    {
        var query = await store.Entitled.PrepareAsync(sql, context, ct: stop);
        var at = columns(query.OutputSchema);
        await using var run = await store.Engine.ExecuteAsync(query.Query, parameters is { Count: > 0 } ? parameters : null, ct: stop);
        await foreach (var batch in run.Batches.WithCancellation(stop))
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    map(new BatchCells(batch, i), at, out var key, out var row);
                    keys.Add(key);
                    rows.Add(row);
                }
            }
    }

    /// <summary>Who was shown a record, from the access log, as the auditor (its rows up to the last refresh).</summary>
    private async Task<ShownTo[]> ShownToAsync(PracticeStore store, byte part, long seq, CancellationToken stop)
    {
        List<ShownTo> shown = [];
        await using var run = await store.Engine.ExecuteAsync(_shownTo!.Query, [(int)part, seq], ct: stop);
        await foreach (var batch in run.Batches.WithCancellation(stop))
            using (batch)
            {
                int Column(string name) => batch.Schema.GetFieldIndex(name);
                var (person, user, topic, first, last, posts, basis) = (Column("person_id"), Column("person_user"),
                    Column("topic"), Column("first_shown"), Column("last_shown"), Column("posts"), Column("basis"));
                for (var i = 0; i < batch.Length; i++)
                {
                    var cells = new BatchCells(batch, i);
                    shown.Add(new ShownTo((int)cells.Int64(person), System.Text.Encoding.UTF8.GetString(cells.Utf8(user)),
                        System.Text.Encoding.UTF8.GetString(cells.Utf8(topic)), new DateTime(cells.Int64(first)),
                        new DateTime(cells.Int64(last)), cells.Int64(posts), (DisclosureBasis)cells.Int64(basis)));
                }
            }

        return [.. shown.OrderBy(s => s.FirstShown)];
    }

    /// <summary>
    ///     A post measured: counted, its time, and, if it's the topic's first this turn to carry a row of this turn (the
    ///     newest it carries: none, 0), its lateness behind it. A row posted again that turn (the notifications' refresh
    ///     changing it) isn't its arrival.
    /// </summary>
    private void Measured(object topic, long started, long newestMicros, int topics, bool whole)
    {
        lock (_measureGate)
        {
            _postTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            _postsOf[topics]++;
            if (whole) _snapshotsOf[topics]++;
            var turn = Volatile.Read(ref _turnMicros);
            if (newestMicros < turn || _sampledIn.GetValueOrDefault(topic) == turn) return;
            _sampledIn[topic] = turn;
            _lateness[topics].Add((Rows.Micros(DateTime.UtcNow) - newestMicros) / 1000.0);
        }
    }

    /// <summary>A turn measured: its write (ChalkQL's refresh with it), then its fan-out.</summary>
    private void Turned(long wrote, long fanned)
    {
        _write!.Add(System.Diagnostics.Stopwatch.GetElapsedTime(wrote, fanned).TotalMilliseconds);
        _fanOut!.Add(System.Diagnostics.Stopwatch.GetElapsedTime(fanned).TotalMilliseconds);
    }

    /// <summary>The newest time of the rows a post carries: its changes, or, whole, its rows.</summary>
    private static long Newest(Drained<FeedRow>[] parts, int count, bool whole)
    {
        var newest = 0L;
        for (var i = 0; i < count; i++)
            newest = Math.Max(newest, whole ? Newest(parts[i].Rows, static r => r.AtMicros) : Newest(parts[i], static r => r.AtMicros));
        return newest;
    }

    private static long Newest<TRow>(Drained<TRow> drained, Func<TRow, long> at) where TRow : unmanaged
    {
        if (drained.WasReset) return Newest(drained.Rows, at);
        var newest = 0L;
        foreach (var change in drained.Changes)
            if (change.Deleted == 0)
                newest = Math.Max(newest, at(change.Row));
        return newest;
    }

    private static long Newest<TRow>(List<TRow> rows, Func<TRow, long> at)
    {
        var newest = 0L;
        foreach (var row in rows) newest = Math.Max(newest, at(row));
        return newest;
    }

    /// <summary>What's been measured (<c>/measure</c>), read on the loop; asked to, it starts again.</summary>
    public async Task<MeasureReport?> MeasureAsync(bool reset, CancellationToken ct)
    {
        if (!_measure || await _ready.Task.WaitAsync(ct) is null) return null;
        var asked = new MeasureRequest(reset);
        _work.Writer.TryWrite(asked);
        return await asked.Done.Task.WaitAsync(ct);
    }

    private MeasureReport Measurement(bool reset)
    {
        lock (_measureGate) return MeasurementOf(reset);
    }

    private MeasureReport MeasurementOf(bool reset)
    {
        var seconds = _measured.Elapsed.TotalSeconds;
        var posts = _postsOf.Sum();
        var report = new MeasureReport(seconds, _activities, posts, posts / Math.Max(seconds, 1e-9),
            posts == 0 ? 0 : _postTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / posts,
            [_write!.Read(), _fanOut!.Read()],
            [.. _lateness.Select((l, i) => new TopicPosts(l.Read().Of, _postsOf[i], _snapshotsOf[i], l.Read()))],
            [.. _updates.Values.Select(u => u.Read())], Collections.Now().Since(_collected));
        if (!reset) return report;
        _collected = Collections.Now();
        (_activities, _postTicks) = (0, 0);
        System.Array.Clear(_postsOf);
        System.Array.Clear(_snapshotsOf);
        _write.Clear();
        _fanOut!.Clear();
        foreach (var lateness in _lateness) lateness.Clear();
        foreach (var updates in _updates.Values) updates.Clear();
        _measured.Restart();
        return report;
    }

    /// <summary>The release job's run, as the notifier: each cohort patient's place in their queues, and their budgets' use.</summary>
    private async Task<List<Waiting>> WaitingAsync(PracticeStore store, CancellationToken stop)
    {
        List<Waiting> waiting = [];
        await using var run = await store.Engine.ExecuteAsync(_release!.Query, ct: stop);
        await foreach (var batch in run.Batches.WithCancellation(stop))
            using (batch)
            {
                int Column(string name) => batch.Schema.GetFieldIndex(name);
                var (patient, notice, cls, urgent, deliverBy, place, pushes, last) = (Column("patient_id"), Column("notice_id"),
                    Column("class"), Column("urgent"), Column("deliver_by"), Column("place"), Column("pushes"), Column("last_push"));
                for (var i = 0; i < batch.Length; i++)
                {
                    var cells = new BatchCells(batch, i);
                    waiting.Add(new Waiting((int)cells.Int64(patient), System.Text.Encoding.UTF8.GetString(cells.Utf8(notice)),
                        System.Text.Encoding.UTF8.GetString(cells.Utf8(cls)), cells.Bool(urgent), cells.Time(deliverBy),
                        cells.Int64(place), cells.Int64(pushes), cells.IsNull(last) ? null : cells.Time(last)));
                }
            }

        // (in a fixed order: the run's rows come as its plan's threads finish, and every instance must release alike)
        waiting.Sort((a, b) => (a.Patient, a.Urgent, a.Place, a.Notice).CompareTo((b.Patient, b.Urgent, b.Place, b.Notice)));
        return waiting;
    }

    /// <summary>Whether a person's feed is open: their topic has a route, on any instance (as the log says).</summary>
    private bool IsOpen(Person person)
    {
        return _openOn.TryGetValue(person.Id, out var on) && on.Count > 0; // (on any instance: the log's)
    }

    /// <summary>Whether a person may see a section (an entitled read, as themselves).</summary>
    private async Task<bool> SectionVisibleAsync(PracticeStore store, Person person, long seq, CancellationToken stop)
    {
        List<FeedKey> keys = [];
        List<FeedRow> rows = [];
        await using var read = await store.Engine.ExecuteAsync(_oneSection!.Query.Query,
            store.Entitlements.Bind(store.Policy.For(person)), [seq], ct: stop);
        await foreach (var batch in read.Batches.WithCancellation(stop))
            using (batch)
            {
                Rows.Read(batch, _oneSection.Columns, keys, rows);
            }

        return rows.Count > 0;
    }

    /// <summary>
    ///     What was written, posted: each class's views updated and their changes fanned out to its members' topics, each
    ///     screen's, and each change that concerns a patient whose feed is open, run as them.
    /// </summary>
    private async Task FanOutAsync(PracticeStore store, Writes writes, DateTime at, CancellationToken stop)
    {
        var delivered = writes.Dispatches.Count > 0 || writes.Notifications.Count > 0; // (reception's outbox and tasks may say so)
        foreach (var cls in _classes.Values) // (each class's views, and what this turn changed in them: every member's alike)
        {
            if (cls.Members.Count == 0) continue;
            for (var i = 0; i < cls.Views.Length; i++)
                if (await UpdateAsync(cls.Views[i], stop))
                {
                    cls.Seen[i] = ViewCursor.Start; // (gave way: every row shown again, as it's sent again)
                    foreach (var member in cls.Members) member.Cursors[i] = ViewCursor.Start;
                }

            DrainParts(cls.Views, cls.Seen, cls.Changed);
        }

        foreach (var home in _homes.Values) // (the access log: each feed open anywhere, in the order the log opened them)
            if (home.Class is { } cls)
                for (var i = 0; i < cls.Views.Length; i++)
                    Shown(home, at, CollectionsMarshal.AsSpan(cls.Changed[i].Keys), CollectionsMarshal.AsSpan(cls.Changed[i].Rows),
                        cls.Changed[i].WasReset ? default : CollectionsMarshal.AsSpan(cls.Changed[i].Changes));
        foreach (var cls in _classes.Values)
        foreach (var member in cls.Members) // (topics here; a site falling behind: its people's fed nothing, their cursors kept)
            if (member.Here && !Held(member.Person))
                Feed(member, cls, delivered && member.Person.Role == Role.Reception);

        foreach (var screen in _screens.Values)
        {
            if (screen.Routes.Count == 0) continue;
            if (await UpdateAsync(screen.View, stop)) screen.Cursor = ViewCursor.Start;
            if (!Volatile.Read(ref _lagging).ContainsKey(screen.Site.Id)) Feed(screen);
        }

        foreach (var stats in _stats.Values)
        {
            if (stats.Routes.Count == 0) continue;
            for (var i = 0; i < stats.Views.Length; i++)
                if (await UpdateAsync(stats.Views[i], stop))
                    stats.Cursors[i] = ViewCursor.Start;
            Feed(stats);
        }

        foreach (var notify in _notify.Values) // (held or not: kept level, every version handed to its reader)
            await AdvanceAsync(store, notify, stop);

        foreach (var activity in writes.Activities) await ConcernsAsync(store, activity.PatientId, FeedKey.Activity, activity.Seq, at, stop);
        foreach (var section in writes.Sections) await ConcernsAsync(store, section.PatientId, FeedKey.Clinical, section.Seq, at, stop);
        foreach (var ack in writes.Acks) // (its activity's summary changed: the patient's row of it, and their carers')
            if (_patientOf.TryGetValue(ack.ActivitySeq, out var patient))
                await ConcernsAsync(store, patient, FeedKey.Activity, ack.ActivitySeq, at, stop);
    }

    /// <summary>
    ///     A row of a patient's, changed: in their feed, if it's open anywhere, and in each of their carers' that is (each
    ///     run as its person: what they may see of it now, or its deletion).
    /// </summary>
    private async Task ConcernsAsync(PracticeStore store, int patient, byte part, long seq, DateTime at, CancellationToken stop)
    {
        if (_homes.TryGetValue(patient, out var home) && home.Class is null) await PostOneAsync(store, home, part, seq, at, stop);
        foreach (var designation in _carers.Of(patient))
            if (_homes.TryGetValue(designation.Carer, out var carer) && carer.Class is null)
                await PostOneAsync(store, carer, part, seq, at, stop);
    }

    /// <summary>
    ///     A site's system caught up (its fault injected over): its partition no longer reported degraded, and its patients'
    ///     and carers' topics that were held sent their feed whole. Staff topics catch up by their cursors.
    /// </summary>
    private async Task CatchUpAsync(PracticeStore store, CancellationToken stop)
    {
        var lagging = Volatile.Read(ref _lagging);
        foreach (var (site, until) in lagging.Where(l => l.Value <= DateTime.UtcNow).ToList())
        {
            Lag(site, TimeSpan.Zero);
            foreach (var home in _homes.Values.Where(h => h.Behind && h.Person.Site?.Id == site).ToList())
            {
                home.Behind = false;
                PostPatient(home); // (its rows as worked out meanwhile)
            }
        }

        if (!Volatile.Read(ref _lagging).IsEmpty) return;
        foreach (var screen in _screens.Values.Where(s => s.Routes.Count > 0)) Feed(screen); // (a screen held: its changes since)
    }

    /// <summary>
    ///     A view brought up to date. True if it gave way to a recomputed one (it differed from its twin, or failed): its
    ///     readers start over, sent it whole, and the log and <c>/views</c> say why.
    /// </summary>
    private async Task<bool> UpdateAsync<TRow>(FeedView<TRow> view, CancellationToken stop) where TRow : unmanaged
    {
        var started = _measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var gaveWay = await view.UpdateAsync(stop);
        if (_measure)
        {
            if (!_updates.TryGetValue(view.Name, out var updates)) _updates[view.Name] = updates = new Samples(view.Name);
            updates.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        if (gaveWay is not { } why) return false;
        _log.LogError("Practice: {View}'s incremental view gave way to a recomputed one, its readers sent it whole: {Why}",
            view.Name, why);
        if (_gaveWay.Count == 20) _gaveWay.RemoveAt(0);
        _gaveWay.Add(new GaveWay(view.Name, DateTime.UtcNow, why));
        return true;
    }

    /// <summary>
    ///     A member's topic, from its class's views: what's changed in each since its cursors, and its own rows. If any part
    ///     starts over (a reader's first drain, or one its view reset), the topic's whole state goes, every part.
    /// </summary>
    private void Feed(Home home, StaffClass cls, bool own = false)
    {
        var parts = cls.Views.Length;
        var whole = DrainParts(cls.Views, home.Cursors, _drained);
        if (!whole && !Any(_drained, parts) && !own) return;
        Own(home, _drained[0]);
        if (!whole && !Any(_drained, parts) && _ownKeys.Count == 0) return; // (its own rows asked after, and the same)
        _keys.Clear();
        for (var i = 0; i < parts; i++) _keys.AddRange(_drained[i].Keys);
        _keys.AddRange(_ownKeys);
        var keys = CollectionsMarshal.AsSpan(_keys);
        // (the topic holds every view's rows, and its own: the person's tasks and status row)
        var (version, checksum) = VersionOf(cls.Views);
        checksum += home.OwnChecksum;
        var intent = 0L;
        try
        {
            var started = _measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var changes = whole ? default : ChangesOf(_changes, _drained, parts, _ownChanges);
            intent = _accessLog.Intend(home.Person.Id, home.Name, keys, changes);
            var posted = whole
                ? home.Ingest!.PostSnapshot(keys, RowsOf(_rows, _drained, parts, _ownRows), version, checksum)
                : home.Ingest!.Post(keys, changes, upstreamVersion: version, expectedChecksum: checksum);
            if (_measure) Measured(home, started, Newest(_drained, parts, whole), StaffTopics, whole);
            Outcome(home, intent, posted.Status);
            if (posted.Status == PublishStatus.Full) // (staged more than it holds: the whole state, next)
            {
                home.Restart();
                _log.LogWarning("Practice: {Topic}'s changes didn't fit its staging: a snapshot next", home.Name);
            }
        }
        catch (StreamFaultException e) // its table faulted (its checksum, say): the whole state recovers it
        {
            if (_accessLog.Outcomes) _accessLog.Refused(intent);
            _log.LogWarning("Practice: {Topic} is faulted ({Fault}): a snapshot next", home.Name, e.Message);
            home.Restart();
        }
    }

    /// <summary>
    ///     A topic's parts, each drained from its view since its cursor. If any starts over (a reader's first drain, or a
    ///     view that reset), every part does, so the topic is sent its whole state: true then.
    /// </summary>
    private static bool DrainParts<TRow>(FeedView<TRow>[] views, ViewCursor[] cursors, Drained<TRow>[] into)
        where TRow : unmanaged
    {
        var (any, all) = (false, true);
        for (var i = 0; i < views.Length; i++)
        {
            Drain(views[i], ref cursors[i], into[i]);
            (any, all) = (any || into[i].WasReset, all && into[i].WasReset);
        }

        if (any && !all) // (one part from the start: so every other too, for the whole state)
            for (var i = 0; i < views.Length; i++)
                if (!into[i].WasReset)
                {
                    cursors[i] = ViewCursor.Start;
                    Drain(views[i], ref cursors[i], into[i]);
                }

        return any;
    }

    private static bool Any<TRow>(Drained<TRow>[] parts, int count) where TRow : unmanaged
    {
        for (var i = 0; i < count; i++)
            if (parts[i].Any)
                return true;
        return false;
    }

    /// <summary>A topic's version and checksum, of its views: the latest version any is at, and their checksums' sum.</summary>
    private static (long Version, uint Checksum) VersionOf<TRow>(FeedView<TRow>[] views) where TRow : unmanaged
    {
        var (version, checksum) = (0L, 0u);
        foreach (var view in views) (version, checksum) = (Math.Max(version, view.Version), checksum + view.Checksum);
        return (version, checksum);
    }

    /// <summary>Every part's rows, then a topic's own, in <paramref name="into" />.</summary>
    private static ReadOnlySpan<TRow> RowsOf<TRow>(List<TRow> into, Drained<TRow>[] parts, int count, List<TRow>? own = null)
        where TRow : unmanaged
    {
        into.Clear();
        for (var i = 0; i < count; i++) into.AddRange(parts[i].Rows);
        if (own is not null) into.AddRange(own);
        return CollectionsMarshal.AsSpan(into);
    }

    /// <summary>Every part's changes, then a topic's own, in <paramref name="into" />.</summary>
    private static ReadOnlySpan<RowChange<TRow>> ChangesOf<TRow>(List<RowChange<TRow>> into, Drained<TRow>[] parts, int count,
        List<RowChange<TRow>>? own = null) where TRow : unmanaged
    {
        into.Clear();
        for (var i = 0; i < count; i++) into.AddRange(parts[i].Changes);
        if (own is not null) into.AddRange(own);
        return CollectionsMarshal.AsSpan(into);
    }

    /// <summary>
    ///     A staff member's own rows, worked out whole from what they hold: their tasks (at reception, each message they've
    ///     yet to read: their own state, which the class's rows can't hold), their access (a GP's emergency access, while
    ///     it lasts) and their status row (the badge: how many are open). What changed since last posted goes beside the
    ///     views' changes; all of them after a reset.
    /// </summary>
    private void Own(Home home, Drained<FeedRow> schedule)
    {
        if (schedule.WasReset) // (the member's messages, as their schedule holds them: what their tasks are made of)
        {
            home.Messages.Clear();
            for (var i = 0; i < schedule.Keys.Count; i++)
                if (schedule.Rows[i].Kind == ActivityKind.Messaged)
                    home.Messages[schedule.Keys[i].Seq] = schedule.Rows[i];
        }
        else
        {
            for (var i = 0; i < schedule.Keys.Count; i++)
                if (schedule.Changes[i] is { Deleted: 0, Row.Kind: ActivityKind.Messaged } change)
                    home.Messages[schedule.Keys[i].Seq] = change.Row;
                else
                    home.Messages.Remove(schedule.Keys[i].Seq);
        }

        Mine(home, StaffOwn(home), schedule.WasReset);
    }

    private Dictionary<FeedKey, FeedRow> StaffOwn(Home home)
    {
        var own = new Dictionary<FeedKey, FeedRow>();
        var tasks = 0;
        if (home.Person.Role == Role.Reception)
            foreach (var (seq, message) in home.Messages)
                if (Unread(home, seq, message))
                {
                    own[new FeedKey(FeedKey.Tasks, seq)] = message;
                    tasks++;
                }

        if (home.Person.Role == Role.Reception) // (their replies, as their notifications went; one undelivered, every desk's task)
            foreach (var (seq, sender) in _replies)
            {
                if (!_patientOf.TryGetValue(seq, out var patientId) || People.Find(patientId) is not { } patient ||
                    !home.Person.Sites.Contains(patient.Sites[0]) || _notifications.DeliveryOf(seq) is not { } delivery)
                    continue;
                var (state, devices, at, by) = delivery;
                var row = new FeedRow
                {
                    Kind = ActivityKind.Replied, Patient = patient.Id, Count = devices, AtMicros = Rows.Micros(at == default ? by : at)
                };
                FixedText.Write(row.PatientName, patient.Name);
                FixedText.Write(row.Reason, state switch
                {
                    "delivered" => $"Delivered to {devices} device{(devices == 1 ? "" : "s")}",
                    "seen" => "Seen in the app: nothing pushed",
                    "waiting" => "Their app is open: waiting for them to see it",
                    "expired" => $"Not delivered (due {by:HH:mm})",
                    "none" => "No device to send it to",
                    "failed" => "Not delivered: refused",
                    _ => "Sending"
                });
                if (sender == home.Person.Id) own[new FeedKey(FeedKey.Outbox, seq)] = row;
                if (state == "expired") // (every desk's to follow up)
                {
                    own[new FeedKey(FeedKey.Tasks, seq)] = row with { Kind = ActivityKind.Replied };
                    tasks++;
                }
            }

        if (home.Person.Role == Role.Clinician) // (results for them to review and release)
            foreach (var (seq, result) in _results)
                if (result.AttendingId == home.Person.Id)
                {
                    var task = new FeedRow
                    {
                        Kind = ActivityKind.Result, Booking = result.Booking, Patient = result.PatientId,
                        Problem = result.ProblemId, AtMicros = Rows.Micros(result.WrittenAt)
                    };
                    FixedText.Write(task.PatientName, People.Find(result.PatientId)?.Name ?? "");
                    FixedText.Write(task.Reason, result.Reason);
                    FixedText.Write(task.Detail, result.Detail);
                    own[new FeedKey(FeedKey.Tasks, seq)] = task;
                    tasks++;
                }

        foreach (var (patient, until) in _emergencies.GetValueOrDefault(home.Person.Id) ?? [])
        {
            var access = new FeedRow { Patient = patient, AtMicros = Rows.Micros(until) };
            FixedText.Write(access.PatientName, People.Find(patient)?.Name ?? "");
            FixedText.Write(access.Reason, "Emergency access");
            own[new FeedKey(FeedKey.Access, patient)] = access;
        }

        own[new FeedKey(FeedKey.Status, 0)] = new FeedRow { Count = tasks };
        return own;
    }

    /// <summary>A designation's key among a person's access rows: beside the consents' (their own sequence).</summary>
    private const long CarerKey = 1_000_000_000;

    /// <summary>
    ///     A patient's own rows, or a carer's: a patient's consents (who reads what, since when) and their carers; a
    ///     carer's whom they care for; and the status row (the reminders yet to acknowledge, theirs or for whom they care).
    /// </summary>
    private Dictionary<FeedKey, FeedRow> PatientOwn(Home home)
    {
        var own = new Dictionary<FeedKey, FeedRow>();
        foreach (var consent in home.Person.Role == Role.Patient ? _consents.Of(home.Person.Id) : [])
        {
            var row = new FeedRow
            {
                Patient = consent.Patient, Clinician = consent.Gp, Count = consent.Everything ? 1 : 0,
                AtMicros = consent.Since == DateTime.MinValue ? 0 : Rows.Micros(consent.Since)
            };
            FixedText.Write(row.Reason, consent.Label);
            FixedText.Write(row.Detail, consent.Everything ? "everything, each sensitive problem named too" : string.Join(", ", consent.Problems));
            own[new FeedKey(FeedKey.Access, consent.Id)] = row;
        }

        foreach (var designation in home.Person.Role == Role.Carer ? _carers.For(home.Person.Id) : _carers.Of(home.Person.Id))
        {
            var other = home.Person.Role == Role.Carer ? designation.Patient : designation.Carer; // (whom they care for; who cares)
            var row = new FeedRow { Patient = other, Count = 2 };
            FixedText.Write(row.PatientName, People.Find(other)?.Name ?? "");
            FixedText.Write(row.Reason, designation.Scope);
            own[new FeedKey(FeedKey.Access, CarerKey + other)] = row;
        }

        var open = 0;
        foreach (var (key, row) in home.Rows)
            if (key.Section == FeedKey.Activity && row is { Kind: ActivityKind.Reminded, AckedMicros: 0 })
                open++;
        own[new FeedKey(FeedKey.Status, 0)] = new FeedRow { Count = open };
        return own;
    }

    /// <summary>
    ///     A person's own rows brought to <paramref name="desired" />: what changed (or, <paramref name="whole" />, all of
    ///     them) in <c>_ownKeys</c> with <c>_ownChanges</c> (or <c>_ownRows</c>). Their checksum kept beside them.
    /// </summary>
    private void Mine(Home home, Dictionary<FeedKey, FeedRow> desired, bool whole)
    {
        _ownKeys.Clear();
        _ownRows.Clear();
        _ownChanges.Clear();
        foreach (var (key, row) in desired)
            if (whole)
            {
                _ownKeys.Add(key);
                _ownRows.Add(row);
            }
            else if (!home.Own.TryGetValue(key, out var held) || !Same(held, row))
            {
                _ownKeys.Add(key);
                _ownChanges.Add(new RowChange<FeedRow>(row, 0));
            }

        if (!whole)
            foreach (var key in home.Own.Keys)
                if (!desired.ContainsKey(key))
                {
                    _ownKeys.Add(key);
                    _ownChanges.Add(new RowChange<FeedRow>(default, 1));
                }

        home.Own.Clear();
        home.OwnChecksum = 0;
        foreach (var (key, row) in desired)
        {
            home.Own[key] = row;
            home.OwnChecksum += Table<FeedKey, FeedRow>.ChecksumOf([key], [row]);
        }
    }

    private static bool Same(in FeedRow a, in FeedRow b)
    {
        return MemoryMarshal.AsBytes(new ReadOnlySpan<FeedRow>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<FeedRow>(in b)));
    }

    /// <summary>A patient's own rows posted as they now stand (a consent given or withdrawn).</summary>
    private void PostOwn(Home home)
    {
        if (home.Class is not null || home.Ingest is null) return;
        Mine(home, PatientOwn(home), false);
        if (_ownKeys.Count == 0) return;
        var intent = _accessLog.Intend(home.Person.Id, home.Name, CollectionsMarshal.AsSpan(_ownKeys), CollectionsMarshal.AsSpan(_ownChanges));
        try
        {
            var started = _measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var posted = home.Ingest.Post(CollectionsMarshal.AsSpan(_ownKeys), CollectionsMarshal.AsSpan(_ownChanges),
                expectedChecksum: home.Checksum + home.OwnChecksum);
            if (_measure) Measured(home, started, 0, PatientTopics, false);
            Outcome(home, intent, posted.Status);
        }
        catch (StreamFaultException e)
        {
            if (_accessLog.Outcomes) _accessLog.Refused(intent);
            _log.LogWarning("Practice: {Topic} is faulted ({Fault})", home.Name, e.Message);
        }
    }

    /// <summary>A GP's grants as they stand: the patients they've broken the glass for (while it lasts).</summary>
    private IReadOnlyCollection<int> EmergenciesOf(Person person)
    {
        return _emergencies.TryGetValue(person.Id, out var broken) ? broken.Keys : [];
    }

    /// <summary>Emergency access past its time, let go: each GP whose grants changed moved to the class they now are.</summary>
    private async Task ExpireAsync(PracticeStore store, DateTime now, CancellationToken stop)
    {
        foreach (var (gp, broken) in _emergencies)
        {
            var over = broken.Where(e => e.Value <= now).Select(e => e.Key).ToList();
            if (over.Count == 0) continue;
            foreach (var patient in over) broken.Remove(patient);
            _log.LogInformation("Practice: {Gp}'s emergency access to {Patients} is over", People.Find(gp)?.Name,
                string.Join(", ", over.Select(p => People.Find(p)?.Name)));
            if (_homes.TryGetValue(gp, out var home)) await ReclassAsync(store, home, now, stop);
        }
    }

    /// <summary>
    ///     A staff member whose grants changed (break glass, or its end), moved to the class they now are: its views found
    ///     or made, and their topic sent its whole state from them. What they may no longer see leaves it; what they may now,
    ///     arrives.
    /// </summary>
    private async Task ReclassAsync(PracticeStore store, Home home, DateTime at, CancellationToken stop)
    {
        if (home.Class is not { } old) return;
        var cls = await ClassOfAsync(store, home.Person, stop);
        if (ReferenceEquals(cls, old)) return;
        if (old.Members.Remove(home) && old.Members.Count == 0) old.IdleSince = DateTime.UtcNow;
        cls.Members.Add(home);
        home.Class = cls;
        ShownWhole(home, cls, at); // (what the new class's views hold, shown to them: its whole state, sent again)
        if (home.Here)
        {
            home.Restart();
            Feed(home, cls); // (its whole state, from the new class's views)
        }

        _log.LogInformation("Practice: {Topic} moved from class {Old} to {New}", home.Name, old.Key, cls.Key);
    }

    /// <summary>What a staff member's class's views hold, whole, shown to them: their feed's first state, or its new class's.</summary>
    private void ShownWhole(Home home, StaffClass cls, DateTime at)
    {
        foreach (var view in cls.Views)
        {
            var cursor = ViewCursor.Start;
            Drain(view, ref cursor, _whole);
            Shown(home, at, CollectionsMarshal.AsSpan(_whole.Keys), CollectionsMarshal.AsSpan(_whole.Rows));
        }
    }

    /// <summary>A class's views' changes, for the access log, from here on (its members' first states are their own).</summary>
    private void Mark(StaffClass cls)
    {
        for (var i = 0; i < cls.Views.Length; i++) Drain(cls.Views[i], ref cls.Seen[i], _whole);
    }

    /// <summary>A message the person has yet to read.</summary>
    private bool Unread(Home home, long seq, in FeedRow row)
    {
        return row.Kind == ActivityKind.Messaged && !_seenBy.Contains((seq, home.Person.Id));
    }

    private static void Drain<TRow>(FeedView<TRow> view, ref ViewCursor cursor, Drained<TRow> into) where TRow : unmanaged
    {
        into.Clear();
        while (view.Drain(ref cursor, into, 1 << 20) == DrainOutcome.Partial)
        {
        }
    }

    private void Feed(ScreenFeed screen)
    {
        var drained = _screenDrained;
        Drain(screen.View, ref screen.Cursor, drained);
        if (!drained.Any) return;
        var keys = CollectionsMarshal.AsSpan(drained.Keys);
        try
        {
            var started = _measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var posted = drained.WasReset
                ? screen.Ingest.PostSnapshot(keys, CollectionsMarshal.AsSpan(drained.Rows), drained.Version,
                    drained.Checksum)
                : screen.Ingest.Post(keys, CollectionsMarshal.AsSpan(drained.Changes), upstreamVersion: drained.Version,
                    expectedChecksum: drained.Checksum);
            if (_measure) Measured(screen, started, Newest(drained, static r => r.AtMicros), ScreenTopics, drained.WasReset);
            if (posted.Status == PublishStatus.Full) screen.Cursor = ViewCursor.Start;
        }
        catch (StreamFaultException e)
        {
            _log.LogWarning("Practice: {Topic} is faulted ({Fault}): a snapshot next", screen.Name, e.Message);
            screen.Cursor = ViewCursor.Start;
        }
    }

    /// <summary>A manager class's statistics topic, from its views: what's changed since its cursors, or, if any part starts over, all.</summary>
    private void Feed(StatsFeed stats)
    {
        var parts = stats.Views.Length;
        var whole = DrainParts(stats.Views, stats.Cursors, _drainedStats);
        if (!whole && !Any(_drainedStats, parts)) return;
        _keys.Clear();
        for (var i = 0; i < parts; i++) _keys.AddRange(_drainedStats[i].Keys);
        var keys = CollectionsMarshal.AsSpan(_keys);
        var (version, checksum) = VersionOf(stats.Views);
        try
        {
            var started = _measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var posted = whole
                ? stats.Ingest.PostSnapshot(keys, RowsOf(_statsRows, _drainedStats, parts), version, checksum)
                : stats.Ingest.Post(keys, ChangesOf(_statsChanges, _drainedStats, parts), upstreamVersion: version,
                    expectedChecksum: checksum);
            if (_measure) Measured(stats, started, 0, StatsTopics, whole); // (an hour's figures: no activity's time)
            if (posted.Status == PublishStatus.Full) stats.Restart();
        }
        catch (StreamFaultException e)
        {
            _log.LogWarning("Practice: {Topic} is faulted ({Fault}): a snapshot next", stats.Name, e.Message);
            stats.Restart();
        }
    }

    /// <summary>
    ///     A shard's dispatcher topic brought level with the engine: each version of its view since the last, drained into a
    ///     batch of its own (its view publishes every version, so none is conflated: an entry added and removed in turn is
    ///     posted both times) and handed to the topic's reader, in order, while a batch is free. With every batch still with
    ///     the reader, the view waits, the engine holding its versions, and the loop goes on: a slow reader holds back only
    ///     its own topic. The rendezvous it owes go with the first batch at or past the version each waits for, alone if
    ///     nothing changed.
    /// </summary>
    private async Task AdvanceAsync(PracticeStore store, NotifyFeed notify, CancellationToken stop)
    {
        if (Volatile.Read(ref notify.Restart)) // (its reader found the topic full, or faulted: its whole state next)
        {
            Volatile.Write(ref notify.Restart, false);
            notify.Cursor = ViewCursor.Start;
        }
        else if (notify.Through == store.Engine.Version && notify.Owed.Count == 0)
            return; // (level, every version handed on)

        while (notify.Pipe.InFlight < notify.Pipe.Capacity)
        {
            var from = notify.View.Version;
            var batch = await notify.Pipe.RentAsync(stop); // (one's free: at once)
            batch.Clear();
            if (from < store.Engine.Version && await UpdateAsync(notify.View, stop)) notify.Cursor = ViewCursor.Start;
            Drain(notify.View, ref notify.Cursor, batch.Rows);
            notify.Through = notify.View.Version; // (what the topic holds once this batch is posted)
            for (var i = 0; i < notify.Owed.Count; i++)
                if (notify.Owed[i].Version <= notify.Through)
                {
                    batch.Rendezvous.Add((notify.Owed[i].Mailbox, notify.Owed[i].Correlation));
                    notify.Owed.RemoveAt(i--);
                }

            if (batch.Rows.Any || batch.Rendezvous.Count > 0)
            {
                if (!batch.Rows.Any) batch.Rows.Commit(notify.View.Stamp, notify.View.Checksum); // (rendezvous alone, at its version)
                notify.Handed = batch.Rows.Version;
                notify.HandedOn++;
                notify.Pipe.Publish(batch);
            }
            else notify.Pipe.Unrent(batch); // (nothing to post: kept for the next)

            if (notify.Through >= store.Engine.Version || notify.Through == from) return; // (level, or nothing moved it)
        }

        notify.Deferred++; // (every batch with its reader: the view waits)
    }

    /// <summary>
    ///     A dispatcher topic's reader, on a task of its own: each batch posted as it's handed over, in order — a whole state
    ///     as a snapshot, changes as changes — then the rendezvous it carries, and whoever waits for the topic to reach its
    ///     version told. A post refused (the topic full) or faulted: its whole state next (the loop's drain), and the
    ///     rendezvous held until it's posted. Its reading over (the topic let go, or the service stopping), the topic's closed.
    /// </summary>
    private async Task ReadAsync(NotifyFeed notify)
    {
        List<(Mailbox Mailbox, byte[] Correlation)> held = []; // (rendezvous waiting for a whole state to be posted)
        var whole = false; // (a post refused or faulted: rendezvous wait for the next whole state)
        try
        {
            await foreach (var batch in notify.Pipe.ReadAllAsync().ConfigureAwait(false)) // (its waits the pipe's own)
            {
                try
                {
                    var drained = batch.Rows;
                    if (drained.Any && !Post(notify, drained)) whole = true;
                    else if (drained.WasReset) whole = false;
                    held.AddRange(batch.Rendezvous);
                    if (!whole)
                    {
                        foreach (var (mailbox, correlation) in held) notify.Ingest.TryRendezvous(mailbox, correlation);
                        held.Clear();
                    }

                    lock (notify.Gate)
                    {
                        notify.Posted = drained.Version;
                        for (var i = notify.Waiting.Count - 1; i >= 0; i--)
                            if (notify.Waiting[i].Version <= notify.Posted)
                            {
                                notify.Waiting[i].Done.TrySetResult();
                                notify.Waiting.RemoveAt(i);
                            }
                    }
                }
                catch (Exception e) // (whatever went wrong with one, the next is posted)
                {
                    _log.LogWarning(e, "Practice: {Topic}'s reader couldn't post a batch: its whole state next", notify.Name);
                    Volatile.Write(ref notify.Restart, true);
                    whole = true;
                }
                finally
                {
                    batch.Clear();
                    notify.Pipe.Return(batch);
                }
            }
        }
        finally
        {
            notify.Ingest.Close();
            lock (notify.Gate)
            {
                foreach (var (_, done) in notify.Waiting) done.TrySetCanceled();
                notify.Waiting.Clear();
            }
        }
    }

    /// <summary>One batch posted to its topic: true if it was taken; refused (full) or faulted, its whole state next.</summary>
    private bool Post(NotifyFeed notify, Drained<NotifyRow> drained)
    {
        var keys = CollectionsMarshal.AsSpan(drained.Keys);
        try
        {
            var started = _measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var posted = drained.WasReset
                ? notify.Ingest.PostSnapshot(keys, CollectionsMarshal.AsSpan(drained.Rows), drained.Version, drained.Checksum)
                : notify.Ingest.Post(keys, CollectionsMarshal.AsSpan(drained.Changes), upstreamVersion: drained.Version,
                    expectedChecksum: drained.Checksum);
            if (_measure) Measured(notify, started, Newest(drained, static r => r.AtMicros), DispatcherTopics, drained.WasReset);
            if (posted.Status != PublishStatus.Full) return true;
        }
        catch (StreamFaultException e)
        {
            _log.LogWarning("Practice: {Topic} is faulted ({Fault}): a snapshot next", notify.Name, e.Message);
        }

        Volatile.Write(ref notify.Restart, true);
        return false;
    }

    /// <summary>
    ///     Once a dispatcher topic's reader has posted every batch handed to it, and the router has applied them: what a
    ///     dispatcher taking the topic, or an oracle, then reads. Only what's handed over: the loop doesn't wait on its own
    ///     drain.
    /// </summary>
    private static async Task PostedAsync(NotifyFeed notify, CancellationToken stop)
    {
        Task? posted = null;
        lock (notify.Gate)
            if (notify.Posted < notify.Handed)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                notify.Waiting.Add((notify.Handed, done));
                posted = done.Task;
            }

        if (posted is not null) await posted.WaitAsync(TimeSpan.FromSeconds(10), stop);
        await AppliedAsync(notify.Ingest, stop);
    }

    /// <summary>
    ///     One row of a patient's or carer's feed, as they may see it now (run as them): an activity, or a section; the row,
    ///     shown to them, or its deletion if they may no longer see it. Posted to their topic, if it's here (their site
    ///     falling behind: held, their whole feed once it's over), the topic's checksum kept beside it.
    /// </summary>
    private async Task PostOneAsync(PracticeStore store, Home home, byte part, long seq, DateTime at, CancellationToken stop)
    {
        var carer = home.Person.Role == Role.Carer;
        var statement = part == FeedKey.Clinical ? carer ? _carerSection! : _oneSection! : carer ? _carerActivity! : _oneActivity!;
        List<FeedKey> keys = [];
        List<FeedRow> rows = [];
        await using (var run = await store.Engine.ExecuteAsync(statement.Query.Query,
                         store.Entitlements.Bind(store.Policy.For(home.Person)), [seq], ct: stop))
        {
            await foreach (var batch in run.Batches.WithCancellation(stop))
                using (batch)
                {
                    Rows.Read(batch, statement.Columns, keys, rows);
                }
        }

        var key = new FeedKey(part, seq);
        RowChange<FeedRow> change;
        if (rows.Count > 0)
        {
            if (home.Rows.Remove(key, out var old)) home.Checksum -= Table<FeedKey, FeedRow>.ChecksumOf([key], [old]);
            home.Rows[key] = rows[0];
            home.Checksum += Table<FeedKey, FeedRow>.ChecksumOf([key], [rows[0]]);
            change = new RowChange<FeedRow>(rows[0], 0);
        }
        else if (home.Rows.Remove(key, out var gone))
        {
            home.Checksum -= Table<FeedKey, FeedRow>.ChecksumOf([key], [gone]);
            change = new RowChange<FeedRow>(default, 1);
        }
        else
        {
            return; // (theirs to see neither before nor now)
        }

        if (change.Deleted == 0) Shown(home, at, [key], [change.Row]);
        if (home.Ingest is null) return;
        if (Held(home.Person))
        {
            home.Behind = true;
            return;
        }

        Mine(home, PatientOwn(home), false);
        _ownKeys.Insert(0, key);
        _ownChanges.Insert(0, change);
        var intent = _accessLog.Intend(home.Person.Id, home.Name, CollectionsMarshal.AsSpan(_ownKeys), CollectionsMarshal.AsSpan(_ownChanges));
        try
        {
            var started = _measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var posted = home.Ingest.Post(CollectionsMarshal.AsSpan(_ownKeys), CollectionsMarshal.AsSpan(_ownChanges),
                expectedChecksum: home.Checksum + home.OwnChecksum);
            if (_measure) Measured(home, started, change.Deleted == 0 ? change.Row.AtMicros : 0, PatientTopics, false);
            Outcome(home, intent, posted.Status);
            if (posted.Status == PublishStatus.Full) PostPatient(home); // (it didn't fit its staging: the whole state)
        }
        catch (StreamFaultException e)
        {
            if (_accessLog.Outcomes) _accessLog.Refused(intent);
            _log.LogWarning("Practice: {Topic} is faulted ({Fault}): its whole state again", home.Name, e.Message);
            PostPatient(home);
        }
    }

    /// <summary>
    ///     A patient's or carer's whole feed run again as them, what of it is new or changed shown to them, and posted whole
    ///     to their topic, if it's here.
    /// </summary>
    private async Task SnapshotPatientAsync(PracticeStore store, Home home, DateTime at, CancellationToken stop)
    {
        await RunPatientAsync(store, home, at, stop);
        if (home.Here) PostPatient(home);
    }

    /// <summary>A patient's or carer's whole feed: their schedule and their sections (a carer's, the twins: basis carer).</summary>
    private Shaped[] WholeOf(Person person)
    {
        return person.Role == Role.Carer ? [_carerSchedule!, _carerSections!] : [_patientSchedule!, _patientSections!];
    }

    /// <summary>
    ///     A patient's or carer's whole feed, run as them (their schedule and their sections): what it holds now, and what of
    ///     it is new or changed since it last ran, shown to them (all of it, the first time).
    /// </summary>
    private async Task RunPatientAsync(PracticeStore store, Home home, DateTime at, CancellationToken stop)
    {
        List<FeedKey> keys = [];
        List<FeedRow> rows = [];
        var context = store.Entitlements.Bind(store.Policy.For(home.Person));
        foreach (var statement in WholeOf(home.Person))
        {
            await using var run = await store.Engine.ExecuteAsync(statement.Query.Query, context, ct: stop);
            await foreach (var batch in run.Batches.WithCancellation(stop))
                using (batch)
                {
                    Rows.Read(batch, statement.Columns, keys, rows);
                }
        }

        List<FeedKey> fresh = [];
        List<FeedRow> freshRows = [];
        for (var i = 0; i < keys.Count; i++)
            if (!home.Rows.TryGetValue(keys[i], out var was) || !Same(was, rows[i]))
            {
                fresh.Add(keys[i]);
                freshRows.Add(rows[i]);
            }
        home.Rows.Clear();
        for (var i = 0; i < keys.Count; i++) home.Rows[keys[i]] = rows[i];
        home.Checksum = Table<FeedKey, FeedRow>.ChecksumOf(CollectionsMarshal.AsSpan(keys), CollectionsMarshal.AsSpan(rows));
        Shown(home, at, CollectionsMarshal.AsSpan(fresh), CollectionsMarshal.AsSpan(freshRows));
    }

    /// <summary>A patient's or carer's topic here sent its whole state: their rows as last worked out, and its own.</summary>
    private void PostPatient(Home home)
    {
        List<FeedKey> keys = [.. home.Rows.Keys];
        List<FeedRow> rows = [.. home.Rows.Values];
        Mine(home, PatientOwn(home), true);
        keys.AddRange(_ownKeys);
        rows.AddRange(_ownRows);
        var intent = _accessLog.Intend(home.Person.Id, home.Name, CollectionsMarshal.AsSpan(keys), default);
        var started = _measure ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var posted = home.Ingest!.PostSnapshot(CollectionsMarshal.AsSpan(keys), CollectionsMarshal.AsSpan(rows),
            expectedChecksum: home.Checksum + home.OwnChecksum);
        if (_measure) Measured(home, started, 0, PatientTopics, true);
        Outcome(home, intent, posted.Status);
    }

    /// <summary>
    ///     A post's outcome, for the access log at the posts, if it wants them: refused (its whole state next, intended anew), or once
    ///     the router has applied it, with the topic's version.
    /// </summary>
    private void Outcome(Home home, long intent, PublishStatus status)
    {
        if (!_accessLog.Outcomes) return;
        if (status == PublishStatus.Full) _accessLog.Refused(intent);
        else home.Ingest!.WhenApplied(version => _accessLog.Applied(intent, version));
    }

    /// <summary>
    ///     Step 1, answered: the person's topic opened if it isn't (its first state posted and applied, so no route starts
    ///     empty), and the route accepted onto it, in the person's site's partition.
    /// </summary>
    private async Task OpenAsync(PracticeStore store, Open open, CancellationToken stop)
    {
        var (request, person) = (open.Request, open.Person);
        if (open.Abandoned) return;
        try
        {
            if (open.Follower is { } follower) // (an instance following the log: its signal, while this one writes)
            {
                if (_logTopic is not { } topic)
                {
                    request.Refuse(RefusalKind.Elsewhere, $"{_name} doesn't write the practice's records now.", null); // (the router asks the next instance)
                    open.Done.TrySetResult();
                    return;
                }

                request.Accept(topic, Instance, "log", InitialState.Snapshot, null, Formats.Log);
                _log.LogInformation("Practice: {Follower} hears {Name}'s log signal", follower, _name);
            }
            else if (open.Dispatcher is { } dispatcher) // (a shard's dispatcher, or an observer: its pending entries)
            {
                var notify = await NotifyOfAsync(store, open.Shard, stop);
                var epoch = request.Accept(notify.Table, Instance, notify.Name, InitialState.Snapshot, Interrupted, Formats.Notify);
                notify.Routes[request.Route] = (request.Mailbox, dispatcher);
                notify.Epoch = epoch;
                _log.LogInformation("Practice: {Dispatcher} took {Topic} ({Pending} entries pending)", dispatcher, notify.Name,
                    notify.Table.Count);
            }
            else if (person!.Role == Role.Screen)
            {
                var screen = await ScreenOfAsync(store, person, stop);
                var epoch = request.Accept(screen.Table, Instance, screen.Name, InitialState.Snapshot, Interrupted,
                    Formats.Screen, person.Site!.Partition);
                screen.Routes[request.Route] = request.Mailbox;
                screen.Epoch = epoch;
            }
            else if (open.Feed == "stats") // (a manager's class's statistics: a site's partition if they work at one)
            {
                var stats = await StatsOfAsync(store, person, stop);
                var epoch = request.Accept(stats.Table, Instance, stats.Name, InitialState.Snapshot, Interrupted,
                    Formats.Stats, person.Site?.Partition);
                stats.Routes[request.Route] = (request.Mailbox, person.Id);
                stats.Epoch = epoch;
            }
            else // (a person's own: open here already, its route accepted; otherwise, once the log opens it here)
            {
                if (_openHere.Contains(person.Id) && _homes.TryGetValue(person.Id, out var home) && home.Here)
                {
                    Attach(open, home);
                    return;
                }

                if (_openHere.Add(person.Id)) _opened.Add(person.Id); // (the log's to say: writing, at once)
                _waiting.Add(open);
                return;
            }

            _routers.Accepted(request.Router, request.Route);
            open.Done.TrySetResult();
        }
        catch (Exception e) when (e is ChalkException or EntitlementException or StandingViewException)
        {
            var correlation = request.Refuse($"ChalkQL: {e.Message}");
            _log.LogWarning("Practice: {Person}'s feed refused (correlation {Correlation}): {Reason}",
                person?.Name ?? open.Dispatcher, correlation, e.Message);
            open.Done.TrySetResult();
        }
        catch (OperationCanceledException) when (open.Abandoned)
        {
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Practice: {Person}'s step 1 failed", person?.Name ?? open.Dispatcher);
            open.Done.TrySetException(e);
        }
    }

    /// <summary>A person's topic, opened: a staff member's fed by their class's views, a patient's by their statements.</summary>
    private async Task<Home> HomeOfAsync(PracticeStore store, Person person, DateTime at, CancellationToken stop)
    {
        var home = new Home { Person = person };
        if (person.Role is Role.Patient or Role.Carer) // (each their own statements, run as them)
        {
            await RunPatientAsync(store, home, at, stop);
        }
        else
        {
            var cls = await ClassOfAsync(store, person, stop);
            home.Class = cls;
            cls.Members.Add(home);
            ShownWhole(home, cls, at);
        }

        _log.LogInformation("Practice: {Feed} open, for {Person} ({How})", home.Name, person.Name,
            home.Class is { } c ? $"class {c.Key}, plan {c.Schedule.Query.PlanDigest:x16}" : $"their statements, run as them: {home.Rows.Count} rows");
        return home;
    }

    /// <summary>
    ///     This instance's open of a person's feed, where the log has it: its topic made (its whole state posted and applied,
    ///     so no route starts empty) and each step 1 waiting for it accepted. None left (each gave up waiting): let go again.
    /// </summary>
    private async Task HereAsync(PracticeStore store, Home home, CancellationToken stop)
    {
        if (!_openHere.Contains(home.Person.Id)) return; // (closed here since: its close is next in the log)
        if (!home.Here)
        {
            var table = new Table<FeedKey, FeedRow>(home.Name, HomeLog) { Format = _homeFormat };
            (home.Table, home.Ingest) = (table, table.Ingest<RowChange<FeedRow>>(
                BatchFold.Of<FeedRow, RowChange<FeedRow>, ReplaceOrDelete<FeedRow>>(), home.Class is null ? PatientStaging : StaffStaging));
            if (home.Class is { } cls)
            {
                home.Restart();
                Feed(home, cls); // (from the start: its whole state)
            }
            else
            {
                PostPatient(home);
            }

            await AppliedAsync(home.Ingest, stop);
            _log.LogInformation("Practice: {Topic} opened here for {Person}, {Rows} rows", home.Name, home.Person.Name, table.Count);
        }

        foreach (var open in TakeWaiting(home.Person.Id)) Attach(open, home);
        if (home.Routes.Count == 0) LetGoHere(home); // (every step 1 for it gave up waiting)
    }

    /// <summary>A step 1's route accepted onto a person's topic here (unless the router gave up on it).</summary>
    private void Attach(Open open, Home home)
    {
        var request = open.Request;
        if (open.Abandoned) return;
        try
        {
            var epoch = request.Accept(home.Table!, Instance, home.Name, InitialState.Snapshot, Interrupted, _homeFormat,
                home.Person.Site?.Partition);
            if (home.Epoch != 0 && epoch != home.Epoch) // (every route gone, then one bound: the whole state, next)
                home.Restart();
            home.Epoch = epoch;
            home.Routes[request.Route] = request.Mailbox;
            _routers.Accepted(request.Router, request.Route);
            open.Done.TrySetResult();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Practice: {Person}'s step 1 failed", home.Person.Name);
            open.Done.TrySetException(e);
        }
    }

    /// <summary>
    ///     The classes' statements prepared for the practice's shapes of staff (a GP's, a GP's with emergency access, a
    ///     receptionist's, a screen's, the manager's), at start: ready before any feed opens, so a class's first feed waits
    ///     for its views alone, and planned again on the practice's rows once it's seeded.
    /// </summary>
    private async Task ShapesAsync(PracticeStore store, CancellationToken stop)
    {
        var (gp, reception, manager) = (People.Staff.First(p => p.Role == Role.Clinician),
            People.Staff.First(p => p.Role == Role.Reception), People.Staff.First(p => p.Role == Role.Manager));
        int[] emergency = [People.Patients[0].Id];
        foreach (var (sql, principal) in new[]
                 {
                     (store.GpSchedule, store.Policy.ForClass(gp)), (store.GpSections, store.Policy.ForClass(gp)),
                     (store.GpScheduleInEmergency, store.Policy.ForClass(gp, emergency)),
                     (store.GpSectionsInEmergency, store.Policy.ForClass(gp, emergency)),
                     (store.ReceptionSchedule, store.Policy.ForClass(reception)),
                     (store.ManagerSchedule, store.Policy.ForClass(manager)),
                     (PracticeStore.ScreenFeed, store.Policy.ForClass(People.Screens[0])),
                     (PracticeStore.StatsHours, store.Policy.ForClass(manager)),
                     (PracticeStore.StatsReasons, store.Policy.ForClass(manager))
                 })
            await ShapedAsync(store, sql, principal, stop);
    }

    /// <summary>
    ///     A class's statement: prepared once for the shape of its principal's grants (open in the lists they fill, every
    ///     other name folded as the principal has it: no other role's grants), and kept for every principal of that shape
    ///     (each GP, say) to make its views from, each binding its own grants. A shape open in every list would serve every
    ///     role, the manager's too, and the policy lets a manager only count a section's reason: such a plan is refused.
    /// </summary>
    private async Task<EntitledQuery> ShapedAsync(PracticeStore store, string sql,
        Chalk.Entitlements.Tenancy.TenancyPrincipal principal, CancellationToken stop)
    {
        var open = store.Entitlements.Explain(principal).Where(g => g.Grants > 0).Select(g => g.List).Distinct().ToArray();
        var shape = store.Entitlements.Bind(principal).Shape(open);
        if (_shaped.TryGetValue((sql, shape.CanonicalHash), out var query)) return query;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _shaped[(sql, shape.CanonicalHash)] = query = await store.Entitled.PrepareAsync(sql, shape, ct: stop);
        _log.LogInformation("Practice: a statement prepared for the shape of {Lists} in {Ms:F0} ms (plan {Plan:x16})",
            string.Join(", ", open), System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, query.PlanDigest);
        return query;
    }

    /// <summary>
    ///     A staff member's class: its views, found by the class's grants, or made, each from its shape's statement with
    ///     the class's grants bound, kept as <c>Practice:Maintenance</c> and <c>Practice:Validate</c> say: its schedule,
    ///     and its sections (none for a class the policy shows no section, such as reception). An idle one is brought up to
    ///     date.
    /// </summary>
    private async Task<StaffClass> ClassOfAsync(PracticeStore store, Person person, CancellationToken stop)
    {
        var emergencies = EmergenciesOf(person);
        var key = PracticePolicy.ClassOf(person, emergencies);
        if (_classes.TryGetValue(key, out var held))
        {
            if (held.Members.Count == 0) // (no reader yet to start over)
            {
                foreach (var view in held.Views) await UpdateAsync(view, stop);
                Mark(held);
            }

            return held;
        }

        var principal = store.Policy.ForClass(person, emergencies);
        var grants = store.Entitlements.Bind(principal);
        var (schedule, sections) = store.StatementsOf(person, emergencies);
        List<FeedView<FeedRow>> views = [];
        try
        {
            views.Add(await FeedView<FeedRow>.MakeAsync(store, $"class {key}: schedule", schedule.Sql,
                await ShapedAsync(store, schedule.Sql, principal, stop), grants, ["seq"],
                s => Rows.FeedMapper(new FeedColumns(s)), _viewPolicy, _log, stop, parameters: schedule.Parameters));
            if (sections is not null) // (reception and the manager read no section: the policy admits none)
                views.Add(await FeedView<FeedRow>.MakeAsync(store, $"class {key}: sections", sections.Sql,
                    await ShapedAsync(store, sections.Sql, principal, stop), grants, ["seq"],
                    s => Rows.FeedMapper(new FeedColumns(s)), _viewPolicy, _log, stop, parameters: sections.Parameters));
        }
        catch
        {
            foreach (var view in views) await view.DisposeAsync();
            throw;
        }

        var cls = new StaffClass { Key = key, Views = [.. views] };
        Mark(cls);
        _classes[key] = cls;
        _log.LogInformation("Practice: class {Class} made (plans {Plans}, its grants {Grants}), its views {Kept}, {Rows} rows; " +
                            "disclosure: {Columns}", key, string.Join(" and ", views.Select(v => $"{v.Query.PlanDigest:x16}")),
            cls.Schedule.Grants, cls.Schedule.Kept, views.Sum(v => v.Count),
            string.Join(", ", views.SelectMany(v => v.Query.Columns)
                .Where(c => !c.Name.EndsWith("__disclosure", StringComparison.Ordinal))
                .Select(c => $"{c.Name} {c.Disclosure}")));
        return cls;
    }

    /// <summary>
    ///     A manager's statistics: their class's topic, shared by every manager of their sites, from its two views (by the
    ///     hour, and by reason), each from its shape's statement with the class's grants bound, and kept as the screens'
    ///     are. An idle one is brought up to date.
    /// </summary>
    private async Task<StatsFeed> StatsOfAsync(PracticeStore store, Person person, CancellationToken stop)
    {
        var key = PracticePolicy.ClassOf(person);
        if (_stats.TryGetValue(key, out var held))
        {
            if (held.Routes.Count == 0)
            {
                for (var i = 0; i < held.Views.Length; i++)
                    if (await UpdateAsync(held.Views[i], stop))
                        held.Cursors[i] = ViewCursor.Start;
                Feed(held);
            }

            return held;
        }

        var principal = store.Policy.ForClass(person);
        var grants = store.Entitlements.Bind(principal);
        List<FeedView<StatsRow>> views = [];
        try
        {
            views.Add(await FeedView<StatsRow>.MakeAsync(store, $"stats {key}: by the hour", PracticeStore.StatsHours,
                await ShapedAsync(store, PracticeStore.StatsHours, principal, stop), grants, ["site_id", "hour_start"],
                s => Rows.StatsMapper(new StatsColumns(s)), _viewPolicy, _log, stop));
            views.Add(await FeedView<StatsRow>.MakeAsync(store, $"stats {key}: by reason", PracticeStore.StatsReasons,
                await ShapedAsync(store, PracticeStore.StatsReasons, principal, stop), grants, ["site_id"],
                s => Rows.StatsMapper(new StatsColumns(s)), _viewPolicy, _log, stop));
        }
        catch
        {
            foreach (var view in views) await view.DisposeAsync();
            throw;
        }

        var sites = string.Join(',', person.Sites.Select(s => People.SiteOf(s).Name.ToLowerInvariant()));
        var table = new Table<FeedKey, StatsRow>($"stats:{sites}", HomeLog) { Format = Formats.Stats };
        var stats = new StatsFeed
        {
            Key = key, Views = [.. views], Table = table,
            Ingest = table.Ingest<RowChange<StatsRow>>(
                BatchFold.Of<StatsRow, RowChange<StatsRow>, ReplaceOrDelete<StatsRow>>(), ScreenStaging)
        };
        _stats[key] = stats;
        Feed(stats);
        await AppliedAsync(stats.Ingest, stop);
        _log.LogInformation("Practice: {Topic} opened (plans {Plans}), its views {Kept}, {Rows} rows; disclosure: {Columns}",
            stats.Name, string.Join(" and ", views.Select(v => $"{v.Query.PlanDigest:x16}")), views[0].Kept, table.Count,
            string.Join(", ", views.SelectMany(v => v.Query.Columns)
                .Where(c => !c.Name.EndsWith("__disclosure", StringComparison.Ordinal))
                .Select(c => $"{c.Name} {c.Disclosure}").Distinct()));
        return stats;
    }

    /// <summary>
    ///     A shard's dispatcher topic: its view of the dispatch log's pending entries, as a dispatcher may read them, made
    ///     once and kept while a dispatcher (or an observer) holds it, its statement prepared with the dispatcher's grants
    ///     folded in (one principal, and a statement for each shard). A standby taking the shard over gets its whole state:
    ///     every pending entry.
    /// </summary>
    private async Task<NotifyFeed> NotifyOfAsync(PracticeStore store, int shard, CancellationToken stop)
    {
        if (_notify.TryGetValue(shard, out var held))
        {
            await AdvanceAsync(store, held, stop);
            await PostedAsync(held, stop); // (what's handed on, posted: what the dispatcher taking it is sent first)
            return held;
        }

        var sql = PracticeStore.NotifyFeed(shard);
        var view = await FeedView<NotifyRow>.MakeAsync(store, $"notify {shard}", sql,
            await store.Entitled.PrepareAsync(sql, store.Entitlements.Bind(store.Policy.ForDispatcher()), ct: stop), null, ["seq"],
            s => Rows.NotifyMapper(new NotifyColumns(s)), _viewPolicy, _log, stop, ViewVersions.Every, NotifyBacklog);
        var table = new Table<FeedKey, NotifyRow>($"notify:{shard}", HomeLog) { Format = Formats.Notify };
        var notify = new NotifyFeed
        {
            Shard = shard, View = view, Table = table,
            Ingest = table.Ingest<RowChange<NotifyRow>>(
                BatchFold.Of<NotifyRow, RowChange<NotifyRow>, ReplaceOrDelete<NotifyRow>>(), ScreenStaging),
            Pipe = new BatchPipe<NotifyBatch>(NotifyBatches, static () => new NotifyBatch())
        };
        _notify[shard] = notify;
        notify.Reader = Task.Run(() => ReadAsync(notify));
        await AdvanceAsync(store, notify, stop); // (its whole state, the first batch)
        await PostedAsync(notify, stop);
        _log.LogInformation("Practice: {Topic} opened (plan {Plan:x16}), its view {Kept}, {Rows} entries; disclosure: {Columns}",
            notify.Name, view.Query.PlanDigest, view.Kept, table.Count,
            string.Join(", ", view.Query.Columns.Where(c => !c.Name.EndsWith("__disclosure", StringComparison.Ordinal))
                .Select(c => $"{c.Name} {c.Disclosure}")));
        return notify;
    }

    /// <summary>
    ///     A site's screen: its topic, shared by every screen there, from its own view: its shape's statement (every
    ///     screen's), the screen's grants bound.
    /// </summary>
    private async Task<ScreenFeed> ScreenOfAsync(PracticeStore store, Person person, CancellationToken stop)
    {
        var site = person.Site!;
        if (_screens.TryGetValue(site.Id, out var held))
        {
            if (held.Routes.Count == 0)
            {
                if (await UpdateAsync(held.View, stop)) held.Cursor = ViewCursor.Start;
                Feed(held);
            }

            return held;
        }

        var principal = store.Policy.ForClass(person);
        var view = await FeedView<ScreenRow>.MakeAsync(store, $"screen {site.Name}", PracticeStore.ScreenFeed,
            await ShapedAsync(store, PracticeStore.ScreenFeed, principal, stop), store.Entitlements.Bind(principal), ["seq"],
            s => Rows.ScreenMapper(new FeedColumns(s)), _viewPolicy, _log, stop);
        var table = new Table<FeedKey, ScreenRow>($"screen:{site.Name.ToLowerInvariant()}", HomeLog)
            { Format = Formats.Screen };
        var screen = new ScreenFeed
        {
            Site = site, View = view, Table = table,
            Ingest = table.Ingest<RowChange<ScreenRow>>(
                BatchFold.Of<ScreenRow, RowChange<ScreenRow>, ReplaceOrDelete<ScreenRow>>(), ScreenStaging)
        };
        _screens[site.Id] = screen;
        Feed(screen);
        await AppliedAsync(screen.Ingest, stop);
        _log.LogInformation("Practice: {Topic} opened (plan {Plan:x16}), its view {Kept}, {Rows} rows; disclosure: {Columns}",
            screen.Name, view.Query.PlanDigest, view.Kept, table.Count,
            string.Join(", ", view.Query.Columns.Select(c => $"{c.Name} {c.Disclosure}")));
        return screen;
    }

    /// <summary>A route removed: its topic let go once its last route has, at the epoch it last accepted.</summary>
    private void Closed(Unsubscription notice)
    {
        foreach (var home in _homes.Values)
        {
            if (!ReferenceEquals(home.Table, notice.Topic)) continue;
            home.Routes.Remove(notice.Route);
            if (notice.Remaining > 0 || notice.Epoch != home.Epoch || home.Routes.Count > 0) return;
            LetGoHere(home);
            _log.LogInformation("Practice: {Topic} closed here: nobody here's watching", home.Name);
            return;
        }

        foreach (var screen in _screens.Values)
        {
            if (!ReferenceEquals(screen.Table, notice.Topic)) continue;
            screen.Routes.Remove(notice.Route);
            if (screen.Routes.Count == 0) screen.IdleSince = DateTime.UtcNow;
            return;
        }

        foreach (var stats in _stats.Values)
        {
            if (!ReferenceEquals(stats.Table, notice.Topic)) continue;
            stats.Routes.Remove(notice.Route);
            if (stats.Routes.Count == 0) stats.IdleSince = DateTime.UtcNow;
            return;
        }

        foreach (var notify in _notify.Values)
        {
            if (!ReferenceEquals(notify.Table, notice.Topic)) continue;
            notify.Routes.Remove(notice.Route);
            if (notify.Routes.Count == 0) notify.IdleSince = DateTime.UtcNow;
            return;
        }
    }

    /// <summary>
    ///     A person's topic here let go (nobody here holds it): no longer posted to. Their feed is kept, as on every instance,
    ///     until the log closes it on the last one.
    /// </summary>
    private void LetGoHere(Home home)
    {
        if (_openHere.Remove(home.Person.Id)) _closed.Add(home.Person.Id); // (closed here: the log's to say)
        home.Ingest?.Close();
        (home.Table, home.Ingest, home.Behind, home.Epoch) = (null, null, false, 0);
        home.Routes.Clear();
    }

    /// <summary>Classes, screens and statistics nobody's watched for a minute: their views let go (made again when asked for).</summary>
    private void LetIdleGo(DateTime now)
    {
        foreach (var cls in _classes.Values.Where(c => c.Members.Count == 0 && now - c.IdleSince > Idle).ToList())
        {
            _classes.Remove(cls.Key);
            foreach (var view in cls.Views) _ = view.DisposeAsync().AsTask();
            _log.LogInformation("Practice: class {Class}'s views let go: nobody in it is watching", cls.Key);
        }

        foreach (var screen in _screens.Values.Where(s => s.Routes.Count == 0 && now - s.IdleSince > Idle).ToList())
        {
            _screens.Remove(screen.Site.Id);
            _ = screen.View.DisposeAsync().AsTask();
            screen.Ingest.Close();
        }

        foreach (var stats in _stats.Values.Where(s => s.Routes.Count == 0 && now - s.IdleSince > Idle).ToList())
        {
            _stats.Remove(stats.Key);
            foreach (var view in stats.Views) _ = view.DisposeAsync().AsTask();
            stats.Ingest.Close();
        }

        foreach (var notify in _notify.Values.Where(n => n.Routes.Count == 0 && now - n.IdleSince > Idle).ToList())
        {
            _notify.Remove(notify.Shard); // (its entries wait in the log: the next dispatcher is sent every one)
            notify.Pipe.Complete(); // (its reader posts what it holds, then closes the topic)
            _ = notify.View.DisposeAsync().AsTask();
        }
    }

    /// <summary>
    ///     The access log: who was shown which records (made available to their feed while it was open: the router may
    ///     conflate a version away), each an activity's or a section's, but for deletions (<paramref name="changes" />,
    ///     beside the keys, unless whole). Worked out from the log on every instance alike, at the time the log gives (the
    ///     turn's, or the feed's opening's): a feed's first state, and what each turn changed in it, wherever it was open.
    ///     In record order, numbered as worked out, and written to ChalkQL's <c>access_log</c> with the next refresh; the
    ///     debug log says so too.
    /// </summary>
    private void Shown(Home home, DateTime at, ReadOnlySpan<FeedKey> keys, ReadOnlySpan<FeedRow> rows,
        ReadOnlySpan<RowChange<FeedRow>> changes = default)
    {
        _showing.Clear();
        for (var i = 0; i < keys.Length; i++)
            if (keys[i].Section is FeedKey.Activity or FeedKey.Clinical && (changes.IsEmpty || changes[i].Deleted == 0))
                _showing.Add((keys[i].Section, keys[i].Seq, // (why: the row's, changed or whole)
                    !changes.IsEmpty ? changes[i].Row.Basis : i < rows.Length ? rows[i].Basis : DisclosureBasis.None));
        if (_showing.Count == 0) return;
        _showing.Sort(); // (a view drains in its own order: the access log's is the records')
        var when = Unspecified(at);
        var debug = _log.IsEnabled(LogLevel.Debug) ? new List<string>() : null;
        foreach (var (part, seq, basis) in _showing)
        {
            var patient = part == FeedKey.Activity ? _patientOf.GetValueOrDefault(seq) : _sectionPatient.GetValueOrDefault(seq);
            var (booking, clinician) = part == FeedKey.Activity
                ? _activityVisit.GetValueOrDefault(seq)
                : _sectionVisit.GetValueOrDefault(seq);
            _shownStash.Add(new Shown(++_shownSeq, home.Person.Id, home.Person.User, home.Name, part, seq, patient,
                People.Find(patient)?.Sites[0] ?? home.Person.Sites[0], when, booking, clinician, basis));
            debug?.Add(part == FeedKey.Clinical ? $"s{seq}" : seq.ToString());
        }

        if (debug is { Count: > 0 })
            _log.LogDebug("Practice: shown to {Person} ({Feed}): {Rows}", home.Person.User, home.Name, string.Join(',', debug));
    }

    /// <summary>Once what's posted to the ingest is applied.</summary>
    private static Task AppliedAsync<TRow>(TableIngest<FeedKey, TRow, RowChange<TRow>> ingest, CancellationToken stop)
        where TRow : unmanaged
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ingest.WhenApplied(_ => applied.TrySetResult());
        return applied.Task.WaitAsync(TimeSpan.FromSeconds(10), stop);
    }

    private static FeedParameters? ParametersOf(string? parameters)
    {
        try
        {
            return parameters is null ? null : JsonSerializer.Deserialize<FeedParameters>(parameters, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The engine's clock: now, in UTC.</summary>
    private static DateTimeOffset Clock(DateTime utc)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    /// <summary>A wall-clock time as ChalkQL's TIMESTAMP holds it: UTC, of kind Unspecified.</summary>
    private static DateTime Unspecified(DateTime utc)
    {
        return DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Step 1's parameters, as the app sets them: the person (the connection signed in as them); or a dispatcher (its name) and the shard it
    ///     takes; or an instance of this service following the log (its name), for the log's signal.
    /// </summary>
    public sealed record FeedParameters(int Person, string? Dispatcher = null, int Shard = 0, string? Follower = null,
        string? Feed = null);

    /// <summary>A statement prepared once from a context's shape (every patient's plan), and where its columns are.</summary>
    private sealed record Shaped(EntitledQuery Query, FeedColumns Columns)
    {
        public static async Task<Shaped> PrepareAsync(PracticeStore store, string sql, Chalk.Client.RequestContext shape,
            CancellationToken stop)
        {
            var query = await store.Entitled.PrepareAsync(sql, shape, ct: stop);
            return new Shaped(query, new FeedColumns(query.OutputSchema));
        }
    }

    private enum CommandKind
    {
        Arrive,
        Call,
        Acknowledge,
        Seen,
        Message,
        Share,
        Withdraw,
        BreakGlass,
        Release,
        RevokeCarer,
        Notice,
        Reply
    }

    private abstract record Work;

    /// <summary>The log moved on: for the loop to read.</summary>
    private sealed record LogAdvanced : Work
    {
        public static readonly LogAdvanced Instance = new();
    }

    /// <summary>A drain (<c>POST /drain</c>): the lease resigned, the routers told to move this instance's clients.</summary>
    private sealed record DrainRequest(TimeSpan Over, TimeSpan Grace) : Work
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Step 1, for the loop to answer: a person's feed, or (a dispatcher named) a shard's dispatcher topic.</summary>
    private sealed record Open(SubscribeRequest Request, Person? Person) : Work
    {
        public volatile bool Abandoned;
        public string? Dispatcher { get; init; }
        public string? Follower { get; init; }
        public int Shard { get; init; }
        public string? Feed { get; init; } // ("stats": a manager's statistics; none: the person's own topic)
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A dispatcher's report of an entry (its shard's number): handed off, failed or recorded.</summary>
    private sealed record DispatchReport(string Dispatcher, int Shard, long Seq, string Outcome, string Reason, byte[] Correlation) : Work
    {
        public TaskCompletionSource<CommandAnswer> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static DispatchReport From(ReportInput input)
        {
            return new DispatchReport(input.Dispatcher, input.Shard, input.Seq, input.Outcome, input.Reason,
                Convert.FromHexString(input.Correlation));
        }
    }

    /// <summary>A person's explanation, for the loop to work out.</summary>
    private sealed record ExplainRequest(Person Person) : Work
    {
        public TaskCompletionSource<Explanation> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A section's note in full, for the loop to read as the person.</summary>
    private sealed record NoteRequest(Person Person, long Section) : Work
    {
        public TaskCompletionSource<NoteAnswer> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record AuditRequest(Person Person, byte Part, long Seq) : Work
    {
        public TaskCompletionSource<AuditAnswer> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary><c>/measure</c>: the loop's counters, read on it.</summary>
    private sealed record MeasureRequest(bool Reset) : Work
    {
        public TaskCompletionSource<MeasureReport> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary><c>/oracle</c>: every open topic, for the loop to check once this turn's writes are posted.</summary>
    private sealed record OracleRequest : Work
    {
        public TaskCompletionSource<OracleReport[]> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary><c>/view</c>: a person's topic, for the loop to render: its newest activity (of those the router holds).</summary>
    private sealed record ViewRequest(Person Person, long Focus, long Held, string Topic) : Work
    {
        public TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary><c>/access</c>: who was shown a record, for the loop to read as the auditor.</summary>
    private sealed record AccessRequest(byte Part, long Seq) : Work
    {
        public TaskCompletionSource<ShownTo[]> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary><c>/dispatches</c>: the dispatch log, as the loop has it.</summary>
    private sealed record LogRequest : Work
    {
        public TaskCompletionSource<DispatchLog> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Close(Unsubscription Notice) : Work;

    /// <summary>A command: what it's about (a booking, an activity, a GP, a consent, a patient), and its room, text or tag.</summary>
    private sealed record Command(CommandKind Kind, int Person, long Booking, string Room, byte[] Correlation) : Work
    {
        public long[] Activities { get; init; } = [];
        public string Text => Room;

        /// <summary>Its sender's order for it (<see cref="CommandOrder" />), if any: its key, and its sender's id as hex beside it.</summary>
        public CommandOrder? Order
        {
            get;
            init => (field, Ordered) = (value, value is { Key.Length: > 0 } o ? (Convert.ToHexString(o.Sender), o.Key) : null);
        }

        public (string Sender, string Key)? Ordered { get; private init; }

        public TaskCompletionSource<CommandAnswer> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>A command as the log has it (another instance took it in): run here as it was there.</summary>
        public static Command From(CommandInput input)
        {
            return new Command(Enum.Parse<CommandKind>(input.Kind), input.Person, input.Booking, input.Text,
                Convert.FromHexString(input.Correlation))
            {
                Activities = input.Activities,
                Order = input.OrderKey.Length > 0
                    ? new CommandOrder { Key = input.OrderKey, Sender = Convert.FromHexString(input.Sender), Seq = input.Seq }
                    : null
            };
        }
    }

    /// <summary>
    ///     A person's feed, on every instance while it's open on any (the log says where and when): what it's shown is worked
    ///     out from it here as on every instance. Its topic, and its routes, only where someone holds it (<see cref="Here" />).
    /// </summary>
    private sealed class Home
    {
        public readonly Dictionary<RouteId, Mailbox> Routes = [];
        public readonly Dictionary<long, FeedRow> Messages = []; // (a staff member's topic's: the messages their schedule holds)
        public readonly Dictionary<FeedKey, FeedRow> Own = []; // (its topic's own rows, as last posted: tasks, access, status)
        public readonly Dictionary<FeedKey, FeedRow> Rows = []; // (a patient's: what their statements hold, as last worked out)
        public uint Checksum; // (a patient's rows')
        public bool Behind; // (a patient's or carer's topic, while their site falls behind: their feed whole, once it's over)
        public uint OwnChecksum; // (its own rows')
        public StaffClass? Class;
        public readonly ViewCursor[] Cursors = [ViewCursor.Start, ViewCursor.Start]; // (a staff member's topic: one a class view)
        public long Epoch;
        public Table<FeedKey, FeedRow>? Table; // (its topic, while someone here holds it)
        public TableIngest<FeedKey, FeedRow, RowChange<FeedRow>>? Ingest;
        public required Person Person { get; init; }
        public string Name => $"home:{Person.Id}";

        /// <summary>Whether someone here holds it: its topic is this instance's to post to.</summary>
        public bool Here => Table is not null;

        /// <summary>Every part from the start: its whole state, next.</summary>
        public void Restart()
        {
            Cursors.AsSpan().Fill(ViewCursor.Start);
        }
    }

    /// <summary>
    ///     An entitlement class: its views (its statements prepared with its grants folded in: its schedule, and a GP's
    ///     sections); its members' topics.
    /// </summary>
    private sealed class StaffClass
    {
        public readonly List<Home> Members = []; // (each member's feed open anywhere)
        public readonly ViewCursor[] Seen = [ViewCursor.Start, ViewCursor.Start]; // (the access log's: each view's, turn by turn)
        public readonly Drained<FeedRow>[] Changed = [new(), new()]; // (what a turn changed: every member's alike)
        public DateTime IdleSince;
        public required string Key { get; init; }
        public required FeedView<FeedRow>[] Views { get; init; }
        public FeedView<FeedRow> Schedule => Views[0];
    }

    /// <summary>A manager class's statistics: its views (by the hour, by reason), and its topic, shared.</summary>
    private sealed class StatsFeed
    {
        public readonly ViewCursor[] Cursors = [ViewCursor.Start, ViewCursor.Start];
        public readonly Dictionary<RouteId, (Mailbox Mailbox, int Person)> Routes = []; // (whose: a command's rendezvous)
        public long Epoch;
        public DateTime IdleSince;
        public required string Key { get; init; }
        public required FeedView<StatsRow>[] Views { get; init; }
        public required Table<FeedKey, StatsRow> Table { get; init; }
        public required TableIngest<FeedKey, StatsRow, RowChange<StatsRow>> Ingest { get; init; }
        public string Name => Table.Label!;

        public void Restart()
        {
            Cursors.AsSpan().Fill(ViewCursor.Start);
        }
    }

    /// <summary>
    ///     A shard's dispatcher topic: its view, which publishes every version; its topic, and who holds it (a dispatcher by
    ///     name, or an observer); and the pipe between them: the loop drains each version into a batch, and the topic's
    ///     reader posts it.
    /// </summary>
    private sealed class NotifyFeed
    {
        public readonly Dictionary<RouteId, (Mailbox Mailbox, string Dispatcher)> Routes = [];
        public readonly List<(long Version, Mailbox Mailbox, byte[] Correlation)> Owed = []; // (rendezvous, each after its version)
        public readonly Lock Gate = new(); // (what its reader has posted, and who waits for it)
        public readonly List<(long Version, TaskCompletionSource Done)> Waiting = [];
        public ViewCursor Cursor = ViewCursor.Start;
        public long Epoch, Through, Handed, HandedOn, Deferred; // (the loop's: drained through, handed through; batches; waits)
        public long Posted; // (its reader's, under Gate: the version it's posted through)
        public bool Restart; // (its reader's word: the topic full, or faulted; its whole state next)
        public DateTime IdleSince;
        public Task? Reader;
        public required int Shard { get; init; }
        public required FeedView<NotifyRow> View { get; init; }
        public required Table<FeedKey, NotifyRow> Table { get; init; }
        public required TableIngest<FeedKey, NotifyRow, RowChange<NotifyRow>> Ingest { get; init; }
        public required BatchPipe<NotifyBatch> Pipe { get; init; }
        public string Name => Table.Label!;
    }

    /// <summary>A version of a dispatcher topic's view, for its reader: its changes (or whole state), and the rendezvous after it.</summary>
    private sealed class NotifyBatch
    {
        public readonly List<(Mailbox Mailbox, byte[] Correlation)> Rendezvous = [];
        public readonly Drained<NotifyRow> Rows = new();

        public void Clear()
        {
            Rows.Clear();
            Rendezvous.Clear();
        }
    }

    /// <summary>A site's screen: its view, and its topic, shared.</summary>
    private sealed class ScreenFeed
    {
        public readonly Dictionary<RouteId, Mailbox> Routes = [];
        public ViewCursor Cursor = ViewCursor.Start;
        public long Epoch;
        public DateTime IdleSince;
        public required Site Site { get; init; }
        public required FeedView<ScreenRow> View { get; init; }
        public required Table<FeedKey, ScreenRow> Table { get; init; }
        public required TableIngest<FeedKey, ScreenRow, RowChange<ScreenRow>> Ingest { get; init; }
        public string Name => Table.Label!;
    }
}
