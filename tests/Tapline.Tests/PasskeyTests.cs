using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tapline;
using Tapline.Authentication;
using Tapline.Client;

/// <summary>Tapline.Authentication: a realm's service of modules — passkeys against a software authenticator, a sequence, the backend's say.</summary>
internal static partial class T
{
    /// <summary>
    ///     An authenticator in software: one P-256 key per credential, its counter; it answers the options a passkey module
    ///     sends, as a browser would hand them back — for a relying party and an origin (either may be wrong, on purpose).
    /// </summary>
    private sealed class SoftAuthenticator(string rpId, string origin)
    {
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public byte[] Id = RandomNumberGenerator.GetBytes(16);
        public string Origin = origin;
        public string RpId = rpId;
        public uint Count;
        public bool Verified = true;
        public byte[]? UserHandle;
        public Func<byte[], byte[]>? TamperSignature;

        public byte[] Create(ReadOnlyMemory<byte> options)
        {
            using var doc = JsonDocument.Parse(options);
            var o = doc.RootElement;
            UserHandle = Base64Url.DecodeFromChars(o.GetProperty("user").GetProperty("id").GetString());
            var p = Key.ExportParameters(false);
            var cose = Cbor(new Dictionary<object, object>
                { [1L] = 2L, [3L] = -7L, [-1L] = 1L, [-2L] = p.Q.X!, [-3L] = p.Q.Y! });
            var auth = new List<byte>(AuthData(0x41 | (Verified ? 0x04 : 0)));
            auth.AddRange(new byte[16]); // aaguid
            auth.Add((byte)(Id.Length >> 8));
            auth.Add((byte)Id.Length);
            auth.AddRange(Id);
            auth.AddRange(cose);
            var attestation = Cbor(new Dictionary<object, object>
                { ["fmt"] = "none", ["attStmt"] = new Dictionary<object, object>(), ["authData"] = auth.ToArray() });
            return Json(w =>
            {
                w.WriteString("id", Base64Url.EncodeToString(Id));
                w.WriteString("clientDataJSON", Base64Url.EncodeToString(ClientData("webauthn.create", o)));
                w.WriteString("attestationObject", Base64Url.EncodeToString(attestation));
                w.WriteStartArray("transports");
                w.WriteStringValue("internal");
                w.WriteEndArray();
            });
        }

        public byte[] Get(ReadOnlyMemory<byte> options)
        {
            using var doc = JsonDocument.Parse(options);
            var o = doc.RootElement;
            var auth = AuthData(0x01 | (Verified ? 0x04 : 0));
            var client = ClientData("webauthn.get", o);
            var signature = Key.SignData([.. auth, .. SHA256.HashData(client)], HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            if (TamperSignature is { } tamper) signature = tamper(signature);
            return Json(w =>
            {
                w.WriteString("id", Base64Url.EncodeToString(Id));
                w.WriteString("clientDataJSON", Base64Url.EncodeToString(client));
                w.WriteString("authenticatorData", Base64Url.EncodeToString(auth));
                w.WriteString("signature", Base64Url.EncodeToString(signature));
                if (UserHandle is { } h) w.WriteString("userHandle", Base64Url.EncodeToString(h));
            });
        }

        private byte[] AuthData(int flags)
        {
            var b = new byte[37];
            SHA256.HashData(Encoding.UTF8.GetBytes(RpId), b);
            b[32] = (byte)flags;
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(33), ++Count);
            return b;
        }

        private byte[] ClientData(string type, JsonElement options)
        {
            return Json(w =>
            {
                w.WriteString("type", type);
                w.WriteString("challenge", options.GetProperty("challenge").GetString());
                w.WriteString("origin", Origin);
                w.WriteBoolean("crossOrigin", false);
            });
        }

