using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using FeedEater.Cards;
using FeedEater.Ui;

namespace FeedEater.Tests;

/// <summary>A2.3 and A2.5: the /ui/cards page and the card MCP tools, booted as the real host.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class CardsEndpointTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Token = "cards-token";
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _http = null!;
    private CardStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _store = new CardStore(pg.Db);
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:FeedEater", pg.ConnectionString);
            b.UseSetting("Mcp:Token", Token);
            b.UseSetting("FeedEater:RunJobs", "false");
            b.UseSetting("FeedEater:Llm:BaseUrl", "http://127.0.0.1:9/");
            b.UseSetting("FeedEater:ProfilePath", "Fixtures/profile.json");
            b.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
        });
        _http = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private static NewCard Card(string title) =>
        new(title.ToLowerInvariant(), title, "what", "feed-eater", "feed-eater: Dapper (nuget, src/App.csproj)", [new EvidenceLink("https://example.com/a", "Evidence <b>A</b>", "your feeds", null)], "small", "gain", []);

    private async Task<string> LoginAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/ui/login") { Content = new FormUrlEncodedContent([new("token", Token)]) };
        request.Headers.Add("Origin", "http://localhost");
        var response = await _http.SendAsync(request);
        return response.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith(UiSession.CookieName + "=", StringComparison.Ordinal))[(UiSession.CookieName.Length + 1)..];
    }

    private async Task<HttpResponseMessage> UiAsync(HttpMethod method, string path, string cookie, params (string Key, string Value)[] form)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{UiSession.CookieName}={cookie}");
        if (method == HttpMethod.Post)
        {
            request.Headers.Add("Origin", "http://localhost");
            request.Content = new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        }

        return await _http.SendAsync(request);
    }

    private async Task<string> CsrfAsync(string cookie) =>
        CsrfPattern().Match(await (await UiAsync(HttpMethod.Get, "/ui/cards", cookie)).Content.ReadAsStringAsync()).Groups[1].Value;

    [GeneratedRegex("name=\"_csrf\" value=\"([^\"]+)\"")]
    private static partial Regex CsrfPattern();

    [Fact]
    public async Task Cards_page_needs_a_session_and_lists_one_row_per_card()
    {
        Assert.Equal(HttpStatusCode.SeeOther, (await _http.GetAsync("/ui/cards")).StatusCode);
        await _store.AddAsync(Card("Dapper: AOT"), default);
        await _store.AddAsync(Card("Npgsql: 10"), default);
        var cookie = await LoginAsync();

        var response = await UiAsync(HttpMethod.Get, "/ui/cards", cookie);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, Regex.Matches(html, "<li class=\"card\">").Count);
        Assert.Contains("Dapper: AOT", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<li class=\"card\">", await (await UiAsync(HttpMethod.Get, "/ui/cards?state=hold", cookie)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cards_detail_escapes_evidence_and_the_state_form_writes_the_reason()
    {
        await _store.AddAsync(Card("Dapper: AOT"), default);
        var id = (await _store.ListAsync(null, 1, default)).Single().Id;
        var cookie = await LoginAsync();

        var detail = await (await UiAsync(HttpMethod.Get, $"/ui/cards/{id}", cookie)).Content.ReadAsStringAsync();
        Assert.Contains("Evidence &lt;b&gt;A&lt;/b&gt;", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>A</b>", detail, StringComparison.Ordinal);

        var csrf = await CsrfAsync(cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await UiAsync(HttpMethod.Post, $"/ui/cards/{id}/state", cookie, ("state", "hold"), ("reason", "r"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UiAsync(HttpMethod.Post, $"/ui/cards/{id}/state", cookie, ("state", "hold"), ("reason", " "), ("_csrf", csrf))).StatusCode);
        var posted = await UiAsync(HttpMethod.Post, $"/ui/cards/{id}/state", cookie, ("state", "hold"), ("reason", "no headscale support"), ("_csrf", csrf));

        Assert.Equal(HttpStatusCode.SeeOther, posted.StatusCode);
        var row = (await _store.GetAsync(id, default))!;
        Assert.Equal(("hold", "no headscale support"), (row.State, row.StateReason));
        Assert.Equal(HttpStatusCode.NotFound, (await UiAsync(HttpMethod.Get, "/ui/cards/999999", cookie)).StatusCode);
    }

    private async Task<string> CallAsync(string tool, string args)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{args}}}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await (await _http.SendAsync(request)).Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Cards_mcp_set_state_rejects_an_unknown_state_and_writes_the_reason()
    {
        await _store.AddAsync(Card("Dapper: AOT"), default);
        var id = (await _store.ListAsync(null, 1, default)).Single().Id;

        Assert.Contains("unknown state", await CallAsync("feed_card_set_state", $$"""{"id":{{id}},"state":"maybe","reason":"x"}"""), StringComparison.Ordinal);
        Assert.Contains("reason is required", await CallAsync("feed_card_set_state", $$"""{"id":{{id}},"state":"hold","reason":""}"""), StringComparison.Ordinal);
        Assert.Equal("proposed", (await _store.GetAsync(id, default))!.State);

        Assert.Contains("no headscale support", await CallAsync("feed_card_set_state", $$"""{"id":{{id}},"state":"hold","reason":"no headscale support"}"""), StringComparison.Ordinal);
        var row = (await _store.GetAsync(id, default))!;
        Assert.Equal(("hold", "no headscale support"), (row.State, row.StateReason));

        var listed = await CallAsync("feed_cards", """{"state":"hold"}""");
        Assert.Contains("Dapper: AOT", listed, StringComparison.Ordinal);
        Assert.Contains("https://example.com/a", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("Dapper: AOT", await CallAsync("feed_cards", """{"state":"adopt"}"""), StringComparison.Ordinal);
    }
}
