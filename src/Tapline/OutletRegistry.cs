using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Tapline;

/// <summary>
///     Live outlets by id (lock-free lookup for inbound acks), each paired with a random nonce, and the
///     <see cref="TimingWheel" /> that wakes rate-limited outlets.
/// </summary>
public sealed class OutletRegistry
{
    private readonly Stack<int> _free = new();

    private readonly Lock _gate = new();

    private readonly Lock _healthLock = new();
    private Outlet?[] _byId = new Outlet?[64];
    private RouterHealth _health;
    private int _next, _count;
    private long _serial;

    public OutletRegistry() : this(new TimingWheel())
    {
    }

    public OutletRegistry(TimingWheel wheel) : this(wheel, new BlobStore(time: wheel.TimeProvider))
    {
    }

    public OutletRegistry(TimingWheel wheel, BlobStore blobs)
    {
        Wheel = wheel;
        Blobs = blobs;
        _health = new RouterHealth(RouterState.Serving, null, wheel.TimeProvider.GetUtcNow(), TimeSpan.Zero,
            wheel.TimeProvider);
    }

    /// <summary>The router's state, as every client's heartbeat carries it: serving from the start.</summary>
    public RouterHealth Health => Volatile.Read(ref _health);

    public TimingWheel Wheel { get; }

    /// <summary>Out-of-band payloads for every outlet in this registry (fetched over HTTP by clients).</summary>
    public BlobStore Blobs { get; }

    /// <summary>The portable stale-data functions of the subscriptions bound, for their clients to fetch.</summary>
    public ModuleRegistry Modules { get; } = new();

    private Publisher? _authentication;

    /// <summary>The router its clients sign in through (set as an authentication provider is registered: <see cref="Publisher.ProvideAuthentication" />).</summary>
    internal Publisher? Authentication
    {
        get => Volatile.Read(ref _authentication);
        set => Volatile.Write(ref _authentication, value);
    }

    /// <summary>Outlets' own frame buffers, passed on from one that has gone to the next.</summary>
    internal FrameBufferPool FrameBuffers { get; } = new();

    /// <summary>Snapshot of the id table; entries may be null.</summary>
    public ReadOnlySpan<Outlet?> Outlets => Volatile.Read(ref _byId);

    /// <summary>Outlets registered now.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The router's state changed (<see cref="SetHealth" />): the change, its correlation id among it — to log.</summary>
    public event Action<RouterHealth>? HealthChanged;

    /// <summary>
    ///     Sets the router's state — its host's to decide: degraded from its own health checks, damped against flapping;
    ///     draining as it stops, with <paramref name="grace" /> for its clients to move. A change gets its own correlation id
    ///     and goes to every client at once, a heartbeat each (as soon as its last one is echoed); the same state again
    ///     (degraded: for the same reason) is no change. Draining is one way: once draining, nothing else is set.
    /// </summary>
    public RouterHealth SetHealth(RouterState state, string? reason = null, TimeSpan grace = default)
    {
        RouterHealth next;
        lock (_healthLock)
        {
            var now = _health;
            if (now.State == RouterState.Draining ||
                (state == now.State && (state != RouterState.Degraded || reason == now.Reason))) return now;
            next = new RouterHealth(state, state == RouterState.Degraded ? reason : null,
                Wheel.TimeProvider.GetUtcNow(), grace, Wheel.TimeProvider);
            Volatile.Write(ref _health, next);
        }

        foreach (var o in Volatile.Read(ref _byId)) o?.HeartbeatSoon();
        HealthChanged?.Invoke(next);
        return next;
    }

    /// <summary>
    ///     Its id (a slot in the table, reused once it's gone), a random nonce, and its serial (never reused in this
    ///     process).
    /// </summary>
    internal (int Id, uint Nonce, long Serial) Register(Outlet outlet)
    {
        lock (_gate)
        {
            var id = _free.Count > 0 ? _free.Pop() : _next++;
            var a = _byId;
            if (id >= a.Length)
            {
                Array.Resize(ref a, a.Length * 2);
                Volatile.Write(ref _byId, a);
            }

            Volatile.Write(ref a[id], outlet);
            _count++;
            return (id, (uint)Random.Shared.Next(1, int.MaxValue), ++_serial);
        }
    }

    internal void Unregister(Outlet outlet)
    {
        lock (_gate)
        {
            var a = Volatile.Read(ref _byId);
            if (ReferenceEquals(a[outlet.Id], outlet))
            {
                a[outlet.Id] = null;
                _free.Push(outlet.Id);
                _count--;
            }
        }
    }

    /// <summary>The outlet with this id, if the nonce matches (so clients can only address their own outlet).</summary>
    public Outlet? Find(long id, long nonce)
    {
        var a = Volatile.Read(ref _byId);
        return (ulong)id < (ulong)a.Length && a[id] is { } o && o.Nonce == nonce ? o : null;
    }

    /// <summary>The live outlet with this serial, if any (a scan of the table: for the viewer, not a hot path).</summary>
    public Outlet? BySerial(long serial)
    {
        foreach (var o in Volatile.Read(ref _byId))
            if (o?.Serial == serial)
                return o;
        return null;
    }

    /// <summary>The live outlet with this id (no nonce check: for callers that authenticate otherwise, e.g. a blob tag).</summary>
    internal Outlet? ById(long id)
    {
        var a = Volatile.Read(ref _byId);
        return (ulong)id < (ulong)a.Length ? a[id] : null;
    }

    public void HeartbeatAll()
    {
        foreach (var o in Volatile.Read(ref _byId)) o?.Heartbeat();
        Blobs.Sweep();
    }
}

/// <summary>
///     Process-wide registry of named sources (state topics, message topics) and data providers — the router between data
///     providers and subscribers (see <see cref="IDataProvider" />). Nothing goes to a client unsolicited: what reaches it
///     is
///     what it subscribed to.
/// </summary>
public sealed partial class Publisher(OutletRegistry outlets)
{
    private const int RecentDead = 32;
    private const int DeadRemembered = 4096;
    private static readonly Predicate<Outlet> Gone = static o => o.IsDisposed;
    private readonly HashSet<Guid> _dead = []; // ids deemed dead (the latest DeadRemembered)
    private readonly HashSet<Guid> _revivable = []; // of those, deemed dead by a verdict it can prove wrong (see Apply)
    private readonly Queue<Guid> _deadOrder = new();
    private long _sweptAt; // when the last sweep began (Stopwatch ticks): one long after, the router was paused
    private readonly Lock _directory = new();
    private readonly List<Holder> _due = [], _silent = [], _late = []; // HeartbeatProviders' scratch

    private readonly Dictionary<(string Name, ProviderEndpoint Endpoint), Holder>
        _instances = new(); // the live instance at each name and endpoint

    private readonly byte[]
        _partitionKey =
            RandomNumberGenerator.GetBytes(32); // this run's alone: ids that mean nothing across runs, or routers

    private readonly List<(ProviderEndpoint Endpoint, ProviderLink Link)> _probes = [];
    private readonly Dictionary<string, Service> _services = new(); // who answers step 1, and who waits for it

    private readonly ConcurrentDictionary<string, object> _sources = new();

    private readonly List<(string Service, Outlet[] Waiting, TimeSpan Spread, int Up)> _ups = [];

    // Where route messages go: copy-on-write — swapped under the directory lock (registering, withdrawing: rare), read
    // without it (every relay and health check). Small, and read far more than written: frozen.
    private FrozenDictionary<ProviderEndpoint, ProviderLink> _endpoints =
        FrozenDictionary<ProviderEndpoint, ProviderLink>.Empty;

    private int _heartbeating, _again; // (a sweep running; one asked for meanwhile: run after it)

    // The viewer's copies of the directory's tables: swapped under the directory lock when they change (rarely), read
    // without it — the viewer never takes a lock the router's threads wait on.
    private Holder[] _holderList = [];
    private long _instancesDown, _routesDrained, _clientsMoved, _routesMoved, _routesLetGo;
    private RouterBeat? _knows, _knowsNot;
    private Holder[] _recentDead = []; // the last instances deemed dead (copy-on-write, power of two)
    private Service[] _serviceList = [];

    /// <summary>The outlets (clients) it routes to.</summary>
    public OutletRegistry Outlets { get; } = outlets;

    private FrozenDictionary<ProviderEndpoint, ProviderLink> Endpoints => Volatile.Read(ref _endpoints);

    /// <summary>
    ///     This router: its stable name and time-ordered id (a UUIDv7, drawn here), in every message it sends a provider. A
    ///     restart is a newer id, which supersedes this one at every provider. Set it before the first <see cref="Provide" />.
    /// </summary>
    public RouterInstance Router { get; init; } = RouterInstance.Start(Environment.MachineName);

    /// <summary>
    ///     How the router decides a service is up again for the clients waiting on it, unless set per service
    ///     (<see cref="SetServiceUp" />). Default: as soon as one instance serves, each client told within a second
    ///     (jittered).
    /// </summary>
    public ServiceUpPolicy ServiceUp { get; set; } = new(Spread: TimeSpan.FromSeconds(1));

    /// <summary>
    ///     How long step 1 may take: a provider that hasn't answered by then is abandoned, and the service is
    ///     unavailable.
    /// </summary>
    public TimeSpan SubscribeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // ───────────────────────────── provider instances ─────────────────────────────

    /// <summary>
    ///     How often the router checks an instance holding routes (<see cref="IProviderHealth.CheckHealthAsync" />), and
    ///     probes
    ///     the endpoints of a service clients wait on (and those not serving, to see them back), at most — as often as
    ///     <see cref="HeartbeatProviders" /> is called, and one at a time (none sent while one is unanswered).
    /// </summary>
    public TimeSpan ProviderHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     An instance holding routes that hasn't answered with its own id for this long is dead; one that hasn't
    ///     answered at all, not up.
    /// </summary>
    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How many of a service's serving instances a step 1 picks from, at random — the least loaded of them taking it, by
    ///     the load each reports (<see cref="HealthReport.Load" />; one not reported, or older than
    ///     <see cref="ProviderTimeout" />, counts as idle). 2 by default: two random choices spread subscriptions far better
    ///     than the least loaded of all, which loads a moment old would send every step 1 to at once. 1: one at random.
    ///     While none of the service's instances has reported a load, each in turn.
    /// </summary>
    public int ProviderChoices { get; set; } = 2;

    /// <summary>Instances deemed dead so far: stopped answering, restarted (a new id), withdrawn, or refusing this router.</summary>
    public long InstancesDown => Interlocked.Read(ref _instancesDown);

    /// <summary>
    ///     Routes a drain closed so far (their clients told drained): at their turn with no grace, or still held when it
    ///     ran out.
    /// </summary>
    public long RoutesDrained => Interlocked.Read(ref _routesDrained);

