using FeedEater.Storage;

namespace FeedEater.PriorArt.Peers;

/// <summary>
/// <c>dotnet FeedEater.dll peer-harvest [--repo owner/name]</c>: without <c>--repo</c>, harvests the configured peer set
/// (<c>PeerHarvest:SetUrl</c>), one repo at a time.
/// </summary>
public static class PeerHarvestCommand
{
    private const string Usage =
        "Usage:\n  peer-harvest [--repo owner/name] [--no-deep] [--force]\n" +
        "    fingerprint, card and (with PeerHarvest:ChargehandUrl set) deep-read either one repo or the configured peer set.\n" +
        "    --no-deep skips the chargehand read; --force reads again even without a new minor or major release.";

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
        var deep = true;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repo" && i + 1 < args.Length)
            {
                repo = args[++i];
            }
            else if (args[i] == "--no-deep")
            {
                deep = false;
            }
            else if (args[i] == "--force")
            {
                force = true;
            }
            else
            {
                await error.WriteLineAsync($"unknown option {args[i]}.\n{Usage}");
                return 2;
            }
        }

        var job = services.GetRequiredService<PeerHarvestJob>();
        var mode = new HarvestMode(deep, force);
        try
        {
            var reports = repo is null ? await job.HarvestSetAsync(mode, CancellationToken.None) : [await job.HarvestAsync(repo, mode, CancellationToken.None)];
            foreach (var r in reports)
            {
                await output.WriteLineAsync(
                    $"{r.Repo}: {(r.Fingerprinted ? $"fingerprinted ({r.Packages} packages)" : "fingerprint unchanged")}, {(r.Carded ? "card written" : "card unchanged or skipped")}, {Deep(r.Deep)}");
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

    private static string Deep(DeepReadResult? d) => d switch
    {
        null or { Read: false } => "deep read unchanged or skipped",
        _ => $"deep read: {d.Facts} facts, ${d.Usd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}" +
             (d.Merges is { } m ? $", features compared {m.Compared}, merged {m.Merged}, kept apart {m.Rejected}, deferred {m.Deferred}" : ""),
    };
}
