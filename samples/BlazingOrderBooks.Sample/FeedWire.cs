using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tapline;

namespace BlazingOrderBooks.Sample;

/// <summary>
///     The exchange's wire: fixed little-endian layouts (SBE-like), each message a 16-byte header — template, length,
///     the exchange's sequence number — then its block, lengths multiples of 8. Read in place: the handler casts the
///     bytes to these structs (<see cref="Unsafe.ReadUnaligned{T}(ref byte)" />), copying nothing. Two feeds share the
///     wire, told apart by template: trades, printed as they happen, and depth, conflated per interval — and, from a
///     venue that publishes one (Binance's <c>bookTicker</c>), its best bid and offer as they change.
/// </summary>
internal static class Wire
{
    public const ushort Pad = 0, TradeTemplate = 1, DepthTemplate = 2, SnapshotTemplate = 3, TopTemplate = 4;
    public const int HeaderSize = 16, DepthLevelSize = 24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ref T At<T>(byte* at) where T : unmanaged
    {
        return ref Unsafe.AsRef<T>(at);
    }

    /// <summary>
    ///     A message's address, where it lies in the wire's ring (<see cref="SpscByteRing" />): its buffer is pinned for
    ///     life, so the address stays put while the handler reads the message in place.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe byte* Address(ReadOnlySpan<byte> message)
    {
        return (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(message));
    }

    /// <summary>A message's first 16 bytes. A pad (skipping to the ring's start) is its first 8: template and length.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = HeaderSize)]
    public struct Header
    {
        public ushort Template;
        public ushort Length; // the whole message, header included
        public uint Reserved;
        public long Seq; // the exchange's event sequence: a trade's own; a depth update's last
    }

    /// <summary>A fill, printed at once: template 1, 56 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 56)]
    public struct Trade
    {
        public Header Header;
        public long TradeId;
        public long Price; // ticks
        public long Size;
        public long Timestamp; // the exchange's clock, ns
        public byte Aggressor; // Level.Bid: a buyer took asks; Level.Ask: a seller hit bids
        private byte _p1, _p2, _p3, _p4, _p5, _p6, _p7;
    }

    /// <summary>
    ///     The venue's best bid and offer, as they changed: template 4, 56 bytes. Nothing is better than either; each is
    ///     at its size. Faster than depth, so it accelerates the book as prints do, and more exactly: it states the top.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 56)]
    public struct Top
    {
        public Header Header;
        public long BidPrice, BidSize, AskPrice, AskSize;
        public long Timestamp; // the venue's clock, ns
    }

    /// <summary>
    ///     A depth update: template 2, 48 bytes then <see cref="Count" /> levels (<see cref="DepthLevel" />) — the levels
    ///     that changed since the last update, each with its size as it stands now (0: gone). The events it reflects are
    ///     <see cref="FirstSeq" /> through the header's seq; the last trade or top-of-book update among them,
    ///     <see cref="LastTradeSeq" />. A
    ///     snapshot (template 3, the recovery channel) has the same layout: every level, as of the header's seq.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 48)]
    public struct Depth
    {
        public Header Header;
        public long FirstSeq;
        public long LastTradeSeq;
        public long Timestamp;
        public ushort Count;
        private ushort _p1;
        private uint _p2;
    }

    /// <summary>A level in a depth update or a snapshot: 24 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 24)]
    public struct DepthLevel
    {
        public byte Side;
        private byte _p1, _p2, _p3, _p4, _p5, _p6, _p7;
        public long Price;
        public long Size;
    }
}
