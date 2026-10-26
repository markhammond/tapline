// Verifies tapline.js against frames produced by the C# library (run the C# tests first to write the fixtures).
// Node's own modules only: nothing to install. (The SignalR JS client's side of the wire is held to captured bytes by
// the C# tests: fixtures/signalr.json.)
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { TaplineClient, rowHash, FaultCode, BlobRef, StaleData } from '../../src/Tapline.AspNetCore/wwwroot/tapline.js';
import { loadOpaque } from '../../src/Tapline.Authentication.Opaque/wwwroot/opaque.mjs';
import { signInWithPassword, registerPassword } from '../../src/Tapline.Authentication.Opaque/wwwroot/tapline-opaque.mjs';

const fx = JSON.parse(readFileSync(new URL('./fixtures/frames.json', import.meta.url)));
const hex = s => Uint8Array.from(Buffer.from(s, 'hex'));
const toHex = b => Buffer.from(b).toString('hex').toUpperCase();
/** A RouterChange record's length (it rides behind a heartbeat): by its state — draining's grace, degraded's reason. */
const routerChangeLength = (fr, o) => 22 + (fr[o + 1] === 2 ? 4 : fr[o + 1] === 1 ? 1 + fr[o + 22] : 0);
let passed = 0;
const tests = [];
const test = (name, fn) => tests.push([name, fn]);

function newClient(sent) {
  const client = new TaplineClient();
  client.bind(3, 77);
  client.connection = { send: (...a) => { sent.push(a); return Promise.resolve(); } };
  return client;
}

test('table replica: every delta verifies; final rows equal the server table', () => {
  const sent = [];
  const client = newClient(sent);
  const messages = [], mail = [];
  const i32 = bytes => new DataView(bytes.buffer, bytes.byteOffset).getInt32(0, true);
  let updates = 0;
  client.subscribe(fx.tableId, t => { assert.ok(t.verified); updates++; });
  client.subscribe(fx.valueId, () => {}, undefined, (bytes, id, meta) => { assert.ok(meta.mailbox && id === fx.valueId); mail.push(i32(bytes)); });
  client.subscribe(fx.alertsId, () => assert.fail('messages go to onMessage'), undefined,
    (bytes, _id, meta) => { assert.ok(!meta.mailbox && !meta.gap); messages.push([meta.seq, i32(bytes)]); });
  assert.deepEqual(sent.map(a => a.slice(0, 4)), [['Tap.Activate', 3, 77, fx.tableId], ['Tap.Activate', 3, 77, fx.valueId], ['Tap.Activate', 3, 77, fx.alertsId]]);

  fx.frames.forEach((b64, i) => client.receive(i + 1, new Uint8Array(Buffer.from(b64, 'base64'))));
  assert.equal(client.bytesReceived, fx.frames.reduce((n, b64) => n + Buffer.from(b64, 'base64').length, 0), 'bytes received');
  assert.ok(client.transferRate > 0, 'a smoothed rate');

  const table = client.tables.get(fx.tableId);
  assert.equal(table.seq, fx.seq);
  assert.equal(table.checksum, fx.checksum);
  assert.equal(table.keySize, 9);
  assert.equal(table.rowSize, 8);
  assert.equal(table.size, fx.rows.length);
  const actual = new Map([...table.rows.values()].map(e => [toHex(e.key), toHex(e.row)]));
  for (const [k, r] of fx.rows) assert.equal(actual.get(k), r);
  assert.ok(updates > 10);
  assert.equal(sent.filter(a => a[0] === 'Tap.Resync').length, 0, 'no resync expected');
  const acks = sent.filter(a => a[0] === 'Tap.Ack');
  assert.ok(sent.findIndex(a => a[0] === 'Tap.Ack') > sent.findLastIndex(a => a[0] === 'Tap.Activate'), 'activation precedes any frame');
  assert.equal(acks.length, fx.frames.length);
  assert.deepEqual(acks.at(-1).slice(0, 4), ['Tap.Ack', 3, 77, fx.frames.length]);
  assert.ok(acks.at(-1)[4] > 0, 'heartbeat echo');
  assert.deepEqual(messages, Array.from({ length: fx.expectedMessages }, (_, i) => [i + 1, i * 10]), 'every message of the topic, in order, numbered from 1');
  assert.deepEqual(mail, Array.from({ length: fx.expectedMail }, (_, i) => 1_000_000 + i * 50), 'the mailbox\'s, in order');
  assert.equal(new DataView(client.values.get(fx.valueId).buffer).getInt32(0, true), fx.lastValue);
});

test('binding (again), the client says only the last frame it received — Tap.Resume, with the heartbeat echo — then re-activates; nothing per subscription', () => {
  const sent = [];
  const client = new TaplineClient();
  client.connection = { send: (...a) => { sent.push(a); return Promise.resolve(); } };
  client.subscribe(fx.tableId, () => {});
  fx.frames.forEach((b64, i) => client.receive(i + 1, new Uint8Array(Buffer.from(b64, 'base64'))));
  assert.equal(sent.length, 0, 'nowhere to send before the bind');
  client.bind(3, 77);
  assert.deepEqual(sent[0].slice(0, 4), ['Tap.Resume', 3, 77, fx.frames.length]);
  assert.ok(sent[0][4] > 0, 'heartbeat echo');
  assert.deepEqual(sent.slice(1), [['Tap.Activate', 3, 77, fx.tableId]], 'its activation, bare');
});

test('transfer rate: a steady stream reads its true rate (2 s exponential smoothing), then decays', async () => {
  const client = newClient([]);
  const frame = new Uint8Array(100);                       // filler: heartbeats
  frame[0] = 0x01; for (let i = 13; i < 100; i += 13) frame[i] = 0x01;
  const start = performance.now();
  for (let k = 0; performance.now() - start < 6000; k++) { client.receive(k + 1, frame.subarray(0, 91)); await new Promise(r => setTimeout(r, 10)); }
  const measured = 91 * client.lastFrame / ((performance.now() - start) / 1000), smoothed = client.transferRate;
  assert.ok(Math.abs(smoothed - measured) / measured < 0.1, `smoothed ${smoothed.toFixed(0)} B/s vs actual ${measured.toFixed(0)} B/s`);
  await new Promise(r => setTimeout(r, 1000));
  assert.ok(client.transferRate < smoothed * Math.exp(-1000 / 2000) * 1.05, 'decays when nothing arrives');
});

test('a view decodes rows itself (the library knows nothing about books)', () => {
  const client = newClient([]);
  client.subscribe(fx.tableId, () => {});
  fx.frames.forEach((b64, i) => client.receive(i + 1, new Uint8Array(Buffer.from(b64, 'base64'))));
  const book = { bids: [], asks: [] };
  for (const { key, row } of client.tables.get(fx.tableId).rows.values()) {
    const k = new DataView(key.buffer, key.byteOffset), r = new DataView(row.buffer, row.byteOffset);
    (key[0] === 0 ? book.bids : book.asks).push([Number(k.getBigInt64(1, true)), Number(r.getBigInt64(0, true))]);
  }
  book.bids.sort((a, b) => b[0] - a[0]);
  book.asks.sort((a, b) => a[0] - b[0]);
  assert.equal(book.bids.length + book.asks.length, fx.rows.length);
  assert.ok(book.bids.every(([p, q]) => p > 4_503_599_627_369_999 && q > 0));
});

test('corrupted delta: checksum mismatch → one resync, deltas ignored until reset', () => {
  const sent = [];
  const client = newClient(sent);
  client.subscribe(fx.tableId, () => {});
  const frames = fx.frames.map(b => new Uint8Array(Buffer.from(b, 'base64')));
  let target = -1;
  outer: for (let f = 1; f < frames.length; f++) {
    const fr = frames[f], dv = new DataView(fr.buffer, fr.byteOffset, fr.byteLength);
    for (let o = 0; o < fr.length;) {
      const t = fr[o];
      if (t === 1) o += 13;
      else if (t === 2) o += 4 + dv.getUint16(o + 2, true);
      else if (t === 3) o += 7 + dv.getUint16(o + 5, true);
      else {
        const n = dv.getUint32(o + 26, true), stride = 1 + dv.getUint16(o + 30, true) + dv.getUint16(o + 32, true);
        if (!(fr[o + 5] & 1) && n > 0 && fr[o + 34] === 0) { fr[o + 34 + 1 + 9] ^= 1; target = f; break outer; }   // flip a row bit of an upsert
        o += 34 + stride * n;
      }
    }
  }
  assert.ok(target > 0);
  frames.forEach((fr, i) => client.receive(i + 1, fr));
  assert.equal(sent.filter(a => a[0] === 'Tap.Resync').length, 1);
  assert.equal(client.tables.get(fx.tableId).awaitingReset, true);
});

test('two-phase: records for taps without a handler are skipped, nothing is buffered', () => {
  const client = newClient([]);
  fx.frames.forEach((b64, i) => client.receive(i + 1, new Uint8Array(Buffer.from(b64, 'base64'))));
  assert.equal(client.tables.size, 0);
  assert.equal(client.values.size, 0);
});

test('subscribe accepts a TapBinding; its endpoint is ignored on a circuit', () => {
  const sent = [];
  const client = newClient(sent);
  client.subscribe({ tapId: fx.tableId, topic: 'book:BTC-PERP', endpoint: 'wss://gw-7.example.net' }, () => {});
  assert.deepEqual(sent, [['Tap.Activate', 3, 77, fx.tableId]]);   // same connection, no other request
  fx.frames.forEach((b64, i) => client.receive(i + 1, new Uint8Array(Buffer.from(b64, 'base64'))));
  assert.equal(client.tables.get(fx.tableId).checksum, fx.checksum);
});

