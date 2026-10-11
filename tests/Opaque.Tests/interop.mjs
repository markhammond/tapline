// Copyright 2026 Mark Hammond. Licensed under the Apache License, Version 2.0 (see src/Tapline.Authentication.Opaque/LICENSE).
//
// The JavaScript host's side for Opaque.Tests: one JSON line in — {op, args}, each arg {hex} or {text} — one JSON line
// out: the call's outputs (their bytes as hex), or {error: status}. Runs the package's wwwroot/opaque.mjs on its
// opaque.wasm, as a page would.

import {readFile} from 'node:fs/promises';
import {createInterface} from 'node:readline';
import {loadOpaque} from '../../src/Tapline.Authentication.Opaque/wwwroot/opaque.mjs';

const opaque = await loadOpaque(await readFile(new URL('../../src/Tapline.Authentication.Opaque/wwwroot/opaque.wasm', import.meta.url)));
const hex = b => Buffer.from(b).toString('hex');
const arg = a => a === null ? null : a.text !== undefined ? a.text : new Uint8Array(Buffer.from(a.hex, 'hex'));

for await (const line of createInterface({input: process.stdin})) {
    const {op, args} = JSON.parse(line);
    let reply;
    try {
        const out = opaque[op](...args.map(arg));
        reply = out instanceof Uint8Array ? {value: hex(out)}
            : Object.fromEntries(Object.entries(out).map(([k, v]) => [k, hex(v)]));
    } catch (e) {
        reply = {error: e.status ?? String(e)};
    }
    process.stdout.write(JSON.stringify(reply) + '\n');
}
