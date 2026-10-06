using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Sources;
using FeedEater.Telegram;
using FeedEater.Ui;

namespace FeedEater.Tests;

/// <summary>The import-opml entry point, run for real against the test database. Environment variables are process-wide, so this stays in the Postgres collection.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ImportOpmlCommandTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"feeds-{Guid.NewGuid():N}.opml");

    public Task InitializeAsync() => pg.ResetAsync();

    public Task DisposeAsync()
    {
        File.Delete(_file);
        return Task.CompletedTask;
    }

    private async Task<(int Exit, string Out, string Error)> RunAsync(string? kind, params string[] args)
    {
        var (oldOut, oldError) = (Console.Out, Console.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        Environment.SetEnvironmentVariable("ConnectionStrings__FeedEater", pg.ConnectionString);
        Environment.SetEnvironmentVariable("FeedEater__Source__Kind", kind);
        try
        {
            return (await ImportOpmlCommand.RunAsync(args), output.ToString(), error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__FeedEater", null);
            Environment.SetEnvironmentVariable("FeedEater__Source__Kind", null);
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    private async Task<List<(string Title, string? Category, string Url)>> FeedsAsync()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.QueryAsync<(string, string?, string)>("select title, category, feed_url from feeds order by title")).ToList();
    }

    [Fact]
    public async Task Imports_the_file_with_folders_as_categories_and_says_how_many_were_new()
    {
        await File.WriteAllTextAsync(_file, """<opml version="2.0"><body><outline text="Tech"><outline text="Blog A" xmlUrl="https://a.example/rss"/></outline><outline text="Loose" xmlUrl="https://b.example/rss"/></body></opml>""");

        var first = await RunAsync(null, _file);
        var second = await RunAsync(null, _file);

        Assert.Equal(0, first.Exit);
        Assert.Contains("Imported 2 feeds, 0 were already there.", first.Out, StringComparison.Ordinal);
        Assert.Contains("Imported 0 feeds, 2 were already there.", second.Out, StringComparison.Ordinal);
        Assert.Equal([("Blog A", "Tech", "https://a.example/rss"), ("Loose", null, "https://b.example/rss")], await FeedsAsync());
    }

    [Fact]
    public async Task An_unknown_Source_Kind_exits_2_and_stores_nothing()
    {
        await File.WriteAllTextAsync(_file, """<opml version="2.0"><body><outline text="Loose" xmlUrl="https://b.example/rss"/></body></opml>""");

        var result = await RunAsync("minifux", _file);

        Assert.Equal(2, result.Exit);
        Assert.Contains("must be builtin or miniflux", result.Error, StringComparison.Ordinal);
        Assert.Empty(await FeedsAsync());
    }

    [Fact]
    public async Task A_file_that_is_not_opml_exits_1_and_stores_nothing()
    {
        await File.WriteAllTextAsync(_file, "<html>nope");

        var run = await RunAsync(null, _file);

        Assert.Equal(1, run.Exit);
        Assert.Contains("not valid OPML", run.Error, StringComparison.Ordinal);
        Assert.Empty(await FeedsAsync());
    }

    [Fact]
    public async Task Missing_arguments_and_missing_files_exit_2()
    {
        Assert.Equal(2, (await RunAsync(null)).Exit);
        Assert.Equal(2, (await RunAsync(null, "a", "b")).Exit);
        var missing = await RunAsync(null, _file);
        Assert.Equal(2, missing.Exit);
        Assert.Contains("No such file", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_miniflux_mode_it_refuses_and_stores_nothing()
    {
        await File.WriteAllTextAsync(_file, """<opml><body><outline text="A" xmlUrl="https://a.example/rss"/></body></opml>""");

        var run = await RunAsync("miniflux", _file);

        Assert.Equal(2, run.Exit);
        Assert.Contains("Kind=builtin", run.Error, StringComparison.Ordinal);
        Assert.Empty(await FeedsAsync());
    }
}

/// <summary>What <c>Source:Kind</c> registers, and that the optional pieces the Miniflux paths ignore resolve in both modes.</summary>
public sealed class SourceCompositionTests
{
    private static IHost Build(string? kind, bool runJobs = true)
    {
        var builder = Host.CreateApplicationBuilder();
        var config = new Dictionary<string, string?>
        {
            ["ConnectionStrings:FeedEater"] = "Host=localhost;Database=unused",
            ["FeedEater:RunJobs"] = runJobs.ToString(),
            ["FeedEater:Telegram:Token"] = "123:test",
        };
        if (kind is not null)
        {
            config["FeedEater:Source:Kind"] = kind;
        }

        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddFeedEater(builder.Configuration);
        return builder.Build();
    }

    [Fact]
    public void Builtin_is_the_default_and_wires_the_reader_into_the_loop_the_digest_and_the_ui()
    {
        using var host = Build(null);

        Assert.True(host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FeedEaterOptions>>().Value.Source.IsBuiltin);
        Assert.NotNull(host.Services.GetService<FeedPoller>());
        Assert.NotNull(host.Services.GetService<ArticleText>());
        Assert.NotNull(host.Services.GetService<FeedManager>());
        Assert.NotNull(host.Services.GetService<FeedsCommand>());
        Assert.NotNull(host.Services.GetService<SourcesUi>());
        Assert.NotNull(host.Services.GetRequiredService<DigestRun>());
        Assert.NotNull(host.Services.GetRequiredService<CommandHandler>());
        Assert.NotNull(host.Services.GetRequiredService<UiHandlers>());
        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is Ingestor);
    }

    [Theory]
    [InlineData("miniflux")]
    [InlineData("Miniflux")]
    public void Miniflux_mode_registers_none_of_the_reader_and_everything_still_resolves(string kind)
    {
        using var host = Build(kind);

        Assert.Null(host.Services.GetService<FeedPoller>());
        Assert.Null(host.Services.GetService<ArticleText>());
        Assert.Null(host.Services.GetService<FeedManager>());
        Assert.Null(host.Services.GetService<FeedsCommand>());
        Assert.Null(host.Services.GetService<SourcesUi>());
        Assert.NotNull(host.Services.GetRequiredService<DigestRun>());
        Assert.NotNull(host.Services.GetRequiredService<CommandHandler>());
        Assert.NotNull(host.Services.GetRequiredService<UiHandlers>());
        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is Ingestor);
    }

    [Theory]
    [InlineData("builtin", true)]
    [InlineData("BuiltIn", true)]
    [InlineData("miniflux", true)]
    [InlineData("MINIFLUX", true)]
    [InlineData("minifux", false)]
    [InlineData("", false)]
    [InlineData("built-in", false)]
    public void Kind_must_be_builtin_or_miniflux_in_any_case(string kind, bool valid) =>
        Assert.Equal(valid, new SourceOptions { Kind = kind }.KindProblem is null);

    [Fact]
    public void The_app_refuses_to_start_with_an_unknown_Kind_and_says_why()
    {
        using var app = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:FeedEater", "Host=localhost;Database=unused")
            .UseSetting("FeedEater:RunJobs", "false")
            .UseSetting("FeedEater:Source:Kind", "minifux"));

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains("Source:Kind is \"minifux\"; it must be builtin or miniflux", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_defaults_are_the_documented_ones()
    {
        var o = new SourceOptions();

        Assert.Equal("builtin", o.Kind);
        Assert.Empty(o.AllowedHosts);
        Assert.Equal(14, o.BackfillDays);
        Assert.Equal(TimeSpan.FromMinutes(30), o.FeedInterval);
        Assert.Equal(TimeSpan.FromHours(24), o.MaxBackoff);
    }
}
