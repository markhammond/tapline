// tapline-grid.js — Tapline.Grid's page body: a LiveGrid's rows, drawn on the page from a Tapline replica of the window
// its server keeps for it (a rows table and their order, read through one watch with { changed: true }), and the
// windows it asks for as the reader scrolls. Blazor renders the grid's chrome (headings, filters, status); this draws
// the body inside an element Blazor leaves empty, and takes the grid's keys, clicks and focus, by row key (createGrid
// joins it to its LiveGrid). It takes nothing page-wide: no title or icon, no keys of the document's; its one document
// listener (visibilitychange) is added beside any others.
//
// The arithmetic is pure, exported, and tested in Node: scrolling scaled past the browser's height cap (Scroller), the
// pre-fetch adapting to the link (Prefetch), the window asked for (windowFor), requests held to an interval and by the
// answers outstanding (Requests), the order value (readOrder) and cells (formatCell). The DOM is GridBody's, checked
// in a browser.

/** Taller than this, a grid's scroll surface is scaled: Chrome caps an element's height at 16,777,214 px. */
export const SAFE_HEIGHT = 8_000_000;

/** What a cell shows for nought: a dash. */
export const NOUGHT = '–';

const clamp = (v, lo, hi) => Math.min(Math.max(v, lo), hi);

/**
 * Where a grid's view is in its rows, and the scroll position that puts it there. Under the safe height, scrolling is
 * native and exact. Above it, the scroll surface is the safe height and the top row is worked out from the scroll
 * fraction, so the thumb means position; wheel, keys and touch move by rows (by), and the scroll position follows the
 * row, not the other way about — its own scroll event, rounded by the browser, leaves the row as it was.
 */
export class Scroller {
    constructor(rowHeight, safe = SAFE_HEIGHT) {
        this.rowHeight = rowHeight;
        this.safe = safe;
        this.total = 0;           // rows in all
        this.viewHeight = 0;      // px
        this.row = 0;             // the row at the top of the view (fractional: a row's part scrolled past)
        this.expected = null;     // the scroll position last set here: its scroll event is this one's own
    }

    /** Whether the surface is scaled: the rows taller than the safe height. */
    get scaled() {
        return this.total * this.rowHeight > this.safe;
    }

    /** The scroll surface's height, px. */
    get height() {
        return Math.min(this.total * this.rowHeight, this.safe);
    }

    /** Rows the view holds (fractional). */
    get viewRows() {
        return this.viewHeight / this.rowHeight;
    }

    /** The furthest the top row goes: the last row at the bottom of the view. */
    get lastTop() {
        return Math.max(0, this.total - this.viewRows);
    }

    /** The scroll position that puts `row` at the top. */
    topFor(row) {
        if (!this.scaled) return row * this.rowHeight;
        const range = this.height - this.viewHeight;
        return this.lastTop > 0 ? row / this.lastTop * range : 0;
    }

    /** A scroll event at `top`: the one set here, the row stands; else the reader's own (a thumb), the row by its fraction. */
    scrolled(top) {
        const own = this.expected !== null && Math.abs(top - this.expected) <= 1;
        this.expected = null;
        if (own) return this.row;
        if (this.scaled) {
            const range = this.height - this.viewHeight;
            this.row = range > 0 ? top / range * this.lastTop : 0;
        } else this.row = top / this.rowHeight;
        this.row = clamp(this.row, 0, this.lastTop);
        return this.row;
    }

    /** Moved by `rows` (wheel, keys, touch, rows let in above): the scroll position to set. */
    by(rows) {
        return this.to(this.row + rows);
    }

    /** Moved to `row` at the top: the scroll position to set. */
    to(row) {
        this.row = clamp(row, 0, this.lastTop);
        this.expected = this.topFor(this.row);
        return this.expected;
    }

    /** The view's height now, px: the row at the top stays where it can. */
    resize(viewHeight) {
        this.viewHeight = viewHeight;
        this.row = clamp(this.row, 0, this.lastTop);
    }

    /** Rows in all now: the row at the top stays where it can. */
    setTotal(total) {
        this.total = total;
        this.row = clamp(this.row, 0, this.lastTop);
    }
}

/**
 * How far beyond the view the grid fetches, from how long its answers take: a view ahead of the reader and two thirds
 * of a view behind at full size, a half or a quarter of that on a slower link. It starts at a quarter, the first
 * answers being the heaviest, and steps up once the middle of its last three answers took less than 150 ms, down once
 * it took more than 500 ms, between the two staying; the first answer after a step isn't judged. A size left for being
 * slow isn't tried again for 30 s, twice as long each time it's left again, up to 5 min. Times are the page's own, from
 * a request to its rendezvous.
 */
export class Prefetch {
    static Steps = [1, 0.5, 0.25];
    static Slow = 500;
    static Fast = 150;
    static FirstHoldBack = 30_000;
    static LongestHoldBack = 300_000;

    constructor(now = () => performance.now()) {
        this.now = now;
        this.step = Prefetch.Steps.length - 1;
        this.view = 20;                                     // rows in view, until the page says
        this.taken = [];                                    // the last answers' times, oldest first
        this.settling = false;
        this.heldBackUntil = Prefetch.Steps.map(() => -Infinity);
        this.holdBack = Prefetch.Steps.map(() => Prefetch.FirstHoldBack);
    }

    /** Rows fetched ahead of the reader. */
    get ahead() {
        return Math.ceil(Prefetch.Steps[this.step] * this.view);
    }

    /** Rows fetched behind. */
    get behind() {
        return Math.ceil(Prefetch.Steps[this.step] * this.view * 2 / 3);
    }

    /** The view holds `rows` now: true if that changed it. */
    see(rows) {
        rows = Math.ceil(rows);
        if (rows < 1 || rows === this.view) return false;
        this.view = rows;
        return true;
    }

    /** An answer took `took` ms: true if the size changed. */
    observe(took) {
        if (this.settling) {
            this.settling = false;
            return false;
        }

        this.taken.push(took);
        if (this.taken.length < 3) return false;
        const judged = [...this.taken].sort((a, b) => a - b)[1];
        this.taken.shift();
        const now = this.now(), last = Prefetch.Steps.length - 1;
        let step = this.step;
        if (judged > Prefetch.Slow && this.step < last) {
            this.heldBackUntil[this.step] = now + this.holdBack[this.step];
            this.holdBack[this.step] = Math.min(this.holdBack[this.step] * 2, Prefetch.LongestHoldBack);
            step = this.step + 1;
        } else if (judged < Prefetch.Fast && this.step > 0 && now >= this.heldBackUntil[this.step - 1]) step = this.step - 1;
        if (step === this.step) return false;
        this.step = step;
        this.taken = [];
        this.settling = true;
        return true;
    }
}

/**
 * How far the reader is about to go, from how fast they're going: a fling's speed decays about exponentially once
 * released, so its stopping distance is about its peak speed × τ (325 ms) — the lead to fetch ahead (as mip-chart
 * projects a pan). The speed smoothed (EWMA 0.45) from the top row's moves; a pause of 120 ms ends a fling, and so does
 * turning back. Rows and milliseconds.
 */
