using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using FeedEater.Setup;

namespace FeedEater.Tests;

/// <summary>The files a stranger copies (compose.example.yaml, .env.example) set only keys that exist, and set every key a first start needs.</summary>
public sealed partial class SetupFilesTests
{
    private static readonly string[] RequiredKeys =
    [
        "POSTGRES_PASSWORD", "Mcp__Token", "FeedEater__Llm__ApiKey", "FeedEater__Telegram__Token", "FeedEater__Telegram__AllowedUserId",
    ];

    private static readonly string Env = File.ReadAllText(RepoRoot.File(".env.example"));
    private static readonly string Compose = File.ReadAllText(RepoRoot.File("compose.example.yaml"));

    /// <summary>Every <c>NAME=</c> in the file, set or commented out with <c>#</c>.</summary>
    [GeneratedRegex(@"^#?\s?([A-Za-z_][A-Za-z0-9_]*)=(.*)$", RegexOptions.Multiline, 1000)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"\b(FeedEater__\w+|ConnectionStrings__\w+|Mcp__Token)\b", RegexOptions.None, 1000)]
    private static partial Regex AppKey();

    /// <summary>The settings in environment spelling: <c>FeedEater:Llm:ApiKey</c> is <c>FeedEater__Llm__ApiKey</c>.</summary>
    private static HashSet<string> KnownKeys()
    {
        var keys = ConfigPrinter.Entries(new FeedEaterOptions(), FeedEaterOptions.Section, schema: true).Select(e => e.Key.Replace(":", "__", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        keys.Add("Mcp__Token");
        keys.Add("ConnectionStrings__FeedEater");
        return keys;
    }

    [Fact]
    public void Every_app_key_the_env_example_sets_or_suggests_is_a_real_setting()
    {
        var known = KnownKeys();
        var keys = Assignment().Matches(Env).Select(m => m.Groups[1].Value).Where(k => AppKey().IsMatch(k)).ToList();

        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.True(known.Contains(k), $"{k} in .env.example is not a setting"));
    }

    [Fact]
    public void Every_app_key_the_compose_file_sets_is_a_real_setting()
    {
        var known = KnownKeys();
        var keys = AppKey().Matches(Compose).Select(m => m.Value).Distinct().ToList();

        Assert.Contains("ConnectionStrings__FeedEater", keys);
        Assert.All(keys, k => Assert.True(known.Contains(k), $"{k} in compose.example.yaml is not a setting"));
    }

    [Fact]
    public void The_env_example_sets_every_key_a_first_start_needs_and_leaves_secrets_empty()
    {
        var set = Assignment().Matches(Env).Where(m => !m.Value.StartsWith('#')).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());

        Assert.All(RequiredKeys, k => Assert.True(set.ContainsKey(k), $"{k} is not set in .env.example"));
        Assert.All(new[] { "POSTGRES_PASSWORD", "Mcp__Token", "FeedEater__Llm__ApiKey", "FeedEater__Telegram__Token" }, k => Assert.Equal("", set[k]));
        Assert.Equal("0", set["FeedEater__Telegram__AllowedUserId"]);
        Assert.Contains("POSTGRES_PASSWORD", Compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_key_in_the_env_example_has_a_comment_above_it()
    {
        var lines = Env.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (Assignment().IsMatch(lines[i]))
            {
                Assert.True(i > 0 && lines[i - 1].StartsWith('#'), $"line {i + 1} ({lines[i]}) has no comment above it");
            }
        }
    }

    [Fact]
    public void The_values_the_env_example_sets_bind_to_the_options_and_the_time_zone_exists()
    {
        var values = Assignment().Matches(Env).Where(m => !m.Value.StartsWith('#') && m.Groups[1].Value.StartsWith("FeedEater__", StringComparison.Ordinal))
            .ToDictionary(m => m.Groups[1].Value.Replace("__", ":", StringComparison.Ordinal), m => (string?)m.Groups[2].Value.Trim());

        var options = new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(FeedEaterOptions.Section).Get<FeedEaterOptions>()!;

        Assert.Equal(0, options.Telegram.AllowedUserId);
        Assert.Equal("https://api.openai.com/v1/", options.Llm.BaseUrl);
        Assert.NotNull(options.Zone);
    }

    [Fact]
    public void The_compose_file_runs_the_app_beside_a_pgvector_database_and_waits_for_it()
    {
        Assert.Contains("image: pgvector/pgvector:pg18", Compose, StringComparison.Ordinal);
        Assert.Contains("image: ghcr.io/egoushka/feed-eater:", Compose, StringComparison.Ordinal);
        Assert.Contains("env_file: .env", Compose, StringComparison.Ordinal);
        Assert.Contains("condition: service_healthy", Compose, StringComparison.Ordinal);
        Assert.Contains("pg_isready", Compose, StringComparison.Ordinal);
        Assert.Contains("./config:/config", Compose, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:8080:8080", Compose, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  db:\r?\n", Compose);
        Assert.Matches(@"(?m)^  feed-eater:\r?\n", Compose);
    }

    [Fact]
    public void The_dockerfile_has_a_healthcheck_on_healthz()
    {
        var dockerfile = File.ReadAllText(RepoRoot.File("Dockerfile"));

        Assert.Matches(@"HEALTHCHECK[^\n]*\\\s*\n\s*CMD curl -fsS http://127\.0\.0\.1:8080/healthz", dockerfile);
    }

    [Fact]
    public void The_image_is_built_for_amd64_and_arm64_and_actions_stay_pinned_by_digest()
    {
        var ci = File.ReadAllText(RepoRoot.File(".github/workflows/ci.yml"));

        Assert.Contains("platforms: linux/amd64,linux/arm64", ci, StringComparison.Ordinal);
        Assert.Contains("docker/setup-qemu-action@", ci, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/egoushka/feed-eater:${{ steps.v.outputs.version }}", ci, StringComparison.Ordinal);
        var uses = Regex.Matches(ci, @"uses: (\S+)", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => m.Groups[1].Value);
        Assert.All(uses, u => Assert.Matches(@"@[0-9a-f]{40}$", u));
    }
}
