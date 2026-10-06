using Microsoft.Extensions.Options;
using FeedEater.Ranking;
using FeedEater.Review;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TasteSwitchTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private TasteSwitch Build(bool configured) =>
        new(new CursorStore(pg.Db), new FeedbackStore(pg.Db), Options.Create(new FeedEaterOptions { Taste = new TasteOptions { Learn = configured } }));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task The_telegram_switch_wins_over_the_config_and_without_it_the_config_decides(bool configured, bool switched)
    {
        var taste = Build(configured);
        Assert.Equal(configured, await taste.IsOnAsync(default));

        await taste.SetAsync(switched, default);

        Assert.Equal(switched, await taste.IsOnAsync(default));
    }

    [Fact]
    public async Task With_too_few_votes_there_is_no_weekly_suggestion() =>
        Assert.Null(await Build(false).SuggestionAsync(default));

    [Fact]
    public void The_weekly_review_shows_the_suggestion_under_ranking()
    {
        var report = new WeeklyReport(new DateTimeOffset(2026, 10, 4, 15, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 11, 15, 30, 0, TimeSpan.Zero), 1, 0, 0, 0, 0, [], [], [], [], []);

        var html = WeeklyFormatter.Message("2026-10-11", report, TimeZoneInfo.Utc, "Send /learn on <now>.").Html;

        Assert.EndsWith("\n\n<b>Ranking</b>\nSend /learn on &lt;now&gt;.", html, StringComparison.Ordinal);
    }
}
