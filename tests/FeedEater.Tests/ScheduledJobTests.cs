using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduledJobTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class CountingJob(CursorStore cursors, TimeProvider time, bool fail)
        : ScheduledJob(cursors, Options.Create(new FeedEaterOptions { TimeZone = "Europe/Kyiv" }), new LoopHealth(time), time, NullLogger.Instance)
    {
        public List<string> Runs { get; } = [];
        protected override string Name => "counting";
        protected override string? DueKey(DateTime localNow) => Schedule.Window(localNow, new TimeSpan(7, 30, 0), new TimeSpan(12, 0, 0));

        protected override Task RunAsync(string key, CancellationToken ct)
        {
            Runs.Add(key);
            return fail ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Runs_once_per_key_and_again_the_next_day()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 4, 40, 0, TimeSpan.Zero)); // 07:40 Kyiv
        var job = new CountingJob(new CursorStore(pg.Db), time, fail: false);

        await job.TickAsync(default);
        await job.TickAsync(default);
        time.Advance(TimeSpan.FromDays(1));
        await job.TickAsync(default);

        Assert.Equal(["2026-10-05", "2026-10-06"], job.Runs);
    }

    [Fact]
    public async Task Does_nothing_outside_the_window_and_retries_after_a_failure()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero)); // 06:00 Kyiv
        var cursors = new CursorStore(pg.Db);
        var job = new CountingJob(cursors, time, fail: true);

        await job.TickAsync(default);
        Assert.Empty(job.Runs);

        time.Advance(TimeSpan.FromHours(2)); // 08:00 Kyiv
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.TickAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.TickAsync(default));

        Assert.Equal(["2026-10-05", "2026-10-05"], job.Runs);
        Assert.Null(await cursors.GetAsync("job:counting", default));
    }
}
