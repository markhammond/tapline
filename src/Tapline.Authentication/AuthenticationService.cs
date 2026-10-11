using System.Text;

namespace Tapline.Authentication;

/// <summary>
///     One way of proving who you are — a mechanism: <c>passkey</c>, <c>passthrough</c>, your own (an LDAP bind, a code
///     sent by mail). It answers a sign-in's messages, round by round, and ends with a user it has proven (or not); it
///     never says what that user may be — the service's <see cref="AuthenticationService.OnAuthenticated" /> does. Its
///     state between rounds is <see cref="ModuleStep.State" />, kept by the service.
/// </summary>
public interface IAuthModule
{
    /// <summary>The module's name, as a client asks for it (<see cref="AuthStep.Module" />).</summary>
    string Name { get; }

    /// <summary>One message of a sign-in by this module: the next one to the client, a user proven, or a refusal.</summary>
    ValueTask<ModuleAnswer> StepAsync(ModuleStep step, CancellationToken cancellationToken);
}

/// <summary>A module's message of a sign-in: the router's step, this module's round in it, its payload, and its state.</summary>
public sealed class ModuleStep(AuthStep step, int round, ReadOnlyMemory<byte> payload, AuthenticationService service)
{
    /// <summary>The router's step: the exchange, the realm, the connection's context, who it is now, a renewal or not.</summary>
    public AuthStep Step { get; } = step;

    /// <summary>This module's round: 0 for its first message (a module inside another counts its own).</summary>
    public int Round { get; } = round;

    /// <summary>The client's message to this module.</summary>
    public ReadOnlyMemory<byte> Payload { get; } = payload;

    /// <summary>The service the module serves (its realm, its clock).</summary>
    public AuthenticationService Service { get; } = service;

    /// <summary>The module's own state between rounds of the sign-in: set it, and it's here at the next round.</summary>
    public object? State { get; set; }

    /// <summary>In a sequence (<see cref="AuthModules.Sequence" />), the user the stages before proved — whom a second factor checks.</summary>
    public AuthenticatedUser? Previous { get; init; }
}

/// <summary>A module's answer (<see cref="IAuthModule.StepAsync" />).</summary>
public sealed record ModuleAnswer
{
    private ModuleAnswer(ReadOnlyMemory<byte> message, AuthenticatedUser? user, string? reason, string? failedUser,
        string? attemptBy = null)
    {
        (Message, User, Reason, FailedUser, AttemptBy) = (message, user, reason, failedUser, attemptBy);
    }

    public ReadOnlyMemory<byte> Message { get; }
    public AuthenticatedUser? User { get; }
    public string? Reason { get; }

    /// <summary>The user a refusal was a failed attempt for, if the module knows (counted: too many and they wait).</summary>
    public string? FailedUser { get; }

    /// <summary>
    ///     An attempt begun for this user, counted as a failure until it succeeds — for a mechanism whose failures the
    ///     server never sees (OPAQUE's wrong password fails at the client, which just gives up).
    /// </summary>
    public string? AttemptBy { get; }

    public bool Proven => User is not null;
    public bool Refused => Reason is not null;

    /// <summary>The sign-in goes on: <paramref name="message" /> to the client.</summary>
    public static ModuleAnswer Continue(ReadOnlyMemory<byte> message, string? attemptBy = null)
    {
        return new ModuleAnswer(message, null, null, null, attemptBy);
    }

    /// <summary>Proven: <paramref name="user" /> is who they said.</summary>
    public static ModuleAnswer Prove(AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new ModuleAnswer(default, user, null, null);
    }

    /// <summary>Not proven: <paramref name="reason" /> is told to the client — say little. A failure of a known user counts against them.</summary>
    public static ModuleAnswer Refuse(string reason, string? failedUser = null)
    {
        return new ModuleAnswer(default, null, reason, failedUser);
    }
}