export class Velocity {
    static Tau = 325;
    static Smoothing = 0.45;
    static Gap = 120;

    constructor() {
        this.reset();
    }

    reset() {
        this.at = null;            // the last sample: { row, time }
        this.speed = 0;            // rows a millisecond, signed, smoothed
        this.peak = 0;             // the fling's fastest, and its way
        this.way = 0;
    }

    /** The top row is `row` at `time`. */
    sample(row, time) {
        const was = this.at;
        this.at = {row, time};
        if (!was) return;
        const dt = time - was.time;
        if (dt <= 0 || dt > Velocity.Gap) {             // (a pause: the fling is over)
            this.speed = this.peak = this.way = 0;
            return;
        }
        const v = (row - was.row) / dt, way = Math.sign(v);
        if (this.way !== 0 && way !== 0 && way !== this.way) this.peak = 0;   // (turned back)
        this.speed = Velocity.Smoothing * v + (1 - Velocity.Smoothing) * this.speed;
        if (Math.abs(this.speed) > this.peak) {
            this.peak = Math.abs(this.speed);
            this.way = Math.sign(this.speed);
        }
    }

    /** Rows the reader is about to cover, the way they're going (+ down, − up): the peak speed × τ. */
    get reach() {
        return this.way * this.peak * Velocity.Tau;
    }
}

/**
 * The window asked for with `rows` in view from `first`, the reader going `down` or up: pre-fetch skewed by direction,
 * the larger part ahead — the pre-fetch's own, or as far as the reader's flinging reaches (`lead`, rows), at most three
 * views; and the whole window no more than `most` rows. (At the top, none above: the server reads from the first row.)
 */
export function windowFor(first, rows, down, prefetch, lead = 0, most = Infinity) {
    first = Math.max(0, Math.floor(first));
    rows = Math.ceil(rows);
    const ahead = Math.max(prefetch.ahead, Math.min(Math.ceil(Math.abs(lead)), 3 * rows)), behind = prefetch.behind;
    let above = down ? behind : ahead, below = down ? ahead : behind;
    above = first === 0 ? 0 : Math.min(above, first);
    const over = rows + above + below - most;             // (the session's bound: the trailing side gives way first)
    if (over > 0) {
        const trail = Math.min(over, down ? above : below);
        if (down) above -= trail; else below -= trail;
        if (over > trail) if (down) below = Math.max(0, below - (over - trail)); else above = Math.max(0, above - (over - trail));
    }
    return {first, count: rows, before: above, after: below};
}

const sameWindow = (a, b) => a.first === b.first && a.count === b.count && a.before === b.before && a.after === b.after &&
    a.holds === b.holds && a.letIn === b.letIn && a.view === b.view;

/**
 * The windows asked for as the reader scrolls: what's wanted meanwhile coalesced, sent at most once a `minInterval`
 * (ms), and only while fewer than `maxOutstanding` are unanswered — the server and the link holding the page back when
 * they fall behind (Tapline conflates the answers on the wire, so a request needn't wait for the last). An answer
 * settles every request sent before it: superseded (the server reads the latest it has). Each answer is timed from its
 * asking, for the pre-fetch.
 */
export class Requests {
    constructor(send, {now = () => performance.now(), id, minInterval = 50, maxOutstanding = 4, setTimer = (fn, ms) => setTimeout(fn, ms)} = {}) {
        this.send = send;
        this.now = now;
        this.id = id ?? (() => globalThis.tapline?.correlation?.() ?? crypto.randomUUID().replaceAll('-', ''));
        this.minInterval = minInterval;
        this.maxOutstanding = maxOutstanding;
        this.setTimer = setTimer;
        this.wanted = null;
        this.sent = null;        // the last asked for
        this.outstanding = [];   // asked for and not answered, oldest first
        this.lastAt = -Infinity; // when the last was sent
        this.timer = null;       // the interval's end, while what's wanted waits for it
    }

    want(window) {
        this.wanted = window;
    }

    /**
     * Asks for what's wanted, if now's the time: the request sent, or null. Held by the interval, it asks again when
     * that ends; held by the requests unanswered, when one is answered (answered, then pump).
     */
    pump() {
        const w = this.wanted;
        if (!w || (this.sent && sameWindow(w, this.sent))) return null;
        if (this.outstanding.length >= this.maxOutstanding) return null;
        const wait = this.lastAt + this.minInterval - this.now();
        if (wait > 0) {
            this.timer ??= this.setTimer(() => {
                this.timer = null;
                this.pump();
            }, wait);
            return null;
        }

        const request = {...w, id: this.id(), at: this.now()};
        this.sent = request;
        this.lastAt = request.at;
        this.outstanding.push(request);
        this.send(request);
        return request;
    }

    /**
     * The request `id` answered: { took (ms), superseded (the ids sent before it, settled with it) } — null for one
     * already settled, or unknown.
     */
    answered(id) {
        const at = this.outstanding.findIndex(r => r.id === id);
        if (at < 0) return null;
        const took = this.now() - this.outstanding[at].at;
        const superseded = this.outstanding.slice(0, at).map(r => r.id);
        this.outstanding = this.outstanding.slice(at + 1);
        return {took, superseded};
    }

    /** Forgets what was asked: a new sort or filter asks afresh. The ids given up, to stop waiting for. */
    reset() {
        const ids = this.outstanding.map(r => r.id);
        this.sent = null;
        this.outstanding = [];
        return ids;
    }
}

function latin1(b, o, n) {
    let s = '';
    for (let i = 0; i < n; i++) s += String.fromCharCode(b[o + i]);
    return s;
}

const TWO32 = 4294967296;
const i64 = (dv, o) => dv.getInt32(o + 4, true) * TWO32 + dv.getUint32(o, true);

/**
 * A grid's order value (Tapline.Grid's GridOrder): u8 format (1) | u8 flags (1 live, 2 stale) | u8 the total's kind |
 * u8 key size | u32 keys | i64 first | i64 total | i64 let in | u32 holds | u32 view | the keys — each as the rows
 * table's replica keys it (its bytes as latin1). Null if it isn't one.
 */
export function readOrder(bytes) {
    if (!(bytes instanceof Uint8Array) || bytes.length < 40 || bytes[0] !== 1) return null;
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const count = dv.getInt32(4, true), keySize = bytes[3];
    if (count < 0 || count * keySize !== bytes.length - 40) return null;
    const keys = new Array(count);
    for (let i = 0, o = 40; i < count; i++, o += keySize) keys[i] = latin1(bytes, o, keySize);
    return {
        live: (bytes[1] & 1) !== 0, stale: (bytes[1] & 2) !== 0, totalKind: bytes[2], keySize, count,
        first: i64(dv, 8), total: i64(dv, 16), letIn: i64(dv, 24), holds: dv.getInt32(32, true), view: dv.getInt32(36, true),
        keys
    };
}

// ───────────── cells ─────────────

const group = digits => digits.replace(/\B(?=(\d{3})+(?!\d))/g, ',');

