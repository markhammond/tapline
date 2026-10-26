// tapline-grid.js — Tapline.Grid's page body: a LiveGrid's rows, drawn on the page from a Tapline replica of the window
// its server keeps for it (a rows table and their order, read through one watch with { changed: true }), and the
// windows it asks for as the reader scrolls. Blazor renders the grid's chrome (headings, filters, status); this draws
// the body inside an element Blazor leaves empty. It takes nothing page-wide: no title or icon, no keys of the
// document's; its one document listener (visibilitychange) is added beside any others.
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
    a.holds === b.holds && a.letIn === b.letIn;

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
 * u8 key size | u32 keys | i64 first | i64 total | i64 let in | u32 holds | u32 | the keys — each as the rows table's
 * replica keys it (its bytes as latin1). Null if it isn't one.
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
        first: i64(dv, 8), total: i64(dv, 16), letIn: i64(dv, 24), holds: dv.getInt32(32, true), keys
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

/** A column's integer: `size` bytes, little-endian, signed (8: a BigInt), and the least of its size "no value". */
function integer(dv, o, size) {
    switch (size) {
        case 1:
            return dv.getInt8(o);
        case 2:
            return dv.getInt16(o, true);
        case 4:
            return dv.getInt32(o, true);
        default:
            return dv.getBigInt64(o, true);
    }
}

const NONE = {1: -128, 2: -32768, 4: -2147483648, 8: -(2n ** 63n)};

/**
 * What a cell shows, and on hover: { text, title, cut } from the row's bytes, by the column's kind —
 * int (an integer, `size` bytes: 1, 2, 4, 8), fixed (scaled by 10^scale: to `decimals` places, or `figures`
 * significant), float (f64), time (µs since 1970, UTC: today's date an ellipsis, the microseconds on hover), bool,
 * enum (an index into the column's `dictionary`), text (`size` bytes: a length, 0x80 in it if the text was cut, then
 * UTF-8), percent (of one column over another, by name). The least integer of its size is no value.
 */
export function formatCell(column, row, dv, columns, now = new Date()) {
    const o = column.offset ?? 0, size = column.size ?? 8;
    switch (column.kind) {
        case 'int': {
            const v = integer(dv, o, size);
            if (v === NONE[size] || v === BigInt(NONE[8])) return {text: ''};
            return {text: group(String(v).replace('-', '')).replace(/^/, v < 0 ? '-' : '')};
        }
        case 'fixed': {
            const v = integer(dv, o, size);
            if (v === NONE[size]) return {text: ''};
            const scale = column.scale ?? 0;
            const text = column.figures ? significant(v, scale, column.figures) : finance(v, scale, column.decimals ?? scale);
            return {text, title: finance(v, scale, scale)};
        }
        case 'float': {
            const v = dv.getFloat64(o, true);
            if (Number.isNaN(v)) return {text: ''};
            const text = v.toFixed(column.decimals ?? 2).replace(/\.?0+$/, '');
            const [whole, fraction] = text.replace('-', '').split('.');
            if (/^0*$/.test(whole) && !fraction) return {text: NOUGHT, title: String(v)};
            return {text: (v < 0 ? '-' : '') + (/^0+$/.test(whole) ? '' : group(whole)) + (fraction ? '.' + fraction : ''), title: String(v)};
        }
        case 'time': {
            const v = dv.getBigInt64(o, true);
            if (v === NONE[8]) return {text: ''};
            const d = new Date(Number(v / 1000n)), micros = Number(((v % 1_000_000n) + 1_000_000n) % 1_000_000n);
            const date = `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
            const time = `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
            const today = date === `${now.getUTCFullYear()}-${pad(now.getUTCMonth() + 1)}-${pad(now.getUTCDate())}`;
            return {text: today ? `…${time}` : `${date} ${time}`, title: `${date} ${time}.${pad(micros, 6)} UTC`};
        }
        case 'bool':
            return dv.getUint8(o) ? {text: '✓', title: 'yes'} : {text: '', title: 'no'};
        case 'enum': {
            const i = size === 2 ? dv.getUint16(o, true) : dv.getUint8(o);
            return {text: column.dictionary?.[i] ?? `#${i}`};
        }
        case 'text': {
            const n = row[o] & 0x7f, cut = (row[o] & 0x80) !== 0;
            const text = utf8.decode(row.subarray(o + 1, o + 1 + Math.min(n, size - 1)));
            return cut ? {text: text + '…', title: text + '…', cut: true} : {text, title: text.length > 12 ? text : undefined};
        }
        case 'percent': {
            const of = columns.find(c => c.name === column.of), over = columns.find(c => c.name === column.over);
            if (!of || !over) return {text: ''};
            const a = Number(integer(dv, of.offset ?? 0, of.size ?? 8)), b = Number(integer(dv, over.offset ?? 0, over.size ?? 8));
            if (!(a > 0 && b > 0)) return {text: NOUGHT};
            return {text: finance(BigInt(Math.round(a / b * 100 * 10 ** (column.decimals ?? 1))), column.decimals ?? 1, column.decimals ?? 1) + '%'};
        }
        default:
            return {text: ''};
    }
}

