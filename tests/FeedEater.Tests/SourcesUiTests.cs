using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Fetch;
using FeedEater.Storage;
using FeedEater.Ui;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class SourcesUiTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Token = "ui-token";
    private readonly List<string> _requested = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _network = _ => SourceKit.Status(HttpStatusCode.NotFound);
    private readonly List<WebApplicationFactory<Program>> _apps = [];

    public Task InitializeAsync() => pg.ResetAsync();

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.DisposeAsync();
        }
    }

    private HttpClient Client(string kind = "builtin")
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:FeedEater", pg.ConnectionString)
            .UseSetting("Mcp:Token", Token)
            .UseSetting("FeedEater:RunJobs", "false")
            .UseSetting("FeedEater:Source:Kind", kind)
            .UseSetting("FeedEater:Llm:BaseUrl", "http://127.0.0.1:9/")
            .ConfigureTestServices(s =>
            {
                s.RemoveAll<IHostedService>();
                s.RemoveAll<SafeFetcher>();
                s.AddSingleton(sp => new SafeFetcher(
                    new HttpClient(new StubHandler((request, _) =>
                    {
                        lock (_requested)
                        {
                            _requested.Add(request.RequestUri!.AbsoluteUri);
                        }

                        return _network(request);
                    })),
                    sp.GetRequiredService<IOptions<FeedEaterOptions>>(), sp.GetRequiredService<CursorStore>(), TimeProvider.System, NullLogger<SafeFetcher>.Instance));
            }));
        _apps.Add(app);
        return app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
    }

    private static async Task<string> LoginAsync(HttpClient http)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/ui/login") { Content = new FormUrlEncodedContent([new("token", Token)]) };
        request.Headers.Add("Origin", "http://localhost");
        var response = await http.SendAsync(request);
        return response.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith(UiSession.CookieName + "=", StringComparison.Ordinal))[(UiSession.CookieName.Length + 1)..];
    }

    private static async Task<string> GetAsync(HttpClient http, string path, string cookie, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{UiSession.CookieName}={cookie}");
        var response = await http.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [GeneratedRegex("name=\"_csrf\" value=\"([^\"]+)\"")]
    private static partial Regex CsrfPattern();

    private static async Task<string> CsrfAsync(HttpClient http, string cookie) => CsrfPattern().Match(await GetAsync(http, "/ui/sources", cookie)).Groups[1].Value;

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, string path, string cookie, HttpContent content, string? origin = "http://localhost")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Cookie", $"{UiSession.CookieName}={cookie}");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await http.SendAsync(request);
    }

    private static FormUrlEncodedContent Form(string csrf, params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).Append(new("_csrf", csrf)));

    private static MultipartFormDataContent Upload(string csrf, string? file, string name = "feeds.opml")
    {
        var content = new MultipartFormDataContent { { new StringContent(csrf), "_csrf" } };
        if (file is not null)
        {
            content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(file)), "file", name);
        }

        return content;
    }

    private async Task<List<(string Title, string? Category, string Url)>> FeedsAsync()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.QueryAsync<(string, string?, string)>("select title, category, feed_url from feeds where feed_url is not null order by title")).ToList();
    }

    [Fact]
    public async Task The_sources_page_shows_the_add_form_opml_and_each_feed_with_its_error_encoded()
    {
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync(
                """
                insert into feeds (title, category, feed_url, fail_count, last_error)
                values ('<script>alert(1)</script>', 'News & Co', 'https://bad.example/feed?a=1&b=<2>', 4, 'HTTP 404'),
                       ('Healthy', null, 'https://ok.example/feed', 0, null)
                """);
            await c.ExecuteAsync("update feeds set last_fetched_at = '2026-10-06T10:00:00Z' where title = 'Healthy'");
        }

        var http = Client();
        var page = await GetAsync(http, "/ui/sources", await LoginAsync(http));

        Assert.Contains("Add a feed", page, StringComparison.Ordinal);
        Assert.Contains("action=\"/ui/sources/import\" enctype=\"multipart/form-data\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/ui/sources.opml\"", page, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", page, StringComparison.Ordinal);
        Assert.Contains("https://bad.example/feed?a=1&amp;b=&lt;2&gt;", page, StringComparison.Ordinal);
        Assert.Contains("failing 4x</span> HTTP 404", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"badge good\">ok</span>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_a_feed_subscribes_fetches_it_and_shows_a_notice()
    {
        _network = _ => SourceKit.Xml(SourceKit.Rss(("g1", "Fresh post", DateTimeOffset.UtcNow.AddHours(-1), "text")));
        var http = Client();
        var cookie = await LoginAsync(http);

        var response = await PostAsync(http, "/ui/sources/add", cookie, Form(await CsrfAsync(http, cookie), ("url", "https://new.example/feed")));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/ui/sources?src=added", response.Headers.Location!.OriginalString);
        Assert.Equal([("Feed title", null, "https://new.example/feed")], await FeedsAsync());
        var page = await GetAsync(http, "/ui/sources?src=added", cookie);
        Assert.Contains("Feed added.", page, StringComparison.Ordinal);
        var posts = await new ItemStore(pg.Db).PostsAsync(new PostFilter(DateTimeOffset.UtcNow.AddDays(-1), null, null, null, null, false, false), null, 10, default);
        Assert.Equal(["Fresh post"], posts.Select(p => p.Title));
    }

    [Fact]
    public async Task A_feed_that_cannot_be_added_is_explained_in_place_and_nothing_is_stored()
    {
        var http = Client();
        var cookie = await LoginAsync(http);

        var response = await PostAsync(http, "/ui/sources/add", cookie, Form(await CsrfAsync(http, cookie), ("url", "https://gone.example/<feed>")));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Could not read it: HTTP 404.", body, StringComparison.Ordinal);
        Assert.Empty(await FeedsAsync());
    }

    [Fact]
    public async Task Importing_an_opml_file_adds_its_feeds_with_folders_as_categories()
    {
        var http = Client();
        var cookie = await LoginAsync(http);
        const string Opml = """<opml version="2.0"><body><outline text="Tech"><outline text="Blog A" xmlUrl="https://a.example/rss"/></outline><outline text="Loose" xmlUrl="https://b.example/rss"/></body></opml>""";

        var response = await PostAsync(http, "/ui/sources/import", cookie, Upload(await CsrfAsync(http, cookie), Opml));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/ui/sources?src=imported&n=2&dup=0", response.Headers.Location!.OriginalString);
        Assert.Equal([("Blog A", "Tech", "https://a.example/rss"), ("Loose", null, "https://b.example/rss")], await FeedsAsync());
        Assert.Empty(_requested);   // nothing is fetched at import
        Assert.Contains("Imported 2 feeds (0 were already there)", await GetAsync(http, response.Headers.Location.OriginalString, cookie), StringComparison.Ordinal);

        var again = await PostAsync(http, "/ui/sources/import", cookie, Upload(await CsrfAsync(http, cookie), Opml));
        Assert.Equal("/ui/sources?src=imported&n=0&dup=2", again.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData(null)]
    public async Task A_bad_or_missing_upload_is_explained_and_stores_nothing(string? file)
    {
        var http = Client();
        var cookie = await LoginAsync(http);

        var response = await PostAsync(http, "/ui/sources/import", cookie, Upload(await CsrfAsync(http, cookie), file));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await FeedsAsync());
    }

    [Fact]
    public async Task An_opml_file_over_2_MB_is_refused()
    {
        var http = Client();
        var cookie = await LoginAsync(http);

        var response = await PostAsync(http, "/ui/sources/import", cookie, Upload(await CsrfAsync(http, cookie), "<opml><body>" + new string(' ', 2 * 1024 * 1024 + 1) + "</body></opml>"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Export_downloads_the_subscriptions_as_opml_and_needs_a_session()
    {
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into feeds (title, category, feed_url, site_url) values ('Blog A', 'Tech', 'https://a.example/rss', 'https://a.example/'), ('Loose', null, 'https://b.example/rss', null)");
        }

        var http = Client();
        var anonymous = await http.GetAsync("/ui/sources.opml");
        var cookie = await LoginAsync(http);
        var request = new HttpRequestMessage(HttpMethod.Get, "/ui/sources.opml");
        request.Headers.Add("Cookie", $"{UiSession.CookieName}={cookie}");
        var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.SeeOther, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/x-opml", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("feed-eater.opml", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Contains("xmlUrl=\"https://a.example/rss\"", body, StringComparison.Ordinal);
        Assert.Contains("<outline text=\"Tech\" title=\"Tech\">", body, StringComparison.Ordinal);
        Assert.Contains("xmlUrl=\"https://b.example/rss\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_feed_unsubscribes_it_and_keeps_its_items()
    {
        long feed;
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            feed = await c.ExecuteScalarAsync<long>("insert into feeds (title, feed_url) values ('Gone soon', 'https://a.example/rss') returning id");
        }

        var item = await Seed.ItemAsync(pg, feed, "Kept item", TestVectors.OneHot(0));
        var http = Client();
        var cookie = await LoginAsync(http);

        var response = await PostAsync(http, "/ui/sources/remove", cookie, Form(await CsrfAsync(http, cookie), ("feed", feed.ToString())));
        var again = await PostAsync(http, "/ui/sources/remove", cookie, Form(await CsrfAsync(http, cookie), ("feed", feed.ToString())));
        var junk = await PostAsync(http, "/ui/sources/remove", cookie, Form(await CsrfAsync(http, cookie), ("feed", "x")));

        Assert.Equal("/ui/sources?src=removed", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
        Assert.Empty(await FeedsAsync());
        Assert.NotNull(await new ItemStore(pg.Db).GetAsync(item, default));
    }

    [Fact]
    public async Task The_new_posts_need_the_same_origin_and_anti_forgery_value_as_every_other_post()
    {
        var http = Client();
        var cookie = await LoginAsync(http);
        var csrf = await CsrfAsync(http, cookie);

        var noOrigin = await PostAsync(http, "/ui/sources/add", cookie, Form(csrf, ("url", "https://x.example/feed")), origin: null);
        var foreign = await PostAsync(http, "/ui/sources/add", cookie, Form(csrf, ("url", "https://x.example/feed")), origin: "https://evil.example");
        var badToken = await PostAsync(http, "/ui/sources/import", cookie, Upload(csrf + "x", "<opml><body/></opml>"));
        var removal = await PostAsync(http, "/ui/sources/remove", cookie, Form("wrong", ("feed", "1")));

        Assert.All(new[] { noOrigin, foreign, badToken, removal }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Empty(_requested);
    }

    [Fact]
    public async Task In_miniflux_mode_the_page_has_no_panel_and_the_routes_do_not_exist()
    {
        var http = Client("miniflux");
        var cookie = await LoginAsync(http);

        var page = await GetAsync(http, "/ui/sources", cookie);
        var csrf = CsrfPattern().Match(page).Groups[1].Value;
        var add = await PostAsync(http, "/ui/sources/add", cookie, Form(csrf, ("url", "https://x.example/feed")));
        var export = new HttpRequestMessage(HttpMethod.Get, "/ui/sources.opml");
        export.Headers.Add("Cookie", $"{UiSession.CookieName}={cookie}");

        Assert.DoesNotContain("Add a feed", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/ui/sources/", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, add.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(export)).StatusCode);
        Assert.Empty(_requested);
    }
}
