namespace Tapline;

/// <summary>
///     What a subscriber to a conflated topic starts with once activated — the provider's choice, accepting it. A message
///     topic has no state to start with: its subscribers start at its next message, whichever this says.
/// </summary>
public enum InitialState : byte
{
    /// <summary>
    ///     The topic's state as it is — a table's snapshot, a value's latest (none until one is published) — sent by the
    ///     library from the topic.
    /// </summary>
    Snapshot = 0,

    /// <summary>
    ///     Nothing: the first record is the topic's next change after step 1. A whole-state topic (a last value) only. The
    ///     provider may still send its own snapshot (<see cref="Mailbox.Send" />), or recent messages (
    ///     <see cref="Mailbox.TryPost" />).
    /// </summary>
    None = 1,

    /// <summary>
    ///     The provider sends the subscriber's initial state itself — declared at step 1, sent once it has the route's step 2
    ///     (<see cref="IDataProvider.ActivateAsync" />: <see cref="Activation.Mailbox" />): a last value's snapshot
    ///     (<see cref="Mailbox.Send" />), or a message topic's recent messages (<see cref="Mailbox.TryPost" />) and then
    ///     <see cref="Mailbox.Ready" />, naming the topic message they run through. Until then the router holds the topic for
    ///     this subscriber at its step-1 position: the initial state goes first, then the topic from where it leaves off —
    ///     never reordered, nothing twice, however late it comes. Not finished within the bind timeout after step 2: the
    ///     subscription ends (<see cref="FaultCode.NoInitialState" />). A last value or a message topic only.
    /// </summary>
    Provider = 2
}

/// <summary>
///     A data provider — a station, a feed — Nchan-style: it owns topics and answers subscriptions to its service, at an
///     endpoint the router reaches it at (<see cref="Publisher.Provide" />). It is an <em>instance</em>
///     (<see cref="ProviderInstance" />): a stable name, its endpoint, and an id drawn at random when it starts — a
///     restart is a
///     new id. A router connecting introduces itself (<see cref="ConnectAsync" />: its <see cref="RouterInstance" /> and a
///     channel
///     back), and every message it sends names it. Step 1 of a subscribe reaches the provider through the router
///     (<see cref="Publisher.SubscribeAsync(Outlet, string, string?, Lane?, CancellationToken)" />), with the subscriber's request and mailbox: it accepts it onto a topic
///     (<see cref="SubscribeRequest.Accept" />) — a shared one, every subscriber the same data, or one of its own —
///     answering
///     with its instance, or refuses it (doesn't accept, or <see cref="SubscribeRequest.Nack" />s a router it presumes
///     dead),
///     and may send it an initial snapshot. The router records the instance on the route and relays the route's later
///     messages to it in order — the client's step 2 (<see cref="ActivateAsync" />) and the route's end
///     (<see cref="UnsubscribeAsync" />). Health goes both ways: the router checks the instance
///     (<see cref="IProviderHealth.CheckHealthAsync" />) while it holds routes — an instance that stops answering, or
///     answers
///     with another id (restarted), is dead: its routes are closed and their clients told the service is down; one that
///     reports service paths degraded (<see cref="HealthReport.DegradedPaths" />: a service, or a partition of one) has
///     the
///     routes there degraded alone — and the provider may heartbeat the router (
///     <see cref="IRouterChannel.HeartbeatAsync" />;
///     <see cref="RouterSessions" /> does). The provider may drain (<see cref="IRouterChannel.DrainAsync" />): one way,
///     its
///     routes let go over a time it sets. The client knows none of this: only a transient id the router gives each
///     instance,
///     its node id (<see cref="TapBinding.NodeId" />), and the ids of a partition's path (
///     <see cref="TapBinding.Partition" />).
///     <para>
///         Its publishes are answered 201/202 (<see cref="Source.Acknowledge()" />): on a 202 carrying the epoch it last
///         accepted a
///         subscriber on, it stops producing for the topic until its next subscriber — and only then (a 202 from an
///         earlier
///         epoch crossed a new subscriber: stale). A route never completed expires (
///         <see cref="OutletOptions.BindTimeout" />).
///         Shaped as messages, so a provider could as well live in another process, at another address.
///     </para>
/// </summary>
public interface IDataProvider : IProviderHealth
{
    /// <summary>
    ///     A router has connected to this endpoint (registered it: <see cref="Publisher.Provide" />): who it is, and a channel
    ///     back to it — for the provider's own heartbeats and its drain. Optional (ignored by default).
    /// </summary>
    ValueTask ConnectAsync(IRouterChannel router, CancellationToken cancellationToken)
    {
        return default;
    }

