using System.Text;
using System.Text.Json;

namespace FeedEater.Instagram;

/// <summary>
/// <c>import-instagram &lt;zip|folder&gt; --inspect [--all] [--labels]</c>: lists the JSON files of an Instagram "Download your information" export with their sizes and
/// structure so the importer can be written against real names. It reads nothing into the database, writes nothing, and prints key names and types, never values.
/// </summary>
public static class InspectCommand
{
    public const long MaxJsonBytes = 20 * 1024 * 1024;

    /// <summary>Words in a file name that mark it as one the importer is likely to want; their files get the full structure. A guess to compare with the real names, not a list of them.</summary>
    private static readonly string[] Interesting = ["like", "saved", "viewed", "watched", "interest", "topic", "ads_", "ad_", "advertis", "recommend", "search", "reel"];

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (paths.Count != 1 || !flags.Contains("--inspect") || flags.Except(["--inspect", "--all", "--labels"]).Any())
        {
            await error.WriteLineAsync("Usage: import-instagram <export.zip|folder> --inspect [--all] [--labels]");
            await error.WriteLineAsync("Only --inspect exists so far: the importer waits until its parsers are checked against a real export (see docs/specs/2026-10-16-swipe-mode.md, gate G1).");
            return 2;
        }

        ExportReader reader;
        try
        {
            reader = ExportReader.Open(paths[0]);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(ex.Message);
            return 2;
        }

        using (reader)
        {
            await output.WriteAsync(await ReportAsync(reader, paths[0], flags.Contains("--all"), flags.Contains("--labels")));
        }

        return 0;
    }

    public static async Task<string> ReportAsync(ExportReader reader, string source, bool all, bool labels)
    {
        var json = reader.Entries.Where(e => e.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
        var html = reader.Entries.Count(e => e.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || e.Path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));
        var text = new StringBuilder();
        text.Append($"Instagram export: {Path.GetFileName(source.TrimEnd('/', '\\'))} ({(reader.IsZip ? "zip" : "folder")})\n")
            .Append($"Files: {reader.Entries.Count}{(reader.Truncated ? $" (stopped at {ExportReader.MaxEntries})" : "")}; {json.Count} JSON, {html} HTML, {reader.Entries.Count - json.Count - html} other (media and the like)\n");
        if (json.Count == 0)
        {
            text.Append("No JSON files. Request the export again with the format set to JSON.\n");
            return text.ToString();
        }

        text.Append("Only key names and value types are shown, never values; '?' marks a key that only some items have.\n\n");
        foreach (var entry in json)
        {
            text.Append($"{entry.Path}  ({Size(entry.Size)})");
            if (entry.Size > MaxJsonBytes)
            {
                text.Append("  too large to inspect\n");
                continue;
            }

            JsonDocument document;
            try
            {
                await using var stream = reader.Read(entry.Path);
                document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                text.Append("  not valid JSON\n");
                continue;
            }

            using (document)
            {
                var shape = Shape.Of(document.RootElement, labels);
                text.Append($"  {shape.Type}{(shape.Type.Contains("array", StringComparison.Ordinal) ? $" ({document.RootElement.GetArrayLength()} items)" : "")}\n");
                if (all || Likely(entry.Path))
                {
                    text.Append(shape.Render(4));
                }
                else if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    text.Append("    keys: ").AppendJoin(", ", document.RootElement.EnumerateObject().Take(Shape.MaxKeys).Select(p => p.Name)).Append('\n');
                }
            }
        }

        var likely = json.Where(e => Likely(e.Path)).Select(e => e.Path).ToList();
        text.Append($"\nFiles whose names suggest likes, saves, views, ads or topics ({likely.Count}; a guess from the names, shown with their full structure above):\n");
        foreach (var path in likely)
        {
            text.Append("  ").Append(path).Append('\n');
        }

        return text.ToString();
    }

    private static bool Likely(string path)
    {
        var name = path.ToLowerInvariant();
        return Interesting.Any(name.Contains);
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