/**
 * A quantity, scaled by 10^scale, as finance writes it to at most `decimals` places (rounded half away from nought):
 * thousands grouped, no zero before the point of a fraction, none after its last digit that counts (0.2500 is .25),
 * nought a dash. Exact: a 64-bit quantity, worked in BigInt.
 */
export function finance(value, scale, decimals) {
    let v = BigInt(value);
    const negative = v < 0n;
    if (negative) v = -v;
    if (decimals < scale) {
        const div = 10n ** BigInt(scale - decimals);
        v = (v + div / 2n) / div;
        scale = decimals;
    }

    const digits = v.toString().padStart(scale + 1, '0');
    const whole = digits.slice(0, digits.length - scale), fraction = (scale > 0 ? digits.slice(-scale) : '').replace(/0+$/, '');
    if (whole === '0' && fraction === '') return NOUGHT;
    return (negative ? '-' : '') + (whole === '0' ? '' : group(whole)) + (fraction ? '.' + fraction : '');
}

/**
 * A price, scaled by 10^scale, to `figures` significant figures at most, as finance writes a quantity; in scientific
 * notation (1.23457e-5) where that would take more than `digits` digits.
 */
export function significant(value, scale, figures = 6, digits = 8) {
    let v = BigInt(value);
    if (v === 0n) return NOUGHT;
    const magnitude = (v < 0n ? -v : v).toString().length - 1 - scale;
    const text = finance(v, scale, Math.max(0, figures - 1 - magnitude));
    if (text.replace(/\D/g, '').length <= digits) return text;
    return (Number(v) / 10 ** scale).toExponential(figures - 1).replace(/\.?0+e/, 'e').replace('e+', 'e');
}

const pad = (n, w = 2) => String(n).padStart(w, '0');
const utf8 = new TextDecoder();

/** A column's integer: `size` bytes, little-endian, signed unless `unsigned` (8: a BigInt). */
function integer(dv, o, size, unsigned = false) {
    switch (size) {
        case 1:
            return unsigned ? dv.getUint8(o) : dv.getInt8(o);
        case 2:
            return unsigned ? dv.getUint16(o, true) : dv.getInt16(o, true);
        case 4:
            return unsigned ? dv.getUint32(o, true) : dv.getInt32(o, true);
        default:
            return unsigned ? dv.getBigUint64(o, true) : dv.getBigInt64(o, true);
    }
}

/** A float32's shortest text that reads back as it: 0.1, not 0.10000000149011612. */
function shortest32(v) {
    for (let p = 1; p < 9; p++) {
        const text = v.toPrecision(p);
        if (Math.fround(Number(text)) === v) return String(Number(text));
    }
    return String(Number(v.toPrecision(9)));
}

const TICKS_1970 = 621_355_968_000_000_000n, TICKS_MASK = (1n << 62n) - 1n;
const hex2 = b => b.toString(16).padStart(2, '0');

const NONE = {1: -128, 2: -32768, 4: -2147483648, 8: -(2n ** 63n)};

/**
 * What a cell shows, and on hover: { text, title, cut } from the row's bytes, by the column's kind —
 * int (an integer, `size` bytes: 1, 2, 4, 8; `unsigned`), fixed (scaled by 10^scale: to `decimals` places, or
 * `figures` significant), float (`size` 8 or 4), time (µs since 1970, or with `unit` 'ticks' a .NET DateTime; shown as
 * UTC: today's date an ellipsis, the microseconds on hover), bool, enum (the column's `dictionary` entry for the
 * value), text (`size` bytes: a length, 0x80 in it if the text was cut, then UTF-8), percent (of one integer over
 * another: `of` and `over`, a column's name or { offset, size, unsigned }), uuid (a .NET Guid's 16 bytes). A signed
 * integer's least value is no value, as are a zero DateTime and an empty Guid.
 */
export function formatCell(column, row, dv, columns, now = new Date()) {
    const o = column.offset ?? 0, size = column.size ?? 8;
    switch (column.kind) {
        case 'int': {
            const v = integer(dv, o, size, column.unsigned);
            if (!column.unsigned && (v === NONE[size] || v === BigInt(NONE[8]))) return {text: ''};
            return {text: group(String(v).replace('-', '')).replace(/^/, v < 0 ? '-' : '')};
        }
        case 'fixed': {
            const v = BigInt(integer(dv, o, size, column.unsigned));
            if (!column.unsigned && v === BigInt(NONE[size])) return {text: ''};
            const scale = column.scale ?? 0;
            const text = column.figures ? significant(v, scale, column.figures) : finance(v, scale, column.decimals ?? scale);
            return {text, title: finance(v, scale, scale)};
        }
        case 'float': {
            const v = column.size === 4 ? dv.getFloat32(o, true) : dv.getFloat64(o, true);
            if (Number.isNaN(v)) return {text: ''};
            const exact = column.size === 4 ? shortest32(v) : String(v);
            const text = v.toFixed(column.decimals ?? 2).replace(/\.?0+$/, '');
            const [whole, fraction] = text.replace('-', '').split('.');
            if (/^0*$/.test(whole) && !fraction) return {text: NOUGHT, title: exact};
            return {text: (v < 0 ? '-' : '') + (/^0+$/.test(whole) ? '' : group(whole)) + (fraction ? '.' + fraction : ''), title: exact};
        }
        case 'time': {
            let v;
            if (column.unit === 'ticks') {                     // (a DateTime: 100 ns since 0001, its kind in the top two bits)
                const ticks = dv.getBigUint64(o, true) & TICKS_MASK;
                if (ticks === 0n) return {text: ''};
                v = (ticks - TICKS_1970) / 10n;
            } else {
                v = dv.getBigInt64(o, true);
                if (v === NONE[8]) return {text: ''};
            }
            const d = new Date(Number(v / 1000n)), micros = Number(((v % 1_000_000n) + 1_000_000n) % 1_000_000n);
            const date = `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
            const time = `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
            const today = date === `${now.getUTCFullYear()}-${pad(now.getUTCMonth() + 1)}-${pad(now.getUTCDate())}`;
            return {text: today ? `…${time}` : `${date} ${time}`, title: `${date} ${time}.${pad(micros, 6)} UTC`};
        }
        case 'bool':
            return dv.getUint8(o) ? {text: '✓', title: 'yes'} : {text: '', title: 'no'};
        case 'enum': {
            const v = integer(dv, o, column.size ?? 1, column.unsigned ?? true);
            return {text: column.dictionary?.[String(v)] ?? `#${v}`};
        }
        case 'text': {
            const n = row[o] & 0x7f, cut = (row[o] & 0x80) !== 0;
            const text = utf8.decode(row.subarray(o + 1, o + 1 + Math.min(n, size - 1)));
            return cut ? {text: text + '…', title: text + '…', cut: true} : {text, title: text.length > 12 ? text : undefined};
        }
        case 'percent': {
            const field = f => typeof f === 'string' ? columns.find(c => c.name === f) : f;
            const of = field(column.of), over = field(column.over);
            if (!of || !over) return {text: ''};
            const a = Number(integer(dv, of.offset ?? 0, of.size ?? 8, of.unsigned)), b = Number(integer(dv, over.offset ?? 0, over.size ?? 8, over.unsigned));
            if (!(a > 0 && b > 0)) return {text: NOUGHT};
            return {text: finance(BigInt(Math.round(a / b * 100 * 10 ** (column.decimals ?? 1))), column.decimals ?? 1, column.decimals ?? 1) + '%'};
        }
        case 'uuid': {
            const b = row.subarray(o, o + 16);
            if (b.every(x => x === 0)) return {text: ''};
            const h = i => hex2(b[i]);
            const text = `${h(3)}${h(2)}${h(1)}${h(0)}-${h(5)}${h(4)}-${h(7)}${h(6)}-${h(8)}${h(9)}-` +
                `${h(10)}${h(11)}${h(12)}${h(13)}${h(14)}${h(15)}`;
            return {text, title: text};
        }
        default:
            return {text: ''};
    }
}

