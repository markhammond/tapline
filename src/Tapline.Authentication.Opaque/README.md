# Tapline.Authentication.Opaque

Password sign-in for [Tapline](https://github.com/markhammond/tapline/blob/main/README.md) by OPAQUE ([RFC 9807](https://www.rfc-editor.org/rfc/rfc9807.html)): a
module of [Tapline.Authentication](https://github.com/markhammond/tapline/blob/main/src/Tapline.Authentication) in which the password never leaves the client.
[opaque-ke](https://github.com/facebook/opaque-ke) 4.0.1 (ristretto255, Argon2id) as one WebAssembly module, run by
Wasmtime on the server and in a headless client, and in the browser (`opaque.mjs`, `tapline-opaque.mjs`).

```csharp
service.Modules.Add(new OpaqueModule(opaque, store));
service.Modules.Add(new OpaqueRegistrationModule(opaque, store));
```

> **Disclaimer:** opaque-ke was reviewed by NCC Group in 2021, at v0.5.0 (its fixes in v1.2.0), for WhatsApp's
> encrypted backups. That review does not cover opaque-ke 4.0.1, nor this package's own code, which has not been
> reviewed.

Documentation: [the reference](https://github.com/markhammond/tapline/blob/main/docs/provider.md#reference-signing-in); the module's source,
[wasm](https://github.com/markhammond/tapline/tree/main/src/Tapline.Authentication.Opaque/wasm). License: Apache-2.0, provided "AS IS", without warranties or conditions of any
kind; the crates built into the module are listed in `THIRD-PARTY-NOTICES.md`.
