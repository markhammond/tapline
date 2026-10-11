using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Tapline;

/// <summary>
///     An authentication provider's part: a data provider that signs a realm's users in
///     (<see cref="Publisher.ProvideAuthentication" />; docs/provider.md, signing in). A sign-in is a handshake between
///     the client and it — one message or several, each relayed by the router as bytes it doesn't read — that ends in the
///     provider asserting an identity, which the router sets on the client's connection (<see cref="Outlet.Identity" />).
///     What the messages hold is the module's business: a user id (passthrough), OPAQUE's KE1 and KE3, a passkey's
///     assertion, a ticket. The provider keeps its own state between messages, by <see cref="AuthStep.Exchange" />, and
///     limits the rate of failures; the router relays one exchange at a time per connection.
/// </summary>
public interface IAuthenticator
{
    /// <summary>One message of a sign-in: answer with the next (<see cref="AuthAnswer.Continue" />), an identity, or a refusal.</summary>
    ValueTask<AuthAnswer> AuthenticateAsync(AuthStep step, CancellationToken cancellationToken);
}

/// <summary>A sign-in's message, relayed.</summary>
/// <param name="Router">The router relaying it.</param>
/// <param name="Exchange">This sign-in (a UUIDv7, the router's): the provider's key for its state between messages.</param>
/// <param name="Realm">The realm signed in to (the provider's).</param>
/// <param name="Module">How: <c>passthrough</c>, <c>opaque</c>, <c>passkey</c>, <c>ticket</c>… — the provider's to know.</param>
/// <param name="Round">0 for the first message, then 1, 2…</param>
/// <param name="Payload">The client's message.</param>
/// <param name="Context">
///     What binds it to its connection, as its client knows it too (<see cref="AuthMessages.Context" />): a module that can
///     binds its proof to it (OPAQUE's context, a passkey's challenge), so a sign-in relayed to another connection fails.
/// </param>
/// <param name="Current">The connection's identity now (null: anonymous).</param>
/// <param name="Renewal">The client asks to renew <paramref name="Current" />: the same user of the same realm, a later expiry.</param>
public sealed record AuthStep(
    RouterInstance Router,
    Guid Exchange,
    string Realm,
    string Module,
    int Round,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> Context,
    ClientIdentity? Current,
    bool Renewal);

/// <summary>How a sign-in's message was answered.</summary>
public enum AuthOutcome : byte
{
    /// <summary>The next message, to the client; the sign-in goes on.</summary>
    Continue = 0,

    /// <summary>Signed in: the identity, set on the connection, and a ticket for the client.</summary>
    Accepted = 1,

    /// <summary>Refused, saying why: the sign-in is over.</summary>
    Rejected = 2,

    /// <summary>
    ///     Renew on another router: its name, and a ticket it takes (the client moves make before break, and only to a
    ///     router it knows already); the identity here is left as it is, to lapse.
    /// </summary>
    Redirected = 3
}

/// <summary>An authentication provider's answer to one message of a sign-in (<see cref="IAuthenticator" />).</summary>
public sealed record AuthAnswer
{
    private AuthAnswer(AuthOutcome outcome, ReadOnlyMemory<byte> payload, ClientIdentity? identity, string? reason,
        SessionPolicy? policy = null)
    {
        (Outcome, Payload, Identity, Reason, Policy) = (outcome, payload, identity, reason, policy);
    }

    public AuthOutcome Outcome { get; }

    /// <summary>The next message to the client (<see cref="AuthOutcome.Continue" />), or the ticket (<see cref="AuthOutcome.Accepted" />).</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Who signed in (<see cref="AuthOutcome.Accepted" />): in the realm signed in to.</summary>
    public ClientIdentity? Identity { get; }

    /// <summary>
    ///     Why not (<see cref="AuthOutcome.Rejected" />): told to the client — say no more than it should know; or the
    ///     router to renew on (<see cref="AuthOutcome.Redirected" />).
    /// </summary>
    public string? Reason { get; }

    /// <summary>How the session may be renewed, and when its client is warned (<see cref="AuthOutcome.Accepted" />; null: the default).</summary>
    public SessionPolicy? Policy { get; }

