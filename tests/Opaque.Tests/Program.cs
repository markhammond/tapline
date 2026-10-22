// Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see src/Tapline.Authentication.Opaque/LICENSE).
//
// Tapline.Client.Opaque's host (OpaqueCrypto: opaque.wasm in Wasmtime): registration and login with both sides
// here, every refusal, logins at once on many threads, passwords normalized — and the JavaScript host (Node, running the
// page's opaque.mjs (the package's wwwroot)) as the client of a server here, the deployment's shape. `dotnet run --project tests/Opaque.Tests`.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tapline.Client.Opaque;

using var opaque = OpaqueCrypto.Load();
var setup = opaque.ServerSetup();
var serverKey = opaque.ServerPublicKey(setup);
var context = Encoding.UTF8.GetBytes("router 7f3a · outlet nonce 1234 · realm north");
var notes = new List<string>();
int passed = 0, failed = 0;

Check("Both sides here: registered, then logged in — one session key, the export key registration's", () =>
{
    var started = Stopwatch.GetTimestamp();
    var (file, registered) = Register("alice", OpaqueCrypto.Utf8("correct horse"));
    var registerMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    started = Stopwatch.GetTimestamp();
    var (client, sessionKey) = Login(file, "alice", OpaqueCrypto.Utf8("correct horse"));
    var loginMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    Assert(client.SessionKey.SequenceEqual(sessionKey) && sessionKey.Length == 64, "one session key, 64 bytes");
    Assert(client.ExportKey.SequenceEqual(registered.ExportKey), "the export key is registration's");
    Assert(client.ServerPublicKey.SequenceEqual(serverKey) && registered.ServerPublicKey.SequenceEqual(serverKey),
        "registered with, and logged in to, this server");
    notes.Add($"registered in {registerMs:F0} ms, logged in in {loginMs:F0} ms (Argon2id at the client); " +
              $"password file {file.Length} B, KE3 {client.Finalization.Length} B, session key {sessionKey.Length} B");
});

Check("Refused: a wrong password, no such user, another user's key, another context, a tampered KE3, malformed input", () =>
{
    var (file, _) = Register("bob", OpaqueCrypto.Utf8("battery staple"));
    Refused(OpaqueStatus.InvalidLogin, () => Login(file, "bob", OpaqueCrypto.Utf8("battery stapler")), "a wrong password");
    Refused(OpaqueStatus.InvalidLogin, () => Login(null, "nobody", OpaqueCrypto.Utf8("battery staple")), "no such user");
    Refused(OpaqueStatus.InvalidLogin, () => Login(file, "carol", OpaqueCrypto.Utf8("battery staple")), "another user's key");
    Refused(OpaqueStatus.InvalidLogin,
        () => Login(file, "bob", OpaqueCrypto.Utf8("battery staple"), Encoding.UTF8.GetBytes("another connection")),
        "a login bound to another context");
    var start = opaque.ClientLoginStart(OpaqueCrypto.Utf8("battery staple"));
    var server = opaque.ServerLoginStart(setup, file, start.Message, OpaqueCrypto.Utf8("bob"), context);
    var client = opaque.ClientLoginFinish(start.State, OpaqueCrypto.Utf8("battery staple"), server.Message, context);
    var tampered = (byte[])client.Finalization.Clone();
    tampered[0] ^= 1;
    Refused(OpaqueStatus.InvalidLogin, () => opaque.ServerLoginFinish(server.State, tampered, context), "a tampered KE3");
    Refused(OpaqueStatus.InvalidInput, () => opaque.ServerRegistrationFinish(new byte[7]), "an upload that isn't one");
    Refused(OpaqueStatus.InvalidInput, () => opaque.ServerPublicKey(new byte[3]), "a setup that isn't one");
});

Check("Passwords normalized: registered decomposed (NFD), logged in composed (NFC)", () =>
{
    Assert(OpaqueCrypto.Utf8("café au lait").SequenceEqual(OpaqueCrypto.Utf8("café au lait")), "the same bytes");
    var (file, _) = Register("dana", OpaqueCrypto.Utf8("café au lait"));
    Login(file, "dana", OpaqueCrypto.Utf8("café au lait"));
});

Check("Logins at once: 8 on as many threads, each in a store of its own", () =>
{
    var (file, _) = Register("erin", OpaqueCrypto.Utf8("tr0ub4dor&3"));
    var started = Stopwatch.GetTimestamp();
    var agreed = 0;
    Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
    {
        var (client, sessionKey) = Login(file, "erin", OpaqueCrypto.Utf8("tr0ub4dor&3"));
        if (client.SessionKey.SequenceEqual(sessionKey)) Interlocked.Increment(ref agreed);
    });
    Assert(agreed == 8, $"{agreed} of 8 agreed a session key");
    notes.Add($"8 logins at once in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");
});

