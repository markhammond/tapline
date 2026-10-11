using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tapline.Authentication;

/// <summary>Whether an authenticator must check its user (a PIN, a fingerprint) as well as their presence.</summary>
public enum UserVerification
{
    Required,
    Preferred,
    Discouraged
}

/// <summary>
///     Where passkeys belong: the relying party — the site's domain, which a passkey is bound to — and the origins its
///     pages are served from; whether the user must be verified; how long the browser waits.
/// </summary>
public sealed class PasskeyOptions
{
    /// <summary>The relying party's id: the site's registrable domain (<c>example.com</c>), or <c>localhost</c>.</summary>
    public required string RelyingPartyId { get; init; }

    /// <summary>Its name, as the browser shows it at registration (default: the id).</summary>
    public string? RelyingPartyName { get; init; }

    /// <summary>The origins pages sign in from (<c>https://app.example.com</c>): a proof from any other is refused.</summary>
    public required IReadOnlyList<string> Origins { get; init; }

    /// <summary>Whether the user must be verified (default <see cref="UserVerification.Preferred" />: asked, not required).</summary>
    public UserVerification UserVerification { get; init; } = UserVerification.Preferred;

    /// <summary>How long the browser waits for the user (default a minute).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
///     A registered passkey: its credential id, whose it is (the user, and the random handle the authenticator holds for
///     them), its public key (COSE), the last signature counter seen, when it was registered and last used.
/// </summary>
public sealed record PasskeyCredential(
    byte[] Id,
    string UserId,
    byte[] UserHandle,
    byte[] PublicKey,
    uint SignCount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string>? Transports = null,
    DateTimeOffset? LastUsedAt = null);

/// <summary>
///     Where passkeys are kept — the backend's: a table of credentials, and a stable random handle per user (never the
///     user id itself: the authenticator stores it). <see cref="InMemoryPasskeyStore" /> for samples and tests.
/// </summary>
public interface IPasskeyStore
{
    ValueTask<PasskeyCredential?> FindAsync(ReadOnlyMemory<byte> credentialId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<PasskeyCredential>> ListAsync(string userId, CancellationToken cancellationToken);

    /// <summary>A new passkey: false if its credential id is registered already.</summary>
    ValueTask<bool> AddAsync(PasskeyCredential credential, CancellationToken cancellationToken);

    /// <summary>A passkey used: its counter and when.</summary>
    ValueTask UpdateAsync(PasskeyCredential credential, CancellationToken cancellationToken);

    /// <summary>The user's handle: made (32 random bytes) the first time, the same ever after.</summary>
    ValueTask<byte[]> UserHandleAsync(string userId, CancellationToken cancellationToken);
}

/// <summary>Passkeys in memory: gone when the process is.</summary>
public sealed class InMemoryPasskeyStore : IPasskeyStore
{
    private readonly ConcurrentDictionary<string, PasskeyCredential> _credentials = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _handles = new(StringComparer.Ordinal);

    public ValueTask<PasskeyCredential?> FindAsync(ReadOnlyMemory<byte> credentialId, CancellationToken cancellationToken)
    {
        return new ValueTask<PasskeyCredential?>(_credentials.GetValueOrDefault(Key(credentialId.Span)));
    }

    public ValueTask<IReadOnlyList<PasskeyCredential>> ListAsync(string userId, CancellationToken cancellationToken)
    {
        return new ValueTask<IReadOnlyList<PasskeyCredential>>(
            [.. _credentials.Values.Where(c => c.UserId == userId).OrderBy(c => c.CreatedAt)]);
    }

    public ValueTask<bool> AddAsync(PasskeyCredential credential, CancellationToken cancellationToken)
    {
        return new ValueTask<bool>(_credentials.TryAdd(Key(credential.Id), credential));
    }

    public ValueTask UpdateAsync(PasskeyCredential credential, CancellationToken cancellationToken)
    {
        _credentials[Key(credential.Id)] = credential;
        return default;
    }

    public ValueTask<byte[]> UserHandleAsync(string userId, CancellationToken cancellationToken)
    {
        return new ValueTask<byte[]>(_handles.GetOrAdd(userId, _ => RandomNumberGenerator.GetBytes(32)));
    }

