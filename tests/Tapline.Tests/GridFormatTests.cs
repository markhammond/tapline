using System.Globalization;
using Tapline.Grid;

/// <summary>
///     The formats a LiveGrid's page writes cells by: checked against their values' types on the server (refused with the
///     reason), a time's standard format spelt out, the room a format's text takes, the culture as the page gets it — and
///     .NET's own output for the JS tests to hold the page to (fixtures/formats.json): numbers of every kind, times and
///     Guids, by standard and custom formats, in cultures that differ in every way the page reads.
/// </summary>
internal static partial class T
{
    /// <summary>Cultures that differ in what the page reads: separators, groups, patterns, signs, names (genitive too), calendars.</summary>
    private static readonly string[] FormatCultures =
    [
        "", "en-US", "en-GB", "de-DE", "de-CH", "fr-FR", "fr-CH", "en-IN", "ja-JP", "zh-CN", "ko-KR", "ru-RU", "pl-PL", "cs-CZ",
        "el-GR", "sv-SE", "fi-FI", "nl-NL", "it-IT", "es-ES", "pt-BR", "tr-TR", "he-IL", "ar-SA", "th-TH", "fa-IR", "hi-IN"
    ];

    private static readonly string[] StandardNumberLetters = ["C", "c", "D", "d", "E", "e", "F", "f", "G", "g", "N", "n", "P", "p", "R", "r", "X", "x", "B", "b"];

    private static readonly string[] CustomNumberFormats =
    [
        "0", "#", "0.00", "#.##", ".##", "0.0#", "00000.00", "#,##0.00", "#,##0.###", "#,###.####;-#,###.####;–", "#,#", "#,", "0,,",
        "0,,.00", "#,##0,K", "0,.0", "0.0,", "0,0", ",0", "0,0.0,0", "0%", "0.##%", "#,##0.0%;-#,##0.0%;–", "%0", "0‰",
        "0.0E+00", "0.###E-0", "##0.0e0", "#E+0", "0.00E+000", "0E", "0E+", "E0", "e+0", "0.0#E+0", "'$'#,##0.00", "\\#0",
        "0;(0);'zero'", ";;", "0;", ";0", "0;;", "0;0;0;0", "#0.0#;(#0.0#);-", "+0;-0;0", "\"q\"0", "0\\'", "00.00.00",
        "#,##0.00;;'nought'", "0.0000000000000000000", "#,##0.0000000000", "- 0", "[0]", "#,##0.00 'units'", "0.0;-0.0;0.0"
    ];

    private static readonly int[] Precisions = [-1, 0, 2, 5, 17];

    private static readonly string[] DatePatterns =
    [
        "yyyy-MM-dd HH:mm:ss.fffffff", "dd MMMM yyyy", "d MMMM", "MMMM yyyy", "MMM d, yyyy", "dddd, d MMMM", "ddd dd MMM", "d. MMMM yyyy",
        "MMMM", "MMM", "dd MMM", "h:mm tt", "h:mm t", "hh:mm:ss.FFF", "HH:mm:ss.FFFFFFF", "ss.F", "'… 'HH:mm:ss", "%d", "%M", "%y", "yy",
        "yyy", "yyyyy", "g", "gg yyyy", "%z", "zz", "zzz", "%K", "K HH", "HH\\:mm", "\"literal\" HH", "'it\\'s' H", "MM/dd/yyyy", "dd.MM.yyyy",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss.fffffffK", "MM", "dd", "ddd", "dddd", "fff", "FFF", "hh", "HH:mm", "s", "tt", "h tt", "%h", "yyyy/M/d"
    ];

    private const string StandardDateLetters = "dDfFgGmMtTyYoOrRsuU";

