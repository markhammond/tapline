using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tapline.MagicOnion.Providers;

/// <summary>
///     Fixed-size blocks of bytes, of any size under 64 KB, as types: what a router makes a table of for a provider in another
///     process, whose keys and rows it knows only by their sizes. A size is a sum of powers of two, each a block of its own,
///     concatenated (<see cref="Cat{TA, TB}" />); a key is wrapped to compare and hash as bytes (<see cref="Key{T}" />).
/// </summary>
internal static class Bytes
{
    private static readonly Type[] Powers =
    [
        typeof(B1), typeof(B2), typeof(B4), typeof(B8), typeof(B16), typeof(B32), typeof(B64), typeof(B128),
        typeof(B256), typeof(B512), typeof(B1024), typeof(B2048), typeof(B4096), typeof(B8192), typeof(B16384),
        typeof(B32768)
    ];

    /// <summary>A type of exactly <paramref name="size" /> bytes, 1 to 65,535.</summary>
    public static Type Of(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, ushort.MaxValue);
        Type? type = null;
        for (var bit = Powers.Length - 1; bit >= 0; bit--)
        {
            if ((size & (1 << bit)) == 0) continue;
            type = type is null ? Powers[bit] : typeof(Cat<,>).MakeGenericType(type, Powers[bit]);
        }

        return type!;
    }

    /// <summary>A key of exactly <paramref name="size" /> bytes, compared and hashed as bytes.</summary>
    public static Type KeyOf(int size)
    {
        return typeof(Key<>).MakeGenericType(Of(size));
    }
}

/// <summary>Two blocks, one after the other.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Cat<TA, TB> where TA : unmanaged where TB : unmanaged
{
    public TA A;
    public TB B;
}

/// <summary>A key's bytes: equal when every byte is, hashed over them all.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct Key<T> : IEquatable<Key<T>> where T : unmanaged
{
    private readonly T _bytes;

    public bool Equals(Key<T> other)
    {
        return MemoryMarshal.AsBytes(new ReadOnlySpan<Key<T>>(in this))
            .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Key<T>>(in other)));
    }

    public override bool Equals(object? obj)
    {
        return obj is Key<T> other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(MemoryMarshal.AsBytes(new ReadOnlySpan<Key<T>>(in this)));
        return hash.ToHashCode();
    }
}

[InlineArray(1)]
internal struct B1
{
    private byte _e;
}

[InlineArray(2)]
internal struct B2
{
    private byte _e;
}

[InlineArray(4)]
internal struct B4
{
    private byte _e;
}

[InlineArray(8)]
internal struct B8
{
    private byte _e;
}

[InlineArray(16)]
internal struct B16
{
    private byte _e;
}

[InlineArray(32)]
internal struct B32
{
    private byte _e;
}

[InlineArray(64)]
internal struct B64
{
    private byte _e;
}

[InlineArray(128)]
internal struct B128
{
    private byte _e;
}

[InlineArray(256)]
internal struct B256
{
    private byte _e;
}

[InlineArray(512)]
internal struct B512
{
    private byte _e;
}

[InlineArray(1024)]
internal struct B1024
{
    private byte _e;
}

[InlineArray(2048)]
internal struct B2048
{
    private byte _e;
}

[InlineArray(4096)]
internal struct B4096
{
    private byte _e;
}

[InlineArray(8192)]
internal struct B8192
{
    private byte _e;
}

[InlineArray(16384)]
internal struct B16384
{
    private byte _e;
}

[InlineArray(32768)]
internal struct B32768
{
    private byte _e;
}