    /// <summary>
    ///     Step 1, on a subscriber's behalf: accept it onto a topic, or refuse it by not accepting (
    ///     <see cref="SubscribeRequest.Router" />: whose).
    /// </summary>
    ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken);

    /// <summary>
    ///     What it declares of <paramref name="service" />: the realm it serves, and whether it requires a signed-in user of
    ///     it (<see cref="ServiceAccess" />). Asked before each step 1 (a provider in another process: once, and again after
    ///     its link reconnects). Default: open — no realm, no identity shown.
    /// </summary>
    ValueTask<ServiceAccess> AccessAsync(string service, CancellationToken cancellationToken)
    {
        return default;
    }

    /// <summary>
    ///     Step 2, relayed: the client completed a route this instance accepted (<see cref="Activation.Route" />, as
    ///     <see cref="SubscribeRequest.Route" /> said) — told after it is live, asynchronously; the client doesn't wait on it.
    ///     Optional (ignored by default): the route counts from step 1. Not told for a route that ended first.
    /// </summary>
    ValueTask ActivateAsync(Activation notice, CancellationToken cancellationToken)
    {
        return default;
    }

    /// <summary>
    ///     A subscriber's route is gone — unsubscribed, expired before step 2, its connection ended: told after the router
    ///     removed it, asynchronously (so perhaps after a later subscribe). Optional: a provider may ignore it (the default)
    ///     and stop on its next publish's 202 instead. <see cref="Unsubscription.Remaining" /> 0 ends the topic's epoch: stop
    ///     only if <see cref="Unsubscription.Epoch" /> is the one last accepted on — as for a 202, and for the same reason.
    ///     (Not told for the routes of an instance deemed dead — its successor at the endpoint never held them — nor for those
    ///     its own drain let go: it asked for it. Those a drain the router made on request let go are told.)
    /// </summary>
    ValueTask UnsubscribeAsync(Unsubscription notice, CancellationToken cancellationToken)
    {
        return default;
    }

    /// <summary>
    ///     The identity a route was accepted with changed, and the route carries on: renewed — the same user, a later
    ///     expiry — or lapsed, the route anonymous now (a service that requires a sign-in isn't told: its route ends, told
    ///     as an unsubscribe). Told after the change, asynchronously, in order with the route's other notices. Optional
    ///     (ignored by default): a provider may lower what it gives an anonymous subscriber.
    /// </summary>
    ValueTask IdentityChangedAsync(IdentityChange notice, CancellationToken cancellationToken)
    {
        return default;
    }
}

/// <summary>
///     A provider the router reaches over a link of its own (in another process: Tapline.MagicOnion.Providers'
///     <c>RemoteProvider</c>), saying whether it's reached now. Step 1 goes to no endpoint whose provider isn't (nothing
///     there would answer it, but after <see cref="Publisher.SubscribeTimeout" />), and a service waited on doesn't count it
///     up; reached again (<see cref="ReachableChanged" />), it's probed at once. A provider in process is always reached:
///     it needn't implement this.
/// </summary>
public interface IReachableProvider
{
    /// <summary>Whether the router reaches it now: its link up.</summary>
    bool Reachable { get; }

    /// <summary>Raised when <see cref="Reachable" /> changes.</summary>
    event Action? ReachableChanged;
}

/// <summary>
///     Where the router reaches a data provider: the address its route messages and heartbeats go to. In process,
///     <c>inproc://localhost/{name}</c> (<see cref="InProcess" />); in another process, its own —
///     <c>tcp://10.0.0.7:7400</c>,
///     say — reached through a client for that connection. The router's alone: kept on each route, never sent to the
///     client,
///     whose binding names only the router (<see cref="OutletOptions.Endpoint" />: this node's).
/// </summary>
public sealed record ProviderEndpoint
{
    public ProviderEndpoint(Uri address)
    {
        if (!address.IsAbsoluteUri) throw new ArgumentException("An endpoint's address is absolute.", nameof(address));
        Address = address;
    }

    public Uri Address { get; }

    /// <summary>A provider in this process, named <paramref name="name" />.</summary>
    public static ProviderEndpoint InProcess(string name)
    {
        return new ProviderEndpoint(new Uri("inproc://localhost/" + Uri.EscapeDataString(name)));
    }

    public override string ToString()
    {
        return Address.OriginalString;
    }
}

/// <summary>
///     A data provider as it runs: its stable <paramref name="Name" />, its <paramref name="Endpoint" />, and its
///     <paramref name="Id" /> — random, drawn when it starts (<see cref="Start" />), so a restart is a new one. In its
///     answers
///     to step 1 and to heartbeats.
/// </summary>
public sealed record ProviderInstance(string Name, ProviderEndpoint Endpoint, Guid Id)
{
    /// <summary>A provider starting: <paramref name="name" /> at <paramref name="endpoint" />, with a new random id.</summary>
    public static ProviderInstance Start(string name, ProviderEndpoint endpoint)
    {
        return new ProviderInstance(name, endpoint, Guid.NewGuid());
    }

    public override string ToString()
    {
        return $"{Name} at {Endpoint} ({Id})";
    }
}

/// <summary>
///     Step 1 found no instance of the service to answer it: none registered, or the one tried didn't answer in time
///     (<see cref="Publisher.SubscribeTimeout" />). The subscriber's outlet is told when the service is up again
///     (<see cref="Wire.ServiceUp" />): subscribe then, not before.
/// </summary>
/// <summary>
///     A subscription refused by its provider, saying why. <see cref="CorrelationId" /> names it wherever it's logged — by
///     the
///     provider (<see cref="SubscribeRequest.Refuse(string?)" /> returns it), by the router (
///     <see cref="Publisher.SubscriptionRefused" />),
///     and by the client, which it's carried to (a <see cref="Wire.Refused" /> record): one needle for three haystacks. A
///     UUIDv7: ordered by when it was minted.
/// </summary>
public sealed record SubscriptionRefusal(
    Guid CorrelationId,
    string Service,
    string? Parameters,
    string Reason,
    ProviderEndpoint Endpoint,
    DateTimeOffset At,
    RefusalKind Kind = RefusalKind.Refused);

