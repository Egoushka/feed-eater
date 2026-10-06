using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using FeedEater.Signals;

namespace FeedEater.Tests;

public sealed class GitHubLookupTests
{
    private const string Repo = """{"full_name":"acme/widget","created_at":"2024-03-02T08:00:00Z","pushed_at":"2026-10-01T09:00:00Z","stargazers_count":1240}""";
    private const string Release = """{"tag_name":"v2.3.0","published_at":"2026-09-20T10:00:00Z"}""";

    private static (GitHubStarsClient Client, StubHandler Stub) Build(Func<string, HttpResponseMessage> answer)
    {
        var stub = new StubHandler((request, _) => answer(request.RequestUri!.AbsolutePath));
        return (new GitHubStarsClient(stub.Client("http://github/"), Options.Create(new FeedEaterOptions())), stub);
    }

    private static bool IsRelease(string path) => path.EndsWith("/releases/latest", StringComparison.Ordinal);

    [Fact]
    public async Task A_found_repo_costs_two_requests_and_carries_the_archived_flag()
    {
        var (client, stub) = Build(path => StubHandler.Json(IsRelease(path) ? Release : Repo.Replace("}", ",\"archived\":true}", StringComparison.Ordinal)));

        var lookup = await client.LookupAsync("acme", "widget", default);

        Assert.Equal((LookupOutcome.Found, 2), (lookup.Outcome, lookup.Requests));
        Assert.Equal(("v2.3.0", 1240, true), (lookup.Facts!.ReleaseTag, lookup.Facts.Stars, lookup.Facts.Archived));
        Assert.Equal(2, stub.Calls.Count);
    }

    [Fact]
    public async Task A_repo_without_a_release_or_an_archived_field_is_found_and_not_archived()
    {
        var (client, _) = Build(path => IsRelease(path) ? StubHandler.Json("{}", HttpStatusCode.NotFound) : StubHandler.Json(Repo));

        var lookup = await client.LookupAsync("acme", "widget", default);

        Assert.Equal(LookupOutcome.Found, lookup.Outcome);
        Assert.Null(lookup.Facts!.ReleaseTag);
        Assert.False(lookup.Facts.Archived);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, LookupOutcome.Gone)]
    [InlineData(HttpStatusCode.Forbidden, LookupOutcome.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, LookupOutcome.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, LookupOutcome.Failed)]
    [InlineData(HttpStatusCode.Unauthorized, LookupOutcome.Failed)]
    public async Task A_refused_repo_call_says_why_and_costs_one_request(HttpStatusCode status, LookupOutcome outcome)
    {
        var (client, stub) = Build(_ => StubHandler.Json("""{"message":"x"}""", status));

        var lookup = await client.LookupAsync("acme", "widget", default);

        Assert.Equal((outcome, 1), (lookup.Outcome, lookup.Requests));
        Assert.Null(lookup.Facts);
        Assert.Single(stub.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_refused_release_call_is_rate_limited_but_the_plain_facts_call_still_gets_the_repo(HttpStatusCode status)
    {
        var (client, _) = Build(path => IsRelease(path) ? StubHandler.Json("{}", status) : StubHandler.Json(Repo));

        var lookup = await client.LookupAsync("acme", "widget", default);
        var facts = await client.RepoFactsAsync("acme", "widget", default);

        Assert.Equal(LookupOutcome.RateLimited, lookup.Outcome);
        Assert.Equal(1240, lookup.Facts!.Stars);
        Assert.Equal(1240, facts!.Stars);   // the digest keeps using what it got, as before
    }

    [Fact]
    public async Task A_timeout_and_a_dead_connection_surface_to_the_caller()
    {
        var timeout = new GitHubStarsClient(new StubHandler((_, _) => throw new TaskCanceledException("timed out", new TimeoutException())).Client("http://github/"), Options.Create(new FeedEaterOptions()));
        var dead = new GitHubStarsClient(new StubHandler((_, _) => throw new HttpRequestException("Connection refused")).Client("http://github/"), Options.Create(new FeedEaterOptions()));

        await Assert.ThrowsAsync<TaskCanceledException>(() => timeout.LookupAsync("acme", "widget", default));
        await Assert.ThrowsAsync<HttpRequestException>(() => dead.LookupAsync("acme", "widget", default));
    }

    private static IHost Host(string token, StubHandler stub)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FeedEater"] = "Host=localhost;Database=unused",
            ["FeedEater:GitHub:BaseUrl"] = "http://github/",
            ["FeedEater:GitHub:User"] = "octocat",
            ["FeedEater:GitHub:Token"] = token,
        });
        builder.Services.AddFeedEater(builder.Configuration);
        builder.Services.AddHttpClient<GitHubStarsClient>().ConfigurePrimaryHttpMessageHandler(() => stub);
        return builder.Build();
    }

    private static async Task CallEverythingAsync(GitHubStarsClient client)
    {
        await client.RepoFactsAsync("acme", "widget", default);
        await client.PingAsync(default);
        await client.PageAsync(1, default);
    }

    [Fact]
    public async Task The_token_is_sent_as_a_bearer_header_on_every_call()
    {
        var seen = new List<string?>();
        var stub = new StubHandler((request, _) =>
        {
            seen.Add(request.Headers.Authorization?.ToString());
            return StubHandler.Json(request.RequestUri!.AbsolutePath.EndsWith("/starred", StringComparison.Ordinal) ? "[]" : Repo);
        });
        using var host = Host("gh-secret-token", stub);

        await CallEverythingAsync(host.Services.GetRequiredService<GitHubStarsClient>());

        Assert.Equal(4, seen.Count);   // repo, release, user, starred
        Assert.All(seen, h => Assert.Equal("Bearer gh-secret-token", h));
    }

    [Fact]
    public async Task Without_a_token_no_authorization_header_is_sent()
    {
        var seen = new List<string?>();
        var stub = new StubHandler((request, _) =>
        {
            seen.Add(request.Headers.Authorization?.ToString());
            return StubHandler.Json(request.RequestUri!.AbsolutePath.EndsWith("/starred", StringComparison.Ordinal) ? "[]" : Repo);
        });
        using var host = Host("", stub);

        await CallEverythingAsync(host.Services.GetRequiredService<GitHubStarsClient>());

        Assert.Equal(4, seen.Count);
        Assert.All(seen, Assert.Null);
    }
}
