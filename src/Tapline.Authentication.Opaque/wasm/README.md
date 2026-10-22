# OPAQUE — the WebAssembly module

OPAQUE ([RFC 9807](https://www.rfc-editor.org/rfc/rfc9807.html)) — a password login in which the server never learns
the password — as one WebAssembly module for the browser and for .NET: [opaque-ke](https://github.com/facebook/opaque-ke)
4.0.1, built for `wasm32-unknown-unknown` behind a plain C ABI (no wasm-bindgen), with a host for each side: JavaScript
(a page, or Node) and .NET (Wasmtime).
It is what Tapline's password sign-in runs — Tapline.Authentication.Opaque's modules and browser scripts (in the
Tapline.Wasm package), Tapline.Client.Opaque's .NET client — and this folder is its source: the password stays in the
client (a plain HTML page's script, or a .NET client), and the router relays opaque bytes.

**Disclaimer.** opaque-ke was reviewed by NCC Group in 2021, at v0.5.0 (its fixes in v1.2.0), for WhatsApp's
encrypted backups. That review does not cover opaque-ke 4.0.1, which Tapline uses, nor Tapline's own OPAQUE code,
which has not been reviewed.

## What's here

| File | |
|---|---|
| `src/lib.rs`, `Cargo.toml`, `Cargo.lock`, `build.sh` | The module's source, and its build, which writes `../wwwroot/opaque.wasm`. |
| `test.mjs` | Registration and login end to end, and every refusal: `node src/Tapline.Authentication.Opaque/wasm/test.mjs` (Node's built-ins only). |
| `..` | Tapline.Authentication.Opaque: `wwwroot/opaque.wasm` (SHA-256 below), `wwwroot/opaque.mjs` (its wrapper, for a browser or Node: `loadOpaque(bytes)`, then the eight steps below), and the modules. |
| `../../Tapline.Client.Opaque` | `OpaqueCrypto` (the .NET host, Wasmtime, the module built in), and the .NET client's sign-in. |
| `../../../tests/Opaque.Tests` | The .NET host's tests, and Node running `opaque.mjs` as the client of a server there: `dotnet run --project tests/Opaque.Tests`. |
| `../LICENSE`, `../THIRD-PARTY-NOTICES.md` | The license (Apache-2.0), and the crates built into the module. |

## The protocol, as configured

- **Cipher suite:** ristretto255 for the OPRF and for the 3DH key exchange, with SHA-512.
- **Key stretching:** Argon2id, 64 MiB, 3 passes, 4 lanes, always — registration's and login's must match. It runs on the
  client alone; the server's steps are cheap. In Node on an Apple M5 Max, a registration took about 250 ms and a login
  about 210 ms; a phone takes longer.
- **Sizes:** the server's setup 128 bytes, a password file 192, KE1 96, KE2 320, KE3 64; a session key 64.
- **Context:** a login is bound to bytes both sides supply — the connection it's for (its router, its outlet's nonce, the
  realm). A login relayed to another connection fails.
- **Passwords:** as strings, normalized (NFC) and encoded as UTF-8 by both hosts, so a password typed on any system is
  the same bytes.
- **No such user:** the server answers with a response like any other, and the login fails at the client — whether an
  account exists isn't told.

## The module's calls

Inputs are (pointer, length) pairs in the module's memory, from `opaque_alloc(len)`; `opaque_free(ptr, len)` zeroes and
releases them. Each call returns 0 (done), 1 (invalid login), 2 (invalid input) or 3 (another protocol error); its
outputs — each a little-endian u32 length and its bytes — are read through `opaque_output_ptr()` and
`opaque_output_len()`, then zeroed with `opaque_clear_output()`. Randomness is the host's: the module imports
`tapline.random(ptr, len)` (`crypto.getRandomValues`; in .NET, `RandomNumberGenerator`).

| Call | Inputs | Outputs |
|---|---|---|
| `opaque_server_setup` | — | setup (the server's secret: its OPRF seed and key pair) |
| `opaque_server_public_key` | setup | the server's public key |
| `opaque_client_registration_start` | password | request, client state |
| `opaque_server_registration_start` | setup, request, credential id | response |
| `opaque_client_registration_finish` | client state, password, response | upload, export key, server's public key |
| `opaque_server_registration_finish` | upload | password file |
| `opaque_client_login_start` | password | KE1, client state |
| `opaque_server_login_start` | setup, password file (empty: no such user), KE1, credential id, context | KE2, server state |
| `opaque_client_login_finish` | client state, password, KE2, context | KE3, session key, export key, server's public key |
| `opaque_server_login_finish` | server state, KE3, context | session key |

The credential id is the server's key for a user's password file. The password file is about as sensitive as a
password's hash; the setup is the server's secret.

## In a page

The password must stay in the page: a plain HTML form whose script runs the client's steps — never an interactive Blazor
input, whose values go to the server over its circuit. Serve the page, `opaque.mjs` and `opaque.wasm` as fingerprinted
static assets under a strict Content-Security-Policy (with Subresource Integrity where they're loaded from another
origin): a host that served other code could still read what's typed. JavaScript can't erase a string, only the bytes
copied from it. Rate limits and lockouts are the server's to keep.

## In .NET

`OpaqueCrypto.Load()` loads the module built into Tapline.Client.Opaque; its methods are the steps above, with
byte arrays in and out (`OpaqueCrypto.Utf8(text)` for a password or a credential id: normalized, UTF-8). Each call runs in a Wasmtime store
and instance of its own, so calls are thread-safe and nothing of one login is there for the next; its memory is bounded
— the server's steps at 16 MiB, the client's, which stretch the password, at 128 MiB. A refusal is an `OpaqueException`
(`InvalidLogin`, `InvalidInput` or `ProtocolError`). On macOS, Wasmtime is set to Unix signals rather than Mach ports, as
in `Tapline.Wasm`, so .NET's own faults on a thread that ran the module aren't taken for its traps.

In the tests, a registration took about 230 ms and a login about 230 ms, the client's Argon2id nearly all of it; eight
logins at once, about 380 ms. A page's client (Node, running `opaque.mjs`) registers and logs in to a server in .NET, and
the two agree a session key.

## Building it

`build.sh` builds `opaque.wasm` with Rust 1.94.0 and its `wasm32-unknown-unknown` target
(`rustup toolchain install 1.94.0 --profile minimal --target wasm32-unknown-unknown`), the dependencies locked and local
paths remapped, so the same toolchain makes the same bytes:

```
9377fe38835d6d20558b110c8f507d69346d98746dc0c92d5c92423820c79cbd  src/Tapline.Authentication.Opaque/wwwroot/opaque.wasm
```

## License

Copyright 2026 Mark Hammond. This code is licensed under the Apache License, Version 2.0 (`../LICENSE`); the crates built
into `opaque.wasm` are listed, with their licenses, in `../THIRD-PARTY-NOTICES.md`. The software is provided strictly on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