    private static string Key(ReadOnlySpan<byte> id)
    {
        return Convert.ToHexString(id);
    }
}

/// <summary>
///     Signing in with a passkey (WebAuthn, <c>passkey</c>). The client's first message is empty — any passkey of the
///     site the browser offers (a discoverable one) — or <c>{"userId": "…"}</c>, that user's passkeys. The module
///     answers with the request's options as JSON (<c>challenge</c>, <c>rpId</c>, <c>timeout</c>,
///     <c>userVerification</c>, <c>allowCredentials</c>; bytes base64url) — for <c>navigator.credentials.get</c> — and
///     the client answers with the assertion (<c>id</c>, <c>clientDataJSON</c>, <c>authenticatorData</c>,
///     <c>signature</c>, <c>userHandle</c>; base64url). Checked: the challenge (this sign-in's, bound to the connection),
///     the origin, the relying party, the user's presence (and verification, if required), the signature (ES256 or
///     RS256), and the counter (never back).
/// </summary>
public sealed class PasskeyModule(PasskeyOptions options, IPasskeyStore store) : IAuthModule
{
    public const string ModuleName = "passkey";
    public string Name => ModuleName;

    public async ValueTask<ModuleAnswer> StepAsync(ModuleStep step, CancellationToken cancellationToken)
    {
        if (step.Round == 0)
        {
            string? userId = null;
            if (!step.Payload.IsEmpty)
                try
                {
                    using var doc = JsonDocument.Parse(step.Payload);
                    if (doc.RootElement.TryGetProperty("userId", out var u)) userId = u.GetString();
                }
                catch (JsonException)
                {
                    return ModuleAnswer.Refuse("Not a passkey request.");
                }

            var allowed = userId is null ? [] : await store.ListAsync(userId, cancellationToken).ConfigureAwait(false);
            var challenge = Challenges.New(step.Step.Context.Span);
            step.State = new Asked(challenge, [.. allowed.Select(c => Convert.ToHexString(c.Id))]);
            return ModuleAnswer.Continue(WebAuthn.Json(w =>
            {
                w.WriteString("challenge", Base64Url.EncodeToString(challenge));
                w.WriteString("rpId", options.RelyingPartyId);
                w.WriteNumber("timeout", (long)options.Timeout.TotalMilliseconds);
                w.WriteString("userVerification", WebAuthn.Name(options.UserVerification));
                WebAuthn.Credentials(w, "allowCredentials", allowed);
            }));
        }

        if (step.State is not Asked asked) return ModuleAnswer.Refuse("Begin again.");
        WebAuthn.Response r;
        try
        {
            r = WebAuthn.Response.Read(step.Payload.Span, "authenticatorData", "signature");
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException
                                      or InvalidOperationException)
        {
            return ModuleAnswer.Refuse("Not a passkey's answer.");
        }

        var credential = await store.FindAsync(r.Id, cancellationToken).ConfigureAwait(false);
        if (credential is null || asked.Allowed.Length > 0 && !asked.Allowed.Contains(Convert.ToHexString(r.Id)))
            return ModuleAnswer.Refuse("That passkey isn't known here.");
        if (r.UserHandle is { } handle && !handle.AsSpan().SequenceEqual(credential.UserHandle))
            return ModuleAnswer.Refuse("That passkey isn't known here.", credential.UserId);
        if (WebAuthn.CheckClientData(r.ClientDataJson, "webauthn.get", asked.Challenge, step.Step.Context.Span,
                options) is { } bad)
            return ModuleAnswer.Refuse(bad, credential.UserId);
        var data = r.Authenticator!;
        if (WebAuthn.CheckAuthenticatorData(data, options, false) is { } wrong)
            return ModuleAnswer.Refuse(wrong, credential.UserId);
        var signed = new byte[data.Length + 32];
        data.CopyTo(signed, 0);
        SHA256.HashData(r.ClientDataJson, signed.AsSpan(data.Length));
        if (!WebAuthn.Verify(credential.PublicKey, signed, r.Signature!))
            return ModuleAnswer.Refuse("That passkey's proof doesn't check out.", credential.UserId);
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(33));
        if ((count != 0 || credential.SignCount != 0) && count <= credential.SignCount)
            return ModuleAnswer.Refuse("That passkey's counter went back (a copy of it?): refused.", credential.UserId);
        await store.UpdateAsync(credential with { SignCount = count, LastUsedAt = step.Service.Time.GetUtcNow() },
            cancellationToken).ConfigureAwait(false);
        return ModuleAnswer.Prove(new AuthenticatedUser(credential.UserId, ModuleName));
    }

