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

/** Views a second up, at least, that a reader is racing at (racingUp). */
const RACING_VIEWS = 2;

/** Rows held either side of the view, unless the grid says (LiveGrid.OverscanCount). */
const OVERSCAN = 150;

/** What a cell shows for nought: a dash. */

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
 * of a view behind at full size, a half or a quarter of that on a slower link (the side ahead, windowFor keeps a view at
 * the least). It starts at a quarter, the first
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
    static Window = 1000;

    constructor() {
        this.reset();
    }

    reset() {
        this.at = null;            // the last sample: { row, time }
        this.speed = 0;            // rows a millisecond, signed, smoothed
        this.peak = 0;             // the fling's fastest, and its way
        this.way = 0;
        this.recent = [];          // the last second's samples, oldest first: the steady rate's
        this.heading = 0;          // the way the top row last moved, and the sample it last turned back at
        this.turn = null;
    }

    /** The top row is `row` at `time`. */
    sample(row, time) {
        const was = this.at;
        this.at = {row, time};
        this.recent.push(this.at);
        while (this.recent.length > 2 && time - this.recent[1].time >= Velocity.Window) this.recent.shift();   // (one before it kept)
        if (!was) return;
        const heading = Math.sign(row - was.row);
        if (heading !== 0) {
            if (this.heading !== 0 && heading !== this.heading) this.turn = was;
            this.heading = heading;
        }
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

    /** Positions moved down by `rows` (rows let in above): where the reader was moves with them, their pace as it was. */
    shift(rows) {
        for (const s of this.recent) s.row += rows;   // (the last sample among them)
    }

    /** Rows the reader is about to cover, the way they're going (+ down, − up): the peak speed × τ. */
    get reach() {
        return this.way * this.peak * Velocity.Tau;
    }

    /**
     * Rows a millisecond the reader has covered over the last second up to `now`, the way they've gone: steady
     * scrolling step by step — wheel clicks, keys, a scripted scroll, whose pauses each end a fling — as well as a fling.
     * Setting off within it after a second or more still, over the time since; turned back within it, from where they
     * turned, over the time since (120 ms at least, either way): their pace, not the second's — a scroll from rest, or
     * back the way it came, asking as far ahead from the first as it will.
     */
    rate(now) {
        const recent = this.recent, last = recent[recent.length - 1];
        if (recent.length < 2 || now - last.time > Velocity.Window) return 0;
        let from = 0;                      // (from where the reader was as the second began, or first seen within it)
        while (from + 1 < recent.length && now - recent[from + 1].time >= Velocity.Window) from++;
        const off = recent[from + 1], turn = this.turn;
        if (turn && turn.time > recent[from].time) return (last.row - turn.row) / Math.max(Velocity.Gap, now - turn.time);
        const over = off && off.time - recent[from].time >= Velocity.Window ? Math.max(Velocity.Gap, now - off.time)
            : Math.min(Velocity.Window, now - recent[from].time);
        return (last.row - recent[from].row) / Math.max(1, over);
    }
}

/**
 * How far ahead to fetch (rows, signed as the reader goes): as far as a fling reaches (`reach`), or as far as the
 * reader's steady `rate` (rows a ms) covers while an answer comes — its median time over the last answers (`took`, ms;
 * 250 ms before any), and the requests' `interval` — and half as much again, whichever is further. (More, on a slow
 * link, asks for wider windows than it can carry: their answers slow, and the lead grows with them.)
 */
export function leadFor(reach, rate, took, interval) {
    const sorted = [...took].sort((a, b) => a - b);
    const answer = sorted.length > 0 ? sorted[sorted.length >> 1] : 250;
    const steady = rate * (answer + interval) * 1.5;
    return Math.abs(steady) > Math.abs(reach) ? steady : reach;
}

/**
 * Whether the reader is racing up: scrolling up two views a second or more, at their steady `rate` over the last second
 * or their fling's speed (`fling`; rows a ms, signed, up negative). A held grid then goes back to the live top if fewer
 * rows arrived above than the view holds, and otherwise, near the top, lets them in faster, to keep pace.
 */
export function racingUp(rate, fling, viewRows) {
    return Math.max(-rate, -fling) * 1000 >= RACING_VIEWS * viewRows;
}

/**
 * The window asked for with `rows` in view from `first`, the reader going `down` or up: the overscan either side
 * (`overscan`, rows: held both ways as the reader moves, whichever way they go next — QuickGrid's term), and ahead,
 * further if the reader's lead reaches further (`lead`, rows: a fling's, or a steady pace's over an answer's trip,
 * leadFor; at most three views), at least `least` (a view, unless the link carries fewer in time: then what it does), or
 * the pre-fetch's own; behind, the pre-fetch's own if more; and the whole window no more than `most` rows, the trailing
 * side giving way first. (At the top, none above: the server reads from the first row.)
 */
export function windowFor(first, rows, down, prefetch, lead = 0, most = Infinity, overscan = 0, least = rows) {
    first = Math.max(0, Math.floor(first));
    rows = Math.ceil(rows);
    const ahead = Math.max(Math.ceil(least), prefetch.ahead, Math.min(Math.ceil(Math.abs(lead)), 3 * rows), overscan);
    const behind = Math.max(prefetch.behind, overscan);
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
    a.holds === b.holds && a.letIn === b.letIn && a.view === b.view && !!a.top === !!b.top;

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

    /** Asks for what's wanted again, even as last asked: its answer says what the window holds now (a grid back in sight). */
    again() {
        this.sent = null;
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
 * A grid's order value (Tapline.Grid's GridOrder): u8 format (1) | u8 flags (1 live, 2 stale, 4 by key) | u8 the total's
 * kind | u8 key size | u32 keys | i64 first | i64 total | i64 let in | u32 holds | u32 view | the keys — each as the rows
 * table's replica keys it (its bytes as latin1). By key, none follow (keys null): the window is the rows the table holds,
 * greatest key first (keysByKey), `count` of them. Null if it isn't one.
 */
export function readOrder(bytes) {
    if (!(bytes instanceof Uint8Array) || bytes.length < 40 || bytes[0] !== 1) return null;
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const count = dv.getInt32(4, true), keySize = bytes[3], byKey = (bytes[1] & 4) !== 0;
    if (count < 0 || (byKey ? 0 : count * keySize) !== bytes.length - 40) return null;
    const keys = byKey ? null : new Array(count);
    if (keys) for (let i = 0, o = 40; i < count; i++, o += keySize) keys[i] = latin1(bytes, o, keySize);
    return {
        live: (bytes[1] & 1) !== 0, stale: (bytes[1] & 2) !== 0, byKey, totalKind: bytes[2], keySize, count,
        first: i64(dv, 8), total: i64(dv, 16), letIn: i64(dv, 24), holds: dv.getInt32(32, true), view: dv.getInt32(36, true),
        keys
    };
}

/**
 * A grid's status value (Tapline.Grid's GridStatusValue), its watch's third member: what the page needs that neither the
 * rows nor their order say. u8 format (1) | u8 flags (1 refused) | u16 the overscan its link carries (rows either side;
 * 65,535, none told: null) | i64 above (held: rows arrived above, not let in) | why the last read was refused, UTF-8.
 * Null if it isn't one.
 */
export function readStatus(bytes) {
    if (!(bytes instanceof Uint8Array) || bytes.length < 12 || bytes[0] !== 1) return null;
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength), overscan = dv.getUint16(2, true);
    return {
        above: i64(dv, 4), overscan: overscan === 0xffff ? null : overscan, moved: (bytes[1] & 2) !== 0,
        refusal: (bytes[1] & 1) !== 0 ? new TextDecoder().decode(bytes.subarray(12)) : null
    };
}

/** Thousands grouped as the status line writes them (in English, as its words are: LiveGrid's status line is 0.1's). */
const group = digits => digits.replace(/\B(?=(\d{3})+(?!\d))/g, ',');

/**
 * The status line the page draws, as [text, class] parts, from its order (readOrder: { total, totalKind, live }), its
 * status value (readStatus: { above, refusal }) and what it knows itself: the rows in all (estimated, ≈; at least so
 * many, ≥), named as `names` has them ([one, many]); live, or held and how many arrived above; whether it has all the
 * server published (`fresh`: 'fresh', up to date; 'behind', catching up; else …); why the last read was refused
 * (tg-refusal); how many are selected.
 */
export function statusParts(status, fresh, selected = 0, names = ['row', 'rows']) {
    const kind = status.totalKind === 1 ? '≈' : status.totalKind === 2 ? '≥' : '';
    const parts = [[`${kind}${group(String(status.total))} ${status.total === 1 ? names[0] : names[1]}`],
        [status.live ? 'live' : status.above > 0 ? `held, ${group(String(status.above))} new above` : 'held'],
        [fresh === 'fresh' ? 'up to date' : fresh === 'behind' ? 'catching up' : '…']];
    if (status.refusal) parts.push([status.refusal, 'tg-refusal']);
    if (selected > 0) parts.push([`${group(String(selected))} selected`]);
    return parts;
}

/** The status line as text (statusParts). */
export function statusText(status, fresh, selected = 0, names = ['row', 'rows']) {
    return statusParts(status, fresh, selected, names).map(p => p[0]).join(' · ');
}

const GUID_BYTES = [3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15];   // (most significant first, as .NET lays a Guid out)

/** Two keys (latin1 text, a character a byte) as the server's key type compares them: below, at or above nought. */
export function compareKeys(a, b, kind) {
    if (kind === 'guid') {
        for (const i of GUID_BYTES) if (a.charCodeAt(i) !== b.charCodeAt(i)) return a.charCodeAt(i) - b.charCodeAt(i);
        return 0;
    }
    const top = a.length - 1, sign = kind === 'int' ? 0x80 : 0;   // (little-endian: the last byte is the most significant)
    if (a.charCodeAt(top) !== b.charCodeAt(top)) return (a.charCodeAt(top) ^ sign) - (b.charCodeAt(top) ^ sign);
    for (let i = top - 1; i >= 0; i--) if (a.charCodeAt(i) !== b.charCodeAt(i)) return a.charCodeAt(i) - b.charCodeAt(i);
    return 0;
}

/**
 * A window sent without its keys (an order by key: GridOrder.ByKey), placed: `keys` — those the rows table holds —
 * greatest first, as the server's key type compares (`kind`: "guid", a .NET Guid as Guid.CompareTo orders it; "int" or
 * "uint", a little-endian integer).
 */
export function keysByKey(keys, kind) {
    return [...keys].sort((a, b) => compareKeys(b, a, kind));
}

// ───────────── cells: written by their column's .NET format ─────────────
//
// A column's Format is a .NET format string (QuickGrid's PropertyColumn.Format), and the page writes each cell as .NET
// writes the value on the server — ToString(format), in the culture current as the grid rendered — to the rules .NET
// documents for standard and custom numeric format strings and for custom date and time patterns. The tests hold the
// page to .NET's own output (tests/js/fixtures/formats.json, written by the C# run). A number is held exactly, as a
// whole number times a power of ten: an integer's, a decimal's, a double's (a double's binary value is always a finite
// decimal). It's rounded as .NET rounds: by a standard format, a double's exact value half to even; by a custom format,
// a double's first fifteen significant digits (a float's seven); everything else, half away from nought.

/** The invariant culture's number symbols (NumberFormatInfo.InvariantInfo), by the keys LiveGrid sends a culture's in. */
const NUMBERS = {
    neg: '-', pos: '+', dec: '.', grp: ',', sizes: [3], digits: 2, nneg: 1, pdec: '.', pgrp: ',', psizes: [3], pdigits: 2,
    ppos: 0, pneg: 0, pct: '%', pml: '‰', cur: '¤', cdec: '.', cgrp: ',', csizes: [3], cdigits: 2, cpos: 0, cneg: 0,
    nan: 'NaN', inf: 'Infinity', ninf: '-Infinity'
};

/** And its dates' (DateTimeFormatInfo.InvariantInfo): genitive month names (mgen, mgabbr) only where a culture has them. */
const DATES = {
    am: 'AM', pm: 'PM', ds: '/', ts: ':', era: 'A.D.',
    months: ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'],
    mabbr: ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'],
    days: ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'],
    dabbr: ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
};

/** The invariant culture, as cultureOf makes one. */
export const INVARIANT = Object.freeze({n: NUMBERS, d: DATES});

/** A culture as LiveGrid sends one (what of it differs from the invariant culture's: { n, d }), made whole. */
export function cultureOf(given) {
    return given ? {n: {...NUMBERS, ...given.n}, d: {...DATES, ...given.d}} : INVARIANT;
}

// The patterns .NET's NumberFormatInfo documents for currency, percentages and negative numbers: n the number, $ the
// currency symbol, % the percent sign, - the negative sign.
const CURRENCY = ['$n', 'n$', '$ n', 'n $'];
const CURRENCY_NEGATIVE = ['($n)', '-$n', '$-n', '$n-', '(n$)', '-n$', 'n-$', 'n$-', '-n $', '-$ n', 'n $-', '$ n-', '$ -n', 'n- $', '($ n)',
    '(n $)', '$- n'];
const PERCENT = ['n %', 'n%', '%n', '% n'];
const PERCENT_NEGATIVE = ['-n %', '-n%', '-%n', '%-n', '%n-', 'n-%', 'n%-', '-% n', 'n %-', '% n-', '% -n', 'n- %'];
const NUMBER_NEGATIVE = ['(n)', '-n', '- n', 'n-', 'n -'];

const fill = (pattern, number, n) =>
    pattern.replace(/[n$%-]/g, c => c === 'n' ? number : c === '$' ? n.cur : c === '%' ? n.pct : n.neg);

const F64 = new Float64Array(1), U64 = new BigUint64Array(F64.buffer), F32 = new Float32Array(1), U32 = new Uint32Array(F32.buffer);
const TENS = [], FIVES = [];
const ten = k => TENS[k] ??= 10n ** BigInt(k);
const five = k => FIVES[k] ??= 5n ** BigInt(k);

// ── a number's magnitude as the formats work on it: { c, e }, the whole number c (a BigInt) times 10^e ──

/** A double's (or a float's) exact magnitude: m × 2^-k is m × 5^k × 10^-k. */
function exactOf(v) {
    F64[0] = Math.abs(v);
    const bits = U64[0], biased = Number(bits >> 52n);
    const m = biased ? (bits & 0xfffffffffffffn) | (1n << 52n) : bits & 0xfffffffffffffn, k = biased ? 1075 - biased : 1074;
    return k <= 0 ? {c: m << BigInt(-k), e: 0} : {c: m * five(k), e: -k};
}

/** Rounded to a whole number of 10^at: half to even (`even`), or half away from nought. */
function quantize(x, at, even) {
    if (x.e >= at) return x;
    const unit = ten(at - x.e), whole = x.c / unit, twice = (x.c % unit) * 2n;
    const up = twice > unit || (twice === unit && (!even || (whole & 1n) === 1n));
    return {c: up ? whole + 1n : whole, e: at};
}

/** Rounded to `figures` significant digits (as quantize rounds). */
function toFigures(x, figures, even) {
    const length = x.c === 0n ? 0 : String(x.c).length;
    return length <= figures ? x : quantize(x, x.e + length - figures, even);
}

/** The noughts after its last digit that counts taken off. */
function trimmed(x) {
    let {c, e} = x;
    if (c !== 0n) while (c % 10n === 0n) (c /= 10n, e++);
    return {c, e};
}

/** Where its point stands, counted from its first digit: 3 for 123.4, 0 for .25, -1 for .05; 0 for nought. */
const placeOf = x => x.c === 0n ? 0 : String(x.c).length + x.e;

