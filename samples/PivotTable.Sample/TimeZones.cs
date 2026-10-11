namespace PivotTable.Sample;

/// <summary>
///     A time zone's offset from UTC over a span (time_zones: zone, starts, ends, utc_offset_minutes, local_starts,
///     previous_offset_minutes): from <c>starts</c> (inclusive) to <c>ends</c> (exclusive), UTC — the application's own,
///     not
///     the operating system's; its start in local time, and the offset before it (its own, for a zone's first span). A
///     local
///     time before <c>starts</c> at the previous offset, and at or after <c>local_starts</c>, happens twice: clocks went
///     back.
/// </summary>
public sealed record ZoneOffset(
    string Zone,
    DateTime Starts,
    DateTime Ends,
    int UtcOffsetMinutes,
    DateTime LocalStarts,
    int PreviousOffsetMinutes);

/// <summary>
///     The application's time zones: each a standard offset and, where it has one, a daylight-saving rule — written here,
///     so
///     what a zone's clock says is the same on every machine, whatever its own time zone database holds. Materialized as
///     spans of one offset (<see cref="Offsets" />), 2020 to 2040: the <c>time_zones</c> table, what a statement's days
///     are
///     computed from.
/// </summary>
public static class TimeZones
{
    // United States (since 2007): the second Sunday of March, 2:00, to the first Sunday of November, 2:00 (daylight time).
    private static readonly Rule Us = new(3, 2, TimeSpan.FromHours(2), 11, 1, TimeSpan.FromHours(1), false);

    // European Union and the UK (since 1996): the last Sunday of March to the last Sunday of October, 1:00 UTC both.
    private static readonly Rule Eu = new(3, -1, TimeSpan.FromHours(1), 10, -1, TimeSpan.FromHours(1), true);

    // New South Wales (since 2008): the first Sunday of October, 2:00, to the first Sunday of April, 3:00 (daylight time).
    private static readonly Rule Nsw = new(10, 1, TimeSpan.FromHours(2), 4, 1, TimeSpan.FromHours(2), false);

    /// <summary>
    ///     The zones, by IANA name: their standard offset (minutes east of UTC) and daylight-saving rule (an hour, if
    ///     any).
    /// </summary>
    private static readonly (string Zone, int Standard, Rule? Daylight)[] Zones =
    [
        ("UTC", 0, null),
        ("America/New_York", -300, Us),
        ("America/Chicago", -360, Us),
        ("Europe/London", 0, Eu),
        ("Europe/Zurich", 60, Eu),
        ("Asia/Hong_Kong", 480, null),
        ("Asia/Singapore", 480, null),
        ("Asia/Tokyo", 540, null),
        ("Australia/Sydney", 600, Nsw)
    ];

    public static readonly string[] Names = [.. Zones.Select(z => z.Zone)];

