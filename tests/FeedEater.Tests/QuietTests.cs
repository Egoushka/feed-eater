using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class QuietHoursTests(PostgresFixture pg) : IAsyncLifetime
{
    // 2026-10-06 is in EEST (UTC+3).
    private static DateTimeOffset Kyiv(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(3));

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private (QuietHours Quiet, FakeTimeProvider Time) Build(TimeSpan? from, TimeSpan? to, DateTimeOffset now, int manualHours = 12)
    {
        var time = new FakeTimeProvider(now);
        var options = Options.Create(new FeedEaterOptions { Quiet = new QuietOptions { From = from, To = to, ManualHours = manualHours } });
        return (new QuietHours(new CursorStore(pg.Db), options, time), time);
    }

    private static readonly TimeSpan Ten = new(22, 0, 0);
    private static readonly TimeSpan Eight = new(8, 0, 0);

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(2, 30, true)]
    [InlineData(7, 59, true)]
    [InlineData(8, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    public async Task A_window_that_crosses_midnight_holds_inside_it(int hour, int minute, bool quiet)
    {
        var (q, _) = Build(Ten, Eight, Kyiv(6, hour, minute));

        Assert.Equal(quiet, await q.IsQuietAsync(default));
    }

    [Theory]
    [InlineData(12, 59, false)]
    [InlineData(13, 0, true)]
    [InlineData(14, 59, true)]
    [InlineData(15, 0, false)]
    public async Task A_same_day_window_works_too(int hour, int minute, bool quiet)
    {
        var (q, _) = Build(new TimeSpan(13, 0, 0), new TimeSpan(15, 0, 0), Kyiv(6, hour, minute));

        Assert.Equal(quiet, await q.IsQuietAsync(default));
    }

    [Fact]
    public async Task No_window_and_no_toggle_is_never_quiet_and_half_a_window_or_an_empty_one_is_ignored()
    {
        Assert.False(await Build(null, null, Kyiv(6, 3)).Quiet.IsQuietAsync(default));
        Assert.False(await Build(Ten, null, Kyiv(6, 23)).Quiet.IsQuietAsync(default));
        Assert.False(await Build(null, Eight, Kyiv(6, 3)).Quiet.IsQuietAsync(default));
        Assert.False(await Build(Eight, Eight, Kyiv(6, 3)).Quiet.IsQuietAsync(default));
        Assert.Equal("Quiet mode is off.", (await Build(null, null, Kyiv(6, 3)).Quiet.StatusAsync(default)).Text);
    }

    [Fact]
    public async Task Turning_it_on_by_hand_lasts_the_configured_hours_then_expires()
    {
        var (q, time) = Build(null, null, Kyiv(6, 13), manualHours: 3);

        var on = await q.SetAsync(true, default);
        Assert.True(on.Quiet);
        Assert.Equal("Quiet mode is on until 16:00.", on.Text);

        time.Advance(TimeSpan.FromHours(2.9));
        Assert.True(await q.IsQuietAsync(default));
        time.Advance(TimeSpan.FromMinutes(7));
        Assert.False(await q.IsQuietAsync(default));
    }

    [Fact]
    public async Task Turning_it_off_inside_a_window_lasts_until_the_window_ends_and_the_next_night_is_quiet_again()
    {
        var (q, time) = Build(Ten, Eight, Kyiv(7, 2));   // 02:00, inside last night's window

        var off = await q.SetAsync(false, default);
        Assert.False(off.Quiet);
        Assert.Equal("Quiet mode is off until 08:00.", off.Text);

        time.SetUtcNow(Kyiv(7, 7, 59));
        Assert.False(await q.IsQuietAsync(default));
        time.SetUtcNow(Kyiv(7, 8, 1));
        Assert.False(await q.IsQuietAsync(default));   // daytime
        time.SetUtcNow(Kyiv(7, 23));
        Assert.True(await q.IsQuietAsync(default));    // the window is back
    }

    [Fact]
    public async Task Toggle_flips_the_effective_state_and_a_garbled_cursor_is_ignored()
    {
        var (q, _) = Build(Ten, Eight, Kyiv(6, 23));
        Assert.True(await q.IsQuietAsync(default));

        Assert.False((await q.ToggleAsync(default)).Quiet);
        Assert.True((await q.ToggleAsync(default)).Quiet);

        await new CursorStore(pg.Db).SetAsync("quiet:manual", "garbage|x", default);
        Assert.True(await q.IsQuietAsync(default));   // back to the window
    }

    [Fact]
    public async Task Status_describes_the_window()
    {
        Assert.Equal("Quiet hours (22:00 to 08:00) are in effect.", (await Build(Ten, Eight, Kyiv(6, 23)).Quiet.StatusAsync(default)).Text);
        Assert.Equal("Quiet hours are 22:00 to 08:00; not in effect now.", (await Build(Ten, Eight, Kyiv(6, 12)).Quiet.StatusAsync(default)).Text);
    }
}