    /// <summary>The sign-in goes on: <paramref name="message" /> to the client, its answer to come.</summary>
    public static AuthAnswer Continue(ReadOnlyMemory<byte> message)
    {
        return new AuthAnswer(AuthOutcome.Continue, message, null, null);
    }

    /// <summary>
    ///     Signed in as <paramref name="identity" /> (its module and insecure flag as the provider says), with a ticket the
    ///     client may present to resume — after a reconnect, on another router — and to renew, where the provider allows.
    /// </summary>
    public static AuthAnswer Accept(ClientIdentity identity, ReadOnlyMemory<byte> ticket = default,
        SessionPolicy? policy = null)
    {
        return new AuthAnswer(AuthOutcome.Accepted, ticket, identity, null, policy);
    }

    /// <summary>
    ///     A renewal answered elsewhere: renew on <paramref name="router" /> (its name), presenting
    ///     <paramref name="ticket" />. The client moves there — make before break — if it's a router it knows.
    /// </summary>
    public static AuthAnswer Redirect(string router, ReadOnlyMemory<byte> ticket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(router);
        return new AuthAnswer(AuthOutcome.Redirected, ticket, null, router);
    }

    /// <summary>Refused: <paramref name="reason" /> is told to the client.</summary>
    public static AuthAnswer Reject(string reason)
    {
        return new AuthAnswer(AuthOutcome.Rejected, default, null, reason);
    }
}

/// <summary>
///     The sign-in's messages from client to router — <c>Tap.Auth(outletId, nonce, message)</c> over a Blazor circuit,
///     <c>ControlKind.Auth</c> over MagicOnion — and the context that binds a sign-in to its connection:
///     <code>
/// Begin    u8 1 | u32 exchange | u8 flags (bit0 renewal) | u8 len | realm | u8 len | module | payload
/// Continue u8 2 | u32 exchange | payload
/// Cancel   u8 3 | u32 exchange
/// </code>
///     The exchange is the client's number for the sign-in (a new one each Begin); the router's answers carry it
///     (<see cref="Wire.Auth" />). A Begin ends the sign-in before it, if any.
/// </summary>
public static class AuthMessages
{
    public const byte Begin = 1, Continue = 2, Cancel = 3, FlagRenewal = 0x01;

    /// <summary>The most a message may be (16 KB): a passkey's assertion fits with room.</summary>
    public const int MaxMessage = 16 * 1024;

    /// <summary>A sign-in's first message: realm, module, the module's first message; a renewal of the identity signed in, or not.</summary>
    public static byte[] BeginOf(uint exchange, string realm, string module, ReadOnlySpan<byte> payload,
        bool renewal = false)
    {
        var r = Encoding.UTF8.GetBytes(realm);
        var m = Encoding.UTF8.GetBytes(module);
        if (r.Length is 0 or > 255 || m.Length is 0 or > 255)
            throw new ArgumentException("A realm and a module are 1 to 255 bytes of UTF-8.");
        var b = new byte[8 + r.Length + m.Length + payload.Length];
        b[0] = Begin;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), exchange);
        b[5] = renewal ? FlagRenewal : (byte)0;
        b[6] = (byte)r.Length;
        r.CopyTo(b, 7);
        b[7 + r.Length] = (byte)m.Length;
        m.CopyTo(b, 8 + r.Length);
        payload.CopyTo(b.AsSpan(8 + r.Length + m.Length));
        return Checked(b);
    }

    /// <summary>A sign-in's next message, answering the router's (<see cref="AuthOutcome.Continue" />).</summary>
    public static byte[] ContinueOf(uint exchange, ReadOnlySpan<byte> payload)
    {
        var b = new byte[5 + payload.Length];
        b[0] = Continue;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), exchange);
        payload.CopyTo(b.AsSpan(5));
        return Checked(b);
    }

    /// <summary>The client gives the sign-in up.</summary>
    public static byte[] CancelOf(uint exchange)
    {
        var b = new byte[5];
        b[0] = Cancel;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), exchange);
        return b;
    }

    /// <summary>
    ///     What binds a sign-in to its connection: <c>tapline/1 {realm} {outletId} {nonce}</c> as UTF-8 — the realm, and the
    ///     connection's outlet and its secret nonce, which its client knows from its binding and nobody else does.
    /// </summary>
    public static byte[] Context(string realm, long outletId, long nonce)
    {
        return Encoding.UTF8.GetBytes($"tapline/1 {realm} {outletId} {nonce}");
    }

    private static byte[] Checked(byte[] message)
    {
        if (message.Length > MaxMessage)
            throw new ArgumentException($"A sign-in's message is at most {MaxMessage:N0} bytes.");
        return message;
    }

    /// <summary>A message, read: false if it isn't one.</summary>
    internal static bool TryRead(ReadOnlySpan<byte> b, out byte op, out uint exchange, out bool renewal,
        out string realm, out string module, out byte[] payload)
    {
        (op, exchange, renewal, realm, module, payload) = (0, 0, false, "", "", []);
        if (b.Length < 5 || b.Length > MaxMessage) return false;
        op = b[0];
        exchange = BinaryPrimitives.ReadUInt32LittleEndian(b[1..]);
        switch (op)
        {
            case Begin:
            {
                if (b.Length < 8) return false;
                renewal = (b[5] & FlagRenewal) != 0;
                int rl = b[6];
                if (rl == 0 || 8 + rl > b.Length) return false;
                int ml = b[7 + rl];
                if (ml == 0 || 8 + rl + ml > b.Length) return false;
                try
                {
                    var strict = new UTF8Encoding(false, true);
                    realm = strict.GetString(b.Slice(7, rl));
                    module = strict.GetString(b.Slice(8 + rl, ml));
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }

                payload = b[(8 + rl + ml)..].ToArray();
                return true;
            }
            case Continue:
                payload = b[5..].ToArray();
                return true;
            case Cancel:
                return b.Length == 5;
            default:
                return false;
        }
    }
}

