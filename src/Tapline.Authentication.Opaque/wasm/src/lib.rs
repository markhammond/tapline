// Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see ../../LICENSE).
//
//! OPAQUE (RFC 9807) for Tapline's samples: opaque-ke 4 with ristretto255 (its OPRF and its 3DH key exchange, SHA-512)
//! and Argon2id as the key stretching function, behind a plain C ABI — one WebAssembly module for the browser and for
//! .NET (Wasmtime), no wasm-bindgen.
//!
//! **Calls.** Each operation takes its inputs as (pointer, length) pairs in the module's memory — allocated with
//! [`opaque_alloc`], released with [`opaque_free`] (which zeroes them first: a password, a state) — and returns a
//! status: 0 done, [`INVALID_LOGIN`], [`INVALID_INPUT`] or [`PROTOCOL_ERROR`]. Its outputs are left in one buffer,
//! read through [`opaque_output_ptr`] and [`opaque_output_len`] until the next call: each output a little-endian u32
//! length and its bytes, in the order its function's documentation gives. The buffer is zeroed before it's reused.
//!
//! **Randomness** is the host's: the module imports `tapline.random(ptr, len)`, to fill `len` bytes at `ptr` from a
//! cryptographically secure generator (`crypto.getRandomValues` in a browser, `RandomNumberGenerator` in .NET).
//!
//! **Argon2id** runs on the client alone (registration's and login's finish), as OPAQUE has it: 64 MiB, 3 passes, 4
//! lanes — what a phone can afford. Registration and login must use the same settings: these, always.
//!
//! **Context** binds a login to where it happens — Tapline's connection, say (its router, its outlet's nonce, the
//! realm): the client's and the server's must be the same bytes, or the login fails.

use std::cell::RefCell;

use argon2::{Algorithm, Argon2, Params, Version};
use opaque_ke::ciphersuite::CipherSuite;
use opaque_ke::errors::ProtocolError;
use opaque_ke::rand::{CryptoRng, RngCore};
use opaque_ke::{
    ClientLogin, ClientLoginFinishParameters, ClientRegistration, ClientRegistrationFinishParameters,
    CredentialFinalization, CredentialRequest, CredentialResponse, Identifiers, RegistrationRequest,
    RegistrationResponse, RegistrationUpload, ServerLogin, ServerLoginParameters, ServerRegistration, ServerSetup,
};
use zeroize::Zeroize;

/// The cipher suite: ristretto255 for the OPRF and the key exchange (3DH, SHA-512); Argon2id to stretch the password.
struct Suite;

impl CipherSuite for Suite {
    type OprfCs = opaque_ke::Ristretto255;
    type KeyExchange = opaque_ke::TripleDh<opaque_ke::Ristretto255, sha2::Sha512>;
    type Ksf = Argon2<'static>;
}

/// Done; its outputs are in the output buffer.
pub const OK: i32 = 0;
/// The login failed: a wrong password (the client's finish), or a client that couldn't prove it (the server's).
pub const INVALID_LOGIN: i32 = 1;
/// An input that isn't what it should be: a message, a state, a setup or a password file that doesn't deserialize.
pub const INVALID_INPUT: i32 = 2;
/// Anything else the protocol refused.
pub const PROTOCOL_ERROR: i32 = 3;

/// Argon2id as the samples use it: 64 MiB (in KiB), 3 passes, 4 lanes (run one after another here).
fn ksf() -> Argon2<'static> {
    Argon2::new(Algorithm::Argon2id, Version::V0x13, Params::new(1 << 16, 3, 4, None).expect("valid parameters"))
}

#[link(wasm_import_module = "tapline")]
extern "C" {
    /// Fills `len` bytes at `ptr` from the host's cryptographically secure generator.
    fn random(ptr: *mut u8, len: usize);
}

/// The host's generator, as the protocol asks for one.
struct HostRng;

impl RngCore for HostRng {
    fn next_u32(&mut self) -> u32 {
        let mut b = [0u8; 4];
        self.fill_bytes(&mut b);
        u32::from_le_bytes(b)
    }

    fn next_u64(&mut self) -> u64 {
        let mut b = [0u8; 8];
        self.fill_bytes(&mut b);
        u64::from_le_bytes(b)
    }

