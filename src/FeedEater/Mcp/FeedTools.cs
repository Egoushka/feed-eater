using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using FeedEater.Search;
using FeedEater.Storage;

namespace FeedEater.Mcp;

[McpServerToolType]
public sealed class FeedTools(ArchiveSearch search, ItemStore items, DigestStore digests, FeedbackStore feedback)
{
    [McpServerTool(Name = "feed_search", ReadOnly = true)]
    [Description("Search Yehor's research archive: every item from his feeds (tech news, blogs, releases, Ukrainian tech), with an AI summary where one was written. Matches by meaning and by keywords. Optional filters: project or topic key, kind (improve, new, fyi), published range. Returns id, title, url, feed, date, summary, project, kind, his vote.")]
    public async Task<string> SearchAsync(
        [Description("What to look for, in words.")] string query,
        [Description("Project or topic key, e.g. homelab, chargehand, postgres.")] string? project = null,
        [Description("improve, new or fyi.")] string? kind = null,
        [Description("Published at or after, ISO 8601.")] DateTimeOffset? from = null,
        [Description("Published before, ISO 8601.")] DateTimeOffset? to = null,
        [Description("At most this many results, 1 to 50; default 20.")] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpProtocolException("query is empty.", McpErrorCode.InvalidParams);
        }

        return Json(await search.SearchAsync(query, project, kind, from, to, Math.Clamp(limit, 1, 50), ct));
    }

    [McpServerTool(Name = "feed_read", ReadOnly = true)]
    [Description("One archived item in full: text, triage verdict, AI summary and suggestion, Yehor's vote, and the Plane project it was filed in.")]
    public async Task<string> ReadAsync([Description("Item id from feed_search or feed_digest.")] long id, CancellationToken ct = default) =>
        Json(await items.GetAsync(id, ct) ?? throw new McpProtocolException($"no item with id {id}.", McpErrorCode.InvalidParams));

    [McpServerTool(Name = "feed_digests", ReadOnly = true)]
    [Description("Daily digests, newest first: date, status, how many items were candidates, triaged and shown, with item ids.")]
    public async Task<string> DigestsAsync(
        [Description("Only digests before this date, yyyy-MM-dd.")] string? before = null,
        [Description("At most this many, 1 to 30; default 10.")] int limit = 10,
        CancellationToken ct = default) =>
        Json(await digests.ListAsync(before is null ? null : Date(before), Math.Clamp(limit, 1, 30), ct));

    [McpServerTool(Name = "feed_digest", ReadOnly = true)]
    [Description("One day's digest with its highlights in order: title, url, feed, project, kind, summary, why, suggestion.")]
    public async Task<string> DigestAsync([Description("yyyy-MM-dd")] string date, CancellationToken ct = default)
    {
        var digest = await digests.GetAsync(Date(date), ct) ?? throw new McpProtocolException($"no digest for {date}.", McpErrorCode.InvalidParams);
        return Json(new { digest, items = await items.DigestItemsAsync(digest.ItemIds, ct) });
    }

    [McpServerTool(Name = "feed_ideas", ReadOnly = true)]
    [Description("Ideas Yehor filed from digests into Plane Intake, newest first, with the Plane project and the source item id.")]
    public async Task<string> IdeasAsync(
        [Description("Plane project identifier, e.g. LAB, SKAR, FEED.")] string? planeProject = null,
        [Description("At most this many, 1 to 50; default 20.")] int limit = 20,
        CancellationToken ct = default) =>
        Json(await feedback.IdeasAsync(planeProject, Math.Clamp(limit, 1, 50), ct));

    private static string Date(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : throw new McpProtocolException("dates are yyyy-MM-dd.", McpErrorCode.InvalidParams);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, FeedEater.Json.Options);
}
