// Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see LICENSE).

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Tapline.Client.Opaque;

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
///     Signing in with a password by OPAQUE (<c>opaque</c>; RFC 9807): the password never leaves the client — the router
///     relays KE1, KE2 and KE3, and this module (the server) proves nothing but that the client knew the password the
///     user registered. The login is bound to the connection (its context, <see cref="AuthStep.Context" />). A user with
///     no password file is answered as any other, and fails at the client, as a wrong password does — whether an account
///     exists isn't told. A wrong password fails at the client too, which gives up without telling: every login begun
///     counts as a failure for its user until it succeeds (<see cref="ModuleAnswer.AttemptBy" />).
/// </summary>
public sealed class OpaqueModule(OpaqueCrypto opaque, IOpaqueStore store) : IAuthModule
{
    public const string ModuleName = OpaqueMessages.SignInModule;
    public string Name => ModuleName;

    public async ValueTask<ModuleAnswer> StepAsync(ModuleStep step, CancellationToken cancellationToken)
    {
        var context = step.Step.Context.ToArray();
        if (step.Round == 0)
        {
            if (!OpaqueMessages.TryRead(step.Payload.Span, out var id, out var ke1) ||
                OpaqueUserIds.Parse(id) is not { } userId)
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
    public const string ModuleName = OpaqueMessages.RegistrationModule;
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

/// <summary>A user id as a sign-in carries it: NFC, UTF-8, at most <see cref="ClientIdentity.MaxName" /> bytes.</summary>
internal static class OpaqueUserIds
{
    public static string? Parse(byte[] utf8)
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
