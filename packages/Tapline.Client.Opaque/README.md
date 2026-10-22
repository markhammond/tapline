# Tapline.Client.Opaque

A .NET client's password sign-in to [Tapline](https://github.com/markhammond/tapline/blob/main/README.md) by OPAQUE ([RFC 9807](https://www.rfc-editor.org/rfc/rfc9807.html)): the password stretched here (Argon2id), never sent, the router relaying KE1, KE2 and KE3 to the realm's OPAQUE module ([Tapline.Wasm](https://www.nuget.org/packages/Tapline.Wasm)). [opaque-ke](https://github.com/facebook/opaque-ke) 4.0.1 (ristretto255, Argon2id) as WebAssembly, run by Wasmtime: the same module the browser runs.

```csharp
var opaque = OpaqueCrypto.Load();              // once: compiling the module takes a moment
var result = await client.SignInWithPasswordAsync(opaque, "north", "alice", password);
```

> **Disclaimer:** opaque-ke was reviewed by NCC Group in 2021, at v0.5.0 (its fixes in v1.2.0), for WhatsApp's encrypted backups. That review does not cover opaque-ke 4.0.1, nor this package's own code, which has not been reviewed.

## Documentation

- [The reference](https://github.com/markhammond/tapline/blob/main/docs/provider.md#reference-signing-in)
- [The module's source](https://github.com/markhammond/tapline/tree/main/src/Tapline.Authentication.Opaque/wasm)

## License

Apache-2.0, provided "AS IS", without warranties or conditions of any kind.
The crates built into the module are listed in `THIRD-PARTY-NOTICES.md`.
