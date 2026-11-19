using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FeedEater.Trials;

/// <summary>Runs one trial somewhere that can edit code and run tests; a seam so tests need no server.</summary>
public interface ITrialExecutor
{
    /// <summary>Returns the outcome of a run that finished. Throws for a run that could not be had: an <see cref="HttpRequestException"/> with 401 or 403 means access was denied.</summary>
    Task<TrialOutcome> RunAsync(TrialRequest request, CancellationToken ct);
}

/// <summary>
/// Hands a trial to chargehand's HTTP API (<c>POST /v1/runs</c>, the same call its <c>orchestrate</c> MCP tool makes) with the <c>code</c> preset and
/// <c>delivery: local</c>: the worker commits on a branch of its own clone, the branch is only fetched into the local checkout, and chargehand does not push or open a pull request.
/// </summary>
public sealed class ChargehandTrialExecutor(HttpClient http, IOptions<FeedEaterOptions> options) : ITrialExecutor
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    public async Task<TrialOutcome> RunAsync(TrialRequest request, CancellationToken ct)
    {
        var settings = options.Value.Trials;
        if (settings.ChargehandUrl.Length == 0)
        {
            throw new InvalidOperationException("Trials:ChargehandUrl is not set.");
        }

        var baseUrl = new Uri(settings.ChargehandUrl.TrimEnd('/') + "/");
        using var start = Signed(new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "v1/runs")) { Content = new StringContent(Body(request, settings), Encoding.UTF8, "application/json") }, settings);
        start.Headers.TryAddWithoutValidation("Prefer", "wait=60");
        var response = await http.SendAsync(start, ct);
        var location = response.Headers.Location?.ToString();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(settings.TimeoutMinutes);
        try
        {
            while (response.StatusCode == HttpStatusCode.Accepted)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    await CancelAsync(baseUrl, location, settings);
                    throw new TimeoutException($"the trial did not finish in {settings.TimeoutMinutes} minutes.");
                }

                await Task.Delay(Poll, ct);
                response.Dispose();
                using var next = Signed(new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, (location ?? throw new HttpRequestException("chargehand gave no Location for the run.")).TrimStart('/'))), settings);
                response = await http.SendAsync(next, ct);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                throw new HttpRequestException($"chargehand refused the trial ({(int)response.StatusCode}): {TrialRules.Redact(Short(body), settings.ChargehandKey)}", null, response.StatusCode);
            }

            if (Interpret(body, settings.ChargehandKey) is { } outcome)
            {
                return outcome;
            }

            throw new HttpRequestException($"chargehand answered {(int)response.StatusCode}: {TrialRules.Redact(Short(body), settings.ChargehandKey)}", null, response.StatusCode);
        }
        finally
        {
            response.Dispose();
        }
    }

    internal static string Body(TrialRequest request, TrialsOptions settings) =>
        JsonSerializer.Serialize(new
        {
            contract_version = "request/v1",
            text = request.Brief,
            context = new { interactive = false, preset = settings.Preset, budget_usd = settings.BudgetUsd, delivery = "local", repository = new { path = request.RepoPath } },
        });

    /// <summary>Reads a <c>result/v1</c> document. Null when the text is not one.</summary>
    internal static TrialOutcome? Interpret(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "status") is not { } status)
            {
                return null;
            }

            var summary = TrialRules.Redact(Text(root, "summary"), key);
            var errorCode = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? Text(error, "code") : null;
            var errorMessage = TrialRules.Redact(error.ValueKind == JsonValueKind.Object ? Text(error, "message") : null, key);
            var benchmark = TrialRules.Redact(TrialRules.Benchmark(Text(root, "summary") ?? ""), key);
            var (tests, exit, diff) = Artifacts(root, key);
            var diffStat = TrialRules.DiffStat(diff);

            switch (status)
            {
                case "completed" when exit == 0:
                    return new TrialOutcome(TrialStatus.Passed, tests, benchmark, diffStat, summary, "");
                case "completed":
                    return new TrialOutcome(TrialStatus.Failed, tests, benchmark, diffStat, summary, exit is null ? "the run reported no test result." : "the tests did not pass.");
                case "denied":
                    return new TrialOutcome(TrialStatus.Blocked, tests, benchmark, diffStat, summary, errorMessage.Length > 0 ? errorMessage : "chargehand denied the request.");
                case "failed" when TrialRules.IsDenialCode(errorCode):
                    return new TrialOutcome(TrialStatus.Blocked, tests, benchmark, diffStat, summary, $"{errorCode}: {errorMessage}");
                case "failed":
                    return new TrialOutcome(TrialStatus.Failed, tests, benchmark, diffStat, summary, $"{errorCode}: {errorMessage}".Trim(' ', ':'));
                default:
                    return new TrialOutcome(TrialStatus.Failed, tests, benchmark, diffStat, summary, "chargehand asked a question; a trial runs without one.");
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Tests, int? Exit, string Diff) Artifacts(JsonElement root, string key)
    {
        string tests = "", diff = "";
        int? exit = null;
        if (!root.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
        {
            return (tests, exit, diff);
        }

        foreach (var a in artifacts.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
        {
            var content = Text(a, "content") ?? "";
            switch (Text(a, "kind"))
            {
                case "verification":
                    try
                    {
                        using var v = JsonDocument.Parse(content);
                        exit = v.RootElement.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : null;
                        var command = v.RootElement.TryGetProperty("command", out var c) ? (c.ValueKind == JsonValueKind.Array ? string.Join(' ', c.EnumerateArray().Select(x => x.ToString())) : c.ToString()) : "";
                        var tail = Text(v.RootElement, "output_tail") ?? "";
                        tests = TrialRules.Redact($"exit {(exit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?")}: {command}\n{tail}".Trim(), key);
                    }
                    catch (JsonException)
                    {
                        tests = TrialRules.Redact(content, key);
                    }

                    break;
                case "diff":
                    diff = content;
                    break;
            }
        }

        return (tests, exit, diff);
    }

    private async Task CancelAsync(Uri baseUrl, string? location, TrialsOptions settings)
    {
        if (location is null)
        {
            return;
        }

        try
        {
            using var cancel = Signed(new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, location.TrimStart('/') + "/cancel")), settings);
            (await http.SendAsync(cancel, CancellationToken.None)).Dispose();
        }
        catch (HttpRequestException)
        {
            // Best effort: the run still ends on chargehand's own deadline.
        }
    }

    private static HttpRequestMessage Signed(HttpRequestMessage request, TrialsOptions settings)
    {
        if (settings.ChargehandKey.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ChargehandKey);
        }

        return request;
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Short(string body) => body.Length <= 300 ? body : body[..300];
}
