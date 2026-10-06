using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Loops;
using FeedEater.Memory;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class WeeklyRetainTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset SundayEvening = new(2026, 10, 4, 15, 30, 0, TimeSpan.Zero); // 18:30 Kyiv

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private WeeklyRetain Build(StubHandler hindsight, TimeProvider time) => new(
        new HindsightClient(hindsight.Client("http://hindsight/")), new FeedbackStore(pg.Db),
        new CursorStore(pg.Db), Options.Create(new FeedEaterOptions { TimeZone = "Europe/Kyiv" }), new LoopHealth(time), time, NullLogger<WeeklyRetain>.Instance);

    [Fact]
    public async Task Retains_one_weekly_summary_to_the_configured_bank()
    {
        var liked = await Seed.ItemAsync(pg, 1, "pgvector 0.8.7 released", TestVectors.OneHot(0));
        var feedback = new FeedbackStore(pg.Db);
        await feedback.SetVoteAsync(liked, 1, default);
        await feedback.AddIdeaAsync(new Idea { ItemId = liked, PlaneProject = "LAB", PlaneIssueId = "i", Title = "Upgrade pgvector", At = DateTime.UtcNow }, default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into digests (local_date, status, item_ids, sent_at) values ('2026-10-04', 'sent', @ids, now())", new { ids = new[] { liked } });
        }

        var hindsight = new StubHandler((_, _) => StubHandler.Json("""{"operation_id":"op1"}"""));
        var time = new FakeTimeProvider(SundayEvening);

        await Build(hindsight, time).TickAsync(default);

        var call = hindsight.Calls.Single();
        Assert.Equal("http://hindsight/v1/default/banks/feed-eater/memories", call.Uri);
        Assert.Contains("\"document_id\":\"feed-eater-2026-W40\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("signal:feed-eater", call.Body, StringComparison.Ordinal);
        Assert.Contains("pgvector 0.8.7 released", call.Body, StringComparison.Ordinal);
        Assert.Contains("Upgrade pgvector (LAB)", call.Body, StringComparison.Ordinal);
        Assert.Contains("1 digests with 1 highlights", call.Body, StringComparison.Ordinal);
        Assert.Contains("voted up 1, down 0", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"timestamp\":\"2026-10-04T18:30:00+03:00\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Hindsight_outage_is_logged_and_the_week_is_skipped_not_retried_forever()
    {
        var liked = await Seed.ItemAsync(pg, 1, "pgvector 0.8.7 released", TestVectors.OneHot(0));
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into digests (local_date, status, item_ids, sent_at) values ('2026-10-04', 'sent', @ids, now())", new { ids = new[] { liked } });
        }

        var hindsight = new StubHandler((_, _) => StubHandler.Json("{}", System.Net.HttpStatusCode.BadGateway));
        var cursors = new CursorStore(pg.Db);

        await Build(hindsight, new FakeTimeProvider(SundayEvening)).TickAsync(default);
        await Build(hindsight, new FakeTimeProvider(SundayEvening)).TickAsync(default);

        Assert.Single(hindsight.Calls);   // the run counts as done, so the second tick has nothing due
        Assert.NotNull(await cursors.GetAsync("job:weekly-retain", default));
    }

    [Fact]
    public async Task A_week_without_digests_retains_nothing()
    {
        var hindsight = new StubHandler((_, _) => StubHandler.Json("{}"));

        await Build(hindsight, new FakeTimeProvider(SundayEvening)).TickAsync(default);

        Assert.Empty(hindsight.Calls);
    }
}
