namespace PowerConsumption.Data;

/// <summary>
/// Danish wall-clock time, whatever zone the machine is in. Days, months and years are
/// counted the way the electricity bill counts them. The database stores UTC.
/// </summary>
public static class DanishTime
{
    public static readonly TimeZoneInfo Zone = FindZone();

    /// <summary>Copenhagen's zone by its IANA id or its Windows id; the machine's own zone as a last resort.</summary>
    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Europe/Copenhagen", "Romance Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>The Danish wall-clock time of a UTC instant.</summary>
    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>Midnight at the start of a Danish calendar day, as a UTC instant.</summary>
    public static DateTime StartOfDayUtc(DateOnly day)
    {
        // Denmark changes the clocks at 02:00/03:00, never at midnight, so local midnight always
        // exists and is unambiguous, and the offset at midnight is the right one.
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(local - Zone.GetUtcOffset(local), DateTimeKind.Utc);
    }

    /// <summary>The number of hours in a run of Danish days: 23 or 25 on the days the clocks change.</summary>
    public static int HoursBetween(DateOnly from, DateOnly toExclusive) =>
        (int)Math.Round((StartOfDayUtc(toExclusive) - StartOfDayUtc(from)).TotalHours);

    /// <summary>Today's date in Denmark.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(ToLocal(DateTime.UtcNow));
}
