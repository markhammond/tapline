// The Show as fixture: cells in several layouts — dates across, dates down, dates inside another column field, none, and
// totals the statement didn't make — derived by the reference (reference.mjs: the page's derivation, of what's shown,
// before the data provider took it over), so the provider's can be checked against it. Node built-ins only. Its figures are made up from a fixed seed, then given the edges
// the stream's have: totals a cent off the sum of their parts (each rounded on its own), figures that don't add up
// across days (none), a date a row has no cell on, a total of zero, percentages rounding half away from zero, a part of
// a total of mixed signs. Writes show-as.fixture.json beside it:
//
//     node tests/PivotTable.Tests/make-fixture.mjs

import {writeFileSync} from 'node:fs';
import {fileURLToPath} from 'node:url';
import {deriveAll} from './reference.mjs';

const SEP = ' · ', UNIT = 100_000_000n, CENT = 1_000_000n;  // (figures in hundred-millionths; a cent of volume)

// mulberry32: the same figures every run.
let seed = 20261015;
const random = () => {
    seed = (seed + 0x6D2B79F5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
};
const between = (lo, hi) => BigInt(Math.floor(lo + random() * (hi - lo)));
const signed = magnitude => (random() < 0.5 ? -1n : 1n) * between(0, magnitude);

/** A cell's figures as the stream gives them: volume to the cent, trades whole, the rest to eight places. */
const leaf = () => ({
    q: signed(5e10), n: signed(2e13), r: signed(5e12), u: signed(5e12), b: signed(3e13),
    v: between(1e7, 5e9) * CENT, t: between(1, 5000) * UNIT, g: null, m: null,
});
const ADDS = ['q', 'n', 'r', 'u', 'b', 'v', 't'];
/** A total of cells: each figure summed — or none where the statement says it doesn't add up across what it spans. */
const total = (parts, none = []) => Object.fromEntries(Object.keys(parts[0]).map(k => {
    if (none.includes(k) || parts.some(p => p[k] === null)) return [k, null];
    let sum = parts.reduce((a, p) => a + p[k], 0n);
    if (k === 'v' && random() < 0.3) sum += random() < 0.5 ? -CENT : CENT;  // (a total rounded on its own: a cent off its parts)
    return [k, sum];
}));
const text = figures => Object.fromEntries(Object.entries(figures).map(([k, x]) => [k, x === null ? null : x.toString()]));

/**
 * A layout's cells: its leaves (row and column paths, all levels), and every total the statement makes over them — each
 * prefix of the rows by each prefix of the columns — unless a field's totals are hidden. Across dates, positions,
 * notional, unrealized PnL and balance don't add up (none); across clients, positions don't (each its own assets).
 */
function cellsOf({rows, columns, rowPaths, columnPaths, missing = [], hideRowTotals = false, hideColumnTotals = false}) {
    const leaves = new Map();
    for (const r of rowPaths) for (const c of columnPaths)
        if (!missing.includes(`${r}|${c}`)) leaves.set(`${r}\u0000${c}`, leaf());
    const prefix = (path, n) => path === '' ? '' : path.split(SEP).slice(0, n).join(SEP);
    const cells = new Map(leaves);
    for (let i = rows.length; i >= 0; i--)
        for (let j = columns.length; j >= 0; j--) {
            if (i === rows.length && j === columns.length) continue;
            if (hideRowTotals && i < rows.length) continue;           // (no totals across the rows: no Total row, nor groups')
            if (hideColumnTotals && j < columns.length) continue;     // (none across the columns: no Total column)
            const groups = new Map();
            for (const [key, figures] of leaves) {
                const [r, c] = key.split('\u0000'), at = `${prefix(r, i)}\u0000${prefix(c, j)}`;
                if (!groups.has(at)) groups.set(at, []);
                groups.get(at).push(figures);
            }
            const acrossDates = (rows.indexOf('date') >= i && rows.includes('date')) || (columns.indexOf('date') >= j && columns.includes('date'));
            const acrossClients = rows.indexOf('client') >= i && rows.includes('client');
            for (const [at, parts] of groups)
                cells.set(at, total(parts, [...(acrossDates ? ['q', 'n', 'u', 'b'] : []), ...(acrossClients ? ['q'] : [])]));
        }
    return [...cells].map(([key, figures]) => {
        const [row, column] = key.split('\u0000');
        return {row, column, figures: text(figures)};
    });
}

const dates = ['2026-10-13', '2026-10-14', '2026-10-15'];
const clients = {Americas: ['Cedar Trust', 'Damson LP'], EMEA: ['Fern Partners', 'Laurel & Co'], APAC: ['Acorn Fund']};
const regionClients = Object.entries(clients).flatMap(([region, names]) => names.map(n => `${region}${SEP}${n}`));
const percents = ['row', 'column', 'total'];
const every = (values, forms) => values.flatMap(v => forms.map(f => `${v}.${f}`));

const layouts = [
    {
        name: 'dates across, rows region › client: every form, a client without a cell on one date',
        rows: ['region', 'client'], columns: ['date'], rowPaths: regionClients, columnPaths: dates,
        missing: [`APAC${SEP}Acorn Fund|2026-10-14`],
        entries: [...every(['volume', 'trades', 'pnl', 'notional', 'position', 'balance'], percents), 'volume.running', 'trades.running'],
    },
    {
        name: 'dates down, rows date › region, columns leg',
        rows: ['date', 'region'], columns: ['leg'],
        rowPaths: dates.flatMap(d => ['Americas', 'EMEA', 'APAC', '—'].map(r => `${d}${SEP}${r}`)), columnPaths: ['risk', 'hedge'],
        missing: dates.flatMap(d => [`${d}${SEP}—|risk`, ...['Americas', 'EMEA', 'APAC'].map(r => `${d}${SEP}${r}|hedge`)]),
        entries: [...every(['volume', 'pnl'], percents), 'volume.running', 'trades.running'],
    },
    {
        name: 'dates inside another column field: columns leg › date, rows client',
        rows: ['client'], columns: ['leg', 'date'], rowPaths: ['Cedar Trust', 'Fern Partners'],
        columnPaths: ['risk', 'hedge'].flatMap(l => dates.map(d => `${l}${SEP}${d}`)),
        missing: [`Fern Partners|hedge${SEP}2026-10-15`],
        entries: [...every(['volume'], percents), 'volume.running', 'trades.running'],
    },
    {
        name: 'no Date in rows or columns: no running total',
        rows: ['client'], columns: ['leg'], rowPaths: ['Cedar Trust', 'Fern Partners'], columnPaths: ['risk', 'hedge'],
        entries: [...every(['volume', 'pnl'], percents), 'volume.running'],
    },
    {
        name: 'totals across the rows hidden (no Total row): % of column and of grand total none',
        rows: ['client'], columns: ['date'], rowPaths: ['Cedar Trust', 'Fern Partners'], columnPaths: dates, hideRowTotals: true,
        entries: [...every(['volume'], percents), 'volume.running'],
    },
    {
        name: 'totals across the columns hidden (no Total column): % of row and of grand total none, running total its own',
        rows: ['client'], columns: ['date'], rowPaths: ['Cedar Trust', 'Fern Partners'], columnPaths: dates, hideColumnTotals: true,
        entries: [...every(['volume'], percents), 'volume.running'],
    },
];

const made = layouts.map(({name, rows, columns, entries, ...paths}) => {
    const cells = cellsOf({rows, columns, ...paths});
    return {name, rows, columns, cells, entries, derived: deriveAll(cells, {rows, columns}, entries)};
});

// The edges, by hand: a part of a total half a hundred-millionth from rounding either way, of a total of mixed signs, of a
// total of zero.
const edges = [
    {row: 'A', column: '', figures: {q: null, n: null, r: (-300n * UNIT).toString(), u: null, b: null, v: '1', t: '0', g: null, m: null}},
    {row: 'B', column: '', figures: {q: null, n: null, r: (500n * UNIT).toString(), u: null, b: null, v: '-1', t: '0', g: null, m: null}},
    {row: 'C', column: '', figures: {q: null, n: null, r: '3', u: '-1', b: null, v: '199999999', t: '0', g: null, m: null}},
    {row: '', column: '', figures: {q: null, n: null, r: (200n * UNIT).toString(), u: null, b: null, v: '200000000', t: '0', g: null, m: null}},
];
const edgeEntries = ['volume.total', 'volume.row', 'volume.column', 'pnl.total', 'trades.total', 'volume.running'];
made.push({
    name: 'by hand: half away from zero either way, a total of mixed signs, a total of zero',
    rows: ['client'], columns: [], cells: edges, entries: edgeEntries, derived: deriveAll(edges, {rows: ['client'], columns: []}, edgeEntries),
});

const file = fileURLToPath(new URL('./show-as.fixture.json', import.meta.url));
writeFileSync(file, JSON.stringify({
    about: 'Show as: cells, and what the reference (reference.mjs deriveAll: the page\'s derivation, of what\'s shown) derives of them — made by make-fixture.mjs',
    separator: SEP,
    layouts: made,
}, null, 1) + '\n');
console.log(`${file}: ${made.length} layouts, ${made.reduce((n, l) => n + l.derived.length, 0)} figures derived (${made.reduce((n, l) => n + l.derived.filter(d => d[3] === null).length, 0)} none)`);
