namespace BlazorRealtime.Sample.Waveform;

/// <summary>
///     The ridge grid's coder — near-lossless prediction and an adaptive binary range coder (LZMA's) — decoded by
///     waveform.js
///     (<c>decodeGrid</c>), bit for bit. Each height is predicted from its reconstructed left, upper and upper-left
///     neighbours (a + b − c); the prediction's error is quantized to steps of <c>step</c> (3: every height within ±1 of
///     the field's; 5: ±2) — predicting from what the browser will reconstruct, so errors don't add up — and coded as
///     bits:
///     zero?, sign, magnitude in unary (an 8-bit escape past 16), each under an adaptive probability chosen by how busy
///     the
///     neighbourhood is: six contexts, from the flat edges to steep peaks. The full grid takes ~1 KB, where deflating its
///     exact residuals took ~2.3 KB. Every frame starts from even odds: each decodes alone, as a last value must.
/// </summary>
internal static class RidgeCodec
{
    private const int
        Contexts = 6, Unary = 16, Stride = 2 + Unary, Shift = 5; // per context: zero, sign, Unary magnitude bits

    /// <summary>
    ///     Codes <paramref name="grid" /> (<paramref name="lines" /> × <paramref name="points" /> heights) into
    ///     <paramref name="dst" /> (four times the grid's size, plus 16: far more than it takes); returns the bytes written.
    /// </summary>
    /// <param name="reconstructed">Scratch, at least the grid's size: what the browser will decode.</param>
    public static int Encode(ReadOnlySpan<byte> grid, int lines, int points, int step, Span<byte> dst,
        Span<int> reconstructed)
    {
        Span<ushort> probs = stackalloc ushort[Contexts * Stride];
        probs.Fill(1024);
        var rc = new RangeEncoder(dst);
        var rec = reconstructed;
        int half = step / 2, n = points;
        for (var i = 0; i < lines; i++)
        {
            var o = i * n;
            for (var s = 0; s < n; s++)
            {
                int a = s > 0 ? rec[o + s - 1] : 0,
                    b = i > 0 ? rec[o - n + s] : 0,
                    c = i > 0 && s > 0 ? rec[o - n + s - 1] : 0;
                var d = i > 0 && s + 1 < n ? rec[o - n + s + 1] : b;
                var busy = Math.Abs(a - c) + Math.Abs(b - c) + Math.Abs(d - b);
                var p = Stride * (busy == 0 ? 0 : busy < 3 ? 1 : busy < 7 ? 2 : busy < 15 ? 3 : busy < 30 ? 4 : 5);
                int predicted = a + b - c, error = grid[o + s] - predicted;
                var q = error >= 0 ? (error + half) / step : -((-error + half) / step);
                rec[o + s] = Math.Clamp(predicted + q * step, 0, 255);
                rc.Bit(ref probs[p], q != 0 ? 1 : 0);
                if (q == 0) continue;
                rc.Bit(ref probs[p + 1], q < 0 ? 1 : 0);
                int magnitude = Math.Abs(q) - 1, k = 0;
                for (; k < Unary && k < magnitude; k++) rc.Bit(ref probs[p + 2 + k], 1);
                if (k < Unary) rc.Bit(ref probs[p + 2 + k], 0);
                else
                    for (var bit = 7; bit >= 0; bit--)
                        rc.Direct(((magnitude - Unary) >> bit) & 1); // |q| ≤ 85
            }
        }

        return rc.Flush();
    }

    /// <summary>LZMA's range encoder: 11-bit probabilities, adapting by 1/32 of the gap each bit.</summary>
    private ref struct RangeEncoder(Span<byte> dst)
    {
        private readonly Span<byte> _dst = dst;
        private ulong _low;
        private uint _range = 0xFFFFFFFF;
        private byte _cache;
        private long _cacheSize = 1;
        private int _pos;

        public void Bit(ref ushort p, int bit)
        {
            var bound = (_range >> 11) * p;
            if (bit == 0)
            {
                _range = bound;
                p += (ushort)((2048 - p) >> Shift);
            }
            else
            {
                _low += bound;
                _range -= bound;
                p -= (ushort)(p >> Shift);
            }

            while (_range < 1u << 24)
            {
                _range <<= 8;
                ShiftLow();
            }
        }

        /// <summary>A bit at even odds, not adapted.</summary>
        public void Direct(int bit)
        {
            var bound = (_range >> 11) * 1024;
            if (bit == 0)
            {
                _range = bound;
            }
            else
            {
                _low += bound;
                _range -= bound;
            }

            while (_range < 1u << 24)
            {
                _range <<= 8;
                ShiftLow();
            }
        }

        public int Flush()
        {
            for (var i = 0; i < 5; i++) ShiftLow();
            return _pos;
        }

        private void ShiftLow()
        {
            if ((uint)_low < 0xFF000000u || _low >> 32 != 0)
            {
                byte carry = (byte)(_low >> 32), temp = _cache;
                do
                {
                    _dst[_pos++] = (byte)(temp + carry);
                    temp = 0xFF;
                } while (--_cacheSize != 0);

                _cache = (byte)((uint)_low >> 24);
            }

            _cacheSize++;
            _low = (uint)_low << 8;
        }
    }
}