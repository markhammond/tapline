using System.Runtime.InteropServices;
using Tapline;
using Tapline.Wasm;

namespace BlazingOrderBooks.Sample;

/// <summary>The domain lives in the app: an L2 book is a Tapline table keyed by (side, price) with quantity rows.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct Level(byte Side, long Price)
{
    public const byte Bid = 0, Ask = 1;
}

/// <summary>
///     A VWAP book's state at a level (a group of levels): the sizes from the best price down to it, and their notional —
///     each level's price × size — summed. Kept by the router, folded from size and notional changes; never sent.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct Sums(long Size, long Notional);

/// <summary>
///     A VWAP book's row, what subscribers see: the cumulative size, and the price filling all of it averages to (notional
///     over size), with <see cref="Market.VwapDigits" /> more places than the book's prices — it falls between ticks.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct VwapRow(long Size, long Vwap);

public static class Market
{
    /// <summary>
    ///     Rows (u32 flags | size i64), then deltas (u32 row | size change i64): each change added to its row, present
    ///     while non-zero.
    /// </summary>
    /// <remarks>
    ///     In C# (the module's memory a byte array, little-endian as WebAssembly's is; a trap an exception):
    ///     <code><![CDATA[
    /// // buffer: room for size bytes (whole 64 KiB pages), the batch written at offset 0.
    /// static int Buffer(ref byte[] memory, int size)
    /// {
    ///     var shortPages = (int)(((uint)size + 65535) >> 16) - memory.Length / 65536;
    ///     if (shortPages > 0) Array.Resize(ref memory, memory.Length + shortPages * 65536); // memory.grow
    ///     return 0;
    /// }
    ///
    /// // fold: rowCount rows (12 bytes each) at at, then deltaCount deltas (12 bytes each).
    /// static void Fold(Span<byte> memory, int at, int rowCount, int rowSize, int deltaCount, int deltaSize)
    /// {
    ///     if (rowSize != 8 || deltaSize != 8) throw new InvalidOperationException(); // unreachable
    ///     var end = at + rowCount * 12 + deltaCount * 12;
    ///     for (var d = at + rowCount * 12; d < end; d += 12)
    ///     {
    ///         var row = at + (int)BinaryPrimitives.ReadUInt32LittleEndian(memory[d..]) * 12;
    ///         var size = BinaryPrimitives.ReadInt64LittleEndian(memory[(row + 4)..])
    ///                    + BinaryPrimitives.ReadInt64LittleEndian(memory[(d + 4)..]);
    ///         BinaryPrimitives.WriteInt64LittleEndian(memory[(row + 4)..], size);
    ///         BinaryPrimitives.WriteUInt32LittleEndian(memory[row..], size != 0 ? 1u : 0u); // present while non-zero
    ///     }
    /// }
    /// ]]></code>
    /// </remarks>
    private const string SizeChangesWat =
        """
        (module
          (memory (export "memory") 1)
          (func (export "buffer") (param $size i32) (result i32)
            (local $short i32)
            (local.set $short (i32.sub (i32.shr_u (i32.add (local.get $size) (i32.const 65535)) (i32.const 16)) (memory.size)))
            (if (i32.gt_s (local.get $short) (i32.const 0))
              (then (if (i32.eq (memory.grow (local.get $short)) (i32.const -1)) (then unreachable))))
            (i32.const 0))
          (func (export "fold") (param $at i32) (param $rowCount i32) (param $rowSize i32) (param $deltaCount i32) (param $deltaSize i32)
            (local $d i32) (local $end i32) (local $row i32) (local $size i64)
            (if (i32.or (i32.ne (local.get $rowSize) (i32.const 8)) (i32.ne (local.get $deltaSize) (i32.const 8))) (then unreachable))
            (local.set $d (i32.add (local.get $at) (i32.mul (local.get $rowCount) (i32.const 12))))
            (local.set $end (i32.add (local.get $d) (i32.mul (local.get $deltaCount) (i32.const 12))))
            (block $done
              (loop $next
                (br_if $done (i32.ge_u (local.get $d) (local.get $end)))
                (local.set $row (i32.add (local.get $at) (i32.mul (i32.load align=1 (local.get $d)) (i32.const 12))))
                (local.set $size (i64.add (i64.load offset=4 align=1 (local.get $row)) (i64.load offset=4 align=1 (local.get $d))))
                (i64.store offset=4 align=1 (local.get $row) (local.get $size))
                (i32.store align=1 (local.get $row) (i64.ne (local.get $size) (i64.const 0)))
                (local.set $d (i32.add (local.get $d) (i32.const 12)))
                (br $next)))))
        """;

