using FeedEater.Loops;

namespace FeedEater.Tests;

public sealed class ScheduleTests
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    private static readonly TimeSpan At0730 = new(7, 30, 0);
    private static readonly TimeSpan Noon = new(12, 0, 0);

    [Theory]
    [InlineData("2026-10-05T07:29:00", null)]
    [InlineData("2026-10-05T07:30:00", "2026-10-05")]
    [InlineData("2026-10-05T11:59:00", "2026-10-05")]
    [InlineData("2026-10-05T12:00:00", null)]
    public void Window_is_open_from_start_until_end(string local, string? expected) =>
        Assert.Equal(expected, Schedule.Window(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), At0730, Noon));

    [Fact]
    public void Local_time_follows_the_october_dst_change()
    {
        // Kyiv is UTC+3 until 2026-10-25 04:00 local, then UTC+2.
        Assert.Equal(new DateTime(2026, 10, 24, 7, 30, 0), Schedule.Local(new DateTimeOffset(2026, 10, 24, 4, 30, 0, TimeSpan.Zero), Kyiv));
        Assert.Equal(new DateTime(2026, 10, 25, 7, 30, 0), Schedule.Local(new DateTimeOffset(2026, 10, 25, 5, 30, 0, TimeSpan.Zero), Kyiv));
    }

    [Fact]
    public void Latest_daily_points_at_yesterday_before_the_hour()
    {
        Assert.Equal("2026-10-04", Schedule.LatestDaily(new DateTime(2026, 10, 5, 1, 0, 0), new TimeSpan(3, 0, 0)));
        Assert.Equal("2026-10-05", Schedule.LatestDaily(new DateTime(2026, 10, 5, 3, 0, 0), new TimeSpan(3, 0, 0)));
    }

    [Fact]
    public void Latest_weekly_points_at_the_last_sunday_evening()
    {
        var at = new TimeSpan(18, 0, 0);
        Assert.Equal("2026-09-27", Schedule.LatestWeekly(new DateTime(2026, 10, 4, 17, 59, 0), DayOfWeek.Sunday, at));
        Assert.Equal("2026-10-04", Schedule.LatestWeekly(new DateTime(2026, 10, 4, 18, 0, 0), DayOfWeek.Sunday, at));
        Assert.Equal("2026-10-04", Schedule.LatestWeekly(new DateTime(2026, 10, 5, 9, 0, 0), DayOfWeek.Sunday, at));
    }
}
