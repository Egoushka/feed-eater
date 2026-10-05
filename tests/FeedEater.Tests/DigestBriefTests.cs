using FeedEater.Digest;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class DigestBriefTests
{
    private static ItemView Item(long id, string? project, string? summary) => new() { Id = id, Project = project, Summary = summary };

    [Fact]
    public void Groups_by_project_largest_first_and_uses_the_first_summary_of_each_group()
    {
        var lines = DigestBrief.Build(
        [
            Item(1, "chargehand", "One."), Item(2, "homelab", "Two."), Item(3, "homelab", "Three."), Item(4, null, "Four."), Item(5, "homelab", "Five."),
        ]);

        Assert.Equal([("homelab", 3, "Two.", 2L), ("chargehand", 1, "One.", 1L), ("other", 1, "Four.", 4L)], lines.Select(l => (l.Project, l.Count, l.Text, l.ItemId)));
    }

    [Fact]
    public void Keeps_five_lines_skips_items_without_a_summary_and_clips_long_text_at_a_word()
    {
        var items = Enumerable.Range(1, 8).Select(i => Item(i, $"p{i}", i == 8 ? null : new string('a', 10) + " " + string.Join(' ', Enumerable.Repeat("word", 60)))).ToList();

        var lines = DigestBrief.Build(items);

        Assert.Equal(5, lines.Count);
        Assert.All(lines, l => Assert.True(l.Text.Length <= 161 && l.Text.EndsWith("…", StringComparison.Ordinal) && !l.Text.EndsWith(" …", StringComparison.Ordinal)));
        Assert.Empty(DigestBrief.Build([Item(1, "x", null), Item(2, "x", "  ")]));
        Assert.Empty(DigestBrief.Build([]));
    }
}