// ───────────── the body ─────────────

const FLASH_ROW_MS = 3_000, QUIET_MS = 1_000;
const NUMERIC = new Set(['int', 'fixed', 'float', 'percent']);

/**
 * A LiveGrid's body: the rows of its window, drawn from the replica of its watch (rows table, order value), in an
 * element the chrome leaves empty. Options:
 *   columns    [{ name, label, kind, offset, size, scale, decimals, figures, dictionary, of, over, width, frozen, align }]
 *   rowHeight  px, one for every row
 *   tapline    the page's TaplineClient (tapline.js); binding: the grid's watch (its session's Binding)
 *   request(request)           sends a window request to the session: { id, first, count, before, after, holds, letIn }
 *   maxRows                    the session's most rows a window (GridOptions.MaxWindowRows): never asked for more
 *   minInterval, maxOutstanding  requests at most once this many ms (50), and while fewer are unanswered (4)
 *   visible(visible)           the page shown or hidden (the session stops reading the source's changes while hidden)
 *   status(status)             what the chrome shows: { total, totalKind, live, stale, holds, letIn, fresh, blanks }
 */
export class GridBody {
    constructor(root, options) {
        this.root = root;
        this.options = options;
        this.columns = options.columns;
        this.rowHeight = options.rowHeight ?? 30;
        this.tapline = options.tapline ?? globalThis.tapline;
        this.tapId = options.binding.tapId ?? options.binding;
        this.scroller = new Scroller(this.rowHeight, options.safeHeight ?? SAFE_HEIGHT);
        this.prefetch = new Prefetch();
        this.velocity = new Velocity();
        this.requests = new Requests(request => this.#ask(request),
            {minInterval: options.minInterval ?? 50, maxOutstanding: options.maxOutstanding ?? 4});
        this.order = null;                 // the order applied: { first, keys, total, live, letIn, holds, … }
        this.table = null;                 // the rows table's replica
        this.pool = [];                    // row elements: { el, cells, key, bytes, position, y }
        this.changed = new Set();          // keys changed since last drawn
        this.whole = true;                 // a state handed over whole: every row drawn afresh
        this.arrived = new Set();          // keys new at the live top, to flash when next drawn
        this.down = true;                  // the reader's direction
        this.frame = 0;                    // requestAnimationFrame id, while one is due
        this.widths = null;                // px a column, once measured
        this.blanks = 0;                   // frames with a placeholder in view (blank sightings)
        this.fresh = 'unknown';            // 'fresh' (Tap.Sync answered) | 'checking' | 'behind' | 'unknown'
        this.hidden = false;
        this.disposed = false;
        this.waits = new Map();            // request id → AbortController of its rendezvous wait
    }

