using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Inbox;
using FeedEater.Plane;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.Text;

namespace FeedEater.WeeklyIdeas;

/// <summary>
/// Mondays at <c>WeeklyIdeas:At</c>: the week's improve items and what was liked, saved or bookmarked become a ranked shortlist.
/// Each idea waits in the inbox for Keep or Drop (Keep files it in Plane Intake in its project); the top few go to Telegram. An idea whose source was filed before,
/// or that repeats a Plane item or a backlog row, is left out. Quiet hours hold it.
/// </summary>
public sealed class WeeklyIdeasJob(
    WeeklyIdeaStore store, ProfileStore profiles, PlaneClient plane, BacklogClient backlog, LiteLlmClient llm, TelegramClient telegram, InboxStore inboxStore, QuietHours quiet,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<WeeklyIdeasJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    private const int MaxLeads = 60;
    private const int MaxPlaneTitles = 100;
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

        var targets = (await profiles.AllAsync(ct)).Where(p => p.PlaneIdentifier is { Length: > 0 })
            .Select(p => new Target(p.PlaneIdentifier!, p.Key, p.Description)).DistinctBy(t => t.Identifier, StringComparer.Ordinal).ToList();
        var fallback = Settings.Plane.FallbackProject;
        if (fallback.Length > 0 && targets.All(t => t.Identifier != fallback))
        {
            targets.Add(new Target(fallback, "other", "Ideas that fit no other project."));
        }

        if (targets.Count == 0)
        {
            Logger.LogWarning("No Plane project to file ideas in (no profile has a Plane identifier and Plane:FallbackProject is empty)");
            return;
        }

        var planeTitles = new List<string>();
        foreach (var t in targets)
        {
            try
            {
                planeTitles.AddRange((await plane.RecentItemTitlesAsync(t.Identifier, ct)).Take(MaxPlaneTitles).Select(title => $"{t.Identifier}: {title}"));
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogWarning(ex, "Plane project {Project} unavailable; its items are not checked", t.Identifier);
            }
        }

        // A configured backlog that cannot be read fails the run: filing without it would repeat rows.
        var backlogText = await backlog.TextAsync(ct);
        var reply = await llm.ChatAsync(Settings.Llm.ReadModel, IdeaShortlist.System(settings.MaxFiled),
            IdeaShortlist.User(leads, targets, planeTitles, await store.RecentTitlesAsync(50, ct), backlogText), MaxTokens, "weekly-ideas", ct);
        var entries = IdeaShortlist.Parse(reply.Content, leads.Count, targets.Select(t => t.Identifier).ToList(), fallback.Length > 0 ? fallback : targets[0].Identifier)
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
            var id = await inboxStore.AddAsync(new InboxEntry($"idea:{week}:{url}", InboxKinds.Idea,
                Payload: new JsonObject
                {
                    ["week"] = week,
                    ["title"] = title,
                    ["project"] = entry.Project,
                    ["html"] = Html(entry, lead, week),
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
            await telegram.SendAsync(Settings.Telegram.AllowedUserId, Message(week, staged.Take(settings.TopInMessage).ToList(), staged.Count), ct);
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

    private static string Html(ShortlistEntry e, Lead lead, string week)
    {
        var html = new StringBuilder($"<p>{E(e.Idea)}</p>");
        if (e.Why.Length > 0)
        {
            html.Append($"<p>Why: {E(e.Why)}</p>");
        }

        html.Append($"<p>Where: {E(e.Where.Length > 0 ? e.Where : e.Project)} · effort {e.Effort}</p>");
        html.Append($"<p>Source: <a href=\"{E(lead.Url)}\">{E(lead.Title)}</a> ({E(lead.Signal)}, feed-eater weekly ideas {week})</p>");
        return html.ToString();
    }

    internal static OutMessage Message(string week, IReadOnlyList<(ShortlistEntry Entry, Lead Lead, long InboxId)> top, int total)
    {
        var html = new StringBuilder($"💡 <b>Ideas for {week}</b> ({total} waiting for Keep or Drop)\n");
        for (var i = 0; i < top.Count; i++)
        {
            var (e, lead, _) = top[i];
            html.Append($"\n{i + 1}. <b>{E(e.Idea)}</b>\n   {E(e.Project)} · {E(e.Where)} · {e.Effort}");
            if (e.Why.Length > 0)
            {
                html.Append($"\n   {E(e.Why)}");
            }

            html.Append($"\n   <a href=\"{E(lead.Url)}\">{E(lead.Title)}</a>\n");
        }

        var keyboard = top.Select((t, i) => (IReadOnlyList<Button>)
            [new Button($"✅ {i + 1}", CallbackData.KeepIdea(t.InboxId)), new Button($"✖ {i + 1}", CallbackData.DropIdea(t.InboxId))]).ToList();
        return new OutMessage(html.ToString(), keyboard);
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
