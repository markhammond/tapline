namespace LiveGrid.Sample;

/// <summary>Times as the rows hold them: microseconds since 1970, UTC.</summary>
public static class Times
{
    public static DateTime Of(long micros)
    {
        return new DateTime(DateTime.UnixEpoch.Ticks + micros * 10, DateTimeKind.Utc);
    }

    public static long Micros(DateTime time)
    {
        return (time.Ticks - DateTime.UnixEpoch.Ticks) / 10;
    }
}
