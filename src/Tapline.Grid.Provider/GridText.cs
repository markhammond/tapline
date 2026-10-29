namespace Tapline.Grid.Provider;

/// <summary>
///     Text for a row's text field (a grid column's <c>CellKind.Text</c>): an inline array of bytes, written as the page
///     reads it.
/// </summary>
public static class GridText
{
    /// <summary>
    ///     Writes <paramref name="text" /> into <paramref name="field" /> (an inline array's bytes, at most 128): its first
    ///     byte the length, then UTF-8 — cut short at a character's boundary if it doesn't fit, and flagged (0x80).
    /// </summary>
    public static void Write(Span<byte> field, ReadOnlySpan<char> text)
    {
        if (field.Length is < 2 or > 128)
            throw new ArgumentException($"A text field of {field.Length} bytes: from 2 to 128.", nameof(field));
        field.Clear();
        var room = field.Length - 1;
        var cut = false;
        var written = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (written + rune.Utf8SequenceLength > room)
            {
                cut = true;
                break;
            }

            written += rune.EncodeToUtf8(field[(1 + written)..]);
        }

        field[0] = (byte)(written | (cut ? 0x80 : 0));
    }

    /// <summary>
    ///     Writes <paramref name="utf8" />, text already UTF-8, into <paramref name="field" /> as <see cref="Write(Span{byte}, ReadOnlySpan{char})" />
    ///     does: cut short at a character's boundary if it doesn't fit, and flagged.
    /// </summary>
    public static void Write(Span<byte> field, ReadOnlySpan<byte> utf8)
    {
        if (field.Length is < 2 or > 128)
            throw new ArgumentException($"A text field of {field.Length} bytes: from 2 to 128.", nameof(field));
        field.Clear();
        var written = Math.Min(utf8.Length, field.Length - 1);
        if (written < utf8.Length)
            while (written > 0 && (utf8[written] & 0xC0) == 0x80) // (not inside a character: back to where one starts)
                written--;
        utf8[..written].CopyTo(field[1..]);
        field[0] = (byte)(written | (written < utf8.Length ? 0x80 : 0));
    }
}
