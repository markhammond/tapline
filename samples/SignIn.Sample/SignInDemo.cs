using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Tapline;
using Tapline.Authentication;
using Tapline.Authentication.Opaque;

namespace SignIn.Sample;

/// <summary>
///     /signin's realm, <c>demo</c>: passkeys and passwords by OPAQUE (Tapline.Authentication, .Opaque), kept in memory —
///     gone when the sample stops. A name no one has claimed is anyone's to claim, by creating a passkey or setting a
///     password for it (the demo's "invitation": the name itself); once claimed, only someone signed in as it adds the
///     other. And a service that requires a sign-in, <c>members</c>: a greeting for whoever subscribes. Passkeys belong
///     to <c>localhost</c>: open the page as http://localhost:…, not 127.0.0.1.
/// </summary>
internal static class SignInDemo
{
    public const string Realm = "demo";

    public static void Map(WebApplication app)
    {
        var publisher = app.Services.GetRequiredService<Publisher>();
        var origins = new List<string>(); // (where the page is served: known once listening)
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            foreach (var address in app.Services.GetRequiredService<IServer>().Features
                         .Get<IServerAddressesFeature>()?.Addresses ?? [])
                if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsLoopback)
                    origins.Add($"{uri.Scheme}://localhost:{uri.Port}");
        });
        var passkeys = new PasskeyOptions { RelyingPartyId = "localhost", RelyingPartyName = "Tapline sample", Origins = origins };
        var passkeyStore = new InMemoryPasskeyStore();
        var passwordStore = new InMemoryOpaqueStore();
        var opaque = OpaqueCrypto.Load();
        app.Lifetime.ApplicationStopped.Register(opaque.Dispose);

        async ValueTask<string?> Unclaimed(ReadOnlyMemory<byte> token, CancellationToken ct)
        {
            var name = Encoding.UTF8.GetString(token.Span).Trim();
            if (name.Length is 0 or > 64) return null;
            var taken = (await passkeyStore.ListAsync(name, ct)).Count > 0 ||
                        await passwordStore.PasswordFileAsync(name, ct) is not null;
            return taken ? null : name;
        }

        publisher.ProvideAuthentication(new AuthenticationService(Realm)
        {
            Modules =
            {
                new PasskeyModule(passkeys, passkeyStore),
                new PasskeyRegistrationModule(passkeys, passkeyStore) { Enroll = (t, _, ct) => Unclaimed(t, ct) },
                new OpaqueModule(opaque, passwordStore),
                new OpaqueRegistrationModule(opaque, passwordStore) { Enroll = (t, _, ct) => Unclaimed(t, ct) }
            },
            Lifetime = TimeSpan.FromMinutes(30),
            OnAuthenticated = (user, _, _) => new ValueTask<AuthenticatedUser?>(user with
            {
                Claims = new Dictionary<string, string> { ["name"] = user.UserId }
            })
        });
        var members = new MembersProvider();
        publisher.Provide("members", members.Endpoint, members);
    }

    /// <summary>
    ///     <c>members</c>: requires a sign-in to the demo realm — the router refuses anyone else before it's asked — and
    ///     greets whoever subscribes, by name and by how they proved it.
    /// </summary>
    private sealed class MembersProvider : IDataProvider
    {
        private readonly Dictionary<string, LastValueSource> _greetings = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public MembersProvider()
        {
            Endpoint = ProviderEndpoint.InProcess("members");
            Instance = ProviderInstance.Start("members", Endpoint);
        }

        public ProviderEndpoint Endpoint { get; }
        public ProviderInstance Instance { get; }

        public ValueTask<ServiceAccess> AccessAsync(string service, CancellationToken cancellationToken)
        {
            return new ValueTask<ServiceAccess>(ServiceAccess.RequiredIn(Realm));
        }

        public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
        {
            if (request.Identity is not { } who) return default; // (the router asks only for the signed in)
            lock (_gate)
            {
                if (!_greetings.TryGetValue(who.UserId, out var greeting))
                    _greetings[who.UserId] = greeting = new LastValueSource { Label = $"members:{who.UserId}" };
                greeting.Publish(Encoding.UTF8.GetBytes(
                    $"Welcome, {who.UserId} — signed in by {who.Module}{(who.Insecure ? " (insecure)" : "")}, " +
                    $"{who.AuthenticatedAt:HH:mm:ss} UTC, session {who.SessionId.ToString()[..8]}…"));
                request.Accept(greeting, Instance, $"members:{who.UserId}");
            }

            return default;
        }

        public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
        {
            return new ValueTask<HealthReport>(new HealthReport(Instance, ProviderStatus.Serving));
        }
    }
}