    fn fill_bytes(&mut self, dest: &mut [u8]) {
        if !dest.is_empty() {
            unsafe { random(dest.as_mut_ptr(), dest.len()) }
        }
    }

    fn try_fill_bytes(&mut self, dest: &mut [u8]) -> Result<(), opaque_ke::rand::Error> {
        self.fill_bytes(dest);
        Ok(())
    }
}

impl CryptoRng for HostRng {}

thread_local! {
    static OUTPUT: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };
}

/// The last call's outputs: where they start.
#[no_mangle]
pub extern "C" fn opaque_output_ptr() -> *const u8 {
    OUTPUT.with(|o| o.borrow().as_ptr())
}

/// The last call's outputs: how many bytes.
#[no_mangle]
pub extern "C" fn opaque_output_len() -> usize {
    OUTPUT.with(|o| o.borrow().len())
}

/// `len` bytes for an input (its contents undefined).
#[no_mangle]
pub extern "C" fn opaque_alloc(len: usize) -> *mut u8 {
    let mut v = Vec::<u8>::with_capacity(len.max(1));
    let p = v.as_mut_ptr();
    std::mem::forget(v);
    p
}

/// Zeroes and releases what [`opaque_alloc`] gave (`len` as asked for).
///
/// # Safety
/// `ptr` and `len` must be exactly what one call of [`opaque_alloc`] gave and was asked for, released once.
#[no_mangle]
pub unsafe extern "C" fn opaque_free(ptr: *mut u8, len: usize) {
    let mut v = Vec::from_raw_parts(ptr, len.max(1), len.max(1));
    v.zeroize();
}

/// Zeroes the output buffer (once its outputs have been read: a session key, a state).
#[no_mangle]
pub extern "C" fn opaque_clear_output() {
    OUTPUT.with(|o| o.borrow_mut().zeroize());
}

unsafe fn input<'a>(ptr: *const u8, len: usize) -> &'a [u8] {
    if len == 0 { &[] } else { std::slice::from_raw_parts(ptr, len) }
}

/// The outputs of a call that succeeded, in order; or its status.
fn finish(result: Result<Vec<&[u8]>, i32>) -> i32 {
    match result {
        Err(status) => status,
        Ok(parts) => OUTPUT.with(|o| {
            let mut out = o.borrow_mut();
            out.zeroize();
            out.clear();
            for p in parts {
                out.extend_from_slice(&(p.len() as u32).to_le_bytes());
                out.extend_from_slice(p);
            }
            OK
        }),
    }
}

fn status<E>(e: ProtocolError<E>) -> i32 {
    match e {
        ProtocolError::InvalidLoginError => INVALID_LOGIN,
        ProtocolError::SerializationError | ProtocolError::SizeError { .. } => INVALID_INPUT,
        _ => PROTOCOL_ERROR,
    }
}

fn context(c: &[u8]) -> Option<&[u8]> {
    if c.is_empty() { None } else { Some(c) }
}

// ───────────────────────────── the server's setup ─────────────────────────────

/// A new server setup — its OPRF seed and its long-term key pair: kept by the server, secret, for every registration
/// and login. Outputs: the setup.
#[no_mangle]
pub extern "C" fn opaque_server_setup() -> i32 {
    let setup = ServerSetup::<Suite>::new(&mut HostRng);
    finish(Ok(vec![&setup.serialize()]))
}

/// The server's public key, from its setup (for a client that pins it). Outputs: the key.
///
/// # Safety
/// The input must be `len` readable bytes of the module's memory.
#[no_mangle]
pub unsafe extern "C" fn opaque_server_public_key(setup: *const u8, setup_len: usize) -> i32 {
    let run = || -> Result<Vec<Vec<u8>>, i32> {
        let setup = ServerSetup::<Suite>::deserialize(input(setup, setup_len)).map_err(status)?;
        Ok(vec![setup.keypair().public().serialize().to_vec()])
    };
    write(run())
}

// ───────────────────────────── registration ─────────────────────────────

