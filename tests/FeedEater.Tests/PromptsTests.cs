using FeedEater.Digest;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class PromptsTests
{
    private static readonly Profile Homelab = new() { Key = "homelab", Kind = "project", Description = "Hetzner VPS. About 120 containers." };
    private static readonly Profile Postgres = new() { Key = "postgres", Kind = "topic", Description = "PostgreSQL internals." };

    [Fact]
    public void Triage_lists_keys_with_one_liners_and_clips_the_text()
    {
        var (system, user) = Prompts.Triage("Backend dev.", [Homelab, Postgres], "Title", "Feed", new string('x', 5000), 2000);

        Assert.Contains("Backend dev.", system, StringComparison.Ordinal);
        Assert.Contains("\"relevance\"", system, StringComparison.Ordinal);
        Assert.Contains("- homelab (project): Hetzner VPS.", user, StringComparison.Ordinal);
        Assert.DoesNotContain("About 120 containers", user, StringComparison.Ordinal);
        Assert.Contains(new string('x', 2000), user, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 2001), user, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_gives_the_matched_project_in_full()
    {
        var (system, user) = Prompts.Read("Backend dev.", [Homelab, Postgres], Homelab, "T", "https://u", "F", "body", 24000);

        Assert.Contains("English", system, StringComparison.Ordinal);
        Assert.Contains("Best match: homelab: Hetzner VPS. About 120 containers.", user, StringComparison.Ordinal);
        Assert.Contains("URL: https://u", user, StringComparison.Ordinal);
    }
}