/// <summary>
///     Step 1 refused, with a reason (<see cref="SubscribeRequest.Refuse(string?)" />): what the provider said, and the
///     refusal's correlation id.
/// </summary>
public sealed class SubscriptionRefusedException(SubscriptionRefusal refusal)
    : Exception(
        $"The service '{refusal.Service}' refused the subscription: {refusal.Reason} (correlation {refusal.CorrelationId})")
{
    public SubscriptionRefusal Refusal { get; } = refusal;
}

public sealed class ServiceUnavailableException(string service)
    : Exception($"The service '{service}' is unavailable: subscribe again when it is up.")
{
    public string Service { get; } = service;
}

/// <summary>
///     When the router tells the clients waiting on a service (told it was down, or found it unavailable) that it is up
///     again: once <paramref name="Quorum" /> of its endpoints have an instance serving, or <paramref name="Grace" />
///     after the
///     first one came up — whichever is sooner — each client at a random moment within <paramref name="Spread" /> of that
///     (jitter). A quorum above one (or a grace period) holds them back until there is room for them; the spread keeps
///     them
///     from arriving at once.
/// </summary>
public sealed record ServiceUpPolicy(int Quorum = 1, TimeSpan Grace = default, TimeSpan Spread = default);

/// <summary>A route, as the router knows it: its outlet (the subscribing client's) and its tap there.</summary>
public readonly record struct RouteId(int Outlet, uint Tap)
{
    public override string ToString()
    {
        return $"{Outlet}/{Tap}";
    }
}

/// <summary>
///     Step 2, relayed: from which router, which route, held by which instance, whose (its subscription's service and
///     parameters), on which topic, in which epoch.
/// </summary>
/// <param name="Mailbox">
///     The subscriber's: for the initial state the provider declared it would send (
///     <see cref="InitialState.Provider" />).
/// </param>
public sealed record Activation(
    RouterInstance Router,
    RouteId Route,
    ProviderInstance Instance,
    string Service,
    string? Parameters,
    Source Topic,
    long Epoch,
    Mailbox Mailbox);

/// <summary>
///     A route removed: by which router, which route, held by which instance, whose (its subscription's service and
///     parameters), from which topic, its epoch and the routes left on it.
/// </summary>
public sealed record Unsubscription(
    RouterInstance Router,
    RouteId Route,
    ProviderInstance Instance,
    string Service,
    string? Parameters,
    Source Topic,
    long Epoch,
    int Remaining);

/// <summary>
///     A route's identity changed, the route carrying on: by which router, which route, held by which instance, whose (its
///     subscription's service and parameters), on which topic — and the identity now: renewed (the same user, a later
///     expiry), or null, <paramref name="Lapsed" />.
/// </summary>
public sealed record IdentityChange(
    RouterInstance Router,
    RouteId Route,
    ProviderInstance Instance,
    string Service,
    string? Parameters,
    Source Topic,
    ClientIdentity? Identity,
    bool Lapsed);

/// <summary>
///     What a subscriber asks of its subscription beyond its parameters, at step 1 (<see cref="Publisher.SubscribeAsync(Outlet, string, string?, SubscribeOptions, CancellationToken)" />):
///     its lane, and its own rate. Each member is an init-only property whose default asks nothing, never a constructor
///     parameter, so code built against one version builds and runs against the next.
/// </summary>
public sealed record SubscribeOptions
{
    /// <summary>Nothing asked: the service's defaults.</summary>
    public static SubscribeOptions None { get; } = new();

    /// <summary>The route's scheduling class: the server's choice, not the provider's (<see cref="Outlet.Tap" />).</summary>
    public Lane? Lane { get; init; }

    /// <summary>
    ///     The least time between its state records (<see cref="Tap.MinInterval" />): set on the route as it binds, before
    ///     its first record, and shown to the provider (<see cref="SubscribeRequest.Limits" />). Zero: none.
    /// </summary>
    public TimeSpan MinInterval { get; init; }
}

/// <summary>
///     The limits a subscriber is under, as step 1 saw them (<see cref="SubscribeRequest.Limits" />): for the provider to
///     choose what to serve it — a lighter shape for a capped client, say — never to hold its posts back for it (the
///     router conflates per subscriber). Each member's zero is "none"; members are added as init-only properties, never
///     as constructor parameters, and a router or link that doesn't know one leaves it zero, so a provider reads what its
///     router knows and treats the rest as unlimited.
/// </summary>
public readonly record struct SubscriberLimits
{
    /// <summary>The subscription's own least time between state records (<see cref="SubscribeOptions.MinInterval" />). Zero: none.</summary>
    public TimeSpan MinInterval { get; init; }

    /// <summary>
    ///     Its connection's Streaming cap in bytes per second — the client's <c>bwlimit</c>, or the server's cap
    ///     (<see cref="Outlet.StreamingBytesPerSecond" />), shared by all of the connection's subscriptions. Zero: none.
    /// </summary>
    public long BytesPerSecond { get; init; }

    /// <summary>No limit known.</summary>
    public bool IsNone => this == default;
}

/// <summary>
///     A subscription to a provider's service: its parameters and the subscriber's mailbox; accepted onto a topic, or
///     not.
/// </summary>
public sealed class SubscribeRequest
{
    private readonly Lane? _lane;
    private readonly Lock _lock = new();
    private readonly Outlet? _outlet;
    private readonly IRemoteStep? _remote; // a router in another process, answered here: the provider link's
    private readonly Publisher? _router;
    private ProviderInstance? _acceptedBy; // (answered for another process: who accepted — a router's holder knows here)
    private bool _ended, _down, _answered, _nacked, _declined, _accepted;
    private SubscriptionRefusal? _refusal;