/** Its whole part's digits ('' for none) and its first `places` digits after the point. */
function partsOf(x, places) {
    const s = x.c === 0n ? '' : String(x.c);
    if (x.e >= 0) return {whole: s && s + '0'.repeat(x.e), fraction: '0'.repeat(places)};
    const after = -x.e, all = s.padStart(after, '0'), fraction = all.slice(all.length - after);
    return {whole: all.slice(0, all.length - after).replace(/^0+/, ''), fraction: fraction.padEnd(places, '0').slice(0, places)};
}

/** Whole digits in groups of `sizes` from the right — the last size repeated, a 0 grouping no further — joined by `separator`. */
function grouped(whole, sizes, separator) {
    if (!separator || !sizes?.length || !sizes[0]) return whole;
    const groups = [];
    let end = whole.length, at = 0, size = sizes[0];
    for (; size > 0 && end > size; end -= size, size = at < sizes.length - 1 ? sizes[++at] : size) groups.unshift(whole.slice(end - size, end));
    groups.unshift(whole.slice(0, end));
    return groups.join(separator);
}

/** A group separator's place in a whole number: after the digit standing `position` from the point (1, units)? */
function groupEnds(position, sizes) {
    if (!sizes?.length || !sizes[0]) return false;
    for (let end = 0, at = 0, size = sizes[0]; size > 0 && end < position; size = at < sizes.length - 1 ? sizes[++at] : size)
        if ((end += size) === position) return true;
    return false;
}

const exponentText = (n, power, char, least, plus) =>
    char + (power < 0 ? n.neg : plus ? n.pos : '') + String(Math.abs(power)).padStart(least, '0');

/** A number as JavaScript writes it (String's, toPrecision's, toFixed's: digits, a point, an exponent), as a magnitude. */
function decimalOf(text) {
    const [mantissa, power = '0'] = text.split('e'), point = mantissa.indexOf('.');
    const digits = point < 0 ? mantissa : mantissa.slice(0, point) + mantissa.slice(point + 1);
    return {c: BigInt(digits), e: Number(power) - (point < 0 ? 0 : mantissa.length - point - 1)};
}

/** A double's shortest digits that read back as it — JavaScript's, which are .NET's too (the nearest such, ties even). */
const shortest64 = v => trimmed(decimalOf(String(Math.abs(v))));

/**
 * A double's magnitude `a` correctly rounded, half to even, as .NET's digits are: to `figures` significant digits, or
 * to `places` after the point. JavaScript's toPrecision and toFixed round it correctly too, but half up — the same,
 * unless it lies exactly halfway, when one more digit of theirs ends in a 5: only then are its exact digits worked out.
 */
function figuresOf(a, figures) {
    if (figures < 100 && !/5(?:e|$)/.test(a.toPrecision(figures + 1))) return decimalOf(a.toPrecision(figures));
    return toFigures(exactOf(a), figures, true);
}

function placesOf(a, places) {
    if (places < 100 && a < 1e21 && !a.toFixed(places + 1).endsWith('5')) return decimalOf(a.toFixed(places));
    return quantize(exactOf(a), -places, true);
}

/**
 * A float's shortest digits that read back as it (a float32's 0.1, not 0.100000001), worked out exactly: at each length,
 * the two numbers of that many digits either side of its value, whichever lies within half a float of it (inclusive if
 * its mantissa is even, as reading one back rounds); both, the nearer, ties even.
 */
function shortest32(v) {
    F32[0] = Math.abs(v);
    const bits = U32[0], biased = bits >>> 23, m = BigInt(biased ? (bits & 0x7fffff) | 0x800000 : bits & 0x7fffff);
    const power = (biased ? biased - 150 : -149) - 2;   // (in quarters of the float's unit: its value 4m, its neighbours' midpoints)
    const at = 4n * m, low = at - (m === 0x800000n && biased > 1 ? 1n : 2n), high = at + 2n, even = (m & 1n) === 0n;
    const compare = (c, e, quarters) => {             // c × 10^e against quarters × 2^power
        const left = c * (e > 0 ? ten(e) : 1n) * (power < 0 ? 2n ** BigInt(-power) : 1n);
        const right = quarters * (power > 0 ? 2n ** BigInt(power) : 1n) * (e < 0 ? ten(-e) : 1n);
        return left < right ? -1 : left > right ? 1 : 0;
    };
    const within = (c, e) => even ? compare(c, e, low) >= 0 && compare(c, e, high) <= 0 : compare(c, e, low) > 0 && compare(c, e, high) < 0;
    const exact = exactOf(v), length = String(exact.c).length;
    for (let figures = 1; figures <= 9; figures++) {
        const e = exact.e + length - figures, down = exact.c / ten(length - figures), up = down + 1n;
        const inDown = down > 0n && within(down, e), inUp = within(up, e);
        if (!inDown && !inUp) continue;
        if (!inDown) return trimmed({c: up, e});
        if (!inUp) return trimmed({c: down, e});
        const toward = compare(down + up, e, 2n * at);   // (their middle against the value: the nearer, ties even)
        return trimmed({c: toward > 0 || (toward === 0 && (down & 1n) === 0n) ? down : up, e});
    }
    return trimmed(exact);
}

// ── standard formats ──

/** G's text of a magnitude: fixed point, or scientific where its first digit is at 10^most or above, or below 10^-4. */
function generalText(x, most, char, n) {
    const t = trimmed(x);
    if (t.c === 0n) return '0';
    const s = String(t.c), place = s.length + t.e;
    if (place > most || place < -3) return s[0] + (s.length > 1 ? n.dec + s.slice(1) : '') + exponentText(n, place - 1, char, 2, true);
    if (place <= 0) return '0' + n.dec + '0'.repeat(-place) + s;
    return place >= s.length ? s + '0'.repeat(place - s.length) : s.slice(0, place) + n.dec + s.slice(place);
}

/**
 * A standard format's text (C, E, F, G, N, P, R) of a magnitude and its sign, for a number of `kind`: 'i' an integer,
 * 'd' a decimal (its places kept by G), 'f' floating point — whose magnitude, for G without a precision and R, is
 * already its shortest round-trip digits (`single`: a float's).
 */
function standardText(x, negative, kind, single, letter, digits, n) {
    const fp = kind === 'f', upper = letter.toUpperCase();
    const sign = y => negative && (fp || y.c !== 0n) ? n.neg : '';   // (an integer or a decimal rounded to nought: none)
    switch (upper) {
        case 'F': case 'N': case 'C': case 'P': {
            const places = digits >= 0 ? digits : upper === 'C' ? n.cdigits : upper === 'P' ? n.pdigits : n.digits;
            const y = quantize(upper === 'P' ? {c: x.c, e: x.e + 2} : x, -places, fp), signed = sign(y);
            const {whole, fraction} = partsOf(y, places);
            const [sizes, point, separator] = upper === 'C' ? [n.csizes, n.cdec, n.cgrp] : upper === 'P' ? [n.psizes, n.pdec, n.pgrp]
                : [upper === 'N' ? n.sizes : null, n.dec, n.grp];
            const text = grouped(whole || '0', sizes, separator) + (places > 0 ? point + fraction : '');
            if (upper === 'F') return signed + text;
            return fill(upper === 'N' ? (signed ? NUMBER_NEGATIVE[n.nneg] : 'n')
                : upper === 'C' ? (signed ? CURRENCY_NEGATIVE[n.cneg] : CURRENCY[n.cpos])
                    : (signed ? PERCENT_NEGATIVE[n.pneg] : PERCENT[n.ppos]), text, n);
        }
        case 'E': {
            const after = digits >= 0 ? digits : 6, y = toFigures(x, after + 1, fp), s = y.c === 0n ? '0' : String(y.c);
            return sign(y) + s[0] + (after > 0 ? n.dec + s.slice(1).padEnd(after, '0').slice(0, after) : '') +
                exponentText(n, y.c === 0n ? 0 : placeOf(y) - 1, letter, 3, true);
        }
        case 'G': case 'R': {                       // (R is G but for a double, whose R is its shortest round trip)
            const char = letter === 'g' || letter === 'r' ? 'e' : 'E';
            if (fp && upper === 'R') digits = -1;
            if (digits >= 1) {
                const y = toFigures(x, digits, fp);
                return sign(y) + generalText(y, digits, char, n);
            }
            if (kind === 'i') return sign(x) + (x.c === 0n ? '0' : String(x.c));
            if (kind === 'd') {
                if (digits === 0) return sign(x) + generalText(x, x.c === 0n ? 0 : String(x.c).length, char, n);
                const {whole, fraction} = partsOf(x, Math.max(0, -x.e));   // (every place it has, its noughts too)
                return sign(x) + (whole || '0') + (fraction ? n.dec + fraction : '');
            }
            return sign(x) + generalText(x, Math.max(x.c === 0n ? 1 : String(x.c).length, single ? 9 : 17), char, n);
        }
        default:
            throw new RangeError(`no number format "${letter}"`);
    }
}

// ── custom formats: up to three sections (positive; negative; nought), each a picture of the number ──

const DIGIT = 0, POINT = 1, PERCENT_SIGN = 2, PERMILLE_SIGN = 3;
const customs = new Map();

/** A custom format's sections, as written, and as read: kept by format, as a grid writes its cells by a few. */
function sectionsOf(format) {
    let sections = customs.get(format);
    if (sections) return sections;
    const texts = [];
    let start = 0;
    for (let i = 0; i < format.length; i++) {
        const c = format[i];
        if (c === "'" || c === '"') {
            const close = format.indexOf(c, i + 1);
            i = close < 0 ? format.length : close;
        } else if (c === '\\') i++;
        else if (c === ';') (texts.push(format.slice(start, i)), start = i + 1);
    }
    texts.push(format.slice(start));
    sections = {texts, read: texts.map(readSection)};
    if (customs.size >= 256) customs.clear();
    customs.set(format, sections);
    return sections;
}

/**
 * A section read: what it writes, in order (digit places, its point, its percent and per-mille signs, its exponent,
 * literal text); its digit places before and after the point; the places it always writes — from its first 0 before
 * the point, to its last 0 after it; whether it groups thousands, and the power of ten it scales by (% and ‰ up; a
 * comma just before the point, or the digits' end, down a thousand).
 */
function readSection(text) {
    const items = [];
    let places = 0, point = -1, firstZero = -1, lastZero = -1, power = 0, exponent = null, comma = -1, commas = 0, groups = false;
    const literal = s => typeof items[items.length - 1] === 'string' ? items[items.length - 1] += s : items.push(s);
    for (let i = 0; i < text.length; i++) {
        const c = text[i];
        if (c === '0' || c === '#') {
            if (c === '0') {
                if (firstZero < 0) firstZero = places;
                lastZero = places + 1;
            }
            places++;
            items.push(DIGIT);
        } else if (c === '.') {
            if (point < 0) (point = places, items.push(POINT));
        } else if (c === ',') {
            if (places > 0 && point < 0)
                if (comma === places) commas++;
                else (groups ||= comma >= 0, comma = places, commas = 1);
        } else if (c === '%') (power += 2, items.push(PERCENT_SIGN));
        else if (c === '‰') (power += 3, items.push(PERMILLE_SIGN));
        else if (c === "'" || c === '"') {
            const close = text.indexOf(c, i + 1);
            literal(text.slice(i + 1, close < 0 ? text.length : close));
            i = close < 0 ? text.length : close;
        } else if (c === '\\') {
            if (i + 1 < text.length) literal(text[++i]);
        } else if (c === 'E' || c === 'e') {
            const sign = text[i + 1] === '+' || text[i + 1] === '-' ? text[i + 1] : '';
            let j = i + 1 + sign.length;
            if (text[j] !== '0') {
                literal(c);
                continue;
            }
            const from = j;
            while (text[j] === '0') j++;
            if (exponent) literal(text.slice(i, j));   // (a second exponent: written as it stands)
            else items.push(exponent = {char: c, plus: sign === '+', least: Math.min(j - from, 10)});
            i = j - 1;
        } else literal(c);
    }
    const before = point < 0 ? places : point;
    if (comma >= 0) comma === before ? power -= 3 * commas : groups = true;
    return {
        items, before, after: places - before, power, groups, exponent,
        least: firstZero >= 0 && firstZero < before ? before - firstZero : 0,   // (the whole digits always written)
        kept: lastZero > before ? lastZero - before : 0                         // (the fraction's digits always written)
    };
}

/**
 * A custom format's text of a magnitude and its sign. Its section is the value's — the negative one written as it is,
 * the first one with the sign in front (if it writes anything) — or, the value rounding to nought there, nought's (or,
 * with none, the first, but for nought); an integer or a decimal rounded to nought is unsigned.
 */
function customText(x, negative, kind, format, n) {
    const {texts, read} = sectionsOf(format);
    const pick = i => i < texts.length && texts[i] !== '' ? i : 0;
    let section = pick(x.c === 0n ? 2 : negative ? 1 : 0), y = x;
    for (; ;) {
        const s = read[section];
        if (y.c === 0n) break;
        y = {c: y.c, e: y.e + s.power};
        y = s.exponent ? toFigures(y, s.before + s.after, false) : quantize(y, -s.after, false);
        if (y.c !== 0n || pick(2) === section) break;
        section = pick(2);
        y = {c: 0n, e: 0};
    }
    if (y.c === 0n && kind !== 'f') negative = false;
    const text = writeSection(read[section], y, n);
    return negative && section === 0 && (text || placeOf(y) !== 0) ? n.neg + text : text;
}

/** A section's picture of a magnitude already scaled and rounded for it. */
function writeSection(s, y, n) {
    let whole, fraction, power = 0;
    if (s.exponent) {
        const digits = y.c === 0n ? '' : String(trimmed(y).c);
        if (y.c !== 0n) power = placeOf(y) - s.before;
        whole = digits.slice(0, s.before).padEnd(s.before, '0');     // (each whole place a digit: the exponent sees to it)
        fraction = digits.slice(s.before).padEnd(s.kept, '0');
    } else {
        const parts = partsOf(y, s.after);
        whole = parts.whole.padStart(s.least, '0');
        fraction = parts.fraction;
        while (fraction.length > s.kept && fraction.endsWith('0')) fraction = fraction.slice(0, -1);
    }
    const digit = position => whole[whole.length - position] +
        (s.groups && position > 1 && groupEnds(position - 1, n.sizes) ? n.grp : '');
    let out = '', place = s.before, written = 0, spilled = false, shown = 0;
    const spill = () => {                          // (whole digits past the places: written at the first of them, or the point)
        if (spilled) return;
        spilled = true;
        for (let position = whole.length; position > s.before; position--) out += digit(position);
    };
    for (const item of s.items) {
        if (item === DIGIT) {
            if (written++ < s.before) {
                spill();
                if (place <= whole.length) out += digit(place);
                place--;
            } else if (shown < fraction.length) out += fraction[shown++];
        } else if (item === POINT) {
            spill();
            if (fraction) out += n.dec;
        } else if (item === PERCENT_SIGN) out += n.pct;
        else if (item === PERMILLE_SIGN) out += n.pml;
        else if (typeof item === 'string') out += item;
        else out += exponentText(n, power, item.char, item.least, item.plus);
    }
    return out;
}

// ── the kinds ──

const MASKS = {1: 0xffn, 2: 0xffffn, 4: 0xffffffffn, 8: 0xffffffffffffffffn};

/**
 * An integer of `size` bytes (signed unless `unsigned`; a Number, or a BigInt) as its .NET type writes it by `format`
 * (null: G) in culture numbers `n`: D, X and B as .NET writes them for its type (a negative's X, its two's complement).
 */
export function formatInteger(v, size, unsigned, format, n = NUMBERS) {
    const big = BigInt(v), x = {c: big < 0n ? -big : big, e: 0}, spec = standardOf(format);
    if (!spec) return customText(x, big < 0n, 'i', format, n);
    const [letter, digits] = spec, upper = letter.toUpperCase();
    if (upper === 'D' || (upper === 'G' && digits < 1)) return (big < 0n ? n.neg : '') + String(x.c).padStart(Math.max(1, digits), '0');
    if (upper === 'X' || upper === 'B') {
        const text = (big & MASKS[size]).toString(upper === 'X' ? 16 : 2).padStart(Math.max(1, digits), '0');
        return letter === 'X' ? text.toUpperCase() : text;
    }
    return standardText(x, big < 0n, 'i', false, letter, digits, n);
}

