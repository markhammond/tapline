// The reference for a pivot's Show as, of what's shown: the derivation the page made before the data provider took it
// over (pivot.js, until then) — kept here, as it was, for the fixture (make-fixture.mjs) the provider's own arithmetic
// (PivotTable.Contracts, ShowAs.cs) is checked against. A percentage: the cell's figure over its total cell's — the
// pivot's own: the Total column's on its row, the Total row's in its column, or the grand total — as a fraction to eight
// places, half away from zero; none where either is none or the total is zero (or not made: its totals hidden). A
// running total in Date: the dates' figures along Date's axis, oldest first, summed. Node built-ins only.

const SEP = ' · ';

/** A label's depth: how many fields it names ('' — a total across all of them — none). */
const levels = text => text === '' ? 0 : text.split(SEP).length;

/** A figure exactly, as decimal text: its hundred-millionths (a BigInt) in units, no trailing zeros — not through a double. */
const exactly = n => {
    const negative = n < 0n, magnitude = negative ? -n : n,
        fraction = (magnitude % 100000000n).toString().padStart(8, '0').replace(/0+$/, '');
    return `${negative ? '-' : ''}${magnitude / 100000000n}${fraction ? `.${fraction}` : ''}`;
};

/** A part of a total, as a fraction in hundred-millionths: to the nearest, half away from zero. */
const ratio = (x, y) => {
    const n = x * 100_000_000n, q = n / y, r = n % y;            // (truncated: away from zero when the rest is half or more)
    return 2n * (r < 0n ? -r : r) >= (y < 0n ? -y : y) ? q + ((n < 0n) !== (y < 0n) ? -1n : 1n) : q;
};

/** Each value's figure in a cell (its figures q, n, r, u, b, v, t, g, m): Pos PnL realized and unrealized — realized alone where unrealized is none. */
const FIGURES = {
    position: c => c.q, mark: c => c.m, notional: c => c.n, gross: c => c.g, realized: c => c.r, unrealized: c => c.u,
    pnl: c => c.r === null ? null : c.u === null ? c.r : c.r + c.u, balance: c => c.b, volume: c => c.v, trades: c => c.t,
};

/**
 * Each cell's context: its row's total, its column's and the grand total — the cells the statement made of them, if it
 * did — and its running total in Date: along the dates on Date's axis — its level's labels, oldest first (their names are
 * ISO dates) — each date's cell with the cell's other labels, none where it has none; a cell above Date's level (a total
 * across the dates), its own figure.
 */
function contexts(cells, layout, rowLabels, columnLabels) {
    const dateAxis = layout.columns.includes('date') ? 'columns' : layout.rows.includes('date') ? 'rows' : null,
        dateLevel = dateAxis ? layout[dateAxis].indexOf('date') : -1;
    const dates = dateAxis ? [...new Set([...(dateAxis === 'columns' ? columnLabels : rowLabels)]
        .filter(l => levels(l) > dateLevel).map(l => l.split(SEP)[dateLevel]))].sort() : [];
    const running = (rowText, columnText) => figure => {
        const path = dateAxis === 'columns' ? columnText : rowText;
        if (levels(path) <= dateLevel) {
            const own = cells.get(`${rowText}\u0000${columnText}`);
            return own ? figure(own) : null;
        }
        const parts = path.split(SEP), date = parts[dateLevel];
        let sum = 0n;
        for (const d of dates) {
            if (d > date) break;
            parts[dateLevel] = d;
            const other = parts.join(SEP), c = dateAxis === 'columns' ? cells.get(`${rowText}\u0000${other}`) : cells.get(`${other}\u0000${columnText}`);
            const x = c ? figure(c) : 0n;                           // (a date it has no cell on: nothing that day)
            if (x === null) return null;
            sum += x;
        }
        return sum;
    };
    const grand = cells.get('\u0000');
    return (rowText, columnText) => ({
        row: cells.get(`${rowText}\u0000`), column: cells.get(`\u0000${columnText}`), total: grand,
        running: dateAxis ? running(rowText, columnText) : null
    });
}

/** An entry's figure in a cell ("volume.column": a percentage of what's shown; "volume.running": a running total in Date). */
function derive(entry, cell, context) {
    const [value, form] = entry.split('.'), figure = FIGURES[value];
    if (form === 'running') return context.running?.(figure) ?? null;
    const x = figure(cell), t = context[form], y = t ? figure(t) : null;
    return x === null || y === null || y === 0n ? null : ratio(x, y);
}

/**
 * What the page derived for cells — each entry ("volume.column", "volume.running"…) — from cells as a stream gives them.
 * Cells: { row, column, figures: { q, n, r, u, b, v, t, g, m } }, each figure in hundred-millionths, as text (none: null;
 * volume too, scaled from its cents, as the grid reads it); the layout: { rows, columns }, its fields (where Date is).
 * Each: [row, column, entry, its figure exactly — as a copy has it — or null].
 */
export function deriveAll(given, layout, entries) {
    const cells = new Map(), rowLabels = new Set(), columnLabels = new Set();
    for (const {row, column, figures} of given) {
        cells.set(`${row}\u0000${column}`, Object.fromEntries(Object.entries(figures).map(([k, x]) => [k, x === null ? null : BigInt(x)])));
        rowLabels.add(row);
        columnLabels.add(column);
    }
    const contextOf = contexts(cells, {rows: layout.rows, columns: layout.columns}, rowLabels, columnLabels);
    return given.flatMap(({row, column}) => entries.map(entry => {
        const f = derive(entry, cells.get(`${row}\u0000${column}`), contextOf(row, column));
        return [row, column, entry, typeof f === 'bigint' ? exactly(f) : null];
    }));
}