    internal SubscribeRequest(Publisher router, Outlet outlet, ProviderEndpoint endpoint, string service,
        string? parameters, SubscribeOptions options, ServiceAccess access = default, ClientIdentity? identity = null)
    {
        _router = router;
        _outlet = outlet;
        _lane = options.Lane;
        Limits = new SubscriberLimits
            { MinInterval = options.MinInterval, BytesPerSecond = outlet.StreamingBytesPerSecond };
        Endpoint = endpoint;
        Service = service;
        Parameters = parameters;
        Mailbox = new Mailbox(outlet);
        Access = access;
        Identity = identity;
    }

    /// <summary>
    ///     A step 1 from a router in another process, for a provider in this one: what a provider link makes (its provider's
    ///     side). Its accept is <paramref name="remote" />'s to carry out — the topic bound here, the router told to bind its
    ///     own — its route is <paramref name="route" />, as the link's notices name it, and its mailbox the link's, relaying
    ///     to the router's (<see cref="Mailbox.Mailbox(IRemoteMailbox)" />). The link ends its step 1 (<see cref="Answered()" />)
    ///     once the provider's <see cref="IDataProvider.SubscribeAsync" /> returns, then reads what it decided.
    ///     <paramref name="access" /> and <paramref name="identity" /> are as the router sent them.
    /// </summary>
    public SubscribeRequest(IRemoteStep remote, ProviderEndpoint endpoint, string service, string? parameters,
        RouteId route, Mailbox mailbox, ServiceAccess access = default, ClientIdentity? identity = null)
    {
        _remote = remote;
        Endpoint = endpoint;
        Service = service;
        Parameters = parameters;
        Route = route;
        Mailbox = mailbox;
        Access = access;
        Identity = identity;
    }

    public string Service { get; }

    /// <summary>What the provider declared of the service, as the router acted on it (<see cref="IDataProvider.AccessAsync" />).</summary>
    public ServiceAccess Access { get; }

    /// <summary>
    ///     The subscriber's identity — its connection signed in to the service's realm — with the module that asserted it
    ///     and whether that module says it's insecure; null if anonymous, signed in elsewhere, lapsed, or the service is
    ///     open. The provider decides what it's worth: refuse (<see cref="RefusalKind.Unauthenticated" />,
    ///     <see cref="RefusalKind.Forbidden" />), serve, give more. As step 1 saw it: a renewal, or a lapse, is told after
    ///     (<see cref="IDataProvider.IdentityChangedAsync" />).
    /// </summary>
    public ClientIdentity? Identity { get; internal set; }

    /// <summary>
    ///     The router asking (its name and time-ordered id): one the provider presumes dead, it NACKs (
    ///     <see cref="Nack" />).
    /// </summary>
    public RouterInstance Router => _remote?.Router ?? _router!.Router;

    /// <summary>The subscriber's outlet, at this router (none for a request from a router in another process).</summary>
    internal Outlet Outlet => _outlet!;

    /// <summary>What the subscriber asked for (the provider's to interpret).</summary>
    public string? Parameters { get; }

    /// <summary>
    ///     The limits the subscriber is under, as step 1 saw them: its own rate, its connection's cap (<see cref="SubscriberLimits" />).
    ///     Changed later — the app setting <see cref="Tap.MinInterval" />, the connection's cap moved — they aren't told
    ///     again. A link's request carries them as the router sent them (set them as it's made).
    /// </summary>
    public SubscriberLimits Limits { get; init; }

    /// <summary>The endpoint this request was sent to (one of the service's: <see cref="Publisher.Provide" />).</summary>
    public ProviderEndpoint Endpoint { get; }

    /// <summary>The subscriber's own address: what is sent there reaches it alone (an initial snapshot).</summary>
    public Mailbox Mailbox { get; }

    /// <summary>The subscriber's route, once accepted.</summary>
    public Tap? Tap { get; private set; }

    /// <summary>
    ///     Once accepted: the route's id (in its <see cref="Activation" /> and <see cref="Unsubscription" />) and its
    ///     epoch.
    /// </summary>
    public RouteId Route { get; private set; }

    public long Epoch { get; private set; }

    /// <summary>Once accepted: the instance holding the route, as the router tracks it.</summary>
    internal Publisher.Holder? Holder { get; private set; }

    public ProviderInstance? AcceptedBy => Holder?.Identity ?? _acceptedBy;

    /// <summary>
    ///     Once accepted: the router's transient id for the instance holding the route, its node id — all the client
    ///     learns of it.
    /// </summary>
    public Guid? NodeId => Holder?.NodeId;

    /// <summary>
    ///     Once accepted into a partition of the service: its path under the service (names: the provider's and the
    ///     router's alone).
    /// </summary>
    public string? Partition { get; private set; }

    /// <summary>Once accepted: where the route is — the service, or <c>service/partition</c> — as the provider named it.</summary>
    public string ServicePath { get; private set; } = "";

    /// <summary>
    ///     Once accepted into a partition: its path as ids of this router's run, as the client sees it (
    ///     <see cref="Publisher.PartitionId" />).
    /// </summary>
    public string? PartitionId { get; private set; }

    /// <summary>Where the route is, as its client sees it: the service, then the partition's ids.</summary>
    internal string Scope => PartitionId is null ? Service : Service + "/" + PartitionId;

    /// <summary>Whether the provider NACKed the request (<see cref="Nack" />).</summary>
    public bool Nacked
    {
        get
        {
            lock (_lock)
            {
                return _nacked;
            }
        }
    }

