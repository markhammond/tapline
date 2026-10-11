using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Tapline.Client;

/// <summary>Where a <see cref="TaplineClient" /> fetches records sent out of band, and its clock.</summary>
public sealed class TaplineClientOptions
{
    /// <summary>
    ///     The router's out-of-band fetch endpoint: Tapline.AspNetCore's <c>MapTapline</c> prefix and <c>/blob</c>, e.g.
    ///     <c>http://localhost:5900/_tapline/blob</c>. Null: a record sent out of band can't be fetched — its subscription is
    ///     resynced (a fresh handle), then fails.
    /// </summary>
    public Uri? BlobBase { get; set; }

    /// <summary>
    ///     The HTTP client fetches use — best on a connection apart from the frames', so a large blob never queues ahead of
    ///     them. Null: one of the library's.
    /// </summary>
    public HttpClient? Http { get; set; }

    /// <summary>Fetches a URL's bytes in place of an HTTP GET (tests, or a fetch of the transport's own).</summary>
    public Func<Uri, CancellationToken, Task<byte[]>>? Fetch { get; set; }

    /// <summary>
    ///     Times a subscription's out-of-band record is fetched — each failure resyncs it (a fresh handle) — before it
    ///     fails.
    /// </summary>
    public int FetchAttempts { get; set; } = 3;

    /// <summary>The clock: when frames and heartbeats arrived, the transfer rate.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>
    ///     Bound to another outlet — its session, and the identity with it, gone — the ticket of its last sign-in is
    ///     presented again, so no one signs in again because a router was replaced (default on).
    /// </summary>
    public bool ResumeSignIn { get; set; } = true;

    /// <summary>
    ///     Warned its session will lapse, and its policy silent, the ticket of its last sign-in is presented to renew it
    ///     (default on).
    /// </summary>
    public bool RenewSilently { get; set; } = true;
}