/// <summary>
///     A <see cref="Wire.Auth" /> record: the router's answer to a sign-in's message —
///     <code>Auth u8 0x0E | u8 status | u32 exchange | u16 round | u16 len | text | u32 len | payload</code>
///     status 0 continue (payload: the provider's next message), 1 signed in (text: the identity as JSON — realm, userId,
///     sessionId, module, insecure, authenticatedAt, expiresAt, renewal, warnAt, claims; payload: the ticket), 2 refused
///     (text: why), 3 redirected (text: the router to renew on; payload: a ticket it takes). The
///     round is the message's it answers (0 the Begin's), <see cref="AnyRound" /> for a refusal before it was read. A
///     notice, resent until acknowledged: a client takes a continue for the round it awaits, and the end of the exchange
///     it awaits, once.
/// </summary>
internal static class AuthRecord
{
    public const int MaxText = 4096, Header = 14;
    public const ushort AnyRound = ushort.MaxValue;

    public static byte[] Of(AuthOutcome status, uint exchange, int round, string? text, ReadOnlySpan<byte> payload)
    {
        var t = RefusedRecord.Utf8(text ?? "", MaxText);
        var b = new byte[Header + t.Length + payload.Length];
        b[0] = Wire.Auth;
        b[1] = (byte)status;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), exchange);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)Math.Min(round, AnyRound));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)t.Length);
        t.CopyTo(b, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10 + t.Length), (uint)payload.Length);
        payload.CopyTo(b.AsSpan(Header + t.Length));
        return b;
    }

    /// <summary>A refusal of <paramref name="message" /> before it was read: its exchange, if it says one.</summary>
    public static byte[] Refused(ReadOnlySpan<byte> message, string reason)
    {
        var exchange = message.Length >= 5 ? BinaryPrimitives.ReadUInt32LittleEndian(message[1..]) : 0;
        return Of(AuthOutcome.Rejected, exchange, AnyRound, reason, []);
    }

    /// <summary>The identity as its client is told it.</summary>
    public static string Json(ClientIdentity identity, SessionPolicy policy)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("realm", identity.Realm);
            w.WriteString("userId", identity.UserId);
            w.WriteString("sessionId", identity.SessionId);
            w.WriteString("module", identity.Module);
            w.WriteBoolean("insecure", identity.Insecure);
            w.WriteString("authenticatedAt", identity.AuthenticatedAt);
            if (identity.ExpiresAt is { } at) w.WriteString("expiresAt", at);
            else w.WriteNull("expiresAt");
            w.WriteString("renewal", policy.Renewal switch
            {
                RenewalPolicy.Silent => "silent", RenewalPolicy.Fixed => "fixed", _ => "reauthenticate"
            });
            if (policy.WarnAt(identity) is { } warnAt) w.WriteString("warnAt", warnAt);
            if (identity.Claims is { Count: > 0 } claims)
            {
                w.WriteStartObject("claims");
                foreach (var (name, value) in claims) w.WriteString(name, value);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
///     A <see cref="Wire.Session" /> record: the connection's session, ahead of its expiry or at it —
///     <code>Session u8 0x0F | u8 status | u8 renewal | u8[16] correlation | u8[16] session | i64 expiresAt | u16 len | reason</code>
///     status 0 expiring (warned once, ahead: renew by the policy), 1 ended (lapsed); renewal the
///     <see cref="RenewalPolicy" />; correlation a UUIDv7 (logged: the needle); session the identity's
///     <see cref="ClientIdentity.SessionId" />; expiresAt Unix milliseconds; reason the policy's. A notice, resent until
///     acknowledged: a client takes each once (a warning of an expiry it has renewed since, not at all).
/// </summary>
internal static class SessionRecord
{
    public const byte Expiring = 0, Ended = 1;
    public const int Header = 45, MaxReason = 1024;

    public static byte[] Of(byte status, SessionPolicy policy, ClientIdentity identity)
    {
        var reason = RefusedRecord.Utf8(policy.Reason ?? "", MaxReason);
        var b = new byte[Header + reason.Length];
        b[0] = Wire.Session;
        b[1] = status;
        b[2] = (byte)policy.Renewal;
        Guid.CreateVersion7().TryWriteBytes(b.AsSpan(3, 16), true, out _);
        identity.SessionId.TryWriteBytes(b.AsSpan(19, 16), true, out _);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(35),
            identity.ExpiresAt?.ToUnixTimeMilliseconds() ?? long.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(43), (ushort)reason.Length);
        reason.CopyTo(b, Header);
        return b;
    }
}

