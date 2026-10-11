using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tapline.MagicOnion.Providers;

/// <summary>
///     A data provider in another process, as a router reaches it (<see cref="Publisher.Provide" />): each step 1 answered
///     there, and each topic it accepts onto mirrored into a copy here (<see cref="Twin" />) that the router binds its
///     routes to. The copies are fed by the link's Tapline stream, one subscription per topic however many routes are on
///     it; the routes' notices, the provider's health, heartbeats and drains, and its subscribers' mailboxes go on the
///     control stream beside it. A broken control stream is an unanswered health check here, as any provider's is:
///     degraded, then deemed dead if it stays broken. The stream is opened again meanwhile, and the routes carry on. While
///     it's down, or until it's first open, the provider isn't reached (<see cref="IReachableProvider" />): the router sends
///     its step 1s to another endpoint of the service, and probes this one once it's open.
/// </summary>
public sealed class RemoteProvider : IDataProvider, IAuthenticator, IReachableProvider, IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250), MaxRetryDelay = TimeSpan.FromSeconds(5);

    private readonly Dictionary<long, TaskCompletionSource<StepAnswered>> _asked = [];
    private readonly Dictionary<string, ServiceAccess> _access = []; // what the provider declared, by service: till the link breaks
    private readonly Dictionary<long, TaskCompletionSource<AccessAnswered>> _accessAsked = [];
    private readonly Dictionary<long, TaskCompletionSource<AuthAnswered>> _authAsked = [];
    private readonly HashSet<Guid> _draining = []; // instances that drained themselves: their routes let go untold
    private readonly Dictionary<long, Queue<MailRelayed>> _early = []; // mail for a route not yet bound here
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private readonly IProviderLink _link;
    private readonly Lock _lock = new();
    private readonly ILogger _log;
    private readonly Dictionary<long, TaskCompletionSource<HealthAnswered>> _probes = [];
    private readonly Dictionary<RouteId, long> _requests = []; // the router's route: its request's id on the link
    private readonly Dictionary<long, Route> _routes = [];
    private readonly Queue<RouterMessage> _owed = new(); // the routes' notices, until the provider has them
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<(Guid Instance, long Topic), Twin> _twins = [];
    private readonly SemaphoreSlim _writing = new(1, 1);
    private IControlStream<RouterMessage, ProviderMessage>? _control;
    private long _next;
    private IRouterChannel? _router;
    private Task? _running;
    private TaskCompletionSource _up = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="endpoint">The provider's endpoint: its address, the router's alone.</param>
    /// <param name="link">How it's reached: <see cref="MagicOnionProviderLink" />, say.</param>
    public RemoteProvider(ProviderEndpoint endpoint, IProviderLink link, ILogger? log = null)
    {
        (Endpoint, _link, _log) = (endpoint, link, log ?? NullLogger.Instance);
        // A copy that can't apply what the provider's router sends is a fault of the link's: said, not swallowed.
        link.Client.HandlerFailed += (subscription, e) => _log.LogError(e,
            "Provider link {Endpoint}: a copy failed to apply a record (tap {Tap}, {Topic})", Endpoint,
            subscription?.TapId, subscription?.Topic);
        link.Client.Malformed += what =>
            _log.LogError("Provider link {Endpoint}: a malformed record from the provider's router: {What}", Endpoint, what);
    }

    public ProviderEndpoint Endpoint { get; }

    /// <summary>Whether the control stream is open, the router's hello said.</summary>
    public bool IsUp => _up.Task.IsCompleted;

    bool IReachableProvider.Reachable => IsUp;

    /// <summary>Raised as the control stream opens, and as it breaks: the router's to know it's reached, or not.</summary>
    public event Action? ReachableChanged;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_running is { } running) await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    ValueTask IDataProvider.ConnectAsync(IRouterChannel router, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _router = router;
            _running ??= RunAsync(_stop.Token);
        }

        return default;
    }

    /// <summary>
    ///     What the provider declares of <paramref name="service" />: asked over the link the first time, then kept until the
    ///     link breaks (a provider whose declaration changes: once it's reconnected).
    /// </summary>
    async ValueTask<ServiceAccess> IDataProvider.AccessAsync(string service, CancellationToken cancellationToken)
    {
        TaskCompletionSource<AccessAnswered> asked;
        TaskCompletionSource up; // this connection of the link: an answer is kept only while it lasts
        long id;
        lock (_lock)
        {
            if (_access.TryGetValue(service, out var known)) return known;
            up = _up;
            id = Interlocked.Increment(ref _next);
            _accessAsked[id] = asked = new TaskCompletionSource<AccessAnswered>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            await up.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(new AccessAsked { Ask = id, Service = service }, cancellationToken).ConfigureAwait(false);
            var answer = await asked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (answer.Error is { } error)
                throw new InvalidOperationException($"The provider at {Endpoint} couldn't say what '{service}' requires: {error}");
            var access = answer.Realm is not { } realm ? ServiceAccess.Open
                : answer.Required ? ServiceAccess.RequiredIn(realm) : ServiceAccess.In(realm);
            lock (_lock)
            {
                if (ReferenceEquals(_up, up)) _access[service] = access; // (the link broke meanwhile: asked again next time)
            }

            return access;
        }
        finally
        {
            lock (_lock)
            {
                _accessAsked.Remove(id);
            }
        }
    }

    /// <summary>
    ///     One message of a sign-in, relayed to the provider over the link (an authentication provider:
    ///     <see cref="Publisher.ProvideAuthentication" />), its answer awaited — never kept: each is the provider's to give.
    /// </summary>
    async ValueTask<AuthAnswer> IAuthenticator.AuthenticateAsync(AuthStep step, CancellationToken cancellationToken)
    {
        TaskCompletionSource<AuthAnswered> asked;
        TaskCompletionSource up;
        long id;
        lock (_lock)
        {
            up = _up;
            id = Interlocked.Increment(ref _next);
            _authAsked[id] = asked = new TaskCompletionSource<AuthAnswered>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            await up.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(new AuthAsked
            {
                Ask = id, Exchange = step.Exchange, Realm = step.Realm, Module = step.Module, Round = step.Round,
                Payload = step.Payload.ToArray(), Context = step.Context.ToArray(),
                Current = IdentityCarried.Of(step.Current), Renewal = step.Renewal
            }, cancellationToken).ConfigureAwait(false);
            var answer = await asked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (answer.Error is { } error)
                throw new InvalidOperationException($"The provider at {Endpoint} couldn't answer a sign-in: {error}");
            return answer.Outcome switch
            {
                AuthOutcome.Continue => AuthAnswer.Continue(answer.Payload ?? []),
                AuthOutcome.Accepted when answer.Identity is { } identity =>
                    AuthAnswer.Accept(identity.ToIdentity(), answer.Payload ?? [],
                        new SessionPolicy(answer.Renewal, answer.Warning, answer.PolicyReason)),
                AuthOutcome.Redirected when answer.Reason is { Length: > 0 } router =>
                    AuthAnswer.Redirect(router, answer.Payload ?? []),
                AuthOutcome.Rejected => AuthAnswer.Reject(answer.Reason ?? "Refused."),
                _ => throw new InvalidOperationException($"The provider at {Endpoint} answered a sign-in unreadably.")
            };
        }
        finally
        {
            lock (_lock)
            {
                _authAsked.Remove(id);
            }
        }
    }

    async ValueTask IDataProvider.SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _next);
        var asked = new TaskCompletionSource<StepAnswered>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _asked[id] = asked;
        }

        StepAnswered? answer = null;
        var kept = false;
        try
        {
            await _up.Task.WaitAsync(cancellationToken).ConfigureAwait(false); // (just started: the link, once it's up)
            await WriteAsync(new SubscribeAsked
            {
                Request = id, Service = request.Service, Parameters = request.Parameters,
                AccessRealm = request.Access.Realm, AccessRequired = request.Access.Required,
                Identity = IdentityCarried.Of(request.Identity), Limits = LimitsCarried.Of(request.Limits)
            }, cancellationToken).ConfigureAwait(false);
            answer = await asked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            switch (answer.Outcome)
            {
                case StepOutcome.Refused:
                    request.Refuse((RefusalKind)answer.RefusalKind, answer.Reason, answer.Correlation);
                    return;
                case StepOutcome.Nacked:
                    request.Nack();
                    return;
                case StepOutcome.Failed:
                    throw new InvalidOperationException(
                        $"The provider at {Endpoint} failed the subscription: {answer.Reason}");
                case StepOutcome.Accepted:
                    break;
                default:
                    return;
            }

            var instance = Instances.Of(answer.InstanceName, answer.InstanceEndpoint, answer.InstanceId);
            var twin = await TwinOfAsync(instance, answer, request.Service, cancellationToken).ConfigureAwait(false);
            request.Accept(twin.Source, instance, answer.Name, (InitialState)answer.Initial, PolicyOf(answer),
                answer.Format is { } format ? PayloadFormat.Parse(format) : null, answer.Partition);
            Queue<MailRelayed>? early;
            Route route;
            lock (_lock)
            {
                _routes[id] = route = new Route(id, request, twin, instance);
                _requests[request.Route] = id;
                twin.Routes++;
                _early.Remove(id, out early);
            }

            kept = true;
            _ = request.Tap!.Completion.ContinueWith(closed => Closed(id, closed.Result),
                TaskContinuationOptions.ExecuteSynchronously);
            if (early is not null)
                foreach (var mail in early)
                    Relay(route, mail);
        }
        finally
        {
            lock (_lock)
            {
                _asked.Remove(id);
                if (!kept) _early.Remove(id);
            }

            // Not kept here, but perhaps accepted there — answered so, or not answered yet (timed out, the link down):
            // let it go there, its provider told, as for a route a step 1 closed here.
            if (!kept && answer is null or { Outcome: StepOutcome.Accepted }) Owe(new StepAbandoned { Request = id });
        }
    }

    async ValueTask IDataProvider.ActivateAsync(Activation notice, CancellationToken cancellationToken)
    {
        long id;
        lock (_lock)
        {
            if (!_requests.TryGetValue(notice.Route, out id)) return;
        }

        Owe(new RouteActivated { Request = id });
        await Task.CompletedTask.ConfigureAwait(false);
    }

    async ValueTask IDataProvider.IdentityChangedAsync(IdentityChange notice, CancellationToken cancellationToken)
    {
        long id;
        lock (_lock)
        {
            if (!_requests.TryGetValue(notice.Route, out id)) return;
        }

        Owe(new RouteIdentity { Request = id, Identity = IdentityCarried.Of(notice.Identity), Lapsed = notice.Lapsed });
        await Task.CompletedTask.ConfigureAwait(false);
    }

    async ValueTask IDataProvider.UnsubscribeAsync(Unsubscription notice, CancellationToken cancellationToken)
    {
        long id;
        lock (_lock)
        {
            if (!_requests.Remove(notice.Route, out id) || !_routes.Remove(id, out var route)) return;
            route.Twin.Routes--; // (its feed ends when the provider lets its tap go: the provider's count decides)
        }

        Owe(new RouteRemoved { Request = id });
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    ///     A route of the provider's closed here. One the router relays no notice for — let go by the instance's own drain,
    ///     or down with its instance deemed dead — is let go there quietly, as the provider isn't told in process; its tap
    ///     goes. A copy whose last route went down with its instance goes too. Any other end is relayed
    ///     (<see cref="IDataProvider.UnsubscribeAsync" />).
    /// </summary>
    private void Closed(long id, StreamFault? fault)
    {
        var code = fault?.Code ?? FaultCode.None;
        Twin? dead = null;
        lock (_lock)
        {
            if (!_routes.TryGetValue(id, out var route)) return; // (relayed already)
            if (code != FaultCode.ServiceDown &&
                !(code == FaultCode.ServiceDrained && _draining.Contains(route.Instance.Id))) return;
            _routes.Remove(id);
            _requests.Remove(route.Request.Route);
            if (--route.Twin.Routes == 0 && code == FaultCode.ServiceDown) dead = route.Twin;
        }

        if (dead is not null)
        {
            Forget(dead);
            dead.Feed?.Dispose();
        }

        Owe(new RouteRemoved { Request = id, Quiet = true });
    }

    async ValueTask<HealthReport> IProviderHealth.CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
    {
        if (!_up.Task.IsCompleted)
            throw new InvalidOperationException($"The link to the provider at {Endpoint} is down.");
        var id = Interlocked.Increment(ref _next);
        var answered = new TaskCompletionSource<HealthAnswered>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _probes[id] = answered;
        }

        try
        {
            await WriteAsync(new HealthAsked
            {
                Probe = id, ExpectedName = probe.Expected?.Name, ExpectedEndpoint = probe.Expected?.Endpoint.ToString(),
                ExpectedId = probe.Expected?.Id
            }, cancellationToken).ConfigureAwait(false);
            var h = await answered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (h.Error is { } error) throw new InvalidOperationException(error);
            return new HealthReport(Instances.Of(h.InstanceName, h.InstanceEndpoint, h.InstanceId),
                (ProviderStatus)h.Status, h.RouterRefused, h.DrainUntil, h.DegradedPaths, h.Load);
        }
        finally
        {
            lock (_lock)
            {
                _probes.Remove(id);
            }
        }
    }

    /// <summary>
    ///     The copy of the topic an answer names: the one made already (fed by the tap the answer names), or a new one,
    ///     fed from now on — once its first state is applied here, so no route starts on it empty.
    /// </summary>
    private async Task<Twin> TwinOfAsync(ProviderInstance instance, StepAnswered answer, string service,
        CancellationToken cancellationToken)
    {
        Twin? twin;
        var made = false;
        lock (_lock)
        {
            if (!_twins.TryGetValue((instance.Id, answer.Topic), out twin) || twin.Tap != answer.Tap)
            {
                twin = Twin.Of(answer.Kind, answer.KeySize, answer.RowSize, answer.Name ?? service) ??
                       throw new NotSupportedException(
                           $"A topic of kind '{answer.Kind}' can't be mirrored from another process.");
                twin.Tap = answer.Tap;
                twin.Key = (instance.Id, answer.Topic);
                twin.Posting = version => Released(twin, version);
                twin.Gone = () => Forget(twin);
                _twins[twin.Key] = twin;
                made = true;
            }
        }

        if (made)
            twin.Feed = _link.Client.Subscribe(new TapBinding(answer.Tap, answer.Name, null), twin.Handler);
        await twin.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return twin;
    }

    /// <summary>The feed of a copy ended (the provider let its tap go, or it faulted): no new route is bound to it.</summary>
    private void Forget(Twin twin)
    {
        lock (_lock)
        {
            if (_twins.TryGetValue(twin.Key, out var known) && ReferenceEquals(known, twin)) _twins.Remove(twin.Key);
        }
    }

    /// <summary>
    ///     A mailbox operation for a route, once its copy has been posted the provider's state it was called at: in order
    ///     with the route's others, each after the last.
    /// </summary>
    private void Relay(Route route, MailRelayed mail)
    {
        bool now;
        lock (_lock)
        {
            route.Waiting.Enqueue(mail);
            now = route.Waiting.Count == 1 && route.Twin.Posted >= mail.Version;
            if (now) route.Waiting.Dequeue();
        }

        if (now) Deliver(route, mail);
    }

    /// <summary>A copy posted the provider's state at <paramref name="version" />: the mail waiting on it goes.</summary>
    private void Released(Twin twin, long version)
    {
        List<(Route, MailRelayed)>? due = null;
        lock (_lock)
        {
            foreach (var route in _routes.Values)
            {
                if (!ReferenceEquals(route.Twin, twin)) continue;
                while (route.Waiting.TryPeek(out var mail) && mail.Version <= version)
                    (due ??= []).Add((route, route.Waiting.Dequeue()));
            }
        }

        if (due is null) return;
        foreach (var (route, mail) in due) Deliver(route, mail);
    }

    /// <summary>The operation, at the router: after what's been posted to the copy, applied.</summary>
    private void Deliver(Route route, MailRelayed mail)
    {
        var (twin, mailbox) = (route.Twin, route.Request.Mailbox);
        var bytes = mail.Bytes ?? [];
        try
        {
            switch (mail.Op)
            {
                case MailOp.Rendezvous:
                    twin.TryRendezvous(mailbox, bytes);
                    break;
                case MailOp.Post:
                    twin.WhenApplied(v => mailbox.TryPost(bytes, new After(twin.Source, v)));
                    break;
                case MailOp.Send:
                    twin.WhenApplied(v => mailbox.Send(bytes, v));
                    break;
                case MailOp.Ready:
                    twin.WhenApplied(v => mailbox.Ready(v));
                    break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            _log.LogWarning(e, "Provider link {Endpoint}: a mailbox's {Op} for route {Route} failed", Endpoint, mail.Op,
                route.Request.Route);
        }
    }

    /// <summary>The link: its Tapline stream kept up, and its control stream opened — again whenever it breaks.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var stream = _link.RunAsync(cancellationToken);
        var delay = RetryDelay;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_link.Session is not { } session)
                    {
                        await Task.Delay(10, cancellationToken).ConfigureAwait(false); // (the stream binding its session)
                        continue;
                    }

                    var control = await _link.OpenControlAsync(cancellationToken).ConfigureAwait(false);
                    await using (control.ConfigureAwait(false))
                    {
                        var router = _router!.Router;
                        await control.WriteAsync(new RouterHello
                        {
                            Name = router.Name, Id = router.Id, Outlet = session.OutletId, Nonce = session.Nonce
                        }, cancellationToken).ConfigureAwait(false);
                        lock (_lock)
                        {
                            _control = control;
                            _up.TrySetResult();
                        }

                        ReachableChanged?.Invoke();
                        delay = RetryDelay;
                        _ = FlushAsync(); // (what the provider is owed from before)
                        await foreach (var message in control.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                            Receive(message, cancellationToken);
                    }
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                {
                    _log.LogInformation("Provider link {Endpoint}: the control stream broke ({Reason}): opening it again",
                        Endpoint, e.Message);
                }
                finally
                {
                    Down();
                }

                if (cancellationToken.IsCancellationRequested) break;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }
        finally
        {
            await stream.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>The control stream gone: what waited on an answer fails, and the link is down until it's open again.</summary>
    private void Down()
    {
        List<TaskCompletionSource<StepAnswered>> asked;
        List<TaskCompletionSource<HealthAnswered>> probes;
        List<TaskCompletionSource<AccessAnswered>> access;
        List<TaskCompletionSource<AuthAnswered>> auth;
        bool was;
        lock (_lock)
        {
            _control = null;
            if (was = _up.Task.IsCompleted) _up = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            asked = [.. _asked.Values];
            probes = [.. _probes.Values];
            access = [.. _accessAsked.Values];
            auth = [.. _authAsked.Values];
            _access.Clear(); // (asked again once it's back: the provider may declare otherwise)
        }

        if (was) ReachableChanged?.Invoke();
        var down = new InvalidOperationException($"The link to the provider at {Endpoint} broke.");
        foreach (var a in asked) a.TrySetException(down);
        foreach (var p in probes) p.TrySetException(down);
        foreach (var a in access) a.TrySetException(down);
        foreach (var a in auth) a.TrySetException(down);
    }

    /// <summary>One of the provider's messages: an answer handed to whoever waits on it, mail relayed, an ask answered.</summary>
    private void Receive(ProviderMessage message, CancellationToken cancellationToken)
    {
        switch (message)
        {
            case StepAnswered a:
                TaskCompletionSource<StepAnswered>? asked;
                lock (_lock)
                {
                    _asked.TryGetValue(a.Request, out asked);
                }

                asked?.TrySetResult(a);
                break;
            case MailRelayed m:
                Route? route;
                lock (_lock)
                {
                    if (!_routes.TryGetValue(m.Request, out route))
                    {
                        if (_asked.ContainsKey(m.Request)) // (posted as it accepted: its route is still being bound here)
                        {
                            if (!_early.TryGetValue(m.Request, out var early)) _early[m.Request] = early = new Queue<MailRelayed>();
                            early.Enqueue(m);
                        }

                        break;
                    }
                }

                Relay(route, m);
                break;
            case AccessAnswered a:
                TaskCompletionSource<AccessAnswered>? accessAsked;
                lock (_lock)
                {
                    _accessAsked.TryGetValue(a.Ask, out accessAsked);
                }

                accessAsked?.TrySetResult(a);
                break;
            case AuthAnswered a:
                TaskCompletionSource<AuthAnswered>? authAsked;
                lock (_lock)
                {
                    _authAsked.TryGetValue(a.Ask, out authAsked);
                }

                authAsked?.TrySetResult(a);
                break;
            case HealthAnswered h:
                TaskCompletionSource<HealthAnswered>? probed;
                lock (_lock)
                {
                    _probes.TryGetValue(h.Probe, out probed);
                }

                probed?.TrySetResult(h);
                break;
            case BeatAsked b:
                _ = BeatAsync(b, cancellationToken);
                break;
            case DrainAsked d:
                _ = DrainAsync(d, cancellationToken);
                break;
            case MoveAsked m:
                _ = MoveAsync(m, cancellationToken);
                break;
        }
    }

    /// <summary>The provider's heartbeat, given to the router, and its answer sent back.</summary>
    private async Task BeatAsync(BeatAsked b, CancellationToken cancellationToken)
    {
        BeatAnswered answer;
        try
        {
            var beat = await _router!.HeartbeatAsync(Instances.Of(b.InstanceName, b.InstanceEndpoint, b.InstanceId),
                new RouterInstance(b.ExpectedName, b.ExpectedId), cancellationToken).ConfigureAwait(false);
            answer = new BeatAnswered
            {
                Beat = b.Beat, RouterName = beat.Router.Name, RouterId = beat.Router.Id, KnowsInstance = beat.KnowsInstance
            };
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            answer = new BeatAnswered { Beat = b.Beat, Error = e.Message };
        }

        await WriteQuietlyAsync(answer).ConfigureAwait(false);
    }

    /// <summary>The provider's drain, given to the router, and its answer sent back.</summary>
    private async Task DrainAsync(DrainAsked d, CancellationToken cancellationToken)
    {
        DrainAnswered answer;
        try
        {
            lock (_lock)
            {
                _draining.Add(d.InstanceId); // (the routes it lets go: untold, as it asked)
            }

            await _router!.DrainAsync(Instances.Of(d.InstanceName, d.InstanceEndpoint, d.InstanceId),
                TimeSpan.FromTicks(d.OverTicks), TimeSpan.FromTicks(d.GraceTicks), cancellationToken).ConfigureAwait(false);
            answer = new DrainAnswered { Drain = d.Drain };
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            answer = new DrainAnswered { Drain = d.Drain, Error = e.Message };
        }

        await WriteQuietlyAsync(answer).ConfigureAwait(false);
    }

    /// <summary>The provider's move, given to the router, and its answer sent back.</summary>
    private async Task MoveAsync(MoveAsked m, CancellationToken cancellationToken)
    {
        MoveAnswered answer;
        try
        {
            var a = await _router!.MoveAsync(Instances.Of(m.InstanceName, m.InstanceEndpoint, m.InstanceId), m.Partition,
                m.RouteOutlet is { } outlet ? new RouteId(outlet, m.RouteTap) : null, TimeSpan.FromTicks(m.OverTicks),
                TimeSpan.FromTicks(m.GraceTicks), m.Reason, cancellationToken).ConfigureAwait(false);
            answer = new MoveAnswered { Move = m.Move, Routes = a.Routes, Correlation = a.Correlation, Refused = a.Refused };
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            answer = new MoveAnswered { Move = m.Move, Error = e.Message };
        }

        await WriteQuietlyAsync(answer).ConfigureAwait(false);
    }

    /// <summary>A message to the provider, in order with the others; throws if the control stream is down.</summary>
    private async Task WriteAsync(RouterMessage message, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var control = Volatile.Read(ref _control) ??
                          throw new InvalidOperationException($"The link to the provider at {Endpoint} is down.");
            await control.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>A route's notice, owed to the provider until it has it: sent now if the link is up, else once it's up again.</summary>
    private void Owe(RouterMessage notice)
    {
        lock (_lock)
        {
            _owed.Enqueue(notice);
        }

        _ = FlushAsync();
    }

    /// <summary>The notices owed, in order, while the link is up: each kept until it's sent.</summary>
    private async Task FlushAsync()
    {
        if (!await _flushing.WaitAsync(0).ConfigureAwait(false)) return; // (another flush has it: it takes this one too)
        try
        {
            while (true)
            {
                RouterMessage? next;
                lock (_lock)
                {
                    if (_control is null || !_owed.TryPeek(out next)) return;
                }

                try
                {
                    await WriteAsync(next, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or IOException)
                {
                    return; // (down: sent once it's up again)
                }

                lock (_lock)
                {
                    _owed.Dequeue();
                }
            }
        }
        finally
        {
            _flushing.Release();
            bool more;
            lock (_lock)
            {
                more = _control is not null && _owed.Count > 0;
            }

            if (more) _ = FlushAsync(); // (owed while this one let go)
        }
    }

    /// <summary>A message the provider is told if the link is up: a notice it would otherwise learn of as the link's end.</summary>
    private async Task WriteQuietlyAsync(RouterMessage message)
    {
        try
        {
            await WriteAsync(message, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or IOException)
        {
        } // (down: the provider learns of it as the link's end)
        catch (Exception e)
        {
            _log.LogDebug(e, "Provider link {Endpoint}: a notice wasn't sent", Endpoint);
        }
    }

    private static ErrorPolicy? PolicyOf(StepAnswered answer)
    {
        if (answer.Stale is not { } kind) return null;
        var payload = (ReadOnlyMemory<byte>)(answer.StalePayload ?? []);
        return (StaleData)kind switch
        {
            StaleData.Static => ErrorPolicy.Static(answer.StaleState ?? [], payload),
            StaleData.ReplayLast => ErrorPolicy.ReplayLast(payload),
            _ => new ErrorPolicy(payload) // (no stale data: the error alone)
        };
    }

    /// <summary>A route of the router's, bound to a copy of the provider's topic; its mailbox's operations waiting on the copy.</summary>
    private sealed class Route(long id, SubscribeRequest request, Twin twin, ProviderInstance instance)
    {
        public readonly long Id = id;
        public readonly ProviderInstance Instance = instance;
        public readonly SubscribeRequest Request = request;
        public readonly Twin Twin = twin;
        public readonly Queue<MailRelayed> Waiting = new();
    }
}

/// <summary>A provider instance, as the link carries it: its name, endpoint and id.</summary>
internal static class Instances
{
    public static ProviderInstance Of(string? name, string? endpoint, Guid id)
    {
        return new ProviderInstance(name ?? "", new ProviderEndpoint(new Uri(endpoint ?? "", UriKind.Absolute)), id);
    }
}
