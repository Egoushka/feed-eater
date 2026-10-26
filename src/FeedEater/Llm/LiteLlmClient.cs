using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Ranking;

namespace FeedEater.Llm;

/// <summary>
/// <c>Cost</c> is null when the gateway sent no cost header and <c>Llm:Prices</c> has no entry for the model: unknown, not free.
/// <c>RateLimited</c> is true when the call had to wait out a 429 token-limit response before it got this answer.
/// </summary>
public sealed record ChatResult(string Content, int InputTokens, int OutputTokens, decimal? Cost, bool RateLimited = false);

public sealed class BudgetExceededException(string message) : Exception(message);

public interface IUsageSink
{
    Task AddAsync(string purpose, string model, int inputTokens, int outputTokens, decimal? cost, CancellationToken ct);
}

/// <summary>OpenAI-compatible calls (LiteLLM, OpenAI, others). Every call's tokens and cost go to the usage sink.</summary>
public sealed class LiteLlmClient(HttpClient http, IUsageSink usage, IOptions<FeedEaterOptions> options, ILogger<LiteLlmClient>? logger = null)
{
    private const int Retries = 2;

    private static readonly string[] BudgetMarkers = ["ExceededTokenBudget", "ExceededBudget", "Budget has been exceeded", "budget_exceeded"];

    private static readonly Regex TokenCounts = new(@"(?<used>\d+)\s*/\s*(?<max>\d+)", RegexOptions.Compiled);

    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest this client waits for one token-limit reset before trying again anyway.</summary>
    internal static TimeSpan MaxRateLimitWait { get; set; } = TimeSpan.FromMinutes(65);

    private readonly ILogger<LiteLlmClient> log = logger ?? NullLogger<LiteLlmClient>.Instance;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, string purpose, CancellationToken ct)
    {
        var model = options.Value.Llm.EmbedModel;
        var payload = new Dictionary<string, object> { ["model"] = model, ["input"] = inputs };
        if (options.Value.Llm.EmbedDimensions > 0)
        {
            payload["dimensions"] = options.Value.Llm.EmbedDimensions;
        }

        var (response, _) = await PostAsync("embeddings", payload, ct);
        using var r = response;
        var body = Json.Parse(await r.Content.ReadAsStringAsync(ct));
        var vectors = body.GetProperty("data").EnumerateArray()
            .OrderBy(d => d.GetProperty("index").GetInt32())
            .Select(d => Vectors.Normalize(d.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray()))
            .ToList();
        var tokens = Tokens(body, "prompt_tokens");
        await usage.AddAsync(purpose, model, tokens, 0, Cost(r, model, tokens, 0), ct);
        if (vectors.Count != inputs.Count)
        {
            throw new InvalidOperationException($"asked for {inputs.Count} embeddings, got {vectors.Count}");
        }

        return vectors;
    }

    public async Task<ChatResult> ChatAsync(string model, string system, string user, int maxTokens, string purpose, CancellationToken ct)
    {
        var (response, rateLimited) = await PostAsync("chat/completions", new
        {
            model,
            max_tokens = maxTokens,
            temperature = 0.2,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
        }, ct);
        using var r = response;
        var body = Json.Parse(await r.Content.ReadAsStringAsync(ct));
        var content = body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var (input, output) = (Tokens(body, "prompt_tokens"), Tokens(body, "completion_tokens"));
        var result = new ChatResult(content, input, output, Cost(r, model, input, output), rateLimited);
        await usage.AddAsync(purpose, model, result.InputTokens, result.OutputTokens, result.Cost, ct);
        return result;
    }

    /// <summary>Posts, retrying transport errors and 5xx, and waiting out a token-limit 429 instead of failing. <c>RateLimited</c> is true when such a wait happened.</summary>
    private async Task<(HttpResponseMessage Response, bool RateLimited)> PostAsync(string path, object payload, CancellationToken ct)
    {
        var rateLimited = false;
        for (int attempt = 0, rateLimitAttempt = 0; ; attempt++)
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
                return (response, rateLimited);
            }

            var status = response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);
            if (status == HttpStatusCode.TooManyRequests && IsTokenLimit(response, text))
            {
                rateLimitAttempt++;
                var wait = RateLimitWait(response, rateLimitAttempt);
                LogTokenLimit(text, wait);
                response.Dispose();
                rateLimited = true;
                await Task.Delay(wait, ct);
                continue;
            }

            response.Dispose();
            if (BudgetMarkers.Any(m => text.Contains(m, StringComparison.Ordinal)))
            {
                throw new BudgetExceededException("the LiteLLM budget for this key is spent");
            }

            throw new HttpRequestException($"LiteLLM {(int)status}: {text[..Math.Min(300, text.Length)]}", null, status);
        }
    }

    /// <summary>A 429 is a token-limit wait, not a hard failure, when the gateway said so in words or gave a Retry-After.</summary>
    private static bool IsTokenLimit(HttpResponseMessage response, string body) =>
        response.Headers.RetryAfter is not null || body.Contains("token limit exceeded", StringComparison.OrdinalIgnoreCase);

    /// <summary>Retry-After (seconds or an HTTP date) if the gateway sent one, else a growing backoff; always capped at <see cref="MaxRateLimitWait"/>.</summary>
    private static TimeSpan RateLimitWait(HttpResponseMessage response, int rateLimitAttempt)
    {
        var wait = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => TimeSpan.FromMinutes(1) * Math.Pow(2, rateLimitAttempt - 1),
        };
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRateLimitWait ? MaxRateLimitWait : wait;
    }

    private void LogTokenLimit(string body, TimeSpan wait)
    {
        var m = TokenCounts.Match(body);
        if (m.Success)
        {
            log.LogWarning("LiteLLM token limit hit ({Used}/{Max} tokens); waiting {Wait} for it to reset", m.Groups["used"].Value, m.Groups["max"].Value, wait);
        }
        else
        {
            log.LogWarning("LiteLLM rate limited; waiting {Wait} for it to reset. {Body}", wait, body[..Math.Min(300, body.Length)]);
        }
    }

    private static int Tokens(JsonElement body, string name) =>
        body.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;

    /// <summary>The gateway's own figure when it sends one, else the configured price, else unknown.</summary>
    private decimal? Cost(HttpResponseMessage response, string model, int inputTokens, int outputTokens) =>
        response.Headers.TryGetValues("x-litellm-response-cost", out var values)
        && decimal.TryParse(values.First(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cost)
            ? cost
            : options.Value.Llm.Estimate(model, inputTokens, outputTokens);
}
