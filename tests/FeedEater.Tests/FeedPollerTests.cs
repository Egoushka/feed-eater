using System.Net;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Sources;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FeedPollerTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SourceKit.Now;

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Row
    {
        public string SourceKey { get; init; } = "";
        public long? MinifluxEntryId { get; init; }
        public string Title { get; init; } = "";
        public string Content { get; init; } = "";
        public DateTime PublishedAt { get; init; }
        public long? DuplicateOf { get; init; }
        public long Id { get; init; }
        public long? FeedId { get; init; }
    }

    private async Task<List<Row>> RowsAsync()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.QueryAsync<Row>("select id, source_key, miniflux_entry_id, title, content, published_at, duplicate_of, feed_id from items order by id")).ToList();
    }

    private async Task<FeedState> StateAsync(long id) => (await new FeedStore(pg.Db).GetAsync(id, default))!;

    [Fact]
    public async Task A_first_fetch_stores_entries_with_source_keys_dates_plain_text_and_the_feed_state()
    {
        var body = SourceKit.Rss(("g1", "First post", Now.AddHours(-3), "<p>Hello <b>world</b></p>"), ("g2", "Second post", Now.AddDays(-1), "text"));
        var kit = new SourceKit(pg, _ => SourceKit.Xml(body, etag: "\"v1\""));
        var feed = await kit.AddFeedAsync("https://a.example/feed", "https://a.example/feed");

        Assert.Equal(2, await kit.Poller.RunAsync(default));

        var rows = await RowsAsync();
        Assert.Equal(["Second post", "First post"], rows.Select(r => r.Title));   // oldest first, so ids follow time
        var first = rows.Single(r => r.Title == "First post");
        Assert.Equal(FeedPoller.SourceKey(feed.Id, "g1"), first.SourceKey);
        Assert.Matches($"^{feed.Id}:[0-9a-f]{{64}}$", first.SourceKey);
        Assert.Null(first.MinifluxEntryId);
        Assert.Equal("Hello world", first.Content);
        Assert.Equal(Now.UtcDateTime.AddHours(-3), first.PublishedAt);
        var state = await StateAsync(feed.Id);
        Assert.Equal("Feed title", state.Title);   // the placeholder title took the feed's own
        Assert.Equal("https://site.example/", state.SiteUrl);
        Assert.Equal("\"v1\"", state.Etag);
        Assert.Equal(Now.UtcDateTime, state.LastFetchedAt);
        Assert.Equal(Now.UtcDateTime.AddMinutes(30), state.NextFetchAt);
        Assert.Equal(0, state.FailCount);
    }

    [Fact]
    public async Task The_title_the_user_gave_is_kept()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Xml(SourceKit.Rss(("g1", "Post", Now, "x"))));
        var feed = await kit.AddFeedAsync("https://a.example/feed", "My name for it");

        await kit.Poller.RunAsync(default);

        Assert.Equal("My name for it", (await StateAsync(feed.Id)).Title);
    }

    [Fact]
    public async Task An_entry_without_a_date_is_published_when_first_seen_and_a_future_date_is_clamped()
    {
        var body = SourceKit.Rss(("g1", "Undated", null, "x"), ("g2", "From the future", Now.AddDays(30), "y"));
        var kit = new SourceKit(pg, _ => SourceKit.Xml(body));
        await kit.AddFeedAsync("https://a.example/feed");

        await kit.Poller.RunAsync(default);

        Assert.All(await RowsAsync(), r => Assert.Equal(Now.UtcDateTime, r.PublishedAt));
    }

    [Fact]
    public async Task The_first_fetch_of_a_feed_keeps_only_the_backfill_window_and_later_fetches_have_none()
    {
        var old = ("g-old", "Ancient", (DateTimeOffset?)Now.AddDays(-15), "x");
        var edge = ("g-edge", "Just inside", (DateTimeOffset?)Now.AddDays(-13), "x");
        var body = SourceKit.Rss(edge, old);
        var kit = new SourceKit(pg, _ => SourceKit.Xml(body));
        await kit.AddFeedAsync("https://a.example/feed");

        Assert.Equal(1, await kit.Poller.RunAsync(default));
        Assert.Equal(["Just inside"], (await RowsAsync()).Select(r => r.Title));

        // Same feed later: an old entry that only now appears is stored, because the window is for the first fetch only.
        body = SourceKit.Rss(edge, old, ("g-late", "Old but new here", Now.AddDays(-40), "x"));
        kit.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(2, await kit.Poller.RunAsync(default));
    }

    [Fact]
    public async Task BackfillDays_is_configurable()
    {
        var body = SourceKit.Rss(("g1", "Two days old", Now.AddDays(-2), "x"), ("g2", "Five days old", Now.AddDays(-5), "x"));
        var kit = new SourceKit(pg, _ => SourceKit.Xml(body), o => o.Source.BackfillDays = 3);
        await kit.AddFeedAsync("https://a.example/feed");

        await kit.Poller.RunAsync(default);

        Assert.Equal(["Two days old"], (await RowsAsync()).Select(r => r.Title));
    }

    [Fact]
    public async Task A_failed_first_fetch_does_not_end_the_backfill_window()
    {
        var up = false;
        var body = SourceKit.Rss(("g1", "Ancient", Now.AddDays(-30), "x"), ("g2", "Fresh", Now.AddHours(-1), "x"));
        var kit = new SourceKit(pg, _ => up ? SourceKit.Xml(body) : SourceKit.Status(HttpStatusCode.ServiceUnavailable));
        await kit.AddFeedAsync("https://a.example/feed");

        await kit.Poller.RunAsync(default);
        up = true;
        kit.Time.Advance(TimeSpan.FromHours(2));
        await kit.Poller.RunAsync(default);

        Assert.Equal(["Fresh"], (await RowsAsync()).Select(r => r.Title));
    }

    [Fact]
    public async Task The_validators_are_sent_back_and_a_304_stores_nothing_and_keeps_the_feed_healthy()
    {
        var sent = new List<string?>();
        var kit = new SourceKit(pg, r =>
        {
            sent.Add(r.Headers.IfNoneMatch.FirstOrDefault()?.Tag);
            return r.Headers.Contains("If-None-Match")
                ? SourceKit.Status(HttpStatusCode.NotModified)
                : SourceKit.Xml(SourceKit.Rss(("g1", "Post", Now.AddHours(-1), "x")), etag: "\"v1\"");
        });
        var feed = await kit.AddFeedAsync("https://a.example/feed");
        await kit.Poller.RunAsync(default);

        kit.Time.Advance(TimeSpan.FromMinutes(31));
        var added = await kit.Poller.RunAsync(default);

        Assert.Equal(0, added);
        Assert.Equal([null, "\"v1\""], sent);
        var state = await StateAsync(feed.Id);
        Assert.Equal("\"v1\"", state.Etag);
        Assert.Equal(Now.UtcDateTime.AddMinutes(31 + 30), state.NextFetchAt);
        Assert.Equal(0, state.FailCount);
        Assert.Single(await RowsAsync());
    }

    [Fact]
    public async Task A_feed_is_fetched_only_when_it_is_due()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Xml(SourceKit.Rss(("g1", "Post", Now, "x"))));
        await kit.AddFeedAsync("https://a.example/feed");

        await kit.Poller.RunAsync(default);
        kit.Time.Advance(TimeSpan.FromMinutes(29));
        await kit.Poller.RunAsync(default);
        Assert.Single(kit.Stub.Calls);

        kit.Time.Advance(TimeSpan.FromMinutes(2));
        await kit.Poller.RunAsync(default);
        Assert.Equal(2, kit.Stub.Calls.Count);
    }

    [Fact]
    public async Task The_same_entries_again_are_not_stored_twice_even_when_the_title_changes()
    {
        var title = "Original";
        var kit = new SourceKit(pg, _ => SourceKit.Xml(SourceKit.Rss(("g1", title, Now.AddHours(-1), "x"))));
        await kit.AddFeedAsync("https://a.example/feed");
        await kit.Poller.RunAsync(default);

        title = "Edited title";
        kit.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(0, await kit.Poller.RunAsync(default));

        Assert.Equal(["Original"], (await RowsAsync()).Select(r => r.Title));
    }

    [Fact]
    public async Task Failures_back_off_by_doubling_up_to_a_day_and_a_success_resets_the_count()
    {
        var failing = true;
        var kit = new SourceKit(pg, _ => failing ? SourceKit.Status(HttpStatusCode.InternalServerError) : SourceKit.Xml(SourceKit.Rss(("g1", "Post", null, "x"))));
        var feed = await kit.AddFeedAsync("https://a.example/feed");

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            kit.Time.Advance(TimeSpan.FromDays(2));
            await kit.Poller.PollAsync(await StateAsync(feed.Id), default);
            var state = await StateAsync(feed.Id);
            Assert.Equal(i + 1, state.FailCount);
            Assert.Equal("HTTP 500", state.LastError);
            waits.Add(state.NextFetchAt!.Value - kit.Time.GetUtcNow().UtcDateTime);
        }

        Assert.Equal([1, 2, 4, 8, 16, 24, 24, 24], waits.Select(w => (int)w.TotalHours));
        Assert.Null((await StateAsync(feed.Id)).LastFetchedAt);   // never fetched well

        failing = false;
        kit.Time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await kit.Poller.PollAsync(await StateAsync(feed.Id), default));
        var healed = await StateAsync(feed.Id);
        Assert.Equal(0, healed.FailCount);
        Assert.Null(healed.LastError);
        Assert.Equal(kit.Time.GetUtcNow().UtcDateTime.AddMinutes(30), healed.NextFetchAt);
    }

    [Fact]
    public void The_delay_follows_the_options()
    {
        var o = new SourceOptions { FeedInterval = TimeSpan.FromMinutes(10), MaxBackoff = TimeSpan.FromHours(3) };

        Assert.Equal([20, 40, 80, 160, 180, 180], new[] { 1, 2, 3, 4, 5, 6 }.Select(n => (int)FeedPoller.Delay(o, n).TotalMinutes));
        Assert.Equal(TimeSpan.FromHours(3), FeedPoller.Delay(o, 10_000));
    }

    [Theory]
    [InlineData("https://a.example/feed", "HTTP 404", 404)]
    [InlineData("https://a.example/feed", "not an RSS, Atom or JSON feed", 200)]
    public async Task The_error_shown_for_a_feed_says_what_went_wrong(string url, string expected, int status)
    {
        var kit = new SourceKit(pg, _ => status == 200 ? SourceKit.Xml("<html><body>login</body></html>", "text/html") : SourceKit.Status((HttpStatusCode)status));
        var feed = await kit.AddFeedAsync(url);

        await kit.Poller.RunAsync(default);

        Assert.Equal(expected, (await StateAsync(feed.Id)).LastError);
    }

    [Fact]
    public async Task A_private_address_is_reported_with_the_setting_that_allows_it_and_an_allowed_one_is_fetched()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Xml(SourceKit.Rss(("g1", "Post", Now, "x"))), o => o.Source.AllowedHosts = ["10.0.0.9"]);
        var refused = await kit.AddFeedAsync("http://10.0.0.5/feed");
        var allowed = await kit.AddFeedAsync("http://10.0.0.9:1200/feed");

        await kit.Poller.RunAsync(default);

        Assert.Contains("Source:AllowedHosts", (await StateAsync(refused.Id)).LastError, StringComparison.Ordinal);
        Assert.Equal(0, (await StateAsync(allowed.Id)).FailCount);
        Assert.Single(await RowsAsync());
        Assert.Single(kit.Stub.Calls);
    }

    [Fact]
    public async Task One_failing_feed_does_not_stop_the_others()
    {
        var kit = new SourceKit(pg, r => r.RequestUri!.Host == "bad.example"
            ? SourceKit.Status(HttpStatusCode.NotFound)
            : SourceKit.Xml(SourceKit.Rss(("g1", "Post", Now.AddHours(-1), "x"))));
        var bad = await kit.AddFeedAsync("https://bad.example/feed", "Bad");
        var good = await kit.AddFeedAsync("https://good.example/feed", "Good");

        Assert.Equal(1, await kit.Poller.RunAsync(default));

        Assert.Equal(1, (await StateAsync(bad.Id)).FailCount);
        Assert.Equal(0, (await StateAsync(good.Id)).FailCount);
    }

    [Fact]
    public async Task The_same_guid_in_two_feeds_is_two_entries_and_the_same_article_is_marked_a_duplicate()
    {
        var kit = new SourceKit(pg, r => SourceKit.Xml(SourceKit.Rss(("shared", "A shared headline", Now.AddHours(-1), "x"))));
        var a = await kit.AddFeedAsync("https://a.example/feed", "A");
        var b = await kit.AddFeedAsync("https://b.example/feed", "B");

        Assert.Equal(2, await kit.Poller.RunAsync(default));

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Select(r => r.SourceKey).Distinct().Count());
        // The article URL is https://site.example/shared in both, so the second copy points at the first.
        Assert.Null(rows[0].DuplicateOf);
        Assert.Equal(rows[0].Id, rows[1].DuplicateOf);
        Assert.Equal([a.Id, b.Id], rows.Select(r => r.FeedId!.Value).Order());
    }

    [Fact]
    public async Task The_title_hash_rule_still_marks_the_same_headline_from_another_site()
    {
        var kit = new SourceKit(pg, r => SourceKit.Xml(
            $"<rss version=\"2.0\"><channel><title>t</title><item><title>Postgres 19 is released today</title><link>https://{r.RequestUri!.Host}/post</link>"
            + $"<guid>{r.RequestUri.Host}</guid><pubDate>{Now.AddHours(-1):R}</pubDate></item></channel></rss>"));
        await kit.AddFeedAsync("https://a.example/feed");
        await kit.AddFeedAsync("https://b.example/feed");

        await kit.Poller.RunAsync(default);

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].Id, rows[1].DuplicateOf);
    }

    [Fact]
    public async Task Source_keys_dedupe_across_both_source_kinds()
    {
        var items = new ItemStore(pg.Db);
        await items.UpsertFeedAsync(new Feed(7, "Miniflux feed", null, null), default);
        var miniflux = new NewItem(500, 7, "https://mf.example/a", "https://mf.example/a", "", "From Miniflux", Now.UtcDateTime, "text");

        var first = await items.InsertAsync(miniflux, default);
        var again = await items.InsertAsync(miniflux with { Title = "Edited" }, default);

        Assert.NotNull(first);
        Assert.Null(again);   // the entry id is the key
        var kit = new SourceKit(pg, _ => SourceKit.Xml(SourceKit.Rss(("g1", "From the built-in reader", Now.AddHours(-1), "x"))));
        var feed = await kit.AddFeedAsync("https://a.example/feed");
        await kit.Poller.RunAsync(default);
        var builtin = (await RowsAsync()).Single(r => r.MinifluxEntryId is null);
        Assert.Contains("mf:500", (await RowsAsync()).Select(r => r.SourceKey));
        Assert.StartsWith($"{feed.Id}:", builtin.SourceKey, StringComparison.Ordinal);

        // A built-in entry with an existing key is not stored, and one for a URL already stored points at it.
        var sameKey = new NewItem(null, feed.Id, "https://x.example/other", "https://x.example/other", "", "Same key", Now.UtcDateTime, "t", SourceKey: builtin.SourceKey);
        var sameUrl = new NewItem(null, feed.Id, "https://mf.example/a", "https://mf.example/a", "", "Same article", Now.UtcDateTime, "t", SourceKey: $"{feed.Id}:other");
        Assert.Null(await items.InsertAsync(sameKey, default));
        var dup = await items.InsertAsync(sameUrl, default);
        Assert.Equal(first, (await RowsAsync()).Single(r => r.Id == dup).DuplicateOf);
    }

    [Fact]
    public async Task Removing_a_feed_keeps_its_items_without_a_feed_and_stops_fetching_it()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Xml(SourceKit.Rss(("g1", "Post", Now.AddHours(-1), "x"))));
        var feed = await kit.AddFeedAsync("https://a.example/feed");
        await kit.Poller.RunAsync(default);

        Assert.True(await kit.Feeds.RemoveAsync(feed.Id, default));
        Assert.False(await kit.Feeds.RemoveAsync(feed.Id, default));
        kit.Time.Advance(TimeSpan.FromHours(1));
        await kit.Poller.RunAsync(default);

        Assert.Single(kit.Stub.Calls);
        Assert.Null((await RowsAsync()).Single().FeedId);
        Assert.Empty(await kit.Feeds.ListAsync(default));
    }

    [Fact]
    public async Task The_digest_note_names_feeds_that_failed_three_times_in_a_row_and_not_a_single_blip()
    {
        var kit = new SourceKit(pg, r => r.RequestUri!.Host == "bad.example" ? SourceKit.Status(HttpStatusCode.NotFound) : SourceKit.Xml(SourceKit.Rss(("g1", "Post", Now, "x"))));
        var bad = await kit.AddFeedAsync("https://bad.example/feed", "Broken <b>feed</b>");
        await kit.AddFeedAsync("https://good.example/feed", "Fine");

        await kit.Poller.RunAsync(default);
        Assert.Empty(await kit.Poller.NotesAsync(default));

        for (var i = 0; i < 2; i++)
        {
            kit.Time.Advance(TimeSpan.FromDays(2));
            await kit.Poller.RunAsync(default);
        }

        var note = Assert.Single(await kit.Poller.NotesAsync(default));
        Assert.Equal("Feed failing: Broken <b>feed</b>; some items may be missing", note);   // encoded later, by the header formatter
        Assert.Equal(3, (await StateAsync(bad.Id)).FailCount);
    }

    [Fact]
    public async Task The_note_lists_three_feeds_and_counts_the_rest_and_a_failed_loop_is_reported_too()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Status(HttpStatusCode.NotFound));
        for (var i = 1; i <= 5; i++)
        {
            await kit.AddFeedAsync($"https://f{i}.example/feed", $"Feed {i}");
        }

        for (var i = 0; i < 3; i++)
        {
            kit.Time.Advance(TimeSpan.FromDays(2));
            await kit.Poller.RunAsync(default);
        }

        kit.Health.Failed(FeedPoller.LoopName, "db down");
        var notes = await kit.Poller.NotesAsync(default);

        Assert.Equal(2, notes.Count);
        Assert.Contains("feed reader failed", notes[0], StringComparison.Ordinal);
        Assert.Equal("Feeds failing: Feed 1, Feed 2, Feed 3 and 2 more; some items may be missing", notes[1]);
    }

    [Fact]
    public async Task The_built_in_ingest_loop_fetches_feeds_embeds_the_items_and_never_calls_Miniflux()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        var kit = new SourceKit(pg, _ => SourceKit.Xml(SourceKit.Rss(("g1", "One", Now.AddHours(-2), "first"), ("g2", "Two", Now.AddHours(-1), "second"))));
        await kit.AddFeedAsync("https://a.example/feed");
        var miniflux = new StubHandler((_, _) => StubHandler.Json("""{"entries":[]}"""));
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var options = kit.Options;
        var fetcher = kit.Fetcher;
        using var ingestor = new Ingestor(
            new MinifluxClient(miniflux.Client("http://miniflux/")), kit.Items,
            new StoryClusterer(new ClusterStore(pg.Db), options, NullLogger<StoryClusterer>.Instance),
            new FeedEater.Fetch.FeedDiscoverer(new DiscoveryStore(pg.Db), kit.Items, fetcher, options, kit.Time, NullLogger<FeedEater.Fetch.FeedDiscoverer>.Instance),
            new PageEnricher(kit.Items, fetcher, new FeedEater.Fetch.HnClient(new StubHandler((_, _) => StubHandler.Json("{}")).Client("http://hn/")), options, kit.Time, NullLogger<PageEnricher>.Instance),
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            options, kit.Health, kit.Time, NullLogger<Ingestor>.Instance, kit.Poller);

        await ingestor.StartAsync(default);
        try
        {
            var embedded = 0;
            for (var i = 0; i < 200 && embedded < 2; i++)
            {
                await Task.Delay(50);
                await using var c = await pg.Db.DataSource.OpenConnectionAsync();
                embedded = await c.ExecuteScalarAsync<int>("select count(*)::int from items where embedding is not null");
            }

            Assert.Equal(2, embedded);
        }
        finally
        {
            await ingestor.StopAsync(default);
        }

        Assert.Empty(miniflux.Calls);
        Assert.False(kit.Health.IsDown(FeedPoller.LoopName));
        Assert.False(kit.Health.IsDown(Ingestor.LoopName));
    }

    [Fact]
    public async Task Migration_0010_gives_existing_miniflux_rows_their_source_key_and_starts_new_feed_ids_above_a_billion()
    {
        var name = $"migration_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Pooling = false }.ConnectionString;
        var target = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
        await using (var c = new NpgsqlConnection(admin))
        {
            await c.OpenAsync();
            await c.ExecuteAsync($"create database {name}");
        }

        try
        {
            var scripts = typeof(DatabaseMigrator).Assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
            async Task RunAsync(NpgsqlConnection c, string resource)
            {
                await using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(resource)!;
                await c.ExecuteAsync(await new StreamReader(stream).ReadToEndAsync());
            }

            await using var conn = new NpgsqlConnection(target);
            await conn.OpenAsync();
            foreach (var script in scripts.Where(s => !s.Contains("0010_", StringComparison.Ordinal)))
            {
                await RunAsync(conn, script);
            }

            await conn.ExecuteAsync("insert into feeds (id, title) values (3, 'Miniflux feed')");
            await conn.ExecuteAsync(
                """
                insert into items (miniflux_entry_id, feed_id, url, canonical_url, title_hash, title, published_at)
                values (5, 3, 'https://x.example/5', 'https://x.example/5', '', 'Five', now()),
                       (6, 3, 'https://x.example/6', 'https://x.example/6', '', 'Six', now()),
                       (null, 3, 'https://x.example/n', 'https://x.example/n', '', 'No entry id', now())
                """);

            await RunAsync(conn, scripts.Single(s => s.Contains("0010_", StringComparison.Ordinal)));

            var keys = (await conn.QueryAsync<string?>("select source_key from items order by id")).ToList();
            Assert.Equal(["mf:5", "mf:6", null], keys);
            Assert.True(await conn.ExecuteScalarAsync<long>("select nextval('feed_id_seq')") > 1_000_000_000);
            Assert.Equal(3, await conn.ExecuteScalarAsync<long>("select id from feeds"));
            await Assert.ThrowsAsync<PostgresException>(async () => await conn.ExecuteAsync("update items set source_key = 'mf:5' where miniflux_entry_id = 6"));
        }
        finally
        {
            await using var c = new NpgsqlConnection(admin);
            await c.OpenAsync();
            await c.ExecuteAsync($"drop database {name} with (force)");
        }
    }
}
