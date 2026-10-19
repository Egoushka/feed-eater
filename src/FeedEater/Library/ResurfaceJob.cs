using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using FeedEater.Inbox;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Library;

/// <summary>
/// Sunday 10:00 local: puts the oldest unread saves (at least <c>ResurfaceDays</c> old, not offered in the last 30 days) into the inbox,
/// at most <c>ResurfacePerWeek</c>. Quiet hours hold the run. The message is plain: the card carries the acts.
/// </summary>
public sealed class ResurfaceJob(
    LibraryStore library, ItemStore items, IInbox inbox, QuietHours quiet,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<ResurfaceJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    protected override string Name => "resurface";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestWeekly(localNow, DayOfWeek.Sunday, new TimeSpan(10, 0, 0));

    protected override async Task<bool> HoldAsync(CancellationToken ct) => await quiet.IsQuietAsync(ct);

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var now = Time.GetUtcNow();
        var o = Settings.Library;
        var added = 0;
        foreach (var id in await library.UnreadSinceAsync(now.AddDays(-o.ResurfaceDays), o.ResurfacePerWeek, ct))
        {
            if (await items.GetAsync(id, ct) is not { } item || await library.GetAsync(id, ct) is not { } saved)
            {
                continue;
            }

            var savedAt = new DateTimeOffset(DateTime.SpecifyKind(saved.SavedAt, DateTimeKind.Utc));
            var days = (int)(now - savedAt).TotalDays;
            var text = $"Saved {days} days ago, still unread: {WebUtility.HtmlEncode(item.Title)}";
            var html = Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? $"{text}\n<a href=\"{WebUtility.HtmlEncode(uri.AbsoluteUri)}\">{WebUtility.HtmlEncode(uri.AbsoluteUri)}</a>"
                : text;
            await inbox.AddAsync(new InboxEntry($"resurface:{id}:{key}", InboxKinds.Resurface, ItemId: id,
                Payload: new JsonObject { ["savedAt"] = savedAt.ToString("O"), ["title"] = item.Title }, ExpiresAt: now.AddDays(7)), new OutMessage(html), ct);
            added++;
        }

        Logger.LogInformation("Resurface offered {Count} saved items", added);
    }
}
