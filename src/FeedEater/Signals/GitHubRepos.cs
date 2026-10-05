using System.Globalization;
using System.Text.RegularExpressions;

namespace FeedEater.Signals;

public sealed record RepoFacts(string FullName, DateTimeOffset CreatedAt, DateTimeOffset? PushedAt, int Stars, string? ReleaseTag, DateTimeOffset? ReleasedAt)
{
    /// <summary>"created 2024-03-02, last push 2026-10-01, 1,240 stars, latest release v2.3.0 on 2026-09-20"</summary>
    public string Line()
    {
        var parts = new List<string> { $"created {Day(CreatedAt)}" };
        if (PushedAt is { } pushed)
        {
            parts.Add($"last push {Day(pushed)}");
        }

        parts.Add($"{Stars.ToString("N0", CultureInfo.InvariantCulture)} stars");
        if (ReleaseTag is not null)
        {
            parts.Add(ReleasedAt is { } released ? $"latest release {ReleaseTag} on {Day(released)}" : $"latest release {ReleaseTag}");
        }

        return string.Join(", ", parts);
    }

    private static string Day(DateTimeOffset d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public static partial class GitHubRepos
{
    private static readonly HashSet<string> NotOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "sponsors", "orgs", "topics", "features", "settings", "marketplace", "about", "login", "pricing", "explore", "apps", "users",
        "search", "notifications", "collections", "events", "trending", "enterprise", "customer-stories", "readme", "security",
    };

    /// <summary>The first github.com/owner/repo in the item URL, else in its text. Paths below the repo (issues, pulls, blob) are ignored.</summary>
    public static (string Owner, string Repo)? Find(string url, string text)
    {
        foreach (var source in new[] { url, text })
        {
            foreach (Match m in Link().Matches(source))
            {
                var owner = m.Groups[1].Value;
                var repo = m.Groups[2].Value.TrimEnd('.');
                if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    repo = repo[..^4];
                }

                if (repo.Length > 0 && !NotOwners.Contains(owner))
                {
                    return (owner, repo);
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9.-])(?:www\.)?github\.com/([A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/([A-Za-z0-9._-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Link();
}