test('rebind (reconnect): the last frame received, then every subscribed tap\'s activation again', () => {
  const sent = [];
  const client = newClient(sent);
  client.subscribe(5, () => {});
  client.subscribe(9, () => {});
  sent.length = 0;
  client.bind(4, 88);
  assert.deepEqual(sent.map(a => a.slice(0, 4)), [['Tap.Resume', 4, 88, 0], ['Tap.Activate', 4, 88, 5], ['Tap.Activate', 4, 88, 9]]);
});

test('rowHash: Σ over the final rows equals the C# checksum', () => {
  let sum = 0;
  for (const [k, r] of fx.rows) {
    const key = hex(k), row = hex(r);
    sum = (sum + rowHash(key, 0, key.length, row, 0, row.length)) >>> 0;
  }
  assert.equal(sum, fx.checksum);
});

const frameBytes = list => list.map(b => new Uint8Array(Buffer.from(b, 'base64')));
const text = b => Buffer.from(b).toString();

test('server fault: degraded snapshot + Error → onError once with payload and the degraded replica; tap forgotten', () => {
  const sent = [];
  const client = newClient(sent);
  const f = fx.fault;
  const states = [], errors = [];
  client.subscribe({ tapId: f.bookId, topic: 'book:BTC-PERP' }, t => states.push({ degraded: t.degraded, size: t.size }), (e, t, id) => errors.push({ e, t, id }));
  frameBytes(f.frames).forEach((fr, i) => client.receive(i + 1, fr));

  assert.equal(states[0].size, f.rowCount, 'normal snapshot first');
  assert.deepEqual(states.at(-1), { degraded: true, size: 2 }, 'degraded snapshot is delivered as state before the error');
  assert.equal(errors.length, 1);
  const { e, t, id } = errors[0];
  assert.equal(id, f.bookId);
  assert.deepEqual({ code: e.code, version: e.version, local: e.local }, { code: FaultCode.VersionMismatch, version: f.version, local: false });
  assert.equal(text(e.payload), 'feed interrupted');
  assert.ok(t.degraded && t.verified, 'the degraded replica still verifies against its checksum');
  assert.deepEqual(new Map([...t.rows.values()].map(r => [toHex(r.key), toHex(r.row)])), new Map(f.degradedRows));
  assert.ok(!client.tables.has(f.bookId) && !client.watchers.has(f.bookId), 'forgotten: the app resubscribes');
  assert.equal(sent.filter(a => a[0] === 'Tap.Resync' || a[0] === 'Tap.Close').length, 0, 'nothing to tell the server');
  sent.length = 0;
  client.bind(3, 77);
  assert.equal(sent.filter(a => a[0] === 'Tap.Activate').length, 0, 'a failed tap is not re-activated on reconnect');
});

test('last-value fault: onError with its payload and the last value', () => {
  const client = newClient([]);
  const f = fx.fault;
  const errors = [];
  client.subscribe(f.markId, () => {}, (e, v) => errors.push({ e, v }));
  frameBytes(f.frames).forEach((fr, i) => client.receive(i + 1, fr));
  assert.equal(errors.length, 1);
  assert.equal(errors[0].e.code, FaultCode.SourceFailed);
  assert.equal(errors[0].e.version, f.markVersion);
  assert.equal(text(errors[0].e.payload), 'stale');
  assert.equal(new DataView(errors[0].v.buffer).getInt32(0, true), 77);
});

test('service down: the subscription through that instance fails (ServiceDown) and onServiceDown runs once; others untouched; a later subscribe with its id fails at once; then up, by name', async () => {
  const sent = [];
  const client = newClient(sent);
  const sv = fx.service, errors = [], downs = [], direct = [];
  assert.match(sv.binding.nodeId, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);   // as Blazor's interop sends it
  client.onServiceDown = id => downs.push(id);
  client.subscribe(sv.binding, () => {}, e => errors.push(e));
  client.subscribe(sv.directId, v => direct.push(v[0]));
  const frames = frameBytes(sv.frames);
  frames.forEach((fr, i) => client.receive(i + 1, fr));
  assert.deepEqual(errors.map(e => [e.code, e.local]), [[FaultCode.ServiceDown, false]]);
  assert.deepEqual(downs, [sv.binding.nodeId]);                 // the wire's 16 bytes read as the binding's string
  assert.equal(client.watchers.has(sv.binding.tapId), false);
  assert.equal(direct.at(-1), 6);                                        // not through the provider: still streaming
  client.receive(frames.length + 1, frames.at(-1));                      // the notice again (resent after a reconnect)
  assert.equal(downs.length, 1);
  sent.length = 0;
  const late = [];
  client.subscribe({ ...sv.binding, tapId: 999 }, () => {}, e => late.push(e.code));   // a binding from the dead instance
  await Promise.resolve();
  assert.deepEqual(late, [FaultCode.ServiceDown]);
  assert.equal(sent.filter(a => a[0] === 'Tap.Activate').length, 0);
  const ups = [];
  client.onServiceUp = service => ups.push(service);
  frameBytes(sv.upFrames).forEach((fr, i) => client.receive(frames.length + 2 + i, fr));
  assert.deepEqual(ups, [sv.binding.service]);                           // up by the service's name — no instance
  assert.equal(downs.length, 1);
});

test('service drained: the subscription through the draining instance fails (ServiceDrained), onServiceDrained runs — not down; a later subscribe with its id fails at once', async () => {
  const sent = [];
  const client = newClient(sent);
  const sv = fx.service, errors = [], drained = [], downs = [];
  client.onServiceDrained = id => drained.push(id);
  client.onServiceDown = id => downs.push(id);
  client.subscribe(sv.drainedBinding, () => {}, e => errors.push(e.code));
  frameBytes(sv.drainedFrames).forEach((fr, i) => client.receive(i + 1, fr));
  assert.deepEqual(errors, [FaultCode.ServiceDrained]);
  assert.deepEqual(drained, [sv.drainedBinding.nodeId]);
  assert.deepEqual(downs, []);
  const late = [];
  client.subscribe({ ...sv.drainedBinding, tapId: 998 }, () => {}, e => late.push(e.code));
  await Promise.resolve();
  assert.deepEqual(late, [FaultCode.ServiceDrained]);
});

test('service draining: told to move (its id, the grace) — nothing fails, the subscription streams on', () => {
  const client = newClient([]);
  const sv = fx.service, errors = [], told = [];
  client.onServiceDraining = (id, graceMs) => told.push([id, graceMs]);
  client.subscribe(sv.drainingBinding, () => {}, e => errors.push(e.code));
  frameBytes(sv.drainingFrames).forEach((fr, i) => client.receive(i + 1, fr));
  assert.deepEqual(told, [[sv.drainingBinding.nodeId, 10000]]);
  assert.deepEqual(errors, []);
  assert.equal(client.watchers.has(sv.drainingBinding.tapId), true);
});

test('move: one subscription told to move (its tap, the grace, the correlation, its node) — once, though resent; nothing fails; another tap\'s move ignored', () => {
  const client = newClient([]);
  const node = '0192aaaa-bbbb-7ccc-8ddd-eeeeffff0001', correlation = '0192aaaa-bbbb-7ccc-8ddd-eeeeffff0002';
  const bytes = g => Uint8Array.from(g.replaceAll('-', '').match(/../g).map(h => parseInt(h, 16)));
  const move = tapId => {                                              // Service u8 0x08 | u8 6 | u16 40 | node | correlation | graceMs | tapId
    const r = new Uint8Array(44), dv = new DataView(r.buffer);
    r[0] = 0x08; r[1] = 6; dv.setUint16(2, 40, true);
    r.set(bytes(node), 4); r.set(bytes(correlation), 20);
    dv.setUint32(36, 2000, true); dv.setUint32(40, tapId, true);
    return r;
  };
  const errors = [], told = [];
  client.onMove = (...args) => told.push(args);
  client.subscribe({ tapId: 51, service: 'svc', nodeId: node }, () => {}, e => errors.push(e.code));
  client.receive(1, move(51));
  client.receive(2, move(51));                                           // resent until acknowledged: told once
  client.receive(3, move(77));                                           // not ours
  assert.deepEqual(told, [[51, 2000, correlation, node]]);
  assert.deepEqual(errors, []);
  assert.equal(client.watchers.has(51), true);
  assert.equal(FaultCode.Moved, 14);
});

test('replace (make before break): the new subscription activated, the old kept streaming until the new one\'s first state, then dropped', () => {
  const sent = [];
  const client = newClient(sent);
  const value = (id, b) => { const f = new Uint8Array(8), dv = new DataView(f.buffer); f[0] = 0x03; dv.setUint32(1, id, true); dv.setUint16(5, 1, true); f[7] = b; return f; };
  const frame = (...records) => { const f = new Uint8Array(records.reduce((n, r) => n + r.length, 0)); let o = 0; for (const r of records) { f.set(r, o); o += r.length; } return f; };
  const old = [], next = [];
  client.subscribe(41, v => old.push(v[0]));
  client.receive(1, frame(value(41, 1)));
  sent.length = 0;
  client.replace(41, { tapId: 42 }, v => next.push(v[0]));
  assert.deepEqual(sent.map(a => a[0]), ['Tap.Activate']);              // the new one activated; the old one kept
  client.receive(2, frame(value(41, 2)));                                // the old one still streaming
  client.receive(3, frame(value(42, 7), value(41, 3)));                  // the new one's first state: the old one dropped
  assert.deepEqual(old, [1, 2]);
  assert.deepEqual(next, [7]);
  assert.deepEqual(sent.filter(a => a[0] === 'Tap.Close').map(a => a.slice(3)), [[41, FaultCode.None]]);
});