    /// <summary>
    ///     A culture as the page gets it: only what differs from the invariant culture's (none for the invariant culture),
    ///     a date's names only where the patterns write them; a time's standard format spelt out (in the invariant
    ///     culture's names for o, r, s and u); a culture of another calendar, in its Gregorian one.
    /// </summary>
    public static Task GridFormatsCulture()
    {
        var invariant = CultureInfo.InvariantCulture;
        Assert(GridFormats.PageCulture(invariant, invariant.DateTimeFormat, ["dddd, dd MMMM yyyy"]) is null, "the invariant culture: nothing to send");
        var us = CultureInfo.GetCultureInfo("en-US");
        var usPage = GridFormats.PageCulture(us, us.DateTimeFormat, ["dddd, MMMM d, yyyy"])!;
        var usNumbers = (Dictionary<string, object>)usPage["n"];
        Assert(usNumbers["cur"] as string == "$" && !usNumbers.ContainsKey("dec") && !usNumbers.ContainsKey("grp") && !usPage.ContainsKey("d"),
            $"en-US: its currency symbol (and what else differs), not its separators; its names English, as the invariant culture's: {string.Join(", ", usNumbers.Keys)}");
        var de = CultureInfo.GetCultureInfo("de-DE");
        var deDates = (Dictionary<string, object>)GridFormats.PageCulture(de, de.DateTimeFormat, ["dd.MM.yyyy HH:mm"])!["d"];
        Assert(deDates["ds"] as string == "." && !deDates.ContainsKey("months") && !deDates.ContainsKey("days"),
            "de-DE, its numbers only: its date separator, no names");
        var withNames = (Dictionary<string, object>)GridFormats.PageCulture(de, de.DateTimeFormat, ["dddd, d. MMM yyyy"])!["d"];
        var quoted = (Dictionary<string, object>)GridFormats.PageCulture(de, de.DateTimeFormat, ["'MMMM dddd' HH"])!["d"];
        Assert(((string[])withNames["months"])[2] == "März" && ((string[])withNames["days"])[0] == "Sonntag" &&
               ((string[])withNames["mabbr"])[2] == "Mär" && ((string[])withNames["mgabbr"])[2] == "März" &&
               !quoted.ContainsKey("months") && !quoted.ContainsKey("days"),
            "its names, a pattern writing them (not one quoting them) — its genitive abbreviations too, which differ (März 5, Mär 2026)");
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var ruDates = (Dictionary<string, object>)GridFormats.PageCulture(ru, ru.DateTimeFormat, ["d MMMM"])!["d"];
        Assert(((string[])ruDates["mgen"])[0] != ((string[])ruDates["months"])[0], "ru-RU: its genitive months too (the page's word to use them)");

        var thai = CultureInfo.GetCultureInfo("th-TH");
        Assert(thai.DateTimeFormat.Calendar is ThaiBuddhistCalendar && GridFormats.DatesOf(thai)?.Calendar is GregorianCalendar &&
               GridFormats.DatesOf(us) == us.DateTimeFormat, "a culture of another calendar in its Gregorian one; a Gregorian culture's own");
        Assert(GridFormats.Expand(null, us.DateTimeFormat) == (us.DateTimeFormat.ShortDatePattern + " " + us.DateTimeFormat.LongTimePattern, false, null) &&
               GridFormats.Expand("d", de.DateTimeFormat) == ("dd.MM.yyyy", false, null) &&
               GridFormats.Expand("o", de.DateTimeFormat) == ("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffffK", true, null) &&
               GridFormats.Expand("R", de.DateTimeFormat).Invariant && GridFormats.Expand("HH:mm", de.DateTimeFormat) == ("HH:mm", false, null) &&
               GridFormats.Expand("%", us.DateTimeFormat).Refusal?.StartsWith("Format \"%\" can't write a time", StringComparison.Ordinal) == true,
            "a time's format spelt out: G by default, a standard one as its culture's pattern (o, r, s, u in the invariant culture's), a custom one as it is");
        return Task.CompletedTask;
    }

    /// <summary>.NET's output for the JS tests (fixtures/formats.json): the page must write each the same.</summary>
    public static object FormatsFixture()
    {
        var rnd = new Random(31);
        var numbers = new List<object?[]>();
        var times = new List<object?[]>();
        var cultures = new Dictionary<string, object?>();
        var invariant = CultureInfo.InvariantCulture;