/// <summary>
///     A user a module proved: who, by which module, whether the proof is worth anything (<see cref="Insecure" />) —
///     and what the backend adds (<see cref="AuthenticationService.OnAuthenticated" />): claims, how long, how renewed.
/// </summary>
public sealed record AuthenticatedUser(
    string UserId,
    string Module,
    bool Insecure = false,
    IReadOnlyDictionary<string, string>? Claims = null,
    SessionPolicy? Policy = null,
    TimeSpan? Lifetime = null);

/// <summary>The backend's refusal of a proven user (<see cref="AuthenticationService.OnAuthenticated" />): told to the client.</summary>
public sealed class SignInRefusedException(string reason) : Exception(reason);

/// <summary>
///     A realm's authentication service, composed of modules — <see cref="PasskeyModule" />,
///     <see cref="PasskeyRegistrationModule" />, <see cref="PassthroughModule" />, your own, several in sequence
///     (<see cref="AuthModules.Sequence" />) — and resuming with its own tickets (module <c>ticket</c>). Each sign-in goes
///     to the module the client names; a user proven is handed to <see cref="OnAuthenticated" /> — the backend's say:
///     claims, a refusal, the session's lifetime and policy — on every sign-in, resume and renewal. It keeps each
///     sign-in's state until it ends or goes quiet (<see cref="ExchangeTimeout" />), and counts failures: too many from a
///     connection, or for a user, and they wait. Register it: <see cref="AuthenticationRegistration.ProvideAuthentication" />.
/// </summary>
public sealed class AuthenticationService : IDataProvider, IAuthenticator
{
    public const string TicketModule = "ticket";
    private readonly Dictionary<Guid, Exchange> _exchanges = [];
    private readonly Dictionary<string, Failures> _failures = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <param name="realm">The realm it signs users in to.</param>
    public AuthenticationService(string realm)
    {
        Realm = ServiceAccess.In(realm).Realm!;
        Instance = ProviderInstance.Start($"auth-{Realm}", Endpoint);
    }

    public string Realm { get; }

    /// <summary>The ways it signs users in (by name; <c>ticket</c> is its own).</summary>
    public IList<IAuthModule> Modules { get; } = new List<IAuthModule>();

    /// <summary>Its tickets: a key of its own by default; share one among a realm's instances.</summary>
    public AuthTickets Tickets { get; init; } = new();

    /// <summary>How long a session holds unless the backend says (default 8 hours).</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Its sessions' policy unless the backend says (default: <see cref="SessionPolicy.Default" />).</summary>
    public SessionPolicy Policy { get; set; } = SessionPolicy.Default;

    /// <summary>
    ///     The backend's say on a proven user — on each sign-in, resume (by ticket) and renewal: the user as it should
    ///     be (claims added, a lifetime, a policy), or null — not allowed — or throw <see cref="SignInRefusedException" />
    ///     to say why. Null (the default): every proven user, as proven.
    /// </summary>
    public Func<AuthenticatedUser, AuthStep, CancellationToken, ValueTask<AuthenticatedUser?>>? OnAuthenticated { get; set; }

    /// <summary>How long a sign-in may go quiet between messages before it's forgotten (default 2 minutes).</summary>
    public TimeSpan ExchangeTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Failed sign-ins a connection may make within <see cref="FailureWindow" /> (default 5); then it waits.</summary>
    public int MaxFailuresPerConnection { get; set; } = 5;

    /// <summary>Failed attempts for one user within <see cref="FailureWindow" /> (default 10); then they wait.</summary>
    public int MaxFailuresPerUser { get; set; } = 10;

    /// <summary>The window failures are counted in (default 15 minutes).</summary>
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The clock (tests).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public ProviderEndpoint Endpoint => ProviderEndpoint.InProcess($"auth-{Realm}");
    public ProviderInstance Instance { get; }

