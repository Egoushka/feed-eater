using System.Net;
using System.Text.RegularExpressions;
using FeedEater.Digest;
using FeedEater.Telegram;

namespace FeedEater.Tests;

public sealed class DigestFormatterTests
{
    private static DigestItem Item(string? suggestion = "Add pg_stat_io to the dashboard.", string url = "https://example.com/a?x=1&y=2") => new()
    {
        Id = 42, Title = "Postgres <18.1> & friends", Url = url, Feed = "PostgreSQL News", Project = "homelab",
        Kind = suggestion is null ? "fyi" : "improve", Summary = "Async I/O fixes.", Why = "Your box runs PG 18.", Suggestion = suggestion,
    };

    private static DigestHeader Header(double? weekUpRate) => new(
        new DateOnly(2026, 10, 5), 12, 412, [("homelab", 5), ("postgres", 3)], 7, 2, 1.234m, weekUpRate, ["Miniflux was unreachable"]);

    private static int VisibleLength(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Length;

    [Fact]
    public void Item_escapes_text_and_shows_the_suggestion()
    {
        var m = DigestFormatter.Item(Item(), null, null);

        Assert.StartsWith("💡 <b><a href=\"https://example.com/a?x=1&amp;y=2\">Postgres &lt;18.1&gt; &amp; friends</a></b>", m.Html, StringComparison.Ordinal);
        Assert.Contains("<i>PostgreSQL News · homelab · improve</i>", m.Html, StringComparison.Ordinal);
        Assert.Contains("💡 Add pg_stat_io to the dashboard.", m.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Buttons_follow_vote_and_filing_state()
    {
        var fresh = DigestFormatter.Buttons(42, true, null, null);
        var voted = DigestFormatter.Buttons(42, true, 1, null);
        var filed = DigestFormatter.Buttons(42, true, 1, "SKAR");
        var plain = DigestFormatter.Buttons(42, false, null, null);

        Assert.Equal(["👍", "👎", "📌 Save"], fresh[0].Select(b => b.Text));
        Assert.Equal(["v:42:u", "v:42:d", "s:42"], fresh[0].Select(b => b.Data));
        Assert.Equal(("📌 Saved ✓", "n"), (DigestFormatter.Buttons(42, false, null, null, saved: true)[0][2].Text, DigestFormatter.Buttons(42, false, null, null, saved: true)[0][2].Data));
        Assert.Equal("i:42", fresh[1].Single().Data);
        Assert.Equal("👍 ✓", voted[0][0].Text);
        Assert.Equal(("✓ Filed in SKAR", "n"), (filed[1].Single().Text, filed[1].Single().Data));
        Assert.Single(plain);
    }

    [Fact]
    public void Optional_buttons_follow_the_style_Karakeep_adds_save_and_Plane_names_the_idea_button()
    {
        var local = new ButtonStyle(CanSave: false, ToPlane: false);

        var fresh = DigestFormatter.Buttons(42, true, null, null, style: local);
        var filed = DigestFormatter.Buttons(42, true, 1, "inbox", style: local);

        Assert.Equal(["👍", "👎"], fresh[0].Select(b => b.Text));
        Assert.Equal(("💡 Save idea", "i:42"), (fresh[1].Single().Text, fresh[1].Single().Data));
        Assert.Equal("✓ Saved as an idea", filed[1].Single().Text);
        Assert.Equal("💡 To Plane", DigestFormatter.Buttons(42, true, null, null)[1].Single().Text);   // default: as before
        Assert.Equal(new ButtonStyle(false, false), ButtonStyle.From(new FeedEaterOptions()));
        Assert.Equal(new ButtonStyle(true, true), ButtonStyle.From(new FeedEaterOptions
        {
            Karakeep = new KarakeepOptions { BaseUrl = "http://k/", Token = "t" },
            Plane = new PlaneOptions { BaseUrl = "http://p/", Token = "t", Workspace = "w" },
        }));
    }

    [Fact]
    public void The_header_shows_unknown_cost_instead_of_a_zero_it_cannot_know()
    {
        string Spend(decimal known, int unpriced) => DigestFormatter.Header(Header(null) with { MonthSpend = known, UnpricedCalls = unpriced }).Html;

        Assert.Contains("spend this month $1.23\n", Spend(1.234m, 0), StringComparison.Ordinal);
        Assert.Contains("spend this month unknown\n", Spend(0m, 12), StringComparison.Ordinal);
        Assert.Contains("spend this month $1.23 and 3 calls of unknown cost\n", Spend(1.234m, 3), StringComparison.Ordinal);
    }

    [Fact]
    public void Huge_fields_stay_under_telegrams_limit_and_a_huge_url_drops_the_link()
    {
        var big = new string('&', 10_000);
        var m = DigestFormatter.Item(Item(big, "https://example.com/" + new string('a', 2000)) with { Title = big, Summary = big, Why = big, Feed = big }, null, null);

        Assert.True(VisibleLength(m.Html) <= DigestFormatter.MaxLength);
        Assert.DoesNotContain("<a href", m.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Clipping_never_leaves_a_lone_surrogate()
    {
        var m = DigestFormatter.Item(Item() with { Title = new string('a', 298) + "👍" + "tail" }, null, null);

        for (var n = 0; n < m.Html.Length; n++)
        {
            Assert.False(char.IsHighSurrogate(m.Html[n]) && (n + 1 == m.Html.Length || !char.IsLowSurrogate(m.Html[n + 1])));
            Assert.False(char.IsLowSurrogate(m.Html[n]) && (n == 0 || !char.IsHighSurrogate(m.Html[n - 1])));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative")]
    public void An_unusable_url_renders_the_plain_title(string url)
    {
        var m = DigestFormatter.Item(Item(url: url), null, null);

        Assert.DoesNotContain("<a href", m.Html, StringComparison.Ordinal);
        Assert.Contains("<b>Postgres &lt;18.1&gt; &amp; friends</b>", m.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_and_kind_are_clipped()
    {
        var m = DigestFormatter.Item(Item() with { Project = new string('p', 100), Kind = new string('k', 100) }, null, null);

        Assert.Contains(new string('p', 39) + "…", m.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('k', 41), m.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_has_counts_votes_spend_and_notes()
    {
        var h = DigestFormatter.Header(Header(null));

        Assert.Contains("<b>Feed digest · 5 Oct</b>", h.Html, StringComparison.Ordinal);
        Assert.Contains("12 of 412 new items", h.Html, StringComparison.Ordinal);
        Assert.Contains("homelab 5 · postgres 3", h.Html, StringComparison.Ordinal);
        Assert.Contains("Yesterday 👍 7 · 👎 2 · spend this month $1.23", h.Html, StringComparison.Ordinal);
        Assert.Contains("⚠️ Miniflux was unreachable", h.Html, StringComparison.Ordinal);
        Assert.Null(h.Keyboard);
    }

    [Fact]
    public void Header_shows_the_weekly_up_rate_when_there_were_votes()
    {
        var h = DigestFormatter.Header(Header(0.7777));

        Assert.Contains("spend this month $1.23\n7-day 👍 rate 78%\n⚠️", h.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_omits_the_weekly_up_rate_without_votes() =>
        Assert.DoesNotContain("7-day", DigestFormatter.Header(Header(null)).Html, StringComparison.Ordinal);
}