    /// <summary>The refusal, if refused with a reason (<see cref="Refuse(string?)" />).</summary>
    public SubscriptionRefusal? Refusal
    {
        get
        {
            lock (_lock)
            {
                return _refusal;
            }
        }
    }

    /// <summary>
    ///     Routes the subscriber to <paramref name="topic" />: binds its tap, to be completed by the client (step 2), held by
    ///     <paramref name="instance" /> — the provider answering (its name, endpoint and id; its endpoint one the router
    ///     reaches,
    ///     usually the one this request came to: <see cref="Endpoint" />). Returns the topic's epoch at this bind — a new one
    ///     if
    ///     the topic's last publish was answered 202 — the one a 202 for it will carry until it closes again: record it, and
    ///     stop producing on a 202 only if it carries the epoch recorded last. An instance with a new id at a known name and
    ///     endpoint supersedes the one before it (restarted: deemed dead); one deemed dead can't accept.
    /// </summary>
    /// <param name="name">The topic's name, shown to the client (default: the service's).</param>
    /// <param name="initial">What the subscriber starts with: the topic's state, or (a whole-state topic) its next change.</param>
    /// <param name="errorPolicy">
    ///     What the subscriber gets if the stream breaks — the topic faults, or this instance is deemed dead (silent past its
    ///     timeout, restarted, withdrawn): the payload, and the stale-data function the router runs on the state it holds
    ///     (e.g. the top of the book, sizes zero), its result sent ahead of the error. The router's: the client's state is
    ///     never the authority.
    /// </param>
    /// <param name="format">
    ///     How the topic's payloads are laid out (<see cref="PayloadFormat" />), for the router's viewer: given here so the
    ///     router has it before the subscriber's first record, and as data — what a provider in another process sends. The
    ///     latest given wins; null leaves the topic's as it is. Its view URL, if any, is on <paramref name="instance" />'s own
    ///     host (its endpoint's; in this process, the loopback): the router posts payloads back to their provider, nowhere
    ///     else (<see cref="ArgumentException" /> if not).
    /// </param>
    /// <param name="partition">
    ///     The partition of the service the topic is in — a path under the service (<c>eu</c>, <c>eu/fx</c>: segments
    ///     separated by '/', at most 255 bytes of UTF-8): it narrows the subscription. Health is told per service path — the
    ///     provider reporting one degraded (<see cref="HealthReport.DegradedPaths" />) degrades the routes at or under it
    ///     alone
    ///     — and its client sees the path as ids of this router's run (<see cref="TapBinding.Partition" />), never its names.
    ///     A
    ///     topic in a partition is in that one alone: accepted again elsewhere — another partition, or none — it throws; a
    ///     topic
    ///     in none may be accepted under several services.
    /// </param>
    public long Accept(Source topic, ProviderInstance instance, string? name = null,
        InitialState initial = InitialState.Snapshot, ErrorPolicy? errorPolicy = null,
        PayloadFormat? format = null, string? partition = null)
    {
        if (format?.ViewUrl is { } view && !PayloadFormat.OnProviderHost(view, instance.Endpoint))
            throw new ArgumentException(
                $"A view URL is on its provider's own host ({instance.Endpoint}): not {view.Host}.", nameof(format));
        if (partition is not null) Publisher.CheckPartition(partition);
        var path = partition is null ? Service : Service + "/" + partition;
        if (_remote is not null) return AcceptHere(topic, instance, name, initial, errorPolicy, format, partition, path);
        var partitionId = partition is null ? null : _router!.PartitionId(Service, partition);
        lock (_lock)
        {
            if (_answered)
                throw new InvalidOperationException(
                    "This subscription has been answered already (step 1 ended: timed out, cancelled, or returned without accepting).");
        }

        var holder = _router!.Resolve(instance); // (a predecessor it supersedes is put down first)
        var held = false;
        long result;
        lock (_lock)
        {
            Unanswered();
            Claim(topic, partition, path);
            if (format is not null)
                topic.Format = format; // (before the route exists: its first record is rendered by it)
            (Partition, ServicePath, PartitionId) = (partition, path, partitionId);
            var tap = Outlet.Bind(topic, name ?? Service, errorPolicy, _lane, initial, out var epoch, this,
                true); // (its relays wait on this lock; its charge reserved before step 1)
            if (Limits.MinInterval > TimeSpan.Zero) tap.MinInterval = Limits.MinInterval; // (before its first record)
            Route = new RouteId(Outlet.Id, tap.Id);
            Epoch = epoch;
            Tap = tap;
            Holder = holder;
            Mailbox.Tap = tap;
            if (tap.IsClosed)
                Unsubscribed(tap, epoch,
                    tap.Source.Routes); // the subscriber is gone (its client gone): told as its end
            else if (!_router.Hold(holder, this))
                Down(holder.DownWith); // dead since it was resolved: the route goes down at once
            else held = true;
            result = epoch;
        }

        if (held) _router.Joined(holder, this); // its node, or a path covering it, degraded: its client told
        return result;
    }

    /// <summary>
    ///     Accept, for a router in another process: the topic is this process's, its route bound here by the link (which
    ///     tells the router to bind its own); the epoch is the topic's here, as this process's publishes answer.
    /// </summary>
    private long AcceptHere(Source topic, ProviderInstance instance, string? name, InitialState initial,
        ErrorPolicy? errorPolicy, PayloadFormat? format, string? partition, string path)
    {
        lock (_lock)
        {
            Unanswered();
            Claim(topic, partition, path);
            if (format is not null) topic.Format = format;
            Epoch = _remote!.Accept(this, topic, instance, name ?? Service, initial, errorPolicy, format, partition);
            (Partition, ServicePath, _accepted, _acceptedBy) = (partition, path, true, instance);
            return Epoch;
        }
    }

