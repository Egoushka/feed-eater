using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using FeedEater.Inbox;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FeedEater.Tests;

/// <summary>
/// tests/contract/*.json are real /api/v1 responses on seeded data; the Android app's tests parse the same files. This test compares
/// each live response with its fixture by shape: property names and JSON value kinds, not values. A null on either side matches any
/// kind (an optional field is null in one sample and set in another). Regenerate with: UPDATE_CONTRACT=1 dotnet test --filter ContractTests
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed partial class ContractTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static WebApplicationFactory<Program>? app;

    private WebApplicationFactory<Program> App => app ??= ApiHost.Create(pg.ConnectionString, s =>
    {
        var model = new StubHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("chat/completions", StringComparison.Ordinal)
            ? StubHandler.Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "Nightly [1]." } } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } }))
            : StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
        s.AddHttpClient<FeedEater.Llm.LiteLlmClient>().ConfigurePrimaryHttpMessageHandler(() => model);
        s.AddHttpClient(FeedEater.Fetch.SafeFetcher.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler((_, _) => StubHandler.Json("{}", HttpStatusCode.NotFound)));
        s.AddHttpClient(FeedEater.Fetch.SafeFetcher.FeedClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler((_, _) =>
            SourceKit.Xml(SourceKit.Rss(("g1", "Entry", DateTimeOffset.UtcNow.AddHours(-1), "text")))));
    }, ("FeedEater:Quiet:From", "22:00"), ("FeedEater:Quiet:To", "07:00"));

    private readonly List<(string File, JsonNode Body)> samples = [];

    private async Task<JsonNode> SampleAsync(string file, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK, string? authorization = null)
    {
        var (json, status) = await CallAsync(method, path, body, authorization);
        Assert.Equal(expected, status);
        samples.Add((file, json));
        return json;
    }

    private async Task<(JsonNode Body, HttpStatusCode Status)> CallAsync(HttpMethod method, string path, object? body = null, string? authorization = null)
    {
        authorization ??= "Bearer " + await ApiHost.TokenAsync(App);
        var response = await ApiHost.RawAsync(App.CreateClient(), method, path, authorization, body: body is null ? null : JsonSerializer.Serialize(body));
        return (JsonNode.Parse(await response.Content.ReadAsStringAsync())!, response.StatusCode);
    }

    [Fact]
    public async Task Every_response_type_matches_its_fixture()
    {
        // Every optional field gets a value in at least one sample; a sample where it is null is a separate file.
        var quokka = await Seed.ItemAsync(pg, 1, "Quokka backups", TestVectors.OneHot(1), DateTime.UtcNow.AddHours(-2), content: "Quokka backups run nightly.");
        await Seed.ReadAsync(pg, quokka, "homelab", "improve");
        await Seed.ItemAsync(pg, 1, "Quokka habitat", TestVectors.OneHot(2), DateTime.UtcNow.AddHours(-3));
        var story = await Seed.ItemAsync(pg, 1, "Digest story", TestVectors.OneHot(4), DateTime.UtcNow.AddHours(-4));
        await Seed.ReadAsync(pg, story, "homelab", "learn");
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update reads set suggestion = 'Upgrade the box.' where item_id = @quokka", new { quokka });
            await c.ExecuteAsync("update reads set suggestion = 'Try it.' where item_id = @story", new { story });
            await c.ExecuteAsync("update feeds set category = 'tech', site_url = 'https://example.com', feed_url = 'https://example.com/feed.xml', fail_count = 2, last_error = 'HTTP 404' where id = 1");
            await c.ExecuteAsync("insert into digests (local_date, status, item_ids, sent_at) values (@today::date, 'sent', @ids, now())",
                new { today = App.Services.GetRequiredService<FeedEater.Digest.DigestTrigger>().Today(), ids = new[] { story } });
            await c.ExecuteAsync("insert into recall_cards (source, ref, front, back, due_at) values ('note', 'note:x', 'What did you note?', 'The answer.', now() - interval '1 hour')");
        }

        await App.Services.GetRequiredService<FeedEater.Swipe.CategoryStore>().ReplaceAllAsync(
            [new FeedEater.Swipe.CategoryRow { Key = "tech", Description = "Tech", Embedding = TestVectors.OneHot(1) }], DateTimeOffset.UtcNow, default);

        await SampleAsync("status-open", HttpMethod.Get, "/api/v1/status");
        await SampleAsync("next-closed-not-started", HttpMethod.Post, "/api/v1/swipe/next", new { position = 0, peek = true });

        // The digest card heads the queue; the rest is shuffled, so the other kinds are found by peeking.
        var digestCard = await SampleAsync("next-card-digest", HttpMethod.Post, "/api/v1/swipe/next", new { });
        var session = digestCard["card"]!["session"]!.GetValue<long>();
        var total = digestCard["card"]!["total"]!.GetValue<int>();
        Assert.Equal("digest", digestCard["card"]!["kind"]!.GetValue<string>());
        int? categorized = null, recall = null;
        for (var position = 1; position < total; position++)
        {
            var peeked = (await CallAsync(HttpMethod.Post, "/api/v1/swipe/next", new { position, peek = true })).Body["card"]!;
            if (peeked["kind"]!.GetValue<string>() == "recall")
            {
                recall = position;
            }
            else if (peeked["category"] is not null)
            {
                categorized = position;
            }
        }

        Assert.NotNull(recall);
        Assert.NotNull(categorized);
        await SampleAsync("next-card-recall", HttpMethod.Post, "/api/v1/swipe/next", new { position = recall, peek = true });
        await SampleAsync("next-card", HttpMethod.Post, "/api/v1/swipe/next", new { position = categorized });
        await SampleAsync("act", HttpMethod.Post, "/api/v1/swipe/act", new { session, position = categorized, act = "like", clientId = Guid.NewGuid() });
        await SampleAsync("next-summary", HttpMethod.Post, "/api/v1/swipe/next", new { position = 99, peek = true });

        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into votes (item_id, value) select @quokka, 1 where not exists (select 1 from votes where item_id = @quokka)", new { quokka });
        }

        await SampleAsync("item", HttpMethod.Get, $"/api/v1/items/{quokka}");
        await SampleAsync("ask", HttpMethod.Post, "/api/v1/ask", new { question = "quokka" });
        await SampleAsync("search", HttpMethod.Get, "/api/v1/search?q=backups");
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(HttpMethod.Post, "/api/v1/follows", new { itemId = quokka })).Status);
        await SampleAsync("follows", HttpMethod.Get, "/api/v1/follows");
        await SampleAsync("feeds", HttpMethod.Get, "/api/v1/feeds");
        await SampleAsync("settings", HttpMethod.Get, "/api/v1/settings");
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(HttpMethod.Post, "/api/v1/swipe/end", new { session })).Status);
        await SampleAsync("ok", HttpMethod.Post, "/api/v1/swipe/felt", new { session, felt = 1 });
        await SampleAsync("status", HttpMethod.Get, "/api/v1/status");
        await SampleAsync("next-closed", HttpMethod.Post, "/api/v1/swipe/next", new { });
        await SampleAsync("problem", HttpMethod.Get, "/api/v1/status", expected: HttpStatusCode.Unauthorized, authorization: "Bearer fe_unknown");
        MatchFixtures();
    }

    [Fact]
    public async Task Every_card_kind_and_write_response_matches_its_fixture()
    {
        var a = await Seed.ItemAsync(pg, 1, "Quokka backups", TestVectors.OneHot(1), DateTime.UtcNow.AddHours(-2), content: "Quokka backups run nightly.");
        var b = await Seed.ItemAsync(pg, 1, "Quokka habitat", TestVectors.OneHot(2), DateTime.UtcNow.AddHours(-3));
        var inbox = App.Services.GetRequiredService<FeedEater.Storage.InboxStore>();
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddDays(7);
        await inbox.AddAsync(new InboxEntry("notice:x", InboxKinds.Notice, Payload: new JsonObject { ["text"] = "Telegram is down." }, ExpiresAt: expires), now.AddMinutes(-5), default);
        await inbox.AddAsync(new InboxEntry("follow:x", InboxKinds.Follow, ItemId: a, Payload: new JsonObject { ["story"] = "Quokkas" }, ExpiresAt: expires), now.AddMinutes(-4), default);
        await inbox.AddAsync(new InboxEntry("duel:x", InboxKinds.Duel, Payload: new JsonObject { ["a"] = a, ["b"] = b }, ExpiresAt: expires), now.AddMinutes(-3), default);
        await inbox.AddAsync(new InboxEntry("idea:x", InboxKinds.Idea, Payload: new JsonObject
        {
            ["title"] = "Cache it",
            ["project"] = "LAB",
            ["url"] = "https://example.com/i",
            ["sourceTitle"] = "Quokka backups",
            ["why"] = "Faster",
            ["where"] = "src",
            ["effort"] = "S",
        }, ExpiresAt: expires), now.AddMinutes(-2), default);
        await inbox.AddAsync(new InboxEntry("release:x", InboxKinds.Release, Payload: new JsonObject
        {
            ["services"] = "grafana",
            ["running"] = "11.0",
            ["version"] = "12.0",
            ["summary"] = "A new major.",
            ["url"] = "https://example.com/r",
            ["breaking"] = true,
            ["urgent"] = true,
        }, ExpiresAt: expires), now.AddMinutes(-1), default);
        await App.Services.GetRequiredService<FeedEater.Swipe.RecallStore>().AddSeedAsync(a, "Why quokkas back up nightly", "tech", default);

        var first = (await CallAsync(HttpMethod.Post, "/api/v1/swipe/next", new { })).Body["card"]!;
        var session = first["session"]!.GetValue<long>();
        var positions = new Dictionary<string, int> { [first["kind"]!.GetValue<string>()] = 0 };
        for (var position = 1; position < first["total"]!.GetValue<int>(); position++)
        {
            positions.TryAdd((await CallAsync(HttpMethod.Post, "/api/v1/swipe/next", new { position, peek = true })).Body["card"]!["kind"]!.GetValue<string>(), position);
        }

        foreach (var kind in new[] { "notice", "follow", "duel", "idea", "release", "seed" })
        {
            Assert.True(positions.ContainsKey(kind), kind + " card not in the queue");
            await SampleAsync("next-card-" + kind, HttpMethod.Post, "/api/v1/swipe/next", new { position = positions[kind], peek = true });
        }

        var follow = positions["follow"];
        var clientId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(HttpMethod.Post, "/api/v1/swipe/event", new { session, position = follow, direction = "up" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(HttpMethod.Post, "/api/v1/swipe/act", new { session, position = follow, act = "like", clientId })).Status);
        await SampleAsync("undo", HttpMethod.Post, "/api/v1/swipe/undo", new { session, position = follow, clientId });
        await SampleAsync("item-ask", HttpMethod.Post, $"/api/v1/items/{a}/ask", new { question = "How often?" });
        await SampleAsync("start-follow", HttpMethod.Post, "/api/v1/follows", new { itemId = b });
        await SampleAsync("add-feed", HttpMethod.Post, "/api/v1/feeds", new { url = "https://good.example/feed" }, HttpStatusCode.Created);
        MatchFixtures();
    }

    private void MatchFixtures()
    {
        var update = Environment.GetEnvironmentVariable("UPDATE_CONTRACT") == "1";
        var dir = RepoRoot.File("tests/contract");
        if (update)
        {
            Directory.CreateDirectory(dir);
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var problems = new List<string>();
        foreach (var (file, body) in samples)
        {
            var path = Path.Combine(dir, file + ".json");
            if (update)
            {
                File.WriteAllText(path, Stable(body)!.ToJsonString(options) + "\n");
            }

            if (!File.Exists(path))
            {
                problems.Add($"{file}.json is missing; run: UPDATE_CONTRACT=1 dotnet test --filter ContractTests");
                continue;
            }

            Compare(file, JsonNode.Parse(File.ReadAllText(path)), body, problems);
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems) + Environment.NewLine + "If the change is intended: UPDATE_CONTRACT=1 dotnet test --filter ContractTests, then commit tests/contract.");
    }

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT")]
    private static partial Regex Instant();

    [GeneratedRegex(@"^\d{4}-\d\d-\d\d$")]
    private static partial Regex Day();

    [GeneratedRegex("^https://example.com/[0-9a-f]{32}$")]
    private static partial Regex GeneratedUrl();

    /// <summary>Replaces what changes between runs (trace ids, generated URLs, clock values) so regenerating a fixture only shows real changes.</summary>
    private static JsonNode? Stable(JsonNode? node, string? name = null)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (key, value) in o)
                {
                    copy[key] = Stable(value, key);
                }

                return copy;
            case JsonArray a:
                return new JsonArray(a.Select(item => Stable(item)).ToArray());
            case JsonValue v when v.TryGetValue<string>(out var text):
                return name switch
                {
                    "traceId" => "trace",
                    "age" => "2 h ago",
                    _ when Instant().IsMatch(text) => "2026-01-01T00:00:00+00:00",
                    _ when Day().IsMatch(text) => "2026-01-01",
                    _ when GeneratedUrl().IsMatch(text) => "https://example.com/item",
                    _ => text,
                };
            default:
                return node?.DeepClone();
        }
    }

    private static string Kind(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        _ => node.GetValueKind().ToString().ToLowerInvariant() switch { "true" or "false" => "boolean", var k => k },
    };

    /// <summary>A null in the live response is fine (the field is optional); a null or empty array in the fixture is not, because it would prove nothing.</summary>
    private static void Compare(string where, JsonNode? fixture, JsonNode? live, List<string> problems)
    {
        var (a, b) = (Kind(fixture), Kind(live));
        if (b == "null")
        {
            return;
        }

        if (a == "null")
        {
            problems.Add($"{where}: the fixture has null, the response has {b}; seed a value so the fixture shows the type");
            return;
        }

        if (a != b)
        {
            problems.Add($"{where}: fixture has {a}, response has {b}");
            return;
        }

        if (fixture is JsonObject fo && live is JsonObject lo)
        {
            foreach (var name in fo.Select(p => p.Key).Except(lo.Select(p => p.Key)))
            {
                problems.Add($"{where}.{name}: in the fixture, not in the response");
            }

            foreach (var name in lo.Select(p => p.Key).Except(fo.Select(p => p.Key)))
            {
                problems.Add($"{where}.{name}: in the response, not in the fixture");
            }

            foreach (var (name, value) in fo.Where(p => lo.ContainsKey(p.Key)))
            {
                Compare($"{where}.{name}", value, lo[name], problems);
            }
        }
        else if (fixture is JsonArray fa && live is JsonArray la)
        {
            if (fa.Count == 0 && la.Count > 0)
            {
                problems.Add($"{where}: the fixture has an empty array, the response has items; seed one so the fixture shows its shape");
            }
            else if (fa.Count > 0 && la.Count > 0)
            {
                Compare(where + "[]", fa[0], la[0], problems);
            }
        }
    }
}