// ───────────── the body ─────────────

const QUIET_MS = 1_000;
const HEARD_KEPT_MS = 60_000;              // (when a change was heard of, kept this long: longer than any flash)
const STAMP_SKEW_MS = 2_000;               // (a row arriving stamped further ahead than this: the page's clock is behind)

/** A row's arrival, by its `stamp` field ({ offset, size, unit }, as a time column's), in ms since 1970: null for none. */
export function stampOf(stamp, row, dv) {
    const o = stamp.offset ?? 0;
    if (o + 8 > row.length) return null;
    if (stamp.unit === 'ticks') {                          // (a .NET DateTime: 100 ns since 0001, its kind in the top bits)
        const ticks = dv.getBigUint64(o, true) & TICKS_MASK;
        return ticks === 0n ? null : Number((ticks - TICKS_1970) / 10n) / 1000;   // (µs, exact, then ms)
    }
    const v = dv.getBigInt64(o, true);                     // (µs since 1970)
    return v === NONE[8] ? null : Number(v) / 1000;
}

/**
 * A flash `elapsed` ms after what it shows happened, `duration` ms long: the animation delay that shows it where it has
 * got to (negative: under way), or null if it's over — so pages side by side, or one reloaded, show it alike.
 */
export function flashDelay(elapsed, duration) {
    if (!(duration > 0) || elapsed >= duration) return null;
    return elapsed > 0 ? -elapsed : 0;
}

/** A CSS time's milliseconds ('5s', '2400ms'; the first of a list): 0 for none. */
export function cssMs(time) {
    const m = /^\s*(-?[\d.]+)(ms|s)/.exec(time ?? '');
    return m ? Number(m[1]) * (m[2] === 's' ? 1000 : 1) : 0;
}
const NUMERIC = new Set(['int', 'fixed', 'float', 'percent']);
let grids = 0;                             // (ids for rows' elements: aria-activedescendant names one)

/**
 * A LiveGrid's body: the rows of its window, drawn from the replica of its watch (rows table, order value), in an
 * element the chrome leaves empty. Options:
 *   columns    [{ name, label, kind, offset, size, unsigned, scale, decimals, figures, unit, dictionary, of, over, width,
 *                frozen, align, link }] (formatCell)
 *   rowHeight  px, one for every row
 *   tapline    the page's TaplineClient (tapline.js; globalThis.tapline if none); binding: the grid's watch (its
 *              session's Binding)
 *   request(request)   sends a window request to the session: { id, first, count, before, after, holds, letIn, view }
 *   maxRows    the session's most rows a window (GridOptions.MaxWindowRows): never asked for more
 *   minInterval, maxOutstanding  requests at most once this many ms (50), and while fewer are unanswered (4)
 *   visible(visible)   the page shown or hidden (the session stops reading the source's changes while hidden)
 *   status(status)     what the chrome shows: { total, totalKind, live, stale, holds, letIn, fresh, blanks }
 *   host       the grid's element, around the body and its headings (the body's own if none): the widths, frozen
 *              offsets and row height are its custom properties (--tg-w<i>, --tg-left<i>, --tg-width, --tg-row-height,
 *              --tg-scrollbar), it takes the keys, and it's the grid to assistive technology (aria-rowcount,
 *              aria-activedescendant)
 *   head       the headings' element: scrolled sideways with the rows (its frozen headings sticky in it)
 *   headerRows rows of headings above the body: the rows' aria-rowindex counts them
 *   view       the view (sort and filter) asked in at first: its number (GridSession.SetView); orders of an earlier
 *              one are passed over
 *   arrivedAt  the rows' arrival time, if they carry one ({ offset, size, unit }, as a time column's): a row's arrival
 *              flash runs from it wherever the row is drawn — pages side by side, or one reloaded, flash alike
 *              (unless a row arrives stamped more than 2 s in the page's future: its clock is behind, and the page
 *              runs flashes from when it heard of the changes, as it does without stamps)
 *   selection  'none', 'single' or 'multiple'; selected: the keys selected at first (setSelection after)
 *   rowClick({ key, column, ctrl, shift, meta, alt }), rowActivate({ key, column }), rowSelect({ key })
 *              a row clicked, activated (a double click, Enter, a link column's cell) or selected (Space): by key, its
 *              column named where there is one
 */
export class GridBody {
    constructor(root, options) {
        this.root = root;
        this.options = options;
        this.host = options.host ?? root;
        this.head = options.head ?? null;
        this.headerRows = options.headerRows ?? 0;
        this.columns = options.columns;
        this.rowHeight = options.rowHeight ?? 30;
        this.tapline = options.tapline ?? globalThis.tapline;
        this.tapId = options.binding.tapId ?? options.binding;
        this.scroller = new Scroller(this.rowHeight, options.safeHeight ?? SAFE_HEIGHT);
        this.prefetch = new Prefetch();
        this.velocity = new Velocity();
        this.requests = new Requests(request => this.#ask(request),
            {minInterval: options.minInterval ?? 50, maxOutstanding: options.maxOutstanding ?? 4});
        this.view = options.view ?? 0;     // the view asked in: its number
        this.order = null;                 // the order applied: { first, keys, total, live, letIn, holds, view, … }
        this.table = null;                 // the rows table's replica
        this.pool = [];                    // row elements: { el, cells, key, bytes, last, position, y, selected, focused }
        this.rowOf = new Map();            // a row element → its pool entry
        this.changed = new Set();          // keys changed since last drawn
        this.whole = true;                 // a state handed over whole: every row drawn afresh
        this.arrived = new Map();          // keys new at the live top → when heard of (ms since 1970), till drawn
        this.changedAt = new Map();        // keys changed → when heard of: their cells' flashes run from it
        this.stamp = options.arrivedAt ?? null;   // the rows' own arrival time, if they carry one (arrivedAt)
        this.stamped = !!this.stamp;       // (its stamps trusted: none from the page's future yet)
        this.durations = new Map();        // a flash's duration as the page's CSS has it (ms)
        this.down = true;                  // the reader's direction
        this.frame = 0;                    // requestAnimationFrame id, while one is due
        this.widths = new Map();           // column name → px: given, or measured once from the first rows answered, then held
        this.blanks = 0;                   // frames with a placeholder in view (blank sightings)
        this.fresh = 'unknown';            // 'fresh' (Tap.Sync answered) | 'checking' | 'behind' | 'unknown'
        this.hidden = false;
        this.disposed = false;
        this.waits = new Map();            // request id → AbortController of its rendezvous wait
        this.selection = options.selection ?? 'none';
        this.selected = new Set(options.selected ?? []);
        this.focusKey = null;              // the row the keys move from, by key: followed as it moves
        this.focusAt = null;               // its position, as last known (or one moved to and not answered yet)
        this.active = null;                // the focused row's element id, as aria-activedescendant has it
        this.scrollLeft = 0;
        this.rowCount = -1;
        this.id = `tg${++grids}`;
    }