        private static byte[] Json(Action<Utf8JsonWriter> write)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                write(w);
                w.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }
    }

    /// <summary>CBOR as an authenticator writes it (definite lengths): integers, byte and text strings, maps.</summary>
    private static byte[] Cbor(object item)
    {
        var o = new List<byte>();

        void Head(int major, ulong n)
        {
            if (n < 24) o.Add((byte)(major << 5 | (int)n));
            else if (n <= 0xFF) o.AddRange([(byte)(major << 5 | 24), (byte)n]);
            else if (n <= 0xFFFF) o.AddRange([(byte)(major << 5 | 25), (byte)(n >> 8), (byte)n]);
            else
            {
                o.Add((byte)(major << 5 | 26));
                o.AddRange(BitConverter.GetBytes((uint)n).Reverse());
            }
        }

        void Write(object x)
        {
            switch (x)
            {
                case long n when n >= 0: Head(0, (ulong)n); break;
                case long n: Head(1, (ulong)(-1 - n)); break;
                case byte[] b:
                    Head(2, (ulong)b.Length);
                    o.AddRange(b);
                    break;
                case string s:
                    var u = Encoding.UTF8.GetBytes(s);
                    Head(3, (ulong)u.Length);
                    o.AddRange(u);
                    break;
                case Dictionary<object, object> m:
                    Head(5, (ulong)m.Count);
                    foreach (var (k, v) in m)
                    {
                        Write(k);
                        Write(v);
                    }

                    break;
                default: throw new ArgumentException(x.GetType().Name);
            }
        }

        Write(item);
        return [.. o];
    }

    /// <summary>A second factor for the tests: a code per user, checked against the user the passkey proved.</summary>
    private sealed class CodeModule(Dictionary<string, string> codes) : IAuthModule
    {
        public string Name => "code";

        public ValueTask<ModuleAnswer> StepAsync(ModuleStep step, CancellationToken cancellationToken)
        {
            var user = step.Previous?.UserId;
            return new ValueTask<ModuleAnswer>(user is not null && codes.TryGetValue(user, out var code) &&
                                               Encoding.UTF8.GetString(step.Payload.Span) == code
                ? ModuleAnswer.Prove(new AuthenticatedUser(user, Name,
                    Claims: new Dictionary<string, string> { ["mfa"] = "code" }))
                : ModuleAnswer.Refuse("Wrong code.", user));
        }
    }

    public static async Task Passkeys()
    {
        const string rp = "app.test", origin = "https://app.test";
        var registry = new OutletRegistry();
        var publisher = new Publisher(registry);
        var s = NewOutlet(registry);
        var owner = new object();
        s.Attach(owner);
        var loop = new MovableConnection(s);
        var client = new TaplineClient(loop);
        client.Bind(s.Id, s.Nonce);
        var options = new PasskeyOptions { RelyingPartyId = rp, Origins = [origin] };
        var store = new InMemoryPasskeyStore();
        var decided = new List<string>();
        var service = new AuthenticationService("north")
        {
            Modules =
            {
                new PasskeyModule(options, store),
                new PasskeyRegistrationModule(options, store)
                {
                    Enroll = (token, _, _) => new ValueTask<string?>(Encoding.UTF8.GetString(token.Span) switch
                    {
                        "invite-alice" => "alice", "invite-mallory" => "mallory", _ => null
                    })
                },
                AuthModules.Sequence("passkey+code", new PasskeyModule(options, store),
                    new CodeModule(new Dictionary<string, string> { ["alice"] = "246810" }))
            },
            OnAuthenticated = (user, step, _) =>
            {
                lock (decided) decided.Add($"{user.UserId} {user.Module} {step.Module}");
                if (user.UserId == "mallory") throw new SignInRefusedException("That account is disabled.");
                var claims = new Dictionary<string, string>(user.Claims ?? new Dictionary<string, string>())
                    { ["role"] = "gp" };
                return new ValueTask<AuthenticatedUser?>(user with { Claims = claims });
            }
        };
        publisher.ProvideAuthentication(service);
        (service.MaxFailuresPerConnection, service.MaxFailuresPerUser) = (100, 100); // (counted at the end)
        var alice = new SoftAuthenticator(rp, origin);

        Task<SignInResult> SignIn(string module, string first, Func<ReadOnlyMemory<byte>, byte[]> answer,
            bool renewal = false) => Pumped(client.SignInAsync("north", module, Encoding.UTF8.GetBytes(first),
            (m, _) => new ValueTask<ReadOnlyMemory<byte>>(answer(m)), renewal), loop, owner, client);

        // Registering: not for the anonymous without a token; with alice's invitation, her passkey — and signed in by it.
        var anonymous = await SignIn("passkey-register", "", alice.Create);
        Assert(anonymous is { Accepted: false, Reason: "Sign in first to add a passkey." }, $"no token ({anonymous.Reason})");
        string? asked = null;
        var registered = await SignIn("passkey-register", "invite-alice", m =>
        {
            asked = Encoding.UTF8.GetString(m.Span);
            return alice.Create(m);
        });
        Assert(registered is { Accepted: true, Identity: { UserId: "alice", Module: "passkey", Insecure: false } } &&
               registered.Identity.Claims?["role"] == "gp",
            $"registered by an invitation: signed in as alice, by passkey, secure, the backend's claims ({registered.Reason})");
        using (var doc = JsonDocument.Parse(asked!))
        {
            var o = doc.RootElement;
            Assert(o.GetProperty("rp").GetProperty("id").GetString() == rp && o.GetProperty("attestation").GetString() == "none" &&
                   o.GetProperty("user").GetProperty("name").GetString() == "alice" &&
                   o.GetProperty("pubKeyCredParams").GetArrayLength() == 2 &&
                   Base64Url.DecodeFromChars(o.GetProperty("challenge").GetString()).Length == 32,
                $"the creation options, for navigator.credentials.create: {asked}");
        }

        var kept = (await store.ListAsync("alice", default)).Single();
        Assert(kept.Id.AsSpan().SequenceEqual(alice.Id) && kept.Transports?.Single() == "internal",
            "the passkey stored: its id, its transports");

        // Signing in: hers by name (allowCredentials), or any the browser offers (discoverable); the counter kept.
        var byName = await SignIn("passkey", "{\"userId\":\"alice\"}", m =>
        {
            asked = Encoding.UTF8.GetString(m.Span);
            return alice.Get(m);
        });
        Assert(byName is { Accepted: true, Identity.UserId: "alice" } && asked!.Contains(Base64Url.EncodeToString(alice.Id)),
            $"signed in by her passkey, offered by name ({byName.Reason})");
        var discoverable = await SignIn("passkey", "", alice.Get);
        Assert(discoverable.Accepted && (await store.FindAsync(alice.Id, default))!.SignCount == alice.Count,
            "a discoverable sign-in; the counter kept");

        // What's refused: another origin, another site's passkey, a tampered signature, a counter gone back, an answer
        // to another sign-in, the user unverified where required, a passkey unknown.
        async Task<string?> RefusedWith(SoftAuthenticator who)
        {
            var r = await SignIn("passkey", "", who.Get);
            return r.Accepted ? null : r.Reason;
        }

        alice.Origin = "https://evil.test";
        Assert((await RefusedWith(alice))?.Contains("page not of this site") == true, "another origin");
        alice.Origin = origin;
        alice.RpId = "evil.test";
        Assert((await RefusedWith(alice))?.Contains("belongs to another site") == true, "another relying party");
        alice.RpId = rp;
        alice.TamperSignature = sig =>
        {
            sig[^1] ^= 1;
            return sig;
        };
        Assert((await RefusedWith(alice))?.Contains("doesn't check out") == true, "a tampered signature");
        alice.TamperSignature = null;
        Assert(await RefusedWith(alice) is null, "…fine again");

        var s2 = NewOutlet(registry); // a fresh connection: the failures above counted against the first
        var owner2 = new object();
        s2.Attach(owner2);
        loop.Outlet = s2;
        owner = owner2;
        s = s2;
        client.Bind(s2.Id, s2.Nonce);
        for (var i = 0; i < 100 && client.Identity is null; i++)
        {
            Pump(s2, owner2, client);
            await Task.Delay(5);
        }

        alice.Count -= 2;
        Assert((await RefusedWith(alice))?.Contains("counter went back") == true, "a counter gone back");
        alice.Count += 5;
        byte[]? stale = null;
        await SignIn("passkey", "", m => stale = alice.Get(m)); // (accepted: its answer kept, to replay)
        Assert((await SignIn("passkey", "", _ => stale!)).Reason?.Contains("answers another sign-in") == true,
            "an answer to another sign-in (its challenge)");
        Assert(!Challenges.Check(new byte[32], new byte[32], "tapline/1 north 1 2"u8), "a challenge bound to its connection");
        var bob = new SoftAuthenticator(rp, origin);
        Assert((await RefusedWith(bob))?.Contains("isn't known here") == true, "a passkey unknown");

        // A sequence: the passkey, then the code — both for the same user; the module named for both, their claims.
        var codeAsked = 0;
        var twoFactor = await Pumped(client.SignInAsync("north", "passkey+code", ReadOnlyMemory<byte>.Empty, (m, _) =>
        {
            var stage = m.Span[0];
            if (stage == 0) return new ValueTask<ReadOnlyMemory<byte>>(alice.Get(m[1..]));
            codeAsked++;
            return new ValueTask<ReadOnlyMemory<byte>>("246810"u8.ToArray());
        }), loop, owner, client);
        Assert(twoFactor is { Accepted: true, Identity: { UserId: "alice", Module: "passkey+code" } } &&
               twoFactor.Identity.Claims?["mfa"] == "code" && twoFactor.Identity.Claims["role"] == "gp" && codeAsked == 1,
            $"two factors in sequence: passkey then code ({twoFactor.Reason})");
        var wrongCode = await Pumped(client.SignInAsync("north", "passkey+code", ReadOnlyMemory<byte>.Empty, (m, _) =>
            new ValueTask<ReadOnlyMemory<byte>>(m.Span[0] == 0 ? alice.Get(m[1..]) : "000000"u8.ToArray())), loop, owner,
            client);
        Assert(wrongCode is { Accepted: false, Reason: "Wrong code." }, "a wrong code: refused");

        // Renewal: the session's policy (reauthenticate by default) wants a fresh proof — a ticket won't do, a passkey will.
        var session = client.Identity!.SessionId;
        var byTicket = await Pumped(client.ResumeAsync(true), loop, owner, client);
        Assert(byTicket is { Accepted: false, Reason: "Prove who you are again to renew." }, $"no silent renewal ({byTicket.Reason})");
        var renewed = await SignIn("passkey", "", alice.Get, renewal: true);
        Assert(renewed.Accepted && renewed.Identity!.SessionId == session, "renewed by a fresh proof: the same session");

        // Resumed by its ticket on another session: the backend asked again (its claims back), the same sign-in.
        var s3 = NewOutlet(registry);
        var owner3 = new object();
        s3.Attach(owner3);
        loop.Outlet = s3;
        client.Bind(s3.Id, s3.Nonce);
        for (var i = 0; i < 400 && s3.Identity is null; i++)
        {
            Pump(s3, owner3, client);
            await Task.Delay(5);
        }

        Assert(s3.Identity is { UserId: "alice", Module: "passkey" } r3 && r3.SessionId == session &&
               r3.Claims?["role"] == "gp" && decided.Last() == "alice passkey ticket",
            $"resumed by its ticket: the backend asked again, its claims; the same session ({s3.Identity})");
        (s, owner) = (s3, owner3);

        // The backend refuses: mallory registers (anonymous, by her invitation), and is told why. (Signed in, a
        // registration adds a passkey for whoever is: alice's connection would add one for her.)
        var m = NewOutlet(registry);
        var mOwner = new object();
        m.Attach(mOwner);
        var mLoop = new MovableConnection(m);
        var mClient = new TaplineClient(mLoop);
        mClient.Bind(m.Id, m.Nonce);
        var mallory = new SoftAuthenticator(rp, origin);
        var refused = await Pumped(mClient.SignInAsync("north", "passkey-register", "invite-mallory"u8.ToArray(),
            (x, _) => new ValueTask<ReadOnlyMemory<byte>>(mallory.Create(x))), mLoop, mOwner, mClient);
        Assert(refused is { Accepted: false, Reason: "That account is disabled." } && m.Identity is null,
            $"the backend's refusal ({refused.Reason})");

        // User verification required: an unverified answer refused.
        var strict = new PasskeyOptions { RelyingPartyId = rp, Origins = [origin], UserVerification = UserVerification.Required };
        var west = new AuthenticationService("west") { Modules = { new PasskeyModule(strict, store) } };
        publisher.ProvideAuthentication(west);
        alice.Verified = false;
        var unverified = await Pumped(client.SignInAsync("west", "passkey", ReadOnlyMemory<byte>.Empty,
            (m, _) => new ValueTask<ReadOnlyMemory<byte>>(alice.Get(m))), loop, owner, client);
        Assert(unverified is { Accepted: false, Reason: "The user wasn't verified." }, $"unverified ({unverified.Reason})");
        alice.Verified = true;

        // Failures counted: too many from a connection, and it waits.
        service.MaxFailuresPerConnection = 2;
        await SignIn("passkey", "", bob.Get);
        await SignIn("passkey", "", bob.Get);
        var waits = await SignIn("passkey", "", alice.Get);
        Assert(waits is { Accepted: false, Reason: "Too many failed sign-ins: try again later." },
            $"too many failures: it waits ({waits.Reason})");
    }
}