    /// <summary>
    ///     The top of the book: entries are u32 flags (bit 0: present) | side u8 | price i64 | row (size i64 first: a book's
    ///     row, or a VWAP book's), at the offset buffer() gives.
    /// </summary>
    /// <remarks>
    ///     In C# (buffer as <see cref="SizeChangesWat" />'s; entries 13 + rowSize bytes: flags at 0, side at 4, price at 5,
    ///     size at 13, the rest of the row after it, kept):
    ///     <code><![CDATA[
    /// // degrade: preserve top of book price with zero size.
    /// static void Degrade(Span<byte> memory, int at, int count, int keySize, int rowSize)
    /// {
    ///     if (keySize != 9 || rowSize < 8) throw new InvalidOperationException(); // unreachable
    ///     var stride = 13 + rowSize;
    ///     int bid = -1, ask = -1;
    ///     long bidPrice = 0, askPrice = 0;
    ///     for (var i = 0; i < count; i++) // the highest bid and the lowest ask, present or not
    ///     {
    ///         var e = memory[(at + i * stride)..];
    ///         var price = BinaryPrimitives.ReadInt64LittleEndian(e[5..]);
    ///         if (e[4] == Level.Bid)
    ///         {
    ///             if (bid < 0 || price > bidPrice) (bid, bidPrice) = (i, price);
    ///         }
    ///         else if (ask < 0 || price < askPrice) (ask, askPrice) = (i, price);
    ///     }
    ///
    ///     for (var i = 0; i < count; i++)
    ///     {
    ///         var e = memory[(at + i * stride)..];
    ///         var flags = BinaryPrimitives.ReadUInt32LittleEndian(e) & ~1u;
    ///         BinaryPrimitives.WriteUInt32LittleEndian(e, flags | (i == bid || i == ask ? 1u : 0u));
    ///         BinaryPrimitives.WriteInt64LittleEndian(e[13..], 0);
    ///     }
    /// }
    /// ]]></code>
    /// </remarks>
    private const string TopOfBookWat =
        """
        (module
          (memory (export "memory") 1)
          (func (export "buffer") (param $size i32) (result i32)
            (local $short i32)
            (local.set $short (i32.sub (i32.shr_u (i32.add (local.get $size) (i32.const 65535)) (i32.const 16)) (memory.size)))
            (if (i32.gt_s (local.get $short) (i32.const 0))
              (then (if (i32.eq (memory.grow (local.get $short)) (i32.const -1)) (then unreachable))))
            (i32.const 0))
          (func (export "degrade") (param $at i32) (param $count i32) (param $keySize i32) (param $rowSize i32)
            (local $i i32) (local $e i32) (local $bid i32) (local $ask i32) (local $p i64) (local $bp i64) (local $ap i64)
            (local $stride i32)
            (if (i32.or (i32.ne (local.get $keySize) (i32.const 9)) (i32.lt_s (local.get $rowSize) (i32.const 8))) (then unreachable))
            (local.set $stride (i32.add (local.get $rowSize) (i32.const 13)))
            (local.set $bid (i32.const -1))
            (local.set $ask (i32.const -1))
            (block $scanned
              (loop $scan
                (br_if $scanned (i32.ge_u (local.get $i) (local.get $count)))
                (local.set $e (i32.add (local.get $at) (i32.mul (local.get $i) (local.get $stride))))
                (local.set $p (i64.load offset=5 align=1 (local.get $e)))
                (if (i32.eqz (i32.load8_u offset=4 (local.get $e)))
                  (then (if (i32.or (i32.lt_s (local.get $bid) (i32.const 0)) (i64.gt_s (local.get $p) (local.get $bp)))
                    (then (local.set $bid (local.get $i)) (local.set $bp (local.get $p)))))
                  (else (if (i32.or (i32.lt_s (local.get $ask) (i32.const 0)) (i64.lt_s (local.get $p) (local.get $ap)))
                    (then (local.set $ask (local.get $i)) (local.set $ap (local.get $p))))))
                (local.set $i (i32.add (local.get $i) (i32.const 1)))
                (br $scan)))
            (local.set $i (i32.const 0))
            (block $marked
              (loop $mark
                (br_if $marked (i32.ge_u (local.get $i) (local.get $count)))
                (local.set $e (i32.add (local.get $at) (i32.mul (local.get $i) (local.get $stride))))
                (i32.store align=1 (local.get $e)
                  (i32.or (i32.and (i32.load align=1 (local.get $e)) (i32.const -2))
                          (i32.or (i32.eq (local.get $i) (local.get $bid)) (i32.eq (local.get $i) (local.get $ask)))))
                (i64.store offset=13 align=1 (local.get $e) (i64.const 0))
                (local.set $i (i32.add (local.get $i) (i32.const 1)))
                (br $mark)))))
        """;

