using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Plane;

namespace FeedEater.Tests;

public sealed class PlaneClientTests
{
    private static PlaneClient Client(StubHandler handler) => new(handler.Client("http://plane/"), Options.Create(new FeedEaterOptions()));

    private static HttpResponseMessage Projects() => StubHandler.Json("""{"results":[{"id":"p1","identifier":"FEED"}]}""");

    [Fact]
    public async Task Falls_back_to_the_issues_path_when_work_items_is_missing()
    {
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/workspaces/homelab/projects/" => Projects(),
            "/api/v1/workspaces/homelab/projects/p1/states/" => StubHandler.Json("""{"results":[{"id":"s1","group":"backlog"}]}"""),
            "/api/v1/workspaces/homelab/projects/p1/work-items/" => StubHandler.Json("{}", HttpStatusCode.NotFound),
            "/api/v1/workspaces/homelab/projects/p1/issues/" => StubHandler.Json("""{"results":[{"name":"A","state":"s1"}]}"""),
            _ => StubHandler.Json("{}", HttpStatusCode.InternalServerError),
        });

        Assert.Equal(["A"], await Client(handler).OpenItemTitlesAsync("FEED", default));
    }

    [Fact]
    public async Task Creates_an_intake_item_and_returns_its_id()
    {
        var handler = new StubHandler((request, _) => request.Method == HttpMethod.Post
            ? StubHandler.Json("""{"id":"intake-1","issue":{"id":"issue-9"}}""", HttpStatusCode.Created)
            : Projects());

        var id = await Client(handler).CreateIntakeAsync("FEED", "Try X", "<p>Try X</p>", default);

        Assert.Equal("issue-9", id);
        var post = handler.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal("http://plane/api/v1/workspaces/homelab/projects/p1/intake-issues/", post.Uri);
        Assert.Contains("\"description_html\":\"\\u003Cp\\u003ETry X\\u003C/p\\u003E\"", post.Body, StringComparison.Ordinal);
        Assert.Contains("\"priority\":\"none\"", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_project_identifier_is_an_error()
    {
        var handler = new StubHandler((_, _) => Projects());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).ProjectIdAsync("NOPE", default));
    }
}