/// The client's first step: a registration request for `password`. Outputs: the request (to the server), the client's
/// state (kept for its finish).
///
/// # Safety
/// The input must be `password_len` readable bytes of the module's memory.
#[no_mangle]
pub unsafe extern "C" fn opaque_client_registration_start(password: *const u8, password_len: usize) -> i32 {
    match ClientRegistration::<Suite>::start(&mut HostRng, input(password, password_len)) {
        Err(e) => status(e),
        Ok(r) => finish(Ok(vec![&r.message.serialize(), &r.state.serialize()])),
    }
}

/// The server's step: its response to a registration request, for the user it names (`credential_id`: the server's
/// key for the user's password file). Outputs: the response (to the client).
///
/// # Safety
/// The inputs must be readable bytes of the module's memory, their lengths as given.
#[no_mangle]
pub unsafe extern "C" fn opaque_server_registration_start(
    setup: *const u8,
    setup_len: usize,
    request: *const u8,
    request_len: usize,
    credential_id: *const u8,
    credential_id_len: usize,
) -> i32 {
    let run = || -> Result<Vec<Vec<u8>>, i32> {
        let setup = ServerSetup::<Suite>::deserialize(input(setup, setup_len)).map_err(status)?;
        let request = RegistrationRequest::<Suite>::deserialize(input(request, request_len)).map_err(status)?;
        let r = ServerRegistration::<Suite>::start(&setup, request, input(credential_id, credential_id_len))
            .map_err(status)?;
        Ok(vec![r.message.serialize().to_vec()])
    };
    write(run())
}

/// The client's finish: its record for the server, from its state, `password` again and the server's response — the
/// password stretched (Argon2id) here. Outputs: the upload (to the server), the export key (the client's alone: a key
/// for its own data, if it wants one), the server's public key.
///
/// # Safety
/// The inputs must be readable bytes of the module's memory, their lengths as given.
#[no_mangle]
pub unsafe extern "C" fn opaque_client_registration_finish(
    state: *const u8,
    state_len: usize,
    password: *const u8,
    password_len: usize,
    response: *const u8,
    response_len: usize,
) -> i32 {
    let run = || -> Result<Vec<Vec<u8>>, i32> {
        let state = ClientRegistration::<Suite>::deserialize(input(state, state_len)).map_err(status)?;
        let response = RegistrationResponse::<Suite>::deserialize(input(response, response_len)).map_err(status)?;
        let ksf = ksf();
        let r = state
            .finish(
                &mut HostRng,
                input(password, password_len),
                response,
                ClientRegistrationFinishParameters::new(Identifiers::default(), Some(&ksf)),
            )
            .map_err(status)?;
        Ok(vec![r.message.serialize().to_vec(), r.export_key.to_vec(), r.server_s_pk.serialize().to_vec()])
    };
    write(run())
}

/// The server's finish: the user's password file, from the client's upload — what the server keeps for them (about
/// as sensitive as a password's hash). Outputs: the password file.
///
/// # Safety
/// The input must be `upload_len` readable bytes of the module's memory.
#[no_mangle]
pub unsafe extern "C" fn opaque_server_registration_finish(upload: *const u8, upload_len: usize) -> i32 {
    let run = || -> Result<Vec<Vec<u8>>, i32> {
        let upload = RegistrationUpload::<Suite>::deserialize(input(upload, upload_len)).map_err(status)?;
        Ok(vec![ServerRegistration::finish(upload).serialize().to_vec()])
    };
    write(run())
}

// ───────────────────────────── login ─────────────────────────────

/// The client's first step: a credential request (KE1) for `password`. Outputs: the request (to the server), the
/// client's state (kept for its finish).
///
/// # Safety
/// The input must be `password_len` readable bytes of the module's memory.
#[no_mangle]
pub unsafe extern "C" fn opaque_client_login_start(password: *const u8, password_len: usize) -> i32 {
    match ClientLogin::<Suite>::start(&mut HostRng, input(password, password_len)) {
        Err(e) => status(e),
        Ok(r) => finish(Ok(vec![&r.message.serialize(), &r.state.serialize()])),
    }
}

