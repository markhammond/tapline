using System.Collections.Frozen;
using System.Diagnostics;

namespace Tapline;

/// <summary>
///     A router — a realtime server — as it runs: its stable <paramref name="Name" /> and its <paramref name="Id" />, a
///     UUIDv7
///     drawn when it starts (<see cref="Start" />). Ids of one name are time-ordered: a newer one supersedes the older (a
///     restart, or a second process claiming the name), so a provider can tell which of two is the live one without
///     asking.
///     In every message a router sends a provider.
/// </summary>
public sealed record RouterInstance(string Name, Guid Id)
{
    /// <summary>When it started: the id's timestamp (milliseconds).</summary>
    public DateTimeOffset Started
    {
        get
        {
            Span<byte> b = stackalloc byte[16];
            Id.TryWriteBytes(b, true, out _);
            long ms = 0;
            for (var i = 0; i < 6; i++) ms = (ms << 8) | b[i];
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
    }

    /// <summary>A router starting: <paramref name="name" />, with a new time-ordered id.</summary>
    public static RouterInstance Start(string name)
    {
        return new RouterInstance(name, Guid.CreateVersion7());
    }

    /// <summary>
    ///     Newer than <paramref name="other" />: the same name, a later id (compared in RFC 4122 byte order: the
    ///     timestamp first).
    /// </summary>
    public bool Supersedes(RouterInstance other)
    {
        if (Name != other.Name) return false;
        Span<byte> mine = stackalloc byte[16], theirs = stackalloc byte[16];
        Id.TryWriteBytes(mine, true, out _);
        other.Id.TryWriteBytes(theirs, true, out _);
        return mine.SequenceCompareTo(theirs) > 0;
    }

    public override string ToString()
    {
        return $"{Name} ({Id})";
    }
}

/// <summary>Readiness, as a provider reports it (<see cref="HealthReport.Status" />).</summary>
public enum ProviderStatus : byte
{
    /// <summary>Takes new subscriptions.</summary>
    Serving = 0,

    /// <summary>
    ///     Alive — not down — holding the routes it has, taking no new ones, and letting them go gradually
    ///     (<see cref="IRouterChannel.DrainAsync" />) — one way. Its replacement (blue/green: another instance, at its own
    ///     endpoint) takes the new subscriptions meanwhile.
    /// </summary>
    Draining = 1,

    /// <summary>Alive, but takes no new subscriptions (starting, or failing): its routes stay.</summary>
    NotServing = 2
}

/// <summary>
///     A health check, from <paramref name="Router" />, of whoever is at an endpoint — expecting
///     <paramref name="Expected" /> there (null: whoever it is).
/// </summary>
public sealed record HealthProbe(RouterInstance Router, ProviderInstance? Expected);

/// <summary>
///     A provider's answer to a health check — which is itself its liveness: it is alive (whatever its status). Who it is
///     (<paramref name="Instance" /> — another id than expected: it restarted), its readiness (<paramref name="Status" />;
///     <paramref name="DrainUntil" /> while draining), and whether it serves the router asking:
///     <paramref name="RouterRefused" />
///     — a router it presumes dead, or superseded by a newer instance of the same name — is refused (a NACK) whatever its
///     status. <paramref name="DegradedPaths" />: the service paths it can't serve properly now — a service, or a
///     partition of
///     one (<c>pivot/eu</c>: <see cref="SubscribeRequest.Accept" />'s) — each degrading the routes it holds at or under it
///     alone
///     (their clients shown their stale data, held), the rest streaming on; left out of a later report, they recover.
///     <paramref name="Load" />: how loaded it is, normalized — 0 idle, 1 at capacity, more when over it — by a measure of
///     its own (its CPU, a queue's depth, its subscribers against what it can serve); null, not said. The router sends each
///     step 1 to the least loaded of a few of the service's instances picked at random
///     (<see cref="Publisher.ProviderChoices" />). A struct: answered per check, from memory, it allocates nothing.
/// </summary>
public readonly record struct HealthReport(
    ProviderInstance Instance,
    ProviderStatus Status,
    bool RouterRefused = false,
    DateTimeOffset? DrainUntil = null,
    IReadOnlyList<string>? DegradedPaths = null,
    double? Load = null)
{
    /// <summary>
    ///     Ready: takes new subscriptions (from routers it serves). Draining or not serving, it is alive but not ready —
    ///     not down.
    /// </summary>
    public bool Ready => Status == ProviderStatus.Serving;

    /// <summary>Takes new subscriptions from the router that asked: ready, and not refusing it.</summary>
    public bool Serves => Ready && !RouterRefused;
}

/// <summary>
///     A data provider's health, Healthz-style: what a router — or a cluster's service registry checking its members (DNS,
///     or otherwise: out of scope here) — asks whoever is at an endpoint, and what it answers. <b>Liveness</b>: it answers
///     at
///     all, with its instance (a new id: it restarted). <b>Readiness</b>: serving — or draining, or not serving: alive,
///     but
///     taking no new subscriptions, which is not down. <b>Its routers</b>: whether it still serves the one asking. Over
///     HTTP,
///     liveness and readiness are two endpoints, so a registry stops sending new subscribers to a draining instance
///     without
///     taking it for dead: <c>GET /livez</c> → 200 with the report whenever it answers (serving, draining, not serving);
///     <c>GET /readyz?router={name}:{id}&amp;expect={name}:{id}</c> → 200 when it <see cref="HealthReport.Serves" />, 503
///     with
///     the report otherwise. The router checks each instance holding its routes with it, probes the endpoints of services
///     its
///     clients wait on, and sends step 1 only where the last report serves it — so of a blue/green pair, the one ready.
/// </summary>
public interface IProviderHealth
{
    ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken);
}

