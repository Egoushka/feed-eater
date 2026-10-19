using System.Net;
using Dapper;
using FeedEater.Inbox;
using FeedEater.Library;
using FeedEater.Signals;
using FeedEater.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class LibraryTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private LibraryService Service(bool mirror = false, HttpStatusCode karakeepStatus = HttpStatusCode.OK, StubHandler? stub = null) => new(
        new LibraryStore(pg.Db), new ItemStore(pg.Db), new FeedbackStore(pg.Db),
        new KarakeepClient((stub ?? new StubHandler((_, _) => StubHandler.Json("""{"id":"bm-7"}""", karakeepStatus))).Client("http://k/")),
        Options.Create(new FeedEaterOptions { Karakeep = new KarakeepOptions { BaseUrl = "http://k/", Token = "t", Mirror = mirror } }),
        new FixedTime(Now), NullLogger<LibraryService>.Instance);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<T> QueryAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no row");
    }

    private Task<long> NewItemAsync(string title = "t") => Seed.ItemAsync(pg, 1, title, TestVectors.OneHot(0));

    // --- store ---

    [Fact]
    public async Task Adding_twice_answers_false_the_second_time_and_removing_unsaves()
    {
        var store = new LibraryStore(pg.Db);
        var id = await NewItemAsync();

        Assert.True(await store.AddAsync(id, "ui", Now, default));
        Assert.False(await store.AddAsync(id, "api", Now.AddHours(1), default));
        Assert.Equal("ui", await QueryAsync<string>("select source from saved where item_id = @id", new { id }));
        Assert.True(await store.IsSavedAsync(id, default));

        Assert.True(await store.RemoveAsync(id, default));
        Assert.False(await store.IsSavedAsync(id, default));
        Assert.False(await store.RemoveAsync(id, default));
    }

    [Fact]
    public async Task The_list_filters_by_read_state_and_pages_newest_save_first()
    {
        var store = new LibraryStore(pg.Db);
        var a = await NewItemAsync("a");
        var b = await NewItemAsync("b");
        var c = await NewItemAsync("c");
        await store.AddAsync(a, "ui", Now, default);
        await store.AddAsync(b, "ui", Now.AddMinutes(1), default);
        await store.AddAsync(c, "ui", Now.AddMinutes(2), default);
        await store.MarkReadAsync(b, true, Now.AddHours(1), default);

        Assert.Equal([c, a], (await store.ListAsync(LibraryState.Unread, null, 10, default)).Select(r => r.ItemId));
        Assert.Equal([b], (await store.ListAsync(LibraryState.Read, null, 10, default)).Select(r => r.ItemId));
        Assert.Equal([c, b, a], (await store.ListAsync(LibraryState.All, null, 10, default)).Select(r => r.ItemId));

        var first = await store.ListAsync(LibraryState.All, null, 2, default);
        Assert.Equal([c, b], first.Select(r => r.ItemId));
        Assert.Equal([a], (await store.ListAsync(LibraryState.All, first[^1].ItemId, 2, default)).Select(r => r.ItemId));

        await store.MarkReadAsync(b, false, Now, default);
        Assert.Empty(await store.ListAsync(LibraryState.Read, null, 10, default));
    }

    [Fact]
    public async Task A_row_carries_the_note_the_summary_and_the_enrichment_flags()
    {
        var store = new LibraryStore(pg.Db);
        var id = await NewItemAsync("title");
        await Seed.ReadAsync(pg, id, "p", "article");
        await store.AddAsync(id, "share", Now, default);
        await store.SetNoteAsync(id, "for the weekend", default);

        var row = (await store.ListAsync(LibraryState.All, null, 10, default)).Single();
        Assert.Equal("for the weekend", row.Note);
        Assert.Equal("share", row.Source);
        Assert.NotNull(row.Summary);
        Assert.False(row.Enriched);
        Assert.False(row.FullText);

        await store.RecordAttemptAsync(id, true, default);
        Assert.True((await store.ListAsync(LibraryState.All, null, 10, default)).Single().Enriched);
        Assert.False(await store.SetNoteAsync(999, "x", default));
    }

    [Fact]
    public async Task Finding_by_canonical_url_skips_duplicates_and_takes_the_lowest_id()
    {
        var items = new ItemStore(pg.Db);
        await items.UpsertFeedAsync(new Feed(1, "Feed 1", null, null), default);
        var first = await items.InsertAsync(New(1, "https://a.example/x", "first story here"), default);
        var second = await items.InsertAsync(New(2, "https://a.example/x", "other story there"), default);

        Assert.Equal(first, await items.FindByCanonicalAsync("https://a.example/x", default));
        Assert.NotNull(second);
        Assert.Null(await items.FindByCanonicalAsync("https://a.example/none", default));

        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set duplicate_of = null where id = @second; update items set duplicate_of = @second where id = @first", new { first, second });
        }

        Assert.Equal(second, await items.FindByCanonicalAsync("https://a.example/x", default));
    }

    [Fact]
    public async Task Unread_since_leaves_out_read_items_recent_saves_and_recently_resurfaced_ones()
    {
        var store = new LibraryStore(pg.Db);
        var inbox = new InboxStore(pg.Db);
        var old = await NewItemAsync("old");
        var read = await NewItemAsync("read");
        var resurfaced = await NewItemAsync("resurfaced");
        var longAgo = await NewItemAsync("long ago resurfaced");
        var fresh = await NewItemAsync("fresh");
        foreach (var (id, days) in new[] { (old, 20), (read, 21), (resurfaced, 22), (longAgo, 23), (fresh, 1) })
        {
            await store.AddAsync(id, "ui", Now.AddDays(-days), default);
        }

        await store.MarkReadAsync(read, true, Now, default);
        await inbox.AddAsync(new InboxEntry("r1", InboxKinds.Resurface, resurfaced), DateTimeOffset.UtcNow.AddDays(-5), default);
        await inbox.AddAsync(new InboxEntry("r2", InboxKinds.Resurface, longAgo), DateTimeOffset.UtcNow.AddDays(-40), default);

        Assert.Equal([longAgo, old], await store.UnreadSinceAsync(Now.AddDays(-14), 10, default));
    }

    [Fact]
    public async Task Pending_enrichment_is_oldest_first_and_stops_after_five_attempts()
    {
        var store = new LibraryStore(pg.Db);
        var newer = await NewItemAsync("newer");
        var older = await NewItemAsync("older");
        var done = await NewItemAsync("done");
        var spent = await NewItemAsync("spent");
        await store.AddAsync(newer, "ui", Now, default);
        await store.AddAsync(older, "ui", Now.AddDays(-1), default);
        await store.AddAsync(done, "ui", Now.AddDays(-2), default);
        await store.AddAsync(spent, "ui", Now.AddDays(-3), default);
        await store.RecordAttemptAsync(done, true, default);
        for (var i = 0; i < 4; i++)
        {
            await store.RecordAttemptAsync(spent, false, default);
        }

        Assert.Equal([spent, older, newer], await store.PendingEnrichmentAsync(10, default));

        await store.RecordAttemptAsync(spent, false, default);
        Assert.Equal([older, newer], await store.PendingEnrichmentAsync(10, default));
        Assert.Equal([older], await store.PendingEnrichmentAsync(1, default));
    }

    private static NewItem New(long entry, string url, string title) =>
        new(entry, 1, url, url, FeedEater.Text.TitleHash.Of(title), title, DateTime.UtcNow, "body");

    // --- service ---

    [Fact]
    public async Task Saving_an_item_writes_the_row_and_a_plain_up_vote_and_a_second_save_changes_nothing()
    {
        var id = await NewItemAsync();
        var feedback = new FeedbackStore(pg.Db);

        var first = await Service().SaveAsync(id, "ui", default);
        Assert.Equal(new SaveResult(SaveStatus.Saved, id), first);
        Assert.Equal("ui", await QueryAsync<string>("select source from saved where item_id = @id", new { id }));
        Assert.Equal(((short)1, 1f), await feedback.VoteOfAsync(id, default));

        var votedAt = await QueryAsync<DateTime>("select at from votes where item_id = @id", new { id });
        await Task.Delay(20);
        var second = await Service().SaveAsync(id, "api", default);

        Assert.Equal(SaveStatus.AlreadySaved, second.Status);
        Assert.Equal(1, await QueryAsync<int>("select count(*)::int from saved"));
        Assert.Equal(votedAt, await QueryAsync<DateTime>("select at from votes where item_id = @id", new { id }));
        Assert.Equal(SaveStatus.NoItem, (await Service().SaveAsync(9999, "ui", default)).Status);
    }

    [Fact]
    public async Task An_import_writes_no_vote()
    {
        var id = await NewItemAsync();

        Assert.Equal(SaveStatus.Saved, (await Service().SaveAsync(id, "import", default)).Status);
        Assert.Null(await new FeedbackStore(pg.Db).VoteOfAsync(id, default));
    }

    [Fact]
    public async Task A_new_url_becomes_a_feed_less_item_and_the_same_url_again_reuses_it()
    {
        var first = await Service().SaveUrlAsync("https://blog.example.org/post?id=1", "A post", "<p>hello</p>", "share", default);
        Assert.Equal(SaveStatus.Saved, first.Status);

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var row = await c.QuerySingleAsync("select feed_id, source_key, content, title from items where id = @id", new { id = first.ItemId });
        Assert.Null(row.feed_id);
        Assert.Equal("share:https://blog.example.org/post?id=1", row.source_key);
        Assert.Equal("hello", row.content);
        Assert.Equal("A post", row.title);

        var again = await Service().SaveUrlAsync("http://www.blog.example.org/post?id=1&utm_source=x", null, null, "ui", default);
        Assert.Equal(new SaveResult(SaveStatus.AlreadySaved, first.ItemId), again);
        Assert.Equal(1, await QueryAsync<int>("select count(*)::int from items"));
        Assert.Equal(1, await QueryAsync<int>("select count(*)::int from saved"));
    }

    [Fact]
    public async Task A_url_without_a_title_is_named_by_its_host_and_two_such_urls_stay_separate_items()
    {
        var a = await Service().SaveUrlAsync("https://example.com/one", null, null, "share", default);
        var b = await Service().SaveUrlAsync("https://example.com/two", null, null, "share", default);

        Assert.NotEqual(a.ItemId, b.ItemId);
        Assert.Equal("example.com", await QueryAsync<string>("select title from items where id = @id", new { id = a.ItemId }));
        Assert.Equal(0, await QueryAsync<int>("select count(*)::int from items where duplicate_of is not null"));
    }

    [Fact]
    public async Task A_url_equal_to_a_feed_item_saves_that_item_without_a_new_row()
    {
        var id = (await new ItemStore(pg.Db).InsertAsync(New(1, "https://news.example.net/story", "a feed story"), default))!.Value;

        var result = await Service().SaveUrlAsync("https://news.example.net/story?utm_medium=mail", "other title", null, "share", default);

        Assert.Equal(new SaveResult(SaveStatus.Saved, id), result);
        Assert.Equal(1, await QueryAsync<int>("select count(*)::int from items"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com/x")]
    public async Task Urls_that_are_not_absolute_http_are_refused(string url)
    {
        Assert.Equal(new SaveResult(SaveStatus.BadUrl, null), await Service().SaveUrlAsync(url, null, null, "share", default));
        Assert.Equal(0, await QueryAsync<int>("select count(*)::int from items"));
    }

    [Fact]
    public async Task A_url_over_2048_characters_is_refused()
    {
        var url = "https://example.com/" + new string('a', 2040);
        Assert.Equal(SaveStatus.BadUrl, (await Service().SaveUrlAsync(url, null, null, "share", default)).Status);
    }

    [Fact]
    public async Task A_failing_mirror_does_not_fail_the_save()
    {
        var id = await NewItemAsync();

        Assert.Equal(SaveStatus.Saved, (await Service(mirror: true, karakeepStatus: HttpStatusCode.InternalServerError).SaveAsync(id, "ui", default)).Status);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Null(await c.ExecuteScalarAsync<string?>("select karakeep_id from saved where item_id = @id", new { id }));
    }

    [Fact]
    public async Task A_working_mirror_stores_the_bookmark_id_and_a_disabled_one_makes_no_call()
    {
        var mirrored = await NewItemAsync("mirrored");
        var plain = await NewItemAsync("plain");
        var stub = new StubHandler((_, _) => StubHandler.Json("""{"id":"bm-7"}"""));

        await Service(mirror: true, stub: stub).SaveAsync(mirrored, "ui", default);
        await Service(mirror: false, stub: stub).SaveAsync(plain, "ui", default);

        Assert.Equal("bm-7", await QueryAsync<string>("select karakeep_id from saved where item_id = @mirrored", new { mirrored }));
        Assert.Single(stub.Calls);
    }

    [Fact]
    public async Task Unsaving_removes_the_row_and_keeps_the_vote()
    {
        var id = await NewItemAsync();
        var service = Service();
        await service.SaveAsync(id, "ui", default);

        Assert.True(await service.UnsaveAsync(id, default));
        Assert.False(await new LibraryStore(pg.Db).IsSavedAsync(id, default));
        Assert.NotNull(await new FeedbackStore(pg.Db).VoteOfAsync(id, default));
        Assert.False(await service.UnsaveAsync(id, default));
    }
}