test('a snapshot that fails verification → Tap.Close(ChecksumMismatch) + local error; no resync loop', () => {
  const sent = [];
  const client = newClient(sent);
  const errors = [];
  client.subscribe(fx.tableId, () => assert.fail('a corrupted snapshot must not reach the view'), e => errors.push(e));
  const frames = frameBytes(fx.frames);
  const fr = frames[0], dv = new DataView(fr.buffer, fr.byteOffset, fr.byteLength);
  let o = 0;
  while (fr[o] !== 4) o += fr[o] === 1 ? 13 : fr[o] === 0x0d ? routerChangeLength(fr, o) : fr[o] === 2 ? 4 + dv.getUint16(o + 2, true) : 7 + dv.getUint16(o + 5, true);
  assert.ok(fr[o + 5] & 1 && dv.getUint32(o + 26, true) > 0, 'first table record is a non-empty snapshot');
  fr[o + 34 + 1 + 9] ^= 1;                                       // flip a row bit
  frames.forEach((f, i) => client.receive(i + 1, f));
  assert.deepEqual(sent.filter(a => a[0] === 'Tap.Close').map(a => a.slice(3)), [[fx.tableId, FaultCode.ChecksumMismatch]]);
  assert.equal(sent.filter(a => a[0] === 'Tap.Resync').length, 0);
  assert.equal(errors.length, 1);
  assert.ok(errors[0].local && errors[0].code === FaultCode.ChecksumMismatch);
  assert.ok(!client.tables.has(fx.tableId));
});

test('drop() unsubscribes on the server (Tap.Close with code None)', () => {
  const sent = [];
  const client = newClient(sent);
  client.subscribe(5, () => {});
  sent.length = 0;
  client.drop(5);
  client.drop(5);
  assert.deepEqual(sent, [['Tap.Close', 3, 77, 5, FaultCode.None]]);
});

// ───────────── changed keys ({ changed: true }) ─────────────

/** The keys whose rows differ between two copies of a replica's rows (key → row hex): added, changed or removed. */
const differing = (was, now) => {
  const keys = new Set();
  for (const [k, r] of now) if (was.get(k) !== r) keys.add(k);
  for (const k of was.keys()) if (!now.has(k)) keys.add(k);
  return keys;
};
const rowsOf = t => new Map([...t.rows].map(([k, e]) => [k, toHex(e.row)]));
const sorted = set => [...set].sort();

/**
 * A Table record for tap (or member) `id`: 2-byte keys named by two letters, 1-byte rows; `entries` [key, row] (row
 * null: a delete) applied to `model` (the server's rows, key → row), whose checksum it carries — `corrupt`: a wrong one.
 */
function tableRecord(id, { reset = false, from, to, entries, model, corrupt = false }) {
  const K = 2, R = 1, rec = new Uint8Array(34 + entries.length * (1 + K + R)), dv = new DataView(rec.buffer);
  rec[0] = 0x04;
  dv.setUint32(1, id, true);
  rec[5] = reset ? 1 : 0;
  dv.setBigInt64(6, BigInt(from), true);
  dv.setBigInt64(14, BigInt(to), true);
  if (reset) model.clear();
  entries.forEach(([key, row], i) => {
    const p = 34 + i * (1 + K + R);
    rec[p] = row === null ? 1 : 0;
    rec.set(Buffer.from(key, 'latin1'), p + 1);
    if (row === null) model.delete(key);
    else model.set(key, rec[p + 1 + K] = row);
  });
  let sum = 0;
  for (const [key, row] of model) sum = (sum + rowHash(Buffer.from(key, 'latin1'), 0, K, Uint8Array.of(row), 0, R)) >>> 0;
  dv.setUint32(22, corrupt ? sum ^ 1 : sum, true);
  dv.setUint32(26, entries.length, true);
  dv.setUint16(30, K, true);
  dv.setUint16(32, R, true);
  return rec;
}
const valueRecord = (id, bytes) => {
  const rec = new Uint8Array(7 + bytes.length), dv = new DataView(rec.buffer);
  rec[0] = 0x03;
  dv.setUint32(1, id, true);
  dv.setUint16(5, bytes.length, true);
  rec.set(bytes, 7);
  return rec;
};
/** A Group record: its members' records, each naming its member's index where a tap id would be. */
const groupRecord = (id, members, reset = false) => {
  const length = members.reduce((n, m) => n + m.length, 0), rec = new Uint8Array(12 + length), dv = new DataView(rec.buffer);
  rec[0] = 0x06;
  dv.setUint32(1, id, true);
  rec[5] = reset ? 1 : 0;
  dv.setUint16(6, members.length, true);
  dv.setUint32(8, length, true);
  let o = 12;
  for (const m of members) { rec.set(m, o); o += m.length; }
  return rec;
};

test('changed keys, asked for at subscribe: every state of a table tells exactly the keys whose rows differ from the state handed over before it — added, changed, removed (a Set, as rows has them) — checked against copies of the replica through every frame of a live book; not asked, nothing noted and no changed', () => {
  const client = newClient([]), plain = newClient([]);
  let shown = new Map(), states = 0, plainStates = 0;
  const kinds = { added: 0, changed: 0, removed: 0 };
  client.subscribe(fx.tableId, (t, _id, info) => {
    const now = rowsOf(t), expected = differing(shown, now);
    assert.ok(info.changed instanceof Set, 'a Set');
    assert.deepEqual(sorted(info.changed), sorted(expected), `state ${states}`);
    for (const k of expected) kinds[!now.has(k) ? 'removed' : shown.has(k) ? 'changed' : 'added']++;
    shown = now;
    states++;
  }, undefined, undefined, { changed: true });
  plain.subscribe(fx.tableId, (_t, _id, info) => {
    assert.ok(!('changed' in info) && 'provider' in info);
    plainStates++;
  });
  frameBytes(fx.frames).forEach((frame, i) => {
    client.receive(i + 1, frame);
    plain.receive(i + 1, frame);
  });
  assert.ok(states > 10 && kinds.added > 0 && kinds.changed > 0 && kinds.removed > 0, JSON.stringify(kinds));
  assert.equal(plainStates, states);
  assert.equal(plain.tables.get(fx.tableId).changes, null, 'not asked: nothing noted');
  assert.equal(client.tables.get(fx.tableId).changes.size, 0, 'asked: nothing held between states');
});

test('changed keys across a resync: a record that fails its checksum tells nothing; the snapshot after it tells each key whose row differs from the state last handed over — not from the rows the failed record left — once; an update to the same bytes, and a delete of a key it never had, tell nothing', () => {
  const sent = [], client = newClient(sent), model = new Map(), told = [];
  client.subscribe(9, (t, _id, info) => told.push([sorted(info.changed), Object.fromEntries([...rowsOf(t)].map(([k, r]) => [k, parseInt(r, 16)]))]),
    undefined, undefined, { changed: true });
  let seq = 0;
  const send = rec => client.receive(++seq, rec);
  send(tableRecord(9, { reset: true, from: 0, to: 1, entries: [['aa', 1], ['bb', 2], ['cc', 3]], model }));
  send(tableRecord(9, { from: 2, to: 2, entries: [['bb', 2], ['cc', 4], ['zz', null], ['dd', 5]], model }));
  const truth = new Map(model);                                          // the server's rows: the next record is corrupted on the way
  send(tableRecord(9, { from: 3, to: 3, entries: [['aa', 9], ['bb', null]], model: new Map(model), corrupt: true }));
  assert.equal(told.length, 2, 'the corrupted record handed nothing over');
  assert.deepEqual(sent.filter(a => a[0] === 'Tap.Resync').map(a => a[3]), [9]);
  truth.set('aa', 9);                                                    // it was right about aa; bb was never deleted
  send(tableRecord(9, { reset: true, from: 0, to: 5, entries: [...truth], model }));
  send(tableRecord(9, { from: 6, to: 6, entries: [['dd', null]], model }));
  assert.deepEqual(told, [
    [['aa', 'bb', 'cc'], { aa: 1, bb: 2, cc: 3 }],
    [['cc', 'dd'], { aa: 1, bb: 2, cc: 4, dd: 5 }],                        // bb to the same byte, zz never there: not told
    [['aa'], { aa: 9, bb: 2, cc: 4, dd: 5 }],                              // aa as the failed record had it: told; bb as before: not
    [['dd'], { aa: 9, bb: 2, cc: 4 }],                                     // removed: no longer in rows
  ]);
});

test('changed keys of a watch, per member by its index: a table\'s keys (a Set), and for a value whether it\'s another (the same bytes again: not) — through a group reset too, told against the members as last handed over; not asked: no changed', () => {
  const client = newClient([]), plain = newClient([]), positions = new Map(), told = [];
  client.subscribe(12, (g, _id, info) => told.push(info.changed.map(c => c instanceof Set ? sorted(c) : c)), undefined, undefined, { changed: true });
  plain.subscribe(12, (_g, _id, info) => assert.ok(!('changed' in info)));
  let seq = 0;
  const send = rec => { client.receive(++seq, rec); plain.receive(seq, rec); };
  send(groupRecord(12, [tableRecord(0, { reset: true, from: 0, to: 1, entries: [['aa', 1], ['bb', 2]], model: positions }), valueRecord(1, [7])], true));
  send(groupRecord(12, [tableRecord(0, { from: 2, to: 2, entries: [['cc', 3]], model: positions })]));
  send(groupRecord(12, [valueRecord(1, [7])]));
  send(groupRecord(12, [valueRecord(1, [8])]));
  const held = client.groups.get(12).members[0];
  send(groupRecord(12, [tableRecord(0, { reset: true, from: 0, to: 4, entries: [['aa', 1], ['bb', 5], ['cc', 3]], model: positions }), valueRecord(1, [8])], true));
  assert.deepEqual(Object.fromEntries([...rowsOf(held)].map(([k, r]) => [k, parseInt(r, 16)])), { aa: 1, bb: 2, cc: 3 }, 'a member held from before the reset is left as it was');
  assert.deepEqual(told, [
    [['aa', 'bb'], true],
    [['cc'], false],
    [[], false],                                                           // the same bytes again
    [[], true],
    [['bb'], false],                                                       // the reset's snapshot, against the members before it
  ]);
  const g = client.groups.get(12);
  assert.deepEqual(Object.fromEntries([...rowsOf(g.members[0])].map(([k, r]) => [k, parseInt(r, 16)])), { aa: 1, bb: 5, cc: 3 });
  assert.equal(plain.groups.get(12).members[0].changes, null, 'not asked: nothing noted');
});

