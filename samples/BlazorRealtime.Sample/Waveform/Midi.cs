using System.Buffers.Binary;
using System.Text;

namespace BlazorRealtime.Sample.Waveform;

/// <summary>A note, in milliseconds from the start of the piece.</summary>
public readonly record struct MidiNote(
    double StartMs,
    double EndMs,
    byte Channel,
    byte Key,
    byte Velocity,
    byte Program,
    byte Track);

/// <summary>
///     A Standard MIDI File (format 0 or 1), read into notes on one timeline: tempo changes applied (from any track),
///     note-ons paired with their note-offs (a note-on of velocity 0 is an off; the sustain pedal holds offs until it
///     lifts),
///     each note with its channel's program at the time. Tracks' names, for the voices. Enough to play a piece, not to
///     edit one.
/// </summary>
public sealed class MidiFile
{
    public required IReadOnlyList<MidiNote> Notes { get; init; }
    public required IReadOnlyList<string> TrackNames { get; init; }
    public required double LengthMs { get; init; }
    public required double FirstTempoBpm { get; init; }

    public static MidiFile Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 14 || !file[..4].SequenceEqual("MThd"u8)) throw new InvalidDataException("Not a MIDI file.");
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(file[4..]);
        int tracks = BinaryPrimitives.ReadUInt16BigEndian(file[10..]),
            division = BinaryPrimitives.ReadUInt16BigEndian(file[12..]);
        if ((division & 0x8000) != 0) throw new NotSupportedException("SMPTE time division.");
        var events = new List<(long Tick, int Order, int Track, byte Status, byte A, byte B, int Tempo)>();
        var names = new List<string>();
        int pos = 8 + headerLength, order = 0;
        for (var t = 0; t < tracks && pos + 8 <= file.Length; t++)
        {
            if (!file.Slice(pos, 4).SequenceEqual("MTrk"u8)) throw new InvalidDataException("Expected a track.");
            var length = BinaryPrimitives.ReadInt32BigEndian(file[(pos + 4)..]);
            var track = file.Slice(pos + 8, Math.Min(length, file.Length - pos - 8));
            pos += 8 + length;
            long tick = 0;
            byte running = 0;
            var name = "";
            for (var i = 0; i < track.Length;)
            {
                tick += ReadVarLen(track, ref i);
                var status = track[i];
                if (status >= 0x80) i++;
                else status = running; // running status: the data byte is this event's first
                if (status == 0xFF)
                {
                    var type = track[i++];
                    var n = (int)ReadVarLen(track, ref i);
                    if (type == 0x51 && n == 3)
                        events.Add(
                            (tick, order++, t, 0xFF, 0, 0, (track[i] << 16) | (track[i + 1] << 8) | track[i + 2]));
                    else if (type == 0x03 && name.Length == 0) name = Encoding.Latin1.GetString(track.Slice(i, n));
                    i += n;
                    continue;
                }

                if (status is 0xF0 or 0xF7)
                {
                    i += (int)ReadVarLen(track, ref i);
                    continue;
                } // sysex

                running = status;
                var kind = status & 0xF0;
                byte a = track[i++], b = kind is 0xC0 or 0xD0 ? (byte)0 : track[i++];
                if (kind is 0x80 or 0x90 or 0xB0 or 0xC0) events.Add((tick, order++, t, status, a, b, 0));
            }

            names.Add(name);
        }

        events.Sort((x, y) => x.Tick != y.Tick ? x.Tick.CompareTo(y.Tick) : x.Order.CompareTo(y.Order));

        // Ticks to milliseconds, through the tempo map; notes paired per channel and key (first on, first off).
        var notes = new List<MidiNote>();
        var open = new Dictionary<int, Queue<(double At, byte Velocity, byte Program, byte Track)>>();
        var held = new List<(int Key, double At, byte Velocity, byte Program, byte Track)>[16];
        var program = new byte[16];
        var sustain = new bool[16];
        double usPerTick = 500_000.0 / division, ms = 0, firstTempo = 0;
        long lastTick = 0;
        foreach (var e in events)
        {
            ms += (e.Tick - lastTick) * usPerTick / 1000;
            lastTick = e.Tick;
            if (e.Status == 0xFF)
            {
                usPerTick = (double)e.Tempo / division;
                if (firstTempo == 0) firstTempo = 60_000_000.0 / e.Tempo;
                continue;
            }

            int channel = e.Status & 0x0F, kind = e.Status & 0xF0;
            if (kind == 0xC0)
            {
                program[channel] = e.A;
                continue;
            }

            if (kind == 0xB0)
            {
                if (e.A != 64) continue; // the sustain pedal
                sustain[channel] = e.B >= 64;
                if (!sustain[channel] && held[channel] is { } list)
                {
                    foreach (var h in list)
                        notes.Add(new MidiNote(h.At, ms, (byte)channel, (byte)h.Key, h.Velocity, h.Program, h.Track));
                    list.Clear();
                }

                continue;
            }

            var slot = (channel << 7) | e.A;
            if (kind == 0x90 && e.B > 0)
            {
                if (!open.TryGetValue(slot, out var q))
                    open[slot] = q = new Queue<(double At, byte Velocity, byte Program, byte Track)>();
                q.Enqueue((ms, e.B, program[channel], (byte)e.Track));
            }
            else if (open.TryGetValue(slot, out var q) && q.Count > 0)
            {
                var on = q.Dequeue();
                if (sustain[channel]) (held[channel] ??= []).Add((e.A, on.At, on.Velocity, on.Program, on.Track));
                else notes.Add(new MidiNote(on.At, ms, (byte)channel, e.A, on.Velocity, on.Program, on.Track));
            }
        }

        foreach (var (slot, q) in open) // never released: to the end
            while (q.Count > 0)
            {
                var on = q.Dequeue();
                notes.Add(new MidiNote(on.At, ms, (byte)(slot >> 7), (byte)(slot & 127), on.Velocity, on.Program,
                    on.Track));
            }

        notes.Sort((x, y) => x.StartMs.CompareTo(y.StartMs));
        return new MidiFile
            { Notes = notes, TrackNames = names, LengthMs = ms, FirstTempoBpm = firstTempo == 0 ? 120 : firstTempo };
    }

    private static long ReadVarLen(ReadOnlySpan<byte> s, ref int i)
    {
        long v = 0;
        byte b;
        do
        {
            b = s[i++];
            v = (v << 7) | (byte)(b & 0x7F);
        } while ((b & 0x80) != 0);

        return v;
    }
}