using System.Xml;
using FeedEater.Fetch;
using FeedEater.Storage;

namespace FeedEater.Sources;

/// <summary><see cref="Error"/> is null when the feed is subscribed (<see cref="Existing"/>: it already was).</summary>
public sealed record AddFeedResult(string? Error, string Title = "", bool Existing = false, int Items = 0)
{
    public bool Ok => Error is null;
}

public sealed record ImportResult(string? Error, int Added = 0, int Existing = 0)
{
    public bool Ok => Error is null;
}

/// <summary>Subscribing, importing and exporting, shared by /ui/sources, the Telegram /feeds command and the import-opml CLI.</summary>
public sealed class FeedManager(FeedStore feeds, FeedReader reader, FeedPoller poller, ItemStore items)
{
    /// <summary>
    /// Subscribes to a feed URL, or to the feed the page at that address links to, and fetches it once now so its recent entries
    /// show up without waiting for the next poll. Nothing is stored when the address is no feed and has no feed link.
    /// </summary>
    public async Task<AddFeedResult> AddAsync(string input, CancellationToken ct)
    {
        var text = input.Trim();
        if (text.Length is 0 or > 2000 || !Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return new AddFeedResult("That is not a web address.");
        }

        var read = await reader.ReadAsync(url, new FeedConditions(), ct);
        if (read.Status == FeedReadStatus.NotAFeed && read.Page is not null && FeedDiscoverer.FindFeed(read.Page, read.FinalUri ?? url) is { } found)
        {
            url = new Uri(found);
            read = await reader.ReadAsync(url, new FeedConditions(), ct);
        }

        if (read.Status != FeedReadStatus.Ok)
        {
            return new AddFeedResult(read.Status == FeedReadStatus.NotAFeed ? "No feed found at that address." : $"Could not read it: {read.Error}.");
        }

        var feed = read.Feed!;
        var title = feed.Title.Length > 0 ? feed.Title : url.Host;
        var (id, added) = await feeds.AddAsync(title, url.AbsoluteUri, feed.SiteUrl, null, ct);
        if (!added)
        {
            return new AddFeedResult(null, (await feeds.GetAsync(id, ct))?.Title ?? title, Existing: true);
        }

        var stored = (await feeds.GetAsync(id, ct))!;
        return new AddFeedResult(null, title, Items: await poller.ApplyAsync(stored, read, ct));
    }

    /// <summary>
    /// Adds the feeds of an OPML file; folders become categories. Nothing is fetched here: the ingest loop picks the new feeds up at its
    /// next poll, so the CLI can run beside a running service.
    /// </summary>
    public async Task<ImportResult> ImportAsync(string opml, CancellationToken ct)
    {
        IReadOnlyList<OpmlFeed> parsed;
        try
        {
            parsed = Opml.Parse(opml);
        }
        catch (XmlException)
        {
            return new ImportResult("That file is not valid OPML.");
        }

        int added = 0, existing = 0;
        foreach (var f in parsed)
        {
            if ((await feeds.AddAsync(f.Title, f.FeedUrl, f.SiteUrl, f.Category, ct)).Added)
            {
                added++;
            }
            else
            {
                existing++;
            }
        }

        return new ImportResult(null, added, existing);
    }

    public async Task<string> ExportAsync(CancellationToken ct) => Opml.Write(await items.FeedsAsync(ct));
}
