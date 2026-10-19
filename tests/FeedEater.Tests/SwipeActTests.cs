using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using FeedEater.Inbox;
using FeedEater.Plane;
using FeedEater.Storage;
using FeedEater.Swipe;
using FeedEater.Ui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using FeedEater.WeeklyIdeas;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class SwipeActTests(PostgresFixture pg, SwipeActTests.Host host) : IAsyncLifetime, IClassFixture<SwipeActTests.Host>
{
    private const string Token = "swipe-token";

    private static readonly ShiftedTime Time = new();
    private static readonly StubHandler Plane = new((request, _) => request.RequestUri!.AbsolutePath switch
    {
        "/api/v1/workspaces/w/projects/" => StubHandler.Json("""{"results":[{"id":"p-lab","identifier":"LAB"}]}"""),
        var p when p.EndsWith("/intake-issues/", StringComparison.Ordinal) => StubHandler.Json("""{"issue":"issue-1"}""", HttpStatusCode.Created),
        _ => StubHandler.Json("{}", HttpStatusCode.NotFound),
    });

    /// <summary>The real clock plus an offset, so the app's time and the database's now() stay comparable; each test starts at zero.</summary>
    private sealed class ShiftedTime : TimeProvider
    {
        private TimeSpan offset;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;

        public void Advance(TimeSpan by) => offset += by;

        public void Reset() => offset = TimeSpan.Zero;
    }

    private HttpClient http = null!;

    private static IReadOnlyList<string> PlaneCreated
    {
        get
        {
            lock (Plane.Calls)
            {
                return [.. Plane.Calls.Where(c => c.Method == HttpMethod.Post && c.Uri.EndsWith("/intake-issues/", StringComparison.Ordinal)).Select(c => c.Uri)];
            }
        }
    }

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        Time.Reset();
        lock (Plane.Calls)
        {
            Plane.Calls.Clear();
        }

        http = host.Client;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// One host for the class, disposed after it: every extra host adds a connection pool, and the shared Postgres has a low connection limit.
    /// Logging in is throttled to 5 a minute, so the session is made once.
    /// </summary>
    public sealed class Host(PostgresFixture pg) : IAsyncLifetime
    {
        private WebApplicationFactory<Program>? app;

        public HttpClient Client { get; private set; } = null!;

        public string Cookie { get; private set; } = "";

        public string Csrf { get; private set; } = "";

        public async Task InitializeAsync()
        {
            app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                (string Key, string Value)[] settings =
                [
                    ("ConnectionStrings:FeedEater", pg.ConnectionString),
                    ("Mcp:Token", Token),
                    ("FeedEater:RunJobs", "false"),
                    ("FeedEater:ProfilePath", "/nope/profile.json"),
                    ("FeedEater:Swipe:Enabled", "true"),
                    ("FeedEater:Swipe:Cards", "3"),
                    ("FeedEater:Plane:BaseUrl", "http://plane/"),
                    ("FeedEater:Plane:Token", "t"),
                    ("FeedEater:Plane:Workspace", "w"),
                    ("FeedEater:Plane:FallbackProject", "FEED"),
                ];
                foreach (var (key, value) in settings)
                {
                    b.UseSetting(key, value);
                }

                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<IHostedService>();
                    s.RemoveAll<TimeProvider>();
                    s.AddSingleton<TimeProvider>(Time);
                    s.AddSingleton<IdeaDecisions>();
                    s.AddHttpClient<PlaneClient>().ConfigurePrimaryHttpMessageHandler(() => Plane);
                });
            });
            Client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
            using var login = new HttpRequestMessage(HttpMethod.Post, "/ui/login") { Content = new FormUrlEncodedContent([new("token", Token)]) };
            login.Headers.Add("Origin", "http://localhost");
            var response = await Client.SendAsync(login);
            Cookie = response.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith(UiSession.CookieName + "=", StringComparison.Ordinal));
            using var shell = new HttpRequestMessage(HttpMethod.Get, "/swipe");
            shell.Headers.Add("Cookie", Cookie);
            Csrf = CsrfPattern().Match(await (await Client.SendAsync(shell)).Content.ReadAsStringAsync()).Groups[1].Value;
        }

        public async Task DisposeAsync()
        {
            Client.Dispose();
            await app!.Services.GetRequiredService<FeedDb>().DataSource.DisposeAsync();   // frees the pool now, not when the idle connections time out
            await app.DisposeAsync();
        }
    }

    [GeneratedRegex("""<meta name="csrf" content="([^"]+)">""", RegexOptions.None, 1000)]
    private static partial Regex CsrfPattern();

    private async Task<HttpResponseMessage> PostAsync(string path, object body)
    {
        var json = JsonSerializer.SerializeToNode(body)!.AsObject();
        json["csrf"] = host.Csrf;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/swipe/" + path) { Content = JsonContent.Create(json) };
        request.Headers.Add("Origin", "http://localhost");
        request.Headers.Add("Cookie", host.Cookie);
        return await http.SendAsync(request);
    }

    private async Task<long> SessionOfNextAsync()
    {
        var next = await (await PostAsync("next", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("card", next.GetProperty("type").GetString());
        return next.GetProperty("card").GetProperty("session").GetInt64();
    }

    private async Task<(long Session, int Position, long Item)> ItemCardAsync()
    {
        var item = await Seed.ItemAsync(pg, 1, "Item", TestVectors.OneHot(1), Time.GetUtcNow().UtcDateTime.AddHours(-2));
        return (await SessionOfNextAsync(), 0, item);
    }

    private async Task<(long Session, int Position)> IdeaCardAsync()
    {
        await new InboxStore(pg.Db).AddAsync(new InboxEntry($"idea:{Guid.NewGuid():N}", InboxKinds.Idea, Payload: new JsonObject
        {
            ["week"] = "2026-W43",
            ["title"] = "Cache it",
            ["project"] = "LAB",
            ["html"] = "<p>x</p>",
            ["url"] = "https://example.com/i",
            ["sourceTitle"] = "S",
            ["why"] = "w",
            ["where"] = "src",
            ["effort"] = "S",
        }, ExpiresAt: Time.GetUtcNow().AddDays(7)), Time.GetUtcNow(), default);
        return (await SessionOfNextAsync(), 0);
    }

    private static async Task<ActResult> ResultAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ActResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private async Task<ActResult> ActAsync(long session, int position, string act, Guid? id) =>
        await ResultAsync(await PostAsync("act", id is null ? new { session, position, act } : new { session, position, act, clientId = id.ToString() }));

    private async Task<ActResult> UndoAsync(long session, int position, Guid id) =>
        await ResultAsync(await PostAsync("undo", new { session, position, clientId = id.ToString() }));

    private async Task SqlAsync(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(sql, args);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no row");
    }

    [Fact]
    public async Task The_same_client_id_twice_votes_once_and_returns_the_first_answer()
    {
        var (session, position, item) = await ItemCardAsync();
        var id = Guid.NewGuid();

        var first = await ActAsync(session, position, "like", id);
        await SqlAsync("update votes set at = now() - interval '1 hour'");
        var second = await ActAsync(session, position, "like", id);

        Assert.Equal(first, second);
        Assert.True(await ScalarAsync<DateTime>("select at from votes where item_id = @item", new { item }) < DateTime.UtcNow.AddMinutes(-30));
    }

    [Fact]
    public async Task Concurrent_repeats_of_one_keep_file_one_Plane_item()
    {
        var (session, position) = await IdeaCardAsync();
        var id = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ActAsync(session, position, "keep", id)));

        Assert.Single(PlaneCreated);
    }

    [Fact]
    public async Task Undo_restores_the_vote_that_was_there_before()
    {
        var (session, position, item) = await ItemCardAsync();
        await new FeedbackStore(pg.Db).SetVoteAsync(item, -1, default, 0.5);
        var id = Guid.NewGuid();
        await ActAsync(session, position, "save", id);

        var undo = await UndoAsync(session, position, id);

        Assert.True(undo.Ok, undo.Message);
        Assert.Equal(-1, await ScalarAsync<short>("select value from votes where item_id = @item", new { item }));
        Assert.DoesNotContain("save", await ScalarAsync<string[]>("select acts from swipe_events where session_id = @session", new { session }));
    }

    [Fact]
    public async Task A_swipe_save_puts_the_item_in_the_library_with_a_firm_vote_and_without_Karakeep()
    {
        var (session, position, item) = await ItemCardAsync();

        var result = await ActAsync(session, position, "save", Guid.NewGuid());

        Assert.Equal(new ActResult(true, "Saved"), result);
        Assert.Equal("swipe", await ScalarAsync<string>("select source from saved where item_id = @item", new { item }));
        Assert.Equal((1, 1f), (await ScalarAsync<short>("select value from votes where item_id = @item", new { item }), await ScalarAsync<float>("select weight from votes where item_id = @item", new { item })));
        Assert.Equal("Already saved", (await ActAsync(session, position, "save", Guid.NewGuid())).Message);
    }

    [Fact]
    public async Task Undoing_a_save_the_swipe_made_removes_the_library_row()
    {
        var (session, position, item) = await ItemCardAsync();
        var id = Guid.NewGuid();
        await ActAsync(session, position, "save", id);

        var undo = await UndoAsync(session, position, id);

        Assert.Equal(new ActResult(true, "Undone; removed from your library"), undo);
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from saved where item_id = @item", new { item }));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from votes where item_id = @item", new { item }));
    }

    [Fact]
    public async Task Undoing_a_swipe_save_of_an_item_saved_before_keeps_the_library_row()
    {
        var (session, position, item) = await ItemCardAsync();
        await SqlAsync("insert into saved (item_id, source) values (@item, 'telegram')", new { item });
        var id = Guid.NewGuid();
        var saved = await ActAsync(session, position, "save", id);

        var undo = await UndoAsync(session, position, id);

        Assert.Equal("Already saved", saved.Message);
        Assert.Equal(new ActResult(true, "Undone; it stays in your library"), undo);
        Assert.Equal("telegram", await ScalarAsync<string>("select source from saved where item_id = @item", new { item }));
    }

    [Fact]
    public async Task Undo_without_a_vote_before_clears_it_and_a_second_undo_is_refused()
    {
        var (session, position, _) = await ItemCardAsync();
        var id = Guid.NewGuid();
        await ActAsync(session, position, "like", id);

        var undo = await UndoAsync(session, position, id);
        var again = await UndoAsync(session, position, id);

        Assert.True(undo.Ok, undo.Message);
        Assert.False(again.Ok);
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from votes"));
    }

    [Fact]
    public async Task Undo_of_a_drop_reopens_the_idea_and_forgets_the_drop()
    {
        var (session, position) = await IdeaCardAsync();
        var id = Guid.NewGuid();
        Assert.True((await ActAsync(session, position, "drop", id)).Ok, "act");
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from weekly_ideas where outcome = 'dropped'"));

        var undo = await UndoAsync(session, position, id);

        Assert.True(undo.Ok, undo.Message);
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from weekly_ideas"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from inbox where done_at is not null"));
    }

    [Fact]
    public async Task Undo_of_a_keep_is_refused_with_the_reason()
    {
        var (session, position) = await IdeaCardAsync();
        var id = Guid.NewGuid();
        Assert.True((await ActAsync(session, position, "keep", id)).Ok, "act");

        var undo = await UndoAsync(session, position, id);

        Assert.False(undo.Ok);
        Assert.Contains("Plane", undo.Message);
        Assert.Single(PlaneCreated);
    }

    [Fact]
    public async Task Undo_of_a_stop_that_closed_nothing_does_not_reopen_the_follow()
    {
        var item = await Seed.ItemAsync(pg, 1, "Item", TestVectors.OneHot(1), Time.GetUtcNow().UtcDateTime.AddHours(-2));
        var follow = await ScalarAsync<long>("insert into follows (root_item_id, started_at, ends_at, status) values (@item, now(), now() + interval '7 days', 'closed') returning id", new { item });
        await new InboxStore(pg.Db).AddAsync(new InboxEntry($"follow:{Guid.NewGuid():N}", InboxKinds.Follow, item, follow, new JsonObject { ["story"] = "S" }, Time.GetUtcNow().AddDays(7)), Time.GetUtcNow(), default);
        var session = await SessionOfNextAsync();
        var id = Guid.NewGuid();
        Assert.True((await ActAsync(session, 0, "stop", id)).Ok, "act");

        var undo = await UndoAsync(session, 0, id);

        Assert.False(undo.Ok);
        Assert.Equal("closed", await ScalarAsync<string>("select status from follows where id = @follow", new { follow }));
    }

    [Fact]
    public async Task Undo_after_a_day_is_refused()
    {
        var (session, position, item) = await ItemCardAsync();
        var id = Guid.NewGuid();
        await ActAsync(session, position, "like", id);
        Time.Advance(TimeSpan.FromHours(25));

        var undo = await UndoAsync(session, position, id);

        Assert.False(undo.Ok);
        Assert.Equal(1, await ScalarAsync<int>("select value::int from votes where item_id = @item", new { item }));
    }

    [Fact]
    public async Task An_act_without_client_id_behaves_as_before()
    {
        var (session, position, item) = await ItemCardAsync();
        await ActAsync(session, position, "like", null);
        await SqlAsync("update votes set at = now() - interval '1 hour'");

        await ActAsync(session, position, "like", null);

        Assert.True(await ScalarAsync<DateTime>("select at from votes where item_id = @item", new { item }) > DateTime.UtcNow.AddMinutes(-30));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from swipe_acts"));
    }

    [Fact]
    public async Task Acts_on_a_session_ended_over_a_day_ago_are_refused()
    {
        var (session, position, _) = await ItemCardAsync();
        await PostAsync("end", new { session });
        Time.Advance(TimeSpan.FromHours(25));

        var late = await ActAsync(session, position, "like", Guid.NewGuid());

        Assert.False(late.Ok);
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from votes"));
    }
}
