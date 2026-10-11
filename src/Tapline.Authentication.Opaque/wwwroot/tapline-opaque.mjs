// Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see LICENSE).
//
// Password sign-in by OPAQUE for tapline.js (Tapline.Authentication.Opaque's modules `opaque` and `opaque-register`):
// the password stays in this page — stretched here (Argon2id), never sent; the router relays KE1, KE2 and KE3. Served
// with the package, beside opaque.mjs and opaque.wasm (_content/Tapline.Authentication.Opaque/). Keep the password in a
// plain form whose script calls these — never an interactive Blazor input, whose values go to the server.
//
//   import {signInWithPassword, registerPassword} from './_content/Tapline.Authentication.Opaque/tapline-opaque.mjs';
//   const result = await signInWithPassword(tapline, 'north', userId, password);   // { accepted, identity, reason, … }

import {loadOpaque} from './opaque.mjs';

let loaded = null;

/** The module, fetched and compiled once (opaque.wasm beside this script, or `url`). */
export function opaque(url = new URL('./opaque.wasm', import.meta.url)) {
    return loaded ??= fetch(url).then(response => loadOpaque(response));
}

const encoder = new TextEncoder();

/** `u16 length | first | rest` (little-endian length): an id or a token, then the protocol's message. */
function framed(first, rest) {
    const b = new Uint8Array(2 + first.length + rest.length);
    new DataView(b.buffer).setUint16(0, first.length, true);
    b.set(first, 2);
    b.set(rest, 2 + first.length);
    return b;
}

/**
 * Signs in to `realm` as `userId` with `password`, by OPAQUE (module `opaque`); resolves as tapline.signIn does. A wrong
 * password — or no such user — resolves refused: `Wrong user id or password.`
 */
export async function signInWithPassword(tapline, realm, userId, password, {renewal = false, signal = null, module = null} = {}) {
    const o = module ?? await opaque();
    const context = tapline.signInContext(realm);
    if (!context) throw new Error('tapline: not bound to a router yet');
    const start = o.clientLoginStart(password);
    try {
        return await tapline.signIn(realm, 'opaque', framed(encoder.encode(userId.normalize('NFC')), start.request), {
            renewal, signal, next: async ke2 => o.clientLoginFinish(start.state, password, ke2, context).finalization,
        });
    } catch (e) {
        if (e?.invalidLogin) return {accepted: false, identity: null, ticket: null, reason: 'Wrong user id or password.'};
        throw e;
    } finally {
        start.state.fill(0);
    }
}

/**
 * Sets `password` in `realm` (module `opaque-register`) — for the user signed in, or for whoever `enrollment`'s token
 * is for — and signs in by it; resolves as tapline.signIn does.
 */
export async function registerPassword(tapline, realm, password, {enrollment = null, signal = null, module = null} = {}) {
    const o = module ?? await opaque();
    const token = typeof enrollment === 'string' ? encoder.encode(enrollment) : enrollment ?? new Uint8Array(0);
    const start = o.clientRegistrationStart(password);
    try {
        return await tapline.signIn(realm, 'opaque-register', framed(token, start.request), {
            signal, next: async message => {
                const n = new DataView(message.buffer, message.byteOffset, 2).getUint16(0, true);
                return o.clientRegistrationFinish(start.state, password, message.subarray(2 + n)).upload;
            },
        });
    } finally {
        start.state.fill(0);
    }
}
