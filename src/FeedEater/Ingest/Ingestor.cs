using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Fetch;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Text;

namespace FeedEater.Ingest;

/// <summary>Copies new Miniflux entries into the archive, then embeds every row that has no vector yet.</summary>
public sealed class Ingestor(
    MinifluxClient miniflux, ItemStore items, StoryClusterer clusterer, PageEnricher pages, LiteLlmClient llm, IOptions<FeedEaterOptions> options,
    LoopHealth health, TimeProvider time, ILogger<Ingestor> logger)
    : PollingLoop(health, time, logger)
{
    public const string LoopName = "miniflux";
    public const string EmbedName = "embeddings";

    private const int RetryChars = 2000;
    private readonly HashSet<long> skipped = [];

    protected override string Name => LoopName;
    protected override TimeSpan Interval => options.Value.Miniflux.PollInterval;

    protected override async Task PollAsync(CancellationToken ct)
    {
        var added = await IngestAsync(ct);
        var embedded = await EmbedSafelyAsync(ct);
        var assigned = await items.AssignProfileKeysAsync(ct);
        var linked = await clusterer.RunAsync(ct);
        var fetched = await pages.RunAsync(ct);
        Logger.LogInformation("Ingested {Added} entries, embedded {Embedded}, assigned {Assigned} profile keys, clustered {Linked}, fetched {Fetched} pages", added, embedded, assigned, linked, fetched);
    }

    /// <summary>An embedding outage is its own health entry, so it is not reported as a Miniflux outage; the next poll retries.</summary>
    private async Task<int> EmbedSafelyAsync(CancellationToken ct)
    {
        try
        {
            var done = await EmbedPendingAsync(ct);
            Health.Succeeded(EmbedName);
            return done;
        }
        catch (Exception ex) when (ex is HttpRequestException or BudgetExceededException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Logger.LogWarning(ex, "Embedding failed; items stay unembedded until the next poll");
            Health.Failed(EmbedName, ex.Message);
            return 0;
        }
    }

    internal async Task<int> IngestAsync(CancellationToken ct)
    {
        var pageSize = options.Value.Miniflux.PageSize;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pageSize, 0);
        var added = 0;
        while (true)
        {
            var page = await miniflux.EntriesAfterAsync(await items.MaxEntryIdAsync(ct), pageSize, ct);
            foreach (var e in page)
            {
                await items.UpsertFeedAsync(new Feed(e.FeedId, e.FeedTitle, e.Category, e.SiteUrl), ct);
                if (await items.InsertAsync(ToNewItem(e), ct) is not null)
                {
                    added++;
                }
            }

            if (page.Count < pageSize)
            {
                return added;
            }
        }
    }

    internal async Task<int> EmbedPendingAsync(CancellationToken ct)
    {
        var o = options.Value;
        var done = 0;
        while (true)
        {
            var batch = await items.UnembeddedAsync(o.Llm.EmbedBatch, skipped.ToArray(), ct);
            if (batch.Count == 0)
            {
                return done;
            }

            IReadOnlyList<float[]> vectors;
            try
            {
                vectors = await llm.EmbedAsync(batch.Select(b => EmbedText(b.Title, b.Content, b.Url, o.Caps.EmbedChars)).ToList(), "embed", ct);
            }
            catch (HttpRequestException ex) when (IsRejection(ex))
            {
                done += await EmbedOneByOneAsync(batch, ct);
                continue;
            }

            await items.SetEmbeddingsAsync(batch.Select((b, i) => (b.Id, vectors[i])).ToList(), ct);
            done += batch.Count;
        }
    }

    /// <summary>400, 413 or 422: the API refused this input. Auth, routing, rate limits and outages are not the row's fault and must retry.</summary>
    private static bool IsRejection(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity;

    /// <summary>
    /// A batch was rejected, so one input is probably too long. Embeds each row alone, retries once with the text cut,
    /// and parks a row that still fails so it cannot stall the rows behind it until the next restart.
    /// </summary>
    private async Task<int> EmbedOneByOneAsync(IReadOnlyList<PendingEmbed> batch, CancellationToken ct)
    {
        var maxChars = options.Value.Caps.EmbedChars;
        var done = 0;
        foreach (var b in batch)
        {
            foreach (var chars in new[] { maxChars, Math.Min(maxChars, RetryChars) })
            {
                try
                {
                    var vectors = await llm.EmbedAsync([EmbedText(b.Title, b.Content, b.Url, chars)], "embed", ct);
                    await items.SetEmbeddingsAsync([(b.Id, vectors[0])], ct);
                    done++;
                    break;
                }
                catch (HttpRequestException ex) when (IsRejection(ex) && chars == maxChars && maxChars > RetryChars)
                {
                }
                catch (HttpRequestException ex) when (IsRejection(ex))
                {
                    Logger.LogWarning("Embedding rejected for item {ItemId}; skipping it until restart", b.Id);
                    skipped.Add(b.Id);
                    break;
                }
            }
        }

        return done;
    }

    /// <summary>Title and the start of the text; the URL when both are empty, because the API rejects empty input.</summary>
    internal static string EmbedText(string title, string content, string url, int maxChars)
    {
        var text = $"{title}\n{(content.Length <= maxChars ? content : content[..maxChars])}".Trim();
        return text.Length > 0 ? text : url;
    }

    private static NewItem ToNewItem(MinifluxEntry e)
    {
        var (link, hn) = LinkExtractor.Find(e.Url, e.Content);
        return new(e.Id, e.FeedId, e.Url, UrlCanonicalizer.Canonical(e.Url), TitleHash.Of(e.Title), e.Title,
            e.PublishedAt.UtcDateTime, HtmlText.ToPlain(e.Content), link, hn);
    }
}
