using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tapline.MagicOnion.Providers;

/// <summary>
///     A data provider serving routers in other processes. Its topics are this process's, and this process's router runs
///     them: the router the routers' Tapline streams reach. Each router that reaches it gets a link, kept across the
///     router's control streams:
///     <list type="bullet">
///         <item>its step 1s answered by the provider;</item>
///         <item>
///             each topic accepted onto bound once, on the router's session here, however many of its routes share it;
///         </item>
///         <item>its routes' notices relayed, and its health checks answered;</item>
///         <item>the provider's heartbeats and drains, and its subscribers' mailboxes, carried back to it.</item>
///     </list>
/// </summary>
public sealed class ProviderLinks
{
    private readonly Dictionary<Guid, ProviderLink> _links = [];
    private readonly Lock _lock = new();
    private readonly Dictionary<Source, int> _routes = new(ReferenceEqualityComparer.Instance);
    private readonly Func<int, uint, Outlet?> _session;

    /// <param name="provider">The provider: its step 1s, notices and health, as in process.</param>
    /// <param name="endpoint">Its endpoint: the address the routers reach it at.</param>
    /// <param name="session">A router's session here, from its hello: the outlet its Tapline stream is bound to.</param>
    public ProviderLinks(IDataProvider provider, ProviderEndpoint endpoint, Func<int, uint, Outlet?> session,
        ILogger? log = null)
    {
        (Provider, Endpoint, _session, Log) = (provider, endpoint, session, log ?? NullLogger.Instance);
    }

    internal IDataProvider Provider { get; }
    internal ProviderEndpoint Endpoint { get; }
    internal ILogger Log { get; }

    /// <summary>The routers linked: those that have said hello, each by its id.</summary>
    public int Routers
    {
        get
        {
            lock (_lock)
            {
                return _links.Count;
            }
        }
    }

    /// <summary>
    ///     A router's control stream, served until it ends: its hello first, which names the router and its session here;
    ///     then its messages, each handed to its link.
    /// </summary>
    public async Task ServeAsync(IControlStream<ProviderMessage, RouterMessage> control,
        CancellationToken cancellationToken)
    {
        ProviderLink? link = null;
        try
        {
            await foreach (var message in control.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                if (link is not null)
                {
                    link.Receive(message);
                }
                else
                {
                    if (message is not RouterHello hello)
                        throw new InvalidOperationException("A control stream starts with the router's hello.");
                    link = await AttachAsync(hello, control, cancellationToken).ConfigureAwait(false);
                }
        }
        catch (Exception e) when (cancellationToken.IsCancellationRequested || e is IOException)
        {
        } // (the call ended)
        finally
        {
            link?.Detach(control);
        }
    }

    private async Task<ProviderLink> AttachAsync(RouterHello hello,
        IControlStream<ProviderMessage, RouterMessage> control, CancellationToken cancellationToken)
    {
        var outlet = _session(hello.Outlet, hello.Nonce) ??
                     throw new UnknownSessionException(
                         $"No session {hello.Outlet}.{hello.Nonce} here: the router's Tapline stream binds it first.");
        ProviderLink? link;
        bool made;
        lock (_lock)
        {
            made = !_links.TryGetValue(hello.Id, out link);
            if (made) _links[hello.Id] = link = new ProviderLink(this, new RouterInstance(hello.Name, hello.Id));
        }

        link!.Attach(control, outlet);
        if (made) await Provider.ConnectAsync(link, cancellationToken).ConfigureAwait(false);
        return link;
    }

    /// <summary>A topic's routes at every router, changed by <paramref name="change" />: what's left of them now.</summary>
    internal int Count(Source topic, int change)
    {
        lock (_lock)
        {
            _routes.TryGetValue(topic, out var n);
            n = Math.Max(0, n + change);
            if (n == 0) _routes.Remove(topic);
            else _routes[topic] = n;
            return n;
        }
    }
}

/// <summary>
///     A router's hello naming a session this provider's router doesn't hold: its Tapline stream binds one first (or bound
///     it to a process before this one). The control stream ends saying so, and the router opens it again once it has one.
/// </summary>
internal sealed class UnknownSessionException(string message) : InvalidOperationException(message);

/// <summary>
///     One router's link, the provider's side: its routes, by the request ids its step 1s came with, and the topics they're
///     on (one tap each, on the router's session here); what's sent to the router waits in order for its control stream.
/// </summary>
internal sealed class ProviderLink : IRemoteStep, IRouterChannel
{
    private readonly Dictionary<long, TaskCompletionSource<BeatAnswered>> _beats = [];
    private readonly Dictionary<long, TaskCompletionSource<DrainAnswered>> _drains = [];
    private readonly Dictionary<long, TaskCompletionSource<MoveAnswered>> _moves = [];
    private readonly ProviderLinks _links;
    private readonly Lock _lock = new();
    private readonly Channel<RouterMessage> _notices = Channel.CreateUnbounded<RouterMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<ProviderMessage> _out = Channel.CreateUnbounded<ProviderMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<Source, SharedTap> _shared = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, Step> _steps = [];
    private IControlStream<ProviderMessage, RouterMessage>? _control;
    private long _next;
    private Outlet? _outlet;
    private Task _writing = Task.CompletedTask;
    private CancellationTokenSource? _writer;

