using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Tapline.Alerts;

/// <summary>
///     An alert shape's state (virtual-grid.md §15.3, as the owner put it: "alerts can be shared if they have the same
///     shape"): one source for every rule, in every audience, with the same source, condition and trigger — the running
///     count of the rows it has fired since it began, when it last fired, and its latest row's key and what it was.
///     Published at every firing; each subscription takes it no oftener than its rule's throttle (Tapline's
///     <c>Tap.MinInterval</c>: "alerts can be conflated … debounced according to minimum interval"), whatever fired
///     meanwhile conflated into it. 192 bytes, packed: <c>tapline-alerts.js</c> reads it at these offsets.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AlertFeedValue
{
    public long Fired; // @0: rows fired since the shape began (a count's: times it entered)
    public long FiredAt; // @8: when it last fired, µs since 1970 (UTC): none, 0
    public AlertText48 Key; // @16: the latest row's key (none, a count's)
    public AlertText128 Summary; // @64: what it was ("26 in the last 5 s", a count's)

    /// <summary>The value's size, as it goes on the wire.</summary>
    public static int Size => Unsafe.SizeOf<AlertFeedValue>();
}

/// <summary>
///     A rule as its audience has it: its row in the audience's inbox, keyed by the rule's id — how far the audience has
///     presented its shape's running count (a page opened later presents what fired since), and what a page needs to
///     present it: its name, its sound, whether it's muted, its source. 105 bytes, packed.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AlertRow
{
    public long Presented; // @0: the shape's running count, as far as a page of the audience has presented it
    public byte Muted; // @8: 1, muted (counted and marked, not sounded)
    public AlertText16 Source; // @9
    public AlertText48 Name; // @25
    public AlertText32 Sound; // @73

    /// <summary>The row's size, as it goes on the wire.</summary>
    public static int Size => Unsafe.SizeOf<AlertRow>();
}

/// <summary>Text of up to 15 bytes of UTF-8, inline: its length, flagged (high bit) if it was cut, then the bytes.</summary>
[InlineArray(16)]
public struct AlertText16
{
    private byte _0;
}

/// <summary>Text of up to 31 bytes of UTF-8, inline (<see cref="AlertText16" />'s layout).</summary>
[InlineArray(32)]
public struct AlertText32
{
    private byte _0;
}

/// <summary>Text of up to 47 bytes of UTF-8, inline (<see cref="AlertText16" />'s layout).</summary>
[InlineArray(48)]
public struct AlertText48
{
    private byte _0;
}

/// <summary>Text of up to 127 bytes of UTF-8, inline (<see cref="AlertText16" />'s layout).</summary>
[InlineArray(128)]
public struct AlertText128
{
    private byte _0;
}

/// <summary>Inline text written and read: its length (low seven bits) flagged if cut (high bit), then its UTF-8.</summary>
public static class AlertText
{
    /// <summary>
    ///     <paramref name="text" /> into <paramref name="field" />: cut at a character's boundary to fit, and flagged so.
    /// </summary>
    public static void Write(Span<byte> field, string text)
    {
        field.Clear();
        var room = Math.Min(field.Length - 1, 0x7F);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= room)
        {
            field[0] = (byte)Encoding.UTF8.GetBytes(text, field[1..]);
            return;
        }

        var written = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (written + rune.Utf8SequenceLength > room) break;
            written += rune.EncodeToUtf8(field[(1 + written)..]);
        }

        field[0] = (byte)(written | 0x80);
    }

    /// <summary>A field's text, and whether it was cut.</summary>
    public static (string Text, bool Cut) Read(ReadOnlySpan<byte> field)
    {
        return (Encoding.UTF8.GetString(field.Slice(1, field[0] & 0x7F)), (field[0] & 0x80) != 0);
    }
}
