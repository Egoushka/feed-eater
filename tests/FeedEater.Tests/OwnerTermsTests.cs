using System.Text.RegularExpressions;

namespace FeedEater.Tests;

/// <summary>
/// Nothing shipped in the image names the owner, their hosts or their projects (v0.7 principle 3). The scan is the same as
/// <c>grep -rniE '...' src config profile.example.json Dockerfile</c>; a file that must match is listed in owner-terms.allowlist.
/// </summary>
public sealed class OwnerTermsTests
{
    private static readonly Regex Terms = new(
        @"yehor|egoushka|hrabovsk|100\.64\.|hedzer|kyiv|jarvis|homelab", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private static readonly string[] Scanned = ["src", "config", "profile.example.json", "Dockerfile"];
    private static readonly string[] Skipped = ["bin", "obj"];

    [Fact]
    public void Shipped_files_match_no_owner_term_except_the_allowlisted_ones()
    {
        var allowed = Allowlist();
        var matching = ScannedFiles().Where(f => Terms.IsMatch(File.ReadAllText(RepoRoot.File(f)))).ToList();

        Assert.Empty(matching.Except(allowed.Keys));
        Assert.Empty(allowed.Keys.Except(matching));   // a stale entry would hide the next leak in that file
    }

    [Fact]
    public void Every_allowlist_entry_says_why()
    {
        Assert.NotEmpty(Allowlist());
        Assert.All(Allowlist(), e => Assert.False(string.IsNullOrWhiteSpace(e.Value), $"{e.Key} has no reason"));
    }

    private static IEnumerable<string> ScannedFiles() => Scanned
        .SelectMany(s => Directory.Exists(RepoRoot.File(s)) ? Directory.EnumerateFiles(RepoRoot.File(s), "*", SearchOption.AllDirectories) : [RepoRoot.File(s)])
        .Select(f => Path.GetRelativePath(RepoRoot.Path, f).Replace('\\', '/'))
        .Where(f => !f.Split('/').Any(Skipped.Contains));

    /// <summary>Lines of <c>path | reason</c>; blank lines and lines starting with # are ignored.</summary>
    private static Dictionary<string, string> Allowlist() => File.ReadAllLines(RepoRoot.File("tests/FeedEater.Tests/owner-terms.allowlist"))
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l => l.Split('|', 2, StringSplitOptions.TrimEntries))
        .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
}
