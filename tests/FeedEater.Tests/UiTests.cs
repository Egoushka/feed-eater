using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using FeedEater.Digest;
using FeedEater.Storage;
using FeedEater.Ui;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class UiTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Token = "ui-token";
    private const string Hostile = "<script>alert(1)</script>";

    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _app = Factory(Token);
        _http = Client(_app);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private WebApplicationFactory<Program> Factory(string token, bool telegram = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:FeedEater", pg.ConnectionString)
            .UseSetting("Mcp:Token", token)
            .UseSetting("FeedEater:RunJobs", telegram ? "true" : "false")
            .UseSetting("FeedEater:Telegram:Token", telegram ? "123:test" : "")
            .UseSetting("FeedEater:Llm:BaseUrl", "http://127.0.0.1:9/")
            .UseSetting("FeedEater:Llm:MonthlyBudget", "5")
            .ConfigureTestServices(s => s.RemoveAll<IHostedService>()));

    private static HttpClient Client(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static HttpRequestMessage Req(HttpMethod method, string path, string? cookie, string? origin = null, IEnumerable<KeyValuePair<string, string>>? form = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{UiSession.CookieName}={cookie}");
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        return request;
    }

    private static string? CookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(UiSession.CookieName + "=", StringComparison.Ordinal))?[(UiSession.CookieName.Length + 1)..]
            : null;

    private async Task<string> LoginAsync(HttpClient? http = null)
    {
        var response = await (http ?? _http).SendAsync(Req(HttpMethod.Post, "/ui/login", null, "http://localhost", [new("token", Token)]));
        return CookieOf(response)!;
    }

    private async Task<string> GetAsync(string path, string cookie, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _http.SendAsync(Req(HttpMethod.Get, path, cookie));
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string CsrfOf(string html) => CsrfPattern().Match(html).Groups[1].Value;

    [GeneratedRegex("name=\"_csrf\" value=\"([^\"]+)\"")]
    private static partial Regex CsrfPattern();

    private async Task<HttpResponseMessage> PostAsync(string path, string cookie, params (string Key, string Value)[] fields)
    {
        var csrf = CsrfOf(await GetAsync("/ui", cookie));
        return await _http.SendAsync(Req(HttpMethod.Post, path, cookie, "http://localhost",
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).Append(new("_csrf", csrf))));
    }

    private async Task<(long Item, string Today)> SeedDigestAsync(string title = "Postgres 19 lands", string url = "https://example.com/a", string feed = "Feed 1", string? suggestion = "Upgrade the box.")
    {
        var id = await Seed.ItemAsync(pg, 1, title, TestVectors.OneHot(1), content: "Full text of the post.");
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set url = @url where id = @id", new { id, url });
            await c.ExecuteAsync("update feeds set title = @feed where id = 1", new { feed });
        }

        await new AnalysisStore(pg.Db).SaveReadAsync(id, new ReadResult
        {
            Summary = "A summary.", Why = "It matters.", Kind = "improve", Project = "homelab", Suggestion = suggestion,
        }, "m", default);
        var today = new DigestTrigger(new CursorStore(pg.Db), Microsoft.Extensions.Options.Options.Create(new FeedEaterOptions()), TimeProvider.System).Today();
        await using var d = await pg.Db.DataSource.OpenConnectionAsync();
        await d.ExecuteAsync("insert into digests (local_date, status, candidates, triaged, item_ids, sent_at) values (@today::date, 'sent', 9, 4, @ids, now())",
            new { today, ids = new[] { id } });
        return (id, today);
    }

    public static TheoryData<string> Pages =>
    [
        "/ui", "/ui/posts", "/ui/feedback", "/ui/search", "/ui/search?q=postgres", "/ui/digests", "/ui/sources", "/ui/ideas", "/ui/usage",
        "/ui/item/1", "/ui/digest/2026-10-05", "/ui/digest/run",
    ];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Every_page_redirects_to_the_login_without_a_session(string path)
    {
        var response = await _http.SendAsync(Req(HttpMethod.Get, path, null));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/ui/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_login_page_is_public_and_carries_the_security_headers()
    {
        var response = await _http.GetAsync("/ui/login");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/ui/app.css")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_token_is_refused_and_a_right_one_sets_a_hardened_cookie()
    {
        var bad = await _http.SendAsync(Req(HttpMethod.Post, "/ui/login", null, "http://localhost", [new("token", "nope")]));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Null(CookieOf(bad));

        var good = await _http.SendAsync(Req(HttpMethod.Post, "/ui/login", null, "http://localhost", [new("token", Token)]));
        var setCookie = good.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();

        Assert.Equal(HttpStatusCode.SeeOther, good.StatusCode);
        Assert.Contains("httponly", setCookie, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("secure", setCookie, StringComparison.Ordinal);
        Assert.Contains("path=/ui", setCookie, StringComparison.Ordinal);
        await GetAsync("/ui", CookieOf(good)!);
    }

    [Fact]
    public async Task The_cookie_is_secure_behind_a_tls_proxy()
    {
        var request = Req(HttpMethod.Post, "/ui/login", null, "https://localhost", [new("token", Token)]);
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await _http.SendAsync(request);

        Assert.Contains("secure", response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant().Split("; "));
    }

    [Fact]
    public async Task Login_from_another_origin_is_refused()
    {
        var response = await _http.SendAsync(Req(HttpMethod.Post, "/ui/login", null, "https://evil.example", [new("token", Token)]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(CookieOf(response));
    }

    [Fact]
    public async Task Expired_tampered_and_foreign_cookies_are_rejected()
    {
        var session = new UiSession(Token, TimeProvider.System);
        var valid = session.IssueNew();
        await GetAsync("/ui/usage", valid);

        var expired = session.Issue(DateTimeOffset.UtcNow.AddMinutes(-1));
        var tampered = valid[..^2] + (valid[^2] == 'A' ? "B" : "A") + valid[^1];
        var longer = session.Issue(DateTimeOffset.UtcNow.AddYears(5)).Split('.');
        var forged = $"{longer[0]}.{longer[1]}.{valid.Split('.')[2]}";   // another expiry under the old signature
        var foreign = new UiSession("another-token", TimeProvider.System).IssueNew();

        foreach (var cookie in new[] { expired, tampered, forged, foreign, "garbage", "" })
        {
            var response = await _http.SendAsync(Req(HttpMethod.Get, "/ui/usage", cookie));
            Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        }
    }

    [Fact]
    public async Task Login_attempts_are_rate_limited()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
        {
            statuses.Add((await _http.SendAsync(Req(HttpMethod.Post, "/ui/login", null, "http://localhost", [new("token", "nope")]))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        // Even the right token waits out the window.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _http.SendAsync(Req(HttpMethod.Post, "/ui/login", null, "http://localhost", [new("token", Token)]))).StatusCode);
    }

    [Fact]
    public async Task With_no_token_configured_the_whole_ui_fails_closed()
    {
        await using var open = Factory("");
        var http = Client(open);
        var forged = new UiSession("", TimeProvider.System).Issue(DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/ui/login")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.SendAsync(Req(HttpMethod.Get, "/ui", forged))).StatusCode);
        var login = await http.SendAsync(Req(HttpMethod.Post, "/ui/login", null, "http://localhost", [new("token", "")]));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        Assert.Null(CookieOf(login));
    }

    [Fact]
    public async Task A_post_needs_a_same_origin_header_and_the_anti_forgery_value()
    {
        var (item, _) = await SeedDigestAsync();
        var cookie = await LoginAsync();
        var csrf = CsrfOf(await GetAsync("/ui", cookie));
        KeyValuePair<string, string>[] Form(string token) => [new("_csrf", token), new("item", item.ToString()), new("v", "up"), new("back", "/ui")];

        var noOrigin = await _http.SendAsync(Req(HttpMethod.Post, "/ui/vote", cookie, null, Form(csrf)));
        var foreignOrigin = await _http.SendAsync(Req(HttpMethod.Post, "/ui/vote", cookie, "https://evil.example", Form(csrf)));
        var otherPort = await _http.SendAsync(Req(HttpMethod.Post, "/ui/vote", cookie, "http://localhost:8080", Form(csrf)));
        var noToken = await _http.SendAsync(Req(HttpMethod.Post, "/ui/vote", cookie, "http://localhost", Form("")));
        var badToken = await _http.SendAsync(Req(HttpMethod.Post, "/ui/vote", cookie, "http://localhost", Form(csrf + "x")));

        foreach (var response in new[] { noOrigin, foreignOrigin, otherPort, noToken, badToken })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Null((await new ItemStore(pg.Db).GetAsync(item, default))!.Vote);

        var referer = Req(HttpMethod.Post, "/ui/vote", cookie, null, Form(csrf));
        referer.Headers.Referrer = new Uri("http://localhost/ui");
        Assert.Equal(HttpStatusCode.SeeOther, (await _http.SendAsync(referer)).StatusCode);
        Assert.Equal(1, (await new ItemStore(pg.Db).GetAsync(item, default))!.Vote);
    }

    [Fact]
    public async Task An_anti_forgery_value_from_another_session_is_refused()
    {
        var (item, _) = await SeedDigestAsync();
        var first = await LoginAsync();
        var second = await LoginAsync(Client(_app));
        var csrfOfFirst = CsrfOf(await GetAsync("/ui", first));

        var response = await _http.SendAsync(Req(HttpMethod.Post, "/ui/vote", second, "http://localhost",
            [new("_csrf", csrfOfFirst), new("item", item.ToString()), new("v", "up")]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Every_page_renders_for_a_signed_in_user_with_data()
    {
        var (item, today) = await SeedDigestAsync();
        await new FeedbackStore(pg.Db).AddIdeaAsync(new Idea { ItemId = item, PlaneProject = "LAB", PlaneIssueId = "i-1", Title = "Upgrade the box.", At = DateTime.UtcNow }, default);
        await new UsageStore(pg.Db).AddAsync("triage", "gpt-4.1-nano", 100, 20, 0.5m, default);
        var cookie = await LoginAsync();

        var todayPage = await GetAsync("/ui", cookie);
        Assert.Contains("Postgres 19 lands", todayPage, StringComparison.Ordinal);
        Assert.Contains("A summary.", todayPage, StringComparison.Ordinal);
        Assert.Contains("It matters.", todayPage, StringComparison.Ordinal);
        Assert.Contains("Upgrade the box.", todayPage, StringComparison.Ordinal);
        Assert.Contains("homelab", todayPage, StringComparison.Ordinal);
        Assert.Contains("Spend this month", todayPage, StringComparison.Ordinal);
        Assert.Contains("$0.50", todayPage, StringComparison.Ordinal);
        Assert.Contains("of $5.00", todayPage, StringComparison.Ordinal);
        Assert.Contains("Run the digest", todayPage, StringComparison.Ordinal);   // telegram off: explains instead of offering a button
        Assert.Contains("Filed in LAB", todayPage, StringComparison.Ordinal);

        Assert.Contains("Postgres 19 lands", await GetAsync("/ui/search?q=postgres", cookie), StringComparison.Ordinal);
        Assert.Contains("Nothing found", await GetAsync("/ui/search?q=zzzz", cookie), StringComparison.Ordinal);
        Assert.Contains(today, await GetAsync("/ui/digests", cookie), StringComparison.Ordinal);
        Assert.Contains("Postgres 19 lands", await GetAsync($"/ui/digest/{today}", cookie), StringComparison.Ordinal);
        var itemPage = await GetAsync($"/ui/item/{item}", cookie);
        Assert.Contains("Full text of the post.", itemPage, StringComparison.Ordinal);
        Assert.Contains("Feed 1", await GetAsync("/ui/sources", cookie), StringComparison.Ordinal);
        Assert.Contains("never liked", await GetAsync("/ui/sources?sort=shown&dir=asc&flag=1", cookie), StringComparison.Ordinal);
        Assert.Contains("Upgrade the box.", await GetAsync("/ui/ideas", cookie), StringComparison.Ordinal);
        Assert.Contains($"/ui/item/{item}", await GetAsync("/ui/ideas", cookie), StringComparison.Ordinal);
        var usage = await GetAsync("/ui/usage", cookie);
        Assert.Contains("gpt-4.1-nano", usage, StringComparison.Ordinal);
        Assert.Contains("$0.50", usage, StringComparison.Ordinal);

        await GetAsync("/ui/item/99999", cookie, HttpStatusCode.NotFound);
        await GetAsync("/ui/digest/2001-01-01", cookie, HttpStatusCode.NotFound);
        await GetAsync("/ui/digest/not-a-date", cookie, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Empty_archive_pages_render_their_empty_states()
    {
        var cookie = await LoginAsync();

        Assert.Contains("No digest yet", await GetAsync("/ui", cookie), StringComparison.Ordinal);
        Assert.Contains("No digests yet", await GetAsync("/ui/digests", cookie), StringComparison.Ordinal);
        Assert.Contains("Nothing filed yet", await GetAsync("/ui/ideas", cookie), StringComparison.Ordinal);
        Assert.Contains("No spend recorded", await GetAsync("/ui/usage", cookie), StringComparison.Ordinal);
        Assert.Contains("No feeds", await GetAsync("/ui/sources", cookie), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Feed_content_is_encoded_and_unsafe_links_are_not_linked()
    {
        var (item, today) = await SeedDigestAsync($"{Hostile} title", "javascript:alert(document.cookie)", $"{Hostile} feed", $"{Hostile} suggestion");
        await new FeedbackStore(pg.Db).AddIdeaAsync(new Idea { ItemId = item, PlaneProject = "LAB", PlaneIssueId = "i", Title = $"{Hostile} idea", At = DateTime.UtcNow }, default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set content = @content where id = @item", new { item, content = $"{Hostile} \" onmouseover=\"x" });
        }

        var cookie = await LoginAsync();
        var pages = new[]
        {
            await GetAsync("/ui", cookie), await GetAsync($"/ui/digest/{today}", cookie), await GetAsync($"/ui/item/{item}", cookie),
            await GetAsync("/ui/search?q=title", cookie), await GetAsync("/ui/ideas", cookie), await GetAsync("/ui/sources", cookie),
            await GetAsync($"/ui/search?q={Uri.EscapeDataString("\"><script>alert(2)</script>")}&project={Uri.EscapeDataString("\"><script>alert(3)</script>")}", cookie),
        };

        foreach (var page in pages)
        {
            Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onmouseover=\"x", page, StringComparison.Ordinal);
        }

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; title", pages[0], StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; feed", pages[0], StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; idea", pages[4], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Safe_feed_links_open_in_a_new_tab_without_opener_or_referrer()
    {
        await SeedDigestAsync();
        var page = await GetAsync("/ui", await LoginAsync());

        Assert.Contains("href=\"https://example.com/a\" rel=\"noopener noreferrer\" target=\"_blank\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vote_posts_persist_and_show_on_the_page()
    {
        var (item, _) = await SeedDigestAsync();
        var cookie = await LoginAsync();
        var store = new ItemStore(pg.Db);

        var up = await PostAsync("/ui/vote", cookie, ("item", item.ToString()), ("v", "up"), ("back", "/ui#item-1"));
        Assert.Equal(HttpStatusCode.SeeOther, up.StatusCode);
        Assert.Equal("/ui#item-1", up.Headers.Location!.OriginalString);
        Assert.Equal(1, (await store.GetAsync(item, default))!.Vote);
        Assert.Contains("aria-pressed=\"true\">👍", await GetAsync("/ui", cookie), StringComparison.Ordinal);

        await PostAsync("/ui/vote", cookie, ("item", item.ToString()), ("v", "down"));
        Assert.Equal(-1, (await store.GetAsync(item, default))!.Vote);
        Assert.Contains("aria-pressed=\"true\">👎", await GetAsync($"/ui/item/{item}", cookie), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/ui/vote", cookie, ("item", "99999"), ("v", "up"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/ui/vote", cookie, ("item", item.ToString()), ("v", "sideways"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/ui/vote", cookie, ("item", "abc"), ("v", "up"))).StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example/ui", "/ui")]
    [InlineData("//evil.example", "/ui")]
    [InlineData("/ui/../admin", "/ui/../admin")]
    [InlineData("/uiX", "/ui")]
    [InlineData("/ui\\evil", "/ui")]
    [InlineData("", "/ui")]
    [InlineData("/ui/item/4#item-4", "/ui/item/4#item-4")]
    public void The_return_path_must_stay_under_ui(string given, string expected) => Assert.Equal(expected, UiHandlers.SafeBack(given));

    [Fact]
    public async Task Filing_an_idea_without_a_suggestion_redirects_with_a_failure_notice_and_files_nothing()
    {
        var (item, _) = await SeedDigestAsync(suggestion: null);
        var cookie = await LoginAsync();

        var response = await PostAsync("/ui/vote", cookie, ("item", item.ToString()), ("v", "idea"), ("back", "/ui#item-1"));

        Assert.Equal("/ui?notice=file-failed#item-1", response.Headers.Location!.OriginalString);
        Assert.Null(await new FeedbackStore(pg.Db).GetIdeaAsync(item, default));
        Assert.Contains("Not filed", await GetAsync("/ui?notice=file-failed", cookie), StringComparison.Ordinal);
        Assert.DoesNotContain("injected", await GetAsync("/ui?notice=injected", cookie), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_run_digest_button_queues_a_force_request_and_the_page_shows_it()
    {
        await using var app = Factory(Token, telegram: true);
        var http = Client(app);
        var cookie = await LoginAsync(http);
        var (_, today) = await SeedDigestAsync();
        var cursors = new CursorStore(pg.Db);

        var page = await (await http.SendAsync(Req(HttpMethod.Get, "/ui", cookie))).Content.ReadAsStringAsync();
        Assert.Contains("Run digest now", page, StringComparison.Ordinal);
        Assert.Contains("Send today's digest again", page, StringComparison.Ordinal);

        var run = await http.SendAsync(Req(HttpMethod.Post, "/ui/digest/run", cookie, "http://localhost", [new("_csrf", CsrfOf(page)), new("mode", "run")]));

        Assert.Equal("/ui?notice=queued", run.Headers.Location!.OriginalString);
        Assert.Equal($"run:{today}", await cursors.GetAsync("digest:force", default));
        Assert.Contains("queued", await (await http.SendAsync(Req(HttpMethod.Get, "/ui", cookie))).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await http.SendAsync(Req(HttpMethod.Post, "/ui/digest/run", cookie, "http://localhost", [new("_csrf", CsrfOf(page)), new("mode", "resend")]));
        Assert.Equal($"resend:{today}", await cursors.GetAsync("digest:force", default));
    }

    [Fact]
    public async Task The_run_digest_button_is_off_without_a_telegram_token()
    {
        var cookie = await LoginAsync();

        var response = await PostAsync("/ui/digest/run", cookie, ("mode", "run"));

        Assert.Equal("/ui?notice=digest-off", response.Headers.Location!.OriginalString);
        Assert.Null(await new CursorStore(pg.Db).GetAsync("digest:force", default));
        Assert.DoesNotContain("Run digest now", await GetAsync("/ui", cookie), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sources_sort_by_the_query_string_and_flag_feeds_to_prune()
    {
        var now = DateTime.UtcNow;
        var liked = await Seed.ItemAsync(pg, 1, "liked", TestVectors.OneHot(1), now.AddDays(-1));
        await Seed.ItemAsync(pg, 2, "ignored a", TestVectors.OneHot(1), now.AddDays(-1));
        await Seed.ItemAsync(pg, 2, "ignored b", TestVectors.OneHot(1), now.AddDays(-2));
        await new FeedbackStore(pg.Db).SetVoteAsync(liked, 1, default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into digests (local_date, status, item_ids) values ('2026-10-01', 'sent', @ids)", new { ids = new[] { liked } });
        }

        var cookie = await LoginAsync();
        var byItems = await GetAsync("/ui/sources?sort=items&dir=desc", cookie);
        var byFeed = await GetAsync("/ui/sources?sort=feed&dir=asc", cookie);
        var flagged = await GetAsync("/ui/sources?flag=1", cookie);

        Assert.True(byItems.IndexOf("Feed 2", StringComparison.Ordinal) < byItems.IndexOf("Feed 1", StringComparison.Ordinal));
        Assert.True(byFeed.IndexOf("Feed 1", StringComparison.Ordinal) < byFeed.IndexOf("Feed 2", StringComparison.Ordinal));
        Assert.Contains("never shown", flagged, StringComparison.Ordinal);
        Assert.DoesNotContain("Feed 1", flagged, StringComparison.Ordinal);
        Assert.Contains("aria-sort=\"descending\"", byItems, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logging_out_clears_the_cookie()
    {
        var cookie = await LoginAsync();

        var response = await PostAsync("/ui/logout", cookie);

        Assert.Equal("/ui/login", response.Headers.Location!.OriginalString);
        Assert.Contains($"{UiSession.CookieName}=;", response.Headers.GetValues("Set-Cookie").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mcp_guard_and_healthz_are_unchanged_by_the_ui()
    {
        var cookie = await LoginAsync();

        HttpRequestMessage Rpc(string? bearer, string? origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Add("Cookie", $"{UiSession.CookieName}={cookie}");
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Rpc(null, null))).StatusCode);   // a UI session is not an MCP credential
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Rpc("nope", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(Rpc(Token, "http://localhost"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Rpc(Token, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/healthz")).StatusCode);
    }

    private async Task<long> PostAsync(long feed, string title, int hoursAgo, string content = "Plain body text.", string url = "https://example.com/p")
    {
        var id = await Seed.ItemAsync(pg, feed, title, TestVectors.OneHot(1), DateTime.UtcNow.AddHours(-hoursAgo), content: content);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update items set url = @url where id = @id", new { id, url });
        return id;
    }

    private async Task CategoryAsync(long feed, string category)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update feeds set category = @category where id = @feed", new { feed, category });
    }

    [Fact]
    public async Task The_posts_page_shows_summaries_or_excerpts_and_filters_by_query_string()
    {
        var withSummary = await PostAsync(1, "Has a summary", 1);
        await PostAsync(2, "Has only an excerpt", 2, "Raw feed text   with\n\nodd   spacing.");
        var voted = await PostAsync(2, "Already voted", 3);
        await CategoryAsync(1, "Postgres");
        await CategoryAsync(2, "Security");
        await Seed.ReadAsync(pg, withSummary, "homelab", "improve");
        await new FeedbackStore(pg.Db).SetVoteAsync(voted, 1, default);
        var cookie = await LoginAsync();

        var all = await GetAsync("/ui/posts", cookie);
        Assert.Contains("Has a summary", all, StringComparison.Ordinal);
        Assert.Contains("<p>s</p>", all, StringComparison.Ordinal);
        Assert.Contains("Raw feed text with odd spacing.", all, StringComparison.Ordinal);
        Assert.Contains("in Security", all, StringComparison.Ordinal);
        Assert.Contains("name=\"unrated\"", all, StringComparison.Ordinal);
        Assert.Contains("href=\"/ui/posts\"", all, StringComparison.Ordinal);   // nav and card forms point back at the page

        var byCategory = await GetAsync("/ui/posts?category=Postgres", cookie);
        Assert.Contains("Has a summary", byCategory, StringComparison.Ordinal);
        Assert.DoesNotContain("Has only an excerpt", byCategory, StringComparison.Ordinal);

        var unrated = await GetAsync("/ui/posts?unrated=1&summary=1&kind=improve&days=30", cookie);
        Assert.Contains("Has a summary", unrated, StringComparison.Ordinal);
        Assert.DoesNotContain("Already voted", unrated, StringComparison.Ordinal);
        Assert.Contains("No posts match", await GetAsync("/ui/posts?kind=fyi", cookie), StringComparison.Ordinal);
        Assert.Contains("Has a summary", await GetAsync("/ui/posts?kind=bogus&days=bogus&feed=x&before=zzz", cookie), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_posts_page_pages_with_a_keyset_link_and_keeps_the_filters()
    {
        for (var i = 0; i < 35; i++)
        {
            await PostAsync(1, $"Post {i:00}", i + 1);
        }

        var cookie = await LoginAsync();
        var first = await GetAsync("/ui/posts?days=30", cookie);
        var next = Regex.Match(first, "rel=\"next\" href=\"([^\"]+)\"").Groups[1].Value.Replace("&amp;", "&");

        Assert.Contains("Post 00", first, StringComparison.Ordinal);
        Assert.Contains("Post 29", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Post 30", first, StringComparison.Ordinal);
        Assert.StartsWith("/ui/posts?days=30&before=", next, StringComparison.Ordinal);

        var second = await GetAsync(next, cookie);
        Assert.Contains("Post 30", second, StringComparison.Ordinal);
        Assert.Contains("Post 34", second, StringComparison.Ordinal);
        Assert.DoesNotContain("Post 29", second, StringComparison.Ordinal);
        Assert.DoesNotContain("rel=\"next\"", second, StringComparison.Ordinal);
        Assert.Contains("Back to the newest", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vote_on_the_posts_page_returns_to_the_same_filtered_page_and_anchor_and_can_be_cleared()
    {
        var id = await PostAsync(1, "Votable", 1);
        await Seed.ReadAsync(pg, id, "homelab", "fyi");
        var cookie = await LoginAsync();
        var page = await GetAsync("/ui/posts?kind=fyi&unrated=1", cookie);
        Assert.Contains($"value=\"/ui/posts?kind=fyi&amp;unrated=1#item-{id}\"", page, StringComparison.Ordinal);

        var up = await PostAsync("/ui/vote", cookie, ("item", id.ToString()), ("v", "up"), ("back", $"/ui/posts?kind=fyi&unrated=1#item-{id}"));
        Assert.Equal($"/ui/posts?kind=fyi&unrated=1#item-{id}", up.Headers.Location!.OriginalString);
        Assert.Equal(1, (await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
        Assert.Contains("Clear vote", await GetAsync("/ui/posts", cookie), StringComparison.Ordinal);

        var clear = await PostAsync("/ui/vote", cookie, ("item", id.ToString()), ("v", "clear"), ("back", "/ui/posts"));
        Assert.Equal(HttpStatusCode.SeeOther, clear.StatusCode);
        Assert.Null((await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
        Assert.DoesNotContain("Clear vote", await GetAsync("/ui/posts", cookie), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_feedback_page_lists_rated_items_per_tab_with_counts_and_rates()
    {
        var liked = await PostAsync(1, "Liked post", 1);
        var disliked = await PostAsync(1, "Disliked post", 2);
        var filed = await PostAsync(1, "Filed post", 3);
        await Seed.ReadAsync(pg, filed, "homelab", "improve");
        var feedback = new FeedbackStore(pg.Db);
        await feedback.SetVoteAsync(liked, 1, default);
        await feedback.SetVoteAsync(disliked, -1, default);
        await feedback.SetVoteAsync(filed, 1, default);
        await feedback.AddIdeaAsync(new Idea { ItemId = filed, PlaneProject = "LAB", PlaneIssueId = "i-9", Title = "t", At = DateTime.UtcNow }, default);
        var cookie = await LoginAsync();

        var up = await GetAsync("/ui/feedback", cookie);
        Assert.Contains("Liked post", up, StringComparison.Ordinal);
        Assert.DoesNotContain("Disliked post", up, StringComparison.Ordinal);
        Assert.Contains("👍 Liked <span class=\"count\">2</span>", up, StringComparison.Ordinal);
        Assert.Contains("👎 Disliked <span class=\"count\">1</span>", up, StringComparison.Ordinal);
        Assert.Contains("💡 Filed ideas <span class=\"count\">1</span>", up, StringComparison.Ordinal);
        Assert.Contains("67%", up, StringComparison.Ordinal);   // 2 of 3 votes, in both windows
        Assert.Contains("Clear vote", up, StringComparison.Ordinal);

        var down = await GetAsync("/ui/feedback?tab=down", cookie);
        Assert.Contains("Disliked post", down, StringComparison.Ordinal);
        Assert.DoesNotContain("Liked post", down, StringComparison.Ordinal);

        var ideas = await GetAsync("/ui/feedback?tab=idea", cookie);
        Assert.Contains("Filed post", ideas, StringComparison.Ordinal);
        Assert.Contains("Filed in LAB", ideas, StringComparison.Ordinal);
        Assert.DoesNotContain("Liked post", ideas, StringComparison.Ordinal);

        Assert.Contains("Liked post", await GetAsync("/ui/feedback?tab=bogus&before=junk", cookie), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_feedback_page_is_empty_friendly_and_pages_by_rating_time()
    {
        var cookie = await LoginAsync();
        Assert.Contains("Nothing here yet", await GetAsync("/ui/feedback", cookie), StringComparison.Ordinal);

        for (var i = 0; i < 32; i++)
        {
            await new FeedbackStore(pg.Db).SetVoteAsync(await PostAsync(1, $"Rated {i:00}", 1), 1, default);
            await Task.Delay(2);
        }

        var first = await GetAsync("/ui/feedback", cookie);
        var next = Regex.Match(first, "rel=\"next\" href=\"([^\"]+)\"").Groups[1].Value.Replace("&amp;", "&");
        Assert.Contains("Rated 31", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Rated 01", first, StringComparison.Ordinal);
        var second = await GetAsync(next, cookie);
        Assert.Contains("Rated 01", second, StringComparison.Ordinal);
        Assert.Contains("Rated 00", second, StringComparison.Ordinal);
        Assert.DoesNotContain("Rated 31", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Posts_and_feedback_encode_hostile_feed_content()
    {
        var id = await PostAsync(1, $"{Hostile} title", 1, $"{Hostile} excerpt \" onmouseover=\"x", "javascript:alert(1)");
        await CategoryAsync(1, $"{Hostile} category");
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update feeds set title = @t where id = 1", new { t = $"{Hostile} feed" });
        }

        await new FeedbackStore(pg.Db).SetVoteAsync(id, 1, default);
        await new FeedbackStore(pg.Db).AddIdeaAsync(new Idea { ItemId = id, PlaneProject = "LAB", PlaneIssueId = "i", Title = Hostile, At = DateTime.UtcNow }, default);
        var cookie = await LoginAsync();
        var pages = new[]
        {
            await GetAsync("/ui/posts", cookie), await GetAsync($"/ui/posts?category={Uri.EscapeDataString(Hostile + " category")}", cookie),
            await GetAsync($"/ui/posts?category={Uri.EscapeDataString("\"><script>alert(2)</script>")}&project={Uri.EscapeDataString("\"><script>alert(3)</script>")}", cookie),
            await GetAsync("/ui/feedback", cookie), await GetAsync("/ui/feedback?tab=idea", cookie),
        };

        foreach (var page in pages)
        {
            Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onmouseover=\"x", page, StringComparison.Ordinal);
        }

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; title", pages[0], StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; category", pages[0], StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; feed", pages[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Today_opens_with_the_day_in_brief_built_from_the_digest()
    {
        var (item, _) = await SeedDigestAsync($"{Hostile} brief", suggestion: "Do it.");
        await new AnalysisStore(pg.Db).SaveReadAsync(item, new ReadResult { Summary = $"{Hostile} summary", Why = "w", Kind = "improve", Project = "homelab", Suggestion = "Do it." }, "m", default);
        var cookie = await LoginAsync();

        var page = await GetAsync("/ui", cookie);

        Assert.Contains("The day in brief", page, StringComparison.Ordinal);
        Assert.Contains("1 highlights, 1 with a suggestion", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"#item-{item}\"", page, StringComparison.Ordinal);
        Assert.True(page.IndexOf("The day in brief", StringComparison.Ordinal) < page.IndexOf("Spend this month", StringComparison.Ordinal));
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The day in brief", await GetAsync("/ui/digests", cookie), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Posts_search_and_the_item_page_show_where_else_a_story_appeared()
    {
        var first = await PostAsync(1, "Story hnsw first", 3, url: "https://a.example/1");
        var second = await PostAsync(2, "Story hnsw second", 2, url: "https://b.example/2");
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set cluster_of = @first where id = @second", new { first, second });
            await c.ExecuteAsync("update feeds set title = @t where id = 2", new { t = $"{Hostile} feed" });
        }

        var cookie = await LoginAsync();
        var posts = await GetAsync("/ui/posts", cookie);
        var search = await GetAsync("/ui/search?q=hnsw", cookie);
        var item = await GetAsync($"/ui/item/{first}", cookie);

        Assert.Contains("Also in: <a href=\"https://b.example/2\" rel=\"noopener noreferrer\" target=\"_blank\">&lt;script&gt;alert(1)&lt;/script&gt; feed</a>", posts, StringComparison.Ordinal);
        Assert.Contains("Also in: <a href=\"https://a.example/1\"", posts, StringComparison.Ordinal);
        Assert.Contains("+1 similar", search, StringComparison.Ordinal);
        Assert.Contains("Also in:", item, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", posts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_sources_page_mutes_and_unmutes_a_feed_and_shows_posts_per_week()
    {
        for (var i = 0; i < 3; i++)
        {
            await PostAsync(1, $"busy {i}", i + 1);
        }

        await PostAsync(2, "quiet", 1);
        var cookie = await LoginAsync();
        var items = new ItemStore(pg.Db);

        var page = await GetAsync("/ui/sources?sort=perweek&dir=desc", cookie);
        Assert.Contains("Posts/week", page, StringComparison.Ordinal);
        Assert.Contains("0.7", page, StringComparison.Ordinal);   // 3 posts in 30 days
        Assert.True(page.IndexOf("Feed 1", StringComparison.Ordinal) < page.IndexOf("Feed 2", StringComparison.Ordinal));
        Assert.Contains("aria-label=\"Mute Feed 1\"", page, StringComparison.Ordinal);

        var mute = await PostAsync("/ui/feeds/mute", cookie, ("feed", "1"), ("mute", "1"), ("back", "/ui/sources?sort=perweek&dir=desc"));
        Assert.Equal("/ui/sources?sort=perweek&dir=desc", mute.Headers.Location!.OriginalString);
        Assert.True((await items.FeedsAsync(default)).Single(f => f.Id == 1).Muted);
        var after = await GetAsync("/ui/sources", cookie);
        Assert.Contains("<span class=\"badge\">muted</span>", after, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Unmute Feed 1\"", after, StringComparison.Ordinal);

        Assert.DoesNotContain("busy 0", await GetAsync("/ui/posts", cookie), StringComparison.Ordinal);
        Assert.Contains("busy 0", await GetAsync("/ui/posts?muted=1", cookie), StringComparison.Ordinal);
        Assert.Contains("Show muted feeds", await GetAsync("/ui/posts", cookie), StringComparison.Ordinal);

        await PostAsync("/ui/feeds/mute", cookie, ("feed", "1"), ("mute", "0"));
        Assert.False((await items.FeedsAsync(default)).Single(f => f.Id == 1).Muted);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/ui/feeds/mute", cookie, ("feed", "99"), ("mute", "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/ui/feeds/mute", cookie, ("feed", "1"), ("mute", "maybe"))).StatusCode);
    }

    [Fact]
    public async Task Muting_needs_the_same_csrf_protection_as_other_posts()
    {
        await PostAsync(1, "x", 1);
        var cookie = await LoginAsync();

        var response = await _http.SendAsync(Req(HttpMethod.Post, "/ui/feeds/mute", cookie, "https://evil.example", [new("feed", "1"), new("mute", "1"), new("_csrf", "x")]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await new ItemStore(pg.Db).FeedsAsync(default)).Single().Muted);
    }

    [Fact]
    public async Task The_today_brief_skips_items_from_muted_feeds()
    {
        var (item, _) = await SeedDigestAsync();
        await new ItemStore(pg.Db).SetFeedMutedAsync(1, true, default);

        var page = await GetAsync("/ui", await LoginAsync());

        Assert.DoesNotContain("The day in brief", page, StringComparison.Ordinal);
        Assert.Contains("Postgres 19 lands", page, StringComparison.Ordinal);   // the digest itself is history and stays
    }
}
