using System.Runtime.CompilerServices;
using System.Text;
using Tapline.Grid;
using Tapline.Grid.Provider;

namespace LiveGrid.Sample;

/// <summary>Text of up to 23 bytes of UTF-8, inline (GridText's layout: its length, then the bytes).</summary>
[InlineArray(24)]
public struct Text24
{
    private byte _0;

    public static Text24 Of(string text)
    {
        var field = new Text24();
        GridText.Write(field, text);
        return field;
    }

    public override string ToString()
    {
        return InlineText.String(this);
    }
}

/// <summary>Text of up to 15 bytes of UTF-8, inline (GridText's layout).</summary>
[InlineArray(16)]
public struct Text16
{
    private byte _0;

    public static Text16 Of(string text)
    {
        var field = new Text16();
        GridText.Write(field, text);
        return field;
    }

    public override string ToString()
    {
        return InlineText.String(this);
    }
}

/// <summary>Inline text read (<see cref="Text24" />, <see cref="Text16" />): the bytes of a field in GridText's layout, or a string of them.</summary>
public static class InlineText
{
    public static ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> field)
    {
        return field.Slice(1, field[0] & 0x7F);
    }

    public static string String(ReadOnlySpan<byte> field)
    {
        return Encoding.UTF8.GetString(Bytes(field));
    }

    public static bool Is(ReadOnlySpan<byte> field, ReadOnlySpan<byte> text)
    {
        return Bytes(field).SequenceEqual(text);
    }
}

/// <summary>The names in rows as strings, each made once: a house's names are few, so a record's are shared.</summary>
internal static class Names
{
    private static readonly Dictionary<string, string> Made = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> Looked = Made.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly Lock Gate = new();

    public static string Of(ReadOnlySpan<byte> field)
    {
        Span<char> chars = stackalloc char[128];
        var name = chars[..Encoding.UTF8.GetChars(InlineText.Bytes(field), chars)];
        lock (Gate)
        {
            if (Looked.TryGetValue(name, out var made)) return made;
            var text = name.ToString();
            Made[text] = text;
            return text;
        }
    }
}