    /// <summary>Clients a drain has told to move (draining, with a grace) so far.</summary>
    public long ClientsMoved => Interlocked.Read(ref _clientsMoved);

    /// <summary>Subscriptions a move has told their clients to move (a rebalance, with a grace) so far.</summary>
    public long RoutesMoved => Interlocked.Read(ref _routesMoved);

    /// <summary>Subscriptions a move let go so far: still held when their grace ran out (<see cref="FaultCode.Moved" />).</summary>
    public long RoutesLetGo => Interlocked.Read(ref _routesLetGo);

    /// <summary>
    ///     How late an instance holding routes may be to answer its health check before it is deemed degraded (2 s): its
    ///     subscribers shown their stale data (<see cref="ErrorPolicy" />) and held — not ended; it recovers when it answers
    ///     healthy again. Late from when it was asked: a check sent and not answered since (failing counts as unanswered) —
    ///     never the router's own pacing, a check not yet sent. Gone, it is deemed dead: <see cref="ProviderTimeout" />. An
    ///     instance reporting itself not serving is degraded at once. At or beyond <see cref="ProviderTimeout" />: never
    ///     degraded for lateness.
    /// </summary>
    public TimeSpan ProviderDegradedAfter { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Damping recoveries against flapping — a node's, or a service path's on it: the first time it degrades within
    ///     <see cref="ProviderFlapWindow" /> it recovers at its first healthy answer; each further time, only once it has
    ///     answered healthy for this long (2 s), doubled for each degradation since the first, up to
    ///     <see cref="ProviderMaxRecoverHold" /> (1 min). Zero: never held.
    /// </summary>
    public TimeSpan ProviderRecoverHold { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest <see cref="ProviderRecoverHold" /> grows to.</summary>
    public TimeSpan ProviderMaxRecoverHold { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How far back degradations count towards holding a recovery (5 min).</summary>
    public TimeSpan ProviderFlapWindow { get; set; } = TimeSpan.FromMinutes(5);

    // ───────────────────────────── drains ─────────────────────────────

    /// <summary>
    ///     The grace a drain gives each client when the provider names none (it reported
    ///     <see cref="ProviderStatus.Draining" />
    ///     without the router hearing its drain): how long its routes stay open after it is told to move. Zero: none —
    ///     break-before-make.
    /// </summary>
    public TimeSpan DrainGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest grace a drain, or a move, may give (a provider asking for more gets this).</summary>
    public TimeSpan MaxDrainGrace { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     How long a subscription stays put after it was made before a move takes it (<see cref="MoveNode" />,
    ///     <see cref="IRouterChannel.MoveAsync" />): one just moved isn't moved again at once — no flapping. 30 s.
    /// </summary>
    public TimeSpan MoveDwell { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How much less loaded another instance must be than the one a move is off (by the loads they report:
    ///     <see cref="HealthReport.Load" />) for its subscriptions to have somewhere better to go — 0.1. None is: the move
    ///     is refused, so instances loaded alike don't swap subscriptions to no end. An instance that hasn't reported a
    ///     load can't be compared: any other serving counts.
    /// </summary>
    public double MoveMargin { get; set; } = 0.1;

    /// <summary>
    ///     A provider refused this router — it presumes it dead (silent past its timeout), or superseded by a newer router of
    ///     its name: the endpoint, and the instance there (if known). The routes it held for this router have been closed
    ///     (their clients told the service is down), and the endpoint gets no step 1s from here while it refuses. A router
    ///     told this repeatedly is a zombie — another of its name has taken over — and its host may want to stop it.
    /// </summary>
    public event Action<ProviderEndpoint, ProviderInstance?>? RefusedBy;

    /// <summary>
    ///     A provider refused a subscription, saying why (<see cref="SubscribeRequest.Refuse(string?)" />) — for the router's log, with
    ///     the refusal's correlation id: the one the provider has, and the client is sent (a <see cref="Wire.Refused" />
    ///     record). Raised as step 1 ends, before the subscriber's <see cref="SubscriptionRefusedException" />.
    /// </summary>
    public event Action<SubscriptionRefusal>? SubscriptionRefused;

    /// <summary>
    ///     A change in a provider's health the router tells its clients — a node down, draining or drained, degraded or
    ///     recovered (or a service path on it), a service up for the clients waiting on it — with the change's correlation id,
    ///     the one its clients are told: for the router's log (Tapline.AspNetCore logs it). Raised as it's told, off the
    ///     router's locks.
    /// </summary>
    public event Action<ServiceHealthChange>? ServiceHealthChanged;

    private void Changed(ServiceHealthEvent what, Guid correlation, Holder? h, string? path, string? scope,
        string reason, int clients, TimeSpan grace = default)
    {
        ServiceHealthChanged?.Invoke(new ServiceHealthChange(correlation, what, h?.NodeId, h?.Identity, path, scope,
            reason, clients, grace, DateTimeOffset.UtcNow));
    }

    /// <summary>
    ///     A partition's path as this router's clients see it (<see cref="TapBinding.Partition" />): each segment an HMAC —
    ///     keyed at random as the router starts — chained from the service's and the segments before it, as 16 hex digits,
    ///     joined by '/'. The same partition, the same ids, for as long as this router runs; another router, or this one
    ///     restarted, others: never to be stored, or compared across runs. A partition's ids begin with its parent's: health
    ///     told for one covers those under it.
    /// </summary>
    public string PartitionId(string service, string partition)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_partitionKey, Encoding.UTF8.GetBytes(service), mac);
        var id = new StringBuilder(partition.Length * 2);
        foreach (var segment in partition.Split('/'))
        {
            var input = new byte[8 + Encoding.UTF8.GetByteCount(segment)];
            mac[..8].CopyTo(input);
            Encoding.UTF8.GetBytes(segment, input.AsSpan(8));
            HMACSHA256.HashData(_partitionKey, input, mac);
            if (id.Length > 0) id.Append('/');
            id.Append(Convert.ToHexStringLower(mac[..8]));
        }

        return id.ToString();
    }

    /// <summary>A partition's path: segments separated by '/', none empty, no control characters, at most 255 bytes of UTF-8.</summary>
    internal static void CheckPartition(string partition)
    {
        if (partition.Length == 0 || Encoding.UTF8.GetByteCount(partition) > 255)
            throw new ArgumentException("A partition is a path of 1 to 255 bytes (UTF-8).", nameof(partition));
        foreach (var segment in partition.Split('/'))
            if (segment.Length == 0 || segment.Any(char.IsControl))
                throw new ArgumentException(
                    $"A partition's segments are separated by '/', none empty, no control characters: '{partition}'.",
                    nameof(partition));
    }

    /// <summary>
    ///     Whether <paramref name="path" /> covers a route at <paramref name="routePath" />: the same, or an ancestor
    ///     (segment by segment).
    /// </summary>
    internal static bool Covers(string path, string routePath)
    {
        return routePath.StartsWith(path, StringComparison.Ordinal) &&
               (routePath.Length == path.Length || routePath[path.Length] == '/');
    }

    /// <summary>A service path as clients see it: the service, then the partition's ids.</summary>
    internal string ScopeOf(string path)
    {
        var slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash] + "/" + PartitionId(path[..slash], path[(slash + 1)..]);
    }

    private Service ServiceNamed(string name)
    {
        if (!_services.TryGetValue(name, out var service))
        {
            _services[name] = service = new Service(name);
            Volatile.Write(ref _serviceList, [.. _services.Values]); // the viewer's copy: read without the lock
        }

        return service;
    }

    /// <summary>
    ///     Registers a provider of <paramref name="service" /> at <paramref name="endpoint" />, reached through
    ///     <paramref name="provider" /> (in process, the provider itself; in another, a client for its connection) — and, the
    ///     first time, introduces this router to it (<see cref="IDataProvider.ConnectAsync" />). A service may have several
    ///     endpoints — step 1 goes to each serving one in turn — and an endpoint several services; an endpoint is one
    ///     provider's. A service once registered stays known: with no endpoint left (withdrawn) it is down, not unknown.
    /// </summary>
    public void Provide(string service, ProviderEndpoint endpoint, IDataProvider provider)
    {
        if (service.Length == 0 || service.Contains('/') || service.Any(char.IsControl))
            throw new ArgumentException(
                $"A service's name is one segment of a service path: no '/', no control characters ('{service}').",
                nameof(service));
        ProviderLink? fresh;
        lock (_directory)
        {
            fresh = LinkOf(endpoint, provider);
            var entry = ServiceNamed(service);
            if (Array.IndexOf(entry.Endpoints, endpoint) >= 0)
                throw new InvalidOperationException($"{endpoint} provides '{service}' already.");
            entry.Endpoints = [.. entry.Endpoints, endpoint]; // (clients waiting on it: probed from the next heartbeat)
        }

        if (fresh is not null) _ = ConnectAsync(fresh);
    }

    /// <summary>
    ///     The link to <paramref name="endpoint" />'s provider — made the first time (returned: to introduce the router to it),
    ///     null if it was made before. Under the directory lock.
    /// </summary>
    private ProviderLink? LinkOf(ProviderEndpoint endpoint, IDataProvider provider)
    {
        if (_endpoints.TryGetValue(endpoint, out var link))
        {
            if (!ReferenceEquals(link.Provider, provider))
                throw new InvalidOperationException($"{endpoint} is another provider's.");
            return null;
        }

        var made = new ProviderLink(endpoint, provider, new RouterChannel(this));
        if (provider is IReachableProvider reachable) // (its link up again: probed at once)
            reachable.ReachableChanged += made.OnReachable = () => Reached(made);
        Volatile.Write(ref _endpoints, _endpoints.Append(KeyValuePair.Create(endpoint, made)).ToFrozenDictionary());
        return made;
    }

    private static async Task ConnectAsync(ProviderLink link)
    {
        try
        {
            await link.Provider.ConnectAsync(link.Back, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        } // the provider's to handle: it may still serve, unintroduced
    }

    /// <summary>
    ///     Withdraws an endpoint: no more step 1s to it, for any service; the instances there are deemed dead — their routes
    ///     closed, their clients told the service is down. What was relayed before is still delivered.
    /// </summary>
    public void Withdraw(ProviderEndpoint endpoint)
    {
        ProviderLink? link;
        List<Holder> there = [];
        lock (_directory)
        {
            if (!_endpoints.TryGetValue(endpoint, out link)) return;
            Volatile.Write(ref _endpoints, _endpoints.Where(kv => kv.Key != endpoint).ToFrozenDictionary());
            foreach (var entry in _services.Values)
                entry.Endpoints = Array.FindAll(entry.Endpoints, e => e != endpoint);
            foreach (var h in _instances.Values)
                if (h.Identity.Endpoint == endpoint)
                    there.Add(h);
        }

        foreach (var h in there) Kill(h, "withdrawn: its endpoint unregistered");
        if (link.Provider is IReachableProvider reachable && link.OnReachable is { } reached)
            reachable.ReachableChanged -= reached;
        link.Close();
    }

    /// <summary>
    ///     A provider reached again, over its link: probed at once, not at the next heartbeat, so the clients waiting on its
    ///     services hear sooner. Not reached: nothing to do (step 1 skips it).
    /// </summary>
    private void Reached(ProviderLink link)
    {
        if (!link.Reachable) return;
        lock (_directory)
        {
            link.ProbedAt = 0;
        }

        SweepAgain();
    }

    /// <summary>Whether a client waits on a service this endpoint provides.</summary>
    private bool Waited(ProviderEndpoint endpoint)
    {
        lock (_directory)
        {
            foreach (var service in _services.Values)
                if (service.Waiting.Count > 0 && Array.IndexOf(service.Endpoints, endpoint) >= 0)
                    return true;
        }

        return false;
    }

    /// <summary>
    ///     Sets <paramref name="service" />'s <see cref="ServiceUpPolicy" />: how many instances up, or how long after
    ///     the first, and over how long to spread the notices.
    /// </summary>
    public void SetServiceUp(string service, ServiceUpPolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.Quorum, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.Spread, TimeSpan.Zero);
        lock (_directory)
        {
            ServiceNamed(service).Policy = policy;
        }
    }

    /// <summary>
    ///     Step 1 of a subscribe, through one of the service's providers (<see cref="IDataProvider" />; with several, each
    ///     serving one in turn — not one draining, not serving, refusing this router, or not reached: its link down,
    ///     <see cref="IReachableProvider" />): the subscriber's route to the topic
    ///     it chose — bound, not yet streaming: hand <see cref="Tap.Binding" /> to the client, which completes it (step 2).
    ///     The
    ///     route records the instance that accepted it (<see cref="Tap.Provider" />): its step 2 and end are relayed there,
    ///     and
    ///     while it holds routes the router checks its health. The client's binding carries only the router's transient id for
    ///     it (<see cref="TapBinding.NodeId" />). Null if the provider refused. Dispose the tap to unsubscribe.
    ///     <paramref name="lane" />: the route's scheduling class (the server's choice, not the provider's; see
    ///     <see cref="Outlet.Tap" />).
    ///     <para>
    ///         Throws <see cref="ServiceUnavailableException" /> when no instance answers — none serving and reached, the one
    ///         tried not within
    ///         <see cref="SubscribeTimeout" />, or it refused this router — and the outlet is told when the service is up
    ///         (<see cref="Wire.ServiceUp" />). A step 1 that fails (times out, is cancelled, the provider throws) leaves no
    ///         route:
    ///         one accepted meanwhile is closed at once, and the provider's later accept refused.
    ///     </para>
    /// </summary>
    public ValueTask<Tap?> SubscribeAsync(Outlet outlet, string service, string? parameters = null,
        Lane? lane = null, CancellationToken cancellationToken = default)
    {
        return SubscribeAsync(outlet, service, parameters,
            lane is null ? SubscribeOptions.None : new SubscribeOptions { Lane = lane }, cancellationToken);
    }

    /// <summary>
    ///     Step 1 of a subscribe, as <see cref="SubscribeAsync(Outlet, string, string?, Lane?, CancellationToken)" />, with
    ///     what the subscriber asks beyond its parameters (<see cref="SubscribeOptions" />): its lane, its own rate (set on
    ///     the route as it binds). The provider sees its limits — that rate and its connection's cap — on
    ///     <see cref="SubscribeRequest.Limits" />.
    /// </summary>
    public async ValueTask<Tap?> SubscribeAsync(Outlet outlet, string service, string? parameters,
        SubscribeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinInterval, TimeSpan.Zero);
        Service entry;
        ProviderEndpoint? endpoint = null;
        ProviderLink? link = null;
        lock (_directory)
        {
            if (!_services.TryGetValue(service, out entry!))
                throw new ArgumentException($"No provider for '{service}'.", nameof(service));
            (endpoint, link) = Choose(entry);
        }

        if (link is null) throw Unavailable(entry, outlet);
        List<ProviderEndpoint>? tried = null; // instances that said another serves it (RefusalKind.Elsewhere)
        var reserved = false;
        Tap? bound = null;
        try
        {
            while (true)
            {
                // What the provider declares of the service: its realm, whether it requires a sign-in. The identity it's
                // shown is the subscriber's in that realm, unlapsed, or none; a required service shown none is refused
                // here, unasked.
                ServiceAccess access;
                try
                {
                    var asked = link.Provider.AccessAsync(service, cancellationToken);
                    access = asked.IsCompletedSuccessfully
                        ? asked.Result
                        : await asked.AsTask().WaitAsync(SubscribeTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw Unavailable(entry, outlet);
                }

                Volatile.Write(ref entry.Access, access.ToString());
                var shown = access.Shown(outlet.Identity, DateTimeOffset.UtcNow);
                if (access.Required && shown is null)
                {
                    var refusal = new SubscriptionRefusal(Guid.CreateVersion7(), service, parameters,
                        outlet.Identity is null ? "Sign in to subscribe." : "Sign in to this service's realm to subscribe.",
                        endpoint!, DateTimeOffset.UtcNow, RefusalKind.Unauthenticated);
                    outlet.Refused(refusal); // carried to the client: sign in, then subscribe again
                    SubscriptionRefused?.Invoke(refusal);
                    throw new SubscriptionRefusedException(refusal);
                }

                if (!reserved)
                {
                    if (!outlet.TryReserveTap())
                        throw outlet.Full(outlet.TapCharge); // over its budget: refused here, the provider never asked
                    reserved = true;
                }

                var request = new SubscribeRequest(this, outlet, endpoint!, service, parameters, options, access, shown);
                var (tap, elsewhere) = await SubscribeAsync(request, entry, endpoint!, link, outlet, cancellationToken)
                    .ConfigureAwait(false);
                if (elsewhere is null) return bound = tap;

                // Another of the service's instances serves it, this one says: the next one not asked, if any.
                (tried ??= []).Add(endpoint!);
                lock (_directory)
                {
                    (endpoint, link) = Choose(entry, tried);
                }

                if (link is null) // every one asked said so: the last one's refusal, told
                {
                    outlet.Refused(elsewhere);
                    SubscriptionRefused?.Invoke(elsewhere);
                    throw new SubscriptionRefusedException(elsewhere);
                }
            }
        }
        finally
        {
            if (reserved && bound is null)
                outlet.UnreserveTap(); // no route bound: the reservation goes (a route's goes when it closes)
        }
    }

    /// <summary>
    ///     Step 1 at one instance: its route, or null — or, if it refused as served elsewhere (<see cref="RefusalKind.Elsewhere" />),
    ///     that refusal, untold: the caller asks another instance, or tells it.
    /// </summary>
    private async ValueTask<(Tap? Tap, SubscriptionRefusal? Elsewhere)> SubscribeAsync(SubscribeRequest request,
        Service entry, ProviderEndpoint endpoint, ProviderLink link, Outlet outlet, CancellationToken cancellationToken)
    {
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var answer = link.Provider.SubscribeAsync(request, abandon.Token);
            if (answer.IsCompletedSuccessfully) answer.GetAwaiter().GetResult();
            else
                await answer.AsTask().WaitAsync(SubscribeTimeout, cancellationToken)
                    .ConfigureAwait(false); // even if it ignores the token
        }
        catch (TimeoutException)
        {
            abandon.Cancel();
            request.Answered(false);
            throw Unavailable(entry, outlet);
        }
        catch
        {
            request.Answered(false);
            throw;
        }

        request.Answered(true);
        if (request.Nacked)
        {
            Refused(endpoint, link, null);
            throw Unavailable(entry, outlet);
        }

        if (request.Refusal is { } refusal)
        {
            if (refusal.Kind == RefusalKind.Elsewhere) return (null, refusal);
            outlet.Refused(refusal); // carried to the client, for its log
            SubscriptionRefused?.Invoke(refusal); // the router's
            throw new SubscriptionRefusedException(refusal);
        }

        if (request.Tap is { IsClosed: false })
            lock (_directory)
            {
                entry.Waiting.Remove(outlet); // subscribed: not waiting any more
            }

        return (request.Tap, null);
    }

    /// <summary>The outlet waits for the service to be up (told then); the exception to throw.</summary>
    private ServiceUnavailableException Unavailable(Service entry, Outlet outlet)
    {
        lock (_directory)
        {
            entry.Waiting.Add(outlet);
        }

        return new ServiceUnavailableException(entry.Name);
    }

    private void InstancesChanged()
    {
        Volatile.Write(ref _holderList, [.. _instances.Values]);
        // under the directory lock
    }

    /// <summary>
    ///     The router as the viewer shows it: its identity, services, endpoints and the instances it knows, with its counters
    ///     —
    ///     read without locks (copies kept for the purpose; fields read as they are), so best effort.
    /// </summary>
    public RouterView Describe()
    {
        var services = new List<ServiceView>();
        foreach (var s in Volatile.Read(ref _serviceList))
        {
            var policy = s.Policy ?? ServiceUp;
            services.Add(new ServiceView(s.Name, [.. Volatile.Read(ref s.Endpoints).Select(e => e.ToString())],
                s.Waiting.Count,
                policy.Quorum, policy.Grace.TotalMilliseconds, policy.Spread.TotalMilliseconds, Volatile.Read(ref s.Access)));
        }

        var endpoints = new List<EndpointView>();
        foreach (var link in Endpoints.Values)
            endpoints.Add(new EndpointView(link.Endpoint.ToString(), link.Status.ToString(), link.Refused, link.Last,
                link.HeardAt == 0 ? null : Clock.At(link.HeardAt), double.IsNaN(link.Load) ? null : link.Load));
        var instances = new List<InstanceView>();
        foreach (var h in Volatile.Read(ref _holderList))
            instances.Add(new InstanceView(h.Identity, h.NodeId, h.Routes.Count, h.Draining, h.Dead,
                Clock.At(h.HeardAt), h.Degraded, Volatile.Read(ref h.DegradedPaths)));
        return new RouterView(Router, services, endpoints, instances, InstancesDown, RoutesDrained, ClientsMoved,
            DescribeRealms(), RoutesMoved, RoutesLetGo);
    }

    /// <summary>
    ///     A provider instance in detail — tracked now, or among the last deemed dead — by its id: what it is, the services at
    ///     its endpoint, the endpoint's last report. Null: unknown. Read without locks.
    /// </summary>
    public InstanceDetail? DescribeInstance(Guid id)
    {
        Holder? h = null;
        foreach (var x in Volatile.Read(ref _holderList))
            if (x.Identity.Id == id)
            {
                h = x;
                break;
            }

        if (h is null)
            foreach (var x in Volatile.Read(ref _recentDead))
                if (x.Identity.Id == id)
                    h = x; // the latest such
        if (h is null) return null;
        var view = new InstanceView(h.Identity, h.NodeId, h.Dead ? 0 : h.Routes.Count, h.Draining, h.Dead,
            Clock.At(h.HeardAt), h.Degraded, Volatile.Read(ref h.DegradedPaths));
        return new InstanceDetail(view, ServicesAt(h.Identity.Endpoint),
            DescribeEndpoint(h.Identity.Endpoint.ToString()),
            h.DiedAt == 0 ? null : Clock.At(h.DiedAt));
    }

    /// <summary>The instances deemed dead lately (the last 32), newest first.</summary>
    public IReadOnlyList<InstanceView> RecentlyDead()
    {
        var dead = Volatile.Read(ref _recentDead);
        var views = new List<InstanceView>(dead.Length);
        for (var i = dead.Length - 1; i >= 0; i--)
            views.Add(new InstanceView(dead[i].Identity, dead[i].NodeId, 0, dead[i].Draining, true,
                Clock.At(dead[i].HeardAt)));
        return views;
    }

    /// <summary>A service by name (null: none such). Read without locks.</summary>
    public ServiceView? DescribeService(string name)
    {
        foreach (var s in Volatile.Read(ref _serviceList))
        {
            if (s.Name != name) continue;
            var policy = s.Policy ?? ServiceUp;
            return new ServiceView(s.Name, [.. Volatile.Read(ref s.Endpoints).Select(e => e.ToString())],
                s.Waiting.Count,
                policy.Quorum, policy.Grace.TotalMilliseconds, policy.Spread.TotalMilliseconds, Volatile.Read(ref s.Access));
        }

        return null;
    }

    /// <summary>An endpoint by its address (null: not registered). Read without locks.</summary>
    public EndpointView? DescribeEndpoint(string address)
    {
        foreach (var link in Endpoints.Values)
            if (link.Endpoint.ToString() == address)
                return new EndpointView(link.Endpoint.ToString(), link.Status.ToString(), link.Refused, link.Last,
                    link.HeardAt == 0 ? null : Clock.At(link.HeardAt), double.IsNaN(link.Load) ? null : link.Load);
        return null;
    }

    /// <summary>The services registered at an endpoint.</summary>
    private IReadOnlyList<string> ServicesAt(ProviderEndpoint endpoint)
    {
        return
        [
            .. Volatile.Read(ref _serviceList).Where(s => Array.IndexOf(Volatile.Read(ref s.Endpoints), endpoint) >= 0)
                .Select(s => s.Name)
        ];
    }

    /// <summary>
    ///     A health check of the endpoint at <paramref name="address" />, now — for a viewer asking explicitly: it changes
    ///     nothing the router knows (its own checks do that). Null: no such endpoint, or no answer within
    ///     <paramref name="timeout" />.
    /// </summary>
    public async Task<HealthReport?> CheckHealthNowAsync(string address, TimeSpan timeout)
    {
        foreach (var link in Endpoints.Values)
        {
            if (link.Endpoint.ToString() != address) continue;
            try
            {
                return await link.Provider.CheckHealthAsync(new HealthProbe(Router, link.Last), CancellationToken.None)
                    .AsTask().WaitAsync(timeout).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The endpoints of a service, as registered.</summary>
    public IReadOnlyList<ProviderEndpoint> EndpointsOf(string service)
    {
        lock (_directory)
        {
            return _services.TryGetValue(service, out var entry) ? entry.Endpoints : [];
        }
    }

    /// <summary>
    ///     Checks the health of the instances holding routes (deeming dead those silent past <see cref="ProviderTimeout" />),
    ///     probes the endpoints of services clients wait on and those not serving, and tells waiting clients a service is up
    ///     once its <see cref="ServiceUpPolicy" /> is met — each at its own moment within the policy's spread. Call it
    ///     regularly — Tapline.AspNetCore does, with the outlets' heartbeats. Allocation-free while instances answer at once
    ///     and
    ///     no client waits.
    /// </summary>
    public void HeartbeatProviders()
    {
        do
        {
            if (Interlocked.Exchange(ref _heartbeating, 1) != 0) return; // (one at a time)
            Volatile.Write(ref _again, 0);
            Sweep();
        } while (Volatile.Read(ref _again) != 0); // (one asked for while it ran: a waited-on service found up, say)
    }

    /// <summary>
    ///     The endpoint a step 1 for <paramref name="entry" /> goes to: of its eligible endpoints, the least loaded of
    ///     <see cref="ProviderChoices" /> picked at random (ties: the first picked, itself random); while none has reported a
    ///     load, each in turn — none of <paramref name="tried" /> (those that said another serves it). None eligible: none.
    ///     Under the directory lock.
    /// </summary>
    private (ProviderEndpoint?, ProviderLink?) Choose(Service entry, List<ProviderEndpoint>? tried = null)
    {
        var endpoints = entry.Endpoints;
        Span<int> eligible = endpoints.Length <= 32 ? stackalloc int[endpoints.Length] : new int[endpoints.Length];
        var count = 0;
        var loaded = false;
        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < endpoints.Length; i++)
            if (_endpoints.TryGetValue(endpoints[i], out var l) && l.Eligible && tried?.Contains(endpoints[i]) != true)
            {
                eligible[count++] = i;
                loaded |= !double.IsNaN(l.Load);
            }

        if (count == 0) return (null, null);
        if (!loaded) // none said how loaded it is: each in turn
            for (var i = 0; i < endpoints.Length; i++)
            {
                var e = endpoints[entry.Next++ % (uint)endpoints.Length];
                if (_endpoints.TryGetValue(e, out var l) && l.Eligible && tried?.Contains(e) != true) return (e, l);
            }

        ProviderEndpoint? best = null;
        ProviderLink? bestLink = null;
        var lowest = double.PositiveInfinity;
        var picks = Math.Clamp(ProviderChoices, 1, count);
        for (var k = 0; k < picks; k++)
        {
            var j = k + Random.Shared.Next(count - k); // a partial shuffle: distinct picks
            (eligible[k], eligible[j]) = (eligible[j], eligible[k]);
            var e = endpoints[eligible[k]];
            var l = _endpoints[e];
            var load = double.IsNaN(l.Load) || Stopwatch.GetElapsedTime(l.LoadAt, now) > ProviderTimeout ? 0 : l.Load;
            if (load < lowest) (best, bestLink, lowest) = (e, l, load);
        }

        return (best, bestLink);
    }

    /// <summary>
    ///     How long this router was paused before this sweep, in Stopwatch ticks (0: it wasn't) — longer since the last one
    ///     than half <see cref="ProviderTimeout" />: its process stopped (a debugger, a machine too loaded to run it), not its
    ///     providers. Their silence meanwhile is its own: not counted against them (<see cref="Resumed" />).
    /// </summary>
    private long Paused(long now)
    {
        var last = Interlocked.Exchange(ref _sweptAt, now);
        if (last == 0) return 0;
        var gap = now - last;
        return gap > ProviderTimeout.TotalSeconds / 2 * Stopwatch.Frequency ? gap : 0;
    }

    /// <summary>
    ///     This router paused for <paramref name="paused" /> ticks: every instance and endpoint heard from as if that much
    ///     later (none ahead of now) — the time it didn't run didn't pass for them. Under the directory lock.
    /// </summary>
    private void Resumed(long paused, long now)
    {
        foreach (var h in _instances.Values)
        {
            h.HeardAt = Math.Min(now, h.HeardAt + paused);
            if (h.AskedAt != 0) h.AskedAt = Math.Min(now, h.AskedAt + paused);
        }

        foreach (var link in _endpoints.Values)
            if (link.HeardAt != 0)
                link.HeardAt = Math.Min(now, link.HeardAt + paused);
    }

    /// <summary>A sweep at once, or, one running, another once it's done: a waited-on service found up, its clients told now.</summary>
    private void SweepAgain()
    {
        Volatile.Write(ref _again, 1);
        HeartbeatProviders();
    }

    private void Sweep()
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            var paused = Paused(now);
            lock (_directory)
            {
                if (paused > 0) Resumed(paused, now);
                foreach (var h in _instances.Values)
                {
                    if (h.Routes.Count == 0) continue; // no listener: nothing to keep alive
                    var quiet = Stopwatch.GetElapsedTime(h.HeardAt, now);
                    if (quiet > ProviderTimeout)
                    {
                        _silent.Add(h);
                        continue;
                    }

                    if (!h.Degraded && h.AskedAt != 0 &&
                        Stopwatch.GetElapsedTime(h.AskedAt, now) >
                        ProviderDegradedAfter) // late to answer: degraded, not gone
                    {
                        (h.Degraded, h.DegradedWith) = (true, Guid.CreateVersion7());
                        h.Flaps.Degraded(now);
                        _late.Add(h);
                    }

                    if (!h.InFlight && Stopwatch.GetElapsedTime(h.SentAt, now) >= ProviderHeartbeatInterval)
                    {
                        h.InFlight = true;
                        h.SentAt = now;
                        if (h.AskedAt == 0) h.AskedAt = now; // (asked: late, unanswered past ProviderDegradedAfter)
                        _due.Add(h);
                    }
                }

                foreach (var link in _endpoints.Values)
                    if (!link.Eligible)
                        Probe(link.Endpoint, link,
                            now); // draining, not serving, refusing: to see it back (a new instance)
                foreach (var service in _services.Values)
                {
                    if (service.Waiting.Count > 0) service.Waiting.RemoveWhere(Gone); // clients gone while waiting
                    if (service.Waiting.Count == 0)
                    {
                        service.UpSince = 0;
                        continue;
                    }

                    var up = 0;
                    foreach (var e in service.Endpoints)
                    {
                        if (!_endpoints.TryGetValue(e, out var link)) continue;
                        if (link.Serving(now, ProviderTimeout)) up++;
                        if (link.HeardAt == 0 ||
                            Stopwatch.GetElapsedTime(link.HeardAt, now) >= ProviderHeartbeatInterval)
                            Probe(e, link, now);
                    }

                    if (up == 0)
                    {
                        service.UpSince = 0;
                        continue;
                    }

                    if (service.UpSince == 0) service.UpSince = now;
                    var policy = service.Policy ?? ServiceUp;
                    if (up < policy.Quorum && Stopwatch.GetElapsedTime(service.UpSince, now) < policy.Grace) continue;
                    _ups.Add((service.Name, [.. service.Waiting], policy.Spread, up));
                    service.Waiting.Clear();
                    service.UpSince = 0;
                }
            }

            foreach (var h in _silent)
                Kill(h, $"silent: no answer for over {ProviderTimeout.TotalSeconds:0.#} s", revivable: true);
            foreach (var h in _late)
                Degrade(h, null, h.DegradedWith,
                    $"late: a health check unanswered for over {ProviderDegradedAfter.TotalSeconds:0.#} s");
            foreach (var h in _due) _ = CheckAsync(h.Identity.Endpoint, h);
            foreach (var (e, link) in _probes) _ = CheckAsync(e, link);
            foreach (var (service, waiting, spread, up) in _ups)
            {
                var correlation = Guid.CreateVersion7();
                Changed(ServiceHealthEvent.Up, correlation, null, service, service, $"{up} of its endpoints serving",
                    waiting.Length);
                _ = TellUpAsync(service, waiting, spread, correlation);
            }
        }
        finally
        {
            _silent.Clear();
            _late.Clear();
            _due.Clear();
            _probes.Clear();
            _ups.Clear();
            Volatile.Write(ref _heartbeating, 0);
        }
    }

    /// <summary>Queues a probe of an endpoint (at most one in flight, one per interval). Under the directory lock.</summary>
    private void Probe(ProviderEndpoint endpoint, ProviderLink link, long now)
    {
        if (link.Probing || Stopwatch.GetElapsedTime(link.ProbedAt, now) < ProviderHeartbeatInterval) return;
        link.Probing = true;
        link.ProbedAt = now;
        _probes.Add((endpoint, link));
    }

    /// <summary>Tells each waiting client the service is up, at a random moment within <paramref name="spread" /> (jitter).</summary>
    private static async Task TellUpAsync(string service, Outlet[] waiting, TimeSpan spread, Guid correlation)
    {
        if (spread <= TimeSpan.Zero)
        {
            foreach (var o in waiting) o.ServiceUp(service, correlation);
            return;
        }

        var at = new long[waiting.Length];
        for (var i = 0; i < at.Length; i++) at[i] = Random.Shared.NextInt64(spread.Ticks);
        Array.Sort(at, waiting);
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < waiting.Length; i++)
        {
            var wait = TimeSpan.FromTicks(at[i]) - Stopwatch.GetElapsedTime(start);
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            if (!waiting[i].IsDisposed) waiting[i].ServiceUp(service, correlation);
        }
    }

    /// <summary>A health check of the instance holding routes (its own probe, made once).</summary>
    private async Task CheckAsync(ProviderEndpoint endpoint, Holder h)
    {
        HealthReport? report = null;
        if (Endpoints.TryGetValue(endpoint, out var link))
            try
            {
                report = await link.Provider.CheckHealthAsync(h.Probe, h.Gone.Token)
                    .ConfigureAwait(false); // one at a time: silence ends in the timeout
            }
            catch
            {
            } // no answer: silence, which the timeout counts

        lock (_directory)
        {
            h.InFlight = false;
        }

        if (report is { } answered && link is not null) Apply(endpoint, link, answered, h);
    }

    /// <summary>A probe of an endpoint: whoever answers there, and how.</summary>
    private async Task CheckAsync(ProviderEndpoint endpoint, ProviderLink link)
    {
        HealthReport? report = null;
        try
        {
            var answer = link.Provider.CheckHealthAsync(new HealthProbe(Router, link.Last), CancellationToken.None);
            report = answer.IsCompletedSuccessfully
                ? answer.Result
                : await answer.AsTask().WaitAsync(ProviderTimeout).ConfigureAwait(false);
        }
        catch
        {
        } // no answer: not up

        lock (_directory)
        {
            link.Probing = false;
        }

        if (report is not { } answered) return;
        Apply(endpoint, link, answered, null);
        if (link.Eligible && Waited(endpoint)) SweepAgain(); // (up now: its waiting clients told at once)
    }

    /// <summary>
    ///     A health report from <paramref name="endpoint" />: refusing this router — its routes there closed, the endpoint
    ///     skipped; or an instance answering — heard of now, known or new (a new id at a known name and endpoint, or another
    ///     than <paramref name="expected" />: its predecessor restarted, and is put down), its readiness recorded, a drain
    ///     begun
    ///     if it reports one this router hadn't heard of. An id deemed dead, or a report for another endpoint, counts for
    ///     nothing.
    /// </summary>
    private void Apply(ProviderEndpoint endpoint, ProviderLink link, HealthReport report, Holder? expected)
    {
        var answer = report.Instance;
        Holder? superseded = null, draining = null, degraded = null, recovered = null, answering = null;
        List<(string Path, Guid Correlation)>? pathsDegraded = null, pathsRecovered = null;
        bool replaced = false, revived = false;
        lock (_directory)
        {
            if (!_endpoints.TryGetValue(endpoint, out var current) || current != link ||
                answer.Endpoint != endpoint) return;
            if (report.RouterRefused)
            {
                if (link.Refused) return;
                link.Refused = true; // (its routes, below, outside the lock)
            }
            else
            {
                link.Refused = false;
                if (_dead.Contains(answer.Id))
                {
                    // Deemed dead by silence, or for refusing this router, yet answering — serving, not refusing: the
                    // verdict was wrong (a pause, a partition healed), and the instance is taken back as a new node; what
                    // the verdict closed stays closed (its clients told down, now told up). Deemed dead otherwise
                    // (restarted, replaced, withdrawn), or not serving: no step 1s till another answers.
                    if (report.Status != ProviderStatus.Serving || !_revivable.Remove(answer.Id))
                    {
                        link.Last = answer;
                        link.Status = ProviderStatus.NotServing;
                        return;
                    }

                    _dead.Remove(answer.Id);
                    revived = true;
                }

                var now = Stopwatch.GetTimestamp();
                link.Last = answer;
                link.HeardAt = now;
                link.Status = report.Status;
                (link.Load, link.LoadAt) = (report.Load ?? double.NaN, now);
                var key = (answer.Name, endpoint);
                if (_instances.TryGetValue(key, out var h) && h.Identity.Id == answer.Id)
                {
                    (h.HeardAt, h.AskedAt) = (now, 0); // (answered: not late)
                }
                else
                {
                    superseded = h;
                    _instances[key] = h = new Holder(answer, Router);
                    InstancesChanged();
                }

                if (h.Draining)
                    link.Status = ProviderStatus.Draining; // one way: an instance draining stays so, whatever it says
                if (report.Status == ProviderStatus.NotServing) // unhealthy, by its own word
                {
                    if (!h.Degraded)
                    {
                        (h.Degraded, h.DegradedWith) = (true, Guid.CreateVersion7());
                        h.Flaps.Degraded(now);
                        degraded = h;
                    }
                    else
                    {
                        h.Flaps.Unhealthy();
                    }
                }
                else if (h.Degraded &&
                         h.Flaps.Recovers(now, ProviderFlapWindow, ProviderRecoverHold, ProviderMaxRecoverHold))
                {
                    (h.Degraded, recovered) =
                        (false, h); // answering healthy (long enough, if it has been flapping): recovered
                }

                PathChanges(h, report.DegradedPaths, now, ref pathsDegraded, ref pathsRecovered);
                answering = h;
                replaced = expected is not null && expected != h; // another id (or another instance) where it was
                if (report.Status == ProviderStatus.Draining && !h.Draining) draining = h;
            }
        }

        if (report.RouterRefused)
        {
            Refused(endpoint, link, answer);
            return;
        }

        if (revived)
            Changed(ServiceHealthEvent.Recovered, Guid.CreateVersion7(), answering, null, null,
                "deemed dead, yet answering healthy: taken back as a new node", 0);
        if (superseded is not null) Kill(superseded, "restarted: another instance answered at its endpoint");
        if (replaced) Kill(expected!, "replaced: another instance answered its health check");
        if (degraded is not null) Degrade(degraded, null, degraded.DegradedWith, "reported not serving");
        if (recovered is not null && !replaced) Recover(recovered, null, Guid.CreateVersion7(), "answering healthy");
        if (pathsDegraded is not null)
            foreach (var (path, correlation) in pathsDegraded)
                Degrade(answering!, path, correlation, $"reported {path} degraded");
        if (pathsRecovered is not null)
            foreach (var (path, correlation) in pathsRecovered)
                Recover(answering!, path, correlation, $"{path} no longer reported degraded");
        if (draining is not null)
            StartDrain(draining, report.DrainUntil is { } until ? until - DateTimeOffset.UtcNow : TimeSpan.Zero,
                DrainGrace, "reported by its provider");
    }

    /// <summary>
    ///     The service paths <paramref name="h" /> reports degraded now, against those it did: each newly degraded (its own
    ///     correlation id), and each no longer reported that recovers (damped against flapping: <see cref="Flaps" />). Paths
    ///     recovered and quiet past the flap window are forgotten. Under the directory lock.
    /// </summary>
    private void PathChanges(Holder h, IReadOnlyList<string>? reported, long now, ref List<(string, Guid)>? degraded,
        ref List<(string, Guid)>? recovered)
    {
        var changed = false;
        if (reported is { Count: > 0 })
            foreach (var path in reported)
            {
                if (string.IsNullOrEmpty(path)) continue;
                h.Paths ??= new Dictionary<string, PathHealth>(StringComparer.Ordinal);
                if (!h.Paths.TryGetValue(path, out var p)) h.Paths[path] = p = new PathHealth();
                if (p.Degraded)
                {
                    p.Flaps.Unhealthy();
                    continue;
                }

                (p.Degraded, p.Correlation, changed) = (true, Guid.CreateVersion7(), true);
                p.Flaps.Degraded(now);
                (degraded ??= []).Add((path, p.Correlation));
            }

        if (h.Paths is null) return;
        List<string>? forget = null;
        foreach (var (path, p) in h.Paths)
        {
            if (!p.Degraded)
            {
                if (p.Flaps.Recent(now, ProviderFlapWindow) == 0) (forget ??= []).Add(path);
                continue;
            }

            if (reported is not null && reported.Contains(path)) continue;
            if (!p.Flaps.Recovers(now, ProviderFlapWindow, ProviderRecoverHold, ProviderMaxRecoverHold)) continue;
            (p.Degraded, p.Correlation, changed) = (false, Guid.CreateVersion7(), true);
            (recovered ??= []).Add((path, p.Correlation));
        }

        if (forget is not null)
            foreach (var path in forget)
                h.Paths.Remove(path);
        if (changed)
            Volatile.Write(ref h.DegradedPaths,
                [.. h.Paths.Where(kv => kv.Value.Degraded).Select(kv => kv.Key).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    ///     Whether a route of <paramref name="h" />'s is degraded by anything: the node, or a service path covering it.
    ///     Under the directory lock.
    /// </summary>
    private static bool Degraded(Holder h, SubscribeRequest route)
    {
        if (h.Degraded) return true;
        if (h.Paths is not null)
            foreach (var (path, p) in h.Paths)
                if (p.Degraded && Covers(path, route.ServicePath))
                    return true;
        return false;
    }

    /// <summary>
    ///     An instance holding routes is degraded (late, or unhealthy by its own word) — or, given a <paramref name="path" />,
    ///     the routes it holds at or under that service path are: each client with routes it covers is told (the change's
    ///     <paramref name="correlation" /> id, the scope as it sees it) and shown each route's stale data; the routes stay,
    ///     held.
    /// </summary>
    private void Degrade(Holder h, string? path, Guid correlation, string reason)
    {
        SubscribeRequest[] routes;
        lock (_directory)
        {
            if (h.Dead) return;
            routes = path is null ? [.. h.Routes] : [.. h.Routes.Where(r => Covers(path, r.ServicePath))];
        }

        var scope = path is null ? null : ScopeOf(path);
        var clients = 0;
        foreach (var client in routes.Where(r => r.Tap is not null).GroupBy(r => r.Outlet))
        {
            clients++;
            client.Key.ProviderDegraded(h.NodeId, scope, correlation, [.. client.Select(r => r.Tap!)]);
        }

        Changed(ServiceHealthEvent.Degraded, correlation, h, path, scope, reason, clients);
    }

    /// <summary>
    ///     It answers healthy again — the node, or a service path on it: each client with routes it covers told, the streams
    ///     of those degraded by nothing else going on.
    /// </summary>
    private void Recover(Holder h, string? path, Guid correlation, string reason)
    {
        var clients = new Dictionary<Outlet, List<Tap>>();
        lock (_directory)
        {
            if (h.Dead) return;
            foreach (var r in h.Routes)
            {
                if (r.Tap is not { } tap || (path is not null && !Covers(path, r.ServicePath))) continue;
                if (!clients.TryGetValue(r.Outlet, out var free)) clients[r.Outlet] = free = [];
                if (!Degraded(h, r))
                    free.Add(tap); // (the scope recovering is clear already: degraded by something else, it stays held)
            }
        }

        var scope = path is null ? null : ScopeOf(path);
        foreach (var (client, free) in clients) client.ProviderRecovered(h.NodeId, scope, correlation, free);
        Changed(ServiceHealthEvent.Recovered, correlation, h, path, scope, reason, clients.Count);
    }

    /// <summary>
    ///     A route just held: if its node, or a service path covering it, is degraded, its client is told (the change's id) —
    ///     the route itself streams on: it has nothing of its own to show stale yet.
    /// </summary>
    internal void Joined(Holder h, SubscribeRequest route)
    {
        List<(string? Path, Guid Correlation)>? degraded = null;
        lock (_directory)
        {
            if (h.Dead) return;
            if (h.Degraded) (degraded ??= []).Add((null, h.DegradedWith));
            if (h.Paths is not null)
                foreach (var (path, p) in h.Paths)
                    if (p.Degraded && Covers(path, route.ServicePath))
                        (degraded ??= []).Add((path, p.Correlation));
        }

        if (degraded is not null)
            foreach (var (path, correlation) in degraded)
                route.Outlet.ProviderDegraded(h.NodeId, path is null ? null : ScopeOf(path), correlation, []);
    }

    /// <summary>
    ///     The provider at <paramref name="endpoint" /> refuses this router: what it held for it is gone — closed here
    ///     too, and told.
    /// </summary>
    private void Refused(ProviderEndpoint endpoint, ProviderLink link, ProviderInstance? instance)
    {
        List<Holder> there = [];
        lock (_directory)
        {
            link.Refused = true;
            foreach (var h in _instances.Values)
                if (h.Identity.Endpoint == endpoint)
                    there.Add(h);
        }

        foreach (var h in there) Kill(h, "refuses this router", revivable: true);
        RefusedBy?.Invoke(endpoint, instance ?? there.FirstOrDefault()?.Identity);
    }

    /// <summary>
    ///     The instance answering step 1, as tracked: known, new, or a new id at a known name and endpoint — a restart, which
    ///     puts its predecessor down. Refuses an endpoint the router can't reach, an instance deemed dead, and one draining.
    /// </summary>
    internal Holder Resolve(ProviderInstance instance)
    {
        Holder? superseded = null, holder;
        lock (_directory)
        {
            if (!_endpoints.TryGetValue(instance.Endpoint, out var link))
                throw new ArgumentException(
                    $"The router can't reach {instance.Endpoint}: provide a service there first.", nameof(instance));
            if (_dead.Contains(instance.Id))
                throw new InvalidOperationException($"{instance} has been deemed dead: it can't accept subscriptions.");
            var now = Stopwatch.GetTimestamp();
            link.Last = instance;
            link.HeardAt = now;
            var key = (instance.Name, instance.Endpoint);
            if (_instances.TryGetValue(key, out holder) && holder.Identity.Id == instance.Id)
            {
                if (holder.Draining)
                    throw new InvalidOperationException($"{instance} is draining: it takes no new subscriptions.");
                holder.HeardAt = now; // an answer: alive
                return holder;
            }

            superseded = holder;
            _instances[key] = holder = new Holder(instance, Router);
            InstancesChanged();
            link.Status = ProviderStatus.Serving; // a new instance, accepting
            link.Refused = false;
        }

        if (superseded is not null)
            Kill(superseded, "restarted: another instance accepted a subscription at its endpoint");
        return holder;
    }

    /// <summary>
    ///     A route accepted by <paramref name="h" />: held by it (false if it has died meanwhile). Its first route starts
    ///     its health checks.
    /// </summary>
    internal bool Hold(Holder h, SubscribeRequest route)
    {
        lock (_directory)
        {
            if (h.Dead) return false;
            if (h.Routes.Count == 0)
                (h.HeardAt, h.SentAt, h.AskedAt) =
                    (Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp(), 0); // silent from now, if it never answers
            h.Routes.Add(route);
            return true;
        }
    }

    /// <summary>A route of <paramref name="h" />'s ended.</summary>
    internal void Release(Holder h, SubscribeRequest route)
    {
        lock (_directory)
        {
            h.Routes.Remove(route);
        }
    }

    /// <summary>
    ///     Deems an instance dead: forgotten (its id remembered as dead), its routes closed and their clients told the service
    ///     is down (one correlation id for all, logged with <paramref name="reason" />) — and waiting, from then, for it to be
    ///     up. Nothing is relayed to its endpoint for them: a successor there never held them.
    /// </summary>
    private void Kill(Holder h, string reason, bool revivable = false)
    {
        SubscribeRequest[] routes;
        lock (_directory)
        {
            if (h.Dead) return;
            h.Dead = true;
            h.DownWith = Guid.CreateVersion7();
            h.DiedAt = Stopwatch.GetTimestamp();
            Volatile.Write(ref _recentDead,
                _recentDead.Length < RecentDead
                    ? [.. _recentDead, h]
                    : [.. _recentDead.AsSpan(1), h]); // the viewer's: the last few
            var key = (h.Identity.Name, h.Identity.Endpoint);
            if (_instances.TryGetValue(key, out var current) && current == h)
            {
                _instances.Remove(key);
                InstancesChanged();
            }

            if (_dead.Add(h.Identity.Id))
            {
                _deadOrder.Enqueue(h.Identity.Id);
                if (_deadOrder.Count > DeadRemembered)
                {
                    var forgotten = _deadOrder.Dequeue();
                    _dead.Remove(forgotten);
                    _revivable.Remove(forgotten);
                }
            }

            if (revivable && !h.Draining) _revivable.Add(h.Identity.Id); // (one draining: one way, whatever it says)

            routes = [.. h.Routes];
            h.Routes.Clear();
        }

        Interlocked.Increment(ref _instancesDown);
        h.Gone.Cancel();
        Down(routes, h.DownWith);
        Changed(ServiceHealthEvent.Down, h.DownWith, h, null, null, reason,
            routes.Select(r => r.Outlet).Distinct().Count());
    }

    /// <summary>
    ///     Routes closed — their clients told the service is down, and waiting for it to be up; or,
    ///     <paramref name="drained" />,
    ///     let go by a drain — their clients told to subscribe again now (not waiting: if no instance takes them then, their
    ///     step 1 finds the service unavailable, and they wait from there). Told with the change's
    ///     <paramref name="correlation" /> id.
    /// </summary>
    private void Down(SubscribeRequest[] routes, Guid correlation, bool drained = false, bool tell = false)
    {
        foreach (var r in routes) r.Down(correlation, drained, tell);
        if (drained) return;
        lock (_directory)
        {
            foreach (var r in routes)
                if (!r.Outlet.IsDisposed)
                    ServiceNamed(r.Service).Waiting.Add(r.Outlet);
        }
    }

    /// <summary>
    ///     Drains a node on this router's side — as its provider's own drain does (<see cref="IRouterChannel.DrainAsync" />),
    ///     though the provider isn't asked (what it lets go is told it, as for an unsubscribe): no more step 1s to it from
    ///     this
    ///     router; its clients moved over <paramref name="over" />, one at a time, each keeping its routes for
    ///     <paramref name="grace" /> (make before break).
    ///     One way: its endpoint takes no step 1s from this router until another instance answers there — with no other
    ///     endpoint serving its service, the service is unavailable here till then. For moving a service's clients by hand
    ///     (the viewer's drain: to try a client's moves), or taking a node out of this router's rotation. Returns the drain's
    ///     correlation id — the one it has, if draining already — or null: no live node with that id here.
    /// </summary>
    public Guid? DrainNode(Guid nodeId, TimeSpan over, TimeSpan grace)
    {
        Holder? h = null;
        lock (_directory)
        {
            foreach (var x in _instances.Values)
                if (x.NodeId == nodeId && !x.Dead)
                {
                    h = x;
                    break;
                }
        }

        if (h is null) return null;
        StartDrain(h, over, grace, "on request, at this router (its provider not asked)", true);
        lock (_directory)
        {
            return h.Dead ? null : h.DrainWith;
        }
    }

    /// <summary>A provider's drain (<see cref="IRouterChannel.DrainAsync" />): of the instance it names, if alive here.</summary>
    internal void Drain(ProviderInstance instance, TimeSpan over, TimeSpan grace)
    {
        Holder? h;
        lock (_directory)
        {
            if (!_endpoints.TryGetValue(instance.Endpoint, out var link) || _dead.Contains(instance.Id)) return;
            var key = (instance.Name, instance.Endpoint);
            if (!_instances.TryGetValue(key, out h) || h.Identity.Id != instance.Id)
            {
                if (h is not null) return; // another instance there: not the one to drain
                _instances[key] =
                    h = new Holder(instance, Router); // unknown till now: nothing to let go, but no step 1s either
                InstancesChanged();
            }
        }

        StartDrain(h, over, grace, "asked by its provider");
    }

    /// <summary>
    ///     Drains an instance — one way: no step 1s to it from now on (its endpoint not serving), and its clients moved over
    ///     <paramref name="over" />, one at a time in a random order, evenly spaced, the last at the end. Each, at its turn,
    ///     is
    ///     told to move (draining) and keeps its routes for <paramref name="grace" /> — make-before-break: it subscribes again
    ///     (another instance), and lets the old go once the new streams — then whatever it still holds there is closed
    ///     (drained). No grace: closed at its turn (break-before-make).
    /// </summary>
    private void StartDrain(Holder h, TimeSpan over, TimeSpan grace, string by, bool tell = false)
    {
        Outlet[] clients;
        lock (_directory)
        {
            if (h.Dead || h.Draining) return;
            h.Draining = true;
            h.DrainWith = Guid.CreateVersion7();
            h.DrainTells = tell;
            if (_endpoints.TryGetValue(h.Identity.Endpoint, out var link) &&
                (link.Last is null || link.Last.Id == h.Identity.Id))
            {
                link.Last = h.Identity;
                link.Status = ProviderStatus.Draining; // no step 1s there, until another instance answers
            }

            clients = [.. h.Routes.Select(r => r.Outlet).Distinct()];
        }

        Random.Shared.Shuffle(clients);
        grace = grace < TimeSpan.Zero ? TimeSpan.Zero : grace > MaxDrainGrace ? MaxDrainGrace : grace;
        over = over < TimeSpan.Zero ? TimeSpan.Zero : over;
        Changed(ServiceHealthEvent.Draining, h.DrainWith, h, null, null,
            $"{by}: its clients moved over {over.TotalSeconds:0.#} s, one at a time", clients.Length, grace);
        _ = DrainAsync(h, clients, over, grace);
    }

    private async Task DrainAsync(Holder h, Outlet[] clients, TimeSpan over, TimeSpan grace)
    {
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < clients.Length; i++)
        {
            var wait = over * (i + 1) / clients.Length - Stopwatch.GetElapsedTime(start);
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            if (grace == TimeSpan.Zero)
            {
                if (!LetGo(h, clients[i])) return;
                continue;
            }

            bool holds;
            lock (_directory)
            {
                if (h.Dead) return; // died meanwhile: its routes went down with it
                holds = h.Routes.Any(r => r.Outlet == clients[i]);
            }

            if (!holds) continue; // gone already (unsubscribed, its client gone)
            Interlocked.Increment(ref _clientsMoved);
            clients[i].ServiceDraining(h.NodeId, h.DrainWith, grace); // move: subscribe again, then let these go
            _ = LetGoAfterAsync(h, clients[i], grace);
        }
    }

    private async Task LetGoAfterAsync(Holder h, Outlet client, TimeSpan grace)
    {
        await Task.Delay(grace).ConfigureAwait(false);
        LetGo(h, client);
    }

    /// <summary>
    ///     Closes what <paramref name="client" /> still holds on a draining instance (drained); false if the instance
    ///     died meanwhile.
    /// </summary>
    private bool LetGo(Holder h, Outlet client)
    {
        SubscribeRequest[] routes;
        lock (_directory)
        {
            if (h.Dead) return false; // its routes went down with it
            routes = [.. h.Routes.Where(r => r.Outlet == client)];
            foreach (var r in routes) h.Routes.Remove(r);
        }

        if (routes.Length == 0) return true; // moved in time: nothing to close
        Interlocked.Add(ref _routesDrained, routes.Length);
        Down(routes, h.DrainWith, true, h.DrainTells);
        Changed(ServiceHealthEvent.Drained, h.DrainWith, h, null, null,
            $"{routes.Length} of its subscriptions let go: not moved in time", 1);
        return true;
    }

    /// <summary>
    ///     Moves some of a node's subscriptions on this router's side, as its provider's own move does
    ///     (<see cref="IRouterChannel.MoveAsync" />): those in <paramref name="partition" /> (at or under it; null: all of
    ///     the node's), or the one <paramref name="route" />, over <paramref name="over" />, one at a time, each kept here
    ///     for <paramref name="grace" /> while its client subscribes again — placed by load. The node keeps serving. For
    ///     rebalancing by hand (the viewer's move), or as this router's own pressure decides. No live node with that id
    ///     here: refused.
    /// </summary>
    public MoveAnswer MoveNode(Guid nodeId, string? partition, RouteId? route, TimeSpan over, TimeSpan grace,
        string reason)
    {
        Holder? h = null;
        lock (_directory)
        {
            foreach (var x in _instances.Values)
                if (x.NodeId == nodeId && !x.Dead)
                {
                    h = x;
                    break;
                }
        }

        return h is null
            ? MoveAnswer.Refuse("No live node with that id here.")
            : StartMove(h, partition, route, over, grace, $"on request, at this router: {reason}");
    }

    /// <summary>A provider's move (<see cref="IRouterChannel.MoveAsync" />): of the instance it names, if alive here.</summary>
    internal MoveAnswer Move(ProviderInstance instance, string? partition, RouteId? route, TimeSpan over,
        TimeSpan grace, string reason)
    {
        Holder? h;
        lock (_directory)
        {
            if (!_instances.TryGetValue((instance.Name, instance.Endpoint), out h) || h.Identity.Id != instance.Id ||
                h.Dead)
                return MoveAnswer.Refuse($"{instance} holds nothing here.");
        }

        return StartMove(h, partition, route, over, grace, $"asked by its provider: {reason}");
    }

    /// <summary>
    ///     Starts moving the subscriptions of <paramref name="h" /> named — those made longer ago than
    ///     <see cref="MoveDwell" />, not being moved already, and of a service another instance serves here (somewhere to
    ///     go) — one at a time, in a random order, evenly spaced over <paramref name="over" />, the last at the end. Refused
    ///     if the node is draining (its clients move already), or none qualifies, saying why.
    /// </summary>
    private MoveAnswer StartMove(Holder h, string? partition, RouteId? route, TimeSpan over, TimeSpan grace,
        string by)
    {
        SubscribeRequest[] moving;
        Guid correlation;
        lock (_directory)
        {
            if (h.Dead) return MoveAnswer.Refuse("The node is gone.");
            if (h.Draining) return MoveAnswer.Refuse("The node is draining: its clients are moving already.");
            int named = 0, settling = 0, stuck = 0, level = 0;
            var now = Stopwatch.GetTimestamp();
            var chosen = new List<SubscribeRequest>();
            foreach (var r in h.Routes)
            {
                if (route is { } one ? r.Route != one : partition is not null && !Under(r.Partition, partition)) continue;
                named++;
                if (h.Moving.Contains(r) || r.Tap is not { } tap ||
                    Stopwatch.GetElapsedTime(tap.BoundAt) < MoveDwell)
                    settling++; // (moved, or made, lately: it stays put)
                else
                    switch (Elsewhere(r.Service, h.Identity.Endpoint, now))
                    {
                        case Better.Yes:
                            chosen.Add(r);
                            break;
                        case Better.None:
                            stuck++;
                            break;
                        default:
                            level++;
                            break;
                    }
            }

            if (chosen.Count == 0)
                return MoveAnswer.Refuse(named == 0
                    ? route is null ? $"No subscriptions{(partition is null ? "" : $" in {partition}")} on the node here."
                        : $"No route {route} on the node here."
                    : stuck > 0 && settling == 0 && level == 0
                        ? "No other instance serves its service here: nowhere to move to."
                        : level > 0 && settling == 0 && stuck == 0
                            ? $"No other instance serving its service here is less loaded by {MoveMargin:0.##} or more: nowhere better to go."
                            : $"Of {named} named, none to move: {settling} made or moved within the dwell time, {stuck} with nowhere to go, {level} with nowhere better.");
            foreach (var r in chosen) h.Moving.Add(r);
            moving = [.. chosen];
            correlation = Guid.CreateVersion7();
        }

        Random.Shared.Shuffle(moving);
        grace = grace < TimeSpan.Zero ? TimeSpan.Zero : grace > MaxDrainGrace ? MaxDrainGrace : grace;
        over = over < TimeSpan.Zero ? TimeSpan.Zero : over;
        Changed(ServiceHealthEvent.Moving, correlation, h, partition, null,
            $"{by}: {moving.Length} of its subscriptions moved over {over.TotalSeconds:0.#} s, one at a time",
            moving.Select(r => r.Outlet).Distinct().Count(), grace);
        _ = MoveAsync(h, moving, over, grace, correlation);
        return new MoveAnswer(moving.Length, correlation);
    }

    /// <summary>Whether partition path <paramref name="path" /> is <paramref name="partition" />, or under it.</summary>
    private static bool Under(string? path, string partition)
    {
        return path is not null && (path == partition ||
                                    (path.Length > partition.Length && path[partition.Length] == '/' &&
                                     path.StartsWith(partition, StringComparison.Ordinal)));
    }

    private enum Better : byte
    {
        None, // no other endpoint serves it
        Level, // others do, none less loaded by MoveMargin
        Yes
    }

    /// <summary>
    ///     Whether an endpoint other than <paramref name="from" /> serves <paramref name="service" /> here, and is less
    ///     loaded than it by <see cref="MoveMargin" /> — or, <paramref name="from" />'s load not known (not reported, or
    ///     stale), serves it at all. Loads as placement reads them: one not reported, or stale, counts as idle. Under the
    ///     directory lock.
    /// </summary>
    private Better Elsewhere(string service, ProviderEndpoint from, long now)
    {
        if (!_services.TryGetValue(service, out var entry)) return Better.None;
        var source = _endpoints.TryGetValue(from, out var f) ? Known(f, now) : double.NaN;
        var any = false;
        foreach (var e in entry.Endpoints)
        {
            if (e == from || !_endpoints.TryGetValue(e, out var l) || !l.Eligible) continue;
            any = true;
            if (double.IsNaN(source) || (double.IsNaN(Known(l, now)) ? 0 : Known(l, now)) <= source - MoveMargin)
                return Better.Yes;
        }

        return any ? Better.Level : Better.None;
    }

    /// <summary>An endpoint's load as last reported, if within <see cref="ProviderTimeout" />; else NaN (not known).</summary>
    private double Known(ProviderLink link, long now)
    {
        return double.IsNaN(link.Load) || Stopwatch.GetElapsedTime(link.LoadAt, now) > ProviderTimeout
            ? double.NaN
            : link.Load;
    }

    private async Task MoveAsync(Holder h, SubscribeRequest[] moving, TimeSpan over, TimeSpan grace,
        Guid correlation)
    {
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < moving.Length; i++)
        {
            var wait = over * (i + 1) / moving.Length - Stopwatch.GetElapsedTime(start);
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            var r = moving[i];
            lock (_directory)
            {
                if (h.Dead || h.Draining) // (gone, or draining now: its own drain moves what's left)
                {
                    foreach (var m in moving) h.Moving.Remove(m);
                    return;
                }

                if (!h.Routes.Contains(r))
                {
                    h.Moving.Remove(r); // gone already (unsubscribed, its client gone)
                    continue;
                }
            }

            Interlocked.Increment(ref _routesMoved);
            if (grace > TimeSpan.Zero) r.Outlet.ServiceMove(h.NodeId, correlation, grace, r.Route.Tap);
            _ = LetGoMovedAfterAsync(h, r, grace, correlation);
        }
    }

    private async Task LetGoMovedAfterAsync(Holder h, SubscribeRequest r, TimeSpan grace, Guid correlation)
    {
        if (grace > TimeSpan.Zero) await Task.Delay(grace).ConfigureAwait(false);
        bool held;
        lock (_directory)
        {
            h.Moving.Remove(r);
            held = !h.Dead && h.Routes.Remove(r);
        }

        if (!held) return; // moved in time (its client let it go), or gone with its node
        Interlocked.Increment(ref _routesLetGo);
        r.Moved();
        Changed(ServiceHealthEvent.Moving, correlation, h, null, null, "a subscription let go: not moved in time", 1);
    }

    /// <summary>
    ///     A route's message, to the instance holding it — in order, behind what went to its endpoint before; dropped if
    ///     it is dead or its endpoint withdrawn.
    /// </summary>
    internal void Relay(Holder h, object message)
    {
        if (!Volatile.Read(ref h.Dead) && Endpoints.TryGetValue(h.Identity.Endpoint, out var link)) link.Post(message);
    }

    /// <summary>
    ///     A provider's heartbeat: this router, and whether it still holds <paramref name="instance" />'s routes (its
    ///     heartbeat is a sign of life, too).
    /// </summary>
    private RouterBeat Beat(ProviderInstance instance)
    {
        bool knows;
        lock (_directory)
        {
            knows = _instances.TryGetValue((instance.Name, instance.Endpoint), out var h) &&
                    h.Identity.Id == instance.Id && !h.Dead;
            if (knows)
            {
                var now = Stopwatch.GetTimestamp();
                h!.HeardAt = now;
                if (_endpoints.TryGetValue(instance.Endpoint, out var link) && link.Last?.Id == instance.Id)
                    link.HeardAt = now;
            }
        }

        return knows ? _knows ??= new RouterBeat(Router, true) : _knowsNot ??= new RouterBeat(Router, false);
    }

    /// <summary>Gets or creates a named source (a <see cref="Table{TKey,TRow}" />, a custom <see cref="Source" />...).</summary>
    public T Get<T>(string name, Func<string, T> create) where T : class
    {
        return (T)_sources.GetOrAdd(name, static (n, c) => c(n), create);
    }

    /// <summary>Gets or creates a named message topic (created with the given buffer and overflow policy).</summary>
    public MessageTopic Messages(string name, int capacity = 1024, int capacityBytes = 1 << 20,
        OverflowPolicy overflow = OverflowPolicy.Fault)
    {
        return Get(name, n => new MessageTopic(capacity, capacityBytes, overflow) { Label = n });
    }

    public LastValues<TKey> LastValues<TKey>(string name) where TKey : notnull
    {
        return Get(name, static n => new LastValues<TKey>(n));
    }

    public Table<TKey, TRow> Table<TKey, TRow>(string name)
        where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
    {
        return Get(name, static n => new Table<TKey, TRow>(n));
    }

    /// <summary>
    ///     A service: its endpoints (step 1 to each in turn), the clients waiting for it to be up (told it was down, or found
    ///     it unavailable), and since when it has been up for them. Under the directory lock.
    /// </summary>
    private sealed class Service(string name)
    {
        public readonly string Name = name;
        public readonly HashSet<Outlet> Waiting = [];
        public ProviderEndpoint[] Endpoints = [];
        public uint Next;
        public ServiceUpPolicy? Policy;
        public long UpSince; // when the first endpoint was seen up while clients waited (0: not yet)
        public string? Access; // what its provider declared at the last step 1 (null: none yet), for the viewer
    }

    /// <summary>
    ///     A provider instance as the router tracks it: who it is, the transient id clients know it by, the routes it holds,
    ///     its health checks, and whether it is draining. Under the directory lock, but for the readonly fields.
    /// </summary>
    internal sealed class Holder(ProviderInstance identity, RouterInstance router)
    {
        public readonly Flaps Flaps = new(); // its degradations: recoveries damped
        public readonly CancellationTokenSource Gone = new(); // cancelled when deemed dead: abandons a check in flight
        public readonly ProviderInstance Identity = identity;

        public readonly Guid
            NodeId = Guid.CreateVersion7(); // the router's own: tells the client nothing of the instance

        public readonly HealthProbe Probe = new(router, identity); // its health check, made once
        public readonly HashSet<SubscribeRequest> Routes = [];
        public readonly HashSet<SubscribeRequest> Moving = []; // asked to move: not asked again while its grace runs
        public long AskedAt; // the first check sent since its last answer (0: none unanswered): late from then
        public string[] DegradedPaths = []; // the viewer's copy: swapped as they change

        public Guid
            DegradedWith,
            DownWith,
            DrainWith; // the correlation ids its clients were told: its degradation, death, drain

        public long DiedAt; // when deemed dead (Stopwatch ticks), for the viewer
        public bool DrainTells; // drained by the router on request (its provider didn't ask): routes let go are told it

        public long
            HeardAt = Stopwatch.GetTimestamp(),
            SentAt; // timestamps: its last answer (an answer made it), the last check sent

        public bool InFlight, Dead, Draining, Degraded;
        public Dictionary<string, PathHealth>? Paths; // service paths it has reported degraded (and lately), by path
    }

    /// <summary>
    ///     A service path a node reported degraded: whether it is now, the change's id, its degradations (recoveries
    ///     damped).
    /// </summary>
    internal sealed class PathHealth
    {
        public readonly Flaps Flaps = new();
        public Guid Correlation;
        public bool Degraded;
    }

    /// <summary>This router, as a provider reaches it (in process): its heartbeats answered, its drains taken.</summary>
    private sealed class RouterChannel(Publisher router) : IRouterChannel
    {
        public RouterInstance Router => router.Router;

        public ValueTask<RouterBeat> HeartbeatAsync(ProviderInstance instance, RouterInstance expected,
            CancellationToken cancellationToken)
        {
            return new ValueTask<RouterBeat>(router.Beat(instance));
        }

        public ValueTask DrainAsync(ProviderInstance instance, TimeSpan over, TimeSpan grace,
            CancellationToken cancellationToken)
        {
            router.Drain(instance, over, grace);
            return default;
        }

        public ValueTask<MoveAnswer> MoveAsync(ProviderInstance instance, string? partition, RouteId? route,
            TimeSpan over, TimeSpan grace, string reason, CancellationToken cancellationToken)
        {
            return new ValueTask<MoveAnswer>(router.Move(instance, partition, route, over, grace, reason));
        }
    }

    /// <summary>
    ///     The router's side of an endpoint: its messages delivered one at a time, in the order relayed, off the thread that
    ///     relayed them (a provider slow to take one holds up only its own); and what the router knows of its health.
    /// </summary>
    private sealed class ProviderLink(ProviderEndpoint endpoint, IDataProvider provider, RouterChannel channel)
    {
        private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
            { SingleReader = true, AllowSynchronousContinuations = false });

        private int _delivering;
        public long HeardAt;

        // Health (under the directory lock): the instance last heard there, when, its readiness, whether it refuses this
        // router; probing one at a time, and when it was last asked.
        public ProviderInstance? Last;
        public long ProbedAt;
        public bool Refused, Probing;
        public ProviderStatus Status = ProviderStatus.Serving; // till heard otherwise
        public double Load = double.NaN; // as last reported (NaN: not said), when (Stopwatch ticks)
        public long LoadAt;
        public Action? OnReachable; // (a provider reached over a link: told when it's reached again)
        public ProviderEndpoint Endpoint { get; } = endpoint;

        public IDataProvider Provider { get; } = provider;
        public RouterChannel Back { get; } = channel; // this router, as the provider reaches it

        /// <summary>Takes step 1s from this router: serving, not refusing it, and reached (its link, if it has one, up).</summary>
        public bool Eligible => !Refused && Status == ProviderStatus.Serving && Reachable;

        /// <summary>Reached: in process, or its link up (<see cref="IReachableProvider" />).</summary>
        public bool Reachable => Provider is not IReachableProvider { Reachable: false };

        /// <summary>Counts as up for a waiting service: eligible, and heard from lately.</summary>
        public bool Serving(long now, TimeSpan timeout)
        {
            return Eligible && HeardAt != 0 && Stopwatch.GetElapsedTime(HeardAt, now) <= timeout;
        }

        public void Post(object message)
        {
            _queue.Writer.TryWrite(message);
            if (Volatile.Read(ref _delivering) == 0 && Interlocked.Exchange(ref _delivering, 1) == 0)
                _ = Task.Run(DeliverAsync); // from the first
        }

        public void Close()
        {
            _queue.Writer.TryComplete();
        }

        private async Task DeliverAsync()
        {
            await foreach (var message in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                try
                {
                    await (message switch
                    {
                        Activation a => Provider.ActivateAsync(a, CancellationToken.None),
                        Unsubscription u => Provider.UnsubscribeAsync(u, CancellationToken.None),
                        IdentityChange c => Provider.IdentityChangedAsync(c, CancellationToken.None),
                        _ => default
                    }).ConfigureAwait(false);
                }
                catch
                {
                } // the provider's to handle; the route is as it is either way
        }
    }
}

/// <summary>
///     Outlets' frame buffers, by exact size — an <see cref="System.Buffers.ArrayPool{T}" /> would round an outlet's
///     65,828
///     bytes up to 128 KB, onto the large object heap — so clients coming and going leave no garbage: one's buffer is the
///     next one's. Keeps at most <see cref="RetainBytes" /> idle. DEBUG: a returned buffer is poisoned.
/// </summary>
internal sealed class FrameBufferPool(long retainBytes = 64L << 20)
{
    private readonly Dictionary<int, Stack<byte[]>> _idle = new();
    private readonly Lock _lock = new();
    private long _idleBytes, _created, _reused;

    public long RetainBytes => retainBytes;

    /// <summary>Buffers made, reused, and idle; for tests and diagnostics.</summary>
    public (long Created, long Reused, long IdleBytes) Stats
    {
        get
        {
            lock (_lock)
            {
                return (_created, _reused, _idleBytes);
            }
        }
    }

    public byte[] Rent(int size)
    {
        lock (_lock)
        {
            if (_idle.TryGetValue(size, out var stack) && stack.TryPop(out var buffer))
            {
                _idleBytes -= size;
                _reused++;
                return buffer;
            }

            _created++;
        }

        return new byte[size];
    }

    public void Return(byte[] buffer)
    {
#if DEBUG
        buffer.AsSpan().Fill(0xDD);                             // a frame read after its release shows as poison
#endif
        lock (_lock)
        {
            if (_idleBytes + buffer.Length > retainBytes) return;
            if (!_idle.TryGetValue(buffer.Length, out var stack)) _idle[buffer.Length] = stack = new Stack<byte[]>();
            stack.Push(buffer);
            _idleBytes += buffer.Length;
        }
    }
}