    public ProviderLink(ProviderLinks links, RouterInstance router)
    {
        (_links, Router) = (links, router);
        _ = NotifyAsync(); // the routes' notices, one at a time, in the order the router sent them
    }

    public RouterInstance Router { get; }

    public async ValueTask<RouterBeat> HeartbeatAsync(ProviderInstance instance, RouterInstance expected,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _next);
        var answered = new TaskCompletionSource<BeatAnswered>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _beats[id] = answered;
        }

        try
        {
            if (!Send(new BeatAsked
                {
                    Beat = id, InstanceName = instance.Name, InstanceEndpoint = instance.Endpoint.ToString(),
                    InstanceId = instance.Id, ExpectedName = expected.Name, ExpectedId = expected.Id
                }))
                throw new InvalidOperationException($"The control stream of router {Router} is down.");
            var a = await answered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (a.Error is { } error) throw new InvalidOperationException(error);
            return new RouterBeat(new RouterInstance(a.RouterName, a.RouterId), a.KnowsInstance);
        }
        finally
        {
            lock (_lock)
            {
                _beats.Remove(id);
            }
        }
    }

    public async ValueTask DrainAsync(ProviderInstance instance, TimeSpan over, TimeSpan grace,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _next);
        var answered = new TaskCompletionSource<DrainAnswered>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _drains[id] = answered;
        }

        try
        {
            if (!Send(new DrainAsked
                {
                    Drain = id, InstanceName = instance.Name, InstanceEndpoint = instance.Endpoint.ToString(),
                    InstanceId = instance.Id, OverTicks = over.Ticks, GraceTicks = grace.Ticks
                }))
                throw new InvalidOperationException($"The control stream of router {Router} is down.");
            var a = await answered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (a.Error is { } error) throw new InvalidOperationException(error);
        }
        finally
        {
            lock (_lock)
            {
                _drains.Remove(id);
            }
        }
    }

    public async ValueTask<MoveAnswer> MoveAsync(ProviderInstance instance, string? partition, RouteId? route,
        TimeSpan over, TimeSpan grace, string reason, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _next);
        var answered = new TaskCompletionSource<MoveAnswered>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _moves[id] = answered;
        }

        try
        {
            if (!Send(new MoveAsked
                {
                    Move = id, InstanceName = instance.Name, InstanceEndpoint = instance.Endpoint.ToString(),
                    InstanceId = instance.Id, Partition = partition, RouteOutlet = route?.Outlet,
                    RouteTap = route?.Tap ?? 0, OverTicks = over.Ticks, GraceTicks = grace.Ticks, Reason = reason
                }))
                throw new InvalidOperationException($"The control stream of router {Router} is down.");
            var a = await answered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (a.Error is { } error) throw new InvalidOperationException(error);
            return new MoveAnswer(a.Routes, a.Correlation, a.Refused);
        }
        finally
        {
            lock (_lock)
            {
                _moves.Remove(id);
            }
        }
    }

    /// <summary>
    ///     A step 1's accept: the topic bound on the router's session here — once, however many of its routes it carries —
    ///     and the answer that names it made ready. Under the request's lock.
    /// </summary>
    public long Accept(SubscribeRequest request, Source topic, ProviderInstance instance, string name,
        InitialState initial, ErrorPolicy? errorPolicy, PayloadFormat? format, string? partition)
    {
        var kind = topic.Describe().Kind;
        if (kind is not ("table" or "value"))
            throw new NotSupportedException(
                $"A topic served to a router in another process is a table or a value (as yet), not a {kind}.");
        if (errorPolicy is { Kind: StaleData.Function, Degrade: not null })
            throw new NotSupportedException(
                "A stale-data function can't cross to a router in another process (as yet): use ErrorPolicy.Static or ErrorPolicy.ReplayLast.");
        var id = (long)request.Route.Tap;
        Tap? bound = null;
        try
        {
            lock (_lock)
            {
                var step = _steps[id];
                if (!_shared.TryGetValue(topic, out var shared))
                {
                    var outlet = _outlet ??
                                 throw new InvalidOperationException($"Router {Router}'s session here is gone.");
                    bound = outlet.Tap(topic, name); // (the topic's own state first: the router's copy starts from it)
                    _shared[topic] = shared = new SharedTap(bound);
                    bound = null;
                }

                shared.Routes++;
                var layout = topic.Layout;
                (step.Topic, step.Shared, step.Instance, step.Epoch) = (topic, shared, instance, topic.Epoch);
                step.Answer = new StepAnswered
                {
                    Request = id, Outcome = StepOutcome.Accepted, Topic = topic.Id, Tap = shared.Tap.Id,
                    Kind = kind, KeySize = layout.Key, RowSize = layout.Row, Name = name,
                    Initial = (byte)initial, Stale = errorPolicy is null ? null : (byte)errorPolicy.Kind,
                    StalePayload = errorPolicy?.Payload.ToArray(), StaleState = errorPolicy?.State.ToArray(),
                    Format = Json(format ?? topic.Format), Partition = partition, InstanceName = instance.Name,
                    InstanceEndpoint = instance.Endpoint.ToString(), InstanceId = instance.Id
                };
                _links.Count(topic, +1);
                return step.Epoch;
            }
        }
        finally
        {
            bound?.Dispose(); // (bound, then refused: let go)
        }
    }

    /// <summary>A control stream for this router: the messages waiting for it go now, in order.</summary>
    public void Attach(IControlStream<ProviderMessage, RouterMessage> control, Outlet outlet)
    {
        lock (_lock)
        {
            _writer?.Cancel();
            var writer = _writer = new CancellationTokenSource();
            (_control, _outlet) = (control, outlet);
            var before = _writing;
            _writing = Task.Run(async () =>
            {
                await before.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // (one writer at a time)
                await WriteAsync(control, writer.Token).ConfigureAwait(false);
            });
        }
    }

    /// <summary>The control stream ended: what's sent to the router waits for the next.</summary>
    public void Detach(IControlStream<ProviderMessage, RouterMessage> control)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_control, control)) return;
            _control = null;
            _writer?.Cancel();
        }
    }

    /// <summary>One of the router's messages: a step 1 begun, a notice queued (in order), an answer handed over.</summary>
    public void Receive(RouterMessage message)
    {
        switch (message)
        {
            case SubscribeAsked asked:
                _ = StepAsync(asked);
                break;
            case AccessAsked access:
                _ = AccessAsync(access);
                break;
            case AuthAsked auth:
                _ = AuthenticateAsync(auth);
                break;
            case StepAbandoned or RouteActivated or RouteRemoved or RouteIdentity:
                _notices.Writer.TryWrite(message);
                break;
            case HealthAsked probe:
                _ = HealthAsync(probe);
                break;
            case BeatAnswered beat:
                TaskCompletionSource<BeatAnswered>? b;
                lock (_lock)
                {
                    _beats.TryGetValue(beat.Beat, out b);
                }

                b?.TrySetResult(beat);
                break;
            case DrainAnswered drain:
                TaskCompletionSource<DrainAnswered>? d;
                lock (_lock)
                {
                    _drains.TryGetValue(drain.Drain, out d);
                }

                d?.TrySetResult(drain);
                break;
            case MoveAnswered move:
                TaskCompletionSource<MoveAnswered>? m;
                lock (_lock)
                {
                    _moves.TryGetValue(move.Move, out m);
                }

                m?.TrySetResult(move);
                break;
        }
    }

    /// <summary>A mailbox operation for the router's subscriber, after the topic's state at <paramref name="version" />.</summary>
    public bool Mail(Step step, MailOp op, ReadOnlySpan<byte> bytes, long version)
    {
        return Send(new MailRelayed { Request = step.Id, Op = op, Bytes = bytes.IsEmpty ? null : bytes.ToArray(), Version = version });
    }

    /// <summary>What the provider declares of a service, asked by the router (once a service, till the link reconnects); sent back.</summary>
    private async Task AccessAsync(AccessAsked asked)
    {
        AccessAnswered answer;
        try
        {
            var access = await _links.Provider.AccessAsync(asked.Service, CancellationToken.None).ConfigureAwait(false);
            answer = new AccessAnswered { Ask = asked.Ask, Realm = access.Realm, Required = access.Required };
        }
        catch (Exception e)
        {
            _links.Log.LogWarning(e, "Provider link: what '{Service}' requires couldn't be said to router {Router}",
                asked.Service, Router);
            answer = new AccessAnswered { Ask = asked.Ask, Error = e.Message };
        }

        Send(answer);
    }

    /// <summary>One message of a sign-in, relayed by the router: the provider's answer (an authentication provider's) sent back.</summary>
    private async Task AuthenticateAsync(AuthAsked asked)
    {
        AuthAnswered answer;
        try
        {
            if (_links.Provider is not IAuthenticator authenticator)
                throw new InvalidOperationException("This provider signs no one in.");
            var step = new AuthStep(Router, asked.Exchange, asked.Realm, asked.Module, asked.Round, asked.Payload ?? [],
                asked.Context ?? [], asked.Current?.ToIdentity(), asked.Renewal);
            var a = await authenticator.AuthenticateAsync(step, CancellationToken.None).ConfigureAwait(false);
            answer = new AuthAnswered
            {
                Ask = asked.Ask, Outcome = a.Outcome, Payload = a.Payload.IsEmpty ? null : a.Payload.ToArray(),
                Identity = IdentityCarried.Of(a.Identity), Reason = a.Reason,
                Renewal = a.Policy?.Renewal ?? RenewalPolicy.Reauthenticate, Warning = a.Policy?.Warning,
                PolicyReason = a.Policy?.Reason
            };
        }
        catch (Exception e)
        {
            _links.Log.LogWarning(e, "Provider link: a sign-in to '{Realm}' couldn't be answered to router {Router}",
                asked.Realm, Router);
            answer = new AuthAnswered { Ask = asked.Ask, Error = e.Message };
        }

        Send(answer);
    }

    /// <summary>Step 1, answered by the provider: the request it accepted, refused or let be; its answer sent back.</summary>
    private async Task StepAsync(SubscribeAsked asked)
    {
        var step = new Step(asked.Request);
        var access = asked.AccessRealm is not { } realm ? ServiceAccess.Open
            : asked.AccessRequired ? ServiceAccess.RequiredIn(realm) : ServiceAccess.In(realm);
        var request = new SubscribeRequest(this, _links.Endpoint, asked.Service, asked.Parameters,
            new RouteId(Volatile.Read(ref _outlet)?.Id ?? 0, (uint)asked.Request), new Mailbox(new RouteMailbox(this, step)),
            access, asked.Identity?.ToIdentity()) { Limits = asked.Limits?.ToLimits() ?? default };
        step.Request = request;
        lock (_lock)
        {
            _steps[asked.Request] = step;
        }

        StepAnswered answer;
        try
        {
            await _links.Provider.SubscribeAsync(request, CancellationToken.None).ConfigureAwait(false);
            request.Answered();
            answer = step.Answer ?? (request.Nacked
                ? new StepAnswered { Request = asked.Request, Outcome = StepOutcome.Nacked }
                : request.Refusal is { } refusal
                    ? new StepAnswered
                    {
                        Request = asked.Request, Outcome = StepOutcome.Refused, Correlation = refusal.CorrelationId,
                        Reason = refusal.Reason, RefusalKind = (byte)refusal.Kind
                    }
                    : new StepAnswered { Request = asked.Request, Outcome = StepOutcome.None });
        }
        catch (Exception e)
        {
            request.Answered();
            _links.Log.LogWarning(e, "Provider link: step 1 for router {Router} ({Service}) failed", Router, asked.Service);
            answer = new StepAnswered { Request = asked.Request, Outcome = StepOutcome.Failed, Reason = e.Message };
        }

        bool gone;
        lock (_lock)
        {
            step.Done = true;
            gone = step.Gone || answer.Outcome != StepOutcome.Accepted;
        }

        Send(answer);
        if (gone) await RemoveAsync(step, answer.Outcome == StepOutcome.Accepted).ConfigureAwait(false);
    }

    /// <summary>The routes' notices, relayed to the provider one at a time, in the order the router sent them.</summary>
    private async Task NotifyAsync()
    {
        await foreach (var notice in _notices.Reader.ReadAllAsync().ConfigureAwait(false))
            try
            {
                switch (notice)
                {
                    case RouteActivated activated:
                        await ActivateAsync(activated.Request).ConfigureAwait(false);
                        break;
                    case StepAbandoned abandoned:
                        await GoneAsync(abandoned.Request, true).ConfigureAwait(false);
                        break;
                    case RouteIdentity changed:
                        await IdentityChangedAsync(changed).ConfigureAwait(false);
                        break;
                    case RouteRemoved removed:
                        await GoneAsync(removed.Request, !removed.Quiet).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception e)
            {
                _links.Log.LogWarning(e, "Provider link: a notice for router {Router} failed", Router);
            }
    }

    private async Task IdentityChangedAsync(RouteIdentity changed)
    {
        Step? step;
        lock (_lock)
        {
            if (!_steps.TryGetValue(changed.Request, out step) || step.Topic is null) return;
        }

        var request = step.Request!;
        var identity = changed.Identity?.ToIdentity();
        await _links.Provider.IdentityChangedAsync(new IdentityChange(Router, request.Route, step.Instance!,
            request.Service, request.Parameters, step.Topic, identity, changed.Lapsed), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task ActivateAsync(long id)
    {
        Step? step;
        lock (_lock)
        {
            if (!_steps.TryGetValue(id, out step) || step.Topic is null) return;
        }

        var request = step.Request!;
        await _links.Provider.ActivateAsync(new Activation(Router, request.Route, step.Instance!, request.Service,
            request.Parameters, step.Topic, step.Epoch, request.Mailbox), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     A route gone at the router, or its step 1 abandoned there: let go here, once its step 1 is over — the provider
    ///     told, unless <paramref name="tell" /> is false (gone as it isn't told in process).
    /// </summary>
    private async Task GoneAsync(long id, bool tell)
    {
        Step? step;
        lock (_lock)
        {
            if (!_steps.TryGetValue(id, out step)) return;
            if (!step.Done)
            {
                step.Gone = true; // (its step 1 lets it go when it's over, and tells)
                return;
            }
        }

        await RemoveAsync(step, tell).ConfigureAwait(false);
    }

    /// <summary>
    ///     A route let go: its topic's tap for this router too, if it was the last on it; the provider told, with the topic's
    ///     epoch at its accept and the routes left on it at every router.
    /// </summary>
    private async Task RemoveAsync(Step step, bool tell)
    {
        Tap? release = null;
        int remaining;
        lock (_lock)
        {
            if (!_steps.Remove(step.Id) || step.Topic is not { } topic || step.Shared is not { } shared) return;
            if (--shared.Routes == 0)
            {
                _shared.Remove(topic);
                release = shared.Tap;
            }

            remaining = _links.Count(topic, -1);
        }

        release?.Dispose(); // (before the provider's told: its next publish knows)
        if (!tell) return;
        var request = step.Request!;
        await _links.Provider.UnsubscribeAsync(new Unsubscription(Router, request.Route, step.Instance!, request.Service,
            request.Parameters, step.Topic, step.Epoch, remaining), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HealthAsync(HealthAsked probe)
    {
        HealthAnswered answer;
        try
        {
            var expected = probe.ExpectedId is { } id
                ? Instances.Of(probe.ExpectedName, probe.ExpectedEndpoint, id)
                : null;
            var report = await _links.Provider.CheckHealthAsync(new HealthProbe(Router, expected), CancellationToken.None)
                .ConfigureAwait(false);
            answer = new HealthAnswered
            {
                Probe = probe.Probe, InstanceName = report.Instance.Name,
                InstanceEndpoint = report.Instance.Endpoint.ToString(), InstanceId = report.Instance.Id,
                Status = (byte)report.Status, RouterRefused = report.RouterRefused, DrainUntil = report.DrainUntil,
                DegradedPaths = report.DegradedPaths?.ToArray(), Load = report.Load
            };
        }
        catch (Exception e)
        {
            answer = new HealthAnswered { Probe = probe.Probe, Error = e.Message };
        }

        Send(answer);
    }

    /// <summary>A message for the router, queued in order: false if there's no control stream to send it on now.</summary>
    private bool Send(ProviderMessage message)
    {
        lock (_lock)
        {
            return _control is not null && _out.Writer.TryWrite(message);
        }
    }

    private async Task WriteAsync(IControlStream<ProviderMessage, RouterMessage> control,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await _out.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                while (_out.Reader.TryPeek(out var message))
                {
                    await control.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                    _out.Reader.TryRead(out _); // (written: the next; one that failed waits for the next stream)
                }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _links.Log.LogInformation("Provider link: writing to router {Router} failed ({Reason})", Router, e.Message);
        }
    }

    private static byte[]? Json(PayloadFormat? format)
    {
        if (format is null) return null;
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) format.WriteTo(w);
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>A topic's one tap on a router's session here, and the routes it carries for that router.</summary>
internal sealed class SharedTap(Tap tap)
{
    public readonly Tap Tap = tap;
    public int Routes;
}

/// <summary>A route's step 1 at the provider, and what became of it: the topic it's on, and that topic's epoch at its accept.</summary>
internal sealed class Step(long id)
{
    public readonly long Id = id;
    public StepAnswered? Answer;
    public bool Done, Gone;
    public long Epoch;
    public ProviderInstance? Instance;
    public SubscribeRequest? Request;
    public SharedTap? Shared;
    public Source? Topic;
}

/// <summary>
///     A route's mailbox, the provider's side: each operation relayed to the router with the version of the route's topic
///     it waits on — the topic's version now, unless it names another of the topic's.
/// </summary>
internal sealed class RouteMailbox(ProviderLink link, Step step) : IRemoteMailbox
{
    private Source Topic => step.Topic ??
                            throw new InvalidOperationException(
                                "Accept the subscription onto a topic first: the mailbox is its route's.");

    public bool TryPost(ReadOnlySpan<byte> message, After after)
    {
        return link.Mail(step, MailOp.Post, message, VersionOf(after));
    }

    public bool TryRendezvous(ReadOnlySpan<byte> correlation, long seq)
    {
        _ = Topic;
        return link.Mail(step, MailOp.Rendezvous, correlation, seq);
    }

    public bool TryRendezvous(ReadOnlySpan<byte> correlation, After after)
    {
        return link.Mail(step, MailOp.Rendezvous, correlation, VersionOf(after));
    }

    public void Send(ReadOnlySpan<byte> snapshot, long? asOf)
    {
        link.Mail(step, MailOp.Send, snapshot, asOf ?? Topic.Version);
    }

    public void Ready(long? through)
    {
        link.Mail(step, MailOp.Ready, default, through ?? Topic.Version);
    }

    private long VersionOf(After after)
    {
        var topic = Topic;
        if (after.Source is null && after.Tap is null) return topic.Version;
        if (ReferenceEquals(after.Source, topic) || ReferenceEquals(after.Tap?.Source, topic)) return after.Version;
        throw new NotSupportedException(
            "A mailbox at a router in another process waits only on its route's own topic.");
    }
}
