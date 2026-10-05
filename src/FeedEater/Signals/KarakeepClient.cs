using System.Text.Json;

namespace FeedEater.Signals;

public sealed record Bookmark(string Id, DateTimeOffset CreatedAt, string? Url, string Title, string? Description);

public sealed class KarakeepClient(HttpClient http)
{
    public async Task<(IReadOnlyList<Bookmark> Items, string? Next)> PageAsync(string? cursor, CancellationToken ct)
    {
        var query = cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor);
        var body = Json.Parse(await http.GetStringAsync($"api/v1/bookmarks?limit=100&sortOrder=desc{query}", ct));
        var items = body.GetProperty("bookmarks").EnumerateArray().Select(b =>
        {
            var content = b.TryGetProperty("content", out var c) ? c : default;
            var url = Json.Str(content, "url");
            return new Bookmark(
                b.GetProperty("id").GetString()!,
                b.GetProperty("createdAt").GetDateTimeOffset(),
                Json.Str(content, "type") == "link" ? url : null,
                Json.Str(b, "title") ?? Json.Str(content, "title") ?? url ?? "",
                Json.Str(content, "description"));
        }).ToList();
        var next = body.TryGetProperty("nextCursor", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return (items, next);
    }
}
