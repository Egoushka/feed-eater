using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using FeedEater.Issues;
using FeedEater.Storage;
using FeedEater.Swipe;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SwipeIdeaTests(PostgresFixture pg) : IAsyncLifetime
{
    private static WebApplicationFactory<Program>? app;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        await new IdeaStore(pg.Db).SeedAsync(LostIdeaSeed.All, default);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FakeSink(int? number, bool fails = false) : IIdeaSink
    {
        public List<IdeaDraft> Filed { get; } = [];
        public string Name => "fake";
        public string? ProjectOf(Profile? profile) => null;
        public string? FallbackProject => "octo/fallback";
        string IIdeaSink.Filed(string project) => $"Filed in {project}";

        public Task<int?> FileAsync(IdeaDraft idea, CancellationToken ct)
        {
            if (fails)
            {
                throw new InvalidOperationException("GitHub issues are not configured");
            }

            Filed.Add(idea);
            return Task.FromResult(number);
        }
    }

    private SwipeService Service(int perSession = 1, int perDay = 2, FakeSink? sink = null)
    {
        app ??= new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            (string Key, string Value)[] settings =
            [
                ("ConnectionStrings:FeedEater", pg.ConnectionString),
                ("Mcp:Token", "swipe-token"),
                ("FeedEater:RunJobs", "false"),
                ("FeedEater:ProfilePath", "/nope/profile.json"),
                ("FeedEater:Swipe:Enabled", "true"),
            ];
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
        });
        var swipe = app.Services.GetRequiredService<IOptions<FeedEaterOptions>>().Value.Swipe;
        swipe.Cards = 20;
        swipe.Ideas.PerSession = perSession;
        swipe.Ideas.PerDay = perDay;
        return sink is null ? app.Services.GetRequiredService<SwipeService>() : ActivatorUtilities.CreateInstance<SwipeService>(app.Services, (IIdeaSink)sink);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no row");
    }

    private async Task SqlAsync(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(sql, args);
    }

    private async Task<CardView> IdeaCardAsync(SwipeService svc)
    {
        var next = await svc.NextAsync(null, default);
        var card = Assert.IsType<CardView>(next.Card);
        Assert.Equal("lost_idea", card.Kind);
        return card;
    }

    private static Task<ActResult> ActAsync(SwipeService svc, CardView card, string act) => svc.ActAsync(card.Session, card.Position, act, null, default);

    [Fact]
    public async Task Seeding_twice_adds_nothing_and_keeps_his_exact_words()
    {
        var store = new IdeaStore(pg.Db);

        Assert.Equal(0, await store.SeedAsync(LostIdeaSeed.All, default));

        Assert.Equal(9, await ScalarAsync<long>("select count(*) from idea_items"));
        Assert.Equal(LostIdeaSeed.All.Select(s => s.Body).Order(), (await ScalarAsync<string[]>("select array_agg(body) from idea_items")).Order());
    }

    [Fact]
    public async Task A_swipe_writes_one_label_row_and_moves_the_schedule()
    {
        var svc = Service(perSession: 1, perDay: 2);
        var card = await IdeaCardAsync(svc);
        var id = await ScalarAsync<long>("select queue_items[1] from swipe_sessions");

        Assert.True((await ActAsync(svc, card, "idea_keep")).Ok);

        Assert.Equal(1, await ScalarAsync<long>("select count(*) from labels"));
        Assert.Equal(("feed-eater", "lost_idea", id.ToString(), "kept"),
            (await ScalarAsync<string>("select surface from labels"), await ScalarAsync<string>("select item_kind from labels"), await ScalarAsync<string>("select item_id from labels"), await ScalarAsync<string>("select action from labels")));
        Assert.Equal(1, await ScalarAsync<int>("select reps from idea_items where id = @id", new { id }));
        Assert.True(await ScalarAsync<bool>("select due_at > now() from idea_items where id = @id", new { id }));
    }

    [Fact]
    public async Task Later_snoozes_the_idea_and_counts_a_lapse()
    {
        var svc = Service(perSession: 1, perDay: 2);
        var card = await IdeaCardAsync(svc);

        Assert.True((await ActAsync(svc, card, "idea_later")).Ok);

        Assert.Equal(["snoozed"], await SqlStringsAsync("select action from labels"));
        Assert.Equal(1, await ScalarAsync<long>("select count(*) from idea_items where lapses = 1 and status = 'active'"));
    }

    [Fact]
    public async Task Archive_ends_the_idea_and_labels_it_dismissed()
    {
        var svc = Service(perSession: 1, perDay: 2);
        var card = await IdeaCardAsync(svc);

        Assert.True((await ActAsync(svc, card, "idea_archive")).Ok);

        Assert.Equal(["dismissed"], await SqlStringsAsync("select action from labels"));
        Assert.Equal(1, await ScalarAsync<long>("select count(*) from idea_items where status = 'archived'"));
    }

    private async Task<string[]> SqlStringsAsync(string sql)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.QueryAsync<string>(sql)).ToArray();
    }

    [Fact]
    public async Task A_stack_holds_each_idea_once_and_the_caps_hold()
    {
        var svc = Service(perSession: 5, perDay: 2);

        var first = await svc.BuildQueueAsync(DateTimeOffset.UtcNow, default);
        var second = await svc.BuildQueueAsync(DateTimeOffset.UtcNow, default);

        var ideas = first.Where(q => q.Kind == "lost_idea").Select(q => q.Id).ToList();
        Assert.Equal(2, ideas.Count);
        Assert.Equal(ideas.Distinct().Count(), ideas.Count);
        Assert.DoesNotContain(second, q => q.Kind == "lost_idea");
    }

    [Fact]
    public async Task Archived_and_promoted_ideas_never_resurface()
    {
        await SqlAsync("update idea_items set status = 'archived' where id % 2 = 0; update idea_items set status = 'promoted' where id % 2 = 1");
        var svc = Service(perSession: 9, perDay: 9);

        Assert.DoesNotContain(await svc.BuildQueueAsync(DateTimeOffset.UtcNow.AddDays(400), default), q => q.Kind == "lost_idea");
    }

    [Fact]
    public async Task A_kept_idea_is_not_due_again_the_same_day()
    {
        var svc = Service(perSession: 1, perDay: 9);
        var card = await IdeaCardAsync(svc);
        await ActAsync(svc, card, "idea_keep");
        var kept = await ScalarAsync<long>("select id from idea_items where reps = 1");

        var queue = await svc.BuildQueueAsync(DateTimeOffset.UtcNow, default);

        Assert.DoesNotContain(queue, q => q.Kind == "lost_idea" && q.Id == kept);
    }

    [Fact]
    public async Task No_count_of_ideas_is_in_a_card_or_a_summary()
    {
        var svc = Service(perSession: 1, perDay: 2);
        var card = await IdeaCardAsync(svc);
        await ActAsync(svc, card, "idea_keep");
        var summary = await svc.EndAsync(card.Session, default);

        Assert.Matches(@"^You said this on \d{1,2} \w{3} 2026$", card.Why);
        Assert.DoesNotContain(new[] { card.Feed, card.Why, card.Age }.Concat(card.Detail?.Values ?? []), v => v is not null && v.Contains("due", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(summary!.Summary!.Acts);
        Assert.DoesNotContain("idea_", JsonSerializer.Serialize(summary), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Promote_files_an_issue_with_the_empty_sections_and_ends_the_idea()
    {
        var sink = new FakeSink(7);
        var svc = Service(sink: sink);
        var card = await IdeaCardAsync(svc);

        var result = await ActAsync(svc, card, "idea_promote");

        Assert.True(result.Ok);
        var draft = Assert.Single(sink.Filed);
        Assert.Contains("## Value hypothesis", draft.Body, StringComparison.Ordinal);
        Assert.Contains("## Cheapest test", draft.Body, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarAsync<long>("select count(*) from idea_items where status = 'promoted' and issue_number = 7"));
        Assert.Equal("promoted", await ScalarAsync<string>("select action from labels"));
    }

    [Fact]
    public async Task Promote_that_cannot_file_fails_and_changes_nothing()
    {
        var svc = Service(sink: new FakeSink(null, fails: true));
        var card = await IdeaCardAsync(svc);

        var result = await ActAsync(svc, card, "idea_promote");

        Assert.False(result.Ok);
        Assert.Equal(0, await ScalarAsync<long>("select count(*) from labels"));
        Assert.Equal(9, await ScalarAsync<long>("select count(*) from idea_items where status = 'active'"));
    }

    [Fact]
    public async Task A_card_left_alone_writes_one_ignored_label_however_many_events_come()
    {
        var svc = Service();
        var card = await IdeaCardAsync(svc);

        await svc.EventAsync(card.Session, card.Position, null, 1200, "up", default);
        await svc.EventAsync(card.Session, card.Position, null, 1500, "up", default);

        Assert.Equal(["ignored"], await SqlStringsAsync("select action from labels"));
    }

    [Fact]
    public async Task Labels_are_append_only()
    {
        await SqlAsync("insert into labels (item_kind, item_id, action) values ('item', '1', 'kept')");

        await Assert.ThrowsAnyAsync<Exception>(() => SqlAsync("update labels set action = 'ignored'"));
        await Assert.ThrowsAnyAsync<Exception>(() => SqlAsync("delete from labels"));
    }
}