test('changed keys through replace and a later view: replace hands { provider, changed } on; a view added by watch() is handed the state there is, whole (no changed), then each record\'s changes', () => {
  const client = newClient([]), model = new Map(), replaced = [], later = [];
  client.replace(null, { tapId: 21 }, (_t, _id, info) => replaced.push(sorted(info.changed)), undefined, { changed: true });
  client.receive(1, tableRecord(21, { reset: true, from: 0, to: 1, entries: [['aa', 1]], model }));
  client.watch(21, (t, _id, info) => later.push('changed' in info ? sorted(info.changed) : `whole: ${t.size}`), undefined, undefined, { changed: true });
  client.receive(2, tableRecord(21, { from: 2, to: 2, entries: [['bb', 2]], model }));
  assert.deepEqual(replaced, [['aa'], ['bb']]);
  assert.deepEqual(later, ['whole: 1', ['bb']]);

  const plain = newClient([]), seen = [];                                 // a view asking later than the subscription: noted from the next record
  plain.subscribe(22, () => {});
  plain.receive(1, tableRecord(22, { reset: true, from: 0, to: 1, entries: [['aa', 1]], model: new Map() }));
  plain.watch(22, (_t, _id, info) => seen.push('changed' in info ? sorted(info.changed) : 'whole'), undefined, undefined, { changed: true });
  plain.receive(2, tableRecord(22, { from: 2, to: 2, entries: [['aa', 3]], model: new Map([['aa', 1]]) }));
  assert.deepEqual(seen, ['whole', ['aa']]);
});

// ───────────── out of band + watches (fixture: fx.oob) ─────────────

const ob = fx.oob;
const b64 = s => new Uint8Array(Buffer.from(s, 'base64'));

function oobClient(sent, blobs = ob.blobs) {
  const client = new TaplineClient();
  client.bind(ob.outletId, ob.nonce);
  client.connection = { send: (...a) => { sent.push(a); return Promise.resolve(); } };
  client.retryDelayMs = 0;
  client.fetchBlob = async url => {
    if (!(url in blobs)) throw new Error(`404 ${url}`);
    return b64(blobs[url]);
  };
  return client;
}

const i64row = row => Number(new DataView(row.buffer, row.byteOffset).getBigInt64(0, true));
const wealth = g => {
  const [positions, cash] = g.members;
  if (!positions || !cash || cash.size !== 1) return NaN;
  return [...positions.rows.values()].reduce((a, r) => a + i64row(r.row), 0) + i64row([...cash.rows.values()][0].row);
};

test('out of band: snapshot and message fetched, verified, applied in order, confirmed; reference handed over; watch coherent', async () => {
  const sent = [];
  const client = oobClient(sent);
  const messages = [], wealths = [];
  let reference;
  client.subscribe(ob.tableId, () => {});
  client.subscribe(ob.refTapId, v => reference = v);
  client.subscribe(ob.watchId, g => wealths.push(wealth(g)));
  client.subscribe(ob.messagesId, () => {}, undefined, (bytes, _id, meta) => messages.push([meta.seq, Uint8Array.from(bytes)]));

  ob.frames1.forEach((f, i) => client.receive(i + 1, b64(f)));
  assert.equal(client.tables.has(ob.tableId), false, 'nothing applied before the fetch');
  await client.idle();
  const fetched = sent.filter(a => a[0] === 'Tap.Fetched').map(a => a.slice(3));
  assert.deepEqual(fetched.map(a => a.slice(0, 2)).sort(), [[0, ob.tableId], [0, ob.messagesId]].sort(), 'Tap.Fetched for the table\'s tap and the message topic\'s');
  assert.ok(fetched.every(a => a[2] >= 1), 'each confirms its handle\'s ticket');
  assert.equal(messages.length, 1, 'the small message behind it was held by the server');

  ob.frames2.forEach((f, i) => client.receive(100 + i, b64(f)));
  await client.idle();
  const table = client.tables.get(ob.tableId);
  assert.ok(table.verified);
  assert.equal(table.seq, ob.bookSeq);
  assert.equal(table.checksum, ob.bookChecksum);
  assert.equal(table.size, ob.bookRows);

  assert.equal(messages.length, 2);
  assert.deepEqual(messages.map(m => m[0]), [1, 2], 'in order, numbered');
  assert.equal(createHash('sha256').update(messages[0][1]).digest('hex').toUpperCase(), ob.bigMessageHex, 'large message intact');
  assert.deepEqual([...messages[1][1]], [7], 'then the one behind it');

  assert.ok(reference instanceof BlobRef);
  assert.equal(reference.url, Object.keys(ob.blobs).find(u => u.startsWith(`_tapline/blob/${reference.id}/`)));
  assert.deepEqual([...await reference.bytes()], [1, 2, 3]);

  assert.ok(wealths.length >= 2 && wealths.every(w => w === ob.endowment), `coherent on every notification: ${wealths}`);
  const g = client.groups.get(ob.watchId);
  assert.equal(g.members[0].seq, ob.positionsSeq);
  assert.equal(g.members[1].seq, ob.cashSeq);
  assert.equal(g.members[0].size, ob.account2Rows);
  assert.equal(sent.filter(a => a[0] === 'Tap.Resync').length, 0);
});

test('out of band: a blob that fails verification is not applied; the tap resyncs (no Tap.Fetched)', async () => {
  const sent = [];
  const tampered = Object.fromEntries(Object.entries(ob.blobs).map(([u, v]) => {
    const b = b64(v); b[b.length - 1] ^= 1; return [u, Buffer.from(b).toString('base64')];
  }));
  const client = oobClient(sent, tampered);
  client.subscribe(ob.tableId, () => assert.fail('unverified data must not be applied'));
  ob.frames1.forEach((f, i) => client.receive(i + 1, b64(f)));
  await client.idle();
  assert.deepEqual(sent.filter(a => a[0] === 'Tap.Resync').map(a => a[3]), [ob.tableId]);
  assert.ok(!sent.some(a => a[0] === 'Tap.Fetched'));
});

test('out of band: a handle without the content-addressed flag (a unique id) is handed over unchecked', async () => {
  const sent = [];
  const tampered = Object.fromEntries(Object.entries(ob.blobs).map(([u, v]) => {
    const b = b64(v); b[b.length - 1] ^= 1; return [u, Buffer.from(b).toString('base64')];
  }));
  const client = oobClient(sent, tampered);
  let reference;
  client.subscribe(ob.refTapId, v => reference = v);
  ob.frames1.forEach((f, i) => client.receive(i + 1, b64(f)));
  await client.idle();
  assert.ok(reference instanceof BlobRef && !reference.contentAddressed, 'the reference value was not content-addressed');
  assert.deepEqual([...await reference.bytes()], [1, 2, 2], 'its bytes, as served: nothing to check them against');
});

test('watch: a member that fails verification resyncs the whole group, without notifying', async () => {
  const sent = [];
  const client = oobClient(sent);
  let notified = 0;
  client.subscribe(ob.watchId, () => notified++);
  const frames = ob.frames1.map(b64);
  let target = -1;
  outer: for (let f = 0; f < frames.length; f++) {
    const fr = frames[f], dv = new DataView(fr.buffer, fr.byteOffset, fr.byteLength);
    for (let o = 0; o < fr.length;) {
      const t = fr[o];
      if (t === 6) { fr[o + 12 + 34 + 1 + 8] ^= 1; target = f; break outer; }   // first member's first row byte
      o += t === 1 ? 13 : t === 0x0d ? routerChangeLength(fr, o) : t === 3 ? 7 + dv.getUint16(o + 5, true)
         : t === 4 ? 34 + dv.getUint32(o + 26, true) * (1 + dv.getUint16(o + 30, true) + dv.getUint16(o + 32, true))
         : t === 5 ? 17 + dv.getUint16(o + 15, true) : t === 9 ? 18 + dv.getUint32(o + 14, true) : 47;
    }
  }
  assert.ok(target >= 0);
  frames.forEach((f, i) => client.receive(i + 1, f));
  await client.idle();
  assert.equal(notified, 0);
  assert.ok(sent.some(a => a[0] === 'Tap.Close' && a[3] === ob.watchId) || sent.some(a => a[0] === 'Tap.Resync' && a[3] === ob.watchId));
});

test('out of band: a fetch overtaken by a reconnect is neither applied nor confirmed', async () => {
  const sent = [];
  const client = oobClient(sent);
  let release;
  const gate = new Promise(r => release = r);
  const fetchBlob = client.fetchBlob;
  client.fetchBlob = async url => { await gate; return fetchBlob(url); };
  client.subscribe(ob.tableId, () => assert.fail('stale snapshot applied'));
  ob.frames1.forEach((f, i) => client.receive(i + 1, b64(f)));
  client.bind(ob.outletId, ob.nonce);                      // reconnect while the fetch is in flight
  release();
  await client.idle();
  assert.equal(client.tables.has(ob.tableId), false);
  assert.ok(!sent.some(a => a[0] === 'Tap.Fetched' && a[3] === 0), 'no confirmation for the stale handle');
});

