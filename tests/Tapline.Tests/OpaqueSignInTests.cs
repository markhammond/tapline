using System.Text;
using Tapline;
using Tapline.Authentication;
using Tapline.Authentication.Opaque;
using Tapline.Client.Opaque;
using Tapline.Client;

/// <summary>Tapline.Authentication.Opaque: password sign-in by OPAQUE through the router, the password never sent.</summary>
internal static partial class T
{
    public static async Task OpaqueSignIns()
    {
        using var crypto = OpaqueCrypto.Load();
        var registry = new OpaqueRegistryRig();
        var (publisher, s, owner, loop, client) = (registry.Publisher, registry.Outlet, registry.Owner, registry.Loop,
            registry.Client);
        var store = new InMemoryOpaqueStore();
        var service = new AuthenticationService("north")
        {
            Modules =
            {
                new OpaqueModule(crypto, store),
                new OpaqueRegistrationModule(crypto, store)
                {
                    Enroll = (token, _, _) => new ValueTask<string?>(
                        Encoding.UTF8.GetString(token.Span) == "invite-alice" ? "alice" : null)
                }
            },
            OnAuthenticated = (user, _, _) => new ValueTask<AuthenticatedUser?>(user with
                { Claims = new Dictionary<string, string> { ["role"] = "gp" } })
        };
        publisher.ProvideAuthentication(service);
        service.MaxFailuresPerConnection = 100; // (a user's attempts are what's counted here)

        Task<SignInResult> Run(Task<SignInResult> signing) => Pumped(signing, loop, owner, client);

        // Setting a password: not for the anonymous without an invitation; with alice's, hers — and signed in by it.
        var anonymous = await Run(client.RegisterPasswordAsync(crypto, "north", "correct horse"));
        Assert(anonymous is { Accepted: false, Reason: "Sign in first to set a password." }, $"no invitation ({anonymous.Reason})");
        var registered = await Run(client.RegisterPasswordAsync(crypto, "north", "correct horse", "invite-alice"u8.ToArray()));
        Assert(registered is { Accepted: true, Identity: { UserId: "alice", Module: "opaque", Insecure: false } } &&
               await store.PasswordFileAsync("alice", default) is { Length: 192 },
            $"registered by an invitation: alice, by opaque, secure; her password file kept, 192 bytes ({registered.Reason})");
        Assert(!loop.Auths.Any(m => Encoding.UTF8.GetString(m).Contains("correct horse")), "the password never sent");

        // Signing in: the right password; a wrong one, or no such user, alike refused — failing at the client.
        var alice = await Run(client.SignInWithPasswordAsync(crypto, "north", "alice", "correct horse"));
        Assert(alice is { Accepted: true, Identity: { UserId: "alice", Module: "opaque" } } &&
               alice.Identity.Claims?["role"] == "gp", $"signed in with her password ({alice.Reason})");
        var wrong = await Run(client.SignInWithPasswordAsync(crypto, "north", "alice", "correct horse!"));
        Assert(wrong is { Accepted: false, Reason: "Wrong user id or password." } &&
               loop.Auths.Last()[0] == AuthMessages.Cancel, "a wrong password: refused at the client, which gave up");
        var nobody = await Run(client.SignInWithPasswordAsync(crypto, "north", "nobody", "correct horse"));
        Assert(nobody is { Accepted: false, Reason: "Wrong user id or password." }, "no such user: refused alike");
        Assert(client.Identity?.UserId == "alice", "refusals change nothing");

        // Bound to the connection: a login finished with another connection's context fails.
        var start = crypto.ClientLoginStart(OpaqueCrypto.Utf8("correct horse"));
        var elsewhere = AuthMessages.Context("north", s.Id + 1, s.Nonce);
        var relayed = false;
        try
        {
            await Run(client.SignInAsync("north", "opaque", OpaqueMessages.Framed("alice"u8, start.Message),
                (ke2, _) => new ValueTask<ReadOnlyMemory<byte>>(crypto.ClientLoginFinish(start.State,
                    OpaqueCrypto.Utf8("correct horse"), ke2.ToArray(), elsewhere).Finalization)));
        }
        catch (OpaqueException e) when (e.InvalidLogin)
        {
            relayed = true;
        }

        Assert(relayed, "a login bound to another connection: refused");

        // A renewal by password: the same session. Changing it, signed in: the old one refused after, the new one taken.
        var session = client.Identity!.SessionId;
        var renewed = await Run(client.SignInWithPasswordAsync(crypto, "north", "alice", "correct horse", renewal: true));
        Assert(renewed.Accepted && renewed.Identity!.SessionId == session, "renewed by her password: the same session");
        var changed = await Run(client.RegisterPasswordAsync(crypto, "north", "battery staple"));
        Assert(changed is { Accepted: true, Identity.UserId: "alice" }, $"changed while signed in ({changed.Reason})");
        Assert((await Run(client.SignInWithPasswordAsync(crypto, "north", "alice", "correct horse"))).Accepted == false &&
               (await Run(client.SignInWithPasswordAsync(crypto, "north", "alice", "battery staple"))).Accepted,
            "the old password refused, the new one taken");

        // Attempts counted until they succeed: past the limit, even the right password waits.
        service.MaxFailuresPerUser = 3; // (alice's so far: the wrong password, the relayed login, the old one — three)
        var waits = await Run(client.SignInWithPasswordAsync(crypto, "north", "alice", "battery staple"));
        Assert(waits is { Accepted: false, Reason: "Too many failed sign-ins: try again later." },
            $"three failed attempts: she waits ({waits.Reason})");
        service.MaxFailuresPerUser = 10;
        var again = await Run(client.SignInWithPasswordAsync(crypto, "north", "alice", "battery staple"));
        Assert(again.Accepted, "under the limit: in; a success counts nothing against her");
    }

    /// <summary>A router, a connection to it, and a .NET client over that connection.</summary>
    private sealed class OpaqueRegistryRig
    {
        public readonly TaplineClient Client;
        public readonly MovableConnection Loop;
        public readonly Outlet Outlet;
        public readonly object Owner = new();
        public readonly Publisher Publisher;

        public OpaqueRegistryRig()
        {
            var registry = new OutletRegistry();
            Publisher = new Publisher(registry);
            Outlet = NewOutlet(registry);
            Outlet.Attach(Owner);
            Loop = new MovableConnection(Outlet);
            Client = new TaplineClient(Loop);
            Client.Bind(Outlet.Id, Outlet.Nonce);
        }
    }
}
