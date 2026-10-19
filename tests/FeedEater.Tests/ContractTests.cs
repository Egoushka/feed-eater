using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FeedEater.Tests;

/// <summary>
/// tests/contract/*.json are real /api/v1 responses on seeded data; the Android app's tests parse the same files. This test compares
/// each live response with its fixture by shape: property names and JSON value kinds, not values. A null on either side matches any
/// kind (an optional field is null in one sample and set in another). Regenerate with: UPDATE_CONTRACT=1 dotnet test --filter ContractTests
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContractTests(PostgresFixture pg) : IAsyncLifetime
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
    });

    private async Task<(JsonNode Body, HttpStatusCode Status)> CallAsync(HttpMethod method, string path, object? body = null, string? authorization = null)
    {
        authorization ??= "Bearer " + await ApiHost.TokenAsync(App);
        var response = await ApiHost.RawAsync(App.CreateClient(), method, path, authorization, body: body is null ? null : JsonSerializer.Serialize(body));
        return (JsonNode.Parse(await response.Content.ReadAsStringAsync())!, response.StatusCode);
    }

    [Fact]
    public async Task Every_response_type_matches_its_fixture()
    {
        var quokka = await Seed.ItemAsync(pg, 1, "Quokka backups", TestVectors.OneHot(1), DateTime.UtcNow.AddHours(-2), content: "Quokka backups run nightly.");
        await Seed.ReadAsync(pg, quokka, "homelab", "improve");
        await Seed.ItemAsync(pg, 1, "Quokka habitat", TestVectors.OneHot(2), DateTime.UtcNow.AddHours(-3));
        await Seed.ItemAsync(pg, 1, "Other news", TestVectors.OneHot(3), DateTime.UtcNow.AddHours(-4));

        var samples = new List<(string File, JsonNode Body)>();
        async Task<JsonNode> SampleAsync(string file, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK, string? authorization = null)
        {
            var (json, status) = await CallAsync(method, path, body, authorization);
            Assert.Equal(expected, status);
            samples.Add((file, json));
            return json;
        }

        // Order matters: the closed answer needs no session yet; the summary comes after an act so its counts are filled.
        await SampleAsync("next-closed", HttpMethod.Post, "/api/v1/swipe/next", new { position = 0, peek = true });
        var card = await SampleAsync("next-card", HttpMethod.Post, "/api/v1/swipe/next", new { });
        var session = card["card"]!["session"]!.GetValue<long>();
        var position = card["card"]!["position"]!.GetValue<int>();
        await SampleAsync("act", HttpMethod.Post, "/api/v1/swipe/act", new { session, position, act = "like", clientId = Guid.NewGuid() });
        await SampleAsync("next-summary", HttpMethod.Post, "/api/v1/swipe/next", new { position = 99, peek = true });
        await SampleAsync("status", HttpMethod.Get, "/api/v1/status");
        await SampleAsync("item", HttpMethod.Get, $"/api/v1/items/{quokka}");
        await SampleAsync("ask", HttpMethod.Post, "/api/v1/ask", new { question = "quokka" });
        await SampleAsync("search", HttpMethod.Get, "/api/v1/search?q=quokka");
        await CallAsync(HttpMethod.Post, "/api/v1/follows", new { itemId = quokka });
        await SampleAsync("follows", HttpMethod.Get, "/api/v1/follows");
        await SampleAsync("feeds", HttpMethod.Get, "/api/v1/feeds");
        await SampleAsync("settings", HttpMethod.Get, "/api/v1/settings");
        await SampleAsync("problem", HttpMethod.Get, "/api/v1/status", expected: HttpStatusCode.Unauthorized, authorization: "Bearer fe_unknown");

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
                File.WriteAllText(path, body.ToJsonString(options) + "\n");
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

    private static string Kind(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        _ => node.GetValueKind().ToString().ToLowerInvariant() switch { "true" or "false" => "boolean", var k => k },
    };

    private static void Compare(string where, JsonNode? fixture, JsonNode? live, List<string> problems)
    {
        var (a, b) = (Kind(fixture), Kind(live));
        if (a == "null" || b == "null")
        {
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
        else if (fixture is JsonArray fa && live is JsonArray la && fa.Count > 0 && la.Count > 0)
        {
            Compare(where + "[]", fa[0], la[0], problems);
        }
    }
}
