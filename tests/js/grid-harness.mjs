// The grid body (tapline-grid.js) in a browser, against a server played here: a window's rows and order as the session
// publishes them, answered after a latency, with rows arriving at the live top and rows in view changing. For checking
// the DOM layer — placeholders, flashes, widths, scaled scrolling, keys, selection and focus — by eye and by script
// (window.harness), laid out as LiveGrid lays it out (the grid around its headings and body).
//   ?rows=5000000  rows in all (50000000: scaled scrolling)    ?latency=80  ms an answer takes
//   ?arrive=700    ms between arrivals (0: none)               ?change=400  ms between changes in view (0: none)
//   ?visible=1     the page taken as shown whatever the browser says (a pane in the background, scripted: body.draw())
import { Table } from '/src/Tapline.AspNetCore/wwwroot/tapline.js';

if (new URLSearchParams(location.search).get('visible') === '1')
    Object.defineProperty(document, 'visibilityState', { get: () => 'visible' });
import { GridBody } from '/src/Tapline.Grid/wwwroot/tapline-grid.js';

const q = new URLSearchParams(location.search);
const ROWS = Number(q.get('rows') ?? 5_000_000), LATENCY = Number(q.get('latency') ?? 80);
const ARRIVE = Number(q.get('arrive') ?? 700), CHANGE = Number(q.get('change') ?? 400);
const KEY = 8, ROW = 64;

// The columns as LiveGrid sends them, and the room each has for its text, as its style gives it (--tg-w<i>: in ch, as
// wide as its format writes a wide value; "auto" here, the client's, measured from the first rows answered).
const quantity = '#,###.####;-#,###.####;–';
const columns = [
    { name: 'placed', label: 'Placed', kind: 'time', offset: 0, frozen: true, format: "'… 'HH:mm:ss", room: '10ch' },
    { name: 'id', label: 'Order', kind: 'int', offset: 8, frozen: true, room: '9ch' },
    { name: 'client', label: 'Client', kind: 'text', offset: 42, size: 22, auto: true, room: '21ch' },
    { name: 'side', label: 'Side', kind: 'enum', offset: 40, size: 1, dictionary: ['Buy', 'Sell'], room: '6ch' },
    { name: 'qty', label: 'Qty', kind: 'fixed', offset: 16, scale: 4, format: quantity, room: '15ch' },
    { name: 'filled', label: 'Filled', kind: 'fixed', offset: 24, scale: 4, format: quantity, room: '15ch' },
    { name: 'fill', label: 'Fill %', kind: 'percent', of: 'filled', over: 'qty', format: '#,###.#%;-#,###.#%;–', room: '8ch' },
    { name: 'price', label: 'Price', kind: 'fixed', offset: 32, scale: 8, format: 'G6', room: '12ch' },
    { name: 'status', label: 'Status', kind: 'enum', offset: 41, size: 1, dictionary: ['New', 'Partial', 'Filled', 'Cancelled'], room: '9ch' }
];

const hash = id => {
    let h = BigInt.asUintN(64, BigInt(id) * 0x9E3779B97F4A7C15n);
    h ^= h >> 31n;
    h = BigInt.asUintN(64, h * 0xD6E8FEB86659FD93n);
    return h ^ (h >> 29n);
};
const keyOf = id => {
    const b = new Uint8Array(KEY);
    new DataView(b.buffer).setBigInt64(0, BigInt(id), true);
    return b;
};
const latin1 = b => String.fromCharCode(...b);
const changes = new Map();                       // id → how often it changed
function rowOf(id) {
    const h = hash(id), n = changes.get(id) ?? 0, b = new Uint8Array(ROW), dv = new DataView(b.buffer);
    dv.setBigInt64(0, BigInt(Date.UTC(2026, 9, 26, 9)) * 1000n + BigInt(id) * 1_000_000n % 86_400_000_000n, true);
    dv.setBigInt64(8, BigInt(id), true);
    const qty = (h % 1000n + 1n) * 10_000n;
    dv.setBigInt64(16, qty, true);
    dv.setBigInt64(24, qty * BigInt(Math.min(4, Number((h >> 12n) % 3n) + n)) / 4n, true);
    dv.setBigInt64(32, 10_000_000_000n + (h >> 20n) % 5_000_000_000n, true);
    b[40] = Number((h >> 33n) & 1n);
    b[41] = Math.min(3, Number((h >> 37n) % 2n) + n);
    const text = new TextEncoder().encode(`Client ${Number((h >> 40n) % 997n)} of ${['Kent', 'Lombard Street', 'the Barbican'][Number(h % 3n)]}`);
    b[42] = Math.min(text.length, 21) | (text.length > 21 ? 0x80 : 0);
    b.set(text.subarray(0, 21), 43);
    return b;
}

// ── the server, as the session would answer: the window's rows (a table replica) and their order ──

let total = ROWS, holds = 0, live = true;
const table = new Table();
table.keySize = KEY;
table.rowSize = ROW;
let order = new Uint8Array(40), asked = null, onState = null;
const waits = new Map();
const stats = { requests: 0, answers: 0, states: 0 };