/// <summary>
///     The router's answer to a provider's heartbeat: the router there now (another id than expected: the expected one is
///     gone — restarted), and whether it still holds the provider instance's routes (false: it deemed the instance dead,
///     or
///     never knew it — its routes there are gone).
/// </summary>
public sealed record RouterBeat(RouterInstance Router, bool KnowsInstance);

/// <summary>
///     A router as a provider reaches it — handed over when the router connects (<see cref="IDataProvider.ConnectAsync" />
///     ):
///     for the provider's own heartbeats to it (heartbeats go both ways), and its drain. In process, the router itself;
///     across
///     processes, the same connection's other direction.
/// </summary>
public interface IRouterChannel
{
    RouterInstance Router { get; }

    /// <summary>
    ///     The provider's heartbeat: it is <paramref name="instance" />, and expects <paramref name="expected" /> at the
    ///     other end.
    /// </summary>
    ValueTask<RouterBeat> HeartbeatAsync(ProviderInstance instance, RouterInstance expected,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Drains <paramref name="instance" /> — one way, it can't be taken back: the router sends it no more step 1s (they go
    ///     to the instances ready — its blue/green replacement), and moves its clients over <paramref name="over" />, evenly,
    ///     one at a time. Each is told to move (draining — not down) and keeps its routes here for <paramref name="grace" />:
    ///     make-before-break — it subscribes again, onto another instance, and lets these go once that streams; whatever it
    ///     still holds here then is closed (drained). No grace: closed at its turn. The instance stays alive, and its health
    ///     checked, meanwhile — it should stay up until <paramref name="over" /> + <paramref name="grace" />.
    /// </summary>
    ValueTask DrainAsync(ProviderInstance instance, TimeSpan over, TimeSpan grace, CancellationToken cancellationToken);

    /// <summary>
    ///     Moves some of <paramref name="instance" />'s subscriptions at this router to the service's other instances — a
    ///     rebalance, not a retirement: unlike <see cref="DrainAsync" />, the instance keeps serving, here too. Those in
    ///     <paramref name="partition" /> (at or under it; null: all of the instance's), or the one
    ///     <paramref name="route" />, are moved over <paramref name="over" />, evenly, one at a time, each told to move and
    ///     kept here for <paramref name="grace" />: the client subscribes again — the router places it by load — and lets
    ///     the old go once the new streams; what it still holds then is closed (<see cref="FaultCode.Moved" />), and the
    ///     provider told, as for an unsubscribe. Subscriptions moved lately (<see cref="Publisher.MoveDwell" />) stay put.
    ///     Answers how many it will move and the move's correlation id — or why it won't: the instance draining, no other
    ///     instance serving the service here (nowhere to go), nothing of that partition or route here.
    /// </summary>
    ValueTask<MoveAnswer> MoveAsync(ProviderInstance instance, string? partition, RouteId? route, TimeSpan over,
        TimeSpan grace, string reason, CancellationToken cancellationToken);
}

/// <summary>
///     A router's answer to a move (<see cref="IRouterChannel.MoveAsync" />): how many subscriptions it will move, and the
///     correlation id its clients are told and it logs the move with — or why it won't (<see cref="Refused" />).
/// </summary>
public sealed record MoveAnswer(int Routes, Guid Correlation, string? Refused = null)
{
    /// <summary>Taken: the subscriptions it names are being moved.</summary>
    public bool Accepted => Refused is null;