    /// <summary>
    ///     While the feed is broken, viewers keep the last best bid and ask with size zero: prices known, no liquidity
    ///     claimed.
    ///     A WebAssembly module, so the page applies the same function itself while the server is out of its reach.
    /// </summary>
    public static readonly ErrorPolicy BookPolicy = new("feed interrupted"u8.ToArray(),
        WasmDegradeFunction.FromText(TopOfBookWat));

    public static readonly ErrorPolicy MarkPolicy = new("mark stale"u8.ToArray());

    public static readonly ErrorPolicy AlertsPolicy = new("alerts interrupted"u8.ToArray());

    /// <summary>
    ///     What the pages' book and price subscriptions ask: a state at most every 50 ms (20 a second), plenty for eyes —
    ///     each the state as it stands, whatever changed meanwhile conflated into it. Messages (the alerts) aren't held.
    /// </summary>
    public static readonly SubscribeOptions ForEyes = new() { MinInterval = TimeSpan.FromMilliseconds(50) };

    /// <summary>
    ///     How the feed describes the book to the router, in step 1 — as data, as a provider in another process would: a
    ///     level's side by name and its price (in ticks of 0.01) as a decimal; a row, the size there. The router checks each
    ///     layout's size against the table's keys and rows, and renders them in its viewer without the feed's types.
    /// </summary>
    public static readonly PayloadFormat BookFormat = new()
    {
        KeyLayout = new PayloadLayout(9,
        [
            new PayloadField("side", 0, FieldKind.UInt8)
                { Names = new Dictionary<long, string> { [Level.Bid] = "bid", [Level.Ask] = "ask" } },
            new PayloadField("price", 1, FieldKind.Int64) { Scale = 2 }
        ]),
        Layout = new PayloadLayout(8, [new PayloadField("size", 0, FieldKind.Int64)])
    };

    /// <summary>The mark: a price in ticks of 0.01, shown bare as a decimal.</summary>
    public static readonly PayloadFormat MarkFormat = new()
        { Layout = new PayloadLayout(8, [new PayloadField("", 0, FieldKind.Int64) { Scale = 2 }]) };

    /// <summary>The alerts: text.</summary>
    public static readonly PayloadFormat AlertsFormat = new() { ContentType = "text/plain; charset=utf-8" };

    /// <summary>
    ///     The book's fold, in WebAssembly: each exchange message's net size change per level added to the level, a level gone
    ///     at zero. The router runs it on whatever the feed posted since it last looked, in one call outside the book's lock
    ///     (see <see cref="MarketFeedHandler" />), bounded by its fuel.
    /// </summary>
    public static readonly WasmBatchFold BookFold = WasmBatchFold.FromText(SizeChangesWat);

    /// <summary>The places a VWAP has past the book's prices': it falls between ticks.</summary>
    public const int VwapDigits = 2;