    public async ValueTask<AuthAnswer> AuthenticateAsync(AuthStep step, CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow();
        var connection = Convert.ToBase64String(step.Context.Span);
        Exchange exchange;
        lock (_lock)
        {
            Sweep(now);
            if (step.Round == 0 && Blocked(connection, now)) // (a sign-in under way goes on: its attempt counted already)
                return AuthAnswer.Reject("Too many failed sign-ins: try again later.");
            if (step.Round == 0 || !_exchanges.TryGetValue(step.Exchange, out exchange!))
            {
                if (step.Round != 0) return AuthAnswer.Reject("That sign-in has gone quiet: begin again.");
                _exchanges[step.Exchange] = exchange = new Exchange();
            }

            exchange.Seen = now;
        }

        ModuleAnswer answer;
        try
        {
            answer = step.Module == TicketModule
                ? Ticket(step, now)
                : Module(step.Module) is { } module
                    ? await Run(module, step, exchange, cancellationToken).ConfigureAwait(false)
                    : ModuleAnswer.Refuse($"No module '{step.Module}' here.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            answer = ModuleAnswer.Refuse("Sign-in failed.");
        }

        if (!answer.Proven && !answer.Refused)
        {
            if (answer.AttemptBy is { } attempt)
            {
                lock (_lock)
                {
                    if (Blocked("user " + attempt, now))
                    {
                        _exchanges.Remove(step.Exchange);
                        return AuthAnswer.Reject("Too many failed sign-ins: try again later.");
                    }
                }

                Failed(connection, attempt, now); // (until it succeeds)
                exchange.Attempt = attempt;
            }

            return AuthAnswer.Continue(answer.Message);
        }

        lock (_lock)
        {
            _exchanges.Remove(step.Exchange);
        }

        if (answer.Refused)
        {
            Failed(connection, answer.FailedUser, now);
            return AuthAnswer.Reject(answer.Reason!);
        }

        var user = answer.User!;
        lock (_lock)
        {
            if (exchange.Attempt is { } attempted) Forgive(connection, attempted, now); // it succeeded
            else if (Blocked("user " + user.UserId, now))
                return AuthAnswer.Reject("Too many failed sign-ins: try again later.");
        }

        try
        {
            if (OnAuthenticated is { } decide && await decide(user, step, cancellationToken).ConfigureAwait(false) is
                    var decided)
            {
                if (decided is null) return AuthAnswer.Reject("Not allowed.");
                if (decided.UserId != user.UserId) return AuthAnswer.Reject("Not allowed.");
                user = decided;
            }
        }
        catch (SignInRefusedException e)
        {
            return AuthAnswer.Reject(e.Message);
        }

        var policy = user.Policy ?? Policy;
        if (step.Module == TicketModule && step.Renewal && policy.Renewal != RenewalPolicy.Silent)
            return AuthAnswer.Reject("Prove who you are again to renew.");
        // A ticket resumes its session (until the ticket's end; renewing, for another lifetime); a renewal keeps the
        // session it renews; anything else is a new one.
        var session = exchange.Resumed?.SessionId ??
                      (step.Renewal && step.Current is { } current && current.Realm == Realm &&
                       current.UserId == user.UserId
                          ? current.SessionId
                          : Guid.CreateVersion7());
        var expires = exchange.Resumed is not null && !step.Renewal
            ? exchange.ResumedUntil
            : now + (user.Lifetime ?? Lifetime);
        var identity = new ClientIdentity(Realm, user.UserId, session, user.Module, user.Insecure,
            exchange.Resumed?.AuthenticatedAt ?? now, expires, user.Claims);
        return AuthAnswer.Accept(identity, Tickets.Issue(identity, expires), policy);
    }

    public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
    {
        return default; // (it serves no topics)
    }

    public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
    {
        return new ValueTask<HealthReport>(new HealthReport(Instance, ProviderStatus.Serving));
    }

    private IAuthModule? Module(string name)
    {
        foreach (var m in Modules)
            if (string.Equals(m.Name, name, StringComparison.Ordinal))
                return m;
        return null;
    }

    private async ValueTask<ModuleAnswer> Run(IAuthModule module, AuthStep step, Exchange exchange,
        CancellationToken cancellationToken)
    {
        var moduleStep = new ModuleStep(step, step.Round, step.Payload, this) { State = exchange.State };
        var answer = await module.StepAsync(moduleStep, cancellationToken).ConfigureAwait(false);
        exchange.State = moduleStep.State;
        return answer;
    }

    /// <summary>
    ///     A ticket presented: to resume (the session it names, until the ticket's end), or to renew — when the session's
    ///     policy is silent — for another lifetime. Either way the backend has its say again.
    /// </summary>
    private ModuleAnswer Ticket(AuthStep step, DateTimeOffset now)
    {
        if (!Tickets.TryRead(step.Payload.Span, now, out var held, out var until) || held!.Realm != Realm)
            return ModuleAnswer.Refuse("That ticket doesn't hold here.");
        lock (_lock)
        {
            if (_exchanges.TryGetValue(step.Exchange, out var exchange)) (exchange.Resumed, exchange.ResumedUntil) = (held, until);
        }

        return ModuleAnswer.Prove(new AuthenticatedUser(held.UserId, held.Module, held.Insecure));
    }

    private void Sweep(DateTimeOffset now)
    {
        List<Guid>? quiet = null;
        foreach (var (id, e) in _exchanges)
            if (now - e.Seen > ExchangeTimeout)
                (quiet ??= []).Add(id);
        if (quiet is not null)
            foreach (var id in quiet)
                _exchanges.Remove(id);
        List<string>? old = null;
        foreach (var (key, f) in _failures)
            if (now - f.Since > FailureWindow)
                (old ??= []).Add(key);
        if (old is not null)
            foreach (var key in old)
                _failures.Remove(key);
    }

    private bool Blocked(string key, DateTimeOffset now)
    {
        var max = key.StartsWith("user ", StringComparison.Ordinal) ? MaxFailuresPerUser : MaxFailuresPerConnection;
        return _failures.TryGetValue(key, out var f) && now - f.Since <= FailureWindow && f.Count >= max;
    }

    private void Failed(string connection, string? user, DateTimeOffset now)
    {
        lock (_lock)
        {
            Count(connection);
            if (user is not null) Count("user " + user);
        }

        void Count(string key)
        {
            if (!_failures.TryGetValue(key, out var f) || now - f.Since > FailureWindow)
                _failures[key] = f = new Failures { Since = now };
            f.Count++;
        }
    }

    private void Forgive(string connection, string user, DateTimeOffset now)
    {
        foreach (var key in (ReadOnlySpan<string>)[connection, "user " + user])
            if (_failures.TryGetValue(key, out var f) && now - f.Since <= FailureWindow && f.Count > 0)
                f.Count--;
    }

    private sealed class Exchange
    {
        public string? Attempt; // the user an attempt was counted for, until it succeeds
        public ClientIdentity? Resumed; // the session a ticket named
        public DateTimeOffset ResumedUntil;
        public DateTimeOffset Seen;
        public object? State;
    }

    private sealed class Failures
    {
        public int Count;
        public DateTimeOffset Since;
    }
}

/// <summary>Modules made of modules, and the simplest one.</summary>
public static class AuthModules
{
    /// <summary>
    ///     Several modules in turn, one sign-in — two factors, say: each must prove the same user. Its messages to the
    ///     client are framed <c>u8 stage | the stage's message</c>; a stage proven, the next begins with
    ///     <c>u8 stage</c> alone, and the client's next message is that stage's first. The user proven is the last's,
    ///     its module the stages' names joined by <c>+</c>, insecure if any stage was, its claims all of theirs.
    /// </summary>
    public static IAuthModule Sequence(string name, params IAuthModule[] stages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (stages.Length < 2) throw new ArgumentException("A sequence is two modules or more.", nameof(stages));
        return new SequenceModule(name, stages);
    }

