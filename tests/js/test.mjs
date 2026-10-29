// Verifies tapline.js against frames produced by the C# library (run the C# tests first to write the fixtures).
// Node's own modules only: nothing to install. (The SignalR JS client's side of the wire is held to captured bytes by
// the C# tests: fixtures/signalr.json.)
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { TaplineClient, rowHash, FaultCode, BlobRef, StaleData } from '../../src/Tapline.AspNetCore/wwwroot/tapline.js';
import { loadOpaque } from '../../src/Tapline.Authentication.Opaque/wwwroot/opaque.mjs';
import { signInWithPassword, registerPassword } from '../../src/Tapline.Authentication.Opaque/wwwroot/tapline-opaque.mjs';
import { startBlazor } from '../../src/Tapline.BlazorRealtime/wwwroot/blazor-realtime.js';
import { Scroller, Prefetch, Velocity, leadFor, racingUp, windowFor, Requests, readOrder, placeRuns, readStatus, statusParts, statusText, keysByKey, compareKeys, formatCell, formatInteger, formatDecimal, formatDouble, formatTime, formatGuid, figuredText, cultureOf, INVARIANT, SAFE_HEIGHT,
  stampOf, flashDelay, cssMs, twoFigures, moved, staleAfter, keyTime, stampsTrusted, arrivedAbove,
  lagShift, flashFrom, inPixels, columnEnds } from '../../src/Tapline.Grid.Blazor/wwwroot/tapline-grid.js';

import { readRow, readShape, guidOf, ROW, SHAPE, AlertInbox } from '../../src/Tapline.Alerts/wwwroot/tapline-alerts.js';
const fx = JSON.parse(readFileSync(new URL('./fixtures/frames.json', import.meta.url)));
const hex = s => Uint8Array.from(Buffer.from(s, 'hex'));
const toHex = b => Buffer.from(b).toString('hex').toUpperCase();
/** A RouterChange record's length (it rides behind a heartbeat): by its state — draining's grace, degraded's reason. */
const routerChangeLength = (fr, o) => 22 + (fr[o + 1] === 2 ? 4 : fr[o + 1] === 1 ? 1 + fr[o + 22] : 0);
const tableLength = (dv, o) => {          // (38 bytes of header; each entry its op and key, an upsert its row too)
  const n = dv.getUint32(o + 26, true);
  return 38 + n * (1 + dv.getUint16(o + 30, true)) + (n - dv.getUint32(o + 34, true)) * dv.getUint16(o + 32, true);
};
let passed = 0;
const tests = [];
const test = (name, fn) => tests.push([name, fn]);

function newClient(sent) {
  const client = new TaplineClient();
  client.bind(3, 77);
  client.connection = { send: (...a) => { sent.push(a); return Promise.resolve(); } };
  return client;
}

test('alerts read as .NET lays them out (fixtures/frames.json "alerts", by the C# tests): an inbox row — how far presented, past 2³², muted, its source, name and sound — and a shape\'s value — its count, when it fired, its latest key, a line cut and marked so — and a rule\'s key, a .NET Guid', () => {
  const a = fx.alerts, bytes = text => new Uint8Array(Buffer.from(text, 'base64'));
  const row = bytes(a.row), shape = bytes(a.shape);
  assert.deepEqual([row.length, shape.length], [ROW.size, SHAPE.size], 'the sizes .NET packs them to');
  assert.deepEqual(readRow(row), { presented: a.presented, muted: true, source: 'fills', name: a.name, sound: 'kaching' });
  assert.deepEqual(readShape(shape), { fired: a.fired, firedAt: a.firedAt, key: a.rule, summary: a.summary });
  assert.equal(guidOf(bytes(a.key)), a.rule);
});

