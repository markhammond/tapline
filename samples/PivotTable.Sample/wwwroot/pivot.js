// The live position viewer's grid. The library knows nothing of pivots: this decodes a cell's key — its row and column
// labels, as text (64 and 32 bytes, zero-padded), each its levels joined by " · " (a total's fewer, the grand total's
// none) — and row — i64 quantity (units of the asset), notional, realized and unrealized PnL, balance, volume (the
// reporting currency), trades (a count), max gross (the reporting currency), mark price (a rate: its instrument's quote per unit of
// its asset), each in hundred-millionths; i64 min: none, a figure that doesn't add up across what the cell spans — from the replica the
// library keeps (its checksum checked against the router's on every record). The statement made every total: the grid
// sums nothing. It lays the cells out as trees — rows nested, columns grouped, each group collapsible to its total — with
// the values asked for under each column (position, notional, PnL: realized and unrealized, or collapsed to one, balance) — or
// as its heading's ⚙ sets them (notional in the Total column alone, say; kept in this browser) — each level in its
// field's order (by its labels, or values one after another: live, held while the pointer is over the grid); the
// headings pinned as it scrolls down, and the grand total's row and columns, first, pinned too; built when what's visible
// changes, updated in place otherwise, a cell flashing as it changes. How it's shown is the page's alone: nothing is
// asked of the server.
//
// Changing the pivot swaps it without a gap. Before the page subscribes to the new one, prepare() makes a correlation id
// and awaits its rendezvous (tapline.rendezvous: before the request goes, so nothing can come first), and marks the grid
// pending — the pivot shown streams on, dimmed, a spinner over it. The id goes with the subscription; the provider hands
// it back once the page has the new pivot's first answer (after it, in order). Meanwhile the new subscription fills a
// grid of its own, off-screen; the rendezvous swaps it in — whole, never empty — and the old one is let go. Refused, the
// pending mark goes and the pivot shown stays. No rendezvous within 5 s of the new pivot's first answer (one gone
// missing): swapped anyway. Until that answer is here (a slow link takes a while) the pivot shown stays, dimmed.

const ROW_BYTES = 64, COLUMN_BYTES = 32, SWAP_TIMEOUT_MS = 5000, SEP = ' · ';
const NONE = -(2n ** 63n);                                       // i64 min: no figure
const CENTS_TO_PLACES = 10n ** 6n;                               // (a cell's volume is in cents: brought to the grid's eight places)
const CELL_BYTES = 72;                                           // a cell's figures, nine i64s — then what it carries besides (Show as)
const CLOCK = '\u0001clock';                                      // the clock cell's row label (no statement's: a control character)

/**
 * A pivot's clock cell, the provider's, in each post with the cells it describes: the answer's watermark — the earliest
 * change it answers, as the provider received it (0: nothing new) — and when it was emitted, in µs since 1970 (UTC, the
 * provider's clock). Not a cell of the grid's.
 */
function clockOf(state) {
    for (const {key, row} of state.rows.values()) {
        if (key[0] !== 1 || label(key, 0, ROW_BYTES) !== CLOCK) continue;
        const v = new DataView(row.buffer, row.byteOffset, row.byteLength);
        return {watermark: Number(v.getBigInt64(0, true)), emitted: Number(v.getBigInt64(8, true))};
    }
    return null;
}

/** A pivot's lags, its last answers' (2,048): emission less watermark, in ms — and their quantiles. */
function lags() {
    const all = new Float64Array(2048);
    let count = 0, next = 0;
    return {
        add(x) {
            all[next] = x;
            next = (next + 1) % all.length;
            count = Math.min(count + 1, all.length);
        },
        get count() {
            return count;
        },
        at(q) {
            const sorted = all.slice(0, count).sort();
            return sorted[Math.min(count - 1, Math.max(0, Math.ceil(q * count) - 1))];
        },
    };
}

const title = name => name.charAt(0).toUpperCase() + name.slice(1);   // a field's name as a label shows it ("Client")
const COLUMN_SETTING = 'pivot.columnValues',
    FIELDS = ['position', 'mark', 'notional', 'gross', 'pnl', 'balance', 'volume', 'trades'];   // (the values a column can show, in order — unless the page's options.order, dragged, says another)
const CHEVRON = '<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M6 3.5 10.5 8 6 12.5" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/></svg>';
const ROW_MENU = '\u0001total row';                              // the grand total row's ⚙ (its menu's key: no column's label)
const COG = '<svg viewBox="0 0 16 16" width="11" height="11" aria-hidden="true"><path fill="currentColor" fill-rule="evenodd" d="M6.9 1h2.2l.4 1.9 1.3.6 1.7-1 1.5 1.5-1 1.7.6 1.3 1.9.4v2.2l-1.9.4-.6 1.3 1 1.7-1.5 1.5-1.7-1-1.3.6-.4 1.9H6.9l-.4-1.9-1.3-.6-1.7 1-1.5-1.5 1-1.7-.6-1.3L1 9.1V6.9l1.9-.4.6-1.3-1-1.7L4 2l1.7 1 1.3-.6zM8 5.6a2.4 2.4 0 1 0 0 4.8 2.4 2.4 0 0 0 0-4.8z"/></svg>';
const utf8 = new TextDecoder();
const label = (bytes, at, length) => {
    const b = bytes.subarray(at, at + length), end = b.indexOf(0);
    return utf8.decode(end < 0 ? b : b.subarray(0, end));
};
const count = n => Number(n).toLocaleString('en-US');
const ms = x => `${x < 100 ? x.toFixed(1) : Math.round(x)} ms`;
const hex = n => '0x' + (n >>> 0).toString(16).toUpperCase().padStart(8, '0');
const levels = text => text === '' ? 0 : text.split(SEP).length;
const parent = text => {
    const i = text.lastIndexOf(SEP);
    return i < 0 ? '' : text.slice(0, i);
};
const last = text => {
    const i = text.lastIndexOf(SEP);
    return i < 0 ? text : text.slice(i + SEP.length);
};
const prefix = (text, n) => text.split(SEP).slice(0, n).join(SEP);
const symbols = new Map();
/** A currency's symbol as en-US writes it ($, €, ¥, £, CHF, CA$…); none if unknown (a statement of your own). */
const symbol = currency => {
    if (!currency) return '';
    if (!symbols.has(currency)) {
        const s = new Intl.NumberFormat('en-US', {
            style: 'currency',
            currency
        }).formatToParts(0).find(p => p.type === 'currency')?.value ?? currency;
        symbols.set(currency, /^[A-Z]+$/.test(s) ? `${s} ` : s);
    }
    return symbols.get(currency);
};
// A figure as shown — an i64 of hundred-millionths (8 places, the statement's DECIMAL scale) — by what it's worth in
// US dollars (the reporting currency's rate, from the provider's stats). An amount: three significant figures, a
// non-zero one worth less than a dollar shown as under that ("<$1"), bare — its heading marked *, the note under the grid
// naming the currency. A position: up, away from zero, to the place nearest a dollar's worth of its asset; blank if it's worth
// less than half a dollar. Its tooltip, precisely enough: an amount away from zero to a step of about 5 cents' worth
// in its currency ($0.05, €0.05, ¥10); a position the same, in its asset (0.000001 BTC), to at most 5 significant
// figures; a non-zero figure worth less than that shown as under it ("<$0.05"). Never more than 8 places.
const usdRates = new Map([['USD', 1]]);                         // US dollars a unit of each currency is worth
const PLACES = 8, SIGNIFICANT = 3, SIGNIFICANT_PRECISE = 5, SHOWN_FLOOR = 1, PRECISE_FLOOR = 0.05, HALF_DOLLAR = 0.5;   // (the floors in US dollars)
const units = n => Number(n) / 1e8;
/** A figure exactly, as decimal text: its hundred-millionths (a BigInt) in units, no trailing zeros — not through a double. */
const exactly = n => {
    const negative = n < 0n, magnitude = negative ? -n : n,
        fraction = (magnitude % 100000000n).toString().padStart(8, '0').replace(/0+$/, '');
    return `${negative ? '-' : ''}${magnitude / 100000000n}${fraction ? `.${fraction}` : ''}`;
};
const inDollars = (amount, currency) => amount * (usdRates.get(currency) ?? 1);    // (a statement of your own, no currency: as if dollars)
const ofDollars = (usd, currency) => usd / (usdRates.get(currency) ?? 1);
const nearestPlace = amount => 10 ** Math.round(Math.log10(amount));
const placeIn = place => place <= 1e-8 ? 1n : BigInt(Math.round(place * 1e8));   // (in hundred-millionths: one at least)
const roundUp = (n, step) => {
    const a = n < 0n ? -n : n, r = (a + step - 1n) / step * step;
    return n < 0n ? -r : r;
};   // away from zero
const roundHalf = (n, step) => {
    const a = n < 0n ? -n : n, r = (a + step / 2n) / step * step;
    return n < 0n ? -r : r;
};   // to the nearest, half away from zero

const decimal = n => {
    const s = (n < 0n ? -n : n).toString().padStart(PLACES + 1, '0');
    return `${n < 0n ? '-' : ''}${s.slice(0, -PLACES)}.${s.slice(-PLACES)}`;
};
const formats = new Map();                                       // (Intl formats a decimal string exactly, never through a double)
const format = (key, options) => {
    if (!formats.has(key)) formats.set(key, new Intl.NumberFormat('en-US', options));
    return formats.get(key);
};
/** A magnitude's places for three significant figures (none past 8). */
const significantPlaces = a => Math.min(PLACES, Math.max(0, SIGNIFICANT - 1 - Math.floor(Math.log10(a))));
/** A non-zero figure worth less than a floor: under it — <$1, >−€0.926, <0.00000077 (the floor to three significant figures). */
const under = (negative, floor, prefix) => `${negative ? '>−' : '<'}${prefix}${floor.toFixed(significantPlaces(floor)).replace(/\.0+$|(\.\d*?)0+$/, '$1')}`;

/** An amount as shown: three significant figures, compactly — $46.6k, −€1.23M, $4.67 — or under a dollar's worth: <$1. Bare: no symbol (46.6k). */
const shownAmount = (n, currency, bare = false, significant = SIGNIFICANT) => {
    const x = units(n), a = Math.abs(x), sign = x < 0 ? -1 : 1, floor = ofDollars(SHOWN_FLOOR, currency),
        sym = bare ? '' : symbol(currency);
    if (n === 0n) return {value: 0, text: `${sym}0`};
    if (a < floor) return {value: sign * floor, text: under(x < 0, floor, sym)};
    const place = v => 10 ** (Math.floor(Math.log10(v)) - significant + 1), r = Math.round(a / place(a)) * place(a);   // (999.6 → 1,000: places of the rounded)
    const [unit, suffix] = r >= 1e12 ? [1e12, 'T'] : r >= 1e9 ? [1e9, 'B'] : r >= 1e6 ? [1e6, 'M'] : r >= 1e3 ? [1e3, 'k'] : [1, ''];
    const digits = Math.min(PLACES, Math.max(0, Math.round(Math.log10(unit / place(r)))));
    return {
        value: sign * r,
        text: `${x < 0 ? '−' : ''}${sym}${(r / unit).toFixed(digits).replace(/\.0+$|(\.\d*?)0+$/, '$1')}${suffix}`
    };   // (no trailing zeros)
};
/** The units amounts can be shown in (the note under the grid's, beside the currency): units, thousands or millions. */
const SCALES = {'1': 1, k: 1e3, M: 1e6};
/**
 * An amount in a unit (thousands, say): its significant figures, no suffix, thousands separated — 18,510, −65.7, 0.029
 * — every amount in one unit, so none jumps from 999k to 1.00M; or under a dollar's worth: <0.001.
 */