test('stalled: one Ended record fails every subscription up to its tap id (Stalled, from the server), not one bound since; then onEnded', () => {
  const sent = [], errors = [], ended = [];
  const st = fx.stalled, client = newClient(sent);
  let later;
  client.onEnded = (code, from, to) => ended.push([code, from, to, errors.length]);
  for (const id of st.ids.slice(0, 2)) client.subscribe(id, () => {}, e => errors.push([id, e.code, e.local]));
  client.subscribe(st.ids[2], v => later = [...v], () => assert.fail('bound since: it stands'));
  st.frames.forEach((f, i) => client.receive(i + 1, b64(f)));
  assert.deepEqual(errors, [[st.ids[0], FaultCode.Stalled, false], [st.ids[1], FaultCode.Stalled, false]]);
  assert.deepEqual(ended, [[FaultCode.Stalled, 1, st.ids[1], 2]], 'told once, after the subscriptions\' onError');
  assert.deepEqual(later, [4]);
  assert.ok(!client.watchers.has(st.ids[0]) && client.watchers.has(st.ids[2]));
});

test('rebind: out-of-band records confirmed on the connection before are confirmed again first (one sent as it fell may be lost), then Tap.Resume', async () => {
  const sent = [];
  const client = oobClient(sent);
  client.subscribe(ob.tableId, () => {});
  ob.frames1.forEach((f, i) => client.receive(i + 1, b64(f)));
  await client.idle();
  const confirmed = sent.filter(a => a[0] === 'Tap.Fetched' && a[4] === ob.tableId);
  assert.equal(confirmed.length, 1, 'fetched and confirmed');
  sent.length = 0;
  client.bind(ob.outletId, ob.nonce);
  assert.deepEqual(sent[0], confirmed[0], 'its confirmation again…');
  assert.equal(sent[1][0], 'Tap.Resume', '…before the resume');
  sent.length = 0;
  client.bind(ob.outletId, ob.nonce);
  assert.ok(!sent.some(a => a[0] === 'Tap.Fetched'), 'once: not again at the next rebind');
});

test('an Ended record naming one subscription fails just that one (from the server), then onEnded', () => {
  const sent = [], errors = [], ended = [];
  const client = newClient(sent);
  client.onEnded = (code, from, to) => ended.push([code, from, to]);
  client.subscribe(7, () => {}, e => errors.push([7, e.code, e.local]));
  client.subscribe(8, () => {}, () => assert.fail('not this one'));
  const record = new Uint8Array(11), dv = new DataView(record.buffer);
  record[0] = 0x0a; dv.setUint16(1, FaultCode.Overflowed, true); dv.setUint32(3, 7, true); dv.setUint32(7, 7, true);
  client.receive(1, record);
  assert.deepEqual(errors, [[7, FaultCode.Overflowed, false]]);
  assert.deepEqual(ended, [[FaultCode.Overflowed, 7, 7]]);
  assert.ok(!client.watchers.has(7) && client.watchers.has(8));
});

test('the router\'s state, behind its heartbeats: serving, degraded (why), draining (gone by) — onRouterHealth once per change, by its correlation id; the records behind each read', () => {
  const r = fx.router;
  const client = newClient([]);
  const seen = [], values = [];
  let beats = 0;
  client.onRouterHealth = h => seen.push(h);
  client.onHeartbeat = () => beats++;
  client.subscribe(r.valueId, v => values.push(v[0]));
  const before = Date.now();
  r.frames.forEach((b64, i) => client.receive(i + 1, new Uint8Array(Buffer.from(b64, 'base64'))));
  assert.equal(beats, r.frames.length, 'every heartbeat');
  assert.deepEqual(values, [1, 2, 3, 4], 'the value behind each: a RouterChange record\'s length follows its state');
  assert.equal(client.routerControls, 0xff, 'the control messages the router takes: all eight, Tap.Activate to Tap.Sync');
  assert.deepEqual(seen.map(h => [h.state, h.correlationId, h.reason]),
    [['serving', r.correlations[0], null], ['degraded', r.correlations[1], r.reason], ['draining', r.correlations[2], null]], 'each change once');
  assert.ok(seen[2].drainsBy >= before + r.graceMs - 2000 && seen[2].drainsBy <= Date.now() + r.graceMs, 'gone by: the grace left, by this page\'s clock');
  assert.equal(client.routerHealth, seen[2]);
});

test('the connection\'s server timeout follows the path: the floor on a fast one, four times the longest recent gap on a slow one, back as it eases; staleMs', () => {
  const client = new TaplineClient();
  let t = 0;
  client.now = () => t;
  const connection = { serverTimeoutInMilliseconds: 30000, keepAliveIntervalInMilliseconds: 15000, on() {} };
  client.attach(connection);
  assert.equal(connection.keepAliveIntervalInMilliseconds, 3000, 'pings when idle: before the heartbeats start');
  assert.equal(client.staleMs, Infinity);
  const heartbeat = new Uint8Array(13); heartbeat[0] = 0x01;
  const every = (ms, until) => { for (; t < until; t += ms) client.receive(0, heartbeat); };
  every(1000, 3000);
  assert.equal(connection.serverTimeoutInMilliseconds, 30000, 'SignalR\'s until 5 s of the path are seen');
  every(1000, 8000);
  assert.equal(connection.serverTimeoutInMilliseconds, 10000, 'a heartbeat a second: the floor');
  t += 6000; client.receive(0, heartbeat);                 // a 7 s gap: a slow path (or a large message on one)
  assert.equal(connection.serverTimeoutInMilliseconds, 28000, 'four times the gap');
  every(1000, t + 120000);
  assert.equal(connection.serverTimeoutInMilliseconds, 10000, 'eased: back to the floor');
  assert.ok(client.staleMs >= 0 && client.staleMs < 1000, 'heard just now');
});

test('partitions: a scope degraded covers the subscriptions at or under it alone — providerOf, onProviderState with its scope and the change\'s id — then recovered; the bindings carry the partition as ids', () => {
  const p = fx.partitions;
  const client = newClient([]);
  const states = [], provider = { fx: [], us: [] };
  const track = (list, state) => { if (list.at(-1) !== state) list.push(state); };   // its transitions
  client.onProviderState = (node, state, meta) => states.push([node, state, meta.scope, meta.correlationId]);
  client.subscribe(p.fx, () => {});
  client.subscribe(p.us, () => {});
  assert.ok(p.fx.nodeId === p.us.nodeId && /^[0-9a-f]{16}\/[0-9a-f]{16}$/.test(p.fx.partition) && /^[0-9a-f]{16}$/.test(p.us.partition) && p.fx.service === 'svc-p',
    'the bindings: one node, the partitions as ids, the service by name');
  p.frames.forEach((b, i) => {
    client.receive(i + 1, new Uint8Array(Buffer.from(b, 'base64')));
    track(provider.fx, client.providerOf(p.fx.tapId)); track(provider.us, client.providerOf(p.us.tapId));
  });
  assert.deepEqual(states, [[p.fx.nodeId, 'degraded', p.scope, p.correlations[0]], [p.fx.nodeId, 'serving', p.scope, p.correlations[1]]], 'told twice: the scope, each change\'s id');
  assert.ok(p.scope === `svc-p/${p.fx.partition.split('/')[0]}`, 'the scope: the service, then the partition\'s ids');
  assert.deepEqual([provider.fx, provider.us], [['serving', 'degraded', 'serving'], ['serving']], 'eu/fx degraded meanwhile; us serving throughout');
  assert.equal(client.values.get(p.us.tapId)[0], 9, 'us streamed on');
});

test('provider state: degraded / serving records change what each state and message carries ({ provider }); a stale message is flagged; reachability follows the connection', () => {
  const sent = [], states = [], seen = [], reach = [];
  const client = newClient(sent);
  let close;
  client.attach({ on() {}, onclose(fn) { close = fn; }, send: (...a) => { sent.push(a); return Promise.resolve(); } });
  client.onProviderState = (instance, state, meta) => states.push(state + (meta.correlationId === correlation ? '' : ' (no id)'));
  client.onReachability = r => reach.push(r);
  const instance = '00112233-4455-6677-8899-aabbccddeeff', correlation = '01890000-0000-7000-8000-000000000001';
  client.subscribe({ tapId: 4, nodeId: instance }, (v, _id, meta) => seen.push(['value', v[0], meta.provider]));
  client.subscribe({ tapId: 5, nodeId: instance }, () => {}, undefined, (m, _id, meta) => seen.push(['message', m[0], meta.provider, meta.stale]));
  const service = status => {                              // u8[16] node | u8[16] correlation: the node itself
    const r = new Uint8Array(36); r[0] = 0x08; r[1] = status; r[2] = 32;
    Buffer.from(instance.replaceAll('-', ''), 'hex').copy(r, 4); Buffer.from(correlation.replaceAll('-', ''), 'hex').copy(r, 20);
    return r;
  };
  const value = b => Uint8Array.from([0x03, 4, 0, 0, 0, 1, 0, b]);
  const message = (b, flags) => { const r = new Uint8Array(19), dv = new DataView(r.buffer); r[0] = 0x09; dv.setUint32(1, 5, true); r[5] = flags; dv.setBigInt64(6, 1n, true); dv.setUint32(14, 1, true); r[18] = b; return r; };
  client.receive(1, value(1));
  client.receive(2, Uint8Array.from([...service(4), ...value(0xee), ...message(2, 0x04)]));   // degraded: then the stale data
  assert.equal(client.providerOf(4), 'degraded');
  client.receive(3, Uint8Array.from([...service(5), ...value(3)]));                       // recovered
  assert.deepEqual(states, ['degraded', 'serving']);
  assert.deepEqual(seen, [['value', 1, 'serving'], ['value', 0xee, 'degraded'], ['message', 2, 'degraded', true], ['value', 3, 'serving']]);
  assert.ok(client.reachable, 'bound (newClient): reachable');
  close(new Error('timed out'));
  client.bind(3, 77);
  assert.deepEqual(reach, [false, true], 'not once the connection closes; again on the next bind');
});