/** A long scaled by 10^scale as the decimal it stands for writes it by `format` (null: G, its places kept). */
export function formatDecimal(v, scale, format, n = NUMBERS) {
    const big = BigInt(v), x = {c: big < 0n ? -big : big, e: -scale}, spec = standardOf(format);
    return spec ? standardText(x, big < 0n, 'd', false, spec[0], spec[1], n) : customText(x, big < 0n, 'd', format, n);
}

/** A double (or, `single`, a float) as .NET writes it by `format` (null: G, its shortest round-trip text). */
export function formatDouble(v, single, format, n = NUMBERS) {
    if (!Number.isFinite(v)) return Number.isNaN(v) ? n.nan : v < 0 ? n.ninf : n.inf;
    const negative = v < 0 || Object.is(v, -0), spec = standardOf(format), a = Math.abs(v);
    if (!spec) return customText(v === 0 ? {c: 0n, e: 0} : figuresOf(a, single ? 7 : 15), negative, 'f', format, n);
    const [letter, digits] = spec, upper = letter.toUpperCase();
    if (!'CEFGNPR'.includes(upper)) throw new RangeError(`no number format "${letter}" for a double`);
    const places = digits >= 0 ? digits : upper === 'C' ? n.cdigits : upper === 'P' ? n.pdigits : n.digits;
    const x = v === 0 ? {c: 0n, e: 0}                // (rounded here as .NET rounds a double's digits; standardText's, then, none)
        : upper === 'R' || (upper === 'G' && digits < 1) ? (single ? shortest32(v) : shortest64(v))
            : upper === 'G' ? figuresOf(a, digits)
                : upper === 'E' ? figuresOf(a, (digits >= 0 ? digits : 6) + 1)
                    : placesOf(a, upper === 'P' ? places + 2 : places);
    return standardText(x, negative, 'f', single, letter, digits, n);
}

/** A magnitude and its sign written by `format`, as a decimal of so many places would be (standard or custom). */
function decimalText(x, negative, format, n) {
    const spec = standardOf(format);
    return spec ? standardText(x, negative, 'd', false, spec[0], spec[1], n) : customText(x, negative, 'd', format, n);
}

const SCIENTIFIC = [];

/**
 * A number rounded to `figures` significant figures (half away from nought), written by `format` — or, showing more than
 * `most` digits (those before the point, and after it to its last that counts: .000123456, nine), in scientific
 * notation to as many figures: 1.23457e8, 1.23456e-4 (LiveColumn.Figures, MostDigits).
 */
export function figuredText(x, negative, figures, most, format, n = NUMBERS) {
    const y = trimmed(toFigures(x, figures, false)), place = placeOf(y);
    if (most == null || Math.max(place, 0) + Math.max(0, -y.e) <= most) return decimalText(y, negative, format, n);
    const scientific = SCIENTIFIC[figures] ??= figures > 1 ? `0.${'#'.repeat(figures - 1)}e0` : '0e0';
    return customText(y, negative, 'd', scientific, n);
}

/** A format's standard specifier, [letter, digits] (digits -1: none given), or null for a custom format. */
function standardOf(format) {
    if (!format) return ['G', -1];
    const m = /^([A-Za-z])(\d*)$/.exec(format);
    return m && [m[1], m[2] ? Number(m[2]) : -1];
}

/** A Guid's 16 bytes, as .NET lays it out, as Guid.ToString(format) writes it: D (null), N, B, P or X. */
export function formatGuid(bytes, format) {
    const h = i => hex2(bytes[i]), d = `${h(3)}${h(2)}${h(1)}${h(0)}-${h(5)}${h(4)}-${h(7)}${h(6)}-${h(8)}${h(9)}-` +
        `${h(10)}${h(11)}${h(12)}${h(13)}${h(14)}${h(15)}`;
    switch (format) {
        case 'N': case 'n':
            return d.replaceAll('-', '');
        case 'B': case 'b':
            return `{${d}}`;
        case 'P': case 'p':
            return `(${d})`;
        case 'X': case 'x':
            return `{0x${d.slice(0, 8)},0x${d.slice(9, 13)},0x${d.slice(14, 18)},{` +
                [8, 9, 10, 11, 12, 13, 14, 15].map(i => '0x' + h(i)).join(',') + '}}';
        default:
            return d;
    }
}

// ── times: a custom pattern, as a time's parts write it (UTC, the Gregorian calendar) ──

const TICKS_DAY = 864_000_000_000n, DAYS_1970 = 719_162n;
const patterns = new Map();

/** A .NET DateTime's ticks (100 ns since 0001, UTC) as its parts: year, month, day, weekday, hour, minute, second, ticks. */
function timeParts(ticks) {
    const of = Number(ticks % TICKS_DAY), date = new Date(Number(ticks / TICKS_DAY - DAYS_1970) * 86_400_000);
    return {
        y: date.getUTCFullYear(), M: date.getUTCMonth() + 1, d: date.getUTCDate(), w: date.getUTCDay(),
        H: Math.floor(of / 36_000_000_000), m: Math.floor(of / 600_000_000) % 60, s: Math.floor(of / 10_000_000) % 60, f: of % 10_000_000
    };
}

/**
 * A month's name beside its day: the nearest d before it (in the pattern as written) ends a run of at most two — the
 * day of the month, not of the week — or, failing that, the nearest after it begins one. Its genitive form, there.
 */
function besideDay(pattern, at, length) {
    let i = pattern.lastIndexOf('d', at - 1), run = 0;
    if (i >= 0) {
        while (i - run >= 0 && pattern[i - run] === 'd') run++;
        if (run <= 2) return true;
    }
    i = pattern.indexOf('d', at + length);
    if (i < 0) return false;
    for (run = 0; pattern[i + run] === 'd';) run++;
    return run <= 2;
}

/** A pattern read once: its letters' runs and its literal text, in order. */
function tokensOf(pattern) {
    let tokens = patterns.get(pattern);
    if (tokens) return tokens;
    tokens = [];
    const literal = s => typeof tokens[tokens.length - 1] === 'string' ? tokens[tokens.length - 1] += s : tokens.push(s);
    for (let i = 0; i < pattern.length;) {
        const c = pattern[i];
        if (c === "'" || c === '"') {               // (quoted text, its backslashes escaping)
            let j = i + 1, text = '';
            for (; j < pattern.length && pattern[j] !== c; j++) text += pattern[j] === '\\' ? pattern[++j] ?? '' : pattern[j];
            literal(text);
            i = j + 1;
        } else if (c === '\\') {
            literal(pattern[i + 1] ?? '');
            i += 2;
        } else if (c === '%') {                     // (the next character, a pattern of its own: %d, a day)
            for (const token of tokensOf(pattern[i + 1] ?? '')) typeof token === 'string' ? literal(token) : tokens.push(token);
            i += 2;
        } else if ('gyMdhHmsfFtz'.includes(c)) {
            let run = 1;
            while (pattern[i + run] === c) run++;
            tokens.push({c, run, genitive: c === 'M' && run >= 3 && besideDay(pattern, i, run)});
            i += run;
        } else {
            if (c === 'K') tokens.push({c, run: 1});
            else if (c === ':' || c === '/') tokens.push({c, run: 1});
            else literal(c);
            i++;
        }
    }
    if (patterns.size >= 256) patterns.clear();
    patterns.set(pattern, tokens);
    return tokens;
}

const two = (value, run) => String(value).padStart(Math.min(run, 2), '0');

/** A time's parts written by a pattern's tokens, in dates `d`. */
function timeText(t, pattern, d) {
    let out = '';
    for (const token of tokensOf(pattern)) {
        if (typeof token === 'string') {
            out += token;
            continue;
        }
        const {c, run} = token;
        switch (c) {
            case 'g': out += d.era; break;
            case 'y': out += run <= 2 ? String(t.y % 100).padStart(run, '0') : String(t.y).padStart(run, '0'); break;
            case 'M':
                out += run <= 2 ? String(t.M).padStart(run, '0')
                    : (token.genitive && d.mgen ? (run === 3 ? d.mgabbr : d.mgen) : (run === 3 ? d.mabbr : d.months))[t.M - 1];
                break;
            case 'd': out += run <= 2 ? String(t.d).padStart(run, '0') : (run === 3 ? d.dabbr : d.days)[t.w]; break;
            case 'h': out += two(t.H % 12 || 12, run); break;
            case 'H': out += two(t.H, run); break;
            case 'm': out += two(t.m, run); break;
            case 's': out += two(t.s, run); break;
            case 'f': case 'F': {                   // (the second's first digits, truncated; F's without its trailing noughts —
                const shown = Math.min(run, 7);     //  none at all, and then no point before them either)
                const digits = String(Math.floor(t.f / 10 ** (7 - shown))).padStart(shown, '0');
                const text = c === 'f' ? digits : digits.replace(/0+$/, '');
                if (text) out += text;
                else if (out.endsWith('.')) out = out.slice(0, -1);
                break;
            }
            case 't': out += run === 1 ? (t.H < 12 ? d.am : d.pm).slice(0, 1) : t.H < 12 ? d.am : d.pm; break;
            case 'z': out += run === 1 ? '+0' : run === 2 ? '+00' : '+00:00'; break;   // (UTC)
            case 'K': out += 'Z'; break;
            case ':': out += d.ts; break;
            case '/': out += d.ds; break;
        }
    }
    return out;
}

/** A .NET DateTime's ticks as a custom `pattern` writes them (a standard format, LiveGrid spells out), in dates `d`. */
export function formatTime(ticks, pattern, d = DATES) {
    return timeText(timeParts(BigInt(ticks)), pattern, d);
}

const INSTANT = "yyyy'-'MM'-'dd HH':'mm':'ss.FFFFFFF 'UTC'";   // (a time's tooltip: the instant to its last digit)
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

const TICKS_1970 = 621_355_968_000_000_000n, TICKS_MASK = (1n << 62n) - 1n;
const hex2 = b => b.toString(16).padStart(2, '0');

const NONE = {1: -128, 2: -32768, 4: -2147483648, 8: -(2n ** 63n)};

/**
 * What a cell shows, and on hover: { text, title, cut } from the row's bytes, by the column's kind, written by its
 * `format` (a .NET format string; none, .NET's default) in `culture` (cultureOf's; the invariant culture's if none) —
 * int (an integer, `size` bytes: 1, 2, 4, 8; `unsigned`), fixed (an integer scaled by 10^scale: the decimal it stands
 * for; on hover, whole), float (`size` 8 or 4; on hover, its shortest round-trip text), time (µs since 1970, or with
 * `unit` 'ticks' a .NET DateTime; its `format` a custom pattern — in the invariant culture's names, `invariant` — as
 * UTC; on hover, the instant to its last digit), bool, enum (the column's `dictionary` entry for the value), text
 * (`size` bytes: a length, 0x80 in it if the text was cut, then UTF-8), percent (the fraction one field is of another,
 * `of` over `over`, a column's name or { offset, size, unsigned, float }; P if no format), uuid (a .NET Guid's 16 bytes).
 * A signed integer's least value is no value, as are NaN, a zero DateTime, an empty Guid and a fraction of nothing.
 */
export function formatCell(column, row, dv, columns, culture = INVARIANT) {
    const o = column.offset ?? 0, size = column.size ?? 8, format = column.format, n = culture.n;
    switch (column.kind) {
        case 'int': {
            const v = integer(dv, o, size, column.unsigned);
            if (!column.unsigned && (v === NONE[size] || v === NONE[8])) return {text: ''};
            if (!column.figures) return {text: formatInteger(v, size, column.unsigned, format, n)};
            const big = BigInt(v);
            return {text: figuredText({c: big < 0n ? -big : big, e: 0}, big < 0n, column.figures, column.mostDigits, format, n),
                title: formatInteger(v, size, column.unsigned, null, n)};
        }
        case 'fixed': {
            const v = BigInt(integer(dv, o, size, column.unsigned)), scale = column.scale ?? 0;
            if (!column.unsigned && v === BigInt(NONE[size])) return {text: ''};
            return {
                text: column.figures ? figuredText({c: v < 0n ? -v : v, e: -scale}, v < 0n, column.figures, column.mostDigits, format, n)
                    : formatDecimal(v, scale, format, n),
                title: formatDecimal(v, scale, null, n)
            };
        }
        case 'float': {
            const single = size === 4, v = single ? dv.getFloat32(o, true) : dv.getFloat64(o, true);
            if (Number.isNaN(v)) return {text: ''};
            const text = column.figures && Number.isFinite(v)  // (a double's own digits rounded half away, as toPrecision does)
                ? figuredText(decimalOf(Math.abs(v).toPrecision(column.figures)), v < 0 || Object.is(v, -0), column.figures,
                    column.mostDigits, format, n)
                : formatDouble(v, single, format, n);
            return {text, title: formatDouble(v, single, null, n)};
        }
        case 'time': {
            let ticks;
            if (column.unit === 'ticks') {                     // (a DateTime: 100 ns since 0001, its kind in the top two bits)
                ticks = dv.getBigUint64(o, true) & TICKS_MASK;
                if (ticks === 0n) return {text: ''};
            } else {
                const micros = dv.getBigInt64(o, true);
                if (micros === NONE[8]) return {text: ''};
                ticks = micros * 10n + TICKS_1970;
            }
            const t = timeParts(ticks);
            return {text: timeText(t, format ?? 'MM/dd/yyyy HH:mm:ss', column.invariant ? DATES : culture.d), title: timeText(t, INSTANT, DATES)};
        }
        case 'bool':
            return dv.getUint8(o) ? {text: '✓', title: 'yes'} : {text: '', title: 'no'};
        case 'enum': {
            const v = integer(dv, o, column.size ?? 1, column.unsigned ?? true);
            return {text: column.dictionary?.[String(v)] ?? `#${v}`};
        }
        case 'text': {
            const length = row[o] & 0x7f, cut = (row[o] & 0x80) !== 0;
            const text = utf8.decode(row.subarray(o + 1, o + 1 + Math.min(length, size - 1)));
            return cut ? {text: text + '…', title: text + '…', cut: true} : {text};
        }
        case 'percent': {
            const field = f => typeof f === 'string' ? columns.find(c => c.name === f) : f;
            const of = field(column.of), over = field(column.over);
            if (!of || !over) return {text: ''};
            const read = f => {
                if (f.float || f.kind === 'float') return f.size === 4 ? dv.getFloat32(f.offset ?? 0, true) : dv.getFloat64(f.offset ?? 0, true);
                const v = integer(dv, f.offset ?? 0, f.size ?? 8, f.unsigned);
                return !f.unsigned && (v === NONE[f.size ?? 8] || v === NONE[8]) ? NaN : Number(v);
            };
            const a = read(of), b = read(over);
            if (b === 0 || Number.isNaN(a) || Number.isNaN(b)) return {text: ''};
            return {text: formatDouble(a / b, false, format ?? 'P', n)};
        }
        case 'uuid': {
            const b = row.subarray(o, o + 16);
            if (b.every(x => x === 0)) return {text: ''};
            return {text: formatGuid(b, format), title: formatGuid(b)};
        }
        default:
            return {text: ''};
    }
}

// ───────────── the body ─────────────

const QUIET_MS = 1_000;
const TIP_DELAY_MS = 500;                  // (a pointer resting on a cell this long: its tooltip shown, as a browser's)
const PASSIVE_AFTER_MS = 2_000;            // (out of sight this long: the session told, its changes not read)
const ANSWER_LATE_MS = 8_000;              // (requests unanswered this long — four round trips, if longer — given up and the window
                                           //  asked again, the session told it's in sight; then twice as long, up to a minute)
