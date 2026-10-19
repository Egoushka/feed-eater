using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Llm;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Search;

/// <summary>The answer and the items it cites, in source-number order; <c>Numbers</c> holds each citation's [n] marker in the answer.</summary>
public sealed record AskResult(string Answer, IReadOnlyList<SearchHit> Citations)
{
    internal IReadOnlyList<int> Numbers { get; init; } = [];
}

/// <summary>
/// A question answered from the archive: the top hits go to the read model as numbered sources, every [n] in the answer must name
/// one of them, and the reply lists the cited items as links.
/// </summary>
public sealed partial class ArchiveAnswer(ArchiveSearch search, ItemStore items, LiteLlmClient llm, IOptions<FeedEaterOptions> options)
{
    private const int Sources = 8;
    private const int SourceChars = 800;
    private const int MaxAnswer = 1500;
    private const int MaxTokens = 400;

    /// <summary>The Telegram reply: the answer and its cited items as HTML.</summary>
    public async Task<OutMessage> AskAsync(string question, CancellationToken ct) => new(Render(await AnswerAsync(question, ct)));

    /// <summary>The answer and the cited hits, ordered by their source number.</summary>
    public async Task<AskResult> AnswerAsync(string question, CancellationToken ct)
    {
        var hits = await search.SearchAsync(question, null, null, null, null, Sources, ct);
        if (hits.Count == 0)
        {
            return new AskResult(Prompts.NothingFound, []);
        }

        var zone = options.Value.Zone;
        var sources = new List<string>();
        foreach (var hit in hits)
        {
            var item = await items.GetAsync(hit.Id, ct);
            var text = item?.Summary ?? item?.Content ?? hit.Summary ?? "";
            sources.Add($"{hit.Title} ({hit.Feed}, {Local(hit.PublishedAt, zone)})\n{Clip(text, SourceChars)}");
        }

        var (system, user) = Prompts.Ask(question, sources);
        var reply = await llm.ChatAsync(options.Value.Llm.ReadModel, system, user, MaxTokens, "ask", ct);
        var (answer, cited) = Check(reply.Content, hits.Count);
        var numbers = cited.Order().ToList();
        return new AskResult(answer, numbers.Select(n => hits[n - 1]).ToList()) { Numbers = numbers };
    }

    /// <summary>Drops [n] markers that name no source; returns the cleaned answer and the cited source numbers in order.</summary>
    internal static (string Answer, IReadOnlyList<int> Cited) Check(string answer, int sources)
    {
        var cited = new List<int>();
        var cleaned = Marker().Replace(answer.Trim(), m =>
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (n < 1 || n > sources)
            {
                return "";
            }

            if (!cited.Contains(n))
            {
                cited.Add(n);
            }

            return m.Value;
        });
        return (Clip(cleaned, MaxAnswer), cited);
    }

    private string Render(AskResult result)
    {
        var html = new StringBuilder(WebUtility.HtmlEncode(result.Answer));
        if (result.Citations.Count == 0)
        {
            return html.ToString();
        }

        var zone = options.Value.Zone;
        html.Append("\n\n");
        for (var i = 0; i < result.Citations.Count; i++)
        {
            var h = result.Citations[i];
            var title = WebUtility.HtmlEncode(Clip(h.Title, 120));
            var link = DigestFormatter.IsLinkable(h.Url) ? $"<a href=\"{WebUtility.HtmlEncode(h.Url)}\">{title}</a>" : title;
            html.Append(CultureInfo.InvariantCulture, $"[{result.Numbers[i]}] {link} · <i>{WebUtility.HtmlEncode(Clip(h.Feed, 60))}, {Local(h.PublishedAt, zone)}</i>\n");
        }

        return html.ToString().TrimEnd();
    }

    private static string Local(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [GeneratedRegex(@"\[(\d{1,3})\]", RegexOptions.None, 250)]
    private static partial Regex Marker();
}
