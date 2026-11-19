using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Follow;
using FeedEater.Inbox;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FollowTests(PostgresFixture pg) : IAsyncLifetime
{
    // 12:00 in Kyiv (EEST).
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync() => pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Rig(StoryFollower Follower, FollowJob Job);

    private Rig Build(Action<FeedEaterOptions>? tweak = null)
    {
        var settings = new FeedEaterOptions { TimeZone = "Europe/Kyiv" };
        tweak?.Invoke(settings);
        var options = Options.Create(settings);
        var follower = new StoryFollower(new FollowStore(pg.Db), new ItemStore(pg.Db), new InboxService(new InboxStore(pg.Db), _time), options, _time);
        var job = new FollowJob(new FollowStore(pg.Db), follower, new QuietHours(new CursorStore(pg.Db), options, _time), options, new LoopHealth(_time), _time, NullLogger<FollowJob>.Instance);
        return new Rig(follower, job);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private Task<long> RootAsync(string title = "Root story", float[]? embedding = null) =>
        Seed.ItemAsync(pg, 1, title, embedding ?? TestVectors.OneHot(0), Now.AddHours(-2), Now.AddHours(-1));

    /// <summary>An item ingested five minutes after the follow starts, as the clusterer left it: considered, a head or a member.</summary>
    private async Task<long> LaterAsync(string title, float[] embedding, long? head, long feed = 2, int minutes = 5, bool considered = true)
    {
        var id = await Seed.ItemAsync(pg, feed, title, embedding, Now, Now.AddMinutes(minutes));
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update items set clustered = @considered, cluster_of = @head where id = @id", new { id, head, considered });
        return id;
    }

    private async Task<T> QueryAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.ExecuteScalarAsync<T>(sql, args))!;
    }

    [Fact]
    public async Task Starting_a_follow_stores_it_for_the_configured_days()
    {
        var rig = Build();
        var root = await RootAsync();

        Assert.Equal(FollowOutcome.Started, await rig.Follower.StartAsync(root, default));

        Assert.Equal(14, await QueryAsync<int>("select extract(day from ends_at - started_at)::int from follows where id = 1"));
        Assert.Equal(root, await QueryAsync<long>("select root_item_id from follows where id = 1"));
    }

    [Fact]
    public async Task One_follow_per_story_even_from_another_member_of_it()
    {
        var rig = Build();
        var root = await RootAsync();
        var member = await LaterAsync("Same story elsewhere", TestVectors.OneHot(0), root);
        await rig.Follower.StartAsync(root, default);

        Assert.Equal(FollowOutcome.AlreadyFollowing, await rig.Follower.StartAsync(member, default));
        Assert.Equal(FollowOutcome.AlreadyFollowing, await rig.Follower.StartAsync(root, default));
        Assert.Equal(1, await QueryAsync<int>("select count(*) from follows"));
    }

    [Fact]
    public async Task At_most_the_configured_number_of_stories_are_followed()
    {
        var rig = Build(o => o.Follow = new FollowOptions { MaxActive = 2 });
        var a = await RootAsync("A", TestVectors.OneHot(0));
        var b = await RootAsync("B", TestVectors.OneHot(1));
        var c = await RootAsync("C", TestVectors.OneHot(2));

        await rig.Follower.StartAsync(a, default);
        await rig.Follower.StartAsync(b, default);

        Assert.Equal(FollowOutcome.TooMany, await rig.Follower.StartAsync(c, default));
        Assert.Equal(2, await QueryAsync<int>("select count(*) from follows"));
    }

    [Fact]
    public async Task A_stopped_follow_frees_its_place()
    {
        var rig = Build(o => o.Follow = new FollowOptions { MaxActive = 1 });
        var a = await RootAsync("A", TestVectors.OneHot(0));
        var b = await RootAsync("B", TestVectors.OneHot(1));
        await rig.Follower.StartAsync(a, default);

        Assert.True(await rig.Follower.CloseAsync(1, default));

        Assert.Equal(FollowOutcome.Started, await rig.Follower.StartAsync(b, default));
        Assert.False(await rig.Follower.CloseAsync(1, default));
    }

    [Fact]
    public async Task With_following_off_nothing_is_stored()
    {
        var rig = Build(o => o.Follow = new FollowOptions { Enabled = false });
        var root = await RootAsync();

        Assert.Equal(FollowOutcome.Off, await rig.Follower.StartAsync(root, default));
        Assert.Equal(0, await QueryAsync<int>("select count(*) from follows"));
    }

    [Fact]
    public async Task An_item_that_is_gone_cannot_be_followed()
    {
        var rig = Build();

        Assert.Equal(FollowOutcome.NoItem, await rig.Follower.StartAsync(9999, default));
    }

    [Fact]
    public async Task Cluster_members_and_near_duplicates_land_in_the_inbox_and_nothing_else_does()
    {
        var rig = Build();
        var root = await RootAsync();
        await rig.Follower.StartAsync(root, default);
        var member = await LaterAsync("Member of the story", TestVectors.OneHot(5), root);
        var near = await LaterAsync("Near duplicate", TestVectors.Blend(0, 1, 0.41f), null);   // cosine 0.82: under the cluster threshold, over the follow's
        var far = await LaterAsync("Related only loosely", TestVectors.Blend(0, 1, 0.5f), null);   // cosine 0.71
        var other = await LaterAsync("Member of another story", TestVectors.OneHot(0), far);
        var pending = await LaterAsync("Not clustered yet", TestVectors.OneHot(0), null, considered: false);

        var sent = await rig.Job.RunAsync(default);

        Assert.Equal(2, sent);
        Assert.Equal(2, await QueryAsync<int>("select count(*) from inbox where kind = 'follow' and ref_id = 1 and item_id in (@member, @near)", new { member, near }));
        Assert.Equal(0, await QueryAsync<int>("select count(*) from follow_items where item_id in (@far, @other, @pending)", new { far, other, pending }));
        Assert.Equal(near, await QueryAsync<long>("select last_item_id from follows where id = 1"));
    }

    [Fact]
    public async Task An_item_close_to_one_already_sent_follows_it_and_nothing_is_added_twice()
    {
        var rig = Build();
        var root = await RootAsync();
        await rig.Follower.StartAsync(root, default);
        await LaterAsync("First development", TestVectors.Blend(0, 1, 0.41f), null);
        await rig.Job.RunAsync(default);
        await LaterAsync("Second development", TestVectors.Blend(0, 1, 0.7f), null);   // 0.39 to the root, 0.85 to the first development

        Assert.Equal(1, await rig.Job.RunAsync(default));
        Assert.Equal(0, await rig.Job.RunAsync(default));
        Assert.Equal(2, await QueryAsync<int>("select count(*) from inbox where kind = 'follow'"));
    }

    [Fact]
    public async Task A_follow_closes_when_its_time_is_up()
    {
        var rig = Build();
        var root = await RootAsync();
        await rig.Follower.StartAsync(root, default);

        _time.Advance(TimeSpan.FromDays(15));
        await rig.Job.RunAsync(default);

        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 1"));
    }

    [Fact]
    public async Task A_follow_closes_when_it_reaches_its_limit()
    {
        var rig = Build(o => o.Follow = new FollowOptions { MaxMessages = 1 });
        var root = await RootAsync();
        await rig.Follower.StartAsync(root, default);
        await LaterAsync("Member", TestVectors.OneHot(5), root);

        await rig.Job.RunAsync(default);

        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 1"));
        Assert.Equal(1, await QueryAsync<int>("select count(*) from inbox where kind = 'follow'"));
    }

    [Fact]
    public async Task Quiet_hours_hold_the_run_and_the_close_until_they_end()
    {
        var rig = Build(o => o.Quiet = new QuietOptions { From = new TimeSpan(11, 0, 0), To = new TimeSpan(13, 0, 0) });
        var root = await RootAsync();
        await rig.Follower.StartAsync(root, default);
        await LaterAsync("Member", TestVectors.OneHot(5), root);

        await rig.Job.TickAsync(default);
        _time.Advance(TimeSpan.FromDays(15) - TimeSpan.FromHours(0.5));   // 11:30 local, past the end of the follow
        await rig.Job.TickAsync(default);

        Assert.Equal("active", await QueryAsync<string>("select status from follows where id = 1"));
        Assert.Equal(0, await QueryAsync<int>("select count(*) from inbox where kind = 'follow'"));

        _time.Advance(TimeSpan.FromHours(2));   // 13:30 local
        await rig.Job.TickAsync(default);

        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 1"));
    }
}
