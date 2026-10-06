using FeedEater.Hype;
using FeedEater.Signals;

namespace FeedEater.Tests;

public sealed class AutopsyRulesTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static RepoSnapshot Snapshot(int? stars = 100, string? tag = null) =>
        new(1, "acme/widget", At.AddDays(-90), stars, At.AddDays(-91), tag, At.AddYears(-2));

    private static RepoFacts Facts(int stars = 100, double pushedDaysAgo = 200, string? tag = null, bool archived = false) =>
        new("acme/widget", At.AddYears(-2), At.AddDays(-pushedDaysAgo), stars, tag, null, archived);

    private static Judgement Judge(RepoSnapshot snapshot, RepoFacts? now, IdeaOutcome idea = IdeaOutcome.None, bool saved = false, int related = 0) =>
        AutopsyRules.Judge(snapshot, now, idea, saved, related, At);

    [Theory]
    [InlineData(100, 120, RepoVerdict.Grew)]    // exactly +20%
    [InlineData(100, 119, RepoVerdict.Quiet)]   // one star short
    [InlineData(100, 500, RepoVerdict.Grew)]
    [InlineData(100, 100, RepoVerdict.Quiet)]
    [InlineData(100, 40, RepoVerdict.Quiet)]    // stars can fall
    [InlineData(5, 6, RepoVerdict.Grew)]        // 6 is exactly 120% of 5
    [InlineData(0, 1, RepoVerdict.Grew)]        // a baseline of 0 counts as 1
    [InlineData(0, 0, RepoVerdict.Quiet)]
    public void Grew_needs_twenty_percent_more_stars(int then, int now, RepoVerdict expected) =>
        Assert.Equal(expected, Judge(Snapshot(then), Facts(now)).Verdict);

    [Theory]
    [InlineData(0, RepoVerdict.Alive)]
    [InlineData(29.99, RepoVerdict.Alive)]
    [InlineData(30, RepoVerdict.Alive)]        // exactly 30 days still counts
    [InlineData(30.01, RepoVerdict.Quiet)]
    [InlineData(400, RepoVerdict.Quiet)]
    public void Alive_means_a_push_within_thirty_days(double pushedDaysAgo, RepoVerdict expected) =>
        Assert.Equal(expected, Judge(Snapshot(), Facts(pushedDaysAgo: pushedDaysAgo)).Verdict);

    [Fact]
    public void A_repo_that_never_got_a_push_is_quiet_not_alive() =>
        Assert.Equal(RepoVerdict.Quiet, Judge(Snapshot(), Facts() with { PushedAt = null }).Verdict);

    [Fact]
    public void Gone_is_a_404_or_an_archived_repo_and_beats_every_other_verdict()
    {
        Assert.Equal(RepoVerdict.Gone, Judge(Snapshot(), null).Verdict);
        Assert.Equal(RepoVerdict.Gone, Judge(Snapshot(), Facts(stars: 900, pushedDaysAgo: 1, archived: true)).Verdict);
    }

    [Fact]
    public void Grew_beats_alive()
    {
        var j = Judge(Snapshot(), Facts(stars: 150, pushedDaysAgo: 2));

        Assert.Equal(RepoVerdict.Grew, j.Verdict);
        Assert.True(j.PushedRecently);
    }

    [Fact]
    public void The_figures_behind_the_verdict_are_reported()
    {
        var j = Judge(Snapshot(80), Facts(stars: 100, pushedDaysAgo: 10), IdeaOutcome.Filed, saved: true, related: 3);

        Assert.Equal(25, j.StarsGrowthPercent);
        Assert.Equal(100, j.StarsNow);
        Assert.True(j.PushedRecently);
        Assert.Equal((IdeaOutcome.Filed, true, 3), (j.Idea, j.Saved, j.RelatedLater));
    }

    [Fact]
    public void A_gone_repo_has_no_growth_and_no_push()
    {
        var j = Judge(Snapshot(), null);

        Assert.Null(j.StarsGrowthPercent);
        Assert.Null(j.StarsNow);
        Assert.False(j.PushedRecently);
        Assert.False(j.NewRelease);
    }

    [Theory]
    [InlineData(null, "v1", true)]
    [InlineData("v1", "v2", true)]
    [InlineData("v1", "v1", false)]
    [InlineData("v1", null, false)]
    [InlineData(null, null, false)]
    public void A_new_release_is_a_changed_tag(string? then, string? now, bool expected) =>
        Assert.Equal(expected, Judge(Snapshot(tag: then), Facts(tag: now)).NewRelease);

    [Fact]
    public void The_report_text_states_every_threshold_from_the_constants()
    {
        var legend = AutopsyRules.Legend;

        Assert.Contains($"{AutopsyRules.GrewStarsPercent}%", legend, StringComparison.Ordinal);
        Assert.Contains($"{AutopsyRules.AliveDays} days", legend, StringComparison.Ordinal);
        Assert.Contains($"{AutopsyRules.MinAgeDays} days", legend, StringComparison.Ordinal);
        Assert.Contains("404", legend, StringComparison.Ordinal);
        Assert.Contains("archived", legend, StringComparison.Ordinal);
        Assert.Equal((20, 30, 87), (AutopsyRules.GrewStarsPercent, AutopsyRules.AliveDays, AutopsyRules.MinAgeDays));
    }

    private static AutopsyItem Item(long id, string feed, int? relevance, RepoVerdict verdict, string title = "t", string url = "https://e.example/") =>
        new(id, title, url, feed, "acme/widget", relevance, verdict, 10, 12, 20, true, false, IdeaOutcome.None, false, 0);

    [Fact]
    public void The_report_totals_and_splits_by_relevance_and_feed()
    {
        var items = new[]
        {
            Item(1, "Blog", 3, RepoVerdict.Grew), Item(2, "Blog", 3, RepoVerdict.Alive), Item(3, "Blog", 3, RepoVerdict.Quiet),
            Item(4, "Forum", 3, RepoVerdict.Gone), Item(5, "Forum", 2, RepoVerdict.Quiet), Item(6, "Forum", 2, RepoVerdict.Quiet),
            Item(7, "Weekly", 2, RepoVerdict.Alive), Item(8, "Weekly", null, RepoVerdict.Grew),
        };

        var r = AutopsyScorer.Build(items, noRepo: 5, noBaseline: 2, notChecked: 1, At);

        Assert.Equal(new VerdictCounts(2, 2, 3, 1), r.Total);
        Assert.Equal(8, r.Total.Total);
        Assert.Equal((5, 2, 1, 16), (r.NoRepo, r.NoBaseline, r.Unchecked, r.Counted));
        Assert.Equal(["3", "2", "none"], r.ByRelevance.Select(g => g.Label));
        Assert.Equal(new VerdictCounts(1, 1, 1, 1), r.ByRelevance[0].Counts);
        Assert.Equal(new VerdictCounts(0, 1, 2, 0), r.ByRelevance[1].Counts);
        Assert.Equal(new VerdictCounts(1, 0, 0, 0), r.ByRelevance[2].Counts);
        Assert.Equal(["Weekly", "Blog", "Forum"], r.ByFeed.Select(g => g.Label));   // 2/2 lasted, 2/3, 0/3
        Assert.Equal(2, r.ByFeed[0].Counts.Lasted);
        Assert.Equal([1L, 8, 2, 7, 3, 5, 6, 4], r.Items.Select(i => i.ItemId));   // by verdict, then id
    }

    [Fact]
    public void Feeds_with_the_same_share_rank_by_size_then_name()
    {
        var items = new[]
        {
            Item(1, "B", 3, RepoVerdict.Alive), Item(2, "A", 3, RepoVerdict.Alive), Item(3, "C", 3, RepoVerdict.Alive), Item(4, "C", 3, RepoVerdict.Grew),
        };

        var r = AutopsyScorer.Build(items, 0, 0, 0, At);

        Assert.Equal(["C", "A", "B"], r.ByFeed.Select(g => g.Label));
    }

    [Fact]
    public void An_empty_month_gives_zero_totals_and_no_groups()
    {
        var r = AutopsyScorer.Build([], 0, 0, 0, At);

        Assert.Equal(0, r.Total.Total);
        Assert.Empty(r.ByRelevance);
        Assert.Empty(r.ByFeed);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void The_message_gives_the_verdict_shares_the_split_the_counts_and_the_thresholds()
    {
        var items = new[] { Item(1, "Blog", 3, RepoVerdict.Grew), Item(2, "Blog", 3, RepoVerdict.Quiet), Item(3, "Forum", 2, RepoVerdict.Alive), Item(4, "Forum", 2, RepoVerdict.Gone) };
        var report = AutopsyScorer.Build(items, noRepo: 2, noBaseline: 1, notChecked: 3, At);

        var html = AutopsyFormatter.Message("2026-11-01", report).Html;

        Assert.StartsWith("<b>Hype autopsy · November 2026</b>", html, StringComparison.Ordinal);
        Assert.Contains("4 👍 items", html, StringComparison.Ordinal);
        Assert.Contains("grew 25% · alive 25% · quiet 25% · gone 25%", html, StringComparison.Ordinal);
        Assert.Contains("2 link no GitHub repo", html, StringComparison.Ordinal);
        Assert.Contains("1 were already gone", html, StringComparison.Ordinal);
        Assert.Contains("3 could not be checked now", html, StringComparison.Ordinal);
        Assert.Contains("3: 2 items, 50% lasted", html, StringComparison.Ordinal);
        Assert.Contains("2: 2 items, 50% lasted", html, StringComparison.Ordinal);
        Assert.Contains("Blog 1/2 · Forum 1/2", html, StringComparison.Ordinal);
        Assert.Contains("stars up 20% or more", html, StringComparison.Ordinal);
        Assert.True(html.Length < 4096);
    }

    [Fact]
    public void The_message_encodes_hostile_feed_data_links_only_safe_urls_and_caps_the_lists()
    {
        const string hostile = "<script>alert(1)</script>";
        var items = Enumerable.Range(0, 12).Select(i => Item(i, $"{hostile} feed {i}", 3, RepoVerdict.Alive, $"{hostile} title {i}", i == 0 ? "javascript:alert(1)" : "https://e.example/?a=1&b=2"))
            .Select(i => i with { Repo = $"acme/{hostile}" }).ToList();
        var report = AutopsyScorer.Build(items, 0, 0, 0, At);

        var html = AutopsyFormatter.Message("2026-11-01", report).Html;

        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; title 1", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://e.example/?a=1&amp;b=2\">", html, StringComparison.Ordinal);
        Assert.Contains("+4 more", html, StringComparison.Ordinal);
        Assert.True(html.Length < 4096);
    }

    [Theory]
    [InlineData(88)]   // the 90-character title limit falls between the two halves of the emoji
    [InlineData(89)]
    public void An_emoji_at_the_cut_is_dropped_whole_so_the_message_stays_valid_text(int before)
    {
        var items = new[] { Item(1, "Blog", 3, RepoVerdict.Alive, new string('a', before) + "😀😀😀") };
        var report = AutopsyScorer.Build(items, 0, 0, 0, At);

        var html = AutopsyFormatter.Message("2026-11-01", report).Html;

        Assert.Contains(new string('a', before) + "…", html, StringComparison.Ordinal);
        var bytes = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(html);   // throws on a lone surrogate
        Assert.NotEmpty(bytes);
    }
}