const scaledAmount = (n, currency, significant, unit) => {
    const x = units(n) / unit, a = Math.abs(x), sign = x < 0 ? -1 : 1, floor = ofDollars(SHOWN_FLOOR, currency) / unit;
    if (n === 0n) return {value: 0, text: '0'};
    if (a < floor) return {value: sign * floor * unit, text: under(x < 0, floor, '')};
    // Its significant figures, but no place finer than a dollar's worth (3 in thousands of dollars, 1 in thousands of yen):
    // a small amount short, not 0.00002940.
    const finest = -Math.round(-Math.log10(floor)),
        place = v => 10 ** Math.max(Math.floor(Math.log10(v)) - significant + 1, finest);
    const r = Math.round(a / place(a)) * place(a),
        digits = Math.min(PLACES, Math.max(0, -Math.floor(Math.log10(place(r)) + 1e-9)));   // (places of the rounded)
    return {
        value: sign * r * unit,
        text: `${x < 0 ? '−' : ''}${format(`scaled ${digits}`, {maximumFractionDigits: digits}).format(r)}`
    };   // (no trailing zeros)
};
/**
 * An amount in lots of its unit — a thousand dollars, in thousands — to the nearest, every digit: 83,353,572 (thousands),
 * 83,353,572,309 (ones). A volume, which each trade adds its notional to: three significant figures would hide them.
 */
const lotAmount = (n, unit) => {
    const lots = roundHalf(n, 100000000n * BigInt(unit)) / (100000000n * BigInt(unit));   // (to the nearest lot: a unit's worth)
    return {
        value: Number(lots) * unit,
        text: format('lots', {maximumFractionDigits: 0}).format(lots).replace('-', '−')
    };
};
/** A step near a target: 1, 2 or 5 times a power of ten, the nearest by ratio — 0.05 for 0.0463, 10 for 7.5. */
const niceStep = target => {
    const p = 10 ** Math.floor(Math.log10(target));
    return [p, 2 * p, 5 * p, 10 * p].reduce((best, s) => Math.abs(Math.log(s / target)) < Math.abs(Math.log(best / target)) ? s : best);
};
/**
 * An amount precisely enough: away from zero to a step of about 5 US cents' worth in its currency — −$689.90,
 * €1,159.15, ¥8,410 — or under that step: <$0.05, <¥10. A statement of your own (no currency): every digit.
 */
const preciseAmount = (n, currency) => {
    if (!currency) return format('amount', {maximumFractionDigits: PLACES}).format(decimal(n)).replace('-', '−');
    const step = niceStep(ofDollars(PRECISE_FLOOR, currency));
    if (n !== 0n && Math.abs(units(n)) < step) return under(n < 0n, step, symbol(currency));   // (<€0.05, <¥10: its step)
    const digits = Math.max(0, -Math.floor(Math.log10(step) + 1e-9)),
        rounded = roundUp(n, BigInt(Math.round(step * 1e8)));
    return format(`amount ${currency} ${digits}`, {
        style: 'currency',
        currency,
        minimumFractionDigits: digits,
        maximumFractionDigits: digits
    }).format(decimal(rounded)).replace('-', '−');
};
/** A position's worth in US dollars, and a unit of its asset's (none without its notional). */
const worth = (n, notional, currency) => {
    if (notional === null) return null;
    const usd = Math.abs(inDollars(units(notional), currency));
    return {usd, unit: n === 0n ? null : usd / Math.abs(units(n))};
};
/** A position as shown: up, away from zero, to the place nearest a dollar's worth of its asset — +2,000, −1.6, +0.0503 — or blank, worth less than half a dollar. */
const shownPosition = (n, notional, currency) => {
    const w = worth(n, notional, currency);
    if (n === 0n || (w && w.usd < HALF_DOLLAR)) return {value: 0, text: ''};
    const step = w?.unit ? placeIn(nearestPlace(1 / w.unit)) : 1n, rounded = roundUp(n, step),
        digits = Math.max(0, PLACES - (step.toString().length - 1));
    return {
        value: units(rounded),
        text: format(`shown position ${digits}`, {
            maximumFractionDigits: digits,
            signDisplay: 'exceptZero'
        }).format(decimal(rounded)).replace('-', '−')
    };
};
/**
 * A position precisely enough: away from zero to a step of about 5 cents' worth of its asset (in US dollars, whatever
 * the reporting currency: 0.000001 BTC, 0.5 DOGE), then to the nearest of at most 5 significant figures — in scientific
 * notation where that rounds a figure at or above the units (+1.2346E5, not a misleading 123,460) — or under that step:
 * <0.000001.
 * Without its notional to price it: 5 significant figures.
 */
const precisePosition = (n, notional, currency) => {
    const w = worth(n, notional, currency), step = w?.unit ? niceStep(PRECISE_FLOOR / w.unit) : null;
    if (n !== 0n && step && Math.abs(units(n)) < step) return under(n < 0n, step, '');
    let r = step ? roundUp(n, placeIn(step)) : n, place = 1n;
    const magnitude = (r < 0n ? -r : r).toString();              // (in hundred-millionths: its significant figures, trailing zeros not)
    if (magnitude.replace(/0+$/, '').length > SIGNIFICANT_PRECISE) {
        place = 10n ** BigInt(magnitude.length - SIGNIFICANT_PRECISE);
        r = roundHalf(r, place);                                    // (the nearest: away from zero, a figure off by a unit or more)
    }
    return (place > 100_000_000n
        ? format('position scientific', {
            notation: 'scientific',
            maximumSignificantDigits: SIGNIFICANT_PRECISE,
            signDisplay: 'exceptZero'
        }).format(Number(decimal(r)))
        : format('position', {
            maximumFractionDigits: PLACES,
            signDisplay: 'exceptZero'
        }).format(decimal(r))).replace('-', '−');
};

/** A figure without a leading zero, its sign kept: .69, −.029, +.6, <.001. */
const leadless = text => text.replace(/(^|[^\d,.])0(?=\.)/, '$1');

/**
 * An amount as a column shows it, in two parts lined up at the point: its whole, and its fraction (from the point, or a
 * unit's letter: k, M; a share's %) — without a leading zero, as trading screens write it: .69, −.029, <.001, .4%.
 * Trailing zeros are gone already. (A position isn't lined up: each is in its own asset's units.)
 */
const parts = text => {
    const t = leadless(text), at = t.indexOf('.');
    if (at >= 0) return [t.slice(0, at), t.slice(at)];
    const unit = /^(.*\d)([kMBT%])$/.exec(t);
    return unit ? [unit[1], unit[2]] : [t, ''];
};

/** A count — trades — whole, thousands separated: 1,284. */
const wholeCount = n => format('count', {maximumFractionDigits: 0}).format(decimal(n));

/**
 * A rate — a mark price, its instrument's quote per unit of its asset — as shown: five significant figures, to the
 * nearest, but none of its whole rounded off; its places fixed by them — 60,123, 3,012.4, 145.23, .12346. Not scaled
 * (thousands are an amount's unit), nor lined up: each is in its own quote.
 */
const RATE_SIGNIFICANT = 5;
const shownRate = n => {
    if (n === 0n) return {value: 0, text: '0'};
    const places = Math.min(PLACES, Math.max(0, RATE_SIGNIFICANT - 1 - Math.floor(Math.log10(Math.abs(units(n))))));
    const r = roundHalf(n, 10n ** BigInt(PLACES - places));
    return {
        value: units(r),
        text: format(`rate ${places}`, {
            minimumFractionDigits: places,
            maximumFractionDigits: places
        }).format(decimal(r)).replace('-', '−')
    };
};
/** A rate precisely: every digit it has (to eight places), thousands separated: 60,123.45678912. */
const preciseRate = n => format('rate', {maximumFractionDigits: PLACES}).format(decimal(n)).replace('-', '−');

/**
 * A value as a percentage of a total (Show as: % of row, % of column, % of grand total) — the provider's quotient, a
 * fraction to eight places, to the nearest (half away from zero) — as shown, to the nearest tenth of a percent, its tenth
 * always written, as a rate's places are: 12.3%, 12.0%, .4%, −3.1%, 140.0% (a part bigger than a total of mixed signs);
 * under a tenth, not none: <.1%, >−.1%; none: 0%. Precisely, to six places: 12.345678%. Lined up at the point.
 */
const shownPercent = n => {
    const r = roundHalf(n * 100n, 10_000_000n);                  // (in hundred-millionths of a percent: to the tenth)
    if (r === 0n) return n === 0n ? {value: 0, text: '0%'} : n < 0n ? {value: -0.1, text: '>−0.1%'} : {value: 0.1, text: '<0.1%'};
    return {value: units(r), text: `${format('percent', {minimumFractionDigits: 1, maximumFractionDigits: 1}).format(decimal(r)).replace('-', '−')}%`};
};
const precisePercent = n => `${format('percent precise', {maximumFractionDigits: 6}).format(decimal(n * 100n)).replace('-', '−')}%`;

/** What a column can show: a cell's figure (null: none), how it's shown and how it reads precisely, and whether its sign is coloured. */
const amount = {shown: (v, c, cur) => shownAmount(v, cur), precise: (v, c, cur) => preciseAmount(v, cur)};
// (bare: its heading marked *, the note under the grid naming the unit and currency)
const bareAmount = (significant = SIGNIFICANT) => ({
    ...amount, named: true, aligned: true,                       // (amounts, in one unit: lined up at the point)
    shown: (v, c, cur, o) => SCALES[o?.scale] ? scaledAmount(v, cur, significant, SCALES[o.scale]) : shownAmount(v, cur, true, significant),
});
const VALUES = {
    position: {
        title: 'Position',
        figure: c => c.q,
        shown: (v, c, cur) => shownPosition(v, c.n, cur),
        precise: (v, c, cur) => precisePosition(v, c.n, cur),
        signed: false
    },
    mark: {
        title: 'Mark price',
        figure: c => c.m,
        shown: v => shownRate(v),
        precise: v => preciseRate(v),
        signed: false
    },   // (a rate, in its instrument's quote: not an amount)
    notional: {title: 'Notional', figure: c => c.n, ...bareAmount(4), signed: false},   // (four significant figures: a position's size)
    gross: {title: 'Max gross', figure: c => c.g, ...bareAmount(4), signed: false},    // (each client's larger side, long or short, summed: margin utilisation)
    realized: {title: 'Realized', figure: c => c.r, ...bareAmount(), signed: true},
    unrealized: {title: 'Unrealized', figure: c => c.u, ...bareAmount(), signed: true},
    pnl: {
        title: 'Pos PnL',
        figure: c => c.r === null ? null : c.u === null ? c.r : c.r + c.u, ...bareAmount(),
        signed: true
    },   // (its unrealized none — across days — as nothing: realized alone)
    balance: {title: 'Balance', figure: c => c.b, ...bareAmount(), signed: true},
    volume: {
        title: 'Volume',
        figure: c => c.v, ...bareAmount(),
        shown: (v, c, cur, o) => lotAmount(v, SCALES[o?.scale] ?? 1),
        signed: false
    },   // (in whole lots of the unit: each trade shows)
    trades: {
        title: 'Trades',
        figure: c => c.t,
        shown: v => ({value: units(v), text: wholeCount(v)}),
        precise: v => wholeCount(v),
        signed: false
    },
};