test('stale data while the router is out of reach: the binding\'s policy applied by the page itself once the connection closes — the provider\'s function (its WebAssembly module, fetched at subscribe and checked), a fixed state, the last message again — to a copy, flagged; bound again, the real state', async () => {
  const wasm = new Uint8Array(readFileSync(new URL('./fixtures/top-of-book.wasm', import.meta.url)));
  const moduleId = createHash('sha256').update(wasm).digest('hex').slice(0, 32);
  const sent = [], fetched = [], seen = [], reach = [];
  const client = newClient(sent);
  let close;
  client.attach({ on() {}, onclose(fn) { close = fn; }, send: (...a) => { sent.push(a); return Promise.resolve(); } });
  client.fetchBlob = async url => { fetched.push(url); return url.endsWith(moduleId) ? wasm : Uint8Array.of(0, 0x61, 0x73, 0x6d, 1, 0, 0, 0); };
  client.onReachability = r => reach.push(r);
  const instance = '00112233-4455-6677-8899-aabbccddeeff';
  const record = (id, meta) => seen.push([id, meta]);
  client.subscribe({ tapId: 1, nodeId: instance, stale: { kind: StaleData.Function, state: '', module: moduleId } }, (t, id, meta) => record(id, { rows: t.size, degraded: t.degraded, prices: [...t.rows.values()].map(e => Number(new DataView(e.key.buffer, e.key.byteOffset).getBigInt64(1, true))).sort((a, b) => a - b), sizes: [...t.rows.values()].map(e => Number(new DataView(e.row.buffer, e.row.byteOffset).getBigInt64(0, true))), ...meta }));
  client.subscribe({ tapId: 2, stale: { kind: StaleData.Static, state: Buffer.from([0xee]).toString('base64') } }, (v, id, meta) => record(id, { value: v[0], ...meta }));
  client.subscribe({ tapId: 3, nodeId: instance, stale: { kind: StaleData.ReplayLast } }, () => assert.fail('to onMessage'), undefined, (m, id, meta) => record(id, { message: m[0], ...meta }));
  client.subscribe({ tapId: 4, stale: { kind: StaleData.Function, state: '', module: moduleId } }, (v, id, meta) => record(id, { value: v[0], ...meta }));   // a value: the book's function traps on it
  client.subscribe({ tapId: 5, stale: { kind: StaleData.Function, state: '', module: '0'.repeat(32) } }, (t, id, meta) => record(id, { rows: t.size, ...meta }));   // its module fails its check
  client.subscribe({ tapId: 6, stale: { kind: StaleData.ReplayLast } }, (v, id, meta) => record(id, { value: v[0], ...meta }));   // nothing received: nothing to show
  await client.idle();
  assert.deepEqual(fetched.sort(), [`_tapline/module/${'0'.repeat(32)}`, `_tapline/module/${moduleId}`], 'each module fetched at subscribe (once), while the router is reachable');
  assert.equal(client.compiled.size, 1, 'the one that matched its id compiled; the other refused');

  // The book: three bids and three asks; a value; two messages.
  const book = [[0, 97, 5], [0, 99, 6], [0, 98, 7], [1, 103, 8], [1, 101, 9], [1, 102, 10]];
  const table = (id, rows) => {
    const K = 9, R = 8, r = new Uint8Array(34 + rows.length * (1 + K + R)), dv = new DataView(r.buffer);
    r[0] = 0x04; dv.setUint32(1, id, true); r[5] = 0x01; dv.setUint32(26, rows.length, true); dv.setUint16(30, K, true); dv.setUint16(32, R, true);
    let sum = 0;
    rows.forEach(([side, price, size], i) => {
      const o = 34 + i * (1 + K + R);
      r[o + 1] = side; dv.setBigInt64(o + 2, BigInt(price), true); dv.setBigInt64(o + 1 + K, BigInt(size), true);
      sum = (sum + rowHash(r, o + 1, K, r, o + 1 + K, R)) >>> 0;
    });
    dv.setUint32(22, sum, true);
    return r;
  };
  const value = (id, b) => Uint8Array.from([0x03, id, 0, 0, 0, 1, 0, b]);
  const message = (id, b, seq) => { const r = new Uint8Array(19), dv = new DataView(r.buffer); r[0] = 0x09; dv.setUint32(1, id, true); dv.setBigInt64(6, BigInt(seq), true); dv.setUint32(14, 1, true); r[18] = b; return r; };
  client.receive(1, Uint8Array.from([...table(1, book), ...value(2, 1), ...message(3, 7, 1), ...message(3, 8, 2), ...value(4, 3), ...table(5, book)]));
  seen.length = 0;

  close(new Error('timed out'));
  assert.deepEqual(reach, [false]);
  assert.deepEqual(seen, [
    [1, { rows: 2, degraded: true, prices: [99, 101], sizes: [0, 0], provider: 'unreachable', stale: true }],
    [2, { value: 0xee, provider: undefined, stale: true }],
    [3, { message: 8, seq: 2, gap: false, mailbox: false, stale: true, provider: 'unreachable' }],
  ], 'the book through its module (the best bid and ask, sizes zero), the fixed value, the last message again — flagged; the rest nothing');
  assert.equal(client.tables.get(1).size, 6, 'the replica itself untouched');
  assert.equal(client.providerOf(1), 'unreachable');

  seen.length = 0;
  client.bind(3, 77);
  assert.deepEqual(reach, [false, true]);
  assert.deepEqual(seen, [
    [1, { rows: 6, degraded: false, prices: [97, 98, 99, 101, 102, 103], sizes: [5, 6, 7, 8, 9, 10], provider: 'serving' }],
    [2, { value: 1, provider: undefined }],
  ], 'bound again: the real state (a message stream: the router goes on)');
});

test('refused, saying why and of what kind: logged once (console.warn) with its correlation id, and onRefused called — a resent record (the same id) not again', () => {
  const client = newClient([]);
  const id = '0198d6a2-7c41-7e3b-9f10-2b4c6d8e0a13', service = 'pivot', reason = 'rows and columns are the same dimension — é';
  const kind = 1;                                               // RefusalKind.Unauthenticated: sign in, then subscribe again
  const enc = new TextEncoder(), s = enc.encode(service), r = enc.encode(reason);
  const record = new Uint8Array(22 + s.length + r.length), dv = new DataView(record.buffer);
  record[0] = 0x0c;
  record[1] = kind;
  record.set(hex(id.replaceAll('-', '')), 2);
  dv.setUint16(18, s.length, true); record.set(s, 20);
  dv.setUint16(20 + s.length, r.length, true); record.set(r, 22 + s.length);
  const warned = [], refused = [];
  const warn = console.warn;
  console.warn = line => warned.push(line);
  try {
    client.onRefused = refusal => refused.push(refusal);
    client.receive(1, record);
    client.receive(2, record);                                  // resent after a reconnect: the same refusal
  } finally { console.warn = warn; }
  assert.deepEqual(refused, [{ correlationId: id, service, reason, kind }]);
  assert.equal(warned.length, 1);
  assert.ok(warned[0].includes(id) && warned[0].includes(reason) && warned[0].includes(service), warned[0]);
});

test('sync: Tap.Sync names the subscription and an id of the page\'s own (counted, apart from correlation()s) — resolved by the router\'s rendezvous named by it (4 bytes, little-endian), each ask by its own; the subscription ending first rejects', async () => {
  const sent = [], client = newClient(sent);
  client.subscribe({ tapId: 5 }, () => {});
  client.subscribe({ tapId: 6 }, () => {});
  const first = client.sync(5), second = client.sync(5), third = client.sync(6);
  assert.deepEqual(sent.filter(a => a[0] === 'Tap.Sync'), [['Tap.Sync', 3, 77, 5, 1], ['Tap.Sync', 3, 77, 5, 2], ['Tap.Sync', 3, 77, 6, 3]]);
  const rendezvous = (tap, seq, id) => {
    const r = new Uint8Array(19), dv = new DataView(r.buffer);
    r[0] = 0x0b; dv.setUint32(1, tap, true); dv.setBigInt64(6, BigInt(seq), true); r[14] = 4; dv.setUint32(15, id, true);
    return r;
  };
  client.receive(1, rendezvous(5, 12, 2));
  assert.deepEqual(await second, { tapId: 5, seq: 12, missed: false }, 'the second ask answered: up to date as of it');
  let firstDone = false;
  first.then(() => { firstDone = true; });
  await new Promise(r => setTimeout(r, 0));
  assert.equal(firstDone, false, 'the first not yet: its own rendezvous');
  client.receive(2, rendezvous(5, 9, 1));
  assert.deepEqual(await first, { tapId: 5, seq: 9, missed: false });
  const ended = third.then(() => 'resolved', () => 'rejected');
  client.drop(6);
  assert.equal(await ended, 'rejected', 'its subscription ended first: rejected');
});

