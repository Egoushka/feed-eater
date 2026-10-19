using System.Text.Json.Nodes;
using FeedEater.Inbox;
using FeedEater.Follow;
using FeedEater.Library;
using FeedEater.Storage;
using FeedEater.Text;
using FeedEater.Ui;
using FeedEater.WeeklyIdeas;

namespace FeedEater.Swipe;

/// <summary>The swipe cards for inbox entries and what the owner can do with them. Payloads are the jobs' own (see their <c>InboxEntry</c> calls); every string is plain text.</summary>
public sealed class InboxCards(InboxStore inbox, ItemStore items, DuelStore duels, StoryFollower follower, LibraryStore library, LibraryService libraryService, TimeProvider time, IdeaDecisions? ideas = null)
{
    private static readonly string[] OpenOnly = ["open"];
    private static readonly string[] FollowActs = ["save", "like", "dislike", "open", "stop"];
    private static readonly string[] DuelActs = ["pick_a", "pick_b"];
    private static readonly string[] IdeaActs = ["keep", "drop", "open"];
    private static readonly string[] ResurfaceActs = ["keep", "remove", "read", "open"];

    /// <summary>The acts a card of this inbox kind takes; empty for a kind with no card.</summary>
    /// <summary>The message of a stop that closed nothing; <see cref="SwipeService.UndoAsync"/> reads it to refuse the undo.</summary>
    public const string AlreadyStopped = "Already stopped";

    /// <summary>The message of a read on an item that was already read; undo leaves its read time alone.</summary>
    public const string AlreadyRead = "Already read";

    public static string[] ActsFor(string kind) => kind switch
    {
        InboxKinds.Follow => FollowActs,
        InboxKinds.Duel => DuelActs,
        InboxKinds.Idea => IdeaActs,
        InboxKinds.Resurface => ResurfaceActs,
        InboxKinds.Release or InboxKinds.Review or InboxKinds.Autopsy or InboxKinds.Notice => OpenOnly,
        _ => [],
    };

    /// <summary>Null when the entry is gone, done or points at an item that no longer exists; the caller skips it.</summary>
    public async Task<CardView?> ViewAsync(InboxRow row, SessionRow s, int position, int total, DateTimeOffset now, bool markShown, CancellationToken ct)
    {
        var view = row.DoneAt is not null ? null : row.Kind switch
        {
            InboxKinds.Follow => await FollowAsync(row, s, position, total, now, ct),
            InboxKinds.Duel => await DuelAsync(row, s, position, total, now, ct),
            InboxKinds.Release => Release(row, s, position, total, now),
            InboxKinds.Idea => Idea(row, s, position, total, now),
            InboxKinds.Resurface => await ResurfaceAsync(row, s, position, total, now, ct),
            InboxKinds.Review => Plain(row, s, position, total, now, "Weekly review", "review", null, HtmlText.ToPlain(Str(row.Payload, "html"))),
            InboxKinds.Autopsy => Plain(row, s, position, total, now, "Hype autopsy", "autopsy", Html.SafeUrl("/ui/autopsy"), HtmlText.ToPlain(Str(row.Payload, "html"))),
            InboxKinds.Notice => Plain(row, s, position, total, now, "Notice", "feed-eater", null, Str(row.Payload, "text") ?? ""),
            _ => null,
        };
        if (view is not null && markShown)
        {
            await inbox.MarkShownAsync(row.Id, s.Id, ct);
        }

        return view;
    }

    private async Task<CardView?> FollowAsync(InboxRow row, SessionRow s, int position, int total, DateTimeOffset now, CancellationToken ct)
    {
        if (row.ItemId is not { } id || await items.GetAsync(id, ct) is not { } item)
        {
            return null;
        }

        var story = Str(row.Payload, "story") ?? "";
        return new CardView(s.Id, position, total, item.Title, item.Feed, null, "New in a story you follow: " + story, Html.SafeUrl(item.Url),
            SwipeService.Age(item.PublishedAt, now), false, InboxKinds.Follow, Detail: new Dictionary<string, string?> { ["story"] = story });
    }

    private async Task<CardView?> ResurfaceAsync(InboxRow row, SessionRow s, int position, int total, DateTimeOffset now, CancellationToken ct)
    {
        if (row.ItemId is not { } id || await items.GetAsync(id, ct) is not { } item || await library.GetAsync(id, ct) is not { ReadAt: null } saved)
        {
            return null;
        }

        var days = Math.Max(0, (int)(now - SessionPolicy.Utc(saved.SavedAt)).TotalDays);
        return new CardView(s.Id, position, total, item.Title, item.Feed, null, $"Saved {days} days ago, still unread", Html.SafeUrl(item.Url),
            SwipeService.Age(item.PublishedAt, now), false, InboxKinds.Resurface,
            Detail: new Dictionary<string, string?> { ["savedAt"] = saved.SavedAt.ToString("yyyy-MM-dd") });
    }