/// <summary>
///     Tickets an authentication provider issues on a sign-in, and checks when one is presented — to resume, after a
///     reconnect or on another router, or to renew where its policy allows: who (realm, user, session, module, insecure),
///     when signed in, and until when the ticket holds, MACed (HMAC-SHA256) with the provider's key. Opaque to the client
///     and the router. A key shared by a realm's providers lets any of them take another's tickets.
/// </summary>
public sealed class AuthTickets
{
    // u8 version | u16 len | realm | u16 len | user | u8[16] session | u16 len | module | u8 insecure
    // | i64 authenticatedAt (Unix ms) | i64 until (Unix ms) | u8[32] HMAC-SHA256 of what's before it
    private const byte Version = 1;
    private readonly byte[] _key;

    /// <param name="key">At least 32 bytes, secret (null: a new one, this instance's alone).</param>
    public AuthTickets(byte[]? key = null)
    {
        _key = key ?? RandomNumberGenerator.GetBytes(32);
        if (_key.Length < 32) throw new ArgumentException("A ticket key is at least 32 bytes.", nameof(key));
    }

    /// <summary>A ticket for <paramref name="identity" />, holding until <paramref name="until" />.</summary>
    public byte[] Issue(ClientIdentity identity, DateTimeOffset until)
    {
        byte[] realm = Encoding.UTF8.GetBytes(identity.Realm), user = Encoding.UTF8.GetBytes(identity.UserId),
            module = Encoding.UTF8.GetBytes(identity.Module);
        var ticket = new byte[1 + 2 + realm.Length + 2 + user.Length + 16 + 2 + module.Length + 1 + 8 + 8 + 32];
        var o = 0;
        ticket[o++] = Version;
        foreach (var part in (ReadOnlySpan<byte[]>)[realm, user])
        {
            BinaryPrimitives.WriteUInt16LittleEndian(ticket.AsSpan(o), (ushort)part.Length);
            part.CopyTo(ticket, o + 2);
            o += 2 + part.Length;
        }

        identity.SessionId.TryWriteBytes(ticket.AsSpan(o, 16), true, out _);
        o += 16;
        BinaryPrimitives.WriteUInt16LittleEndian(ticket.AsSpan(o), (ushort)module.Length);
        module.CopyTo(ticket, o + 2);
        o += 2 + module.Length;
        ticket[o++] = identity.Insecure ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(ticket.AsSpan(o), identity.AuthenticatedAt.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt64LittleEndian(ticket.AsSpan(o + 8), until.ToUnixTimeMilliseconds());
        o += 16;
        HMACSHA256.HashData(_key, ticket.AsSpan(0, o), ticket.AsSpan(o));
        return ticket;
    }

    /// <summary>
    ///     Who a ticket names, if it's this key's, unaltered and still holding at <paramref name="now" /> (its realm, user,
    ///     session, module and insecure flag; when it was signed in to); else false.
    /// </summary>
    public bool TryRead(ReadOnlySpan<byte> ticket, DateTimeOffset now, out ClientIdentity? identity,
        out DateTimeOffset until)
    {
        (identity, until) = (null, default);
        if (ticket.Length < 1 + 2 + 2 + 16 + 2 + 1 + 16 + 32 || ticket[0] != Version) return false;
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, ticket[..^32], mac);
        if (!CryptographicOperations.FixedTimeEquals(mac, ticket[^32..])) return false;
        var b = ticket[..^32];
        var o = 1;
        if (!Text(b, ref o, out var realm) || !Text(b, ref o, out var user) || o + 16 > b.Length) return false;
        var session = new Guid(b.Slice(o, 16), true);
        o += 16;
        if (!Text(b, ref o, out var module) || o + 17 != b.Length) return false;
        var insecure = b[o++] != 0;
        var at = BinaryPrimitives.ReadInt64LittleEndian(b[o..]);
        until = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(b[(o + 8)..]));
        if (until <= now) return false;
        try
        {
            identity = new ClientIdentity(realm, user, session, module, insecure, DateTimeOffset.FromUnixTimeMilliseconds(at));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool Text(ReadOnlySpan<byte> b, ref int o, out string text)
    {
        text = "";
        if (o + 2 > b.Length) return false;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
        if (o + 2 + n > b.Length) return false;
        text = Encoding.UTF8.GetString(b.Slice(o + 2, n));
        o += 2 + n;
        return true;
    }
}

/// <summary>
///     Passthrough: the client says who it is, and it's taken as given — module <c>passthrough</c>, every identity
///     <see cref="ClientIdentity.Insecure" />. Enabled only by its option set to <see cref="AcceptAnyCredential" /> (any
///     other value throws); allowed in any environment — its identities are marked insecure wherever they go, and each
///     provider decides what that's worth. The payload is the user id (UTF-8). It also takes back its own tickets (module
///     <c>ticket</c>): to resume, until the ticket's end; to renew (silently, by default), for another lifetime. An
///     authentication provider in process: <see cref="Publisher.ProvidePassthrough" />.
/// </summary>
public sealed class PassthroughAuthenticator : IDataProvider, IAuthenticator
{
    /// <summary>The value that enables it.</summary>
    public const string AcceptAnyCredential = nameof(AcceptAnyCredential);

