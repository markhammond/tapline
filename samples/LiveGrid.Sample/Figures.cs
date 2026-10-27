using System.Globalization;

namespace LiveGrid.Sample;

/// <summary>Figures as the status bar shows them, as the ChalkQL blotter's does: to two significant figures, again only once moved.</summary>
internal static class Figures
{
    /// <summary>A figure to two significant figures; under a hundredth, nought.</summary>
    public static string Two(double value)
    {
        if (value < 0.01) return "0";
        var step = Math.Pow(10, Math.Floor(Math.Log10(value)) - 1);
        var rounded = Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
        return rounded.ToString("F" + Math.Max(0, 1 - (int)Math.Floor(Math.Log10(rounded))), CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a figure shown should show again: it reads differently, and moved by 5 % or more.</summary>
    public static bool Moved(double shown, double now)
    {
        return Two(shown) != Two(now) && (shown == 0 || Math.Abs(now - shown) >= 0.05 * Math.Abs(shown));
    }
}
