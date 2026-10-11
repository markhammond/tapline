using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tapline;

namespace BlazorRealtime.Sample.Waveform;

/// <summary>
///     Up to 200 ms of a MIDI piece's notes — each at its start, with its voice and duration, so a row plays on its own —
///     as a
///     row of the station's rolling window (see <see cref="WaveformStation.Notes" />). Per note, 6 bytes: offset from
///     <see cref="StartMs" /> (ms), voice (<see cref="MidiProgram.Voice" />), key, velocity, duration (ms, little-endian).
///     A busy 200 ms takes several rows (16 notes each).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NoteRow
{
    public uint Seq; // row number since the station started
    public uint StartMs; // carrier time its notes' offsets count from
    public byte Count; // notes in the row
    public byte Length; // bytes of Data used
    public ushort Reserved;
    public NoteBytes Data;
}

[InlineArray(MidiProgram.RowBytes)]
public struct NoteBytes
{
    private byte _first;
}

/// <summary>
///     A MIDI file as the station's program: its notes on the carrier clock, looping (a 2 s rest between plays), streamed
///     as
///     <see cref="NoteRow" />s for the browser to synthesize, and the cues the ridges move to — the kick and the bass pump
///     them, the notes above middle C sharpen them, how many notes sound opens them. Each track gets a voice from its name
///     or,
///     failing that, its General MIDI program.
/// </summary>
internal sealed class MidiProgram(MidiFile midi, Table<byte, NoteRow> rows)
{
    public enum Voice : byte
    {
        Drums,
        Pad,
        Pluck,
        Piano,
        EPiano,
        Harp,
        Koto,
        Flute,
        Sine,
        Square,
        Saw,
        Bass
    }

    public const int RowNotes = 16, RowBytes = RowNotes * 6, RowMs = 200, Window = 24;
    private const double GapMs = 2000;
    private readonly double _barMs = 4 * 60_000 / midi.FirstTempoBpm;

    private readonly List<(double Start, double End, byte Key, float Weight, bool High, Voice Voice)>
        _sounding = []; // and the last 0.6 s's

    private readonly Voice[] _voices = midi.TrackNames
        .Select((name, t) => VoiceOf(name, midi.Notes.FirstOrDefault(n => n.Track == t))).ToArray();

    private PublishResult? _acknowledgement;
    private Predicate<(double Start, double End, byte Key, float Weight, bool High, Voice Voice)>? _expired;
    private double _expiredBefore;
    private float _kick, _high, _busy;
    private int _length;
    private int _next;
    private double _playStart = double.NaN; // carrier time the current play began
    private NoteRow _row;
    private double _rowStart = double.NaN;
    private uint _seq;

    public string Title { get; init; } = "MIDI";

    /// <summary>Who made it, for the page's credits (null: unknown — a file of the operator's own).</summary>
    public string? Credit { get; init; }

    public int Starts { get; private set; }
    public int Bar { get; private set; }
    public int Bars => (int)Math.Ceiling(midi.LengthMs / _barMs);
    public long RowsPublished { get; private set; }
    public long BytesPublished { get; private set; }

    /// <summary>Rows are published (the notes topic has subscribers); otherwise built and dropped — the music keeps time.</summary>
    public bool Publishing { get; set; } = true;

    /// <summary>The last publish's answer since asked (201/202, its epoch), once.</summary>
    public PublishResult? TakeAcknowledgement()
    {
        var a = _acknowledgement;
        _acknowledgement = null;
        return a;
    }

    /// <summary>
    ///     The notes starting in [<paramref name="fromMs" />, <paramref name="toMs" />) of carrier time — one 60 Hz
    ///     frame — into rows; and the frame's cues: low (kick, bass), high (above middle C), filter (how busy).
    /// </summary>
    public (float Low, float High, float Filter) Frame(double fromMs, double toMs)
    {
        if (double.IsNaN(_playStart) || fromMs - _playStart >= midi.LengthMs + GapMs)
        {
            _playStart = fromMs;
            _next = 0;
            Starts++;
        }

        if (double.IsNaN(_rowStart)) _rowStart = Math.Floor(fromMs); // rows every 200 ms from here on, empty or not
        var notes = midi.Notes;
        float hit = 0;
        for (; _next < notes.Count && _playStart + notes[_next].StartMs < toMs; _next++)
        {
            var n = notes[_next];
            var voice = _voices[n.Track];
            double at = _playStart + n.StartMs, length = Math.Max(30, n.EndMs - n.StartMs);
            Add(at, voice, n.Key, n.Velocity, length);
            var weight = n.Velocity / 127f;
            if (voice == Voice.Drums && n.Key is 35 or 36)
                hit = Math.Max(hit, weight); // only the kick pumps the field: a bassline shows as notes
            _sounding.Add((at, at + length, n.Key, weight,
                n.Key >= 60 || (voice == Voice.Drums && n.Key is 42 or 44 or 46 or 51), voice));
        }

        _expiredBefore = fromMs - 1500;
        _sounding.RemoveAll(_expired ??= s =>
            s.End <= _expiredBefore); // one predicate, made once (a lambda over fromMs: one a frame)
        float high = 0, busy = 0;
        foreach (var s in _sounding)
        {
            if (s.End <= fromMs) continue; // released
            busy++;
            if (s.High) high += s.Weight;
        }

        _kick = Math.Max(hit, _kick * 0.85f);
        _high += (Math.Min(1, high / 3) - _high) * 0.2f;
        _busy += (Math.Min(1, busy / 24) - _busy) * 0.01f; // slow: ~1.7 s at 60 Hz
        Bar = (int)((fromMs - _playStart) / _barMs) + 1;
        while (toMs >= _rowStart + RowMs) Flush(_rowStart + RowMs); // windows whose time is up
        return (_kick, _high, 0.3f + 0.7f * _busy);
    }

