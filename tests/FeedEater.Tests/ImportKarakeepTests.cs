using Dapper;
using FeedEater.Library;
using FeedEater.Signals;
using FeedEater.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ImportKarakeepTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Page = """
        {"bookmarks":[
          {"id":"bm-new","createdAt":"2026-10-01T00:00:00Z","title":"New one","content":{"type":"link","url":"https://new.example/a"}},
          {"id":"bm-old","createdAt":"2026-10-02T00:00:00Z","title":"Old one","content":{"type":"link","url":"https://old.example/b?utm_source=x"}},
          {"id":"bm-text","createdAt":"2026-10-03T00:00:00Z","title":"A note","content":{"type":"text","text":"hello"}}
        ],"nextCursor":null}
        """;

    private (ImportKarakeepCommand Command, LibraryService Service) Build()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(Page));
        var client = new KarakeepClient(stub.Client("http://k/"));
        var store = new LibraryStore(pg.Db);
        var service = new LibraryService(store, new ItemStore(pg.Db), new FeedbackStore(pg.Db), client,
            Options.Create(new FeedEaterOptions()), TimeProvider.System, NullLogger<LibraryService>.Instance);
        return (new ImportKarakeepCommand(client, service, store), service);
    }

    private async Task<T> QueryAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no row");
    }

    [Fact]
    public async Task Saves_new_links_counts_the_saved_and_the_non_links_and_a_rerun_saves_nothing()
    {
        var (command, service) = Build();
        var existing = (await service.SaveUrlAsync("https://old.example/b", "Old one", null, "ui", default)).ItemId;

        var first = await command.ImportAsync(default);
        var second = await command.ImportAsync(default);

        Assert.Equal(new ImportCounts(Saved: 1, AlreadySaved: 1, SkippedNonLink: 1, Failed: 0), first);
        Assert.Equal(new ImportCounts(Saved: 0, AlreadySaved: 2, SkippedNonLink: 1, Failed: 0), second);
        Assert.Equal(2, await QueryAsync<int>("select count(*) from saved"));
        Assert.Equal("import", await QueryAsync<string>("select source from saved where karakeep_id = 'bm-new'"));
        Assert.Equal("bm-old", await QueryAsync<string>("select karakeep_id from saved where item_id = @existing", new { existing }));
        Assert.Equal(1, await QueryAsync<int>("select count(*) from votes"));
    }

    [Fact]
    public async Task Without_a_Karakeep_URL_and_key_the_command_exits_2_and_says_why()
    {
        var (oldError, error) = (Console.Error, new StringWriter());
        Console.SetError(error);
        Environment.SetEnvironmentVariable("ConnectionStrings__FeedEater", pg.ConnectionString);
        try
        {
            Assert.Equal(2, await ImportKarakeepCommand.RunAsync([]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__FeedEater", null);
            Console.SetError(oldError);
        }

        Assert.Contains("Karakeep:BaseUrl", error.ToString(), StringComparison.Ordinal);
    }
}