    public static MoveAnswer Refuse(string reason)
    {
        return new MoveAnswer(0, Guid.Empty, reason);
    }
}

/// <summary>
///     The routers a data provider serves, kept on its side: the live instance of each router name (the newest id seen —
///     ids
///     are time-ordered), the routes each holds here, and the provider's own heartbeats to it (while it holds routes). A
///     router instance superseded by a newer one of its name, silent past <see cref="Timeout" />, or that no longer knows
///     this
///     provider instance, is presumed dead: its routes go to <see cref="Lost" /> (to clean up), and from then on it is
///     refused
///     (<see cref="Admits" /> false — a NACK: refuse its step 1s, report it refused, ignore its notices) — till it shows it
///     isn't dead after all: a health check from it is answered refused once (<see cref="Refuses" />), and it closes what it
///     held here; asked again, it's admitted afresh, with no routes. A router's own silence while this provider was paused
///     (a debugger, a machine too loaded to run it) isn't counted against it. Superseded by a newer instance: refused for
///     good. For a provider to use as it likes; the library's router doesn't need it.
/// </summary>
public sealed class RouterSessions(Func<ProviderInstance> self)
{
    private readonly List<Session> _due = [];
    private readonly Lock _lock = new();
    private readonly List<(RouterInstance, RouteId[])> _lost = [];
    private int _beating;
    private long _beatAt; // the last HeartbeatRouters (Stopwatch ticks): one long after, this provider was paused

    // The live session per router name: copy-on-write — swapped under the lock when a router appears or is superseded
    // (rare), read without it by Admits (every message). A session's own fields are under the lock.
    private FrozenDictionary<string, Session> _byName = FrozenDictionary<string, Session>.Empty;

    /// <summary>
    ///     How often the provider heartbeats a router holding its routes, at most (as often as
    ///     <see cref="HeartbeatRouters" /> is called).
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A router holding routes that hasn't answered for this long is presumed dead.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     A router presumed dead (superseded, silent, or no longer knowing this instance), and the routes it held here —
    ///     to clean up.
    /// </summary>
    public Action<RouterInstance, RouteId[]>? Lost { get; set; }

    /// <summary>
    ///     A router connected: its channel, for heartbeats and drains (an older instance of a name already newer here is
    ///     refused).
    /// </summary>
    public void Connected(IRouterChannel channel)
    {
        var session = Admit(channel.Router);
        if (session is not null)
            lock (_lock)
            {
                session.Channel = channel;
            }
    }

    /// <summary>
    ///     Whether to serve <paramref name="router" />: the newest instance of its name and not presumed dead. False: a
    ///     NACK.
    /// </summary>
    public bool Admits(RouterInstance router)
    {
        return Admit(router) is not null;
    }

    /// <summary>
    ///     A health check's answer to <paramref name="router" /> (<see cref="HealthReport.RouterRefused" />): refused if
    ///     superseded, and if presumed dead — told so once, then, asked again, admitted afresh: alive after all (a pause, a
    ///     partition healed), and having closed what it held here at the first answer — a new session, no routes (what it
    ///     held stays forgotten, its notices for them ignored). Its step 1s are taken again from then on.
    /// </summary>
    public bool Refuses(RouterInstance router)
    {
        if (Admit(router) is not null) return false;
        lock (_lock)
        {
            if (!_byName.TryGetValue(router.Name, out var s) || s.Router != router || !s.Dead)
                return true; // (superseded: an older instance of its name)
            if (!s.Told)
            {
                s.Told = true; // (the NACK, once: it closes what it held here)
                return true;
            }

            Replace(router.Name, new Session(router) { Channel = s.Channel }); // (taken back: its channel as it was)
            return false;
        }
    }