    /** Builds the body, subscribes to the watch, and asks for the first window — measured first, in the same task. */
    start() {
        const root = this.root, host = this.host;
        root.classList.add('tapline-grid');
        host.style.setProperty('--tg-row-height', `${this.rowHeight}px`);
        root.replaceChildren();
        this.scrollerEl = Object.assign(document.createElement('div'), {className: 'tg-scroller', tabIndex: host === root ? 0 : -1});
        this.surfaceEl = Object.assign(document.createElement('div'), {className: 'tg-surface'});
        this.layerEl = Object.assign(document.createElement('div'), {className: 'tg-rows'});
        this.scrollerEl.setAttribute('role', 'rowgroup');
        this.surfaceEl.append(this.layerEl);
        this.scrollerEl.append(this.surfaceEl);
        root.append(this.scrollerEl);
        this.keys = host === root ? this.scrollerEl : host;   // (what takes the keys: the grid, or the body alone)
        this.#styleColumns();

        this.onScroll = () => this.#scrolled();
        this.onWheel = e => this.#wheel(e);
        this.onKey = e => this.#key(e);
        this.onClick = e => this.#click(e);
        this.onDouble = e => this.#double(e);
        this.onFocus = () => this.keys.focus({preventScroll: true});   // (clicked: the grid takes the focus, not its scroller)
        this.onVisibility = () => this.#visibility();
        this.scrollerEl.addEventListener('scroll', this.onScroll, {passive: true});
        this.scrollerEl.addEventListener('wheel', this.onWheel, {passive: false});
        this.keys.addEventListener('keydown', this.onKey);
        this.layerEl.addEventListener('click', this.onClick);
        this.layerEl.addEventListener('dblclick', this.onDouble);
        if (this.keys !== this.scrollerEl) this.scrollerEl.addEventListener('focus', this.onFocus);
        document.addEventListener('visibilitychange', this.onVisibility);
        this.hidden = document.visibilityState === 'hidden';
        this.resizer = new ResizeObserver(() => this.#resized());
        this.resizer.observe(this.scrollerEl);

        this.off = this.tapline.subscribe(this.options.binding, (state, id, info) => this.#state(state, info),
            error => this.#ended(error), undefined, {changed: true});
        this.#resized();                   // measured, then the first window asked for: never "asked for nothing"
    }

    /**
     * A new sort or filter, the `view`th (the next, if not said): back to the top, its rows placeholders until its top
     * arrives — unless its order came first, and was taken as the word of it.
     */
    reset(view = this.view + 1) {
        if (view <= this.view) return;
        this.#newView(view);
        this.order = null;
        this.whole = true;
        this.#want();
        this.#schedule();
    }

    /** A new set of columns: drawn afresh; widths measured for those not measured before. */
    setColumns(columns) {
        this.columns = columns;
        for (const r of this.pool) r.el.remove();
        this.pool = [];
        this.rowOf.clear();
        this.whole = true;
        this.#styleColumns();
        this.#schedule();
    }

    /** The keys selected (the grid's word, after a click's or a key's shown at once). */
    setSelection(keys) {
        this.selected = new Set(keys);
        this.#paint();
    }

    /** Brings a row into view (LiveGrid.RevealAsync): `position` placed `above` rows from the top, and focused. */
    reveal(position, above, key) {
        this.focusKey = key ?? null;
        this.focusAt = position;
        this.velocity.reset();
        this.down = true;
        this.scrollerEl.scrollTop = this.scroller.to(clamp(position - (above ?? 0), 0, Math.max(0, this.scroller.lastTop)));
        this.#want();
        this.#schedule();
    }

    dispose() {
        this.disposed = true;
        this.off?.();
        for (const id of [...this.waits.keys()]) this.#settle(id);
        cancelAnimationFrame(this.frame);
        clearTimeout(this.quiet);
        this.resizer?.disconnect();
        this.scrollerEl?.removeEventListener('scroll', this.onScroll);
        this.scrollerEl?.removeEventListener('wheel', this.onWheel);
        this.scrollerEl?.removeEventListener('focus', this.onFocus);
        this.keys?.removeEventListener('keydown', this.onKey);
        this.layerEl?.removeEventListener('click', this.onClick);
        this.layerEl?.removeEventListener('dblclick', this.onDouble);
        document.removeEventListener('visibilitychange', this.onVisibility);
        if (this.host !== this.root) this.host.removeAttribute('aria-activedescendant');
        this.root.replaceChildren();
    }

    // ── what arrives ──

    #state(group, info) {
        const [table, orderBytes] = group.members;
        if (table) this.table = table;
        const changed = info?.changed, now = Date.now();
        if (orderBytes instanceof Uint8Array && (!changed || changed[1] !== false)) this.#order(readOrder(orderBytes), now);
        if (changed?.[0] instanceof Set) {
            for (const key of changed[0]) {
                this.changed.add(key);
                this.changedAt.delete(key); // (to the end: the map kept in the order heard)
                this.changedAt.set(key, now);
            }
        } else this.whole = true;          // (handed over whole: drawn whole)
        this.#forget(now);
        if (this.fresh !== 'fresh' && !info?.stale) {
            this.fresh = 'fresh';          // (a state from the router; quiet a second, Tap.Sync says whether there's more)
            this.#report();
        }
        this.#quiet();
        this.#schedule();
    }

    #order(order, now) {
        if (!order || order.view < this.view) return;   // (of a view replaced)
        if (order.view > this.view) {                    // (the new view's, ahead of the word of it: taken as that)
            this.#newView(order.view);
            this.order = null;
            this.whole = true;
        }
        const was = this.order;
        if (order.total !== this.scroller.total) {
            this.scroller.setTotal(order.total);
            this.surfaceEl.style.height = `${this.scroller.height}px`;
        }
        if (order.total + this.headerRows !== this.rowCount)
            this.host.setAttribute('aria-rowcount', String(this.rowCount = order.total + this.headerRows));
        // Rows let in above (in this hold): the page moves its scroll down by as many, the rows in view staying put.
        if (was && order.holds === was.holds && order.letIn > was.letIn)
            this.scrollerEl.scrollTop = this.scroller.by(order.letIn - was.letIn);
        // Rows arrived at the live top, above the row that was first: they flash once drawn, from when they arrived.
        if (was && order.live && was.live && was.keys.length) {
            const before = new Set(was.keys), top = order.keys.indexOf(was.keys[0]);
            for (let i = 0; i < top; i++) {
                const key = order.keys[i];
                if (before.has(key)) continue;
                this.arrived.set(key, now);
                if (this.stamped) this.#checkStamp(key, now);
            }
        }
        // The focused row, by key: where it stands now.
        if (this.focusKey !== null) {
            const at = order.keys.indexOf(this.focusKey);
            if (at >= 0) this.focusAt = order.first + at;
        }
        this.order = order;
        this.#report();
    }