    /// <summary>
    ///     A VWAP book's fold and finish, one module: states (<see cref="Sums" />: u32 flags | size i64 | notional i64), each
    ///     change (size i64 | notional i64) added to its state, present while its size is non-zero; then each state's row
    ///     (<see cref="VwapRow" />): its size, and notional × 100 / size, rounded to the nearest.
    /// </summary>
    /// <remarks>
    ///     In C# (buffer as <see cref="SizeChangesWat" />'s; finalize entries 36 bytes: flags | state 16 | row 16):
    ///     <code><![CDATA[
    /// static void Fold(Span<byte> memory, int at, int rowCount, int rowSize, int deltaCount, int deltaSize)
    /// {
    ///     if (rowSize != 16 || deltaSize != 16) throw new InvalidOperationException(); // unreachable
    ///     var end = at + rowCount * 20 + deltaCount * 20;
    ///     for (var d = at + rowCount * 20; d < end; d += 20)
    ///     {
    ///         var row = at + (int)BinaryPrimitives.ReadUInt32LittleEndian(memory[d..]) * 20;
    ///         var size = BinaryPrimitives.ReadInt64LittleEndian(memory[(row + 4)..]) + BinaryPrimitives.ReadInt64LittleEndian(memory[(d + 4)..]);
    ///         var notional = BinaryPrimitives.ReadInt64LittleEndian(memory[(row + 12)..]) + BinaryPrimitives.ReadInt64LittleEndian(memory[(d + 12)..]);
    ///         BinaryPrimitives.WriteInt64LittleEndian(memory[(row + 4)..], size);
    ///         BinaryPrimitives.WriteInt64LittleEndian(memory[(row + 12)..], notional);
    ///         BinaryPrimitives.WriteUInt32LittleEndian(memory[row..], size != 0 ? 1u : 0u); // present while non-zero
    ///     }
    /// }
    ///
    /// static void Finalize(Span<byte> memory, int at, int count, int stateSize, int rowSize)
    /// {
    ///     if (stateSize != 16 || rowSize != 16) throw new InvalidOperationException(); // unreachable
    ///     for (var e = at; e < at + count * 36; e += 36)
    ///     {
    ///         if ((BinaryPrimitives.ReadUInt32LittleEndian(memory[e..]) & 1) == 0) continue;
    ///         var size = BinaryPrimitives.ReadInt64LittleEndian(memory[(e + 4)..]);
    ///         var notional = BinaryPrimitives.ReadInt64LittleEndian(memory[(e + 12)..]);
    ///         BinaryPrimitives.WriteInt64LittleEndian(memory[(e + 20)..], size);
    ///         BinaryPrimitives.WriteInt64LittleEndian(memory[(e + 28)..], (notional * 200 + size) / (2 * size));
    ///     }
    /// }
    /// ]]></code>
    /// </remarks>
    private const string VwapWat =
        """
        (module
          (memory (export "memory") 1)
          (func (export "buffer") (param $size i32) (result i32)
            (local $short i32)
            (local.set $short (i32.sub (i32.shr_u (i32.add (local.get $size) (i32.const 65535)) (i32.const 16)) (memory.size)))
            (if (i32.gt_s (local.get $short) (i32.const 0))
              (then (if (i32.eq (memory.grow (local.get $short)) (i32.const -1)) (then unreachable))))
            (i32.const 0))
          (func (export "fold") (param $at i32) (param $rowCount i32) (param $rowSize i32) (param $deltaCount i32) (param $deltaSize i32)
            (local $d i32) (local $end i32) (local $row i32) (local $size i64)
            (if (i32.or (i32.ne (local.get $rowSize) (i32.const 16)) (i32.ne (local.get $deltaSize) (i32.const 16))) (then unreachable))
            (local.set $d (i32.add (local.get $at) (i32.mul (local.get $rowCount) (i32.const 20))))
            (local.set $end (i32.add (local.get $d) (i32.mul (local.get $deltaCount) (i32.const 20))))
            (block $done
              (loop $next
                (br_if $done (i32.ge_u (local.get $d) (local.get $end)))
                (local.set $row (i32.add (local.get $at) (i32.mul (i32.load align=1 (local.get $d)) (i32.const 20))))
                (local.set $size (i64.add (i64.load offset=4 align=1 (local.get $row)) (i64.load offset=4 align=1 (local.get $d))))
                (i64.store offset=4 align=1 (local.get $row) (local.get $size))
                (i64.store offset=12 align=1 (local.get $row)
                  (i64.add (i64.load offset=12 align=1 (local.get $row)) (i64.load offset=12 align=1 (local.get $d))))
                (i32.store align=1 (local.get $row) (i64.ne (local.get $size) (i64.const 0)))
                (local.set $d (i32.add (local.get $d) (i32.const 20)))
                (br $next))))
          (func (export "finalize") (param $at i32) (param $count i32) (param $stateSize i32) (param $rowSize i32)
            (local $e i32) (local $end i32) (local $size i64)
            (if (i32.or (i32.ne (local.get $stateSize) (i32.const 16)) (i32.ne (local.get $rowSize) (i32.const 16))) (then unreachable))
            (local.set $e (local.get $at))
            (local.set $end (i32.add (local.get $at) (i32.mul (local.get $count) (i32.const 36))))
            (block $done
              (loop $next
                (br_if $done (i32.ge_u (local.get $e) (local.get $end)))
                (if (i32.and (i32.load align=1 (local.get $e)) (i32.const 1))
                  (then
                    (local.set $size (i64.load offset=4 align=1 (local.get $e)))
                    (i64.store offset=20 align=1 (local.get $e) (local.get $size))
                    (i64.store offset=28 align=1 (local.get $e)
                      (i64.div_s (i64.add (i64.mul (i64.load offset=12 align=1 (local.get $e)) (i64.const 200)) (local.get $size))
                                 (i64.shl (local.get $size) (i64.const 1))))))
                (local.set $e (i32.add (local.get $e) (i32.const 36)))
                (br $next))))
        )
        """;

