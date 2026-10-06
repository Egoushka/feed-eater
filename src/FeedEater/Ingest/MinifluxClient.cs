using System.Text.Json;

namespace FeedEater.Ingest;

public sealed record MinifluxEntry(
    long Id, long FeedId, string FeedTitle, string? Category, string? SiteUrl,
    string Url, string Title, string Content, DateTimeOffset PublishedAt, string? FeedUrl = null);

/// <summary>Read-only: entries by id and the full-text fetch. Never changes read/unread state.</summary>
public sealed class MinifluxClient(HttpClient http)
{
    public async Task<IReadOnlyList<MinifluxEntry>> EntriesAfterAsync(long afterId, int limit, CancellationToken ct)
    {
        var body = Json.Parse(await http.GetStringAsync($"v1/entries?after_entry_id={afterId}&order=id&direction=asc&limit={limit}", ct));
        return body.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
            ? entries.EnumerateArray().Select(Parse).ToList()
            : [];
    }

    /// <summary>The original article's HTML, scraped by Miniflux; not written back into Miniflux.</summary>
    public async Task<string> FetchContentAsync(long entryId, CancellationToken ct)
    {
        var body = Json.Parse(await http.GetStringAsync($"v1/entries/{entryId}/fetch-content?update_content=false", ct));
        return body.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
    }

    private static MinifluxEntry Parse(JsonElement e)
    {
        var feed = e.GetProperty("feed");
        return new MinifluxEntry(
            e.GetProperty("id").GetInt64(),
            e.GetProperty("feed_id").GetInt64(),
            Json.Str(feed, "title") ?? "",
            feed.TryGetProperty("category", out var category) ? Json.Str(category, "title") : null,
            Json.Str(feed, "site_url"),
            Json.Str(e, "url") ?? "",
            Json.Str(e, "title") ?? "",
            Json.Str(e, "content") ?? "",
            e.GetProperty("published_at").GetDateTimeOffset(),
            Json.Str(feed, "feed_url"));
    }
}
