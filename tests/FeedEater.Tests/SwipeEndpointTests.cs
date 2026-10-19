using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Ui;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class SwipeEndpointTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Token = "swipe-token";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _http = null!;
    private string _cookie = "";
    private string _csrf = "";

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            (string Key, string Value)[] settings =
            [
                ("ConnectionStrings:FeedEater", pg.ConnectionString),
                ("Mcp:Token", Token),
                ("FeedEater:RunJobs", "false"),
                ("FeedEater:ProfilePath", "/nope/profile.json"),
                ("FeedEater:Swipe:Enabled", "true"),
                ("FeedEater:Swipe:Cards", "3"),
                ("FeedEater:Swipe:Minutes", "15"),
                ("FeedEater:Swipe:CooldownMinutes", "180"),
                ("FeedEater:Swipe:SessionsPerDay", "3"),
            ];
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IHostedService>();
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(_time);
            });
        });
        _http = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        await LoginAsync();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    [GeneratedRegex("""<meta name="csrf" content="([^"]+)">""", RegexOptions.None, 1000)]
    private static partial Regex CsrfPattern();

    private async Task LoginAsync()
    {
        using var login = new HttpRequestMessage(HttpMethod.Post, "/ui/login") { Content = new FormUrlEncodedContent([new("token", Token)]) };
        login.Headers.Add("Origin", "http://localhost");
        var response = await _http.SendAsync(login);
        _cookie = response.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith(UiSession.CookieName + "=", StringComparison.Ordinal));
        using var shell = new HttpRequestMessage(HttpMethod.Get, "/swipe");
        shell.Headers.Add("Cookie", _cookie);
        _csrf = CsrfPattern().Match(await (await _http.SendAsync(shell)).Content.ReadAsStringAsync()).Groups[1].Value;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body, bool withCsrf = true, bool withCookie = true, string origin = "http://localhost")
    {
        var json = JsonSerializer.SerializeToNode(body)!.AsObject();
        if (withCsrf)
        {
            json["csrf"] = _csrf;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/swipe/" + path) { Content = JsonContent.Create(json) };
        request.Headers.Add("Origin", origin);
        if (withCookie)
        {
            request.Headers.Add("Cookie", _cookie);
        }

        return await _http.SendAsync(request);
    }

    private async Task<JsonElement> NextAsync(int? position = null)
    {
        var response = await PostAsync("next", new { position });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SeedItemsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Seed.ItemAsync(pg, 1, $"Item {i}", TestVectors.OneHot(i), _time.GetUtcNow().UtcDateTime.AddHours(-i), content: $"Body {i}");
        }
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no row");
    }

    [Fact]
    public async Task The_page_needs_the_login_and_carries_a_script_only_policy()
    {
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "/swipe");
        var refused = await _http.SendAsync(anonymous);
        using var signedIn = new HttpRequestMessage(HttpMethod.Get, "/swipe");
        signedIn.Headers.Add("Cookie", _cookie);
        var shell = await _http.SendAsync(signedIn);

        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Equal("/ui/login", refused.Headers.Location!.OriginalString);
        Assert.Equal(SwipeEndpointsPolicy(), shell.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("unsafe-inline", SwipeEndpointsPolicy(), StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", SwipeEndpointsPolicy(), StringComparison.Ordinal);
    }

    private static string SwipeEndpointsPolicy() => FeedEater.Swipe.SwipeEndpoints.ContentSecurityPolicy;

    [Fact]
    public async Task The_ui_policy_is_not_loosened() =>
        Assert.DoesNotContain("script-src", UiEndpoints.ContentSecurityPolicy, StringComparison.Ordinal);

    [Fact]
    public async Task A_post_needs_the_cookie_the_anti_forgery_value_and_the_same_origin()
    {
        await SeedItemsAsync(3);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("next", new { }, withCookie: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("next", new { }, withCsrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("next", new { }, origin: "http://evil.example")).StatusCode);
        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from swipe_sessions"));
    }

    [Fact]
    public async Task The_assets_are_served_without_a_login()
    {
        foreach (var path in new[] { "app.js", "app.css", "manifest.webmanifest", "sw.js", "icon.svg" })
        {
            var response = await _http.GetAsync("/swipe/" + path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task The_shell_has_a_button_set_for_every_card_kind_and_the_script_sends_client_ids_and_undoes()
    {
        var shell = await (await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/swipe") { Headers = { { "Cookie", _cookie } } })).Content.ReadAsStringAsync();
        foreach (var marker in new[] { "data-for=\"follow\"", "data-for=\"duel\"", "data-for=\"idea\"", "data-for=\"info\"", "data-do=\"undo\"", "data-do=\"stop\"", "data-do=\"keep\"" })
            Assert.Contains(marker, shell);

        var script = await _http.GetStringAsync("/swipe/app.js");
        Assert.Contains("randomUUID", script);
        Assert.Contains("post('undo'", script);
        Assert.DoesNotContain("innerHTML", script);
    }

    [Fact]
    public async Task Cards_come_from_the_session_queue_and_the_cap_ends_it_with_a_summary_then_closes()
    {
        await SeedItemsAsync(5);

        var first = await NextAsync();
        Assert.Equal("card", first.GetProperty("type").GetString());
        Assert.Equal(0, first.GetProperty("card").GetProperty("position").GetInt32());
        Assert.Equal(3, first.GetProperty("card").GetProperty("total").GetInt32());
        await NextAsync(1);
        await NextAsync(2);
        var summary = await NextAsync(3);

        Assert.Equal("summary", summary.GetProperty("type").GetString());
        Assert.Equal("cards", summary.GetProperty("summary").GetProperty("reason").GetString());
        Assert.Equal(3, summary.GetProperty("summary").GetProperty("cards").GetInt32());
        Assert.Equal("cards", await ScalarAsync<string>("select end_reason from swipe_sessions"));

        _time.Advance(TimeSpan.FromMinutes(11));
        var closed = await NextAsync();
        Assert.Equal("closed", closed.GetProperty("type").GetString());
        Assert.Equal("cooldown", closed.GetProperty("closed").GetProperty("why").GetString());
        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from swipe_sessions"));

        _time.Advance(TimeSpan.FromMinutes(180));
        Assert.Equal("card", (await NextAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_reload_resumes_at_the_same_card_and_does_not_restart_the_clock()
    {
        await SeedItemsAsync(5);
        await NextAsync();
        var second = await NextAsync(1);
        _time.Advance(TimeSpan.FromMinutes(14));

        var reloaded = await NextAsync();

        Assert.Equal(second.GetProperty("card").GetProperty("title").GetString(), reloaded.GetProperty("card").GetProperty("title").GetString());
        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from swipe_sessions"));

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("summary", (await NextAsync()).GetProperty("type").GetString());
        Assert.Equal("time", await ScalarAsync<string>("select end_reason from swipe_sessions"));
    }

    [Fact]
    public async Task A_session_with_no_new_items_is_not_started()
    {
        var result = await NextAsync();

        Assert.Equal("closed", result.GetProperty("type").GetString());
        Assert.Equal("empty", result.GetProperty("closed").GetProperty("why").GetString());
        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from swipe_sessions"));
    }

    [Fact]
    public async Task Voted_items_and_items_shown_before_are_not_offered()
    {
        await SeedItemsAsync(4);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into votes (item_id, value) select min(id), 1 from items");
        }

        var titles = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            titles.Add((await NextAsync(i)).GetProperty("card").GetProperty("title").GetString()!);
        }

        Assert.DoesNotContain("Item 0", titles);
    }

    [Fact]
    public async Task Like_dislike_and_save_write_votes_and_the_acts_are_logged_once_per_card()
    {
        await SeedItemsAsync(3);
        var session = (await NextAsync()).GetProperty("card").GetProperty("session").GetInt64();
        await NextAsync(1);
        await NextAsync(2);

        Assert.True((await (await PostAsync("act", new { session, position = 0, act = "like" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        await PostAsync("act", new { session, position = 1, act = "dislike" });
        await PostAsync("act", new { session, position = 2, act = "like" });
        await PostAsync("act", new { session, position = 2, act = "save" });
        await PostAsync("act", new { session, position = 2, act = "save" });

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var votes = (await c.QueryAsync<(long Item, short Value, float Weight)>("select item_id, value, weight from votes order by item_id")).ToList();
        Assert.Equal(3, votes.Count);
        Assert.Contains(votes, v => v.Value == 1 && v.Weight == 0.5f);
        Assert.Contains(votes, v => v.Value == -1 && v.Weight == 0.5f);
        Assert.Contains(votes, v => v.Value == 1 && v.Weight == 1f);   // the save replaced the like on card 2
        Assert.Equal(3, await ScalarAsync<int>("select count(*)::int from swipe_events"));
        Assert.Equal("save", await ScalarAsync<string>("select action from swipe_events where position = 2"));
        Assert.Equal(["like", "save"], (await c.QuerySingleAsync<string[]>("select acts from swipe_events where position = 2")).Order().ToArray());
    }

    [Fact]
    public async Task The_client_cannot_name_a_card_outside_its_queue_or_an_unknown_act()
    {
        await SeedItemsAsync(3);
        var session = (await NextAsync()).GetProperty("card").GetProperty("session").GetInt64();

        foreach (var body in new object[] { new { session, position = 2, act = "like" }, new { session, position = -1, act = "like" }, new { session = 999, position = 0, act = "like" }, new { session, position = 0, act = "dwell" } })
        {
            Assert.False((await (await PostAsync("act", body)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        }

        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from votes"));
    }

    [Fact]
    public async Task The_event_logs_dwell_and_direction_and_the_last_call_wins()
    {
        await SeedItemsAsync(3);
        var session = (await NextAsync()).GetProperty("card").GetProperty("session").GetInt64();
        var shownAt = _time.GetUtcNow().ToUnixTimeMilliseconds();

        await PostAsync("event", new { session, position = 0, shownAt, dwellMs = 1200, direction = "up" });
        await PostAsync("event", new { session, position = 0, shownAt, dwellMs = 99_999_999_999L, direction = "sideways" });

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var row = await c.QuerySingleAsync<(int Dwell, string Direction, string Action)>("select dwell_ms, direction, action from swipe_events");
        Assert.Equal(3_600_000, row.Dwell);
        Assert.Equal("none", row.Direction);
        Assert.Equal("next", row.Action);
    }

    [Fact]
    public async Task An_event_row_carries_how_the_card_was_chosen()
    {
        await SeedItemsAsync(3);
        var session = (await NextAsync()).GetProperty("card").GetProperty("session").GetInt64();
        await PostAsync("act", new { session, position = 0, act = "like" });

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var row = await c.QuerySingleAsync<(string Source, float? Predicted)>("select rank_source, predicted from swipe_events");
        Assert.Contains(row.Source, new[] { "exploit", "explore" });
        Assert.Null(row.Predicted);   // no model yet
        Assert.Equal(3, await ScalarAsync<int>("select cardinality(queue_sources) from swipe_sessions"));
    }

    private async Task<(long Recall, long Seed, long SeedItem)> SeedSpecialAsync()
    {
        await SeedItemsAsync(2);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var recall = await c.ExecuteScalarAsync<long>(
            "insert into recall_cards (source, ref, front, back, due_at) values ('note', 'note:x', 'What did you note?', 'The answer.', @due) returning id",
            new { due = _time.GetUtcNow().UtcDateTime.AddHours(-1) });
        var item = await c.ExecuteScalarAsync<long>("select min(id) from items");
        var seed = await c.ExecuteScalarAsync<long>("insert into post_seeds (item_id, angle) values (@item, 'Argue that X.') returning id", new { item });
        return (recall, seed, item);
    }

    private async Task<Dictionary<string, int>> PositionsAsync(int count)
    {
        var positions = new Dictionary<string, int>();
        for (var i = 0; i < count; i++)
        {
            var card = (await NextAsync(i)).GetProperty("card");
            positions[card.GetProperty("kind").GetString()!] = i;
        }

        return positions;
    }

    [Fact]
    public async Task Due_recall_cards_and_new_seeds_take_places_in_the_queue_and_items_fill_the_rest()
    {
        await SeedSpecialAsync();

        var positions = await PositionsAsync(3);

        Assert.Equal(["item", "recall", "seed"], positions.Keys.Order());
        var recall = (await NextAsync(positions["recall"])).GetProperty("card");
        Assert.Equal("What did you note?", recall.GetProperty("title").GetString());
        Assert.Equal("The answer.", recall.GetProperty("back").GetString());
        Assert.Equal("Argue that X.", (await NextAsync(positions["seed"])).GetProperty("card").GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_recall_answer_reschedules_the_card_and_item_acts_are_refused_on_it()
    {
        var (card, _, _) = await SeedSpecialAsync();
        var positions = await PositionsAsync(3);
        var session = await ScalarAsync<long>("select max(id) from swipe_sessions");

        Assert.False((await (await PostAsync("act", new { session, position = positions["recall"], act = "like" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        Assert.True((await (await PostAsync("act", new { session, position = positions["recall"], act = "good" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());

        Assert.Equal(1, await ScalarAsync<int>($"select step from recall_cards where id = {card}"));
        Assert.Equal("good", await ScalarAsync<string>("select result from recall_reviews"));
        Assert.Equal("good", await ScalarAsync<string>("select action from swipe_events where kind = 'recall'"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from votes"));
    }

    [Fact]
    public async Task Sending_a_seed_files_an_idea_and_dismissing_one_files_nothing()
    {
        var (_, seed, item) = await SeedSpecialAsync();
        var positions = await PositionsAsync(3);
        var session = await ScalarAsync<long>("select max(id) from swipe_sessions");

        Assert.True((await (await PostAsync("act", new { session, position = positions["seed"], act = "send" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());

        Assert.Equal("sent", await ScalarAsync<string>($"select status from post_seeds where id = {seed}"));
        Assert.Equal("[post seed] Argue that X.", await ScalarAsync<string>($"select title from ideas where item_id = {item}"));
        Assert.Equal(1, await ScalarAsync<int>($"select value::int from votes where item_id = {item}"));

        // A second press does nothing more.
        await PostAsync("act", new { session, position = positions["seed"], act = "dismiss" });
        Assert.Equal("sent", await ScalarAsync<string>($"select status from post_seeds where id = {seed}"));
    }

    [Fact]
    public async Task Dismissing_a_seed_leaves_no_idea()
    {
        var (_, seed, _) = await SeedSpecialAsync();
        var positions = await PositionsAsync(3);
        var session = await ScalarAsync<long>("select max(id) from swipe_sessions");

        await PostAsync("act", new { session, position = positions["seed"], act = "dismiss" });

        Assert.Equal("dismissed", await ScalarAsync<string>($"select status from post_seeds where id = {seed}"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from ideas"));
    }

    [Fact]
    public async Task The_felt_tap_is_stored_once_and_only_for_an_ended_session()
    {
        await SeedItemsAsync(3);
        var session = (await NextAsync()).GetProperty("card").GetProperty("session").GetInt64();

        Assert.False((await (await PostAsync("felt", new { session, felt = 1 })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());

        await PostAsync("end", new { session });
        Assert.True((await (await PostAsync("felt", new { session, felt = -1 })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        Assert.False((await (await PostAsync("felt", new { session, felt = 1 })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        Assert.False((await (await PostAsync("felt", new { session, felt = 7 })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());

        Assert.Equal(-1, await ScalarAsync<int>("select felt::int from swipe_sessions"));
        Assert.Equal("user", await ScalarAsync<string>("select end_reason from swipe_sessions"));
    }

    [Fact]
    public async Task The_report_page_lists_the_week()
    {
        await SeedItemsAsync(3);
        var session = (await NextAsync()).GetProperty("card").GetProperty("session").GetInt64();
        await PostAsync("act", new { session, position = 0, act = "save" });
        await PostAsync("event", new { session, position = 0, shownAt = _time.GetUtcNow().ToUnixTimeMilliseconds(), dwellMs = 4000, direction = "right" });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/ui/swipe");
        request.Headers.Add("Cookie", _cookie);
        var html = await (await _http.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("<h1>Swipe</h1>", html, StringComparison.Ordinal);
        Assert.Contains("2026-10-12", html, StringComparison.Ordinal);   // the Monday of that week
        Assert.Contains("<td class=\"num\">4</td>", html, StringComparison.Ordinal);   // dwell shown
    }
}
