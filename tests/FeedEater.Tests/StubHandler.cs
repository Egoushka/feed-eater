using System.Net;
using System.Text;

namespace FeedEater.Tests;

/// <summary>Answers every request with <paramref name="respond"/>(request, body) and records the calls.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Uri, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Calls)
        {
            Calls.Add((request.Method, request.RequestUri!.ToString(), body));
        }

        return respond(request, body);
    }

    public HttpClient Client(string baseUrl) => new(this) { BaseAddress = new Uri(baseUrl) };

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
