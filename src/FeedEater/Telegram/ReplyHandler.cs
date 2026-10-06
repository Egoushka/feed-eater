using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Llm;
using FeedEater.Plane;
using FeedEater.Storage;

namespace FeedEater.Telegram;

/// <summary>
/// A text reply to a digest item or search result: the small model turns it into one of the actions the buttons already offer
/// (vote, file an idea, save), or mutes the item's feed, or answers a question about the item from its text.
/// </summary>
public sealed class ReplyHandler(
    TelegramClient telegram, CallbackHandler callbacks, ItemStore items, ProfileStore profiles, LiteLlmClient llm, IIdeaSink sink,
    IOptions<FeedEaterOptions> options, ILogger<ReplyHandler> logger)
{
    private const int IntentMaxTokens = 200;
    private const int AnswerMaxTokens = 400;
    private const int MaxAnswer = 1500;

    public const string Help = "Reply to an item with 👍 or 👎, “file this” (or “idea for &lt;project&gt;: …”), “save”, “mute this feed”, or a question about it.";

    public async Task HandleAsync(long chatId, long itemId, string text, CancellationToken ct)
    {
        if (await items.GetAsync(itemId, ct) is not { } item)
        {
            await SendAsync(chatId, "That item is no longer in the archive.", ct);
            return;
        }

        var projects = (await profiles.AllAsync(ct))
            .Where(p => sink.ProjectOf(p) is not null)
            .Select(p => (Project: sink.ProjectOf(p)!, About: $"{p.Key}: {p.Description}"))
            .ToList();
        if (sink.FallbackProject is { } fallback)
        {
            projects.Add((Project: fallback, About: "feed-eater and anything without a project of its own"));
        }

        projects = projects.DistinctBy(p => p.Project, StringComparer.OrdinalIgnoreCase).ToList();

        ReplyIntent intent;
        string answer;
        try
        {
            intent = Shortcut(text) ?? await IntentAsync(item, projects, text, ct);
            answer = await ActAsync(item, intent, projects.Select(p => p.Project).ToList(), ct);
        }
        catch (BudgetExceededException)
        {
            answer = "The model budget is used up; the buttons still work.";
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Reply to item {Item} failed", itemId);
            answer = "The model is not reachable; try again later or use the buttons.";
        }

        await SendAsync(chatId, answer, ct);
    }

    /// <summary>A bare 👍 or 👎 needs no model call.</summary>
    internal static ReplyIntent? Shortcut(string text) => text.Trim() switch
    {
        "👍" or "+" or "+1" => new ReplyIntent("up"),
        "👎" or "-" or "-1" => new ReplyIntent("down"),
        _ => null,
    };

    private async Task<ReplyIntent> IntentAsync(ItemView item, IReadOnlyList<(string Project, string About)> projects, string text, CancellationToken ct)
    {
        var (system, user) = Prompts.Reply(projects, item.Title, item.Feed, item.Summary, text);
        var reply = await llm.ChatAsync(options.Value.Llm.TriageModel, system, user, IntentMaxTokens, "reply", ct);
        return LlmJson.Reply(reply.Content, projects.Select(p => p.Project).ToList());
    }

    /// <summary>The reply as Telegram HTML.</summary>
    private async Task<string> ActAsync(ItemView item, ReplyIntent intent, IReadOnlyList<string> projects, CancellationToken ct)
    {
        switch (intent.Action)
        {
            case "up" or "down":
                await callbacks.VoteAsync(item.Id, (short)(intent.Action == "up" ? 1 : -1), ct);
                return intent.Action == "up" ? "👍 saved" : "👎 saved";

            case "idea" when intent.UnknownProject is { } unknown:
                return E($"No {(sink.Name == IdeasOptions.PlaneSink ? "Plane project" : "project")} called {unknown}. Known: {string.Join(", ", projects)}.");

            case "idea":
                var (project, error) = await callbacks.FileIdeaAsync(item.Id, intent.Project, intent.Idea, ct);
                return project is null ? $"Not filed: {E(error ?? "")}" : $"💡 {E(sink.Filed(project))}";

            case "save":
                return CallbackHandler.SaveMessage(await callbacks.SaveAsync(item.Id, ct));

            case "mute":
                if (item.FeedId is not { } feedId || !await items.SetFeedMutedAsync(feedId, true, ct))
                {
                    return "This item has no feed to mute.";
                }

                return $"🔇 Muted {E(item.Feed)}. It is still archived and searchable; unmute it on /ui/sources.";

            case "ask" when intent.Question is { } question:
                var o = options.Value;
                var (system, user) = Prompts.AboutItem(question, item.Title, item.Url, item.Feed, item.Content, o.Caps.ReadChars);
                var reply = await llm.ChatAsync(o.Llm.ReadModel, system, user, AnswerMaxTokens, "reply", ct);
                var text = reply.Content.Trim();
                return E(text.Length <= MaxAnswer ? text : text[..(MaxAnswer - 1)] + "…");

            default:
                return Help;
        }
    }

    /// <summary>Fixed texts hold no markup; every variable part is encoded where it is built.</summary>
    private Task SendAsync(long chatId, string html, CancellationToken ct) => telegram.SendAsync(chatId, new OutMessage(html), ct);

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