test('an audience\'s alerts (AlertInbox): each rule\'s shape past what its audience presented, presented once and said back — sounded unless muted; away if its shape last fired before the page opened, gathered in one line; a rule\'s shape subscribed again only when its tap changes; another page\'s presenting honoured', async () => {
  const guid = id => { const h = id.replace(/-/g, ''), b = i => parseInt(h.slice(2 * i, 2 * i + 2), 16);
    return Uint8Array.from([3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15].map(b)); };
  const text = (bytes, at, value) => { const utf8 = new TextEncoder().encode(value); bytes[at] = utf8.length; bytes.set(utf8, at + 1); };
  const row = (presented, muted, name) => { const b = new Uint8Array(ROW.size), dv = new DataView(b.buffer);
    dv.setBigInt64(ROW.presented, BigInt(presented), true); b[ROW.muted] = muted ? 1 : 0;
    text(b, ROW.source, 'orders'); text(b, ROW.name, name); text(b, ROW.sound, 'kaching'); return b; };
  const shape = (fired, firedAtMs, summary = 'FERN Buy 1 BTC') => { const b = new Uint8Array(SHAPE.size), dv = new DataView(b.buffer);
    dv.setBigInt64(SHAPE.fired, BigInt(fired), true); dv.setBigInt64(SHAPE.firedAt, BigInt(firedAtMs * 1000), true);
    text(b, SHAPE.summary, summary); return b; };
  const r1 = '01a18356-af60-7000-86cf-7ea6c6c82c20', r2 = '01a18356-af60-7000-86cf-7ea6c6c82c21';
  const inbox = rows => ({ members: [{ rows: new Map(rows.map(([id, bytes], i) => [i, { key: guid(id), row: bytes }])) }] });
  const handlers = new Map(), activated = [];
  const tapline = { subscribe(binding, onState) { const id = binding.tapId; activated.push(id); handlers.set(id, onState); return () => handlers.delete(id); } };
  const said = { alerts: [], played: [], presented: [], away: [] };
  const since = 1_793_000_000_000;
  globalThis.document = { hidden: false, addEventListener() {}, removeEventListener() {} };
  try {
    const box = new AlertInbox({ inbox: { tapId: 1 }, shapes: { [r1]: { tapId: 2 }, [r2]: { tapId: 3 } }, since, tapline, soundOn: true,
      alert: a => said.alerts.push([a.name, a.count, a.away]), play: s => said.played.push(s), presented: (r, n) => said.presented.push([r, n]),
      away: list => said.away.push(list.map(a => [a.name, a.count])) });
    handlers.get(1)(inbox([[r1, row(10, false, 'Filled')], [r2, row(0, true, 'Quiet')]]));
    assert.deepEqual(said.alerts, [], 'rows alone: nothing to present');
    handlers.get(2)(shape(15, since - 1000));
    handlers.get(3)(shape(0, 0));
    handlers.get(2)(shape(15, since - 1000));
    handlers.get(2)(shape(18, since + 500));
    handlers.get(3)(shape(2, since + 600));
    assert.deepEqual(said.alerts, [['Filled', 5, true], ['Filled', 3, false], ['Quiet', 2, false]], 'what fired past the presented: away before the page opened, once each');
    assert.deepEqual(said.played, ['kaching', 'kaching'], 'sounded, but not the muted rule');
    assert.deepEqual(said.presented, [[r1, 15], [r1, 18], [r2, 2]], 'each presented said back, as far as its shape\'s count');
    await new Promise(r => setTimeout(r, 300));
    assert.deepEqual(said.away, [[['Filled', 5]]], 'what fired while away, gathered in one line');
    box.setShapes({ [r1]: { tapId: 2 }, [r2]: { tapId: 4 } });
    assert.deepEqual(activated, [1, 2, 3, 4], 'the rule whose tap changed subscribed again; the other kept');
    assert.deepEqual([...handlers.keys()].sort(), [1, 2, 4], 'the old tap let go');
    handlers.get(1)(inbox([[r1, row(20, false, 'Filled')], [r2, row(2, true, 'Quiet')]]));
    handlers.get(2)(shape(20, since + 900));
    handlers.get(2)(shape(21, since + 950));
    assert.deepEqual(said.alerts.slice(3), [['Filled', 1, false]], 'presented by another page of the audience: from there');
    box.dispose();
    assert.equal(handlers.size, 0, 'disposed: nothing left subscribed');
  } finally {
    delete globalThis.document;
  }
});

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
        const n = dv.getUint32(o + 26, true);
        if (!(fr[o + 5] & 1) && n > 0 && fr[o + 38] === 0) { fr[o + 38 + 1 + 9] ^= 1; target = f; break outer; }   // flip a row bit of an upsert
        o += tableLength(dv, o);
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
  fr[o + 38 + 1 + 9] ^= 1;                                       // flip a row bit
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
 * null: a delete, its key alone) applied to `model` (the server's rows, key → row), whose checksum it carries —
 * `corrupt`: a wrong one.
 */
function tableRecord(id, { reset = false, from, to, entries, model, corrupt = false }) {
  const K = 2, R = 1, deletes = entries.filter(([, row]) => row === null).length;
  const rec = new Uint8Array(38 + entries.length * (1 + K) + (entries.length - deletes) * R), dv = new DataView(rec.buffer);
  rec[0] = 0x04;
  dv.setUint32(1, id, true);
  rec[5] = reset ? 1 : 0;
  dv.setBigInt64(6, BigInt(from), true);
  dv.setBigInt64(14, BigInt(to), true);
  if (reset) model.clear();
  let p = 38;
  for (const [key, row] of entries) {
    rec[p] = row === null ? 1 : 0;
    rec.set(Buffer.from(key, 'latin1'), p + 1);
    if (row === null) {
      model.delete(key);
      p += 1 + K;
    } else {
      model.set(key, rec[p + 1 + K] = row);
      p += 1 + K + R;
    }
  }
  let sum = 0;
  for (const [key, row] of model) sum = (sum + rowHash(Buffer.from(key, 'latin1'), 0, K, Uint8Array.of(row), 0, R)) >>> 0;
  dv.setUint32(22, corrupt ? sum ^ 1 : sum, true);
  dv.setUint32(26, entries.length, true);
  dv.setUint16(30, K, true);
  dv.setUint16(32, R, true);
  dv.setUint32(34, deletes, true);
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
      if (t === 6) { fr[o + 12 + 38 + 1 + 8] ^= 1; target = f; break outer; }   // first member's first row byte
      o += t === 1 ? 13 : t === 0x0d ? routerChangeLength(fr, o) : t === 3 ? 7 + dv.getUint16(o + 5, true)
         : t === 4 ? tableLength(dv, o)
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

test('the connection\'s server timeout follows the path: the floor on a fast one; on a slow one (gaps past 1.5 s) SignalR\'s default, or four times the longest recent gap if more, up to that default; back as it eases; staleMs', () => {
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
  t += 1000; client.receive(0, heartbeat);                 // a 2 s gap: a slow path — a message there may take longer than the floor
  assert.equal(connection.serverTimeoutInMilliseconds, 30000, 'a slow path: SignalR\'s default, not four times the gap (8 s)');
  every(1000, t + 120000);
  assert.equal(connection.serverTimeoutInMilliseconds, 10000, 'eased: back to the floor');
  t += 6000; client.receive(0, heartbeat);                 // a 7 s gap: a slow path (or a large message on one)
  assert.equal(connection.serverTimeoutInMilliseconds, 30000, 'four times the gap (28 s), within SignalR\'s default');
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
    const K = 9, R = 8, r = new Uint8Array(38 + rows.length * (1 + K + R)), dv = new DataView(r.buffer);
    r[0] = 0x04; dv.setUint32(1, id, true); r[5] = 0x01; dv.setUint32(26, rows.length, true); dv.setUint16(30, K, true); dv.setUint16(32, R, true);
    let sum = 0;
    rows.forEach(([side, price, size], i) => {
      const o = 38 + i * (1 + K + R);
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

// ───────────── tapline-grid.js: the page body's arithmetic ─────────────

test('grid scrolling: native and exact under the safe height; above it, scaled — the thumb by its fraction (90 % shows 90 %, the bottom the last row), wheel and keys by rows, the row standing through the browser\'s rounding of its own scroll position', () => {
  const native = new Scroller(30);
  native.resize(600);
  native.setTotal(1_000);
  assert.equal(native.scaled, false);
  assert.equal(native.height, 30_000);
  assert.equal(native.scrolled(3_000), 100, 'native: a row each 30 px');
  assert.equal(native.by(5), 3_150, 'by rows: the scroll position follows');
  assert.equal(native.scrolled(3_150), 105, 'its own scroll event: the row stands');
  assert.equal(native.to(5_000), (1_000 - 20) * 30, 'no further than the last row at the bottom');

  const edge = new Scroller(30, 300_000);
  edge.resize(600);
  edge.setTotal(10_000);
  assert.equal(edge.scaled, false, 'exactly the safe height: native');
  edge.setTotal(10_001);
  assert.equal(edge.scaled, true, 'a row more: scaled');

  const big = new Scroller(30);
  big.resize(600);
  big.setTotal(50_000_000);
  assert.equal(big.scaled, true);
  assert.equal(big.height, SAFE_HEIGHT);
  const range = SAFE_HEIGHT - 600, last = 50_000_000 - 20;
  assert.ok(Math.abs(big.scrolled(range * 0.9) - last * 0.9) < 1, 'the thumb at 90 %: 90 % of the way');
  assert.equal(big.scrolled(range), last, 'the thumb at the bottom: the last row in view');
  const at = big.scrolled(range / 2);
  const top = big.by(3);
  assert.equal(big.row, at + 3, 'a wheel of three rows: three rows, though a pixel is ~187 of them here');
  assert.equal(big.scrolled(Math.round(top)), at + 3, 'the browser rounding its own position: the row stands');
  assert.ok(Math.abs(big.scrolled(range / 4) - last / 4) < 1, 'the reader\'s own (a thumb): by its fraction again');
  big.setTotal(10);
  assert.equal(big.row, 0, 'fewer rows than the view: the top');
});

test('grid columns in pixels, as WebKit zoomed needs them (it scales a ch apart from its text, and a registered length at each sum): a width\'s ch, em and rem written as pixels from the font\'s measures, the rest left to the browser; the places each column\'s end and its gutter, one given no room standing where the one before it ends', () => {
    const units = {ch: 9.890625, em: 16, rem: 18};
    assert.equal(inPixels('14ch', units), '138.46875px');
    assert.equal(inPixels('calc(14ch + 2 * 1.25em)', units), 'calc(138.46875px + 2 * 20px)');
    assert.equal(inPixels('2rem', units), '36px');
    assert.equal(inPixels('.5em', units), '8px');
    assert.equal(inPixels('9EM', units), '144px');
    assert.equal(inPixels('120px', units), '120px');
    const runs = {ch: n => n === 6 ? 44.75 : n * 7.418, em: 12};   // (a ch as so many characters run: six a hair wider than six shares)
    assert.equal(inPixels('6ch', runs), '44.75px');
    assert.equal(inPixels('calc(6ch + 2 * 1.25em)', runs), 'calc(44.75px + 2 * 15px)');
    assert.deepEqual(columnEnds([100, 50, undefined, 20], 16), [116, 182, 182, 218]);
    assert.deepEqual(columnEnds([], 16), []);
});

test('grid pre-fetch: a view ahead and two thirds behind at full size, starting at a quarter; up below 150 ms (the median of the last three), down above 500 ms, each on its boundary staying; the first answer after a step unjudged; a size left for being slow held back 30 s, doubling to 5 min', () => {
  let now = 0;
  const p = new Prefetch(() => now);
  p.see(24);
  assert.deepEqual([p.ahead, p.behind], [6, 4], 'a quarter: 6 ahead, 4 behind');
  for (const t of [150, 150]) assert.equal(p.observe(t), false);
  assert.equal(p.observe(150), false, '150 ms: not fast, stays');
  assert.equal(p.observe(149), false, '[150, 150, 149]: the median 150, stays');
  assert.equal(p.observe(149), true, '[150, 149, 149]: 149, up');
  assert.deepEqual([p.ahead, p.behind], [12, 8], 'a half');
  assert.equal(p.observe(10_000), false, 'the first after a step: not judged');
  for (const t of [500, 500]) assert.equal(p.observe(t), false);
  assert.equal(p.observe(500), false, '500 ms: not slow, stays');
  assert.equal(p.observe(501), false, '[500, 500, 501]: 500, stays');
  now = 1_000;
  assert.equal(p.observe(501), true, '[500, 501, 501]: 501, down');
  assert.deepEqual([p.ahead, p.behind], [6, 4], 'a quarter again');

  // The half held back 30 s from leaving it: fast answers a millisecond before, no; on it, up.
  p.observe(1);                                    // (unjudged: the step's)
  now = 1_000 + 30_000 - 1;
  p.observe(1); p.observe(1);
  assert.equal(p.observe(1), false, 'a millisecond before its hold-back ends: not tried again');
  now = 1_000 + 30_000;
  assert.equal(p.observe(1), true, 'on it: tried again');
  for (const [left, held] of [[1, 60_000], [2, 120_000], [3, 240_000], [4, 300_000], [5, 300_000]]) {
    p.observe(1);                                  // (unjudged)
    p.observe(900); p.observe(900);
    now += 1;
    assert.equal(p.observe(900), true, `left for being slow (${left})`);
    const from = now;
    p.observe(1);
    p.observe(1); p.observe(1);
    now = from + held - 1;
    assert.equal(p.observe(1), false, `held back ${held / 1000} s: a millisecond before, not`);
    now = from + held;
    assert.equal(p.observe(1), true, `held back ${held / 1000} s, doubling to five minutes: on it, up`);
  }

  // Full size: a view ahead, two thirds behind.
  p.observe(1);
  p.observe(1); p.observe(1); p.observe(1);
  assert.equal(p.step, 0);
  assert.deepEqual([p.ahead, p.behind], [24, 16]);
});

test('grid velocity (as mip-chart projects a pan): the lead ahead is the fling\'s peak speed × 325 ms, smoothed (EWMA 0.45); a pause of 120 ms ends the fling (a millisecond less doesn\'t); turning back forgets the peak; rows let in above move its samples, not the reader', () => {
  const v = new Velocity();
  v.sample(0, 0);
  v.sample(10, 10);                                  // 1 row/ms
  assert.ok(Math.abs(v.speed - 0.45) < 1e-9 && Math.abs(v.reach - 0.45 * 325) < 1e-9, 'one sample: 0.45 of its speed');
  v.sample(20, 20);
  assert.ok(Math.abs(v.speed - (0.45 + 0.55 * 0.45)) < 1e-9, 'smoothed');
  const peak = v.peak;
  v.sample(21, 30);                                  // slowing: the peak stands
  assert.equal(v.peak, peak, 'slowing: the peak kept (the whole swipe, not its tail)');
  assert.ok(v.reach > 0, 'down: ahead below');
  v.sample(22, 30 + 119);
  assert.ok(v.peak > 0, 'a pause of 119 ms: still the fling');
  v.sample(23, 149 + 120 + 1);
  assert.deepEqual([v.speed, v.peak, v.reach], [0, 0, 0], 'a pause past 120 ms: over');
  v.sample(13, 280);                                 // up, fast
  assert.ok(v.reach < 0, 'up: ahead above');
  const up = v.peak;
  v.sample(14, 290);                                 // turned back down
  assert.ok(v.peak < up, 'turned back: the old peak forgotten');

  const racing = new Velocity();
  for (let i = 0; i <= 10; i++) racing.sample(300 - i, 8 * i);   // (up a row every 8 ms)
  const [rate, reach] = [racing.rate(80), racing.reach];
  racing.shift(57);                                              // (57 let in above: the scroll moved down by as many)
  racing.sample(357 - 11, 88);
  assert.ok(racing.reach < 0 && Math.abs(racing.reach - reach) < 1, 'rows let in above: the fling up goes on as it was');
  assert.ok(Math.abs(racing.rate(88) - rate) < 1e-3, 'and the pace');
});

test('grid lead on a slow link: a steady pace step by step (5 rows every 300 ms, each pause ending a fling) is the rows covered over the last second (setting off after a second still, or turning back, the pace since); ahead, as far as it goes while an answer comes (its median time and the requests\' interval) and half again — or a fling\'s reach, whichever is further', () => {
  const v = new Velocity();
  for (let i = 0; i <= 4; i++) v.sample(100 + 5 * i, 300 * i);           // (steps: no fling survives them)
  assert.equal(v.reach, 0, 'no fling');
  assert.ok(Math.abs(v.rate(1200) - 20 / 1000) < 1e-9, 'the last second: 20 rows, from 200 ms to 1,200 ms');
  assert.ok(Math.abs(v.rate(1500) - 15 / 1000) < 1e-9, 'a pause: the last second\'s steps over the whole second');
  assert.equal(v.rate(2300), 0, 'stopped a second: none');
  const up = new Velocity();
  for (let i = 0; i <= 4; i++) up.sample(100 - 5 * i, 300 * i);
  assert.ok(up.rate(1200) < 0, 'up: negative');
  const off = new Velocity();
  off.sample(100, 0);                                                     // (still)
  for (let i = 0; i <= 2; i++) off.sample(101 + 10 * i, 5000 + 100 * i);   // (off again five seconds on, 10 rows a 100 ms)
  assert.ok(Math.abs(off.rate(5200) - 21 / 200) < 1e-9, 'set off after a second still: the pace since (21 rows in 200 ms), not the second\'s');
  const just = new Velocity();
  just.sample(100, 0);
  just.sample(101, 5000);
  assert.ok(Math.abs(just.rate(5010) - 1 / 120) < 1e-9, 'just set off: over 120 ms at least');
  const back = new Velocity();
  for (let i = 0; i <= 10; i++) back.sample(100 + 10 * i, 100 * i);          // (down 10 rows a 100 ms for a second)
  for (let i = 1; i <= 3; i++) back.sample(200 - 10 * i, 1000 + 100 * i);    // (then back up as fast)
  assert.ok(Math.abs(back.rate(1300) + 0.1) < 1e-9, 'turned back: the pace from the turn (up 30 rows in 300 ms), not the second\'s (down 40)');

  assert.equal(leadFor(0, 20 / 1000, [380, 420, 400], 50), 20 / 1000 * (400 + 50) * 1.5, 'a 400 ms answer at 20 rows a second: 13.5 rows');
  assert.equal(leadFor(0, 20 / 1000, [], 50), 20 / 1000 * (250 + 50) * 1.5, 'no answer yet: 250 ms taken');
  assert.equal(leadFor(-90, 20 / 1000, [400], 50), -90, 'a fling reaching further: its reach');
  assert.equal(leadFor(0, 0, [400], 50), 0, 'still: none');
});

test('grid overscan: rows held either side as the reader moves, whichever way they go next — behind as much as ahead; the lead ahead if it reaches further; none above the top; the trailing side giving way first to the session\'s most', () => {
  const p = new Prefetch(() => 0);
  p.see(20);                                                                        // (a quarter: 5 ahead, 4 behind)
  assert.deepEqual(windowFor(100, 20, true, p, 0, Infinity, 150), { first: 100, count: 20, before: 100, after: 150 }, 'still: both ways, no more above than there is');
  assert.deepEqual(windowFor(1000, 20, true, p, 45, Infinity, 150), { first: 1000, count: 20, before: 150, after: 150 }, 'moving: kept behind as ahead');
  assert.deepEqual(windowFor(1000, 20, false, p, -45, Infinity, 150), { first: 1000, count: 20, before: 150, after: 150 }, 'turned back: the same');
  assert.deepEqual(windowFor(1000, 20, true, p, 500, Infinity, 30), { first: 1000, count: 20, before: 30, after: 60 }, 'a small overscan: a lead reaching further, ahead (three views at most)');
  assert.deepEqual(windowFor(0, 20, true, p, 0, Infinity, 150), { first: 0, count: 20, before: 0, after: 150 }, 'at the top: none above');
  assert.deepEqual(windowFor(1000, 20, true, p, 0, 250, 150), { first: 1000, count: 20, before: 80, after: 150 }, 'the most rows: behind gives way');
});

test('grid first window: as the page asks before any overscan is told — its view, a quarter ahead — the rows LiveGrid reads from the body\'s height before it asks (LiveGrid.FirstWindow, held to the same rows in the C# test)', () => {
  const first = (height, most = 1000) => {
    const p = new Prefetch(() => 0), rows = height / 30;
    p.see(rows);
    return windowFor(0, rows, true, p, 0, most, 0, Math.min(rows, 0));
  };
  assert.deepEqual(first(780), { first: 0, count: 26, before: 0, after: 7 }, '780 px of 30 px rows: 26, and 7 ahead');
  assert.deepEqual(first(790), { first: 0, count: 27, before: 0, after: 7 }, 'a part row: one more in view');
  assert.deepEqual(first(100), { first: 0, count: 4, before: 0, after: 1 }, 'a few: one ahead');
  assert.deepEqual(first(29_000), { first: 0, count: 967, before: 0, after: 33 }, 'near the window\'s bound: the rows ahead give way');
});

test('grid racing up: two views a second or more, upward, at the steady pace or a fling\'s speed; down, or slower, not', () => {
  assert.equal(racingUp(-40 / 1000, 0, 20), true, '40 rows a second up, a view of 20: on two views a second');
  assert.equal(racingUp(-39 / 1000, 0, 20), false, 'a row a second less: not');
  assert.equal(racingUp(0, -0.1, 20), true, 'a fling up at 100 rows a second');
  assert.equal(racingUp(60 / 1000, 0.1, 20), false, 'down: not');
});

test('grid window asked for: the larger part ahead of the reader, which way they go — a view at least, unless the link carries fewer in time (then those, the pre-fetch\'s own, or the lead, whichever is most); none above the top, no more above than there is; the reader\'s lead ahead (at most three views); never more than the session\'s most, the trailing side giving way first', () => {
  const lead = new Prefetch(() => 0);
  lead.see(20);
  assert.deepEqual(windowFor(100, 20, true, lead, 45), { first: 100, count: 20, before: 4, after: 45 }, 'a fling: its lead below, the quarter view above');
  assert.deepEqual(windowFor(100, 20, true, lead, 500), { first: 100, count: 20, before: 4, after: 60 }, 'at most three views');
  assert.deepEqual(windowFor(100, 20, true, lead, 2), { first: 100, count: 20, before: 4, after: 20 }, 'a slow drag on a slow link: a view ahead, the quarter view behind');
  assert.deepEqual(windowFor(100, 20, true, lead, 45, 60), { first: 100, count: 20, before: 0, after: 40 }, 'the most rows: the trailing side gives way first, then the lead');
  assert.deepEqual(windowFor(0, 20, true, lead, 0, Infinity, 4, 4), { first: 0, count: 20, before: 0, after: 5 },
    'the live top on a link that carries four rows a side in time: the quarter view below, not a view');
  assert.deepEqual(windowFor(0, 20, true, lead, 0, Infinity, 0, 0), { first: 0, count: 20, before: 0, after: 5 }, 'before the link says: the same');
  assert.deepEqual(windowFor(100, 20, true, lead, 45, Infinity, 4, 4), { first: 100, count: 20, before: 4, after: 45 }, 'a fling on it: its lead still');
  const p = new Prefetch(() => 0);
  p.see(20);
  p.step = 0;
  assert.deepEqual(windowFor(100, 20, true, p), { first: 100, count: 20, before: 14, after: 20 }, 'down: two thirds above, a view below');
  assert.deepEqual(windowFor(100, 20, false, p), { first: 100, count: 20, before: 20, after: 14 }, 'up: a view above');
  assert.deepEqual(windowFor(0, 20, false, p), { first: 0, count: 20, before: 0, after: 14 }, 'at the top: none above');
  assert.deepEqual(windowFor(5.7, 19.4, false, p), { first: 5, count: 20, before: 5, after: 14 }, 'whole rows; no more above than there is');
});

test('grid requests: coalesced, at most one an interval and while fewer than the most are unanswered (the server and the link pace them); an answer settles those sent before it; the same window not asked twice; a reset asks afresh', () => {
  let now = 0, n = 0;
  const sent = [], timers = [];
  const r = new Requests(q => sent.push(q), {
    now: () => now, id: () => `r${++n}`, minInterval: 50, maxOutstanding: 3,
    setTimer: (fn, ms) => (timers.push({ fn, at: now + ms }), timers.length)
  });
  const w = first => ({ first, count: 20, before: 5, after: 20, holds: 0, letIn: 0 });
  r.want(w(0));
  assert.equal(r.pump().id, 'r1', 'the first: at once');
  now = 10;
  r.want(w(3));
  assert.equal(r.pump(), null, '10 ms on: held by the interval');
  r.want(w(6));
  assert.equal(r.pump(), null, 'what\'s wanted meanwhile coalesced');
  assert.equal(timers.length, 1, 'once: a timer for the interval\'s end');
  assert.equal(timers[0].at, 50, 'at its end');
  now = 50;
  timers[0].fn();
  assert.deepEqual(sent.map(q => [q.id, q.first]), [['r1', 0], ['r2', 6]], 'then the latest wanted');
  now = 100;
  r.want(w(6));
  assert.equal(r.pump(), null, 'the same window: not asked again');
  r.want(w(9));
  assert.equal(r.pump().id, 'r3');
  now = 150;
  r.want(w(12));
  assert.equal(r.pump(), null, 'three unanswered, the most: held');
  now = 180;
  const answered = r.answered('r2');
  assert.deepEqual(answered, { took: 130, superseded: ['r1'] }, 'r2 answered after 130 ms; r1, sent before it, settled with it');
  assert.equal(r.answered('r1'), null, 'settled already');
  assert.equal(r.pump().id, 'r4', 'room again: what\'s wanted asked for');
  assert.deepEqual(r.reset(), ['r3', 'r4'], 'a reset gives up those unanswered');
  now = 300;
  r.want(w(12));
  assert.equal(r.pump().id, 'r5', 'and asks afresh, the same window too');
  now = 400;
  r.answered('r5');
  assert.equal(r.pump(), null, 'the same window: not asked twice');
  r.again();
  assert.equal(r.pump().id, 'r6', 'asked again (a grid back in sight): its answer says what the window holds now');
});

test('grid order value: read as Tapline.Grid writes it (40 bytes, the view its rows are of among them, then keys); another format, cut short, a count that doesn\'t fit: not one', () => {
  const keys = [7n, 5n, 3n];
  const bytes = new Uint8Array(40 + 24), dv = new DataView(bytes.buffer);
  bytes[0] = 2; bytes[1] = 2; bytes[2] = 1; bytes[3] = 8;
  dv.setInt32(4, 3, true);
  dv.setBigInt64(8, 1_234_567_890_123n, true);
  dv.setBigInt64(16, 9_876_543_210n, true);
  dv.setBigInt64(24, 30n, true);
  dv.setInt32(32, 4, true);
  dv.setInt32(36, 2, true);
  keys.forEach((k, i) => dv.setBigInt64(40 + 8 * i, k, true));
  const order = readOrder(bytes);
  const latin1 = k => { const b = new Uint8Array(8); new DataView(b.buffer).setBigInt64(0, k, true); return String.fromCharCode(...b); };
  assert.deepEqual(order, { live: false, stale: true, byKey: false, allOpen: false, totalKind: 1, keySize: 8, count: 3, first: 1_234_567_890_123, total: 9_876_543_210,
    letIn: 30, holds: 4, view: 2, keys: keys.map(latin1), runs: null });
  assert.equal(readOrder(Uint8Array.of(1, ...bytes.subarray(1))), null, 'another format (the first, before runs)');
  assert.equal(readOrder(bytes.subarray(0, 63)), null, 'cut short');
  dv.setInt32(4, 4, true);
  assert.equal(readOrder(bytes), null, 'more keys than it holds');

  // By key: the header alone, the count the rows held; with key bytes after it, not one.
  const byKey = bytes.slice(0, 40);
  byKey[1] = 4 | 1;
  new DataView(byKey.buffer).setInt32(4, 3, true);
  const placed = readOrder(byKey);
  assert.deepEqual([placed.live, placed.byKey, placed.count, placed.keys], [true, true, 3, null], 'by key: three rows, no keys');
  assert.equal(readOrder(Uint8Array.of(...byKey, 0, 0, 0, 0, 0, 0, 0, 0)), null, 'by key, with key bytes: not one');
});

test('grid order value with runs: each open row\'s run after the keys — its key, whether it\'s above the window, its rows\' keys and depths — read as Tapline.Grid writes it, and placed: a run above the window first, each other after its open row, an open row with none of its run in the window open still; by key, the rows held but the runs\', greatest first; cut short, or bytes left over: not one', () => {
  const key = k => { const b = new Uint8Array(8); new DataView(b.buffer).setBigInt64(0, BigInt(k), true); return b; };
  const latin1 = k => String.fromCharCode(...key(k));
  const runs = [[100, 1, [101, 102], [1, 2]], [5, 0, [51, 52, 53], [1, 2, 1]], [3, 0, [], []]];   // (owner, flags, keys, depths)
  const parts = [Uint8Array.of(2, 8 | 1, 0, 8, 3, 0, 0, 0), new Uint8Array(32), key(5), key(4), key(3), Uint8Array.of(3, 0, 0, 0)];
  for (const [owner, flags, keys, depths] of runs)
    parts.push(key(owner), Uint8Array.of(flags, keys.length, 0, 0, 0), ...keys.map(key), Uint8Array.from(depths));
  const bytes = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  parts.reduce((o, p) => (bytes.set(p, o), o + p.length), 0);
  const order = readOrder(bytes);
  assert.deepEqual(order.keys, [5, 4, 3].map(latin1), 'the view\'s rows in the window, listed');
  assert.deepEqual(order.runs.map(r => [r.owner, r.lead, r.keys, [...r.depths]]),
    runs.map(([owner, flags, keys, depths]) => [latin1(owner), flags === 1, keys.map(latin1), depths]), 'its runs, as written');
  const placed = placeRuns(order.keys, order.runs);
  assert.deepEqual(placed.keys, [101, 102, 5, 51, 52, 53, 4, 3].map(latin1), 'the run above the window first, each other after its row');
  assert.deepEqual(placed.keys.map(k => placed.depth.get(k) ?? 0), [1, 2, 0, 1, 2, 1, 0, 0], 'each run\'s row at its depth');
  assert.deepEqual([...placed.open].sort(), [100, 5, 3].map(latin1).sort(), 'open: each run\'s row, one with none in the window too');
  assert.equal(readOrder(bytes.subarray(0, bytes.length - 1)), null, 'cut short');
  assert.equal(readOrder(Uint8Array.of(...bytes, 0)), null, 'a byte left over');
  assert.deepEqual(placeRuns(['a', 'b'], null), { keys: ['a', 'b'], depth: null, open: null }, 'no runs: the rows as they are');
});

test('grid order value, every row open: the flag, and runs only for rows with rows of theirs in the window, a closed one (no rows) for each row closed in it — placed: every row of the view open but those closed; with no runs at all, every row open still', () => {
  const key = k => { const b = new Uint8Array(8); new DataView(b.buffer).setBigInt64(0, BigInt(k), true); return b; };
  const latin1 = k => String.fromCharCode(...key(k));
  const runs = [[5, 0, [51, 52], [1, 1]], [4, 2, [], []]];   // (owner, flags — 2, closed — keys, depths)
  const parts = [Uint8Array.of(2, 16 | 8 | 1, 2, 8, 3, 0, 0, 0), new Uint8Array(32), key(5), key(4), key(3), Uint8Array.of(2, 0, 0, 0)];
  for (const [owner, flags, keys, depths] of runs)
    parts.push(key(owner), Uint8Array.of(flags, keys.length, 0, 0, 0), ...keys.map(key), Uint8Array.from(depths));
  const bytes = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  parts.reduce((o, p) => (bytes.set(p, o), o + p.length), 0);
  const order = readOrder(bytes);
  assert.deepEqual([order.allOpen, order.totalKind], [true, 2], 'every row open, the total at least so many');
  assert.deepEqual(order.runs.map(r => [r.owner, r.lead, r.closed, r.keys.length]), [[latin1(5), false, false, 2], [latin1(4), false, true, 0]],
    'an open row\'s run, and a closed row\'s, empty');
  const placed = placeRuns(order.keys, order.runs, order.allOpen);
  assert.deepEqual(placed.keys, [5, 51, 52, 4, 3].map(latin1), 'the run after its row; the closed row alone');
  assert.deepEqual([...placed.open].sort(), [5, 3].map(latin1).sort(), 'open: every row of the view but the closed one — one with none of its run in the window too');

  const bare = bytes.slice(0, 64);   // (every row open, no runs: none with rows in the window, none closed)
  bare[1] = 16 | 1;
  const none = readOrder(bare);
  assert.deepEqual([...placeRuns(none.keys, none.runs, none.allOpen).open].sort(), [5, 4, 3].map(latin1).sort(), 'no runs: every row open still');
});

test('grid status: the value read as Tapline.Grid writes it (12 bytes: the overscan the link carries, none told null; rows arrived above; the source moved past the rows while passive; then why the last read was refused); another format, cut short: not one — and the status line from it, the order and the page: the total (estimated ≈, at least ≥) named as the grid names its rows, live or held and the rows above, freshness (asking: the last word stands), the refusal marked, the selection', () => {
  const bytes = new Uint8Array(12 + 3), dv = new DataView(bytes.buffer);
  bytes[0] = 1;
  dv.setUint16(2, 64, true);
  dv.setBigInt64(4, 1_234n, true);
  assert.deepEqual(readStatus(bytes.subarray(0, 12)), { above: 1_234, overscan: 64, moved: false, refusal: null }, 'none refused: twelve bytes');
  bytes[1] = 2;
  assert.equal(readStatus(bytes.subarray(0, 12)).moved, true, 'the source moved past the rows, the page passive');
  bytes[1] = 1;
  dv.setUint16(2, 0xffff, true);
  bytes.set([0x6e, 0x6f, 0x21], 12);
  assert.deepEqual(readStatus(bytes), { above: 1_234, overscan: null, moved: false, refusal: 'no!' }, 'refused: its words after; no overscan told');
  assert.equal(readStatus(Uint8Array.of(2, ...bytes.subarray(1))), null, 'another format');
  assert.equal(readStatus(bytes.subarray(0, 11)), null, 'cut short');

  const names = ['order', 'orders'];
  assert.equal(statusText({ total: 1_000_000, totalKind: 0, live: true, above: 0, refusal: null }, 'fresh', 0, names),
    '1,000,000 orders · live · up to date');
  assert.equal(statusText({ total: 1, totalKind: 1, live: false, above: 0, refusal: null }, 'behind', 0, names), '≈1 order · held · catching up');
  assert.equal(statusText({ total: 5_000, totalKind: 2, live: false, above: 12_345, refusal: null }, 'unknown', 2, names),
    '≥5,000 orders · held, 12,345 new above · … · 2 selected');
  assert.deepEqual(statusParts({ total: 7, totalKind: 0, live: false, above: 0, refusal: 'timed out' }, 'fresh'),
    [['7 rows'], ['held'], ['up to date'], ['timed out', 'tg-refusal']], 'a refusal, marked to be drawn as one; rows by default');
});

test('circuit keep-alive: startBlazor has the page ping every 3 s it sends nothing else (heard however busy its download), before the page\'s own configureSignalR, which has the last word; 0 leaves SignalR\'s own', () => {
  const client = { attach: c => c, bind: () => {} };
  const started = options => {
    let given;
    startBlazor({ start: o => { given = o; } }, client, options);
    const calls = [];
    const builder = { withKeepAliveInterval: ms => calls.push(ms), build: () => ({}) };
    given.circuit.configureSignalR(builder);
    builder.build();
    return calls;
  };
  assert.deepEqual(started({ reloadOnRestore: false }), [3000], 'pings every 3 s by default');
  assert.deepEqual(started({ reloadOnRestore: false, circuit: { configureSignalR: b => b.withKeepAliveInterval(5000) } }), [3000, 5000], 'the page\'s own after: its own wins');
  assert.deepEqual(started({ reloadOnRestore: false, keepAliveInterval: 0 }), [], '0: SignalR\'s own (15 s)');
});

test('grid keys by key: placed greatest first, as the server\'s key type compares — a .NET Guid as Guid.CompareTo (its first three fields little-endian), an integer by value, signed or not', () => {
  const guid = hex => { const b = Buffer.from(hex.replaceAll('-', ''), 'hex'); return String.fromCharCode(b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], ...b.subarray(8)); };
  const ids = ['019a0000-0000-7000-8000-000000000002', '019a0000-0000-7000-8000-000000000010', '019a0000-0001-7000-8000-000000000001',
    '019b0000-0000-7000-8000-000000000000', '019a0000-0000-7001-8000-000000000000'];
  assert.deepEqual(keysByKey(ids.map(guid), 'guid'), ['019b0000-0000-7000-8000-000000000000', '019a0000-0001-7000-8000-000000000001',
    '019a0000-0000-7001-8000-000000000000', '019a0000-0000-7000-8000-000000000010', '019a0000-0000-7000-8000-000000000002'].map(guid),
    'by time first (its first 48 bits), then on, byte by byte as the canonical text reads');
  const long = n => { const b = new Uint8Array(8); new DataView(b.buffer).setBigInt64(0, BigInt(n), true); return String.fromCharCode(...b); };
  assert.deepEqual(keysByKey([3, -1, 256, 255, -300].map(long), 'int'), [256, 255, 3, -1, -300].map(long), 'signed integers by value');
  const ulong = n => { const b = new Uint8Array(8); new DataView(b.buffer).setBigUint64(0, BigInt(n), true); return String.fromCharCode(...b); };
  assert.deepEqual(keysByKey([1n, 2n ** 63n, 255n].map(ulong), 'uint'), [2n ** 63n, 255n, 1n].map(ulong), 'unsigned: the top bit large');
  assert.equal(compareKeys(long(5), long(5), 'int'), 0, 'the same key: nought');
});

test('grid cells by their column\'s .NET format (a blotter\'s, here): a quantity grouped, no nought before its point, a dash for nought (a custom format\'s sections), half away from nought; a time by its pattern, the instant on hover; the default, .NET\'s G; booleans, enumerations, text cut and flagged, a fraction of one column over another; the least integer no value', () => {
  const quantity = '#,###.####;-#,###.####;–';
  assert.equal(formatDecimal(2_500n, 4, quantity), '.25');
  assert.equal(formatDecimal(-2_500n, 4, quantity), '-.25', 'the negative section: its own sign');
  assert.equal(formatDecimal(12_345_678n, 2, quantity), '123,456.78');
  assert.equal(formatDecimal(0n, 4, quantity), '–', 'nought\'s section');
  assert.equal(formatDecimal(-4n, 4, '#,###.##;-#,###.##;–'), '–', 'rounded to nought: nought\'s section, no sign');
  assert.equal(formatDecimal(12_355n, 4, '0.00'), '1.24', 'half away from nought');
  assert.equal(formatDecimal(-12_355n, 4, '0.00'), '-1.24');
  assert.equal(formatDecimal(1_000_000_000_000_000_000n, 0, '#,0'), '1,000,000,000,000,000,000', 'exact past 2^53');
  assert.equal(formatDecimal(12_300n, 4, null), '1.2300', 'G: a decimal keeps its places');
  assert.equal(formatDouble(0.125, false, 'F2'), '0.12', 'a standard format: a double\'s exact value, half to even');
  assert.equal(formatDouble(0.125, false, '0.00'), '0.13', 'a custom one: fifteen digits, half away from nought');

  const row = new Uint8Array(64), dv = new DataView(row.buffer);
  const columns = [
    { name: 'qty', kind: 'fixed', offset: 0, scale: 4, format: quantity },
    { name: 'filled', kind: 'fixed', offset: 8, scale: 4 },
    { name: 'placed', kind: 'time', offset: 16, format: "'… 'HH:mm:ss" },
    { name: 'side', kind: 'enum', offset: 24, size: 1, dictionary: ['Buy', 'Sell'] },
    { name: 'urgent', kind: 'bool', offset: 25 },
    { name: 'client', kind: 'text', offset: 26, size: 13 },
    { name: 'fill', kind: 'percent', of: 'filled', over: 'qty', format: '0.#%' },
    { name: 'count', kind: 'int', offset: 40, size: 4 },
    { name: 'rate', kind: 'float', offset: 48, format: '#,##0.###;-#,##0.###;–' },
    { name: 'px', kind: 'fixed', offset: 56, scale: 8, format: 'G6' }
  ];
  dv.setBigInt64(0, 1_000_000n, true);                    // 100
  dv.setBigInt64(8, 250_000n, true);                      // 25
  dv.setBigInt64(16, BigInt(Date.UTC(2026, 9, 26, 14, 32, 5)) * 1000n + 123_456n, true);
  row[24] = 1; row[25] = 1;
  const text = new TextEncoder().encode('Client 0042 ltd');
  row[26] = 0x80 | 12; row.set(text.subarray(0, 12), 27);
  dv.setInt32(40, -2_147_483_648, true);                   // no value
  dv.setFloat64(48, 0.0004, true);
  dv.setBigInt64(56, 12_345_678_901n, true);               // 123.45678901
  const cell = name => formatCell(columns.find(c => c.name === name), row, dv, columns);
  assert.deepEqual(cell('qty'), { text: '100', title: '100.0000' }, 'whole on hover: the decimal, its places');
  assert.deepEqual(cell('placed'), { text: '… 14:32:05', title: '2026-10-26 14:32:05.123456 UTC' }, 'by its pattern; the instant on hover');
  assert.equal(formatCell({ ...columns[2], format: undefined }, row, dv, columns).text, '10/26/2026 14:32:05', 'no pattern: the invariant culture\'s G');
  assert.equal(cell('side').text, 'Sell');
  assert.deepEqual(cell('urgent'), { text: '✓', title: 'yes' });
  assert.deepEqual(cell('client'), { text: 'Client 0042 …', title: 'Client 0042 …', cut: true }, 'cut: flagged');
  assert.equal(cell('fill').text, '25%');
  assert.equal(formatCell({ ...columns[6], format: undefined }, row, dv, columns).text, '25.00 %', 'a fraction\'s default: P');
  assert.equal(cell('count').text, '', 'the least integer: no value');
  assert.equal(cell('rate').text, '–', 'rounded to nought: nought\'s section');
  dv.setFloat64(48, -1234.5678, true);
  assert.deepEqual(cell('rate'), { text: '-1,234.568', title: '-1234.5678' }, 'a double to its places, grouped; whole on hover');
  assert.deepEqual(cell('px'), { text: '123.457', title: '123.45678901' }, 'six figures shown (G6), exact on hover');
  dv.setFloat64(8, 0, true);
  dv.setBigInt64(0, 0n, true);
  assert.equal(cell('fill').text, '', 'a fraction of nothing: none');
});

test('grid cells as LiveGrid describes its columns: unsigned integers (none taken as no value), a float, its shortest text on hover, a DateTime\'s ticks shown as UTC (zero: none), an enum of any size and sign by its dictionary, a Guid in its text (empty: none), a percentage of fields by place', () => {
  const row = new Uint8Array(72), dv = new DataView(row.buffer);
  const columns = [
    { name: 'u8', kind: 'int', offset: 0, size: 1, unsigned: true },
    { name: 'u64', kind: 'int', offset: 8, size: 8, unsigned: true },
    { name: 'f32', kind: 'float', offset: 16, size: 4 },
    { name: 'at', kind: 'time', offset: 24, size: 8, unit: 'ticks', format: 'dd MMM HH:mm:ss.fff' },
    { name: 'side', kind: 'enum', offset: 32, size: 4, unsigned: false, dictionary: { '-1': 'None', '0': 'Buy', '1': 'Sell' } },
    { name: 'id', kind: 'uuid', offset: 40, size: 16 },
    { name: 'fill', kind: 'percent', of: { offset: 56, size: 4, unsigned: true }, over: { offset: 60, size: 4, unsigned: true }, format: '0%' }
  ];
  row[0] = 255;
  dv.setBigUint64(8, 2n ** 64n - 1n, true);
  dv.setFloat32(16, 0.1, true);
  const ticks = BigInt(Date.UTC(2026, 9, 26, 14, 32, 5)) * 10_000n + 1_234_560n + 621_355_968_000_000_000n;
  dv.setBigUint64(24, ticks | (1n << 62n), true);          // (DateTimeKind.Utc in the top bits)
  dv.setInt32(32, -1, true);
  row.set([0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff], 40);
  dv.setUint32(56, 3, true);
  dv.setUint32(60, 4, true);
  const cell = name => formatCell(columns.find(c => c.name === name), row, dv, columns);
  assert.equal(cell('u8').text, '255', 'unsigned: its greatest a value, not none');
  assert.equal(cell('u64').text, '18446744073709551615', 'a ulong, exact: its G');
  assert.equal(formatCell({ ...columns[1], format: 'N0' }, row, dv, columns).text, '18,446,744,073,709,551,615', 'and grouped (N0)');
  assert.deepEqual(cell('f32'), { text: '0.1', title: '0.1' }, 'a float32 as .NET writes it, not 0.10000000149011612');
  assert.deepEqual(cell('at'), { text: '26 Oct 14:32:05.123', title: '2026-10-26 14:32:05.123456 UTC' }, 'a DateTime\'s ticks, its kind bits aside');
  assert.equal(cell('side').text, 'None', 'a signed enum\'s -1');
  dv.setInt32(32, 1, true);
  assert.equal(cell('side').text, 'Sell');
  dv.setInt32(32, 7, true);
  assert.equal(cell('side').text, '#7', 'a value not in the dictionary: its number');
  assert.deepEqual(cell('id'), { text: '00112233-4455-6677-8899-aabbccddeeff', title: '00112233-4455-6677-8899-aabbccddeeff' }, 'a Guid\'s bytes as .NET lays them out');
  assert.equal(cell('fill').text, '75%');
  assert.equal(formatCell({ ...columns[5], format: 'N' }, row, dv, columns).text, '00112233445566778899aabbccddeeff', 'a Guid by its format (N)');
  row.fill(0);
  assert.equal(cell('at').text, '', 'DateTime.MinValue: none');
  assert.equal(cell('id').text, '', 'Guid.Empty: none');
  assert.equal(cell('u8').text, '0');
});

test('grid flashes on a slow link: run from the stamp as late as the link usually makes the page hear of a change — the median of the last eight states\' slowest lags — or from when the page heard of it, if sooner; none measured, or no stamp: from when it was heard', () => {
  assert.equal(lagShift([]), null, 'none measured');
  assert.equal(lagShift([1800, 2600, 2100]), 2100, 'the median');
  assert.equal(lagShift([90_000, 90_000, 2000, 2100, 1900, 2200, 2000, 1800, 2300, 2100]), 2100, 'the last eight states only (an even count: the upper middle)');
  assert.equal(lagShift([-40]), 0, 'never less than nought');
  const t = 1_000_000;
  assert.equal(flashFrom(t + 5_900, t, 5_900), t + 5_900, 'heard 5.9 s after its stamp, as the link usually is: from its start');
  assert.equal(flashFrom(t + 8_000, t, 5_900), t + 5_900, 'later than usual (a burst): 2.1 s on');
  assert.equal(flashFrom(t + 1_500, t, 5_900), t + 1_500, 'heard sooner than usual: from when heard');
  assert.equal(flashFrom(undefined, t, 2_100), t + 2_100, 'not heard of here (scrolled to): its stamp, shifted');
  assert.equal(flashFrom(t + 5_900, t, null), t + 5_900, 'no delay measured, or the stamps not trusted: from when heard');
  assert.equal(flashFrom(t + 5_900, null, 2_100), t + 5_900, 'no stamp: from when heard');
});

test('grid flashes as they happened: the delay showing a flash where it has got to (none once over, from its start if not yet begun), on its boundaries; a row\'s arrival stamp — µs since 1970, or a DateTime\'s ticks, its kind aside — in ms, none for none; a CSS time in ms', () => {
  assert.equal(flashDelay(0, 5000), 0, 'just now: from its start');
  assert.equal(flashDelay(-40, 5000), 0, 'stamped a little ahead of the page: from its start');
  assert.equal(flashDelay(1200, 5000), -1200, 'a second and a bit ago: that far in');
  assert.equal(flashDelay(4999, 5000), -4999, 'a millisecond before its end: shown');
  assert.equal(flashDelay(5000, 5000), null, 'on its end: over');
  assert.equal(flashDelay(5001, 5000), null, 'past it: over');
  assert.equal(flashDelay(10, 0), null, 'no flash in the page\'s CSS: none');
  const row = new Uint8Array(16), dv = new DataView(row.buffer);
  const at = Date.UTC(2026, 9, 26, 14, 32, 5, 123);
  dv.setBigInt64(0, BigInt(at) * 1000n + 456n, true);
  assert.ok(Math.abs(stampOf({ offset: 0, size: 8 }, row, dv) - (at + 0.456)) < 1e-3, 'µs since 1970, to the µs');
  dv.setBigUint64(8, (BigInt(at) * 10_000n + 4_560n + 621_355_968_000_000_000n) | (1n << 62n), true);
  assert.ok(Math.abs(stampOf({ offset: 8, size: 8, unit: 'ticks' }, row, dv) - (at + 0.456)) < 1e-3, 'a DateTime\'s ticks, its kind (Utc) aside');
  dv.setBigInt64(0, -(2n ** 63n), true);
  dv.setBigUint64(8, 1n << 62n, true);
  assert.equal(stampOf({ offset: 0 }, row, dv), null, 'the least long: none');
  assert.equal(stampOf({ offset: 8, unit: 'ticks' }, row, dv), null, 'DateTime.MinValue (whatever its kind): none');
  assert.equal(stampOf({ offset: 12 }, row, dv), null, 'past the row\'s end: none');
  assert.equal(cssMs('5s'), 5000);
  assert.equal(cssMs('2400ms'), 2400);
  assert.equal(cssMs('0.25s, 1s'), 250, 'the first of a list');
  assert.equal(cssMs(''), 0);
});

test('grid stamps from keys and the page\'s clock: a UUIDv7 key\'s time from its bytes as a .NET Guid lays them out (another version or variant, another size: none); stamps trusted while the least lag of the last live arrivals is within 500 ms either way, and the page\'s round trip more the later way', () => {
  // 0192f4a1-b2c3-7d5e-8f60-718293a4b5c6: ms 0x0192f4a1b2c3 (2024-10-30T…), version 7, variant 10
  const rfc = [0x01, 0x92, 0xf4, 0xa1, 0xb2, 0xc3, 0x7d, 0x5e, 0x8f, 0x60, 0x71, 0x82, 0x93, 0xa4, 0xb5, 0xc6];
  const memory = [rfc[3], rfc[2], rfc[1], rfc[0], rfc[5], rfc[4], rfc[7], rfc[6], ...rfc.slice(8)];   // (Data1–3 little-endian)
  const key = String.fromCharCode(...memory);
  assert.equal(keyTime(key), 0x0192f4a1b2c3, 'its first 48 bits, in ms');
  assert.equal(stampOf({ unit: 'uuid7' }, new Uint8Array(0), null, key), 0x0192f4a1b2c3, 'as a stamp, the row unread');
  const v4 = [...memory];
  v4[7] = 0x4d;
  assert.equal(keyTime(String.fromCharCode(...v4)), null, 'version 4: none');
  const otherVariant = [...memory];
  otherVariant[8] = 0xcf;
  assert.equal(keyTime(String.fromCharCode(...otherVariant)), null, 'another variant: none');
  assert.equal(keyTime('ø\u0007\u0006\u0005\u0004\u0003\u0002\u0001'), null, 'a long\'s key: none');

  assert.equal(stampsTrusted([]), true, 'none heard yet: trusted');
  assert.equal(stampsTrusted([40, 900, 120]), true, 'one arrival slow: the least is what counts');
  assert.equal(stampsTrusted([500, 700]), true, 'on 500 ms');
  assert.equal(stampsTrusted([501, 900]), false, 'every arrival later than a trip takes: the page\'s clock ahead');
  assert.equal(stampsTrusted([-500, 30]), true);
  assert.equal(stampsTrusted([-501, 30]), false, 'stamped further ahead than a trip: the page\'s clock behind');
  assert.equal(stampsTrusted([3300, 3700], 5600), true, 'a slow link (16 Kbit): a round trip of 5.6 s, arrivals 3.3 s late');
  assert.equal(stampsTrusted([6101], 5600), false, 'later than the round trip and 500 ms');
  assert.equal(stampsTrusted([-501], 5600), false, 'ahead by more than 500 ms, however slow the link');
});

test('grid arrivals at the live top: those above the row that was first, or the first still held; by key, every row above the row that was first, the window turned over', () => {
  assert.equal(arrivedAbove(['c', 'b', 'a'], ['e', 'd', 'c', 'b'], false), 2, 'above the row that was first');
  assert.equal(arrivedAbove(['c', 'b', 'a'], ['e', 'd', 'b', 'a'], false), 2, 'that row gone: above the first still held');
  assert.equal(arrivedAbove(['c', 'b', 'a'], ['f', 'e', 'd'], false), 0, 'none held still, in another order: none known');
  assert.equal(arrivedAbove([], ['a'], false), 0, 'none held before');
  const long = n => { const b = new Uint8Array(8); new DataView(b.buffer).setBigInt64(0, BigInt(n), true); return String.fromCharCode(...b); };
  const keys = ns => ns.map(long);
  assert.equal(arrivedAbove(keys([30, 20, 10]), keys([90, 80, 70]), true, 'int'), 3, 'by key, the window turned over: every row');
  assert.equal(arrivedAbove(keys([30, 20, 10]), keys([50, 40, 20, 10]), true, 'int'), 2, 'by key, the row that was first gone: those above it');
  assert.equal(arrivedAbove(keys([30, 20, 10]), keys([50, 30, 20]), true, 'int'), 1, 'by key, as the row that was first says');
});

test('grid stale rows: a new view\'s first rows late after twice the page\'s round trip — the median of its last answers\' times — and never more than 500 ms (500 ms before any answer)', () => {
  assert.equal(staleAfter([]), 500, 'none answered yet');
  assert.equal(staleAfter([12]), 24);
  assert.equal(staleAfter([40, 9, 300]), 80, 'the median, not the slowest');
  assert.equal(staleAfter([10, 20, 30, 40]), 60, 'an even count: the upper middle');
  assert.equal(staleAfter([400, 260, 900]), 500, 'a slow link: at most 500 ms');
});

test('grid cells written by .NET\'s rules — its output (fixtures/formats.json, by the C# tests) for every integer type, decimals of several scales, doubles and floats (edges, ties, random bits), by every standard format and custom ones, times by standard and custom patterns, and Guids, in cultures that differ in their separators, groups, patterns, signs, names (genitive too) and calendars — written alike', () => {
  const fx = JSON.parse(readFileSync(new URL('./fixtures/formats.json', import.meta.url)));
  const cultures = Object.fromEntries(Object.entries(fx.cultures).map(([name, given]) => [name, cultureOf(given)]));
  const f64 = new Float64Array(1), u64 = new BigUint64Array(f64.buffer), f32 = new Float32Array(1), u32 = new Uint32Array(f32.buffer);
  const misses = [];
  for (const [culture, type, value, format, want] of fx.numbers) {
    const n = cultures[culture].n, kind = type[0], size = Number(type.slice(1));
    const got = kind === 'i' || kind === 'u' ? formatInteger(size === 8 ? BigInt(value) : Number(value), size, kind === 'u', format, n)
      : kind === 'd' ? formatDecimal(BigInt(value), size, format, n)
        : type === 'f8' ? (u64[0] = BigInt('0x' + value), formatDouble(f64[0], false, format, n))
          : (u32[0] = parseInt(value, 16), formatDouble(f32[0], true, format, n));
    if (got !== want) misses.push(`${culture || 'invariant'} ${type} ${value} ${JSON.stringify(format)}: ${JSON.stringify(got)}, .NET ${JSON.stringify(want)}`);
  }
  for (const [culture, ticks, pattern, invariant, want] of fx.times) {
    const got = formatTime(BigInt(ticks), pattern, invariant ? INVARIANT.d : cultures[culture].d);
    if (got !== want) misses.push(`${culture || 'invariant'} ${ticks} ${JSON.stringify(pattern)}: ${JSON.stringify(got)}, .NET ${JSON.stringify(want)}`);
  }
  for (const [hex, format, want] of fx.guids) {
    const got = formatGuid(Uint8Array.from(hex.match(/../g), h => parseInt(h, 16)), format);
    if (got !== want) misses.push(`${hex} ${format}: ${got}, .NET ${want}`);
  }
  assert.ok(fx.numbers.length > 50_000 && fx.times.length > 2_000 && Object.keys(fx.cultures).length > 20, 'the fixture is whole');
  assert.deepEqual(misses.slice(0, 20), [], `${misses.length} of ${fx.numbers.length + fx.times.length + fx.guids.length} written otherwise than .NET writes them`);
});

test('grid cells to significant figures (LiveColumn.Figures, MostDigits): rounded half away from nought, then by the format; past the most digits — those before the point and after it to its last that counts — in scientific notation to as many figures; the tooltip the value whole', () => {
  const size = '#,###.########;-#,###.########;–', m = (c, e = 0) => ({c, e});
  assert.equal(figuredText(m(123456789n), false, 6, 8, size), '1.23457e8', 'nine digits: scientific');
  assert.equal(figuredText(m(123456789n), true, 6, 8, size), '-1.23457e8');
  assert.equal(figuredText(m(12345678n), false, 6, 8, size), '12,345,700', 'eight: fixed, to six figures');
  assert.equal(figuredText(m(123456n, -9), false, 6, 8, size), '1.23456e-4', '.000123456: nine digits');
  assert.equal(figuredText(m(123456n, -8), false, 6, 8, size), '.00123456', '.00123456: eight');
  assert.equal(figuredText(m(15n, -1), false, 6, 8, size), '1.5', 'its figures only those that count');
  assert.equal(figuredText(m(1234565n, -3), false, 6, 8, size), '1,234.57', 'half away from nought');
  assert.equal(figuredText(m(0n), false, 6, 8, size), '–', 'nought: the format\'s section');
  assert.equal(figuredText(m(987654321n), false, 1, 8, size), '1e9', 'one figure');
  assert.equal(figuredText(m(123456789n), false, 6, null, size), '123,457,000', 'no most: never scientific');
  const row = new Uint8Array(8), dv = new DataView(row.buffer), qty = { name: 'qty', kind: 'float', offset: 0, size: 8, format: size, figures: 6, mostDigits: 8 };
  dv.setFloat64(0, 123456789.25, true);
  assert.deepEqual(formatCell(qty, row, dv, [qty]), { text: '1.23457e8', title: '123456789.25' }, 'a double: the tooltip, the value whole');
  dv.setFloat64(0, 0.25, true);
  assert.deepEqual(formatCell(qty, row, dv, [qty]), { text: '.25', title: '0.25' });
  const fixed = { name: 'fixed', kind: 'fixed', offset: 0, size: 8, scale: 4, format: size, figures: 6, mostDigits: 8 };
  dv.setBigInt64(0, 1234567891234n, true);   // 123,456,789.1234
  assert.deepEqual(formatCell(fixed, row, dv, [fixed]), { text: '1.23457e8', title: '123456789.1234' }, 'a Fixed: its decimal whole on hover');
});

test('grid figures in the status line: to two significant figures, shown again only once they move by 5 %', () => {
  assert.equal(twoFigures(0.004), '0');
  assert.equal(twoFigures(0.0567), '0.057');
  assert.equal(twoFigures(1.234), '1.2');
  assert.equal(twoFigures(12.34), '12');
  assert.equal(twoFigures(1234), '1200');
  assert.ok(!moved(1.2, 1.25), '4 %: not again');
  assert.ok(moved(1.2, 1.3), 'by 8 %, and reading differently: again');
  assert.ok(moved(0, 0.5) && !moved(0, 0.004), 'from nought: once it reads as more than nought');
  assert.ok(!moved(100, 104), 'by 4 % of a hundred: not again');
});

test('grid on the wire: a session\'s frames as Tapline.Grid writes them, through the client with { changed: true } — after each step the order read as written (live or held, first, total, rows let in, holds, keys; at the live top by key, no keys, the rows held placed by theirs), the status value (rows arrived above) and the changed keys those whose rows differ; held, arrivals above: the status alone', () => {
  const g = fx.grid;
  const client = newClient([]);
  const latin1 = id => { const b = new Uint8Array(8); new DataView(b.buffer).setBigInt64(0, BigInt(id), true); return String.fromCharCode(...b); };
  let last = null, states = 0, seq = 0;
  client.subscribe(g.watchId, (group, _id, info) => { last = { group, info }; states++; }, undefined, undefined, { changed: true });
  for (const step of g.steps) {
    const before = states;
    for (const f of step.frames) client.receive(++seq, new Uint8Array(Buffer.from(f, 'base64')));
    if (!step.sent) {
      assert.equal(states, before, `${step.what}: nothing handed over`);
      continue;
    }
    assert.ok(states > before, `${step.what}: handed over`);
    const order = readOrder(last.group.members[1]), e = step.expect, table = last.group.members[0];
    assert.deepEqual([readStatus(last.group.members[2])?.above, readStatus(last.group.members[2])?.overscan], [e.Above, null],
      `${step.what}: the status value, rows arrived above (no overscan told: the page's own)`);
    if (step.statusOnly)
      assert.deepEqual([last.info.changed[0].size, last.info.changed[1], last.info.changed[2]], [0, false, true], `${step.what}: the status alone changed`);
    assert.deepEqual([order.live, order.byKey, order.first, order.total, order.letIn, order.holds], [e.Live, e.ByKey, e.First, e.Total, e.LetIn, e.Holds], `${step.what}: the order's header`);
    if (order.byKey) {
      assert.equal(order.keys, null, `${step.what}: by key, no keys sent`);
      order.keys = keysByKey(table.rows.keys(), 'int');
    }
    assert.deepEqual(order.keys, e.keys.map(latin1), `${step.what}: its keys, in order${order.byKey ? ' (by key: the rows held, greatest first)' : ''}`);
    assert.equal(table.size, e.keys.length, `${step.what}: the rows table the window's rows alone`);
    assert.deepEqual(order.keys.map(k => new DataView(table.rows.get(k).row.buffer, table.rows.get(k).row.byteOffset).getInt32(16, true)), e.qty,
      `${step.what}: each row as the server has it`);
    assert.deepEqual([...last.info.changed[0]].sort(), e.changed.map(latin1).sort(), `${step.what}: the keys changed, and only those`);
  }
});

for (const [name, fn] of tests) { await fn(); passed++; console.log(`PASS  ${name}`); }
console.log(`\nAll ${passed} JS tests passed.`);
