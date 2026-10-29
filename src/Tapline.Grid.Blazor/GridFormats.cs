using System.Globalization;

namespace Tapline.Grid.Blazor;

/// <summary>
///     The .NET formats a LiveGrid's page writes its cells by (tapline-grid.js keeps .NET's rules): a column's format
///     checked against its value's type, a time's standard format spelt out as the pattern it stands for, the room a
///     format's text takes, and the culture as the page needs it — those of its numbers' and dates' symbols that differ
///     from the invariant culture's.
/// </summary>
internal static class GridFormats
{
    /// <summary>The most columns a grid has: tapline-grid.css registers each one's place (--tg-x0 to --tg-x64).</summary>
    public const int MostColumns = 64;

    /// <summary>The most digits a standard format asks for (F100): past it, .NET writes on and on, and so would the page.</summary>
    private const int MostPrecision = 100;

    /// <summary>The time a format is tried on: UTC, as the page shows times (its ticks, a fraction of every digit).</summary>
    private static readonly DateTime SampleTime = new DateTime(2026, 10, 27, 13, 45, 30, DateTimeKind.Utc).AddTicks(1234567);

    /// <summary>A culture's dates in the Gregorian calendar, the page's: its own, or a copy so set; null if it has none.</summary>
    public static DateTimeFormatInfo? DatesOf(CultureInfo culture)
    {
        var dates = culture.DateTimeFormat;
        if (dates.Calendar is GregorianCalendar) return dates;
        if (culture.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault() is not { } gregorian) return null;
        var copy = (DateTimeFormatInfo)dates.Clone();
        copy.Calendar = gregorian;
        return copy;
    }

