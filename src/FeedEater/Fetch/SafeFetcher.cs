using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Fetch;

public enum FetchOutcome { Ok, Refused, Blocked, BadType, TooManyRedirects, Failed, CapReached, Cached, NotModified, TooLarge }

public sealed record FetchResult(FetchOutcome Outcome, string? Body = null, Uri? FinalUri = null, string? Detail = null, string? ETag = null, string? LastModified = null)
{
    public bool Ok => Outcome == FetchOutcome.Ok;
}

/// <summary>The validators of the last good fetch of a feed, sent back so an unchanged feed answers 304.</summary>
public sealed record FeedConditions(string? ETag = null, string? LastModified = null);

/// <summary>The only code that opens a connection to a host named by a feed. The rules are in docs/specs/2026-10-06-page-fetch.md.</summary>
public sealed class SafeFetcher(HttpClient http, IOptions<FeedEaterOptions> options, CursorStore cursors, TimeProvider time, ILogger<SafeFetcher> logger)
{
    public const string ClientName = "safe-fetch";
    public const string UserAgent = "feed-eater/0.5.0 (+https://github.com/Egoushka/feed-eater)";
    private const int MaxRedirects = 3;
    private const int MaxBytes = 1024 * 1024;
    private const int MaxFeedBytes = 5 * 1024 * 1024;
    internal static int PruneAbove { get; set; } = 256;
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureMemory = TimeSpan.FromHours(24);
    private static readonly TimeSpan HostGap = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextAt = new(StringComparer.OrdinalIgnoreCase);
    internal int TrackedHosts => _failedUntil.Count + _nextAt.Count;

    private readonly SemaphoreSlim _counter = new(1, 1);

    /// <summary>The handler for the typed client: pinned connect, no redirects, no cookies, no proxy.</summary>
    public static SocketsHttpHandler CreateHandler(IReadOnlyCollection<string>? allowedHosts = null) => new()
    {
        ConnectCallback = (allowedHosts is { Count: > 0 } ? GuardedConnector.Default.Trusting(allowedHosts) : GuardedConnector.Default).ConnectAsync,
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    public Task<FetchResult> FetchAsync(Uri uri, CancellationToken ct) => RunAsync(uri, null, ct);

    /// <summary>
    /// A feed: conditional GET, any content type, up to 5 MB, not counted against <c>MaxPerDay</c> and not on the blocked list.
    /// A failure does not park the host: the feed's own backoff decides when to ask again.
    /// </summary>
    public Task<FetchResult> FetchFeedAsync(Uri uri, FeedConditions conditions, CancellationToken ct) => RunAsync(uri, conditions, ct);

    private async Task<FetchResult> RunAsync(Uri uri, FeedConditions? feed, CancellationToken ct)
    {
        var cfg = options.Value.Fetch;
        if (Check(uri, cfg, feed is not null) is { } refused)
        {
            return refused;
        }

        Prune();
        if (feed is null)
        {
            if (_failedUntil.TryGetValue(uri.Host, out var until) && until > time.GetUtcNow())
            {
                return new FetchResult(FetchOutcome.Cached, Detail: "failed recently");
            }

            if (!await CountAsync(cfg.MaxPerDay, ct))
            {
                return new FetchResult(FetchOutcome.CapReached);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            return await FollowAsync(uri, cfg, feed, ct, timeout);
        }
        catch (HttpRequestException ex) when (ex.InnerException is UnsafeAddressException || ex.GetBaseException() is UnsafeAddressException)
        {
            return new FetchResult(FetchOutcome.Refused, Detail: ex.GetBaseException().Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogDebug(ex, "Fetching {Host} failed", uri.Host);
            if (feed is null)
            {
                _failedUntil[uri.Host] = time.GetUtcNow() + FailureMemory;
            }

            return new FetchResult(FetchOutcome.Failed, Detail: ex.GetType().Name);
        }
    }

    /// <summary>The 10 s limit runs per request and starts after the politeness wait, so queueing behind another fetch never fails a host.</summary>
    private async Task<FetchResult> FollowAsync(Uri start, FetchOptions cfg, FeedConditions? feed, CancellationToken caller, CancellationTokenSource limit)
    {
        var current = start;
        for (var hop = 0; ; hop++)
        {
            await GateAsync(current.Host, caller);
            limit.CancelAfter(Timeout);
            var ct = limit.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            if (feed is null)
            {
                request.Headers.Accept.ParseAdd("text/html,text/plain;q=0.9");
            }
            else
            {
                request.Headers.Accept.ParseAdd("application/rss+xml,application/atom+xml,application/feed+json,application/xml;q=0.9,text/xml;q=0.9,application/json;q=0.8,*/*;q=0.5");
                AddValidators(request, feed);
            }

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
                if (Check(current, cfg, feed is not null) is { } refused)
                {
                    return refused;
                }

                continue;
            }

            if (feed is not null && response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchResult(FetchOutcome.NotModified, FinalUri: current);
            }

            if (!response.IsSuccessStatusCode)
            {
                if (feed is null)
                {
                    _failedUntil[current.Host] = time.GetUtcNow() + FailureMemory;
                }

                return new FetchResult(FetchOutcome.Failed, Detail: ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            if (feed is not null)
            {
                var (text, over) = await ReadCappedAsync(response, MaxFeedBytes, ct);
                return over
                    ? new FetchResult(FetchOutcome.TooLarge)
                    : new FetchResult(FetchOutcome.Ok, text, current, ETag: Header(response, "ETag"), LastModified: Header(response, "Last-Modified"));
            }

            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("text/html" or "application/xhtml+xml" or "text/plain"))
            {
                return new FetchResult(FetchOutcome.BadType, Detail: type);
            }

            return new FetchResult(FetchOutcome.Ok, (await ReadCappedAsync(response, MaxBytes, ct)).Text, current);
        }
    }

    private static void AddValidators(HttpRequestMessage request, FeedConditions feed)
    {
        if (feed.ETag is { Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", feed.ETag);
        }

        if (feed.LastModified is { Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", feed.LastModified);
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)) ? values.FirstOrDefault() : null;

    /// <summary>Private hosts are refused unless listed in <c>Source:AllowedHosts</c>, which also lifts the port rule for them. Feeds skip the blocked list.</summary>
    private FetchResult? Check(Uri uri, FetchOptions cfg, bool feed = false)
    {
        var allowed = options.Value.Source.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        if (uri.Scheme is not ("http" or "https") || (!allowed && uri.Port is not (80 or 443)) || uri.UserInfo.Length > 0)
        {
            return new FetchResult(FetchOutcome.Refused, Detail: "scheme, port or credentials");
        }

        if (!feed && cfg.BlockedHosts.Any(b => uri.Host.Equals(b, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + b, StringComparison.OrdinalIgnoreCase)))
        {
            return new FetchResult(FetchOutcome.Blocked);
        }

        return !allowed && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !IpGuard.IsPublic(literal)
            ? new FetchResult(FetchOutcome.Refused, Detail: "address")
            : null;
    }

    /// <summary>Reads at most <paramref name="max"/> bytes; Over says the body was longer.</summary>
    private static async Task<(string Text, bool Over)> ReadCappedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(max + 1);
        try
        {
            var read = 0;
            while (read <= max)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, max + 1 - read), ct);
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

            return (encoding.GetString(buffer, 0, Math.Min(read, max)), read > max);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
