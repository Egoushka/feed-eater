using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using FeedEater.Setup;

namespace FeedEater.Tests;

public sealed class ConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static List<ConfigEntry> Schema() => ConfigPrinter.Entries(new FeedEaterOptions(), FeedEaterOptions.Section, schema: true).ToList();

    [Fact]
    public void Print_config_lists_every_setting_with_its_default_as_sorted_key_value_lines()
    {
        var lines = ConfigPrinter.Lines(Config());

        Assert.Equal(lines.Order(StringComparer.Ordinal), lines);
        Assert.Contains("FeedEater:TimeZone=UTC", lines);
        Assert.Contains("FeedEater:Llm:BaseUrl=https://api.openai.com/v1/", lines);
        Assert.Contains("FeedEater:Llm:ReadModel=gpt-4.1-mini", lines);
        Assert.Contains("FeedEater:Hindsight:Bank=feed-eater", lines);
        Assert.Contains("FeedEater:DigestAt=07:30:00", lines);
        Assert.Contains("FeedEater:Taste:Learn=false", lines);
        Assert.Contains("FeedEater:Quiet:From=", lines);
        Assert.Contains("FeedEater:Plane:BaseUrl=", lines);
        Assert.Contains("FeedEater:Fetch:BlockedHosts=facebook.com,instagram.com,x.com,twitter.com,linkedin.com,tiktok.com,youtube.com,youtu.be,t.me,discord.com,medium.com,nytimes.com,wsj.com,bloomberg.com,ft.com", lines);
        Assert.All(lines, l => Assert.Contains('=', l));
    }

    [Fact]
    public void Print_config_shows_what_the_environment_changed_and_prices_per_model()
    {
        var lines = ConfigPrinter.Lines(Config(
            ("FeedEater:TimeZone", "Europe/Kyiv"), ("FeedEater:Llm:Prices:gpt-4.1-nano:Input", "0.1"), ("FeedEater:Llm:Prices:gpt-4.1-nano:Output", "0.4")));

        Assert.Contains("FeedEater:TimeZone=Europe/Kyiv", lines);
        Assert.Contains("FeedEater:Llm:Prices:gpt-4.1-nano:Input=0.1", lines);
        Assert.Contains("FeedEater:Llm:Prices:gpt-4.1-nano:Output=0.4", lines);
    }

    [Fact]
    public void Print_config_masks_secrets_but_still_shows_that_they_are_set()
    {
        var lines = ConfigPrinter.Lines(Config(
            ("FeedEater:Llm:ApiKey", "sk-very-secret"), ("FeedEater:Telegram:Token", "123:abc"), ("FeedEater:Plane:Token", ""),
            ("FeedEater:Watch:Source", "https://example.com/pins?access_token=SECRET"), ("FeedEater:Watch:Token", "wt"), ("FeedEater:GitHub:Token", "gh-secret-token"),
            ("FeedEater:Plane:BaseUrl", "https://user:pw@plane.example/"),
            ("ConnectionStrings:FeedEater", "Host=db;Database=feed;Username=u;Password=hunter2;Pooling=true"), ("Mcp:Token", "mcp-secret")));

        Assert.Contains("FeedEater:Llm:ApiKey=***", lines);
        Assert.Contains("FeedEater:Telegram:Token=***", lines);
        Assert.Contains("FeedEater:Watch:Token=***", lines);
        Assert.Contains("FeedEater:GitHub:Token=***", lines);
        Assert.Contains("FeedEater:Plane:Token=", lines);   // unset stays visibly unset
        Assert.Contains("FeedEater:Watch:Source=https://example.com/pins?***", lines);
        Assert.Contains("FeedEater:Plane:BaseUrl=https://plane.example/", lines);
        Assert.Contains("ConnectionStrings:FeedEater=Host=db;Database=feed;Username=u;Password=***;Pooling=true", lines);
        Assert.Contains("Mcp:Token=***", lines);
        var everything = string.Join('\n', lines);
        foreach (var secret in new[] { "sk-very-secret", "123:abc", "SECRET", "hunter2", "mcp-secret", "user:pw", "gh-secret-token" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_setting_is_described_and_every_one_that_looks_secret_is_marked_secret()
    {
        var entries = Schema();

        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Property.GetCustomAttribute<DescriptionAttribute>()?.Description), $"{e.Key} has no [Description]"));
        foreach (var e in entries.Where(e => Regex.IsMatch(e.Property.Name, "Token|ApiKey|Password|Secret", RegexOptions.None, TimeSpan.FromSeconds(1))))
        {
            Assert.True(e.Property.IsDefined(typeof(SecretAttribute)), $"{e.Key} must carry [Secret]");
        }

        Assert.All(entries.Where(e => e.Property.IsDefined(typeof(SecretAttribute))), e => Assert.Equal(typeof(string), e.Property.PropertyType));
    }

    [Fact]
    public void Each_integration_is_off_until_its_required_settings_are_filled()
    {
        Assert.False(new MinifluxOptions().Enabled);
        Assert.False(new MinifluxOptions { BaseUrl = "http://m/" }.Enabled);
        Assert.True(new MinifluxOptions { BaseUrl = "http://m/", Token = "t" }.Enabled);

        Assert.False(new PlaneOptions { BaseUrl = "http://p/", Token = "t" }.Enabled);   // no workspace
        Assert.False(new PlaneOptions { BaseUrl = "http://p/", Workspace = "w" }.Enabled);   // no key
        Assert.True(new PlaneOptions { BaseUrl = "http://p/", Token = "t", Workspace = "w" }.Enabled);

        Assert.False(new KarakeepOptions { Token = "t" }.Enabled);
        Assert.True(new KarakeepOptions { BaseUrl = "http://k/", Token = "t" }.Enabled);

        Assert.False(new GitHubOptions().Enabled);   // the API URL has a default; the user does not
        Assert.True(new GitHubOptions { User = "octocat" }.Enabled);

        Assert.False(new HindsightOptions { Token = "t" }.Enabled);
        Assert.True(new HindsightOptions { BaseUrl = "http://h/" }.Enabled);   // a server that needs no key

        Assert.False(new WatchOptions { Token = "t", MapPath = "/m.json" }.Enabled);
        Assert.True(new WatchOptions { Source = "/PINS.md" }.Enabled);
        Assert.True(new WatchOptions { FallbackPath = "/watch.json" }.Enabled);

        Assert.True(new FetchOptions().Enabled);
        Assert.False(new FetchOptions { MaxPerDay = 0 }.Enabled);
    }

    [Fact]
    public void The_idea_sink_is_the_configured_one_else_Plane_when_it_is_on_else_local()
    {
        var plane = new PlaneOptions { BaseUrl = "http://p/", Token = "t", Workspace = "w" };

        Assert.Equal("local", new FeedEaterOptions().IdeaSink);
        Assert.Equal("plane", new FeedEaterOptions { Plane = plane }.IdeaSink);
        Assert.Equal("local", new FeedEaterOptions { Plane = plane, Ideas = new IdeasOptions { Sink = "local" } }.IdeaSink);
        Assert.Equal("plane", new FeedEaterOptions { Ideas = new IdeasOptions { Sink = "plane" } }.IdeaSink);
        Assert.Equal("local", new FeedEaterOptions { Ideas = new IdeasOptions { Sink = "webhook" } }.IdeaSink);   // unknown: the always-on default
    }
}

