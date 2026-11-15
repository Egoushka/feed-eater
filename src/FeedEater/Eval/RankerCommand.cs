using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Eval;

public static class RankerCommand
{
    /// <summary>Entry for <c>dotnet FeedEater.dll ranker</c>: prints hit rate per ranker, changes nothing.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0)
        {
            await Console.Error.WriteLineAsync("Usage: ranker");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["FeedEater:RunJobs"] = "false";
        builder.Services.AddFeedEater(builder.Configuration);
        using var host = builder.Build();
        var rows = await host.Services.GetRequiredService<FeedbackStore>().RankerRowsAsync(CancellationToken.None);
        Console.WriteLine(RankerReport.Render(rows));
        return 0;
    }
}
