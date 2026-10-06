using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using FeedEater.Storage;
using FeedEater.Ui;

namespace FeedEater.Tests;

/// <summary>What <see cref="TasteMap"/> reads and when, counted as the item queries Npgsql logs.</summary>
[Collection(PostgresCollection.Name)]
public sealed class UiMapCacheTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class QueryCounter : ILoggerProvider, ILogger
    {
        private int _queries;

        public int Queries => Volatile.Read(ref _queries);

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);   // Npgsql logs each command when it starts and when it completes: count the second
            if (message.Contains("completed", StringComparison.Ordinal) && message.Contains("taste-map", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _queries);
            }
        }

        public void Dispose()
        {
        }
    }

    private (TasteMap Map, QueryCounter Counter, FakeTimeProvider Time) Build()
    {
        var counter = new QueryCounter();
        var source = new NpgsqlDataSourceBuilder(pg.ConnectionString).UseLoggerFactory(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(counter))).Build();
        var db = new FeedDb(source);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return (new TasteMap(new MapStore(db), new ProfileStore(db), Options.Create(new FeedEaterOptions()), time), counter, time);
    }

    private async Task SeedAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Seed.ItemAsync(pg, 1, $"Map item {i}", TestVectors.OneHot(i));
        }
    }

    [Fact]
    public async Task Stepping_through_the_months_reads_and_projects_the_items_once()
    {
        await SeedAsync(5);
        var (map, counter, time) = Build();
        var now = time.GetUtcNow();

        var current = await map.GetAsync(null, default);
        var back = await map.GetAsync($"{now.AddMonths(-1):yyyy-MM}", default);
        var further = await map.GetAsync($"{now.AddMonths(-2):yyyy-MM}", default);

        Assert.Equal(1, counter.Queries);
        Assert.Equal(current.Dots.Select(d => (d.Id, d.X, d.Y)), further.Dots.Select(d => (d.Id, d.X, d.Y)));
        Assert.Equal(back.Dots.Select(d => (d.X, d.Y)), current.Dots.Select(d => (d.X, d.Y)));
    }

    [Fact]
    public async Task Requests_that_arrive_together_on_a_cold_cache_build_the_map_once()
    {
        await SeedAsync(5);
        var (map, counter, _) = Build();

        var views = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => map.GetAsync(null, default))));

        Assert.Equal(1, counter.Queries);
        Assert.All(views, v => Assert.Equal(5, v.Dots.Count));
    }

    [Fact]
    public async Task The_layout_is_built_again_after_ten_minutes()
    {
        await SeedAsync(5);
        var (map, counter, time) = Build();

        await map.GetAsync(null, default);
        time.Advance(TimeSpan.FromMinutes(9));
        await map.GetAsync(null, default);
        Assert.Equal(1, counter.Queries);

        time.Advance(TimeSpan.FromMinutes(2));
        await map.GetAsync(null, default);

        Assert.Equal(2, counter.Queries);
    }
}
