using System.Buffers.Binary;

namespace BlazorRealtime.Sample.Waveform;

/// <summary>
///     The Unknown Pleasures field, after Book of Shapes' "Joy Division" pattern (Nikolaj Sokolowski): horizontal lines
///     displaced by simplex noise under a bell-shaped envelope, deeper lines taller. Defaults from the variant
///     https://bookofshapes.com/patterns/joy_division — line count 77.5, wave width 485, amplitude 62, noise scale X 3.4,
///     noise scale Y 0.009, Y spread 232, envelope sharpness 6.8, vertical fade 0.7, softness 47.8. Here the field drifts
///     up
///     back through the lines over time, and the music moves it: low-band energy (kick, bass) pumps the amplitude,
///     high-band
///     energy and the filter's opening sharpen the ridges, and every note sounding rises as a bump either side of the
///     centre,
///     as far out as it is high (<see cref="AddNote" />), on the front lines — receding with the field, fading as it goes,
///     so
///     the last few seconds of the music trail back through the lines.
///     <para>
///         Encoded once per tick for every viewer: a 40-byte header, then a grid of heights (a byte each, 255 =
///         <see cref="Headroom" />
///         amplitudes) — every <c>lineStep</c>-th line, every <c>pointStep</c>-th point — coded by
///         <see cref="RidgeCodec" />
///         (near-lossless: within ±1 of the field's heights, tiny ±2). The browser draws its lines across the field from
///         it —
///         as many as fit its page — through a Catmull-Rom spline down the lines and along them: the field's lines are
///         0.03
///         noise units apart, so it is smooth down them — every other line splined back is off by 0.27 height units RMS
///         (0.16 px
///         at the field's own scale) — while
///         along them the peaks are sharp and the points carry their detail. Three grids: full, every other line (~1 KB);
///         lite, every fourth (~0.7 KB); tiny, every eighth line and every other point (~0.27 KB) — for viewers on slower
///         paths,
///         where a larger frame would hold the line for longer than the music behind it can wait. Every one draws the same
///         lines: a change of grid softens or sharpens the detail, nothing more.
///     </para>
/// </summary>
internal sealed class Ridges
{
    public const int Lines = 77, Points = 160, HeaderSize = 40;
    public const float WaveWidth = 485, Amplitude = 62, YSpread = 232, VerticalFade = 0.7f, Headroom = 2.4f;
    private const float NoiseScaleX = 3.4f, NoiseScaleY = 0.009f, Sharpness = 6.8f, Softness = 47.8f;

    private const float
        Drift = 0.165f, Morph = 0.05f; // noise units per second: back through the lines (~6 a second), and in place

    private const float
        NoteHeight = 1.0f, TrailFade = 1.2f; // a note at full energy, amplitudes; seconds to 1/e as it recedes

    private const int TrailFrames = 300; // 5 s of note spectra at 60 Hz

    /// <summary>Points of a note spectrum (see <see cref="AddNote" />, <see cref="Compute" />).</summary>
    public const int Width = GridPoints;

    // Each grid's steps (lines, points). Its last line and point may fall past the field's: the field is computed that
    // far (GridLines × GridPoints), so every step is uniform and the spline needs no special end.
    private const int FullLineStep = 2, LiteLineStep = 4, TinyLineStep = 8, TinyPointStep = 2;

    private const int GridLines = (Lines - 2) / TinyLineStep * TinyLineStep + TinyLineStep + 1,
        GridPoints = (Points - 2) / TinyPointStep * TinyPointStep + TinyPointStep + 1;

    private const int
        FrameCapacity = HeaderSize + 4 * GridLines * GridPoints + 16; // ~1.3 bits a height in practice; room for 32

    private static readonly float
        TrailLinesPerSecond = Drift / (YSpread / (Lines - 1) * NoiseScaleY); // as fast as the field drifts back

    private readonly float[] _envelope = new float[GridPoints];

    private readonly byte[] _frame = new byte[FrameCapacity],
        _liteFrame = new byte[FrameCapacity],
        _tinyFrame = new byte[FrameCapacity];

    private readonly byte[] _heights = new byte[GridLines * GridPoints], _grid = new byte[GridLines * GridPoints];
    private readonly float[] _notes = new float[GridPoints]; // the spectrum given, smoothed (~100 ms): no note jumps in
    private readonly int[] _reconstructed = new int[GridLines * GridPoints];
    private readonly float[] _trail = new float[TrailFrames * GridPoints]; // the note spectra of the last 5 s, a ring
    private double _last = -1; // carrier time of the previous field, s
    private float _pump, _detail;
    private int _trailHead; // the slot the next spectrum goes in

