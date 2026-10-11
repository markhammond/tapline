// The browser side of the Waveform station (the page at /): it draws the server's ridge frames and plays the server's music.
//
// Carrier clock: every scene frame and music row is stamped with the station's clock (ms). The smallest
// (arrival − stamp) seen over the last 10 s is the offset to local time — the delivery delay with nothing queued.
// Ridges: a last value; at each display refresh the field is drawn as it was a moment ago — far enough behind that the
// frame after that moment has (nearly always) arrived: the 95th percentile, over the last 5 s, of how long after one
// frame's moment the next arrives — blended between the two frames either side of it, so it moves smoothly however few
// frames get through (about one frame's interval behind on a fast path; on a slow one, more). While the music plays, at
// least as far behind as it is heard. Each frame is decoded once, when first needed. A frame is a grid — every other line (full), fourth (lite)
// or eighth line and other point (tiny) — of heights, range-coded near-losslessly (RidgeCodec.cs; decodeGrid here),
// then splined (Catmull-Rom) down the lines and along them. The field has 77 lines;
// as many as fit in the middle four-sevenths of the page's height (at most 60) are drawn across it, centred, 16:10 — at
// most three-fifths of its width (in a narrow window, shorter to fit) — its peaks rising above; spaced with the window's size: 0.8em on a 15-inch laptop's full-screen window, in proportion to
// the square root of the window's area elsewhere (a length, so twice the size is twice the spacing), at least 0.5em. Age at paint = paint time − (stamp + offset): how much staler than the best case
// the shown frame is — the measure the server's flow control minimizes. The display's refresh is reported (Display): a
// page that isn't being painted (hidden, or throttled by the browser) or a display slower than the station is sent no
// more frames than it can show. `o` toggles occlusion (as on the album cover).
// Rendering: on a high-dynamic-range display with WebGPU, an extended-range canvas (rgba16float, tone mapping
// 'extended' — Chrome 129+; Safari may show it as standard range) where the lines grow brighter than SDR white with their
// height and pulse with the kick; `h` toggles that. Otherwise canvas 2D.
// Music: a MIDI piece's notes, a rolling window of rows (up to 200 ms each), synthesized here (Synth, Web Audio): each
// note played at stamp + offset + playout delay. The delay is the
// output's own latency plus a jitter buffer of no more than the path needs: set from 300 ms of rows, before a note
// plays; raised, before scheduling, for a row that would otherwise play late — a pause, not a stutter; lowered a frame
// (1/60 s) every quarter second toward the latest arrival of the last 10 s, 10% to spare. A slow path shows (as a
// pause), rather than being hidden behind a deep buffer. While the music plays, the ridges are shown that much behind
// too, in step with it.

const SCENE = 0x56534c50;                       // 'PLSV'
const HEADER = 40, FRAME_MS = 1000 / 60;
// The caption's glow, layer by layer, in font sizes: stroke width, blur (standard deviation), opacity — each twice as
// wide and soft as the last, but only ~0.65× as bright: a bloom, bright at the letters with a long, faint tail.
const GLOW = [[0.05, 0.04, 0.6], [0.12, 0.1, 0.41], [0.25, 0.22, 0.275], [0.5, 0.45, 0.18], [1.0, 0.9, 0.12]];
const LABEL_TRACKING = -0.02, LABEL_WORD_GAP = 0.4;  // the caption's letter spacing and gaps between words (ink to ink), in ems of its letters:
// a little tight — retro's outlines (as wide as the ridges, outside the letters) meet
const LABEL_LINE_GAP = 0.6;                     // between its lines: from one's baseline to the next's capitals, in ems
const LABEL_DROP = 1.25;                        // its capitals' tops, this far below the front line (ems)
const NAME_SPAN = 2 / 3;                        // split (the page's own caption): the name below the field alone, its ink this much of the field's
                                                // width, and the tagline beneath it as wide — spaced in the name's capitals' height (the name's
                                                // size is the field's: the same in every theme, whatever the tagline's font)…
const SPLIT = {drop: 0.8, gap: 0.2, tagline: 0.4};   // …its capitals' tops this far below the front line; the tagline's capitals this far
// below its descenders; and, centring the whole, the tagline taken as this tall (its own font's aside)…
const PEAKS = 0.14;                             // …the whole centred from as high as the peaks mostly rise: this much of the field's depth above its
                                                // top line (~0.11 typically, 0.14 at p95, ~0.17 at most)
const BLEED = {stops: 48, lines: 6, funnel: 6, name: 4};   // the caption's colour from the ridges (a theme's --waveform-bleed): sampled this
// often across, from the front lines (the WebGPU caption's shader, too: 6 lines),
// spreading as it falls: across, ± this much of how far below the front line (a split
// caption's name, larger and deeper: less, so the peaks' light reaches further down it)
const BREAK = {word: '//', stroke: 0.07, gap: 1};   // the caption's separator, drawn as an axis break: heavier (stroked this wide, in
// ems of its letters, no taller for it), its slashes this many strokes apart
const CAPTION_GLOW = 0.4;                       // the WebGPU caption's glow: as the CRT's CSS has the SVG's (.waveform-glow opacity)…
const SOFTEN = 0.03;                            // the WebGPU caption's letters, blurred this much (ems), as a CRT's spot softens them (a theme's --waveform-caption-soften: a pixel font's, 0)…
const HALO = {width: 0.16, blur: 0.14, opacity: 0.75};   // …and its black glow: a stroke this wide, this soft (ems of its letters:
// half outside them), this dark (the SVG's: CSS paint-order, drop-shadow)
// Chromium clips light past the display's HDR headroom channel by channel — and on a Mac the headroom shrinks once HDR
// has been up a while — leaving highlights flat and saturated, where Safari tone-maps to whatever headroom there is;
// the headroom isn't exposed. There, the shaders roll light past SDR white off themselves: white soon past it (white
// clips as white, however little headroom is left), its brightness toward this peak (encoded; ~2.1× the light).
// (Engine detection: userAgentData is Chromium's. ?rolloff=<peak> overrides it there, 0 off: to compare.)
const CHROMIUM = navigator.userAgentData?.brands?.some(b => b.brand === 'Chromium') ?? false;
const ROLL_OFF_QUERY = new URLSearchParams(location.search).get('rolloff');
const HDR_ROLL_OFF = CHROMIUM ? (ROLL_OFF_QUERY === null ? 1.4 : +ROLL_OFF_QUERY || 0) : 0;
// Light past SDR white turns white-hot, as an overdriven phosphor's does: its colour toward white, 63% of the way by this
// far past it, its brightest channel kept. (A display's own clipping whitens only an ink with all three channels to
// spare, as green's: amber's blue, 0.15, stays far below white even at the peaks' 4×, which would show yellow.) Rolled
// off, sooner: white clips as white.
const WHITE_HOT = HDR_ROLL_OFF > 0 ? 0.4 : 1;

// That roll-off in script, for a light the shaders don't draw (a theme's page, cleared to it): a grey's brightness.
function rolledOff(m) {
    if (m <= 1 || HDR_ROLL_OFF <= 1) return m;
    const room = HDR_ROLL_OFF - 1;
    return 1 + room * (1 - Math.exp((1 - m) / room));
}

const LINE_GAP_EM = 0.8, MIN_GAP_EM = 0.5, MAX_LINES = 60, MIN_LINES = 3;   // lines drawn: this far apart, within the field (FIELD)…
const FIELD = {height: 4 / 7, width: 3 / 5, aspect: 16 / 10};   // the lines' extent: this much of the page's height, this much wider than
// tall — and no wider than this much of the page's width (a narrow window: shorter)
const REFERENCE_AREA = 1440 * 800;              // …LINE_GAP_EM in a window this size (CSS px: a 15-inch laptop's, full screen)
const LINE_WIDTH = 0.2;                         // lines' width, of their spacing (a theme's --waveform-line-width): 2.2 px at 0.8em (14 px); finer as they close up (≥ 1 device px)…
const THEMES = ['amber', 'green', 'retro', 'paper', 'grid'];   // the page's data-waveform-theme (CSS: WaveformView.razor); grid: the four at once
const GRID = THEMES.slice(0, 4);                // its quadrants' themes: left to right, top to bottom
const THEME_CYCLE_MS = 30000;                   // each shown this long, in turn, until t takes over
const THEME_KEY = 'tapline.waveform.theme';     // local storage: { theme, cycling } — a reload (a ?bwlimit link) resumes there (App.razor: before paint)

const FONTS = ['theme', 'ioskeley', 'iosevka-slab'];  // the caption's font (a trial: f cycles them) — the page's data-waveform-font (CSS: WaveformView.razor's --waveform-mono, --waveform-crt)…
const FONT_NAMES = ["its theme's own", 'Ioskeley Mono', 'Iosevka Slab'];   // …its theme's own: VT323 on the CRTs, IBM Plex Mono on retro and paper
const FONT_KEY = 'tapline.waveform.font';       // local storage: the font left (App.razor: before paint)

function savedFont() {
    try {
        return Math.max(0, FONTS.indexOf(localStorage.getItem(FONT_KEY)));
    } catch {
        return 0;
    }                      // storage blocked or cleared: the theme's own
}

function savedTheme() {
    try {
        const saved = JSON.parse(localStorage.getItem(THEME_KEY));
        return THEMES.includes(saved?.theme) ? saved : null;
    } catch {
        return null;
    }                      // storage blocked or cleared: the default
}

const LINE_SOFTEN = 0.4;                        // …their edges softened, as a CRT's spot: a falloff either side this much of the width

export function createWaveform(tapline) {
    let view = null;
    return {
        attach(scene, canvas, hud, details, status, sound, pause, dotnet) {
            view?.detach();
            view = new WaveformView(tapline, scene, canvas, hud, details, status, sound, pause, dotnet);
        },
        detach() {
            view?.detach();
            view = null;
        },
        retune(scene) {
            view?.retune(scene);
        },
        get view() {
            return view;
        },                // for inspection from the console
    };
}

class CarrierClock {
    #buckets = [];                                // per-second minima of (local − carrier), the last 10
    offset = null;