    /** Builds the body, subscribes to the watch, and asks for the first window — measured first, in the same task. */
    start() {
        const root = this.root;
        root.classList.add('tapline-grid');
        root.style.setProperty('--tg-row-height', `${this.rowHeight}px`);
        root.replaceChildren();
        this.scrollerEl = Object.assign(document.createElement('div'), {className: 'tg-scroller', tabIndex: 0});
        this.surfaceEl = Object.assign(document.createElement('div'), {className: 'tg-surface'});
        this.layerEl = Object.assign(document.createElement('div'), {className: 'tg-rows'});
        this.scrollerEl.setAttribute('role', 'rowgroup');
        this.surfaceEl.append(this.layerEl);
        this.scrollerEl.append(this.surfaceEl);
        root.append(this.scrollerEl);
        this.#styleColumns();

        this.onScroll = () => this.#scrolled();
        this.onWheel = e => this.#wheel(e);
        this.onKey = e => this.#key(e);
        this.onVisibility = () => this.#visibility();
        this.scrollerEl.addEventListener('scroll', this.onScroll, {passive: true});
        this.scrollerEl.addEventListener('wheel', this.onWheel, {passive: false});
        this.scrollerEl.addEventListener('keydown', this.onKey);
        document.addEventListener('visibilitychange', this.onVisibility);
        this.hidden = document.visibilityState === 'hidden';
        this.resizer = new ResizeObserver(() => this.#resized());
        this.resizer.observe(this.scrollerEl);

        this.off = this.tapline.subscribe(this.options.binding, (state, id, info) => this.#state(state, info),
            error => this.#ended(error), undefined, {changed: true});
        this.#resized();                   // measured, then the first window asked for: never "asked for nothing"
    }

    /** A new sort or filter: back to the top, its rows placeholders until the new top arrives. */
    reset() {
        for (const id of this.requests.reset()) this.#settle(id);
        this.order = null;
        this.whole = true;
        this.arrived.clear();
        this.velocity.reset();
        this.scrollerEl.scrollTop = this.scroller.to(0);
        this.#want();
        this.#schedule();
    }

    /** A new set of columns: drawn afresh, their widths measured again. */
    setColumns(columns) {
        this.columns = columns;
        this.widths = null;
        for (const r of this.pool) r.el.remove();
        this.pool = [];
        this.whole = true;
        this.#styleColumns();
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
        this.scrollerEl?.removeEventListener('keydown', this.onKey);
        document.removeEventListener('visibilitychange', this.onVisibility);
        this.root.replaceChildren();
    }

    // ── what arrives ──

    #state(group, info) {
        const [table, orderBytes] = group.members;
        if (table) this.table = table;
        const changed = info?.changed;
        if (orderBytes instanceof Uint8Array && (!changed || changed[1] !== false)) this.#order(readOrder(orderBytes));
        if (changed?.[0] instanceof Set) for (const key of changed[0]) this.changed.add(key);
        else this.whole = true;            // (handed over whole: drawn whole)
        if (this.fresh !== 'fresh' && !info?.stale) {
            this.fresh = 'fresh';          // (a state from the router; quiet a second, Tap.Sync says whether there's more)
            this.#report();
        }
        this.#quiet();
        this.#schedule();
    }

    #order(order) {
        if (!order) return;
        const was = this.order;
        if (order.total !== this.scroller.total) {
            this.scroller.setTotal(order.total);
            this.surfaceEl.style.height = `${this.scroller.height}px`;
        }
        // Rows let in above (in this hold): the page moves its scroll down by as many, the rows in view staying put.
        if (was && order.holds === was.holds && order.letIn > was.letIn)
            this.scrollerEl.scrollTop = this.scroller.by(order.letIn - was.letIn);
        // Rows arrived at the live top, above the row that was first: they flash once drawn.
        if (was && order.live && was.live && was.keys.length) {
            const before = new Set(was.keys), top = order.keys.indexOf(was.keys[0]);
            for (let i = 0; i < top; i++) if (!before.has(order.keys[i])) this.arrived.add(order.keys[i]);
        }
        this.order = order;
        this.#report();
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
        this.requests.want({...window, holds: this.order?.holds ?? 0, letIn: this.order?.letIn ?? 0});
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
        if (height === this.scroller.viewHeight) return;
        this.scroller.resize(height);
        this.prefetch.see(this.scroller.viewRows);
        this.layerEl.style.height = `${height}px`;
        this.#want();
        this.#schedule();
    }

    #scrolled() {
        this.root.style.setProperty('--tg-scroll-left', `${this.scrollerEl.scrollLeft}px`);   // (for the headings)
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

