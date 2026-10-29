// Tapline's alert presenters (virtual-grid.md §15.4), for an audience's alerts as Tapline.Alerts delivers them: shared by
// shape — each rule's shape (its source, condition and trigger) one source of its running count, latest row and when it
// last fired, every page of every audience with that shape subscribed to it — and conflated, each rule's subscription
// taking its shape no oftener than the rule's throttle (Tapline's MinInterval), whatever fired meanwhile counted into it;
// beside the audience's inbox, a row per rule: its name, sound and mute, and how far a page of the audience has presented
// its shape's count. Each rule's alerts beyond those this page presented last are sounded (Web Audio, once a gesture
// allows it), notified in the background (with the browser's permission, one notification per rule, a newer replacing
// it), and marked unseen (the page's icon dotted while it's in the background); then said back, so every page of the
// audience presents from there, and a page opened later presents only what fired meanwhile ("while you were away").
// Knows nothing of grids. And a browser-local store of the rules, sent to the host as a page opens (decision (r)).

/** Where AlertRow's fields are (an inbox's row): packed, little-endian; a text, its length (high bit: cut) then its UTF-8. */
export const ROW = Object.freeze({
    presented: 0, muted: 8, source: 9, name: 25, sound: 73, size: 105,
    widths: Object.freeze({source: 16, name: 48, sound: 32})
});

/** Where AlertFeedValue's fields are (a shape's value): packed, little-endian. */
export const SHAPE = Object.freeze({
    fired: 0, firedAt: 8, key: 16, summary: 64, size: 192,
    widths: Object.freeze({key: 48, summary: 128})
});

const utf8 = new TextDecoder();

/** An inline text field: its string, and whether it was cut. */
function text(bytes, at, width) {
    const length = Math.min(bytes[at] & 0x7F, width - 1);
    return {value: utf8.decode(bytes.subarray(at + 1, at + 1 + length)), cut: (bytes[at] & 0x80) !== 0};
}

const int64 = (bytes, at) => Number(new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getBigInt64(at, true));

/** A rule's row, as its audience's inbox has it: how far presented, muted or not, its source, name and sound. */
export function readRow(bytes) {
    return {
        presented: int64(bytes, ROW.presented),
        muted: bytes[ROW.muted] === 1,
        source: text(bytes, ROW.source, ROW.widths.source).value,
        name: text(bytes, ROW.name, ROW.widths.name).value,
        sound: text(bytes, ROW.sound, ROW.widths.sound).value
    };
}

/** A shape's value: its running count, when it last fired (ms since 1970, null if never), its latest row's key and line. */
export function readShape(bytes) {
    const firedAt = int64(bytes, SHAPE.firedAt), summary = text(bytes, SHAPE.summary, SHAPE.widths.summary);
    return {
        fired: int64(bytes, SHAPE.fired),
        firedAt: firedAt === 0 ? null : firedAt / 1000,
        key: text(bytes, SHAPE.key, SHAPE.widths.key).value,
        summary: summary.value + (summary.cut ? '…' : '')
    };
}

/** A .NET Guid's 16 bytes (its fields little-endian, as it lays them out) as its string. */
export function guidOf(bytes) {
    const hex = i => bytes[i].toString(16).padStart(2, '0');
    const at = order => order.map(hex).join('');
    return `${at([3, 2, 1, 0])}-${at([5, 4])}-${at([7, 6])}-${at([8, 9])}-${at([10, 11, 12, 13, 14, 15])}`;
}

/** How long the alerts that fired while away are gathered, after the last of them, to be said in one line. */
const AWAY_GATHER_MS = 250;

/**
 * An audience's alerts, presented on this page. Options:
 *   inbox       the audience's inbox (AlertPage.Inbox): a row per rule
 *   shapes      { rule: binding } — each rule's shape at its throttle (AlertPage.Shapes); again by setShapes as rules change
 *   since       when the page was opened, by the desk's clock (AlertPage.AttachedAt, ms since 1970): a rule's alerts whose
 *               shape last fired before it fired while away, no page of the audience open to present them
 *   tapline     the page's Tapline client (default: globalThis.tapline)
 *   presented   (rule, count) → told what this page presented of a rule: up to its shape's count (to the host)
 *   play        (sound) → plays a rule's sound (AlertSounds.play, say); not called for a muted rule
 *   alert       ({rule, name, count, summary, key, source, muted, firedAt, away}) → the app's own presenter (a toast, a list)
 *   away        (alerts) → what fired while no page of the audience was open: said in one line, by the app
 *   unseen      (bool) → whether alerts have come since the app last called seen(): its bell's dot
 *   notify      true: notified in the background, with the browser's permission
 *   soundOn     false: nothing sounded (till soundOn is set, by a gesture)
 */
