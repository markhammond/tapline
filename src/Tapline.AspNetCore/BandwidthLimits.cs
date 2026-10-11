using System.Globalization;

namespace Tapline.AspNetCore;

/// <summary>
///     A client's Streaming cap, written as for rsync's <c>--bwlimit</c> — by a page's address, or a transport's
///     request.
/// </summary>
public static class BandwidthLimits
{
    /// <summary>
    ///     Bytes per second from a rate written as for rsync's <c>--bwlimit</c>: a number, possibly fractional, in KiB/s
    ///     unless suffixed — <c>b</c> for bytes, <c>k</c>/<c>m</c>/<c>g</c>/<c>t</c>/<c>p</c> (or <c>kib</c>, <c>mib</c>…) for
    ///     powers of 1024, <c>kb</c>/<c>mb</c>/<c>gb</c>… for powers of 1000, in either case, optionally followed by <c>+1</c>
    ///     or
    ///     <c>-1</c> (a byte more or less). Null if malformed, <c>0</c> (rsync: no limit), under rsync's minimum of 512 B/s,
    ///     or
    ///     over 1 TB/s.
    /// </summary>
    public static long? Parse(string? value)
    {
        var s = value.AsSpan().Trim();
        var i = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        if (i < s.Length && s[i] == '.')
            for (i++; i < s.Length && char.IsAsciiDigit(s[i]); i++)
            {
            }

        if (!double.TryParse(s[..i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
            return null;

        var power = i < s.Length && s[i] is not ('+' or '-')
            ? "bkmgtp".IndexOf(char.ToLowerInvariant(s[i++]))
            : 1; // default: K
        if (power < 0) return null;
        double unit = 1024;
        if (i < s.Length && s[i] is 'b' or 'B')
        {
            unit = 1000;
            i++;
        }
        else if (s[i..].StartsWith("ib", StringComparison.OrdinalIgnoreCase))
        {
            i += 2;
        }

        var bytes = n * Math.Pow(unit, power);
        if (i < s.Length)
        {
            if (s.Length - i != 2 || s[i] is not ('+' or '-') || s[i + 1] != '1') return null;
            bytes += s[i] == '+' ? 1 : -1;
        }

        return bytes is >= 512 and <= 1e12 ? (long)bytes : null;
    }
}