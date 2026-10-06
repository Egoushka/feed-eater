using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Memory;
using FeedEater.Plane;
using FeedEater.Setup;
using FeedEater.Signals;
using FeedEater.Telegram;
using FeedEater.Watch;

namespace FeedEater.Tests;

public sealed class SetupChecksTests
{
    private const string Key = "sk-very-secret";

    private static FeedEaterOptions Opts(Action<FeedEaterOptions>? set = null)
    {
        var o = new FeedEaterOptions { Llm = new LlmOptions { ApiKey = Key, BaseUrl = "https://llm.example/v1/" } };
        set?.Invoke(o);
        return o;
    }

    private static IConfiguration Config() => new ConfigurationBuilder().Build();

    private static IServiceProvider Services(Action<IServiceCollection> add)
    {
        var services = new ServiceCollection();
        add(services);
        return services.BuildServiceProvider();
    }

    private static async Task<CheckRow> RunAsync(IIntegrationCheck check, TimeSpan? timeout = null) =>
        (await new CheckRunner([check], Options.Create(Opts()), Config(), timeout).RunAsync(default)).Single();

    private static async Task<CheckRow> RunAsync(Func<IServiceProvider, IOptions<FeedEaterOptions>, IIntegrationCheck> make, FeedEaterOptions options, Action<IServiceCollection> add)
    {
        var o = Options.Create(options);
        var check = make(Services(add), o);
        return (await new CheckRunner([check], o, Config()).RunAsync(default)).Single();
    }