var node = FindNode();
if (node is null)
{
    Console.WriteLine("skip  The JavaScript host against this one: no node on the PATH");
}
else
{
    Check("The JavaScript host (the page's opaque.mjs (the package's wwwroot), in Node) as client of this server: registered, logged in, one session key; a wrong password refused; its password normalized as here", () =>
    {
        using var js = new Js(node);
        // Registration: the client's steps there, the server's here — its password decomposed there, composed here.
        var reg = js.Call("clientRegistrationStart", "não é segredo");
        var response = opaque.ServerRegistrationStart(setup, reg.Hex("request"), OpaqueCrypto.Utf8("filipa"));
        var up = js.Call("clientRegistrationFinish", reg.Hex("state"), "não é segredo", response);
        var file = opaque.ServerRegistrationFinish(up.Hex("upload"));
        Assert(up.Hex("serverPublicKey").SequenceEqual(serverKey), "registered with this server");

        // Login: KE1 there, KE2 here, KE3 there, the session key here — the same as the client's.
        var start = js.Call("clientLoginStart", "não é segredo");
        var server = opaque.ServerLoginStart(setup, file, start.Hex("request"), OpaqueCrypto.Utf8("filipa"), context);
        var done = js.Call("clientLoginFinish", start.Hex("state"), "não é segredo", server.Message, context);
        var sessionKey = opaque.ServerLoginFinish(server.State, done.Hex("finalization"), context);
        Assert(done.Hex("sessionKey").SequenceEqual(sessionKey), "one session key, at both hosts");
        Assert(done.Hex("exportKey").SequenceEqual(up.Hex("exportKey")), "the export key is registration's");

        // A wrong password: refused at the client, there.
        var wrong = js.Call("clientLoginStart", "outro segredo");
        var answer = opaque.ServerLoginStart(setup, file, wrong.Hex("request"), OpaqueCrypto.Utf8("filipa"), context);
        var refused = js.Call("clientLoginFinish", wrong.Hex("state"), "outro segredo", answer.Message, context);
        Assert(refused.Error == (int)OpaqueStatus.InvalidLogin, "a wrong password, refused there");
    });
}

foreach (var note in notes) Console.WriteLine($"  · {note}");
Console.WriteLine(failed == 0 ? $"All {passed} tests passed." : $"{failed} of {passed + failed} tests FAILED.");
return failed == 0 ? 0 : 1;

(byte[] File, OpaqueRegistered Registered) Register(string user, byte[] password)
{
    var start = opaque.ClientRegistrationStart(password);
    var response = opaque.ServerRegistrationStart(setup, start.Message, OpaqueCrypto.Utf8(user));
    var registered = opaque.ClientRegistrationFinish(start.State, password, response);
    return (opaque.ServerRegistrationFinish(registered.Upload), registered);
}

(OpaqueLoggedIn Client, byte[] SessionKey) Login(byte[]? file, string user, byte[] password,
    byte[]? clientContext = null)
{
    var start = opaque.ClientLoginStart(password);
    var server = opaque.ServerLoginStart(setup, file, start.Message, OpaqueCrypto.Utf8(user), context);
    var client = opaque.ClientLoginFinish(start.State, password, server.Message, clientContext ?? context);
    return (client, opaque.ServerLoginFinish(server.State, client.Finalization, context));
}

void Check(string name, Action test)
{
    try
    {
        test();
        passed++;
        Console.WriteLine($"ok    {name}");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {e.GetType().Name}: {e.Message}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Refused(OpaqueStatus status, Action call, string what)
{
    try
    {
        call();
    }
    catch (OpaqueException e) when (e.Status == status)
    {
        return;
    }

    throw new Exception($"{what}: not refused as {status}");
}

static string? FindNode()
{
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        if (File.Exists(Path.Combine(dir, "node")))
            return Path.Combine(dir, "node");
    return null;
}

/// <summary>tests/Opaque.Tests/interop.mjs in Node: a call a line each way.</summary>
internal sealed class Js : IDisposable
{
    private readonly Process _node;

    public Js(string node)
    {
        var script = FindUp(Path.Combine("tests", "Opaque.Tests", "interop.mjs"));
        _node = Process.Start(new ProcessStartInfo(node, $"\"{script}\"")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false
        })!;
    }

    public void Dispose()
    {
        _node.StandardInput.Close();
        if (!_node.WaitForExit(5000)) _node.Kill();
        _node.Dispose();
    }

    public Reply Call(string op, params object?[] args)
    {
        var line = JsonSerializer.Serialize(new
        {
            op,
            args = args.Select(a => a switch
            {
                null => null,
                string text => (object)new { text },
                byte[] bytes => new { hex = Convert.ToHexString(bytes) },
                _ => throw new ArgumentException($"an argument of type {a.GetType()}")
            })
        });
        _node.StandardInput.WriteLine(line);
        _node.StandardInput.Flush();
        var reply = _node.StandardOutput.ReadLine() ?? throw new Exception("node ended");
        return new Reply(JsonDocument.Parse(reply).RootElement.Clone());
    }

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative)))
                return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    internal sealed record Reply(JsonElement Json)
    {
        public int? Error => Json.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt32()
            : null;

        public byte[] Hex(string name)
        {
            if (Error is { } status) throw new OpaqueException((OpaqueStatus)status);
            return Convert.FromHexString(Json.GetProperty(name).GetString()!);
        }
    }
}