    sample(delta) {
        const second = Math.floor(performance.now() / 1000);
        const last = this.#buckets.at(-1);
        if (last?.second === second) last.min = Math.min(last.min, delta);
        else {
            this.#buckets.push({second, min: delta});
            if (this.#buckets.length > 10) this.#buckets.shift();
        }
        this.offset = Math.min(...this.#buckets.map(b => b.min));
    }

    toLocal(carrierMs) {
        return carrierMs + this.offset;
    }
}

/** Events per second over the last two seconds. */
class Rate {
    #times = [];

    get value() {
        const cut = performance.now() - 2000;
        while (this.#times.length && this.#times[0] < cut) this.#times.shift();
        return this.#times.length / 2;
    }

    add(t = performance.now()) {
        this.#times.push(t);
    }
}

/** One pass of a Catmull-Rom spline: `count` runs of `n` grid samples (`stride` apart, runs `next` apart), each sampled
 *  at the grid coordinates `at` into a run of `dst`. */
function spline(src, count, n, stride, next, at, dst, dstStride, dstNext) {
    const taps = Array.from(at, u => {
        const j = Math.floor(u), t = u - j, t2 = t * t, t3 = t2 * t;
        return [[j - 1, j, j + 1, j + 2].map(q => Math.min(Math.max(q, 0), n - 1) * stride),
            [(-t + 2 * t2 - t3) / 2, (2 - 5 * t2 + 3 * t3) / 2, (t + 4 * t2 - 3 * t3) / 2, (t3 - t2) / 2]];
    });
    for (let r = 0; r < count; r++) {
        const base = r * next, out = r * dstNext;
        for (let x = 0; x < taps.length; x++) {
            const [i, w] = taps[x];
            const v = w[0] * src[base + i[0]] + w[1] * src[base + i[1]] + w[2] * src[base + i[2]] + w[3] * src[base + i[3]];
            dst[out + x * dstStride] = v > 0 ? v : 0;  // the spline dips a little below the flat ends: no lower than the base
        }
    }
}

const CONTEXTS = 6, UNARY = 16, STRIDE = 2 + UNARY;

/**
 * A frame's grid of heights (m lines × n points) from its coded bytes — RidgeCodec.cs, bit for bit: LZMA's range
 * decoder; each height's quantized prediction error (zero?, sign, magnitude in unary) under the adaptive probabilities
 * of its neighbourhood's context, added (× step) to the prediction from its decoded left, upper and upper-left
 * neighbours.
 */
function decodeGrid(src, m, n, step) {
    const probs = new Uint16Array(CONTEXTS * STRIDE).fill(1024), g = new Uint8Array(m * n);
    let pos = 0, range = 0xFFFFFFFF, code = 0;
    const next = () => pos < src.length ? src[pos++] : 0;
    for (let k = 0; k < 5; k++) code = (code * 256 + next()) >>> 0;
    const bit = p => {                             // range and code: unsigned 32-bit, as numbers
        const prob = probs[p], bound = (range >>> 11) * prob;
        let b = 0;
        if (code < bound) {
            range = bound;
            probs[p] = prob + ((2048 - prob) >> 5);
        } else {
            code -= bound;
            range -= bound;
            probs[p] = prob - (prob >> 5);
            b = 1;
        }
        while (range < 16777216) {
            range *= 256;
            code = (code * 256 + next()) >>> 0;
        }
        return b;
    };
    const direct = () => {                         // even odds, not adapted
        const bound = (range >>> 11) * 1024;
        let b = 0;
        if (code < bound) range = bound;
        else {
            code -= bound;
            range -= bound;
            b = 1;
        }
        while (range < 16777216) {
            range *= 256;
            code = (code * 256 + next()) >>> 0;
        }
        return b;
    };
    for (let i = 0; i < m; i++) {
        const o = i * n;
        for (let s = 0; s < n; s++) {
            const a = s > 0 ? g[o + s - 1] : 0, b = i > 0 ? g[o - n + s] : 0, c = i > 0 && s > 0 ? g[o - n + s - 1] : 0;
            const d = i > 0 && s + 1 < n ? g[o - n + s + 1] : b;
            const busy = Math.abs(a - c) + Math.abs(b - c) + Math.abs(d - b);
            const p = STRIDE * (busy === 0 ? 0 : busy < 3 ? 1 : busy < 7 ? 2 : busy < 15 ? 3 : busy < 30 ? 4 : 5);
            let q = 0;
            if (bit(p)) {
                const negative = bit(p + 1);
                let magnitude = 0;
                while (magnitude < UNARY && bit(p + 2 + magnitude)) magnitude++;
                if (magnitude === UNARY) {
                    let rest = 0;
                    for (let j = 0; j < 8; j++) rest = rest << 1 | direct();
                    magnitude += rest;
                }
                q = negative ? -(magnitude + 1) : magnitude + 1;
            }
            const v = a + b - c + q * step;
            g[o + s] = v < 0 ? 0 : v > 255 ? 255 : v;
        }
    }
    return g;
}

/** `lines` lines evenly across the field, every point, splined from the frame's grid: down the lines, then along them. */
function resample(f, lines) {
    const down = Float64Array.from({length: lines}, (_, r) => r * (f.fieldLines - 1) / (lines - 1) / f.lineStep);
    const columns = new Float32Array(lines * f.n);
    spline(f.grid, f.n, f.m, f.n, 1, down, columns, f.n, 1);
    const along = Float64Array.from({length: f.points}, (_, s) => s / f.pointStep);
    const heights = new Float32Array(lines * f.points);
    spline(columns, lines, f.n, 1, f.n, along, heights, 1, f.points);
    f.lines = lines;
    f.heights = heights;
}

/** Draws a frame with canvas 2D, in standard dynamic range. */
class Canvas2dRenderer {
    label = 'canvas 2D';

    constructor(canvas) {
        this.canvas = canvas;
        this.g = canvas.getContext('2d');
    }

    draw(f, {occlude, gap, lineWidth, background, ink, glow, afterglow, overdraw, lift = 0}) {
        const c = this.canvas, dpr = window.devicePixelRatio || 1;
        const w = Math.round(c.clientWidth * dpr), h = Math.round(c.clientHeight * dpr);
        if (c.width !== w || c.height !== h) {
            c.width = w;
            c.height = h;
        }
        // With an afterglow, the frame is drawn alone (in `frame`), then lightens the last frames, faded toward the page.
        this.frame = afterglow > 0 ? this.frame ?? document.createElement('canvas') : null;
        const target = this.frame ?? c, g = target.getContext('2d');
        if (target.width !== w || target.height !== h) {
            target.width = w;
            target.height = h;
        }
        g.fillStyle = background.css;
        g.fillRect(0, 0, w, h);
        g.shadowColor = ink.css;
        const {x0, y0, dx, dy, dh} = layout(f, w, h, gap * dpr, lift * dpr);
        g.lineWidth = Math.max(1, lineWidth * gap * dpr);
        g.lineJoin = 'round';
        const ys = this.ys?.length >= f.points ? this.ys : (this.ys = new Float32Array(f.points));
        for (let i = 0; i < f.lines; i++) {          // back (top) to front (bottom)
            const base = y0 + i * dy, o = i * f.points;
            for (let s = 0; s < f.points; s++) ys[s] = base - f.heights[o + s] * dh;
            if (occlude) {
                g.beginPath();
                g.moveTo(x0, ys[0]);
                for (let s = 1; s < f.points; s++) g.lineTo(x0 + s * dx, ys[s]);
                g.lineTo(x0 + (f.points - 1) * dx, base + dy);
                g.lineTo(x0, base + dy);
                g.closePath();
                g.fillStyle = background.css;
                g.fill();
            }
            g.beginPath();
            g.moveTo(x0, ys[0]);
            for (let s = 1; s < f.points; s++) g.lineTo(x0 + s * dx, ys[s]);
            g.strokeStyle = ink.css;
            g.globalAlpha = lineAlpha(f, i);
            g.shadowBlur = glow * gap * dpr;                     // a phosphor's glow: the stroke's shadow
            if (overdraw) g.globalCompositeOperation = 'lighter';   // overdrawn: its light adds to what's there
            g.stroke();
            g.globalAlpha = 1;
            g.shadowBlur = 0;
            g.globalCompositeOperation = 'source-over';
        }
        if (this.frame) {
            const now = performance.now(), dt = this.last ? now - this.last : Infinity, v = this.g;
            this.last = now;
            v.globalAlpha = 1 - Math.exp(-dt / afterglow);
            v.fillStyle = background.css;
            v.fillRect(0, 0, w, h);
            v.globalAlpha = 1;
            v.globalCompositeOperation = 'lighten';
            v.drawImage(this.frame, 0, 0);   // the brighter of each
            v.globalCompositeOperation = 'source-over';
        }
    }
}

/**
 * Line i's opacity, back (0) to front: lines in the ink colour, fading evenly into the page as they recede — opaque at the front, the
 * pattern's vertical fade (0.7) at the back: 30%. Browsers blend in gamma-encoded colour, which is near perceptual, so an
 * even ramp in opacity reads as an even fade into the background, whatever its colour.
 */
function lineAlpha(f, i) {
    return 1 - f.fade * (1 - i / (f.lines - 1));
}

/** Adds a coverage image, blurred (σ px) and weighted, to `out` (both w × h): a wide blur on a coarser grid — the image
 *  averaged down by a factor that leaves σ ≥ 3 cells, blurred there, and sampled back up (bilinear) — costing the same
 *  however wide. */
function blurInto(out, coverage, w, h, sigma, weight) {
    const f = Math.max(1, Math.floor(sigma / 3));
    if (f === 1) {
        gaussianBlur(coverage, w, h, sigma);
        for (let i = 0; i < out.length; i++) out[i] += coverage[i] * weight;
        return;
    }
    const sw = Math.ceil(w / f), sh = Math.ceil(h / f), small = new Float32Array(sw * sh);
    for (let y = 0; y < h; y++) {
        const row = (y / f | 0) * sw;
        for (let x = 0; x < w; x++) small[row + (x / f | 0)] += coverage[y * w + x];
    }
    for (let i = 0; i < small.length; i++) small[i] /= f * f;
    gaussianBlur(small, sw, sh, sigma / f);
    for (let y = 0; y < h; y++) {
        const fy = Math.min(sh - 1, Math.max(0, (y + 0.5) / f - 0.5)), y0 = fy | 0, y1 = Math.min(sh - 1, y0 + 1),
            ty = fy - y0;
        for (let x = 0; x < w; x++) {
            const fx = Math.min(sw - 1, Math.max(0, (x + 0.5) / f - 0.5)), x0 = fx | 0, x1 = Math.min(sw - 1, x0 + 1),
                tx = fx - x0;
            const top = small[y0 * sw + x0] * (1 - tx) + small[y0 * sw + x1] * tx;
            const bottom = small[y1 * sw + x0] * (1 - tx) + small[y1 * sw + x1] * tx;
            out[y * w + x] += (top * (1 - ty) + bottom * ty) * weight;
        }
    }
}

/** A Gaussian blur of σ (px), near enough, of a w × h coverage image, in place: three box blurs each way, each a running
 *  sum (its cost the same however wide), outside the image empty. The same in every browser. */
function gaussianBlur(image, w, h, sigma) {
    if (sigma < 0.3) return;
    const r = Math.max(1, Math.round((Math.sqrt(4 * sigma * sigma + 1) - 1) / 2)), norm = 1 / (2 * r + 1);   // 3 boxes ≈ σ
    const other = new Float32Array(image.length);
    const pass = (src, dst, along, across, step, line) => {   // a box along each of `across` lines of `along` samples
        for (let j = 0; j < across; j++) {
            const base = j * line;
            let sum = 0;
            for (let i = 0; i <= r && i < along; i++) sum += src[base + i * step];
            for (let i = 0; i < along; i++) {
                dst[base + i * step] = sum * norm;
                if (i + r + 1 < along) sum += src[base + (i + r + 1) * step];
                if (i - r >= 0) sum -= src[base + (i - r) * step];
            }
        }
    };
    for (let k = 0; k < 3; k++) {
        pass(image, other, w, h, 1, w);                       // across, row by row
        pass(other, image, h, w, w, 1);                       // down, column by column
    }
}

/** A caption mask's blurs and sum (Pane #captionMask), on whichever thread runs it: each of its bands' batches — an image
 *  whose red, green and blue are a layer's coverage each, with that layer's channel, weight and blur (σ, device px) —
 *  blurred into the band's three channels; the bands stacked. RGBA, `w` wide. */
function composeMask({w, bands}) {
    const px = new Uint8ClampedArray(w * bands.reduce((sum, b) => sum + b.h, 0) * 4);
    let row = 0;
    for (const {h, batches} of bands) {
        const n = w * h, channels = [new Float32Array(n), new Float32Array(n), new Float32Array(n)];   // red, green, blue
        for (const {data, layers} of batches)
            layers.forEach(([channel, alpha, sigma], k) => {
                const coverage = new Float32Array(n);
                for (let i = 0; i < n; i++) coverage[i] = data[i * 4 + k] / 255;
                blurInto(channels[channel], coverage, w, h, sigma, alpha);
            });
        for (let i = 0, o = row * w * 4; i < n; i++, o += 4) {
            px[o] = channels[0][i] * 255;
            px[o + 1] = channels[1][i] * 255;
            px[o + 2] = channels[2][i] * 255;
            px[o + 3] = 255;
        }
        row += h;
    }
    return px;
}

/** The worker that composes caption masks (composeMask): off the page's thread, so laying a caption out again — a theme,
 *  a font, the window's size — costs the ridges nothing (on the page, a few hundred ms: frames missed). Made from this
 *  script's own functions; null where a worker can't be made (the page composes them itself). */
let maskWorker;

function maskWorkerOf() {
    if (maskWorker !== undefined) return maskWorker;
    try {
        const source = `${gaussianBlur}\n${blurInto}\n${composeMask}\n` +
            'onmessage = e => { const px = composeMask(e.data); postMessage({id: e.data.id, px}, [px.buffer]); };';
        const worker = new Worker(URL.createObjectURL(new Blob([source], {type: 'text/javascript'})));
        worker.pending = new Map();                   // job → what to do with its mask
        worker.onmessage = e => {
            const done = worker.pending.get(e.data.id);
            worker.pending.delete(e.data.id);
            done?.(e.data.px);
        };
        worker.onerror = () => {                      // (a page that refuses workers from blobs): composed on the page instead
            maskWorker = null;
            for (const done of worker.pending.values()) done(null);
            worker.pending.clear();
            worker.terminate();
        };
        maskWorker = worker;
    } catch {
        maskWorker = null;
    }
    return maskWorker;
}

let maskJobs = 0;

/** Where a frame goes on a w × h canvas: its lines `gap` apart, centred (raised by `lift`: a caption below it), the field
 *  FIELD.aspect times as wide as they span (#fit chose how many: so it fits), its peaks rising above — as tall, for the
 *  spacing, as the station's field has them (the pattern narrower than the station's, not flatter). */
function layout(f, w, h, gap, lift = 0) {
    const spread = (f.lines - 1) * gap, width = spread * FIELD.aspect;
    return {
        x0: (w - width) / 2,
        y0: (h - spread) / 2 - lift,
        dx: width / (f.points - 1),
        dy: gap,
        dh: f.fullScale * spread / f.spread / 255
    };
}

const RIDGES_WGSL = /* wgsl */ `
struct U {
  size: vec2f, origin: vec2f, step: vec2f,
  dh: f32, halfWidth: f32, lines: u32, points: u32,
  fade: f32, boost: f32, pump: f32, occlude: u32,
  glow: f32, decay: f32,                        // a phosphor's: the glow's reach (px), the last frame's fade this frame
  background: vec4f, ink: vec4f,
  overdraw: f32,                                // 1: the lines' light adds (a phosphor's), 0: each line over those behind
  bleed: f32, captionGlow: f32, captionHalo: f32,   // the caption's: light from the ridges (0: not drawn here), glow's and halo's opacity
  caption: vec4f,                               // the caption's rect (device px): x, y, width, height
  funnel: f32,                                  // the ridges' light on it spreads across ± this × how far below the front line
  composed: f32, peak: f32,                     // 1: the frame is composed after its light is summed (lighten); HDR_ROLL_OFF
  soft: f32,                                    // the lines' edges: a falloff (px) either side of half the width
  whiten: f32,                                  // WHITE_HOT
  skipFrom: f32, skipBy: f32,                   // the caption's mask: its bands stacked, the field between them (this tall, from this far down) left out
};
@group(0) @binding(0) var<uniform> u: U;

// Light past SDR white: white-hot, its colour toward white (63% by u.whiten past it); where the browser would clip it
// (u.peak > 0: Chromium), rolled off too, its brightest channel eased toward the peak (a soft knee at white).
fn rollOff(c: vec3f) -> vec3f {
  let m = max(c.r, max(c.g, c.b));
  if (m <= 1.0) { return c; }
  let hot = mix(c / m, vec3f(1.0), 1.0 - exp((1.0 - m) / u.whiten));
  if (u.peak <= 1.0) { return hot * m; }
  let room = u.peak - 1.0;
  return hot * (1.0 + room * (1.0 - exp((1.0 - m) / room)));
}
@group(0) @binding(1) var<storage, read> heights: array<f32>;
@group(0) @binding(2) var frame: texture_2d<f32>;      // with an afterglow: this frame, drawn alone
@group(0) @binding(3) var mask: texture_2d<f32>;       // the caption: its letters' coverage (r), their glow's (g), black halo's (b)

struct V { @builtin(position) pos: vec4f, @location(0) across: f32, @location(1) color: vec4f };

fn clip(p: vec2f) -> vec4f { return vec4f(p.x / u.size.x * 2.0 - 1.0, 1.0 - p.y / u.size.y * 2.0, 0.0, 1.0); }

// Per instance one segment of one line (and, occluding, first its fill down to the next line's base: painter's order,
// back to front); per vertex a corner of its quad. A line's segments are extruded across by half the width plus a
// pixel (and the glow's reach), and the fragment shader covers what is within half the width: anti-aliased, for the
// price of a quad — and, glowing, a falloff either side.
@vertex fn vs(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> V {
  var corners = array<vec2u, 6>(vec2u(0, 0), vec2u(1, 0), vec2u(0, 1), vec2u(0, 1), vec2u(1, 0), vec2u(1, 1));
  let corner = corners[vi];
  let segs = u.points - 1u;
  let perLine = select(segs, 2u * segs, u.occlude != 0u);
  let line = ii / perLine;
  var k = ii % perLine;
  let fill = u.occlude != 0u && k < segs;
  if (u.occlude != 0u && !fill) { k = k - segs; }
  let base = u.origin.y + f32(line) * u.step.y;
  let h0 = heights[line * u.points + k];
  let h1 = heights[line * u.points + k + 1u];
  let p0 = vec2f(u.origin.x + f32(k) * u.step.x, base - h0 * u.dh);
  let p1 = vec2f(u.origin.x + f32(k + 1u) * u.step.x, base - h1 * u.dh);
  var out: V;
  if (fill) {
    let top = select(p0, p1, corner.x == 1u);
    out.pos = clip(vec2f(top.x, select(top.y, base + u.step.y, corner.y == 1u)));
    out.across = 0.0;
    out.color = vec4f(0.0, 0.0, 0.0, -1.0);    // alpha < 0: opaque, the background
    return out;
  }
  let d = normalize(p1 - p0);
  let w = u.halfWidth + 1.0 + u.glow;
  let side = select(-1.0, 1.0, corner.y == 1u);
  out.pos = clip(select(p0, p1, corner.x == 1u) + vec2f(-d.y, d.x) * w * side);
  out.across = side * w;
  let h = select(h0, h1, corner.x == 1u) / 255.0;
  let fade = 1.0 - u.fade * (1.0 - f32(line) / f32(u.lines - 1u));   // opacity: into the page as it recedes (lineAlpha)
  let lit = u.ink.rgb * (1.0 + u.boost * pow(max(h, 0.0), 1.2) * (0.6 + 0.4 * u.pump));   // the ink; brighter (> 1: than SDR) at the peaks
  out.color = vec4f(select(rollOff(lit), lit, u.composed > 0.0), fade);   // white-hot here, or once summed (lighten)
  return out;
}

@fragment fn fs(v: V) -> @location(0) vec4f {
  if (v.color.a < 0.0) { return vec4f(u.background.rgb, 1.0); }   // an occluding fill: the page's colour
  let d = abs(v.across);
  let core = 1.0 - smoothstep(u.halfWidth - u.soft - 0.5, u.halfWidth + u.soft + 0.5, d);   // half lit at half the width, softened
  // (edges in order: reversed, smoothstep is undefined — Metal, under Safari, takes it literally)
  let bloom = select(0.0, 0.35 * exp(-4.0 * d * d / (u.glow * u.glow)), u.glow > 0.0);   // the glow: ~0 at its reach
  let a = v.color.a * min(1.0, core + bloom);
  return vec4f(v.color.rgb * a, a * (1.0 - u.overdraw));   // premultiplied; overdrawn, alpha 0: adds, crossings brighten
}

// The afterglow, on the last frames kept (each a triangle over the whole target): those faded toward the page (by
// u.decay), then this frame where it is the brighter (blended by max) — what stays lit holds, what moved fades.
@vertex fn screen(@builtin(vertex_index) vi: u32) -> @builtin(position) vec4f {
  return vec4f(f32((vi << 1u) & 2u) * 2.0 - 1.0, f32(vi & 2u) * 2.0 - 1.0, 0.0, 1.0);
}
@fragment fn afterglow() -> @location(0) vec4f { return vec4f(u.background.rgb * u.decay, u.decay); }
@fragment fn lighten(@builtin(position) p: vec4f) -> @location(0) vec4f {
  let c = textureLoad(frame, vec2i(p.xy), 0);
  return vec4f(rollOff(c.rgb), c.a);            // the lines' light, overdrawn, summed (and the caption's)
}

// The caption, lit by the ridges above it (as waveform.js's #bleedCaption, but in extended range, the funnel's spread
// growing down the letters): at each x the front lines' light (BLEED.lines of them, nearer counting more, by height as the lines brighten, pumped by the kick) takes it
// from its ink, dimmed a little, to white-hot — past SDR white where the display has headroom; its glow in the same light.
@vertex fn captionQuad(@builtin(vertex_index) vi: u32) -> @builtin(position) vec4f {
  var corners = array<vec2f, 6>(vec2f(0.0, 0.0), vec2f(1.0, 0.0), vec2f(0.0, 1.0), vec2f(0.0, 1.0), vec2f(1.0, 0.0), vec2f(1.0, 1.0));
  return clip(u.caption.xy + corners[vi] * u.caption.zw);
}
fn lightAt(s0: f32) -> f32 {               // the front lines' light at point s0
  let s = clamp(s0, 0.0, f32(u.points - 1u));
  let i = u32(floor(s));
  let j = min(i + 1u, u.points - 1u);
  var sum = 0.0;
  var weight = 1.0;
  for (var k = 0u; k < min(6u, u.lines); k++) {
    let row = (u.lines - 1u - k) * u.points;
    sum += weight * pow(max(mix(heights[row + i], heights[row + j], s - f32(i)) / 255.0, 0.0), 1.2);
    weight *= 0.6;
  }
  return sum;
}
fn light(p: vec2f) -> f32 {                  // a funnel: the further below the front line, the wider the light falls
  let below = p.y - (u.origin.y + f32(u.lines - 1u) * u.step.y);
  if (below < 0.0) { return 0.0; }            // (above it — a tagline over the field — none)
  let reach = below * u.funnel / u.step.x;    // half-width, in points
  let s = (p.x - u.origin.x) / u.step.x;
  var sum = 0.0;
  var total = 0.0;
  for (var i = -4; i <= 4; i++) {
    let o = f32(i) / 4.0;
    let weight = exp(-2.0 * o * o);
    sum += weight * lightAt(s + o * reach);
    total += weight;
  }
  return sum / total;
}
@fragment fn caption(@builtin(position) p: vec4f) -> @location(0) vec4f {
  var at = p.xy - u.caption.xy;
  if (at.y >= u.skipFrom) {                   // past the first band: the field between, then the second band
    if (at.y < u.skipFrom + u.skipBy) { discard; }
    at.y -= u.skipBy;
  }
  let m = textureLoad(mask, vec2i(at), 0);
  if (m.r + m.g + m.b == 0.0) { discard; }    // nothing of it here
  let t = clamp(light(p.xy) * (0.6 + 0.4 * u.pump) * u.bleed, 0.0, 1.0);
  let c = mix(mix(u.background.rgb, u.ink.rgb, 0.8), mix(u.ink.rgb, vec3f(1.0), 0.75), t) * (1.0 + 0.5 * u.boost * t);
  let keep = (1.0 - u.captionHalo * m.b) * (1.0 - m.r);              // what shows through the letters and their halo
  return vec4f(c * m.r + c * u.captionGlow * m.g * keep, 1.0 - keep);   // premultiplied: the glow adds, the halo darkens, the letters cover
}`;

/** Draws a frame with WebGPU on an extended-range canvas: brightness above SDR white where the display has headroom. */
class HdrRenderer {
    static #device = null;                        // one for every canvas (the grid's four)
    label = 'WebGPU, extended range (HDR)';

    constructor(canvas, device, context) {
        Object.assign(this, {canvas, device, context});
        const module = device.createShaderModule({code: RIDGES_WGSL});
        const over = {
            color: {srcFactor: 'one', dstFactor: 'one-minus-src-alpha'},
            alpha: {srcFactor: 'one', dstFactor: 'one-minus-src-alpha'}
        };
        const max = {
            color: {operation: 'max', srcFactor: 'one', dstFactor: 'one'},
            alpha: {operation: 'max', srcFactor: 'one', dstFactor: 'one'}
        };
        const pipeline = (vs, fs, blend = over) => device.createRenderPipeline({
            layout: 'auto',
            vertex: {module, entryPoint: vs},
            fragment: {module, entryPoint: fs, targets: [{format: 'rgba16float', blend}]},
            primitive: {topology: 'triangle-list'},
        });
        this.pipeline = pipeline('vs', 'fs');
        this.decay = pipeline('screen', 'afterglow');
        this.lighten = pipeline('screen', 'lighten', max);
        this.captionPipeline = pipeline('captionQuad', 'caption');
        this.uniforms = device.createBuffer({size: 160, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST});
        this.heights = device.createBuffer({
            size: 256 * 256 * 4,
            usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST
        });
        this.bindings = device.createBindGroup({
            layout: this.pipeline.getBindGroupLayout(0),
            entries: [{binding: 0, resource: {buffer: this.uniforms}}, {binding: 1, resource: {buffer: this.heights}}],
        });
        this.decayBindings = device.createBindGroup({
            layout: this.decay.getBindGroupLayout(0), entries: [{binding: 0, resource: {buffer: this.uniforms}}],
        });
        this.u = new ArrayBuffer(160);
        this.uf = new Float32Array(this.u);
        this.uu = new Uint32Array(this.u);
    }

    static async create(canvas) {
        if (!navigator.gpu || !matchMedia('(dynamic-range: high)').matches) return null;
        const device = await (HdrRenderer.#device ??= navigator.gpu.requestAdapter().then(a => a?.requestDevice() ?? null));
        const context = device && canvas.getContext('webgpu');
        if (!context) return null;
        context.configure({
            device, format: 'rgba16float', alphaMode: 'opaque', toneMapping: {mode: 'extended'},
            usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_DST,   // drawn to, or (with an afterglow) copied to
        });
        return new HdrRenderer(canvas, device, context);
    }

    /** Where the HDR highlights are headed: up (h), or down while the hover text shows — it reads over the ridges. */
    static #boost({hdr, shown}) {
        return hdr && !shown ? 3 : 0.15;
    }

    /** Whether its highlights are still easing there: drawn again until they have — a held frame (paused) too. */
    easing(pane) {
        return this.boost !== undefined && Math.abs(HdrRenderer.#boost(pane) - this.boost) > 0.01;
    }

    /** The caption to draw (waveform.js #placeLabel): a mask — its letters' coverage red, their glow's green — and where it
     *  goes on the canvas (device px), and what of that the mask leaves out (`skip`: from this far down, this much — the
     *  field between a tagline above it and a name below: the mask is its two bands, stacked). Drawn while a theme bleeds
     *  the ridges' light into it. */
    caption(canvas, rect, skip = [0, 0]) {
        this.mask?.destroy();
        this.mask = this.device.createTexture({
            size: [canvas.width, canvas.height], format: 'rgba8unorm',
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
        });
        this.device.queue.copyExternalImageToTexture({source: canvas}, {texture: this.mask}, [canvas.width, canvas.height]);
        this.captionRect = rect;
        this.captionSkip = skip;
        this.captionBindings = this.device.createBindGroup({
            layout: this.captionPipeline.getBindGroupLayout(0),
            entries: [
                {binding: 0, resource: {buffer: this.uniforms}}, {binding: 1, resource: {buffer: this.heights}},
                {binding: 3, resource: this.mask.createView()},
            ],
        });
    }

    draw(f, pane) {
        const {
            occlude,
            gap,
            lineWidth,
            background,
            ink,
            glow,
            afterglow,
            overdraw,
            bleed,
            backgroundLight,
            lift = 0
        } = pane;
        const c = this.canvas, dpr = window.devicePixelRatio || 1;
        const w = Math.round(c.clientWidth * dpr), h = Math.round(c.clientHeight * dpr);
        if (c.width !== w || c.height !== h) {
            c.width = w;
            c.height = h;
        }
        const {x0, y0, dx, dy, dh} = layout(f, w, h, gap * dpr, lift * dpr);
        const uf = this.uf, uu = this.uu;
        uf[0] = w;
        uf[1] = h;
        uf[2] = x0;
        uf[3] = y0;
        uf[4] = dx;
        uf[5] = dy;
        uf[6] = dh;
        uf[7] = Math.max(0.5, lineWidth * gap * dpr / 2);
        uu[8] = f.lines;
        uu[9] = f.points;
        const now = performance.now(), dt = this.last ? now - this.last : Infinity;
        this.last = now;
        // The HDR highlights (#boost), eased in and out (~0.6 s, as the CSS dims the ridges) — a frame's step at most: a held
        // frame drawn again after a while eases from where it was too.
        const boost = HdrRenderer.#boost(pane), step = Math.min(dt, 50);
        this.boost = Number.isFinite(dt) && this.boost !== undefined ? this.boost + (boost - this.boost) * (1 - Math.exp(-step / 200)) : boost;
        uf[10] = f.fade;
        uf[11] = this.boost;
        uf[12] = f.pump;
        uu[13] = occlude ? 1 : 0;
        uf[14] = glow * gap * dpr;
        uf[15] = afterglow > 0 ? 1 - Math.exp(-dt / afterglow) : 1;
        // The page, lit past SDR white (a theme's --waveform-bg-light) as the highlights are (h, the hover text): cleared to
        // it, the occluding fills in it — rolled off, as the shaders' light is.
        const bright = backgroundLight > 0 ? rolledOff(1 + backgroundLight * this.boost / 3) : 1;
        uf[16] = background.rgb[0] * bright;
        uf[17] = background.rgb[1] * bright;
        uf[18] = background.rgb[2] * bright;
        uf[19] = 1;
        uf.set(ink.rgb, 20);
        uf[23] = 1;
        uf[24] = overdraw ? 1 : 0;
        const caption = bleed > 0 && this.mask;
        uf[25] = bleed;
        uf[26] = CAPTION_GLOW;
        uf[27] = HALO.opacity;
        uf[32] = pane.funnelRate ?? BLEED.funnel;
        uf[33] = afterglow > 0 ? 1 : 0;
        uf[34] = HDR_ROLL_OFF;
        uf[35] = LINE_SOFTEN * Math.max(1, lineWidth * gap * dpr);
        uf[36] = WHITE_HOT;
        if (caption) {
            uf.set(this.captionRect, 28);
            uf[37] = this.captionSkip[0];
            uf[38] = this.captionSkip[1];
        }
        this.device.queue.writeBuffer(this.uniforms, 0, this.u);
        this.device.queue.writeBuffer(this.heights, 0, f.heights);
        // With an afterglow, the frame is drawn alone (in `frame`), then lightens the last frames kept (in `trace`, faded toward
        // the page first), which are copied to the canvas: the canvas's own texture is new every frame.
        let fresh = false;
        if (!(afterglow > 0)) this.#release();
        else if (this.trace?.width !== w || this.trace?.height !== h) {
            this.#release();
            const texture = usage => this.device.createTexture({
                size: [w, h],
                format: 'rgba16float',
                usage: GPUTextureUsage.RENDER_ATTACHMENT | usage
            });
            this.frame = texture(GPUTextureUsage.TEXTURE_BINDING);
            this.trace = texture(GPUTextureUsage.COPY_SRC);
            this.lightenBindings = this.device.createBindGroup({
                layout: this.lighten.getBindGroupLayout(0),
                entries: [{binding: 0, resource: {buffer: this.uniforms}}, {
                    binding: 2,
                    resource: this.frame.createView()
                }],
            });
            fresh = true;
        }
        const encoder = this.device.createCommandEncoder();
        const clearValue = {r: uf[16], g: uf[17], b: uf[18], a: 1};
        const pass = encoder.beginRenderPass({
            colorAttachments: [{
                view: (this.frame ?? this.context.getCurrentTexture()).createView(),
                loadOp: 'clear',
                storeOp: 'store',
                clearValue,
            }]
        });
        pass.setPipeline(this.pipeline);
        pass.setBindGroup(0, this.bindings);
        pass.draw(6, f.lines * (f.points - 1) * (occlude ? 2 : 1));
        if (caption) {
            pass.setPipeline(this.captionPipeline);
            pass.setBindGroup(0, this.captionBindings);
            pass.draw(6);
        }
        pass.end();
        if (this.trace) {
            const trail = encoder.beginRenderPass({
                colorAttachments: [{
                    view: this.trace.createView(), loadOp: fresh ? 'clear' : 'load', storeOp: 'store', clearValue,
                }]
            });
            trail.setPipeline(this.decay);
            trail.setBindGroup(0, this.decayBindings);
            trail.draw(3);
            trail.setPipeline(this.lighten);
            trail.setBindGroup(0, this.lightenBindings);
            trail.draw(3);
            trail.end();
            encoder.copyTextureToTexture({texture: this.trace}, {texture: this.context.getCurrentTexture()}, [w, h]);
        }
        this.device.queue.submit([encoder.finish()]);
    }

    #release() {
        this.frame?.destroy();
        this.trace?.destroy();
        this.frame = this.trace = null;
    }
}

/** Context time at which a sound is heard at local time `ms` (output latency included where the browser says). */
function contextTime(ctx, ms) {
    const ts = ctx.getOutputTimestamp?.();
    if (ts?.performanceTime > 0) return ts.contextTime + (ms - ts.performanceTime) / 1000;
    return ctx.currentTime + (ms - performance.now()) / 1000 - (ctx.outputLatency || ctx.baseLatency || 0);
}

/**
 * Where the ridges are drawn in one theme: a canvas, the caption over it (its label's SVG), a renderer, and the theme's
 * values — read from the CSS of the element that carries the theme's variables (`root`). The page's own (in the page's
 * theme), or one of the grid's four (a theme each: the CSS gives it its caption, or none). Fed by its view: the frame
 * to draw, and the lines' spacing (`gap`).
 */
class Pane {
    constructor(view, root, canvas, svg, index) {
        Object.assign(this, {view, root, canvas, svg, index});
        this.renderer = null;
        this.gap = 0;
        this.lineWidth = LINE_WIDTH;
        root.style.setProperty('--waveform-bleed-paint', `url(#waveform-bleed-${index})`);   // its caption's gradient (#placeLabel: an id of its own)
        HdrRenderer.create(canvas)
            .catch(e => {
                console.info('waveform: no HDR rendering', e);
                return null;
            })
            .then(r => {
                if (this.disposed) return;
                this.renderer = r ?? new Canvas2dRenderer(canvas);
                if (!r) canvas.style.filter = 'blur(0.5px)';    // canvas 2D: its lines softened (WebGPU's: LINE_SOFTEN, in the shader)
                this.labelKey = null;
                this.theme();
            });
    }

    // What its renderer reads of the view's (its toggles, and whether the hover text shows).
    get occlude() {
        return this.view.occlude;
    }

    get hdr() {
        return this.view.hdr;
    }

    get shown() {
        return this.view.shown;
    }

    /** Draws frame `f`, and lays its caption out (only when the layout has changed) and lights it. */
    draw(f) {
        this.renderer.draw(f, this);
        this.placeLabel(f);
        if (this.bleed > 0 && !this.renderer.caption) this.#bleedCaption(f);   // the SVG's; WebGPU lights its own
    }

    dispose() {
        this.disposed = true;
        clearTimeout(this.maskTimer);
    }

    /** The theme, from the page's CSS: its colour and the lines' (--waveform-bg, --waveform-ink; #rrggbb) — the renderers'
     *  background and occluding fills, their strokes — and a phosphor's: glow (--waveform-glow: its reach, in line spacings),
     *  afterglow (--waveform-afterglow: its time constant, ms) and overdraw (--waveform-overdraw: 1, where the lines cross or
     *  pile up their light adds, as a beam's would); and how strongly the ridges' light bleeds into the caption below them
     *  (--waveform-bleed, see #bleedCaption); and the page's own light (--waveform-bg-light: past SDR white, encoded, at
     *  the highlights' full — WebGPU, in HDR: on an SDR display, the page's colour as it is). */
    /** The ridges' width, CSS px, for the CSS (--waveform-line: retro outlines the caption in it). */
    lineVar() {
        if (this.gap) this.root.style.setProperty('--waveform-line', `${Math.max(1 / (window.devicePixelRatio || 1), this.lineWidth * this.gap)}px`);
    }

    theme() {
        const style = getComputedStyle(this.root), colour = (name, fallback) => {
            const css = style.getPropertyValue(name).trim() || fallback;
            return {css, rgb: [1, 3, 5].map(i => parseInt(css.slice(i, i + 2), 16) / 255)};
        };
        this.background = colour('--waveform-bg', '#000000');
        this.ink = colour('--waveform-ink', '#ffffff');
        this.glow = parseFloat(style.getPropertyValue('--waveform-glow')) || 0;
        this.afterglow = parseFloat(style.getPropertyValue('--waveform-afterglow')) || 0;
        this.overdraw = parseFloat(style.getPropertyValue('--waveform-overdraw')) > 0;
        this.bleed = parseFloat(style.getPropertyValue('--waveform-bleed')) || 0;
        this.lineWidth = parseFloat(style.getPropertyValue('--waveform-line-width')) || LINE_WIDTH;
        this.backgroundLight = parseFloat(style.getPropertyValue('--waveform-bg-light')) || 0;
        this.lineVar();
        // A bled caption, where the renderer draws it (WebGPU, in HDR): the SVG's stays, unseen, for its text.
        this.root.classList.toggle('waveform-gpu-caption', this.bleed > 0 && !!this.renderer?.caption);
        this.view.drawn = null;
    }

    /** A grid quadrant's: no caption, its field centred from as high as its peaks mostly rise (PEAKS) to its front line. */
    #placeField(f) {
        const lift = -PEAKS * (f.lines - 1) * this.gap / 2;
        if (lift !== this.lift) {
            this.lift = lift;
            this.view.drawn = null;
        }   // the field lowered: drawn again there
    }

    /** The caption under the ridges, in page coordinates: in lines ('|' between them, LABEL_LINE_GAP apart), each centred,
     *  the widest's ink spanning the field, the rest at its size — letters a little tight (LABEL_TRACKING), words half an
     *  em apart ink to ink (LABEL_WORD_GAP: the glyphs' side bearings allowed for), its separator an axis break (BREAK:
     *  heavier — stroked in its own colour, smaller by the stroke's reach, its middle where it was — its slashes close) —
     *  its capitals' tops LABEL_DROP below the front line — or, the page's own (not a grid quadrant's), split: its name
     *  alone below the field, two-thirds of its width, its tagline beneath as wide, the whole raised to sit centred above
     *  the page's buttons. Behind it a glow that spreads evenly out from the letters (GLOW).
     *  Measured in its own font: again once that font has loaded. Moved only when the layout changes. */
    placeLabel(f) {
        const svg = this.svg, text = svg?.querySelector(':scope > text');   // not a glow copy (they come first)
        if (svg && !text) return;
        const dpr = window.devicePixelRatio || 1;
        const w = this.canvas.clientWidth, h = this.canvas.clientHeight,
            key = `${w}×${h}×${f.lines}×${this.gap}×${dpr}`;
        if (key === this.labelKey) return;
        this.labelKey = key;
        this.lineVar();
        if (!svg || getComputedStyle(svg).display === 'none') return this.#placeField(f);   // a grid quadrant without a caption
        const {x0, y0, dx, dy} = layout(f, w, h, this.gap);
        const width = dx * (f.points - 1), label = text.dataset.label ?? '';
        const style = getComputedStyle(text), {fontFamily: family, fontWeight: weight} = style;   // the CSS's
        // A pixel font (its theme's --waveform-caption-pixels: its pixels to the em): untracked, its words whole font pixels
        // apart and each starting on a whole device pixel (as its baselines), its // its own glyphs — sized as any font, its ink
        // spanning the field (whole-pixel sizes are steps too coarse to follow it). Its letters' softening: the theme's.
        const pixels = parseFloat(style.getPropertyValue('--waveform-caption-pixels')) || 0,
            tracking = pixels ? 0 : LABEL_TRACKING;
        const soften = parseFloat(style.getPropertyValue('--waveform-caption-soften'));
        const snap = v => pixels ? Math.round(v * dpr) / dpr : v;
        // The name (its first word), in a font of its own where the CSS names one (--waveform-name-font): its capitals as tall
        // as the rest's, tracked as any font (not a pixel font's).
        const name = label.split(/[ |]/)[0], nameFamily = style.getPropertyValue('--waveform-name-font').trim(),
            nameWeight = '400';
        if (this.labelFont !== `${weight} ${family}|${nameFamily}`) {   // its fonts loaded — asked for here, not just awaited: until the caption shows,
            this.labelFont = `${weight} ${family}|${nameFamily}`;         // nothing has asked for them — then laid out again, in their own metrics (#fontMetrics)
            Promise.all([document.fonts.load(`${weight} 100px ${family}`, label), nameFamily && document.fonts.load(`${nameWeight} 100px ${nameFamily}`, name)])
                .then(() => {
                    this.labelKey = null;
                    this.view.drawn = null;
                }, () => {
                });
        }
        const font = this.view.fontMetrics(family, weight, label);             // per px of font size
        const named = nameFamily ? this.view.fontMetrics(nameFamily, nameWeight, name) : null;
        // Per px of font size: each word's font and scale (the break's: smaller by its stroke's reach; the name's: its own
        // font's, to the rest's capitals), its letters' steps, its ink's insets at either end, its width, how far its baseline
        // moves (the break's: to keep its middle); the gap before each word, ink to ink; each line's ink.
        const lines = label.split('|').map((line, l) => {
            const words = line.split(' ').map((word, i) => {
                const own = named && l === 0 && i === 0, metrics = own ? named : font;
                const m = [...word].map(c => metrics.glyphs[c]),
                    stroke = word === BREAK.word && !pixels ? BREAK.stroke : 0;
                const scale = own ? font.cap / named.cap : 1 - stroke / m[0].height;
                const steps = m.slice(0, -1).map(g => stroke ? (g.thickness * scale + stroke) * (1 + BREAK.gap) : (g.advance + (own ? LABEL_TRACKING : tracking)) * scale);
                return {
                    word, scale, stroke, steps, shift: stroke ? m[0].middle * (scale - 1) : 0, name: own,
                    family: own ? nameFamily : family, weight: own ? nameWeight : weight,
                    left: m[0].left * scale - stroke / 2, right: m.at(-1).right * scale - stroke / 2,
                    width: steps.reduce((sum, a) => sum + a, 0) + m.at(-1).advance * scale,
                };
            });
            const gap = pixels ? Math.round(LABEL_WORD_GAP * pixels) / pixels : LABEL_WORD_GAP;
            words.forEach((w, i) => w.before = i ? gap - words[i - 1].right - w.left : -w.left);
            return {words, ink: words.reduce((sum, w) => sum + w.before + w.width, 0) - words.at(-1).right};
        });
        let size = Math.max(8, Math.min(width * 0.08, width / Math.max(...lines.map(l => l.ink))));
        const step = (font.cap + LABEL_LINE_GAP) * size;                    // baseline to baseline
        const front = y0 + (f.lines - 1) * dy,
            ink = ws => ws.reduce((sum, w) => sum + w.before + w.width, 0) - ws.at(-1).right;
        // Its blocks of lines, each with its first baseline and its band (top, bottom): the caption, its capitals' tops
        // LABEL_DROP below the front line — or, split (the page's own caption, not a grid quadrant's: --waveform-caption-split),
        // the name alone there, NAME_SPAN of the field wide, and beneath it the rest — the tagline (the break dropped) — as
        // wide, at its own size (the caption's, now); the whole, from as high as the peaks mostly rise (PEAKS) to the
        // tagline, centred in the page above its buttons: raised by `lift` (the renderers draw the field there too).
        // `middle`: the block below the line's (unraised), for the bleed's funnel.
        const parts = lines[0].words.filter(w => w.word !== BREAK.word);
        const split = named && lines.length === 1 && parts.length > 1 && parseFloat(style.getPropertyValue('--waveform-caption-split')) > 0;
        let blocks, middle, lift = 0;
        if (split) {
            const [title, ...rest] = parts;
            rest[0].before = -rest[0].left;
            size = Math.max(8, NAME_SPAN * width / ink(rest));
            const k = NAME_SPAN * width / size / (title.width - title.left - title.right);
            Object.assign(title, {
                scale: title.scale * k,
                left: title.left * k,
                right: title.right * k,
                width: title.width * k,
                steps: title.steps.map(a => a * k)
            });
            title.before = -title.left;
            const px = title.scale * size, glyphs = [...title.word].map(c => named.glyphs[c]);
            const rise = Math.max(...glyphs.map(g => g.middle + g.height / 2)),
                fall = Math.max(...glyphs.map(g => g.height / 2 - g.middle));
            const cap = named.cap * px, nameFirst = front + (SPLIT.drop + 1) * cap;
            const taglineTop = nameFirst + fall * px + SPLIT.gap * cap, taglineFirst = taglineTop + font.cap * size;
            const buttons = this.index ? Infinity : (this.view.pause?.getBoundingClientRect().top ?? Infinity) - this.canvas.getBoundingClientRect().top;   // (a quadrant: none of its own)
            const high = y0 - PEAKS * (f.lines - 1) * dy, low = taglineTop + SPLIT.tagline * cap;   // (so the field, and the name, sit alike in every theme)
            lift = Math.max(0, Math.min(high, (high + low - Math.min(h, buttons)) / 2));   // (never its peaks off the top)
            blocks = [
                {
                    lines: [{words: [title], ink: ink([title])}],
                    first: nameFirst - lift,
                    band: [nameFirst - rise * px - lift, nameFirst + fall * px - lift]
                },
                {
                    lines: [{words: rest, ink: ink(rest)}],
                    first: taglineFirst - lift,
                    band: [taglineFirst - size - lift, taglineFirst + 0.3 * size - lift]
                },
            ];
            middle = nameFirst - named.cap * px / 2;
        } else {
            const first = front + (LABEL_DROP + font.cap) * size;                // its first baseline: the capitals' tops LABEL_DROP below the line
            blocks = [{lines, first, band: [first - size, first + (lines.length - 1) * step + 0.3 * size]}];
            middle = first - font.cap * size / 2;
        }
        if (lift !== this.lift) {
            this.lift = lift;
            this.view.drawn = null;
        }   // the field raised (or not): drawn again there
        const bands = blocks.map(b => b.band).sort((a, b) => a[0] - b[0]), top = bands[0][0],
            bottom = Math.max(...bands.map(b => b[1]));
        // Placed: a tspan a word, an x a letter; each line centred.
        const ns = 'http://www.w3.org/2000/svg', words = [];
        for (const block of blocks) block.lines.forEach((line, k) => {
            let x = snap(x0 + (width - line.ink * size) / 2);
            for (const w of line.words) {
                x += w.before * size;
                w.xs = [snap(x)];                               // (a pixel font's words each on whole device pixels: its ink starts off them, at its corners' bevels)
                for (const s of w.steps) w.xs.push(w.xs.at(-1) + s * size);
                w.y = snap(block.first + k * step + w.shift * size);
                x += w.width * size;
                words.push(w);
            }
        });
        text.replaceChildren(...words.map(w => {
            const span = document.createElementNS(ns, 'tspan');
            span.setAttribute('x', w.xs.map(v => v.toFixed(2)).join(' '));
            span.setAttribute('y', w.y);
            if (w.scale !== 1) span.setAttribute('font-size', size * w.scale);
            if (w.name) span.classList.add('waveform-name');   // (its font: the CSS's)
            if (w.stroke) {
                span.classList.add('waveform-break');
                span.style.setProperty('--stroke', `${w.stroke * size}px`);
            }
            span.textContent = w.word;
            return span;
        }));
        text.setAttribute('font-size', size);
        this.funnelRate = split ? BLEED.name : BLEED.funnel;  // how fast the ridges' light spreads as it falls (the shader's)…
        this.funnel = this.funnelRate * (middle - front) / dx;   // …and how far at its middle, in points (#bleedCaption)
        if (this.renderer?.caption && this.bleed > 0) {       // (drawn only where the theme bleeds into it) just after this frame, not in it
            clearTimeout(this.maskTimer);
            this.maskTimer = setTimeout(() => this.#captionMask(words, size, {
                left: x0,
                right: x0 + width,
                bands,
                soften: Number.isFinite(soften) ? soften : SOFTEN
            }));
        }
        // The glow: copies of the caption behind it, each a wider stroke under a wider blur and fainter as it widens — so it
        // spreads evenly out from the letters' shapes.
        const glow = svg.querySelector('.waveform-glow'), defs = svg.querySelector('defs');
        glow.replaceChildren();
        defs.replaceChildren();
        // The bleed's gradient, across the caption as the field is across (see #bleed); the CSS paints with it where a theme does.
        const bleed = document.createElementNS(ns, 'linearGradient');
        for (const [k, v] of Object.entries({
            id: `waveform-bleed-${this.index}`,
            gradientUnits: 'userSpaceOnUse',
            x1: x0,
            y1: 0,
            x2: x0 + width,
            y2: 0
        })) bleed.setAttribute(k, v);
        this.bleedStops = Array.from({length: BLEED.stops}, (_, i) => {
            const stop = bleed.appendChild(document.createElementNS(ns, 'stop'));
            stop.setAttribute('offset', i / (BLEED.stops - 1));
            stop.setAttribute('stop-color', this.ink.css);
            return stop;
        });
        defs.append(bleed);
        GLOW.forEach(([stroke, blur, opacity], i) => {
            const filter = document.createElementNS(ns, 'filter'),
                gauss = document.createElementNS(ns, 'feGaussianBlur');
            filter.id = `waveform-glow-${this.index}-${i}`;
            for (const [k, v] of Object.entries({
                filterUnits: 'userSpaceOnUse',
                x: x0 - 4 * size,
                y: top - 4 * size,
                width: width + 8 * size,
                height: bottom - top + 8 * size
            }))
                filter.setAttribute(k, v);
            gauss.setAttribute('stdDeviation', blur * size);
            filter.append(gauss);
            defs.append(filter);
            const copy = text.cloneNode(true);
            copy.removeAttribute('data-label');
            copy.setAttribute('stroke-width', stroke * size);
            copy.setAttribute('opacity', opacity);
            copy.setAttribute('filter', `url(#${filter.id})`);
            glow.append(copy);
        });
    }

