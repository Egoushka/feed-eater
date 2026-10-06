using System.Globalization;

namespace FeedEater.Loops;

/// <summary>Run keys for scheduled jobs. A key is a local date; a job runs once per key.</summary>
public static class Schedule
{
    public static DateTime Local(DateTimeOffset utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(utc, zone).DateTime;

    public static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The most recent day on which <paramref name="at"/> has passed: a job missed while down runs at start.</summary>
    public static string LatestDaily(DateTime local, TimeSpan at) => Key(local.TimeOfDay >= at ? local.Date : local.Date.AddDays(-1));

    /// <summary>Today's key while inside [from, until), otherwise none: a missed window is skipped, not caught up.</summary>
    public static string? Window(DateTime local, TimeSpan from, TimeSpan until) =>
        local.TimeOfDay >= from && local.TimeOfDay < until ? Key(local.Date) : null;

    public static string LatestWeekly(DateTime local, DayOfWeek day, TimeSpan at)
    {
        var back = ((int)local.DayOfWeek - (int)day + 7) % 7;
        var candidate = local.Date.AddDays(-back);
        if (back == 0 && local.TimeOfDay < at)
        {
            candidate = candidate.AddDays(-7);
        }

        return Key(candidate);
    }

    /// <summary>The most recent month in which day <paramref name="day"/> (1 to 28, so every month has it) at <paramref name="at"/> has passed; the key is that day.</summary>
    public static string LatestMonthly(DateTime local, int day, TimeSpan at)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(day, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(day, 28);
        var candidate = new DateTime(local.Year, local.Month, day);
        return Key(local >= candidate + at ? candidate : candidate.AddMonths(-1));
    }
}
