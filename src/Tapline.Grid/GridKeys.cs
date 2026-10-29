using System.Runtime.InteropServices;

namespace Tapline.Grid;

/// <summary>A grid's keys as the page holds them: their bytes, one character each (latin1), as tapline.js keys its replica.</summary>
internal static class GridKeys<TKey> where TKey : unmanaged
{
    private static readonly int Size = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(new TKey[1])).Length;

    /// <summary>
    ///     How the page compares the keys, to order a window sent without them (<see cref="GridOrder.ByKey" />): "guid" (a
    ///     <see cref="Guid" />'s bytes as .NET lays them out, compared as <see cref="Guid.CompareTo(Guid)" />), "int" or
    ///     "uint" (a little-endian integer). Null: it can't, so every order goes with its keys.
    /// </summary>
    public static readonly string? Order =
        typeof(TKey) == typeof(Guid) ? "guid"
        : typeof(TKey) == typeof(long) || typeof(TKey) == typeof(int) || typeof(TKey) == typeof(short) ||
          typeof(TKey) == typeof(sbyte) ? "int"
        : typeof(TKey) == typeof(ulong) || typeof(TKey) == typeof(uint) || typeof(TKey) == typeof(ushort) ||
          typeof(TKey) == typeof(byte) ? "uint"
        : null;

    public static string ToPage(in TKey key)
    {
        return string.Create(Size, key, static (chars, k) =>
        {
            var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in k));
            for (var i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
        });
    }

    /// <summary>A key from the page's text of it: false if it isn't one (another size, a character past 255).</summary>
    public static bool TryFromPage(string? text, out TKey key)
    {
        key = default;
        if (text is null || text.Length != Size) return false;
        Span<byte> bytes = stackalloc byte[Size];
        for (var i = 0; i < Size; i++)
        {
            if (text[i] > 0xFF) return false;
            bytes[i] = (byte)text[i];
        }

        key = MemoryMarshal.Read<TKey>(bytes);
        return true;
    }
}
