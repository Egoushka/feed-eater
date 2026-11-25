using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PriorArt.Bench;

/// <summary>The systems the base is compared with. Each answers a question with at most <c>limit</c> results.</summary>
internal static partial class Systems
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "that", "with", "how", "do", "does", "can", "are", "is", "of", "to", "in", "on", "or", "a", "an", "when", "which", "what",
        "their", "they", "it", "its", "by", "from", "as", "at", "be", "so", "than", "into", "such", "while", "without", "instead", "some", "one", "other",
    };

    [GeneratedRegex(@"[a-z][a-z0-9+#.-]{2,}")]
    private static partial Regex Word();

    /// <summary>D-warm: the base's own top results, with the <c>coverage</c> it answered the question with.</summary>
    public static async Task<WarmOutcome> WarmAsync(Mcp mcp, Decision d, int limit, CancellationToken ct)
    {
        var answer = await mcp.CallAsync("prior_art_search", new { question = d.Question, limit }, ct);
        var results = answer.GetProperty("facts").EnumerateArray().Select(f =>
        {
            var parts = new List<string> { f.GetProperty("text").GetString()!, f.GetProperty("tool").GetString()! };
            if (f.TryGetProperty("approach", out var approach) && approach.ValueKind == JsonValueKind.String)
            {
                parts.Add(approach.GetString()!);
            }

            return new Result(string.Join(' ', parts), f.TryGetProperty("validFrom", out var v) && v.ValueKind == JsonValueKind.String ? DateOnly.FromDateTime(v.GetDateTime()) : null);
        }).ToList();
        var coverage = answer.TryGetProperty("coverage", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
        return new WarmOutcome(results, coverage);
    }

    /// <summary>
    /// B0, nothing new: an rg stand-in over the files of <paramref name="audits"/> (every line scored by how many distinct question words it holds,
    /// best five with at least two) plus <c>feed_search</c> over the archive (top five). Ten results at most.
    /// </summary>
    public static async Task<IReadOnlyList<Result>> B0Async(Mcp mcp, Decision d, string audits, IReadOnlySet<string>? only, int limit, CancellationToken ct)
    {
        var words = Word().Matches(d.Question.ToLowerInvariant()).Select(m => m.Value).Where(w => !Stop.Contains(w)).Distinct().ToList();
        var lines = Directory.EnumerateFiles(audits, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".md", StringComparison.Ordinal) || f.EndsWith(".csv", StringComparison.Ordinal))
            .Where(f => only is null || only.Contains(Path.GetFileName(f)))
            .SelectMany(f => File.ReadLines(f).Select(l => (File: f, Line: l)))
            .Select(x => (x.File, x.Line, Score: words.Count(w => x.Line.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score >= 2)
            .OrderByDescending(x => x.Score).ThenBy(x => x.File, StringComparer.Ordinal)
            .Take(limit / 2)
            .Select(x => new Result(x.Line, FileDate(x.File)))
            .ToList();
        var feed = await mcp.CallAsync("feed_search", new { query = d.Question, limit = limit - lines.Count }, ct);
        var items = feed.ValueKind == JsonValueKind.Array ? feed.EnumerateArray() : feed.TryGetProperty("items", out var i) ? i.EnumerateArray() : default;
        return lines.Concat(items.Select(it => new Result(
            $"{Str(it, "title")} {Str(it, "summary")} {Str(it, "url")}",
            DateTimeOffset.TryParse(Str(it, "published") is { Length: > 0 } p ? p : Str(it, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? DateOnly.FromDateTime(at.UtcDateTime) : null))).Take(limit).ToList();
    }

    /// <summary>B1, Hindsight: the recall results of the scratch bank, produced outside the harness (id to a list of texts).</summary>
    public static IReadOnlyList<Result> B1(JsonElement file, Decision d, int limit) =>
        file.TryGetProperty(d.Id.ToString(CultureInfo.InvariantCulture), out var list)
            ? list.EnumerateArray().Take(limit).Select(x => new Result(x.ValueKind == JsonValueKind.String ? x.GetString()! : Str(x, "text"), null)).ToList()
            : [];

    private static string Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static DateOnly? FileDate(string path) =>
        Regex.Match(Path.GetFileName(path), @"\d{4}-\d{2}-\d{2}") is { Success: true } m && DateOnly.TryParseExact(m.Value, "yyyy-MM-dd", out var day) ? day : null;
}

/// <summary>D-warm's results for one question plus the <c>coverage</c> (<c>hit</c>, <c>thin</c> or <c>miss</c>) it answered with.</summary>
internal sealed record WarmOutcome(IReadOnlyList<Result> Results, string Coverage);
