// Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see LICENSE).
//
// OPAQUE (RFC 9807) in the browser — or Node — through opaque.wasm (built from ../wasm: opaque-ke 4, ristretto255,
// Argon2id): the
// module's plain C ABI wrapped, nothing else. Its bytes are the caller's to fetch (a fingerprinted asset, say);
// randomness is crypto.getRandomValues. Every input is copied into the module's memory, and zeroed and released when
// the call returns; outputs are copied out, and the module's copy zeroed. A string — a password, a credential id — is
// normalized (NFC) and encoded as UTF-8, as the .NET host does: the same password typed anywhere, the same bytes.

const STATUS = ['ok', 'invalid login', 'invalid input', 'protocol error'];

/** An error from the module: `status` 1 (a wrong password, or a client that couldn't prove it), 2 or 3. */
export class OpaqueError extends Error {
    constructor(status) {
        super(`opaque: ${STATUS[status] ?? `status ${status}`}`);
        this.status = status;
    }

    /** The login failed: a wrong password (the client's finish), or a client that couldn't prove it (the server's). */
    get invalidLogin() {
        return this.status === 1;
    }
}

/** The module, instantiated: `bytes` its .wasm (an ArrayBuffer, a typed array, or a Response to stream). */
export async function loadOpaque(bytes) {
    let memory;
    const imports = {
        tapline: {
            random(ptr, len) {                          // (getRandomValues takes at most 64 KiB a call)
                for (let at = 0; at < len; at += 65536)
                    crypto.getRandomValues(new Uint8Array(memory.buffer, ptr + at, Math.min(65536, len - at)));
            },
        },
    };
    const {instance} = bytes instanceof Response
        ? await WebAssembly.instantiateStreaming(bytes, imports)
        : await WebAssembly.instantiate(bytes, imports);
    const x = instance.exports;
    memory = x.memory;
    const encoder = new TextEncoder();

    // One call: its inputs (Uint8Array or string, UTF-8) copied in — (pointer, length) pairs — then released, zeroed.
    function call(name, ...inputs) {
        const held = inputs.map(v => {
            const b = typeof v === 'string' ? encoder.encode(v.normalize('NFC')) : (v ?? new Uint8Array(0));
            const p = x.opaque_alloc(b.length);
            new Uint8Array(memory.buffer, p, b.length).set(b);   // (a view made now: an allocation may have grown memory)
            if (typeof v === 'string') b.fill(0);
            return [p, b.length];
        });
        try {
            const status = x[name](...held.flat());
            if (status !== 0) throw new OpaqueError(status);
            const out = new Uint8Array(memory.buffer, x.opaque_output_ptr(), x.opaque_output_len()), parts = [];
            for (let at = 0; at < out.length;) {
                const n = new DataView(out.buffer, out.byteOffset + at, 4).getUint32(0, true);
                parts.push(out.slice(at + 4, at + 4 + n));
                at += 4 + n;
            }
            x.opaque_clear_output();
            return parts;
        } finally {
            for (const [p, n] of held) x.opaque_free(p, n);
        }
    }

    return {
        // The server's
        /** A new setup — its OPRF seed and key pair: secret, kept for every registration and login. */
        serverSetup: () => call('opaque_server_setup')[0],
        /** The setup's public key (for a client that pins it). */
        serverPublicKey: setup => call('opaque_server_public_key', setup)[0],
        /** Registration's response, for the user `credentialId` names (the server's key for their password file). */
        serverRegistrationStart: (setup, request, credentialId) =>
            call('opaque_server_registration_start', setup, request, credentialId)[0],
        /** The user's password file, from the client's upload (as sensitive as a password's hash). */
        serverRegistrationFinish: upload => call('opaque_server_registration_finish', upload)[0],
        /** Login's response (KE2) — `passwordFile` null for no such user (the login fails at the client) — and state. */
        serverLoginStart(setup, passwordFile, request, credentialId, context) {
            const [response, state] = call('opaque_server_login_start', setup, passwordFile, request, credentialId, context);
            return {response, state};
        },
        /** The session key, if the client proved the password (else an OpaqueError, invalidLogin). */
        serverLoginFinish: (state, finalization, context) =>
            call('opaque_server_login_finish', state, finalization, context)[0],

        // The client's
        /** Registration's request and the client's state. */
        clientRegistrationStart(password) {
            const [request, state] = call('opaque_client_registration_start', password);
            return {request, state};
        },
        /** The upload for the server, the export key (the client's alone), the server's public key. Argon2id runs here. */
        clientRegistrationFinish(state, password, response) {
            const [upload, exportKey, serverPublicKey] = call('opaque_client_registration_finish', state, password, response);
            return {upload, exportKey, serverPublicKey};
        },
        /** Login's request (KE1) and the client's state. */
        clientLoginStart(password) {
            const [request, state] = call('opaque_client_login_start', password);
            return {request, state};
        },
        /** The finalization (KE3) for the server, the session key, the export key, the server's public key — or an
         *  OpaqueError, invalidLogin: a wrong password. Argon2id runs here. */
        clientLoginFinish(state, password, response, context) {
            const [finalization, sessionKey, exportKey, serverPublicKey] =
                call('opaque_client_login_finish', state, password, response, context);
            return {finalization, sessionKey, exportKey, serverPublicKey};
        },
    };
}
