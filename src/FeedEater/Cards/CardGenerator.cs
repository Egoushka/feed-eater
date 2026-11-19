using Microsoft.Extensions.Options;

namespace FeedEater.Cards;

/// <summary>Joins the inventory to ranked feed items and outside signals and writes cards. No model call: a card states what was found and what was not read.</summary>
public sealed class CardGenerator(
    CardStore store, HnSearch hn, DepsDevClient depsDev, IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<CardGenerator> logger)
{
    public async Task<int> GenerateAsync(CancellationToken ct)
    {
        var o = options.Value.Cards;
        var since = time.GetUtcNow().UtcDateTime.AddDays(-o.LookbackDays);
        var lookups = o.MaxLookups;
        var inventory = await store.InventoryAsync(ct);
        var cards = new List<(int Weight, NewCard Card)>();

        foreach (var group in inventory.Where(i => CardRules.Searchable(i.Term)).GroupBy(i => i.Term, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var usages = group.ToList();
            var signals = new List<CardSignal>();
            foreach (var hit in await store.HitsAsync(group.Key, since, o.MinRelevance, ct))
            {
                if (CardRules.Mentions(hit.Title, group.Key))
                {
                    signals.Add(new CardSignal("your feeds", hit.Url, hit.Title, CardRules.IsVersionBump(hit.Title, group.Key), hit.Id, hit.PublishedAt, 20 + hit.Relevance * 10));
                }
            }

            signals.AddRange(await LookupAsync(() => hn.StoriesAsync(group.Key, since, o.HnMinPoints, ct), () => lookups-- > 0, group.Key, "Hacker News", ct));
            if (o.DepsDevBase.Length > 0)
            {
                foreach (var usage in usages.Where(u => u.Kind is "nuget" or "npm").DistinctBy(u => (u.Name, u.Version)))
                {
                    signals.AddRange(await LookupAsync(() => depsDev.AdvisoriesAsync(usage, ct), () => lookups-- > 0, group.Key, "deps.dev", ct));
                }
            }

            if (CardRules.Build(new CardCandidate(group.Key, usages, signals)) is { } card)
            {
                cards.Add((signals.Where(s => !s.VersionBump).Max(s => s.Weight), card));
            }
        }

        var added = 0;
        foreach (var (_, card) in cards.OrderByDescending(c => c.Weight).Take(o.MaxNewCards))
        {
            if (await store.AddAsync(card, ct))
            {
                added++;
            }
        }

        logger.LogInformation("Cards: {Added} new of {Candidates} candidates", added, cards.Count);
        return added;
    }

    /// <summary>One outside lookup; a failure is a missing signal for this term, never a failed run.</summary>
    private async Task<IReadOnlyList<CardSignal>> LookupAsync(Func<Task<IReadOnlyList<CardSignal>>> call, Func<bool> allowed, string term, string source, CancellationToken ct)
    {
        if (!allowed())
        {
            return [];
        }

        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("{Source} lookup for {Term} failed: {Reason}", source, term, ex.GetType().Name);
            return [];
        }
    }
}