    private sealed class SequenceModule(string name, IAuthModule[] stages) : IAuthModule
    {
        public string Name => name;

        public async ValueTask<ModuleAnswer> StepAsync(ModuleStep step, CancellationToken cancellationToken)
        {
            var state = step.State as State ?? new State();
            step.State = state;
            var inner = new ModuleStep(step.Step, state.Round, step.Payload, step.Service)
                { State = state.Inner, Previous = state.Users.Count > 0 ? state.Users[^1] : null };
            var answer = await stages[state.Stage].StepAsync(inner, cancellationToken).ConfigureAwait(false);
            state.Inner = inner.State;
            state.Round++;
            if (answer.Refused) return answer;
            if (!answer.Proven) return ModuleAnswer.Continue(Framed((byte)state.Stage, answer.Message.Span));
            var user = answer.User!;
            if (state.Users.Count > 0 && state.Users[0].UserId != user.UserId)
                return ModuleAnswer.Refuse("The factors proved different users.", user.UserId);
            state.Users.Add(user);
            if (state.Stage + 1 < stages.Length)
            {
                (state.Stage, state.Round, state.Inner) = (state.Stage + 1, 0, null);
                return ModuleAnswer.Continue(new[] { (byte)state.Stage });
            }

            Dictionary<string, string>? claims = null;
            foreach (var u in state.Users)
                if (u.Claims is { Count: > 0 } c)
                    foreach (var (k, v) in c)
                        (claims ??= new Dictionary<string, string>(StringComparer.Ordinal))[k] = v;
            return ModuleAnswer.Prove(user with
            {
                Module = string.Join("+", state.Users.Select(u => u.Module)),
                Insecure = state.Users.Any(u => u.Insecure), Claims = claims
            });
        }