    /** The caption as a mask, for a renderer that draws it itself (WebGPU: lit past SDR white): the same layout, drawn in
     *  device pixels — the letters' coverage red, softened a little (SOFTEN), their glow's green (the GLOW layers, each a
     *  wider stroke under a wider blur, fainter as it widens), summed, their black halo's blue (HALO) — margined for the
     *  glow; handed over with where it goes. A band a block of its lines (`bands`, page coordinates, top to bottom: a
     *  tagline above the field, a name below it), stacked — the field between them left out, not blurred for nothing.
     *  Each layer is drawn sharp here and blurred (gaussianBlur): canvas filters and shadows blur differently from
     *  browser to browser (Safari's filters not at all). The blurs — the cost, a few hundred ms — are the mask worker's
     *  (composeMask), the ridges playing on meanwhile, the caption as it was till the mask comes back; a mask like the one
     *  drawn already (amber's is green's) isn't made again. */
    #captionMask(words, size, {left, right, bands, soften}) {
        const dpr = window.devicePixelRatio || 1, pad = 3.5 * size;          // the glow's reach: its widest stroke, three of its widest blurs
        const x = Math.floor((left - pad) * dpr), w = Math.ceil((right + pad) * dpr) - x;
        const spans = [];                                                     // device px, from and to: two that would overlap, one
        for (const [top, bottom] of bands) {
            const from = Math.floor((top - pad) * dpr), to = Math.ceil((bottom + pad) * dpr), last = spans.at(-1);
            if (last && from <= last[1]) last[1] = Math.max(last[1], to); else spans.push([from, to]);
        }
        const key = JSON.stringify([dpr, size, soften, x, w, spans,
            words.map(d => [d.word, d.family, d.weight, d.scale, d.stroke, d.xs, d.y,
                document.fonts.check(`${d.weight} 10px ${d.family}`, d.word)])]);   // (drawn in its own font once loaded)
        if (key === this.maskKey) return;
        this.maskKey = key;
        const scratch = this.maskScratch ??= document.createElement('canvas');
        const layers = [...GLOW.map(([stroke, blur, opacity]) => [1, opacity, stroke, blur]), [2, 1, HALO.width, HALO.blur], [0, 1, 0, soften]];
        const job = {id: ++maskJobs, w, bands: []}, buffers = [];
        for (const [from, to] of spans) {
            const h = to - from, batches = [];
            Object.assign(scratch, {width: w, height: h});
            const s = scratch.getContext('2d', {willReadFrequently: true});
            // Three layers at a time, one to a colour of the scratch canvas: one read back for the three.
            for (let first = 0; first < layers.length; first += 3) {
                const batch = layers.slice(first, first + 3);
                s.setTransform(1, 0, 0, 1, 0, 0);
                s.globalCompositeOperation = 'source-over';
                s.fillStyle = '#000';
                s.fillRect(0, 0, w, h);
                s.setTransform(dpr, 0, 0, dpr, -x, -from);                        // the page's coordinates
                s.globalCompositeOperation = 'lighter';
                s.lineJoin = 'round';
                batch.forEach(([, , widen], k) => {
                    s.fillStyle = s.strokeStyle = ['#f00', '#0f0', '#00f'][k];
                    for (const word of words) {
                        const line = word.stroke * size + widen * size;   // (a width of 0 isn't one: canvas keeps the last)
                        s.font = `${word.weight} ${size * word.scale}px ${word.family}`;
                        if (line > 0) s.lineWidth = line;
                        [...word.word].forEach((c, i) => {
                            s.fillText(c, word.xs[i], word.y);
                            if (line > 0) s.strokeText(c, word.xs[i], word.y);
                        });
                    }
                });
                const data = s.getImageData(0, 0, w, h).data;
                batches.push({data, layers: batch.map(([channel, alpha, , blur]) => [channel, alpha, blur * size * dpr])});
                buffers.push(data.buffer);
            }
            job.bands.push({h, batches});
        }
        const height = spans.reduce((sum, [from, to]) => sum + to - from, 0);
        const place = px => {
            if (this.disposed || this.maskKey !== key || !this.renderer?.caption) return;   // laid out again meanwhile: that one's
            if (!px) {                                // the worker failed: made again, here
                this.maskKey = this.labelKey = null;
                return;
            }
            const canvas = this.maskCanvas ??= document.createElement('canvas');
            Object.assign(canvas, {width: w, height});
            canvas.getContext('2d').putImageData(new ImageData(px, w, height), 0, 0);
            const [[top, end], second] = spans;
            this.renderer.caption(canvas, [x, top, w, spans.at(-1)[1] - top], second ? [end - top, second[0] - end] : [0, 0]);
        };
        const worker = maskWorkerOf();
        if (!worker) return place(composeMask(job));
        worker.pending.set(job.id, place);
        worker.postMessage(job, buffers);
    }

    /** The ridges' light bled into the caption beneath them: at each point across, the front lines' (BLEED.lines, nearer
     *  counting more; spread across as at the caption's middle, BLEED.funnel) as the HDR shader brightens them — by height, pumped by the kick — takes the caption from its ink,
     *  dimmed a little, toward the phosphor's white-hot. Only stops whose colour changes are touched. */
    #bleedCaption(f) {
        const stops = this.bleedStops, ink = this.ink.rgb, page = this.background.rgb;
        if (!stops) return;
        const base = ink.map((c, i) => c * 0.8 + page[i] * 0.2), hot = ink.map(c => c + (1 - c) * 0.75);
        const at = s => {                              // the front lines' light at point s
            s = Math.round(Math.min(f.points - 1, Math.max(0, s)));
            let light = 0;
            for (let k = 0, weight = 1; k < BLEED.lines && k < f.lines; k++, weight *= 0.6)
                light += weight * Math.pow(Math.max(0, f.heights[(f.lines - 1 - k) * f.points + s]) / 255, 1.2);
            return light;
        };
        for (let i = 0; i < stops.length; i++) {
            const s = i / (stops.length - 1) * (f.points - 1);
            let light = 0, total = 0;                    // spread as at the caption's middle (the funnel: see the shader)
            for (let o = -1; o <= 1; o += 0.25) {
                const weight = Math.exp(-2 * o * o);
                light += weight * at(s + o * (this.funnel ?? 0));
                total += weight;
            }
            const t = Math.min(1, light / total * (0.6 + 0.4 * f.pump) * this.bleed);
            const css = `rgb(${base.map((c, j) => Math.round((c + (hot[j] - c) * t) * 255)).join(' ')})`;
            if (stops[i].css !== css) {
                stops[i].setAttribute('stop-color', css);
                stops[i].css = css;
            }
        }
    }

}

