// Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see LICENSE).

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Tapline.Client;

namespace Tapline.Authentication.Opaque;

/// <summary>
///     Where OPAQUE's secrets are kept — the backend's: the server's setup (its OPRF seed and key pair: made once, kept for
///     every registration and login, shared by a realm's instances) and each user's password file (as sensitive as a
///     password's hash). <see cref="InMemoryOpaqueStore" /> for samples and tests.
/// </summary>
public interface IOpaqueStore
{
    /// <summary>The server's setup: <paramref name="create" />d and kept the first time, the same ever after.</summary>
    ValueTask<byte[]> SetupAsync(Func<byte[]> create, CancellationToken cancellationToken);

    /// <summary>The user's password file, or null: none (the login then fails at the client, as a wrong password does).</summary>
    ValueTask<byte[]?> PasswordFileAsync(string userId, CancellationToken cancellationToken);

    /// <summary>The user's password file, new or replacing theirs.</summary>
    ValueTask SavePasswordFileAsync(string userId, byte[] passwordFile, CancellationToken cancellationToken);
}

/// <summary>OPAQUE's secrets in memory: gone when the process is.</summary>
public sealed class InMemoryOpaqueStore : IOpaqueStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private byte[]? _setup;

    public ValueTask<byte[]> SetupAsync(Func<byte[]> create, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return new ValueTask<byte[]>(_setup ??= create());
        }
    }

    public ValueTask<byte[]?> PasswordFileAsync(string userId, CancellationToken cancellationToken)
    {
        return new ValueTask<byte[]?>(_files.GetValueOrDefault(userId));
    }

    public ValueTask SavePasswordFileAsync(string userId, byte[] passwordFile, CancellationToken cancellationToken)
    {
        _files[userId] = passwordFile;
        return default;
    }
}

