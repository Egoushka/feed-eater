using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FeedEater.Inbox;
using FeedEater.Library;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Swipe;
using FeedEater.Telegram;
using FeedEater.Ui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class ResurfaceTests(PostgresFixture pg, ResurfaceTests.Host host) : IAsyncLifetime, IClassFixture<ResurfaceTests.Host>
{
    private const string Token = "resurface-token";

    // Sunday 2026-10-25 10:30 in Kyiv (UTC+2): the weekly run is due.
    private static readonly DateTimeOffset Sunday = new(2026, 10, 25, 8, 30, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

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
                ];
                foreach (var (key, value) in settings)
                {
                    b.UseSetting(key, value);
                }

                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<IHostedService>();
                    s.RemoveAll<TimeProvider>();
                    s.AddSingleton(TimeProvider.System);
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
            await app!.Services.GetRequiredService<FeedDb>().DataSource.DisposeAsync();
            await app.DisposeAsync();
        }
    }

    [GeneratedRegex("""<meta name="csrf" content="([^"]+)">""", RegexOptions.None, 1000)]
    private static partial Regex CsrfPattern();

    private sealed record Rig(ResurfaceJob Job, FakeTimeProvider Time, List<string> Sent);

    private Rig Build(int perWeek = 3, DateTimeOffset? at = null)
    {
        var time = new FakeTimeProvider(at ?? Sunday);
        var sent = new List<string>();
        var options = Options.Create(new FeedEaterOptions
        {
            TimeZone = "Europe/Kyiv",
            Telegram = new TelegramOptions { Token = "T", AllowedUserId = 42 },
            Library = new LibraryOptions { ResurfacePerWeek = perWeek },
        });
        var stub = new StubHandler((_, body) =>
        {
            sent.Add(body);
            return StubHandler.Json("""{"ok":true,"result":{"message_id":1}}""");
        });
        var cursors = new CursorStore(pg.Db);
        var job = new ResurfaceJob(
            new LibraryStore(pg.Db), new ItemStore(pg.Db), new InboxService(new InboxStore(pg.Db), new TelegramClient(stub.Client("http://tg/botT/")), options, time),
            new QuietHours(cursors, options, time), cursors, options, new LoopHealth(time), time, NullLogger<ResurfaceJob>.Instance);
        return new Rig(job, time, sent);
    }

    private async Task<long> SavedAsync(string title, int daysAgo, bool read = false, string source = "telegram")
    {
        var id = await Seed.ItemAsync(pg, 1, title, TestVectors.OneHot(1), Sunday.UtcDateTime.AddDays(-daysAgo - 1));
        var store = new LibraryStore(pg.Db);
        var at = Sunday.AddDays(-daysAgo);
        await store.AddAsync(id, source, at, default);
        if (read)
        {
            await store.MarkReadAsync(id, true, at, default);
        }

        return id;
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no row");
    }

    private async Task SqlAsync(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(sql, args);
    }

    [Fact]
    public async Task One_run_offers_the_unread_saves_of_two_weeks_ago_and_no_others()
    {
        var a = await SavedAsync("Old A", 20);
        var b = await SavedAsync("Old B", 20);
        await SavedAsync("Read", 20, read: true);
        await SavedAsync("Fresh", 3);
        var rig = Build();

        await rig.Job.TickAsync(default);

        Assert.Equivalent(new[] { a, b }, await ItemIdsAsync());
        Assert.Equal(2, rig.Sent.Count);
        Assert.Contains("still unread: Old", rig.Sent[0], StringComparison.Ordinal);
        Assert.Contains("Saved 20 days ago", rig.Sent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("reply_markup", rig.Sent[0], StringComparison.Ordinal);
    }

    private async Task<long[]> ItemIdsAsync()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.QueryAsync<long>("select item_id from inbox where kind = 'resurface' order by id")).ToArray();
    }

    [Fact]
    public async Task A_second_run_the_same_week_adds_nothing_and_the_next_week_waits_out_thirty_days()
    {
        await SavedAsync("Old A", 20);
        await SavedAsync("Old B", 20);
        var rig = Build();

        await rig.Job.TickAsync(default);
        await rig.Job.TickAsync(default);
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from inbox where kind = 'resurface'"));

        rig.Time.Advance(TimeSpan.FromDays(7));
        await rig.Job.TickAsync(default);
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from inbox where kind = 'resurface'"));

        await SqlAsync("update inbox set created_at = now() - interval '31 days'");
        rig.Time.Advance(TimeSpan.FromDays(7));
        await rig.Job.TickAsync(default);
        Assert.Equal(4, await ScalarAsync<int>("select count(*) from inbox where kind = 'resurface'"));
    }

    [Fact]
    public async Task A_run_offers_at_most_the_weekly_limit_oldest_first()
    {
        var oldest = await SavedAsync("S1", 40);
        await SavedAsync("S2", 30);
        await SavedAsync("S3", 25);
        await SavedAsync("S4", 20);
        var rig = Build(perWeek: 3);

        await rig.Job.TickAsync(default);

        var ids = await ItemIdsAsync();
        Assert.Equal(3, ids.Length);
        Assert.Contains(oldest, ids);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(body)!.AsObject();
        json["csrf"] = host.Csrf;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/swipe/" + path) { Content = JsonContent.Create(json) };
        request.Headers.Add("Origin", "http://localhost");
        request.Headers.Add("Cookie", host.Cookie);
        return await host.Client.SendAsync(request);
    }

    private async Task<(long Session, JsonElement Card)> CardAsync(long item)
    {
        var entry = new InboxEntry($"resurface:{item}:t", InboxKinds.Resurface, ItemId: item, ExpiresAt: DateTimeOffset.UtcNow.AddDays(7), Payload: new()
        {
            ["savedAt"] = (await ScalarAsync<DateTime>("select at from saved where item_id = @item", new { item })).ToString("O"),
            ["title"] = "t",
        });
        await new InboxStore(pg.Db).AddAsync(entry, DateTimeOffset.UtcNow, default);
        var next = await (await PostAsync("next", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("card", next.GetProperty("type").GetString());
        var card = next.GetProperty("card");
        return (card.GetProperty("session").GetInt64(), card);
    }

    private async Task<JsonElement> ActAsync(long session, string act, Guid? id = null)
    {
        var response = await PostAsync("act", new { session, position = 0, act, clientId = (id ?? Guid.NewGuid()).ToString() });
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> UndoAsync(long session, Guid id) =>
        await (await PostAsync("undo", new { session, position = 0, clientId = id.ToString() })).Content.ReadFromJsonAsync<JsonElement>();

    private Task<bool> DoneAsync() => ScalarAsync<bool>("select done_at is not null from inbox where kind = 'resurface'");

    [Fact]
    public async Task The_card_says_how_long_the_item_has_waited_and_offers_keep_remove_read_open()
    {
        var item = await SavedAsync("Waiting", 20);
        await SqlAsync("update saved set at = now() - interval '20 days' where item_id = @item", new { item });

        var (_, card) = await CardAsync(item);

        Assert.Equal("resurface", card.GetProperty("kind").GetString());
        Assert.Equal("Waiting", card.GetProperty("title").GetString());
        Assert.Equal("Saved 20 days ago, still unread", card.GetProperty("why").GetString());
        Assert.True(card.GetProperty("detail").TryGetProperty("savedAt", out _));
    }

    [Fact]
    public async Task Keep_closes_the_entry_and_undo_reopens_it()
    {
        var item = await SavedAsync("Keep me", 20);
        var (session, _) = await CardAsync(item);
        var id = Guid.NewGuid();

        Assert.True((await ActAsync(session, "keep", id)).GetProperty("ok").GetBoolean());
        Assert.True(await DoneAsync());
        Assert.True(await ScalarAsync<bool>("select exists (select 1 from saved where item_id = @item)", new { item }));

        Assert.True((await UndoAsync(session, id)).GetProperty("ok").GetBoolean());
        Assert.False(await DoneAsync());
    }

    [Fact]
    public async Task Open_does_nothing_to_the_entry_or_the_save()
    {
        var item = await SavedAsync("Open me", 20);
        var (session, _) = await CardAsync(item);

        Assert.True((await ActAsync(session, "open")).GetProperty("ok").GetBoolean());

        Assert.False(await DoneAsync());
        Assert.True(await ScalarAsync<bool>("select exists (select 1 from saved where item_id = @item)", new { item }));
    }

    [Fact]
    public async Task Read_marks_the_item_read_and_undo_marks_it_unread_and_reopens_the_entry()
    {
        var item = await SavedAsync("Read me", 20);
        var (session, _) = await CardAsync(item);
        var id = Guid.NewGuid();

        Assert.True((await ActAsync(session, "read", id)).GetProperty("ok").GetBoolean());
        Assert.True(await ScalarAsync<bool>("select read_at is not null from saved where item_id = @item", new { item }));
        Assert.True(await DoneAsync());

        Assert.True((await UndoAsync(session, id)).GetProperty("ok").GetBoolean());
        Assert.False(await ScalarAsync<bool>("select read_at is not null from saved where item_id = @item", new { item }));
        Assert.False(await DoneAsync());
    }

    [Fact]
    public async Task Remove_drops_the_save_and_undo_restores_it_with_its_source_and_time_and_no_vote()
    {
        var item = await SavedAsync("Remove me", 20, source: "share");
        var savedAt = await ScalarAsync<DateTime>("select at from saved where item_id = @item", new { item });
        var (session, _) = await CardAsync(item);
        var id = Guid.NewGuid();

        Assert.True((await ActAsync(session, "remove", id)).GetProperty("ok").GetBoolean());
        Assert.False(await ScalarAsync<bool>("select exists (select 1 from saved where item_id = @item)", new { item }));
        Assert.True(await DoneAsync());

        Assert.True((await UndoAsync(session, id)).GetProperty("ok").GetBoolean());
        Assert.Equal("share", await ScalarAsync<string>("select source from saved where item_id = @item", new { item }));
        Assert.Equal(savedAt, await ScalarAsync<DateTime>("select at from saved where item_id = @item", new { item }), TimeSpan.FromSeconds(1));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from votes where item_id = @item", new { item }));
        Assert.False(await DoneAsync());
    }

    [Fact]
    public async Task Undo_of_a_remove_on_an_item_that_was_already_unsaved_adds_nothing()
    {
        var item = await SavedAsync("Gone", 20);
        var (session, _) = await CardAsync(item);
        await SqlAsync("delete from saved where item_id = @item", new { item });
        var id = Guid.NewGuid();

        await ActAsync(session, "remove", id);
        Assert.True((await UndoAsync(session, id)).GetProperty("ok").GetBoolean());

        Assert.False(await ScalarAsync<bool>("select exists (select 1 from saved where item_id = @item)", new { item }));
        Assert.False(await DoneAsync());
    }

    [Fact]
    public async Task An_item_read_after_the_entry_was_made_has_no_card()
    {
        var item = await SavedAsync("Read elsewhere", 20);
        await new InboxStore(pg.Db).AddAsync(new InboxEntry($"resurface:{item}:t", InboxKinds.Resurface, ItemId: item, ExpiresAt: DateTimeOffset.UtcNow.AddDays(7)), DateTimeOffset.UtcNow, default);
        await SqlAsync("update saved set read_at = now() where item_id = @item", new { item });

        var next = await (await PostAsync("next", new { })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("summary", next.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_read_act_on_an_item_read_meanwhile_keeps_its_read_time_through_undo()
    {
        var item = await SavedAsync("Read meanwhile", 20);
        var (session, _) = await CardAsync(item);
        await SqlAsync("update saved set read_at = now() - interval '1 day' where item_id = @item", new { item });
        var before = await ScalarAsync<DateTime>("select read_at from saved where item_id = @item", new { item });
        var id = Guid.NewGuid();

        await ActAsync(session, "read", id);
        Assert.Equal(before, await ScalarAsync<DateTime>("select read_at from saved where item_id = @item", new { item }));

        await UndoAsync(session, id);
        Assert.Equal(before, await ScalarAsync<DateTime>("select read_at from saved where item_id = @item", new { item }));
    }

    [Fact]
    public async Task The_swipe_page_has_the_resurface_button_set_and_the_script_maps_the_swipes()
    {
        var shell = await (await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/swipe") { Headers = { { "Cookie", host.Cookie } } })).Content.ReadAsStringAsync();
        foreach (var marker in new[] { "data-for=\"resurface\"", "data-do=\"remove\"", "data-do=\"read\"", "✅ keep", "🗑 remove", "✓ read", "↗ open" })
        {
            Assert.Contains(marker, shell, StringComparison.Ordinal);
        }

        var script = await host.Client.GetStringAsync("/swipe/app.js");
        Assert.Contains("card.kind === 'resurface'", script, StringComparison.Ordinal);
        Assert.Contains("['remove', 'Removed']", script, StringComparison.Ordinal);
        Assert.Contains("['read', 'Marked read']", script, StringComparison.Ordinal);
    }
}