class WaveformView {
    constructor(tapline, scene, canvas, hud, details, status, sound, pause, dotnet) {
        Object.assign(this, {tapline, canvas, hud, details, status, sound, pause, dotnet});
        this.paused = false;
        this.pauseTurn = 0;
        this.sceneId = scene.tapId;
        this.clock = new CarrierClock();
        this.received = new Rate();
        this.painted = new Rate();
        this.refreshes = new Rate();
        this.bytes = 0;
        this.frames = 0;
        this.ages = new Float64Array(1200);
        this.ageCount = 0;   // age at paint, last 1,200 paints
        this.latest = null;
        this.drawn = null;
        this.drawnCarrier = null;
        this.history = [];
        this.gaps = [];
        this.mix = null;   // frames by carrier time, blended at the moment shown (#moment)
        this.interp = 0;
        this.interpTarget = 0;     // how far behind the newest moment to show, ms (see #onScene)
        this.occlude = false;
        this.hdr = true;
        this.root = canvas.parentElement;
        this.resized = new ResizeObserver(() => this.#fit());
        this.resized.observe(canvas);
        this.page = new Pane(this, this.root, canvas, this.root.querySelector(':scope > .waveform-label'), 0);   // its own: in the page's theme
        this.grid = null;                             // the grid's four, made when first shown
        this.panes = [this.page];                     // those shown
        this.radio = null;
        this.offScene = tapline.subscribe(scene, value => this.#onScene(value));
        this.sceneId = scene.tapId;
        this.sceneService = scene.nodeId;
        this.service = scene.service ?? 'waveform';
        tapline.onServiceDown = () => this.#down();                   // the station gone (restarted): wait for it…
        tapline.onServiceUp = service => {
            if (service === this.service) this.#rejoin();
        };   // …then subscribe again
        tapline.onServiceDraining = () => this.#move();               // its station draining: move — make before break
        tapline.onServiceDrained = id => {                            // not moved in time (or no grace): onto another, now
            if (id === this.sceneService || id === this.radio?.service) {
                this.down = true;
                this.#rejoin();
            }
        };
        tapline.onEnded = () => {
            this.down = true;
            this.#rejoin();
        };   // the router ended our subscriptions (this page stalled): again
        this.onClick = () => this.#toggleSound();
        sound.addEventListener('click', this.onClick);
        this.onPause = () => this.#togglePause();
        pause.addEventListener('click', this.onPause);
        this.onKey = e => {
            if (e.metaKey || e.ctrlKey) return;
            if (e.key === 'o') {
                this.occlude = !this.occlude;
                this.drawn = null;
            }
            if (e.key === 'h') {
                this.hdr = !this.hdr;
                this.drawn = null;
            }
            if (e.key === 't') {
                clearInterval(this.cycle);
                this.cycle = null;
                this.#nextTheme();
            }   // taken over: no more cycling
            if (e.key === 'f') this.#nextFont();
            if (e.key === 'i') {
                this.root.classList.toggle('waveform-detailed');
                this.#report();
                this.#status();
            }
            if (e.key === 'p') this.#togglePause();
        };
        window.addEventListener('keydown', this.onKey);
        this.reportedHz = null;
        this.throttled = false;
        this.regularSince = performance.now();
        this.lastRaf = 0;
        this.onVisibility = () => {
            if (!document.hidden) {
                this.throttled = false;
                this.regularSince = performance.now();
                this.lastRaf = 0;
                this.refreshes = new Rate();
            }
            this.#hold(document.hidden);
            this.#display();
        };
        document.addEventListener('visibilitychange', this.onVisibility);
        this.#hold(document.hidden);                                  // (a page opened in the background starts held)
        this.raf = requestAnimationFrame(t => this.#paint(t));
        this.timer = setInterval(() => {
            this.#report();
            this.#display();
        }, 500);
        // The server's status only while the full stats show (i): no traffic for it otherwise.
        const saved = savedTheme();                                   // as it was left: the theme, and whether it cycles
        this.themeIndex = Math.max(0, THEMES.indexOf(saved?.theme));
        this.fontIndex = savedFont();                                 // and the caption's font
        document.documentElement.dataset.waveformFont = FONTS[this.fontIndex];
        this.#applyTheme();
        this.root.classList.toggle('waveform-chromium', CHROMIUM);   // (its HDR kept composited: see .waveform-hdr-anchor)
        this.cycle = saved?.cycling === false ? null : setInterval(() => this.#nextTheme(), THEME_CYCLE_MS);
        this.onEnter = () => {
            this.shown = true;
            this.#status();
        };
        this.onLeave = () => {
            this.shown = false;
        };
        this.root.addEventListener('mouseenter', this.onEnter);
        this.root.addEventListener('mouseleave', this.onLeave);
        this.statusTimer = setInterval(() => this.#status(), 1000);
    }

    async #status() {
        if (!this.shown || !this.root.classList.contains('waveform-detailed') || this.fetching) return;
        this.fetching = true;
        try {
            this.status.textContent = await this.dotnet.invokeMethodAsync('Status');
        } catch {
        } finally {
            this.fetching = false;
        }
    }

    /**
     * The page held — hidden, or covered (refreshes all but stopped): the server sends a frame a second (Display) — or
     * back. The held frames' spacing is the cap's, not the path's: not counted, and back, the picture is as far behind as
     * it was before (not a second behind, easing down for 10 s). A frame sent at a second's spacing may still arrive
     * just after: for 2 s, gaps over 250 ms aren't counted either.
     */
    #hold(on) {
        if (on === !!this.held) return;
        this.held = on;
        if (on) {
            this.interpBeforeHold = this.interp;
            return;
        }
        this.gaps.length = 0;
        this.interp = this.interpTarget = this.interpBeforeHold ?? this.interp;
        this.settleUntil = performance.now() + 2000;
    }

    /**
     * The ridges paused (p, the button) or played: paused, both subscriptions — the ridges' and the music's — end, here and
     * at the server (the station stops producing what nobody listens to); played, both start again through the station
     * (step 1), the next frame the live one. The picture holds the last frame; the first one after isn't counted as a late
     * arrival (it would delay the picture by the pause). The music's output fades meanwhile (RowRadio.pause).
     */
    async #togglePause() {
        const paused = this.paused = !this.paused, turn = ++this.pauseTurn;   // a quick pause-play: the last one counts
        this.pause.setAttribute('aria-pressed', String(paused));
        this.pause.setAttribute('aria-label', paused ? 'Play' : 'Pause');
        this.pause.title = paused ? 'Play (p)' : 'Pause (p)';
        this.#report();
        const radio = this.radio?.resume ? this.radio : null;
        try {
            if (paused) {                                               // unsubscribed: here, and (the routes) at the server
                this.offScene?.();
                this.offScene = null;
                if (this.sceneId !== undefined) this.tapline.drop(this.sceneId);
                this.sceneId = undefined;
                radio?.pause();
                await this.dotnet.invokeMethodAsync('Ridges', false);
                if (radio) await this.dotnet.invokeMethodAsync('Listen', false);
                return;
            }
            this.skipGap = true;                                        // subscribed again: the first frame is a fresh start
            const scene = await this.dotnet.invokeMethodAsync('Ridges', true);
            if (turn !== this.pauseTurn) return;                        // paused again meanwhile (its unsubscribe follows)
            if (scene) {
                this.#subscribeScene(scene);
                this.down = false;
                clearTimeout(this.downRetry);
            }
            if (radio) radio.resume(await this.dotnet.invokeMethodAsync('Listen', true));
        } catch {
        }                                                   // the circuit is gone (or the station: its up notice rejoins)
    }

    /**
     * The station is down — the router found it gone (restarted, silent): both subscriptions are over (their onError ran).
     * Wait for the router to say the service is up again (onServiceUp — which it may hold back until there's room, so a
     * crowd of viewers doesn't land at once), then subscribe again as play does; nothing while paused (play will). Should
     * that notice never come, try again after 30 s.
     */
    #down() {
        this.down = true;
        clearTimeout(this.downRetry);
        this.downRetry = setTimeout(() => this.#rejoin(), 30000);
    }

    async #rejoin() {
        if (!this.down || this.paused || this.rejoining) return;
        this.rejoining = true;
        clearTimeout(this.downRetry);
        const radio = this.radio?.resume ? this.radio : null, turn = this.pauseTurn;
        try {
            this.skipGap = true;
            const scene = await this.dotnet.invokeMethodAsync('Ridges', true);
            if (this.paused || turn !== this.pauseTurn) return;        // paused (or played) meanwhile: that took over
            if (!scene) throw new Error('no station serving');          // (drained, and no replacement yet: wait for up)
            this.#subscribeScene(scene);
            if (radio) radio.resume(await this.dotnet.invokeMethodAsync('Listen', true));
            this.down = false;
        } catch {                                                     // unavailable still: the next up notice (or this) tries again
            this.downRetry = setTimeout(() => this.#rejoin(), 30000);
        } finally {
            this.rejoining = false;
        }
    }

    #subscribeScene(scene) {
        this.sceneId = scene.tapId;
        this.sceneService = scene.nodeId;
        this.offScene = this.tapline.subscribe(scene, value => this.#onScene(value));
    }

    /**
     * The station draining (blue/green: its successor on air): move, make before break — subscribe again, onto the station
     * taking subscribers, and let the old subscription go once the new one streams (tapline.replace). Both stations play on
     * one carrier clock, row for row: the ridges' frames are ordered by it, the music's rows numbered alike — so the
     * overlap neither shows nor sounds. Nothing while paused (nothing subscribed); should no station take it, the old one
     * streams on till its grace runs out — then "drained", and the up notice.
     */
    async #move() {
        if (this.paused || this.moving) return;
        this.moving = true;
        try {
            const scene = await this.dotnet.invokeMethodAsync('MoveRidges');
            if (scene && !this.paused) {
                const old = this.sceneId;
                this.sceneId = scene.tapId;
                this.sceneService = scene.nodeId;
                this.offScene = this.tapline.replace(old, scene, value => this.#onScene(value));
            }
            const radio = this.radio?.move ? this.radio : null;
            if (radio?.audioId !== undefined && !this.paused) {
                const music = await this.dotnet.invokeMethodAsync('MoveMusic');
                if (music) radio.move(music);
            }
        } catch {
        }                                                   // the circuit is gone
        finally {
            this.moving = false;
        }
    }

    /** Another representation of the ridges (full, lite or tiny): subscribe to it, then let the old one go. */
    retune(scene) {
        const off = this.offScene, id = this.sceneId;
        this.sceneId = scene.tapId;
        this.sceneService = scene.nodeId;
        this.offScene = this.tapline.subscribe(scene, value => this.#onScene(value));
        off?.();
        if (id !== undefined) this.tapline.drop(id);
    }

    detach() {
        this.tapline.onServiceDown = this.tapline.onServiceUp = this.tapline.onServiceDrained = this.tapline.onServiceDraining = null;
        clearTimeout(this.downRetry);
        cancelAnimationFrame(this.raf);
        this.resized.disconnect();
        clearInterval(this.timer);
        clearInterval(this.statusTimer);
        for (const pane of [this.page, ...(this.grid ?? [])]) pane.dispose();
        this.root.querySelector(':scope > .waveform-grid')?.remove();
        clearInterval(this.cycle);
        this.root.removeEventListener('mouseenter', this.onEnter);
        this.root.removeEventListener('mouseleave', this.onLeave);
        document.removeEventListener('visibilitychange', this.onVisibility);
        this.offScene?.();
        if (this.sceneId !== undefined) this.tapline.drop(this.sceneId);
        this.sound.removeEventListener('click', this.onClick);
        this.pause.removeEventListener('click', this.onPause);
        window.removeEventListener('keydown', this.onKey);
        this.radio?.stop();
    }

    // ── ridges ──

    /** How far apart to draw the lines — with the window's size, at least 0.5em — and how many: those that fit the field
     *  (FIELD: four-sevenths of the page's height, 16:10, at most three-fifths of its width), at most 60. */
    #fit() {
        const canvas = this.panes[0].canvas;         // (the grid's four are one size: they share the spacing, and the frame)
        const em = parseFloat(getComputedStyle(canvas).fontSize) || 16, area = canvas.clientWidth * canvas.clientHeight;
        if (!area) return;
        const gap = Math.max(MIN_GAP_EM * em, LINE_GAP_EM * em * Math.sqrt(area / REFERENCE_AREA));
        const field = Math.min(canvas.clientHeight * FIELD.height, canvas.clientWidth * FIELD.width / FIELD.aspect);   // its height
        this.lineCount = Math.max(MIN_LINES, Math.min(MAX_LINES, Math.floor(field / gap) + 1));
        for (const pane of this.panes) {
            pane.gap = gap;
            pane.lineVar();
        }
        this.drawn = null;                           // redraw at the new size
    }

    #onScene(value) {
        const now = performance.now();
        const dv = new DataView(value.buffer, value.byteOffset, value.byteLength);
        if (value.byteLength < HEADER || dv.getUint32(0, true) !== SCENE) return;
        const carrier = dv.getFloat64(8, true);
        this.clock.sample(now - carrier);
        this.received.add(now);
        this.bytes += value.byteLength;
        this.frames++;
        const h = this.history, last = h.at(-1);
        if (last && carrier <= last.carrier) return;  // not newer (a retune's overlap)
        const late = last ? now - this.clock.offset - last.carrier : 0;
        // The page's own thread busy (no refresh for 50 ms: a long task, a collection): the frames queued meanwhile come
        // all at once, late — the page's delay, not the path's. Shown further behind for it, nothing more would be drawn
        // in time (the page couldn't draw), only later from then on.
        const stalled = this.lastRaf > 0 && now - this.lastRaf > 50 && !this.throttled;
        if (this.skipGap) this.skipGap = false;       // the first frame after a pause: its gap is the pause, not the path
        else if (last && !this.held && !stalled && !(now < this.settleUntil && late > 250)) {   // held, a frame a second: the cap, not the path
            // How long after the last frame's moment this one arrived: shown that far behind, the moment between them can be
            // drawn — the next frame is there. The 95th percentile of the last 5 s, 5 ms to spare: raised at once, eased down.
            const g = this.gaps;
            g.push({at: now, v: late});
            while (g[0].at < now - 5000) g.shift();
            const sorted = g.map(x => x.v).sort((x, y) => x - y);
            this.interpTarget = sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95))] + 5;
            if (this.interpTarget > this.interp) this.interp = this.interpTarget;
        }
        h.push({carrier, bytes: value.slice()});
        while (h.length > 2 && h[0].carrier < carrier - 5000) h.shift();   // bounded while nothing is drawn (a hidden page)
    }

    /** A frame's heights at the page's line count: decoded once (range decoder and spline, well under a millisecond),
     *  resampled if the window has changed size since. */
    #decoded(frame) {
        let f = frame.decoded;
        if (!f) {
            if (frame.bad) return null;
            const bytes = frame.bytes, dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
            const fieldLines = bytes[16], points = bytes[17], lineStep = bytes[19] || 1, pointStep = bytes[38] || 1;
            const m = Math.floor((fieldLines - 2) / lineStep) + 2, n = Math.floor((points - 2) / pointStep) + 2;
            const began = performance.now();
            try {
                f = frame.decoded = {
                    fieldLines,
                    points,
                    lineStep,
                    pointStep,
                    m,
                    n,
                    grid: decodeGrid(bytes.subarray(HEADER), m, n, bytes[39] || 1),
                    carrier: frame.carrier,
                    bar: bytes[18],
                    pump: bytes[36] / 255,
                    width: dv.getFloat32(20, true),
                    fullScale: dv.getFloat32(24, true),
                    spread: dv.getFloat32(28, true),
                    fade: dv.getFloat32(32, true),
                };
                resample(f, this.lineCount);
                this.latest = f;
            } catch (e) {
                console.warn('waveform: frame dropped', e);
                frame.bad = true;
                return null;
            }
            this.decodeMs = (this.decodeMs ?? 0) * 0.9 + (performance.now() - began) * 0.1;   // what a frame costs this browser
        }
        if (f.lines !== this.lineCount) resample(f, this.lineCount);
        return f;
    }

    /**
     * What to draw now: the field at the moment `interp` behind the station's clock — blended between the frames either
     * side of it (the next has nearly always arrived: that is how `interp` is chosen) — or, with none after it yet, the
     * newest, held. While the music plays, at least as far behind as it is heard: in step with it whenever the frames come
     * often enough to allow it.
     */
    #moment(now) {
        const h = this.history;
        if (!h.length || this.clock.offset === null || !this.lineCount) return null;   // (no lines yet: a page opened unseen, unsized)
        const dt = now - (this.momentAt ?? now);
        this.momentAt = now;
        if (this.interpTarget < this.interp) this.interp = Math.max(this.interpTarget, this.interp - dt * 0.1);   // the picture runs 10% fast meanwhile
        const at = now - this.clock.offset - Math.max(this.interp, this.radio?.delay ?? 0);   // carrier time to show
        while (h.length > 2 && h[1].carrier <= at) h.shift();
        const a = h[0], b = h[1];
        if (!b || at <= a.carrier) return this.#decoded(a);
        if (at >= b.carrier) return this.#decoded(b) ?? this.#decoded(a);
        const fa = this.#decoded(a), fb = this.#decoded(b);
        if (!fa || !fb) return fb ?? fa;
        const w = (at - a.carrier) / (b.carrier - a.carrier);
        let m = this.mix;
        if (!m || m.heights.length !== fb.heights.length) m = this.mix = {heights: new Float32Array(fb.heights.length)};
        const heights = m.heights;
        Object.assign(m, fb, {heights, carrier: at, pump: fa.pump + w * (fb.pump - fa.pump)});
        for (let i = 0; i < heights.length; i++) heights[i] = fa.heights[i] + w * (fb.heights[i] - fa.heights[i]);
        return m;
    }

    #paint(t) {
        this.raf = requestAnimationFrame(tt => this.#paint(tt));
        this.refreshes.add(t);
        const gap = this.lastRaf ? t - this.lastRaf : 0;
        this.lastRaf = t;
        if (gap > 250 && !this.throttled) {
            this.throttled = true;
            this.#hold(true);
            this.#display();
        }   // refreshes all but stopped (covered)
        else if (gap > 0 && gap < 50 && this.throttled) {                                    // …and back
            this.throttled = false;
            this.regularSince = performance.now();
            this.refreshes = new Rate();
            this.#hold(document.hidden);
            this.#display();
        }
        const began = performance.now();
        try {
            const f = this.#moment(began);
            if (!f || !this.panes.some(p => p.renderer)) return;
            this.ages[this.ageCount++ % this.ages.length] = performance.now() - this.clock.toLocal(f.carrier);
            if (f === this.drawn && f.carrier === this.drawnCarrier && !this.panes.some(p => p.renderer?.easing?.(p))) return;   // a frame held: drawn already (its highlights settled)
            for (const pane of this.panes) if (pane.renderer) pane.draw(f);
            if (!this.drawn) this.root.classList.add('waveform-live');   // fade in from black (the CSS transition) as the first frame lands
            this.drawn = f;
            this.drawnCarrier = f.carrier;
            this.painted.add(t);
        } finally {
            this.busy = (this.busy ?? 0) + performance.now() - began;   // the main thread's time on the ridges (decode, blend, draw)
        }
    }

    #nextTheme() {
        this.themeIndex = (this.themeIndex + 1) % THEMES.length;
        this.#applyTheme();
        this.#report();
        try {
            localStorage.setItem(THEME_KEY, JSON.stringify({
                theme: THEMES[this.themeIndex],
                cycling: this.cycle !== null
            }));
        } catch {
        }
    }

    /** The caption's next font (its data-waveform-font: the CSS takes it from there), laid out again in it once it's loaded. */
    #nextFont() {
        this.fontIndex = (this.fontIndex + 1) % FONTS.length;
        document.documentElement.dataset.waveformFont = FONTS[this.fontIndex];
        for (const pane of this.panes) pane.labelKey = null;
        this.drawn = null;
        this.#report();
        try {
            localStorage.setItem(FONT_KEY, FONTS[this.fontIndex]);
        } catch {
        }
    }

    /**
     * The page's theme (its data-waveform-theme: the CSS takes it from there) — or the grid: the four at once, a quadrant
     * each (the CSS shows the grid, not the page's canvas). Its panes are made the first time; the frame is the same.
     */
    #applyTheme() {
        const theme = THEMES[this.themeIndex];
        document.documentElement.dataset.waveformTheme = theme;
        this.panes = theme === 'grid' ? this.grid ??= this.#makeGrid() : [this.page];
        this.#fit();
        for (const pane of this.panes) {
            pane.labelKey = null;
            pane.theme();
        }
    }

    /** The grid: a quadrant a theme — each a canvas and a copy of the caption (the page's is the one read out). */
    #makeGrid() {
        const grid = document.createElement('div');
        grid.className = 'waveform-grid';
        const panes = GRID.map((theme, i) => {
            const cell = grid.appendChild(document.createElement('div')), canvas = document.createElement('canvas');
            cell.className = 'waveform-pane';
            cell.dataset.waveformTheme = theme;
            const svg = this.page.svg.cloneNode(true);
            svg.removeAttribute('role');
            svg.removeAttribute('aria-label');
            svg.setAttribute('aria-hidden', 'true');
            svg.querySelector(':scope > defs').replaceChildren();
            svg.querySelector(':scope > .waveform-glow').replaceChildren();
            svg.querySelector(':scope > text').replaceChildren();
            cell.append(canvas, svg);
            this.resized.observe(canvas);
            return new Pane(this, cell, canvas, svg, i + 1);
        });
        this.page.svg.after(grid);
        return panes;
    }

    /** The caption's font, measured per px of font size: each of its letters' advance and ink — its insets from the
     *  advance either side (side bearings), its height, its middle above the baseline, (the break's) its width across
     *  halfway up: its stroke's — and the capitals' height. Measured again until its font has loaded (the fallback's metrics
     *  aren't its own). */
    fontMetrics(family, weight, label) {
        const key = `${weight} ${family}|${label}`, cached = (this.fonts ??= new Map()).get(key);   // (the grid's themes: more than one)
        if (cached?.loaded) return cached;
        const g = (this.fontCanvas ??= document.createElement('canvas')).getContext('2d');
        g.font = `${weight} 100px ${family}`;
        const across = c => {                          // the ink's width halfway up, from its pixels
            const k = 200, ink = (this.inkCanvas ??= Object.assign(document.createElement('canvas'), {
                width: k,
                height: k
            })).getContext('2d', {willReadFrequently: true});
            ink.clearRect(0, 0, k, k);
            ink.font = `${weight} ${k / 2}px ${family}`;
            const m = ink.measureText(c),
                row = Math.round(k / 2 - (m.actualBoundingBoxAscent - m.actualBoundingBoxDescent) / 2);
            ink.fillText(c, k / 4, k / 2);
            const d = ink.getImageData(0, row, k, 1).data;
            let sum = 0;
            for (let i = 3; i < d.length; i += 4) sum += d[i] / 255;
            return sum / (k / 2);
        };
        const glyphs = {};
        for (const c of new Set(label.replace(/[ |]/g, ''))) {
            const m = g.measureText(c);
            glyphs[c] = {
                advance: m.width / 100,
                left: -m.actualBoundingBoxLeft / 100,
                right: (m.width - m.actualBoundingBoxRight) / 100,
                height: (m.actualBoundingBoxAscent + m.actualBoundingBoxDescent) / 100,
                middle: (m.actualBoundingBoxAscent - m.actualBoundingBoxDescent) / 200,
                thickness: BREAK.word.includes(c) ? across(c) : 0,
            };
        }
        const cap = g.measureText('H').actualBoundingBoxAscent / 100 || 0.7;
        const font = {
            family,
            weight,
            label,
            loaded: document.fonts.check(`${weight} 100px ${family}`, label),
            glyphs,
            cap
        };
        this.fonts.set(key, font);
        return font;
    }

    /**
     * Tells the server what this page can show: 0 while it isn't painted (hidden or throttled), the display's refresh once
     * 2 s of regular refreshes have been counted, null (unknown) until then. Only changes are sent.
     */
    #display() {
        const hz = document.hidden || this.throttled ? 0
            : performance.now() - this.regularSince > 2500 ? Math.round(this.refreshes.value) : null;
        const last = this.reportedHz;
        if (hz === last || hz > 0 && last > 0 && Math.abs(hz - last) < Math.max(3, last / 10)) return;
        this.reportedHz = hz;
        this.dotnet.invokeMethodAsync('Display', hz).catch(() => {
        });
    }

    /** The hover's line — frames a second received, the data rate — and, while shown (i), the full stats. */
    #report() {
        const rate = this.tapline.transferRate / 1024;
        this.hud.textContent = `${this.paused ? 'paused' : `${Math.round(this.received.value)} fps`} · ${rate.toFixed(rate < 10 ? 1 : 0)} KiB/s`;
        if (!this.root?.classList.contains('waveform-detailed')) {
            this.reportedAt = 0;
            return;
        }
        const now = performance.now(), busy = this.busy ?? 0;   // main-thread ms a second on the ridges, since the last report
        const load = this.reportedAt ? (busy - this.reportedBusy) / ((now - this.reportedAt) / 1000) : 0;
        this.reportedAt = now;
        this.reportedBusy = busy;
        const n = Math.min(this.ageCount, this.ages.length);
        let age = '';
        if (n > 0) {
            const sorted = Array.from(this.ages.subarray(0, n)).sort((a, b) => a - b);
            const q = p => sorted[Math.min(n - 1, Math.floor(p * n))].toFixed(1);
            age = `age at paint p50 ${q(0.5)} · p99.5 ${q(0.995)} ms`;
        }
        const kib = this.frames ? (this.bytes / this.frames / 1024).toFixed(2) : '–';
        const f = this.latest;
        this.details.textContent = [
            `ridges   ${f ? `${f.lines} lines × ${f.points} splined from ${f.m}×${f.n} · ` : ''}` +
            `${this.received.value.toFixed(1)} frames/s received, shown blended ${Math.round(Math.max(this.interp, this.radio?.delay ?? 0))} ms behind` +
            `${this.radio?.delay > 0 ? this.radio.delay >= this.interp ? ' (in step with the music)' : ` (the music ${this.radio.delay})` : ''} · ` +
            `${this.painted.value.toFixed(1)}/s drawn (display ${this.refreshes.value.toFixed(0)} Hz) · decode ${(this.decodeMs ?? 0).toFixed(1)} ms · main thread ${load.toFixed(0)} ms/s · ${age} · ${kib} KiB/frame`,
            this.radio ? this.radio.report() : 'music    off',
            `received ${(this.tapline.transferRate / 1024).toFixed(1)} KiB/s` +
            (this.clock.offset === null ? '' : ` · carrier offset ${this.clock.offset.toFixed(1)} ms`) +
            ` · ${this.panes[0].renderer?.label ?? '…'}` + (this.panes[0].renderer instanceof HdrRenderer ? ` · h: HDR ${this.hdr ? 'on' : 'off'}` : '') +
            ` · o: occlude ${this.occlude ? 'on' : 'off'} · t: theme ${THEMES[this.themeIndex]}${this.cycle ? ', cycling' : ''}` +
            ` · f: font ${FONT_NAMES[this.fontIndex]}`,
        ].join('\n');
    }

    // ── music ──

    async #toggleSound() {
        if (this.radio) {
            this.radio.stop();
            this.radio = null;
            this.sound.setAttribute('aria-pressed', 'false');
            try {
                await this.dotnet.invokeMethodAsync('Listen', false);
            } catch {
            }
            return;
        }
        // Browsers start audio only from a user gesture: the context starts here, in the click.
        const ctx = new AudioContext({latencyHint: 'interactive'});
        ctx.resume();
        const token = this.radio = {stop: () => ctx.close(), report: () => 'music    starting…'};
        this.sound.setAttribute('aria-pressed', 'true');
        try {
            const binding = this.paused ? null : await this.dotnet.invokeMethodAsync('Listen', true);
            if (this.radio !== token) return;          // turned off meanwhile
            const radio = new NoteRadio(this.tapline, this.clock, ctx);
            this.radio = radio;
            if (this.paused) {                         // sound on while paused (or paused meanwhile): subscribed on play
                radio.mute(true);
                if (binding) await this.dotnet.invokeMethodAsync('Listen', false);
                return;
            }
            await radio.tune(binding);
        } catch (e) {
            console.warn('waveform: no music', e);
        }
    }
}

