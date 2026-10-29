using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion.Client;
using MagicOnion.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tapline;
using Tapline.Client;
using Tapline.MagicOnion.Providers;

/// <summary>
///     A data provider in another process, over the link: step 1 answered there, its topics mirrored into the router's
///     own copies, its routes' notices relayed, its mailboxes, health, heartbeats and drains carried back.
/// </summary>
internal static partial class T
{
    /// <summary>The link in process: the control stream over channels, the topics' stream pumped straight from the provider's outlet.</summary>
    public static async Task ProviderLinkInProcess()
    {
        var endpoint = new ProviderEndpoint(new Uri("tcp://provider.test:7400"));
        var provider = new LinkedProvider(ProviderInstance.Start("linked", endpoint));
        await using var rig = await LinkRig.StartAsync(provider, endpoint);
        await LinkBasics(rig, provider);
    }

    /// <summary>The same over MagicOnion: the provider served by Kestrel (HTTP/2, cleartext), the router a gRPC client of it.</summary>
    public static async Task ProviderLinkMagicOnion()
    {
        var port = FreePort();
        var endpoint = new ProviderEndpoint(new Uri($"http://127.0.0.1:{port}"));
        var provider = new LinkedProvider(ProviderInstance.Start("linked", endpoint));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddMagicOnion();
        builder.Services.AddTaplineProviderLink(endpoint, _ => provider);
        await using var app = builder.Build();
        app.MapTaplineProviderLink();
        await app.StartAsync();
        try
        {
            await using var rig = await LinkRig.StartAsync(provider, endpoint,
                provide: router => router.ProvideRemote("svc", endpoint.Address));
            await LinkBasics(rig, provider);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    ///     Identities over the link (MagicOnion, so its messages are serialized): what the provider declares, asked once and
    ///     kept; a required service refused to an anonymous subscriber by the router, step 1 not sent; the identity carried
    ///     whole to the provider — realm, user, session, module, insecure, times, claims; the provider's Forbidden carried
    ///     back to the router and on to the client.
    /// </summary>
    public static async Task ProviderLinkIdentity()
    {
        var port = FreePort();
        var endpoint = new ProviderEndpoint(new Uri($"http://127.0.0.1:{port}"));
        var provider = new LinkedProvider(ProviderInstance.Start("linked", endpoint))
            { Access = ServiceAccess.RequiredIn("north") };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddMagicOnion();
        builder.Services.AddTaplineProviderLink(endpoint, _ => provider);
        await using var app = builder.Build();
        app.MapTaplineProviderLink();
        await app.StartAsync();
        try
        {
            await using var rig = await LinkRig.StartAsync(provider, endpoint,
                provide: router => router.ProvideRemote("svc", endpoint.Address));
            var (router, outlet, owner, client) = (rig.Router, rig.Outlet, rig.Owner, rig.Client);
            var told = new List<Refusal>();
            client.Refused += told.Add;

            var anonymous = await ThrewAsync<SubscriptionRefusedException>(() => router.SubscribeAsync(outlet, "svc", "book").AsTask());
            Assert(anonymous?.Refusal.Kind == RefusalKind.Unauthenticated && provider.Last is null,
                "anonymous: refused by the router, Unauthenticated; no step 1 sent");

            var claims = new Dictionary<string, string> { ["role"] = "gp", ["name"] = "Dr Alice Ng" };
            var alice = new ClientIdentity("north", "alice", Guid.CreateVersion7(), "passthrough", true,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), claims);
            outlet.SetIdentity(alice);
            var tap = await router.SubscribeAsync(outlet, "svc", "book");
            var seen = provider.Last?.Identity;
            Assert(tap is not null && seen is not null && seen.Realm == alice.Realm && seen.UserId == alice.UserId &&
                   seen.SessionId == alice.SessionId && seen.Module == alice.Module && seen.Insecure &&
                   seen.AuthenticatedAt == alice.AuthenticatedAt && seen.ExpiresAt == alice.ExpiresAt &&
                   seen.Claims is { Count: 2 } c && c["role"] == "gp" && c["name"] == "Dr Alice Ng",
                $"the identity carried whole to the provider ({seen})");
            Assert(provider.Last!.Access is { Realm: "north", Required: true }, "and the declaration, as acted on");
            Assert(provider.Last.Limits.IsNone && tap!.MinInterval == TimeSpan.Zero, "no limits: none carried");


            outlet.SetIdentity(alice with { ExpiresAt = alice.ExpiresAt!.Value.AddHours(1) });
            await Until(() => !provider.IdentityChanges.IsEmpty, () => { }, "a renewal told over the link");
            Assert(provider.IdentityChanges.Single() is { Lapsed: false, Service: "svc", Identity: { UserId: "alice" } r } &&
                   r.ExpiresAt == alice.ExpiresAt.Value.AddHours(1) && r.SessionId == alice.SessionId,
                "the provider told of the renewal: the same session, the later expiry");

            var forbidden = await ThrewAsync<SubscriptionRefusedException>(() => router.SubscribeAsync(outlet, "svc", "forbid").AsTask());
            Assert(forbidden?.Refusal is { Kind: RefusalKind.Forbidden, Reason: "not this book" } &&
                   forbidden.Refusal.CorrelationId == provider.Refused,
                $"the provider's Forbidden carried back, with its correlation id ({forbidden?.Refusal})");
            await Until(() => told.Any(r => r.Kind == RefusalKind.Forbidden), () => Pump(outlet, owner, client),
                "and on to the client, of its kind");
            Assert(told.Any(r => r.Kind == RefusalKind.Unauthenticated), "the router's Unauthenticated too");
            Assert(provider.AccessAsks == 1, $"the declaration asked once, and kept ({provider.AccessAsks})");

            // The subscriber's limits carried: its own rate, set on the router's route too, and its connection's cap.
            outlet.SetStreamingRate(32 * 1024);
            var paced = await router.SubscribeAsync(outlet, "svc", "book",
                new SubscribeOptions { MinInterval = TimeSpan.FromMilliseconds(400) });
            Assert(provider.Last!.Limits is { BytesPerSecond: 32 * 1024 } carried &&
                   carried.MinInterval == TimeSpan.FromMilliseconds(400) &&
                   paced!.MinInterval == TimeSpan.FromMilliseconds(400),
                $"the limits carried to the provider ({provider.Last.Limits}), the rate on the router's route");

            // Keys only added: a link skips keys it doesn't know (as one before the limits skips them), and reads a key not
            // sent (from one before them) as none.
            var later = SubscribeAskedShape(9, limits: true);
            var read = MessagePack.MessagePackSerializer.Deserialize<SubscribeAsked>(later);
            Assert(read is { Request: 7, Service: "svc", Parameters: "book", Limits: { BytesPerSecond: 1024 } } &&
                   read.Limits.MinIntervalTicks == 400 * TimeSpan.TicksPerMillisecond,
                "keys it doesn't know skipped (a later one of the limits', and the request's 7th and 8th): the rest read");
            var before = MessagePack.MessagePackSerializer.Deserialize<SubscribeAsked>(SubscribeAskedShape(6, limits: false));
            Assert(before is { Request: 7, Limits: null } && (before.Limits?.ToLimits() ?? default).IsNone,
                "from a link before the limits (keys 0–5): none");
            Assert(new LimitsCarried { MinIntervalTicks = 0, BytesPerSecond = 0, }.ToLimits().IsNone &&
                   LimitsCarried.Of(default) is null, "none: nothing sent");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    ///     Sign-ins relayed over the link (MagicOnion): passthrough, served in the provider's process — the identity it
    ///     asserts, insecure, set on the router's connection and told to the client with its ticket; the ticket taken back;
    ///     a module of two rounds, the connection's context and its current identity carried whole; the provider's refusal.
    /// </summary>
    public static async Task ProviderLinkSignIn()
    {
        var port = FreePort();
        var endpoint = new ProviderEndpoint(new Uri($"http://127.0.0.1:{port}"));
        var provider = new LinkedAuthenticator(ProviderInstance.Start("auth", endpoint),
            new PassthroughAuthenticator("north", PassthroughAuthenticator.AcceptAnyCredential));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddMagicOnion();
        builder.Services.AddTaplineProviderLink(endpoint, _ => provider);
        await using var app = builder.Build();
        app.MapTaplineProviderLink();
        await app.StartAsync();
        try
        {
            await using var rig = await LinkRig.StartAsync(provider, endpoint,
                provide: router => router.ProvideRemoteAuthentication("north", endpoint.Address));
            var (outlet, owner, client) = (rig.Outlet, rig.Owner, rig.Client);

            async Task<SignInResult> Signed(Task<SignInResult> signing)
            {
                await Until(() => signing.IsCompleted, () => Pump(outlet, owner, client), "the sign-in answered");
                return await signing;
            }

            var alice = await Signed(client.SignInAsync("north", "passthrough", "alice"u8.ToArray()));
            Assert(alice is { Accepted: true, Identity: { UserId: "alice", Insecure: true, Module: "passthrough" } } &&
                   alice.Renewal == RenewalPolicy.Silent && outlet.SessionPolicy.Renewal == RenewalPolicy.Silent &&
                   outlet.Identity?.SessionId == alice.Identity.SessionId && !alice.Ticket.IsEmpty,
                $"signed in over the link: alice, insecure, on the router's connection, with a ticket ({alice.Reason})");
            var step = provider.Steps.Single();
            Assert(step.Router.Name == rig.Router.Router.Name && step.Round == 0 && step.Current is null &&
                   step.Context.Span.SequenceEqual(client.SignInContext("north")),
                "the provider was given the router, round 0, the connection's context, nobody signed in yet");

            var resumed = await Signed(client.SignInAsync("north", "ticket", alice.Ticket));
            Assert(resumed is { Accepted: true } && resumed.Identity!.SessionId == alice.Identity.SessionId &&
                   provider.Steps[^1].Current?.UserId == "alice",
                "the ticket taken back over the link: the same sign-in; the current identity carried");

            var refused = await Signed(client.SignInAsync("north", "telepathy", "alice"u8.ToArray()));
            Assert(refused is { Accepted: false } && refused.Reason!.Contains("telepathy"),
                $"the provider's refusal carried back ({refused.Reason})");

            provider.Inner = new ChallengeAuthenticator();
            var context = client.SignInContext("north")!;
            var dana = await Signed(client.SignInAsync("north", "challenge", "hi"u8.ToArray(),
                (m, _) => new ValueTask<ReadOnlyMemory<byte>>(System.Security.Cryptography.HMACSHA256.HashData(m.Span, context))));
            Assert(dana is { Accepted: true, Identity: { UserId: "dana", Insecure: false } } &&
                   dana.Identity.Claims?["role"] == "nurse" && outlet.Identity?.UserId == "dana",
                $"two rounds over the link, bound to the connection: dana, claims carried ({dana.Reason})");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    ///     A renewal redirected, end to end over MagicOnion (the client's sign-in messages through the stream's control
    ///     messages): router a signs alice in and answers her renewal with "renew on b" and a ticket b takes (a key the two
    ///     share). A client of two routers moves there — make before break — the ticket presented on its new connection
    ///     before the app subscribes: alice on b, the same sign-in. A redirect to a router it doesn't know: not moved.
    /// </summary>
    public static async Task RenewalRedirect()
    {
        var tickets = new AuthTickets(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var target = "elsewhere";

        async Task<WebApplication> RouterAsync(string name, int port, Action<Publisher> provide)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
                k.Listen(IPAddress.Loopback, port, o => o.Protocols = HttpProtocols.Http2));
            builder.Services.AddSingleton(RouterInstance.Start(name));
            builder.Services.AddMagicOnion();
            Tapline.MagicOnion.Server.TaplineMagicOnionExtensions.AddTaplineMagicOnion(builder.Services);
            var app = builder.Build();
            Tapline.MagicOnion.Server.TaplineMagicOnionExtensions.MapTaplineMagicOnion(app);
            provide(app.Services.GetRequiredService<Publisher>());
            await app.StartAsync();
            return app;
        }

        int portA = FreePort(), portB = FreePort();
        await using var a = await RouterAsync("a", portA, router => router.ProvideAuthentication("north",
            At("auth-a"), new FuncAuthenticator("auth-a", step =>
            {
                if (step.Renewal) return AuthAnswer.Redirect(target, tickets.Issue(step.Current!, DateTimeOffset.UtcNow.AddHours(1)));
                var alice = new ClientIdentity("north", "alice", Guid.CreateVersion7(), "a-module", false,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
                return AuthAnswer.Accept(alice, tickets.Issue(alice, alice.ExpiresAt!.Value));
            })));
        var passthroughB = new PassthroughAuthenticator("north", PassthroughAuthenticator.AcceptAnyCredential,
            tickets: tickets);
        await using var b = await RouterAsync("b", portB,
            router => router.ProvideAuthentication("north", passthroughB.Endpoint, passthroughB));
        try
        {
            var routers = new Tapline.MagicOnion.TaplineRouters([
                new Tapline.MagicOnion.TaplineRouter(GrpcChannel.ForAddress($"http://127.0.0.1:{portA}"), Name: "a"),
                new Tapline.MagicOnion.TaplineRouter(GrpcChannel.ForAddress($"http://127.0.0.1:{portB}"), Name: "b")
            ]) { Move = _ => Task.CompletedTask };
            var moves = new List<Tapline.MagicOnion.RouterMove>();
            var failures = new List<string>();
            routers.Moved += moves.Add;
            routers.MoveFailed += failures.Add;
            using var stop = new CancellationTokenSource();
            var run = routers.RunAsync(stop.Token);
            await Until(() => routers.Current is { Session: not null, Client.Reachable: true }, () => { }, "bound on a");

            var first = routers.Current;
            var signedIn = await first.Client.SignInAsync("north", "a-module", "alice"u8.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(5));
            var sessionsA = a.Services.GetRequiredService<Tapline.MagicOnion.Server.TaplineStreamSessions>();
            var (idA, nonceA) = first.Session!.Value;
            Assert(signedIn.Accepted && sessionsA.Find(idA, nonceA)?.Identity?.UserId == "alice",
                $"signed in on a, through the stream's control messages ({signedIn.Reason})");

            var unknown = await first.Client.SignInAsync("north", "a-module", "alice"u8.ToArray(), renewal: true)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => failures.Count > 0, () => { }, "a redirect to a router not known here: given up");
            Assert(unknown.RedirectTo == "elsewhere" && failures[0].Contains("'elsewhere'") && moves.Count == 0 &&
                   ReferenceEquals(routers.Current, first), "not moved: it stays on a");

            target = "b";
            var redirected = await first.Client.SignInAsync("north", "a-module", "alice"u8.ToArray(), renewal: true)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => moves.Count > 0, () => { }, "moved to b");
            var now = routers.Current;
            var sessionsB = b.Services.GetRequiredService<Tapline.MagicOnion.Server.TaplineStreamSessions>();
            var (idB, nonceB) = now.Session!.Value;
            Assert(redirected.RedirectTo == "b" && moves[0].Router.Name == "b" && !ReferenceEquals(now, first) &&
                   now.Client.Identity?.UserId == "alice" &&
                   now.Client.Identity.SessionId == signedIn.Identity!.SessionId &&
                   sessionsB.Find(idB, nonceB)?.Identity?.UserId == "alice",
                "moved to b: alice there before the app subscribed, by the redirect's ticket — the same sign-in");
            stop.Cancel();
            await run;
        }
        finally
        {
            await a.StopAsync();
            await b.StopAsync();
        }
    }

    /// <summary>An authentication provider served over the link: its own instance, its sign-ins another's (switchable).</summary>
    private sealed class LinkedAuthenticator(ProviderInstance instance, IAuthenticator inner) : IDataProvider, IAuthenticator
    {
        public readonly List<AuthStep> Steps = [];
        public IAuthenticator Inner = inner;

        public ValueTask<AuthAnswer> AuthenticateAsync(AuthStep step, CancellationToken cancellationToken)
        {
            lock (Steps) Steps.Add(step);
            return Inner.AuthenticateAsync(step, cancellationToken);
        }

        public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken) => default;

        public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken) =>
            new(new HealthReport(instance, ProviderStatus.Serving));
    }

    /// <summary>
    ///     Over MagicOnion, a table whose records are larger than a stream's out-of-band threshold (16 KB by default) and
    ///     than an outlet's own frame buffer (64 KB): its snapshot and a delta as large travel in band, in full-size frames,
    ///     as the link's sessions send them — a router fetches nothing out of band over the link.
    /// </summary>
    public static async Task ProviderLinkLargeTable()
    {
        var port = FreePort();
        var endpoint = new ProviderEndpoint(new Uri($"http://127.0.0.1:{port}"));
        var provider = new LinkedProvider(ProviderInstance.Start("linked", endpoint));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddMagicOnion();
        builder.Services.AddTaplineProviderLink(endpoint, _ => provider);
        await using var app = builder.Build();
        app.MapTaplineProviderLink();
        await app.StartAsync();
        try
        {
            // (The router's own client in band too: it fetches nothing out of band here, and the link is what's tested.)
            await using var rig = await LinkRig.StartAsync(provider, endpoint,
                provide: router => router.ProvideRemote("svc", endpoint.Address), client: o => o.OutOfBandThreshold = 0);
            var (router, outlet, owner, client) = (rig.Router, rig.Outlet, rig.Owner, rig.Client);
            provider.SetMany(10_000, i => i); // (a record of 10,000 rows: about 130 KB)
            var tap = await router.SubscribeAsync(outlet, "svc", "book"); // (its copy's first state: across the link)
            Assert(tap is not null, "step 1 answered, the router's copy of the book made");
            TableReplica? replica = null;
            client.Subscribe(tap!.Binding, new SubscriptionHandler { Table = (_, t, _) => replica = t });
            await Until(() => Same(replica, provider.Book), () => Pump(outlet, owner, client),
                "a snapshot of 10,000 rows (over 64 KB), mirrored");
            provider.SetMany(10_000, i => i * 7);
            await Until(() => Same(replica, provider.Book), () => Pump(outlet, owner, client),
                "every row changed in one update, mirrored");
        }
        finally
        {
            await app.StopAsync();
        }
    }


    /// <summary>What both runs check: a table and a value mirrored, its changes, refusals, notices, health, heartbeats, a rendezvous.</summary>
    private static async Task LinkBasics(LinkRig rig, LinkedProvider provider)
    {
        var (router, outlet, owner, client) = (rig.Router, rig.Outlet, rig.Owner, rig.Client);
        for (var i = 0; i < 20; i++) provider.Set(i, i * 10);

        // Step 1, answered by the provider: the router's route on its copy of the book, the copy's first state its own.
        var tap = (await router.SubscribeAsync(outlet, "svc", "book"))!;
        Assert(tap.Provider?.Id == provider.Instance.Id, "the route's held by the provider's instance");
        TableReplica? replica = null;
        var sub = client.Subscribe(tap.Binding, new SubscriptionHandler { Table = (_, t, _) => replica = t });
        await Until(() => Same(replica, provider.Book), () => Pump(outlet, owner, client), "the book, mirrored");
        await Until(() => provider.Notices.Any(n => n is Activation a && a.Route == provider.Last!.Route && a.Epoch == provider.Epoch),
            () => Pump(outlet, owner, client), "step 2 relayed to the provider: its route (as its request named it), its epoch");

        // Its changes, as the provider makes them.
        provider.Set(3, 333);
        provider.Delete(7);
        provider.Set(42, 420);
        await Until(() => Same(replica, provider.Book), () => Pump(outlet, owner, client), "the book's changes, mirrored");

        // A rendezvous after a change: the client has the change when it's handed the id.
        var correlation = Guid.CreateVersion7().ToByteArray();
        var at = provider.Set(5, 555);
        var seen = -1L;
        client.Rendezvous += (id, _) =>
        {
            if (id.Span.SequenceEqual(correlation) && replica!.TryGetRow<LinkKey, long>(new LinkKey(5), out var row))
                seen = row;
        };
        Assert(provider.Last!.Mailbox.TryRendezvous(correlation, at), "the rendezvous relayed");
        await Until(() => seen >= 0, () => Pump(outlet, owner, client), "the rendezvous handed over");
        Assert(seen == 555, $"after the change it waited on ({seen})");

        // A second route shares the copy (and the provider's one tap for this router).
        var second = (await router.SubscribeAsync(outlet, "svc", "book"))!;
        Assert(ReferenceEquals(second.Source, tap.Source) && provider.Book.Routes == 1,
            $"one copy here, one tap there ({provider.Book.Routes} at the provider)");

        // Refused, with the provider's reason and correlation id; NACKed.
        var refused = await ThrewAsync<SubscriptionRefusedException>(() => router.SubscribeAsync(outlet, "svc", "refuse").AsTask());
        Assert(refused?.Refusal.CorrelationId == provider.Refused && refused.Refusal.Reason == "no such book",
            $"refused as the provider said, its correlation id the same ({refused?.Refusal.CorrelationId}, {provider.Refused})");
        var elsewhere = await ThrewAsync<SubscriptionRefusedException>(() => router.SubscribeAsync(outlet, "svc", "elsewhere").AsTask());
        Assert(elsewhere?.Refusal is { Kind: RefusalKind.Elsewhere, Reason: "not the writer" } &&
               elsewhere.Refusal.CorrelationId == provider.Refused,
            $"served elsewhere, its kind carried across (no other instance here: told) ({elsewhere?.Refusal.Kind})");

        // A value: mirrored, its changes too.
        provider.Value.Publish("one"u8);
        var valueTap = (await router.SubscribeAsync(outlet, "svc", "value"))!;
        var value = "";
        client.Subscribe(valueTap.Binding, new SubscriptionHandler { Value = (_, v) => value = System.Text.Encoding.UTF8.GetString(v.Span) });
        await Until(() => value == "one", () => Pump(outlet, owner, client), "the value, mirrored");
        provider.Value.Publish("two"u8);
        await Until(() => value == "two", () => Pump(outlet, owner, client), "the value's change, mirrored");

        // Health and heartbeats across the link.
        var report = await ((IProviderHealth)rig.Remote).CheckHealthAsync(new HealthProbe(router.Router, provider.Instance), default);
        Assert(report.Instance == provider.Instance && report.Status == ProviderStatus.Serving && report.Load == 0.42,
            $"its health, as it reports it, its load too ({report})");
        var beat = await provider.Channel!.HeartbeatAsync(provider.Instance, router.Router, default);
        Assert(beat.Router == router.Router && beat.KnowsInstance, $"its heartbeat answered by the router ({beat})");
        var move = await provider.Channel!.MoveAsync(provider.Instance, null, null, TimeSpan.Zero, TimeSpan.Zero, "link test", default);
        Assert(move is { Accepted: false, Refused: { } nowhere } && nowhere.Contains("nowhere"),
            $"its move asked across the link, the router's refusal back ({move})");

        // Unsubscribed: the provider told, with its epoch and the routes left; the last lets its tap go there.
        sub.Dispose();
        tap.Dispose();
        await Until(() => provider.Notices.Any(n => n is Unsubscription { Remaining: 1 }), () => Pump(outlet, owner, client),
            "the first route's end relayed: one left");
        second.Dispose();
        await Until(() => provider.Notices.Any(n => n is Unsubscription { Remaining: 0 } u && u.Epoch == provider.Epoch),
            () => Pump(outlet, owner, client), "the last route's end relayed, with the epoch it was accepted at");
        await Until(() => provider.Book.Routes == 0, () => Pump(outlet, owner, client), "the provider's tap for the router let go");
    }

    private static bool Same(TableReplica? replica, Table<LinkKey, long> book)
    {
        if (replica is null) return false;
        var rows = new List<KeyValuePair<LinkKey, long>>();
        book.CopyTo(rows);
        return replica.Count == rows.Count && rows.All(kv => replica.TryGetRow<LinkKey, long>(kv.Key, out var row) && row == kv.Value);
    }

    /// <summary>
    ///     Step 1 as MessagePack, written by hand: its first <paramref name="keys" /> keys (6: as a link knew it before the
    ///     subscriber's limits), with <paramref name="limits" /> the limits at key 6 (400 ms, 1 KiB/s, and a key of theirs
    ///     added later), keys past 6 unknown values.
    /// </summary>
    private static byte[] SubscribeAskedShape(int keys, bool limits)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var w = new MessagePack.MessagePackWriter(buffer);
        w.WriteArrayHeader(keys);
        w.Write(7L);
        w.Write("svc");
        w.Write("book");
        w.WriteNil();
        w.Write(false);
        w.WriteNil();
        for (var k = 6; k < keys; k++)
            if (k == 6 && limits)
            {
                w.WriteArrayHeader(3);
                w.Write(400 * TimeSpan.TicksPerMillisecond);
                w.Write(1024L);
                w.Write("a key added later");
            }
            else w.Write("a key added later");
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    ///     Waits for <paramref name="done" />, <paramref name="each" /> run before each look, up to <paramref name="ms" />
    ///     (10 s: what it waits for comes in milliseconds — the deadline is for a loaded machine); throws, saying what, if not.
    /// </summary>
    private static async Task Until(Func<bool> done, Action each, string what, int ms = 10_000)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            each();
            if (done()) return;
            await Task.Delay(5);
        }

