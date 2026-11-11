using Dapper;
using FeedEater.Storage;
using FeedEater.WeeklyIdeas;
using Microsoft.Extensions.Time.Testing;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ResearchNotesTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private const string Folder = "Fixtures/research-notes";

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private ResearchNotesImporter Importer() => new(new ItemStore(pg.Db), new FakeTimeProvider(Now));

    private async Task<long> ExistingItemAsync(string url)
    {
        var id = await Seed.ItemAsync(pg, 1, "From a feed", TestVectors.OneHot(1));
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update items set url = @url, canonical_url = @url where id = @id", new { url, id });
        return id;
    }

    [Fact]
    public async Task A_folder_of_three_notes_imports_each_finding_once_and_leaves_out_what_the_archive_has()
    {
        await ExistingItemAsync("https://already.example/post");

        var counts = await Importer().ImportAsync(Folder, default);

        Assert.Equal(new NotesImportCounts(Files: 3, Imported: 3, AlreadyThere: 1, Skipped: 1), counts);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var rows = (await c.QueryAsync<(string Title, string CanonicalUrl, DateTime PublishedAt, string Content, bool Backfilled, string SourceKey)>(
            "select title, canonical_url, published_at, content, backfilled, source_key from items where starts_with(source_key, 'research:') order by title")).ToList();
        Assert.Equal(["Miniflux keeps feeds archived", "Postgres full-text stemming", "Souin: HTTP cache middleware"], rows.Select(r => r.Title));
        Assert.All(rows, r => Assert.True(r.Backfilled));
        Assert.All(rows, r => Assert.Equal("research:" + r.CanonicalUrl, r.SourceKey));
        Assert.DoesNotContain("utm_source", rows[0].CanonicalUrl, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 10, 5), rows[0].PublishedAt.Date);    // the source date
        Assert.Equal(new DateTime(2026, 10, 15), rows[1].PublishedAt.Date);   // no source date: the day it was retrieved
        Assert.Contains("RFC 9111", rows[2].Content, StringComparison.Ordinal);
        Assert.Contains("an HTTP cache system", rows[2].Content, StringComparison.Ordinal);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from items where url like '%no-fact%' or url like 'ftp:%'"));
    }

    [Fact]
    public async Task Importing_the_folder_again_adds_nothing()
    {
        await Importer().ImportAsync(Folder, default);

        var again = await Importer().ImportAsync(Folder, default);

        Assert.Equal(new NotesImportCounts(Files: 3, Imported: 0, AlreadyThere: 4, Skipped: 1), again);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Equal(4, await c.ExecuteScalarAsync<int>("select count(*) from items"));
    }

    [Fact]
    public async Task Imported_findings_come_back_as_weekly_idea_leads()
    {
        await Importer().ImportAsync(Folder, default);

        var notes = await new WeeklyIdeaStore(pg.Db).NotesAsync(DateTimeOffset.UtcNow.AddDays(-7), 60, default);

        Assert.Equal(4, notes.Count);
        Assert.Contains(notes, n => n.Title == "Souin: HTTP cache middleware" && n.Url == "https://github.com/darkweak/souin" && n.Content.Contains("RFC 9111", StringComparison.Ordinal));
    }
}
