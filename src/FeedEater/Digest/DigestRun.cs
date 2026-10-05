using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Profiles;
using FeedEater.Ranking;
using FeedEater.Signals;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.Text;

namespace FeedEater.Digest;

public sealed record ForceOutcome(bool Sent, string Text);

public sealed record Selection(int Candidates, int Triaged, IReadOnlyList<long> ItemIds, IReadOnlyList<string> Notes);

/// <summary>
/// One digest: rank candidates, triage the top N with the small model, read the top M with the strong one, send.
/// Every step is stored, so a rerun resumes: results per item in triage/reads, delivered messages in sent_count.
/// </summary>
public sealed class DigestRun(
    ItemStore items, ProfileStore profiles, FeedbackStore feedback, AnalysisStore analysis, DigestStore digests, UsageStore usage,
    LiteLlmClient llm, MinifluxClient miniflux, GitHubStarsClient github, TelegramClient telegram, LoopHealth health,
    IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<DigestRun> logger)
{
    private const int TriageMaxTokens = 200;
    private const int ReadMaxTokens = 600;

    public async Task RunAsync(string date, CancellationToken ct)
    {
        await digests.CloseStaleAsync(date, ct);
        var digest = await digests.GetAsync(date, ct);
        if (digest?.Status == "sent" || (digest?.Status == "failed" && digest.ItemIds.Length == 0))
        {
            return;
        }

        if (digest is null || digest.ItemIds.Length == 0)
        {
            await digests.CreateAsync(date, ct);
            var selection = await SelectAsync(ct);
            if (selection.ItemIds.Count == 0)
            {
                var why = selection.Notes.Count > 0 ? string.Join("; ", selection.Notes) : $"none of {selection.Candidates} new items passed triage";
                await telegram.SendAsync(options.Value.Telegram.AllowedUserId, new OutMessage($"No digest today: {WebUtility.HtmlEncode(why)}"), ct);
                await digests.MarkFailedAsync(date, why, ct);
                return;
            }

            await digests.SetSelectionAsync(date, selection.Candidates, selection.Triaged, [.. selection.ItemIds],
                selection.Notes.Count == 0 ? null : string.Join('\n', selection.Notes), ct);
            digest = (await digests.GetAsync(date, ct))!;
        }

        await SendAsync(digest, ct);
    }

    /// <summary>
    /// A digest on demand. A digest that was already sent is not sent again unless <paramref name="resend"/>; one that never
    /// went out is built (or resumed) now. Returns what happened, in words.
    /// </summary>
    public async Task<ForceOutcome> ForceAsync(string date, bool resend, CancellationToken ct)
    {
        var digest = await digests.GetAsync(date, ct);
        if (digest?.Status == "sent")
        {
            if (!resend)
            {
                return new ForceOutcome(false, "Today's digest was already sent; nothing sent again.");
            }

            await digests.SetSentCountAsync(date, 0, ct);
            await SendAsync(digest with { SentCount = 0 }, ct);
            return new ForceOutcome(true, "Sent again.");
        }

        await digests.ReopenAsync(date, ct);
        await RunAsync(date, ct);
        var after = await digests.GetAsync(date, ct);
        return after?.Status == "sent"
            ? new ForceOutcome(true, "Sent.")
            : new ForceOutcome(false, $"No digest: {after?.Error ?? "nothing to send"}");
    }

    internal async Task<Selection> SelectAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var notes = new List<string>();
        if (health.IsDown(Ingestor.LoopName))
        {
            notes.Add("Miniflux was unreachable at the last poll; some items may be missing");
        }

        if (health.IsDown(Ingestor.EmbedName))
        {
            notes.Add("Embeddings were unavailable at the last poll; new items may be missing");
        }

        var zone = o.Zone;
        var localDay = TimeZoneInfo.ConvertTime(now, zone).Date;
        var dayStart = new DateTimeOffset(localDay, zone.GetUtcOffset(localDay));
        var floor = now - TimeSpan.FromDays(o.Caps.CandidateDays);
        var candidates = await items.CandidatesAsync(floor, dayStart, ct);
        var profileList = await profiles.AllAsync(ct);
        var taste = Taste.Build(
            await feedback.PositiveVectorsAsync(o.Caps.Centroid, ct), await feedback.NegativeVectorsAsync(o.Caps.Centroid, ct),
            await feedback.FeedVotesAsync(ct), o.Weights);
        var scored = candidates.Select(c => Scorer.Score(c.Id, c.FeedId, c.Embedding, profileList, taste, o.Weights)).ToList();
        await items.SetScoresAsync(scored, ct);

        var byId = candidates.ToDictionary(c => c.Id);
        var keys = profileList.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var about = ProfileFile.Load(o.ProfilePath).About;

        var budgetSpent = false;
        var triaged = new List<(Scored Score, TriageResult Triage)>();
        int triageCalls = 0, triageTransportFailures = 0;
        foreach (var s in scored.OrderByDescending(s => s.Score).Take(o.Caps.Triage))
        {
            var t = await analysis.GetTriageAsync(s.ItemId, ct);
            if (t is null)
            {
                if (budgetSpent)
                {
                    break;
                }

                var c = byId[s.ItemId];
                var (system, user) = Prompts.Triage(about, profileList, c.Title, c.FeedTitle, c.Content, o.Caps.TriageChars);
                triageCalls++;
                try
                {
                    t = LlmJson.Triage((await llm.ChatAsync(o.Llm.TriageModel, system, user, TriageMaxTokens, "triage", ct)).Content, keys);
                    await analysis.SaveTriageAsync(s.ItemId, t, o.Llm.TriageModel, ct);
                }
                catch (BudgetExceededException)
                {
                    notes.Add("LLM budget reached during triage");
                    budgetSpent = true;
                    triageCalls--;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    logger.LogWarning(ex, "Triage of item {Item} failed; counted as marginal", s.ItemId);
                    triageTransportFailures++;
                    t = new TriageResult { Relevance = 1, Reason = "triage failed" };
                }
            }

            triaged.Add((s, t));
        }

        if (triageCalls > 0 && triageTransportFailures == triageCalls && !budgetSpent)
        {
            throw new InvalidOperationException($"All {triageCalls} triage calls failed; the LLM looks unreachable");
        }

        var picked = triaged
            .Where(x => x.Triage.Relevance >= o.Caps.MinRelevance)
            .OrderByDescending(x => x.Triage.Relevance).ThenByDescending(x => x.Score.Score)
            .Take(o.Caps.Read)
            .ToList();

        var repoFacts = new Dictionary<(string, string), string?>();
        var read = new List<(Scored Score, TriageResult Triage, ReadResult Read)>();
        int readCalls = 0, readTransportFailures = 0, readsCached = 0;
        foreach (var (s, t) in picked)
        {
            var r = await analysis.GetReadAsync(s.ItemId, ct);
            if (r is not null)
            {
                readsCached++;
            }

            if (r is null && !budgetSpent)
            {
                readCalls++;
                var c = byId[s.ItemId];
                try
                {
                    var text = await FullTextAsync(c, ct);
                    var match = profileList.FirstOrDefault(p => p.Key == (t.Project ?? s.ProfileKey));
                    var facts = await RepoFactsAsync(repoFacts, c.Url, text, ct);
                    var (system, user) = Prompts.Read(about, profileList, match, c.Title, c.Url, c.FeedTitle, text, o.Caps.ReadChars, facts);
                    r = LlmJson.Read((await llm.ChatAsync(o.Llm.ReadModel, system, user, ReadMaxTokens, "read", ct)).Content, keys);
                    if (r is not null)
                    {
                        await analysis.SaveReadAsync(s.ItemId, r, o.Llm.ReadModel, ct);
                    }
                }
                catch (BudgetExceededException)
                {
                    notes.Add("LLM budget reached; fewer highlights today");
                    budgetSpent = true;
                    readCalls--;
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    logger.LogWarning(ex, "Reading item {Item} failed; left out of the digest", s.ItemId);
                    readTransportFailures++;
                }
            }

            if (r is not null)
            {
                read.Add((s, t, r));
            }
        }

        if (readCalls > 0 && readTransportFailures == readCalls && readsCached == 0 && !budgetSpent)
        {
            throw new InvalidOperationException($"All {readCalls} reads failed; the LLM looks unreachable");
        }

        var ordered = read
            .OrderBy(x => x.Read.Suggestion is null ? 1 : 0)
            .ThenByDescending(x => x.Triage.Relevance)
            .ThenByDescending(x => x.Score.Score)
            .Select(x => x.Score.ItemId)
            .ToList();
        return new Selection(candidates.Count, triaged.Count, ordered, notes);
    }

    /// <summary>One line of GitHub facts for a repo the item links, fetched once per repo per run; null when there is no link or GitHub does not answer.</summary>
    private async Task<string?> RepoFactsAsync(Dictionary<(string, string), string?> cache, string url, string text, CancellationToken ct)
    {
        if (GitHubRepos.Find(url, text) is not var (owner, repo))
        {
            return null;
        }

        var key = (owner.ToLowerInvariant(), repo.ToLowerInvariant());
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string? line = null;
        try
        {
            line = (await github.RepoFactsAsync(owner, repo, ct))?.Line();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogDebug(ex, "GitHub facts for {Owner}/{Repo} unavailable", owner, repo);
        }

        if (line is null)
        {
            logger.LogDebug("No GitHub facts for {Owner}/{Repo}", owner, repo);
        }

        cache[key] = line;
        return line;
    }

    private async Task<string> FullTextAsync(Candidate c, CancellationToken ct)
    {
        if (c.Content.Length >= options.Value.Caps.ShortContentChars || c.MinifluxEntryId is not { } entryId)
        {
            return c.Content;
        }

        try
        {
            var text = HtmlText.ToPlain(await miniflux.FetchContentAsync(entryId, ct));
            if (text.Length <= c.Content.Length)
            {
                return c.Content;
            }

            await items.SetContentAsync(c.Id, text, ct);
            return text;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Full text for item {Item} unavailable; using the feed text", c.Id);
            return c.Content;
        }
    }

    private async Task SendAsync(DigestRow digest, CancellationToken ct)
    {
        var chat = options.Value.Telegram.AllowedUserId;
        var messages = await BuildMessagesAsync(digest, ct);
        for (var i = digest.SentCount; i < messages.Count; i++)
        {
            await telegram.SendAsync(chat, messages[i], ct);
            await digests.SetSentCountAsync(digest.LocalDate, i + 1, ct);
        }

        await digests.MarkSentAsync(digest.LocalDate, time.GetUtcNow(), ct);
    }

    private async Task<List<OutMessage>> BuildMessagesAsync(DigestRow d, CancellationToken ct)
    {
        var shown = await items.DigestItemsAsync(d.ItemIds, ct);
        var now = time.GetUtcNow();
        var votes = await feedback.VotesSinceAsync(now.AddDays(-1), ct);
        var week = await feedback.VotesSinceAsync(now.AddDays(-7), ct);
        var weekUpRate = DigestStats.UpRate(week);
        var spend = await usage.SpendSinceAsync(DigestStats.MonthStart(now, options.Value.Zone), ct);
        var byProject = shown
            .GroupBy(v => v.Project ?? "other")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()))
            .ToList();
        var header = DigestFormatter.Header(new DigestHeader(
            DateOnly.ParseExact(d.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), shown.Count, d.Candidates, byProject,
            votes.Up, votes.Down, spend, weekUpRate, d.Note is null ? [] : d.Note.Split('\n')));
        return [header, .. shown.Select(v => DigestFormatter.Item(v, null, null))];
    }
}
