using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Loops;
using FeedEater.Plane;
using FeedEater.Sources;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FeedManagementTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SourceKit.Now;

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Atom =
        """<feed xmlns="http://www.w3.org/2005/Atom"><title>Atom feed</title><link href="https://atom.example/"/><entry><title>Entry</title><id>e1</id><link href="https://atom.example/e1"/><updated>2026-10-06T10:00:00Z</updated><summary>s</summary></entry></feed>""";

    private const string Json =
        """{"version":"https://jsonfeed.org/version/1.1","title":"JSON feed","home_page_url":"https://json.example/","items":[{"id":"1","url":"https://json.example/1","title":"Entry","content_text":"t","date_published":"2026-10-06T10:00:00Z"}]}""";

    [Theory]
    [InlineData("rss", "Feed title")]
    [InlineData("atom", "Atom feed")]
    [InlineData("json", "JSON feed")]
    public async Task Adding_a_feed_url_subscribes_and_stores_its_recent_entries_in_each_format(string format, string title)
    {
        var body = format switch
        {
            "atom" => Atom,
            "json" => Json,
            _ => SourceKit.Rss(("g1", "Entry", Now.AddHours(-2), "t")),
        };
        var kit = new SourceKit(pg, _ => SourceKit.Xml(body, format == "json" ? "application/feed+json" : "application/xml"));

        var result = await kit.Manager.AddAsync("https://x.example/feed", default);

        Assert.True(result.Ok);
        Assert.False(result.Existing);
        Assert.Equal(1, result.Items);
        Assert.Equal(["Entry"], (await new ItemStore(pg.Db).SearchAsync(null, "Entry", null, null, null, null, 10, default)).Select(h => h.Title));
        var feed = Assert.Single(await kit.Feeds.ListAsync(default));
        Assert.Equal(title, feed.Title);
        Assert.Equal("https://x.example/feed", feed.FeedUrl);
        Assert.Equal(Now.UtcDateTime, feed.LastFetchedAt);
    }

    [Fact]
    public async Task A_site_address_is_resolved_to_the_feed_its_page_links()
    {
        // The feed lives on another host, so the one-request-a-second wait per host does not apply between the two fetches.
        var kit = new SourceKit(pg, r => r.RequestUri!.Host == "feeds.example"
            ? SourceKit.Xml(SourceKit.Rss(("g1", "Entry", Now.AddHours(-1), "t")))
            : SourceKit.Xml("""<html><head><link rel="alternate" type="application/rss+xml" href="https://feeds.example/feed.xml"></head><body>hi</body></html>""", "text/html"));

        var result = await kit.Manager.AddAsync("blog.example", default);   // no scheme

        Assert.True(result.Ok);
        Assert.Equal("https://blog.example/", kit.Stub.Calls[0].Uri);
        Assert.Equal("https://feeds.example/feed.xml", Assert.Single(await kit.Feeds.ListAsync(default)).FeedUrl);
    }

    [Fact]
    public async Task Adding_a_feed_twice_says_so_and_keeps_one()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Xml(SourceKit.Rss(("g1", "Entry", Now.AddHours(-1), "t"))));
        await kit.Manager.AddAsync("https://x.example/feed", default);
        kit.Time.Advance(TimeSpan.FromSeconds(2));

        var again = await kit.Manager.AddAsync("https://X.example/feed", default);

        Assert.True(again.Ok);
        Assert.True(again.Existing);
        Assert.Single(await kit.Feeds.ListAsync(default));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://x.example/feed")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Something_that_is_not_a_web_address_is_refused_without_a_request(string input)
    {
        var kit = new SourceKit(pg, _ => SourceKit.Xml(Atom));

        var result = await kit.Manager.AddAsync(input, default);

        Assert.False(result.Ok);
        Assert.Empty(kit.Stub.Calls);
        Assert.Empty(await kit.Feeds.ListAsync(default));
    }

    [Fact]
    public async Task A_page_with_no_feed_link_and_an_unreachable_address_store_nothing_and_say_why()
    {
        var page = new SourceKit(pg, _ => SourceKit.Xml("<html><body>no feed here</body></html>", "text/html"));
        var gone = new SourceKit(pg, _ => SourceKit.Status(HttpStatusCode.NotFound));

        var noFeed = await page.Manager.AddAsync("https://x.example/", default);
        var missing = await gone.Manager.AddAsync("https://y.example/feed", default);

        Assert.Equal("No feed found at that address.", noFeed.Error);
        Assert.Equal("Could not read it: HTTP 404.", missing.Error);
        Assert.Empty(await page.Feeds.ListAsync(default));
    }

    [Fact]
    public async Task A_private_address_is_not_added_unless_allowed()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Xml(Atom));

        var result = await kit.Manager.AddAsync("http://10.0.0.5/feed", default);

        Assert.Contains("Source:AllowedHosts", result.Error, StringComparison.Ordinal);
        Assert.Empty(kit.Stub.Calls);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class FeedsCommandTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly List<string> _sent = [];

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private CommandHandler Handler(SourceKit kit, bool builtin = true)
    {
        var options = Options.Create(new FeedEaterOptions { Telegram = new TelegramOptions { AllowedUserId = 42 } });
        var telegram = new TelegramClient(new StubHandler((request, body) =>
        {
            if (request.RequestUri!.Segments[^1] == "sendMessage")
            {
                _sent.Add(JsonSerializer.Deserialize<JsonElement>(body).GetProperty("text").GetString()!);
            }

            return StubHandler.Json("""{"ok":true,"result":{"message_id":1}}""");
        }).Client("http://tg/botT/"));
        var items = new ItemStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        var llm = new FeedEater.Llm.LiteLlmClient(new StubHandler((_, _) => StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable)).Client("http://llm/"), new UsageStore(pg.Db), options);
        IIdeaSink sink = new PlaneIdeaSink(new PlaneClient(new StubHandler((_, _) => StubHandler.Json("{}")).Client("http://plane/"), options), options);
        var filer = new IdeaFiler(items, feedback, new ProfileStore(pg.Db), sink, TimeProvider.System);
        var callbacks = new CallbackHandler(telegram, feedback, filer, items, new FeedEater.Signals.KarakeepClient(new StubHandler((_, _) => StubHandler.Json("{}")).Client("http://k/")), options, NullLogger<CallbackHandler>.Instance);
        var search = new FeedEater.Search.ArchiveSearch(items, llm);
        var replies = new ReplyHandler(telegram, callbacks, items, new ProfileStore(pg.Db), llm, sink, options, NullLogger<ReplyHandler>.Instance);
        return new CommandHandler(
            telegram, new DigestTrigger(new CursorStore(pg.Db), options, TimeProvider.System), new QuietHours(new CursorStore(pg.Db), options, TimeProvider.System),
            search, new FeedEater.Search.ArchiveAnswer(search, items, llm, options), replies,
            new FeedEater.Ranking.TasteSwitch(new CursorStore(pg.Db), feedback, options), options,
            builtin ? new FeedsCommand(kit.Feeds, kit.Manager) : null);
    }

    private static TgMessage From(long id, string text) => new(id, 42, text);

    [Fact]
    public async Task Feeds_lists_every_feed_with_failures_marked_and_every_title_encoded()
    {
        var kit = new SourceKit(pg, r => r.RequestUri!.Host == "bad.example" ? SourceKit.Status(HttpStatusCode.NotFound) : SourceKit.Xml(SourceKit.Rss(("g1", "Post", SourceKit.Now, "x"))));
        await kit.AddFeedAsync("https://good.example/feed", "Fine & <i>fancy</i>");
        await kit.AddFeedAsync("https://bad.example/feed", "Broken");
        await kit.Poller.RunAsync(default);

        await Handler(kit).HandleAsync(From(42, "/feeds"), default);

        var text = Assert.Single(_sent);
        Assert.StartsWith("2 feeds", text, StringComparison.Ordinal);
        Assert.Contains("Fine &amp; &lt;i&gt;fancy&lt;/i&gt;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>", text, StringComparison.Ordinal);
        Assert.Contains("Broken ⚠️ HTTP 404", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Feeds_with_no_feeds_says_how_to_add_one()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Status(HttpStatusCode.NotFound));

        await Handler(kit).HandleAsync(From(42, "/feeds"), default);

        Assert.Contains("/feeds add", Assert.Single(_sent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Feeds_add_subscribes_and_reports_what_it_stored_or_why_not()
    {
        var kit = new SourceKit(pg, r => r.RequestUri!.Host == "good.example"
            ? SourceKit.Xml(SourceKit.Rss(("g1", "Post", SourceKit.Now.AddHours(-1), "x")))
            : SourceKit.Status(HttpStatusCode.NotFound));
        var handler = Handler(kit);

        await handler.HandleAsync(From(42, "/feeds add https://good.example/feed"), default);
        kit.Time.Advance(TimeSpan.FromSeconds(2));   // the same host is asked once a second
        await handler.HandleAsync(From(42, "/feeds add https://good.example/feed"), default);
        await handler.HandleAsync(From(42, "/feeds add https://bad.example/<feed>"), default);

        Assert.Equal(
            ["Added Feed title; 1 recent entry stored.", "Already subscribed: Feed title.", "Not added: Could not read it: HTTP 404."],
            _sent);
    }

    [Theory]
    [InlineData("/feeds add")]
    [InlineData("/feeds remove https://x.example/feed")]
    [InlineData("/feeds nonsense")]
    public async Task Other_arguments_get_the_usage_line(string text)
    {
        var kit = new SourceKit(pg, _ => SourceKit.Status(HttpStatusCode.NotFound));

        await Handler(kit).HandleAsync(From(42, text), default);

        Assert.Equal(FeedsCommand.Usage, Assert.Single(_sent));
    }

    [Fact]
    public async Task Nobody_but_the_owner_is_answered()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Status(HttpStatusCode.NotFound));

        await Handler(kit).HandleAsync(From(99, "/feeds add https://x.example/feed"), default);

        Assert.Empty(_sent);
        Assert.Empty(kit.Stub.Calls);
    }

    [Fact]
    public async Task In_miniflux_mode_feeds_points_at_Miniflux()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Status(HttpStatusCode.NotFound));

        await Handler(kit, builtin: false).HandleAsync(From(42, "/feeds add https://x.example/feed"), default);

        Assert.Contains("Miniflux", Assert.Single(_sent), StringComparison.Ordinal);
        Assert.Empty(kit.Stub.Calls);
    }
}
