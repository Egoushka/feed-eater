using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using FeedEater.PriorArt;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PriorArtCommandTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(int Code, string Out, string Err)> RunAsync(WebApplicationFactory<Program> app, string stdin, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await PriorArtCommand.RunAsync(args, app.Services, new StringReader(stdin), output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Import_from_stdin_picks_the_format_from_the_ref()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:FeedEater", pg.ConnectionString)
            .UseSetting("Mcp:Token", "t")
            .UseSetting("FeedEater:RunJobs", "false")
            .UseSetting("FeedEater:Llm:BaseUrl", "http://127.0.0.1:9/"));

        var audit = await RunAsync(app, "| Candidate | What it does |\n|---|---|\n| [m/tool](https://github.com/m/tool) | Tests things |\n", "import", "-", "--ref", "audits/2026-10-29-a.md");
        var csv = await RunAsync(app, "repo,stars,pushed,license\nm/tool,158,2026-09-14,MIT\n", "import", "-", "--ref", "audits/2026-10-29-a.csv");

        Assert.Equal(0, audit.Code);
        Assert.Equal(0, csv.Code);
        Assert.StartsWith("snapshot audits/2026-10-29-a.csv: 3 facts", csv.Out, StringComparison.Ordinal);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Equal(["audit", "snapshot"], (await c.QueryAsync<string>("select source_kind from facts.episodes order by 1")).ToArray());
    }

    [Fact]
    public async Task Import_with_a_bad_option_explains_and_exits_2()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:FeedEater", pg.ConnectionString)
            .UseSetting("Mcp:Token", "t")
            .UseSetting("FeedEater:RunJobs", "false"));

        var result = await RunAsync(app, "x", "import", "-", "--format", "pdf", "--ref", "a.md");

        Assert.Equal(2, result.Code);
        Assert.Contains("format is", result.Err, StringComparison.Ordinal);
    }
}