const RATE_SPAN_MS = 10_000;               // (the live top's arrival rate: over its totals this long)
const CHANGED_KEPT = 8_192;                // (keys changed and not drawn past this — a hidden page, kept live — drawn whole)
const HEARD_KEPT_MS = 60_000;              // (when a change was heard of, kept this long: longer than any flash)
const STAMP_LAG_MS = 500;                  // (the longest a live arrival's trip to the page is taken to be, on a quick link)
const STAMP_LAGS_KEPT = 32;                // (the last states' least live-arrival lags: the least of them, the clocks' difference)
const LAG_SHIFT_STATES = 8;                // (flashes run this many states' median slowest lag after their stamps: lagShift)
const STALE_AFTER_MS = 500;                // (a new view's rows later than this, at most: the rows drawn greyed)
const SCROLLING_MS = 1_000;                // (the rows scrolled a way this long ago: still scrolling it — its scrollbar shown)
const ANSWERS_KEPT = 5;                    // (the last answers' times: their median, the page's round trip)
const TRIPS_KEPT = 5;                      // (the last round trips, answers' and Tap.Sync's: their median, a live arrival's most)

/**
 * How long a new view's first rows may take before the rows drawn are greyed: twice the page's round trip — the median
 * of its last answers' times, each from a request to its rendezvous — and no more than 500 ms (500 ms till answered).
 */
export function staleAfter(took) {
    if (took.length === 0) return STALE_AFTER_MS;
    const sorted = [...took].sort((a, b) => a - b);
    return Math.min(STALE_AFTER_MS, 2 * sorted[sorted.length >> 1]);
}

/**
 * A row's arrival, by its `stamp` field ({ offset, size, unit }, as a time column's) — or, unit 'uuid7', by its key's
 * time — in ms since 1970: null for none.
 */