    private static string Chat(string content = "ok") =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } }, usage = new { prompt_tokens = 3, completion_tokens = 1 } });

    private static LiteLlmClient Llm(StubHandler stub, FeedEaterOptions o) => new(stub.Client("https://llm.example/v1/"), new DiscardUsage(), Options.Create(o));

    private static StubHandler LlmStub(Func<string, HttpResponseMessage>? chat = null, Func<string, HttpResponseMessage>? embed = null) => new((request, body) =>
        request.RequestUri!.AbsolutePath.EndsWith("embeddings", StringComparison.Ordinal)
            ? (embed ?? (_ => StubHandler.Json(TestVectors.EmbeddingResponse(1))))(body)
            : (chat ?? (_ => StubHandler.Json(Chat())))(body));

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}") => StubHandler.Json(body, code);

    // llm chat

    [Fact]
    public async Task Llm_chat_is_ok_when_both_models_answer()
    {
        var o = Opts();

        var row = await RunAsync((s, opts) => new LlmChatCheck(s, opts), o, s => s.AddSingleton(Llm(LlmStub(), o)));

        Assert.Equal(CheckStatus.Ok, row.Result.Status);
        Assert.True(row.Required);
        Assert.Contains("llm.example answered for gpt-4.1-nano and gpt-4.1-mini", row.Result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Llm_chat_fails_with_the_key_to_fix_when_the_key_is_refused()
    {
        var o = Opts();
        var stub = LlmStub(chat: _ => Status(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided"}}"""));

        var row = await RunAsync((s, opts) => new LlmChatCheck(s, opts), o, s => s.AddSingleton(Llm(stub, o)));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Equal("gpt-4.1-nano and gpt-4.1-mini: HTTP 401: Incorrect API key provided", row.Result.Detail);
        Assert.Contains("FeedEater__Llm__ApiKey", row.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Llm_chat_names_the_model_the_endpoint_does_not_know()
    {
        var o = Opts();
        var stub = LlmStub(chat: body => body.Contains("gpt-4.1-mini", StringComparison.Ordinal) ? Status(HttpStatusCode.NotFound, """{"error":"model not found"}""") : StubHandler.Json(Chat()));

        var row = await RunAsync((s, opts) => new LlmChatCheck(s, opts), o, s => s.AddSingleton(Llm(stub, o)));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Contains("gpt-4.1-mini", row.Result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("gpt-4.1-nano:", row.Result.Detail, StringComparison.Ordinal);
        Assert.Contains("FeedEater__Llm__ReadModel", row.Result.Fix, StringComparison.Ordinal);
        Assert.Contains("version path", row.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Llm_chat_reports_a_spent_gateway_budget()
    {
        var o = Opts();
        var stub = LlmStub(chat: _ => Status(HttpStatusCode.Unauthorized, """{"error":{"message":"ExceededBudget"}}"""));

        var row = await RunAsync((s, opts) => new LlmChatCheck(s, opts), o, s => s.AddSingleton(Llm(stub, o)));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Contains("budget", row.Result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_llm_url_fails_the_check_instead_of_the_whole_run()
    {
        var o = Opts(x => x.Llm.BaseUrl = "not a url");

        var row = await RunAsync((s, opts) => new LlmChatCheck(s, opts), o, s => s.AddHttpClient<LiteLlmClient>((sp, http) => http.BaseAddress = new Uri(o.Llm.BaseUrl)).Services
            .AddSingleton<IUsageSink, DiscardUsage>().AddSingleton(Options.Create(o)));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Contains("FeedEater__Llm__BaseUrl", row.Result.Fix, StringComparison.Ordinal);
    }

    // llm embeddings

    [Fact]
    public async Task Embeddings_are_ok_at_1536_dimensions()
    {
        var o = Opts();

        var row = await RunAsync((s, opts) => new LlmEmbeddingCheck(s, opts), o, s => s.AddSingleton(Llm(LlmStub(), o)));

        Assert.Equal(CheckStatus.Ok, row.Result.Status);
        Assert.Contains("text-embedding-3-small returns 1536 dimensions", row.Result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embeddings_of_another_size_fail_and_the_fix_says_the_column_is_fixed()
    {
        var o = Opts(x => x.Llm.EmbedModel = "big-model");
        var stub = LlmStub(embed: _ => StubHandler.Json(TestVectors.EmbeddingResponse(1, _ => { var v = new float[3072]; v[0] = 1; return v; })));

        var row = await RunAsync((s, opts) => new LlmEmbeddingCheck(s, opts), o, s => s.AddSingleton(Llm(stub, o)));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.True(row.Required);
        Assert.Contains("big-model returns 3072 dimensions", row.Result.Detail, StringComparison.Ordinal);
        Assert.Contains("vector(1536)", row.Result.Detail, StringComparison.Ordinal);
        Assert.Contains("fixed", row.Result.Fix, StringComparison.Ordinal);
        Assert.Contains("FeedEater__Llm__EmbedModel", row.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embeddings_fail_with_the_embed_model_to_fix_when_the_endpoint_has_no_such_model()
    {
        var o = Opts();
        var stub = LlmStub(embed: _ => Status(HttpStatusCode.NotFound));

        var row = await RunAsync((s, opts) => new LlmEmbeddingCheck(s, opts), o, s => s.AddSingleton(Llm(stub, o)));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Contains("FeedEater__Llm__EmbedModel", row.Result.Fix, StringComparison.Ordinal);
    }

    // telegram

    private static TelegramClient Bot(string response) => new(new StubHandler((_, _) => StubHandler.Json(response)).Client("http://tg/botT/"));

    [Fact]
    public async Task Telegram_is_ok_with_a_working_token_and_an_owner()
    {
        var o = Opts(x => x.Telegram = new TelegramOptions { Token = "123:abc", AllowedUserId = 42 });

        var row = await RunAsync((s, opts) => new TelegramCheck(s, opts), o, s => s.AddSingleton(Bot("""{"ok":true,"result":{"username":"my_feed_bot"}}""")));

        Assert.Equal(CheckStatus.Ok, row.Result.Status);
        Assert.Equal("bot @my_feed_bot, owner id 42", row.Result.Detail);
    }

    [Fact]
    public async Task Telegram_with_owner_id_0_is_a_failure_that_points_at_start()
    {
        var o = Opts(x => x.Telegram = new TelegramOptions { Token = "123:abc" });

        var row = await RunAsync((s, opts) => new TelegramCheck(s, opts), o, s => s.AddSingleton(Bot("""{"ok":true,"result":{"username":"my_feed_bot"}}""")));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.True(row.Required);
        Assert.Contains("/start", row.Result.Fix, StringComparison.Ordinal);
        Assert.Contains("@my_feed_bot", row.Result.Fix, StringComparison.Ordinal);
        Assert.Contains("FeedEater__Telegram__AllowedUserId", row.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telegram_without_a_token_fails_and_names_BotFather()
    {
        var row = await RunAsync((s, opts) => new TelegramCheck(s, opts), Opts(), s => { });

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Contains("@BotFather", row.Result.Fix, StringComparison.Ordinal);
        Assert.Contains("FeedEater__Telegram__Token", row.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telegram_with_a_rejected_token_fails()
    {
        var o = Opts(x => x.Telegram = new TelegramOptions { Token = "123:abc", AllowedUserId = 42 });

        var row = await RunAsync((s, opts) => new TelegramCheck(s, opts), o, s => s.AddSingleton(Bot("""{"ok":false,"error_code":401,"description":"Unauthorized"}""")));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Contains("Unauthorized", row.Result.Detail, StringComparison.Ordinal);
        Assert.Contains("copy it again", row.Result.Fix, StringComparison.Ordinal);
    }

    // profile

    [Fact]
    public async Task A_valid_profile_is_ok_and_the_example_is_a_warning_not_a_failure()
    {
        var valid = await RunAsync(new ProfileCheck(Options.Create(Opts(x => x.ProfilePath = "Fixtures/profile.json"))));
        var example = await RunAsync(new ProfileCheck(Options.Create(Opts(x => x.ProfilePath = "/nope/profile.json"))));

        Assert.Equal(CheckStatus.Ok, valid.Result.Status);
        Assert.Contains("3 projects, 2 topics", valid.Result.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Warn, example.Result.Status);
        Assert.False(example.Required);
        Assert.Contains("example interests in use", example.Result.Detail, StringComparison.Ordinal);
        Assert.Contains("/nope/profile.json", example.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_profile_file_that_is_invalid_fails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{"about":"x","projects":[],"topics":[]}""");

        var row = await RunAsync(new ProfileCheck(Options.Create(Opts(x => x.ProfilePath = path))));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Contains("no projects or topics", row.Result.Detail, StringComparison.Ordinal);
        Assert.Contains("profile.example.json", row.Result.Fix, StringComparison.Ordinal);
    }

    // feed source

    private sealed class FakeProbe(CheckResult result, string name = "built-in reader") : IFeedSourceProbe
    {
        public string Name => name;
        public Task<CheckResult> CheckAsync(CancellationToken ct) => Task.FromResult(result);
    }

    [Fact]
    public async Task The_feed_source_line_shows_what_the_registered_probe_says()
    {
        var ok = await RunAsync(new FeedSourceCheck(Services(s => s.AddSingleton<IFeedSourceProbe>(new FakeProbe(CheckResult.Ok("12 feeds"))))));
        var fail = await RunAsync(new FeedSourceCheck(Services(s => s.AddSingleton<IFeedSourceProbe>(new FakeProbe(CheckResult.Fail("no feeds", "Add one."), "Miniflux")))));

        Assert.Equal("built-in reader: 12 feeds", ok.Result.Detail);
        Assert.True(ok.Required);
        Assert.Equal(CheckStatus.Fail, fail.Result.Status);
        Assert.Equal("Miniflux: no feeds", fail.Result.Detail);
        Assert.Equal("Add one.", fail.Result.Fix);
    }

    [Fact]
    public async Task A_probe_that_throws_fails_the_line_without_throwing()
    {
        var row = await RunAsync(new FeedSourceCheck(Services(_ => { })));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
    }

    // optional integrations

    [Fact]
    public async Task Every_optional_integration_is_off_until_it_is_configured()
    {
        IIntegrationCheck[] checks =
        [
            new MinifluxCheck(Services(_ => { }), Options.Create(Opts())), new PlaneCheck(Services(_ => { }), Options.Create(Opts())),
            new KarakeepCheck(Services(_ => { }), Options.Create(Opts())), new GitHubCheck(Services(_ => { }), Options.Create(Opts())),
            new HindsightCheck(Services(_ => { }), Options.Create(Opts())), new WatchCheck(Services(_ => { }), Options.Create(Opts())),
        ];

        var rows = await new CheckRunner(checks, Options.Create(Opts()), Config()).RunAsync(default);

        Assert.Equal(["miniflux", "plane", "karakeep", "github", "hindsight", "release watch"], rows.Select(r => r.Name));
        Assert.All(rows, r =>
        {
            Assert.Equal(CheckStatus.Off, r.Result.Status);
            Assert.Equal("not configured", r.Result.Detail);
            Assert.False(r.Required);
        });
    }

    private static StubHandler Svc(HttpStatusCode status, string body = "{}") => new((_, _) => StubHandler.Json(body, status));

    [Fact]
    public async Task Plane_is_ok_when_the_fallback_project_exists_and_fails_when_it_does_not()
    {
        var o = Opts(x => x.Plane = new PlaneOptions { BaseUrl = "http://plane/", Token = "pk-plane-key", Workspace = "ws", FallbackProject = "FEED" });
        var plane = Svc(HttpStatusCode.OK, """{"results":[{"id":"p1","identifier":"FEED"}]}""");
        var missing = Svc(HttpStatusCode.OK, """{"results":[{"id":"p2","identifier":"OTHER"}]}""");

        var ok = await RunAsync((s, opts) => new PlaneCheck(s, opts), o, s => s.AddSingleton(new PlaneClient(plane.Client("http://plane/"), Options.Create(o))));
        var fail = await RunAsync((s, opts) => new PlaneCheck(s, opts), o, s => s.AddSingleton(new PlaneClient(missing.Client("http://plane/"), Options.Create(o))));

        Assert.Equal(CheckStatus.Ok, ok.Result.Status);
        Assert.Equal("workspace ws, project FEED found", ok.Result.Detail);
        Assert.Equal(CheckStatus.Fail, fail.Result.Status);
        Assert.Contains("FEED does not exist", fail.Result.Detail, StringComparison.Ordinal);
        Assert.Contains("FeedEater__Plane__FallbackProject", fail.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plane_without_a_fallback_project_counts_the_projects_and_a_refused_key_fails()
    {
        var o = Opts(x => x.Plane = new PlaneOptions { BaseUrl = "http://plane/", Token = "pk-plane-key", Workspace = "ws" });
        var plane = Svc(HttpStatusCode.OK, """{"results":[{"id":"p1","identifier":"A"},{"id":"p2","identifier":"B"}]}""");
        var refused = Svc(HttpStatusCode.Unauthorized);

        var ok = await RunAsync((s, opts) => new PlaneCheck(s, opts), o, s => s.AddSingleton(new PlaneClient(plane.Client("http://plane/"), Options.Create(o))));
        var fail = await RunAsync((s, opts) => new PlaneCheck(s, opts), o, s => s.AddSingleton(new PlaneClient(refused.Client("http://plane/"), Options.Create(o))));

        Assert.Equal("workspace ws, 2 projects", ok.Result.Detail);
        Assert.Equal(CheckStatus.Fail, fail.Result.Status);
        Assert.Contains("FeedEater__Plane__Token", fail.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Karakeep_is_ok_when_it_answers_and_fails_when_the_key_is_refused()
    {
        var o = Opts(x => x.Karakeep = new KarakeepOptions { BaseUrl = "http://karakeep.example/", Token = "kk-karakeep-key" });
        var up = Svc(HttpStatusCode.OK, """{"bookmarks":[]}""");
        var refused = Svc(HttpStatusCode.Unauthorized);

        var ok = await RunAsync((s, opts) => new KarakeepCheck(s, opts), o, s => s.AddSingleton(new KarakeepClient(up.Client("http://karakeep.example/"))));
        var fail = await RunAsync((s, opts) => new KarakeepCheck(s, opts), o, s => s.AddSingleton(new KarakeepClient(refused.Client("http://karakeep.example/"))));

        Assert.Equal("karakeep.example answers", ok.Result.Detail);
        Assert.Equal(CheckStatus.Fail, fail.Result.Status);
        Assert.Contains("FeedEater__Karakeep__Token", fail.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_is_ok_for_a_known_user_and_fails_for_an_unknown_one()
    {
        var o = Opts(x => x.GitHub = new GitHubOptions { User = "octocat" });
        var up = Svc(HttpStatusCode.OK);
        var unknown = Svc(HttpStatusCode.NotFound);

        var ok = await RunAsync((s, opts) => new GitHubCheck(s, opts), o, s => s.AddSingleton(new GitHubStarsClient(up.Client("http://github/"), Options.Create(o))));
        var fail = await RunAsync((s, opts) => new GitHubCheck(s, opts), o, s => s.AddSingleton(new GitHubStarsClient(unknown.Client("http://github/"), Options.Create(o))));

        Assert.Equal("user octocat found", ok.Result.Detail);
        Assert.Equal(CheckStatus.Fail, fail.Result.Status);
        Assert.Contains("FeedEater__GitHub__User", fail.Result.Fix, StringComparison.Ordinal);
        Assert.EndsWith("/users/octocat", up.Calls.Single().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hindsight_is_ok_when_it_lists_banks_and_fails_when_it_is_unreachable()
    {
        var o = Opts(x => x.Hindsight = new HindsightOptions { BaseUrl = "http://hindsight.example/" });
        var up = Svc(HttpStatusCode.OK, "[]");
        var down = new StubHandler((_, _) => throw new HttpRequestException("Connection refused (hindsight.example:80)"));

        var ok = await RunAsync((s, opts) => new HindsightCheck(s, opts), o, s => s.AddSingleton(new HindsightClient(up.Client("http://hindsight.example/"))));
        var fail = await RunAsync((s, opts) => new HindsightCheck(s, opts), o, s => s.AddSingleton(new HindsightClient(down.Client("http://hindsight.example/"))));

        Assert.Equal("hindsight.example answers", ok.Result.Detail);
        Assert.Equal(CheckStatus.Fail, fail.Result.Status);
        Assert.Contains("Connection refused", fail.Result.Detail, StringComparison.Ordinal);
        Assert.Contains("FeedEater__Hindsight__BaseUrl", fail.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Miniflux_is_ok_when_it_answers_and_fails_when_the_key_is_refused()
    {
        var o = Opts(x => x.Miniflux = new MinifluxOptions { BaseUrl = "http://miniflux.example/", Token = "mf-miniflux-key" });
        var up = Svc(HttpStatusCode.OK, """{"total":0,"entries":[]}""");
        var refused = new StubHandler((_, _) => throw new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).", null, HttpStatusCode.Unauthorized));

        var ok = await RunAsync((s, opts) => new MinifluxCheck(s, opts), o, s => s.AddSingleton(new MinifluxClient(up.Client("http://miniflux.example/"))));
        var fail = await RunAsync((s, opts) => new MinifluxCheck(s, opts), o, s => s.AddSingleton(new MinifluxClient(refused.Client("http://miniflux.example/"))));

        Assert.Equal(CheckStatus.Ok, ok.Result.Status);
        Assert.Equal(CheckStatus.Fail, fail.Result.Status);
        Assert.Contains("FeedEater__Miniflux__Token", fail.Result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_watch_is_ok_with_a_list_that_maps_and_fails_with_one_that_does_not_load()
    {
        var example = Path.Combine(AppContext.BaseDirectory, "config", "watch.example.json");
        var good = Opts(x => x.Watch = new WatchOptions { FallbackPath = example });
        var bad = Opts(x => x.Watch = new WatchOptions { FallbackPath = "/nope/watch.json" });
        void Add(IServiceCollection s, FeedEaterOptions o) =>
            s.AddSingleton(new WatchSource(new HttpClient(), Options.Create(o), NullLogger<WatchSource>.Instance));

        var ok = await RunAsync((s, opts) => new WatchCheck(s, opts), good, s => Add(s, good));
        var fail = await RunAsync((s, opts) => new WatchCheck(s, opts), bad, s => Add(s, bad));

        Assert.Equal(CheckStatus.Ok, ok.Result.Status);
        Assert.EndsWith("products watched", ok.Result.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Fail, fail.Result.Status);
        Assert.Contains("FeedEater__Watch__FallbackPath", fail.Result.Fix, StringComparison.Ordinal);
    }

    // the runner

    private sealed class FakeCheck(string name, Func<CancellationToken, Task<CheckResult>> run, bool required = false) : IIntegrationCheck
    {
        public string Name => name;
        public bool Required => required;
        public Task<CheckResult> RunAsync(CancellationToken ct) => run(ct);
    }

    [Fact]
    public async Task A_check_that_never_answers_is_cut_off_with_a_failure()
    {
        var hang = new FakeCheck("slow", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return CheckResult.Ok("never");
        }, required: true);

        var row = await RunAsync(hang, TimeSpan.FromMilliseconds(200));

        Assert.Equal(CheckStatus.Fail, row.Result.Status);
        Assert.Contains("no answer within 0.2 s", row.Result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_limit_is_ten_seconds() => Assert.Equal(TimeSpan.FromSeconds(10), CheckRunner.Timeout);

    [Fact]
    public async Task A_check_that_throws_is_a_failure_and_the_other_checks_still_run()
    {
        IIntegrationCheck[] checks =
        [
            new FakeCheck("broken", _ => throw new InvalidOperationException("boom"), required: true),
            new FakeCheck("fine", _ => Task.FromResult(CheckResult.Ok("works"))),
        ];

        var rows = await new CheckRunner(checks, Options.Create(Opts()), Config()).RunAsync(default);

        Assert.Equal(["broken", "fine"], rows.Select(r => r.Name));
        Assert.Equal(CheckStatus.Fail, rows[0].Result.Status);
        Assert.Equal("boom", rows[0].Result.Detail);
        Assert.Equal(CheckStatus.Ok, rows[1].Result.Status);
    }

    [Fact]
    public async Task What_a_check_says_is_one_line_and_never_holds_a_secret()
    {
        var options = Opts(x =>
        {
            x.Telegram = new TelegramOptions { Token = "123456:telegram-secret" };
            x.Plane = new PlaneOptions { Token = "plane-secret-key" };
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:Token"] = "mcp-secret-token",
            ["ConnectionStrings:FeedEater"] = "Host=db;Database=feed;Username=feed;Password=hunter2hunter2",
        }).Build();
        var leaky = new FakeCheck("leaky", _ => Task.FromResult(CheckResult.Fail(
            $"401 for key {Key}\nand token 123456:telegram-secret, plane-secret-key, mcp-secret-token, hunter2hunter2",
            $"Fix {Key}.\n\n   Again {Key}")), required: true);

        var row = (await new CheckRunner([leaky], Options.Create(options), config).RunAsync(default)).Single();

        var text = row.Result.Detail + "|" + row.Result.Fix;
        foreach (var secret in new[] { Key, "123456:telegram-secret", "plane-secret-key", "mcp-secret-token", "hunter2hunter2" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        Assert.Equal("401 for key *** and token ***, ***, ***, ***", row.Result.Detail);
        Assert.Equal("Fix ***. Again ***", row.Result.Fix);
    }

    [Fact]
    public async Task A_long_message_is_cut()
    {
        var row = await RunAsync(new FakeCheck("wordy", _ => Task.FromResult(CheckResult.Fail(new string('x', 2000), "Fix."))));

        Assert.Equal(300, row.Result.Detail.Length);
        Assert.EndsWith("…", row.Result.Detail, StringComparison.Ordinal);
    }

    // the report

    [Fact]
    public void Each_state_prints_as_ok_off_warn_or_FAIL_with_the_fix_on_the_same_line()
    {
        CheckRow[] rows =
        [
            new("database", true, CheckResult.Ok("Postgres 18.0, pgvector 0.8.1")),
            new("llm chat", true, CheckResult.Fail("401 from llm.example", "Check FeedEater__Llm__ApiKey.")),
            new("profile", false, CheckResult.Warn("example interests in use", "Copy profile.example.json.")),
            new("plane", false, CheckResult.Off("not configured")),
        ];

        var lines = DoctorReport.Lines(rows).ToList();

        Assert.Equal("ok    database  Postgres 18.0, pgvector 0.8.1", lines[0]);
        Assert.Equal("FAIL  llm chat  401 from llm.example - fix: Check FeedEater__Llm__ApiKey.", lines[1]);
        Assert.Equal("warn  profile   example interests in use - fix: Copy profile.example.json.", lines[2]);
        Assert.Equal("off   plane     not configured", lines[3]);
        Assert.Equal("1 required check failing: llm chat.", lines[^1]);
    }

    [Fact]
    public void Only_a_failing_required_check_makes_doctor_fail()
    {
        CheckRow Row(bool required, CheckStatus status) => new("x", required, new CheckResult(status, "d"));

        Assert.True(DoctorReport.Failed([Row(true, CheckStatus.Fail)]));
        Assert.False(DoctorReport.Failed([Row(false, CheckStatus.Fail)]));
        Assert.False(DoctorReport.Failed([Row(true, CheckStatus.Warn), Row(true, CheckStatus.Off), Row(true, CheckStatus.Ok)]));
        Assert.Equal("Everything required works.", DoctorReport.Lines([Row(true, CheckStatus.Ok)]).Last());
    }
}
