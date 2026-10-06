using Npgsql;
using FeedEater.Llm;

namespace FeedEater.Setup;

/// <summary>The lines <c>doctor</c> prints: one per check, then what is still missing.</summary>
public static class DoctorReport
{
    public static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "ok",
        CheckStatus.Off => "off",
        CheckStatus.Warn => "warn",
        _ => "FAIL",
    };

    /// <summary>True when a check that the digest cannot run without has failed.</summary>
    public static bool Failed(IReadOnlyList<CheckRow> rows) => rows.Any(r => r.Required && r.Result.Status == CheckStatus.Fail);

    public static IEnumerable<string> Lines(IReadOnlyList<CheckRow> rows)
    {
        var width = rows.Max(r => r.Name.Length);
        foreach (var row in rows)
        {
            var r = row.Result;
            yield return $"{Label(r.Status),-4}  {row.Name.PadRight(width)}  {r.Detail}{(r.Fix is null ? "" : " - fix: " + r.Fix)}";
        }

        var failing = rows.Where(r => r.Required && r.Result.Status == CheckStatus.Fail).Select(r => r.Name).ToList();
        yield return "";
        yield return failing.Count == 0 ? "Everything required works." : $"{failing.Count} required {(failing.Count == 1 ? "check" : "checks")} failing: {string.Join(", ", failing)}.";
    }
}

/// <summary>A doctor run is not usage: it must not write rows when the database is the thing that is broken.</summary>
internal sealed class DiscardUsage : IUsageSink
{
    public Task AddAsync(string purpose, string model, int inputTokens, int outputTokens, decimal? cost, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Entry for <c>dotnet FeedEater.dll doctor [--print-config]</c>. It builds the configuration and the service container and runs the
/// checks, but never the web host: no job, poller or migration starts.
/// </summary>
public static class DoctorCommand
{
    private const string Usage = "Usage: doctor [--print-config]";

    public static Task<int> RunAsync(string[] args) => RunAsync(args, Host.CreateApplicationBuilder().Configuration, Console.Out);

    internal static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output, Action<IServiceCollection>? configure = null)
    {
        if (args.Length == 1 && args[0] == "--print-config")
        {
            foreach (var line in ConfigPrinter.Lines(configuration))
            {
                await output.WriteLineAsync(line);
            }

            return 0;
        }

        if (args.Length > 0)
        {
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }

        // The container builds its data source while it is registered, so a connection string that does not parse stops it before any check can run.
        if (ConnectionProblem(configuration) is { } problem)
        {
            var row = new CheckRow("database", true, CheckResult.Fail(problem, "Set ConnectionStrings__FeedEater, for example Host=db;Database=feed;Username=feed;Password=...; then run doctor again."));
            foreach (var line in DoctorReport.Lines([row]))
            {
                await output.WriteLineAsync(line);
            }

            return 1;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddFeedEater(configuration);
        services.AddSingleton<IUsageSink, DiscardUsage>();
        configure?.Invoke(services);
        await using var provider = services.BuildServiceProvider();
        var rows = await provider.GetRequiredService<CheckRunner>().RunAsync(CancellationToken.None);
        foreach (var line in DoctorReport.Lines(rows))
        {
            await output.WriteLineAsync(line);
        }

        return DoctorReport.Failed(rows) ? 1 : 0;
    }

    private static string? ConnectionProblem(IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("FeedEater");
        if (string.IsNullOrWhiteSpace(connection))
        {
            return "ConnectionStrings__FeedEater is not set";
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(connection);
            return null;
        }
        catch (ArgumentException)
        {
            return "ConnectionStrings__FeedEater is not a valid Postgres connection string";
        }
    }
}