        foreach (var name in FormatCultures)
        {
            var culture = CultureInfo.GetCultureInfo(name);
            var dates = GridFormats.DatesOf(culture)!;
            var full = name is "" or "en-US";       // (every format in two; the rest, those that read the culture)
            var formats = new List<string?> { null };
            if (full)
                foreach (var letter in StandardNumberLetters)
                foreach (var precision in Precisions)
                    formats.Add(precision < 0 ? letter : letter + precision.ToString(invariant));
            formats.AddRange(full
                ? CustomNumberFormats
                : ["C", "N", "N0", "P", "P1", "E", "F2", "#,##0.00", "#,###.####;-#,###.####;–", "0.0E+00", "0.##%", "0‰", "#,#", "- 0"]);

            void Add(string type, string value, IFormattable sample)
            {
                foreach (var format in formats)
                    try
                    {
                        numbers.Add([name, type, value, format, sample.ToString(format, culture)]);
                    }
                    catch (FormatException)
                    {
                        // (a format the type refuses: refused on the server, never sent)
                    }
            }

            foreach (var (size, unsigned, values) in IntegerSamples(rnd, full).Where(s => full || (s.Size == 4 && !s.Unsigned)))
            foreach (var value in values)
                Add($"{(unsigned ? 'u' : 'i')}{size}", value.ToString(invariant), IntegerOf(value, size, unsigned));
            foreach (var scale in full ? [0, 4, 18] : new[] { 4 })
            foreach (var mantissa in DecimalSamples(rnd, full))
                Add($"d{scale}", mantissa.ToString(invariant), DecimalOf(mantissa, scale));
            foreach (var value in DoubleSamples(rnd, full))
                Add("f8", BitConverter.DoubleToInt64Bits(value).ToString("x16"), value);
            foreach (var value in FloatSamples(rnd, full).Take(full ? int.MaxValue : 6))
                Add("f4", BitConverter.SingleToInt32Bits(value).ToString("x8"), value);

            var patterns = new List<string>();
            var letters = full ? StandardDateLetters : "dDgGTMYUr";
            foreach (var format in letters.Select(c => c.ToString()).Concat(full ? DatePatterns : ["d MMMM yyyy", "MMMM", "dddd", "h:mm tt", "g"]))
            {
                var (pattern, inInvariant, refusal) = GridFormats.Expand(format, dates);
                if (refusal is not null) throw new InvalidOperationException($"{name}: {format}: {refusal}");
                if (!inInvariant) patterns.Add(pattern!);
                foreach (var time in TimeSamples(rnd, full))
                    times.Add([name, time.Ticks.ToString(invariant), pattern, inInvariant, time.ToString(format, dates)]);
            }

            cultures[name] = GridFormats.PageCulture(culture, dates, patterns);
        }

        var guids = new List<object?[]>();
        foreach (var guid in new[] { Guid.Empty, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Guid.CreateVersion7(), Guid.NewGuid() })
        foreach (var format in new[] { null, "D", "d", "N", "n", "B", "b", "P", "p", "X", "x" })
            guids.Add([Convert.ToHexString(guid.ToByteArray()), format, guid.ToString(format)]);
        Notes.Add($"formats fixture: {numbers.Count} numbers, {times.Count} times, {guids.Count} Guids in {cultures.Count} cultures");
        return new { cultures, numbers, times, guids };
    }