    /// <summary>Why <paramref name="format" /> can't write <paramref name="sample" /> (a number's or a Guid's), or null.</summary>
    public static string? Refusal(IFormattable sample, string format, IFormatProvider culture)
    {
        if (Precision(format) > MostPrecision)
            return $"Format \"{format}\" asks for {Precision(format)} digits: at most {MostPrecision}.";
        try
        {
            _ = sample.ToString(format, culture);
            return null;
        }
        catch (FormatException e)
        {
            return $"Format \"{format}\" can't write a {sample.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>A time's format as the page applies it (null: <c>G</c>): its pattern, and whether in the invariant culture.</summary>
    public static (string? Pattern, bool Invariant, string? Refusal) Expand(string? format, DateTimeFormatInfo dates)
    {
        try
        {
            _ = SampleTime.ToString(format, dates);
        }
        catch (FormatException e)
        {
            return (null, false, $"Format \"{format}\" can't write a time: {e.Message}");
        }

        if (string.IsNullOrEmpty(format)) return (dates.ShortDatePattern + " " + dates.LongTimePattern, false, null);
        if (format.Length != 1) return (format, false, null);
        return format[0] switch
        {
            'd' => (dates.ShortDatePattern, false, null),
            'D' => (dates.LongDatePattern, false, null),
            'f' => (dates.LongDatePattern + " " + dates.ShortTimePattern, false, null),
            'F' or 'U' => (dates.FullDateTimePattern, false, null), // (U: the time as UTC, as every time here is)
            'g' => (dates.ShortDatePattern + " " + dates.ShortTimePattern, false, null),
            'G' => (dates.ShortDatePattern + " " + dates.LongTimePattern, false, null),
            'm' or 'M' => (dates.MonthDayPattern, false, null),
            't' => (dates.ShortTimePattern, false, null),
            'T' => (dates.LongTimePattern, false, null),
            'y' or 'Y' => (dates.YearMonthPattern, false, null),
            'o' or 'O' => ("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffffK", true, null),
            'r' or 'R' => ("ddd, dd MMM yyyy HH':'mm':'ss 'GMT'", true, null),
            's' => ("yyyy'-'MM'-'dd'T'HH':'mm':'ss", true, null),
            'u' => ("yyyy'-'MM'-'dd HH':'mm':'ss'Z'", true, null),
            _ => (format, false, null) // (a single character that's no standard format: one of a pattern's, %-less — refused above)
        };
    }

    /// <summary>The digits a standard numeric format asks for (N2: 2), or 0 for a custom one.</summary>
    private static int Precision(string format)
    {
        if (format.Length < 2 || !char.IsAsciiLetter(format[0])) return 0;
        for (var i = 1; i < format.Length; i++)
            if (!char.IsAsciiDigit(format[i])) return 0;
        return format.Length > 10 ? int.MaxValue : int.Parse(format.AsSpan(1), CultureInfo.InvariantCulture);
    }

    /// <summary>How many characters a column's format writes for a wide value of its kind (its estimated width).</summary>
    public static int Room(GridColumn column, Type type, CultureInfo culture, DateTimeFormatInfo? dates,
        IReadOnlyDictionary<string, string>? names)
    {
        var format = column.Format;
        if (column.Figures is { } figures) // (the widest it writes over magnitudes 10^-12 to 10^20, as the page writes it)
        {
            var widest = 1;
            for (var k = -12; k <= 20; k++)
                widest = Math.Max(widest, Figured(-1.2345678912345678m * Ten(k), figures, column.MostDigits, format, culture).Length);
            return widest;
        }

        switch (column.Kind)
        {
            case "int":
                return Text(Wide(type), format, culture).Length;
            case "fixed": // (seven whole digits and every place: -1234567.99 at a scale of 2)
            {
                var scale = column.Scale ?? 0;
                var wide = decimal.Parse(scale == 0 ? "-1234567" : "-1234567." + new string('9', scale), CultureInfo.InvariantCulture);
                return Text(wide, format, culture).Length;
            }
            case "float":
                return Text(type == typeof(float) ? -1_234_567.9f : -1_234_567.891, format, culture).Length;
            case "percent":
                return Text(-0.1234, format ?? "P", culture).Length;
            case "time":
            {
                var most = 1;
                var info = column.Invariant ? DateTimeFormatInfo.InvariantInfo : dates!;
                for (var month = 1; month <= 12; month++) // (every month's name, and every day's)
                for (var day = 22; day <= 28; day++)
                    most = Math.Max(most, new DateTime(2026, month, day, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999)
                        .ToString(column.Format, info).Length);
                return most;
            }
            case "uuid":
                return Guid.Empty.ToString(format).Length;
            case "enum":
                return Math.Max(1, names?.Values.Max(n => n.Length) ?? 1);
            case "text":
                return Math.Min(column.Size - 1, 24);
            default:
                return 1; // (a bool's tick)
        }
    }

    /// <summary>
    ///     A number as the page writes it rounded to <paramref name="figures" /> (tapline-grid.js, figuredText): by its
    ///     format, or in scientific notation if it would show more than <paramref name="most" /> digits.
    /// </summary>
    internal static string Figured(decimal value, int figures, int? most, string? format, CultureInfo culture)
    {
        if (value == 0) return value.ToString(format, culture);
        var unit = Ten((int)Math.Floor(Math.Log10((double)Math.Abs(value))) - figures + 1);
        var rounded = Math.Round(value / unit, MidpointRounding.AwayFromZero) * unit;
        var place = (int)Math.Floor(Math.Log10((double)Math.Abs(rounded))) + 1;
        return most is { } m && Math.Max(place, 0) + Math.Max(0, figures - place) > m
            ? rounded.ToString(figures > 1 ? "0." + new string('#', figures - 1) + "e0" : "0e0", culture)
            : rounded.ToString(format, culture);
    }

    private static decimal Ten(int power)
    {
        var ten = 1m;
        for (var i = 0; i < Math.Abs(power); i++) ten *= 10;
        return power < 0 ? 1m / ten : ten;
    }

    private static string Text(IFormattable value, string? format, CultureInfo culture)
    {
        return value.ToString(format, culture);
    }

    /// <summary>A wide value of an integer type: its least, or seven digits, as it holds.</summary>
    private static IFormattable Wide(Type type)
    {
        if (type == typeof(sbyte)) return sbyte.MinValue;
        if (type == typeof(byte)) return byte.MaxValue;
        if (type == typeof(short)) return short.MinValue;
        if (type == typeof(ushort)) return ushort.MaxValue;
        if (type == typeof(int)) return -1_234_567;
        if (type == typeof(uint)) return 1_234_567u;
        if (type == typeof(ulong)) return 1_234_567ul;
        return -1_234_567L;
    }

    /// <summary>
    ///     The culture as the page writes cells in it (tapline-grid.js, its option <c>culture</c>): <c>n</c>, its numbers'
    ///     symbols, and <c>d</c>, its dates' — of each, only what differs from the invariant culture's, and a date's
    ///     names only if <paramref name="patterns" /> write them. Null if nothing differs.
    /// </summary>
    public static Dictionary<string, object>? PageCulture(CultureInfo culture, DateTimeFormatInfo? dates, IEnumerable<string> patterns)
    {
        var page = new Dictionary<string, object>();
        if (Numbers(culture.NumberFormat) is { Count: > 0 } numbers) page["n"] = numbers;
        if (dates is not null && Dates(dates, Uses(patterns)) is { Count: > 0 } d) page["d"] = d;
        return page.Count > 0 ? page : null;
    }

    private static Dictionary<string, object> Numbers(NumberFormatInfo n)
    {
        var i = NumberFormatInfo.InvariantInfo;
        var page = new Dictionary<string, object>();
        void Text(string key, string value, string invariant)
        {
            if (value != invariant) page[key] = value;
        }

        void Number(string key, int value, int invariant)
        {
            if (value != invariant) page[key] = value;
        }

        void Sizes(string key, int[] value, int[] invariant)
        {
            if (!value.SequenceEqual(invariant)) page[key] = value;
        }

        Text("neg", n.NegativeSign, i.NegativeSign);
        Text("pos", n.PositiveSign, i.PositiveSign);
        Text("dec", n.NumberDecimalSeparator, i.NumberDecimalSeparator);
        Text("grp", n.NumberGroupSeparator, i.NumberGroupSeparator);
        Sizes("sizes", n.NumberGroupSizes, i.NumberGroupSizes);
        Number("digits", n.NumberDecimalDigits, i.NumberDecimalDigits);
        Number("nneg", n.NumberNegativePattern, i.NumberNegativePattern);
        Text("pdec", n.PercentDecimalSeparator, i.PercentDecimalSeparator);
        Text("pgrp", n.PercentGroupSeparator, i.PercentGroupSeparator);
        Sizes("psizes", n.PercentGroupSizes, i.PercentGroupSizes);
        Number("pdigits", n.PercentDecimalDigits, i.PercentDecimalDigits);
        Number("ppos", n.PercentPositivePattern, i.PercentPositivePattern);
        Number("pneg", n.PercentNegativePattern, i.PercentNegativePattern);
        Text("pct", n.PercentSymbol, i.PercentSymbol);
        Text("pml", n.PerMilleSymbol, i.PerMilleSymbol);
        Text("cur", n.CurrencySymbol, i.CurrencySymbol);
        Text("cdec", n.CurrencyDecimalSeparator, i.CurrencyDecimalSeparator);
        Text("cgrp", n.CurrencyGroupSeparator, i.CurrencyGroupSeparator);
        Sizes("csizes", n.CurrencyGroupSizes, i.CurrencyGroupSizes);
        Number("cdigits", n.CurrencyDecimalDigits, i.CurrencyDecimalDigits);
        Number("cpos", n.CurrencyPositivePattern, i.CurrencyPositivePattern);
        Number("cneg", n.CurrencyNegativePattern, i.CurrencyNegativePattern);
        Text("nan", n.NaNSymbol, i.NaNSymbol);
        Text("inf", n.PositiveInfinitySymbol, i.PositiveInfinitySymbol);
        Text("ninf", n.NegativeInfinitySymbol, i.NegativeInfinitySymbol);
        return page;
    }

    [Flags]
    private enum Names
    {
        None = 0,
        Months = 1, // MMM, MMMM
        Days = 2, // ddd, dddd
        Era = 4 // g
    }

    private static Dictionary<string, object> Dates(DateTimeFormatInfo d, Names names)
    {
        var i = DateTimeFormatInfo.InvariantInfo;
        var page = new Dictionary<string, object>();
        if (d.AMDesignator != i.AMDesignator) page["am"] = d.AMDesignator;
        if (d.PMDesignator != i.PMDesignator) page["pm"] = d.PMDesignator;
        if (d.DateSeparator != i.DateSeparator) page["ds"] = d.DateSeparator;
        if (d.TimeSeparator != i.TimeSeparator) page["ts"] = d.TimeSeparator;
        if (names.HasFlag(Names.Era) && d.GetEraName(1) != i.GetEraName(1)) page["era"] = d.GetEraName(1);
        if (names.HasFlag(Names.Months))
        {
            string[] months = d.MonthNames[..12], abbreviated = d.AbbreviatedMonthNames[..12];
            if (!months.SequenceEqual(i.MonthNames[..12])) page["months"] = months;
            if (!abbreviated.SequenceEqual(i.AbbreviatedMonthNames[..12])) page["mabbr"] = abbreviated;
            string[] genitive = d.MonthGenitiveNames[..12], abbreviatedGenitive = d.AbbreviatedMonthGenitiveNames[..12];
            if (!genitive.SequenceEqual(months) || !abbreviatedGenitive.SequenceEqual(abbreviated))
            {
                page["mgen"] = genitive; // (the page's word to use them by .NET's rule: a day of the month beside the month)
                page["mgabbr"] = abbreviatedGenitive;
            }
        }

        if (names.HasFlag(Names.Days))
        {
            if (!d.DayNames.SequenceEqual(i.DayNames)) page["days"] = d.DayNames;
            if (!d.AbbreviatedDayNames.SequenceEqual(i.AbbreviatedDayNames)) page["dabbr"] = d.AbbreviatedDayNames;
        }

        return page;
    }

    /// <summary>The names the patterns write: months' (MMM), days' (ddd), the era's (g) — read as .NET reads a pattern.</summary>
    private static Names Uses(IEnumerable<string> patterns)
    {
        var names = Names.None;
        foreach (var pattern in patterns)
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c is '\'' or '"') // (a quoted literal, its backslashes escapes)
                {
                    for (i++; i < pattern.Length && pattern[i] != c; i++)
                        if (pattern[i] == '\\')
                            i++;
                    continue;
                }

                if (c == '\\')
                {
                    i++;
                    continue;
                }

                var run = 1;
                while (i + run < pattern.Length && pattern[i + run] == c) run++;
                if (c == 'M' && run >= 3) names |= Names.Months;
                else if (c == 'd' && run >= 3) names |= Names.Days;
                else if (c == 'g') names |= Names.Era;
                i += run - 1;
            }

        return names;
    }
}