export function stampOf(stamp, row, dv, key) {
    if (stamp.unit === 'uuid7') return keyTime(key);
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
 * A UUIDv7 key's time (RFC 9562: its first 48 bits, ms since 1970), from the page's text of it — a .NET Guid's bytes in
 * memory, its first two fields little-endian: null if it isn't one (another size, version or variant).
 */
export function keyTime(key) {
    if (typeof key !== 'string' || key.length !== 16) return null;
    const b = i => key.charCodeAt(i);
    if (b(7) >> 4 !== 7 || (b(8) & 0xC0) !== 0x80) return null;
    return ((b(0) | b(1) << 8 | b(2) << 16) + b(3) * 0x1000000) * 0x10000 + (b(4) | b(5) << 8);
}

/**
 * Whether the rows' stamps agree with the page's clock: the least lag of the last live arrivals (heard less stamped,
 * ms; a state's least each, as GridBody keeps them) within a trip's length either way — 500 ms, and later by the page's
 * round trip (`trip`, ms: its median, answers' and Tap.Sync's), as no live arrival takes longer to reach the page than
 * a round trip does, however slow the link.
 * Past it, the clocks differ by more, and flashes run from when the page hears of what they show; back within it, from
 * the stamps again. (So on a slow link, clocks a round trip apart pass for agreeing.)
 */
export function stampsTrusted(lags, trip = 0) {
    if (lags.length === 0) return true;
    const least = Math.min(...lags);
    return least >= -STAMP_LAG_MS && least <= STAMP_LAG_MS + trip;
}

/**
 * How many of the live top's first rows arrived since the window before (`was`, its keys in order; `now`, the window's
 * now): those above the row that was first — or, that row gone, above the first still held. A window by key (`byKey`,
 * its keys compared as `kind` says: keysByKey) counts those above the row that was first, gone or not, so when more
 * arrived than it holds — none still held — every row is one; another order can't say what came from below, so none.
 */
export function arrivedAbove(was, now, byKey = false, kind = undefined) {
    if (was.length === 0) return 0;
    if (byKey) {
        let top = 0;
        while (top < now.length && compareKeys(now[top], was[0], kind) > 0) top++;
        return top;
    }
    const first = now.indexOf(was[0]);
    if (first >= 0) return first;
    const held = new Set(was);
    return Math.max(0, now.findIndex(key => held.has(key)));
}

/**
 * How late the link usually makes the page hear of a change (ms): the median of the last states' slowest lags (`lags`,
 * heard less stamped, one a state: its live changes' longest) — as the page's lag says, smoothed. Null with none.
 */
export function lagShift(lags, kept = LAG_SHIFT_STATES) {
    if (lags.length === 0) return null;
    const recent = lags.slice(-kept).sort((a, b) => a - b);
    return Math.max(0, recent[recent.length >> 1]);
}

/**
 * When a change's flash starts (ms since 1970): its `stamp` as late as the link usually makes the page hear of one
 * (`shift`: lagShift) — or when the page `heard` of it, if that was sooner — so on a slow link every change a state
 * brings flashes from its start as it's drawn, and only one later than usual starts as far on as it's later; on a quick
 * link, near its stamp, pages side by side (or one reloaded) all but in step. No stamp, or no shift (none measured, or
 * the stamps not trusted): when the page heard of it.
 */
export function flashFrom(heard, stamp, shift) {
    if (stamp === null || stamp === undefined || shift === null) return heard;
    return heard === undefined ? stamp + shift : Math.min(heard, stamp + shift);
}

/**
 * A flash `elapsed` ms after what it shows happened, `duration` ms long: the animation delay that shows it where it has
 * got to (negative: under way), or null if it's over — so pages side by side, or one reloaded, show it alike.
 */
export function flashDelay(elapsed, duration) {
    if (!(duration > 0) || elapsed >= duration) return null;
    return elapsed > 0 ? -elapsed : 0;
}

/** A figure to two significant figures, as a status shows it: under a hundredth, nought. */
export function twoFigures(value) {
    if (!(value >= 0.01)) return '0';
    const step = 10 ** (Math.floor(Math.log10(value)) - 1);
    const rounded = Math.round(value / step) * step;
    return rounded.toFixed(Math.max(0, 1 - Math.floor(Math.log10(rounded))));
}

/** Whether a figure shown should show again: it reads differently to two figures, and moved by 5 % or more. */
export function moved(shown, now) {
    return twoFigures(shown) !== twoFigures(now) && (shown === 0 || Math.abs(now - shown) >= 0.05 * Math.abs(shown));
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
 * element the chrome leaves empty — till its first window is answered, the view's places as placeholders. A cell's
 * tooltip (formatCell's title, its aria-description too) is the grid's own: it says what the cell under the pointer
 * holds now, as rows move and change, and nothing over a placeholder. Options:
 *   columns    [{ name, label, kind, offset, size, unsigned, scale, unit, dictionary, of, over, format, figures,
 *                mostDigits, invariant, frozen, align, overflow, class, link, mark, auto }] (formatCell): align 'start',
 *              'center', 'end', 'left' or 'right' (none: a number's end, the rest's start); figures, a number rounded to
 *              so many significant figures, written in scientific notation past mostDigits digits (figuredText);
 *              overflow 'clip', 'ellipsis-start' or 'clip-start' (none: an ellipsis at the end; cut at the start, a cell
 *              runs right to left, its text isolated); class, the cells' own; auto, its width measured from the first
 *              rows answered (else the grid's style gives it, --tg-w<i>)
 *   culture    the culture the cells are written in: what of it differs from the invariant culture's (cultureOf)
 *   rowHeight  px, one for every row
 *   overscan   the most rows held either side of the view, kept as the reader scrolls (LiveGrid.OverscanCount; 150): as
 *              many as the session says the link carries in time (its status value; GridOptions.OverscanFill), none
 *              before it says, and once the first rows are drawn a view at least; and ahead, a view at least — before
 *              then, fewer if the link carries fewer in time
 *   keyOrder   how the keys compare, to place a window sent without them ("guid", "int", "uint": keysByKey)
 *   tapline   the page's TaplineClient (tapline.js; globalThis.tapline if none); binding: the grid's watch (its
 *              session's Binding)
 *   request(request)   sends a window request to the session: { id, first, count, before, after, holds, letIn, view,
 *                      racing, top, anchor } — anchor, from a live window away from its top, the key of the row shown at
 *                      the view's first position (a hold keeps it there)
 *   maxRows    the session's most rows a window (GridOptions.MaxWindowRows): never asked for more
 *   minInterval, maxOutstanding  requests at most once this many ms (50), and while fewer are unanswered (4)
 *   visible(visible)   the grid in sight or passive: out of it two seconds — its page hidden, or the grid more than
 *                      a view from the viewport — the session stops reading the source's changes; back, at once, the
 *                      window asked again, the rows greyed if its answer is late (or, more arrived at the live top than
 *                      it holds, drawn as placeholders) till it comes
 *   passive    false: never passive, kept live out of sight (its page holds a shared Web Lock, as a browser keeps a
 *              tab holding one from sleep)
 *   status(status)     what the chrome shows: { total, totalKind, live, stale, holds, letIn, fresh, blanks }
 *   statusless true: the watch has no status value (a harness's) — the grid holds its own overscan; else, its status
 *              not yet published, none is told (the first window its view's alone)
 *   statusLine the status line's element, drawn as the grid's status value streams to the page (statusParts), and as
 *              its freshness and selection change; none, none drawn. rowName, rowsName: what it calls a row, and rows
 *              ('row', 'rows')
 *   host      the grid's element, around the body and its headings (the body's own if none): the stylesheet places the
 *              columns from the room each has (--tg-w<i>, given above it: LiveGrid's style), in its font, at --tg-x<i>
 *              (and --tg-width, them all); the module keeps here a measured column's room, the row height and the
 *              scrollbars' room (--tg-row-height, --tg-scrollbar, --tg-scrollbar-x), and how its scrollbars show
 *              (data-overlay-scrollbars: the platform's overlaid; data-scrolling-x, -y); it takes the keys, and it's the grid to assistive
 *              technology (aria-rowcount, aria-activedescendant)
 *   head       the headings' element: scrolled sideways with the rows (its frozen headings sticky in it)
 *   headerRows rows of headings above the body: the rows' aria-rowindex counts them
 *   view       the view (sort and filter) asked in at first: its number (GridSession.SetView); orders of an earlier
 *              one are passed over
 *   arrivedAt  the rows' arrival time, if they carry one ({ offset, size, unit }, as a time column's; unit 'uuid7': the
 *              key's, a UUIDv7's milliseconds): a row's arrival flash runs from it wherever the row is drawn — pages
 *              side by side, or one reloaded, flash alike — while the stamps agree with the page's clock (the least
 *              lag of the last live arrivals, a state's least each while the page is shown, within 500 ms either way,
 *              a trip's most, and the page's round trip more the later way: stampsTrusted); else, as without stamps,
 *              from when the page heard of the changes
 *   changedAt  the rows' last change's time, if they carry one (as arrivedAt): a changed cell's flash runs from it, and,
 *              while the stamps agree, lag(ms) tells the page's lag each second — the longest a change it saw happen
 *              took from its stamp to being drawn (none drawn, not told); lag(null), the stamps not agreeing
 *   newAbove   true: a pill over the rows while the grid holds its place with rows arrived above — how many (999+ past
 *              that), in English ("12 new above"), and a click back to the live top (top), the page's own: no round trip
 *              (LiveGrid.NewAbove; its class tg-new-above)
 *   jumpHold   ms a jump to the live top (top, Home) whose rows aren't here yet keeps the rows in view while it asks for
 *              them (LiveGrid.JumpHold; 500): they come in that time, the grid goes straight to them; not, it goes then,
 *              their places placeholders till they come — given up as the reader moves
 *   selection  'none', 'single' or 'multiple'; selected: the keys selected at first (setSelection after)
 *   rowClick({ key, column, ctrl, shift, meta, alt }), rowActivate({ key, column }), rowSelect({ key })
 *              a row clicked, activated (a double click, Enter, a link column's cell) or selected (Space): by key, its
 *              column named where there is one — each only as heard: a click if `clickable` (its rows then show they
 *              can be: the body's tg-clickable) or rows are selected, an activation if `activatable` (else link columns
 *              are plain), a selection if rows are selected (setRowEvents after)
 */
export class GridBody {
    constructor(root, options) {
        this.root = root;
        this.options = options;
        this.host = options.host ?? root;
        this.head = options.head ?? null;
        this.headerRows = options.headerRows ?? 0;
        this.columns = options.columns;
        this.culture = cultureOf(options.culture);   // (the cells written in it: the circuit's, as LiveGrid resolved the columns)
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
        this.pool = [];                    // row elements: { el, cells, key, bytes, position, y, selected, focused }
        this.rowOf = new Map();            // a row element → its pool entry
        this.changed = new Set();          // keys changed since last drawn
        this.updated = new Set();          // of those, changes the page saw happen (rows its window held, arrivals at the
                                           // live top): drawn in the next frame, their lag counts; scrolled to later, not
        this.whole = true;                 // a state handed over whole: every row drawn afresh
        this.arrived = new Map();          // keys new at the live top → when heard of (ms since 1970), till drawn
        this.changedAt = new Map();        // keys changed → when heard of: their cells' flashes run from it
        this.revealed = new Set();         // keys arrived while the grid held, revealed since (live again): new to the reader,
                                           // their arrival flash runs from when the page heard of them, not their stamps
        this.flashing = new Map();         // keys whose arrival flash is under way → when it runs from (ms since 1970): drawn
                                           // again (scrolled out and back), each goes on where it has got to
        this.heldFrom = null;              // held: the newest arrival stamp the page showed live before (rows stamped later
                                           // arrived while it held); null, none known
        this.heldAbove = 0;                // the rows arrived above, as the last held status said them (without stamps, the
                                           // first so many of the live top are those)
        this.seen = new Map();             // keys drawn → { texts, at }: their cells as last drawn, each's last change (ms)
        this.stamp = options.arrivedAt ?? null;   // the rows' own arrival time, if they carry one (arrivedAt)
        this.changeStamp = options.changedAt ?? null;   // and their last change's (changedAt)
        this.stamped = !!(this.stamp || this.changeStamp);   // (their stamps trusted: as the last live arrivals' lags say)
        this.lags = [];                    // the last states' least live-arrival lags (ms): heard less stamped
        this.slowest = [];                 // and their slowest live changes' lags (ms): the flashes' shift (lagShift)
        this.stateSlowest = null;          // (this state's, as it's applied)
        this.durations = new Map();        // a flash's duration as the page's CSS has it (ms)
        this.lagWorst = -1;                // the longest a change drawn lately took from its stamp to the page (ms; -1, none)
        this.scrolledShown = false;        // whether the grid's element says it's scrolled from the top
        this.staleShown = false;           // and that its rows are stale (data-stale: a view replaced, a read refused)
        this.staleTimer = 0;               // till a new view's rows are late enough to grey those drawn (staleAfter)
        this.toTop = false;                // the live top asked for (top, Home): said in the requests till the reader moves
        this.jumpHold = options.jumpHold ?? 500;   // (ms a jump to the top waits for its rows before drawing placeholders)
        this.jumpTimer = 0;                // while a jump to the top waits for its rows, the rows in view kept (top)
        this.holding = false;              // asked to stay below a live window's top, the hold not here yet: live orders
                                           // still arriving were sent before it, and the rows in view stay as drawn
        this.kept = null;                  // and meanwhile, the live window's rows as they were when it asked (table
                                           // entries, by key): drawn from as later live states drop them
        this.refused = -1;                 // a new view refused before any rows of its own: its number
        this.took = [];                    // the last answers' times (ms), oldest first: the page's round trip
        this.trips = [];                   // and Tap.Sync's with them (ms): what a live arrival takes at most (stampsTrusted)
        this.down = true;                  // the reader's direction
        this.frame = 0;                    // requestAnimationFrame id, while one is due
        this.widths = new Map();           // column name → px: an auto column's room, measured once from the first rows answered, then held
        this.blanks = 0;                   // frames with a placeholder in view (blank sightings)
        this.fresh = 'unknown';            // 'fresh' (Tap.Sync answered) | 'checking' | 'behind' | 'unknown'
        this.freshSaid = 'unknown';        // as the status line says it: the last word stands while the router's asked
        this.statusValue = null;           // the grid's status value, as last read (readStatus)
        this.statusShown = null;           // the status line as drawn (its text)
        this.statusless = options.statusless === true;   // the watch has no status value (a harness's): the overscan the grid's own
        this.hidden = false;               // passive: out of sight a while, the session told (its changes not read), nothing asked
        this.docHidden = false;            // the page hidden (visibilitychange)
        this.inView = true;                // the grid within a view of the viewport (IntersectionObserver)
        this.passiveAt = 0;                // when it went passive
        this.passiveTimer = 0;             // till it goes passive, out of sight
        this.waking = false;               // back in sight: till an answer asked since says its rows are current
        this.wokeAt = 0;
        this.answerTimer = 0;              // till requests unanswered are given up and the window asked again (#watchAnswers)
        this.answerAfter = ANSWER_LATE_MS;
        this.answeredAt = 0;               // when a request was last answered
        this.wakeBlank = false;            // waking, more arrived at the live top than it holds: its places to draw if late
        this.blankShown = false;           // drawing its places, not its rows
        this.totals = [];                  // the live top's totals lately ({ at, total }): its arrival rate
        this.rate = 0;                     // rows arriving at the live top a second, as last measured
        this.disposed = false;
        this.waits = new Map();            // request id → AbortController of its rendezvous wait
        this.selection = options.selection ?? 'none';
        this.clickable = options.clickable ?? false;   // (a click heard: LiveGrid's OnRowClick)
        this.activatable = options.activatable ?? false;   // (an activation heard: OnRowActivate)
        this.selected = new Set(options.selected ?? []);
        this.focusKey = null;              // the row the keys move from, by key: followed as it moves
        this.focusAt = null;               // its position, as last known (or one moved to and not answered yet)
        this.active = null;                // the focused row's element id, as aria-activedescendant has it
        this.scrollLeft = 0;
        this.rowCount = -1;
        this.id = `tg${++grids}`;
        this.answered = false;             // a window answered yet: till then, the view's places drawn as placeholders
        this.tipCell = null;               // the cell under the pointer (its element): its tooltip shown, or due
        this.tipAt = null;                 // where the pointer is: { x, y } (client px)
        this.tipTimer = 0;                 // till a resting pointer's tooltip shows
        this.tipShown = false;
    }

    /** Builds the body, subscribes to the watch, and asks for the first window — measured first, in the same task. */
    start() {
        const root = this.root, host = this.host;
        // (The skeleton shimmering as the server drew it: the placeholders go on from where it has got to — their shimmer
        // timed from when it began, not afresh. None, the page's own clock.)
        this.shimmerFrom = root.getAnimations?.({subtree: true})
            .find(a => a.effect?.target === root && a.effect.pseudoElement === '::before')?.startTime ?? 0;
        root.classList.add('tapline-grid');
        root.classList.toggle('tg-clickable', this.clickable);
        host.style.setProperty('--tg-row-height', `${this.rowHeight}px`);
        root.replaceChildren();
        this.scrollerEl = Object.assign(document.createElement('div'), {className: 'tg-scroller', tabIndex: host === root ? 0 : -1});
        this.surfaceEl = Object.assign(document.createElement('div'), {className: 'tg-surface'});
        this.layerEl = Object.assign(document.createElement('div'), {className: 'tg-rows'});
        this.scrollerEl.setAttribute('role', 'rowgroup');
        this.surfaceEl.append(this.layerEl);
        this.scrollerEl.append(this.surfaceEl);
        this.tipEl = Object.assign(document.createElement('div'), {className: 'tg-tip', id: `${this.id}-tip`});
        this.tipEl.setAttribute('role', 'tooltip');
        if (this.tipEl.showPopover) this.tipEl.popover = 'manual';   // (the top layer: over whatever clips or stacks the grid)
        else this.tipEl.hidden = true;
        root.append(this.scrollerEl, this.tipEl);
        if (this.options.newAbove) {       // (rows arrived above a held grid: how many, and a click back to the live top)
            this.pillEl = Object.assign(document.createElement('button'), {type: 'button', className: 'tg-new-above', hidden: true});
            this.onPill = () => {
                this.pillEl.hidden = true;     // (taken: gone at once, the jump under way)
                this.top();
            };
            this.onPillWheel = e => this.#wheelOver(e);   // (over the rows' top: a wheel there scrolls them, as beside it)
            this.pillEl.addEventListener('click', this.onPill);
            this.pillEl.addEventListener('wheel', this.onPillWheel, {passive: false});
            root.append(this.pillEl);
        }
        this.keys = host === root ? this.scrollerEl : host;   // (what takes the keys: the grid, or the body alone)
        // (Its scrollbars, the stylesheet's beside frozen columns: shown as the platform's are — overlaid, while scrolling.)
        host.toggleAttribute('data-overlay-scrollbars', overlayScrollbars());
        this.scrollingAt = {x: 0, y: 0};   // the rows last scrolled each way (ms, the page's clock): its scrollbar shown since
        this.scrollingTimer = {x: 0, y: 0};
        this.rtl = getComputedStyle(host).direction === 'rtl';   // (a cell cut at its start says its side physically)
        this.#styleColumns();

        this.onScroll = () => this.#scrolled();
        this.onWheel = e => this.#wheel(e);
        this.onKey = e => this.#key(e);
        this.onClick = e => this.#click(e);
        this.onDouble = e => this.#double(e);
        this.onFocus = () => this.keys.focus({preventScroll: true});   // (clicked: the grid takes the focus, not its scroller)
        this.onVisibility = () => this.#visibility();
        this.onPointer = e => this.#pointer(e);
        this.onPointerOut = () => this.#tipHide(true);
        this.onPress = () => (this.#tipHide(), this.#stay());
        this.onCopy = e => this.#copy(e);
        this.scrollerEl.addEventListener('scroll', this.onScroll, {passive: true});
        this.scrollerEl.addEventListener('pointermove', this.onPointer, {passive: true});
        this.scrollerEl.addEventListener('pointerleave', this.onPointerOut, {passive: true});
        this.scrollerEl.addEventListener('pointerdown', this.onPress, {passive: true});
        this.scrollerEl.addEventListener('wheel', this.onWheel, {passive: false});
        this.keys.addEventListener('keydown', this.onKey);
        this.layerEl.addEventListener('click', this.onClick);
        this.layerEl.addEventListener('dblclick', this.onDouble);
        if (this.keys !== this.scrollerEl) this.scrollerEl.addEventListener('focus', this.onFocus);
        document.addEventListener('visibilitychange', this.onVisibility);
        document.addEventListener('copy', this.onCopy);
        this.docHidden = document.visibilityState === 'hidden';
        if (this.docHidden && this.options.passive !== false) this.#passivate();   // (started out of sight: asks once shown)
        if (typeof IntersectionObserver === 'function') {   // (a view's margin: back in sight just before it scrolls in)
            this.sight = new IntersectionObserver(seen => {
                const inView = seen[seen.length - 1].isIntersecting;
                if (inView === this.inView) return;
                this.inView = inView;
                this.#seen();
            }, {rootMargin: '100% 0px'});
            this.sight.observe(this.scrollerEl);
        }
        if (this.options.passive === false && navigator.locks?.request)   // (kept live: its tab held from sleep, as a lock does)
            navigator.locks.request('tapline-grid-live', {mode: 'shared'}, () => new Promise(release => this.unlock = release));
        this.resizer = new ResizeObserver(() => this.#resized());
        this.resizer.observe(this.scrollerEl);
        if (this.options.lag && this.changeStamp)       // (each second, the longest a change drawn took to arrive —
            this.lagTimer = setInterval(() => {         // none drawn, as it was; the stamps not trusted, not known)
                const worst = this.lagWorst;
                this.lagWorst = -1;
                if (!this.stamped) this.options.lag(null);
                else if (worst >= 0) this.options.lag(worst);
            }, 1000);

        this.off = this.tapline.subscribe(this.options.binding, (state, id, info) => this.#state(state, info),
            error => this.#ended(error), undefined, {changed: true});
        this.#resized();                   // measured, then the first window asked for: never "asked for nothing"
    }

    /**
     * A new sort or filter, the `view`th (the next, if not said): its top asked for at once. The rows drawn stay until
     * its first rows arrive, then go, the grid at its top — greyed meanwhile (the host's data-stale) if they're late —
     * unless its order came first, and was taken as the word of it.
     */
    reset(view = this.view + 1) {
        if (view <= this.view) return;
        this.#asked(view);
        this.#showStale();
        this.#want();
        this.#schedule();
    }

    /** A new set of columns: drawn afresh; widths measured for those not measured before. */
    setColumns(columns) {
        this.#tipHide(true);
        this.columns = columns;
        for (const r of this.pool) r.el.remove();
        this.pool = [];
        this.rowOf.clear();
        this.whole = true;
        this.#styleColumns();
        this.#schedule();
    }

    /**
     * The row events heard (LiveGrid's OnRowClick and OnRowActivate given): a click's, its rows showing they can be
     * clicked; an activation's, its link columns drawn as links.
     */
    setRowEvents(clickable, activatable) {
        this.clickable = clickable;
        this.root.classList.toggle('tg-clickable', clickable);
        if (activatable !== this.activatable) {
            this.activatable = activatable;
            this.setColumns(this.columns);
        }
    }

    /** The keys selected (the grid's word, after a click's or a key's shown at once). */
    setSelection(keys) {
        this.selected = new Set(keys);
        this.#paint();
        this.#drawStatus();
    }

    /**
     * Back to the live top (LiveGrid.ToTopAsync, Home): the first row, the grid following the top again. Its rows not here
     * yet, the rows in view stay while the top's are asked for, up to `jumpHold` ms: come in that time, the grid goes
     * straight to them (#order); not, it goes then, their places drawn as placeholders till they come.
     */
    top() {
        this.velocity.reset();
        this.down = false;
        this.toTop = true;                 // (however many arrived above: scrolled to its first row, a held grid lets them in)
        if (this.jumpHold > 0 && !this.#topHeld()) {
            if (!this.jumpTimer) this.jumpTimer = setTimeout(() => this.#jump(), this.jumpHold);
            this.#want();
        } else this.#jump();
    }

    /** The jump to the top made: its rows here, or its hold over. */
    #jump() {
        clearTimeout(this.jumpTimer);
        this.jumpTimer = 0;
        this.scrollerEl.scrollTop = this.scroller.to(0);
        this.#want();
        this.#schedule();
    }

    /** A jump waiting for its rows given up: the reader moved (their scroll, wheel, drag or keys stand). */
    #stay() {
        if (!this.jumpTimer) return;
        clearTimeout(this.jumpTimer);
        this.jumpTimer = 0;
        this.toTop = false;
        this.#drawPill();
    }

    /**
     * Whether the rows the top shows are here (a jump there draws no placeholders): a held window's top isn't the live
     * top's once rows have arrived above it.
     */
    #topHeld() {
        const o = this.order, rows = Math.min(Math.ceil(this.scroller.viewRows), this.scroller.total);
        if (!o || !this.table || o.first > 0 || this.#beforeTop) return false;
        for (let at = 0; at < rows; at++) if (!this.table.rows.has(o.keys[at - o.first])) return false;
        return true;
    }

    /** Held, with rows arrived above it: the window's top is older rows, not the live top's. */
    get #beforeTop() {
        return this.order !== null && !this.order.live && (this.statusValue?.above ?? 0) > 0;
    }

    /** Brings a row into view (LiveGrid.RevealAsync): `position` placed `above` rows from the top, and focused. */
    reveal(position, above, key) {
        this.#stay();
        this.focusKey = key ?? null;
        this.focusAt = position;
        this.velocity.reset();
        this.toTop = false;
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
        clearTimeout(this.staleTimer);
        clearTimeout(this.jumpTimer);
        clearTimeout(this.scrollingTimer?.x);
        clearTimeout(this.scrollingTimer?.y);
        clearInterval(this.lagTimer);
        clearTimeout(this.passiveTimer);
        clearTimeout(this.answerTimer);
        this.sight?.disconnect();
        this.unlock?.();
        this.resizer?.disconnect();
        this.#tipHide(true);
        this.scrollerEl?.removeEventListener('scroll', this.onScroll);
        this.scrollerEl?.removeEventListener('pointermove', this.onPointer);
        this.scrollerEl?.removeEventListener('pointerleave', this.onPointerOut);
        this.scrollerEl?.removeEventListener('pointerdown', this.onPress);
        this.scrollerEl?.removeEventListener('wheel', this.onWheel);
        this.scrollerEl?.removeEventListener('focus', this.onFocus);
        this.keys?.removeEventListener('keydown', this.onKey);
        this.layerEl?.removeEventListener('click', this.onClick);
        this.layerEl?.removeEventListener('dblclick', this.onDouble);
        document.removeEventListener('visibilitychange', this.onVisibility);
        document.removeEventListener('copy', this.onCopy);
        if (this.host !== this.root) this.host.removeAttribute('aria-activedescendant');
        this.root.replaceChildren();
        this.options.statusLine?.replaceChildren();
    }

    // ── what arrives ──

    #state(group, info) {
        const [table, orderBytes, statusBytes] = group.members;
        if (table) this.table = table;
        this.stateSlowest = null;
        const changed = info?.changed, now = Date.now(), held = this.order;   // (the window before this state)
        const status = statusBytes instanceof Uint8Array && (!changed || changed[2] !== false), overscan = this.#overscan;
        const moved = this.statusValue?.moved;   // (as it said before this state)
        if (this.order && !this.order.live) this.heldAbove = this.statusValue?.above ?? 0;   // (held, as the last status said)
        if (status) this.statusValue = readStatus(statusBytes);
        if (status && this.waking && !this.wakeBlank && (this.wakeBlank = this.#turnedOver()) && this.staleShown)
            this.#stale(true);             // (the word it moved came late, behind what was on the way: its places after all)
        if (orderBytes instanceof Uint8Array && (!changed || changed[1] !== false)) this.#order(readOrder(orderBytes), now);
        else if (this.order?.byKey && changed?.[0]?.size) this.#order({...this.order}, now);   // (rows come or gone under an order by key: placed again)
        if (changed?.[0] instanceof Set) {
            const kept = this.changeStamp && held && held.view === this.order?.view ? new Set(held.keys) : null;
            for (const key of changed[0]) {
                this.changed.add(key);
                if (kept?.has(key) && !this.waking) {   // (not one come into the window, nor a catch-up)
                    this.updated.add(key);
                    this.#slowest(this.#changeLagOf(key, now));
                }
                this.changedAt.delete(key); // (to the end: the map kept in the order heard)
                this.changedAt.set(key, now);
            }
            if (this.stateSlowest !== null && !this.hidden && !this.waking) {   // (the state's slowest change: the flashes' shift)
                this.slowest.push(this.stateSlowest);
                if (this.slowest.length > LAG_SHIFT_STATES) this.slowest.shift();
            }
            if (this.changed.size > CHANGED_KEPT) {   // (not drawn a long while — hidden, kept live: drawn whole when it is)
                this.changed.clear();
                this.updated.clear();
                this.whole = true;
            }
        } else this.whole = true;          // (handed over whole: drawn whole)
        if (this.waking && moved && this.statusValue && !this.statusValue.moved)
            this.#woke();                  // (read since it woke — the word it moved unsaid with these rows: they're current)
        this.#forget(now);
        if (this.fresh !== 'fresh' && !info?.stale) {
            this.fresh = 'fresh';          // (a state from the router; quiet a second, Tap.Sync says whether there's more)
            this.#report();
        } else if (status) this.#drawStatus();
        // (The link carries more, or fewer — or the first rows are drawn, a view held either side from now: the window follows.)
        if (this.#overscan !== overscan) this.#want();
        if (this.jumpTimer && this.#topHeld()) this.#jump();   // (a jump waiting for the top's rows: they're here — made, no placeholders)
        this.#drawPill();
        this.#quiet();
        this.#schedule();
    }

    /** The new-above pill (newAbove): held, rows arrived above — how many; else, or a jump to the top under way, hidden. */
    #drawPill() {
        const pill = this.pillEl;
        if (!pill) return;
        const above = !this.order || this.order.live || this.hidden || this.jumpTimer || this.toTop ? 0 : this.statusValue?.above ?? 0;
        if (above > 0) {
            const text = `${above > 999 ? '999+' : group(String(above))} new above`;
            if (pill.textContent !== text) pill.textContent = text;
        }
        if (pill.hidden !== !(above > 0)) pill.hidden = !(above > 0);
    }

    #order(order, now) {
        if (!order || order.view < this.view) return;   // (of a view replaced)
        // Asked to stay below the live top, a live order still arriving was sent before the hold: its positions have
        // moved on from the rows in view (a slow link's seconds of arrivals), which stay as drawn until the hold comes —
        // set on the row at the top of the view, so they stand where they are.
        if (this.holding && order.live && this.order?.live && order.view === this.order.view) return;
        if (!order.live) (this.holding = false, this.kept = null);
        if (order.byKey) order.keys = keysByKey(this.table?.rows.keys() ?? [], this.options.keyOrder);   // (the rows held, by key)
        if (order.view > this.view) this.#asked(order.view);   // (the new view's, ahead of the word of it: taken as that)
        if (this.order && this.order.view < order.view) {
            if (order.stale) {                           // (refused before any rows of its own: the last view's stay, greyed)
                this.refused = order.view;
                this.#showStale();
                return;
            }
            this.#newView();                             // (its first rows: the last view's go)
        }
        this.refused = -1;
        const was = this.order;
        if (order.total !== this.scroller.total) {
            this.scroller.setTotal(order.total);
            this.surfaceEl.style.height = `${this.scroller.height}px`;
        }
        if (order.total + this.headerRows !== this.rowCount)
            this.host.setAttribute('aria-rowcount', String(this.rowCount = order.total + this.headerRows));
        // Rows that arrived while it held are new to the reader once live again (the pill, Home, racing up), and flash from
        // now, as the page hears of them, not their stamps: a fresh start. Let in as the reader scrolls up into them, they're
        // rows like any scrolled to: flashing from their stamps, as at the live top, if that's under way — an old one not.
        if (was && was.live && !order.live) this.heldFrom = this.#newestStamp(was.keys);
        else if (was && !was.live && order.live) this.#reveal(was, order, now);
        if (order.live) this.heldFrom = null;
        // Live again while away from the top (raced up, few arrived above): the page goes to the top with it — but for a
        // jump there waiting for its rows (top), which goes once they're here.
        if (was && !was.live && order.live && this.scroller.row > 0 && !this.jumpTimer) {
            this.velocity.reset();
            this.scrollerEl.scrollTop = this.scroller.to(0);
        }
        // Rows let in above (in this hold): the page moves its scroll down by as many, the rows in view staying put, and the
        // reader scrolls on into them.
        if (was && order.holds === was.holds && order.letIn > was.letIn) {
            this.scrollerEl.scrollTop = this.scroller.by(order.letIn - was.letIn);
            this.velocity.shift(order.letIn - was.letIn);
        }
        // Away from the top, the rows in view stay put as the window's positions move under them — rows arriving above a
        // live window push its rows down; a hold is set on the row the page asked from, which may stand elsewhere by then:
        // the row at the top of the view, by key, where it stands now.
        else if (was && was.view === order.view && !(order.live && !was.live) && !this.toTop && !this.jumpTimer && this.#offTop) {
            const at = Math.floor(this.scroller.row), key = was.keys[at - was.first], now = key === undefined ? -1 : order.keys.indexOf(key);
            if (now >= 0 && order.first + now !== at) {
                const shift = order.first + now - at;
                this.scrollerEl.scrollTop = this.scroller.by(shift);
                this.velocity.shift(shift);
            }
        }
        // The live top's arrival rate, from its totals over the last ten seconds: how far it moves while out of sight.
        if (order.live) {
            this.totals.push({at: now, total: order.total});
            while (now - this.totals[0].at > RATE_SPAN_MS) this.totals.shift();
            const [first, last] = [this.totals[0], this.totals[this.totals.length - 1]];
            if (last.at - first.at >= 1_000) this.rate = Math.max(0, (last.total - first.total) * 1_000 / (last.at - first.at));
        } else this.totals = [];
        // Rows arrived at the live top, above the row that was first (arrivedAbove: every row, by key, if more arrived than
        // the window holds): they flash once drawn, from when they arrived, and their lag counts — not a catch-up's.
        if (was && order.live && was.live && was.keys.length) {
            const before = new Set(was.keys), top = arrivedAbove(was.keys, order.keys, order.byKey, this.options.keyOrder);
            let least = null;
            for (let i = 0; i < top; i++) {
                const key = order.keys[i];
                if (before.has(key)) continue;
                this.arrived.set(key, now);
                if (this.waking) continue;
                this.updated.add(key);
                const lag = this.stamp || this.changeStamp ? this.#lagOf(key, now) : null;
                if (lag !== null && (least === null || lag < least)) least = lag;
                this.#slowest(lag);
            }
            if (least !== null && !this.hidden) this.#checkStamps(least);   // (hidden, its states wait their interval)
        }
        // The focused row, by key: where it stands now.
        if (this.focusKey !== null) {
            const at = order.keys.indexOf(this.focusKey);
            if (at >= 0) this.focusAt = order.first + at;
        }
        if (this.seen.size > 2 * order.keys.length + 64) {   // (now and then, the rows gone from the window forgotten)
            const kept = new Set(order.keys);
            for (const key of this.seen.keys()) if (!kept.has(key)) this.seen.delete(key);
        }
        this.order = order;
        this.answered = true;
        this.#showStale();
        this.#report();
    }

    /** Whether the rows drawn are of a view since replaced, the new one's top asked for and not here yet. */
    get #awaiting() {
        return this.order !== null && this.order.view < this.view;
    }

    /**
     * The host's data-stale: a read refused, at once; the rows drawn a replaced view's, or a grid's back in sight, once
     * the answer that makes them current is late — later than twice the page's round trip, or 500 ms (staleAfter) — till
     * it comes.
     */
    #showStale() {
        const refused = this.order !== null && (this.order.stale || this.refused === this.view);
        const late = () => this.#awaiting || this.waking;
        clearTimeout(this.staleTimer);
        this.staleTimer = 0;
        if (refused || !late()) this.#stale(refused);
        else if (this.blankShown && !this.waking) this.#stale(true);   // (woken, its places drawn: greyed rows for the view pending)
        else if (!this.staleShown && !this.blankShown)
            this.staleTimer = setTimeout(() => {
                this.staleTimer = 0;
                this.#stale(late());
            }, staleAfter(this.took));
    }

    /** Stale, greyed — or, back in sight with more arrived at the live top than it holds, its places drawn instead. */
    #stale(stale) {
        const blank = stale && this.waking && this.wakeBlank;
        if (blank !== this.blankShown) {
            this.blankShown = blank;
            this.#schedule();
        }
        stale &&= !blank;
        if (stale === this.staleShown) return;
        this.staleShown = stale;
        if (stale) this.host.setAttribute('data-stale', '');
        else this.host.removeAttribute('data-stale');
        this.#report();
    }

    /** A live arrival's lag (ms): heard less stamped — null, the row unstamped. */
    #lagOf(key, now) {
        const row = this.table?.rows.get(key)?.row;
        const at = row ? stampOf(this.stamp ?? this.changeStamp, row, new DataView(row.buffer, row.byteOffset, row.byteLength), key) : null;
        return at === null ? null : now - at;
    }

    /**
     * A state's live arrivals against the page's clock: the least of their lags kept — one a state, so a whole window
     * arriving at once can't crowd out the states before it — and the stamps trusted as the last few say (stampsTrusted).
     */
    #checkStamps(least) {
        this.lags.push(least);
        if (this.lags.length > STAMP_LAGS_KEPT) this.lags.shift();
        const trips = [...this.trips].sort((a, b) => a - b);
        this.stamped = stampsTrusted(this.lags, trips.length > 0 ? trips[trips.length >> 1] : 0);
    }

    /** A round trip taken (ms): an answer's, or Tap.Sync's. */
    #tripped(ms) {
        this.trips.push(ms);
        if (this.trips.length > TRIPS_KEPT) this.trips.shift();
    }

    /**
     * When a row arrived, to run its arrival flash from: as when last drawn, its flash under way; else its stamp, as late
     * as the link usually is (`shift`: flashFrom), else — no stamps, none trusted, or no delay measured yet — when the
     * page heard of it arriving.
     */
    #arrivedAt(key, row, shift) {
        const flashing = this.flashing.get(key);   // (under way when last drawn: it goes on)
        if (flashing !== undefined && !this.arrived.has(key)) return flashing;
        if (shift === null || !this.stamp || this.revealed.has(key)) return this.arrived.get(key);
        const at = stampOf(this.stamp, row, new DataView(row.buffer, row.byteOffset, row.byteLength), key);
        return at === null ? this.arrived.get(key) : flashFrom(this.arrived.get(key) ?? this.changedAt.get(key), at, shift);
    }

    /** The newest arrival stamp of the rows `keys` (ms since 1970), as the page holds them: null, none stamped. */
    #newestStamp(keys) {
        if (!this.stamp) return null;
        let newest = null;
        for (const key of keys) {
            const row = this.table?.rows.get(key)?.row;
            const at = this.stamp.unit === 'uuid7' ? keyTime(key)
                : row ? stampOf(this.stamp, row, new DataView(row.buffer, row.byteOffset, row.byteLength), key) : null;
            if (at !== null && (newest === null || at > newest)) newest = at;
        }
        return newest;
    }

    /**
     * Rows a held window's going live again reveals (`order`, after `was`), that arrived while it held: heard of now,
     * flashing from now. Stamped, those stamped after the newest the page showed live (heldFrom) — in any order; else
     * the live top's first rows, as many as arrived above (heldAbove).
     */
    #reveal(was, order, now) {
        const before = new Set(was.keys), reveal = key => (this.arrived.set(key, now), this.revealed.add(key));
        if (this.heldFrom !== null) {
            for (const key of order.keys) {
                if (before.has(key)) continue;
                const at = this.stamp.unit === 'uuid7' ? keyTime(key) : this.#arrivalOf(key);
                if (at !== null && at > this.heldFrom) reveal(key);
            }
        } else for (const key of order.keys.slice(0, Math.min(this.heldAbove, order.keys.length))) reveal(key);
    }

    /** A row's arrival stamp (ms since 1970), by its own field: null, none. */
    #arrivalOf(key) {
        const row = this.table?.rows.get(key)?.row;
        return row ? stampOf(this.stamp, row, new DataView(row.buffer, row.byteOffset, row.byteLength), key) : null;
    }

    /** How late the link usually makes the page hear of a change (lagShift), while the stamps are trusted: null otherwise. */
    get #shift() {
        return this.stamped ? lagShift(this.slowest) : null;
    }

    /** A live change's lag, to the state's slowest. */
    #slowest(lag) {
        if (lag !== null && (this.stateSlowest === null || lag > this.stateSlowest)) this.stateSlowest = lag;
    }

    /** A row's change's lag (ms): heard less its change stamp — null, the row gone or unstamped. */
    #changeLagOf(key, now) {
        const row = this.table?.rows.get(key)?.row;
        if (!row || !this.changeStamp) return null;
        const at = stampOf(this.changeStamp, row, new DataView(row.buffer, row.byteOffset, row.byteLength));
        return at === null ? null : now - at;
    }

    /**
     * How long ago a row's last change happened, for its cells' flash: by its own stamp (trusted), as late as the link
     * usually is (`shift`: flashFrom), else since the page heard of it. A change the page saw happen, drawn as it came
     * (`live`: to a row its window held, or arriving at the live top), counts toward the page's lag, as it is; not a row
     * come into the window — scrolled to, or let in — nor one drawn only once scrolled to, whose stamp says when it last
     * changed, not how late it came.
     */
    #changedFor(key, row, now, live, shift) {
        const heard = this.changedAt.get(key) ?? now;
        if (this.changeStamp && this.stamped) {
            const at = stampOf(this.changeStamp, row, new DataView(row.buffer, row.byteOffset, row.byteLength));
            if (at !== null) {
                if (live && now >= at) this.lagWorst = Math.max(this.lagWorst, now - at);
                return now - flashFrom(heard, at, shift);
            }
        }
        return now - heard;
    }

    /** Forgets when changes were heard of, once older than any flash. */
    #forget(now) {
        for (const heard of [this.arrived, this.changedAt, this.flashing])
            for (const [key, at] of heard) {
                if (now - at < HEARD_KEPT_MS) break;
                heard.delete(key);
            }
    }

    /** The `view`th view asked in: the requests of the last given up (the server drops them unanswered). */
    #asked(view) {
        this.view = view;
        for (const id of this.requests.reset()) this.#settle(id);
        this.velocity.reset();
    }

    /** A new view's first rows: those drawn go, the grid at its top. */
    #newView() {
        this.order = null;
        this.kept = null;
        this.whole = true;
        this.arrived.clear();
        this.changedAt.clear();
        this.revealed.clear();
        this.flashing.clear();
        this.heldFrom = null;
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
        const awaiting = this.#awaiting, o = awaiting ? null : this.order;   // (a new view's top: the last's rows still drawn)
        const now = performance.now(), rate = this.velocity.rate(now);
        const lead = awaiting ? 0 : leadFor(this.velocity.reach, rate, this.took,
            this.requests.minInterval);   // (enough ahead to cover an answer's trip at the reader's pace)
        // (A new view's top, or a jump's waiting for its rows; scrolled into the first row — off the top by any part of a row —
        // asked from the second, so a live window holds there and the rows stay put: only at the very top does it tail.)
        const row = this.scroller.row, from = awaiting || this.jumpTimer ? 0 : row > 0 && row < 1 && this.#offTop ? 1 : row;
        const window = windowFor(from, this.scroller.viewRows, awaiting || this.down, this.prefetch, lead, this.options.maxRows ?? Infinity,
            this.#overscan, Math.min(this.scroller.viewRows, this.#told));
        const racing = !awaiting && racingUp(rate, this.velocity.way * this.velocity.peak, this.scroller.viewRows);
        // From a live window, away from its top: the row shown at the view's first position, by key — a hold keeps it there,
        // however many rows arrived since this page drew it (the anchor).
        const anchor = o?.live && window.first > 0 ? this.#keyAt(window.first) ?? null : null;
        if (o?.live) {                     // (asked to stay down there: held, once it's here)
            const holding = window.first > 0 && !this.toTop;
            if (holding && !this.holding) this.kept = this.#keep(o.keys);
            else if (!holding) this.kept = null;
            this.holding = holding;
        }
        this.requests.want({...window, holds: o?.holds ?? 0, letIn: o?.letIn ?? 0, view: this.view, racing, top: this.toTop, anchor});
        this.requests.pump();
    }

    /**
     * The rows of `keys` the table has now, by key: their entries, which the table drops but never reuses — so they stay
     * as they last were (a slow link's live states, sent before the hold, drop the window's rows the reader is scrolling
     * into: kept, they're drawn till the hold comes).
     */
    #keep(keys) {
        const kept = new Map(), rows = this.table?.rows;
        if (rows) for (const key of keys) {
            const entry = rows.get(key);
            if (entry) kept.set(key, entry);
        }
        return kept;
    }

    /**
     * The rows either side of the view the session says the link carries in time: none until its status says (the first
     * window's fill as small as it can be on a slow link); no bound (Infinity) if it says nothing — none told, or a watch
     * with no status to say it.
     */
    get #told() {
        if (this.statusless) return Infinity;
        const status = this.statusValue;
        return status ? status.overscan ?? Infinity : 0;
    }

    /**
     * Rows held either side of the view: the grid's count (overscan), or as many as the link carries in time, if fewer —
     * but, once the first rows are drawn (as soon as they can be: none held round them), never fewer than a view.
     */
    get #overscan() {
        const told = this.#told;
        return Math.min(this.options.overscan ?? OVERSCAN, this.answered ? Math.max(told, Math.ceil(this.scroller.viewRows)) : told);
    }

    /** Scrolled off the very top: by half a pixel or more (a position's rounding isn't a reader's move). */
    get #offTop() {
        return this.scroller.row * this.rowHeight >= 0.5;
    }

    #ask(request) {
        const wait = new AbortController();
        this.waits.set(request.id, wait);
        this.tapline.rendezvous(request.id, {tapId: this.tapId, signal: wait.signal}).then(() => {
            this.waits.delete(request.id);
            this.answeredAt = performance.now();
            this.answerAfter = ANSWER_LATE_MS;
            if (this.waking && request.at >= this.wokeAt) this.#woke();   // (asked since it woke: its rows are current)
            const answered = this.requests.answered(request.id);
            if (!answered) return;
            this.took.push(answered.took);
            if (this.took.length > ANSWERS_KEPT) this.took.shift();
            this.#tripped(answered.took);
            for (const id of answered.superseded) this.#settle(id);   // (never to be answered: the server read a later one)
            if (this.prefetch.observe(answered.took)) this.#want();
            else this.requests.pump();                              // (what was wanted meanwhile)
        }, () => this.waits.delete(request.id));
        this.options.request(request);
        this.#watchAnswers();
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
        this.host.style.setProperty('--tg-scrollbar-x', `${this.scrollerEl.offsetHeight - height}px`);   // (sideways: the frozen divider stops above it)
        this.host.style.setProperty('--tg-view-width', `${this.scrollerEl.clientWidth}px`);   // (a placeholder's bar no wider)
        if (height === this.scroller.viewHeight) return;
        this.scroller.resize(height);
        this.prefetch.see(this.scroller.viewRows);
        this.layerEl.style.height = `${height}px`;
        this.#want();
        this.#schedule();
    }

    #scrolled() {
        this.#tipHide();                   // (what's under the pointer moved: shown again once it rests)
        const left = this.scrollerEl.scrollLeft;
        if (left !== this.scrollLeft) {    // (sideways: the headings follow, and placeholders' bars stay in view)
            if ((left > 0) !== (this.scrollLeft > 0)) this.host.toggleAttribute('data-scrolled-x', left > 0);   // (the frozen columns' fade)
            this.scrollLeft = left;
            this.host.style.setProperty('--tg-scroll-x', `${left}px`);
            this.#scrolling('x');
            if (this.head) this.head.scrollLeft = left;
            else this.host.style.setProperty('--tg-scroll-left', `${left}px`);
        }
        const was = this.scroller.row;
        const row = this.scroller.scrolled(this.scrollerEl.scrollTop);
        if ((row > 0) !== this.scrolledShown) {        // (for the chrome: a fade over the top edge, say)
            this.scrolledShown = row > 0;
            if (this.scrolledShown) this.host.setAttribute('data-scrolled', '');
            else this.host.removeAttribute('data-scrolled');
        }
        if (row === was) return;
        this.#scrolling('y');
        this.down = row > was;
        this.toTop = false;
        this.velocity.sample(row, performance.now());
        this.#want();
        this.#schedule();
    }

    /**
     * The rows scrolling a way ('x', 'y'): the host's data-scrolling-x (-y) while they do and a second after
     * (SCROLLING_MS) — the stylesheet shows the scrollbar that way then, as the platform's overlaid ones show.
     */
    #scrolling(axis) {
        this.scrollingAt[axis] = performance.now();
        if (this.scrollingTimer[axis]) return;
        this.host.setAttribute(`data-scrolling-${axis}`, '');
        const still = () => {
            const left = SCROLLING_MS - (performance.now() - this.scrollingAt[axis]);
            if (left > 0) this.scrollingTimer[axis] = setTimeout(still, left);
            else (this.scrollingTimer[axis] = 0, this.host.removeAttribute(`data-scrolling-${axis}`));
        };
        this.scrollingTimer[axis] = setTimeout(still, SCROLLING_MS);
    }

    #wheel(e) {
        this.#stay();
        if (!this.scroller.scaled || e.ctrlKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;   // (native, a zoom, sideways)
        e.preventDefault();
        const px = e.deltaMode === 1 ? e.deltaY * this.rowHeight : e.deltaMode === 2 ? e.deltaY * this.scroller.viewHeight : e.deltaY;
        this.#move(px / this.rowHeight);
    }

    /**
     * A wheel over what stands over the rows, outside their scroller (the new-above pill): the rows scrolled as if it
     * were over them — not the page, nor nothing.
     */
    #wheelOver(e) {
        if (e.ctrlKey) return;             // (a zoom)
        if (this.scroller.scaled && Math.abs(e.deltaX) <= Math.abs(e.deltaY)) return this.#wheel(e);   // (by rows)
        this.#stay();
        e.preventDefault();
        const px = (delta, page) => e.deltaMode === 1 ? delta * this.rowHeight : e.deltaMode === 2 ? delta * page : delta;
        this.scrollerEl.scrollLeft += px(e.deltaX, this.scrollerEl.clientWidth);
        if (!this.scroller.scaled) this.scrollerEl.scrollTop += px(e.deltaY, this.scroller.viewHeight);
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
                if (this.activatable) this.options.rowActivate?.({key: this.focusKey});
                return;
            }
            if (this.selection === 'none') return;
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
        if (rows === -Infinity) return this.top();   // (Home: the live top, as the pill's)
        this.#stay();
        this.toTop = false;
        if (at < top) this.scrollerEl.scrollTop = this.scroller.to(at);
        else if (at + 1 > top + view) this.scrollerEl.scrollTop = this.scroller.to(Math.max(0, at + 1 - view));
        this.down = rows > 0;
        this.velocity.sample(this.scroller.row, performance.now());
        this.#want();
        this.#schedule();
    }

    #move(rows) {
        this.#stay();
        this.down = rows > 0;
        this.toTop = false;
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
        if (column?.link && this.activatable) this.options.rowActivate?.({key: r.key, column: column.name});
        else {
            this.#select(r.key, this.selection === 'multiple' && (e.ctrlKey || e.metaKey));
            if (this.clickable || this.selection !== 'none')   // (heard: OnRowClick, or the selection — else nothing sent)
                this.options.rowClick?.({key: r.key, column: column?.name, ctrl: e.ctrlKey, shift: e.shiftKey, meta: e.metaKey, alt: e.altKey});
        }
        this.#paint();
    }

    #double(e) {
        const r = this.#answered(e.target), column = r && this.#columnOf(r, e.target);
        if (r && this.activatable && !column?.link) this.options.rowActivate?.({key: r.key, column: column?.name});
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
        this.#drawStatus();
    }

    #keyAt(position) {
        const o = this.order, i = o ? position - o.first : -1;
        return o && i >= 0 && i < o.keys.length ? o.keys[i] : undefined;
    }

    #visibility() {
        this.docHidden = document.visibilityState === 'hidden';
        this.#seen();
    }

    // ── in sight: the page shown and the grid within a view of the viewport; out of it a while, passive ──

    /** In sight, or out of it: passive once out a while (unless kept live: options.passive false), back at once. */
    #seen() {
        const out = this.docHidden || !this.inView;
        clearTimeout(this.passiveTimer);
        this.passiveTimer = 0;
        if (!out) {
            if (this.hidden) this.#wake();
            else this.#schedule();
        } else if (!this.hidden && this.options.passive !== false)
            this.passiveTimer = setTimeout(() => this.#passivate(), PASSIVE_AFTER_MS);
    }

    /**
     * Whether more arrived at the live top while it was passive than the window holds: the source said it moved, and at
     * the rate rows arrived before, that many would have.
     */
    #turnedOver() {
        return !!(this.statusValue?.moved && this.order?.live &&
            this.rate * (this.wokeAt - this.passiveAt) / 1_000 >= this.scroller.viewRows);
    }

    /**
     * Its rows current again, back in sight: the answer to a request asked since it woke — or, sooner, the state read since
     * it woke, which unsays the word that the source moved.
     */
    #woke() {
        this.waking = false;
        this.fresh = 'fresh';
        this.#showStale();
        this.#report();
    }

    /** Out of sight a while: the session told, so it reads none of the source's changes, and nothing asked, till it's back. */
    #passivate() {
        this.passiveTimer = 0;
        if (this.hidden || this.disposed) return;
        this.hidden = true;
        this.passiveAt = performance.now();
        this.#tipHide(true);
        this.options.visible?.(false);
        this.#want();
    }

    /**
     * Back in sight: the session told, and the window asked again — its answer the word that the rows are current. Till
     * then, past the stale delay, they're greyed; or, if more arrived at the live top meanwhile than the window holds (the
     * source said it moved, and at the rate rows arrived before that many would have), its places are drawn instead.
     */
    #wake() {
        this.hidden = false;
        this.waking = true;
        this.wokeAt = performance.now();
        this.wakeBlank = this.#turnedOver();
        this.fresh = 'checking';
        this.options.visible?.(true);
        this.velocity.reset();
        this.requests.again();
        this.#want();
        this.#showStale();
        this.#report();
        this.#schedule();
    }

    /**
     * Requests unanswered a while (8 s, or four round trips if longer) — lost with a connection, say, or dropped by the
     * session as another view's — given up: the session told the grid's in sight (that word lost too, maybe), and the
     * window asked again; then after twice as long, up to a minute. An answer resets it. Armed as a request goes.
     */
    #watchAnswers() {
        if (this.answerTimer || this.disposed) return;
        const trips = [...this.trips].sort((a, b) => a - b), trip = trips.length > 0 ? trips[trips.length >> 1] : 0;
        const after = Math.max(this.answerAfter, 4 * trip);
        this.answerTimer = setTimeout(() => {
            this.answerTimer = 0;
            if (this.disposed || this.hidden || this.requests.outstanding.length === 0) return;
            if (performance.now() - Math.max(this.answeredAt, this.requests.outstanding[0].at) < after) {
                this.#watchAnswers();      // (answered lately, or asked lately: on their way still)
                return;
            }
            this.options.visible?.(true);
            for (const id of this.requests.reset()) this.#settle(id);
            this.#want();
            this.answerAfter = Math.min(60_000, this.answerAfter * 2);
            this.#watchAnswers();
        }, after);
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
        }, QUIET_MS), asked = performance.now();
        this.tapline.sync(this.tapId).then(() => {
            clearTimeout(late);
            this.#tripped(performance.now() - asked);
            this.fresh = 'fresh';
            this.#report();
        }, () => clearTimeout(late));
    }

    #report() {
        const o = this.order;
        this.options.status?.({
            total: o?.total ?? 0, totalKind: o?.totalKind ?? 0, live: o?.live ?? true, stale: this.staleShown,
            holds: o?.holds ?? 0, letIn: o?.letIn ?? 0, fresh: this.fresh, blanks: this.blanks
        });
        this.#drawStatus();
    }

    /** The status line (statusLine), drawn again only where its text has moved. */
    #drawStatus() {
        if (this.fresh !== 'checking') this.freshSaid = this.fresh;
        const line = this.options.statusLine, o = this.order, s = this.statusValue;
        if (!line || !o || this.disposed) return;
        const parts = statusParts({total: o.total, totalKind: o.totalKind, live: o.live, above: s?.above ?? 0, refusal: s?.refusal},
            this.freshSaid, this.selection === 'none' ? 0 : this.selected.size, [this.options.rowName ?? 'row', this.options.rowsName ?? 'rows']);
        const text = parts.map(p => p[0]).join(' · ');
        if (text === this.statusShown) return;
        this.statusShown = text;
        const nodes = [];
        for (const [part, className] of parts) {
            if (nodes.length) nodes.push(' · ');
            nodes.push(className ? Object.assign(document.createElement('span'), {className, textContent: part}) : part);
        }
        line.replaceChildren(...nodes);
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
        const changed = this.changed, updated = this.updated, whole = this.whole, now = Date.now(), shift = this.#shift;
        // (Jumped to the live top ahead of its rows — the hold over — and still held: its places, not the held rows there.)
        const places = this.blankShown || (this.toTop && !this.jumpTimer && this.#beforeTop);
        this.changed = new Set();
        this.updated = new Set();
        this.whole = false;
        while (this.pool.length < rows) this.pool.push(this.#rowElement());
        if (order && table && order.keys.length && this.columns.some(c => c.auto && !this.widths.has(c.name)))
            this.#measure(order, table);
        let blank = false, active = null;
        const total = this.answered ? this.scroller.total : Math.ceil(this.scroller.viewRows);   // (none answered: the view's places)
        for (let i = 0; i < this.pool.length; i++) {
            const r = this.pool[i], position = first + i;
            if (i >= rows || position >= total) {
                if (!r.el.hidden) r.el.hidden = true;
                continue;
            }
            if (r.el.hidden) r.el.hidden = false;
            const y = offset + i * this.rowHeight;
            if (r.y !== y) r.el.style.transform = `translateY(${(r.y = y)}px)`;
            if (r.position !== position) {     // (tg-alt: every other row, by position, for striping)
                r.el.setAttribute('aria-rowindex', String((r.position = position) + 1 + this.headerRows));
                r.el.classList.toggle('tg-alt', (position & 1) === 1);
            }
            const at = order ? position - order.first : -1;
            const key = order && at >= 0 && at < order.keys.length ? order.keys[at] : undefined;
            // (Dropped by a live state sent before a hold asked for: as it was, kept — or as drawn — till the hold comes.)
            const entry = key !== undefined && !places ? table?.rows.get(key) ?? this.kept?.get(key) : undefined;
            if (!entry && this.holding && key !== undefined && r.key === key) continue;
            if (!entry) {
                if (r.key !== null) this.#placeholder(r);
                if (position < top + this.scroller.viewRows) blank = true;
            } else if (whole || r.key !== key || r.bytes !== entry.row || changed.has(key)) {
                // Changed, its cells that differ flash; arrived, or (stamped) drawn here newly, the row does — each from
                // when it happened (scrolled into view unstamped: no flash).
                const drawn = r.key !== key;
                this.#fill(r, key, entry.row,
                    changed.has(key) ? this.#changedFor(key, entry.row, now, updated.has(key), shift) : undefined, now);
                const arrived = drawn || this.arrived.has(key) ? this.#arrivedAt(key, entry.row, shift) : undefined;
                this.arrived.delete(key);
                this.revealed.delete(key);
                if (arrived !== undefined && now - arrived < this.#duration('tg-arrived')) this.flashing.set(key, arrived);
                if (arrived !== undefined || drawn) this.#flash(r.el, 'tg-arrived', now - (arrived ?? -Infinity));
            }
            if (this.focusKey === null && this.focusAt === position && entry) this.focusKey = key;   // (moved to, now answered)
            if (this.#mark(r)) active = r.el.id;
        }
        this.#activate(active);
        if (blank && this.answered && !this.blankShown) this.blanks++;
        if (this.tipShown) this.#tipUpdate();
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
        el.style.setProperty('--tg-row-slot', String(this.pool.length));   // (its place in the view: its placeholder's sheen leans by it)
        // (Beside frozen columns, the others in a span of their own: scrolled under the frozen, they fade as one, the
        // row's background and flash whole behind them.)
        const rest = this.columns.some(c => c.frozen) ? document.createElement('span') : el;
        const cells = this.columns.map((c, i) => {
            const cell = document.createElement('span');
            cell.className = cellClass(c, this.rtl, this.activatable);
            cell.style.width = i === 0 ? 'var(--tg-x1)' : `calc(var(--tg-x${i + 1}) - var(--tg-x${i}))`;
            if (c.frozen) cell.style.left = i === 0 ? '0' : `var(--tg-x${i})`;
            cell.setAttribute('role', 'gridcell');
            (c.frozen ? el : rest).append(cell);
            return cell;
        });
        if (rest !== el) {
            rest.className = 'tg-unfrozen';
            rest.setAttribute('role', 'none');
            el.append(rest);
        }

        this.layerEl.append(el);
        const r = {el, cells, key: undefined, bytes: null, position: -1, y: NaN, selected: false, focused: false,
            tips: new Array(cells.length).fill(null)};   // (its cells' tooltips: formatCell's titles)
        this.rowOf.set(el, r);
        return r;
    }

    /**
     * A copy (⌘C, Ctrl+C) from the grid: the values whole — a number's every digit and a time's instant, as its tooltip
     * has them, where its cell may show them rounded. A selection within one cell: that cell's. None, the grid focused:
     * the rows selected, or else the focused row, tab-separated, a line each, in the view's order. Any other selection:
     * the browser's.
     */
    #copy(e) {
        const selection = getSelection();
        let text;
        if (selection && !selection.isCollapsed) {
            const cellOf = node => (node?.nodeType === 1 ? node : node?.parentElement)?.closest?.('.tg-cell') ?? null;
            const cell = cellOf(selection.anchorNode), r = cell && this.rowOf.get(cell.closest('.tg-row'));
            if (!r || r.key == null || cell !== cellOf(selection.focusNode)) return;
            text = this.#whole(r.key, r.cells.indexOf(cell));
        } else {
            if (!this.host.contains(document.activeElement) || !this.order) return;
            const keys = this.order.keys.filter(key => this.selected.has(key) && this.table?.rows.has(key));
            if (!keys.length && this.focusKey !== null && this.table?.rows.has(this.focusKey)) keys.push(this.focusKey);
            if (!keys.length) return;
            text = keys.map(key => this.columns.map((_, i) => this.#whole(key, i)).join('\t')).join('\n');
        }
        e.clipboardData.setData('text/plain', text);
        e.preventDefault();
    }

    /** A cell's value whole, by its row's key and its column: a number's and a time's as its tooltip has it; the rest as shown. */
    #whole(key, i) {
        const entry = this.table?.rows.get(key) ?? this.kept?.get(key), c = this.columns[i];
        if (!entry || !c) return '';
        const row = entry.row, {text, title} = formatCell(c, row, new DataView(row.buffer, row.byteOffset, row.byteLength), this.columns, this.culture);
        return c.kind === 'fixed' || c.kind === 'float' || c.kind === 'time' || c.figures ? title ?? text : text;
    }

    // ── a cell's tooltip: the grid's own, so it shows what the cell under the pointer holds now — rows move and change
    //    under a pointer at rest, and a browser's title tooltip, once shown, keeps what it said ──

    #pointer(e) {
        if (e.pointerType === 'touch') return;
        this.tipAt = {x: e.clientX, y: e.clientY};
        const cell = e.target.closest?.('.tg-cell') ?? null;
        if (cell === this.tipCell) return;             // (moving within the cell: shown, or due, as it was)
        this.#tipHide();
        this.tipCell = cell;
        if (cell) this.tipTimer = setTimeout(() => {
            this.tipTimer = 0;
            this.#tipShow();
        }, TIP_DELAY_MS);
    }

    /**
     * What the cell under the pointer says on hover now: its row's (formatCell's title), or, its text cut by its column,
     * that text whole; none over a placeholder.
     */
    #tipText() {
        const r = this.rowOf.get(this.tipCell?.closest('.tg-row')), cell = this.tipCell;
        if (!r || r.key == null) return null;
        const title = r.tips[r.cells.indexOf(cell)];
        return title ?? (cell.scrollWidth > cell.clientWidth ? cell.textContent.replace(/[\u2066\u2069]/g, '') : null);
    }

    #tipShow() {
        const text = this.#tipText(), tip = this.tipEl;
        if (!text || this.disposed) return;
        tip.textContent = text;
        if (tip.popover) tip.showPopover(); else tip.hidden = false;
        this.tipShown = true;
        const {x, y} = this.tipAt, width = tip.offsetWidth, height = tip.offsetHeight;   // (below the pointer, in the viewport)
        tip.style.left = `${Math.max(0, Math.min(x + 12, innerWidth - width - 4))}px`;
        tip.style.top = `${y + 20 + height <= innerHeight ? y + 20 : Math.max(0, y - height - 8)}px`;
    }

    /** Drawn again: the tooltip says what its cell holds now — another row's, as rows move — or goes. */
    #tipUpdate() {
        const text = this.#tipText();
        if (!text) this.#tipHide();
        else if (this.tipEl.textContent !== text) this.tipEl.textContent = text;
    }

    /** The tooltip hidden, or one due given up (`gone`: the pointer left — till it's back on a cell, none due). */
    #tipHide(gone = false) {
        clearTimeout(this.tipTimer);
        this.tipTimer = 0;
        if (gone) this.tipCell = null;
        if (!this.tipShown) return;
        this.tipShown = false;
        if (this.tipEl.popover) this.tipEl.hidePopover(); else this.tipEl.hidden = true;
    }

    #placeholder(r) {
        r.key = null;
        r.bytes = null;
        const period = this.#shimmer;      // (its bar's shimmer in step with every other's: started where the clock has it)
        if (period > 0) r.el.style.setProperty('--tg-shimmer-delay', `${-((performance.now() - this.shimmerFrom) % period)}ms`);
        r.el.classList.add('tg-placeholder');
        r.el.classList.remove('tg-arrived-a', 'tg-arrived-b');
        r.cells.forEach((cell, i) => {     // (nothing of the row it showed: its text, tooltip or value)
            cell.textContent = '';
            cell.classList.remove('tg-changed-a', 'tg-changed-b');
            if (r.tips[i] !== null) (r.tips[i] = null, cell.removeAttribute('aria-description'));
            if (cell.dataset.value !== undefined) delete cell.dataset.value;
        });
    }

    /** Writes a row's cells; changed in place since last drawn, the cells that differ flash. */
    /**
     * Writes a row's cells. Changed `changedFor` ms ago (undefined: not since last drawn), the cells that differ from
     * the row as last drawn flash from then — compared by key, wherever it was drawn, as rows move when others arrive.
     * Drawn by another element than before, its cells' flashes go on from where they've got to.
     */
    #fill(r, key, row, changedFor, now) {
        const dv = new DataView(row.buffer, row.byteOffset, row.byteLength);
        r.el.classList.remove('tg-placeholder');
        const seen = this.seen.get(key), n = this.columns.length;
        const texts = new Array(n), at = seen?.at ?? new Array(n).fill(0);
        let flash = r.key !== key;         // (another element's: its flashes, or none)
        for (let i = 0; i < n; i++) {
            const column = this.columns[i], {text, title} = formatCell(column, row, dv, this.columns, this.culture), cell = r.cells[i];
            const shown = column.overflow?.endsWith('-start') ? `\u2066${text}\u2069` : text;   // (cut at its start: isolated, left to right)
            if (cell.textContent !== shown) cell.textContent = shown;   // (text, never HTML)
            if (r.tips[i] !== (title ?? null)) {   // (its tooltip, the grid's own — live as the cell changes — and description)
                r.tips[i] = title ?? null;
                if (title) cell.setAttribute('aria-description', title); else cell.removeAttribute('aria-description');
            }
            if (this.columns[i].mark && cell.dataset.value !== text) cell.dataset.value = text;   // (for styling by value)
            if (seen && changedFor !== undefined && seen.texts[i] !== text) (at[i] = now - changedFor, flash = true);
            texts[i] = text;
        }
        if (flash)
            for (let i = 0; i < n; i++) this.#flash(r.cells[i], 'tg-changed', at[i] ? now - at[i] : Infinity);
        this.seen.set(key, {texts, at});
        r.key = key;
        r.bytes = row;
    }

    /**
     * Flashes an element, `elapsed` ms after what it shows happened: started where it has got to (a negative delay);
     * over, or never (Infinity), whatever flash it had stops. Two animations of the one name alternate, so a flash
     * starts again without a reflow — the other of the two each time, even if it was taken off this frame (an element
     * taking another row): taken off and put back in one frame, the style never changes, and nothing would start.
     */
    #flash(el, name, elapsed = 0) {
        const delay = flashDelay(elapsed, this.#duration(name));
        const a = el.classList.contains(`${name}-a`);
        el.classList.remove(`${name}-a`, `${name}-b`);
        if (delay === null) return;
        el.style.setProperty(`--${name}-delay`, `${delay}ms`);   // (the stylesheet's animation reads it: a row's, its cells)
        el.classList.add(`${name}-${a ? 'b' : 'a'}`);
    }

    /** The placeholders' shimmer's period as the page's CSS has it (ms; 0, none), looked up once. */
    get #shimmer() {
        if (this.shimmerMs === undefined) {
            const probe = document.createElement('div');
            probe.className = 'tg-row tg-placeholder tg-probe';
            this.layerEl.append(probe);
            this.shimmerMs = cssMs(getComputedStyle(probe, '::before').animationDuration);
            probe.remove();
        }
        return this.shimmerMs;
    }

    /** A flash's duration as the page's CSS has it (tg-changed, tg-arrived; --tg-flash-for), looked up once. */
    #duration(name) {
        let ms = this.durations.get(name);
        if (ms === undefined) {            // (a cell's flash, or a row's: a probe of each, unseen)
            const probe = document.createElement('div'), cell = document.createElement('span');
            probe.className = `${name}-a tg-probe`;
            cell.className = `tg-cell ${name}-a`;
            probe.append(cell);
            this.layerEl.append(probe);
            ms = Math.max(cssMs(getComputedStyle(probe).animationDuration), cssMs(getComputedStyle(cell).animationDuration));
            probe.remove();
            this.durations.set(name, ms);
        }
        return ms;
    }

    // ── columns: the stylesheet places them from the room each has (--tg-w<i>, LiveGrid's style); the module's, a measured
    //    column's, set on the grid's element over it ──

    /**
     * Each measured column's room, where the columns now stand (a chooser's change moves them); and where the frozen
     * columns end, the headings fading there scrolled sideways (--tg-frozen-x, data-frozen).
     */
    #styleColumns() {
        for (const name of [...this.host.style].filter(p => p.startsWith('--tg-w'))) this.host.style.removeProperty(name);
        const frozen = this.columns.filter(c => c.frozen).length;
        this.host.toggleAttribute('data-frozen', frozen > 0);
        if (frozen > 0) this.host.style.setProperty('--tg-frozen-x', `var(--tg-x${frozen})`);
        else this.host.style.removeProperty('--tg-frozen-x');
        this.columns.forEach((c, i) => {
            const px = c.auto ? this.widths.get(c.name) : undefined;
            if (px !== undefined) this.host.style.setProperty(`--tg-w${i}`, `${px}px`);
        });
    }

    /**
     * The room of the columns measured (auto), from the first rows answered: as wide as their widest text, or their
     * heading's — its label, and whatever the chrome put in it (a filter), with room for its sort arrow and rank.
     */
    #measure(order, table) {
        const probe = document.createElement('span');
        probe.className = 'tg-cell tg-probe';
        probe.style.padding = '0';
        this.layerEl.append(probe);
        const headings = this.head?.querySelectorAll('.tg-heading');
        this.columns.forEach((c, i) => {
            if (!c.auto || this.widths.has(c.name)) return;
            probe.textContent = c.label ?? c.name;
            let widest = probe.getBoundingClientRect().width + 28;   // (the arrow and a multi-column sort's rank)
            const heading = headings?.[i]?.cloneNode(true);          // (a copy, laid out at its own width, unseen)
            if (heading) {
                heading.style.cssText = 'position: absolute; visibility: hidden; width: max-content; padding: 0';
                this.layerEl.append(heading);
                widest = Math.max(widest, heading.getBoundingClientRect().width + 4);
                heading.remove();
            }
            for (const key of order.keys.slice(0, 200)) {
                const entry = table.rows.get(key);
                if (!entry) continue;
                const dv = new DataView(entry.row.buffer, entry.row.byteOffset, entry.row.byteLength);
                probe.textContent = formatCell(c, entry.row, dv, this.columns, this.culture).text;
                widest = Math.max(widest, probe.getBoundingClientRect().width);
            }
            this.widths.set(c.name, Math.ceil(widest));
        });
        probe.remove();
        this.#styleColumns();
    }
}