    public const string Module = "passthrough", TicketModule = "ticket";

    private readonly TimeProvider _time;

    /// <param name="realm">The realm it signs users in to.</param>
    /// <param name="enable">Must be <see cref="AcceptAnyCredential" />: passthrough is never on by accident.</param>
    /// <param name="lifetime">How long an identity holds (default 8 hours); its ticket as long.</param>
    /// <param name="policy">Its sessions' (default: renewed silently, by the ticket).</param>
    public PassthroughAuthenticator(string realm, string enable, TimeSpan? lifetime = null,
        AuthTickets? tickets = null, TimeProvider? time = null, SessionPolicy? policy = null)
    {
        if (!string.Equals(enable, AcceptAnyCredential, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Passthrough takes any user id as given: it's enabled only by \"{AcceptAnyCredential}\".", nameof(enable));
        Realm = ServiceAccess.In(realm).Realm!;
        Lifetime = lifetime ?? TimeSpan.FromHours(8);
        Tickets = tickets ?? new AuthTickets();
        Policy = policy ?? new SessionPolicy(RenewalPolicy.Silent);
        _time = time ?? TimeProvider.System;
        Instance = ProviderInstance.Start($"passthrough-{realm}", Endpoint);
    }

    public string Realm { get; }
    public TimeSpan Lifetime { get; }
    public AuthTickets Tickets { get; }
    public SessionPolicy Policy { get; }
    public ProviderEndpoint Endpoint => ProviderEndpoint.InProcess($"auth-{Realm}");
    public ProviderInstance Instance { get; }

    public ValueTask<AuthAnswer> AuthenticateAsync(AuthStep step, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (step.Module == TicketModule)
        {
            if (!Tickets.TryRead(step.Payload.Span, now, out var held, out var until) || held!.Realm != Realm)
                return new ValueTask<AuthAnswer>(AuthAnswer.Reject("That ticket doesn't hold here."));
            if (!step.Renewal) // resumed: until the ticket's end
                return new ValueTask<AuthAnswer>(AuthAnswer.Accept(held with { ExpiresAt = until }, step.Payload,
                    Policy));
            if (Policy.Renewal != RenewalPolicy.Silent)
                return new ValueTask<AuthAnswer>(AuthAnswer.Reject("Say who you are again to renew."));
            var renewed = held with { ExpiresAt = now + Lifetime };
            return new ValueTask<AuthAnswer>(AuthAnswer.Accept(renewed, Tickets.Issue(renewed, now + Lifetime),
                Policy));
        }

        if (step.Module != Module) return new ValueTask<AuthAnswer>(AuthAnswer.Reject($"No module '{step.Module}' here."));
        string user;
        try
        {
            user = new UTF8Encoding(false, true).GetString(step.Payload.Span).Trim();
        }
        catch (DecoderFallbackException)
        {
            return new ValueTask<AuthAnswer>(AuthAnswer.Reject("A user id is UTF-8."));
        }

        if (user.Length == 0 || Encoding.UTF8.GetByteCount(user) > ClientIdentity.MaxName)
            return new ValueTask<AuthAnswer>(AuthAnswer.Reject("Say who you are."));
        var session = step.Renewal && step.Current is { } current && current.UserId == user
            ? current.SessionId
            : Guid.CreateVersion7();
        var expires = now + Lifetime;
        var identity = new ClientIdentity(Realm, user, session, Module, true, now, expires);
        return new ValueTask<AuthAnswer>(AuthAnswer.Accept(identity, Tickets.Issue(identity, expires), Policy));
    }

    public ValueTask SubscribeAsync(SubscribeRequest request, CancellationToken cancellationToken)
    {
        return default; // (it serves no topics)
    }

    public ValueTask<HealthReport> CheckHealthAsync(HealthProbe probe, CancellationToken cancellationToken)
    {
        return new ValueTask<HealthReport>(new HealthReport(Instance, ProviderStatus.Serving));
    }
}

public sealed partial class Publisher
{
    private readonly Dictionary<string, Realm> _realms = new(StringComparer.Ordinal);

    /// <summary>How long one message of a sign-in may take its provider (default 10 s): then it's refused, unavailable.</summary>
    public TimeSpan AuthTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A connection signed in (or renewed), or a sign-in refused: who, where, why — the router's log.</summary>
    public event Action<Outlet, string, ClientIdentity?, string?>? SignIn;

    /// <summary>
    ///     Registers <paramref name="provider" /> — a data provider and an authenticator (<see cref="IAuthenticator" />) —
    ///     as the authentication service of <paramref name="realm" />, at <paramref name="endpoint" />: a client's sign-in
    ///     to the realm is relayed to it, and what it asserts is set on the client's connection. A realm has one
    ///     authentication service, which signs in by every module the realm offers; registered again at another endpoint,
    ///     it's another instance of it — interchangeable (the same modules, a ticket key shared), each sign-in going to the
    ///     next eligible one — so of the same kind: the same type in process; over a link, the operator's word. Another kind
    ///     for a realm that has one is refused. Its endpoint is linked as a data provider's is (introduced, health-checked,
    ///     reached).
    /// </summary>
    public void ProvideAuthentication(string realm, ProviderEndpoint endpoint, IDataProvider provider)
    {
        realm = ServiceAccess.In(realm).Realm!;
        if (provider is not IAuthenticator)
            throw new ArgumentException("An authentication provider is an IAuthenticator too.", nameof(provider));
        ProviderLink? fresh;
        lock (_directory)
        {
            fresh = LinkOf(endpoint, provider);
            if (!_realms.TryGetValue(realm, out var entry)) _realms[realm] = entry = new Realm();
            if (Array.IndexOf(entry.Endpoints, endpoint) >= 0)
                throw new InvalidOperationException($"{endpoint} signs users in to '{realm}' already.");
            if (entry.Kind is { } kind && kind != provider.GetType())
                throw new InvalidOperationException(
                    $"Realm '{realm}' has an authentication service already ({kind.Name}): one a realm, its instances alike — not a {provider.GetType().Name} beside it.");
            entry.Kind = provider.GetType();
            entry.Endpoints = [.. entry.Endpoints, endpoint];
        }

        Outlets.Authentication = this;
        if (fresh is not null) _ = ConnectAsync(fresh);
    }

    /// <summary>Passthrough for <paramref name="realm" />, in process (see <see cref="PassthroughAuthenticator" />): enabled by <paramref name="enable" /> alone.</summary>
    public PassthroughAuthenticator ProvidePassthrough(string realm, string enable, TimeSpan? lifetime = null,
        SessionPolicy? policy = null)
    {
        var passthrough = new PassthroughAuthenticator(realm, enable, lifetime, policy: policy);
        ProvideAuthentication(passthrough.Realm, passthrough.Endpoint, passthrough);
        return passthrough;
    }

    /// <summary>A client's sign-in message, from its outlet: queued, relayed in order (one exchange at a time).</summary>
    internal void Authenticate(Outlet outlet, byte[] message)
    {
        var queue = Volatile.Read(ref outlet.AuthQueue);
        if (queue is null)
        {
            var made = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
                { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
            queue = Interlocked.CompareExchange(ref outlet.AuthQueue, made, null) ?? made;
            if (ReferenceEquals(queue, made)) _ = RelayAsync(outlet, made.Reader); // its one relay, in order
        }

        if (outlet.IsDisposed) queue.Writer.TryComplete(); // (Dispose may have looked before it was made)
        if (!queue.Writer.TryWrite(message) && !outlet.IsDisposed)
            outlet.AuthAnswered(AuthRecord.Refused(message, "Too many sign-in messages at once."));
    }

    private async Task RelayAsync(Outlet outlet, ChannelReader<byte[]> messages)
    {
        Exchange? current = null;
        await foreach (var message in messages.ReadAllAsync().ConfigureAwait(false))
        {
            if (outlet.IsDisposed) return;
            if (!AuthMessages.TryRead(message, out var op, out var number, out var renewal, out var realm,
                    out var module, out var payload))
            {
                outlet.AuthAnswered(AuthRecord.Refused(message, "Not a sign-in message."));
                continue;
            }

            switch (op)
            {
                case AuthMessages.Cancel:
                    if (current?.Number == number) current = null;
                    continue;
                case AuthMessages.Begin:
                    current = new Exchange(number, Guid.CreateVersion7(), realm, module, renewal);
                    break;
                case AuthMessages.Continue when current?.Number != number:
                    outlet.AuthAnswered(AuthRecord.Of(AuthOutcome.Rejected, number, AuthRecord.AnyRound,
                        "No such sign-in under way.", []));
                    continue;
            }

            var exchange = current!;
            var answer = await StepAsync(outlet, exchange, payload).ConfigureAwait(false);
            var round = exchange.Round++;
            if (answer.Outcome != AuthOutcome.Continue) current = null;
            outlet.AuthAnswered(answer.Outcome switch
            {
                AuthOutcome.Continue => AuthRecord.Of(AuthOutcome.Continue, number, round, null, answer.Payload.Span),
                AuthOutcome.Accepted => AuthRecord.Of(AuthOutcome.Accepted, number, round,
                    AuthRecord.Json(answer.Identity!, answer.Policy ?? SessionPolicy.Default), answer.Payload.Span),
                AuthOutcome.Redirected => AuthRecord.Of(AuthOutcome.Redirected, number, round, answer.Reason,
                    answer.Payload.Span),
                _ => AuthRecord.Of(AuthOutcome.Rejected, number, round, answer.Reason, [])
            });
        }
    }

    /// <summary>One message to the realm's provider, and what becomes of its answer: an identity set, or a refusal.</summary>
    private async Task<AuthAnswer> StepAsync(Outlet outlet, Exchange exchange, byte[] payload)
    {
        var current = outlet.Identity;
        if (exchange.Renewal && current is null)
            return Refused(outlet, exchange, "There's no sign-in to renew.");
        if (exchange.Renewal && outlet.SessionPolicy is { Renewal: RenewalPolicy.Fixed } fixedPolicy)
            return Refused(outlet, exchange,
                $"This session can't be renewed{(fixedPolicy.Reason is { } why ? $": {why}" : ".")}");
        var target = exchange.Target ??= Authenticator(exchange.Realm);
        if (target is null) return Refused(outlet, exchange, $"No sign-in to '{exchange.Realm}' here.");
        AuthAnswer answer;
        try
        {
            using var timeout = new CancellationTokenSource(AuthTimeout);
            var step = new AuthStep(Router, exchange.Id, exchange.Realm, exchange.Module, exchange.Round, payload,
                AuthMessages.Context(exchange.Realm, outlet.Id, outlet.Nonce), current, exchange.Renewal);
            answer = await target.AuthenticateAsync(step, timeout.Token).AsTask().WaitAsync(AuthTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Refused(outlet, exchange, "Sign-in is unavailable: try again.");
        }

        if (answer.Outcome == AuthOutcome.Rejected) return Refused(outlet, exchange, answer.Reason ?? "Refused.");
        if (answer.Payload.Length > AuthMessages.MaxMessage)
            return Refused(outlet, exchange, "The sign-in's provider answered too much.");
        if (answer.Outcome == AuthOutcome.Continue) return answer;
        if (answer.Outcome == AuthOutcome.Redirected)
            return exchange.Renewal
                ? answer
                : Refused(outlet, exchange, "Only a renewal is redirected.");
        var identity = answer.Identity!;
        if (!string.Equals(identity.Realm, exchange.Realm, StringComparison.Ordinal))
            return Refused(outlet, exchange, "Signed in to another realm than asked.");
        if (exchange.Renewal && !identity.SameUser(current!))
            return Refused(outlet, exchange, "A renewal is the same user's.");
        outlet.SetIdentity(identity, answer.Policy);
        SignIn?.Invoke(outlet, exchange.Realm, identity, null);
        return answer;
    }

    private AuthAnswer Refused(Outlet outlet, Exchange exchange, string reason)
    {
        SignIn?.Invoke(outlet, exchange.Realm, null, reason);
        return AuthAnswer.Reject(reason);
    }

    /// <summary>The realms signed in to here, as the viewer shows them.</summary>
    private List<RealmView> DescribeRealms()
    {
        lock (_directory)
        {
            return
            [
                .. _realms.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new RealmView(r.Key,
                    [.. r.Value.Endpoints.Select(e => e.ToString())],
                    r.Value.Kind == typeof(PassthroughAuthenticator)))
            ];
        }
    }

    /// <summary>The realm's next authentication provider that's eligible (serving, reached, not refusing this router).</summary>
    private IAuthenticator? Authenticator(string realm)
    {
        lock (_directory)
        {
            if (!_realms.TryGetValue(realm, out var entry)) return null;
            for (var i = 0; i < entry.Endpoints.Length; i++)
            {
                var e = entry.Endpoints[entry.Next++ % (uint)entry.Endpoints.Length];
                if (_endpoints.TryGetValue(e, out var link) && link.Eligible && link.Provider is IAuthenticator a)
                    return a;
            }

            return null;
        }
    }

    private sealed class Realm
    {
        public ProviderEndpoint[] Endpoints = [];
        public Type? Kind; // its instances' type: one service a realm
        public uint Next;
    }

    private sealed class Exchange(uint number, Guid id, string realm, string module, bool renewal)
    {
        public readonly Guid Id = id;
        public readonly string Module = module;
        public readonly uint Number = number;
        public readonly string Realm = realm;
        public readonly bool Renewal = renewal;
        public int Round;
        public IAuthenticator? Target; // the provider its first message went to: the rest go there too
    }
}