/// <summary>
///     Tapline's .NET client, whatever carries its frames. Its transport hands it what arrives — <see cref="Bind" /> on
///     each
///     connection, <see cref="Receive" /> each frame, <see cref="Disconnected" /> when one closes — and says what it
///     answers
///     (an <see cref="ITaplineConnection" />). It keeps each subscription's state (a table's rows, sequence and checksum
///     verified; a value; a watch's members; a message stream handed over in order, once), acknowledges every frame, asks
///     for a snapshot where a delta fails, resumes after a reconnect from the last frame it received, fetches records sent
///     out of band, and raises the router's notices. Subscribing is two-phase: the app's step 1, on its server, binds a
///     tap
///     and hands back its <see cref="TapBinding" />; <see cref="Subscribe" /> registers its handler, then activates it —
///     so its
///     first record always has somewhere to go. Everything happens under one lock, handlers and events included: one at a
///     time, in order. Its status — <see cref="Reachable" />, <see cref="Router" />, <see cref="LastFrame" />,
///     <see cref="SinceLastFrame" />, <see cref="BytesReceived" />, <see cref="TransferRate" />, <see cref="Resyncs" /> —
///     reads
///     without it, from any thread: never waiting on a handler.
/// </summary>
public sealed partial class TaplineClient
{
    private const double RateTauMs = 2000; // transfer rate smoothing: exponential, this time constant
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient());

    private readonly Dictionary<uint, uint>
        _confirmed = []; // tap → the ticket of its last out-of-band record confirmed (said again on a rebind)

    private readonly ITaplineConnection _connection;
    private int _syncs; // sync ids handed out (Tap.Sync): the next, one more

    private readonly Dictionary<Guid, List<string>>
        _degradedScopes = []; // service paths degraded on a node (as this client sees them)

    private readonly HashSet<Guid>
        _draining = [], _degraded = []; // nodes draining; degraded (late, or unhealthy by their word)

    private readonly Lock _gate = new();
    private readonly HashSet<Task> _inflight = [];

    private readonly Dictionary<string, Guid>
        _lastNotice = []; // service → the correlation id of the last notice about it

    private readonly TaplineClientOptions _options;
    private readonly Dictionary<string, RendezvousWait> _rendezvous = [];
    private readonly HashSet<Guid> _servicesDown = [], _servicesDrained = [], _refusalsSeen = [];
    private readonly Dictionary<uint, Subscription> _subscriptions = [];
    private readonly TimeProvider _time;

    private readonly HashSet<string>
        _waitingFor = [], _moving = []; // services told down (waiting for up); let go by a drain (to subscribe again)

    private bool _bound, _reachable;
    private long _epoch, _lastFrameAt, _rateAt, _bytesReceived, _lastFrame;
    private Heartbeat _heartbeat;
    private uint _nonce;
    private double _rate;
    private int _resyncs;
    private StrongBox<RouterStatus> _router = new(); // replaced, never changed: read without the lock

    public TaplineClient(ITaplineConnection connection, TaplineClientOptions? options = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? new TaplineClientOptions();
        _time = _options.Time;
        BlobBase = _options.BlobBase is { } at
            ? at.AbsoluteUri.EndsWith('/') ? at : new Uri(at.AbsoluteUri + "/")
            : null;
    }

    /// <summary>The outlet this client is bound to (its id on the router), once bound.</summary>
    public int? OutletId { get; private set; }

    /// <summary>The router is in reach: bound, and the connection not closed since.</summary>
    public bool Reachable => Volatile.Read(ref _reachable);

    /// <summary>The last frame received (its sequence number): every subscription resumes from there after a reconnect.</summary>
    public long LastFrame => Volatile.Read(ref _lastFrame);

    /// <summary>The router's last heartbeat.</summary>
    public Heartbeat LastHeartbeat
    {
        get
        {
            lock (_gate)
            {
                return _heartbeat;
            }
        }
    }

    /// <summary>Frame bytes received in all.</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>
    ///     Snapshots asked for: a delta that didn't check out, or an out-of-band record that couldn't be fetched. None,
    ///     on a healthy stream.
    /// </summary>
    public int Resyncs => Volatile.Read(ref _resyncs);

    /// <summary>
    ///     The receive rate in bytes per second, smoothed: each byte counts e^(−age/2 s)/2 s, so a steady stream reads
    ///     its true rate.
    /// </summary>
    public double TransferRate => RateAt(_time.GetTimestamp());

    /// <summary>
    ///     How long since the router was last heard from — the data shown is at least this old (
    ///     <see cref="TimeSpan.MaxValue" />: never).
    /// </summary>
    public TimeSpan SinceLastFrame => Volatile.Read(ref _lastFrameAt) is var at && at != 0
        ? _time.GetElapsedTime(at)
        : TimeSpan.MaxValue;

    internal Uri? BlobBase { get; }

    /// <summary>
    ///     The router's own state, as its heartbeats carry it — serving, degraded (why) or draining (by when) — and the change
    ///     that made it (its correlation id, as the router logged it). Serving, with no correlation, before the first
    ///     heartbeat.
    /// </summary>
    public RouterStatus Router => Volatile.Read(ref _router).Value;

    /// <summary>A heartbeat arrived: the router's clock, and its number (echoed in this client's next acknowledgement).</summary>
    public event Action<Heartbeat>? HeartbeatReceived;

    /// <summary>The router's state changed (a new correlation id): its heartbeats say so — at once, as the router changes it.</summary>
    public event Action<RouterStatus>? RouterStatusChanged;

    /// <summary>
    ///     The router's health as this client sees it, for the app's own health checks (<c>AddCheck</c>): its state, and
    ///     whether its heartbeats keep coming — late after <paramref name="lateAfter" /> (3 s; one a second while it's in
    ///     reach), silent after <paramref name="silentAfter" /> (10 s).
    /// </summary>
    public RouterHealthCheck RouterHealthCheck(TimeSpan? lateAfter = null, TimeSpan? silentAfter = null)
    {
        return new RouterHealthCheck(this, lateAfter ?? TimeSpan.FromSeconds(3),
            silentAfter ?? TimeSpan.FromSeconds(10));
    }

    /// <summary>
    ///     A service's health as this client sees it, for the app's own health checks (<c>AddCheck</c>), over its
    ///     subscriptions
    ///     to <paramref name="service" /> (by name: a partition's ids are this router run's alone): healthy while each streams
    ///     from a node serving it; degraded while one is shown stale data (its node, or a partition there, degraded), its node
    ///     drains, or a drain let one go (to be subscribed again); unhealthy while the service is down for this client (told
    ///     so: waiting for it to be up), or the router is out of reach.
    /// </summary>
    public ServiceHealthCheck ServiceHealthCheck(string service)
    {
        return new ServiceHealthCheck(this, service);
    }

    /// <summary>
    ///     A node — a data provider instance, by its node id — is gone: every subscription through it failed first
    ///     (<see cref="FaultCode.ServiceDown" />). Wait for <see cref="ServiceUp" />. With the change's correlation id, as the
    ///     router logged it.
    /// </summary>
    public event Action<Guid, Guid>? ServiceDown;

    /// <summary>A service this client waited on is up again (its name, and the change's correlation id): subscribe again.</summary>
    public event Action<string, Guid>? ServiceUp;

    /// <summary>
    ///     A draining node let this client's subscriptions through it go (they failed first,
    ///     <see cref="FaultCode.ServiceDrained" />): subscribe again now. Its node id, the drain's correlation id.
    /// </summary>
    public event Action<Guid, Guid>? ServiceDrained;

    /// <summary>
    ///     A node is draining: move — subscribe again (another node takes it), then let the old ones go (
    ///     <see cref="Replace" />) within the grace. Its node id, the grace, the drain's correlation id.
    /// </summary>
    public event Action<Guid, TimeSpan, Guid>? ServiceDraining;

    /// <summary>
    ///     One subscription is to move — its instance rebalancing, not retiring: subscribe again (the router places it by
    ///     load), then let the old go (<see cref="Replace" />) within the grace; still held then, it ends
    ///     (<see cref="FaultCode.Moved" />). The subscription, the grace, the move's correlation id.
    /// </summary>
    public event Action<Subscription, TimeSpan, Guid>? SubscriptionMoving;

    /// <summary>
    ///     A node's state changed — degraded, serving again, draining, down, drained — or, with a scope, a service path's
    ///     there (degraded, or serving again): each with its change's correlation id.
    /// </summary>
    public event Action<ProviderChange>? ProviderStateChanged;

    /// <summary>
    ///     The router ended this client's subscriptions from one tap id to another (each failed first), and why: stalled,
    ///     or gone while it was away.
    /// </summary>
    public event Action<FaultCode, uint, uint>? Ended;

    /// <summary>
    ///     A provider refused a subscription of this client's, saying why — once per refusal (a resent record isn't
    ///     raised again).
    /// </summary>
    public event Action<Refusal>? Refused;

    /// <summary>The router came in reach (bound) or went out of it (the connection closed).</summary>
    public event Action<bool>? ReachabilityChanged;

    /// <summary>A rendezvous nobody awaited (<see cref="RendezvousAsync" />): its correlation, and where it came through.</summary>
    public event Action<ReadOnlyMemory<byte>, RendezvousInfo>? Rendezvous;

    /// <summary>A handler (or an event's) threw: the client goes on.</summary>
    public event Action<Subscription?, Exception>? HandlerFailed;

    /// <summary>A record this client can't read: the rest of its frame is skipped.</summary>
    public event Action<string>? Malformed;

    /// <summary>
    ///     The transport: this client's connection reaches the router, bound to its outlet (its id and nonce) — on the first
    ///     connection and every reconnect. Bound again to the same outlet, it says where to go on from (the last frame
    ///     received), activates its subscriptions again (an activation lost in flight; duplicates are ignored), and confirms
    ///     again the last out-of-band record of each (one may have been lost as the connection fell). Bound to another — its
    ///     session gone while it was away — its subscriptions are over (<see cref="FaultCode.Gone" />: subscribe again), and
    ///     it
    ///     starts afresh.
    /// </summary>
    public void Bind(int outletId, uint nonce)
    {
        lock (_gate)
        {
            var another = _bound && (OutletId != outletId || _nonce != nonce);
            if (another)
            {
                foreach (var gone in _subscriptions.Values.ToList()) ServiceEnded(gone, FaultCode.Gone);
                _heartbeat = default;
                Volatile.Write(ref _lastFrame, 0);
                _confirmed.Clear();
                SessionLost();
            }

            (OutletId, _nonce, _bound) = (outletId, nonce, true);
            Volatile.Write(ref _routerControls, 0); // (a new connection: what its router takes, told in its first frame)
            _epoch++; // fetches started before: stale now
            SetReachable(true);
            foreach (var (tapId, ticket) in _confirmed) _connection.Send(ControlMessage.Fetched(tapId, ticket));
            _confirmed.Clear();
            _connection.Send(ControlMessage.Resume(_lastFrame, _heartbeat.Seq));
            foreach (var tapId in _subscriptions.Keys) _connection.Send(ControlMessage.Activate(tapId));
            if (another) ResumeOnNewSession();
        }
    }

    /// <summary>The transport: the connection closed (dropped, or timed out) — the router out of reach until bound again.</summary>
    public void Disconnected()
    {
        lock (_gate)
        {
            SetReachable(false);
        }
    }

    /// <summary>
    ///     Step 2: registers <paramref name="handler" /> for the tap step 1 bound, then activates it. Its handler hears of it
    ///     from then on — and at once, before this returns, if its provider instance is already down or drained (it fails).
    /// </summary>
    public Subscription Subscribe(TapBinding binding, ISubscriptionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_subscriptions.ContainsKey(binding.TapId))
                throw new InvalidOperationException($"Tap {binding.TapId} is subscribed already.");
            var subscription = new Subscription(this, binding, handler);
            _subscriptions[binding.TapId] = subscription;
            if (binding.Service is { } service && !(binding.NodeId is { } node &&
                                                    (_servicesDown.Contains(node) || _servicesDrained.Contains(node))))
            {
                _waitingFor.Remove(service); // subscribed again: not waiting, nor moving
                _moving.Remove(service);
            }

            if (binding.NodeId is { } instance && _servicesDown.Contains(instance))
                ServiceEnded(subscription, FaultCode.ServiceDown); // gone before it was activated
            else if (binding.NodeId is { } drained && _servicesDrained.Contains(drained))
                ServiceEnded(subscription, FaultCode.ServiceDrained);
            else if (_bound) _connection.Send(ControlMessage.Activate(binding.TapId));
            return subscription;
        }
    }

    /// <summary>
    ///     Make before break: subscribes <paramref name="binding" /> in place of the tap <paramref name="oldTapId" /> — the
    ///     old
    ///     one streams on until the new one's first state, which drops it; from then, the new one alone. Their states never
    ///     mix: each comes under its own tap id. For moving off a draining instance, or to another form of the same topic.
    /// </summary>
    public Subscription Replace(uint oldTapId, TapBinding binding, ISubscriptionHandler handler)
    {
        lock (_gate)
        {
            var subscription = Subscribe(binding, handler);
            if (!subscription.IsEnded) subscription.Replaces = oldTapId;
            return subscription;
        }
    }

    /// <summary>Unsubscribes: its state forgotten, nothing more handed over, the router told (a no-op if it has ended).</summary>
    public void Drop(uint tapId)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(tapId, out var subscription)) return;
            Forget(subscription);
            EndRendezvous(tapId, FaultCode.None);
            if (_bound) _connection.Send(ControlMessage.Close(tapId, FaultCode.None));
        }
    }

    /// <summary>
    ///     A correlation id for a request: a UUIDv7 (this machine's clock in its first 48 bits), its 16 bytes in RFC 9562
    ///     order.
    /// </summary>
    public static byte[] NewCorrelation()
    {
        var id = new byte[16];
        Guid.CreateVersion7().TryWriteBytes(id, true, out _);
        return id;
    }

    /// <summary>
    ///     Completes when the router delivers the rendezvous <paramref name="correlation" /> (at most 64 bytes) — strictly
    ///     after
    ///     the state the change it names made. Await it before sending the request, so nothing can come first. With
    ///     <paramref name="tapId" />, it fails if that subscription ends first (<see cref="SubscriptionEndedException" />).
    /// </summary>
    public Task<RendezvousInfo> RendezvousAsync(ReadOnlyMemory<byte> correlation, uint? tapId = null,
        CancellationToken cancellationToken = default)
    {
        var key = Convert.ToHexString(correlation.Span);
        lock (_gate)
        {
            if (_rendezvous.TryGetValue(key, out var awaited)) return awaited.Source.Task;
            var wait = new RendezvousWait(tapId);
            _rendezvous[key] = wait;
            if (cancellationToken.CanBeCanceled)
                wait.Cancellation = cancellationToken.Register(() =>
                {
                    lock (_gate)
                    {
                        if (_rendezvous.TryGetValue(key, out var current) && ReferenceEquals(current, wait))
                        {
                            _rendezvous.Remove(key);
                            wait.Source.TrySetCanceled(cancellationToken);
                        }
                    }
                });
            return wait.Source.Task;
        }
    }

    private uint _routerControls; // (the control kinds this connection's router takes, as it said: 0, not said yet)

    /// <summary>
    ///     The control messages this connection's router takes (<see cref="ControlKinds" />: a bit each), as it says on each
    ///     new connection; 0 until it has. A request it doesn't take fails at once, not by its timeout.
    /// </summary>
    public uint RouterControls => Volatile.Read(ref _routerControls);

    /// <summary>Whether the router may take <paramref name="kind" />: it says it does, or hasn't said yet.</summary>
    private bool MayTake(ControlKind kind)
    {
        var controls = RouterControls;
        return controls == 0 || ControlKinds.Includes(controls, kind);
    }

    /// <summary>
    ///     Is this client up to date on a subscription? Asks the router (<c>Tap.Sync</c>), which answers with a rendezvous
    ///     strictly after this client has the subscription's state as of when the router received the ask: at once if
    ///     nothing is held back (a quiet source, a fast path), as late as the delivery of what is otherwise — a rate cap's,
    ///     an interval's. So it tells a quiet subscription from one whose changes are waiting. Fails if the subscription
    ///     ends first (<see cref="SubscriptionEndedException" />), and at once (<see cref="NotSupportedException" />) if the
    ///     router says it doesn't take <c>Tap.Sync</c> (<see cref="RouterControls" />). Asked while not bound, nothing is
    ///     sent: bound or not, give it a timeout (<paramref name="cancellationToken" />).
    /// </summary>
    public Task<RendezvousInfo> SyncAsync(uint tapId, CancellationToken cancellationToken = default)
    {
        if (!MayTake(ControlKind.Sync))
            return Task.FromException<RendezvousInfo>(new NotSupportedException(
                "The router doesn't take Tap.Sync: it says so on this connection (an older router)."));
        var syncId = (uint)Interlocked.Increment(ref _syncs);
        if (syncId == 0) syncId = (uint)Interlocked.Increment(ref _syncs); // (0 is no id)
        var correlation = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(correlation, syncId);
        var answered = RendezvousAsync(correlation, tapId, cancellationToken); // (awaited before it's asked)
        bool bound;
        lock (_gate)
        {
            bound = _bound;
        }

        if (bound) _connection.Send(ControlMessage.Sync(tapId, syncId));
        return answered;
    }

    /// <summary>Completes once no out-of-band fetch is in flight.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                pending = [.. _inflight];
            }

            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The transport: a frame arrived — its sequence number and payload (its records). Each record is applied and handed
    ///     over, then the frame acknowledged, with the last heartbeat's echo.
    /// </summary>
    public void Receive(long seq, ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            Volatile.Write(ref _rate,
                RateAt(now) + frame.Length * 1000.0 / RateTauMs); // (the status: written here, read anywhere)
            Volatile.Write(ref _rateAt, now);
            Volatile.Write(ref _lastFrameAt, now);
            Volatile.Write(ref _bytesReceived, _bytesReceived + frame.Length);
            for (var at = 0; at < frame.Length;)
            {
                var rest = frame[at..];
                var length = LengthOf(rest);
                if (length < 0)
                {
                    Raise(Malformed,
                        $"A record this client can't read (type 0x{rest[0]:X2}, at byte {at} of frame {seq}): the rest of the frame skipped.");
                    break;
                }

                Apply(rest[..length], now);
                at += length;
            }

            Volatile.Write(ref _lastFrame, seq);
            _connection.Send(ControlMessage.Ack(seq, _heartbeat.Seq));
        }
    }

    /// <summary>A record's length, its header read — or −1: truncated, or of a type this client doesn't know.</summary>
    private static int LengthOf(ReadOnlySpan<byte> r)
    {
        var length = r[0] switch
        {
            Wire.Heartbeat => Wire.HeartbeatSize,
            Wire.RouterChange => Wire.RouterChangeLength(r),
            Wire.Value when r.Length >= Wire.ValueHeader => Wire.ValueHeader +
                                                            BinaryPrimitives.ReadUInt16LittleEndian(r[5..]),
            Wire.Table => TableReplica.LengthOf(r),
            Wire.Error when r.Length >= Wire.ErrorHeader => Wire.ErrorHeader +
                                                            BinaryPrimitives.ReadUInt16LittleEndian(r[15..]),
            Wire.Group when r.Length >= Wire.GroupHeader => Wire.GroupHeader +
                                                            (int)Math.Min(int.MaxValue - Wire.GroupHeader,
                                                                BinaryPrimitives.ReadUInt32LittleEndian(r[8..])),
            Wire.OobRef => Wire.OobRefSize,
            Wire.Service when r.Length >= Wire.ServiceHeader => Wire.ServiceHeader +
                                                                BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
            Wire.TopicMessage when r.Length >= Wire.TopicMessageHeader => Wire.TopicMessageHeader +
                                                                          (int)Math.Min(
                                                                              int.MaxValue - Wire.TopicMessageHeader,
                                                                              BinaryPrimitives.ReadUInt32LittleEndian(
                                                                                  r[14..])),
            Wire.Ended => Wire.EndedSize,
            Wire.Rendezvous when r.Length >= Wire.RendezvousHeader => Wire.RendezvousHeader + r[14],
            Wire.Auth => AuthLength(r),
            Wire.Session => SessionLength(r),
            Wire.Refused when r.Length >= 20 && r.Length >= 22 + BinaryPrimitives.ReadUInt16LittleEndian(r[18..])
                => 22 + BinaryPrimitives.ReadUInt16LittleEndian(r[18..]) +
                   BinaryPrimitives.ReadUInt16LittleEndian(
                       r[(20 + BinaryPrimitives.ReadUInt16LittleEndian(r[18..]))..]),
            _ => -1
        };
        return length <= r.Length ? length : -1;
    }

    private void Apply(ReadOnlySpan<byte> r, long now)
    {
        switch (r[0])
        {
            case Wire.Heartbeat:
            {
                _heartbeat = new Heartbeat(BinaryPrimitives.ReadUInt32LittleEndian(r[1..]),
                    DateTimeOffset.UnixEpoch.AddTicks(BinaryPrimitives.ReadInt64LittleEndian(r[5..]) * 10), now);
                Raise(HeartbeatReceived, _heartbeat);
                break;
            }
            case Wire.RouterChange: // the router's state, behind a heartbeat till echoed: a change once here
            {
                Volatile.Write(ref _routerControls, BinaryPrimitives.ReadUInt32LittleEndian(r[Wire.RouterChangeControls..]));
                var correlation = new Guid(r.Slice(2, 16), true);
                if (correlation == _router.Value.Correlation) break;
                var state = (RouterState)r[1];
                var reason = state == RouterState.Degraded
                    ? Encoding.UTF8.GetString(r.Slice(Wire.RouterChangeHeader + 1, r[Wire.RouterChangeHeader]))
                    : null;
                DateTimeOffset? drainsBy = state == RouterState.Draining
                    ? _time.GetUtcNow()
                        .AddMilliseconds(BinaryPrimitives.ReadUInt32LittleEndian(r[Wire.RouterChangeHeader..]))
                    : null;
                var status = new RouterStatus(state, correlation, reason, drainsBy);
                Volatile.Write(ref _router, new StrongBox<RouterStatus>(status));
                Raise(RouterStatusChanged, status);
                break;
            }
            case Wire.Value:
                if (Streaming(r, out var valued))
                {
                    valued.Generation++;
                    valued.Value = r.Slice(Wire.ValueHeader, BinaryPrimitives.ReadUInt16LittleEndian(r[5..]))
                        .ToArray(); // a copy: the frame's buffer goes back
                    valued.Blob = null;
                    NotifyValue(valued);
                }

                break;
            case Wire.Table:
                if (Streaming(r, out var tabled))
                {
                    tabled.Generation++;
                    ApplyTable(tabled, r);
                }

                break;
            case Wire.Group:
                if (Streaming(r, out var grouped))
                {
                    grouped.Generation++;
                    ApplyGroup(grouped, r);
                }

                break;
            case Wire.OobRef:
                if (Streaming(r, out var referred))
                {
                    var generation = ++referred.Generation;
                    var blob = new BlobRef(this, r.Slice(7, 16), r.Slice(23, 16),
                        (int)BinaryPrimitives.ReadUInt32LittleEndian(r[39..]), (r[6] & Wire.OobContentAddressed) != 0);
                    if ((r[6] & Wire.OobModeMask) == Wire.OobReference)
                    {
                        (referred.Blob, referred.Value) = (blob, default);
                        Handle(referred, static s => s.Handler.OnBlob(s, s.Blob!));
                    }
                    else
                    {
                        FetchRecord(referred, r[5], blob, BinaryPrimitives.ReadUInt32LittleEndian(r[43..]), generation);
                    }
                }

                break;
            case Wire.TopicMessage:
                if (Streaming(r, out var messaged))
                {
                    messaged.Generation++;
                    ApplyMessage(messaged, r);
                }

                break;
            case Wire.Error:
                if (Streaming(r, out var failed))
                    Fail(failed, new TapError((FaultCode)BinaryPrimitives.ReadUInt16LittleEndian(r[5..]),
                        BinaryPrimitives.ReadInt64LittleEndian(r[7..]),
                        r.Slice(Wire.ErrorHeader, BinaryPrimitives.ReadUInt16LittleEndian(r[15..])).ToArray(), false));
                break;
            case Wire.Service:
                ApplyService(r[1], r[Wire.ServiceHeader..]);
                break;
            case Wire.Ended:
            {
                var code = (FaultCode)BinaryPrimitives.ReadUInt16LittleEndian(r[1..]);
                uint from = BinaryPrimitives.ReadUInt32LittleEndian(r[3..]),
                    to = BinaryPrimitives.ReadUInt32LittleEndian(r[7..]);
                foreach (var ended in _subscriptions.Values.Where(s => s.TapId >= from && s.TapId <= to).ToList())
                    ServiceEnded(ended, code);
                Raise(Ended, code, from, to);
                break;
            }
            case Wire.Rendezvous:
            {
                var info = new RendezvousInfo(BinaryPrimitives.ReadUInt32LittleEndian(r[1..]),
                    BinaryPrimitives.ReadInt64LittleEndian(r[6..]), (r[5] & Wire.FlagMissed) != 0);
                var correlation = r[Wire.RendezvousHeader..];
                var key = Convert.ToHexString(correlation);
                if (_rendezvous.Remove(key, out var wait))
                {
                    wait.Cancellation.Dispose();
                    wait.Source.TrySetResult(info);
                }
                else
                {
                    Raise(Rendezvous, (ReadOnlyMemory<byte>)correlation.ToArray(), info);
                }

                break;
            }
            case Wire.Auth:
                ApplyAuth(r);
                break;
            case Wire.Session:
                ApplySession(r);
                break;
            case Wire.Refused:
            {
                var kind = (RefusalKind)r[1];
                var correlationId = new Guid(r.Slice(2, 16), true);
                int serviceLength = BinaryPrimitives.ReadUInt16LittleEndian(r[18..]);
                var service = Encoding.UTF8.GetString(r.Slice(20, serviceLength));
                var reason = Encoding.UTF8.GetString(r.Slice(22 + serviceLength,
                    BinaryPrimitives.ReadUInt16LittleEndian(r[(20 + serviceLength)..])));
                if (_refusalsSeen.Add(correlationId))
                    Raise(Refused, new Refusal(correlationId, service, reason, kind));
                break;
            }
        }
    }

    /// <summary>The subscription a record is for — only those subscribed (activated) stream.</summary>
    private bool Streaming(ReadOnlySpan<byte> record, [MaybeNullWhen(false)] out Subscription subscription)
    {
        return _subscriptions.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(record[1..]), out subscription);
    }

    private void ApplyTable(Subscription subscription, ReadOnlySpan<byte> record)
    {
        var table = subscription.Table ??= new TableReplica();
        if (table.Apply(record))
        {
            NotifyTable(subscription, table, record);
            return;
        }

        if (table.LastWasReset && !table.Degraded)
        {
            Reject(subscription, FaultCode.ChecksumMismatch, table.Version); // a resync can't fix a bad snapshot
        }
        else if (!table.ResyncSent && !table.Degraded)
        {
            table.ResyncSent = true;
            Resync(subscription);
        }
    }

    /// <summary>A watch's coherent cut: every member record applied, then its handler told once.</summary>
    private void ApplyGroup(Subscription subscription, ReadOnlySpan<byte> record)
    {
        var flags = record[5];
        int count = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        var group = subscription.Group ??= new GroupReplica();
        if ((flags & Wire.FlagReset) != 0)
        {
            group.Clear();
            group.ResyncSent = false;
        }

        group.Degraded = (flags & Wire.FlagDegraded) != 0;
        bool ok = true, badSnapshot = false;
        var members = record[Wire.GroupHeader..];
        for (int i = 0, at = 0; i < count; i++)
        {
            var member = members[at..];
            var length = member.Length > 0 && member[0] is Wire.Table or Wire.Value ? LengthOf(member) : -1;
            if (length < 0)
            {
                Raise(Malformed, $"A record inside watch {subscription.TapId}'s group this client can't read.");
                ok = false;
                break;
            }

            var index = (int)BinaryPrimitives.ReadUInt32LittleEndian(member[1..]);
            if (member[0] == Wire.Table)
            {
                var table = group.TableFor(index);
                if (!table.Apply(member[..length]))
                {
                    ok = false;
                    badSnapshot |= table.LastWasReset && !table.Degraded;
                }
            }
            else
            {
                group.SetValue(index, member.Slice(Wire.ValueHeader, length - Wire.ValueHeader).ToArray());
            }

            at += length;
        }

        if (ok)
        {
            NotifyGroup(subscription, group);
            return;
        }

        if (badSnapshot)
        {
            Reject(subscription, FaultCode.ChecksumMismatch, -1);
        }
        else if (!group.ResyncSent && !group.Degraded)
        {
            group.ResyncSent = true;
            Resync(subscription);
        }
    }

    private void ApplyMessage(Subscription subscription, ReadOnlySpan<byte> record)
    {
        var flags = record[5];
        var info = new MessageInfo(BinaryPrimitives.ReadInt64LittleEndian(record[6..]), (flags & Wire.FlagGap) != 0,
            (flags & Wire.FlagMailbox) != 0,
            (flags & Wire.FlagStale) != 0, ProviderOf(subscription));
        var message = record.Slice(Wire.TopicMessageHeader, (int)BinaryPrimitives.ReadUInt32LittleEndian(record[14..]));
        try
        {
            subscription.Handler.OnMessage(subscription, message, info);
        }
        catch (Exception e)
        {
            Failed(subscription, e);
        }
    }

    private void ApplyService(byte status, ReadOnlySpan<byte> payload)
    {
        if (status == Wire.ServiceUp)
        {
            var (correlation, service) = (new Guid(payload[..16], true), Encoding.UTF8.GetString(payload[16..]));
            _waitingFor.Remove(service);
            _lastNotice[service] = correlation;
            Raise(ServiceUp, service, correlation);
            return;
        }

        var node = new Guid(payload[..16], true);
        var id = new Guid(payload.Slice(16, 16), true);
        switch (status)
        {
            case Wire.ServiceDown or Wire.ServiceDrained:
            {
                var down = status == Wire.ServiceDown;
                if (!(down ? _servicesDown : _servicesDrained).Add(node))
                    return; // said already (resent until acknowledged)
                NodeChanged(node, down ? ProviderState.Down : ProviderState.Drained, id);
                foreach (var through in _subscriptions.Values.Where(s => s.Binding.NodeId == node).ToList())
                    ServiceEnded(through,
                        down
                            ? FaultCode.ServiceDown
                            : FaultCode.ServiceDrained); // (those its error records ended are gone already)
                Raise(down ? ServiceDown : ServiceDrained, node, id);
                break;
            }
            case Wire.ServiceDegraded or Wire.ServiceRecovered:
            {
                var degraded = status == Wire.ServiceDegraded;
                if (payload.Length == 32) // the node
                {
                    if (!(degraded ? _degraded.Add(node) : _degraded.Remove(node)))
                        return; // as it was (resent until acknowledged)
                    NodeChanged(node, degraded ? ProviderState.Degraded : ProviderState.Serving, id);
                    break;
                }

                var scope = Encoding.UTF8.GetString(payload[32..]); // a service path there
                if (!_degradedScopes.TryGetValue(node, out var scopes)) _degradedScopes[node] = scopes = [];
                if (degraded == scopes.Contains(scope)) return;
                if (degraded) scopes.Add(scope);
                else if (scopes.Remove(scope) && scopes.Count == 0) _degradedScopes.Remove(node);
                _lastNotice[ServiceOf(scope)] = id;
                Raise(ProviderStateChanged,
                    new ProviderChange(node, degraded ? ProviderState.Degraded : ProviderState.Serving, scope, id));
                break;
            }
            case Wire.ServiceDraining:
            {
                if (!_draining.Add(node)) return;
                NodeChanged(node, ProviderState.Draining, id);
                Raise(ServiceDraining, node,
                    TimeSpan.FromMilliseconds(BinaryPrimitives.ReadUInt32LittleEndian(payload[32..])), id);
                break;
            }
            case Wire.ServiceMove:
            {
                var tap = BinaryPrimitives.ReadUInt32LittleEndian(payload[36..]);
                if (!_subscriptions.TryGetValue(tap, out var moving) || moving.IsEnded || moving.MovedBy == id)
                    return; // not held any more, or said already (resent until acknowledged)
                moving.MovedBy = id;
                Raise(SubscriptionMoving, moving,
                    TimeSpan.FromMilliseconds(BinaryPrimitives.ReadUInt32LittleEndian(payload[32..])), id);
                break;
            }
        }
    }

    /// <summary>A node's state changed: its services' last notice noted, the change raised.</summary>
    private void NodeChanged(Guid node, ProviderState state, Guid correlation)
    {
        foreach (var s in _subscriptions.Values)
            if (s.Binding.NodeId == node && s.Binding.Service is { } service)
                _lastNotice[service] = correlation;
        Raise(ProviderStateChanged, new ProviderChange(node, StateOf(node), null, correlation));
    }

    /// <summary>A node's own state: down or drained (over), draining, degraded — or serving.</summary>
    private ProviderState StateOf(Guid node)
    {
        return _servicesDown.Contains(node) ? ProviderState.Down
            : _servicesDrained.Contains(node) ? ProviderState.Drained
            : _draining.Contains(node) ? ProviderState.Draining
            : _degraded.Contains(node) ? ProviderState.Degraded : ProviderState.Serving;
    }

    private static string ServiceOf(string scope)
    {
        return scope.IndexOf('/') is var slash and >= 0 ? scope[..slash] : scope;
    }

    /// <summary>Where a subscription is, as the router shows it: its service, then its partition's ids.</summary>
    private static string PathOf(TapBinding binding)
    {
        return binding.Partition is { } partition ? binding.Service + "/" + partition : binding.Service ?? "";
    }

    /// <summary>Whether a scope covers a path: the same, or an ancestor (segment by segment).</summary>
    private static bool Covers(string scope, string path)
    {
        return path.StartsWith(scope, StringComparison.Ordinal) &&
               (path.Length == scope.Length || path[scope.Length] == '/');
    }

    /// <summary>
    ///     A transparent out-of-band record: fetched off the receive thread, checked, applied in place and confirmed — the
    ///     router holds its tap meanwhile. A fetch overtaken by a later record of the tap, a reconnect or its end is dropped.
    /// </summary>
    private void FetchRecord(Subscription subscription, byte inner, BlobRef blob, uint ticket, long generation)
    {
        var epoch = _epoch;
        var fetch = FetchRecordAsync(subscription, inner, blob, ticket, generation, epoch);
        _inflight.Add(fetch);
        fetch.ContinueWith(done =>
        {
            lock (_gate)
            {
                _inflight.Remove(done);
            }
        }, TaskScheduler.Default);
    }

    private async Task FetchRecordAsync(Subscription subscription, byte inner, BlobRef blob, uint ticket,
        long generation, long epoch)
    {
        await Task.Yield(); // off the receive thread (and its lock)
        byte[] bytes;
        try
        {
            bytes = await blob.FetchAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            lock (_gate)
            {
                if (!Current(subscription, generation, epoch)) return;
                if (++subscription.FetchFailures >= _options.FetchAttempts) Reject(subscription, FaultCode.Unknown, -1);
                else Resync(subscription); // a fresh snapshot, with a fresh handle
            }

            return;
        }

        lock (_gate)
        {
            if (!Current(subscription, generation, epoch))
                return; // dropped, failed, reconnected or overtaken meanwhile: stale
            subscription.FetchFailures = 0;
            switch (inner)
            {
                case Wire.Value:
                    (subscription.Value, subscription.Blob) = (bytes, null);
                    NotifyValue(subscription);
                    break;
                case Wire.Table when TableReplica.LengthOf(bytes) == bytes.Length:
                    ApplyTable(subscription, bytes);
                    break;
                case Wire.Group when LengthOf(bytes) == bytes.Length:
                    ApplyGroup(subscription, bytes);
                    break;
                case Wire.TopicMessage when LengthOf(bytes) == bytes.Length:
                    ApplyMessage(subscription, bytes);
                    break;
                default:
                    Reject(subscription, FaultCode.Unknown, -1);
                    return;
            }

            if (subscription.IsEnded) return; // its record didn't check out: it failed
            if (_bound) _connection.Send(ControlMessage.Fetched(subscription.TapId, ticket));
            _confirmed[subscription.TapId] = ticket;
        }
    }

    private void Resync(Subscription subscription)
    {
        Volatile.Write(ref _resyncs, _resyncs + 1);
        if (_bound) _connection.Send(ControlMessage.Resync(subscription.TapId));
    }

    private bool Current(Subscription subscription, long generation, long epoch)
    {
        return _epoch == epoch && subscription.Generation == generation && !subscription.IsEnded;
    }

    internal Task<byte[]> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        return _options.Fetch is { } fetch
            ? fetch(url, cancellationToken)
            : (_options.Http ?? SharedHttp.Value).GetByteArrayAsync(url, cancellationToken);
    }

    /// <summary>The state of the provider a subscription comes through — null for one bound directly.</summary>
    internal ProviderState? ProviderOf(Subscription subscription)
    {
        lock (_gate)
        {
            return ProviderOfLocked(subscription);
        }
    }

    /// <summary>
    ///     As <see cref="ProviderOf" />, under the lock: unreachable; else its node's state; serving, degraded if a
    ///     degraded scope there covers it.
    /// </summary>
    private ProviderState? ProviderOfLocked(Subscription subscription)
    {
        if (subscription.Binding.NodeId is not { } node) return null;
        if (!_reachable && _bound) return ProviderState.Unreachable;
        var state = StateOf(node);
        if (state != ProviderState.Serving || !_degradedScopes.TryGetValue(node, out var scopes)) return state;
        var path = PathOf(subscription.Binding);
        foreach (var scope in scopes)
            if (Covers(scope, path))
                return ProviderState.Degraded;
        return state;
    }

    /// <summary>
    ///     A service's health, for <see cref="Tapline.Client.ServiceHealthCheck" />: over its subscriptions, as this
    ///     client sees them.
    /// </summary>
    internal (HealthStatus Status, string Description, Dictionary<string, object> Data) ServiceHealth(string service)
    {
        lock (_gate)
        {
            int subscriptions = 0, degraded = 0, draining = 0;
            var nodes = new SortedSet<Guid>();
            var scopes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var s in _subscriptions.Values)
            {
                if (s.Binding.Service != service || s.IsEnded) continue;
                subscriptions++;
                if (s.Binding.NodeId is { } node)
                {
                    nodes.Add(node);
                    if (_degradedScopes.TryGetValue(node, out var degradedThere))
                        foreach (var scope in degradedThere)
                            if (Covers(scope, PathOf(s.Binding)))
                                scopes.Add(scope);
                }

                switch (ProviderOfLocked(s))
                {
                    case ProviderState.Degraded: degraded++; break;
                    case ProviderState.Draining: draining++; break;
                }
            }

            bool unreachable = !_reachable && _bound,
                waiting = _waitingFor.Contains(service),
                moving = _moving.Contains(service);
            var data = new Dictionary<string, object>
            {
                ["service"] = service, ["subscriptions"] = subscriptions, ["degraded"] = degraded,
                ["draining"] = draining, ["waitingForUp"] = waiting,
                ["moving"] = moving, ["nodes"] = nodes.ToArray(), ["degradedScopes"] = scopes.ToArray()
            };
            if (_lastNotice.TryGetValue(service, out var correlation)) data["correlation"] = correlation;
            var (status, description) = unreachable ? (HealthStatus.Unhealthy, "the router is out of reach")
                : waiting ? (HealthStatus.Unhealthy, $"{service} is down: waiting for it to be up")
                : degraded > 0 ? (HealthStatus.Degraded,
                    $"{degraded} of {subscriptions} subscriptions to {service} shown stale data")
                : draining > 0 ? (HealthStatus.Degraded,
                    $"{draining} of {subscriptions} subscriptions to {service} on draining nodes: moving")
                : moving ? (HealthStatus.Degraded, $"a drain let subscriptions to {service} go: to subscribe again")
                : (HealthStatus.Healthy, $"{service}: {subscriptions} subscriptions streaming");
            return (status, description, data);
        }
    }

    private void SetReachable(bool reachable)
    {
        if (_reachable == reachable) return;
        Volatile.Write(ref _reachable, reachable);
        Raise(ReachabilityChanged, reachable);
    }

    private void NotifyTable(Subscription subscription, TableReplica table, ReadOnlySpan<byte> record)
    {
        Switched(subscription);
        try
        {
            subscription.Handler.OnTable(subscription, table, new TableBatch(record));
        }
        catch (Exception e)
        {
            Failed(subscription, e);
        }
    }

    private void NotifyValue(Subscription subscription)
    {
        Switched(subscription);
        Handle(subscription, static s => s.Handler.OnValue(s, s.Value));
    }

    private void NotifyGroup(Subscription subscription, GroupReplica group)
    {
        Switched(subscription);
        try
        {
            subscription.Handler.OnGroup(subscription, group);
        }
        catch (Exception e)
        {
            Failed(subscription, e);
        }
    }

    /// <summary>Make before break: a replacement's first state drops the subscription it replaces.</summary>
    private void Switched(Subscription subscription)
    {
        if (subscription.Replaces is not { } old) return;
        subscription.Replaces = null;
        Drop(old);
    }

    private void Handle(Subscription subscription, Action<Subscription> handler)
    {
        try
        {
            handler(subscription);
        }
        catch (Exception e)
        {
            Failed(subscription, e);
        }
    }

    /// <summary>Ends a subscription with an error: its state forgotten, then its handler told once.</summary>
    private void Fail(Subscription subscription, TapError error)
    {
        if (subscription.Binding.Service is
            { } service) // its node gone, or a drain let it go: the service waits, or moves
            if (error.Code == FaultCode.ServiceDown) _waitingFor.Add(service);
            else if (error.Code == FaultCode.ServiceDrained) _moving.Add(service);
        Forget(subscription);
        EndRendezvous(subscription.TapId, error.Code);
        Handle(subscription, s => s.Handler.OnError(s, error));
    }

    /// <summary>
    ///     A subscription whose provider instance is down or drained, or that the router ended: over (the router closed
    ///     it already).
    /// </summary>
    private void ServiceEnded(Subscription subscription, FaultCode code)
    {
        Fail(subscription, new TapError(code, 0, ReadOnlyMemory<byte>.Empty, false));
    }

    /// <summary>This client gives up on a stream the router thinks healthy: it tells the router why, then fails it.</summary>
    private void Reject(Subscription subscription, FaultCode code, long version)
    {
        if (_bound) _connection.Send(ControlMessage.Close(subscription.TapId, code));
        Fail(subscription, new TapError(code, version, ReadOnlyMemory<byte>.Empty, true));
    }

    private void Forget(Subscription subscription)
    {
        _subscriptions.Remove(subscription.TapId);
        _confirmed.Remove(subscription.TapId);
        subscription.IsEnded = true;
    }

    /// <summary>Fails the rendezvous awaited through a subscription that ended.</summary>
    private void EndRendezvous(uint tapId, FaultCode code)
    {
        foreach (var (key, wait) in _rendezvous.Where(w => w.Value.TapId == tapId).ToList())
        {
            _rendezvous.Remove(key);
            wait.Cancellation.Dispose();
            wait.Source.TrySetException(new SubscriptionEndedException(tapId, code));
        }
    }

    private double RateAt(long now)
    {
        return Volatile.Read(ref _rateAt) is var at && at != 0
            ? Volatile.Read(ref _rate) * Math.Exp(-_time.GetElapsedTime(at, now).TotalMilliseconds / RateTauMs)
            : 0;
    }

    private void Raise<T>(Action<T>? handler, T argument)
    {
        try
        {
            handler?.Invoke(argument);
        }
        catch (Exception e)
        {
            Failed(null, e);
        }
    }

    private void Raise<T1, T2>(Action<T1, T2>? handler, T1 first, T2 second)
    {
        try
        {
            handler?.Invoke(first, second);
        }
        catch (Exception e)
        {
            Failed(null, e);
        }
    }

    private void Raise<T1, T2, T3>(Action<T1, T2, T3>? handler, T1 first, T2 second, T3 third)
    {
        try
        {
            handler?.Invoke(first, second, third);
        }
        catch (Exception e)
        {
            Failed(null, e);
        }
    }

    /// <summary>A handler threw: said (<see cref="HandlerFailed" />) — and if that throws too, nothing more.</summary>
    private void Failed(Subscription? subscription, Exception exception)
    {
        try
        {
            HandlerFailed?.Invoke(subscription, exception);
        }
        catch (Exception)
        {
        }
    }

    private sealed class RendezvousWait(uint? tapId)
    {
        public readonly TaskCompletionSource<RendezvousInfo> Source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly uint? TapId = tapId;
        public CancellationTokenRegistration Cancellation;
    }
}