    private async Task<CardView?> DuelAsync(InboxRow row, SessionRow s, int position, int total, DateTimeOffset now, CancellationToken ct)
    {
        if (Id(row.Payload, "a") is not { } a || Id(row.Payload, "b") is not { } b
            || await items.GetAsync(a, ct) is not { } first || await items.GetAsync(b, ct) is not { } second)
        {
            return null;
        }

        return new CardView(s.Id, position, total, "Which was more useful?", "duel", null, "", null, Age(row, now), false, InboxKinds.Duel, Detail: new Dictionary<string, string?>
        {
            ["aTitle"] = first.Title,
            ["aUrl"] = Html.SafeUrl(first.Url),
            ["bTitle"] = second.Title,
            ["bUrl"] = Html.SafeUrl(second.Url),
        });
    }

    private static CardView Release(InboxRow row, SessionRow s, int position, int total, DateTimeOffset now)
    {
        var p = row.Payload;
        return new CardView(s.Id, position, total, $"{Str(p, "services")} {Str(p, "running")} → {Str(p, "version")}", "release", null, Str(p, "summary") ?? "",
            Html.SafeUrl(Str(p, "url")), Age(row, now), false, InboxKinds.Release,
            Detail: new Dictionary<string, string?> { ["breaking"] = Flag(p, "breaking"), ["urgent"] = Flag(p, "urgent") });
    }

    private static CardView Idea(InboxRow row, SessionRow s, int position, int total, DateTimeOffset now)
    {
        var p = row.Payload;
        return new CardView(s.Id, position, total, Str(p, "title") ?? "", Str(p, "project") ?? "", null, Str(p, "why") ?? "", Html.SafeUrl(Str(p, "url")),
            Age(row, now), false, InboxKinds.Idea,
            Detail: new Dictionary<string, string?> { ["where"] = Str(p, "where"), ["effort"] = Str(p, "effort"), ["sourceTitle"] = Str(p, "sourceTitle") });
    }

    private static CardView Plain(InboxRow row, SessionRow s, int position, int total, DateTimeOffset now, string title, string feed, string? url, string why) =>
        new(s.Id, position, total, title, feed, null, why, url, Age(row, now), false, row.Kind);

    /// <summary>Duel, idea, stop and the info cards' open. The follow card's item acts are the swipe service's, which also closes the row.</summary>
    public async Task<ActResult> ActAsync(InboxRow row, string act, CancellationToken ct)
    {
        switch (row.Kind, act)
        {
            case (_, "open"):
                return new ActResult(true, "");
            case (InboxKinds.Follow, "stop"):
                return await follower.CloseAsync(row.RefId ?? 0, null, ct) ? new ActResult(true, "Stopped following") : new ActResult(true, AlreadyStopped);
            case (InboxKinds.Resurface, "keep"):
                return new ActResult(true, "Kept");
            case (InboxKinds.Resurface, "read") when row.ItemId is { } readId && await library.GetAsync(readId, ct) is { ReadAt: not null }:
                return new ActResult(true, AlreadyRead);
            case (InboxKinds.Resurface, "remove" or "read") when row.ItemId is { } itemId:
                var changed = act == "remove" ? await libraryService.UnsaveAsync(itemId, ct) : await library.MarkReadAsync(itemId, true, time.GetUtcNow(), ct);
                return new ActResult(true, !changed ? "Not in your library any more" : act == "remove" ? "Removed from your library" : "Marked read");
            case (InboxKinds.Duel, "pick_a" or "pick_b"):
                var answer = await duels.AnswerAsync(row.RefId ?? 0, act == "pick_a" ? 'a' : 'b', ct);
                return answer.Outcome switch
                {
                    DuelOutcome.Answered => new ActResult(true, answer.Title is null ? "Thanks" : "Noted: " + answer.Title),
                    DuelOutcome.AlreadyAnswered => new ActResult(true, "Already answered"),
                    _ => new ActResult(false, "Could not do that."),
                };
            case (InboxKinds.Idea, "keep" or "drop"):
                if (ideas is null)
                {
                    return new ActResult(false, "Ideas are off");
                }

                var decision = act == "keep" ? await ideas.KeepAsync(row.Id, ct) : await ideas.DropAsync(row.Id, ct);
                return decision.Outcome switch
                {
                    IdeaOutcome.Filed => new ActResult(true, $"Filed in {decision.Project}"),
                    IdeaOutcome.Dropped => new ActResult(true, "Dropped"),
                    IdeaOutcome.AlreadyDecided => new ActResult(true, "Already decided"),
                    IdeaOutcome.Failed => new ActResult(false, $"Not filed: {decision.Error}"),
                    _ => new ActResult(false, "Could not do that."),
                };
            default:
                return new ActResult(false, "Nothing to do.");
        }
    }

    /// <summary>The act has taken effect: the entry will not be offered again.</summary>
    public Task DoneAsync(long inboxId, CancellationToken ct) => inbox.DoneAsync(inboxId, time.GetUtcNow(), ct);

    private static string Age(InboxRow row, DateTimeOffset now) => SwipeService.Age(row.CreatedAt.UtcDateTime, now);

    private static string? Str(JsonObject p, string key) => p[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Flag(JsonObject p, string key) => p[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b ? "true" : null;

    /// <summary>Ids are JSON numbers; a string is accepted too.</summary>
    private static long? Id(JsonObject p, string key) => p[key] is JsonValue v
        ? v.TryGetValue<long>(out var n) ? n : v.TryGetValue<string>(out var s) && long.TryParse(s, out var parsed) ? parsed : null
        : null;
}