        throw new Exception($"Not within {ms} ms: {what}");
    }

    private static async Task<TException?> ThrewAsync<TException>(Func<Task> run) where TException : Exception
    {
        try
        {
            await run();
            return null;
        }
        catch (TException e)
        {
            return e;
        }
    }

    /// <summary>
    ///     Step 1 ending as it does in process, across the link. Timed out at the router before the provider answered: its
    ///     late accept let go there, the provider told. NACKed: unavailable, the router refused by that endpoint, what it
    ///     held there closed, and let go there untold.
    /// </summary>
    public static async Task ProviderLinkStepEnds()
    {
        var endpoint = new ProviderEndpoint(new Uri("tcp://station.test:7400"));
        var topic = new Table<LinkKey, long>("station");
        var station = new TestProvider(topic) { Instance = ProviderInstance.Start("station", endpoint) };
        await using var rig = await LinkRig.StartAsync(station, endpoint, router =>
        {
            router.SubscribeTimeout = TimeSpan.FromMilliseconds(300);
            router.ProviderHeartbeatInterval = TimeSpan.Zero;
        });
        var (router, outlet) = (rig.Router, rig.Outlet);

        // Timed out: the provider accepts late, after the router gave up.
        station.Instead = async (request, _) =>
        {
            await Task.Delay(700);
            station.Epoch = request.Accept(topic, station.Instance!);
        };
        var timedOut = await ThrewAsync<ServiceUnavailableException>(() => router.SubscribeAsync(outlet, "svc").AsTask());
        Assert(timedOut is not null, "step 1 past the router's timeout: unavailable");
        await Until(() => station.Relayed.Any(n => n is Unsubscription { Remaining: 0 } u && u.Epoch == station.Epoch),
            () => { }, "the late accept let go there, as an unsubscribe");
        Assert(topic.Routes == 0, $"its tap there let go ({topic.Routes})");

        // NACKed, with a route held there.
        station.Instead = null;
        var held = (await router.SubscribeAsync(outlet, "svc"))!;
        held.Activate();
        await Until(() => station.Relayed.Any(n => n is Activation), () => { }, "the held route's activation relayed");
        var told = station.Relayed.Count(n => n is Unsubscription);
        (ProviderEndpoint Endpoint, ProviderInstance? Instance)? refusedBy = null;
        router.RefusedBy += (e, i) => refusedBy = (e, i);
        station.Instead = (request, _) =>
        {
            request.Nack();
            return default;
        };
        var nacked = await ThrewAsync<ServiceUnavailableException>(() => router.SubscribeAsync(outlet, "svc").AsTask());
        Assert(nacked is not null && refusedBy?.Endpoint == endpoint,
            $"NACKed: unavailable, refused by its endpoint ({refusedBy})");
        await Until(() => held.IsClosed, () => { }, "what it held there closed");
        Assert(held.Completion.Result?.Code == FaultCode.ServiceDown, $"…as down ({held.Completion.Result?.Code})");
        await Until(() => topic.Routes == 0, () => { }, "let go there too");
        await Task.Delay(30);
        Assert(station.Relayed.Count(n => n is Unsubscription) == told, "…untold, as in process");
    }

    /// <summary>
    ///     The provider's own drain, across the link: one way, with no step 1s to it after; its routes let go as drained,
    ///     not down; nothing relayed for them (it asked); its taps there let go.
    /// </summary>
    public static async Task ProviderLinkDrain()
    {
        var endpoint = new ProviderEndpoint(new Uri("tcp://station.test:7400"));
        var topic = new Table<LinkKey, long>("station");
        var station = new TestProvider(topic) { Instance = ProviderInstance.Start("station", endpoint) };
        await using var rig = await LinkRig.StartAsync(station, endpoint,
            router => router.ProviderHeartbeatInterval = TimeSpan.Zero);
        var (router, outlet) = (rig.Router, rig.Outlet);
        var taps = new List<Tap>();
        for (var i = 0; i < 3; i++)
        {
            var t = (await router.SubscribeAsync(outlet, "svc"))!;
            t.Activate();
            taps.Add(t);
        }

        await Until(() => station.Relayed.Count(n => n is Activation) == 3, () => { }, "the routes' activations relayed");
        var (asked, told) = (station.Subscribes, station.Relayed.Count(n => n is Unsubscription));
        station.Status = ProviderStatus.Draining;
        await station.Routers.DrainAsync(TimeSpan.FromMilliseconds(300));
        await Until(() => taps.All(t => t.IsClosed), () => { }, "its routes let go over the drain");
        Assert(taps.All(t => t.Completion.Result?.Code == FaultCode.ServiceDrained), "…as drained, not down");
        var after = await ThrewAsync<ServiceUnavailableException>(() => router.SubscribeAsync(outlet, "svc").AsTask());
        Assert(after is not null && station.Subscribes == asked, "no step 1s to it after: one way");
        await Until(() => topic.Routes == 0, () => { }, "its tap there let go");
        await Task.Delay(30);
        Assert(station.Relayed.Count(n => n is Unsubscription) == told, "nothing relayed for the routes it let go: it asked");
    }

    /// <summary>
    ///     The control stream broken, then opened again. The topics' stream carries on meanwhile, and health checks go
    ///     unanswered. A route's end is owed meanwhile, and told once the stream's open again. A step 1 after lands on the
    ///     same copy.
    /// </summary>
    public static async Task ProviderLinkControlBreak()
    {
        var endpoint = new ProviderEndpoint(new Uri("tcp://provider.test:7400"));
        var provider = new LinkedProvider(ProviderInstance.Start("linked", endpoint));
        await using var rig = await LinkRig.StartAsync(provider, endpoint,
            router => router.ProviderHeartbeatInterval = TimeSpan.Zero);
        var (router, outlet, owner, client) = (rig.Router, rig.Outlet, rig.Owner, rig.Client);
        for (var i = 0; i < 10; i++) provider.Set(i, i);
        var first = (await router.SubscribeAsync(outlet, "svc", "book"))!;
        var second = (await router.SubscribeAsync(outlet, "svc", "book"))!;
        TableReplica? replica = null;
        client.Subscribe(first.Binding, new SubscriptionHandler { Table = (_, t, _) => replica = t });
        await Until(() => Same(replica, provider.Book), () => Pump(outlet, owner, client), "mirrored");

        rig.Loop!.Break();
        await Until(() => !rig.Remote.IsUp, () => { }, "the control stream down");
        provider.Set(1, 111);
        await Until(() => Same(replica, provider.Book), () => Pump(outlet, owner, client),
            "the topics' stream carries on meanwhile");
        var probe = new HealthProbe(router.Router, provider.Instance);
        var unanswered = await ThrewAsync<InvalidOperationException>(() =>
            ((IProviderHealth)rig.Remote).CheckHealthAsync(probe, default).AsTask());
        Assert(unanswered is not null, "a health check unanswered meanwhile");
        second.Dispose();
        await Task.Delay(50);
        Assert(!provider.Notices.Any(n => n is Unsubscription), "a route's end not told while the stream's down");

        rig.Loop.Heal();
        await Until(() => rig.Remote.IsUp, () => { }, "the control stream open again");
        await Until(() => provider.Notices.Any(n => n is Unsubscription { Remaining: 1 }), () => Pump(outlet, owner, client),
            "the owed end told once it's open again");
        var report = await ((IProviderHealth)rig.Remote).CheckHealthAsync(probe, default);
        Assert(report.Instance == provider.Instance, "health checks answered again");
        var third = (await router.SubscribeAsync(outlet, "svc", "book"))!;
        Assert(ReferenceEquals(third.Source, first.Source), "a step 1 after: onto the same copy");
    }

    /// <summary>
    ///     The provider's process gone. Its checks go unanswered: degraded past ProviderDegradedAfter, dead past
    ///     ProviderTimeout. Its routes go down, each client shown the provider's stale data (its policy, which crossed at
    ///     the accept) with its payload, and told the service is down.
    /// </summary>
    public static async Task ProviderLinkDeath()
    {
        var endpoint = new ProviderEndpoint(new Uri("tcp://station.test:7400"));
        var topic = new Table<LinkKey, long>("station");
        using (var u = topic.BeginUpdate())
        {
            for (var i = 0; i < 8; i++) u.Set(new LinkKey(i), i);
            u.Complete();
        }

        var station = new TestProvider(topic)
        {
            Instance = ProviderInstance.Start("station", endpoint), Policy = ErrorPolicy.ReplayLast("station gone"u8.ToArray())
        };
        await using var rig = await LinkRig.StartAsync(station, endpoint, router =>
        {
            router.ProviderHeartbeatInterval = TimeSpan.Zero;
            router.ProviderDegradedAfter = TimeSpan.FromMilliseconds(100);
            router.ProviderTimeout = TimeSpan.FromMilliseconds(400);
        });
        var (router, outlet, owner, client) = (rig.Router, rig.Outlet, rig.Owner, rig.Client);
        var tap = (await router.SubscribeAsync(outlet, "svc"))!;
        TableReplica? replica = null;
        TapError? ended = null;
        var states = new ConcurrentQueue<ProviderState>();
        client.ProviderStateChanged += change => states.Enqueue(change.State);
        client.Subscribe(tap.Binding,
            new SubscriptionHandler { Table = (_, t, _) => replica = t, Error = (_, e) => ended = e });
        await Until(() => replica is { Count: 8 }, () => Pump(outlet, owner, client), "mirrored");

        rig.Loop!.Kill();
        var dead = await HeartbeatUntil(router, () =>
        {
            Pump(outlet, owner, client);
            return tap.IsClosed;
        });
        Assert(dead && tap.Completion.Result?.Code == FaultCode.ServiceDown, "dead past its timeout: its route down");
        await Until(() => ended is not null, () => Pump(outlet, owner, client), "the client told");
        Assert(ended is { Code: FaultCode.ServiceDown } && System.Text.Encoding.UTF8.GetString(ended.Payload.Span) == "station gone",
            $"with the provider's payload ({ended?.Code})");
        Assert(replica is { Degraded: true, Count: 8 }, "shown its last state, stale: the provider's policy, the last again");
        Assert(states.Contains(ProviderState.Degraded), "degraded first, while its checks went unanswered");
    }

    /// <summary>
    ///     Over MagicOnion, a control stream whose hello names a session the provider's router doesn't hold (a router
    ///     greeting a process started again with the session it had of the one before, say): it ends with
    ///     FailedPrecondition, saying so, and the provider logs no failure.
    /// </summary>
    public static async Task ProviderLinkUnknownSession()
    {
        var port = FreePort();
        var endpoint = new ProviderEndpoint(new Uri($"http://127.0.0.1:{port}"));
        var served = new ListLogger();
        var app = await ServeLinkAsync(port, endpoint, new LinkedProvider(ProviderInstance.Start("linked", endpoint)), served);
        try
        {
            using var channel = GrpcChannel.ForAddress(endpoint.Address);
            using var call = await MagicOnionClient.Create<ITaplineProviderLink>(channel).OpenAsync();
            await call.RequestStream.WriteAsync(new RouterHello { Name = "router", Id = Guid.CreateVersion7(), Outlet = 999, Nonce = 1 });
            var ended = await ThrewAsync<RpcException>(() => call.ResponseStream.MoveNext());
            Assert(ended is { StatusCode: StatusCode.FailedPrecondition } && ended.Status.Detail.Contains("No session 999.1"),
                $"the control stream ended with FailedPrecondition, saying why ({ended?.Status})");
            Assert(Failures(served).Count == 0, $"no failure logged at the provider: {string.Join(" | ", Failures(served))}");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>
    ///     Over MagicOnion, the provider's process started again at its address, three times: each time the router opens its
    ///     control stream again only once its Tapline stream is bound to a session there (a new one, the process's own),
    ///     never greeting it with the session it had of the one before; a step 1 after is answered by the new instance.
    ///     Nothing is logged as a failure at the provider.
    /// </summary>
    public static async Task ProviderLinkRestart()
    {
        var port = FreePort();
        var endpoint = new ProviderEndpoint(new Uri($"http://127.0.0.1:{port}"));
        var (served, routed) = (new ListLogger(), new ListLogger());
        var provider = new LinkedProvider(ProviderInstance.Start("linked", endpoint));
        var app = await ServeLinkAsync(port, endpoint, provider, served);
        try
        {
            await using var rig = await LinkRig.StartAsync(provider, endpoint,
                provide: router => router.ProvideRemote("svc", endpoint.Address, log: routed));
            var (router, outlet) = (rig.Router, rig.Outlet);
            for (var restart = 1; restart <= 3; restart++)
            {
                // Gone only once it holds the router's link: its control stream served, not just opened (the router's
                // side is up as its hello is written). A host disposed with that call still on its way in disposes its
                // services under it, and the call fails resolving its service (an ObjectDisposedException, which gRPC
                // logs as an error) — a host stopped first stops taking calls before it disposes anything.
                await Until(() => provider.Channel is not null, () => { },
                    $"restart {restart}: the process holding the router's link");
                await app.DisposeAsync();
                await Until(() => !rig.Remote.IsUp, () => { }, $"restart {restart}: the control stream down with the process");
                provider = new LinkedProvider(ProviderInstance.Start("linked", endpoint));
                app = await ServeLinkAsync(port, endpoint, provider, served);
                await Until(() => rig.Remote.IsUp, () => { }, $"restart {restart}: the control stream open again", 15_000);
                var answered = false;
                for (var until = Environment.TickCount64 + 15_000; !answered && Environment.TickCount64 < until;)
                {
                    try
                    {
                        answered = (await router.SubscribeAsync(outlet, "svc", "book"))?.Provider?.Id == provider.Instance.Id;
                    }
                    catch (ServiceUnavailableException)
                    {
                    } // (the instance before presumed alive a moment longer)

                    if (!answered) await Task.Delay(100);
                }

                Assert(answered, $"restart {restart}: a step 1 answered by the new instance");
            }

            Assert(Failures(served).Count == 0, $"no failure logged at the provider: {string.Join(" | ", Failures(served))}");
            var stale = routed.Lines.Where(l => l.Message.Contains("No session")).ToList();
            Assert(stale.Count == 0, $"never greeted with a session it didn't hold: {string.Join(" | ", stale.Select(l => l.Message))}");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>
    ///     Over MagicOnion, a service's second provider registered before it runs (blue/green's green, say): step 1 goes to
    ///     the one reached, every time, at once — none to the other, none waiting out SubscribeTimeout. Once it runs, its link
    ///     up, step 1s go to it too.
    /// </summary>
    public static async Task ProviderLinkNotRunning()
    {
        var (portA, portB) = (FreePort(), FreePort());
        var (endpointA, endpointB) = (new ProviderEndpoint(new Uri($"http://127.0.0.1:{portA}")),
            new ProviderEndpoint(new Uri($"http://127.0.0.1:{portB}")));
        var a = new LinkedProvider(ProviderInstance.Start("a", endpointA));
        var appA = await ServeLinkAsync(portA, endpointA, a, new ListLogger());
        WebApplication? appB = null;
        var registry = new OutletRegistry();
        var router = new Publisher(registry);
        var remoteA = router.ProvideRemote("svc", endpointA.Address);
        var remoteB = router.ProvideRemote("svc", endpointB.Address); // (b not running yet)
        try
        {
            var outlet = NewOutlet(registry);
            outlet.Attach(new object());
            await Until(() => remoteA.IsUp, () => { }, "a's link up");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var onA = 0;
            for (var i = 0; i < 6; i++)
                if ((await router.SubscribeAsync(outlet, "svc", "book"))?.Provider?.Id == a.Instance.Id)
                    onA++;
            Assert(onA == 6 && timer.Elapsed < TimeSpan.FromSeconds(2),
                $"b not running: every step 1 answered by a, at once ({onA} of 6, {timer.ElapsedMilliseconds} ms)");

            var b = new LinkedProvider(ProviderInstance.Start("b", endpointB));
            appB = await ServeLinkAsync(portB, endpointB, b, new ListLogger());
            await Until(() => remoteB.IsUp, () => { }, "b running: its link up", 15_000);
            var onB = 0;
            for (var i = 0; i < 6; i++)
                if ((await router.SubscribeAsync(outlet, "svc", "book"))?.Provider?.Id == b.Instance.Id)
                    onB++;
            Assert(onB > 0, "b running: step 1s go to it too");
        }
        finally
        {
            await remoteA.DisposeAsync();
            await remoteB.DisposeAsync();
            await appA.DisposeAsync();
            if (appB is not null) await appB.DisposeAsync();
        }
    }

    /// <summary>A provider served over MagicOnion (Kestrel, HTTP/2 cleartext) on <paramref name="port" />, what it logs kept.</summary>
    private static async Task<WebApplication> ServeLinkAsync(int port, ProviderEndpoint endpoint, IDataProvider provider,
        ListLogger log)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(log);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddMagicOnion();
        builder.Services.AddTaplineProviderLink(endpoint, _ => provider);
        var app = builder.Build();
        app.MapTaplineProviderLink();
        await app.StartAsync();
        return app;
    }

    /// <summary>What was logged as a failure: errors and worse.</summary>
    private static List<string> Failures(ListLogger log)
    {
        lock (log.Lines)
        {
            return [.. log.Lines.Where(l => l.Level >= LogLevel.Error).Select(l => l.Message)];
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal readonly record struct LinkKey(int Id);

    /// <summary>A provider in another process: a book (a table) and a value, accepted onto by what's asked; its notices kept.</summary>
    private sealed class LinkedProvider(ProviderInstance instance) : IDataProvider
    {
        public readonly Table<LinkKey, long> Book = new("book");
        public readonly ConcurrentQueue<object> Notices = new();
        public readonly LastValueSource Value = new();
        public IRouterChannel? Channel;
        public long Epoch;
        public SubscribeRequest? Last;
        public Guid Refused;
        public ServiceAccess Access = ServiceAccess.Open; // what it declares of its service
        public int AccessAsks; // times the router asked what it declares
        public readonly ConcurrentQueue<IdentityChange> IdentityChanges = new(); // renewals and lapses told
        public ProviderInstance Instance { get; } = instance;

        public ValueTask IdentityChangedAsync(IdentityChange notice, CancellationToken cancellationToken)
        {
            IdentityChanges.Enqueue(notice);
            return default;
        }

        public ValueTask<ServiceAccess> AccessAsync(string service, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref AccessAsks);
            return new ValueTask<ServiceAccess>(Access);
        }

        public ValueTask ConnectAsync(IRouterChannel router, CancellationToken cancellationToken)
        {
            Channel = router;
            return default;
        }

        public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
        {
            switch (request.Parameters)
            {
                case "refuse":
                    Refused = request.Refuse("no such book");
                    break;
                case "elsewhere":
                    Refused = request.Refuse(RefusalKind.Elsewhere, "not the writer", null);
                    break;
                case "nack":
                    request.Nack();
                    break;
                case "forbid":
                    Last = request;
                    Refused = request.Forbid("not this book");
                    break;
                case "value":
                    request.Accept(Value, Instance, "value");
                    break;
                default:
                    Epoch = request.Accept(Book, Instance, "book");
                    Last = request;
                    break;
            }

            return default;
        }

        public ValueTask ActivateAsync(Activation notice, CancellationToken cancellationToken)
        {
            Notices.Enqueue(notice);
            return default;
        }

        public ValueTask UnsubscribeAsync(Unsubscription notice, CancellationToken cancellationToken)
        {
            Notices.Enqueue(notice);
            return default;
        }

        public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
        {
            return new ValueTask<HealthReport>(new HealthReport(Instance, ProviderStatus.Serving, Load: 0.42));
        }

        public long Set(int id, long value)
        {
            using var u = Book.BeginUpdate();
            u.Set(new LinkKey(id), value);
            return u.Complete();
        }

        public void Delete(int id)
        {
            using var u = Book.BeginUpdate();
            u.Delete(new LinkKey(id));
            u.Complete();
        }

        /// <summary>Rows 0 to <paramref name="count" /> − 1 set in one update.</summary>
        public long SetMany(int count, Func<int, long> value)
        {
            using var u = Book.BeginUpdate();
            for (var i = 0; i < count; i++) u.Set(new LinkKey(i), value(i));
            return u.Complete();
        }
    }

    /// <summary>
    ///     A router reaching a provider over a link, and a client of the router's: in process (<see cref="LoopLink" />), or
    ///     as <c>provide</c> registers it. <c>configure</c> sets the router's timings first.
    /// </summary>
    private sealed class LinkRig : IAsyncDisposable
    {
        private LinkRig(Publisher router, RemoteProvider remote, Outlet outlet, object owner, TaplineClient client,
            LoopLink? loop)
        {
            (Router, Remote, Outlet, Owner, Client, Loop) = (router, remote, outlet, owner, client, loop);
        }

        public Publisher Router { get; }
        public RemoteProvider Remote { get; }
        public Outlet Outlet { get; }
        public object Owner { get; }
        public TaplineClient Client { get; }

        /// <summary>The link in process, to break or kill (null over MagicOnion).</summary>
        public LoopLink? Loop { get; }

        public async ValueTask DisposeAsync()
        {
            await Remote.DisposeAsync();
            if (Loop is not null) await Loop.DisposeAsync();
        }

        public static async Task<LinkRig> StartAsync(IDataProvider provider, ProviderEndpoint endpoint,
            Action<Publisher>? configure = null, Func<Publisher, RemoteProvider>? provide = null,
            Action<OutletOptions>? client = null)
        {
            var registry = new OutletRegistry();
            var router = new Publisher(registry);
            configure?.Invoke(router);
            RemoteProvider remote;
            LoopLink? loop = null;
            if (provide is null)
            {
                loop = new LoopLink(provider, endpoint);
                remote = new RemoteProvider(endpoint, loop);
                router.Provide("svc", endpoint, remote);
            }
            else
            {
                remote = provide(router);
            }

            var outlet = NewOutlet(registry, client);
            var owner = new object();
            outlet.Attach(owner);
            var subscriber = new TaplineClient(new LoopConnection(outlet));
            subscriber.Bind(outlet.Id, outlet.Nonce);
            await Until(() => remote.IsUp, () => { }, "the link up");
            return new LinkRig(router, remote, outlet, owner, subscriber, loop);
        }
    }

    /// <summary>
    ///     The link in process: the provider's links served over channels, and the router's client of the provider's router
    ///     pumped straight from its outlet there. Its control stream can be broken (each new one held back until healed), or
    ///     the provider killed: no control stream again, no frames.
    /// </summary>
    private sealed class LoopLink : IProviderLink, IAsyncDisposable
    {
        private readonly ProviderLinks _links;
        private readonly Lock _lock = new();
        private readonly Outlet _outlet;
        private readonly object _owner = new();
        private (ChannelWriter<RouterMessage> ToProvider, ChannelWriter<ProviderMessage> ToRouter)? _current;
        private volatile bool _dead;
        private TaskCompletionSource _healed = Healed();

        public LoopLink(IDataProvider provider, ProviderEndpoint endpoint)
        {
            _outlet = NewOutlet(new OutletRegistry());
            _outlet.Attach(_owner);
            _links = new ProviderLinks(provider, endpoint,
                (id, nonce) => id == _outlet.Id && nonce == _outlet.Nonce ? _outlet : null);
            Client = new TaplineClient(new LoopConnection(_outlet));
            Client.Bind(_outlet.Id, _outlet.Nonce);
        }

        public ValueTask DisposeAsync()
        {
            _dead = true;
            _outlet.Dispose();
            return default;
        }

        public TaplineClient Client { get; }

        public (int OutletId, uint Nonce)? Session => (_outlet.Id, _outlet.Nonce);

        public event Action<int, uint>? Connected
        {
            add { }
            remove { }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            using var interrupt = cancellationToken.UnsafeRegister(static o => ((Outlet)o!).Interrupt(), _outlet);
            while (!cancellationToken.IsCancellationRequested && !_dead)
            {
                if (!_outlet.IsReady)
                {
                    await _outlet.WaitAsync();
                    continue;
                }

                if (!_outlet.TryBuildFrame(_owner, false, out var frame, out var seq))
                {
                    await Task.Yield();
                    continue;
                }

                var bytes = frame.ToArray();
                _outlet.ReleaseFrame();
                if (!_dead) Client.Receive(seq, bytes);
            }
        }

        public async Task<IControlStream<RouterMessage, ProviderMessage>> OpenControlAsync(CancellationToken cancellationToken)
        {
            Task healed;
            lock (_lock)
            {
                healed = _healed.Task;
            }

            await healed.WaitAsync(cancellationToken);
            if (_dead) await Task.Delay(Timeout.Infinite, cancellationToken); // (gone: nobody answers)
            var toProvider = System.Threading.Channels.Channel.CreateUnbounded<RouterMessage>();
            var toRouter = System.Threading.Channels.Channel.CreateUnbounded<ProviderMessage>();
            lock (_lock)
            {
                _current = (toProvider.Writer, toRouter.Writer);
            }

            _ = _links.ServeAsync(new ChannelControl<ProviderMessage, RouterMessage>(toRouter.Writer, toProvider.Reader),
                cancellationToken).ContinueWith(_ => toRouter.Writer.TryComplete(), TaskScheduler.Default);
            return new ChannelControl<RouterMessage, ProviderMessage>(toProvider.Writer, toRouter.Reader);
        }

        /// <summary>The control stream broken: each one opened from now on held back until <see cref="Heal" />.</summary>
        public void Break()
        {
            lock (_lock)
            {
                if (_healed.Task.IsCompleted) _healed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_current is var (toProvider, toRouter))
                {
                    toProvider.TryComplete();
                    toRouter.TryComplete();
                }
            }
        }

        public void Heal()
        {
            lock (_lock)
            {
                _healed.TrySetResult();
            }
        }

        /// <summary>The provider's process gone: its control stream broken for good, and no frames from it.</summary>
        public void Kill()
        {
            _dead = true;
            Break();
            Heal();
        }

        private static TaskCompletionSource Healed()
        {
            var healed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            healed.SetResult();
            return healed;
        }
    }

    private sealed class ChannelControl<TOut, TIn>(ChannelWriter<TOut> writer, ChannelReader<TIn> reader)
        : IControlStream<TOut, TIn>
    {
        public ValueTask WriteAsync(TOut message, CancellationToken cancellationToken)
        {
            return writer.WriteAsync(message, cancellationToken);
        }

        public IAsyncEnumerable<TIn> ReadAllAsync(CancellationToken cancellationToken)
        {
            return reader.ReadAllAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            writer.TryComplete();
            return default;
        }
    }
}
