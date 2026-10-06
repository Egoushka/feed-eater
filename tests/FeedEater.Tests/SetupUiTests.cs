using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using FeedEater.Setup;
using FeedEater.Ui;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class SetupUiTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Token = "ui-token";

    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _http = null!;

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
                ("FeedEater:Llm:BaseUrl", "http://llm.example/v1/"),
                ("FeedEater:Llm:ApiKey", SetupStubs.ApiKey),
                ("FeedEater:Telegram:Token", SetupStubs.BotToken),
                ("FeedEater:Karakeep:BaseUrl", "http://karakeep.example/"),
                ("FeedEater:Karakeep:Token", "kk-karakeep-key"),
                ("FeedEater:Plane:BaseUrl", "http://plane.example/"),
                ("FeedEater:Plane:Token", "plane-secret-key"),
                ("FeedEater:Plane:Workspace", "ws"),
                ("FeedEater:ProfilePath", "/nope/profile.json"),
            ];
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            var stub = SetupStubs.Services(request => request.RequestUri!.Host == "plane.example" ? StubHandler.Json("{}", HttpStatusCode.Unauthorized) : null);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IHostedService>();
                SetupStubs.Use(s, stub);
            });
        });
        _http = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<string> LoginAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ui/login") { Content = new FormUrlEncodedContent([new("token", Token)]) };
        request.Headers.Add("Origin", "http://localhost");
        var response = await _http.SendAsync(request);
        return response.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith(UiSession.CookieName + "=", StringComparison.Ordinal))[(UiSession.CookieName.Length + 1)..];
    }

    private async Task<HttpResponseMessage> GetSetupAsync(string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ui/setup");
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{UiSession.CookieName}={cookie}");
        }

        return await _http.SendAsync(request);
    }

    [GeneratedRegex("""<span class="badge[^"]*">(ok|off|warn|FAIL)</span></td><td>([^<]+)""", RegexOptions.None, 1000)]
    private static partial Regex RowPattern();

    [Fact]
    public async Task The_setup_page_is_behind_the_login()
    {
        var response = await GetSetupAsync(null);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/ui/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_setup_page_shows_the_same_checks_with_the_same_results_as_doctor()
    {
        var cookie = await LoginAsync();

        var response = await GetSetupAsync(cookie);
        var html = await response.Content.ReadAsStringAsync();
        var doctor = await _app.Services.GetRequiredService<CheckRunner>().RunAsync(default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var shown = RowPattern().Matches(html).Select(m => (Label: m.Groups[1].Value, Name: m.Groups[2].Value.Trim())).ToList();
        Assert.Equal(doctor.Select(r => (DoctorReport.Label(r.Result.Status), r.Name)), shown);
        Assert.Contains(("FAIL", "plane"), shown);
        Assert.Contains(("ok", "karakeep"), shown);
        Assert.Contains(("off", "hindsight"), shown);
        Assert.Contains(("warn", "profile"), shown);
        Assert.Contains("href=\"/ui/setup\"", html, StringComparison.Ordinal);
        Assert.Contains("required checks fail.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_setup_page_shows_the_fix_and_holds_no_secret()
    {
        var html = await (await GetSetupAsync(await LoginAsync())).Content.ReadAsStringAsync();

        Assert.Contains("FeedEater__Plane__Token", html, StringComparison.Ordinal);
        Assert.Contains("Using the example interests; edit /nope/profile.json", html, StringComparison.Ordinal);
        foreach (var secret in new[] { SetupStubs.ApiKey, SetupStubs.BotToken, "plane-secret-key", "kk-karakeep-key", Token })
        {
            Assert.DoesNotContain(secret, html, StringComparison.Ordinal);
        }
    }
}
