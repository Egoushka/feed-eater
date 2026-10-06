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

    [Fact]
    public void Both_prompts_forbid_claiming_novelty_without_evidence_and_define_kind_new()
    {
        var (triage, _) = Prompts.Triage("Dev.", [Homelab], "T", "F", "body", 2000);
        var (read, _) = Prompts.Read("Dev.", [Homelab], null, "T", "https://u", "F", "body", 24000);

        foreach (var system in new[] { triage, read })
        {
            Assert.Contains("Never call a project or product new, recent", system, StringComparison.Ordinal);
            Assert.Contains("new to his stack, not new in the world", system, StringComparison.Ordinal);
        }

        Assert.Contains("showing off their own tool is not evidence", read, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"improve\" | \"new\" | \"fyi\"", read, StringComparison.Ordinal);   // contract unchanged
        Assert.DoesNotContain("what is new", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_adds_one_repository_facts_line_only_when_given()
    {
        var (_, without) = Prompts.Read("Dev.", [Homelab], null, "T", "https://u", "F", "body", 24000);
        var (_, with) = Prompts.Read("Dev.", [Homelab], null, "T", "https://u", "F", "body", 24000, "created 2024-03-02, 5 stars");

        Assert.DoesNotContain("Repository facts", without, StringComparison.Ordinal);
        Assert.Contains("Feed: F\nRepository facts: created 2024-03-02, 5 stars\n", with, StringComparison.Ordinal);
    }
}
