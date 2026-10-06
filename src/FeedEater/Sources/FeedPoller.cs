using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Fetch;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Text;

namespace FeedEater.Sources;

/// <summary>
/// The built-in reader's fetch step, run by the ingest loop in place of the Miniflux copy: fetches the feeds that are due (conditional GET),
/// stores new entries and records each feed's state. A failing feed backs off on its own and never fails the others.
/// </summary>
public sealed class FeedPoller(
    FeedStore feeds, FeedReader reader, ItemStore items, LoopHealth health, IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<FeedPoller> logger)
{
    public const string LoopName = "feeds";

    /// <summary>Consecutive failures after which a feed is named in the digest header: one blip is not news.</summary>
    public const int FailingAfter = 3;

    private const int MaxPerPoll = 200;
    private const int MaxNamed = 3;
    private const int MaxError = 300;

    /// <summary>
    /// Fetches every due feed; returns how many entries were new. Whatever goes wrong with one feed is recorded on it and backs it off, so it
    /// never keeps the feeds behind it, or the embedding step after the poll, from running.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var added = 0;
        foreach (var feed in await feeds.DueAsync(time.GetUtcNow(), MaxPerPoll, ct))
        {
            try
            {
                added += await PollAsync(feed, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Feed {Feed} failed unexpectedly ({Failures} in a row)", feed.FeedUrl, feed.FailCount + 1);
                await FailAsync(feed, $"could not process the feed ({ex.GetType().Name})", ct);
            }
        }

        return added;
    }

    internal async Task<int> PollAsync(FeedState feed, CancellationToken ct)
    {
        if (!Uri.TryCreate(feed.FeedUrl, UriKind.Absolute, out var url))
        {
            return await ApplyAsync(feed, new FeedRead(FeedReadStatus.Failed, Error: "not a valid URL"), ct);
        }

        return await ApplyAsync(feed, await reader.ReadAsync(url, new FeedConditions(feed.Etag, feed.LastModified), ct), ct);
    }

    /// <summary>Records the outcome of one read and stores the new entries of a good one; returns how many were new.</summary>
    internal async Task<int> ApplyAsync(FeedState feed, FeedRead read, CancellationToken ct)
    {
        var o = options.Value.Source;
        var now = time.GetUtcNow();
        switch (read.Status)
        {
            case FeedReadStatus.NotModified:
                await feeds.SucceededAsync(feed.Id, feed.Etag, feed.LastModified, null, null, now, now + o.FeedInterval, ct);
                return 0;
            case FeedReadStatus.Ok:
                var added = await StoreAsync(feed, read.Feed!, now, ct);
                await feeds.SucceededAsync(feed.Id, read.ETag, read.LastModified, read.Feed!.Title, read.Feed.SiteUrl, now, now + o.FeedInterval, ct);
                return added;
            default:
                var error = read.Error ?? "not an RSS, Atom or JSON feed";
                logger.LogInformation("Feed {Feed} failed ({Failures} in a row): {Error}", feed.FeedUrl, feed.FailCount + 1, error);
                await FailAsync(feed, error, ct);
                return 0;
        }
    }

    private Task FailAsync(FeedState feed, string error, CancellationToken ct) =>
        feeds.FailedAsync(feed.Id, error.Length <= MaxError ? error : error[..MaxError], time.GetUtcNow() + Delay(options.Value.Source, feed.FailCount + 1), ct);

    /// <summary>The interval doubles with every failure in a row, up to <see cref="SourceOptions.MaxBackoff"/>.</summary>
    internal static TimeSpan Delay(SourceOptions o, int failures) =>
        TimeSpan.FromTicks((long)Math.Min(o.FeedInterval.Ticks * Math.Pow(2, Math.Min(failures, 30)), o.MaxBackoff.Ticks));

    /// <summary>
    /// Oldest first, so duplicate_of points at the earliest copy. A feed's first fetch keeps only entries from the last
    /// <see cref="SourceOptions.BackfillDays"/>; an entry without a date counts as published now, and one dated in the future is clamped to now.
    /// </summary>
    private async Task<int> StoreAsync(FeedState feed, ParsedFeed parsed, DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value.Source;
        var floor = feed.LastFetchedAt is null ? now.UtcDateTime.AddDays(-o.BackfillDays) : DateTime.MinValue;
        var added = 0;
        foreach (var e in parsed.Entries.Reverse())
        {
            var published = e.PublishedAt is { } at && at < now.UtcDateTime ? at : now.UtcDateTime;
            if (published < floor)
            {
                continue;
            }

            var (link, hn) = LinkExtractor.Find(e.Url, e.Html);
            var item = new NewItem(null, feed.Id, e.Url, UrlCanonicalizer.Canonical(e.Url), TitleHash.Of(e.Title), e.Title, published, HtmlText.ToPlain(e.Html), link, hn,
                SourceKey(feed.Id, e.Guid));
            if (await items.InsertAsync(item, ct) is not null)
            {
                added++;
            }
        }

        return added;
    }

    /// <summary><c>feed id:sha256 of the guid</c> (the URL when the entry has none): the same entry in another feed is its own row.</summary>
    internal static string SourceKey(long feedId, string guid) =>
        string.Create(CultureInfo.InvariantCulture, $"{feedId}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(guid)))}");

    /// <summary>Notes for the digest header: the feeds that keep failing, and the reader itself failing.</summary>
    public async Task<IReadOnlyList<string>> NotesAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        if (health.IsDown(LoopName))
        {
            notes.Add("The feed reader failed at the last poll; some items may be missing");
        }

        var failing = await feeds.FailingAsync(FailingAfter, ct);
        if (failing.Count > 0)
        {
            var names = string.Join(", ", failing.Take(MaxNamed).Select(f => f.Title.Length <= 40 ? f.Title : f.Title[..39] + "…"));
            notes.Add($"{(failing.Count == 1 ? "Feed" : "Feeds")} failing: {names}{(failing.Count > MaxNamed ? $" and {failing.Count - MaxNamed} more" : "")}; some items may be missing");
        }

        return notes;
    }
}
