using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Watch;

/// <summary>
/// Matches GitHub release items in the archive to what the owner runs. A release newer than the running version gets a short read
/// (what changed, breaking or not, with evidence). One that mentions security, a CVE, a vulnerability or breaking goes to Telegram on its
/// own; the rest ride in the next digest header. Each release is recorded and announced once. Nothing is ever applied.
/// </summary>
public sealed partial class ReleaseWatcher(
    WatchSource source, ItemStore items, ReleaseStore releases, LiteLlmClient llm, TelegramClient telegram, QuietHours quiet,
    IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<ReleaseWatcher> logger)
    : PollingLoop(health, time, logger)
{
    private const int NotesMaxTokens = 300;
    private const int NotesChars = 4000;

    protected override string Name => "release-watch";
    protected override TimeSpan Interval => options.Value.Miniflux.PollInterval;

    protected override async Task PollAsync(CancellationToken ct) => Logger.LogInformation("Release watch recorded {Count} releases", await RunAsync(ct));

    internal async Task<int> RunAsync(CancellationToken ct)
    {
        var watched = (await source.LoadAsync(ct)).ToDictionary(p => p.Repo, StringComparer.Ordinal);
        if (watched.Count == 0)
        {
            return 0;
        }

        var recorded = 0;
        foreach (var item in await items.ReleaseItemsAsync(Time.GetUtcNow().AddDays(-3), 300, ct))
        {
            if (ReleaseUrl().Match(item.Url) is not { Success: true } m)
            {
                continue;
            }

            var repo = m.Groups[1].Value.ToLowerInvariant();
            var tag = WebUtility.UrlDecode(m.Groups[2].Value);
            if (!watched.TryGetValue(repo, out var product) || Versions.IsPrerelease(tag) || Versions.Parse(tag) is not { } version
                || await releases.ExistsAsync(repo, tag, ct))
            {
                continue;
            }

            var newer = product.Running is { } running && Versions.Compare(version, running) > 0;
            var row = new ReleaseRow { Repo = repo, Tag = tag, Version = Versions.Text(version), Title = item.Title, Url = item.Url, ItemId = item.Id, Newer = newer };
            if (newer)
            {
                if (await ReadAsync(product, row, item, ct) is not { } read)
                {
                    continue;   // the model was unavailable: not recorded, so the next poll tries again
                }

                row = read;
            }

            if (await releases.AddAsync(row, ct))
            {
                recorded++;
                if (row.Urgent)
                {
                    await AnnounceAsync(product, row, ct);
                }
            }
        }

        return recorded;
    }

    private async Task<ReleaseRow?> ReadAsync(WatchedProduct product, ReleaseRow row, ReleaseItem item, CancellationToken ct)
    {
        var o = options.Value;
        var notes = item.Content.Length <= NotesChars ? item.Content : item.Content[..NotesChars];
        var (system, user) = Prompts.Release(
            string.Join(", ", product.Services) + " (" + product.Repo + ")", product.RunningText ?? "unknown", $"{row.Tag} ({item.Title})", notes);
        ReleaseNote? note;
        try
        {
            note = LlmJson.Release((await llm.ChatAsync(o.Llm.ReadModel, system, user, NotesMaxTokens, "release", ct)).Content);
        }
        catch (Exception ex) when (ex is HttpRequestException or BudgetExceededException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Reading the {Repo} {Tag} notes failed", row.Repo, row.Tag);
            return null;
        }

        return row with
        {
            Summary = note?.Changes,
            Breaking = note?.Breaking ?? "unknown",
            Evidence = note?.Evidence,
            Urgent = Urgent().IsMatch(item.Title + " " + item.Content),
        };
    }

    private async Task AnnounceAsync(WatchedProduct product, ReleaseRow row, CancellationToken ct)
    {
        if (options.Value.Telegram.Token.Length == 0 || await quiet.IsQuietAsync(ct))
        {
            return;   // left for the next digest
        }

        try
        {
            await telegram.SendAsync(options.Value.Telegram.AllowedUserId, Message(product, row), ct);
            await releases.MarkAnnouncedAsync(row.Repo, row.Tag, Time.GetUtcNow(), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TelegramException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Announcing {Repo} {Tag} failed; it goes in the next digest", row.Repo, row.Tag);
        }
    }

    internal static OutMessage Message(WatchedProduct product, ReleaseRow r)
    {
        static string E(string t) => WebUtility.HtmlEncode(t);
        var link = Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? $"<a href=\"{E(r.Url)}\">release notes</a>" : "release notes";
        var text = $"<b>You run {E(string.Join(", ", product.Services))} {E(product.RunningText ?? "(version unknown)")}; {E(r.Version)} is out</b>";
        if (r.Summary is not null)
        {
            text += $"\n{E(r.Summary)}";
        }

        text += $"\nBreaking: {E(r.Breaking ?? "unknown")}";
        if (r.Evidence is not null)
        {
            text += $" · \"{E(r.Evidence)}\"";
        }

        return new OutMessage($"{text}\nMentions security or breaking changes · {link}");
    }

    [GeneratedRegex(@"^https://github\.com/([^/]+/[^/]+)/releases/tag/([^/?#]+)")]
    private static partial Regex ReleaseUrl();

    [GeneratedRegex(@"security|\bCVE-\d|vulnerab|breaking", RegexOptions.IgnoreCase)]
    private static partial Regex Urgent();
}
