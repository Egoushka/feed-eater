using FeedEater.Storage;

namespace FeedEater.WeeklyIdeas;

public static class ImportNotesCommand
{
    /// <summary>
    /// Entry for <c>dotnet FeedEater.dll import-notes &lt;folder&gt;</c>: reads every <c>*.md</c> research notes file under the folder
    /// into the archive, one item per finding, leaving out findings whose URL is already there. Safe to re-run. Applies the migrations first, like the service does.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var folder])
        {
            await Console.Error.WriteLineAsync("Usage: import-notes <folder>");
            return 2;
        }

        if (!Directory.Exists(folder))
        {
            await Console.Error.WriteLineAsync($"No such folder: {folder}");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["FeedEater:RunJobs"] = "false";
        builder.Services.AddFeedEater(builder.Configuration);
        using var host = builder.Build();
        host.Services.GetRequiredService<DatabaseMigrator>().Run();
        var importer = new ResearchNotesImporter(host.Services.GetRequiredService<ItemStore>(), TimeProvider.System);
        var counts = await importer.ImportAsync(folder, CancellationToken.None);
        Console.WriteLine($"Read {counts.Files} files: imported {counts.Imported} findings, {counts.AlreadyThere} were already there, skipped {counts.Skipped} without a web URL.");
        return 0;
    }
}