    /// <summary>A route accepted for <paramref name="router" /> (its first starts the heartbeats).</summary>
    public void Accepted(RouterInstance router, RouteId route)
    {
        lock (_lock)
        {
            if (_byName.TryGetValue(router.Name, out var s) && s.Router == router && !s.Dead)
            {
                if (s.Routes.Count == 0)
                    s.HeardAt = s.SentAt = Stopwatch.GetTimestamp(); // silent from now, if it never answers
                s.Routes.Add(route);
            }
        }
    }

    /// <summary>A route of <paramref name="router" />'s ended (an unsubscribe notice).</summary>
    public void Ended(RouterInstance router, RouteId route)
    {
        lock (_lock)
        {
            if (_byName.TryGetValue(router.Name, out var s) && s.Router == router) s.Routes.Remove(route);
        }
    }

    /// <summary>The routes held here for <paramref name="router" /> (0 if it is not the live instance of its name).</summary>
    public int RoutesOf(RouterInstance router)
    {
        lock (_lock)
        {
            return _byName.TryGetValue(router.Name, out var s) && s.Router == router && !s.Dead ? s.Routes.Count : 0;
        }
    }

    /// <summary>The provider restarted (a new instance): the routes it held are gone with the old one; the routers stay known.</summary>
    public void Restarted()
    {
        lock (_lock)
        {
            foreach (var s in _byName.Values) s.Routes.Clear();
        }
    }

    /// <summary>
    ///     Tells every live router this instance is draining, over <paramref name="over" />, each client given
    ///     <paramref name="grace" /> to move (one way).
    /// </summary>
    public async Task DrainAsync(TimeSpan over, TimeSpan grace = default, CancellationToken cancellationToken = default)
    {
        IRouterChannel[] channels;
        lock (_lock)
        {
            channels = [.. _byName.Values.Where(s => !s.Dead && s.Channel is not null).Select(s => s.Channel!)];
        }

        foreach (var channel in channels)
            try
            {
                await channel.DrainAsync(self(), over, grace, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            } // that router silent: its timeout will tell
    }

    /// <summary>
    ///     Asks the live routers holding routes here to move some of them to the service's other instances (a rebalance:
    ///     <see cref="IRouterChannel.MoveAsync" />) — those in <paramref name="partition" /> (null: all), or the one
    ///     <paramref name="route" /> (asking only the router holding it). Each router's answer; a router silent or down,
    ///     left out (its timeout will tell).
    /// </summary>
    public async Task<IReadOnlyList<(RouterInstance Router, MoveAnswer Answer)>> MoveAsync(string? partition,
        RouteId? route, TimeSpan over, TimeSpan grace, string reason, CancellationToken cancellationToken = default)
    {
        IRouterChannel[] channels;
        lock (_lock)
        {
            channels =
            [
                .. _byName.Values.Where(s => !s.Dead && s.Channel is not null && s.Routes.Count > 0 &&
                                             (route is not { } r || s.Routes.Contains(r)))
                    .Select(s => s.Channel!)
            ];
        }

        var answers = new List<(RouterInstance, MoveAnswer)>(channels.Length);
        foreach (var channel in channels)
            try
            {
                answers.Add((channel.Router,
                    await channel.MoveAsync(self(), partition, route, over, grace, reason, cancellationToken)
                        .ConfigureAwait(false)));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            } // that router silent: its timeout will tell

        return answers;
    }

    /// <summary>
    ///     Heartbeats the routers holding routes here (each at most every <see cref="HeartbeatInterval" />, one at a time),
    ///     and presumes dead those silent past <see cref="Timeout" /> — not counting a pause of this provider's own (called
    ///     more than half the timeout after the last time). Call it regularly.
    /// </summary>
    public void HeartbeatRouters()
    {
        if (Interlocked.Exchange(ref _beating, 1) != 0) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            var last = _beatAt;
            _beatAt = now;
            var paused = last != 0 && now - last > Timeout.TotalSeconds / 2 * Stopwatch.Frequency ? now - last : 0;
            lock (_lock)
            {
                foreach (var s in _byName.Values)
                {
                    // Called long after the last time: this provider was paused, not its routers — their silence
                    // meanwhile its own, not counted (heard from as if that much later, none ahead of now).
                    if (paused > 0) s.HeardAt = Math.Min(now, s.HeardAt + paused);
                    if (s.Dead || s.Channel is null || s.Routes.Count == 0) continue;
                    if (Stopwatch.GetElapsedTime(s.HeardAt, now) > Timeout)
                    {
                        _lost.Add((s.Router, Bury(s)));
                    }
                    else if (!s.InFlight && Stopwatch.GetElapsedTime(s.SentAt, now) >= HeartbeatInterval)
                    {
                        s.InFlight = true;
                        s.SentAt = now;
                        _due.Add(s);
                    }
                }
            }

            foreach (var (router, routes) in _lost) Lost?.Invoke(router, routes);
            foreach (var s in _due) _ = BeatAsync(s);
        }
        finally
        {
            _lost.Clear();
            _due.Clear();
            Volatile.Write(ref _beating, 0);
        }
    }

