using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tapline.AspNetCore;
using Tapline;
using Tapline.Client;
using Tapline.Tests;
using BlazorRealtime;

/// <summary>Sign-ins: the handshake relayed to a realm's authentication provider, passthrough, tickets, the transports.</summary>
internal static partial class T
{
    /// <summary>A connection whose outlet can change under it (its session lost, another bound): what the client says goes to the current one.</summary>
    private sealed class MovableConnection(Outlet outlet) : ITaplineConnection
    {
        public Outlet Outlet = outlet;
        public readonly List<byte[]> Auths = [];
        public void Send(in ControlMessage message)
        {
            if (message.Kind == ControlKind.Auth)
                lock (Auths) Auths.Add(message.Payload.ToArray());
            Outlet.Apply(message);
        }
    }

    /// <summary>Pumps frames into the client until <paramref name="task" /> completes (5 s at most).</summary>
    private static async Task<TResult> Pumped<TResult>(Task<TResult> task, MovableConnection loop, object owner,
        TaplineClient client)
    {
        for (var i = 0; i < 1000 && !task.IsCompleted; i++)
        {
            Pump(loop.Outlet, owner, client);
            await Task.WhenAny(task, Task.Delay(5));
        }

        Pump(loop.Outlet, owner, client);
        Assert(task.IsCompleted, "the sign-in ended within 5 s");
        return await task;
    }

    /// <summary>
    ///     A module of two messages, bound to its connection: round 0 asks a challenge, round 1 answers it with
    ///     HMAC-SHA256(challenge, context) — so an answer relayed from another connection fails. Can stall, on asking.
    /// </summary>
    private sealed class ChallengeAuthenticator : IDataProvider, IAuthenticator
    {
        private readonly Dictionary<Guid, byte[]> _challenges = [];
        public readonly List<AuthStep> Steps = [];
        public TaskCompletionSource? Stall;

        public ProviderInstance Instance { get; } = ProviderInstance.Start("challenge", At("challenge"));

        public async ValueTask<AuthAnswer> AuthenticateAsync(AuthStep step, CancellationToken cancellationToken)
        {
            lock (Steps) Steps.Add(step);
            if (Stall is { } stall) await stall.Task.WaitAsync(cancellationToken);
            if (step.Round == 0)
            {
                var challenge = RandomNumberGenerator.GetBytes(16);
                lock (_challenges) _challenges[step.Exchange] = challenge;
                return AuthAnswer.Continue(challenge);
            }

            byte[]? asked;
            lock (_challenges) _challenges.Remove(step.Exchange, out asked);
            if (asked is null) return AuthAnswer.Reject("No challenge asked.");
            var expected = HMACSHA256.HashData(asked, step.Context.Span);
            if (!CryptographicOperations.FixedTimeEquals(expected, step.Payload.Span))
                return AuthAnswer.Reject("Wrong answer.");
            return AuthAnswer.Accept(new ClientIdentity(step.Realm, "dana", Guid.CreateVersion7(), "challenge", false,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
                new Dictionary<string, string> { ["role"] = "nurse" }));
        }

        public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken) => default;

