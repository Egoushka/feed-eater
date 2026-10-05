using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Llm;

namespace FeedEater.Tests;

public sealed class LiteLlmClientTests
{
    public LiteLlmClientTests() => LiteLlmClient.RetryDelay = TimeSpan.Zero;

    private sealed class Sink : IUsageSink
    {
        public List<(string Purpose, string Model, int In, int Out, decimal Cost)> Rows { get; } = [];

        public Task AddAsync(string purpose, string model, int inputTokens, int outputTokens, decimal cost, CancellationToken ct)
        {
            Rows.Add((purpose, model, inputTokens, outputTokens, cost));
            return Task.CompletedTask;
        }
    }

    private static LiteLlmClient Client(StubHandler handler, Sink sink) =>
        new(handler.Client("http://llm/"), sink, Options.Create(new FeedEaterOptions()));

    [Fact]
    public async Task Embed_returns_unit_vectors_in_input_order_and_records_cost()
    {
        var v0 = TestVectors.OneHot(0).Select(x => x * 2).ToArray();
        var v1 = TestVectors.OneHot(1).Select(x => x * 3).ToArray();
        var json = JsonSerializer.Serialize(new
        {
            data = new[] { new { index = 1, embedding = v1 }, new { index = 0, embedding = v0 } },
            usage = new { prompt_tokens = 20 },
        });
        var handler = new StubHandler((_, _) =>
        {
            var r = StubHandler.Json(json);
            r.Headers.Add("x-litellm-response-cost", "0.0001");
            return r;
        });
        var sink = new Sink();

        var vectors = await Client(handler, sink).EmbedAsync(["a", "b"], "embed", default);

        Assert.Equal(1f, vectors[0][0]);
        Assert.Equal(1f, vectors[1][1]);
        Assert.Equal(("embed", "text-embedding-3-small", 20, 0, 0.0001m), sink.Rows.Single());
        Assert.Equal("http://llm/v1/embeddings", handler.Calls.Single().Uri);
    }

    [Fact]
    public async Task Cost_header_in_scientific_notation_is_parsed()
    {
        var handler = new StubHandler((_, _) =>
        {
            var r = StubHandler.Json(TestVectors.EmbeddingResponse(1));
            r.Headers.Add("x-litellm-response-cost", "2e-08");
            return r;
        });
        var sink = new Sink();

        await Client(handler, sink).EmbedAsync(["a"], "embed", default);

        Assert.Equal(0.00000002m, sink.Rows.Single().Cost);
    }

    [Fact]
    public async Task Chat_returns_content_and_tokens()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(
            """{"choices":[{"message":{"content":"{\"relevance\":2}"}}],"usage":{"prompt_tokens":100,"completion_tokens":7}}"""));
        var sink = new Sink();

        var result = await Client(handler, sink).ChatAsync("gpt-4.1-nano", "sys", "user", 200, "triage", default);

        Assert.Equal("{\"relevance\":2}", result.Content);
        Assert.Equal(("triage", "gpt-4.1-nano", 100, 7, 0m), sink.Rows.Single());
        Assert.Contains("\"max_tokens\":200", handler.Calls.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_spent_budget_becomes_BudgetExceededException()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(
            """{"error":{"message":"Authentication Error, ExceededTokenBudget: Current spend for token: 10.01; Max Budget for Token: 10.0"}}""",
            HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<BudgetExceededException>(() => Client(handler, new Sink()).ChatAsync("m", "s", "u", 10, "read", default));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_key_budget_error_with_status_400_becomes_BudgetExceededException()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(
            """{"error":{"message":"Budget has been exceeded! Key=abc Current cost: 10.01, Max budget: 10.0","type":"budget_exceeded","param":null,"code":"400"}}""",
            HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<BudgetExceededException>(() => Client(handler, new Sink()).ChatAsync("m", "s", "u", 10, "read", default));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Server_errors_are_retried_twice()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) => ++calls < 3
            ? StubHandler.Json("{}", HttpStatusCode.BadGateway)
            : StubHandler.Json("""{"choices":[{"message":{"content":"ok"}}]}"""));

        var result = await Client(handler, new Sink()).ChatAsync("m", "s", "u", 10, "read", default);

        Assert.Equal("ok", result.Content);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task Gives_up_after_the_second_retry()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("{}", HttpStatusCode.BadGateway));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler, new Sink()).ChatAsync("m", "s", "u", 10, "read", default));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal(3, handler.Calls.Count);
    }
}