let overlaid;

/**
 * Whether the platform's scrollbars overlay what scrolls, taking no room (the Mac's, unless set to show always): looked at
 * once, on a probe with scrollbars of its own.
 */
function overlayScrollbars() {
    if (overlaid === undefined) {
        const probe = document.createElement('div');
        probe.style.cssText = 'position: absolute; visibility: hidden; overflow: scroll; width: 64px; height: 64px';
        document.body.append(probe);
        overlaid = probe.offsetWidth === probe.clientWidth;
        probe.remove();
    }
    return overlaid;
}

/**
 * A cell's classes: its side (cut at its start, running right to left, said physically), a number's, frozen, a link's
 * (activations heard), cut, its column's.
 */
function cellClass(c, rtl, activatable) {
    const number = NUMERIC.has(c.kind), align = c.align ?? (number ? 'end' : 'start');
    const side = c.overflow?.endsWith('-start') && (align === 'start' || align === 'end')
        ? (align === 'start') !== rtl ? 'left' : 'right'
        : align;
    return `tg-cell tg-${side}` + (number ? ' tg-num' : '') + (c.frozen ? ' tg-frozen' : '') + (c.link && activatable ? ' tg-link' : '') +
        (c.overflow ? ` tg-${c.overflow}` : '') + (c.class ? ` ${c.class}` : '');
}

