using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Tapline.Client;

/// <summary>How a sign-in ended (<see cref="TaplineClient.SignInAsync" />).</summary>
/// <param name="Accepted">Signed in: the connection is <paramref name="Identity" /> now.</param>
/// <param name="Identity">Who, as the realm's authentication provider asserted it (its module and insecure flag with it).</param>
/// <param name="Ticket">The provider's ticket, to resume with (<see cref="TaplineClient.ResumeAsync" />): opaque here.</param>
/// <param name="Reason">Why not, as the provider or the router said it.</param>
/// <param name="Renewal">How the session may be renewed (signed in).</param>
/// <param name="WarnAt">When the router will warn of its expiry (signed in, and it expires).</param>
/// <param name="RedirectTo">
///     A renewal answered elsewhere: the router to renew on (by name), with <paramref name="Ticket" /> — a router this
///     client knows, or none.
/// </param>
public sealed record SignInResult(
    bool Accepted,
    ClientIdentity? Identity,
    ReadOnlyMemory<byte> Ticket,
    string? Reason,
    RenewalPolicy Renewal = RenewalPolicy.Reauthenticate,
    DateTimeOffset? WarnAt = null,
    string? RedirectTo = null)
{
    internal static SignInResult Refused(string reason)
    {
        return new SignInResult(false, null, default, reason);
    }
}

/// <summary>
///     The router's word on this connection's session (<see cref="TaplineClient.SessionExpiring" />,
///     <see cref="TaplineClient.SessionEnded" />): when it lapses, how it may be renewed, why — and the correlation id it
///     was logged with.
/// </summary>
public sealed record SessionNotice(
    Guid CorrelationId,
    Guid SessionId,
    DateTimeOffset? ExpiresAt,
    RenewalPolicy Renewal,
    string? Reason);

