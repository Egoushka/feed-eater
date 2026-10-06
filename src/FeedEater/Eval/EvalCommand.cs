using FeedEater.Storage;

namespace FeedEater.Eval;

public static class EvalCommand
{
    /// <summary>Entry for <c>dotnet FeedEater.dll eval ...</c>: builds the services without starting the web host or any job.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var (settings, error) = EvalSettings.Parse(args);
        if (settings is null)
        {
            await Console.Error.WriteLineAsync(error);
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["FeedEater:RunJobs"] = "false";
        builder.Services.AddFeedEater(builder.Configuration);
        using var host = builder.Build();
        string report;
        try
        {
            report = await host.Services.GetRequiredService<EvalRunner>().RunAsync(settings, CancellationToken.None);
        }
        catch (CostUnknownException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        Console.WriteLine(report);
        if (settings.OutPath is not null)
        {
            await File.WriteAllTextAsync(settings.OutPath, report);
        }

        return 0;
    }
}
