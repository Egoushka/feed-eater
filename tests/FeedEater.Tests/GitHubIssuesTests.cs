using System.Net;
using FeedEater.Issues;

namespace FeedEater.Tests;

public sealed class GitHubIssuesTests
{
    private static GitHubIssues Client(StubHandler handler) => new(handler.Client("http://github/"));

    [Fact]
    public async Task Creates_an_issue_labelled_triage_and_returns_its_number()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("""{"number":42,"title":"Try X"}""", HttpStatusCode.Created));

        var number = await Client(handler).CreateAsync("octo/repo", "Try X", "Try **X**", default);

        Assert.Equal(42, number);
        var post = handler.Calls.Single();
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("http://github/repos/octo/repo/issues", post.Uri);
        Assert.Contains("\"labels\":[\"triage\"]", post.Body, StringComparison.Ordinal);
        Assert.Contains("\"body\":\"Try **X**\"", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Titles_leave_out_pull_requests()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("""[{"title":"A"},{"title":"B","pull_request":{}},{"title":""}]"""));

        Assert.Equal(["A"], await Client(handler).OpenTitlesAsync("octo/repo", default));
        Assert.Equal(["A"], await Client(handler).RecentTitlesAsync("octo/repo", default));
        Assert.Contains("state=open", handler.Calls[0].Uri, StringComparison.Ordinal);
        Assert.Contains("state=all", handler.Calls[1].Uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LAB")]
    [InlineData("octo/repo/issues")]
    [InlineData("../x")]
    public async Task A_name_that_is_not_owner_slash_name_is_an_error_without_a_call(string repo)
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("{}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).CreateAsync(repo, "t", "b", default));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Check_fails_when_the_token_cannot_write_to_the_repo()
    {
        var readOnly = new StubHandler((_, _) => StubHandler.Json("""{"full_name":"octo/repo","permissions":{"push":false}}"""));
        var writable = new StubHandler((_, _) => StubHandler.Json("""{"full_name":"octo/repo","permissions":{"push":true}}"""));
        var missing = new StubHandler((_, _) => StubHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(readOnly).CheckAsync("octo/repo", default));
        await Client(writable).CheckAsync("octo/repo", default);
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Client(missing).CheckAsync("octo/repo", default));
        Assert.Equal(HttpStatusCode.NotFound, e.StatusCode);
    }

    [Fact]
    public void Markdown_keeps_feed_text_from_formatting_linking_or_mentioning()
    {
        Assert.Equal(@"\@dotnet ships \#12 \*fast\*", Markdown.Text("@dotnet ships #12 *fast*"));
        Assert.Equal(@"[a \[b\]](https://x.test/a%20%28b%29)", Markdown.Link("a [b]", "https://x.test/a (b)"));
    }
}
