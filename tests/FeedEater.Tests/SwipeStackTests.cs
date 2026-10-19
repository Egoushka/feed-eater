using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using FeedEater.Inbox;
using FeedEater.Storage;
using FeedEater.Swipe;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SwipeStackTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // One host for the class: every extra host adds a connection pool, and the shared Postgres has a low connection limit.
    private static WebApplicationFactory<Program>? app;

    private SwipeService Service(int cards = 20)
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
        app.Services.GetRequiredService<IOptions<FeedEaterOptions>>().Value.Swipe.Cards = cards;
        return app.Services.GetRequiredService<SwipeService>();
    }

    private async Task<long[]> DigestAsync(int n)
    {
        var ids = new List<long>();
        for (var i = 1; i <= n; i++)
        {
            ids.Add(await ItemAsync(i));
        }

        await SqlAsync("insert into digests (local_date, status, item_ids, sent_at) values ('2026-10-19', 'sent', @ids, @at)",
            new { ids = ids.ToArray(), at = Now.AddHours(-1).UtcDateTime });
        return [.. ids];
    }

    private async Task RankedItemsAsync(int n)
    {
        for (var i = 0; i < n; i++)
        {
            await ItemAsync(100 + i);
        }
    }

    private Task<long> StartSessionAsync() =>
        new SwipeStore(pg.Db).StartAsync(20, 600, [], [], [], [], [], Now.AddHours(-2), default);

    private Task<long> ItemAsync(int n) => Seed.ItemAsync(pg, 1, $"Item {n}", TestVectors.OneHot(n), Now.UtcDateTime.AddHours(-5));

    private async Task SqlAsync(string sql, object args)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(sql, args);
    }

    private async Task ShownAsync(long item, string kind)
    {
        var session = await StartSessionAsync();
        await SqlAsync("insert into swipe_events (session_id, position, kind, ref_id) values (@session, 0, @kind, @item)", new { session, kind, item });
    }

    [Fact]
    public async Task Eligible_skips_done_expired_and_twice_shown_entries()
    {
        var store = new InboxStore(pg.Db);
        var now = Now;
        var open = await store.AddAsync(new InboxEntry("a", InboxKinds.Notice, ExpiresAt: now.AddDays(1)), now.AddHours(-3), default);
        var twice = await store.AddAsync(new InboxEntry("b", InboxKinds.Notice, ExpiresAt: now.AddDays(1)), now.AddHours(-2), default);
        var session = await StartSessionAsync();
        await store.MarkShownAsync(twice, session, default);
        await store.MarkShownAsync(twice, session, default);
        var once = await store.AddAsync(new InboxEntry("c", InboxKinds.Notice, ExpiresAt: now.AddDays(1)), now.AddHours(-1), default);
        await store.MarkShownAsync(once, session, default);

        Assert.Equal([open, once], (await store.EligibleAsync(now, 10, default)).Select(r => r.Id));
    }

    [Fact]
    public async Task Digest_slots_keep_digest_order_and_drop_voted_and_shown_items()
    {
        var (a, b, c) = (await ItemAsync(1), await ItemAsync(2), await ItemAsync(3));
        await SqlAsync("insert into digests (local_date, status, item_ids, sent_at) values ('2026-10-19', 'sent', @ids, @at)",
            new { ids = new[] { c, a, b }, at = Now.AddHours(-1).UtcDateTime });
        await new FeedbackStore(pg.Db).SetVoteAsync(a, 1, default);
        await ShownAsync(b, "digest");

        Assert.Equal([c], await new SwipeStore(pg.Db).DigestSlotsAsync(Now.AddDays(-3), default));
    }

    [Fact]
    public async Task A_claimed_client_id_returns_the_stored_act()
    {
        var acts = new SwipeActStore(pg.Db);
        var id = Guid.NewGuid();
        var session = await StartSessionAsync();

        Assert.Null(await acts.ClaimAsync(id, session, 0, "like", ((short)-1, 0.5f), default));
        await acts.CompleteAsync(id, true, "Liked", default);
        var again = await acts.ClaimAsync(id, session, 0, "like", null, default);

        Assert.Equal("Liked", again!.Message);
        Assert.Equal((short)-1, again.PreviousValue);
    }

    [Fact]
    public async Task VoteOf_returns_the_vote_or_null()
    {
        var store = new FeedbackStore(pg.Db);
        var item = await ItemAsync(1);

        Assert.Null(await store.VoteOfAsync(item, default));
        await store.SetVoteAsync(item, -1, default, weight: 2);

        Assert.Equal(((short)-1, 2f), await store.VoteOfAsync(item, default));
    }

    [Fact]
    public async Task The_head_is_notices_then_digest_then_inbox_unshuffled()
    {
        var digest = await DigestAsync(3);
        var inbox = new InboxStore(pg.Db);
        var follow = await inbox.AddAsync(new InboxEntry("f", InboxKinds.Follow, ItemId: digest[0], ExpiresAt: Now.AddDays(7)), Now.AddHours(-2), default);
        var notice = await inbox.AddAsync(new InboxEntry("n", InboxKinds.Notice, ExpiresAt: Now.AddDays(1)), Now.AddHours(-1), default);
        await RankedItemsAsync(10);

        var queue = await Service().BuildQueueAsync(Now, default);

        Assert.Equal(
            [("notice", notice), ("digest", digest[0]), ("digest", digest[1]), ("digest", digest[2]), ("follow", follow)],
            queue.Take(5).Select(q => (q.Kind, q.Id)));
        Assert.DoesNotContain(queue.Skip(5), q => digest.Contains(q.Id) && q.Kind == "item");
    }

    [Fact]
    public async Task Overflow_beyond_the_cap_waits_for_the_next_session()
    {
        var digest = await DigestAsync(5);
        var svc = Service(cards: 3);

        var first = await svc.BuildQueueAsync(Now, default);

        Assert.Equal(digest.Take(3), first.Select(q => q.Id));
        foreach (var q in first)
        {
            await ShownAsync(q.Id, "digest");
        }

        Assert.Equal(digest.Skip(3), await new SwipeStore(pg.Db).DigestSlotsAsync(Now.AddDays(-3), default));
    }
}
