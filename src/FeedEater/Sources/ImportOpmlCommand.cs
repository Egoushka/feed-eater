using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Sources;

public static class ImportOpmlCommand
{
    /// <summary>
    /// Entry for <c>dotnet FeedEater.dll import-opml &lt;file&gt;</c>: adds the file's feeds to the database and fetches nothing, so it
    /// can run beside the service, which picks them up at its next poll. Applies the migrations first, like the service does.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var path])
        {
            await Console.Error.WriteLineAsync("Usage: import-opml <file.opml>");
            return 2;
        }

        if (!File.Exists(path))
        {
            await Console.Error.WriteLineAsync($"No such file: {path}");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["FeedEater:RunJobs"] = "false";
        builder.Services.AddFeedEater(builder.Configuration);
        using var host = builder.Build();
        if (host.Services.GetRequiredService<IOptions<FeedEaterOptions>>().Value.Source.KindProblem is { } problem)
        {
            await Console.Error.WriteLineAsync(problem);
            return 2;
        }

        if (host.Services.GetService<FeedManager>() is not { } manager)
        {
            await Console.Error.WriteLineAsync("import-opml needs FeedEater:Source:Kind=builtin; with miniflux, subscribe in Miniflux.");
            return 2;
        }

        host.Services.GetRequiredService<DatabaseMigrator>().Run();
        var result = await manager.ImportAsync(await File.ReadAllTextAsync(path), CancellationToken.None);
        if (!result.Ok)
        {
            await Console.Error.WriteLineAsync(result.Error);
            return 1;
        }

        Console.WriteLine($"Imported {result.Added} feeds, {result.Existing} were already there.");
        return 0;
    }
}