/**
 * How a value can show — as a spreadsheet pivot's "Show as": the value itself, or a percentage of a total — its row's (the
 * Total column's figure on its row), its column's (the Total row's in its column) or the grand total, each of the market
 * (the total ignoring the filters on the fields it runs across: filter two clients, and each is still a share of all of
 * them) or of what's shown (the pivot's own total: they add up to 100%) — or a running total in Date. Each form a page
 * asks for (options.showAs: by value, absent its value alone) is a column of its own, in this order; its heading is its
 * value's, marked with its total ("% col", "% col shown") or "cum.". The data provider computes each — the page asks for
 * them with the pivot, and its cells carry them (options.extras: their order) — so every client of a pivot reads the same
 * figures. Volume as % of column, dates across: each row's market share; as a running total in Date: what each row has
 * dealt since the first date shown.
 */
const FORMS = {
    row: {mark: '% row', of: "its row's total in the market (the Total column's, the filters on the column fields ignored)", total: 'row', at: 'row', market: true},
    column: {mark: '% col', of: "its column's total in the market (the Total row's, the filters on the row fields ignored)", total: 'column', at: 'column', market: true},
    total: {mark: '% total', of: 'the grand total in the market (the filters on the row and column fields ignored)', total: 'grand', at: 'total', market: true},
    rowShown: {mark: '% row shown', of: "its row's total shown (the Total column, on its row)", total: 'row', at: 'row'},
    columnShown: {mark: '% col shown', of: "its column's total shown (the Total row, in its column)", total: 'column', at: 'column'},
    totalShown: {mark: '% total shown', of: 'the grand total shown', total: 'grand', at: 'total'},
    running: {mark: 'cum.'},
};
/** What a running total in Date can be of: what adds up across dates (each day's) — a close's position, notional or PnL doesn't. */
const RUNS = new Set(['volume', 'trades']);
const dated = options => options.rows?.includes('date') || options.columns?.includes('date');
/** A value's forms, as asked — a running total only where Date is in the rows or columns; none left, its value. */
const formsOf = (options, v) => {
    if (v === 'mark') return ['value'];                          // (a rate, averaged: a percentage of nothing, nor a sum)
    const forms = (options.showAs?.[v] ?? ['value']).filter(f => f !== 'running' || (RUNS.has(v) && dated(options)));
    return forms.length ? forms : ['value'];
};
const derived = new Map();

/**
 * What a column shows by its key: a value's own (VALUES), or one the provider computed for it ("volume.column") — as
 * the cell carries it; not carried yet (just asked for: the pivot carrying it is on its way), undefined.
 */
const spec = key => VALUES[key] ?? derived.get(key) ?? derived.set(key, key.endsWith('.running') ? runningOf(key) : percentOf(key)).get(key);
const carried = (c, key) => c.x !== undefined && Object.hasOwn(c.x, key) ? c.x[key] : undefined;

function percentOf(key) {
    const [v, form] = key.split('.'), base = VALUES[v], {mark, of, total, at, market} = FORMS[form];
    return {
        get name() { return base.title; }, mark,
        get title() { return `${base.title} ${mark}`; },
        get description() { return `${base.description ? `${base.description} — ` : ''}as a percentage of ${of}`; },
        figure: c => carried(c, key),
        shown: n => shownPercent(n),
        precise: (n, c, cur, totals) => {
            const t = market ? undefined : totals?.[at], y = t ? base.figure(t) : null;   // (the market's: in no cell)
            return y === null ? `${precisePercent(n)} of ${of}` : `${precisePercent(n)} of ${base.precise(y, t, cur)}`;
        },
        none: (c, totals) => base.figure(c) === null ? null : !totals?.[at] ? `no ${total} total to be a percentage of (its totals hidden)`
            : market ? `its ${total} total in the market is none or zero` : base.figure(totals[at]) === null ? `its ${total} total doesn't add up (—)` : `its ${total} total is zero`,
        aligned: true,
        signed: base.signed
    };
}

/**
 * A value as a running total in Date (Excel's "Running Total In"): each date's figure and those of every date before it
 * in the pivot's window — on the cell's row (dates across), or in its column (dates down), the other fields as they are:
 * within each venue, say, with dates › venue across. A cell spanning every date (a total across them) shows its own
 * figure: the last running value. Shown as its value is; exact in its tooltip and a copy.
 */
function runningOf(key) {
    const [v] = key.split('.'), base = VALUES[v];
    return {
        get name() { return base.title; }, mark: FORMS.running.mark,
        get title() { return `${base.title} ${FORMS.running.mark}`; },
        get description() { return `${base.description ? `${base.description} — ` : ''}a running total in Date, since the first date's open`; },
        figure: c => carried(c, key),
        shown: base.shown,
        precise: base.precise,
        named: base.named,
        aligned: base.aligned,
        signed: base.signed
    };
}

/** Each cell's totals — its row's, its column's and the grand total: the cells the statement made of them, if it did — for what a percentage's tooltip says it's of. */
function totalsOf(cells) {
    const grand = cells.get('\u0000');
    return (rowText, columnText) => ({row: cells.get(`${rowText}\u0000`), column: cells.get(`\u0000${columnText}`), total: grand});
}