/// <summary>docs/CONFIG.md is generated from <see cref="FeedEaterOptions"/>; a setting without a row, or a stale row, fails here.</summary>
public sealed class ConfigDocTests
{
    private static readonly string Path = RepoRoot.File("docs/CONFIG.md");

    [Fact]
    public void The_config_doc_matches_the_options()
    {
        var expected = ConfigDocs.Render();
        if (Environment.GetEnvironmentVariable("UPDATE_CONFIG_DOC") == "1")
        {
            File.WriteAllText(Path, expected);
        }

        Assert.True(File.Exists(Path), "docs/CONFIG.md is missing; run: UPDATE_CONFIG_DOC=1 dotnet test --filter ConfigDocTests");
        Assert.True(expected == File.ReadAllText(Path), "docs/CONFIG.md is out of date; run: UPDATE_CONFIG_DOC=1 dotnet test --filter ConfigDocTests, then commit it");
    }

    [Fact]
    public void Every_setting_key_appears_in_the_doc()
    {
        var doc = File.ReadAllText(Path);

        foreach (var entry in ConfigPrinter.Entries(new FeedEaterOptions(), FeedEaterOptions.Section, schema: true))
        {
            Assert.Contains($"`{entry.Key}`", doc, StringComparison.Ordinal);
        }
    }
}

