using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Approvals;
using FeedEater.Inbox;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

/// <summary>Cards for the approvals phone inbox: the item shape the approvals server validates, and which inbox kinds send one.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ApprovalsTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Expires = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IOptions<FeedEaterOptions> Settings(string baseUrl = "http://approvals/", string token = "") =>
        Options.Create(new FeedEaterOptions
        {
            Approvals = new ApprovalsOptions { BaseUrl = baseUrl, Token = token.Length > 0 ? token : new string('a', 32) },
            Api = new ApiOptions { BaseUrl = "https://feed.tail.ts.net/" },
        });

    private static ApprovalsClient Client(StubHandler stub, IOptions<FeedEaterOptions> options) =>
        new(stub.Client("http://approvals/"), options, NullLogger<ApprovalsClient>.Instance);

    [Fact]
    public async Task A_card_is_an_info_item_with_a_link_to_the_stack()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json("{}"));
        await Client(stub, Settings()).NotifyAsync(new ApprovalsNote("feed-eater:digest:2026-11-01", "Digest", "3 homelab", "text", Expires), default);

        var call = Assert.Single(stub.Calls);
        Assert.Equal((HttpMethod.Put, "http://approvals/items/feed-eater:digest:2026-11-01"), (call.Method, call.Uri));
        var item = JsonNode.Parse(call.Body)!;
        Assert.Equal("info", item["kind"]!.GetValue<string>());
        Assert.Equal("feed-eater", item["source"]!.GetValue<string>());
        Assert.Equal("feed-eater:digest:2026-11-01", item["id"]!.GetValue<string>());
        Assert.Equal("2026-11-03T08:00:00Z", item["expires_at"]!.GetValue<string>());
        Assert.Equal("https://feed.tail.ts.net/swipe", item["links"]![0]!["url"]!.GetValue<string>());
        Assert.Null(item["actions"]);
    }

    [Fact]
    public async Task Title_and_summary_are_cut_to_the_limits_the_approvals_server_enforces()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json("{}"));
        await Client(stub, Settings()).NotifyAsync(new ApprovalsNote("feed-eater:x", new string('t', 500), new string('s', 500), "text", Expires), default);

        var item = JsonNode.Parse(stub.Calls[0].Body)!;
        Assert.Equal(200, item["title"]!.GetValue<string>().Length);
        Assert.Equal(300, item["summary"]!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData("", "tok")]
    [InlineData("http://approvals/", "")]
    public async Task Without_an_address_or_a_token_nothing_is_sent(string baseUrl, string token)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json("{}"));
        var options = Options.Create(new FeedEaterOptions { Approvals = new ApprovalsOptions { BaseUrl = baseUrl, Token = token } });
        var client = Client(stub, options);

        await client.NotifyAsync(new ApprovalsNote("feed-eater:x", "t", "", "t", Expires), default);

        Assert.False(client.Enabled);
        Assert.Empty(stub.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_refusal_or_an_outage_is_swallowed(HttpStatusCode status)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json("""{"error":"no"}""", status));
        await Client(stub, Settings()).NotifyAsync(new ApprovalsNote("feed-eater:x", "t", "", "t", Expires), default);

        var down = new StubHandler((_, _) => throw new HttpRequestException("down"));
        await Client(down, Settings()).NotifyAsync(new ApprovalsNote("feed-eater:x", "t", "", "t", Expires), default);

        Assert.Single(stub.Calls);
        Assert.Single(down.Calls);
    }

    private InboxService Inbox(StubHandler approvals) =>
        new(new InboxStore(pg.Db), new TelegramClient(new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":{"message_id":1}}""")).Client("http://tg/botT/")),
            Options.Create(new FeedEaterOptions { Telegram = new TelegramOptions { Token = "T", AllowedUserId = 42 }, Approvals = Settings().Value.Approvals }),
            approvals: Client(approvals, Settings()));

    [Fact]
    public async Task A_notice_row_sends_one_card_and_a_duel_row_none()
    {
        var approvals = new StubHandler((_, _) => StubHandler.Json("{}"));
        var inbox = Inbox(approvals);

        await inbox.AddAsync(new InboxEntry("notice:digest:2026-11-01:none", InboxKinds.Notice, Payload: new JsonObject { ["text"] = "No digest today: nothing new" }),
            new OutMessage("x"), default);
        await inbox.AddAsync(new InboxEntry("duel:1", InboxKinds.Duel, Payload: new JsonObject { ["text"] = "a or b" }), new OutMessage("y"), default);

        var call = Assert.Single(approvals.Calls);
        Assert.Equal("http://approvals/items/feed-eater:notice:digest:2026-11-01:none", call.Uri);
        Assert.Equal("No digest today: nothing new", JsonDocument.Parse(call.Body).RootElement.GetProperty("title").GetString());
    }
}
