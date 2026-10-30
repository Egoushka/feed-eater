using FeedEater.Storage;

namespace FeedEater.PriorArt.Peers;

/// <summary>
/// <c>dotnet FeedEater.dll peer-harvest [--repo owner/name]</c>: without <c>--repo</c>, harvests the configured peer set
/// (<c>PeerHarvest:SetUrl</c>), one repo at a time.
/// </summary>
public static class PeerHarvestCommand
{
    private const string Usage = "Usage:\n  peer-harvest [--repo owner/name]   fingerprint and card either one repo or the configured peer set";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["FeedEater:RunJobs"] = "false";
        builder.Services.AddFeedEater(builder.Configuration);
        using var host = builder.Build();
        host.Services.GetRequiredService<DatabaseMigrator>().Run();
        return await RunAsync(args, host.Services, output, error);
    }

    internal static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output, TextWriter error)
    {
        string? repo = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repo" && i + 1 < args.Length)
            {
                repo = args[++i];
            }
            else
            {
                await error.WriteLineAsync($"unknown option {args[i]}.\n{Usage}");
                return 2;
            }
        }

        var job = services.GetRequiredService<PeerHarvestJob>();
        try
        {
            var reports = repo is null ? await job.HarvestSetAsync(CancellationToken.None) : [await job.HarvestAsync(repo, CancellationToken.None)];
            foreach (var r in reports)
            {
                await output.WriteLineAsync(
                    $"{r.Repo}: {(r.Fingerprinted ? $"fingerprinted ({r.Packages} packages)" : "fingerprint unchanged")}, {(r.Carded ? "card written" : "card unchanged or skipped")}");
                foreach (var s in r.Skipped)
                {
                    await error.WriteLineAsync($"{r.Repo}: {s}");
                }
            }

            return 0;
        }
        catch (InvalidOperationException e)
        {
            await error.WriteLineAsync(e.Message);
            return 2;
        }
        catch (ArgumentException e)
        {
            await error.WriteLineAsync(e.Message);
            return 2;
        }
    }
}
