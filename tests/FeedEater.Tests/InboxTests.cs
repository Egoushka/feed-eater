using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Options;
using FeedEater.Inbox;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InboxTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly List<string> _sent = [];
    private bool _telegramFails;

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private InboxService Build(string token = "T")
    {
        var options = Options.Create(new FeedEaterOptions { Telegram = new TelegramOptions { Token = token, AllowedUserId = 42 } });
        var stub = new StubHandler((_, body) =>
        {
            if (_telegramFails)
            {
                return StubHandler.Json("""{"ok":false,"error_code":429,"description":"Too Many Requests"}""");
            }

            _sent.Add(body);
            return StubHandler.Json("""{"ok":true,"result":{"message_id":77}}""");
        });
        return new InboxService(new InboxStore(pg.Db), new TelegramClient(stub.Client("http://tg/botT/")), options);
    }

    private static InboxEntry Review(string key = "review:2026-10-18") =>
        new(key, InboxKinds.Review, Payload: new JsonObject { ["text"] = "week" });

    [Fact]
    public async Task Stores_the_entry_and_sends_it_while_Telegram_is_on()
    {
        var post = await Build().AddAsync(Review(), new OutMessage("<b>week</b>"), default);

        Assert.Equal(77, post.TelegramMessageId);
        Assert.Single(_sent);
        var row = await new InboxStore(pg.Db).GetAsync(post.Id, default);
        Assert.Equal(InboxKinds.Review, row!.Kind);
        Assert.Equal("week", row.Payload["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_a_token_it_only_stores()
    {
        var post = await Build(token: "").AddAsync(Review(), new OutMessage("x"), default);

        Assert.Null(post.TelegramMessageId);
        Assert.Empty(_sent);
        Assert.NotNull(await new InboxStore(pg.Db).GetAsync(post.Id, default));
    }

    [Fact]
    public async Task The_same_key_twice_is_one_row_and_two_sends()
    {
        var inbox = Build();
        var first = await inbox.AddAsync(Review(), new OutMessage("x"), default);
        var second = await inbox.AddAsync(Review(), new OutMessage("x"), default);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, _sent.Count);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from inbox"));
    }

    [Fact]
    public async Task A_failed_send_keeps_the_row_and_rethrows()
    {
        _telegramFails = true;
        await Assert.ThrowsAsync<TelegramException>(() => Build().AddAsync(Review(), new OutMessage("x"), default));

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
}
