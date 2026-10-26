using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Inbox;
using FeedEater.Issues;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.Text;

namespace FeedEater.WeeklyIdeas;

/// <summary>
/// Mondays at <c>WeeklyIdeas:At</c>: the week's improve items and what was liked, saved or bookmarked become a ranked shortlist.
/// Each idea waits in the inbox for Keep or Drop (Keep files it as a GitHub issue labelled triage in its project's repo); the top few go to Telegram.
/// An idea whose source was filed before, or that repeats an issue or a backlog row, is left out. Quiet hours hold it.
/// </summary>
public sealed class WeeklyIdeasJob(
    WeeklyIdeaStore store, ProfileStore profiles, GitHubIssues issues, BacklogClient backlog, LiteLlmClient llm, TelegramClient telegram, InboxStore inboxStore, QuietHours quiet,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<WeeklyIdeasJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    private const int MaxLeads = 60;
    private const int MaxIssueTitles = 100;
    private const int MaxTokens = 2000;

    /// <summary>Set by /ideas: the next tick runs the shortlist now, window or not, once.</summary>
    public const string ForceCursor = "weekly-ideas:force";

    protected override string Name => "weekly-ideas";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestWeekly(localNow, DayOfWeek.Monday, Settings.WeeklyIdeas.At);

    protected override async Task PollAsync(CancellationToken ct)
    {
        if (await Cursors.GetAsync(ForceCursor, ct) is null)
        {
            await base.PollAsync(ct);
            return;
        }

        try
        {
            await RunAsync(Schedule.LatestWeekly(Schedule.Local(Time.GetUtcNow(), Settings.Zone), DayOfWeek.Monday, Settings.WeeklyIdeas.At), ct);
        }
        finally
        {
            await Cursors.DeleteAsync(ForceCursor, CancellationToken.None);
        }
    }

    protected override async Task<bool> HoldAsync(CancellationToken ct) => await quiet.IsQuietAsync(ct);

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var settings = Settings.WeeklyIdeas;
        var since = Time.GetUtcNow().AddDays(-7);
        var filed = await store.FiledUrlsAsync(ct);
        var leads = await LeadsAsync(since, filed, ct);
        if (leads.Count == 0)
        {
            Logger.LogInformation("No leads this week; no shortlist");
            return;
        }

        var targets = (await profiles.AllAsync(ct)).Where(p => p.Repo is { Length: > 0 })
            .Select(p => new Target(p.Repo!, p.Key, p.Description)).DistinctBy(t => t.Repo, StringComparer.OrdinalIgnoreCase).ToList();
        var fallback = Settings.GitHub.FallbackRepo;
        if (fallback.Length > 0 && targets.All(t => !string.Equals(t.Repo, fallback, StringComparison.OrdinalIgnoreCase)))
        {
            targets.Add(new Target(fallback, "other", "Ideas that fit no other project."));
        }

        if (targets.Count == 0)
        {
            Logger.LogWarning("No repo to file ideas in (no profile has a repo and GitHub:FallbackRepo is empty)");
            return;
        }

        var issueTitles = new List<string>();
        foreach (var t in targets)
        {
            try
            {
                issueTitles.AddRange((await issues.RecentTitlesAsync(t.Repo, ct)).Take(MaxIssueTitles).Select(title => $"{t.Repo}: {title}"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden })
            {
                Logger.LogWarning(ex, "Repo {Repo} unavailable; its issues are not checked", t.Repo);
            }
        }

        // A configured backlog that cannot be read fails the run: filing without it would repeat rows.
        var backlogText = await backlog.TextAsync(ct);
        var reply = await llm.ChatAsync(Settings.Llm.ReadModel, IdeaShortlist.System(settings.MaxFiled),
            IdeaShortlist.User(leads, targets, issueTitles, await store.RecentTitlesAsync(50, ct), backlogText), MaxTokens, "weekly-ideas", ct);
        var entries = IdeaShortlist.Parse(reply.Content, leads.Count, targets.Select(t => t.Repo).ToList(), fallback.Length > 0 ? fallback : targets[0].Repo)
            .Take(settings.MaxFiled).ToList();
        if (entries.Count == 0)
        {
            Logger.LogInformation("The model found no idea among {Count} leads: {Reply}", leads.Count, reply.Content.Length <= 600 ? reply.Content : reply.Content[..600]);
            return;
        }

        var monday = DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var week = string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(monday)}-W{ISOWeek.GetWeekOfYear(monday):00}");
        var staged = new List<(ShortlistEntry Entry, Lead Lead, long InboxId)>();
        foreach (var entry in entries)
        {
            var lead = leads[entry.Lead];
            var title = entry.Idea.Length <= 80 ? entry.Idea : entry.Idea[..79] + "…";
            var url = UrlCanonicalizer.Canonical(lead.Url);
            var id = await inboxStore.AddAsync(new InboxEntry($"idea:{url}", InboxKinds.Idea,
                Payload: new JsonObject
                {
                    ["week"] = week,
                    ["title"] = title,
                    ["project"] = entry.Project,
                    ["body"] = Body(entry, lead, week),
                    ["url"] = url,
                    ["sourceTitle"] = lead.Title,
                    ["signal"] = lead.Signal,
                    ["why"] = entry.Why,
                    ["where"] = entry.Where,
                    ["effort"] = entry.Effort.ToString(),
                },
                ExpiresAt: Time.GetUtcNow().AddDays(14)), Time.GetUtcNow(), ct);
            staged.Add((entry, lead, id));
        }

        Logger.LogInformation("Staged {Count} ideas for {Week}", staged.Count, week);
        if (Settings.Telegram.Token.Length > 0)
        {
            await telegram.SendAsync(Settings.Telegram.AllowedUserId, Message(week, staged, settings.TopInMessage), ct);
        }
    }

    private async Task<List<Lead>> LeadsAsync(DateTimeOffset since, IReadOnlySet<string> filed, CancellationToken ct)
    {
        var leads = new List<Lead>();
        foreach (var c in await store.CandidatesAsync(since, MaxLeads, ct))
        {
            leads.Add(new Lead(c.Signal, c.Title, c.Url, c.Suggestion ?? c.Summary ?? c.Why ?? ""));
        }

        foreach (var b in await store.BookmarksAsync(since, MaxLeads, ct))
        {
            leads.Add(new Lead("bookmarked", b.Title, b.Url!, ""));
        }

        var seen = new HashSet<string>(filed, StringComparer.Ordinal);
        return leads.Where(l => seen.Add(UrlCanonicalizer.Canonical(l.Url))).Take(MaxLeads).ToList();
    }

    /// <summary>The issue body, in Markdown.</summary>
    internal static string Body(ShortlistEntry e, Lead lead, string week)
    {
        var body = new StringBuilder(Markdown.Text(e.Idea));
        if (e.Why.Length > 0)
        {
            body.Append($"\n\nWhy: {Markdown.Text(e.Why)}");
        }

        body.Append($"\n\nWhere: {Markdown.Text(e.Where.Length > 0 ? e.Where : e.Project)} · effort {e.Effort}");
        body.Append($"\n\nSource: {Markdown.Link(lead.Title, lead.Url)} ({Markdown.Text(lead.Signal)}, feed-eater weekly ideas {week})");
        return body.ToString();
    }

    internal static OutMessage Message(string week, IReadOnlyList<(ShortlistEntry Entry, Lead Lead, long InboxId)> staged, int detailed)
    {
        var html = new StringBuilder($"💡 <b>Ideas for {week}</b> ({staged.Count} waiting for Keep or Drop)\n");
        for (var i = 0; i < staged.Count; i++)
        {
            var (e, lead, _) = staged[i];
            if (i >= detailed)
            {
                html.Append($"\n{i + 1}. {E(e.Idea)}");
                continue;
            }

            html.Append($"\n{i + 1}. <b>{E(e.Idea)}</b>\n   {E(e.Project)} · {E(e.Where)} · {e.Effort}");
            if (e.Why.Length > 0)
            {
                html.Append($"\n   {E(e.Why)}");
            }

            html.Append($"\n   <a href=\"{E(lead.Url)}\">{E(lead.Title)}</a>\n");
        }

        var keyboard = staged.Select((t, i) => (IReadOnlyList<Button>)
            [new Button($"✅ {i + 1}", CallbackData.KeepIdea(t.InboxId)), new Button($"✖ {i + 1}", CallbackData.DropIdea(t.InboxId))]).ToList();
        return new OutMessage(html.ToString(), keyboard);
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