    /// <summary>Throws unless the request is still to be answered. Under the lock.</summary>
    private void Unanswered()
    {
        if (Tap is not null || _accepted)
            throw new InvalidOperationException("This subscription has been accepted already.");
        if (_answered) throw new InvalidOperationException("This subscription has been answered already.");
        if (_nacked) throw new InvalidOperationException("This subscription has been refused (Nack).");
        if (_declined) throw new InvalidOperationException("This subscription has been refused (Refuse).");
    }

    /// <summary>The topic claimed for <paramref name="path" />: a partition's topic is its alone; one in none, any service's.</summary>
    private void Claim(Source topic, string? partition, string path)
    {
        var claim = partition is null ? "" : path;
        if (Interlocked.CompareExchange(ref topic.ServicePath, claim, null) is { } at && at != claim)
            throw new InvalidOperationException(at.Length == 0
                ? $"A topic in a partition is in that one alone: this one has been accepted outside any, not into '{path}'."
                : $"A topic in a partition is in that one alone: this one is in '{at}', not {(partition is null ? $"'{Service}' outside any" : $"'{path}'")}.");
    }

    /// <summary>
    ///     Refuses the request because the provider doesn't serve <see cref="Router" /> — a router instance it presumes dead
    ///     (silent past its timeout, or superseded by a newer one of its name). The router learns it is refused there.
    /// </summary>
    public void Nack()
    {
        lock (_lock)
        {
            if (Tap is not null || _accepted)
                throw new InvalidOperationException("This subscription has been accepted already.");
            _nacked = true;
        }
    }

    /// <summary>
    ///     Refuses the request, saying why — the client's criteria aren't valid, say, or there's no room for another topic.
    ///     <paramref name="reason" /> (its first 1,024 bytes) goes to the client, and the router and the client each log it
    ///     with a correlation id minted here, returned for the provider's own log: the same id in all three. The subscriber's
    ///     <c>SubscribeAsync</c> throws <see cref="SubscriptionRefusedException" />. Without a reason, refusing is as not
    ///     accepting is — nothing logged, nothing sent, <c>SubscribeAsync</c> returns null — and this returns
    ///     <see cref="Guid.Empty" />. Either way, the request can't be accepted after.
    /// </summary>
    public Guid Refuse(string? reason = null)
    {
        return Refuse(reason, null);
    }

    /// <summary>
    ///     As <see cref="Refuse(string?)" />, with the correlation id the refusal was given where it was decided: by a
    ///     provider in another process, whose log has it already (a provider link's router side). Null mints one here.
    /// </summary>
    public Guid Refuse(string? reason, Guid? correlation)
    {
        return Refuse(RefusalKind.Refused, reason, correlation);
    }

    /// <summary>
    ///     Refuses the request: the subscriber isn't signed in — to the service's realm, or at all (<see cref="Identity" />
    ///     null) — and should sign in, then subscribe again. Its client is told so, with <paramref name="reason" /> if given.
    /// </summary>
    public Guid RefuseUnauthenticated(string? reason = null)
    {
        return Refuse(RefusalKind.Unauthenticated, reason ?? "Sign in to subscribe.", null);
    }

    /// <summary>Refuses the request: the subscriber is signed in, and not allowed. Its client is told so, with <paramref name="reason" /> if given.</summary>
    public Guid Forbid(string? reason = null)
    {
        return Refuse(RefusalKind.Forbidden, reason ?? "Not allowed.", null);
    }

    /// <summary>
    ///     As <see cref="Refuse(string?, Guid?)" />, of a kind (<see cref="RefusalKind" />): its client is told which, so it
    ///     can ask for a sign-in or say no. An unauthenticated or forbidden refusal is told even without a reason.
    /// </summary>
    public Guid Refuse(RefusalKind kind, string? reason, Guid? correlation)
    {
        lock (_lock)
        {
            if (Tap is not null || _accepted)
                throw new InvalidOperationException("This subscription has been accepted already.");
            if (_answered)
                throw new InvalidOperationException(
                    "This subscription has been answered already (step 1 ended: timed out, cancelled, or returned).");
            if (_nacked || _declined)
                throw new InvalidOperationException("This subscription has been refused already.");
            _declined = true;
            if (string.IsNullOrWhiteSpace(reason))
            {
                if (kind == RefusalKind.Refused) return Guid.Empty;
                reason = kind.ToString();
            }

            var id = correlation ?? Guid.CreateVersion7();
            _refusal = new SubscriptionRefusal(id, Service, Parameters,
                reason.Length > RefusedRecord.MaxReason ? reason[..RefusedRecord.MaxReason] : reason, Endpoint,
                DateTimeOffset.UtcNow, kind);
            return id;
        }
    }

    /// <summary>
    ///     Step 1 is over, for a request from a router in another process (<see
    ///     cref="SubscribeRequest(IRemoteStep, ProviderEndpoint, string, string?, RouteId, Mailbox, ServiceAccess, ClientIdentity?)" />): the provider has answered, or not, and a later accept
    ///     is refused. A router ends its own requests' step 1s.
    /// </summary>
    public void Answered()
    {
        if (_remote is null) throw new InvalidOperationException("A router ends its own requests' step 1s.");
        Answered(true);
    }