    private sealed record Asked(byte[] Challenge, string[] Allowed);
}

/// <summary>
///     Registering a passkey (WebAuthn, <c>passkey-register</c>) — for a user signed in to the realm already (adding one),
///     or for whoever <see cref="Enroll" /> says the client's first message (an enrollment token from the backend:
///     an invitation, a recovery code) is for. The module answers with the creation options as JSON (<c>challenge</c>,
///     <c>rp</c>, <c>user</c>, <c>pubKeyCredParams</c>: ES256 and RS256, <c>timeout</c>, <c>attestation</c>: none,
///     <c>authenticatorSelection</c>, <c>excludeCredentials</c>) — for <c>navigator.credentials.create</c> — and the
///     client answers with the attestation (<c>id</c>, <c>clientDataJSON</c>, <c>attestationObject</c>,
///     <c>transports</c>). Checked as a sign-in is (no attestation statement: attestation none), the passkey is stored,
///     and the user is signed in by it.
/// </summary>
public sealed class PasskeyRegistrationModule(PasskeyOptions options, IPasskeyStore store) : IAuthModule
{
    public const string ModuleName = "passkey-register";
    public string Name => ModuleName;

    /// <summary>
    ///     Whom an anonymous client's first message (an enrollment token) registers a passkey for: their user id, or null —
    ///     refused. Null (the default): only a user signed in to the realm adds a passkey.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, AuthStep, CancellationToken, ValueTask<string?>>? Enroll { get; init; }