/**
 * Joins a body to its LiveGrid (Blazor): `host` the grid's element, `body` the one Blazor leaves empty, `head` the
 * headings'; `grid` the component's DotNetObjectReference, which the page's requests, visibility, freshness, clicks and
 * keys go to; `options` GridBody's. Returns what the component calls: reset(view), setColumns(columns),
 * setSelection(keys), setRowEvents(clickable, activatable), reveal(position, above, key), dispose(). The body is the
 * grid element's `gridBody`, for scripts (a measuring run reads its blank sightings, `blanks`; a page in the background
 * draws by `draw()`).
 */
export function createGrid(host, body, head, grid, options) {
    host.gridBody?.dispose();              // (one made before, whose making the component never heard back from: gone)
    let fresh = 'unknown', lag = null;
    const call = (method, ...args) => grid.invokeMethodAsync(method, ...args).catch(() => {});   // (the circuit gone)
    const g = new GridBody(body, {
        ...options, host, head,
        request: r => call('Request', r.id, r.first, r.count, r.before, r.after, r.holds, r.letIn, r.view, !!r.racing, !!r.top,
            r.anchor ?? null),
        visible: shown => call('Visible', shown),
        status: s => {                     // (the rest of the status is the server's: only freshness comes from here)
            const now = s.fresh === 'checking' ? fresh : s.fresh;
            if (now !== undefined && now !== fresh) call('Fresh', fresh = now);
        },
        lag: ms => {                       // (told only as the figure shown would move: by 5 %, to two figures; or known no more)
            if (ms === null ? lag !== null : lag === null || moved(lag / 1000, ms / 1000)) call('Lag', lag = ms);
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
        setRowEvents: (clickable, activatable) => g.setRowEvents(clickable, activatable),
        top: () => g.top(),
        reveal: (position, above, key) => g.reveal(position, above, key),
        dispose: () => {
            g.dispose();
            delete host.gridBody;
        }
    };
}