    private async Task BeatAsync(Session s)
    {
        RouterBeat? answer = null;
        try
        {
            answer = await s.Channel!.HeartbeatAsync(self(), s.Router, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        } // silence: the timeout counts it

        (RouterInstance, RouteId[])? lost = null;
        lock (_lock)
        {
            s.InFlight = false;
            if (answer is null || s.Dead) return;
            if (answer.Router != s.Router)
            {
                if (_byName.GetValueOrDefault(s.Router.Name) == s) lost = (s.Router, Bury(s));
            } // another router there now (kept as dead)
            else if (!answer.KnowsInstance)
            {
                lost = (s.Router, [.. s.Routes]);
                s.Routes.Clear();
            } // alive, but our routes there are gone
            else
            {
                s.HeardAt = Stopwatch.GetTimestamp();
            }
        }

        if (lost is { } l) Lost?.Invoke(l.Item1, l.Item2);
        if (answer.Router != s.Router &&
            Admit(answer.Router) is { } next) // the newer router at that channel: served from now on
            lock (_lock)
            {
                next.Channel ??= s.Channel;
            }
    }

    /// <summary>Presumes a session's router dead (kept, so its id stays refused); returns the routes it held. Under the lock.</summary>
    private static RouteId[] Bury(Session s)
    {
        s.Dead = true;
        RouteId[] routes = [.. s.Routes];
        s.Routes.Clear();
        return routes;
    }

    private Session? Admit(RouterInstance router)
    {
        if (Volatile.Read(ref _byName).TryGetValue(router.Name, out var known) && known.Router == router)
            return known.Dead ? null : known; // the live router of its name (or one presumed dead): no lock
        (RouterInstance, RouteId[])? lost = null;
        Session? admitted;
        lock (_lock)
        {
            if (!_byName.TryGetValue(router.Name, out var s))
            {
                Replace(router.Name, admitted = new Session(router));
            }
            else if (s.Router == router)
            {
                admitted = s.Dead ? null : s;
            }
            else if (router.Supersedes(s.Router))
            {
                if (!s.Dead) lost = (s.Router, Bury(s));
                Replace(router.Name, admitted = new Session(router));
            }
            else
            {
                admitted = null; // older than the one known: dead
            }
        }

        if (lost is { } l) Lost?.Invoke(l.Item1, l.Item2);
        return admitted;
    }

    /// <summary>The session for <paramref name="name" /> from now on: a new map. Under the lock.</summary>
    private void Replace(string name, Session session)
    {
        Volatile.Write(ref _byName,
            _byName.Where(kv => kv.Key != name).Append(KeyValuePair.Create(name, session)).ToFrozenDictionary());
    }

    private sealed class Session(RouterInstance router)
    {
        public readonly RouterInstance Router = router;
        public readonly HashSet<RouteId> Routes = [];
        public IRouterChannel? Channel;
        public volatile bool Dead; // (read without the lock: Admits)
        public long HeardAt = Stopwatch.GetTimestamp(), SentAt;
        public bool InFlight, Told; // Told: presumed dead, and a health check answered it refused (Refuses)
    }
}

/// <summary>What a <see cref="ServiceHealthChange" /> is: as the Service record's statuses (<see cref="Wire.Service" />).</summary>
public enum ServiceHealthEvent : byte
{
    /// <summary>A node — a provider instance — is gone: its routes closed, their clients waiting for the service to be up.</summary>
    Down = 0,

