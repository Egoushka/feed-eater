using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace FeedEater.Ui;

/// <summary>
/// Signed-cookie sessions for the web UI: "expiry.nonce.signature", HMAC-SHA256. The key is derived from Mcp:Token, so
/// changing the token signs everyone out, and an unset token disables the UI.
/// </summary>
public sealed class UiSession(string? token, TimeProvider time)
{
    public const string CookieName = "fe_session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly byte[] _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token ?? ""));
    private readonly byte[] _key = string.IsNullOrEmpty(token)
        ? []
        : HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(token), 32, info: "feed-eater ui session"u8.ToArray());

    public bool Enabled => !string.IsNullOrEmpty(token);

    /// <summary>Fixed-time comparison of hashes, so neither the value nor its length leaks.</summary>
    public bool CheckToken(string? given) =>
        Enabled && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given ?? "")), _tokenHash);

    public string Issue(DateTimeOffset expires)
    {
        var payload = string.Create(CultureInfo.InvariantCulture, $"{expires.ToUnixTimeSeconds()}.{Base64Url(RandomNumberGenerator.GetBytes(12))}");
        return $"{payload}.{Sign(payload)}";
    }

    public string IssueNew() => Issue(time.GetUtcNow() + Lifetime);

    public bool IsValid(string? cookie)
    {
        if (!Enabled || cookie?.Split('.') is not [var expiry, var nonce, var signature]
            || !long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
        {
            return false;
        }

        return Same(signature, Sign($"{expiry}.{nonce}")) && unix > time.GetUtcNow().ToUnixTimeSeconds();
    }

    /// <summary>The per-session anti-forgery value, rendered into every form.</summary>
    public string AntiForgery(string cookie) => Sign("csrf:" + cookie);

    public bool CheckAntiForgery(string cookie, string? given) => given is not null && Same(given, AntiForgery(cookie));

    private string Sign(string text) => Base64Url(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(text)));

    private static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>At most 5 login attempts a minute across all clients; behind a proxy there is no trustworthy client address to key on.</summary>
public sealed class LoginThrottle(TimeProvider time)
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Queue<DateTimeOffset> _attempts = new();

    public bool TryAcquire()
    {
        lock (_attempts)
        {
            var now = time.GetUtcNow();
            while (_attempts.Count > 0 && now - _attempts.Peek() >= Window)
            {
                _attempts.Dequeue();
            }

            if (_attempts.Count >= MaxAttempts)
            {
                return false;
            }

            _attempts.Enqueue(now);
            return true;
        }
    }
}
