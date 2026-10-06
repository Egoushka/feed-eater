using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Fetch;

public enum FetchOutcome { Ok, Refused, Blocked, BadType, TooManyRedirects, Failed, CapReached, Cached }

public sealed record FetchResult(FetchOutcome Outcome, string? Body = null, Uri? FinalUri = null, string? Detail = null)
{
    public bool Ok => Outcome == FetchOutcome.Ok;
}

/// <summary>The only code that opens a connection to a host named by a feed. The rules are in docs/specs/2026-10-06-page-fetch.md.</summary>
public sealed class SafeFetcher(HttpClient http, IOptions<FeedEaterOptions> options, CursorStore cursors, TimeProvider time, ILogger<SafeFetcher> logger)
{
    public const string ClientName = "safe-fetch";
    public const string UserAgent = "feed-eater/0.5.0 (+https://github.com/Egoushka/feed-eater)";
    private const int MaxRedirects = 3;
    private const int MaxBytes = 1024 * 1024;
    internal static int PruneAbove { get; set; } = 256;
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureMemory = TimeSpan.FromHours(24);
    private static readonly TimeSpan HostGap = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextAt = new(StringComparer.OrdinalIgnoreCase);
    internal int TrackedHosts => _failedUntil.Count + _nextAt.Count;

    private readonly SemaphoreSlim _counter = new(1, 1);

    /// <summary>The handler for the typed client: pinned connect, no redirects, no cookies, no proxy.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = GuardedConnector.Default.ConnectAsync,
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken ct)
    {
        var cfg = options.Value.Fetch;
        if (Check(uri, cfg) is { } refused)
        {
            return refused;
        }

        Prune();
        if (_failedUntil.TryGetValue(uri.Host, out var until) && until > time.GetUtcNow())
        {
            return new FetchResult(FetchOutcome.Cached, Detail: "failed recently");
        }

        if (!await CountAsync(cfg.MaxPerDay, ct))
        {
            return new FetchResult(FetchOutcome.CapReached);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            return await FollowAsync(uri, cfg, ct, timeout);
        }
        catch (HttpRequestException ex) when (ex.InnerException is UnsafeAddressException || ex.GetBaseException() is UnsafeAddressException)
        {
            return new FetchResult(FetchOutcome.Refused, Detail: ex.GetBaseException().Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogDebug(ex, "Fetching {Host} failed", uri.Host);
            _failedUntil[uri.Host] = time.GetUtcNow() + FailureMemory;
            return new FetchResult(FetchOutcome.Failed, Detail: ex.GetType().Name);
        }
    }

    /// <summary>The 10 s limit runs per request and starts after the politeness wait, so queueing behind another fetch never fails a host.</summary>
    private async Task<FetchResult> FollowAsync(Uri start, FetchOptions cfg, CancellationToken caller, CancellationTokenSource limit)
    {
        var current = start;
        for (var hop = 0; ; hop++)
        {
            await GateAsync(current.Host, caller);
            limit.CancelAfter(Timeout);
            var ct = limit.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("text/html,text/plain;q=0.9");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                if (hop >= MaxRedirects)
                {
                    return new FetchResult(FetchOutcome.TooManyRedirects);
                }

                var next = new Uri(current, location);
                if (current.Scheme == "https" && next.Scheme == "http")
                {
                    return new FetchResult(FetchOutcome.Refused, Detail: "redirect from https to http");
                }

                current = next;
                if (Check(current, cfg) is { } refused)
                {
                    return refused;
                }

                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                _failedUntil[current.Host] = time.GetUtcNow() + FailureMemory;
                return new FetchResult(FetchOutcome.Failed, Detail: ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("text/html" or "application/xhtml+xml" or "text/plain"))
            {
                return new FetchResult(FetchOutcome.BadType, Detail: type);
            }

            return new FetchResult(FetchOutcome.Ok, await ReadCappedAsync(response, ct), current);
        }
    }

    private static FetchResult? Check(Uri uri, FetchOptions cfg)
    {
        if (uri.Scheme is not ("http" or "https") || uri.Port is not (80 or 443) || uri.UserInfo.Length > 0)
        {
            return new FetchResult(FetchOutcome.Refused, Detail: "scheme, port or credentials");
        }

        if (cfg.BlockedHosts.Any(b => uri.Host.Equals(b, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + b, StringComparison.OrdinalIgnoreCase)))
        {
            return new FetchResult(FetchOutcome.Blocked);
        }

        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !IpGuard.IsPublic(literal)
            ? new FetchResult(FetchOutcome.Refused, Detail: "address")
            : null;
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBytes];
        var read = 0;
        while (read < MaxBytes)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, MaxBytes - read), ct);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(response.Content.Headers.ContentType?.CharSet ?? "utf-8");
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(buffer, 0, read);
    }

    /// <summary>Drops expired failure and politeness entries once the tables grow, so a long run over many hosts does not accumulate them.</summary>
    private void Prune()
    {
        var now = time.GetUtcNow();
        if (_failedUntil.Count > PruneAbove)
        {
            foreach (var (host, until) in _failedUntil)
            {
                if (until <= now)
                {
                    _failedUntil.TryRemove(host, out _);
                }
            }
        }

        lock (_nextAt)
        {
            if (_nextAt.Count > PruneAbove)
            {
                foreach (var host in _nextAt.Where(e => e.Value <= now).Select(e => e.Key).ToList())
                {
                    _nextAt.Remove(host);
                }
            }
        }
    }

    /// <summary>One request a second per host: the next slot is reserved up front, so concurrent callers queue.</summary>
    private async Task GateAsync(string host, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_nextAt)
        {
            var now = time.GetUtcNow();
            var at = _nextAt.TryGetValue(host, out var next) && next > now ? next : now;
            _nextAt[host] = at + HostGap;
            wait = at - now;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, time, ct);
        }
    }

    /// <summary>Counts this fetch against today's cap, kept in the database so a restart does not reset it.</summary>
    private async Task<bool> CountAsync(int cap, CancellationToken ct)
    {
        await _counter.WaitAsync(ct);
        try
        {
            var key = $"fetch:count:{time.GetUtcNow():yyyy-MM-dd}";
            var used = int.TryParse(await cursors.GetAsync(key, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (used >= cap)
            {
                return false;
            }

            await cursors.SetAsync(key, (used + 1).ToString(CultureInfo.InvariantCulture), ct);
            return true;
        }
        finally
        {
            _counter.Release();
        }
    }
}