    /// <summary>
    ///     The notes sounding at <paramref name="atMs" /> (carrier time) as a spectrum for <see cref="Ridges.Compute" />:
    ///     each at its pitch, as loud as it is played, swelling in (~150 ms) and dying away once released (~350 ms) — no
    ///     strike: a quiet, repeating part rises and falls gently rather than jumping with every note. Drums aside.
    /// </summary>
    public void Spectrum(double atMs, Span<float> spectrum)
    {
        spectrum.Clear();
        foreach (var s in _sounding)
        {
            if (s.Voice == Voice.Drums || s.Start > atMs) continue;
            var held = Math.Min(atMs, s.End) - s.Start;
            var energy = s.Weight * (1 - Math.Exp(-held / 150));
            if (atMs > s.End) energy *= Math.Exp(-(atMs - s.End) / 350);
            Ridges.AddNote(spectrum, s.Key, (float)energy);
        }
    }

    private void Add(double at, Voice voice, byte key, byte velocity, double length)
    {
        while (at >= _rowStart + RowMs) Flush(_rowStart + RowMs);
        if (_row.Count == RowNotes) Flush(Math.Floor(at)); // full: the next row starts here, its window from here
        Span<byte> d = _row.Data;
        d[_length] = (byte)(at - _rowStart);
        d[_length + 1] = (byte)voice;
        d[_length + 2] = key;
        d[_length + 3] = velocity;
        BinaryPrimitives.WriteUInt16LittleEndian(d[(_length + 4)..], (ushort)Math.Min(ushort.MaxValue, length));
        _length += 6;
        _row.Count++;
    }

    private void Flush(double nextStart)
    {
        _row.Seq = _seq;
        _row.StartMs = (uint)_rowStart;
        _row.Length = (byte)_length;
        if (Publishing)
        {
            using (var u = rows.BeginUpdate())
            {
                u.Set((byte)(_seq % Window), _row); // overwrites the row a window ago
                u.Complete();
            }

            _acknowledgement = rows.Acknowledge();
            RowsPublished++;
            BytesPublished += 12 + _length;
        }

        _seq++;
        _row = default;
        _length = 0;
        _rowStart = nextStart;
    }

    private static Voice VoiceOf(string name, MidiNote first)
    {
        if (first.Channel == 9) return Voice.Drums;
        var n = name.ToLowerInvariant();
        if (n.Contains("sine")) return Voice.Sine;
        if (n.Contains("square")) return Voice.Square;
        if (n.Contains("saw")) return Voice.Saw;
        if (n.Contains("flute")) return Voice.Flute;
        if (n.Contains("electric piano") || n.Contains("e-piano") || n.Contains("rhodes")) return Voice.EPiano;
        if (n.Contains("piano")) return Voice.Piano;
        if (n.Contains("harp")) return Voice.Harp;
        if (n.Contains("koto")) return Voice.Koto;
        if (n.Contains("guitar")) return Voice.Pluck;
        if (n.Contains("bass")) return Voice.Bass;
        if (n.Contains("synth") || n.Contains("pad") || n.Contains("string")) return Voice.Pad;
        return first.Program switch // General MIDI families
        {
            < 8 => Voice.Piano, < 16 => Voice.EPiano, < 24 => Voice.Pad, < 32 => Voice.Pluck, < 40 => Voice.Bass,
            < 56 => Voice.Pad, < 80 => Voice.Flute, < 88 => Voice.Saw, < 104 => Voice.Pad, < 112 => Voice.Koto,
            _ => Voice.Pluck
        };
    }
}