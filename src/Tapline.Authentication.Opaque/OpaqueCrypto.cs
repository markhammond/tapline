// Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see LICENSE).

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Wasmtime;
using WasmModule = Wasmtime.Module;

namespace Tapline.Authentication.Opaque;

/// <summary>Why a call was refused (see <see cref="OpaqueException" />).</summary>
public enum OpaqueStatus
{
    /// <summary>The login failed: a wrong password (the client's finish), or a client that couldn't prove it (the server's).</summary>
    InvalidLogin = 1,

    /// <summary>An input that isn't what it should be: a message, a state, a setup or a password file.</summary>
    InvalidInput = 2,

    /// <summary>Anything else the protocol refused.</summary>
    ProtocolError = 3
}

/// <summary>A call the module refused.</summary>
public sealed class OpaqueException(OpaqueStatus status) : Exception($"OPAQUE: {status}")
{
    public OpaqueStatus Status { get; } = status;

    /// <summary>A wrong password, or a client that couldn't prove it.</summary>
    public bool InvalidLogin => Status == OpaqueStatus.InvalidLogin;
}

/// <summary>Login's or registration's first message, and the state its side keeps for its next step.</summary>
public sealed record OpaqueStart(byte[] Message, byte[] State);

/// <summary>The client's registration finished: the upload for the server, its export key, the server's public key.</summary>
public sealed record OpaqueRegistered(byte[] Upload, byte[] ExportKey, byte[] ServerPublicKey);

/// <summary>The client's login finished: the finalization (KE3) for the server, the session key, the export key, the server's public key.</summary>
public sealed record OpaqueLoggedIn(byte[] Finalization, byte[] SessionKey, byte[] ExportKey, byte[] ServerPublicKey);

/// <summary>
///     OPAQUE (RFC 9807) through <c>opaque.wasm</c> (built from <c>wasm</c>, beside it; opaque-ke's review by NCC Group
///     in 2021 was of an earlier release, not of 4.0.1, nor of this code) — opaque-ke 4, ristretto255, Argon2id (64 MiB, 3 passes, 4 lanes) — in
///     Wasmtime: the module the browser runs (<c>opaque.mjs</c>), the same bytes. The server's steps, and a headless client's. Thread-safe: each
///     call has a store and an instance of its own, so nothing of one login is there for the next, its memory bounded
///     (the server's steps 16 MiB; the client's, which stretch the password, 128 MiB). Inputs are copied in, and zeroed and
///     released when the call returns; outputs are copied out, and the module's copy zeroed. A password or credential id
///     given as a string is normalized (NFC) and encoded as UTF-8, as the JavaScript host does.
/// </summary>
public sealed class OpaqueCrypto : IDisposable
{
    private const long ServerMemory = 16L << 20, ClientMemory = 128L << 20;
    private readonly Engine _engine;
    private readonly WasmModule _module;

    private OpaqueCrypto(byte[] wasm)
    {
        _engine = new Engine(Configure());
        _module = WasmModule.FromBytes(_engine, "opaque", wasm);
    }

    public void Dispose()
    {
        _module.Dispose();
        _engine.Dispose();
    }

    /// <summary>The module built into this assembly (<c>opaque.wasm</c>). Load it once: compiling it takes a moment.</summary>
    public static OpaqueCrypto Load()
    {
        using var s = typeof(OpaqueCrypto).Assembly.GetManifestResourceStream("opaque.wasm")
                      ?? throw new InvalidOperationException("opaque.wasm isn't built in.");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return new OpaqueCrypto(bytes);
    }

    /// <summary>A module of these bytes (the same ABI).</summary>
    public static OpaqueCrypto Load(byte[] wasm)
    {
        return new OpaqueCrypto(wasm);
    }

