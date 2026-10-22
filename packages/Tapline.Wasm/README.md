# Tapline.Wasm

[Tapline](https://github.com/markhammond/tapline/blob/main/README.md)'s WebAssembly, run by Wasmtime:

- **Custom functions:** a data provider's folds (`WasmBatchFold`), finalizers (`WasmBatchFinalizer`) and stale-data functions (`WasmDegradeFunction`) as modules the router runs, each sandboxed, fuel- and memory-bounded (`WasmLimits`), and allocation-free once warm.
- **Password sign-in** by OPAQUE ([RFC 9807](https://www.rfc-editor.org/rfc/rfc9807.html)), the password never leaving the client: modules of Tapline's authentication service, and the browser's scripts (`opaque.mjs`, `tapline-opaque.mjs`), served at `_content/Tapline.Wasm/`. [opaque-ke](https://github.com/facebook/opaque-ke) 4.0.1 (ristretto255, Argon2id), the same module in the browser and on the server.

```csharp
var opaque = OpaqueCrypto.Load();              // once: compiling the module takes a moment
service.Modules.Add(new OpaqueModule(opaque, store));
service.Modules.Add(new OpaqueRegistrationModule(opaque, store));
```

> **Disclaimer:** opaque-ke was reviewed by NCC Group in 2021, at v0.5.0 (its fixes in v1.2.0), for WhatsApp's encrypted backups. That review does not cover opaque-ke 4.0.1, nor Tapline's own OPAQUE code, which has not been reviewed.

## Documentation

- [Tables, folds and ingest](https://github.com/markhammond/tapline/blob/main/docs/provider.md#reference-tables-folds-and-ingest)
- [Making custom functions safe](https://github.com/markhammond/tapline/blob/main/docs/provider.md#reference-making-custom-functions-safe)
- [Signing in](https://github.com/markhammond/tapline/blob/main/docs/provider.md#reference-signing-in)
- [The module's source](https://github.com/markhammond/tapline/tree/main/src/Tapline.Authentication.Opaque/wasm)

## License

- The folds: Eclipse Public License 2.0 ([EPL-2.0](https://www.eclipse.org/legal/epl-2.0/)), included as `LICENSE`.
- Password sign-in: Apache-2.0, included as `LICENSE-APACHE`, provided "AS IS", without warranties or conditions of any kind.
- The crates built into the module are listed in `THIRD-PARTY-NOTICES.md`.

The source code is available at [github.com/markhammond/tapline](https://github.com/markhammond/tapline).