export class AlertInbox {
    constructor(options) {
        this.options = options;
        this.tapline = options.tapline ?? globalThis.tapline;
        this.rules = new Map();          // rule → its row
        this.values = new Map();         // rule → its shape's value
        this.shown = new Map();          // rule → its shape's count, as far as this page has presented it
        this.subscriptions = new Map();  // rule → [binding, unsubscribe]
        this.awayList = [];
        this.since = options.since ?? -Infinity;
        this.unseenNow = false;
        this.notify = !!options.notify;
        this.soundOn = options.soundOn !== false;
        this.icon = new UnseenIcon();
        this.onVisible = () => {
            if (!document.hidden) this.icon.mark(false);
        };
        document.addEventListener('visibilitychange', this.onVisible);
        this.off = this.tapline.subscribe(options.inbox, state => this.#inbox(state), error => options.error?.(error));
        this.setShapes(options.shapes ?? {});
    }

    /** Each rule's shape, as the host says it now: subscribed to those new (by tap id), those gone let go. */
    setShapes(shapes) {
        const tapOf = binding => typeof binding === 'number' ? binding : binding?.tapId;
        for (const [rule, [binding, off]] of this.subscriptions)
            if (shapes[rule] === undefined || tapOf(shapes[rule]) !== tapOf(binding)) {
                off?.();
                this.subscriptions.delete(rule);
                this.values.delete(rule);
            }
        for (const [rule, binding] of Object.entries(shapes))
            if (!this.subscriptions.has(rule))
                this.subscriptions.set(rule, [binding, this.tapline.subscribe(binding, state => this.#shape(rule, state),
                    error => this.options.error?.(error))]);
    }

    /** The alerts looked at: the unseen mark gone. */
    seen() {
        if (!this.unseenNow) return;
        this.unseenNow = false;
        this.options.unseen?.(false);
    }

    dispose() {
        clearTimeout(this.awayTimer);
        this.off?.();
        for (const [, off] of this.subscriptions.values()) off?.();
        this.subscriptions.clear();
        document.removeEventListener('visibilitychange', this.onVisible);
        this.icon.mark(false);
    }

    #inbox(state) {
        const table = state?.members?.[0] ?? state;
        if (!table?.rows) return;
        const now = new Map();
        for (const entry of table.rows.values()) now.set(guidOf(entry.key), readRow(entry.row));
        for (const rule of this.rules.keys()) if (!now.has(rule)) this.shown.delete(rule); // (rules removed)
        this.rules = now;
        for (const rule of now.keys()) this.#consider(rule);
    }

    #shape(rule, state) {
        if (!(state instanceof Uint8Array) || state.length < SHAPE.size) return;
        this.values.set(rule, readShape(state));
        this.#consider(rule);
    }

    /** A rule whose row and shape are both here: what its shape counted past what this page, or its audience, presented. */
    #consider(rule) {
        const row = this.rules.get(rule), value = this.values.get(rule);
        if (!row || !value) return;
        const shown = Math.max(this.shown.get(rule) ?? row.presented, row.presented);
        this.shown.set(rule, Math.max(shown, value.fired));
        if (value.fired <= shown) return;
        const alert = {rule, ...row, ...value, count: value.fired - shown, away: value.firedAt !== null && value.firedAt < this.since};
        if (alert.away) {
            this.awayList.push(alert);
            clearTimeout(this.awayTimer);
            this.awayTimer = setTimeout(() => this.#gathered(), AWAY_GATHER_MS);
        }
        this.#present(alert);
    }

    /** What fired while away, said in one line. */
    #gathered() {
        if (this.awayList.length > 0) this.options.away?.(this.awayList);
        this.awayList = [];
    }

    #present(alert) {
        this.options.alert?.(alert);
        if (!alert.muted && this.soundOn) this.options.play?.(alert.sound);
        if (!this.unseenNow) {
            this.unseenNow = true;
            this.options.unseen?.(true);
        }
        if (document.hidden) {
            this.icon.mark(true);
            if (this.notify && typeof Notification !== 'undefined' && Notification.permission === 'granted')
                new Notification(alert.name, {body: (alert.count > 1 ? `×${alert.count} ` : '') + alert.summary, tag: alert.rule});
        }
        this.options.presented?.(alert.rule, alert.fired);
    }
}

