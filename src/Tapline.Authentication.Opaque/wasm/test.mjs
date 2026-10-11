// Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see LICENSE).
//
// opaque.wasm end to end, in Node (built-ins only): `node src/Tapline.Authentication.Opaque/wasm/test.mjs`. Registration then login; a wrong
// password, no such user, another user's file, a login bound to another context, a tampered finalization, and input
// that isn't what it says are each refused.

import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {loadOpaque} from '../wwwroot/opaque.mjs';

const opaque = await loadOpaque(await readFile(new URL('../wwwroot/opaque.wasm', import.meta.url)));
const equal = (a, b) => Buffer.from(a).equals(Buffer.from(b));
const refused = (status, run, what) => assert.throws(run, e => e.status === status, what);

const setup = opaque.serverSetup();
const serverKey = opaque.serverPublicKey(setup);
const context = new TextEncoder().encode('router 7f3a · outlet nonce 1234 · realm north');

// Registration: the password stays on the client; the server keeps a file.
let began = performance.now();
const reg = opaque.clientRegistrationStart('correct horse');
const regResponse = opaque.serverRegistrationStart(setup, reg.request, 'alice');
const upload = opaque.clientRegistrationFinish(reg.state, 'correct horse', regResponse);
const file = opaque.serverRegistrationFinish(upload.upload);
const registerMs = performance.now() - began;
assert.ok(equal(upload.serverPublicKey, serverKey), 'registered with this server');

function login(password, {passwordFile = file, user = 'alice', clientContext = context} = {}) {
    const start = opaque.clientLoginStart(password);
    const server = opaque.serverLoginStart(setup, passwordFile, start.request, user, context);
    const client = opaque.clientLoginFinish(start.state, password, server.response, clientContext);
    return {server, client};
}

// Login: the same session key at both ends, the same export key as registration's.
began = performance.now();
const {server, client} = login('correct horse');
const serverKeyAgreed = opaque.serverLoginFinish(server.state, client.finalization, context);
const loginMs = performance.now() - began;
assert.ok(equal(client.sessionKey, serverKeyAgreed) && client.sessionKey.length === 64, 'one session key, 64 bytes');
assert.ok(equal(client.exportKey, upload.exportKey), 'the export key is registration\'s');
assert.ok(equal(client.serverPublicKey, serverKey), 'logged in to this server');

// Refused.
refused(1, () => login('wrong horse'), 'a wrong password');
refused(1, () => login('correct horse', {passwordFile: null}), 'no such user: a response like any other, failing at the client');
refused(1, () => login('correct horse', {user: 'bob'}), 'another user\'s key for this file');
refused(1, () => login('correct horse', {clientContext: new TextEncoder().encode('another connection')}),
    'a login bound to another context');
const again = login('correct horse');
const tampered = again.client.finalization.slice();
tampered[0] ^= 1;
refused(1, () => opaque.serverLoginFinish(again.server.state, tampered, context), 'a tampered finalization');
refused(2, () => opaque.serverRegistrationFinish(new Uint8Array(7)), 'an upload that isn\'t one');
refused(2, () => opaque.serverPublicKey(new Uint8Array(3)), 'a setup that isn\'t one');

console.log(`opaque.wasm: registered in ${registerMs.toFixed(0)} ms, logged in in ${loginMs.toFixed(0)} ms ` +
    `(Argon2id 64 MiB × 3 at the client); setup ${setup.length} B, password file ${file.length} B, ` +
    `KE1 ${opaque.clientLoginStart('x').request.length} B, ` +
    `KE2 ${server.response.length} B, KE3 ${client.finalization.length} B — all refusals as expected`);