    /// <summary>A password or credential id as the module takes it: normalized (NFC), UTF-8.</summary>
    public static byte[] Utf8(string text)
    {
        return Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormC));
    }

    // ───────────────────────────── the server's ─────────────────────────────

    /// <summary>A new setup — its OPRF seed and key pair: the server's secret, kept for every registration and login.</summary>
    public byte[] ServerSetup()
    {
        return Call(ServerMemory, "opaque_server_setup")[0];
    }

    /// <summary>The setup's public key (for a client that pins it).</summary>
    public byte[] ServerPublicKey(byte[] setup)
    {
        return Call(ServerMemory, "opaque_server_public_key", setup)[0];
    }

    /// <summary>Registration's response, for the user <paramref name="credentialId" /> names (the key for their password file).</summary>
    public byte[] ServerRegistrationStart(byte[] setup, byte[] request, byte[] credentialId)
    {
        return Call(ServerMemory, "opaque_server_registration_start", setup, request, credentialId)[0];
    }

    /// <summary>The user's password file, from the client's upload — as sensitive as a password's hash.</summary>
    public byte[] ServerRegistrationFinish(byte[] upload)
    {
        return Call(ServerMemory, "opaque_server_registration_finish", upload)[0];
    }

    /// <summary>
    ///     Login's response (KE2) and the server's state, bound to <paramref name="context" />. No password file (null): no
    ///     such user — the response looks the same, and the login fails at the client.
    /// </summary>
    public OpaqueStart ServerLoginStart(byte[] setup, byte[]? passwordFile, byte[] request, byte[] credentialId,
        byte[] context)
    {
        var r = Call(ServerMemory, "opaque_server_login_start", setup, passwordFile ?? [], request, credentialId,
            context);
        return new OpaqueStart(r[0], r[1]);
    }

    /// <summary>The session key — the client's, the same — if it proved it knows the password (else <see cref="OpaqueStatus.InvalidLogin" />).</summary>
    public byte[] ServerLoginFinish(byte[] state, byte[] finalization, byte[] context)
    {
        return Call(ServerMemory, "opaque_server_login_finish", state, finalization, context)[0];
    }

    // ───────────────────────────── a client's ─────────────────────────────

    /// <summary>Registration's request and the client's state.</summary>
    public OpaqueStart ClientRegistrationStart(byte[] password)
    {
        var r = Call(ClientMemory, "opaque_client_registration_start", password);
        return new OpaqueStart(r[0], r[1]);
    }

    /// <summary>The upload for the server, the export key and the server's public key; the password stretched here.</summary>
    public OpaqueRegistered ClientRegistrationFinish(byte[] state, byte[] password, byte[] response)
    {
        var r = Call(ClientMemory, "opaque_client_registration_finish", state, password, response);
        return new OpaqueRegistered(r[0], r[1], r[2]);
    }

    /// <summary>Login's request (KE1) and the client's state.</summary>
    public OpaqueStart ClientLoginStart(byte[] password)
    {
        var r = Call(ClientMemory, "opaque_client_login_start", password);
        return new OpaqueStart(r[0], r[1]);
    }

    /// <summary>
    ///     The finalization (KE3), the session key, the export key and the server's public key — or
    ///     <see cref="OpaqueStatus.InvalidLogin" />, a wrong password; the password stretched here.
    /// </summary>
    public OpaqueLoggedIn ClientLoginFinish(byte[] state, byte[] password, byte[] response, byte[] context)
    {
        var r = Call(ClientMemory, "opaque_client_login_finish", state, password, response, context);
        return new OpaqueLoggedIn(r[0], r[1], r[2], r[3]);
    }

    // ───────────────────────────── the module ─────────────────────────────

    /// <summary>One call, in a store of its own: inputs copied in as (pointer, length) pairs, outputs read and zeroed, inputs released.</summary>
    private byte[][] Call(long memoryLimit, string name, params byte[][] inputs)
    {
        using var store = new Store(_engine);
        store.SetLimits(memoryLimit, 10_000, 1, 4, 1);
        using var linker = new Linker(_engine);
        linker.DefineFunction("tapline", "random", (Caller caller, int at, int length) =>
            RandomNumberGenerator.Fill(caller.GetMemory("memory")!.GetSpan(at, length)));
        var instance = linker.Instantiate(store, _module);
        var memory = instance.GetMemory("memory") ?? throw Missing("memory");
        var alloc = instance.GetFunction<int, int>("opaque_alloc") ?? throw Missing("opaque_alloc");
        var free = instance.GetAction<int, int>("opaque_free") ?? throw Missing("opaque_free");
        var outputAt = instance.GetFunction<int>("opaque_output_ptr") ?? throw Missing("opaque_output_ptr");
        var outputLength = instance.GetFunction<int>("opaque_output_len") ?? throw Missing("opaque_output_len");
        var clear = instance.GetAction("opaque_clear_output") ?? throw Missing("opaque_clear_output");
        var call = instance.GetFunction(name) ?? throw Missing(name);

        var held = new (int At, int Length)[inputs.Length];
        try
        {
            var args = new ValueBox[inputs.Length * 2];
            for (var i = 0; i < inputs.Length; i++)
            {
                var at = alloc(inputs[i].Length);
                inputs[i].CopyTo(memory.GetSpan(at, inputs[i].Length)); // (a span now: an allocation may grow memory)
                held[i] = (at, inputs[i].Length);
                args[2 * i] = at;
                args[2 * i + 1] = inputs[i].Length;
            }

            var status = (int)call.Invoke(args)!;
            if (status != 0) throw new OpaqueException((OpaqueStatus)status);
            var output = memory.GetSpan(outputAt(), outputLength());
            var parts = new List<byte[]>();
            for (var at = 0; at < output.Length;)
            {
                var n = BinaryPrimitives.ReadInt32LittleEndian(output[at..]);
                parts.Add(output.Slice(at + 4, n).ToArray());
                at += 4 + n;
            }

            clear();
            return [.. parts];
        }
        finally
        {
            foreach (var (at, length) in held)
                if (length > 0 || at != 0)
                    free(at, length);
        }
    }

    private static InvalidOperationException Missing(string export)
    {
        return new InvalidOperationException($"opaque.wasm has no {export}: not the module this host is for.");
    }

    /// <summary>
    ///     Wasmtime catches its traps on macOS through Mach exception ports by default — and then hands .NET's own faults
    ///     on a thread that ran a module (the hardware faults behind a NullReferenceException) to itself, and the process
    ///     dies. Unix signals instead, as Tapline.Wasm does (whose binding calls the setting by its older name: by this one).
    /// </summary>
    private static Config Configure()
    {
        var config = new Config();
        if (!OperatingSystem.IsMacOS()) return config;
        try
        {
            return config.WithMacosMachPorts(false);
        }
        catch (EntryPointNotFoundException)
        {
            var handle = (SafeHandle)typeof(Config)
                .GetProperty("NativeHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(config)!;
            wasmtime_config_macos_use_mach_ports_set(handle.DangerousGetHandle(), false);
            return config;
        }
    }

    [DllImport("wasmtime")]
    private static extern void wasmtime_config_macos_use_mach_ports_set(IntPtr config,
        [MarshalAs(UnmanagedType.I1)] bool enable);
}