        public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken) =>
            new(new HealthReport(Instance, ProviderStatus.Serving));
    }

    public static async Task SignIns()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var loop = new MovableConnection(s);
        var client = new TaplineClient(loop);
        client.Bind(s.Id, s.Nonce);
        var changes = new List<ClientIdentity?>();
        client.IdentityChanged += changes.Add;
        var log = new List<(string Realm, ClientIdentity? Identity, string? Reason)>();
        publisher.SignIn += (_, realm, identity, reason) =>
        {
            lock (log) log.Add((realm, identity, reason));
        };

        Task<SignInResult> SignIn(string realm, string module, string message, bool renewal = false) =>
            Pumped(client.SignInAsync(realm, module, Encoding.UTF8.GetBytes(message), renewal: renewal), loop, owner,
                client);

        // No authentication provider on this router: refused at once.
        var none = await SignIn("north", "passthrough", "alice");
        Assert(none is { Accepted: false, Reason: "No sign-in on this router." }, $"no sign-in here ({none.Reason})");

        // Passthrough: enabled by AcceptAnyCredential alone.
        var refusedToEnable = false;
        try
        {
            publisher.ProvidePassthrough("north", "true");
        }
        catch (ArgumentException)
        {
            refusedToEnable = true;
        }

        Assert(refusedToEnable, "passthrough isn't enabled by anything but \"AcceptAnyCredential\"");
        var passthrough = publisher.ProvidePassthrough("north", PassthroughAuthenticator.AcceptAnyCredential);
        var another = false;
        try
        {
            publisher.ProvideAuthentication("north", At("challenge-north"), new ChallengeAuthenticator());
        }
        catch (InvalidOperationException e) when (e.Message.Contains("has an authentication service already"))
        {
            another = true;
        }

        Assert(another, "one authentication service a realm: another kind beside passthrough refused");

        // Signed in: insecure, module passthrough — on the connection, told to the client with a ticket; the router's log.
        var alice = await SignIn("north", "passthrough", "alice");
        Assert(alice is { Accepted: true, Identity: { UserId: "alice", Realm: "north", Insecure: true } } &&
               alice.Identity.Module == "passthrough" && !alice.Ticket.IsEmpty,
            $"alice signed in by passthrough, insecure, with a ticket ({alice.Reason})");
        Assert(s.Identity is { UserId: "alice", Insecure: true, Module: "passthrough" } &&
               s.Identity.SessionId == alice.Identity!.SessionId && s.Identity.ExpiresAt is not null,
            $"the connection is alice, until an expiry ({s.Identity})");
        Assert(client.Identity == alice.Identity && changes.SequenceEqual([alice.Identity]),
            "the client's identity, changed once");
        Assert(log.Any(e => e.Identity?.UserId == "alice" && e.Reason is null), "the router's log has it");

        // Its subscriptions carry it: a required service of the realm served, shown alice — insecure.
        var topic = new LastValueSource { Label = "feed" };
        var provider = new TestProvider(topic) { Access = _ => ServiceAccess.RequiredIn("north") };
        Provide(publisher, "home", "feeds", provider);
        var home = await publisher.SubscribeAsync(s, "home");
        Assert(home is not null && provider.Last?.Identity is { UserId: "alice", Insecure: true, Module: "passthrough" },
            "signed in: a required service served, shown alice, passthrough, insecure");

        // Refusals: another realm, a module the provider lacks, nobody.
        var south = await SignIn("south", "passthrough", "alice");
        Assert(south is { Accepted: false, Reason: "No sign-in to 'south' here." }, $"another realm ({south.Reason})");
        var odd = await SignIn("north", "telepathy", "alice");
        Assert(odd is { Accepted: false } && odd.Reason!.Contains("telepathy"), $"an unknown module ({odd.Reason})");
        var nobody = await SignIn("north", "passthrough", "  ");
        Assert(nobody is { Accepted: false, Reason: "Say who you are." }, "no user id");
        Assert(s.Identity?.UserId == "alice" && !home!.IsClosed, "refused sign-ins change nothing");
        Assert(log.Count(e => e.Reason is not null) == 3, "each refusal logged");

        // A renewal: the same user, the same session, a later expiry — every subscription carries on.
        var before = s.Identity!;
        await Task.Delay(5);
        var renewed = await SignIn("north", "passthrough", "alice", renewal: true);
        Assert(renewed.Accepted && renewed.Identity!.SessionId == before.SessionId &&
               s.Identity!.ExpiresAt > before.ExpiresAt && !home!.IsClosed,
            "renewed: the same session, a later expiry, the subscription carries on");
        var hijack = await SignIn("north", "passthrough", "mallory", renewal: true);
        Assert(hijack is { Accepted: false, Reason: "A renewal is the same user's." } && s.Identity!.UserId == "alice",
            $"a renewal by another user: refused ({hijack.Reason})");

        // The ticket: presented on a new session (another outlet) — done by the client itself — it resumes alice.
        var ticket = client.Ticket;
        var s2 = NewOutlet(registry);
        var owner2 = new object();
        s2.Attach(owner2);
        loop.Outlet = s2;
        var count = changes.Count;
        client.Bind(s2.Id, s2.Nonce); // (its session gone: subscriptions Gone, its identity with it, the ticket resumes)
        Assert(client.Identity is null && changes.Count == count + 1 && changes[^1] is null,
            "a new session: the identity gone with the old");
        for (var i = 0; i < 400 && s2.Identity is null; i++)
        {
            Pump(s2, owner2, client);
            await Task.Delay(5);
        }

        Pump(s2, owner2, client);
        Assert(s2.Identity is { UserId: "alice", Module: "passthrough", Insecure: true } &&
               s2.Identity.SessionId == before.SessionId && client.Identity?.UserId == "alice",
            $"the ticket resumed alice on the new session, the same sign-in ({s2.Identity})");
        Assert(loop.Auths.Last()[0] == AuthMessages.Begin &&
               Encoding.UTF8.GetString(loop.Auths.Last()).Contains("ticket"), "by module ticket");

        // A ticket altered, or of another key, doesn't hold.
        var forged = ticket.ToArray();
        forged[5] ^= 1;
        var tampered = await Pumped(client.SignInAsync("north", "ticket", forged), loop, owner2, client);
        Assert(tampered is { Accepted: false }, "an altered ticket: refused");
        var otherKey = new AuthTickets().Issue(before, DateTimeOffset.UtcNow.AddHours(1));
        var foreign = await Pumped(client.SignInAsync("north", "ticket", otherKey), loop, owner2, client);
        Assert(foreign is { Accepted: false }, "another key's ticket: refused");
        Assert(!passthrough.Tickets.TryRead(ticket.Span, DateTimeOffset.UtcNow.AddHours(9), out _, out _),
            "a ticket lapses with its identity");

        // Another user signs in (not a renewal): what was shown alice ends.
        loop.Outlet = s;
        s2.Dispose();
        var s3 = s; // (back on the first session, alice signed in there still)
        client.Bind(s3.Id, s3.Nonce);
        Pump(s3, owner, client);
        var bob = await Pumped(client.SignInAsync("north", "passthrough", "bob"u8.ToArray()), loop, owner, client);
        Assert(bob.Accepted && s.Identity?.UserId == "bob" && home!.IsClosed,
            "bob signed in: alice's subscription to the required service ended (IdentityChanged)");

        // A module of several messages, bound to the connection: a challenge, answered with the connection's context.
        var challenge = new ChallengeAuthenticator();
        publisher.ProvideAuthentication("west", At("challenge"), challenge);
        var context = client.SignInContext("west")!;
        Assert(Encoding.UTF8.GetString(context) == $"tapline/1 west {s.Id} {s.Nonce}", "the context: realm, outlet, nonce");
        var asked = 0;
        var dana = await Pumped(client.SignInAsync("west", "challenge", "hello"u8.ToArray(), (message, _) =>
        {
            asked++;
            return new ValueTask<ReadOnlyMemory<byte>>(HMACSHA256.HashData(message.Span, context));
        }), loop, owner, client);
        Assert(dana is { Accepted: true, Identity: { UserId: "dana", Insecure: false, Module: "challenge" } } &&
               dana.Identity.Claims?["role"] == "nurse" && asked == 1,
            $"two rounds: challenged once, answered, signed in — claims carried ({dana.Reason})");
        Assert(challenge.Steps.Select(x => x.Round).SequenceEqual([0, 1]) &&
               challenge.Steps[0].Exchange == challenge.Steps[1].Exchange &&
               challenge.Steps[0].Context.Span.SequenceEqual(context) &&
               Encoding.UTF8.GetString(challenge.Steps[0].Payload.Span) == "hello" &&
               challenge.Steps[1].Current?.UserId == "bob",
            "the provider: rounds 0 and 1 of one exchange, the context, the payload, who the connection was");

        // Answered from another connection's context: refused.
        var wrong = AuthMessages.Context("west", s.Id + 1, s.Nonce);
        var relayed = await Pumped(client.SignInAsync("west", "challenge", "hello"u8.ToArray(),
            (message, _) => new ValueTask<ReadOnlyMemory<byte>>(HMACSHA256.HashData(message.Span, wrong))), loop,
            owner, client);
        Assert(relayed is { Accepted: false, Reason: "Wrong answer." } && s.Identity?.UserId == "dana",
            "a proof bound to another connection: refused");

        // A module of one message, asked for more: the client gives up (Cancel).
        var curt = await Pumped(client.SignInAsync("west", "challenge", "hello"u8.ToArray()), loop, owner, client);
        Assert(curt is { Accepted: false } && loop.Auths.Last()[0] == AuthMessages.Cancel,
            "asked for more than it says: cancelled");

        // A provider that doesn't answer in time: unavailable.
        publisher.AuthTimeout = TimeSpan.FromMilliseconds(100);
        challenge.Stall = new TaskCompletionSource();
        var slow = await Pumped(client.SignInAsync("west", "challenge", "hello"u8.ToArray()), loop, owner, client);
        Assert(slow is { Accepted: false, Reason: "Sign-in is unavailable: try again." }, $"timed out ({slow.Reason})");

        // Cancelled by the client while the provider thinks: cancelled, the router told.
        using var cancel = new CancellationTokenSource(50);
        var cancelled = false;
        try
        {
            await Pumped(client.SignInAsync("west", "challenge", "hello"u8.ToArray(), cancellationToken: cancel.Token),
                loop, owner, client);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled && loop.Auths.Last()[0] == AuthMessages.Cancel, "cancelled: the router told");
        challenge.Stall.SetResult();
        publisher.AuthTimeout = TimeSpan.FromSeconds(10);

        // A second sign-in begun ends the first.
        challenge.Stall = new TaskCompletionSource();
        var first = client.SignInAsync("west", "challenge", "hello"u8.ToArray());
        var second = client.SignInAsync("north", "passthrough", "erin"u8.ToArray());
        Assert(await first.WaitAsync(TimeSpan.FromSeconds(1)) is { Accepted: false, Reason: "Another sign-in began." },
            "a second sign-in ends the first");
        challenge.Stall.SetResult();
        var erin = await Pumped(second, loop, owner, client);
        Assert(erin.Accepted && s.Identity?.UserId == "erin", $"the second goes on: erin ({erin.Reason})");

        // Too large, or not a sign-in message: refused before it's read — its exchange, if it says one, any round.
        var c = new RefClient();
        var large = new byte[AuthMessages.MaxMessage + 1];
        large[1] = 42;
        s.Auth(large);
        s.Auth([9, 43, 0, 0, 0]);
        for (var i = 0; i < 100 && c.Auths.Count < 2; i++)
        {
            DrainFrames(s, owner, c);
            await Task.Delay(5);
        }

        Assert(c.Auths.Select(a => (a.Status, a.Exchange, a.Round, a.Text)).SequenceEqual([
                (AuthOutcome.Rejected, 42u, AuthRecord.AnyRound, "That sign-in message is too large."),
                (AuthOutcome.Rejected, 43u, AuthRecord.AnyRound, "Not a sign-in message.")
            ]), $"refused before read: {string.Join("; ", c.Auths.Select(a => $"{a.Exchange} {a.Round} {a.Text}"))}");
        Assert(s.Identity?.UserId == "erin", "nothing changed");

        // Records: the identity as JSON, the round, the ticket.
        var record = AuthRecord.Of(AuthOutcome.Accepted, 7, 1, AuthRecord.Json(erin.Identity!, SessionPolicy.Default), erin.Ticket.Span);
        Assert(record[0] == Wire.Auth && BitConverter.ToUInt32(record, 2) == 7 && BitConverter.ToUInt16(record, 6) == 1 &&
               record.Length == AuthRecord.Header + BitConverter.ToUInt16(record, 8) + erin.Ticket.Length,
            "an Auth record: status, exchange, round, text, ticket");
        Assert(Encoding.UTF8.GetString(record, 10, BitConverter.ToUInt16(record, 8)).Contains("\"insecure\":true"),
            "its identity, as JSON");

        // The viewer shows who, by what, and insecure.
        Assert(s.Describe().Identity is { UserId: "erin", Insecure: true, Module: "passthrough" }, "the viewer has it");
    }

    /// <summary>An authentication provider of one function: its answer to each message.</summary>
    private sealed class FuncAuthenticator(string name, Func<AuthStep, AuthAnswer> answer) : IDataProvider, IAuthenticator
    {
        public ProviderInstance Instance { get; } = ProviderInstance.Start(name, At(name));

        public ValueTask<AuthAnswer> AuthenticateAsync(AuthStep step, CancellationToken cancellationToken) =>
            new(answer(step));

        public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken) => default;

        public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken) =>
            new(new HealthReport(Instance, ProviderStatus.Serving));
    }

    public static async Task SessionExpiry()
    {
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var loop = new MovableConnection(s);
        var client = new TaplineClient(loop);
        client.Bind(s.Id, s.Nonce);
        var warnings = new List<SessionNotice>();
        var ends = new List<SessionNotice>();
        client.SessionExpiring += warnings.Add;
        client.SessionEnded += ends.Add;
        var passthrough = publisher.ProvidePassthrough("north", PassthroughAuthenticator.AcceptAnyCredential,
            TimeSpan.FromMinutes(10));

        var alice = await Pumped(client.SignInAsync("north", "passthrough", "alice"u8.ToArray()), loop, owner, client);
        var expires = alice.Identity!.ExpiresAt!.Value;
        Assert(alice is { Accepted: true, Renewal: RenewalPolicy.Silent } &&
               alice.WarnAt == expires - TimeSpan.FromMinutes(2.5) && s.SessionPolicy.Renewal == RenewalPolicy.Silent,
            $"passthrough: renewed silently, warned a quarter of its lifetime ahead ({alice.Renewal}, {alice.WarnAt})");

        var topic = new LastValueSource { Label = "feed" };
        var provider = new TestProvider(topic)
        {
            Access = service => service == "home" ? ServiceAccess.RequiredIn("north") : ServiceAccess.In("north")
        };
        Provide(publisher, "home", "feeds", provider);
        Provide(publisher, "lobby", "feeds", provider);
        var home = await publisher.SubscribeAsync(s, "home");
        var lobby = await publisher.SubscribeAsync(s, "lobby");
        Assert(home is not null && lobby is not null, "subscribed: a required service, an optional one");

        // Nothing before its warning; then warned once — the policy, a correlation id — and, silent, renewed by the
        // client with its ticket: the same session, a later expiry, every subscription on, its providers told.
        s.CheckSession(expires - TimeSpan.FromMinutes(3));
        Pump(s, owner, client);
        Assert(warnings.Count == 0, "nothing before its warning");
        s.CheckSession(expires - TimeSpan.FromMinutes(2));
        s.CheckSession(expires - TimeSpan.FromMinutes(1.5)); // (once)
        var c = new RefClient();
        for (var i = 0; i < 400 && s.Identity!.ExpiresAt == expires; i++)
        {
            Pump(s, owner, client);
            await Task.Delay(5);
        }

        Assert(warnings.Count == 1 && warnings[0] is { Renewal: RenewalPolicy.Silent } w &&
               w.SessionId == alice.Identity.SessionId && w.ExpiresAt?.ToUnixTimeMilliseconds() == expires.ToUnixTimeMilliseconds() && w.CorrelationId.Version == 7,
            $"warned once: silent, its session and expiry, a UUIDv7 ({warnings.Count})");
        var renewed = s.Identity!;
        Assert(renewed.SessionId == alice.Identity.SessionId && renewed.ExpiresAt > expires &&
               loop.Auths.Last()[5] == AuthMessages.FlagRenewal && !home!.IsClosed && !lobby!.IsClosed,
            "renewed by its ticket, silently: the same session, a later expiry, every subscription on");
        for (var i = 0; i < 100 && provider.IdentityChanges.Count < 2; i++) await Task.Delay(5);
        Assert(provider.IdentityChanges.Count == 2 &&
               provider.IdentityChanges.All(x => x is { Lapsed: false, Identity: { } id } && id.ExpiresAt == renewed.ExpiresAt),
            $"its providers told of the renewal ({provider.IdentityChanges.Count})");
        Pump(s, owner, client);
        Assert(client.Identity?.ExpiresAt == renewed.ExpiresAt, "the client holds the renewed identity");

        // A fixed session: warned, with why; a renewal refused; at its expiry it lapses — what requires a sign-in ends,
        // the rest goes on anonymous, its providers told; the client told.
        var fixedPolicy = new SessionPolicy(RenewalPolicy.Fixed, TimeSpan.FromMinutes(1), "the practice's 10-minute rule");
        s.SetIdentity(renewed, fixedPolicy);
        var until = renewed.ExpiresAt!.Value;
        s.CheckSession(until - TimeSpan.FromSeconds(30));
        DrainFrames(s, owner, c);
        Assert(c.Sessions.Count == 1 && c.Sessions[0] is { Status: 0, Renewal: RenewalPolicy.Fixed } f &&
               f.Reason == "the practice's 10-minute rule" && f.ExpiresAt == until.ToUnixTimeMilliseconds(),
            "warned: fixed, saying why, when");
        var again = await Pumped(client.SignInAsync("north", "passthrough", "alice"u8.ToArray(), renewal: true), loop,
            owner, client);
        Assert(again is { Accepted: false } && again.Reason!.Contains("can't be renewed: the practice's 10-minute rule"),
            $"a fixed session can't be renewed ({again.Reason})");
        var told = provider.IdentityChanges.Count;
        s.CheckSession(until + TimeSpan.FromSeconds(1));
        Assert(s.Identity is null && home!.IsClosed && !lobby!.IsClosed,
            "lapsed: anonymous; the required service's subscription ended, the optional one on");
        for (var i = 0; i < 100 && provider.IdentityChanges.Count == told; i++) await Task.Delay(5);
        Assert(provider.IdentityChanges.Skip(told).SingleOrDefault() is { Lapsed: true, Identity: null, Service: "lobby" },
            "the optional service's provider told it's anonymous now");
        DrainFrames(s, owner, c);
        Assert(c.Ended.Any(e => e.Code == FaultCode.SessionExpired && e.From == home.Id) &&
               c.Sessions.Count == 2 && c.Sessions[1].Status == 1, "its client told: SessionExpired, the session ended");
        var refused = await Refused(publisher, s, "home");
        Assert(refused is { Kind: RefusalKind.Unauthenticated }, "lapsed: a required service refused");

        // The .NET client on a new session (the frames above went to the reference client): resumed by its renewed
        // ticket, then warned — reauthenticate: not renewed by itself — and ended.
        var s2 = NewOutlet(registry);
        var owner2 = new object();
        s2.Attach(owner2);
        loop.Outlet = s2;
        client.Bind(s2.Id, s2.Nonce); // (another session: the ticket resumes)
        for (var i = 0; i < 400 && s2.Identity is null; i++)
        {
            Pump(s2, owner2, client);
            await Task.Delay(5);
        }

        Assert(s2.Identity?.UserId == "alice", "resumed by its renewed ticket on a new session");
        var id2 = s2.Identity!;
        s2.SetIdentity(id2, new SessionPolicy(RenewalPolicy.Reauthenticate));
        s2.CheckSession(id2.ExpiresAt!.Value - TimeSpan.FromSeconds(10));
        s2.CheckSession(id2.ExpiresAt!.Value);
        Pump(s2, owner2, client);
        Assert(warnings.Count == 2 && warnings[1].Renewal == RenewalPolicy.Reauthenticate && ends.Count == 1 &&
               client.Identity is null, "the client: warned (reauthenticate: not renewed by itself), then ended");

        // A redirect: for a renewal alone, told to the client — the router to renew on, a ticket — nothing changed here.
        var east = new FuncAuthenticator("east", step => step.Renewal
            ? AuthAnswer.Redirect("router-b", "for-b"u8.ToArray())
            : AuthAnswer.Accept(new ClientIdentity("east", "erin", Guid.CreateVersion7(), "east", false,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1))));
        publisher.ProvideAuthentication("east", At("east"), east);
        var erin = await Pumped(client.SignInAsync("east", "east", "x"u8.ToArray()), loop, owner2, client);
        Assert(erin.Accepted && erin.Renewal == RenewalPolicy.Reauthenticate, "erin signed in, the default policy");
        var redirects = new List<string>();
        client.RenewalRedirected += (router, _) => redirects.Add(router);
        var moved = await Pumped(client.SignInAsync("east", "east", "x"u8.ToArray(), renewal: true), loop, owner2,
            client);
        Assert(moved is { Accepted: false, RedirectTo: "router-b" } && moved.Ticket.Span.SequenceEqual("for-b"u8) &&
               redirects.SequenceEqual(["router-b"]) && s2.Identity?.UserId == "erin",
            "a renewal redirected: the router, its ticket, told; the identity here as it was");

        // The timer: warned, then lapsed, by itself.
        var s3 = NewOutlet(registry);
        var owner3 = new object();
        s3.Attach(owner3);
        var now = DateTimeOffset.UtcNow;
        s3.SetIdentity(new ClientIdentity("north", "frank", Guid.CreateVersion7(), "host", false, now,
            now.AddMilliseconds(400)), new SessionPolicy(Warning: TimeSpan.FromMilliseconds(300)));
        var c3 = new RefClient();
        for (var i = 0; i < 200 && (s3.Identity is not null || c3.Sessions.Count < 2); i++)
        {
            DrainFrames(s3, owner3, c3);
            await Task.Delay(10);
        }

        DrainFrames(s3, owner3, c3);
        Assert(s3.Identity is null && c3.Sessions.Select(x => x.Status).SequenceEqual([(byte)0, (byte)1]),
            $"the timer: warned, then lapsed ({string.Join(",", c3.Sessions.Select(x => x.Status))})");
        Assert(s2.Describe().SessionPolicy is { Renewal: RenewalPolicy.Reauthenticate }, "the viewer has the policy");
    }

    public static async Task PassthroughConfiguration()
    {
        async Task<WebApplication> AppAsync(Dictionary<string, string?> settings, int opsPort)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.WebHost.UseUrls("http://127.0.0.1:0", $"http://127.0.0.1:{opsPort}");
            builder.Services.AddTapline(o => o.Ops.Port = opsPort);
            var app = builder.Build();
            app.MapTaplineViewer();
            await app.StartAsync();
            return app;
        }

        async Task<string?> FailsToStart(Dictionary<string, string?> settings)
        {
            try
            {
                await using var app = await AppAsync(settings, FreePort());
                await app.StopAsync();
                return null;
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
        }

        const string P = "Tapline:Authentication:Passthrough:";
        var opsPort = FreePort();
        await using (var app = await AppAsync(new Dictionary<string, string?>
                     {
                         [P + "default:Enable"] = "AcceptAnyCredential", // (as Tapline__Authentication__Passthrough__default__Enable)
                         [P + "north:Enable"] = "AcceptAnyCredential",
                         [P + "north:Lifetime"] = "01:00:00",
                         [P + "north:Renewal"] = "Fixed",
                         [P + "north:Reason"] = "the practice's rule",
                         [P + "south:Lifetime"] = "01:00:00" // (no Enable: off)
                     }, opsPort))
        {
            var publisher = app.Services.GetRequiredService<Publisher>();
            var realms = publisher.Describe().Realms!;
            Assert(realms.Select(r => (r.Realm, r.Passthrough)).SequenceEqual([("default", true), ("north", true)]),
                $"two realms in force, by their keys; one kept off ({string.Join("; ", realms)})");

            var registry = app.Services.GetRequiredService<OutletRegistry>();
            var s = NewOutlet(registry);
            s.Auth(AuthMessages.BeginOf(1, "north", "passthrough", "alice"u8));
            for (var i = 0; i < 200 && s.Identity is null; i++) await Task.Delay(5);
            Assert(s.Identity is { UserId: "alice", Realm: "north", Insecure: true } id &&
                   id.ExpiresAt - id.AuthenticatedAt == TimeSpan.FromHours(1) &&
                   s.SessionPolicy is { Renewal: RenewalPolicy.Fixed, Reason: "the practice's rule" },
                $"signed in to north: its lifetime, its policy ({s.Identity})");
            s.Auth(AuthMessages.BeginOf(2, "default", "passthrough", "bob"u8));
            for (var i = 0; i < 200 && s.Identity?.UserId != "bob"; i++) await Task.Delay(5);
            Assert(s.Identity is { Realm: "default", UserId: "bob" } && s.SessionPolicy.Renewal == RenewalPolicy.Silent,
                "and to default: renewed silently");

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{opsPort}") };
            var page = await http.GetStringAsync("/ops/realtime/");
            Assert(page.Contains("Sign-in realms") && page.Contains("yes — any user id taken as given"),
                "the router page lists them, passthrough marked");
            await app.StopAsync();
        }

        var near = await FailsToStart(new() { [P + "default:Enable"] = "true" });
        Assert(near?.Contains("enabled by \"AcceptAnyCredential\" alone") == true, $"a near miss doesn't start ({near})");
        var blank = await FailsToStart(new() { [P + " :Enable"] = "AcceptAnyCredential" });
        Assert(blank?.Contains("realm") == true, $"a blank realm doesn't start ({blank})");
    }

    private static async Task<SubscriptionRefusal?> Refused(Publisher publisher, Outlet outlet, string service)
    {
        try
        {
            await publisher.SubscribeAsync(outlet, service);
            return null;
        }
        catch (SubscriptionRefusedException e)
        {
            return e.Refusal;
        }
    }

    public static async Task SignInFromJs()
    {
        // Tap.Auth(outletId, nonce, bin) as the SignalR JS client's MessagePack encodes a Uint8Array — bin8, bin16 —
        // consumed in place and relayed; a spoofed nonce acted on by none.
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        publisher.ProvidePassthrough("north", PassthroughAuthenticator.AcceptAnyCredential);
        var s = NewOutlet(registry);
        var protocol = new RealtimeProtocol(BlazorPack(), registry);

        bool Consumed(byte[] bytes)
        {
            var input = new ReadOnlySequence<byte>(bytes);
            return protocol.TryParseMessage(ref input, new Binder(), out var m) && m is PingMessage && input.IsEmpty;
        }

        var spoof = AuthBytes(s.Id, s.Nonce ^ 1, AuthMessages.BeginOf(1, "north", "passthrough", "mallory"u8));
        Assert(Consumed(spoof), "a spoofed nonce: consumed");
        var small = AuthBytes(s.Id, s.Nonce, AuthMessages.BeginOf(2, "north", "passthrough", "alice"u8));
        Assert(Consumed(small), "bin8: consumed in place");
        for (var i = 0; i < 200 && s.Identity is null; i++) await Task.Delay(5);
        Assert(s.Identity?.UserId == "alice", $"bin8: relayed, alice signed in ({s.Identity})");

        var big = new string('b', 240); // a bin16 (with its trailing spaces, trimmed), past the small path's 96 bytes
        var large = AuthBytes(s.Id, s.Nonce,
            AuthMessages.BeginOf(3, "north", "passthrough", Encoding.UTF8.GetBytes(big + new string(' ', 60))));
        Assert(large.Length > 300 && large.AsSpan().IndexOf((byte)0xC5) > 0, "a bin16");
        Assert(Consumed(large), "bin16: consumed in place");
        for (var i = 0; i < 200 && s.Identity?.UserId != big; i++) await Task.Delay(5);
        Assert(s.Identity?.UserId == big, "bin16: relayed");
        var tooLarge = new ReadOnlySequence<byte>(AuthBytes(s.Id, s.Nonce, new byte[AuthMessages.MaxMessage + 64]));
        Assert(!ControlParser.TryConsume(ref tooLarge, registry), "past the limit: not ours (left to Blazor's hub)");

        // Over MagicOnion: ControlKind.Auth carries the message as its payload; the other controls carry none.
        var begin = AuthMessages.BeginOf(4, "north", "passthrough", "alice"u8);
        var control = MessagePack.MessagePackSerializer.Deserialize<Tapline.MagicOnion.Control>(
            MessagePack.MessagePackSerializer.Serialize(new Tapline.MagicOnion.Control
                { Kind = ControlKind.Auth, Payload = begin }));
        var ack = MessagePack.MessagePackSerializer.Deserialize<Tapline.MagicOnion.Control>(
            MessagePack.MessagePackSerializer.Serialize(new Tapline.MagicOnion.Control
                { Kind = ControlKind.Ack, A = 5, B = 6 }));
        var sync = MessagePack.MessagePackSerializer.Deserialize<Tapline.MagicOnion.Control>(
            MessagePack.MessagePackSerializer.Serialize(new Tapline.MagicOnion.Control
                { Kind = ControlKind.Sync, A = 9, B = uint.MaxValue }));
        Assert(control is { Kind: ControlKind.Auth } && control.Payload!.SequenceEqual(begin) &&
               ack is { Kind: ControlKind.Ack, A: 5, B: 6, Payload: null } &&
               sync is { Kind: ControlKind.Sync, A: 9, B: uint.MaxValue, Payload: null },
            "MagicOnion's Control: an Auth's payload round-trips; an Ack has none; a Sync its tap and id");
    }

    /// <summary>Sign-in messages and the router's answers, as C# writes them: what tapline.js must write and read alike.</summary>
    private static object AuthFixture()
    {
        var identity = new ClientIdentity("north", "alice", Guid.Parse("0198d6a2-7c41-7e3b-9f10-2b4c6d8e0a13"),
            "passthrough", true, DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000),
            DateTimeOffset.FromUnixTimeMilliseconds(1_790_028_800_000), new Dictionary<string, string> { ["role"] = "é" });
        return new
        {
            beginHex = Convert.ToHexString(AuthMessages.BeginOf(1, "north", "passthrough", "alice"u8)),
            renewalHex = Convert.ToHexString(AuthMessages.BeginOf(2, "north", "ticket", [1, 2, 3], true)),
            continueHex = Convert.ToHexString(AuthMessages.ContinueOf(1, [4, 5, 6])),
            cancelHex = Convert.ToHexString(AuthMessages.CancelOf(1)),
            context = Encoding.UTF8.GetString(AuthMessages.Context("north", 3, 77)),
            continuedHex = Convert.ToHexString(AuthRecord.Of(AuthOutcome.Continue, 1, 0, null, [9, 8, 7])),
            acceptedHex = Convert.ToHexString(AuthRecord.Of(AuthOutcome.Accepted, 1, 1, AuthRecord.Json(identity, new SessionPolicy(RenewalPolicy.Silent, TimeSpan.FromMinutes(5))),
                [0xAA, 0xBB])),
            refusedHex = Convert.ToHexString(AuthRecord.Of(AuthOutcome.Rejected, 1, AuthRecord.AnyRound,
                "No sign-in to 'south' here.", [])),
            redirectedHex = Convert.ToHexString(AuthRecord.Of(AuthOutcome.Redirected, 1, 0, "router-b", [0xCC])),
            expiringHex = Convert.ToHexString(SessionRecord.Of(SessionRecord.Expiring,
                new SessionPolicy(RenewalPolicy.Silent, Reason: "é"), identity)),
            fixedHex = Convert.ToHexString(SessionRecord.Of(SessionRecord.Expiring,
                new SessionPolicy(RenewalPolicy.Fixed, Reason: "the rule"), identity)),
            endedHex = Convert.ToHexString(SessionRecord.Of(SessionRecord.Ended, SessionPolicy.Default, identity))
        };
    }

    /// <summary><c>Tap.Auth(outletId, nonce, bytes)</c> as the SignalR JS client writes it: bin8, bin16 or bin32 by length.</summary>
    private static byte[] AuthBytes(long outletId, long nonce, byte[] message)
    {
        var head = ControlBytes("Tap.Auth", outletId, nonce, 0);
        // the head's body without its varint length, its argument count 3 and the placeholder 0 dropped
        var start = 1;
        while ((head[start - 1] & 0x80) != 0) start++;
        var body = new List<byte>(head[start..^1]);
        if (message.Length < 256) body.AddRange([0xC4, (byte)message.Length]);
        else if (message.Length < 65536) body.AddRange([0xC5, (byte)(message.Length >> 8), (byte)message.Length]);
        else
            body.AddRange([
                0xC6, (byte)(message.Length >> 24), (byte)(message.Length >> 16), (byte)(message.Length >> 8),
                (byte)message.Length
            ]);
        body.AddRange(message);
        var result = new List<byte>();
        var v = (uint)body.Count;
        while (v >= 0x80)
        {
            result.Add((byte)(v | 0x80));
            v >>= 7;
        }

        result.Add((byte)v);
        result.AddRange(body);
        return result.ToArray();
    }
}
