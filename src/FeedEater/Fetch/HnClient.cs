using System.Text.Json;
using FeedEater.Text;

namespace FeedEater.Fetch;

/// <summary>Top comments of a Hacker News item from the Algolia API: a fixed host and a numeric id, so no feed-supplied URL is involved.</summary>
public sealed class HnClient(HttpClient http)
{
    public async Task<string?> TopCommentsAsync(long id, int count, int maxCharsEach, CancellationToken ct)
    {
        var body = Json.Parse(await http.GetStringAsync($"items/{id}", ct));
        if (!body.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var comments = children.EnumerateArray()
            .Select(c => HtmlText.ToPlain(Json.Str(c, "text")))
            .Where(t => t.Length > 0)
            .Take(count)
            .Select(t => "- " + (t.Length <= maxCharsEach ? t : t[..maxCharsEach] + "…"))
            .ToList();
        return comments.Count == 0 ? null : "Top comments:\n" + string.Join('\n', comments);
    }
}
