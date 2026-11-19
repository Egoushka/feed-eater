using System.Text.Json.Nodes;
using Dapper;
using FeedEater.Inbox;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InboxTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private InboxService Build() => new(new InboxStore(pg.Db));

    private static InboxEntry Review(string key = "review:2026-10-18") =>
        new(key, InboxKinds.Review, Payload: new JsonObject { ["text"] = "week" });

    [Fact]
    public async Task Stores_the_entry()
    {
        var post = await Build().AddAsync(Review(), default);

        var row = await new InboxStore(pg.Db).GetAsync(post.Id, default);
        Assert.Equal(InboxKinds.Review, row!.Kind);
        Assert.Equal("week", row.Payload["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_same_key_twice_is_one_row()
    {
        var inbox = Build();
        var first = await inbox.AddAsync(Review(), default);
        var second = await inbox.AddAsync(Review(), default);

        Assert.Equal(first.Id, second.Id);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from inbox"));
    }

    [Fact]
    public async Task Open_lists_undone_unexpired_entries_oldest_first()
    {
        var store = new InboxStore(pg.Db);
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var a = await store.AddAsync(new InboxEntry("a", InboxKinds.Notice), now.AddHours(-3), default);
        var b = await store.AddAsync(new InboxEntry("b", InboxKinds.Notice, ExpiresAt: now.AddHours(-1)), now.AddHours(-2), default);
        var c = await store.AddAsync(new InboxEntry("c", InboxKinds.Notice), now.AddHours(-1), default);
        await store.DoneAsync(c, now, default);

        var open = await store.OpenAsync(now, 10, default);

        Assert.Equal([a], open.Select(r => r.Id));
        Assert.DoesNotContain(b, open.Select(r => r.Id));
    }

    [Fact]
    public async Task The_same_key_again_refreshes_the_expiry_and_lists_an_expired_row_again()
    {
        var store = new InboxStore(pg.Db);
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var id = await store.AddAsync(new InboxEntry("idea:x", InboxKinds.Idea, ExpiresAt: now.AddDays(14)), now, default);
        var later = now.AddDays(15);
        Assert.Empty(await store.OpenAsync(later, 10, default));

        var again = await store.AddAsync(new InboxEntry("idea:x", InboxKinds.Idea, ExpiresAt: later.AddDays(14)), later, default);

        Assert.Equal(id, again);
        Assert.Equal(later.AddDays(14), (await store.GetAsync(id, default))!.ExpiresAt);
        Assert.Equal([id], (await store.OpenAsync(later, 10, default)).Select(r => r.Id));
    }
}
