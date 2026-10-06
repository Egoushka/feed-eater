using System.Net.Http.Json;
using System.Text.Json;

namespace FeedEater.Signals;

public sealed record Bookmark(string Id, DateTimeOffset CreatedAt, string? Url, string Title, string? Description);

public sealed class KarakeepClient(HttpClient http)
{
    /// <summary>Creates a link bookmark and returns its id; Karakeep answers with the existing one when the URL is already saved.</summary>
    public async Task<string> CreateLinkAsync(string url, string title, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/v1/bookmarks", new { type = "link", url, title = title.Length <= 500 ? title : title[..500] }, ct);
        response.EnsureSuccessStatusCode();
        return Json.Str(Json.Parse(await response.Content.ReadAsStringAsync(ct)), "id") ?? throw new InvalidOperationException("Karakeep returned no bookmark id");
    }

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