/**
 * Sounds, through Web Audio: each fetched the first time it's played (by its URL: `urlOf(sound)`), decoded and kept.
 * A browser plays nothing till a gesture: `unlock()` from a click (the app's splash, say) lets it.
 */
export class AlertSounds {
    constructor(urlOf) {
        this.urlOf = urlOf;
        this.buffers = new Map();
        this.context = null;
    }

    /** From a gesture: sound allowed from now. */
    unlock() {
        this.context ??= new AudioContext();
        return this.context.resume();
    }

    get unlocked() {
        return this.context?.state === 'running';
    }

    async play(sound) {
        if (!this.unlocked || !sound) return;
        let buffer = this.buffers.get(sound);
        if (!buffer) {
            const response = await fetch(this.urlOf(sound));
            if (!response.ok) return;
            buffer = await this.context.decodeAudioData(await response.arrayBuffer());
            this.buffers.set(sound, buffer);
        }
        const source = this.context.createBufferSource();
        source.buffer = buffer;
        source.connect(this.context.destination);
        source.start();
    }
}

/** The page's icon, dotted red while alerts have come in the background: drawn over the icon the page has, then put back. */
class UnseenIcon {
    constructor() {
        this.link = null;
        this.original = null;
        this.marked = false;
    }

    mark(on) {
        if (on === this.marked || typeof document === 'undefined') return;
        this.marked = on;
        this.link ??= document.querySelector('link[rel~="icon"]') ?? Object.assign(document.createElement('link'), {rel: 'icon'});
        if (!this.link.isConnected) document.head.appendChild(this.link);
        this.original ??= this.link.href || '';
        if (!on) {
            if (this.original) this.link.href = this.original;
            else this.link.removeAttribute('href');
            return;
        }
        const canvas = Object.assign(document.createElement('canvas'), {width: 32, height: 32});
        const g = canvas.getContext('2d');
        const dot = () => {
            g.beginPath();
            g.arc(24, 8, 7, 0, 2 * Math.PI);
            g.fillStyle = '#d93025';
            g.fill();
            if (this.marked) this.link.href = canvas.toDataURL('image/png');
        };
        if (!this.original) return dot();
        const image = new Image();
        image.onload = () => {
            g.drawImage(image, 0, 0, 32, 32);
            dot();
        };
        image.onerror = dot;
        image.src = this.original;
    }
}

/** The rules as this browser keeps them (its local settings): its audience's id — random, kept, a bearer secret — and its rules. */
export function loadRules(name = 'tapline-alerts') {
    let saved = null;
    try {
        saved = JSON.parse(localStorage.getItem(name) ?? 'null');
    } catch {
        saved = null;
    }
    const audience = typeof saved?.audience === 'string' && saved.audience.length >= 32 ? saved.audience : crypto.randomUUID();
    const rules = Array.isArray(saved?.rules) ? saved.rules : [];
    saveRules(rules, name, audience);
    return {audience, rules, soundOn: saved?.soundOn !== false, notify: saved?.notify === true};
}

/** The rules kept (and the audience's id with them, and whether to sound and notify). */
export function saveRules(rules, name = 'tapline-alerts', audience = null, settings = {}) {
    try {
        const saved = JSON.parse(localStorage.getItem(name) ?? 'null') ?? {};
        localStorage.setItem(name, JSON.stringify({
            ...saved, ...settings, audience: audience ?? saved.audience ?? crypto.randomUUID(), rules
        }));
    } catch {
        // (local settings unavailable: the rules last this page's life)
    }
}
