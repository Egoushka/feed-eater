using Dapper;
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
}