export function createPivotView(tapline) {
    const views = new Map();                                     // element → its view
    const pending = new Map();                                   // grid element → the change awaited: { id, shown (the rendezvous), abort }
    const collapsedBy = new Map();                               // grid element → layout → { rows, columns (Sets of labels), pnl }
    const hovered = new Set();                                   // grid elements the pointer is over (a sort by value holds still)
    const menu = {el: null, table: null, label: null};         // a column's ⚙, open: its grid and label
    let topic = '';                                              // the pivot shown (its stats are picked out by it)
    let shownGrid = null;                                        // its view (its lags reported with the stats)
    let page = null;                                             // the page's component (its fields' drops, a column's values changed)
    let columnSettings;                                          // (read from local storage when first needed)

    document.addEventListener('pointerdown', e => {
        if (menu.el && !menu.el.contains(e.target) && !e.target.closest?.('[data-cog]')) closeMenu();
    }, true);
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape') {
            closeMenu();
            hideTip();
        }
    });
    document.addEventListener('click', e => {
        const button = e.target.closest?.('[data-copy-grid]');
        if (button) copyGrid(button);
    });

    /**
     * The grid shown, copied to the clipboard for a spreadsheet — as HTML (its headings merged as they are) and as text,
     * tab-separated: a first line naming when and in what currency; the headings; then each row, its label in a column
     * of its level's own (an instrument, then each of its clients…), its figures precise — as they are before they're rounded
     * and scaled to be shown: amounts in units of the reporting currency, a position in its asset's, trades a count, a mark price in its instrument's quote;
     * none ("—") an empty cell. As laid out: collapsed groups, their totals alone; only the values shown.
     */
    async function copyGrid(button) {
        const view = shownGrid, table = view?.target;
        if (!table?.tHead || !table.tBodies[0]) return;
        const head = [...table.tHead.rows], body = [...table.tBodies[0].rows];
        const depth = tr => Number(tr.cells[0]?.dataset.depth ?? 0);
        const labelColumns = Math.max(1, ...body.map(tr => depth(tr) + 1));
        const text = cell => cell.textContent.replace(/\s+/g, ' ').trim();
        const rows = [], taken = [];
        let width = 0;
        [...head, ...body].forEach((tr, y) => {
            const row = rows[y] = [], inBody = tr.parentElement === table.tBodies[0];
            let x = 0;
            const put = (value, cellKind, w = 1, h = 1) => {
                while (taken[y]?.[x]) x++;
                row.push({x, value, kind: cellKind, w, h});
                for (let i = 0; i < h; i++) for (let j = 0; j < w; j++) (taken[y + i] ??= [])[x + j] = true;
                x += w;
                width = Math.max(width, x);
            };
            for (const cell of tr.cells) {
                if (cell.tagName === 'TD') put(cell.dataset.figure ?? '', 'figure');
                else if (cell.classList.contains('corner')) put(text(cell), 'heading', labelColumns, cell.rowSpan);
                else if (inBody) {                                     // (a row's label: in its level's column)
                    const d = depth(tr);
                    for (let i = 0; i < labelColumns; i++) put(i === d ? text(cell) : '', 'label');
                } else put(text(cell), 'heading', cell.colSpan, cell.rowSpan);
            }
        });
        const when = new Date().toISOString().replace('T', ' ').slice(0, 19) + ' UTC';
        const caption = `Live positions, ${when}` + (view.options.currency ? ` · * Amounts in ${view.options.currency}` : '');
        const escape = s => s.replace(/[&<>"]/g, ch => ({'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;'})[ch]);
        const html = `<table><tr><td colspan="${width}">${escape(caption)}</td></tr>` + rows.map(row => '<tr>' + row.map(c => {
            const tag = c.kind === 'heading' ? 'th' : 'td',
                spans = (c.w > 1 ? ` colspan="${c.w}"` : '') + (c.h > 1 ? ` rowspan="${c.h}"` : '');
            return `<${tag}${spans}>${escape(c.value)}</${tag}>`;
        }).join('') + '</tr>').join('') + '</table>';
        const tsv = [caption, ...rows.map(row => {
            const line = Array(width).fill('');
            for (const c of row) line[c.x] = c.value.replace(/[\t\r\n]+/g, ' ');
            return line.join('\t');
        })].join('\n');
        const label = button.querySelector('span');
        try {
            if (window.ClipboardItem)
                await navigator.clipboard.write([new ClipboardItem({
                    'text/html': new Blob([html], {type: 'text/html'}),
                    'text/plain': new Blob([tsv], {type: 'text/plain'})
                })]);
            else await navigator.clipboard.writeText(tsv);
            if (label) label.textContent = 'Copied';
        } catch (e) {
            console.info(`pivot: the grid wasn't copied (${e.message})`);
            if (label) label.textContent = 'Not copied';
        }
        button.classList.add('done');
        clearTimeout(button.copied);
        button.copied = setTimeout(() => {
            if (label) label.textContent = 'Copy';
            button.classList.remove('done');
        }, 1500);
    }

    document.addEventListener('scroll', () => {
        if (menu.el) placeMenu();
        hideTip();
    }, true);   // (a menu kept by its ⚙, the page or the grid scrolled; a tooltip let go)

    // A cell's figure precisely, on hover: the page's own tooltip, not the browser's — that one waits for the pointer to
    // rest, and a figure changing under it (its title rewritten each tick) never lets it. Kept up as the figure moves.
    const tip = {el: null, cell: null};

    function showTip(cell) {
        const text = cell?.dataset.tip;
        if (!text) {
            hideTip();
            return;
        }
        if (!tip.el) {
            tip.el = document.createElement('div');
            tip.el.className = 'pivot-tip';
            tip.el.setAttribute('role', 'tooltip');
            document.body.appendChild(tip.el);
        }
        tip.cell = cell;
        tip.el.textContent = text;
        tip.el.hidden = false;
        placeTip();
    }

    /** Under its cell, right-aligned with the figure (above, if there's no room below), kept in the window. */
    function placeTip() {
        if (!tip.cell || !tip.el) return;
        const r = tip.cell.getBoundingClientRect(), t = tip.el.getBoundingClientRect();
        const left = Math.max(4, Math.min(r.right - t.width, innerWidth - t.width - 4));
        const top = r.bottom + 4 + t.height <= innerHeight - 4 ? r.bottom + 4 : r.top - t.height - 4;
        tip.el.style.left = `${Math.round(left)}px`;
        tip.el.style.top = `${Math.round(top)}px`;
    }

    function hideTip() {
        if (tip.el) tip.el.hidden = true;
        tip.cell = null;
    }

    addEventListener('resize', () => {
        if (menu.el) placeMenu();
    });

    /** A change about to be asked for: its correlation id, its rendezvous awaited (before the request), the grid marked pending. */
    function prepare(table) {
        cancel(table);                                             // (a change not yet shown: superseded by this one)
        const id = tapline.correlation(), abort = new AbortController();
        const shown = tapline.rendezvous(id, {signal: abort.signal});
        shown.catch(() => {
        });                                    // (aborted: superseded, or refused)
        pending.set(table, {id, shown, abort});
        mark(table, true);
        return id;
    }

    /** A change that won't be shown (refused, unavailable): the pivot shown stays. */
    function cancel(table, id) {
        const change = pending.get(table);
        if (!change || (id && change.id !== id)) return;
        change.abort.abort();
        pending.delete(table);
        mark(table, false);
    }

    function mark(table, on) {
        table.parentElement?.classList.toggle('pending', on);
    }

    /**
     * The note under the grid — its amounts' unit and currency ("* Amounts in thousands of USD": their headings marked *) and
     * when its days end, each a choice (the page's, its currency grey till the pivot in it is shown) — shown while a pivot
     * of the fields' is: none for a statement of your own.
     */
    function note(currency) {
        const el = document.querySelector('[data-amounts-note]');
        if (el) el.hidden = !currency;
    }

    /** What a grid has collapsed, kept by its layout (rows and columns): a new pivot of the same layout — in another currency, say — keeps it. */
    function collapsedOf(table, view) {
        const layout = JSON.stringify([view.options.rows, view.options.columns]);
        if (!collapsedBy.has(table)) collapsedBy.set(table, new Map());
        const byLayout = collapsedBy.get(table);
        if (!byLayout.has(layout)) byLayout.set(layout, {rows: new Set(), columns: new Set(), pnl: false});
        return byLayout.get(layout);
    }

    /** What the grid shown has collapsed, told to the page when it changes: its address keeps it. */
    function tellCollapsed(table, view) {
        const {rows, columns, pnl} = collapsedOf(table, view), told = JSON.stringify([[...rows], [...columns], pnl]);
        if (told === view.told) return;
        view.told = told;
        page?.invokeMethodAsync('SetCollapsed', [...rows], [...columns], pnl);
    }

    /**
     * A pivot's subscription, shown in place of the one shown — once its first answer is in front of it (the rendezvous
     * by `correlation`, if the page awaits one), never before: its replica fills meanwhile, off-screen. 'shown',
     * 'superseded' (a later change asked for meanwhile: this one let go), or 'failed: why' (it failed before it was
     * shown — its statement wouldn't run: let go, the pivot shown kept, as for a refusal).
     */
    async function attachGrid(binding, table, replica, topicName, options, correlation) {
        annotate(options.fields);
        const view = {
            options: {rows: [], columns: [], values: ['position', 'notional', 'pnl', 'balance'], sort: {}, ...options},
            state: null,
            banner: '',
            signature: '',
            shown: new Map(),
            widths: new Map(),
            fractions: [],
            fractionWidths: new Map(),
            target: null,
            replica: null,
            topic: topicName,
            lags: lags(),
            emitted: 0
        };   // (no target yet: off-screen, its replica filling)
        let frame = false, failed, answered;
        const failure = new Promise(resolve => failed = resolve);
        const answer = new Promise(resolve => answered = resolve);  // its first answer in its replica (the rendezvous follows, in order)
        view.schedule = () => {
            if (!frame) {
                frame = true;
                requestAnimationFrame(() => {
                    frame = false;
                    render(view);
                });
            }
        };
        const off = tapline.subscribe(binding,
            t => {
                view.state = t;
                view.banner = '';
                const clock = clockOf(t);                              // (an answer's lag, once: a record that brings it)
                if (clock && clock.emitted !== view.emitted) {
                    view.emitted = clock.emitted;
                    if (clock.watermark > 0) view.lags.add((clock.emitted - clock.watermark) / 1000);
                }
                if (t.seq > 0) answered();
                view.schedule();
            },
            (error, last) => {
                view.state = last ?? view.state;
                view.banner = `${utf8.decode(error.payload)} (code ${error.code})`;
                view.schedule();
                failed(`failed: ${view.banner}`);                        // (before it's shown: not swapped in)
            });
        view.off = () => {
            off();
            tapline.drop(binding.tapId);
            // Let go, it renders nothing more: a frame it scheduled before the swap would lay the table out again as its
            // own — the one swapped in left writing to cells no longer in it.
            view.target = view.replica = null;
        };

        const change = correlation ? pending.get(table) : undefined;
        if (change && change.id === correlation) {
            // Shown anyway only if its rendezvous goes missing: SWAP_TIMEOUT_MS after its first answer arrived, not after the
            // request. On a slow link (?bwlimit) the answer itself takes longer — sharing the link with the pivot shown — and
            // until it's here there is nothing to show: the pivot shown stays, with the spinner.
            const timeout = answer.then(() => new Promise(resolve => setTimeout(resolve, SWAP_TIMEOUT_MS, 'timeout')));
            const before = tapline.bytesReceived;
            const slow = setTimeout(() => console.info(`pivot: ${correlation} still awaiting its first answer after ${SWAP_TIMEOUT_MS} ms (${count(tapline.bytesReceived - before)} bytes received meanwhile)`), SWAP_TIMEOUT_MS);
            const outcome = await Promise.race([change.shown.then(() => 'rendezvous', () => 'aborted'), timeout, failure]);
            clearTimeout(slow);
            if (outcome === 'aborted' || pending.get(table) !== change) {
                view.off();
                return 'superseded';
            }
            if (outcome.startsWith('failed')) {
                view.off();
                cancel(table, correlation);
                return outcome;
            }
            if (outcome === 'timeout') console.info(`pivot: no rendezvous for ${correlation} within ${SWAP_TIMEOUT_MS} ms of its first answer — shown anyway`);
            pending.delete(table);
        } else if (correlation) {
            view.off();
            return 'superseded';
        }   // (prepared again since: a later change is awaited)

        // The swap: its first answer is in its replica (the rendezvous came after it, in order) — laid out at once, in place.
        views.get(table)?.off();
        topic = topicName;
        view.target = table;
        view.replica = replica;
        view.signature = '';
        if (!table.dataset.toggles) {                              // a toggle: a group collapsed or expanded (PnL too) — laid out again; a column's ⚙
            table.dataset.toggles = 'on';
            table.addEventListener('pointerenter', () => hovered.add(table));
            table.addEventListener('pointerleave', () => {
                hovered.delete(table);
                hideTip();
                views.get(table)?.schedule();
            });   // (a sort held meanwhile catches up)
            table.addEventListener('pointerover', e => {
                const cell = e.target.closest?.('td');
                if (cell !== tip.cell) showTip(cell);
            });
            table.addEventListener('click', e => {
                const cog = e.target.closest('[data-cog]');
                if (cog) {
                    openMenu(table, cog);
                    return;
                }
                const toggle = e.target.closest('[data-toggle]'), shown = views.get(table);
                if (!toggle || !shown) return;
                const collapsed = collapsedOf(table, shown), {toggle: kind, label: what} = toggle.dataset;
                if (kind === 'pnl') collapsed.pnl = !collapsed.pnl;
                else {
                    const set = kind === 'row' ? collapsed.rows : collapsed.columns;
                    if (!set.delete(what)) set.add(what);
                }
                shown.signature = '';
                render(shown);
                tellCollapsed(table, shown);
            });
        }
        if (!collapsedBy.has(table) && view.options.collapsed) {   // (the grid's first: what the address has collapsed — a link opens as it was left)
            const {rows = [], columns = [], pnl = false} = view.options.collapsed;
            Object.assign(collapsedOf(table, view), {rows: new Set(rows), columns: new Set(columns), pnl});
        }
        views.set(table, view);
        shownGrid = view;
        render(view);
        tellCollapsed(table, view);
        note(view.options.currency ?? '');
        mark(table, false);
        return 'shown';
    }

    function configure(table, options) {
        annotate(options.fields);
        const view = views.get(table);
        if (!view) return;
        view.options = {...view.options, ...options};
        view.signature = '';                                       // laid out again
        view.schedule();
        requestAnimationFrame(() => {
            if (menu.el && menu.table === table) placeMenu();
        });   // (a ⚙ menu open: under its ⚙, where it is now)
    }

    /** A tree of labels: each label's children (the labels one level under it), in the order its level's field asks (`compare`: by its name). */
    function tree(labels, names, compare) {
        const children = new Map([['', []]]);
        for (const text of labels)
            for (let p = text; p !== ''; p = parent(p)) {
                const up = parent(p);
                if (!children.has(up)) children.set(up, []);
                if (!children.has(p)) children.set(p, []);
                if (!children.get(up).includes(p)) children.get(up).push(p);
            }
        for (const [text, list] of children) list.sort(compare(names[levels(text)]));
        return children;
    }

    /** A tree's labels, each its place in it (depth-first, collapsed or not): the order a sort by value left them in. */
    function ranks(children) {
        const rank = new Map();
        const walk = text => {
            for (const c of children.get(text) ?? []) {
                rank.set(c, rank.size);
                walk(c);
            }
        };
        walk('');
        return rank;
    }

    function render(view) {
        const {state, options, target: table, replica} = view;
        if (!state || !replica) return;                            // (off-screen: rendered once it's swapped in)
        const cells = new Map(), rowLabels = new Set(), columnLabels = new Set();
        // What its cells carry besides their figures (Show as), each an i64 after them in the order the pivot was asked for
        // them: a fraction or a count to eight places, a running volume to the cent (brought to eight places, as volume is).
        const extras = (options.extras ?? []).map(({entry, places}, i) => ({entry, at: CELL_BYTES + 8 * i, scale: 10n ** BigInt(PLACES - places)}));
        for (const {key, row} of state.rows.values()) {
            const v = new DataView(row.buffer, row.byteOffset, row.byteLength), figure = at => {
                const x = v.getBigInt64(at, true);
                return x === NONE ? null : x;
            };
            const r = label(key, 0, ROW_BYTES), c = label(key, ROW_BYTES, COLUMN_BYTES);
            if (r === CLOCK) continue;                               // (the clock cell: none of the grid's)
            const volume = figure(40), x = {};
            for (const {entry, at, scale} of extras) {
                const f = at + 8 <= row.byteLength ? figure(at) : null;
                x[entry] = f === null ? null : f * scale;
            }
            cells.set(`${r}\u0000${c}`, {
                q: figure(0),
                n: figure(8),
                r: figure(16),
                u: figure(24),
                b: figure(32),
                v: volume === null ? null : volume * CENTS_TO_PLACES,
                t: figure(48),
                g: figure(56),
                m: figure(64),
                x
            });
            rowLabels.add(r);
            columnLabels.add(c);
        }
        // Levels: the page's — or, a statement of your own, as deep as its labels go.
        const rowDepth = options.rows.length || Math.max(0, ...[...rowLabels].map(levels));
        const columnDepth = options.columns.length || Math.max(0, ...[...columnLabels].map(levels));
        const collapsed = collapsedOf(table, view);

        // Each level in its field's order — its keys (options.sort[field]: { by, desc } each): its labels; or values, as a
        // spreadsheet's pivot sorts by one, each group by its figure in the Total column (a column's, in the grand total row),
        // the next value where they tie and its labels where they all do; a figure that's none ("—") last. Live: sorted again
        // as the figures change, at most once a second — but held while the pointer is over the grid (nothing moves under a
        // click), the order it was left in kept meanwhile (anything new after it, by label).
        const keysOf = name => options.sort[name] ?? [];
        const valuesOf = name => keysOf(name).filter(k => Object.hasOwn(VALUES, k.by));   // (any value: its figures are in every cell, shown or not)
        let resort = false;
        if ([...options.rows, ...options.columns].some(name => valuesOf(name).length)) {
            const sortKey = JSON.stringify([options.rows, options.columns, options.sort]), now = performance.now();
            resort = view.sortKey !== sortKey || (!hovered.has(table) && now - view.sortedAt >= 1000);
            if (resort) {
                view.sortKey = sortKey;
                view.sortedAt = now;
            }
        }
        const ordered = (axis, cellOf) => name => {
            const first = keysOf(name)[0], desc = first?.by === 'label' && !!first.desc, keys = valuesOf(name);
            const byLabel = (a, b) => desc ? last(b).localeCompare(last(a)) : last(a).localeCompare(last(b));
            if (!keys.length) return byLabel;
            if (!resort) {                                           // (held: the order it was left in)
                const rank = view.ranks[axis];
                return (a, b) => {
                    const x = rank.get(a) ?? null, y = rank.get(b) ?? null;
                    return x === null || y === null ? (x === y ? byLabel(a, b) : x === null ? 1 : -1) : x - y;
                };
            }
            const figures = new Map();                               // (each group's, looked up once)
            const of = t => {
                if (!figures.has(t)) {
                    const c = cellOf(t);
                    figures.set(t, keys.map(k => c ? VALUES[k.by].figure(c) : null));
                }
                return figures.get(t);
            };
            return (a, b) => {
                const x = of(a), y = of(b);
                for (let i = 0; i < keys.length; i++) {
                    if (x[i] === y[i]) continue;                         // (a tie: the next key)
                    if (x[i] === null) return 1;
                    if (y[i] === null) return -1;
                    return (x[i] < y[i]) === !keys[i].desc ? -1 : 1;
                }
                return byLabel(a, b);
            };
        };
        const rowTree = tree([...rowLabels].filter(r => levels(r) === rowDepth), options.rows, ordered('rows', r => cells.get(`${r}\u0000`)));
        const columnTree = tree([...columnLabels].filter(c => levels(c) === columnDepth), options.columns, ordered('columns', c => cells.get(`\u0000${c}`)));
        if (resort) view.ranks = {rows: ranks(rowTree), columns: ranks(columnTree)};

        // Which totals the statement made: a level's, if a label stops there — the grand total's, the empty label. (Its
        // fields' Σ: each a GROUP BY in it, or not.) A group without one has nothing to collapse to: open, its label alone.
        const rowTotals = new Set([...rowLabels].map(levels).filter(n => n < rowDepth));
        const columnTotals = new Set([...columnLabels].map(levels).filter(n => n < columnDepth));

        // What's visible: rows depth-first — a group with its total (collapsed: its total alone) — then the grand total;
        // columns each a leaf of what's expanded, a collapsed group (its total), or an expanded group's total (after its
        // columns — or, total first, before them), and the grand total's (last, or first: anchored).
        const rows = [];
        const walkRows = text => {
            for (const r of rowTree.get(text) ?? []) {
                const group = levels(r) < rowDepth, totalled = group && rowTotals.has(levels(r)),
                    open = group && !(totalled && collapsed.rows.has(r));
                rows.push({text: r, depth: levels(r) - 1, group, totalled, open});
                if (open) walkRows(r);
            }
        };
        walkRows('');
        if (rowTotals.has(0)) {                                    // (the grand total's row: last, or first — pinned below the headings)
            const grand = {text: '', depth: 0, group: false, totalled: true, open: false, grand: true};
            if (options.totalRowFirst) rows.unshift(grand); else rows.push(grand);
        }
        const columns = [];
        const walkColumns = text => {
            for (const c of columnTree.get(text) ?? []) {
                if (levels(c) === columnDepth) {
                    columns.push({text: c, kind: 'leaf'});
                    continue;
                }
                const totalled = columnTotals.has(levels(c));
                if (totalled && collapsed.columns.has(c)) {
                    columns.push({text: c, kind: 'collapsed'});
                    continue;
                }
                if (totalled && options.totalFirst) columns.push({text: c, kind: 'total'});
                walkColumns(c);
                if (totalled && !options.totalFirst) columns.push({text: c, kind: 'total'});
            }
        };
        walkColumns('');
        if (columnDepth === 0) columns.push({text: '', kind: 'leaf'});   // (no columns: the one — not a total)
        else if (columnTotals.has(0)) {
            const grand = {text: '', kind: 'total', grand: true};
            if (options.totalFirst) columns.unshift(grand); else columns.push(grand);
        }
        // Each column's values: those asked for (Values: every column's default), but what its heading's ⚙ shows or hides
        // there — chosen for it alone, or for its level (every venue, every venue's Total…): notional in the Total column
        // alone, say. Never none: a column showing none shows those asked for.
        const config = columnConfig(options);
        for (const c of columns) {
            const shown = shownIn(config, c.text, options.values, options.order);
            c.values = shown.length ? shown : options.values;
        }
        const pnl = columns.some(c => c.values.includes('pnl')) && formsOf(options, 'pnl').includes('value'), pnlOpen = pnl && !collapsed.pnl;
        const expand = keys => keys.flatMap(v => formsOf(options, v).flatMap(form => form !== 'value' ? [`${v}.${form}`]
            : v === 'pnl' ? (pnlOpen ? ['realized', 'unrealized'] : ['pnl']) : [v]));   // (each value's forms: its own, then its percentages)
        const values = expand((options.order ?? FIELDS).filter(v => columns.some(c => c.values.includes(v))));
        const slots = columns.flatMap(c => expand(c.values).map(value => ({text: c.text, value})));
        const signature = JSON.stringify([rows.map(r => [r.text, r.open]), columns, values, rowDepth, columnDepth, collapsed.columns.size, !!options.totalFirst, !!options.totalRowFirst]);
        if (signature !== view.signature) build(view, table, rows, columns, expand, collapsed, columnTotals, columnDepth, pnlOpen, signature);
        const contextOf = totalsOf(cells);                       // (each cell's totals: what its percentages' tooltips say they're of)
        view.grid.forEach((tds, i) => tds.forEach((td, j) => {
            const c = cells.get(`${rows[i].text}\u0000${slots[j].text}`);
            show(view, td, c, slots[j].value, j, contextOf(rows[i].text, slots[j].text));
        }));
        align(view, table, slots);
        watermark(view, slots);
        pin(view, table);
        table.classList.toggle('degraded', !!(state.degraded || view.banner));   // (a class, not an inline opacity: pending dims it too)
        table.title = view.banner;
        // This page's copy: said only when something's wrong with it — interrupted, or not the router's (resyncing).
        replica.textContent = view.banner ? `⚠ ${view.banner}` : state.verified ? '' : `✗ this page's copy of ${view.topic} isn't the router's (checksum ${hex(state.checksum)}): resyncing`;
        replica.hidden = !replica.textContent;
    }

    /**
     * A toggle's chevron: a row group's points right, collapsed, and down, open (it opens downwards); a column group's or
     * PnL's right, collapsed, and left, open (they open sideways — and a sideways chevron reads as no menu).
     */
    function chevron(kind, text, open) {
        const b = document.createElement('button');
        b.className = open ? 'toggle open' : 'toggle';
        b.dataset.toggle = kind;
        b.dataset.label = text;
        b.innerHTML = CHEVRON;
        b.title = open ? 'collapse' : 'expand';
        b.setAttribute('aria-expanded', open);
        return b;
    }

    /**
     * The grid, laid out: a header row for each level of columns (a group spanning what's under it, with its chevron; a
     * collapsed group, its total, spanning down; the grand total's spanning all — each heading over a column's values with
     * a ⚙ on hover), the values row — each column's (PnL with its chevron) — and, PnL expanded, realized and unrealized under
     * it; then a row for each visible row (indented, a group's chevron), and the grand total's.
     */
    function build(view, table, rows, columns, expand, collapsed, columnTotals, columnDepth, pnlOpen, signature) {
        if (tip.cell && table.contains(tip.cell)) hideTip();        // (its cell about to go)
        table.innerHTML = '';
        view.pinnedAt = '';
        const width = c => expand(c.values).length, grand = columns.find(c => c.grand),
            grandWidth = grand ? width(grand) : 0;
        // The grand total's cells, first: pinned beside the row labels (where they scroll to), each by its value column — the
        // last of them edged, the anchor's rule.
        const pinning = !!view.options.totalFirst;
        const pinned = (cell, k, span) => {
            if (!pinning || k < 0) return cell;
            cell.classList.add('pin');
            cell.dataset.pin = k;
            if (k + span === grandWidth) cell.classList.add('edge');
            return cell;
        };
        // A heading over a column's values: its ⚙ (shown on hover), choosing them.
        const cog = (label, content) => {
            const b = document.createElement('button');
            b.className = 'column-cog';
            b.dataset.cog = label;
            b.title = 'the values this column shows';
            b.innerHTML = COG;
            if (menu.table === table && menu.label === label) b.classList.add('open');
            return [...content, b];
        };
        const head = table.createTHead(), body = table.createTBody();
        const valueRows = pnlOpen ? 2 : 1, headerRows = columnDepth + valueRows;
        const named = title => view.options.currency ? [title, Object.assign(document.createElement('span'), {
            className: 'amount-mark',
            textContent: '*'
        })] : [title];   // (a heading over bare amounts: the note under the grid names their currency — its * grey, as the note is)
        const described = (cell, v) => {
            if (spec(v).description) cell.title = spec(v).description;
            return cell;
        };   // (a value's heading: what it is, as its chip says — a percentage's, of which total)
        const th = (tr, content, cls, col = 1, row = 1) => {
            const cell = document.createElement('th');
            if (cls) cell.className = cls;
            if (col > 1) cell.colSpan = col;
            if (row > 1) cell.rowSpan = row;
            if (lines.indexOf(tr) + row === headerRows) cell.classList.add('last');   // (down to the headings' bottom: their rule, pinned with them)
            cell.append(...content);
            tr.appendChild(cell);
            return cell;
        };
        const lines = Array.from({length: headerRows}, () => head.insertRow());
        th(lines[0], [view.options.rows.map(n => view.options.fields?.[n]?.title ?? title(n)).join(' › ')], 'corner', 1, headerRows);   // (its row fields, as their chips show them)
        // Each level of columns: consecutive columns under one group merge into its header — its total's column too. A
        // total's column is headed where its group's levels end, spanning down (the grand total's, from the top); a
        // collapsed group, where it is. A group whose total the statement didn't make can't collapse: no chevron.
        for (let level = 0; level < columnDepth; level++)
            for (let i = 0; i < columns.length;) {
                const {text: c, kind, grand} = columns[i], at = levels(c);
                if (kind === 'total' && at === level) {
                    pinned(th(lines[level], cog(c, ['Total']), grand ? 'grand' : 'subtotal', width(columns[i]), columnDepth - level), grand ? 0 : -1, grandWidth);
                    i++;
                    continue;
                }
                if (at <= level) {
                    i++;
                    continue;
                }                    // (headed above, spanning down)
                const group = prefix(c, level + 1);
                let j = i;
                while (j < columns.length && levels(columns[j].text) > level && prefix(columns[j].text, level + 1) === group) j++;
                const isGroup = level + 1 < columnDepth, collapsible = isGroup && columnTotals.has(level + 1),
                    closed = collapsible && collapsed.columns.has(group);
                const content = collapsible ? [chevron('column', group, !closed), ' ', last(group)] : [last(group)];
                th(lines[level], isGroup && !closed ? content : cog(group, content), isGroup ? (closed ? 'group total' : 'group') : 'leaf',
                    columns.slice(i, j).reduce((n, c) => n + width(c), 0), closed ? columnDepth - level : 1);
                i = j;
            }
        // The values row — PnL with its chevron — and PnL's two, expanded; each value's percentages after it, its heading
        // marked with its total. (Under the grand total, first: pinned, each by the first value column it heads.)
        for (const c of columns) {
            let k = 0;
            const at = (cell, span) => {
                if (c.grand) pinned(cell, k, span);
                k += span;
            };
            const value = c.kind === 'leaf' ? 'value' : 'value total';   // (a total's: checked, as its cells are)
            for (const v of c.values) for (const form of formsOf(view.options, v)) {
                if (form !== 'value') {
                    const key = `${v}.${form}`, s = spec(key), mark = Object.assign(document.createElement('span'), {className: 'form-mark', textContent: ` ${s.mark}`});
                    at(described(th(lines[columnDepth], [...(s.named ? named(s.name) : [s.name]), mark], value, 1, valueRows), key), 1);
                } else if (v === 'pnl') {
                    const head = described(th(lines[columnDepth], [chevron('pnl', '', pnlOpen), ' ', ...named(VALUES.pnl.title)], value, pnlOpen ? 2 : 1), 'pnl');   // (its figures bare)
                    if (pnlOpen) {
                        if (c.grand) pinned(head, k, 2);
                        at(described(th(lines[columnDepth + 1], [VALUES.realized.title], value), 'realized'), 1);   // (its two: no chips of their own, their tooltips here)
                        at(described(th(lines[columnDepth + 1], [VALUES.unrealized.title], value), 'unrealized'), 1);
                    } else at(head, 1);
                } else at(described(th(lines[columnDepth], VALUES[v].named ? named(VALUES[v].title) : [VALUES[v].title], value, 1, valueRows), v), 1);
            }
        }
        view.grid = [];
        view.shown = new Map();
        view.fractions = [];                                       // (each value column's widest fraction, this layout)
        for (const r of rows) {
            const tr = body.insertRow(), name = document.createElement('th');
            tr.className = r.grand ? (rows[0] === r ? 'grand first' : 'grand') : r.group ? (r.totalled ? 'subtotal' : 'bare') : '';   // (bare: a group with no total)
            name.style.paddingLeft = `calc(8px + ${r.depth} * var(--level-indent))`;   // (each level further in: see the grid's style)
            name.dataset.depth = r.depth;                             // (its level: a copy puts it in a column of its own)
            if (r.group && r.totalled) name.append(chevron('row', r.text, r.open), ' ');
            name.append(r.grand ? 'Total' : last(r.text));
            if (r.grand) {                                           // (its ⚙, shown on hover: where it goes, first or last)
                const b = document.createElement('button');
                Object.assign(b, {className: 'column-cog', title: "where the Total row goes", innerHTML: COG});
                b.dataset.cog = ROW_MENU;
                if (menu.table === table && menu.label === ROW_MENU) b.classList.add('open');
                name.append(b);
            }
            tr.appendChild(name);
            view.grid.push(columns.flatMap(c => expand(c.values).map((v, k) => {
                const td = tr.insertCell();
                const kind = v.replace('.', ' of-');                     // (a percentage's: its value's, and its total's)
                td.className = c.kind === 'leaf' ? `cell ${kind}` : `cell ${kind} total`;   // (a total column's: a collapsed group's too)
                if (c.grand) pinned(td, k, 1);
                return td;
            })));
        }
        view.signature = signature;
    }

    /**
     * Each value column at least as wide as it's been, for this pivot: its figures change width as they move (a position
     * an order of magnitude bigger, say), and a column that narrowed again would shift the grid under the pointer. The
     * widest each has been, a min-width on its first row's cell: what a column can't go under, even in a grid wider than
     * the page (a <col>'s width is only preferred there). A wider figure still widens it: its new watermark.
     */
    /**
     * Each value column's figures lined up at the point: their fractions all as wide as its widest has been (in ch: the
     * figures' font is monospaced), so a whole number ends where the points are.
     */
    function align(view, table, slots) {
        slots.forEach((slot, j) => {
            const key = `${slot.text}\u0000${slot.value}`,
                width = Math.max(view.fractions[j] ?? 0, view.fractionWidths.get(key) ?? 0);
            view.fractionWidths.set(key, width);                      // (the widest it's been: kept as the grid's laid out again)
            if (table.style.getPropertyValue(`--f${j}`) !== `${width}ch`) table.style.setProperty(`--f${j}`, `${width}ch`);
        });
    }

    function watermark(view, slots) {
        view.grid[0]?.forEach((td, j) => {
            const key = `${slots[j].text}\u0000${slots[j].value}`;
            const mark = Math.max(Math.ceil(td.getBoundingClientRect().width), view.widths.get(key) ?? 0),
                min = `${mark}px`;
            view.widths.set(key, mark);
            if (td.style.minWidth !== min) {
                td.style.boxSizing = 'border-box';
                td.style.minWidth = min;
            }
        });
    }

    /**
     * What stays in view as the grid scrolls: the headings, pinned at the top, each row of them below the ones above it;
     * the grand total's row, first, pinned below them; the grand total's columns, first, pinned beside the row labels,
     * each at the row labels' width and the widths of the pinned columns before it. Measured, and set again only when
     * they change (a figure's width can).
     */
    function pin(view, table) {
        const row = table.tBodies[0]?.rows[0];
        if (!row) return;
        const heights = [...table.tHead.rows].map(r => r.getBoundingClientRect().height);
        const tops = heights.map((_, i) => heights.slice(0, i).reduce((a, b) => a + b, 0)),
            below = heights.reduce((a, b) => a + b, 0);
        const widths = view.options.totalFirst ? [row.cells[0], ...row.querySelectorAll('td.pin')].map(cell => cell.getBoundingClientRect().width) : [];
        const lefts = widths.slice(1).map((_, k) => widths.slice(0, k + 1).reduce((a, b) => a + b, 0));
        const key = `${tops} ${below} ${lefts}`;
        if (key === view.pinnedAt) return;
        view.pinnedAt = key;
        // (whole pixels, each a fraction over the one before: a fractional edge, antialiased, lets what scrolls beneath show through)
        [...table.tHead.rows].forEach((tr, i) => {
            for (const cell of tr.cells) cell.style.top = `${Math.floor(tops[i])}px`;
        });
        for (const cell of table.querySelectorAll('tr.grand.first > *')) cell.style.top = `${Math.floor(below)}px`;
        for (const cell of table.querySelectorAll('[data-pin]')) cell.style.left = `${Math.floor(lefts[+cell.dataset.pin])}px`;
    }

    /**
     * A cell's figure, if what's shown of it changed (flashing it: up or down, as the shown value moved) — true if it did.
     * Its tooltip, the figure precisely, kept up whether or not. None: "—", it doesn't add up across what the cell spans.
     */
    function show(view, td, c, value, j, totals) {
        const s = spec(value), figure = c ? s.figure(c, totals) : undefined, currency = view.options.currency;
        const exact = typeof figure === 'bigint' ? exactly(figure) : '';   // (what a copy takes: the figure before it's rounded to be shown)
        if (td.dataset.figure !== exact) td.dataset.figure = exact;
        const asked = c !== undefined && figure === undefined;   // (asked for, not carried yet: the pivot carrying it is on its way)
        const shown = c === undefined ? {text: td.closest('tr.bare') ? '' : '·'} : asked ? {text: '…'} : figure === null ? {text: '—'} : s.shown(figure, c, currency, view.options);
        const title = asked ? 'asked for: the pivot that carries it is on its way'
            : figure === null ? s.none?.(c, totals) ?? 'doesn\'t add up across what this cell spans (assets, or days)'
            : typeof figure === 'bigint' ? `${s.title}: ${s.precise(figure, c, currency, totals)}${shown.text === '' && figure !== 0n ? ' (worth less than half a dollar)' : ''}`
                + (value === 'pnl' && c.u === null ? ' — realized only: unrealized doesn\'t add up across days (it\'s each day\'s close)' : '') : '';
        if (td.dataset.tip !== title) {                            // (a figure can change while what's shown of it doesn't)
            td.dataset.tip = title;
            if (tip.cell === td) showTip(td);                        // (hovered: its tooltip kept up)
        }
        const was = view.shown.get(td);
        if (was && was.text === shown.text) return false;
        view.shown.set(td, shown);
        td.classList.toggle('empty', c === undefined);
        td.classList.toggle('none', figure === null || asked);
        td.classList.toggle('pos', s.signed && shown.value > 0);
        td.classList.toggle('neg', s.signed && shown.value < 0);
        if (s.aligned) {                                        // an amount: its whole and its fraction, the points in line
            const [whole, fraction] = parts(shown.text), f = document.createElement('span');
            f.className = 'f';
            f.style.minWidth = `var(--f${j}, 0)`;                     // (its column's fraction width)
            f.textContent = fraction;
            td.replaceChildren(whole, f);
            if (fraction.length > (view.fractions[j] ?? 0)) view.fractions[j] = fraction.length;
        } else td.textContent = leadless(shown.text);              // (a position, in its asset's units: not lined up, +.05; a count)
        if (!was || was.value === undefined || shown.value === undefined) return false;   // (the first paint, or from or to none: no flash)
        td.classList.remove('up', 'down');
        void td.offsetWidth;                                       // (restarts the flash)
        td.classList.add(shown.value < was.value ? 'down' : 'up');
        return true;
    }

    /** The provider's stats (published directly): the brokerage, and the pivot shown — its subscribers, checksum, times. */
    const plural = (n, noun) => `${count(n)} ${noun}${n === 1 ? '' : 's'}`;
    const mb = bytes => `${(bytes / 1048576).toFixed(bytes < 10485760 ? 1 : 0)} MB`;

    function attachStats(binding, el) {
        views.get(el)?.off();
        const off = tapline.subscribe(binding, bytes => {
            const s = JSON.parse(utf8.decode(bytes));
            if (s.error || s.starting) {
                el.textContent = s.error ?? s.starting;
                return;
            }
            for (const [currency, rate] of Object.entries(s.usdRates ?? {})) usdRates.set(currency, rate);   // (what figures are worth, for rounding them)
            const mine = s.topics.find(t => t.name === topic);
            // Its vital statistics: the brokerage (its pace a link: ?trades in this page's address sets it), ChalkQL, and the
            // pivots — the one shown's run and cells.
            const pace = document.createElement('a'), address = new URL(location.href);
            address.searchParams.set('trades', s.tradesPerDay);
            Object.assign(pace, {
                href: address.href,
                target: '_top',
                textContent: `${count(s.tradesPerDay)} a day`,
                title: "the brokerage's pace, every page's: ?trades in this page's address sets it (up to 100M a day)"
            });
            el.replaceChildren(`${count(s.trades)} trades, ${count(s.clients)} clients, ${count(s.tradesPerMinute)} executions per minute (`, pace,
                `) · ChalkQL holds ${count(s.rows)} rows, refresh ${s.refreshMs.toFixed(1)} ms · ` +
                `${plural(s.topics.filter(t => t.live).length, 'pivot')} running (${s.topics.length} held), ${mb(s.memoryReserved)} of ${mb(s.memoryBudget)} reserved, ` +
                `${s.pivotMs.toFixed(1)} of ${s.pivotBudgetMs} ms a second` +
                (mine ? `, pivot ${mine.queryMs.toFixed(1)} ms, ${count(mine.cells)} cells` + (mine.error ? ` · ⚠ ${mine.error}` : '') : ''));
            answerLag(mine ? shownGrid?.lags : null);
            refusal(mine);
        }, error => {
            el.textContent = utf8.decode(error.payload);
        });
        views.set(el, {
            off: () => {
                off();
                tapline.drop(binding.tapId);
            }
        });
    }

    /**
     * The house's drawdown (published directly, as its tables change: at most each second) — the provider's, its figures
     * the statements' (none derived here but the meter's width): its equity and PnL on its capital; its drawdown from its
     * high over the hour before; the trigger (the seven days' 5th percentile of those drawdowns, less the haircut); how
     * close it is — a meter, the trigger at its end; the breach now (confirmed after five minutes on end); and the hour's
     * episodes, latest first.
     */
    const dollars = x => `${x < 0 ? '−' : ''}$${format('dollars', {maximumFractionDigits: 0}).format(Math.abs(x))}`;
    const clock = iso => new Date(iso).toLocaleTimeString('en-GB', {hour: '2-digit', minute: '2-digit', second: '2-digit'});
    const lasted = seconds => `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`;

    function attachDrawdown(binding, el) {
        views.get(el)?.off();
        const node = (tag, className, text, title) => Object.assign(document.createElement(tag), {className, textContent: text ?? '', title: title ?? ''});
        const off = tapline.subscribe(binding, bytes => {
            const d = JSON.parse(utf8.decode(bytes));
            const t = d.threshold, b = d.breach;
            const state = !t ? ['pending', 'Threshold pending', `the ${d.windowDays}-day threshold is being computed`]
                : !b ? ['clear', 'Clear', `the drawdown is above the trigger`]
                    : b.confirmed ? ['alert', `Alert · since ${clock(b.since)} (${lasted(b.seconds)})`, `breached for ${d.confirmMinutes} minutes or more on end: confirmed`]
                        : ['breached', `Breached ${lasted(b.seconds)} of ${d.confirmMinutes}:00`, `below the trigger since ${clock(b.since)}: confirmed after ${d.confirmMinutes} minutes on end`];
            el.dataset.state = state[0];
            const head = node('p', 'drawdown-head');
            head.append(node('strong', '', 'House drawdown'), ' ', node('span', 'drawdown-state', state[1], state[2]));
            const figures = node('p', 'drawdown-figures');
            figures.append(
                node('span', '', `equity ${dollars(d.equity)}`, `its cash at each currency's rate, and what its open positions — its side of its clients', and its hedges — would settle for at the marks, as of ${clock(d.asOf)}`),
                ` (PnL ${dollars(d.pnl)} on ${dollars(d.capital)} capital) · `,
                node('span', '', `drawdown ${dollars(d.drawdown)}`, `from its high over the ${d.horizonMinutes} minutes before: ${dollars(d.peak)}`),
                t ? ` · trigger ${dollars(t.trigger)}` : '',
                t ? node('span', 'muted', ` (5th percentile ${dollars(t.p05)}${t.haircut ? `, less ${dollars(t.haircut)}` : ''})`, `of ${count(t.points)} drawdowns, every ${d.stepSeconds} s from ${new Date(t.since).toLocaleString('en-GB')} to ${new Date(t.until).toLocaleString('en-GB')} (computed in ${t.ms} ms)`) : '',
                d.headroom != null ? ` · headroom ${dollars(d.headroom)}` : '');
            const meter = node('div', 'drawdown-meter', '', t ? `the drawdown as a fraction of the trigger: ${(100 * (d.proximity ?? 0)).toFixed(0)}%` : '');
            const fill = node('span', 'fill');
            fill.style.width = `${Math.min(100, Math.max(0, 100 * (d.proximity ?? 0)) / 1.25)}%`;   // (the trigger at 80% of the meter: beyond it shown)
            meter.append(fill, node('span', 'trigger'));
            const episodes = node('p', 'drawdown-episodes');
            episodes.append(`Episodes in the last ${d.liveMinutes} minutes: `);
            if (d.episodes.length === 0) episodes.append('none');
            d.episodes.forEach((e, i) => {
                if (i) episodes.append(' · ');
                episodes.append(node('span', e.confirmed ? 'confirmed' : '', `${clock(e.started)}–${clock(e.last)}, worst ${dollars(e.worst)}${e.confirmed ? ' (confirmed)' : ''}`));
            });
            episodes.append(node('span', 'muted', ` · ${d.ms} ms`, `how long the live view's last update took (its statement kept ${d.maintained})`));
            el.replaceChildren(head, meter, figures, episodes);
            el.hidden = false;
        }, error => {
            el.textContent = utf8.decode(error.payload);
        });
        views.set(el, {
            off: () => {
                off();
                tapline.drop(binding.tapId);
            }
        });
    }

    /**
     * The IVM switch (under the grid), the pivot shown asked to be maintained incrementally and ChalkQL refused (UnsupportedInView):
     * " · recomputed" beside it, its track amber, ChalkQL's reason its tooltip. The switch stays as asked: other pivots may
     * be maintained.
     */
    function refusal(mine) {
        const note = document.querySelector('[data-maintenance-note]');
        if (!note) return;
        const why = mine?.refusal ?? null;
        note.hidden = !why;
        note.title = why ? `ChalkQL can't maintain this pivot incrementally, so it's recomputed — ${why}` : '';
    }

    /** The pivot shown's steady-state sweep time, under the vital statistics (each second): its answers' clock cells' (its first, its view made, has none), earliest event to publication to Tapline, p50 and p99.5. */
    function answerLag(lags) {
        const el = document.querySelector('[data-answer-lag]');
        if (el) el.textContent = lags?.count ? ` · steady-state sweep time (earliest event → publication to Tapline): p50 ${ms(lags.at(0.5))}, p99.5 ${ms(lags.at(0.995))}` : '';
    }

    function detach(...elements) {
        for (const el of elements) {
            cancel(el);
            views.get(el)?.off();
            views.delete(el);
        }
    }

    /**
     * The values each column shows, chosen at its heading's ⚙ — this browser's, kept by column layout (the column fields,
     * in order): for a level (every venue; every venue's Total) or one column (by its label; the grand total's, ''), each
     * value shown (true) or hidden (false) there, or — not set — as the level's, then as Values.
     */
    function columnConfig(options) {
        if (!columnSettings) {
            try {
                columnSettings = JSON.parse(loadSetting(COLUMN_SETTING));
            } catch {
            }
            if (typeof columnSettings !== 'object' || !columnSettings) columnSettings = {};
        }
        return columnSettings[options.columns.join(' › ')] ??= {levels: {}, columns: {}};
    }

    const shownIn = (config, label, values, order = FIELDS) => {
        const own = config.columns[label] ?? {}, level = config.levels[levels(label)] ?? {};
        return order.filter(v => own[v] ?? level[v] ?? values.includes(v));
    };

    /** Whether a column layout's ⚙ shows a value — at a level, or a column — so the statement must answer it (the page asks). */
    function columnsShow(columns, value) {
        const config = columnConfig({columns});
        return [...Object.values(config.levels), ...Object.values(config.columns)].some(shown => shown[value] === true);
    }

    function saveColumns() {
        const kept = Object.fromEntries(Object.entries(columnSettings).filter(([, c]) => Object.keys(c.levels).length || Object.keys(c.columns).length));
        saveSetting(COLUMN_SETTING, JSON.stringify(kept));
    }

    /** A column's ⚙: every value, as chips (on: shown there), for every column of its level or it alone; a reset. Its ⚙ again closes it. */
    function openMenu(table, button) {
        const again = menu.table === table && menu.label === button.dataset.cog;
        closeMenu();
        if (again || !views.get(table)) return;
        Object.assign(menu, {table, label: button.dataset.cog, el: document.createElement('div')});
        menu.el.className = 'popup column-menu';
        document.body.appendChild(menu.el);
        button.classList.add('open');
        drawMenu();
    }

    /** A ⚙ menu's choice of where a grand total goes — first (pinned) or last: the page's, which it's told. */
    function firstToggle(text, on, method, hint) {
        const box = document.createElement('div'), label = document.createElement('label'),
            input = document.createElement('input'), note = document.createElement('p');
        box.className = 'first';
        Object.assign(input, {type: 'checkbox', checked: !!on});
        input.onchange = () => page?.invokeMethodAsync(method, input.checked);
        label.append(input, ` ${text}`);
        Object.assign(note, {className: 'hint', textContent: hint});
        box.append(label, note);
        return box;
    }

    /** The menu under its ⚙ (laid out again, the grid has a new one) — closed if its column's gone. */
    function placeMenu() {
        const cog = [...(menu.table?.querySelectorAll('.column-cog') ?? [])].find(b => b.dataset.cog === menu.label);
        if (!cog) {
            closeMenu();
            return;
        }
        const r = cog.getBoundingClientRect();
        menu.el.style.left = `${Math.max(8, Math.min(r.left - 10, innerWidth - menu.el.offsetWidth - 8))}px`;
        menu.el.style.top = `${r.bottom + 6}px`;
    }

    function closeMenu() {
        menu.el?.remove();
        menu.table?.querySelector('.column-cog.open')?.classList.remove('open');
        Object.assign(menu, {el: null, table: null, label: null});
    }

    function drawMenu() {
        const view = views.get(menu.table);
        if (!view) {
            closeMenu();
            return;
        }
        if (menu.label === ROW_MENU) {                             // the Total row's: where it goes
            menu.el.replaceChildren(firstToggle('Total row first', view.options.totalRowFirst, 'SetTotalRowFirst',
                "Pinned below the column headings as the grid scrolls down. Each group's total is its first row already. Kept in this browser."));
            placeMenu();
            return;
        }
        const {options} = view, config = columnConfig(options), label = menu.label, level = levels(label),
            depth = options.columns.length;
        // Its level: every leaf (every venue — or, under venues, every counter), every group's total at it, or the grand total (one).
        const name = level === 0 ? 'Total' : level === depth ? label : `${label}'s Total`;
        const every = level === 0 ? null : level === depth ? `every ${options.columns[depth - 1]}` : `every ${options.columns[level - 1]}'s Total`;
        const alone = level === 0 || label in config.columns, own = config.columns[label] ?? {},
            levelSet = config.levels[level] ?? {};
        const shown = shownIn(config, label, options.values, options.order), asked = v => options.values.includes(v);
        const apply = change => {
            change();
            saveColumns();
            view.signature = '';
            render(view);
            drawMenu();
            page?.invokeMethodAsync('ColumnValuesChanged');
        };   // (a value asked for now, or no longer: the page decides)
        const atLevel = () => Object.keys(config.columns).filter(l => levels(l) === level);
        // A value shown or hidden: for this column (as its level's: not set) or every column of its level (as Values: not set;
        // its columns' own choice of it let go — they're all this now).
        const show = (v, on) => apply(() => {
            if (alone) {
                const map = config.columns[label] ??= {};
                if (on === (levelSet[v] ?? asked(v))) delete map[v]; else map[v] = on;
            } else {
                for (const l of atLevel()) delete config.columns[l][v];
                const map = config.levels[level] ??= {};
                if (on === asked(v)) delete map[v]; else map[v] = on;
                if (!Object.keys(map).length) delete config.levels[level];
            }
        });
        // The scope: this column alone (as it is: its level's, till changed), or every column of its level (as this one is).
        const scope = to => apply(() => {
            if (to === 'column') {
                config.columns[label] = {};
                return;
            }
            const map = Object.fromEntries(FIELDS.filter(v => shown.includes(v) !== asked(v)).map(v => [v, shown.includes(v)]));
            for (const l of atLevel()) delete config.columns[l];
            if (Object.keys(map).length) config.levels[level] = map; else delete config.levels[level];
        });
        // Reset: a column of its own back to its level's; a level (its columns' own too), or the grand total, back to Values.
        const reset = () => apply(() => {
            if (alone) {
                delete config.columns[label];
                return;
            }
            for (const l of atLevel()) delete config.columns[l];
            delete config.levels[level];
        });
        const el = (tag, cls, text) => {
            const e = document.createElement(tag);
            if (cls) e.className = cls;
            if (text) e.textContent = text;
            return e;
        };
        menu.el.replaceChildren();
        menu.el.append(el('p', 'menu-title', `${name} shows`));
        const chips = el('div', 'chips');
        for (const v of options.order ?? FIELDS) {                 // (in the Values area's order)
            const on = shown.includes(v), chip = el('button', `field value ${on ? 'on' : 'off'}`, VALUES[v].title);
            chip.disabled = on && shown.length === 1;               // (never none)
            chip.title = (VALUES[v].description ? `${VALUES[v].description} — ` : '') + (chip.disabled ? 'the only value shown here' : `${on ? 'hide' : 'show'} it here${on === asked(v) ? '' : ` (Values: ${asked(v) ? 'shown' : 'hidden'})`}`);
            chip.onclick = () => show(v, !on);
            chips.append(chip);
        }
        menu.el.append(chips);
        if (every) {
            const scopes = el('div', 'scope');
            for (const [value, text] of [['level', every], ['column', `${label} only`]]) {
                const row = el('label'), radio = el('input');
                Object.assign(radio, {type: 'radio', name: 'column-scope', checked: (value === 'column') === alone});
                radio.onchange = () => scope(value);
                row.append(radio, ` ${text}`);
                scopes.append(row);
            }
            menu.el.append(scopes);
        }
        if (level === 0)                                           // the Total column's: where it goes too
            menu.el.append(firstToggle('Total column first', options.totalFirst, 'SetTotalFirst',
                "Pinned beside the row labels as the grid scrolls across; each group's total first too. Kept in this browser."));
        const back = el('button', 'link', alone && every ? `Reset to ${every}` : 'Reset to Values');
        back.disabled = alone ? !every && !Object.keys(own).length : !Object.keys(levelSet).length && !atLevel().length;
        back.onclick = reset;
        menu.el.append(back);
        placeMenu();
    }

    /** This browser's settings (its local storage): none if it keeps none, or won't. */
    function loadSetting(key) {
        try {
            return localStorage.getItem(key);
        } catch {
            return null;
        }
    }

    function saveSetting(key, value) {
        try {
            localStorage.setItem(key, value);
        } catch {
        }
    }

    /** The page's annotations — each field's and value's label and tooltip, their [Display] — over the grid's own. */
    function annotate(fields) {
        for (const [key, field] of Object.entries(fields ?? {}))
            if (VALUES[key]) Object.assign(VALUES[key], {title: field.title, description: field.description});
    }

    /** The address kept to the layout shown: replaced in place — no navigation, the history untouched (its state kept). */
    function replaceAddress(url) {
        if (url !== location.href) history.replaceState(history.state, '', url);
    }

    /**
     * The areas' drops, placed by where a field lands: before the field whose left half it's over (or one in a line below
     * it), after the last if it's past every one — left of the first, the area's name included, makes it first. A marker
     * shows where as it's dragged. The page is told the field to go before (null: the end); the filters keep no order.
     * A value moves among the values alone (their order, every column's), and a field never lands among them.
     */
    function attachAreas(element, component) {
        page = component;                                          // (told when a column's ⚙ changes what it shows)
        // A chip's popup — a field's sort or filter, a value's Show as — opens under it, from its left: nudged left as it
        // opens, as far as it would run past the page's right (a chip at the right of its area, say), so nothing scrolls.
        new MutationObserver(records => {
            for (const record of records)
                for (const node of record.addedNodes) {
                    if (node.nodeType !== Node.ELEMENT_NODE) continue;
                    for (const popup of node.matches('.popup') ? [node] : node.querySelectorAll('.popup')) {
                        const over = popup.getBoundingClientRect().right - (document.documentElement.clientWidth - 8);
                        if (over > 0) popup.style.translate = `${-Math.ceil(over)}px 0`;
                    }
                }
        }).observe(element, {childList: true, subtree: true});
        let marked = null, dragged = null;                         // (what's dragged: a value, or a field)
        const unmark = () => {
            marked?.classList.remove('insert-before', 'insert-after');
            marked = null;
        };
        element.addEventListener('dragstart', e => {
            dragged = e.target.closest?.('.field')?.classList.contains('value') ? 'value' : 'field';
        });
        element.addEventListener('dragend', () => {
            dragged = null;
        });
        const place = e => {
            const area = e.target.closest?.('.area[data-area]');
            if (!area || (area.dataset.area === 'values') !== (dragged === 'value')) return null;   // (not its kind's area: no drop)
            const fields = [...area.querySelectorAll(':scope > .field[data-field]')];
            const at = fields.findIndex(f => {
                const r = f.getBoundingClientRect();
                return e.clientY < r.top || (e.clientY <= r.bottom && e.clientX < r.left + r.width / 2);
            });
            return {area: area.dataset.area, fields, at: at < 0 ? fields.length : at};
        };
        element.addEventListener('dragover', e => {
            const p = place(e);
            unmark();
            if (!p) return;
            e.preventDefault();
            if (p.area === 'filters' || p.fields.length === 0) return;
            marked = p.fields[Math.min(p.at, p.fields.length - 1)];
            marked.classList.add(p.at < p.fields.length ? 'insert-before' : 'insert-after');
        });
        element.addEventListener('dragleave', e => {
            if (!element.contains(e.relatedTarget)) unmark();
        });
        element.addEventListener('dragend', unmark);
        element.addEventListener('drop', e => {
            const p = place(e);
            unmark();
            if (!p) return;
            e.preventDefault();
            page.invokeMethodAsync('DropAt', p.area, p.area === 'filters' ? null : p.fields[p.at]?.dataset.field ?? null);
        });
    }

    return {
        prepare,
        cancel,
        attachGrid,
        configure,
        attachStats,
        attachDrawdown,
        detach,
        loadSetting,
        saveSetting,
        replaceAddress,
        attachAreas,
        columnsShow
    };
}