test('sync against a router that says it doesn\'t take Tap.Sync (its RouterChange\'s controls): rejected at once, nothing sent; one that does: sent', async () => {
  const sent = [], client = newClient(sent);
  client.subscribe({ tapId: 5 }, () => {});
  const heartbeat = (controls, correlation) => {
    const f = new Uint8Array(13 + 22), dv = new DataView(f.buffer);
    f[0] = 0x01; dv.setUint32(1, 1, true); f[13] = 0x0d; f[14] = 0; f.fill(correlation, 15, 31); dv.setUint32(31, controls, true);
    return f;
  };
  client.receive(1, heartbeat(0x7f, 1));                                    // Tap.Activate … Tap.Auth: no Tap.Sync
  await assert.rejects(client.sync(5), /doesn't take Tap\.Sync/);
  assert.equal(sent.filter(a => a[0] === 'Tap.Sync').length, 0, 'nothing sent');
  client.receive(2, heartbeat(0xff, 2));                                    // a router that does
  client.sync(5).catch(() => {});
  assert.equal(sent.filter(a => a[0] === 'Tap.Sync').length, 1, 'sent');
});

test('rendezvous: awaited by its correlation id before the request — resolved by its record, after the records before it in the frame; missed said so; one nobody awaits to onRendezvous; the subscription ending first, or an abort, rejects; correlation() is a UUIDv7, its time read back', async () => {
  const client = newClient([]);
  const order = [], unawaited = [];
  client.onRendezvous = (id, info) => unawaited.push([id, info]);
  client.subscribe({ tapId: 5 }, () => {}, undefined, m => order.push(`message ${m[0]}`));
  client.subscribe({ tapId: 6 }, () => {});
  const before = Date.now(), id = client.correlation(), other = client.correlation(), after = Date.now();
  assert.match(id, /^[0-9a-f]{12}7[0-9a-f]{3}[89ab][0-9a-f]{15}$/, 'a UUIDv7, hex');
  assert.notEqual(id, other);
  const made = client.correlationTime(id);
  assert.ok(made >= before && made <= after, 'its time: when it was made');
  assert.equal(client.correlationTime(id.toUpperCase()), made);
  assert.equal(client.correlationTime('abcd'), undefined, 'another kind of id: no time');
  const done = client.rendezvous(id, { tapId: 5 }).then(info => { order.push('rendezvous'); return info; });
  assert.equal(client.rendezvous(id), client.rendezvous(id), 'awaited again: the same promise');
  const message = (tap, b, seq) => { const r = new Uint8Array(19), dv = new DataView(r.buffer); r[0] = 0x09; dv.setUint32(1, tap, true); dv.setBigInt64(6, BigInt(seq), true); dv.setUint32(14, 1, true); r[18] = b; return r; };
  const rendezvous = (tap, flags, seq, idHex) => {
    const c = Buffer.from(idHex, 'hex'), r = new Uint8Array(15 + c.length), dv = new DataView(r.buffer);
    r[0] = 0x0b; dv.setUint32(1, tap, true); r[5] = flags; dv.setBigInt64(6, BigInt(seq), true); r[14] = c.length; r.set(c, 15);
    return r;
  };
  client.receive(1, Uint8Array.from([...message(5, 7, 41), ...rendezvous(5, 0, 41, id), ...rendezvous(5, 1, 9, 'abcd')]));
  assert.deepEqual(await done, { tapId: 5, seq: 41, missed: false });
  assert.deepEqual(order, ['message 7', 'rendezvous'], 'the message handed over, then the rendezvous resolved');
  assert.deepEqual(unawaited, [['abcd', { tapId: 5, seq: 9, missed: true }]], 'one nobody awaited (missed: never sent to it)');

  const ended = client.rendezvous(other, { tapId: 6 });
  const ending = Uint8Array.from([0x0a, 0, 0, 6, 0, 0, 0, 6, 0, 0, 0]);
  new DataView(ending.buffer).setUint16(1, FaultCode.Overflowed, true);
  client.receive(2, ending);
  await assert.rejects(ended, e => e.code === FaultCode.Overflowed && e.tapId === 6, 'its subscription ended first: rejected');
  const aborts = new AbortController(), aborted = client.rendezvous(client.correlation(), { signal: aborts.signal });
  aborts.abort();
  await assert.rejects(aborted);
  assert.equal(client.rendezvousWaits.size, 0, 'nothing left awaited');
});

test('sign-in: Begin as C# writes it; the provider\'s message answered by next() (Continue); signed in — identity, ticket, onIdentity — each record taken once (resent: ignored); refused; bound to another outlet, the identity lost and the ticket presented again; the context', async () => {
  const sent = [], client = newClient(sent), a = fx.auth;
  const lastAuth = () => sent.filter(s => s[0] === 'Tap.Auth').at(-1);
  const identities = [];
  client.onIdentity = i => identities.push(i);
  assert.equal(new TextDecoder().decode(client.signInContext('north')), a.context, 'the context: as the router gives it');
  const asked = [];
  const signedIn = client.signIn('north', 'passthrough', 'alice', { next: async m => { asked.push([...m]); return Uint8Array.from([4, 5, 6]); } });
  assert.deepEqual(sent.map(s => s[0]), ['Tap.Auth']);
  assert.deepEqual(sent[0].slice(1, 3), [3, 77]);
  assert.equal(toHex(sent[0][3]), a.beginHex, 'Begin: as C# writes it');
  client.receive(1, hex(a.continuedHex));
  client.receive(2, hex(a.continuedHex));                        // resent (not yet acknowledged): once
  await new Promise(r => setTimeout(r, 0));
  assert.deepEqual(asked, [[9, 8, 7]], 'next() asked once, with the provider\'s message');
  assert.equal(toHex(sent.filter(s => s[0] === 'Tap.Auth')[1][3]), a.continueHex, 'Continue: as C# writes it');
  client.receive(3, hex(a.acceptedHex));
  const result = await signedIn;
  assert.equal(result.accepted, true);
  const { renewal, warnAt, ...who } = result.identity;
  assert.equal(renewal, 'silent', 'its renewal policy');
  assert.equal(Date.parse(warnAt), 1790028800000 - 5 * 60000, 'when the router will warn');
  assert.deepEqual({ ...who, claims: { ...who.claims } }, {
    realm: 'north', userId: 'alice', sessionId: '0198d6a2-7c41-7e3b-9f10-2b4c6d8e0a13', module: 'passthrough', insecure: true,
    authenticatedAt: new Date(1790000000000).toISOString().replace('.000Z', '+00:00'), expiresAt: new Date(1790028800000).toISOString().replace('.000Z', '+00:00'),
    claims: { role: 'é' } });
  assert.deepEqual([...result.ticket], [0xaa, 0xbb]);
  assert.equal(client.identity, result.identity);
  client.receive(4, hex(a.acceptedHex));                         // resent: taken once
  assert.equal(identities.length, 1, 'onIdentity once');

  const refused = client.signIn('south', 'passthrough', 'alice');
  const exchange = new DataView(lastAuth()[3].buffer).getUint32(1, true);
  const r = hex(a.refusedHex); new DataView(r.buffer).setUint32(2, exchange, true);
  client.receive(5, r);
  assert.deepEqual(await refused, { accepted: false, identity: null, ticket: null, reason: "No sign-in to 'south' here." });
  assert.equal(client.identity.userId, 'alice', 'a refusal changes nothing');

  const curt = client.signIn('north', 'passthrough', 'bob');    // no next(): asked for more, it gives up
  const c = hex(a.continuedHex); new DataView(c.buffer).setUint32(2, new DataView(lastAuth()[3].buffer).getUint32(1, true), true);
  client.receive(6, c);
  assert.equal((await curt).accepted, false);
  assert.equal(lastAuth()[3][0], 3, 'Cancel sent');

  const before = sent.length;
  client.bind(4, 99);                                            // another outlet: the session (and the identity) gone
  assert.equal(client.identity, null);
  assert.equal(identities.at(-1), null, 'onIdentity(null)');
  const resume = sent.slice(before).find(s => s[0] === 'Tap.Auth');
  assert.ok(resume, 'the ticket presented again');
  const m = resume[3];
  assert.deepEqual(resume.slice(1, 3), [4, 99]);
  assert.equal(new TextDecoder().decode(m.subarray(7, 7 + m[6])), 'north');
  assert.equal(new TextDecoder().decode(m.subarray(8 + m[6], 8 + m[6] + m[7 + m[6]])), 'ticket');
  assert.deepEqual([...m.subarray(8 + m[6] + m[7 + m[6]])], [0xaa, 0xbb]);
  const aborts = new AbortController(), aborted = client.signIn('north', 'passthrough', 'carol', { signal: aborts.signal });
  aborts.abort();
  await assert.rejects(aborted);
  assert.equal(lastAuth()[3][0], 3, 'aborted: Cancel sent');
});

test('session: warned ahead (once; silent — renewed with the ticket by itself, a renewal Begin), a fixed one only told; ended — identity gone, onSessionEnded, onIdentity(null); a renewal redirected — resolved with the router and ticket, onRedirect', async () => {
  const sent = [], client = newClient(sent), a = fx.auth;
  const lastAuth = () => sent.filter(s => s[0] === 'Tap.Auth').at(-1);
  const signedIn = client.signIn('north', 'passthrough', 'alice');
  client.receive(1, hex(a.acceptedHex));
  await signedIn;
  const warned = [], ended = [], identities = [], redirects = [];
  client.onSessionExpiring = n => warned.push(n);
  client.onSessionEnded = n => ended.push(n);
  client.onIdentity = i => identities.push(i);
  client.onRedirect = (router, ticket) => redirects.push([router, [...ticket]]);
  const auths = sent.filter(s => s[0] === 'Tap.Auth').length;
  client.receive(2, hex(a.expiringHex));
  client.receive(3, hex(a.expiringHex));                         // resent: once
  assert.equal(warned.length, 1);
  assert.deepEqual({ ...warned[0], correlationId: undefined }, { correlationId: undefined, sessionId: '0198d6a2-7c41-7e3b-9f10-2b4c6d8e0a13',
    expiresAt: new Date(1790028800000), renewal: 'silent', reason: 'é' });
  const renewal = lastAuth()[3];
  assert.equal(sent.filter(s => s[0] === 'Tap.Auth').length, auths + 1, 'silent: renewed by itself');
  assert.equal(renewal[0], 1);
  assert.equal(renewal[5], 1, 'a renewal');
  assert.equal(new TextDecoder().decode(renewal.subarray(8 + renewal[6], 8 + renewal[6] + renewal[7 + renewal[6]])), 'ticket');
  const r = hex(a.redirectedHex); new DataView(r.buffer).setUint32(2, new DataView(renewal.buffer).getUint32(1, true), true);
  client.receive(4, r);
  assert.deepEqual(redirects, [['router-b', [0xcc]]], 'the renewal redirected: onRedirect');
  client.receive(5, hex(a.fixedHex));
  assert.equal(warned.length, 2);
  assert.equal(warned[1].renewal, 'fixed');
  assert.equal(sent.filter(s => s[0] === 'Tap.Auth').length, auths + 1, 'fixed: not renewed');
  client.receive(6, hex(a.endedHex));
  client.receive(7, hex(a.endedHex));
  assert.equal(ended.length, 1, 'ended once');
  assert.equal(client.identity, null);
  assert.deepEqual(identities, [null]);
  const late = hex(a.expiringHex); late[3] ^= 1;                // a warning arriving after its end: nobody to warn
  client.receive(8, late);
  assert.equal(warned.length, 2);
});

test('passkeys: signInWithPasskey and registerPasskey — the module\'s options (JSON, base64url) handed to navigator.credentials as bytes, the browser\'s answer sent back as JSON, base64url', async () => {
  const sent = [], client = newClient(sent);
  const lastAuth = () => sent.filter(s => s[0] === 'Tap.Auth').at(-1)[3];
  const record = (exchange, round, json) => {                      // the router's continue: the module's options
    const text = new TextEncoder().encode(json), r = new Uint8Array(14 + text.length), dv = new DataView(r.buffer);
    r[0] = 0x0e; r[1] = 0; dv.setUint32(2, exchange, true); dv.setUint16(6, round, true); dv.setUint16(8, 0, true);
    dv.setUint32(10, text.length, true); r.set(text, 14);
    return r;
  };
  const asked = [];
  const credentials = {
    get: async o => { asked.push(o); return { rawId: Uint8Array.from([1, 2, 3]).buffer, response: { clientDataJSON: Uint8Array.from([0xfb, 0xff]).buffer, authenticatorData: Uint8Array.from([9]).buffer, signature: Uint8Array.from([7, 7]).buffer, userHandle: Uint8Array.from([5]).buffer } }; },
    create: async o => { asked.push(o); return { rawId: Uint8Array.from([4]).buffer, response: { clientDataJSON: Uint8Array.from([1]).buffer, attestationObject: Uint8Array.from([0xa0]).buffer, getTransports: () => ['internal'] } }; },
  };
  const signedIn = client.signInWithPasskey('north', { userId: 'alice', credentials });
  let begin = lastAuth();
  assert.equal(new TextDecoder().decode(begin.subarray(8 + begin[6], 8 + begin[6] + begin[7 + begin[6]])), 'passkey');
  assert.equal(new TextDecoder().decode(begin.subarray(8 + begin[6] + begin[7 + begin[6]])), '{"userId":"alice"}');
  const exchange = new DataView(begin.buffer).getUint32(1, true);
  client.receive(1, record(exchange, 0, '{"challenge":"AAEC_w","rpId":"app.test","timeout":60000,"userVerification":"preferred","allowCredentials":[{"type":"public-key","id":"AQID","transports":["internal"]}]}'));
  await new Promise(r => setTimeout(r, 0));
  const pk = asked[0].publicKey;
  assert.deepEqual([...pk.challenge], [0, 1, 2, 255], 'the challenge, as bytes');
  assert.equal(pk.rpId, 'app.test');
  assert.deepEqual([...pk.allowCredentials[0].id], [1, 2, 3]);
  assert.deepEqual(pk.allowCredentials[0].transports, ['internal']);
  const answer = lastAuth();
  assert.equal(answer[0], 2, 'a Continue');
  assert.deepEqual(JSON.parse(new TextDecoder().decode(answer.subarray(5))), { id: 'AQID', clientDataJSON: '-_8', authenticatorData: 'CQ', signature: 'Bwc', userHandle: 'BQ' });

  client.registerPasskey('north', { enrollment: 'invite-alice', credentials });
  begin = lastAuth();
  assert.equal(new TextDecoder().decode(begin.subarray(8 + begin[6] + begin[7 + begin[6]])), 'invite-alice');
  client.receive(2, record(new DataView(begin.buffer).getUint32(1, true), 0, '{"challenge":"AA","rp":{"id":"app.test","name":"App"},"user":{"id":"BQ","name":"alice","displayName":"Alice"},"pubKeyCredParams":[{"type":"public-key","alg":-7}],"timeout":60000,"attestation":"none","authenticatorSelection":{"residentKey":"preferred"},"excludeCredentials":[]}'));
  await new Promise(r => setTimeout(r, 0));
  const create = asked[1].publicKey;
  assert.deepEqual([...create.user.id], [5]);
  assert.equal(create.user.name, 'alice');
  assert.equal(create.attestation, 'none');
  assert.deepEqual(JSON.parse(new TextDecoder().decode(lastAuth().subarray(5))), { id: 'BA', clientDataJSON: 'AQ', attestationObject: 'oA', transports: ['internal'] });
  void signedIn;
});

test('password sign-in by OPAQUE (tapline-opaque.mjs on the real opaque.wasm; the server\'s steps played here): registered by an invitation, signed in — the messages framed u16 length | id, bound to the connection\'s context; a wrong password refused at the client, which gives up', async () => {
  const o = await loadOpaque(readFileSync(new URL('../../src/Tapline.Authentication.Opaque/wwwroot/opaque.wasm', import.meta.url)));
  const sent = [], client = newClient(sent), setup = o.serverSetup();
  const auths = () => sent.filter(s => s[0] === 'Tap.Auth').map(s => s[3]);
  const next = async count => { for (let i = 0; i < 400 && auths().length < count; i++) await new Promise(r => setTimeout(r, 5)); return auths().at(-1); };
  const record = (status, exchange, round, text, payload) => {
    const tx = new TextEncoder().encode(text), r = new Uint8Array(14 + tx.length + payload.length), dv = new DataView(r.buffer);
    r[0] = 0x0e; r[1] = status; dv.setUint32(2, exchange, true); dv.setUint16(6, round, true); dv.setUint16(8, tx.length, true);
    r.set(tx, 10); dv.setUint32(10 + tx.length, payload.length, true); r.set(payload, 14 + tx.length);
    return r;
  };
  const body = m => { const p = m.subarray(8 + m[6] + m[7 + m[6]]), n = new DataView(p.buffer, p.byteOffset).getUint16(0, true); return [p.subarray(2, 2 + n), p.subarray(2 + n)]; };
  const exchangeOf = m => new DataView(m.buffer, m.byteOffset).getUint32(1, true);
  const identity = JSON.stringify({ realm: 'north', userId: 'alice', sessionId: '0198d6a2-7c41-7e3b-9f10-2b4c6d8e0a13', module: 'opaque', insecure: false, authenticatedAt: '2026-10-20T00:00:00+00:00', expiresAt: null });
  let seq = 0;

  // Registration: the invitation and the request; the server's response framed with her id; the upload.
  const registering = registerPassword(client, 'north', 'correct horse', { enrollment: 'invite-alice', module: o });
  let begin = await next(1);
  const [token, request] = body(begin);
  assert.equal(new TextDecoder().decode(token), 'invite-alice');
  const id = new TextEncoder().encode('alice');
  const response = o.serverRegistrationStart(setup, request, id);
  const framed = new Uint8Array(2 + id.length + response.length); new DataView(framed.buffer).setUint16(0, id.length, true); framed.set(id, 2); framed.set(response, 7);
  client.receive(++seq, record(0, exchangeOf(begin), 0, '', framed));
  const upload = (await next(2)).subarray(5);
  const file = o.serverRegistrationFinish(upload);
  assert.equal(file.length, 192, 'a password file');
  client.receive(++seq, record(1, exchangeOf(begin), 1, identity, new Uint8Array([1])));
  assert.equal((await registering).accepted, true);

  // Login: id | KE1; KE2 bound to this connection's context; KE3 checked by the server.
  const context = client.signInContext('north');
  const login = async (password, count) => {
    const signing = signInWithPassword(client, 'north', 'alice', password, { module: o });
    const m = await next(count);
    const [who, ke1] = body(m);
    assert.equal(new TextDecoder().decode(who), 'alice');
    const server = o.serverLoginStart(setup, file, ke1, who, context);
    client.receive(++seq, record(0, exchangeOf(m), 0, '', server.response));
    const reply = await next(count + 1);
    return { signing, reply, server, exchange: exchangeOf(m) };
  };
  const ok = await login('correct horse', 3);
  assert.equal(ok.reply[0], 2, 'a Continue: KE3');
  assert.equal(o.serverLoginFinish(ok.server.state, ok.reply.subarray(5), context).length, 64, 'the server agrees: a session key');
  client.receive(++seq, record(1, ok.exchange, 1, identity, new Uint8Array([2])));
  assert.equal((await ok.signing).identity.userId, 'alice');

  const bad = await login('wrong horse', 5);
  assert.equal(bad.reply[0], 3, 'a wrong password: the client gives up (Cancel)');
  assert.deepEqual(await bad.signing, { accepted: false, identity: null, ticket: null, reason: 'Wrong user id or password.' });
});

for (const [name, fn] of tests) { await fn(); passed++; console.log(`PASS  ${name}`); }
console.log(`\nAll ${passed} JS tests passed.`);