    /// <summary>
    ///     Step 1 is over: the provider has answered (or not) — a later accept is refused. <paramref name="kept" /> false
    ///     (step 1 failed: timed out, cancelled, the provider threw): a route accepted meanwhile is closed at once, as an
    ///     unsubscribe, rather than left to expire (and count as a listener till then).
    /// </summary>
    internal void Answered(bool kept)
    {
        Tap? tap;
        lock (_lock)
        {
            _answered = true;
            tap = kept ? null : Tap;
        }

        tap?.Dispose();
    }

    /// <summary>
    ///     Step 2 done: relayed to the instance holding the route (unless it has ended already: then its end is all there
    ///     is to tell).
    /// </summary>
    internal void Activated(Tap tap)
    {
        lock (_lock)
        {
            if (!_ended)
                _router!.Relay(Holder!,
                    new Activation(Router, Route, Holder!.Identity, Service, Parameters, tap.Source, Epoch, Mailbox));
        }
    }

    /// <summary>
    ///     The connection's identity changed and the route carries on (renewed, or lapsed: <paramref name="identity" />
    ///     null): the request follows it, and the instance holding the route is told, after anything relayed before.
    /// </summary>
    internal void IdentityChanged(Tap tap, ClientIdentity? identity)
    {
        lock (_lock)
        {
            Identity = identity;
            if (_ended || Holder is null) return;
            _router!.Relay(Holder,
                new IdentityChange(Router, Route, Holder.Identity, Service, Parameters, tap.Source, identity,
                    identity is null));
        }
    }

    /// <summary>
    ///     The route has been removed: relayed to the instance holding it, after anything relayed before (the closer
    ///     doesn't wait on it).
    /// </summary>
    internal void Unsubscribed(Tap tap, long epoch, int remaining)
    {
        lock (_lock)
        {
            _ended = true;
            if (_down) return; // its instance is dead: nobody to tell
            _router!.Release(Holder!, this);
            _router.Relay(Holder!,
                new Unsubscription(Router, Route, Holder!.Identity, Service, Parameters, tap.Source, epoch, remaining));
        }
    }

    /// <summary>
    ///     The instance holding the route is dead — the route is closed, and its client told the service is down — or, with
    ///     <paramref name="drained" />, draining and letting it go: closed, and its client told to subscribe again (not down);
    ///     told with the change's <paramref name="correlation" /> id. <paramref name="tell" />: its provider didn't ask for
    ///     the
    ///     drain (the router drained its node on request) — told, as for an unsubscribe.
    /// </summary>
    internal void Down(Guid correlation, bool drained = false, bool tell = false)
    {
        Tap? tap;
        lock (_lock)
        {
            if (_down) return;
            _down = _ended = true;
            tap = Tap;
        }

        if (tap is null) return;
        if (drained)
        {
            Outlet.ServiceDrained(Holder!.NodeId, correlation);
            Outlet.Close(tap,
                new StreamFault(FaultCode.ServiceDrained, tap.Source.Version,
                    "The data provider holding this subscription is draining: subscribe again."));
            if (tell)
                _router!.Relay(Holder!,
                    new Unsubscription(Router, Route, Holder!.Identity, Service, Parameters, tap.Source, Epoch,
                        tap.Source.Routes));
        }
        else // its error policy (the provider's stale-data function) applied by the router, then down
        {
            Outlet.Down(tap,
                new StreamFault(FaultCode.ServiceDown, tap.Source.Version,
                    "The data provider holding this subscription is gone."), Holder!.NodeId, correlation);
        }
    }

    /// <summary>
    ///     A route asked to move and still held when its grace ran out: closed (<see cref="FaultCode.Moved" />), and its
    ///     provider told, as for an unsubscribe — it keeps serving, so it needs to know.
    /// </summary>
    internal void Moved()
    {
        Tap? tap;
        lock (_lock)
        {
            if (_down) return;
            _down = _ended = true;
            tap = Tap;
        }

        if (tap is null) return;
        Outlet.Close(tap,
            new StreamFault(FaultCode.Moved, tap.Source.Version,
                "This subscription was to move, and wasn't within its grace: subscribe again."));
        Outlet.Ended(tap.Id, FaultCode.Moved); // (the client told: it fails it, and subscribes again)
        _router!.Relay(Holder!,
            new Unsubscription(Router, Route, Holder!.Identity, Service, Parameters, tap.Source, Epoch,
                tap.Source.Routes));
    }
}

/// <summary>
///     A subscriber's own address, in its subscription request: what the provider sends here reaches that subscriber
///     alone,
///     ahead of anything further from its topic — an initial snapshot, even on a shared topic. (To give every subscriber
///     the
///     same, publish to the topic.)
/// </summary>
public sealed class Mailbox
{
    private readonly Outlet? _outlet;
    private readonly IRemoteMailbox? _remote; // the subscriber's at a router in another process: the provider link's

    internal Tap? Tap;

    internal Mailbox(Outlet outlet)
    {
        _outlet = outlet;
    }

    /// <summary>
    ///     A subscriber's mailbox at a router in another process, for a request a provider link makes: each operation
    ///     relayed there, in order (<see cref="IRemoteMailbox" />).
    /// </summary>
    public Mailbox(IRemoteMailbox remote)
    {
        _remote = remote;
    }

    private Tap Route => Volatile.Read(ref Tap) ??
                         throw new InvalidOperationException(
                             "Accept the subscription onto a topic first: the mailbox is its route's.");

