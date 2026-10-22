// Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see LICENSE).

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Tapline.Client.Opaque;

/// <summary>
///     The OPAQUE modules' messages (Tapline.Authentication.Opaque's), inside a sign-in's (<see cref="AuthMessages" />) — a user id or an enrollment token
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
    /// <summary>The sign-in module's name (<c>opaque</c>).</summary>
    public const string SignInModule = "opaque";

    /// <summary>The registration module's name (<c>opaque-register</c>).</summary>
    public const string RegistrationModule = "opaque-register";

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
            return await client.SignInAsync(realm, OpaqueMessages.SignInModule, first,
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
            return await client.SignInAsync(realm, OpaqueMessages.RegistrationModule,
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