/// The server's step: its credential response (KE2) for the user `credential_id` names — with their password file, or
/// none (`password_file_len` 0: no such user; the response looks the same, and the login fails at the client), bound
/// to `context`. Outputs: the response (to the client), the server's state (kept for its finish).
///
/// # Safety
/// The inputs must be readable bytes of the module's memory, their lengths as given.
#[no_mangle]
pub unsafe extern "C" fn opaque_server_login_start(
    setup: *const u8,
    setup_len: usize,
    password_file: *const u8,
    password_file_len: usize,
    request: *const u8,
    request_len: usize,
    credential_id: *const u8,
    credential_id_len: usize,
    context_ptr: *const u8,
    context_len: usize,
) -> i32 {
    let run = || -> Result<Vec<Vec<u8>>, i32> {
        let setup = ServerSetup::<Suite>::deserialize(input(setup, setup_len)).map_err(status)?;
        let file = match password_file_len {
            0 => None,
            n => Some(ServerRegistration::<Suite>::deserialize(input(password_file, n)).map_err(status)?),
        };
        let request = CredentialRequest::<Suite>::deserialize(input(request, request_len)).map_err(status)?;
        let r = ServerLogin::start(
            &mut HostRng,
            &setup,
            file,
            request,
            input(credential_id, credential_id_len),
            ServerLoginParameters { context: context(input(context_ptr, context_len)), identifiers: Identifiers::default() },
        )
        .map_err(status)?;
        Ok(vec![r.message.serialize().to_vec(), r.state.serialize().to_vec()])
    };
    write(run())
}

/// The client's finish: its credential finalization (KE3), from its state, `password` again and the server's response,
/// bound to `context` — the password stretched (Argon2id) here. [`INVALID_LOGIN`]: a wrong password (or a server that
/// isn't the one registered with). Outputs: the finalization (to the server), the session key, the export key, the
/// server's public key.
///
/// # Safety
/// The inputs must be readable bytes of the module's memory, their lengths as given.
#[no_mangle]
pub unsafe extern "C" fn opaque_client_login_finish(
    state: *const u8,
    state_len: usize,
    password: *const u8,
    password_len: usize,
    response: *const u8,
    response_len: usize,
    context_ptr: *const u8,
    context_len: usize,
) -> i32 {
    let run = || -> Result<Vec<Vec<u8>>, i32> {
        let state = ClientLogin::<Suite>::deserialize(input(state, state_len)).map_err(status)?;
        let response = CredentialResponse::<Suite>::deserialize(input(response, response_len)).map_err(status)?;
        let ksf = ksf();
        let r = state
            .finish(
                &mut HostRng,
                input(password, password_len),
                response,
                ClientLoginFinishParameters::new(context(input(context_ptr, context_len)), Identifiers::default(), Some(&ksf)),
            )
            .map_err(status)?;
        Ok(vec![
            r.message.serialize().to_vec(),
            r.session_key.to_vec(),
            r.export_key.to_vec(),
            r.server_s_pk.serialize().to_vec(),
        ])
    };
    write(run())
}

/// The server's finish: the client proved it knows the password (else [`INVALID_LOGIN`]), bound to the same `context`.
/// Outputs: the session key — the client's, the same.
///
/// # Safety
/// The inputs must be readable bytes of the module's memory, their lengths as given.
#[no_mangle]
pub unsafe extern "C" fn opaque_server_login_finish(
    state: *const u8,
    state_len: usize,
    finalization: *const u8,
    finalization_len: usize,
    context_ptr: *const u8,
    context_len: usize,
) -> i32 {
    let run = || -> Result<Vec<Vec<u8>>, i32> {
        let state = ServerLogin::<Suite>::deserialize(input(state, state_len)).map_err(status)?;
        let finalization =
            CredentialFinalization::<Suite>::deserialize(input(finalization, finalization_len)).map_err(status)?;
        let r = state
            .finish(
                finalization,
                ServerLoginParameters { context: context(input(context_ptr, context_len)), identifiers: Identifiers::default() },
            )
            .map_err(status)?;
        Ok(vec![r.session_key.to_vec()])
    };
    write(run())
}

/// Writes owned outputs (zeroing the copies made on the way).
fn write(result: Result<Vec<Vec<u8>>, i32>) -> i32 {
    match result {
        Err(s) => s,
        Ok(mut owned) => {
            let s = finish(Ok(owned.iter().map(|v| v.as_slice()).collect()));
            for v in owned.iter_mut() {
                v.zeroize();
            }
            s
        }
    }
}