    #key(e) {
        if (e.altKey || e.metaKey || e.ctrlKey) return;
        const page = Math.max(1, Math.floor(this.scroller.viewRows) - 1);
        const rows = {ArrowDown: 1, ArrowUp: -1, PageDown: page, PageUp: -page, Home: -Infinity, End: Infinity}[e.key];
        if (rows === undefined) return;
        e.preventDefault();
        this.#move(rows);
    }

    #move(rows) {
        this.down = rows > 0;
        this.scrollerEl.scrollTop = Number.isFinite(rows) ? this.scroller.by(rows) : this.scroller.to(rows > 0 ? this.scroller.lastTop : 0);
        this.velocity.sample(this.scroller.row, performance.now());
        this.#want();
        this.#schedule();
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
        const changed = this.changed, whole = this.whole;
        this.changed = new Set();
        this.whole = false;
        while (this.pool.length < rows) this.pool.push(this.#rowElement());
        if (!this.widths && order && table && order.keys.length) this.#measure(order, table);
        let blank = false;
        for (let i = 0; i < this.pool.length; i++) {
            const r = this.pool[i], position = first + i;
            if (i >= rows || position >= this.scroller.total) {
                if (!r.el.hidden) r.el.hidden = true;
                continue;
            }
            if (r.el.hidden) r.el.hidden = false;
            const y = offset + i * this.rowHeight;
            if (r.y !== y) r.el.style.transform = `translateY(${(r.y = y)}px)`;
            if (r.position !== position) r.el.setAttribute('aria-rowindex', String((r.position = position) + 1));
            const at = order ? position - order.first : -1;
            const key = order && at >= 0 && at < order.keys.length ? order.keys[at] : undefined;
            const entry = key !== undefined ? table?.rows.get(key) : undefined;
            if (!entry) {
                if (r.key !== null) this.#placeholder(r);
                if (position < top + this.scroller.viewRows) blank = true;
                continue;
            }
            if (whole || r.key !== key || r.bytes !== entry.row || changed.has(key)) {
                const inPlace = r.key === key && changed.has(key);           // (scrolled into view: no flash)
                this.#fill(r, key, entry.row, inPlace);
                if (this.arrived.delete(key)) this.#flash(r.el, 'tg-arrived');
            }
        }
        if (blank) this.blanks++;
    }

    #rowElement() {
        const el = document.createElement('div');
        el.className = 'tg-row';
        el.setAttribute('role', 'row');
        const cells = this.columns.map((c, i) => {
            const cell = document.createElement('span');
            cell.className = 'tg-cell' + (c.frozen ? ' tg-frozen' : '') + (c.align === 'right' || (c.align !== 'left' && NUMERIC.has(c.kind)) ? ' tg-right' : '');
            cell.style.width = `var(--tg-w${i}, ${c.width ?? 120}px)`;
            if (c.frozen) cell.style.left = `var(--tg-left${i}, 0px)`;
            cell.setAttribute('role', 'gridcell');
            el.append(cell);
            return cell;
        });
        this.layerEl.append(el);
        return {el, cells, key: undefined, bytes: null, last: null, position: -1, y: NaN};
    }

    #placeholder(r) {
        r.key = null;
        r.bytes = r.last = null;
        r.el.classList.add('tg-placeholder');
        for (const cell of r.cells) cell.textContent = '';
    }

    /** Writes a row's cells; changed in place since last drawn, the cells that differ flash. */
    #fill(r, key, row, inPlace) {
        const dv = new DataView(row.buffer, row.byteOffset, row.byteLength), now = new Date();
        r.el.classList.remove('tg-placeholder');
        const texts = r.last && inPlace ? r.last : null, last = new Array(this.columns.length);
        for (let i = 0; i < this.columns.length; i++) {
            const {text, title} = formatCell(this.columns[i], row, dv, this.columns, now), cell = r.cells[i];
            if (cell.textContent !== text) cell.textContent = text;   // (text, never HTML)
            if (title) cell.title = title; else if (cell.title) cell.removeAttribute('title');
            if (texts && texts[i] !== text) this.#flash(cell, 'tg-changed');
            last[i] = text;
        }
        r.key = key;
        r.bytes = row;
        r.last = last;
    }

    /** Flashes an element: two animations of the one name alternated, so a flash under way starts again without a reflow. */
    #flash(el, name) {
        const a = el.classList.contains(`${name}-a`);
        el.classList.remove(`${name}-a`, `${name}-b`);
        el.classList.add(`${name}-${a ? 'b' : 'a'}`);
    }

    // ── columns: widths and frozen offsets, the module's, as custom properties the headings read too ──

    #styleColumns() {
        this.columns.forEach((c, i) => {
            if (c.width) this.root.style.setProperty(`--tg-w${i}`, `${c.width}px`);
        });
        this.#placeColumns();
    }

    /** Widths from the first rows answered (where the schema gives none), with room in a heading for its sort arrow and rank. */
    #measure(order, table) {
        const probe = document.createElement('span');
        probe.className = 'tg-cell tg-probe';
        this.layerEl.append(probe);
        this.widths = this.columns.map((c, i) => {
            if (c.width) return c.width;
            probe.textContent = c.label ?? c.name;
            let widest = probe.getBoundingClientRect().width + 28;   // (the arrow and a multi-column sort's rank)
            for (const key of order.keys.slice(0, 200)) {
                const entry = table.rows.get(key);
                if (!entry) continue;
                const dv = new DataView(entry.row.buffer, entry.row.byteOffset, entry.row.byteLength);
                probe.textContent = formatCell(c, entry.row, dv, this.columns).text;
                widest = Math.max(widest, probe.getBoundingClientRect().width + 16);
            }
            widest = Math.ceil(widest);
            this.root.style.setProperty(`--tg-w${i}`, `${widest}px`);
            return widest;
        });
        probe.remove();
        this.#placeColumns();
    }

    /** A frozen column stands as far from the left as the frozen columns before it are wide; the rows as wide as them all. */
    #placeColumns() {
        let left = 0, width = 0;
        this.columns.forEach((c, i) => {
            const w = this.widths?.[i] ?? c.width ?? 120;
            if (c.frozen) {
                this.root.style.setProperty(`--tg-left${i}`, `${left}px`);
                left += w;
            }
            width += w;
        });
        this.root.style.setProperty('--tg-width', `${width}px`);
    }
}