/// <summary>
///     The OPAQUE modules' messages, inside a sign-in's (<see cref="AuthMessages" />) — a user id or an enrollment token
///     first, as <c>u16 length | UTF-8</c> (little-endian), then the protocol's own message:
///     <code>
/// opaque           client: id | KE1    server: KE2    client: KE3
/// opaque-register  client: token | registration request    server: id | registration response    client: upload
/// </code>
///     The id is the user's (NFC, UTF-8: the credential id their password file is kept under); the token, empty for a
///     user signed in to the realm already.
/// </summary>
public static class OpaqueMessages
{
    public static byte[] Framed(ReadOnlySpan<byte> first, ReadOnlySpan<byte> rest)
    {
        if (first.Length > ushort.MaxValue) throw new ArgumentException("At most 65,535 bytes.", nameof(first));
        var b = new byte[2 + first.Length + rest.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)first.Length);
        first.CopyTo(b.AsSpan(2));
        rest.CopyTo(b.AsSpan(2 + first.Length));
        return b;
    }

    public static bool TryRead(ReadOnlySpan<byte> message, out byte[] first, out byte[] rest)
    {
        (first, rest) = ([], []);
        if (message.Length < 2) return false;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(message);
        if (2 + n > message.Length) return false;
        (first, rest) = (message.Slice(2, n).ToArray(), message[(2 + n)..].ToArray());
        return true;
    }

    internal static string? UserId(byte[] utf8)
    {
        try
        {
            var id = new UTF8Encoding(false, true).GetString(utf8);
            return id.Length > 0 && utf8.Length <= ClientIdentity.MaxName && id.IsNormalized(NormalizationForm.FormC)
                ? id
                : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>
///     Signing in with a password by OPAQUE (<c>opaque</c>; RFC 9807): the password never leaves the client — the router
///     relays KE1, KE2 and KE3, and this module (the server) proves nothing but that the client knew the password the
///     user registered. The login is bound to the connection (its context, <see cref="AuthStep.Context" />). A user with
///     no password file is answered as any other, and fails at the client, as a wrong password does — whether an account
///     exists isn't told. A wrong password fails at the client too, which gives up without telling: every login begun
///     counts as a failure for its user until it succeeds (<see cref="ModuleAnswer.AttemptBy" />).
/// </summary>
public sealed class OpaqueModule(OpaqueCrypto opaque, IOpaqueStore store) : IAuthModule
{
    public const string ModuleName = "opaque";
    public string Name => ModuleName;

    public async ValueTask<ModuleAnswer> StepAsync(ModuleStep step, CancellationToken cancellationToken)
    {
        var context = step.Step.Context.ToArray();
        if (step.Round == 0)
        {
            if (!OpaqueMessages.TryRead(step.Payload.Span, out var id, out var ke1) ||
                OpaqueMessages.UserId(id) is not { } userId)
                return ModuleAnswer.Refuse("Not a password sign-in.");
            var setup = await store.SetupAsync(opaque.ServerSetup, cancellationToken).ConfigureAwait(false);
            var file = await store.PasswordFileAsync(userId, cancellationToken).ConfigureAwait(false);
            try
            {
                var start = opaque.ServerLoginStart(setup, file, ke1, id, context);
                step.State = new Login(userId, start.State);
                return ModuleAnswer.Continue(start.Message, userId);
            }
            catch (OpaqueException)
            {
                return ModuleAnswer.Refuse("Not a password sign-in.");
            }
        }

        if (step.State is not Login login) return ModuleAnswer.Refuse("Begin again.");
        try
        {
            opaque.ServerLoginFinish(login.ServerState, step.Payload.ToArray(), context);
            return ModuleAnswer.Prove(new AuthenticatedUser(login.UserId, ModuleName));
        }
        catch (OpaqueException e)
        {
            return ModuleAnswer.Refuse(e.InvalidLogin ? "Wrong user id or password." : "Not a password sign-in.",
                login.UserId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(login.ServerState);
        }
    }

    private sealed record Login(string UserId, byte[] ServerState);
}

/// <summary>
///     Setting a password by OPAQUE (<c>opaque-register</c>): for a user signed in to the realm already (changing theirs),
///     or for whoever <see cref="Enroll" /> says the client's enrollment token (an invitation, a reset link's) is for. The
///     client stretches the password (Argon2id) and uploads what the server keeps — the password file — never the
///     password; then the user is signed in.
/// </summary>
public sealed class OpaqueRegistrationModule(OpaqueCrypto opaque, IOpaqueStore store) : IAuthModule
{
    public const string ModuleName = "opaque-register";
    public string Name => ModuleName;

    /// <summary>
    ///     Whom an anonymous client's enrollment token sets a password for: their user id, or null — refused. Null (the
    ///     default): only a user signed in to the realm sets theirs.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, AuthStep, CancellationToken, ValueTask<string?>>? Enroll { get; init; }

    public async ValueTask<ModuleAnswer> StepAsync(ModuleStep step, CancellationToken cancellationToken)
    {
        if (step.Round == 0)
        {
            if (!OpaqueMessages.TryRead(step.Payload.Span, out var token, out var request))
                return ModuleAnswer.Refuse("Not a password registration.");
            var userId = step.Step.Current is { } current && current.Realm == step.Step.Realm &&
                         !current.ExpiredAt(step.Service.Time.GetUtcNow())
                ? current.UserId
                : Enroll is { } enroll && token.Length > 0
                    ? await enroll(token, step.Step, cancellationToken).ConfigureAwait(false)
                    : null;
            if (userId is null) return ModuleAnswer.Refuse("Sign in first to set a password.");
            var id = OpaqueCrypto.Utf8(userId);
            var setup = await store.SetupAsync(opaque.ServerSetup, cancellationToken).ConfigureAwait(false);
            try
            {
                var response = opaque.ServerRegistrationStart(setup, request, id);
                step.State = userId;
                return ModuleAnswer.Continue(OpaqueMessages.Framed(id, response));
            }
            catch (OpaqueException)
            {
                return ModuleAnswer.Refuse("Not a password registration.");
            }
        }

        if (step.State is not string user) return ModuleAnswer.Refuse("Begin again.");
        byte[] file;
        try
        {
            file = opaque.ServerRegistrationFinish(step.Payload.ToArray());
        }
        catch (OpaqueException)
        {
            return ModuleAnswer.Refuse("Not a password registration.", user);
        }

        await store.SavePasswordFileAsync(user, file, cancellationToken).ConfigureAwait(false);
        return ModuleAnswer.Prove(new AuthenticatedUser(user, OpaqueModule.ModuleName));
    }
}

/// <summary>A headless client's side (Tapline.Client): signing in with a password, and setting one, by OPAQUE.</summary>
public static class OpaqueSignIn
{
    /// <summary>
    ///     Signs in to <paramref name="realm" /> as <paramref name="userId" /> with <paramref name="password" />, by OPAQUE:
    ///     the password stretched here (Argon2id, some hundreds of milliseconds), never sent. A wrong password — or no such
    ///     user — is a refusal, <c>Wrong user id or password.</c>
    /// </summary>
    public static async Task<SignInResult> SignInWithPasswordAsync(this TaplineClient client, OpaqueCrypto opaque,
        string realm, string userId, string password, bool renewal = false,
        CancellationToken cancellationToken = default)
    {
        var context = client.SignInContext(realm) ?? throw new InvalidOperationException("Not bound to a router yet.");
        var secret = OpaqueCrypto.Utf8(password);
        try
        {
            var start = opaque.ClientLoginStart(secret);
            var first = OpaqueMessages.Framed(OpaqueCrypto.Utf8(userId), start.Message);
            return await client.SignInAsync(realm, OpaqueModule.ModuleName, first,
                (ke2, _) => new ValueTask<ReadOnlyMemory<byte>>(
                    opaque.ClientLoginFinish(start.State, secret, ke2.ToArray(), context).Finalization),
                renewal, cancellationToken).ConfigureAwait(false);
        }
        catch (OpaqueException e) when (e.InvalidLogin)
        {
            return new SignInResult(false, null, default, "Wrong user id or password.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    ///     Sets <paramref name="password" /> in <paramref name="realm" /> — for the user signed in, or for whoever
    ///     <paramref name="enrollment" />'s token is for — by OPAQUE, and signs in by it.
    /// </summary>
    public static async Task<SignInResult> RegisterPasswordAsync(this TaplineClient client, OpaqueCrypto opaque,
        string realm, string password, ReadOnlyMemory<byte> enrollment = default,
        CancellationToken cancellationToken = default)
    {
        var secret = OpaqueCrypto.Utf8(password);
        try
        {
            var start = opaque.ClientRegistrationStart(secret);
            return await client.SignInAsync(realm, OpaqueRegistrationModule.ModuleName,
                OpaqueMessages.Framed(enrollment.Span, start.Message), (message, cancellation) =>
                {
                    if (!OpaqueMessages.TryRead(message.Span, out _, out var response))
                        throw new InvalidOperationException("Not a password registration's response.");
                    return new ValueTask<ReadOnlyMemory<byte>>(
                        opaque.ClientRegistrationFinish(start.State, secret, response).Upload);
                }, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