    public Ridges()
    {
        for (var s = 0; s < GridPoints; s++)
        {
            var x = 2f * s / (Points - 1) - 1; // −1 … 1 across the wave (past 1 beyond the last point)
            _envelope[s] = MathF.Exp(-Sharpness * x * x);
        }
    }

    /// <summary>Bytes of the last frames encoded (header + deflated heights).</summary>
    public int FrameBytes { get; private set; }

    public int LiteFrameBytes { get; private set; }
    public int TinyFrameBytes { get; private set; }

    /// <param name="seconds">Carrier time.</param>
    /// <param name="low">Low-band level of the music now, 0..1 (kick, bass).</param>
    /// <param name="high">High-band level, 0..1 (melody, drone, noise).</param>
    /// <param name="filter">The SID filter's cutoff, 0..1 (it opens over the piece).</param>
    /// <param name="notes">
    ///     The notes sounding now, as a spectrum across the field (<see cref="Width" /> points;
    ///     <see cref="AddNote" />).
    /// </param>
    public void Compute(double seconds, float low, float high, float filter, ReadOnlySpan<float> notes)
    {
        Advance(seconds, low, high, notes);
        var newest = (_trailHead - 1 + TrailFrames) % TrailFrames;
        float t = (float)(seconds % 10_000), drift = t * Drift, morph = t * Morph;
        var octave = (1 - Softness / 100) * (0.6f + 0.8f * filter + 0.6f * _detail);
        var gain = 1.7f * (0.65f + 0.6f * _pump);
        for (var i = 0; i < GridLines; i++) // past the last line: only for the spline's sake
        {
            var ny = i * (YSpread / (Lines - 1)) * NoiseScaleY + drift; // receding: features move back, line by line
            var depth = 0.55f + 0.45f * i / (Lines - 1); // further back, lower
            var row = i * GridPoints;
            var age = Math.Max(0, Lines - 1 - i) / TrailLinesPerSecond; // the front line is now; further back, earlier
            var frames = Math.Min(TrailFrames - 1, (int)MathF.Round(age * 60));
            var trail = _trail.AsSpan((newest - frames + TrailFrames) % TrailFrames * GridPoints, GridPoints);
            var fade = NoteHeight * MathF.Exp(-age / TrailFade);
            for (var s = 0; s < GridPoints; s++)
            {
                var envelope = _envelope[s];
                if (envelope < 0.02f)
                {
                    _heights[row + s] = 0;
                    continue;
                } // under a pixel: flat

                var nx = s * (WaveWidth / (Points - 1)) * NoiseScaleX / 100; // noise scale per 100 px
                var n = Simplex.Noise(nx, ny, morph) +
                        octave * Simplex.Noise(nx * 2.3f + 17.3f, ny * 2.3f, morph * 1.9f);
                var v = (n / (1 + octave) + 1) * 0.5f; // 0..1
                var d = (envelope * v * v * gain + fade * MathF.Tanh(0.7f * trail[s])) *
                        depth; // in amplitudes: chords add, softly capped
                _heights[row + s] = (byte)Math.Clamp(d / Headroom * 255f, 0f, 255f);
            }
        }
    }

    /// <summary>
    ///     The field's memory moved on to <paramref name="seconds" /> — the notes' trail, the pump, the detail — without the
    ///     field itself (a few hundred floats, not the noise): while nobody watches, so the field is right when someone does;
    ///     and for a station taking over from another, to catch up (<see cref="WaveformStation" />).
    /// </summary>
    public void Advance(double seconds, float low, float high, ReadOnlySpan<float> notes)
    {
        var dt = _last < 0 ? 1 / 60f : Math.Clamp((float)(seconds - _last), 0, 0.1f);
        var ease = 1 - MathF.Exp(-dt / 0.1f);
        for (var s = 0; s < GridPoints; s++) _notes[s] += (notes[s] - _notes[s]) * ease;
        _notes.CopyTo(_trail.AsSpan(_trailHead * GridPoints, GridPoints));
        _trailHead = (_trailHead + 1) % TrailFrames;
        _last = seconds;
        _pump = low > _pump
            ? low
            : _pump * MathF.Exp(-dt / 0.6f); // up at once, down gently (~0.6 s): the field breathes
        _detail += (high - _detail) * (1 - MathF.Exp(-dt / 0.037f));
    }

