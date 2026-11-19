using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Loops;
using FeedEater.Push;
using FeedEater.Storage;

namespace FeedEater.Tests;

internal static class PushKit
{
    /// <summary>A notifier whose ntfy server is <paramref name="ntfy"/>; with none given every post succeeds unseen.</summary>
    public static PushNotifier Notifier(PostgresFixture pg, IOptions<FeedEaterOptions> options, TimeProvider time, StubHandler? ntfy = null) => new(
        new NtfyClient((ntfy ?? new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))).Client("http://ntfy/")),
        new QuietHours(new CursorStore(pg.Db), options, time), new CursorStore(pg.Db), new DigestStore(pg.Db), options, time, NullLogger<PushNotifier>.Instance);
}

[Collection(PostgresCollection.Name)]
public sealed class PushTests(PostgresFixture pg) : IAsyncLifetime
{
    // 12:00 in Kyiv (EEST).
    private static readonly DateTimeOffset Noon = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private const string Date = "2026-10-06";

    private readonly FakeTimeProvider _time = new(Noon);
    private readonly List<(string Title, string? Click, string Body)> _posts = [];
    private HttpStatusCode _status = HttpStatusCode.OK;

    public Task InitializeAsync() => pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private PushNotifier Build(Action<FeedEaterOptions>? tweak = null)
    {
        var settings = new FeedEaterOptions
        {
            TimeZone = "Europe/Kyiv",
            Push = new PushOptions { NtfyUrl = "http://ntfy/", Topic = "feed", BaseUrl = "https://feed.example/" },
        };
        tweak?.Invoke(settings);
        var ntfy = new StubHandler((request, body) =>
        {
            _posts.Add((request.Headers.GetValues("Title").Single(), request.Headers.TryGetValues("Click", out var click) ? click.Single() : null, body));
            return new HttpResponseMessage(_status);
        });
        return PushKit.Notifier(pg, Options.Create(settings), _time, ntfy);
    }

    [Fact]
    public async Task Push_posts_one_ntfy_message_per_digest()
    {
        var push = Build();

        Assert.True(await push.NotifyDigestAsync(Date, 3, default));
        Assert.True(await push.NotifyDigestAsync(Date, 3, default));

        var post = Assert.Single(_posts);
        Assert.Equal(("Today: 3 items", "https://feed.example/ui", "Today: 3 items"), post);
    }

    [Fact]
    public async Task Push_posts_nothing_inside_quiet_hours_and_one_message_when_they_end()
    {
        var push = Build(o => o.Quiet = new QuietOptions { From = new TimeSpan(11, 0, 0), To = new TimeSpan(13, 0, 0) });

        Assert.False(await push.NotifyDigestAsync(Date, 3, default));
        Assert.Empty(_posts);

        _time.Advance(TimeSpan.FromHours(2));   // 14:00 local
        Assert.True(await push.NotifyDigestAsync(Date, 3, default));
        Assert.Single(_posts);
    }

    [Fact]
    public async Task A_click_address_that_is_not_http_is_left_off()
    {
        var push = Build(o => o.Push = new PushOptions { NtfyUrl = "http://ntfy/", Topic = "feed", BaseUrl = "javascript:alert(1)//" });

        Assert.True(await push.NotifyDigestAsync(Date, 3, default));

        Assert.Null(Assert.Single(_posts).Click);
    }

    [Fact]
    public async Task Push_does_nothing_without_a_server_and_topic()
    {
        var push = Build(o => o.Push = new PushOptions());

        Assert.False(await push.NotifyDigestAsync(Date, 3, default));
        Assert.Empty(_posts);
    }

    [Fact]
    public async Task Push_retries_at_most_once_an_hour()
    {
        var push = Build();
        _status = HttpStatusCode.ServiceUnavailable;

        Assert.False(await push.NotifyDigestAsync(Date, 3, default));
        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(await push.NotifyDigestAsync(Date, 3, default));
        Assert.Single(_posts);   // the second call within the hour did not post

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await push.NotifyDigestAsync(Date, 3, default));
        Assert.Equal(2, _posts.Count);   // one retry after the hour

        _status = HttpStatusCode.OK;
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(await push.NotifyDigestAsync(Date, 3, default));
        Assert.Equal(3, _posts.Count);

        _time.Advance(TimeSpan.FromHours(2));
        Assert.True(await push.NotifyDigestAsync(Date, 3, default));
        Assert.Equal(3, _posts.Count);   // delivered once, never again
    }

    [Fact]
    public async Task Retrying_a_held_push_picks_up_todays_sent_digest()
    {
        await new DigestStore(pg.Db).CreateAsync(Date, default);
        await new DigestStore(pg.Db).SetSelectionAsync(Date, 5, 5, [1, 2], null, default);
        await new DigestStore(pg.Db).MarkSentAsync(Date, _time.GetUtcNow(), default);

        await Build().RetryPendingAsync(default);

        Assert.Equal("Today: 2 items", Assert.Single(_posts).Title);
    }
}
