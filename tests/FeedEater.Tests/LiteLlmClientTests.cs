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
        public List<(string Purpose, string Model, int In, int Out, decimal? Cost)> Rows { get; } = [];

        public Task AddAsync(string purpose, string model, int inputTokens, int outputTokens, decimal? cost, CancellationToken ct)
        {
            Rows.Add((purpose, model, inputTokens, outputTokens, cost));
            return Task.CompletedTask;
        }
    }

    private static LiteLlmClient Client(StubHandler handler, Sink sink, LlmOptions? llm = null) =>
        new(handler.Client("http://llm/v1/"), sink, Options.Create(new FeedEaterOptions { Llm = llm ?? new LlmOptions() }));

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
        Assert.Equal(("triage", "gpt-4.1-nano", 100, 7, (decimal?)null), sink.Rows.Single());   // no header, no price: unknown, not free
        Assert.Contains("\"max_tokens\":200", handler.Calls.Single().Body, StringComparison.Ordinal);
    }

    private static StubHandler ChatReply(string? costHeader = null) => new((_, _) =>
    {
        var r = StubHandler.Json("""{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":2000000,"completion_tokens":500000}}""");
        if (costHeader is not null)
        {
            r.Headers.Add("x-litellm-response-cost", costHeader);
        }

        return r;
    });

    private static LlmOptions Priced() => new() { Prices = { ["gpt-4.1-nano"] = new ModelPrice { Input = 0.1m, Output = 0.4m } } };

    [Fact]
    public async Task A_configured_price_estimates_the_cost_when_the_gateway_sends_no_header()
    {
        var sink = new Sink();

        var result = await Client(ChatReply(), sink, Priced()).ChatAsync("gpt-4.1-nano", "s", "u", 10, "triage", default);

        Assert.Equal(0.4m, result.Cost);   // 2 M tokens in at 0.10 plus 0.5 M out at 0.40
        Assert.Equal(0.4m, sink.Rows.Single().Cost);
    }

    [Fact]
    public async Task The_gateways_cost_header_wins_over_a_configured_price()
    {
        var result = await Client(ChatReply("0.0007"), new Sink(), Priced()).ChatAsync("gpt-4.1-nano", "s", "u", 10, "triage", default);

        Assert.Equal(0.0007m, result.Cost);
    }

    [Fact]
    public async Task A_model_without_a_price_or_header_has_an_unknown_cost_and_a_zero_price_is_free()
    {
        var sink = new Sink();

        Assert.Null((await Client(ChatReply(), sink, Priced()).ChatAsync("other-model", "s", "u", 10, "triage", default)).Cost);
        var free = new LlmOptions { Prices = { ["local"] = new ModelPrice() } };
        Assert.Equal(0m, (await Client(ChatReply(), sink, free).ChatAsync("local", "s", "u", 10, "triage", default)).Cost);
    }

    [Fact]
    public async Task Embeddings_are_priced_on_input_tokens_only()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(
            """{"data":[{"index":0,"embedding":[1,0]}],"usage":{"prompt_tokens":1000000}}"""));
        var sink = new Sink();
        var llm = new LlmOptions { Prices = { ["text-embedding-3-small"] = new ModelPrice { Input = 0.02m } } };

        await Client(handler, sink, llm).EmbedAsync(["a"], "embed", default);

        Assert.Equal(0.02m, sink.Rows.Single().Cost);
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
