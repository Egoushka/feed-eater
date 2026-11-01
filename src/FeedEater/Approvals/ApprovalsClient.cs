using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace FeedEater.Approvals;

/// <summary>An info card for the approvals phone inbox. <paramref name="Id"/> is unique per thing said, so a resend rewrites the card.</summary>
public sealed record ApprovalsNote(string Id, string Title, string Summary, string Text, DateTimeOffset ExpiresAt);

/// <summary>
/// Puts info items into the approvals inbox (<c>PUT /items/{id}</c>, source token as bearer; approvals server README, "Routes"). Off while
/// Approvals:BaseUrl or Approvals:Token is empty. A nudge, not the product: a failure is logged and swallowed.
/// </summary>
public sealed class ApprovalsClient(HttpClient http, IOptions<FeedEaterOptions> options, ILogger<ApprovalsClient> logger)
{
    public const string Source = "feed-eater";

    // approvals/server/src/items.ts: title 200, summary 300, link label 60.
    private const int TitleMax = 200;
    private const int SummaryMax = 300;

    public bool Enabled => options.Value.Approvals.BaseUrl.Length > 0 && options.Value.Approvals.Token.Length > 0;

    public async Task NotifyAsync(ApprovalsNote note, CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        var item = new JsonObject
        {
            ["source"] = Source,
            ["id"] = note.Id,
            ["kind"] = "info",
            ["title"] = Clip(note.Title, TitleMax),
            ["summary"] = Clip(note.Summary, SummaryMax),
            ["body"] = new JsonObject { ["text"] = note.Text },
            ["expires_at"] = note.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (StackUrl() is { } stack)
        {
            item["links"] = new JsonArray(new JsonObject { ["label"] = "Open the stack", ["url"] = stack });
        }

        try
        {
            using var response = await http.PutAsJsonAsync("items/" + note.Id, item, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Approvals refused {Id}: {Status} {Body}", note.Id, (int)response.StatusCode, Clip(await response.Content.ReadAsStringAsync(ct), 300));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Could not send {Id} to approvals", note.Id);
        }
    }

    private string? StackUrl() => options.Value.Api.BaseUrl.Length > 0 ? options.Value.Api.BaseUrl.TrimEnd('/') + "/swipe" : null;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