    private static readonly DateTime First = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Last = new(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Every zone's offsets, as spans (zone, then time): the <c>time_zones</c> table.</summary>
    public static readonly ZoneOffset[] Offsets =
        [.. Zones.SelectMany(Spans).OrderBy(o => o.Zone, StringComparer.Ordinal).ThenBy(o => o.Starts)];

    private static readonly Dictionary<string, ZoneOffset[]> ByZone =
        Offsets.GroupBy(o => o.Zone).ToDictionary(g => g.Key, g => g.ToArray());

    private static IEnumerable<ZoneOffset> Spans((string Zone, int Standard, Rule? Daylight) zone)
    {
        if (zone.Daylight is not { } rule)
        {
            yield return Span(zone.Zone, First, Last, zone.Standard, zone.Standard);
            yield break;
        }

        // The year's changes, in UTC: daylight time starts (standard time to then), and ends (daylight time to then).
        var changes = new List<(DateTime At, int Offset)>();
        for (var year = First.Year - 1; year <= Last.Year; year++)
        {
            changes.Add((Utc(year, rule.StartMonth, rule.StartSunday, rule.StartAt, rule.AtUtc ? 0 : zone.Standard),
                zone.Standard + 60));
            changes.Add((Utc(year, rule.EndMonth, rule.EndSunday, rule.EndAt, rule.AtUtc ? 0 : zone.Standard),
                zone.Standard));
        }

        changes.Sort((a, b) => a.At.CompareTo(b.At));
        for (var i = 0; i + 1 < changes.Count; i++)
        {
            var (from, offset) = changes[i];
            var to = changes[i + 1].At;
            if (to <= First || from >= Last) continue;
            var ends = to > Last ? Last : to;
            yield return Span(zone.Zone, from < First ? First : from, ends, offset,
                from < First || i == 0 ? offset : changes[i - 1].Offset);
        }
    }

    private static ZoneOffset Span(string zone, DateTime starts, DateTime ends, int offset, int previous)
    {
        return new ZoneOffset(zone, starts, ends, offset,
            DateTime.SpecifyKind(starts.AddMinutes(offset), DateTimeKind.Unspecified), previous);
    }

    /// <summary>The n-th Sunday (−1: the last) of a month, at a local time with a standard offset (0: UTC) — as UTC.</summary>
    private static DateTime Utc(int year, int month, int sunday, TimeSpan at, int standard)
    {
        DateTime day;
        if (sunday > 0)
        {
            day = new DateTime(year, month, 1);
            while (day.DayOfWeek != DayOfWeek.Sunday) day = day.AddDays(1);
            day = day.AddDays(7 * (sunday - 1));
        }
        else
        {
            day = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            while (day.DayOfWeek != DayOfWeek.Sunday) day = day.AddDays(-1);
        }

        return DateTime.SpecifyKind(day + at - TimeSpan.FromMinutes(standard), DateTimeKind.Utc);
    }

    /// <summary>A zone's offset (minutes east of UTC) at a UTC instant.</summary>
    public static int OffsetAt(string zone, DateTime utc)
    {
        var spans = ByZone[zone];
        int lo = 0, hi = spans.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (spans[mid].Starts <= utc) lo = mid;
            else hi = mid - 1;
        }

        return spans[lo].UtcOffsetMinutes;
    }

    /// <summary>
    ///     A zone's local time as UTC — in a gap (clocks going forward), as if they hadn't yet; in an overlap (clocks going
    ///     back), the first. The span whose local start is the latest at or before it: its offset — or the previous span's,
    ///     while the previous span's local time hasn't yet run out (an overlap). In a gap that span is the one before the
    ///     change, its offset the one meant. As <c>trading_days</c> reckons it, in SQL.
    /// </summary>
    public static DateTime ToUtc(string zone, DateTime local)
    {
        var spans = ByZone[zone];
        int lo = 0, hi = spans.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (spans[mid].LocalStarts <= local) lo = mid;
            else hi = mid - 1;
        }

        var span = spans[lo];
        var offset = local < span.Starts.AddMinutes(span.PreviousOffsetMinutes)
            ? span.PreviousOffsetMinutes
            : span.UtcOffsetMinutes;
        return DateTime.SpecifyKind(local.AddMinutes(-offset), DateTimeKind.Utc);
    }

    /// <summary>A UTC instant as a zone's local time.</summary>
    public static DateTime ToLocal(string zone, DateTime utc)
    {
        return DateTime.SpecifyKind(utc.AddMinutes(OffsetAt(zone, utc)), DateTimeKind.Unspecified);
    }

    /// <summary>
    ///     A daylight-saving rule: the n-th Sunday (or the last: n = −1) of a month, at a time — local standard time, or
    ///     UTC.
    /// </summary>
    private sealed record Rule(
        int StartMonth,
        int StartSunday,
        TimeSpan StartAt,
        int EndMonth,
        int EndSunday,
        TimeSpan EndAt,
        bool AtUtc);
}