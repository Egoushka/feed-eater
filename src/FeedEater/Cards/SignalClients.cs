using System.Globalization;
using System.Text.Json;

namespace FeedEater.Cards;

/// <summary>Hacker News stories that name a term, from the Algolia search API. A fixed host; the term is only ever a query value.</summary>
public sealed class HnSearch(HttpClient http)
{
    public async Task<IReadOnlyList<CardSignal>> StoriesAsync(string term, DateTime since, int minPoints, CancellationToken ct)
    {
        var from = new DateTimeOffset(since, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var url = $"search?query={Uri.EscapeDataString("\"" + term + "\"")}&tags=story&hitsPerPage=5&numericFilters={Uri.EscapeDataString($"created_at_i>{from},points>={minPoints}")}";
        var body = Json.Parse(await http.GetStringAsync(url, ct));
        if (!body.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = new List<CardSignal>();
        foreach (var hit in hits.EnumerateArray())
        {
            var id = Json.Str(hit, "objectID");
            var title = Json.Str(hit, "title");
            if (id is null || title is null || !id.All(char.IsAsciiDigit) || !CardRules.Mentions(title, term))
            {
                continue;   // the search is fuzzy: keep only stories whose title names the term
            }

            var points = hit.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            var at = hit.TryGetProperty("created_at_i", out var t) && t.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()).UtcDateTime : (DateTime?)null;
            found.Add(new CardSignal("Hacker News", $"https://news.ycombinator.com/item?id={id}", $"{title} ({points} points)",
                CardRules.IsVersionBump(title, term), null, at, 10 + points / 10));
        }

        return found;
    }
}

/// <summary>Known advisories for the exact version a repo runs, from deps.dev. Public data, no token.</summary>
public sealed class DepsDevClient(HttpClient http)
{
    public async Task<IReadOnlyList<CardSignal>> AdvisoriesAsync(InventoryItem item, CancellationToken ct)
    {
        var system = item.Kind switch { "nuget" => "nuget", "npm" => "npm", _ => null };
        if (system is null || !ExactVersion(item.Version))
        {
            return [];
        }

        using var response = await http.GetAsync($"systems/{system}/packages/{Uri.EscapeDataString(item.Name)}/versions/{Uri.EscapeDataString(item.Version)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];   // an unknown package or version is "no signal", not an error
        }

        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!body.TryGetProperty("advisoryKeys", out var keys) || keys.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return keys.EnumerateArray().Select(k => Json.Str(k, "id")).Where(id => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            .Select(id => new CardSignal("deps.dev", $"https://osv.dev/vulnerability/{id}", $"Advisory {id} against {item.Name} {item.Version}", false, null, null, 100))
            .ToList();
    }

    /// <summary>A range such as <c>^1.2.0</c> or <c>[1,2)</c> or an unresolved <c>$(Version)</c> names no single version.</summary>
    internal static bool ExactVersion(string v) => v.Length > 0 && char.IsAsciiDigit(v[0]) && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');
}