    /// <summary>A VWAP book's fold, in WebAssembly: its states' sizes and notionals summed (<see cref="VwapWat" />).</summary>
    public static readonly WasmBatchFold VwapFold = WasmBatchFold.FromText(VwapWat);

    /// <summary>
    ///     A VWAP book's finish, the same module: each state's row, what subscribers see — run by the router after each
    ///     fold, on the states it touched.
    /// </summary>
    public static readonly WasmBatchFinalizer VwapFinalizer = WasmBatchFinalizer.FromText(VwapWat);

    /// <summary>A VWAP book's format: the book's key; a row of its size and its VWAP, to the price's places and two more.</summary>
    public static PayloadFormat VwapFormat(PayloadFormat book)
    {
        var price = book.KeyLayout!.Fields.First(f => f.Name == "price");
        var size = book.Layout!.Fields.First(f => f.Name == "size");
        return new PayloadFormat
        {
            KeyLayout = book.KeyLayout,
            Layout = new PayloadLayout(16,
                [size with { Offset = 0 }, new PayloadField("vwap", 8, FieldKind.Int64) { Scale = price.Scale + VwapDigits }])
        };
    }

    public static Table<Level, long> Book(this Publisher publisher, string instrument)
    {
        return publisher.Table<Level, long>($"book:{instrument}");
    }

    /// <summary>
    ///     The same book seen through the prints the depth feed has yet to catch up with (see <see cref="BookBuilder" />
    ///     ).
    /// </summary>
    public static Table<Level, long> AcceleratedBook(this Publisher publisher, string instrument)
        => publisher.Table<Level, long>($"book:{instrument}:accelerated");

    public static LastValues<string> Marks(this Publisher publisher)
        => publisher.LastValues<string>("mark");

    /// <summary>
    ///     The feed's alerts: a message topic — each subscriber is sent every one, in order; a new one gets the last 16
    ///     (replayed).
    /// </summary>
    public static MessageTopic Alerts(this Publisher publisher)
        => publisher.Messages("alerts", 16, 16 * 1024);
}

/// <summary>
///     The BTC-PERP books' ingests (a table has one): the feed posts to them, the page shows how the router keeps up. The
///     accelerated book is folded by the same module: its posts are size changes too.
/// </summary>
public sealed class BookFeed(Publisher publisher)
{
    public TableIngest<Level, long, long> Ingest { get; } =
        publisher.Book("BTC-PERP").Ingest<long>(Market.BookFold);

    public TableIngest<Level, long, long> Accelerated { get; } =
        publisher.AcceleratedBook("BTC-PERP").Ingest<long>(Market.BookFold);
}