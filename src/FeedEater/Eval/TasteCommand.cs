using Microsoft.Extensions.Options;
using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Eval;

public static class TasteCommand
{
    /// <summary>Entry for <c>dotnet FeedEater.dll taste [--out file.md]</c>: reads votes, prints the offline report, changes nothing.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var output = args is ["--out", var path] ? path : null;
        if (args.Length > 0 && output is null)
        {
            await Console.Error.WriteLineAsync("Usage: taste [--out <file.md>]");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["FeedEater:RunJobs"] = "false";
        builder.Services.AddFeedEater(builder.Configuration);
        using var host = builder.Build();
        var data = await host.Services.GetRequiredService<FeedbackStore>().LabeledVectorsAsync(CancellationToken.None);
        var report = TasteReport.Render(data, host.Services.GetRequiredService<IOptions<FeedEaterOptions>>().Value.Taste);
        Console.WriteLine(report);
        if (output is not null)
        {
            await File.WriteAllTextAsync(output, report);
        }

        return 0;
    }
}