    /// <summary>
    ///     Adds a sounding note to a spectrum for <see cref="Compute" />: a bump either side of the centre — as far out as the
    ///     note is high, C1 at the centre and C7 at the bell's shoulders (a single bump where the two would meet) — as tall
    ///     as <paramref name="energy" /> (0..1), tapered as the bell falls away.
    /// </summary>
    public static void AddNote(Span<float> spectrum, float key, float energy)
    {
        const float Sigma = 0.028f; // in −1…1 across the wave: about 2 points, ~3 semitones
        var r = Math.Clamp((key - 24) / 72f, 0, 1) * 0.72f;
        for (var side = r < Sigma ? 0 : -1; side <= 1; side += 2) // −1 and +1; or 0, once
        {
            var cx = side * r;
            int from = Math.Max(0, (int)((cx - 3 * Sigma + 1) * (Points - 1) / 2)),
                to = Math.Min(GridPoints - 1, (int)((cx + 3 * Sigma + 1) * (Points - 1) / 2) + 1);
            for (var s = from; s <= to; s++)
            {
                float x = 2f * s / (Points - 1) - 1, u = (x - cx) / Sigma;
                spectrum[s] += energy * MathF.Exp(-0.5f * u * u) * MathF.Exp(-0.5f * Sharpness * x * x); // × √bell
            }

            if (side == 0) break;
        }
    }

    /// <summary>
    ///     Encodes the field computed last, in full (every other line), stamped with the carrier time; valid until the
    ///     next call.
    /// </summary>
    public ReadOnlySpan<byte> Encode(uint tick, double carrierMs, int bar)
    {
        var f = Encode(_frame, FullLineStep, 1, 3, tick, carrierMs, bar);
        FrameBytes = f.Length;
        return f;
    }

    /// <summary>The same field, lite: every fourth line; valid until the next call.</summary>
    public ReadOnlySpan<byte> EncodeLite(uint tick, double carrierMs, int bar)
    {
        var f = Encode(_liteFrame, LiteLineStep, 1, 3, tick, carrierMs, bar);
        LiteFrameBytes = f.Length;
        return f;
    }

    /// <summary>The same field, tiny: every eighth line, every other point; valid until the next call.</summary>
    public ReadOnlySpan<byte> EncodeTiny(uint tick, double carrierMs, int bar)
    {
        var f = Encode(_tinyFrame, TinyLineStep, TinyPointStep, 5, tick, carrierMs, bar);
        TinyFrameBytes = f.Length;
        return f;
    }

    private Span<byte> Encode(byte[] frame, int lineStep, int pointStep, int step, uint tick, double carrierMs, int bar)
    {
        int lines = (Lines - 2) / lineStep + 2,
            points = (Points - 2) / pointStep + 2; // the grid: the field's ends covered
        for (var i = 0; i < lines; i++)
        for (int s = 0, row = i * lineStep * GridPoints; s < points; s++)
            _grid[i * points + s] = _heights[row + s * pointStep];
        var n = HeaderSize + RidgeCodec.Encode(_grid.AsSpan(0, lines * points), lines, points, step,
            frame.AsSpan(HeaderSize), _reconstructed);
        var f = frame.AsSpan(0, n);
        "PLSV"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32LittleEndian(f[4..], tick);
        BinaryPrimitives.WriteDoubleLittleEndian(f[8..], carrierMs);
        f[16] = Lines;
        f[17] = Points;
        f[18] = (byte)bar;
        f[19] = (byte)lineStep; // the field's lines and points; the grid: every lineStep-th line
        BinaryPrimitives.WriteSingleLittleEndian(f[20..], WaveWidth);
        BinaryPrimitives.WriteSingleLittleEndian(f[24..], Amplitude * Headroom); // pixels at height 255
        BinaryPrimitives.WriteSingleLittleEndian(f[28..], YSpread);
        BinaryPrimitives.WriteSingleLittleEndian(f[32..], VerticalFade);
        f[36] = (byte)(_pump * 255);
        f[37] = (byte)(_detail * 255);
        f[38] = (byte)pointStep;
        f[39] = (byte)step; // …every pointStep-th point, heights ± step / 2
        return f;
    }
}