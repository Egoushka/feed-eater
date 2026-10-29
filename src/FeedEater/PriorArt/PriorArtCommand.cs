using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.PriorArt;

/// <summary>
/// <c>dotnet FeedEater.dll prior-art import|embed|stats</c>: loads sources into the prior-art namespace of the configured database,
/// applying the migrations first like the service does. <c>import</c> reads a file, or stdin for <c>-</c>.
/// </summary>
public static class PriorArtCommand
{
    private const string Usage =
        """
        Usage:
          prior-art import <file|-> [--format auto|result|audit|notes|csv] [--ref <path naming the source>] [--tool owner/name] [--problem <question>]
                           [--approach <slug>] [--subject gh:owner/name] [--commit <sha>] [--date yyyy-MM-dd] [--confidence 0..1] [--unverified]
          prior-art embed        embeds the live facts that have no vector yet
          prior-art stats        counts what the base holds
        """;

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is not ("import" or "embed" or "stats"))
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["FeedEater:RunJobs"] = "false";
        builder.Services.AddFeedEater(builder.Configuration);
        using var host = builder.Build();
        host.Services.GetRequiredService<DatabaseMigrator>().Run();
        return await RunAsync(args, host.Services, input, output, error);
    }

    internal static async Task<int> RunAsync(string[] args, IServiceProvider services, TextReader input, TextWriter output, TextWriter error)
    {
        var store = services.GetRequiredService<PriorArtStore>();
        var importer = services.GetRequiredService<PriorArtImporter>();
        try
        {
            switch (args[0])
            {
                case "embed":
                    await store.EnsureReadyAsync(CancellationToken.None);
                    var (embedded, pending) = await importer.EmbedAsync(CancellationToken.None);
                    await output.WriteLineAsync($"Embedded {embedded} facts, {pending} still without a vector.");
                    return pending > 0 ? 1 : 0;
                case "stats":
                    await store.EnsureReadyAsync(CancellationToken.None);
                    var model = services.GetRequiredService<IOptions<FeedEaterOptions>>().Value.Llm.EmbedModel;
                    await output.WriteLineAsync(JsonSerializer.Serialize(await store.StatsAsync(model, CancellationToken.None), new JsonSerializerOptions(FeedEater.Json.Options) { WriteIndented = true }));
                    return 0;
                default:
                    return await ImportAsync(args[1..], importer, input, output, error);
            }
        }
        catch (PriorArtInputException e)
        {
            await error.WriteLineAsync(e.Message);
            return 2;
        }
    }

    private static async Task<int> ImportAsync(string[] args, PriorArtImporter importer, TextReader input, TextWriter output, TextWriter error)
    {
        string? file = null, format = "auto", sourceRef = null, tool = null, problem = null, approach = null, subject = null, commit = null, date = null, confidence = null;
        var unverified = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new PriorArtInputException($"{args[i]} needs a value.\n{Usage}");
            switch (args[i])
            {
                case "--format": format = Next(); break;
                case "--ref": sourceRef = Next(); break;
                case "--tool": tool = Next(); break;
                case "--problem": problem = Next(); break;
                case "--approach": approach = Next(); break;
                case "--subject": subject = Next(); break;
                case "--commit": commit = Next(); break;
                case "--date": date = Next(); break;
                case "--confidence": confidence = Next(); break;
                case "--unverified": unverified = true; break;
                case var a when a.StartsWith("--", StringComparison.Ordinal): throw new PriorArtInputException($"unknown option {a}.\n{Usage}");
                default: file = file is null ? args[i] : throw new PriorArtInputException($"one file at a time.\n{Usage}"); break;
            }
        }

        if (file is null)
        {
            throw new PriorArtInputException(Usage);
        }

        var content = file == "-" ? await input.ReadToEndAsync() : await File.ReadAllTextAsync(file);
        sourceRef ??= file == "-" ? null : Path.GetFileName(file);
        var parsed = format switch
        {
            "result" or "result_v1" => ImportFormat.ResultV1,
            "audit" => ImportFormat.Audit,
            "notes" => ImportFormat.Notes,
            "csv" or "snapshot" => ImportFormat.Snapshot,
            "auto" => Detect(file, content),
            _ => throw new PriorArtInputException("format is auto, result, audit, notes or csv."),
        };
        DateOnly? day = null;
        if (date is not null)
        {
            day = DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new PriorArtInputException("date is yyyy-MM-dd.");
        }

        double? level = null;
        if (confidence is not null)
        {
            level = double.TryParse(confidence, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : throw new PriorArtInputException("confidence is a number from 0 to 1.");
        }

        var report = await importer.ImportAsync(new ImportRequest(parsed, content, sourceRef, tool, problem, approach, subject, commit, day, level, unverified), CancellationToken.None);
        await output.WriteLineAsync(
            $"{report.SourceKind} {report.SourceRef}: " + (report.Duplicate
                ? "already stored, nothing changed."
                : $"{report.Facts} facts ({report.Written} written, {report.Corroborated} corroborated, {report.Outranked} outranked), {report.Embedded} embedded."));
        if (report.EmbedPending > 0)
        {
            await error.WriteLineAsync($"{report.EmbedPending} facts still have no vector (embedder unreachable); run `prior-art embed` later.");
        }

        foreach (var skipped in report.Skipped)
        {
            await error.WriteLineAsync("skipped: " + skipped);
        }

        return report.Facts == 0 && !report.Duplicate ? 1 : 0;
    }

    private static ImportFormat Detect(string file, string content)
    {
        if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return ImportFormat.Snapshot;
        }

        var head = content.AsSpan(0, Math.Min(content.Length, 4000));
        if (head.TrimStart().StartsWith("{") && head.Contains("result/v1", StringComparison.Ordinal))
        {
            return ImportFormat.ResultV1;
        }

        return content.Contains("\n- Fact:", StringComparison.Ordinal) && content.Contains("\n### ", StringComparison.Ordinal) ? ImportFormat.Notes : ImportFormat.Audit;
    }
}