/**
 * Music as rows of a rolling window — a MIDI piece's notes — played on the carrier clock behind a jitter buffer: the rows'
 * parsing and playing are the subclass's (dueOf, spanOf, scheduleRow, describe).
 */
class RowRadio {
    // at least Floor; at most 2 s in all. Set after Warmup ms of rows, before anything plays.
    static Floor = 20;

    // The playout delay, ms: the output's own latency (lag: Bluetooth headphones add ~200 ms) plus a jitter buffer of
    // what the path needs — no more: the latest arrival seen (in the last Window ms), Safety for scheduling, Spare on top;
    static Max = 2000;
    static Safety = 20;
    static Warmup = 300;
    static Spare = 1.1;
    static Window = 10000;

    constructor(tapline, clock, ctx) {
        Object.assign(this, {tapline, clock, ctx});
        this.live = false;                           // the output is ready for rows
        this.lastSeq = -1;
        this.margin = RowRadio.Floor;
        this.lateness = [];
        this.releaseAt = 0;
        this.raised = 0;
        this.warm = [];
        this.warmFrom = 0;
        this.started = false;
        this.rows = 0;
        this.late = 0;
        this.skipped = 0;
        this.bytes = 0;
        this.since = performance.now();
    }

    /** How far behind the station's clock the music plays (and, while it does, the ridges), ms. */
    get delay() {
        return this.live && this.started ? Math.round(this.margin) : 0;
    }

