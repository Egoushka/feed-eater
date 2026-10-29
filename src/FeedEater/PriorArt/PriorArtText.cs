using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FeedEater.PriorArt;

internal static partial class PriorArtText
{
    /// <summary>A fact's sentence is at most 300 characters in the library.</summary>
    public const int MaxFactText = 300;

    [GeneratedRegex(@"^(gh:[\w.-]+/[\w.-]+|pkg:(nuget|pypi|npm|cargo|go)/[\w./@-]+|url:[\w.-]+(/[\w./%~@-]*)?|name:[a-z0-9][a-z0-9-]*)$")]
    private static partial Regex ToolRefPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,59}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"\[([^\]]*)\]\(([^)\s]+)[^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"[*_`]+")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoDate();

    public static bool IsToolRef(string value) => value.Length <= 160 && ToolRefPattern().IsMatch(value);

    public static bool IsSlug(string value) => SlugPattern().IsMatch(value);

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Slug(string text, int max = 60)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > max)
        {
            slug = slug[..max].TrimEnd('-');
        }

        return slug;
    }

    /// <summary>The first date (yyyy-MM-dd) in a file name, or null.</summary>
    public static DateOnly? DateInName(string name) =>
        IsoDate().Match(name) is { Success: true } m && DateOnly.TryParseExact(m.Value, "yyyy-MM-dd", out var d) ? d : null;

    /// <summary>A tool reference for a URL: <c>gh:owner/name</c>, a package, else <c>url:host/path</c>; null for anything that is not http(s).</summary>
    public static string? RefFromUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? result = (host, parts) switch
        {
            ("github.com", { Length: >= 2 }) => $"gh:{parts[0]}/{(parts[1].EndsWith(".git", StringComparison.Ordinal) ? parts[1][..^4] : parts[1])}",
            ("nuget.org", { Length: >= 2 }) when parts[0] == "packages" => $"pkg:nuget/{parts[1]}",
            ("pypi.org", { Length: >= 2 }) when parts[0] == "project" => $"pkg:pypi/{parts[1]}",
            ("npmjs.com", { Length: >= 2 }) when parts[0] == "package" => $"pkg:npm/{string.Join('/', parts.Skip(1))}",
            _ => $"url:{host}" + (parts.Length > 0 ? "/" + string.Join('/', parts) : ""),
        };
        return IsToolRef(result) ? result : null;
    }

    /// <summary>The markdown as plain words: links keep their text, emphasis and code marks go, runs of space become one.</summary>
    public static string Plain(string markdown) =>
        Spaces().Replace(Emphasis().Replace(MarkdownLink().Replace(markdown, "$1"), ""), " ").Trim();

    /// <summary>The first link of a markdown cell, or null.</summary>
    public static (string Text, string Url)? FirstLink(string markdown) =>
        MarkdownLink().Match(markdown) is { Success: true } m ? (m.Groups[1].Value, m.Groups[2].Value) : null;

    /// <summary>At most <paramref name="max"/> characters, cut at a word and marked with an ellipsis.</summary>
    public static string Cut(string text, int max = MaxFactText)
    {
        text = Spaces().Replace(text, " ").Trim();
        if (text.Length <= max)
        {
            return text;
        }

        var cut = text[..(max - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
    }

    /// <summary>At most <paramref name="words"/> words: the research repo allows quotes of 25.</summary>
    public static string? Words(string? text, int words = 25)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= words ? string.Join(' ', parts) : string.Join(' ', parts.Take(words)) + "…";
    }
}
