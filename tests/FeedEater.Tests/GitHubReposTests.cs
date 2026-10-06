using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Signals;

namespace FeedEater.Tests;

public sealed class GitHubReposTests
{
    [Theory]
    [InlineData("https://github.com/acme/widget", "acme", "widget")]
    [InlineData("https://www.github.com/acme/widget/", "acme", "widget")]
    [InlineData("https://github.com/acme/widget/issues/12", "acme", "widget")]
    [InlineData("https://github.com/acme/widget/pull/3#discussion", "acme", "widget")]
    [InlineData("https://github.com/acme/widget.git", "acme", "widget")]
    [InlineData("https://github.com/acme/my.repo-2", "acme", "my.repo-2")]
    [InlineData("http://github.com/Acme/Widget?tab=readme", "Acme", "Widget")]
    public void Finds_the_repo_in_the_item_url(string url, string owner, string repo) =>
        Assert.Equal((owner, repo), GitHubRepos.Find(url, ""));

    [Theory]
    [InlineData("Source code: https://github.com/acme/widget.", "acme", "widget")]
    [InlineData("see (github.com/acme/widget) for more", "acme", "widget")]
    [InlineData("a [link](https://github.com/acme/widget/releases/tag/v1), done", "acme", "widget")]
    public void Finds_the_repo_in_the_text(string text, string owner, string repo) =>
        Assert.Equal((owner, repo), GitHubRepos.Find("https://example.com/post", text));

    [Fact]
    public void The_item_url_wins_over_the_text()
    {
        Assert.Equal(("a", "one"), GitHubRepos.Find("https://github.com/a/one", "also https://github.com/b/two"));
    }

    [Theory]
    [InlineData("https://pingularity.dev", "Pingularity: A self-hosted dashboard for scheduled Ookla speed tests. Try it at https://pingularity.dev")]
    [InlineData("https://example.com", "mirror at notgithub.com/acme/widget and https://github.com/acme")]
    [InlineData("https://example.com", "https://github.com/sponsors/acme and https://github.com/topics/selfhosted")]
    [InlineData("https://example.com", "https://gist.github.com/acme/abc123")]
    [InlineData("", "")]
    public void Finds_nothing_when_there_is_no_repo_link(string url, string text) => Assert.Null(GitHubRepos.Find(url, text));

    private static GitHubStarsClient Client(Func<string, HttpResponseMessage> answer) =>
        new(new StubHandler((request, _) => answer(request.RequestUri!.AbsolutePath)).Client("http://github/"), Options.Create(new FeedEaterOptions()));

    [Fact]
    public async Task Facts_combine_the_repo_and_its_latest_release_into_one_line()
    {
        var client = Client(path => path.EndsWith("/releases/latest", StringComparison.Ordinal)
            ? StubHandler.Json("""{"tag_name":"v2.3.0","published_at":"2026-09-20T10:00:00Z"}""")
            : StubHandler.Json("""{"full_name":"acme/widget","created_at":"2024-03-02T08:00:00Z","pushed_at":"2026-10-01T09:00:00Z","stargazers_count":1240}"""));

        var facts = await client.RepoFactsAsync("acme", "widget", default);

        Assert.Equal("created 2024-03-02, last push 2026-10-01, 1,240 stars, latest release v2.3.0 on 2026-09-20", facts!.Line());
    }

    [Fact]
    public async Task A_repo_without_releases_still_gives_facts()
    {
        var client = Client(path => path.EndsWith("/releases/latest", StringComparison.Ordinal)
            ? StubHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound)
            : StubHandler.Json("""{"created_at":"2024-03-02T08:00:00Z","pushed_at":null,"stargazers_count":3}"""));

        Assert.Equal("created 2024-03-02, 3 stars", (await client.RepoFactsAsync("acme", "widget", default))!.Line());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_missing_repo_or_a_rate_limit_gives_no_facts(HttpStatusCode status) =>
        Assert.Null(await Client(_ => StubHandler.Json("""{"message":"x"}""", status)).RepoFactsAsync("acme", "widget", default));
}