    async tune(binding) {
        await this.ready;
        this.live = true;
        this.audioId = binding.tapId;
        this.service = binding.nodeId;
        this.off = this.tapline.subscribe(binding, table => this.#onWindow(table));
    }

    /**
     * Moved to another station's rows, make before break: the old subscription kept until the new one's first window
     * (tapline.replace). The rows run on — numbered and timed alike by both stations — so nothing restarts: rows seen
     * already are passed over, the rest played as ever.
     */
    move(binding) {
        const old = this.audioId;
        this.audioId = binding.tapId;
        this.service = binding.nodeId;
        this.off = this.tapline.replace(old, binding, table => this.#onWindow(table));
    }

    /** The music paused with the ridges: unsubscribed (its tap dropped; the server's side the view closes) and faded out. */
    pause() {
        this.off?.();
        if (this.audioId !== undefined) this.tapline.drop(this.audioId);
        this.audioId = undefined;
        this.service = undefined;
        this.mute(true);
    }

    /**
     * Subscribed again (a new route, `binding`): joined afresh — its rows are a new start, not rows that came late (they
     * would raise the delay to its limit, then ease it down for a minute): warmed up again, the delay set from what the
     * path does now.
     */
    resume(binding) {
        this.lastSeq = -1;
        this.started = false;
        this.warm = [];
        this.warmFrom = 0;
        this.lateness = [];
        this.margin = RowRadio.Floor;
        if (binding) {
            this.audioId = binding.tapId;
            this.service = binding.nodeId;
            this.off = this.tapline.subscribe(binding, table => this.#onWindow(table));
        }
        this.mute(false);
    }

    mute(on) {
    }

    stop() {
        this.off?.();
        if (this.audioId !== undefined) this.tapline.drop(this.audioId);
        this.ctx.close();
    }

    #onWindow(table) {
        if (this.clock.offset === null || !this.live) return;
        const fresh = [];
        for (const {row} of table.rows.values()) {
            const dv = new DataView(row.buffer, row.byteOffset, row.byteLength);
            const seq = dv.getUint32(0, true);
            if (seq > this.lastSeq) fresh.push({
                seq,
                first: dv.getUint32(4, true),
                n: row[8],
                length: row[9],
                data: row.subarray(12)
            });
        }
        fresh.sort((a, b) => a.seq - b.seq);
        const joining = this.lastSeq < 0;
        if (fresh.length && !joining) this.skipped += fresh[0].seq - this.lastSeq - 1;   // squashed: gone from the window
        for (const row of fresh) this.#play(row, joining);
    }

    /** How long after the output renders a sample it is heard, ms: its latency, as the browser reports it. */
    lag() {
        const ts = this.ctx.getOutputTimestamp?.();
        if (ts?.performanceTime > 0) return Math.max(0, (this.ctx.currentTime - ts.contextTime) * 1000 - (performance.now() - ts.performanceTime));
        return (this.ctx.outputLatency || this.ctx.baseLatency || 0) * 1000;
    }

    #play(row, joining) {
        this.lastSeq = row.seq;
        this.rows++;
        this.bytes += 12 + row.length;
        const now = performance.now(), due = this.clock.toLocal(this.dueOf(row));   // its start, with no delay
        if (!this.started) {                         // warming up: watch how late rows come, before a note plays
            if (joining && now - due > RowRadio.Max + 200) {
                this.skipped++;
                return;
            }      // the window's past, beyond any delay
            this.warm.push({row, due, lateness: joining ? -Infinity : now - due});        // the join's own rows say nothing of the path
            this.warmFrom ||= now;
            if (now - this.warmFrom >= RowRadio.Warmup) this.#start(now);
            return;
        }
        this.#adapt(now, now - due);
        this.#schedule(row, due);
    }

    /** The warm-up is over: a delay that would have played every row seen on time, a little more, then the rows. */
    #start(now) {
        const worst = Math.max(...this.warm.map(w => w.lateness));
        const needed = (worst + RowRadio.Safety) * RowRadio.Spare;
        this.margin = Math.min(RowRadio.Max, this.lag() + Math.max(RowRadio.Floor, needed));
        for (const w of this.warm) if (w.lateness > -Infinity) this.lateness.push({at: now, v: w.lateness});
        this.started = true;
        this.releaseAt = now + 1000;
        for (const w of this.warm) this.#schedule(w.row, w.due, true);   // before anything is heard: nothing is late yet
        this.warm = [];
    }

