using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using FeedEater.Llm;

namespace FeedEater.Setup;

public enum CheckStatus
{
    Ok,
    /// <summary>An optional integration that is not configured.</summary>
    Off,
    /// <summary>Works, but the reader should look: not a failure.</summary>
    Warn,
    Fail,
}

/// <summary>What one check found. <c>Fix</c> says what to change; it is set for <see cref="CheckStatus.Warn"/> and <see cref="CheckStatus.Fail"/>.</summary>
public sealed record CheckResult(CheckStatus Status, string Detail, string? Fix = null)
{
    public static CheckResult Ok(string detail) => new(CheckStatus.Ok, detail);
    public static CheckResult Off(string detail) => new(CheckStatus.Off, detail);
    public static CheckResult Warn(string detail, string fix) => new(CheckStatus.Warn, detail, fix);
    public static CheckResult Fail(string detail, string fix) => new(CheckStatus.Fail, detail, fix);
}

public sealed record CheckRow(string Name, bool Required, CheckResult Result);

/// <summary>
/// One line of <c>doctor</c> and of <c>/ui/setup</c>. An implementation reads or calls one thing and says what it found; it must not
/// change anything. A <see cref="Required"/> check that fails makes <c>doctor</c> exit 1.
/// </summary>
public interface IIntegrationCheck
{
    string Name { get; }
    bool Required { get; }
    Task<CheckResult> RunAsync(CancellationToken ct);
}

/// <summary>
/// Runs every registered check at once, each with its own time limit, and never throws. Whatever a check or an exception says is
/// cut to one line and stripped of every configured secret before it reaches the caller.
/// </summary>
public sealed partial class CheckRunner(
    IEnumerable<IIntegrationCheck> checks, IOptions<FeedEaterOptions> options, IConfiguration configuration, TimeSpan? timeout = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const int MaxText = 300;
    private const int MinSecretLength = 4;   // shorter values would mangle ordinary words when masked

    public async Task<IReadOnlyList<CheckRow>> RunAsync(CancellationToken ct)
    {
        var secrets = Secrets();
        return await Task.WhenAll(checks.Select(c => RunOneAsync(c, secrets, ct)));
    }

    private async Task<CheckRow> RunOneAsync(IIntegrationCheck check, IReadOnlyList<string> secrets, CancellationToken ct)
    {
        var limit = timeout ?? Timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        CheckResult result;
        try
        {
            result = await check.RunAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = CheckResult.Fail($"no answer within {limit.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s", "The service is slow or unreachable from this container: check its address and that it is up.");
        }
        catch (Exception ex)
        {
            result = CheckResult.Fail(Hints.Describe(ex), "See the message; check the setting it names.");
        }

        return new CheckRow(check.Name, check.Required, result with { Detail = Clean(result.Detail, secrets), Fix = result.Fix is null ? null : Clean(result.Fix, secrets) });
    }

    private static string Clean(string text, IReadOnlyList<string> secrets)
    {
        text = Whitespace().Replace(text, " ").Trim();
        foreach (var secret in secrets)
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return text.Length <= MaxText ? text : text[..(MaxText - 1)] + "…";
    }

    /// <summary>Every configured secret value, longest first so a secret that contains another is masked whole.</summary>
    private List<string> Secrets()
    {
        var values = ConfigPrinter.Entries(options.Value, FeedEaterOptions.Section, schema: false)
            .Where(e => e.Property.IsDefined(typeof(SecretAttribute)))
            .Select(e => e.Value as string)
            .Append(configuration["Mcp:Token"])
            .Append(Password(configuration.GetConnectionString("FeedEater")));
        return values.OfType<string>().Where(v => v.Length >= MinSecretLength).Distinct().OrderByDescending(v => v.Length).ToList();
    }

    private static string? Password(string? connection)
    {
        try
        {
            return string.IsNullOrEmpty(connection) ? null : new NpgsqlConnectionStringBuilder(connection).Password;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\s+", RegexOptions.None, 250)]
    private static partial Regex Whitespace();
}

/// <summary>Plain-words reasons and fixes shared by the checks. Settings are named the way they are set in the environment.</summary>
internal static partial class Hints
{
    public static string Env(string key) => "FeedEater__" + key.Replace(":", "__", StringComparison.Ordinal);

    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(not a valid URL)";

    /// <summary>What went wrong, short: an API's own <c>"message"</c> instead of its whole error body.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        BudgetExceededException => "the gateway budget for this key is spent",
        TaskCanceledException { InnerException: TimeoutException } => "the request timed out",
        JsonException or KeyNotFoundException => "the answer is not what this API sends",
        HttpRequestException { StatusCode: { } code } when ProviderMessage().Match(ex.Message) is { Success: true } m => $"HTTP {(int)code}: {m.Groups[1].Value}",
        _ => ex.Message,
    };

    [GeneratedRegex("\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.None, 250)]
    private static partial Regex ProviderMessage();

    /// <summary>The fix for a failed call to a service set by <paramref name="urlKey"/> and, when it has one, <paramref name="tokenKey"/>.</summary>
    public static string Http(Exception ex, string urlKey, string? tokenKey)
    {
        var token = tokenKey is null ? "" : $" and {Env(tokenKey)}";
        return ex switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
                $"The key was refused: check {Env(tokenKey ?? urlKey)}.",
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                $"The server answers but not at this path: check {Env(urlKey)} (include the version path, such as /v1/, where the service has one).",
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "Rate limit or no credit on the key: check the provider's billing page, then retry.",
            HttpRequestException { StatusCode: null } or TaskCanceledException =>
                $"Cannot reach it from this container: check {Env(urlKey)} and that the service is up.",
            JsonException or KeyNotFoundException =>
                $"The address answers, but not as this API: {Env(urlKey)} must point at the API itself.",
            UriFormatException => $"{Env(urlKey)} is not a valid absolute URL (it needs http:// or https://).",
            _ => $"Check {Env(urlKey)}{token}.",
        };
    }
}

/// <summary>An optional service: <see cref="OffReason"/> says when it is not configured, <see cref="ProbeAsync"/> makes one cheap read.</summary>
internal abstract class ServiceCheck(IServiceProvider services, IOptions<FeedEaterOptions> options) : IIntegrationCheck
{
    public abstract string Name { get; }
    public virtual bool Required => false;

    protected FeedEaterOptions Settings => options.Value;
    protected IServiceProvider Services => services;

    /// <summary>Non-null when the integration is not configured.</summary>
    protected abstract string? OffReason { get; }

    /// <summary>The detail of a working service. Clients are taken from <see cref="Services"/> here, so a bad URL fails this check and nothing else.</summary>
    protected abstract Task<string> ProbeAsync(CancellationToken ct);

    protected abstract string Fix(Exception ex);

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        if (OffReason is { } reason)
        {
            return CheckResult.Off(reason);
        }

        try
        {
            return CheckResult.Ok(await ProbeAsync(ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return CheckResult.Fail(Hints.Describe(ex), Fix(ex));
        }
    }
}