/// <summary>A module's answer to the provider's message in a sign-in of several (its next message to the provider).</summary>
public delegate ValueTask<ReadOnlyMemory<byte>> SignInStep(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

public sealed partial class TaplineClient
{
    private AuthWait? _auth; // the sign-in under way (under the gate)
    private uint _exchanges;
    private ClientIdentity? _identity;
    private ReadOnlyMemory<byte> _ticket;
    private string? _ticketRealm;
    private readonly HashSet<Guid> _sessionNoticesSeen = [];

    /// <summary>Who this client's connection is, as its last sign-in left it (null: anonymous).</summary>
    public ClientIdentity? Identity => Volatile.Read(ref _identity);

    /// <summary>The ticket of its last sign-in, to resume with (empty: none).</summary>
    public ReadOnlyMemory<byte> Ticket
    {
        get
        {
            lock (_gate)
            {
                return _ticket;
            }
        }
    }

    /// <summary>
    ///     The router warns its session will lapse (once each expiry): renew it — by its ticket, done here by itself when
    ///     the policy is <see cref="RenewalPolicy.Silent" /> (<see cref="TaplineClientOptions.RenewSilently" />); by a fresh
    ///     proof (<see cref="SignInAsync" />, renewal) when it's <see cref="RenewalPolicy.Reauthenticate" />; not at all
    ///     when it's <see cref="RenewalPolicy.Fixed" />.
    /// </summary>
    public event Action<SessionNotice>? SessionExpiring;

    /// <summary>Its session lapsed: anonymous now (its subscriptions to services that require a sign-in ended).</summary>
    public event Action<SessionNotice>? SessionEnded;

    /// <summary>
    ///     A renewal was answered elsewhere: renew on that router (by name), presenting the ticket — if it's one this
    ///     client knows (Tapline.MagicOnion's <c>TaplineRouters</c> moves there, make before break).
    /// </summary>
    public event Action<string, ReadOnlyMemory<byte>>? RenewalRedirected;

    /// <summary>
    ///     Takes a ticket from elsewhere — a sign-in on another router's connection, or a redirect — as its own, to resume
    ///     with (<see cref="ResumeAsync" />).
    /// </summary>
    public void UseTicket(string realm, ReadOnlyMemory<byte> ticket)
    {
        lock (_gate)
        {
            (_ticketRealm, _ticket) = (realm, ticket.ToArray());
        }
    }

    /// <summary>
    ///     Its identity changed: signed in, renewed, or lost with its session (null: anonymous — a resume, if it holds a
    ///     ticket, under way).
    /// </summary>
    public event Action<ClientIdentity?>? IdentityChanged;

    /// <summary>
    ///     What binds a sign-in to <paramref name="realm" /> to this connection, as the router gives it to the provider:
    ///     what a module binds its proof to (OPAQUE's context, a passkey's challenge). Null until bound.
    /// </summary>
    public byte[]? SignInContext(string realm)
    {
        lock (_gate)
        {
            return OutletId is { } id ? AuthMessages.Context(realm, id, _nonce) : null;
        }
    }

    /// <summary>
    ///     Signs in to <paramref name="realm" /> by <paramref name="module" />: <paramref name="message" /> to the realm's
    ///     authentication provider, through the router, and — for a module of several messages — each of the provider's
    ///     answered by <paramref name="next" />. Signed in, the connection is the identity the provider asserted, and its
    ///     ticket is kept to resume with. A renewal (<paramref name="renewal" />) is of the same user: its subscriptions
    ///     carry on. One sign-in at a time: another begun ends this one (refused). A session lost (bound to another outlet)
    ///     ends it too.
    /// </summary>
    public async Task<SignInResult> SignInAsync(string realm, string module, ReadOnlyMemory<byte> message,
        SignInStep? next = null, bool renewal = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthWait wait;
        lock (_gate)
        {
            var exchange = ++_exchanges;
            var begin = AuthMessages.BeginOf(exchange, realm, module, message.Span, renewal); // (checks its arguments)
            if (_auth is { } before)
            {
                before.Abort.Cancel();
                before.Source.TrySetResult(SignInResult.Refused("Another sign-in began."));
            }

            _auth = wait = new AuthWait(exchange, next);
            _connection.Send(ControlMessage.Auth(begin));
        }

        using var registration = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_auth, wait)) return;
                _auth = null;
                _connection.Send(ControlMessage.Auth(AuthMessages.CancelOf(wait.Exchange)));
            }

            wait.Abort.Cancel();
            wait.Source.TrySetCanceled(cancellationToken);
        });
        return await wait.Source.Task.ConfigureAwait(false);
    }

    /// <summary>
    ///     Presents the ticket of its last sign-in (module <c>ticket</c>) to its realm: on a new session — done for it when
    ///     bound to another outlet — or to renew, where the provider allows it.
    /// </summary>
    public Task<SignInResult> ResumeAsync(bool renewal = false, CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> ticket;
        string? realm;
        lock (_gate)
        {
            (ticket, realm) = (_ticket, _ticketRealm);
        }

        return realm is null || ticket.IsEmpty
            ? Task.FromResult(SignInResult.Refused("No ticket to resume with."))
            : SignInAsync(realm, "ticket", ticket, null, renewal, cancellationToken);
    }

    /// <summary>Bound to another outlet (its session gone, its identity with it): the sign-in under way ends; the ticket resumes.</summary>
    private void SessionLost()
    {
        if (_auth is { } pending)
        {
            _auth = null;
            pending.Abort.Cancel();
            pending.Source.TrySetResult(SignInResult.Refused("The session was lost."));
        }

        if (_identity is null) return;
        Volatile.Write(ref _identity, null);
        Raise(IdentityChanged, (ClientIdentity?)null);
    }

    /// <summary>Bound to another outlet, its ticket presented again (<see cref="TaplineClientOptions.ResumeSignIn" />).</summary>
    private void ResumeOnNewSession()
    {
        if (_options.ResumeSignIn && _ticketRealm is not null && !_ticket.IsEmpty)
            _ = ResumeAsync(); // (its end raises IdentityChanged)
    }

    private static int AuthLength(ReadOnlySpan<byte> r)
    {
        if (r.Length < 14) return -1;
        int text = BinaryPrimitives.ReadUInt16LittleEndian(r[8..]);
        if (r.Length < 14 + text) return -1;
        var payload = BinaryPrimitives.ReadUInt32LittleEndian(r[(10 + text)..]);
        return payload > int.MaxValue - 14 - text ? -1 : 14 + text + (int)payload;
    }

    /// <summary>The router's answer to a sign-in's message: taken once — a continue for the round awaited, the end of the sign-in awaited.</summary>
    private void ApplyAuth(ReadOnlySpan<byte> r)
    {
        var status = (AuthOutcome)r[1];
        var exchange = BinaryPrimitives.ReadUInt32LittleEndian(r[2..]);
        var round = BinaryPrimitives.ReadUInt16LittleEndian(r[6..]);
        int textLength = BinaryPrimitives.ReadUInt16LittleEndian(r[8..]);
        var text = Encoding.UTF8.GetString(r.Slice(10, textLength));
        var payload = r[(14 + textLength)..];
        if (_auth is not { } wait || wait.Exchange != exchange) return; // another's, or said again
        switch (status)
        {
            case AuthOutcome.Continue when round == wait.Round:
                wait.Round++;
                if (wait.Next is null)
                {
                    _auth = null;
                    wait.Abort.Cancel();
                    _connection.Send(ControlMessage.Auth(AuthMessages.CancelOf(exchange)));
                    wait.Source.TrySetResult(SignInResult.Refused("The provider asked for more than this sign-in says."));
                }
                else _ = ContinueAsync(wait, payload.ToArray());

                break;
            case AuthOutcome.Accepted:
            {
                _auth = null;
                ClientIdentity identity;
                try
                {
                    identity = IdentityOf(text);
                }
                catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException
                                              or FormatException)
                {
                    wait.Source.TrySetResult(SignInResult.Refused("The router's account of the identity is unreadable."));
                    Raise(Malformed, "A sign-in's identity this client can't read.");
                    break;
                }

                (_ticket, _ticketRealm) = (payload.ToArray(), identity.Realm);
                Volatile.Write(ref _identity, identity);
                var (renewal, warnAt) = PolicyOf(text);
                wait.Source.TrySetResult(new SignInResult(true, identity, _ticket, null, renewal, warnAt));
                Raise(IdentityChanged, (ClientIdentity?)identity);
                break;
            }
            case AuthOutcome.Rejected:
                _auth = null;
                wait.Abort.Cancel();
                wait.Source.TrySetResult(SignInResult.Refused(text));
                break;
            case AuthOutcome.Redirected:
            {
                _auth = null;
                wait.Abort.Cancel();
                var ticket = payload.ToArray();
                wait.Source.TrySetResult(new SignInResult(false, null, ticket, null, RedirectTo: text));
                Raise(RenewalRedirected, text, (ReadOnlyMemory<byte>)ticket);
                break;
            }
        }
    }

    private static int SessionLength(ReadOnlySpan<byte> r)
    {
        return r.Length < 45 ? -1 : 45 + BinaryPrimitives.ReadUInt16LittleEndian(r[43..]);
    }

    /// <summary>
    ///     The router's word on the session: a warning (its expiry ahead — renewed here by itself if the policy is silent),
    ///     or its end. Each once, and only for the session and expiry this client holds (resent, or renewed since: not).
    /// </summary>
    private void ApplySession(ReadOnlySpan<byte> r)
    {
        var correlation = new Guid(r.Slice(3, 16), true);
        if (!_sessionNoticesSeen.Add(correlation)) return;
        var at = BinaryPrimitives.ReadInt64LittleEndian(r[35..]);
        var notice = new SessionNotice(correlation, new Guid(r.Slice(19, 16), true),
            at == long.MaxValue ? null : DateTimeOffset.FromUnixTimeMilliseconds(at), (RenewalPolicy)r[2],
            BinaryPrimitives.ReadUInt16LittleEndian(r[43..]) is var n and > 0 ? Encoding.UTF8.GetString(r.Slice(45, n)) : null);
        if (_identity is not { } identity || identity.SessionId != notice.SessionId ||
            identity.ExpiresAt?.ToUnixTimeMilliseconds() > at) return; // another session's, or renewed since
        if (r[1] == 0)
        {
            Raise(SessionExpiring, notice);
            if (notice.Renewal == RenewalPolicy.Silent && _options.RenewSilently && !_ticket.IsEmpty)
                _ = ResumeAsync(true); // (its end raises IdentityChanged)
            return;
        }

        Volatile.Write(ref _identity, null);
        Raise(SessionEnded, notice);
        Raise(IdentityChanged, (ClientIdentity?)null);
    }

    /// <summary>The module's next message, off the gate: sent if its sign-in is still the one under way.</summary>
    private async Task ContinueAsync(AuthWait wait, byte[] message)
    {
        try
        {
            // on the pool: off the gate and the thread that received the frame (a module may compute a while: Argon2id)
            var reply = await Task.Run(() => wait.Next!(message, wait.Abort.Token).AsTask(), wait.Abort.Token)
                .ConfigureAwait(false);
            var next = AuthMessages.ContinueOf(wait.Exchange, reply.Span);
            lock (_gate)
            {
                if (ReferenceEquals(_auth, wait)) _connection.Send(ControlMessage.Auth(next));
            }
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_auth, wait)) return;
                _auth = null;
                _connection.Send(ControlMessage.Auth(AuthMessages.CancelOf(wait.Exchange)));
            }

            if (e is OperationCanceledException) wait.Source.TrySetCanceled();
            else wait.Source.TrySetException(e);
        }
    }

    private static (RenewalPolicy, DateTimeOffset?) PolicyOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        var renewal = o.TryGetProperty("renewal", out var r) ? r.GetString() switch
        {
            "silent" => RenewalPolicy.Silent, "fixed" => RenewalPolicy.Fixed, _ => RenewalPolicy.Reauthenticate
        } : RenewalPolicy.Reauthenticate;
        return (renewal, o.TryGetProperty("warnAt", out var w) && w.ValueKind == JsonValueKind.String
            ? w.GetDateTimeOffset()
            : null);
    }

    private static ClientIdentity IdentityOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        Dictionary<string, string>? claims = null;
        if (o.TryGetProperty("claims", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            claims = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in c.EnumerateObject()) claims[p.Name] = p.Value.GetString() ?? "";
        }

        return new ClientIdentity(o.GetProperty("realm").GetString()!, o.GetProperty("userId").GetString()!,
            o.GetProperty("sessionId").GetGuid(), o.GetProperty("module").GetString()!,
            o.GetProperty("insecure").GetBoolean(), o.GetProperty("authenticatedAt").GetDateTimeOffset(),
            o.TryGetProperty("expiresAt", out var x) && x.ValueKind == JsonValueKind.String ? x.GetDateTimeOffset() : null,
            claims);
    }

    private sealed class AuthWait(uint exchange, SignInStep? next)
    {
        public readonly CancellationTokenSource Abort = new(); // its module's steps (no timer: never disposed)
        public readonly uint Exchange = exchange;
        public readonly SignInStep? Next = next;

        public readonly TaskCompletionSource<SignInResult> Source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Round;
    }
}