    #schedule(row, due, starting = false) {
        const start = contextTime(this.ctx, due + this.margin);
        if (start + this.spanOf(row) < this.ctx.currentTime) {
            this.skipped++;
            return;
        }   // wholly past
        if (start < this.ctx.currentTime + 0.005 && !starting) this.late++;               // later than even the most delay
        this.scheduleRow(row, start);
    }

    /**
     * The jitter buffer. `lateness`: how long after its start was due (with no delay) a row arrived. A row that would
     * play late raises the delay at once, with 10% to spare — before it is scheduled: a pause, not a stutter; every quarter
     * second, while above the latest arrival of the last 10 s (with the same to spare), the delay drops one frame (1/60 s)
     * — a frame skipped, barely audible. Only what the path has lately needed: a slow path shows as a pause, not hidden.
     */
    #adapt(now, lateness) {
        const l = this.lateness, lag = this.lag();
        l.push({at: now, v: lateness});
        while (l[0].at < now - RowRadio.Window) l.shift();
        const needed = lateness + RowRadio.Safety;
        if (lag + needed > this.margin) {
            this.margin = Math.min(RowRadio.Max, Math.ceil(lag + needed * RowRadio.Spare));
            this.raised++;
            this.releaseAt = now + 1000;
        } else if (now >= this.releaseAt) {
            this.releaseAt = now + 250;
            let latest = -Infinity;
            for (const x of l) latest = Math.max(latest, x.v);
            const target = lag + Math.max(RowRadio.Floor, (latest + RowRadio.Safety) * RowRadio.Spare);
            if (target < this.margin - FRAME_MS) this.margin -= FRAME_MS;
        }
        this.margin = Math.min(RowRadio.Max, Math.max(this.margin, lag + RowRadio.Floor));
    }

    report() {
        const seconds = (performance.now() - this.since) / 1000;
        const [what, done] = this.describe();
        return `music    ${what}, synthesized here · ${(this.bytes / seconds / 1024).toFixed(2)} KiB/s · ${this.rows} rows · ${done} · ` +
            `${this.started ? `playing ${Math.round(this.margin)} ms behind (output ${Math.round(this.lag())} ms + jitter buffer, raised ${this.raised}×)` : 'warming up'} · ` +
            (this.lateness.length ? `rows ≤ ${Math.round(Math.max(...this.lateness.map(x => x.v)))} ms after due (10 s) · ` : '') +
            `late ${this.late} · squashed or expired ${this.skipped} · ${this.ctx.sampleRate / 1000} kHz`;
    }
}

/** A MIDI piece's notes (MidiProgram.cs: per note, offset ms, voice, key, velocity, duration ms), played by Synth. */
class NoteRadio extends RowRadio {
    constructor(tapline, clock, ctx) {
        super(tapline, clock, ctx);
        this.synth = new Synth(ctx);
        this.played = 0;
        this.ready = Promise.resolve();
    }

    dueOf(row) {
        return row.first;
    }

    mute(on) {
        this.synth.mute(on);
    }

    describe() {
        const {drums, cut, dropped} = this.synth;   // (limit: Synth.Voices unless the output has overrun)
        return ['notes', `${this.played} played (${drums} drums), ${cut} cut short (output overrun: ≤ ${this.synth.limit} voices), ${dropped} dropped (output behind)`];
    }

    * #notes(row) {
        const d = row.data;
        for (let k = 0, o = 0; k < row.n; k++, o += 6) yield {
            offset: d[o],
            voice: d[o + 1],
            key: d[o + 2],
            velocity: d[o + 3],
            duration: d[o + 4] | d[o + 5] << 8
        };
    }

    spanOf(row) {
        let span = 0;
        for (const n of this.#notes(row)) span = Math.max(span, (n.offset + n.duration) / 1000);
        return span;
    }

    scheduleRow(row, start) {
        const now = this.ctx.currentTime;
        for (const n of this.#notes(row)) {
            const at = start + n.offset / 1000, end = at + n.duration / 1000;
            if (end <= now + 0.02) continue;           // over already (a late row): only what still sounds
            if (this.synth.play(n.voice, n.key, n.velocity, Math.max(at, now), end)) this.played++;
        }
    }
}

