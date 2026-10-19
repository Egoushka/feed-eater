using FeedEater.Signals;
using FeedEater.Storage;
using Microsoft.Extensions.Options;

namespace FeedEater.Library;

public sealed record ImportCounts(int Saved, int AlreadySaved, int SkippedNonLink, int Failed);

public sealed class ImportKarakeepCommand(KarakeepClient karakeep, LibraryService library, LibraryStore store)
{
    /// <summary>
    /// Entry for <c>dotnet FeedEater.dll import-karakeep</c>: saves every link bookmark once, with source "import" and no vote
    /// (bookmarks already count as likes). Safe to re-run. Applies the migrations first, like the service does.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0)
        {
            await Console.Error.WriteLineAsync("Usage: import-karakeep");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["FeedEater:RunJobs"] = "false";
        builder.Services.AddFeedEater(builder.Configuration);
        using var host = builder.Build();
        if (!host.Services.GetRequiredService<IOptions<FeedEaterOptions>>().Value.Karakeep.Enabled)
        {
            await Console.Error.WriteLineAsync("import-karakeep needs FeedEater:Karakeep:BaseUrl and FeedEater:Karakeep:Token.");
            return 2;
        }

        host.Services.GetRequiredService<DatabaseMigrator>().Run();
        try
        {
            var command = new ImportKarakeepCommand(host.Services.GetRequiredService<KarakeepClient>(),
                host.Services.GetRequiredService<LibraryService>(), host.Services.GetRequiredService<LibraryStore>());
            var counts = await command.ImportAsync(CancellationToken.None);
            Console.WriteLine($"Saved {counts.Saved}, {counts.AlreadySaved} were already saved, skipped {counts.SkippedNonLink} that are not links, {counts.Failed} failed.");
            return counts.Failed > 0 ? 1 : 0;
        }
        catch (HttpRequestException e)
        {
            await Console.Error.WriteLineAsync($"Karakeep: {e.Message}");
            return 1;
        }
    }

    public async Task<ImportCounts> ImportAsync(CancellationToken ct)
    {
        int saved = 0, already = 0, skipped = 0, failed = 0;
        string? cursor = null;
        do
        {
            var (bookmarks, next) = await karakeep.PageAsync(cursor, ct);
            foreach (var bookmark in bookmarks)
            {
                if (bookmark.Url is null)
                {
                    skipped++;
                    continue;
                }

                var result = await library.SaveUrlAsync(bookmark.Url, bookmark.Title, null, LibraryService.ImportSource, ct);
                switch (result.Status)
                {
                    case SaveStatus.Saved or SaveStatus.AlreadySaved when result.ItemId is { } id:
                        await store.SetKarakeepIdAsync(id, bookmark.Id, ct);
                        _ = result.Status == SaveStatus.Saved ? saved++ : already++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }

            cursor = next;
        }
        while (cursor is not null);

        return new ImportCounts(saved, already, skipped, failed);
    }
}