    public async ValueTask<ModuleAnswer> StepAsync(ModuleStep step, CancellationToken cancellationToken)
    {
        if (step.Round == 0)
        {
            var userId = step.Step.Current is { } current && current.Realm == step.Step.Realm &&
                         !current.ExpiredAt(step.Service.Time.GetUtcNow())
                ? current.UserId
                : Enroll is { } enroll && !step.Payload.IsEmpty
                    ? await enroll(step.Payload, step.Step, cancellationToken).ConfigureAwait(false)
                    : null;
            if (userId is null) return ModuleAnswer.Refuse("Sign in first to add a passkey.");
            var handle = await store.UserHandleAsync(userId, cancellationToken).ConfigureAwait(false);
            var existing = await store.ListAsync(userId, cancellationToken).ConfigureAwait(false);
            var challenge = Challenges.New(step.Step.Context.Span);
            var display = step.Step.Current?.Claims?.GetValueOrDefault("name") ?? userId;
            step.State = new Asked(challenge, userId, handle);
            return ModuleAnswer.Continue(WebAuthn.Json(w =>
            {
                w.WriteString("challenge", Base64Url.EncodeToString(challenge));
                w.WriteStartObject("rp");
                w.WriteString("id", options.RelyingPartyId);
                w.WriteString("name", options.RelyingPartyName ?? options.RelyingPartyId);
                w.WriteEndObject();
                w.WriteStartObject("user");
                w.WriteString("id", Base64Url.EncodeToString(handle));
                w.WriteString("name", userId);
                w.WriteString("displayName", display);
                w.WriteEndObject();
                w.WriteStartArray("pubKeyCredParams");
                foreach (var alg in (ReadOnlySpan<int>)[WebAuthn.ES256, WebAuthn.RS256])
                {
                    w.WriteStartObject();
                    w.WriteString("type", "public-key");
                    w.WriteNumber("alg", alg);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteNumber("timeout", (long)options.Timeout.TotalMilliseconds);
                w.WriteString("attestation", "none");
                w.WriteStartObject("authenticatorSelection");
                w.WriteString("residentKey", "preferred");
                w.WriteString("userVerification", WebAuthn.Name(options.UserVerification));
                w.WriteEndObject();
                WebAuthn.Credentials(w, "excludeCredentials", existing);
            }));
        }

        if (step.State is not Asked asked) return ModuleAnswer.Refuse("Begin again.");
        WebAuthn.Response r;
        byte[] authData;
        try
        {
            r = WebAuthn.Response.Read(step.Payload.Span, "attestationObject", null);
            if (Cbor.Read(r.Attestation, out _) is not Dictionary<object, object?> att ||
                att.GetValueOrDefault("authData") is not byte[] data)
                return ModuleAnswer.Refuse("Not a passkey's attestation.");
            authData = data;
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException
                                      or InvalidOperationException)
        {
            return ModuleAnswer.Refuse("Not a passkey's attestation.");
        }

        if (WebAuthn.CheckClientData(r.ClientDataJson, "webauthn.create", asked.Challenge, step.Step.Context.Span,
                options) is { } bad)
            return ModuleAnswer.Refuse(bad, asked.UserId);
        if (WebAuthn.CheckAuthenticatorData(authData, options, true) is { } wrong)
            return ModuleAnswer.Refuse(wrong, asked.UserId);
        // attested credential data: aaguid(16) | u16 length | credential id | COSE key
        if (authData.Length < 37 + 18) return ModuleAnswer.Refuse("Not a passkey's attestation.");
        int idLength = BinaryPrimitives.ReadUInt16BigEndian(authData.AsSpan(53));
        if (idLength is 0 or > 1023 || 55 + idLength >= authData.Length)
            return ModuleAnswer.Refuse("Not a passkey's attestation.");
        var id = authData.AsSpan(55, idLength).ToArray();
        if (!id.AsSpan().SequenceEqual(r.Id)) return ModuleAnswer.Refuse("Not a passkey's attestation.");
        byte[] key;
        try
        {
            Cbor.Read(authData.AsSpan(55 + idLength), out var keyLength);
            key = authData.AsSpan(55 + idLength, keyLength).ToArray();
            WebAuthn.CheckKey(key);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or KeyNotFoundException
                                      or CryptographicException)
        {
            return ModuleAnswer.Refuse("That passkey's key isn't one this site takes (ES256 or RS256).");
        }

        var now = step.Service.Time.GetUtcNow();
        var credential = new PasskeyCredential(id, asked.UserId, asked.Handle, key,
            BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33)), now, r.Transports);
        if (!await store.AddAsync(credential, cancellationToken).ConfigureAwait(false))
            return ModuleAnswer.Refuse("That passkey is registered already.");
        return ModuleAnswer.Prove(new AuthenticatedUser(asked.UserId, PasskeyModule.ModuleName));
    }

    private sealed record Asked(byte[] Challenge, string UserId, byte[] Handle);
}

/// <summary>The checks both passkey modules share.</summary>
internal static class WebAuthn
{
    public const int ES256 = -7, RS256 = -257;
    private const byte UserPresent = 0x01, UserVerified = 0x04, AttestedData = 0x40;

    public static string Name(UserVerification v)
    {
        return v switch
        {
            UserVerification.Required => "required", UserVerification.Discouraged => "discouraged", _ => "preferred"
        };
    }