    /// <summary>A service is up again for the clients waiting on it.</summary>
    Up = 1,

    /// <summary>A draining node let a client's routes go (not moved in time, or no grace).</summary>
    Drained = 2,

    /// <summary>A node is draining: its clients told to move, each in its turn, with a grace.</summary>
    Draining = 3,

    /// <summary>
    ///     A node — or a service path on it — is degraded: late, or reported unhealthy; its routes there held, shown
    ///     their stale data.
    /// </summary>
    Degraded = 4,

    /// <summary>It is healthy again: its routes stream on.</summary>
    Recovered = 5,

    /// <summary>
    ///     Some of a node's subscriptions are moving to the service's other instances, one at a time, with a grace — a
    ///     rebalance: it keeps serving. Also: those not moved within the grace, let go.
    /// </summary>
    Moving = 6
}

/// <summary>
///     A change in a data provider's health the router tells its clients, for the router's log
///     (<see cref="Publisher.ServiceHealthChanged" />): what (<paramref name="Event" />), its correlation id — a UUIDv7,
///     the one
///     its clients are told — the node (its <paramref name="NodeId" />, the router's transient id, and the
///     <paramref name="Instance" /> it stands for), the service <paramref name="Path" /> it covers (names; null: the whole
///     node)
///     and the same as its clients see it (<paramref name="Scope" />: the partition as ids), why (
///     <paramref name="Reason" />),
///     how many clients were told, and the grace a drain gives each.
/// </summary>
public sealed record ServiceHealthChange(
    Guid Correlation,
    ServiceHealthEvent Event,
    Guid? NodeId,
    ProviderInstance? Instance,
    string? Path,
    string? Scope,
    string Reason,
    int Clients,
    TimeSpan Grace,
    DateTimeOffset At);

/// <summary>
///     A health signal's degradations — a node's, or a service path's on it — the latest kept (eight: past that the hold
///     is at
///     its cap anyway), to damp its recoveries against flapping: the first degradation within the window recovers at its
///     first healthy answer; each further one only once it has answered healthy for the hold, doubled per degradation
///     since
///     the first (up to the cap). Under its owner's lock.
/// </summary>
internal sealed class Flaps
{
    private readonly long[] _at = new long[8]; // Stopwatch timestamps (a ring)
    private long _healthySince; // while degraded: healthy since (0: not)
    private int _next, _count;

    public void Degraded(long now)
    {
        _at[_next] = now;
        _next = (_next + 1) & 7;
        if (_count < 8) _count++;
        _healthySince = 0;
    }

    /// <summary>Unhealthy again while degraded: not recovering.</summary>
    public void Unhealthy()
    {
        _healthySince = 0;
    }

    /// <summary>
    ///     Healthy now, while degraded: whether it recovers now (its first degradation in the window: at once; else once
    ///     healthy for the hold).
    /// </summary>
    public bool Recovers(long now, TimeSpan window, TimeSpan hold, TimeSpan max)
    {
        if (_healthySince == 0) _healthySince = now;
        var recent = Recent(now, window);
        if (recent <= 1 || hold <= TimeSpan.Zero)
        {
            _healthySince = 0;
            return true;
        }

        var need = hold;
        for (var i = 2; i < recent && need < max; i++) need += need;
        if (Stopwatch.GetElapsedTime(_healthySince, now) < (need < max ? need : max)) return false;
        _healthySince = 0;
        return true;
    }

    /// <summary>Its degradations within <paramref name="window" /> of <paramref name="now" />.</summary>
    public int Recent(long now, TimeSpan window)
    {
        var recent = 0;
        for (var i = 0; i < _count; i++)
            if (Stopwatch.GetElapsedTime(_at[i], now) <= window)
                recent++;
        return recent;
    }
}