    /** A live arrival's stamp against the page's clock: from its future, the page's clock is behind — stamps unused. */
    #checkStamp(key, now) {
        const row = this.table?.rows.get(key)?.row;
        const at = row ? stampOf(this.stamp, row, new DataView(row.buffer, row.byteOffset, row.byteLength)) : null;
        if (at !== null && at - now > STAMP_SKEW_MS) this.stamped = false;
    }

    /** When a row arrived, to run its arrival flash from: its stamp (trusted), else when the page heard of it. */
    #arrivedAt(key, row) {
        if (!this.stamped) return this.arrived.get(key);
        return stampOf(this.stamp, row, new DataView(row.buffer, row.byteOffset, row.byteLength)) ?? undefined;
    }

    /** Forgets when changes were heard of, once older than any flash. */
    #forget(now) {
        for (const heard of [this.arrived, this.changedAt])
            for (const [key, at] of heard) {
                if (now - at < HEARD_KEPT_MS) break;
                heard.delete(key);
            }
    }

    #newView(view) {
        this.view = view;
        for (const id of this.requests.reset()) this.#settle(id);
        this.arrived.clear();
        this.changedAt.clear();
        this.velocity.reset();
        this.focusAt = null;               // (the focused row, by key, placed again where the new view has it)
        this.scrollerEl.scrollTop = this.scroller.to(0);
    }

    #ended(error) {
        this.options.status?.({ended: error});
    }

    // ── asking ──

    #want() {
        if (this.hidden) {
            this.requests.want(null);      // (nothing asked while hidden; shown, asked afresh)
            return;
        }
        const window = windowFor(this.scroller.row, this.scroller.viewRows, this.down, this.prefetch, this.velocity.reach,
            this.options.maxRows ?? Infinity);
        this.requests.want({...window, holds: this.order?.holds ?? 0, letIn: this.order?.letIn ?? 0, view: this.view});
        this.requests.pump();
    }

    #ask(request) {
        const wait = new AbortController();
        this.waits.set(request.id, wait);
        this.tapline.rendezvous(request.id, {tapId: this.tapId, signal: wait.signal}).then(() => {
            this.waits.delete(request.id);
            const answered = this.requests.answered(request.id);
            if (!answered) return;
            for (const id of answered.superseded) this.#settle(id);   // (never to be answered: the server read a later one)
            if (this.prefetch.observe(answered.took)) this.#want();
            else this.requests.pump();                              // (what was wanted meanwhile)
        }, () => this.waits.delete(request.id));
        this.options.request(request);
    }

    /** Stops waiting for a request's rendezvous. */
    #settle(id) {
        this.waits.get(id)?.abort();
        this.waits.delete(id);
    }

    // ── the reader ──

    #resized() {
        const height = this.scrollerEl.clientHeight;
        this.host.style.setProperty('--tg-scrollbar', `${this.scrollerEl.offsetWidth - this.scrollerEl.clientWidth}px`);
        if (height === this.scroller.viewHeight) return;
        this.scroller.resize(height);
        this.prefetch.see(this.scroller.viewRows);
        this.layerEl.style.height = `${height}px`;
        this.#want();
        this.#schedule();
    }

    #scrolled() {
        const left = this.scrollerEl.scrollLeft;
        if (left !== this.scrollLeft) {    // (sideways: the headings follow)
            this.scrollLeft = left;
            if (this.head) this.head.scrollLeft = left;
            else this.host.style.setProperty('--tg-scroll-left', `${left}px`);
        }
        const was = this.scroller.row;
        const row = this.scroller.scrolled(this.scrollerEl.scrollTop);
        if (row === was) return;
        this.down = row > was;
        this.velocity.sample(row, performance.now());
        this.#want();
        this.#schedule();
    }

    #wheel(e) {
        if (!this.scroller.scaled || e.ctrlKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;   // (native, a zoom, sideways)
        e.preventDefault();
        const px = e.deltaMode === 1 ? e.deltaY * this.rowHeight : e.deltaMode === 2 ? e.deltaY * this.scroller.viewHeight : e.deltaY;
        this.#move(px / this.rowHeight);
    }

    /** Keys move the focused row (and the view with it), Space selects it, Enter activates it. */
    #key(e) {
        if (e.altKey || e.metaKey || (e.target !== this.keys && !this.scrollerEl.contains(e.target))) return;   // (a heading's)
        const page = Math.max(1, Math.floor(this.scroller.viewRows) - 1);
        const rows = {ArrowDown: 1, ArrowUp: -1, PageDown: page, PageUp: -page, Home: -Infinity, End: Infinity}[e.key];
        if (rows !== undefined && (!e.ctrlKey || !Number.isFinite(rows))) {
            e.preventDefault();
            this.#focusBy(rows);
        } else if ((e.key === ' ' || e.key === 'Enter') && !e.ctrlKey && this.focusKey !== null) {
            e.preventDefault();
            if (e.key === 'Enter') {
                this.options.rowActivate?.({key: this.focusKey});
                return;
            }
            this.#select(this.focusKey, this.selection === 'multiple');
            this.options.rowSelect?.({key: this.focusKey});
            this.#paint();
        }
    }

    /** The focus moved by `rows` (±Infinity: to the first or last), from the first row in view if there's none. */
    #focusBy(rows) {
        const total = this.scroller.total;
        if (!total) return;
        const top = this.scroller.row, view = this.scroller.viewRows;
        const at = !Number.isFinite(rows) ? (rows > 0 ? total - 1 : 0)
            : this.focusAt === null ? Math.min(total - 1, Math.ceil(top))
                : clamp(this.focusAt + rows, 0, total - 1);
        this.focusAt = at;
        this.focusKey = this.#keyAt(at) ?? null;
        if (at < top) this.scrollerEl.scrollTop = this.scroller.to(at);
        else if (at + 1 > top + view) this.scrollerEl.scrollTop = this.scroller.to(Math.max(0, at + 1 - view));
        this.down = rows > 0;
        this.velocity.sample(this.scroller.row, performance.now());
        this.#want();
        this.#schedule();
    }

    #move(rows) {
        this.down = rows > 0;
        this.scrollerEl.scrollTop = Number.isFinite(rows) ? this.scroller.by(rows) : this.scroller.to(rows > 0 ? this.scroller.lastTop : 0);
        this.velocity.sample(this.scroller.row, performance.now());
        this.#want();
        this.#schedule();
    }

    #click(e) {
        const r = this.#answered(e.target);
        if (!r) return;
        const column = this.#columnOf(r, e.target);
        this.focusKey = r.key;
        this.focusAt = r.position;
        if (column?.link) this.options.rowActivate?.({key: r.key, column: column.name});
        else {
            this.#select(r.key, this.selection === 'multiple' && (e.ctrlKey || e.metaKey));
            this.options.rowClick?.({key: r.key, column: column?.name, ctrl: e.ctrlKey, shift: e.shiftKey, meta: e.metaKey, alt: e.altKey});
        }
        this.#paint();
    }

    #double(e) {
        const r = this.#answered(e.target), column = r && this.#columnOf(r, e.target);
        if (r && !column?.link) this.options.rowActivate?.({key: r.key, column: column?.name});
    }

    /** The row an event's target is in, if it's answered (a placeholder's is no one's). */
    #answered(target) {
        const r = this.rowOf.get(target.closest?.('.tg-row'));
        return r && r.key != null ? r : undefined;
    }

    #columnOf(r, target) {
        const i = r.cells.indexOf(target.closest?.('.tg-cell'));
        return i >= 0 ? this.columns[i] : undefined;
    }

    /** A selection as the grid will make it, shown at once: one row, or one toggled among several. */
    #select(key, toggle) {
        if (this.selection === 'none') return;
        if (toggle) {
            if (!this.selected.delete(key)) this.selected.add(key);
        } else this.selected = new Set([key]);
    }

    #keyAt(position) {
        const o = this.order, i = o ? position - o.first : -1;
        return o && i >= 0 && i < o.keys.length ? o.keys[i] : undefined;
    }

    #visibility() {
        this.hidden = document.visibilityState === 'hidden';
        this.options.visible?.(!this.hidden);
        this.velocity.reset();
        this.#want();
        if (!this.hidden) this.#schedule();
    }

    // ── freshness: once quiet, the router asked whether the page has all it published (Tap.Sync) ──

    #quiet() {
        clearTimeout(this.quiet);
        this.quiet = setTimeout(() => this.#sync(), QUIET_MS);
    }

    #sync() {
        if (this.disposed || this.hidden || !this.tapline.sync) return;
        this.fresh = 'checking';
        this.#report();
        const late = setTimeout(() => {
            this.fresh = 'behind';
            this.#report();
        }, QUIET_MS);
        this.tapline.sync(this.tapId).then(() => {
            clearTimeout(late);
            this.fresh = 'fresh';
            this.#report();
        }, () => clearTimeout(late));
    }

    #report() {
        const o = this.order;
        this.options.status?.({
            total: o?.total ?? 0, totalKind: o?.totalKind ?? 0, live: o?.live ?? true, stale: o?.stale ?? false,
            holds: o?.holds ?? 0, letIn: o?.letIn ?? 0, fresh: this.fresh, blanks: this.blanks
        });
    }

    // ── drawing: once a frame, only what changed or moved ──

    /** Draws now what the next frame would: for a test, or a page that can't wait for a frame (one in the background). */
    draw() {
        cancelAnimationFrame(this.frame);
        this.#draw();
    }

    #schedule() {
        if (!this.frame && !this.disposed) this.frame = requestAnimationFrame(() => this.#draw());
    }

    #draw() {
        this.frame = 0;
        const top = this.scroller.row, first = Math.floor(top), rows = Math.ceil(this.scroller.viewRows) + 1;
        const offset = (first - top) * this.rowHeight, order = this.order, table = this.table;
        const changed = this.changed, whole = this.whole, now = Date.now();
        this.changed = new Set();
        this.whole = false;
        while (this.pool.length < rows) this.pool.push(this.#rowElement());
        if (order && table && order.keys.length && this.columns.some(c => !c.width && !this.widths.has(c.name)))
            this.#measure(order, table);
        let blank = false, active = null;
        for (let i = 0; i < this.pool.length; i++) {
            const r = this.pool[i], position = first + i;
            if (i >= rows || position >= this.scroller.total) {
                if (!r.el.hidden) r.el.hidden = true;
                continue;
            }
            if (r.el.hidden) r.el.hidden = false;
            const y = offset + i * this.rowHeight;
            if (r.y !== y) r.el.style.transform = `translateY(${(r.y = y)}px)`;
            if (r.position !== position)
                r.el.setAttribute('aria-rowindex', String((r.position = position) + 1 + this.headerRows));
            const at = order ? position - order.first : -1;
            const key = order && at >= 0 && at < order.keys.length ? order.keys[at] : undefined;
            const entry = key !== undefined ? table?.rows.get(key) : undefined;
            if (!entry) {
                if (r.key !== null) this.#placeholder(r);
                if (position < top + this.scroller.viewRows) blank = true;
            } else if (whole || r.key !== key || r.bytes !== entry.row || changed.has(key)) {
                // Changed in place, its cells that differ flash; arrived, or (stamped) drawn here newly, the row does —
                // each from when it happened (scrolled into view unstamped: no flash).
                const drawn = r.key !== key, inPlace = !drawn && changed.has(key);
                this.#fill(r, key, entry.row, inPlace, now - (this.changedAt.get(key) ?? now));
                const arrived = drawn || this.arrived.has(key) ? this.#arrivedAt(key, entry.row) : undefined;
                this.arrived.delete(key);
                if (arrived !== undefined) this.#flash(r.el, 'tg-arrived', now - arrived);
            }
            if (this.focusKey === null && this.focusAt === position && entry) this.focusKey = key;   // (moved to, now answered)
            if (this.#mark(r)) active = r.el.id;
        }
        this.#activate(active);
        if (blank) this.blanks++;
    }

    /** Selection and focus on the rows drawn, without drawing them again. */
    #paint() {
        let active = null;
        for (const r of this.pool) if (!r.el.hidden && this.#mark(r)) active = r.el.id;
        this.#activate(active);
    }

    /** A row's selected and focused marks, as its key and position now say: true if it's the focused one. */
    #mark(r) {
        const selected = r.key != null && this.selected.has(r.key);
        if (r.selected !== selected) {
            r.selected = selected;
            r.el.classList.toggle('tg-selected', selected);
        }
        if (this.selection !== 'none' && r.el.getAttribute('aria-selected') !== String(selected))
            r.el.setAttribute('aria-selected', String(selected));
        const focused = this.focusKey !== null ? r.key === this.focusKey : this.focusAt !== null && r.position === this.focusAt;
        if (r.focused !== focused) r.el.classList.toggle('tg-focused', r.focused = focused);
        return focused;
    }

    #activate(id) {
        if (id === this.active) return;
        this.active = id;
        if (id) this.keys.setAttribute('aria-activedescendant', id);
        else this.keys.removeAttribute('aria-activedescendant');
    }

    #rowElement() {
        const el = document.createElement('div');
        el.className = 'tg-row';
        el.id = `${this.id}-r${this.pool.length}`;
        el.setAttribute('role', 'row');
        const cells = this.columns.map((c, i) => {
            const cell = document.createElement('span');
            cell.className = 'tg-cell' + (c.frozen ? ' tg-frozen' : '') + (c.link ? ' tg-link' : '') +
                (c.align === 'right' || (c.align !== 'left' && NUMERIC.has(c.kind)) ? ' tg-right' : '');
            cell.style.width = `var(--tg-w${i}, ${c.width ?? 120}px)`;
            if (c.frozen) cell.style.left = `var(--tg-left${i}, 0px)`;
            cell.setAttribute('role', 'gridcell');
            el.append(cell);
            return cell;
        });
        this.layerEl.append(el);
        const r = {el, cells, key: undefined, bytes: null, last: null, position: -1, y: NaN, selected: false, focused: false};
        this.rowOf.set(el, r);
        return r;
    }

    #placeholder(r) {
        r.key = null;
        r.bytes = r.last = null;
        r.el.classList.add('tg-placeholder');
        for (const cell of r.cells) cell.textContent = '';
    }

    /** Writes a row's cells; changed in place since last drawn, the cells that differ flash. */
    #fill(r, key, row, inPlace, changedFor) {
        const dv = new DataView(row.buffer, row.byteOffset, row.byteLength), now = new Date();
        r.el.classList.remove('tg-placeholder');
        if (r.key !== key) {               // (an element taking another row: not the last one's flashes)
            r.el.classList.remove('tg-arrived-a', 'tg-arrived-b');
            for (const cell of r.cells) cell.classList.remove('tg-changed-a', 'tg-changed-b');
        }
        const texts = r.last && inPlace ? r.last : null, last = new Array(this.columns.length);
        for (let i = 0; i < this.columns.length; i++) {
            const {text, title} = formatCell(this.columns[i], row, dv, this.columns, now), cell = r.cells[i];
            if (cell.textContent !== text) cell.textContent = text;   // (text, never HTML)
            if (title) cell.title = title; else if (cell.title) cell.removeAttribute('title');
            if (texts && texts[i] !== text) this.#flash(cell, 'tg-changed', changedFor);
            last[i] = text;
        }
        r.key = key;
        r.bytes = row;
        r.last = last;
    }

    /**
     * Flashes an element, `elapsed` ms after what it shows happened: started where it has got to (a negative delay),
     * none if it's over. Two animations of the one name alternate, so a flash under way starts again without a reflow.
     */
    #flash(el, name, elapsed = 0) {
        const delay = flashDelay(elapsed, this.#duration(name));
        if (delay === null) return;
        const a = el.classList.contains(`${name}-a`);
        el.style.animationDelay = delay ? `${delay}ms` : '';
        el.classList.remove(`${name}-a`, `${name}-b`);
        el.classList.add(`${name}-${a ? 'b' : 'a'}`);
    }

    /** A flash's duration as the page's CSS has it (tg-changed, tg-arrived; --tg-flash-for), looked up once. */
    #duration(name) {
        let ms = this.durations.get(name);
        if (ms === undefined) {
            const probe = document.createElement('span');
            probe.className = `${name}-a`;
            this.layerEl.append(probe);
            ms = cssMs(getComputedStyle(probe).animationDuration);
            probe.remove();
            this.durations.set(name, ms);
        }
        return ms;
    }

    // ── columns: widths and frozen offsets, the module's, as custom properties the headings read too ──

    #styleColumns() {
        this.columns.forEach((c, i) => {
            const width = c.width ?? this.widths.get(c.name);
            if (width) this.host.style.setProperty(`--tg-w${i}`, `${width}px`);
            else this.host.style.removeProperty(`--tg-w${i}`);
        });
        this.#placeColumns();
    }

    /**
     * Widths from the first rows answered, for columns with none given or measured before: as wide as their widest
     * cell, and their heading — its label, and whatever the chrome put in it (a filter) — with room for its sort arrow
     * and rank.
     */
    #measure(order, table) {
        const probe = document.createElement('span');
        probe.className = 'tg-cell tg-probe';
        this.layerEl.append(probe);
        const headings = this.head?.querySelectorAll('.tg-heading');
        this.columns.forEach((c, i) => {
            if (c.width || this.widths.has(c.name)) return;
            probe.textContent = c.label ?? c.name;
            let widest = probe.getBoundingClientRect().width + 28;   // (the arrow and a multi-column sort's rank)
            const heading = headings?.[i]?.cloneNode(true);          // (a copy, laid out at its own width, unseen)
            if (heading) {
                heading.style.cssText = 'position: absolute; visibility: hidden; width: max-content';
                this.layerEl.append(heading);
                widest = Math.max(widest, heading.getBoundingClientRect().width + 20);
                heading.remove();
            }
            for (const key of order.keys.slice(0, 200)) {
                const entry = table.rows.get(key);
                if (!entry) continue;
                const dv = new DataView(entry.row.buffer, entry.row.byteOffset, entry.row.byteLength);
                probe.textContent = formatCell(c, entry.row, dv, this.columns).text;
                widest = Math.max(widest, probe.getBoundingClientRect().width + 16);
            }
            widest = Math.ceil(widest);
            this.widths.set(c.name, widest);
            this.host.style.setProperty(`--tg-w${i}`, `${widest}px`);
        });
        probe.remove();
        this.#placeColumns();
    }

    /** A frozen column stands as far from the left as the frozen columns before it are wide; the rows as wide as them all. */
    #placeColumns() {
        let left = 0, width = 0;
        this.columns.forEach((c, i) => {
            const w = c.width ?? this.widths.get(c.name) ?? 120;
            if (c.frozen) {
                this.host.style.setProperty(`--tg-left${i}`, `${left}px`);
                left += w;
            }
            width += w;
        });
        this.host.style.setProperty('--tg-width', `${width}px`);
    }
}

