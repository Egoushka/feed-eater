using Dapper;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net;
using FeedEater.Inbox;
using FeedEater.Plane;
using FeedEater.WeeklyIdeas;
using FeedEater.Storage;
using FeedEater.Swipe;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SwipeStackTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // The tests rely on the serial Postgres collection.
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
        await store.MarkShownAsync(twice, await StartSessionAsync(), default);
        var once = await store.AddAsync(new InboxEntry("c", InboxKinds.Notice, ExpiresAt: now.AddDays(1)), now.AddHours(-1), default);
        await store.MarkShownAsync(once, session, default);

        Assert.Equal([open, once], (await store.EligibleAsync(now, 10, default)).Select(r => r.Id));
    }

    [Fact]
    public async Task Rendering_the_same_inbox_card_twice_in_a_session_counts_one_showing()
    {
        await AddAsync("notice", new JsonObject { ["text"] = "Hi" });
        var svc = Service();
        var card = await ShowAsync(svc);

        await svc.NextAsync(card.Position, default);
        await svc.NextAsync(card.Position, default);

        Assert.Equal(1, await ScalarAsync<int>("select shown_count from inbox"));
    }

    [Fact]
    public async Task A_digest_card_shown_before_is_not_a_ranked_candidate_later()
    {
        var item = await ItemAsync(1);
        await ShownAsync(item, "digest");

        var candidates = await new SwipeStore(pg.Db).CandidatesAsync(Now.AddDays(-3), Now.AddDays(-1), [], default);

        Assert.DoesNotContain(candidates, c => c.Id == item);
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

    private async Task<CardView> FirstCardAsync()
    {
        var next = await Service().NextAsync(null, default);
        Assert.Equal("card", next.Type);
        return next.Card!;
    }

    private Task<long> AddAsync(string kind, JsonObject payload, long? itemId = null, long? refId = null) =>
        new InboxStore(pg.Db).AddAsync(new InboxEntry($"{kind}:{Guid.NewGuid():N}", kind, itemId, refId, payload, DateTimeOffset.UtcNow.AddDays(7)), DateTimeOffset.UtcNow, default);

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no row");
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("follow")]
    [InlineData("duel")]
    [InlineData("release")]
    [InlineData("idea")]
    [InlineData("review")]
    [InlineData("autopsy")]
    [InlineData("notice")]
    public async Task Each_kind_builds_its_card_from_the_payload_the_jobs_write(string kind)
    {
        var first = await ItemAsync(1);
        var second = await ItemAsync(2);
        string title, feed, why;
        string? url;
        (string, string?)[] detail;
        switch (kind)
        {
            case "digest":
                await SqlAsync("insert into reads (item_id, summary, why, kind, suggestion, model) values (@first, 'The gist', 'Because it matters', 'news', 'Try it', 'm')", new { first });
                await SqlAsync("insert into digests (local_date, status, item_ids, sent_at) values ('2026-10-19', 'sent', @ids, @at)",
                    new { ids = new[] { first }, at = DateTime.UtcNow.AddHours(-1) });
                (title, feed, why) = ("Item 1", "Feed 1", "Because it matters");
                url = await ScalarAsync<string>("select url from items where id = @first", new { first });
                detail = [("summary", "The gist"), ("suggestion", "Try it")];
                break;
            case "follow":
                await AddAsync("follow", new JsonObject { ["story"] = "Big story" }, first, 7);
                (title, feed, why) = ("Item 1", "Feed 1", "New in a story you follow: Big story");
                url = await ScalarAsync<string>("select url from items where id = @first", new { first });
                detail = [("story", "Big story")];
                break;
            case "duel":
                await AddAsync("duel", new JsonObject { ["a"] = first, ["b"] = second }, refId: 3);
                (title, feed, why, url) = ("Which was more useful?", "duel", "", null);
                detail = [("aTitle", "Item 1"), ("bTitle", "Item 2")];
                break;
            case "release":
                await AddAsync("release", new JsonObject
                {
                    ["services"] = "grafana",
                    ["running"] = "11.1",
                    ["version"] = "11.2",
                    ["url"] = "https://example.com/r",
                    ["summary"] = "Fixes",
                    ["breaking"] = true,
                    ["urgent"] = false,
                });
                (title, feed, why, url) = ("grafana 11.1 → 11.2", "release", "Fixes", "https://example.com/r");
                detail = [("breaking", "true"), ("urgent", null)];
                break;
            case "idea":
                await AddAsync("idea", new JsonObject
                {
                    ["week"] = "2026-W43",
                    ["title"] = "Build a thing",
                    ["project"] = "homelab",
                    ["html"] = "<b>x</b>",
                    ["url"] = "https://example.com/i",
                    ["sourceTitle"] = "Source",
                    ["signal"] = "s",
                    ["why"] = "It fits",
                    ["where"] = "src/",
                    ["effort"] = "Small",
                });
                (title, feed, why, url) = ("Build a thing", "homelab", "It fits", "https://example.com/i");
                detail = [("where", "src/"), ("effort", "Small"), ("sourceTitle", "Source")];
                break;
            case "review":
                await AddAsync("review", new JsonObject { ["html"] = "<p>Week <b>one</b></p>" });
                (title, feed, why, url) = ("Weekly review", "review", "Week one", null);
                detail = [];
                break;
            case "autopsy":
                await AddAsync("autopsy", new JsonObject { ["html"] = "<p>Hype</p>" });
                (title, feed, why, url) = ("Hype autopsy", "autopsy", "Hype", null);
                detail = [];
                break;
            default:
                await AddAsync("notice", new JsonObject { ["text"] = "Digest failed" });
                (title, feed, why, url) = ("Notice", "feed-eater", "Digest failed", null);
                detail = [];
                break;
        }

        var card = await FirstCardAsync();

        Assert.Equal(kind, card.Kind);
        Assert.Equal((title, feed, why, url), (card.Title, card.Feed, card.Why, card.Url));
        foreach (var (key, value) in detail)
        {
            Assert.Equal(value, card.Detail![key]);
        }

        if (kind == "duel")
        {
            Assert.StartsWith("https://example.com/", card.Detail!["aUrl"], StringComparison.Ordinal);
        }

        if (detail.Length == 0)
        {
            Assert.Null(card.Detail);
        }
    }

    [Fact]
    public async Task A_duel_with_a_deleted_item_is_skipped()
    {
        await AddAsync("duel", new JsonObject { ["a"] = await ItemAsync(1), ["b"] = 99999 }, refId: 1);
        await AddAsync("review", new JsonObject { ["html"] = "Next up" });

        var card = await FirstCardAsync();

        Assert.Equal("review", card.Kind);
        Assert.Equal(1, card.Position);
    }

    [Fact]
    public async Task Payload_html_and_script_stay_text()
    {
        await AddAsync("notice", new JsonObject { ["text"] = "<script>alert(1)</script>" });

        var card = await FirstCardAsync();

        Assert.Equal("<script>alert(1)</script>", card.Why);           // JSON carries the text; the page sets textContent
        Assert.Equal(1, await ScalarAsync<int>("select shown_count from inbox"));
    }

    private async Task<CardView> ShowAsync(SwipeService svc)
    {
        var next = await svc.NextAsync(null, default);
        Assert.Equal("card", next.Type);
        return next.Card!;
    }

    private static Task<ActResult> ActAsync(SwipeService svc, CardView card, string act) => svc.ActAsync(card.Session, card.Position, act, null, default);

    private async Task<long> DoneCountAsync() => await ScalarAsync<long>("select count(*) from inbox where done_at is not null");

    [Theory]
    [InlineData("like", 1)]
    [InlineData("dislike", -1)]
    [InlineData("save", 1)]
    public async Task A_follow_card_votes_on_its_item_and_is_done(string act, int value)
    {
        var item = await ItemAsync(1);
        await AddAsync("follow", new JsonObject { ["story"] = "S" }, item, null);
        var svc = Service();
        var card = await ShowAsync(svc);

        var result = await ActAsync(svc, card, act);

        Assert.True(result.Ok);
        Assert.Equal(value, await ScalarAsync<int>("select value::int from votes where item_id = @item", new { item }));
        Assert.Equal(1, await DoneCountAsync());
        Assert.Equal(("follow", await ScalarAsync<long>("select id from inbox")), (await ScalarAsync<string>("select kind from swipe_events"), await ScalarAsync<long>("select ref_id from swipe_events")));
        Assert.Equal(act, await ScalarAsync<string>("select action from swipe_events"));
    }

    [Fact]
    public async Task Stop_closes_the_follow_and_the_entry()
    {
        var item = await ItemAsync(1);
        var follow = await ScalarAsync<long>("insert into follows (root_item_id, started_at, ends_at) values (@item, now(), now() + interval '7 days') returning id", new { item });
        await AddAsync("follow", new JsonObject { ["story"] = "S" }, item, follow);
        var svc = Service();
        var card = await ShowAsync(svc);

        var result = await ActAsync(svc, card, "stop");

        Assert.True(result.Ok);
        Assert.Equal("closed", await ScalarAsync<string>("select status from follows where id = @follow", new { follow }));
        Assert.Equal(1, await DoneCountAsync());
    }

    [Fact]
    public async Task Open_on_a_follow_card_leaves_the_entry_open()
    {
        await AddAsync("follow", new JsonObject { ["story"] = "S" }, await ItemAsync(1), null);
        var svc = Service();
        var card = await ShowAsync(svc);

        Assert.True((await ActAsync(svc, card, "open")).Ok);

        Assert.Equal(0, await DoneCountAsync());
        Assert.Equal("open", await ScalarAsync<string>("select action from swipe_events"));
    }

    [Theory]
    [InlineData("pick_a", true)]
    [InlineData("pick_b", false)]
    public async Task A_duel_pick_answers_it_and_is_done(string act, bool firstWins)
    {
        var (a, b) = (await ItemAsync(1), await ItemAsync(2));
        var duel = await new DuelStore(pg.Db).CreateAsync(a, b, DateTimeOffset.UtcNow, default);
        await AddAsync("duel", new JsonObject { ["a"] = a, ["b"] = b }, refId: duel);
        var svc = Service();
        var card = await ShowAsync(svc);

        var result = await ActAsync(svc, card, act);
        var again = await ActAsync(svc, card, act == "pick_a" ? "pick_b" : "pick_a");

        Assert.True(result.Ok);
        Assert.Equal(firstWins ? a : b, await ScalarAsync<long>("select winner from duels where id = @duel", new { duel }));
        Assert.Equal(1, await DoneCountAsync());
        Assert.Equal(new ActResult(true, "Already answered"), again);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("review")]
    [InlineData("autopsy")]
    [InlineData("notice")]
    public async Task Info_cards_only_open_and_stay_open(string kind)
    {
        await AddAsync(kind, new JsonObject { ["text"] = "t", ["html"] = "h", ["services"] = "s", ["running"] = "r", ["version"] = "v" });
        var svc = Service();
        var card = await ShowAsync(svc);

        var nothing = await ActAsync(svc, card, "like");
        var opened = await ActAsync(svc, card, "open");

        Assert.Equal(new ActResult(false, "Nothing to do."), nothing);
        Assert.True(opened.Ok);
        Assert.Equal(0, await DoneCountAsync());
        Assert.Equal(0, await ScalarAsync<long>("select count(*) from votes"));
    }

    [Fact]
    public async Task A_digest_card_acts_like_an_item_and_logs_the_digest_kind()
    {
        var item = await ItemAsync(1);
        await SqlAsync("insert into digests (local_date, status, item_ids, sent_at) values ('2026-10-19', 'sent', @ids, @at)", new { ids = new[] { item }, at = DateTime.UtcNow.AddHours(-1) });
        var svc = Service();
        var card = await ShowAsync(svc);

        var result = await ActAsync(svc, card, "like");

        Assert.Equal("digest", card.Kind);
        Assert.True(result.Ok);
        Assert.Equal(1, await ScalarAsync<int>("select value::int from votes where item_id = @item", new { item }));
        Assert.Equal(("digest", item), (await ScalarAsync<string>("select kind from swipe_events"), await ScalarAsync<long>("select ref_id from swipe_events")));
    }

    private SwipeService IdeaService(bool planeDown, out StubHandler plane)
    {
        var options = Options.Create(new FeedEaterOptions { Plane = new PlaneOptions { BaseUrl = "http://plane/", Token = "t", Workspace = "w", FallbackProject = "FEED" } });
        plane = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/workspaces/w/projects/" => StubHandler.Json("""{"results":[{"id":"p-lab","identifier":"LAB"}]}"""),
            var p when p.EndsWith("/intake-issues/", StringComparison.Ordinal) => planeDown
                ? StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable)
                : StubHandler.Json("""{"issue":"issue-1"}""", HttpStatusCode.Created),
            _ => StubHandler.Json("{}", HttpStatusCode.NotFound),
        });
        var sp = app!.Services;
        var decisions = new IdeaDecisions(new InboxStore(pg.Db), new WeeklyIdeaStore(pg.Db), new PlaneClient(plane.Client("http://plane/"), options), TimeProvider.System);
        var cards = new InboxCards(new InboxStore(pg.Db), sp.GetRequiredService<ItemStore>(), new DuelStore(pg.Db), sp.GetRequiredService<FeedEater.Follow.StoryFollower>(), TimeProvider.System, decisions);
        return ActivatorUtilities.CreateInstance<SwipeService>(sp, cards);
    }

    private Task<long> IdeaAsync() => AddAsync("idea", new JsonObject
    {
        ["week"] = "2026-W43",
        ["title"] = "Cache it",
        ["project"] = "LAB",
        ["html"] = "<p>x</p>",
        ["url"] = "https://example.com/i",
        ["sourceTitle"] = "S",
        ["signal"] = "s",
        ["why"] = "w",
        ["where"] = "src",
        ["effort"] = "S",
    });

    [Fact]
    public async Task Keep_files_the_idea_and_Drop_remembers_it()
    {
        Service();
        await IdeaAsync();
        var svc = IdeaService(false, out var plane);
        var card = await ShowAsync(svc);

        var kept = await ActAsync(svc, card, "keep");
        var again = await ActAsync(svc, card, "drop");

        Assert.Equal(new ActResult(true, "Filed in LAB"), kept);
        Assert.Equal(new ActResult(true, "Already decided"), again);
        Assert.Contains(plane.Calls, c => c.Uri.EndsWith("/intake-issues/", StringComparison.Ordinal));
        Assert.Equal("issue-1", await ScalarAsync<string>("select plane_issue_id from weekly_ideas"));
        Assert.Equal(1, await DoneCountAsync());
    }

    [Fact]
    public async Task Drop_marks_the_idea_dropped_without_calling_Plane()
    {
        Service();
        await IdeaAsync();
        var svc = IdeaService(false, out var plane);
        var card = await ShowAsync(svc);

        var result = await ActAsync(svc, card, "drop");

        Assert.Equal(new ActResult(true, "Dropped"), result);
        Assert.Equal("dropped", await ScalarAsync<string>("select outcome from weekly_ideas"));
        Assert.DoesNotContain(plane.Calls, c => c.Uri.EndsWith("/intake-issues/", StringComparison.Ordinal));
        Assert.Equal(1, await DoneCountAsync());
    }

    [Fact]
    public async Task Keep_while_Plane_is_down_fails_and_leaves_the_idea_open()
    {
        Service();
        await IdeaAsync();
        var svc = IdeaService(true, out _);
        var card = await ShowAsync(svc);

        var result = await ActAsync(svc, card, "keep");

        Assert.False(result.Ok);
        Assert.StartsWith("Not filed: ", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, await DoneCountAsync());
        Assert.Equal(0, await ScalarAsync<long>("select count(*) from swipe_events"));
    }

    [Fact]
    public async Task Keep_without_ideas_registered_says_so()
    {
        await IdeaAsync();
        var svc = Service();
        var card = await ShowAsync(svc);

        Assert.Equal(new ActResult(false, "Ideas are off"), await ActAsync(svc, card, "keep"));
        Assert.Equal(0, await DoneCountAsync());
    }
}
