using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using FeedEater.Ingest;
using FeedEater.Setup;
using FeedEater.Storage;

namespace FeedEater.Tests;

/// <summary>Stand-ins for every service a doctor run can call, keyed by the path of the request.</summary>
internal static class SetupStubs
{
    public const string ApiKey = "sk-very-secret";
    public const string BotToken = "123456:telegram-secret";

    private static readonly string Chat = """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";

    /// <summary>Answers like healthy services; <paramref name="over"/> may answer a request first, with null meaning "not mine".</summary>
    public static StubHandler Services(Func<HttpRequestMessage, HttpResponseMessage?>? over = null) => new((request, _) =>
    {
        if (over?.Invoke(request) is { } custom)
        {
            return custom;
        }

        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            _ when path.EndsWith("/chat/completions", StringComparison.Ordinal) => StubHandler.Json(Chat),
            _ when path.EndsWith("/embeddings", StringComparison.Ordinal) => StubHandler.Json(TestVectors.EmbeddingResponse(1)),
            _ when path.EndsWith("/getMe", StringComparison.Ordinal) => StubHandler.Json("""{"ok":true,"result":{"id":1,"username":"my_feed_bot"}}"""),
            _ when path.EndsWith("/projects/", StringComparison.Ordinal) => StubHandler.Json("""{"results":[]}"""),
            _ when path.EndsWith("/bookmarks", StringComparison.Ordinal) => StubHandler.Json("""{"bookmarks":[]}"""),
            _ when path.EndsWith("/entries", StringComparison.Ordinal) => StubHandler.Json("""{"entries":[]}"""),
            _ => StubHandler.Json("{}", HttpStatusCode.NotFound),
        };
    });

    public static void Use(IServiceCollection services, StubHandler stub) =>
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => stub));
}

[Collection(PostgresCollection.Name)]
public sealed class SetupDatabaseTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<CheckRow> RunAsync(IIntegrationCheck check) =>
        (await new CheckRunner([check], Options.Create(new FeedEaterOptions()), new ConfigurationBuilder().Build()).RunAsync(default)).Single();

    // the database check

    [Fact]
    public async Task The_database_is_ok_with_its_version_and_pgvector()
    {
        var row = await RunAsync(new DatabaseCheck(pg.Db));

        Assert.Equal(CheckStatus.Ok, row.Result.Status);
        Assert.True(row.Required);
        Assert.Matches(@"^Postgres 18\.\d+, pgvector \d+\.\d+", row.Result.Detail);
    }

    [Fact]
    public async Task An_unreachable_database_fails_and_the_fix_names_the_connection_string()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=9;Database=feed;Username=feed;Password=pw-pw-pw;Timeout=3");

        var row = await RunAsync(new DatabaseCheck(new FeedDb(source)));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Contains("ConnectionStrings__FeedEater", row.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_password_says_so()
    {
        var builder = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Password = "not-the-password" };
        await using var source = NpgsqlDataSource.Create(builder.ConnectionString);

        var row = await RunAsync(new DatabaseCheck(new FeedDb(source)));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Contains("password", row.Result.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-password", row.Result.Detail, StringComparison.Ordinal);
    }

    // the default feed source probe

    private DefaultFeedSourceProbe Probe(FeedEaterOptions? options = null, StubHandler? miniflux = null, FeedDb? db = null)
    {
        var o = Options.Create(options ?? new FeedEaterOptions());
        var services = new ServiceCollection();
        services.AddSingleton(new MinifluxClient((miniflux ?? SetupStubs.Services()).Client("http://miniflux.example/")));
        return new DefaultFeedSourceProbe(db ?? pg.Db, services.BuildServiceProvider(), o);
    }

    [Fact]
    public async Task Without_Miniflux_no_feeds_is_a_failure_that_says_where_to_add_one()
    {
        var probe = Probe();

        var result = await probe.CheckAsync(default);

        Assert.Equal("built-in reader", probe.Name);
        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Equal("no feeds", result.Detail);
        Assert.Contains("import an OPML", result.Fix, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/ui/sources", result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_Miniflux_feeds_that_were_never_fetched_are_a_warning()
    {
        await Seed.ItemAsync(pg, 1, "A post", TestVectors.OneHot(0));
        await Seed.ItemAsync(pg, 2, "Another", TestVectors.OneHot(1));

        var result = await Probe().CheckAsync(default);

        Assert.Equal(CheckStatus.Warn, result.Status);
        Assert.Equal("2 feeds, none fetched yet", result.Detail);
    }

    [Fact]
    public async Task Without_Miniflux_fetched_feeds_are_ok_and_a_failing_one_is_counted()
    {
        await Seed.ItemAsync(pg, 1, "A post", TestVectors.OneHot(0));
        await Seed.ItemAsync(pg, 2, "Another", TestVectors.OneHot(1));
        await SetFeedStateAsync("last_fetched_at = now()");
        await SetFeedStateAsync("fail_count = 3, last_error = 'HTTP 404'", 2);

        Assert.Equal(CheckResult.Ok("2 feeds, 2 fetched, 1 failing"), await Probe().CheckAsync(default));
    }

    [Fact]
    public async Task Without_Miniflux_every_feed_failing_is_a_failure_with_the_last_error()
    {
        await Seed.ItemAsync(pg, 1, "A post", TestVectors.OneHot(0));
        await SetFeedStateAsync("fail_count = 5, last_error = 'HTTP 404', last_fetched_at = now()");

        var result = await Probe().CheckAsync(default);

        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Equal("1 feed, every one failing: HTTP 404", result.Detail);
    }

    private async Task SetFeedStateAsync(string assignments, long? feedId = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await Dapper.SqlMapper.ExecuteAsync(c, $"update feeds set {assignments} where (@feedId::bigint is null or id = @feedId)", new { feedId });
    }

    [Fact]
    public async Task A_database_without_tables_asks_for_a_first_start()
    {
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("drop database if exists doctor_empty");
            await c.ExecuteAsync("create database doctor_empty");
        }

        var empty = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = "doctor_empty", Pooling = false };
        await using var source = NpgsqlDataSource.Create(empty.ConnectionString);

        var result = await Probe(db: new FeedDb(source)).CheckAsync(default);

        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Contains("no tables", result.Detail, StringComparison.Ordinal);
        Assert.Contains("docker compose up -d", result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_Miniflux_mode_the_probe_calls_Miniflux_and_does_not_count_feeds()
    {
        var o = new FeedEaterOptions { Miniflux = new MinifluxOptions { BaseUrl = "http://miniflux.example/", Token = "mf-miniflux-key" } };
        var up = SetupStubs.Services();
        var down = SetupStubs.Services(_ => StubHandler.Json("{}", HttpStatusCode.Unauthorized));

        var probe = Probe(o, up);
        var ok = await probe.CheckAsync(default);
        var refused = await Probe(o, down).CheckAsync(default);

        Assert.Equal("Miniflux", probe.Name);
        Assert.Equal(CheckResult.Ok("miniflux.example answers"), ok);
        Assert.Contains("/v1/entries", up.Calls.Single().Uri, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Fail, refused.Status);
        Assert.Contains("FeedEater__Miniflux__Token", refused.Fix, StringComparison.Ordinal);
    }

    // doctor

    private IConfiguration Config(params (string Key, string? Value)[] extra)
    {
        (string Key, string? Value)[] defaults =
        [
            ("ConnectionStrings:FeedEater", pg.ConnectionString),
            ("Mcp:Token", "mcp-secret-token"),
            ("FeedEater:RunJobs", "true"),
            ("FeedEater:Llm:ApiKey", SetupStubs.ApiKey),
            ("FeedEater:Telegram:Token", SetupStubs.BotToken),
            ("FeedEater:Telegram:AllowedUserId", "42"),
            ("FeedEater:ProfilePath", "Fixtures/profile.json"),
        ];
        return new ConfigurationBuilder()
            .AddInMemoryCollection(defaults.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .AddInMemoryCollection(extra.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))   // the later source wins
            .Build();
    }

    private static async Task<(int Code, string Output)> DoctorAsync(IConfiguration config, StubHandler stub, string[]? args = null, Action<IServiceCollection>? configure = null)
    {
        var writer = new StringWriter();
        var code = await DoctorCommand.RunAsync(args ?? [], config, writer, s =>
        {
            SetupStubs.Use(s, stub);
            configure?.Invoke(s);
        });
        return (code, writer.ToString());
    }

    private static string StatusOf(string output, string name) =>
        Regex.Match(output, $@"^(ok|off|warn|FAIL)\s+{Regex.Escape(name)}\s", RegexOptions.Multiline, TimeSpan.FromSeconds(1)).Groups[1].Value;

    [Fact]
    public async Task Doctor_prints_one_line_per_integration_and_exits_0_when_the_required_ones_work()
    {
        await Seed.ItemAsync(pg, 1, "A post", TestVectors.OneHot(0));
        await SetFeedStateAsync("last_fetched_at = now()");

        var (code, output) = await DoctorAsync(Config(), SetupStubs.Services());

        Assert.Equal(0, code);
        foreach (var name in new[] { "database", "llm chat", "llm embeddings", "telegram", "feed source", "profile" })
        {
            Assert.Equal("ok", StatusOf(output, name));
        }

        foreach (var name in new[] { "miniflux", "plane", "karakeep", "github", "hindsight", "release watch" })
        {
            Assert.Equal("off", StatusOf(output, name));
        }

        Assert.Contains("bot @my_feed_bot, owner id 42", output, StringComparison.Ordinal);
        Assert.Contains("built-in reader: 1 feed, 1 fetched", output, StringComparison.Ordinal);
        Assert.EndsWith("Everything required works." + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_exits_1_and_says_what_to_fix_when_a_required_item_fails()
    {
        var stub = SetupStubs.Services(request => request.RequestUri!.AbsolutePath.Contains("/chat/completions", StringComparison.Ordinal) || request.RequestUri.AbsolutePath.Contains("/embeddings", StringComparison.Ordinal)
            ? StubHandler.Json($$$"""{"error":{"message":"Incorrect API key provided: {{{SetupStubs.ApiKey}}}"}}""", HttpStatusCode.Unauthorized)
            : null);

        var (code, output) = await DoctorAsync(Config(("FeedEater:Telegram:AllowedUserId", "0")), stub);

        Assert.Equal(1, code);
        Assert.Equal("ok", StatusOf(output, "database"));
        Assert.Equal("FAIL", StatusOf(output, "llm chat"));
        Assert.Equal("FAIL", StatusOf(output, "llm embeddings"));
        Assert.Equal("FAIL", StatusOf(output, "telegram"));
        Assert.Equal("FAIL", StatusOf(output, "feed source"));
        Assert.Contains("FeedEater__Llm__ApiKey", output, StringComparison.Ordinal);
        Assert.Contains("Send /start to @my_feed_bot", output, StringComparison.Ordinal);
        Assert.Contains("or add one at /ui/sources", output, StringComparison.Ordinal);
        Assert.EndsWith("4 required checks failing: llm chat, llm embeddings, telegram, feed source." + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_exits_0_when_only_an_optional_service_fails_and_shows_it_as_FAIL()
    {
        await Seed.ItemAsync(pg, 1, "A post", TestVectors.OneHot(0));
        var config = Config(("FeedEater:Plane:BaseUrl", "http://plane.example/"), ("FeedEater:Plane:Token", "plane-secret-key"), ("FeedEater:Plane:Workspace", "ws"),
            ("FeedEater:Karakeep:BaseUrl", "http://karakeep.example/"), ("FeedEater:Karakeep:Token", "kk-karakeep-key"));
        var stub = SetupStubs.Services(request => request.RequestUri!.Host == "plane.example" ? StubHandler.Json("{}", HttpStatusCode.Unauthorized) : null);

        var (code, output) = await DoctorAsync(config, stub);

        Assert.Equal(0, code);
        Assert.Equal("FAIL", StatusOf(output, "plane"));
        Assert.Equal("ok", StatusOf(output, "karakeep"));
        Assert.Contains("FeedEater__Plane__Token", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_never_prints_a_secret_even_when_a_service_echoes_it()
    {
        var stub = SetupStubs.Services(request => request.RequestUri!.AbsolutePath.EndsWith("/getMe", StringComparison.Ordinal)
            ? StubHandler.Json($$"""{"ok":false,"description":"bad token {{SetupStubs.BotToken}} for {{SetupStubs.ApiKey}}"}""")
            : request.RequestUri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? StubHandler.Json($$"""{"error":"key {{SetupStubs.ApiKey}} mcp-secret-token"}""", HttpStatusCode.Unauthorized)
                : null);

        var (_, output) = await DoctorAsync(Config(), stub);

        foreach (var secret in new[] { SetupStubs.ApiKey, SetupStubs.BotToken, "mcp-secret-token", new NpgsqlConnectionStringBuilder(pg.ConnectionString).Password! })
        {
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        }

        Assert.Contains("***", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_runs_no_background_job_and_leaves_no_usage_rows()
    {
        await Seed.ItemAsync(pg, 1, "A post", TestVectors.OneHot(0));
        var marker = new StartMarker();

        await DoctorAsync(Config(), SetupStubs.Services(), configure: s => s.AddSingleton<IHostedService>(marker));

        Assert.False(marker.Started);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from llm_usage"));
    }

    private sealed class StartMarker : IHostedService
    {
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.FromResult(Started = true);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Doctor_without_a_connection_string_fails_the_database_line_and_exits_1()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeedEater:Llm:ApiKey"] = "x" }).Build();

        var (code, output) = await DoctorAsync(config, SetupStubs.Services());

        Assert.Equal(1, code);
        Assert.Equal("FAIL", StatusOf(output, "database"));
        Assert.Contains("ConnectionStrings__FeedEater is not set", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_with_a_connection_string_that_does_not_parse_fails_without_printing_it()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:FeedEater"] = "garbage;Password=topsecretpw" }).Build();

        var (code, output) = await DoctorAsync(config, SetupStubs.Services());

        Assert.Equal(1, code);
        Assert.Contains("not a valid Postgres connection string", output, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecretpw", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_print_config_still_prints_the_effective_config_with_secrets_masked()
    {
        var (code, output) = await DoctorAsync(Config(), SetupStubs.Services(), ["--print-config"]);

        Assert.Equal(0, code);
        Assert.Contains("FeedEater:Llm:ApiKey=***", output, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupStubs.ApiKey, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_with_an_unknown_argument_prints_usage_and_exits_2()
    {
        var (code, output) = await DoctorAsync(Config(), SetupStubs.Services(), ["--nope"]);

        Assert.Equal(2, code);
        Assert.Equal("", output);
    }
}
