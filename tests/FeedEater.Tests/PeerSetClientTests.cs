using Microsoft.Extensions.Options;
using FeedEater.PriorArt.Peers;

namespace FeedEater.Tests;

public sealed class PeerSetClientTests
{
    private static PeerSetClient Client(string setUrl, string setToken, Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        new(new StubHandler((request, _) => answer(request)).Client("http://github/"),
            Options.Create(new FeedEaterOptions { PeerHarvest = new() { SetUrl = setUrl, SetToken = setToken } }));

    [Fact]
    public async Task No_set_url_gives_null_without_a_request()
    {
        var client = Client("", "", _ => throw new InvalidOperationException("should not be called"));

        Assert.Null(await client.TextAsync(default));
    }

    [Fact]
    public async Task Sends_the_github_raw_accept_header_and_a_user_agent()
    {
        HttpRequestMessage? sent = null;
        var client = Client("https://api.github.com/repos/acme/research/contents/peers/feed-eater.yaml", "", request =>
        {
            sent = request;
            return StubHandler.Json("repo: a/one\n");
        });

        await client.TextAsync(default);

        Assert.Equal("application/vnd.github.raw+json", sent!.Headers.Accept.ToString());
        Assert.Equal("feed-eater", sent.Headers.UserAgent.ToString());
        Assert.Null(sent.Headers.Authorization);
    }

    [Fact]
    public async Task Sends_a_bearer_token_when_one_is_set()
    {
        HttpRequestMessage? sent = null;
        var client = Client("https://api.github.com/repos/acme/research/contents/peers/feed-eater.yaml", "s3cr3t", request =>
        {
            sent = request;
            return StubHandler.Json("repo: a/one\n");
        });

        await client.TextAsync(default);

        Assert.Equal("Bearer", sent!.Headers.Authorization!.Scheme);
        Assert.Equal("s3cr3t", sent.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Returns_the_response_body_as_is()
    {
        const string yaml = "repo: a/one\npeers:\n  - b/two\n";
        var client = Client("https://api.github.com/repos/acme/research/contents/peers/feed-eater.yaml", "", _ => StubHandler.Json(yaml));

        Assert.Equal(yaml, await client.TextAsync(default));
    }
}