    public static byte[] Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static void Credentials(Utf8JsonWriter w, string name, IReadOnlyList<PasskeyCredential> credentials)
    {
        w.WriteStartArray(name);
        foreach (var c in credentials)
        {
            w.WriteStartObject();
            w.WriteString("type", "public-key");
            w.WriteString("id", Base64Url.EncodeToString(c.Id));
            if (c.Transports is { Count: > 0 } transports)
            {
                w.WriteStartArray("transports");
                foreach (var t in transports) w.WriteStringValue(t);
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    /// <summary>What the browser says it signed: its type, this sign-in's challenge (bound to the connection), an allowed origin. Null: fine.</summary>
    public static string? CheckClientData(byte[] json, string type, byte[] challenge, ReadOnlySpan<byte> context,
        PasskeyOptions options)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (o.GetProperty("type").GetString() != type) return "Not the browser's answer to this.";
            var said = Base64Url.DecodeFromChars(o.GetProperty("challenge").GetString());
            if (!Challenges.Check(said, challenge, context)) return "That answers another sign-in.";
            var origin = o.GetProperty("origin").GetString();
            if (origin is null || !options.Origins.Contains(origin, StringComparer.Ordinal))
                return "That passkey was used from a page not of this site.";
            if (o.TryGetProperty("crossOrigin", out var cross) && cross.ValueKind == JsonValueKind.True)
                return "That passkey was used inside another site's frame.";
            return null;
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException
                                      or InvalidOperationException)
        {
            return "Not the browser's answer.";
        }
    }

    /// <summary>The authenticator's data: this relying party's, the user present (verified, if required), attested credential data if registering. Null: fine.</summary>
    public static string? CheckAuthenticatorData(byte[] data, PasskeyOptions options, bool registering)
    {
        if (data.Length < 37) return "Not an authenticator's data.";
        Span<byte> rp = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(options.RelyingPartyId), rp);
        if (!data.AsSpan(0, 32).SequenceEqual(rp)) return "That passkey belongs to another site.";
        var flags = data[32];
        if ((flags & UserPresent) == 0) return "The user wasn't present.";
        if (options.UserVerification == UserVerification.Required && (flags & UserVerified) == 0)
            return "The user wasn't verified.";
        if (registering && (flags & AttestedData) == 0) return "Not a new passkey.";
        return null;
    }

    /// <summary>A COSE key this site takes — ES256 on P-256, or RS256 of 2,048 bits or more — else it throws.</summary>
    public static void CheckKey(byte[] cose)
    {
        using var _ = Key(cose);
    }

    public static bool Verify(byte[] cose, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var key = Key(cose);
            return key switch
            {
                ECDsa ec => ec.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence),
                RSA rsa => rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                _ => false
            };
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or KeyNotFoundException
                                      or CryptographicException)
        {
            return false;
        }
    }

    private static AsymmetricAlgorithm Key(byte[] cose)
    {
        if (Cbor.Read(cose, out _) is not Dictionary<object, object?> k)
            throw new FormatException("Not a COSE key.");
        long kty = (long)k[1L]!, alg = (long)k[3L]!;
        if (kty == 2 && alg == ES256 && (long)k[-1L]! == 1 && k[-2L] is byte[] { Length: 32 } x &&
            k[-3L] is byte[] { Length: 32 } y)
            return ECDsa.Create(new ECParameters
                { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
        if (kty == 3 && alg == RS256 && k[-1L] is byte[] n && k[-2L] is byte[] e && n.Length >= 256)
            return RSA.Create(new RSAParameters { Modulus = n, Exponent = e });
        throw new FormatException("A COSE key this site doesn't take.");
    }

    /// <summary>The browser's answer, as JSON with its bytes base64url.</summary>
    public sealed class Response
    {
        public byte[] Attestation = [];
        public byte[]? Authenticator, Signature, UserHandle;
        public byte[] ClientDataJson = [];
        public byte[] Id = [];
        public IReadOnlyList<string>? Transports;

        public static Response Read(ReadOnlySpan<byte> json, string data, string? signature)
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var o = doc.RootElement;
            var r = new Response
            {
                Id = Bytes(o, "id"), ClientDataJson = Bytes(o, "clientDataJSON")
            };
            if (signature is null) r.Attestation = Bytes(o, data);
            else
            {
                r.Authenticator = Bytes(o, data);
                r.Signature = Bytes(o, signature);
                if (o.TryGetProperty("userHandle", out var h) && h.ValueKind == JsonValueKind.String)
                    r.UserHandle = Base64Url.DecodeFromChars(h.GetString());
            }

            if (o.TryGetProperty("transports", out var t) && t.ValueKind == JsonValueKind.Array)
                r.Transports = [.. t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length is > 0 and < 32)];
            return r;
        }

        private static byte[] Bytes(JsonElement o, string name)
        {
            return Base64Url.DecodeFromChars(o.GetProperty(name).GetString());
        }
    }
}