internal static class ConfigDocs
{
    private static readonly (string Name, string On, string Off)[] Integrations =
    [
        ("Miniflux", "`Miniflux:BaseUrl` and `Miniflux:Token`", "Nothing is copied from Miniflux and no full text is fetched through it."),
        ("Plane", "`Plane:BaseUrl`, `Plane:Token` and `Plane:Workspace`", "Ideas go to the local list (see `Ideas:Sink`); open Plane work is not read into project vectors."),
        ("Karakeep", "`Karakeep:BaseUrl` and `Karakeep:Token`", "No 📌 button and no bookmark import."),
        ("GitHub stars", "`GitHub:User`", "No stars import."),
        ("Hype autopsy", "`GitHub:User` and `Telegram:Token`", "No repo snapshots and no monthly autopsy; `GitHub:Token` is optional and lifts the 60 requests an hour limit."),
        ("Hindsight", "`Hindsight:BaseUrl`", "No weekly summary is sent."),
        ("Release watch", "`Watch:Source` or `Watch:FallbackPath`", "No release watch and no release alerts."),
        ("Linked pages", "`Fetch:MaxPerDay` above 0", "No linked page or Hacker News comment is fetched."),
    ];

    public static string Render()
    {
        var entries = ConfigPrinter.Entries(new FeedEaterOptions(), FeedEaterOptions.Section, schema: true).ToList();
        var sb = new StringBuilder();
        sb.Append("# Configuration\n\n");
        sb.Append("Generated from `FeedEaterOptions` by `ConfigDocTests`. Do not edit by hand: change the `[Description]` in `src/FeedEater/FeedEaterOptions.cs`, ");
        sb.Append("then run `UPDATE_CONFIG_DOC=1 dotnet test --filter ConfigDocTests` and commit the result.\n\n");
        sb.Append("Settings are keys of the `FeedEater` section. In the environment write `__` for `:`, so `FeedEater:Llm:BaseUrl` is `FeedEater__Llm__BaseUrl`. ");
        sb.Append("`dotnet FeedEater.dll doctor --print-config` prints every effective value with secrets masked.\n\n");
        sb.Append("## Outside the `FeedEater` section\n\n| Key | Description |\n|---|---|\n");
        sb.Append("| `ConnectionStrings:FeedEater` | Required. Npgsql connection string for Postgres 18 with the pgvector extension. |\n");
        sb.Append("| `Mcp:Token` | Bearer token for `/mcp` and the password for `/ui/login`. Empty: both refuse every request. |\n\n");
        sb.Append("## Integrations\n\n");
        sb.Append("An integration is off until its required settings are filled. An off integration registers no background job, shows no button and makes no call.\n\n");
        sb.Append("| Integration | On when | Off means |\n|---|---|---|\n");
        foreach (var (name, on, off) in Integrations)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {name} | {on} | {off} |\n");
        }

        var sections = entries.GroupBy(Section).OrderBy(g => g.Key == "General" ? "" : g.Key, StringComparer.Ordinal);
        foreach (var section in sections)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n## {section.Key}\n\n| Key | Default | Description |\n|---|---|---|\n");
            foreach (var e in section)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| `{e.Key}` | {Default(e)} | {Cell(e.Property.GetCustomAttribute<DescriptionAttribute>()!.Description)} |\n");
            }
        }

        return sb.ToString();
    }

    private static string Section(ConfigEntry e)
    {
        var parts = e.Key.Split(':');
        return parts.Length > 2 ? parts[1] : "General";
    }

    private static string Default(ConfigEntry e) => ConfigPrinter.Display(e) is { Length: > 0 } value ? $"`{Cell(value)}`" : "(empty)";

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