    /// <summary>A long scaled by 10^scale as the decimal it stands for, its scale kept (1.2300, not 1.23), as the page reads a Fixed.</summary>
    private static decimal DecimalOf(long mantissa, int scale)
    {
        var magnitude = mantissa < 0 ? (ulong)-(mantissa + 1) + 1 : (ulong)mantissa;
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), 0, mantissa < 0, (byte)scale);
    }

    private static IEnumerable<(int Size, bool Unsigned, long[] Values)> IntegerSamples(Random rnd, bool full)
    {
        long[] Common(long least, long most)
        {
            List<long> values = [0, 1, 5, 12, 125, 999, 1000, 1234, 99_999, 123_456, 1_234_567, most, least];
            if (least < 0) values.AddRange([-1, -5, -125, -1_234_567, least + 1]);
            if (full)
                for (var i = 0; i < 6; i++)
                    values.Add(least + (long)(rnd.NextDouble() * ((double)most - least)));
            return values.Where(v => v >= least && v <= most).Distinct().ToArray();
        }

        yield return (1, false, Common(sbyte.MinValue, sbyte.MaxValue));
        yield return (1, true, Common(0, byte.MaxValue));
        yield return (2, false, Common(short.MinValue, short.MaxValue));
        yield return (2, true, Common(0, ushort.MaxValue));
        yield return (4, false, Common(int.MinValue, int.MaxValue));
        yield return (4, true, Common(0, uint.MaxValue));
        yield return (8, false, Common(long.MinValue, long.MaxValue));
        if (full) yield return (8, true, [0, 1, 1_234_567, long.MaxValue]); // (a ulong past a long's: the JS test reads the text)
    }

    private static IFormattable IntegerOf(long value, int size, bool unsigned)
    {
        return (size, unsigned) switch
        {
            (1, false) => (sbyte)value, (1, true) => (byte)value, (2, false) => (short)value, (2, true) => (ushort)value,
            (4, false) => (int)value, (4, true) => (uint)value, (8, false) => value, _ => (ulong)value
        };
    }

    private static IEnumerable<long> DecimalSamples(Random rnd, bool full)
    {
        long[] values = [0, 1, -1, 5, 125, 12_345, -12_355, 1_000_000, 9_999_999, 123_456_789_012, long.MaxValue, long.MinValue + 1];
        foreach (var v in values) yield return v;
        if (full)
            for (var i = 0; i < 8; i++)
                yield return rnd.NextInt64(-10_000_000_000, 10_000_000_000);
    }

    private static IEnumerable<double> DoubleSamples(Random rnd, bool full)
    {
        double[] values =
        [
            0, -0.0, 1, -1, 0.5, 1.5, 2.5, -2.5, 0.125, 0.375, 0.625, 1.005, 0.1, 0.2, 0.3, 1.0 / 3, 2.0 / 3, 123.456, 1234.5678, -1234.5678,
            12345.678, 65_012.3456, 104_523.5, 0.000123456, 1e15, 1e16, 1e17, 1e21, 1e22, 1e-5, 1e-4, 1e-7, 5e-324, 2.2250738585072014e-308,
            double.MaxValue, 9007199254740993, 1234567890123455, 0.15, 0.25, 0.35, 150, 250, double.NaN, double.PositiveInfinity,
            double.NegativeInfinity, Math.PI, -Math.E, 0.045, 0.0045, 99.995, 999_999.5, 0.999_999_95
        ];
        foreach (var v in values) yield return v;
        if (!full) yield break;
        for (var i = -12; i <= 64; i += 7) yield return Math.ScaleB(1, i); // (powers of two: a halved low margin)
        for (var i = 0; i < 20; i++) // (prices and quantities, to places)
            yield return Math.Round((rnd.NextDouble() - 0.3) * Math.Pow(10, rnd.Next(-3, 8)), rnd.Next(0, 7));
        for (var i = 0; i < 10; i++) // (ties: an odd number of halves, or eighths, exactly)
            yield return (rnd.Next(-2000, 2000) * 2 + 1) / (double)(1 << rnd.Next(1, 4));
        for (var i = 0; i < 15; i++) // (any double at all, of moderate size)
            yield return BitConverter.Int64BitsToDouble((long)(rnd.NextInt64() & 0x7FFF_FFFF_FFFF_FFFF) % 0x4200_0000_0000_0000 + 0x3C00_0000_0000_0000) *
                         (rnd.Next(2) == 0 ? 1 : -1);
        for (var i = 0; i < 5; i++)
            yield return BitConverter.Int64BitsToDouble(rnd.NextInt64() & 0x7FEF_FFFF_FFFF_FFFF);
    }

    private static IEnumerable<float> FloatSamples(Random rnd, bool full)
    {
        float[] values =
        [
            0, -0.0f, 1, -1, 0.5f, 2.5f, 0.1f, 1f / 3, 16_777_217, float.MaxValue, float.Epsilon, 1.17549435e-38f, 123.456f, -1234.5678f,
            65_012.35f, 1e-7f, 3.4e10f, float.NaN, float.PositiveInfinity, 0.125f, 99.995f
        ];
        foreach (var v in values) yield return v;
        if (!full) yield break;
        for (var i = -10; i <= 40; i += 5) yield return MathF.ScaleB(1, i);
        for (var i = 0; i < 15; i++)
            yield return BitConverter.Int32BitsToSingle(rnd.Next() & 0x7F7F_FFFF) * (rnd.Next(2) == 0 ? 1 : -1);
    }

    private static IEnumerable<DateTime> TimeSamples(Random rnd, bool full)
    {
        yield return new DateTime(1, 1, 2, 0, 0, 0, DateTimeKind.Utc).AddTicks(1); // (not the first day: there .NET writes a time-only z in the local offset)
        yield return DateTime.UnixEpoch;
        yield return new DateTime(2000, 2, 29, 12, 0, 0, DateTimeKind.Utc);
        yield return new DateTime(2026, 10, 27, 13, 45, 30, DateTimeKind.Utc).AddTicks(1_234_567);
        yield return new DateTime(2026, 10, 27, 0, 5, 9, DateTimeKind.Utc).AddTicks(1_200_000);
        yield return new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999);
        if (!full) yield break;
        for (var month = 1; month <= 12; month++) // (every month's name, and every day's)
            yield return new DateTime(2026, month, 15 + month % 7, (month * 5) % 24, month * 4, month, DateTimeKind.Utc).AddTicks(month * 10_000);
        for (var i = 0; i < 6; i++)
            yield return new DateTime(rnd.NextInt64(DateTime.MinValue.Ticks + 1, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
    }
}