function orderBytes(first, ids) {
    const b = new Uint8Array(40 + KEY * ids.length), dv = new DataView(b.buffer);
    b[0] = 1;
    b[1] = live ? 1 : 0;
    b[3] = KEY;
    dv.setInt32(4, ids.length, true);
    dv.setBigInt64(8, BigInt(first), true);
    dv.setBigInt64(16, BigInt(total), true);
    dv.setInt32(32, holds, true);
    dv.setInt32(36, asked?.view ?? 0, true);
    ids.forEach((id, i) => b.set(keyOf(id), 40 + KEY * i));
    return b;
}

/** The window for `asked`, published: rows in and out, the order; the changed keys handed over as tapline.js would. */
function publish(updated = new Set()) {
    const from = Math.max(0, asked.first - asked.before), to = Math.min(total, asked.first + asked.count + asked.after);
    const ids = [];
    for (let rank = from; rank < to; rank++) ids.push(total - 1 - rank);
    const changed = new Set(), keep = new Set(ids.map(id => latin1(keyOf(id))));
    for (const key of table.rows.keys()) if (!keep.has(key)) {
        table.rows.delete(key);
        changed.add(key);
    }
    for (const id of ids) {
        const key = latin1(keyOf(id));
        if (!table.rows.has(key) || updated.has(id)) {
            table.rows.set(key, { key: keyOf(id), row: rowOf(id) });
            changed.add(key);
        }
    }
    const next = orderBytes(from, ids), was = order;
    order = next;
    stats.states++;
    onState?.({ members: [table, order], degraded: false }, 1, { provider: undefined, changed: [changed, !sameBytes(was, next)] });
}
const sameBytes = (a, b) => a.length === b.length && a.every((v, i) => v === b[i]);

function request(r) {
    stats.requests++;
    setTimeout(() => {
        if (r.first > 0 && live) holds++;
        live = r.first === 0;
        asked = r;
        publish();
        stats.answers++;
        waits.get(r.id)?.();
        waits.delete(r.id);
    }, LATENCY * (0.75 + Math.random() / 2));
}

if (ARRIVE) setInterval(() => {
    total += 1 + Math.floor(Math.random() * 2);
    if (asked && live) publish();          // (held, the window's rows stay where they are: positions are ranks here)
}, ARRIVE);
if (CHANGE) setInterval(() => {
    if (!asked) return;
    const rank = Math.max(0, asked.first) + Math.floor(Math.random() * asked.count), id = total - 1 - rank;
    changes.set(id, (changes.get(id) ?? 0) + 1);
    publish(new Set([id]));
}, CHANGE);

const tapline = {
    subscribe(binding, state) {
        onState = state;
        return () => onState = null;
    },
    rendezvous(id, { signal } = {}) {
        return new Promise((resolve, reject) => {
            waits.set(id, resolve);
            signal?.addEventListener('abort', () => {
                waits.delete(id);
                reject(new Error('aborted'));
            }, { once: true });
        });
    },
    sync: () => new Promise(resolve => setTimeout(resolve, LATENCY / 2)),
    correlation: () => crypto.randomUUID().replaceAll('-', '')
};

// ── the page: headings (the chrome's, here plain) and the body ──

const root = document.getElementById('grid'), host = document.getElementById('host'), head = document.getElementById('headings');
columns.forEach((c, i) => {
    document.getElementById('frame').style.setProperty(`--tg-w${i}`, c.room);
    const h = document.createElement('div');
    h.className = 'tg-heading' + (c.frozen ? ' tg-frozen' : '') + (['int', 'fixed', 'float', 'percent'].includes(c.kind) ? ' tg-end' : ' tg-start');
    h.setAttribute('role', 'columnheader');
    h.textContent = c.label;
    h.style.width = i === 0 ? 'var(--tg-x1)' : `calc(var(--tg-x${i + 1}) - var(--tg-x${i}))`;
    if (c.frozen) h.style.left = i === 0 ? '0' : `var(--tg-x${i})`;
    head.firstElementChild.append(h);
});
const status = document.getElementById('status'), events = [];
const body = new GridBody(root, {
    columns, rowHeight: 28, tapline, binding: { tapId: 1 }, request, maxRows: 1000, host, head, headerRows: 1, statusless: true,
    selection: 'multiple', clickable: true, activatable: true,
    rowClick: e => events.push({ click: e }), rowActivate: e => events.push({ activate: e }), rowSelect: e => events.push({ select: e }),
    visible: shown => console.log(`grid: ${shown ? 'shown' : 'hidden'}`),
    status: s => {
        status.textContent = s.ended ? 'ended' : `${s.total.toLocaleString('en-GB')} rows · ${s.live ? 'live' : 'held'} · ` +
            `${s.fresh} · ${s.blanks} blank frames`;
    }
});
body.start();
window.harness = { body, stats, columns, events, keyOf: id => latin1(keyOf(id)), get total() { return total; } };