        private static byte[] Framed(byte stage, ReadOnlySpan<byte> message)
        {
            var b = new byte[1 + message.Length];
            b[0] = stage;
            message.CopyTo(b.AsSpan(1));
            return b;
        }

        private sealed class State
        {
            public readonly List<AuthenticatedUser> Users = [];
            public object? Inner;
            public int Round, Stage;
        }
    }
}

/// <summary>
///     Passthrough as a module (<c>passthrough</c>): the client's message is its user id, taken as given — insecure.
///     Enabled only by <see cref="PassthroughAuthenticator.AcceptAnyCredential" />.
/// </summary>
public sealed class PassthroughModule : IAuthModule
{
    public PassthroughModule(string enable)
    {
        if (!string.Equals(enable, PassthroughAuthenticator.AcceptAnyCredential, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Passthrough takes any user id as given: it's enabled only by \"{PassthroughAuthenticator.AcceptAnyCredential}\".",
                nameof(enable));
    }

    public string Name => PassthroughAuthenticator.Module;

    public ValueTask<ModuleAnswer> StepAsync(ModuleStep step, CancellationToken cancellationToken)
    {
        string user;
        try
        {
            user = new UTF8Encoding(false, true).GetString(step.Payload.Span).Trim();
        }
        catch (DecoderFallbackException)
        {
            return new ValueTask<ModuleAnswer>(ModuleAnswer.Refuse("A user id is UTF-8."));
        }

        return new ValueTask<ModuleAnswer>(user.Length == 0 || Encoding.UTF8.GetByteCount(user) > ClientIdentity.MaxName
            ? ModuleAnswer.Refuse("Say who you are.")
            : ModuleAnswer.Prove(new AuthenticatedUser(user, Name, true)));
    }
}

public static class AuthenticationRegistration
{
    /// <summary>Registers <paramref name="service" /> as its realm's authentication service, in process.</summary>
    public static AuthenticationService ProvideAuthentication(this Publisher publisher, AuthenticationService service)
    {
        publisher.ProvideAuthentication(service.Realm, service.Endpoint, service);
        return service;
    }
}

/// <summary>The challenge a module binds to the connection: 16 random bytes, then the first 16 of SHA-256 of its context.</summary>
internal static class Challenges
{
    public static byte[] New(ReadOnlySpan<byte> context)
    {
        var c = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(c.AsSpan(0, 16));
        System.Security.Cryptography.SHA256.HashData(context)[..16].CopyTo(c, 16);
        return c;
    }

    /// <summary><paramref name="challenge" /> is <paramref name="issued" />, and bound to <paramref name="context" />.</summary>
    public static bool Check(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> issued, ReadOnlySpan<byte> context)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(context, hash);
        return challenge.Length == 32 &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(challenge, issued) &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(challenge[16..], hash[..16]);
    }

}
