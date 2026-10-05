using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Ranking;

namespace FeedEater.Llm;

public sealed record ChatResult(string Content, int InputTokens, int OutputTokens, decimal Cost);

public sealed class BudgetExceededException(string message) : Exception(message);

public interface IUsageSink
{
    Task AddAsync(string purpose, string model, int inputTokens, int outputTokens, decimal cost, CancellationToken ct);
}

/// <summary>OpenAI-compatible calls through LiteLLM. Every call's tokens and cost go to the usage sink.</summary>
public sealed class LiteLlmClient(HttpClient http, IUsageSink usage, IOptions<FeedEaterOptions> options)
{
    private const int Retries = 2;

    private static readonly string[] BudgetMarkers = ["ExceededTokenBudget", "ExceededBudget", "Budget has been exceeded", "budget_exceeded"];

    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, string purpose, CancellationToken ct)
    {
        var model = options.Value.Llm.EmbedModel;
        using var response = await PostAsync("v1/embeddings", new { model, input = inputs }, ct);
        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        var vectors = body.GetProperty("data").EnumerateArray()
            .OrderBy(d => d.GetProperty("index").GetInt32())
            .Select(d => Vectors.Normalize(d.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray()))
            .ToList();
        await usage.AddAsync(purpose, model, Tokens(body, "prompt_tokens"), 0, Cost(response), ct);
        if (vectors.Count != inputs.Count)
        {
            throw new InvalidOperationException($"asked for {inputs.Count} embeddings, got {vectors.Count}");
        }

        return vectors;
    }

    public async Task<ChatResult> ChatAsync(string model, string system, string user, int maxTokens, string purpose, CancellationToken ct)
    {
        using var response = await PostAsync("v1/chat/completions", new
        {
            model,
            max_tokens = maxTokens,
            temperature = 0.2,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
        }, ct);
        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        var content = body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var result = new ChatResult(content, Tokens(body, "prompt_tokens"), Tokens(body, "completion_tokens"), Cost(response));
        await usage.AddAsync(purpose, model, result.InputTokens, result.OutputTokens, result.Cost, ct);
        return result;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object payload, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.PostAsJsonAsync(path, payload, ct);
            }
            catch (HttpRequestException) when (attempt < Retries)
            {
                await Task.Delay(RetryDelay * (attempt + 1), ct);
                continue;
            }

            if ((int)response.StatusCode >= 500 && attempt < Retries)
            {
                response.Dispose();
                await Task.Delay(RetryDelay * (attempt + 1), ct);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (BudgetMarkers.Any(m => text.Contains(m, StringComparison.Ordinal)))
            {
                throw new BudgetExceededException("the LiteLLM budget for this key is spent");
            }

            throw new HttpRequestException($"LiteLLM {(int)status}: {text[..Math.Min(300, text.Length)]}", null, status);
        }
    }

    private static int Tokens(JsonElement body, string name) =>
        body.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;

    private static decimal Cost(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-litellm-response-cost", out var values)
        && decimal.TryParse(values.First(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cost) ? cost : 0m;
}