/**
 * Joins a body to its LiveGrid (Blazor): `host` the grid's element, `body` the one Blazor leaves empty, `head` the
 * headings'; `grid` the component's DotNetObjectReference, which the page's requests, visibility, freshness, clicks and
 * keys go to; `options` GridBody's. Returns what the component calls: reset(view), setColumns(columns),
 * setSelection(keys), reveal(position, above, key), dispose(). The body is the grid element's `gridBody`, for scripts
 * (a measuring run reads its blank sightings, `blanks`; a page in the background draws by `draw()`).
 */
export function createGrid(host, body, head, grid, options) {
    let fresh = 'unknown';
    const call = (method, ...args) => grid.invokeMethodAsync(method, ...args).catch(() => {});   // (the circuit gone)
    const g = new GridBody(body, {
        ...options, host, head,
        request: r => call('Request', r.id, r.first, r.count, r.before, r.after, r.holds, r.letIn, r.view),
        visible: shown => call('Visible', shown),
        status: s => {                     // (the rest of the status is the server's: only freshness comes from here)
            const now = s.fresh === 'checking' ? fresh : s.fresh;
            if (now !== undefined && now !== fresh) call('Fresh', fresh = now);
        },
        rowClick: e => call('RowClick', e.key, e.column ?? null, e.ctrl, e.shift, e.meta, e.alt),
        rowActivate: e => call('RowActivate', e.key, e.column ?? null),
        rowSelect: e => call('RowSelect', e.key)
    });
    g.start();
    host.gridBody = g;
    return {
        reset: view => g.reset(view),
        setColumns: columns => g.setColumns(columns),
        setSelection: keys => g.setSelection(keys),
        reveal: (position, above, key) => g.reveal(position, above, key),
        dispose: () => {
            g.dispose();
            delete host.gridBody;
        }
    };
}
