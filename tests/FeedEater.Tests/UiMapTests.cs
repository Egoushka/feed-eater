using System.Diagnostics;
using System.Net;
using Dapper;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Storage;
using FeedEater.Ui;

namespace FeedEater.Tests;

/// <summary>The taste map page; a part of <see cref="UiTests"/> to reuse its factory and login helpers.</summary>
public sealed partial class UiTests
{
    private async Task<long[]> MapItemsAsync(int count, Func<int, string>? title = null)
    {
        var ids = new long[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = await Seed.ItemAsync(pg, 1, title?.Invoke(i) ?? $"Map item {i}", TestVectors.OneHot(i));
        }

        return ids;
    }

    private async Task VoteAtAsync(long item, short value, DateTime at, double weight = 1)
    {
        await new FeedbackStore(pg.Db).SetVoteAsync(item, value, default, weight);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update votes set at = @at where item_id = @item", new { item, at });
    }

    private static int Count(string html, string text) => html.Split(text).Length - 1;

    [Fact]
    public async Task The_map_says_so_when_there_are_too_few_items_to_draw()
    {
        await MapItemsAsync(2);

        var page = await GetAsync("/ui/map", await LoginAsync());

        Assert.Contains("needs at least 3 items", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_map_draws_votes_as_different_shapes_with_a_table_as_text_alternative_and_no_script_or_inline_style()
    {
        var ids = await MapItemsAsync(6);
        await VoteAtAsync(ids[0], 1, DateTime.UtcNow, 3);
        await VoteAtAsync(ids[1], -1, DateTime.UtcNow, 0.5);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into digests (local_date, status, item_ids, sent_at) values ('2026-10-01', 'sent', @ids, now())", new { ids = new[] { ids[2] } });
        }

        await new ProfileStore(pg.Db).ReplaceAllAsync([new Profile { Key = "homelab", Kind = "project", Description = "d", Embedding = TestVectors.OneHot(0) }], DateTimeOffset.UtcNow, default);

        var page = await GetAsync("/ui/map", await LoginAsync());

        Assert.Contains("<svg class=\"map\"", page, StringComparison.Ordinal);
        Assert.Contains("role=\"img\" aria-label=\"Taste map: 1 liked items as green circles, 1 disliked items as red crosses and 4 items without a vote as grey dots.", page, StringComparison.Ordinal);
        Assert.Equal(1, Count(page, "<circle class=\"up\""));
        Assert.Contains("r=\"9\"", page, StringComparison.Ordinal);
        Assert.Equal(1, Count(page, "<path class=\"down\""));
        Assert.Equal(4, Count(page, "<circle class=\"none\""));
        foreach (var (id, i) in ids.Select((id, i) => (id, i)))
        {
            Assert.Contains($"<a href=\"/ui/item/{id}\">", page, StringComparison.Ordinal);
            Assert.Contains($"<title>Map item {i}</title>", page, StringComparison.Ordinal);
        }

        Assert.InRange(Count(page, "class=\"unseen\""), 1, 5);
        Assert.Contains(">homelab</text>", page, StringComparison.Ordinal);
        Assert.Contains("<td>All points</td><td class=\"num\">1</td><td class=\"num\">1</td><td class=\"num\">4</td>", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"badge\">homelab</span></td><td class=\"num\">1</td><td class=\"num\">1</td><td class=\"num\">4</td>", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/ui/map\" aria-current=\"page\"", page, StringComparison.Ordinal);
        Assert.Contains(">Map</a>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" style=", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<image", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("xlink", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"http", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Item_titles_and_profile_keys_on_the_map_are_encoded()
    {
        const string Title = "a \"quoted\" <b>bold</b> & <script>alert(1)</script>";
        const string Key = "<img src=x onerror=alert(1)>\"'";
        var ids = await MapItemsAsync(4, i => i == 0 ? Title : $"Plain {i}");
        await VoteAtAsync(ids[0], 1, DateTime.UtcNow);
        await new ProfileStore(pg.Db).ReplaceAllAsync([new Profile { Key = Key, Kind = "project", Description = "d", Embedding = TestVectors.OneHot(0) }], DateTimeOffset.UtcNow, default);

        var page = await GetAsync("/ui/map", await LoginAsync());

        Assert.Contains("<title>a &quot;quoted&quot; &lt;b&gt;bold&lt;/b&gt; &amp; &lt;script&gt;alert(1)&lt;/script&gt;</title>", page, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;&quot;&#39;</text>", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"badge\">&lt;img src=x onerror=alert(1)&gt;&quot;&#39;</span>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_month_link_shows_the_votes_up_to_the_end_of_that_month_and_bad_months_mean_the_current_one()
    {
        var now = DateTime.UtcNow;
        var ids = await MapItemsAsync(5);
        var twoBack = new DateTime(now.Year, now.Month, 15, 12, 0, 0, DateTimeKind.Utc).AddMonths(-2);
        await VoteAtAsync(ids[0], 1, twoBack);
        await VoteAtAsync(ids[1], -1, now);
        var cookie = await LoginAsync();

        var current = await GetAsync("/ui/map", cookie);
        var past = await GetAsync($"/ui/map?month={twoBack:yyyy-MM}", cookie);

        Assert.Contains("<td>All points</td><td class=\"num\">1</td><td class=\"num\">1</td><td class=\"num\">3</td>", current, StringComparison.Ordinal);
        Assert.Contains("<td>All points</td><td class=\"num\">1</td><td class=\"num\">0</td><td class=\"num\">4</td>", past, StringComparison.Ordinal);
        Assert.Contains($"Votes up to the end of {twoBack:yyyy-MM}.", past, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"/ui/map?month={twoBack:yyyy-MM}\" aria-current=\"page\">{twoBack:yyyy-MM}</a>", past, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"/ui/map?month={twoBack.AddMonths(1):yyyy-MM}\">", past, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/ui/map\">", past, StringComparison.Ordinal);
        Assert.DoesNotContain($"month={twoBack.AddMonths(-1):yyyy-MM}", past, StringComparison.Ordinal);

        foreach (var bad in new[] { "garbage", "2026-13", "2026-1", "2026-00", "0000-01", "1999-12", "+2026-1", "9999-12", " 2026-10", "2026-10-01", "\"><script>alert(1)</script>", "" })
        {
            var page = await GetAsync($"/ui/map?month={Uri.EscapeDataString(bad)}", cookie);
            Assert.Equal(current, page);
        }
    }

    [Theory]
    [InlineData("2026-10", "2026-10")]
    [InlineData("2026-09", "2026-09")]
    [InlineData("2026-11", "2026-10")]
    [InlineData("2026-1", "2026-10")]
    [InlineData("2026-13", "2026-10")]
    [InlineData("1999-12", "2026-10")]
    [InlineData("2026-10 ", "2026-10")]
    [InlineData("abc", "2026-10")]
    [InlineData(null, "2026-10")]
    public void The_month_must_be_exactly_year_dash_two_digit_month_and_not_in_the_future(string? text, string expected) =>
        Assert.Equal(expected, TasteMap.Text(TasteMap.ParseMonth(text, new DateOnly(2026, 10, 1))));

    [Fact]
    public async Task A_built_map_is_kept_for_ten_minutes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = _app.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
        }));
        using var http = Client(app);
        var cookie = await LoginAsync(http);
        var ids = await MapItemsAsync(5);

        async Task<string> MapAsync()
        {
            var response = await http.SendAsync(Req(HttpMethod.Get, "/ui/map", cookie));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        Assert.Contains("<td>All points</td><td class=\"num\">0</td><td class=\"num\">0</td><td class=\"num\">5</td>", await MapAsync(), StringComparison.Ordinal);
        await VoteAtAsync(ids[0], 1, time.GetUtcNow().UtcDateTime);

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Contains("<td>All points</td><td class=\"num\">0</td><td class=\"num\">0</td><td class=\"num\">5</td>", await MapAsync(), StringComparison.Ordinal);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Contains("<td>All points</td><td class=\"num\">1</td><td class=\"num\">0</td><td class=\"num\">4</td>", await MapAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_map_of_2500_items_with_1536_dimensions_is_built_in_a_few_seconds()
    {
        // One small map for last month first, so the timed request does not pay for JIT and query-mapper setup that every later request skips.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = _app.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
        }));
        using var http = Client(app);
        await MapItemsAsync(3);
        var cookie = await LoginAsync(http);

        async Task<string> MapAsync() => await (await http.SendAsync(Req(HttpMethod.Get, "/ui/map", cookie))).Content.ReadAsStringAsync();

        await MapAsync();
        time.Advance(TimeSpan.FromMinutes(11));   // the small map is built and cached for ten minutes: let it expire

        // The vector index makes seeding thousands of rows slow and the map does not use it: drop it for the seed, restore it empty afterwards.
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("drop index items_embedding_idx");
            await c.ExecuteAsync("insert into feeds (id, title) values (1, 'Feed 1') on conflict do nothing");
            await c.ExecuteAsync(
                """
                insert into items (feed_id, url, canonical_url, title_hash, title, published_at, embedding)
                select 1, 'https://example.com/' || g, 'https://example.com/' || g, '', 'Item ' || g, now(),
                       array(select (random() - 0.5)::real from generate_series(1, 1536) k where g > 0)::vector
                from generate_series(1, 2500) g
                """);
            await c.ExecuteAsync("insert into votes (item_id, value, at) select id, case when id % 2 = 0 then 1 else -1 end, now() from items where id <= 1000");
        }

        var watch = Stopwatch.StartNew();
        var page = await MapAsync();
        watch.Stop();
        await pg.ResetAsync();
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("create index items_embedding_idx on items using hnsw (embedding vector_cosine_ops)");
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"/ui/map took {watch.Elapsed.TotalSeconds:0.00}s");
        Assert.Contains("<td>All points</td><td class=\"num\">500</td><td class=\"num\">500</td><td class=\"num\">1500</td>", page, StringComparison.Ordinal);
    }
}