const VOICE = Object.freeze({
    Drums: 0,
    Pad: 1,
    Pluck: 2,
    Piano: 3,
    EPiano: 4,
    Harp: 5,
    Koto: 6,
    Flute: 7,
    Sine: 8,
    Square: 9,
    Saw: 10,
    Bass: 11
});

/**
 * A small polyphonic synth for the notes, in Web Audio nodes: per voice an oscillator or two through a filter and an
 * envelope — pads of detuned saws, plucks with a closing filter, a bell-ish electric piano, sine leads (a sub bass in
 * the low octaves), squares and saws, and a kit from noise and falling sines — into a stereo chorus, a generated reverb
 * and a gentle compressor.
 */
class Synth {
    static Voices = 192;
    static Fewest = 32;
    static Level = 0.45;

    constructor(ctx) {
        this.ctx = ctx;
        this.voices = [];
        this.limit = Synth.Voices;
        this.calm = 0;
        this.cut = 0;
        this.dropped = 0;
        this.drums = 0;
        const out = ctx.createDynamicsCompressor(), limit = ctx.createDynamicsCompressor();
        out.threshold.value = -18;
        out.knee.value = 12;
        out.ratio.value = 3;
        out.attack.value = 0.01;
        out.release.value = 0.25;
        limit.threshold.value = -3;
        limit.knee.value = 0;
        limit.ratio.value = 20;
        limit.attack.value = 0.001;
        limit.release.value = 0.1;
        out.connect(limit).connect(ctx.destination);   // …and a limiter: a kick's transient never clips
        const master = this.master = ctx.createGain();
        master.gain.value = Synth.Level;
        master.connect(out);
        const bus = this.bus = ctx.createGain();
        bus.connect(master);
        for (const [delay, rate, pan] of [[0.012, 0.27, -0.7], [0.017, 0.37, 0.7]]) {   // chorus: two slowly swaying delays, apart
            const d = ctx.createDelay(0.05), lfo = ctx.createOscillator(), depth = ctx.createGain(),
                p = ctx.createStereoPanner(), g = ctx.createGain();
            d.delayTime.value = delay;
            lfo.frequency.value = rate;
            depth.gain.value = 0.002;
            p.pan.value = pan;
            g.gain.value = 0.45;
            lfo.connect(depth).connect(d.delayTime);
            lfo.start();
            bus.connect(d).connect(p).connect(g).connect(master);
        }
        const reverb = ctx.createConvolver(), send = ctx.createGain();
        reverb.buffer = Synth.#impulse(ctx, 2.8);
        send.gain.value = 0.32;
        bus.connect(send).connect(reverb).connect(master);
        this.noise = Synth.#noise(ctx, 1);
    }

    static #impulse(ctx, seconds) {               // a hall: decaying stereo noise, darker as it dies
        const n = Math.floor(ctx.sampleRate * seconds), b = ctx.createBuffer(2, n, ctx.sampleRate);
        for (let c = 0; c < 2; c++) {
            const d = b.getChannelData(c);
            let lp = 0;
            for (let i = 0; i < n; i++) {
                const t = i / n, a = 0.15 + 0.8 * t;     // the low-pass closes as the tail goes on
                lp += (Math.random() * 2 - 1 - lp) * (1 - a);
                d[i] = lp * Math.pow(1 - t, 2.5) * (i < ctx.sampleRate * 0.02 ? i / (ctx.sampleRate * 0.02) : 1);
            }
        }
        return b;
    }

    static #noise(ctx, seconds) {
        const b = ctx.createBuffer(1, Math.floor(ctx.sampleRate * seconds), ctx.sampleRate), d = b.getChannelData(0);
        for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
        return b;
    }

    /** An envelope on a new gain node: up to `peak` in `a` s, toward `sustain` × peak (time constant `decay`), and from
     *  `end` down (time constant `release`). Returns the node and when the sound is gone. */
    #envelope(t, end, peak, a, decay, sustain, release) {
        const g = this.ctx.createGain(), p = g.gain;
        p.setValueAtTime(0, t);
        p.linearRampToValueAtTime(peak, t + a);
        p.setTargetAtTime(peak * sustain, t + a, decay);
        const off = Math.max(end, t + a);
        p.setTargetAtTime(0, off, release);
        return [g, off + release * 4, off];          // −35 dB: gone, as far as anyone can hear; and when its release begins
    }

    #osc(type, f, t, stop, detuneCents = 0) {
        const o = this.ctx.createOscillator();
        o.type = type;
        o.frequency.value = f;
        o.detune.value = detuneCents;
        o.start(t);
        o.stop(stop);
        this.made.push(o);
        return o;
    }

    /**
     * Room for a note at `t`: no more voices than the output keeps up with. Without a limit a busy passage (Resonance:
     * ~80 notes a second, its pads ringing 2 s past their release: ~170 voices) overran Chromium's audio thread: its
     * clock stalled, nothing ended, and every note piled on — silent until the sound was toggled. So: while the output
     * overruns (Chromium's playbackStats count it; anywhere, its clock running slow), the limit drops to 85% of what is
     * sounding, and climbs back a voice a second (to Synth.Voices) while it doesn't; past the limit the voice longest into
     * its release (else the oldest) is cut, fading in ~10 ms. False while the clock is slow: the note is dropped, so an
     * overrun can't feed itself.
     */
    #room(t) {
        const ctx = this.ctx, now = ctx.currentTime, wall = performance.now(), c = this.clock;
        this.voices = this.voices.filter(n => n.stop > now);
        if (!c || wall - c.wall >= 250) {
            const under = ctx.playbackStats?.underrunDuration ?? 0;
            this.behind = !!c && (now - c.at) * 1000 < 0.9 * (wall - c.wall);
            if (this.behind || c && under > c.under) {
                this.limit = Math.max(Synth.Fewest, Math.floor(Math.min(this.limit, this.voices.length) * 0.85));
                this.calm = 0;
            } else if (++this.calm % 4 === 0) this.limit = Math.min(Synth.Voices, this.limit + 1);
            this.clock = {wall, at: now, under};
        }
        if (this.behind) {
            this.dropped++;
            return false;
        }
        const at = Math.max(t, now);
        while (this.voices.length >= this.limit) {
            const victim = this.#victim(at);
            if (!victim) {
                this.dropped++;
                return false;
            }   // all yet to start
            this.#end(victim, at);
            this.cut++;
        }
        return true;
    }

    /** Of the voices sounding at `at`: the one longest into its release, else the oldest. */
    #victim(at) {
        let victim = null;
        for (const n of this.voices) {
            if (n.t >= at) continue;                   // yet to start
            const released = n.off <= at, was = victim && victim.off <= at;
            if (!victim || (released !== was ? released : released ? n.off < victim.off : n.t < victim.t)) victim = n;
        }
        return victim;
    }

    #end(n, at) {
        const g = n.env.gain;
        if (g.cancelAndHoldAtTime) g.cancelAndHoldAtTime(at); else g.cancelScheduledValues(at);
        g.setTargetAtTime(0, at, 0.01);
        for (const o of n.sources) o.stop(at + 0.06);
        n.stop = at + 0.06;
        this.voices.splice(this.voices.indexOf(n), 1);
    }

    /** Silenced (paused) or back, eased (~20 ms): no click; the voices sounding just ring on unheard. */
    mute(on) {
        this.master.gain.setTargetAtTime(on ? 0 : Synth.Level, this.ctx.currentTime, 0.02);
    }

    /** Plays a note; false if it was dropped (the output behind). */
    play(voice, key, velocity, t, end) {
        const v = Math.pow(velocity / 127, 0.8);
        if (voice === VOICE.Drums) this.drums++;
        if (voice === VOICE.Drums) return this.#drum(key, v, t);   // short: not counted
        if (!this.#room(t)) return false;
        const ctx = this.ctx, f = 440 * Math.pow(2, (key - 69) / 12);
        this.made = [];
        let src = [], filter = null, env, stop, off;
        const lowpass = (fc, q = 0.7) => {
            filter = ctx.createBiquadFilter();
            filter.type = 'lowpass';
            filter.frequency.value = fc;
            filter.Q.value = q;
            return filter;
        };
        switch (voice) {
            case VOICE.Pad: {
                [env, stop, off] = this.#envelope(t, end, 0.045 * v, 0.35, 0.6, 0.8, 0.5);
                lowpass(900 + 1400 * v, 0.6);
                src = [this.#osc('sawtooth', f, t, stop, -8), this.#osc('sawtooth', f, t, stop, 8), this.#osc('triangle', f / 2, t, stop)];
                break;
            }
            case VOICE.Pluck:
            case VOICE.Koto:
            case VOICE.Harp: {
                const bright = voice === VOICE.Koto ? 4200 : voice === VOICE.Harp ? 3200 : 2600;
                [env, stop, off] = this.#envelope(t, Math.min(end, t + 0.6), (voice === VOICE.Koto ? 0.15 : 0.18) * v, 0.003, voice === VOICE.Harp ? 0.45 : 0.25, 0.15, 0.2);
                lowpass(bright, 1.2);
                filter.frequency.setValueAtTime(bright, t);
                filter.frequency.setTargetAtTime(500, t, 0.12);   // the pluck closes
                src = [this.#osc(voice === VOICE.Koto ? 'square' : voice === VOICE.Harp ? 'triangle' : 'sawtooth', f, t, stop)];
                break;
            }
            case VOICE.Piano:
            case VOICE.EPiano: {
                [env, stop, off] = this.#envelope(t, end, 0.12 * v, 0.003, voice === VOICE.EPiano ? 0.9 : 0.6, 0.2, 0.3);
                lowpass(voice === VOICE.EPiano ? 2400 : 3600);
                src = [this.#osc(voice === VOICE.EPiano ? 'sine' : 'triangle', f, t, stop), this.#osc('sine', f * (voice === VOICE.EPiano ? 3.5 : 2), t, stop)];
                const bell = ctx.createGain();              // the overtone dies first: a bell's strike
                bell.gain.setValueAtTime(0.35, t);
                bell.gain.setTargetAtTime(0, t, 0.15);
                src[1].connect(bell);
                src[1] = bell;
                break;
            }
            case VOICE.Flute: {
                [env, stop, off] = this.#envelope(t, end, 0.18 * v, 0.08, 0.5, 0.9, 0.15);
                lowpass(2200);
                const o = this.#osc('sine', f, t, stop), lfo = this.#osc('sine', 5, t, stop), depth = ctx.createGain();
                depth.gain.setValueAtTime(0, t);
                depth.gain.linearRampToValueAtTime(6, t + 0.3);   // vibrato, cents, coming in
                lfo.connect(depth).connect(o.detune);
                src = [o];
                break;
            }
            case VOICE.Sine: {
                if (key < 40) {                             // the low octaves: a sub bass
                    [env, stop, off] = this.#envelope(t, end, 0.3 * v, 0.006, 0.4, 0.85, 0.08);
                    src = [this.#osc('sine', f, t, stop), this.#osc('triangle', f * 2, t, stop)];
                    const g = ctx.createGain();
                    g.gain.value = 0.15;
                    src[1].connect(g);
                    src[1] = g;
                } else {
                    [env, stop, off] = this.#envelope(t, end, 0.09 * v, 0.01, 0.4, 0.8, 0.12);
                    src = [this.#osc('sine', f, t, stop), this.#osc('triangle', f, t, stop, 5)];
                }
                break;
            }
            case VOICE.Square: {
                [env, stop, off] = this.#envelope(t, end, 0.18 * v, 0.003, 0.15, 0.4, 0.06);
                lowpass(key < 40 ? 700 : 2400);
                src = [this.#osc('square', f, t, stop)];
                break;
            }
            case VOICE.Saw: {
                [env, stop, off] = this.#envelope(t, end, 0.05 * v, 0.01, 0.3, 0.75, 0.15);
                lowpass(3000, 1);
                src = [this.#osc('sawtooth', f, t, stop, -5), this.#osc('sawtooth', f, t, stop, 5)];
                break;
            }
            default: {                                    // Bass
                [env, stop, off] = this.#envelope(t, end, 0.2 * v, 0.005, 0.3, 0.7, 0.08);
                lowpass(600, 1.5);
                src = [this.#osc('sawtooth', f, t, stop), this.#osc('sine', f / 2, t, stop)];
            }
        }
        for (const s of src) s.connect(filter ?? env);
        if (filter) filter.connect(env);
        env.connect(this.bus);
        this.voices.push({voice, key, t, off, stop, env, sources: this.made});
        return true;
    }

    #drum(key, v, t) {
        const ctx = this.ctx;
        const noise = (type, fc, q, peak, decay, length = decay * 6) => {
            const s = ctx.createBufferSource(), flt = ctx.createBiquadFilter(), g = ctx.createGain();
            s.buffer = this.noise;
            flt.type = type;
            flt.frequency.value = fc;
            flt.Q.value = q;
            g.gain.setValueAtTime(peak, t);
            g.gain.setTargetAtTime(0, t + 0.002, decay);
            s.connect(flt).connect(g).connect(this.bus);
            s.start(t, Math.random() * 0.5);
            s.stop(t + length);
        };
        const tone = (f0, f1, peak, decay, type = 'sine') => {
            const o = ctx.createOscillator(), g = ctx.createGain();
            o.type = type;
            o.frequency.setValueAtTime(f0, t);
            o.frequency.exponentialRampToValueAtTime(f1, t + decay);
            g.gain.setValueAtTime(peak, t);
            g.gain.setTargetAtTime(0, t + 0.003, decay);
            o.connect(g).connect(this.bus);
            o.start(t);
            o.stop(t + decay * 7);
        };
        const level = Math.min(1, v * 2.2);           // the kit's velocities are low: brought up
        if (key === 35 || key === 36) {
            tone(150, 45, 0.9 * level, 0.13);
            noise('highpass', 3000, 0.7, 0.15 * level, 0.008);
        } else if (key === 38 || key === 40) {
            noise('bandpass', 1900, 0.8, 0.45 * level, 0.07);
            tone(190, 160, 0.25 * level, 0.05, 'triangle');
        } else if (key === 37) noise('bandpass', 2500, 2, 0.35 * level, 0.02);
        else if (key === 39) {
            for (let k = 0; k < 3; k++) noise('bandpass', 1300, 1.5, 0.3 * level, 0.012 + k * 0.004);
        } else if (key === 42 || key === 44) noise('highpass', 7500, 0.7, 0.18 * level, 0.025);
        else if (key === 46) noise('highpass', 7000, 0.7, 0.16 * level, 0.16);
        else if (key === 49 || key === 52 || key === 55 || key === 57) noise('highpass', 4500, 0.5, 0.16 * level, 0.55);
        else if (key === 51 || key === 53 || key === 59) noise('bandpass', 5200, 1.2, 0.12 * level, 0.3);
        else if ([41, 43, 45, 47, 48, 50].includes(key)) {
            const f = 70 + (key - 41) * 14;
            tone(f * 1.5, f, 0.6 * level, 0.16);
        } else noise('bandpass', 3000, 2, 0.15 * level, 0.012);   // sticks, clicks, the rest
        return true;
    }
}