    /// <summary>
    ///     Posts a message to the subscriber alone — in order with its other records, after anything already posted (its
    ///     subscription's mailbox: <see cref="Tap.TryPost" />). After <see cref="SubscribeRequest.Accept" />. False if its
    ///     mailbox
    ///     is full of messages not yet sent: it isn't keeping up. <paramref name="after" />: the subscriber gets it only once
    ///     it
    ///     has that source's state at that version (<see cref="Tapline.After" />).
    /// </summary>
    public bool TryPost(ReadOnlySpan<byte> message, After after = default)
    {
        return _remote?.TryPost(message, after) ?? Route.TryPost(message, after);
    }

    /// <summary>
    ///     A rendezvous with the subscriber: <paramref name="correlation" /> — the id its client gave the request for a change
    ///     you applied — handed to it strictly after it has this route's topic at <paramref name="seq" /> or past it (what the
    ///     change made: <see cref="PublishResult.Seq" />, or a table update's version) — applied, an out-of-band record
    ///     fetched.
    ///     Its client awaits it by that id. See <see cref="Tapline.Tap.TryRendezvous(ReadOnlySpan{byte}, long)" />. False if
    ///     the
    ///     mailbox is full of entries not yet sent, or the subscription has ended: answer the request without it.
    /// </summary>
    public bool TryRendezvous(ReadOnlySpan<byte> correlation, long seq)
    {
        return _remote?.TryRendezvous(correlation, seq) ?? Route.TryRendezvous(correlation, seq);
    }

    /// <summary>
    ///     As <see cref="TryRendezvous(ReadOnlySpan{byte}, long)" />, after another state of the subscriber's (
    ///     <see cref="Tapline.After" />).
    /// </summary>
    public bool TryRendezvous(ReadOnlySpan<byte> correlation, After after)
    {
        return _remote?.TryRendezvous(correlation, after) ?? Route.TryRendezvous(correlation, after);
    }

    /// <summary>
    ///     Sends the subscriber an initial snapshot: a whole value (at most 64 KB), its first record — once activated —
    ///     followed by the topic's changes after <paramref name="asOf" />, the topic version it is as of (default: the topic's
    ///     version now — send it in order with your own publishes), nothing older. After
    ///     <see cref="SubscribeRequest.Accept" />,
    ///     onto a whole-state topic (a last value; a table's initial state is its snapshot:
    ///     <see cref="InitialState.Snapshot" />).
    ///     Under <see cref="InitialState.Provider" /> it is the initial state: the topic held for the subscriber till now goes
    ///     on.
    /// </summary>
    public void Send(ReadOnlySpan<byte> snapshot, long? asOf = null)
    {
        if (_remote is not null)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(snapshot.Length, ushort.MaxValue);
            _remote.Send(snapshot, asOf);
            return;
        }

        var tap = Volatile.Read(ref Tap) ??
                  throw new InvalidOperationException(
                      "Accept the subscription onto a topic first: the snapshot is the topic's.");
        if (!tap.Source.RecordIsWholeState)
            throw new InvalidOperationException(
                "A mailbox snapshot is a whole value: a table's initial state is its snapshot (InitialState.Snapshot).");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(snapshot.Length, ushort.MaxValue);
        _outlet!.SendSnapshot(tap, snapshot, asOf);
    }

    /// <summary>
    ///     The initial state is complete (<see cref="InitialState.Provider" />): the topic, held for the subscriber since step
    ///     1,
    ///     goes on from the message after <paramref name="through" /> — the last topic message the initial messages cover
    ///     (default: the topic's last now — call it in order with your own publishes). The messages it covers aren't sent
    ///     again; those after it the topic still keeps are, then what comes next. Once; ignored when nothing is held.
    /// </summary>
    public void Ready(long? through = null)
    {
        if (_remote is not null)
        {
            _remote.Ready(through);
            return;
        }

        var tap = Volatile.Read(ref Tap) ??
                  throw new InvalidOperationException(
                      "Accept the subscription onto a topic first: its initial state is the route's.");
        _outlet!.InitialReady(tap, through);
    }
}

/// <summary>
///     Step 1 for a router in another process, answered by a provider in this one: what an accept does, as a provider
///     link carries it (its provider's side). It binds the topic here — its state, for the router's copy — and readies the
///     answer that tells the router to bind its own route. It returns the topic's epoch here, as this process's publishes
///     answer for it.
/// </summary>
public interface IRemoteStep
{
    RouterInstance Router { get; }

    long Accept(SubscribeRequest request, Source topic, ProviderInstance instance, string name, InitialState initial,
        ErrorPolicy? errorPolicy, PayloadFormat? format, string? partition);
}

/// <summary>
///     A subscriber's mailbox at a router in another process, as a provider link carries it: each of
///     <see cref="Mailbox" />'s operations relayed there, in order, after the topic's state as it was here when it was
///     called.
/// </summary>
public interface IRemoteMailbox
{
    bool TryPost(ReadOnlySpan<byte> message, After after);
    bool TryRendezvous(ReadOnlySpan<byte> correlation, long seq);
    bool TryRendezvous(ReadOnlySpan<byte> correlation, After after);
    void Send(ReadOnlySpan<byte> snapshot, long? asOf);
    void Ready(long? through);
}

/// <summary>A provider's snapshot waiting to be a tap's first record, and where the tap is after it.</summary>
internal sealed record PendingSnapshot(byte[] Bytes, Cursor After);