using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using FeedEater.Ingest;
using FeedEater.Storage;

namespace FeedEater.Setup;

/// <summary>
/// What <c>doctor</c> and <c>/ui/setup</c> ask about the feed source. Where feeds come from differs by mode, so the mode brings its
/// own probe; the last one registered is used.
/// </summary>
public interface IFeedSourceProbe
{
    /// <summary>Which source this is, as shown before the detail ("Miniflux", "built-in reader").</summary>
    string Name { get; }

    Task<CheckResult> CheckAsync(CancellationToken ct);
}

/// <summary>Miniflux when it is configured (one read through its client), otherwise a count of the subscribed feeds.</summary>
public sealed class DefaultFeedSourceProbe(FeedDb db, IServiceProvider services, IOptions<FeedEaterOptions> options) : IFeedSourceProbe
{
    private const string NoTable = "42P01";

    public string Name => options.Value.Miniflux.Enabled ? "Miniflux" : "built-in reader";

    public async Task<CheckResult> CheckAsync(CancellationToken ct)
    {
        if (options.Value.Miniflux.Enabled)
        {
            try
            {
                await services.GetRequiredService<MinifluxClient>().EntriesAfterAsync(0, 1, ct);
                return CheckResult.Ok($"{Hints.Host(options.Value.Miniflux.BaseUrl)} answers");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return CheckResult.Fail(Hints.Describe(ex), Hints.Http(ex, "Miniflux:BaseUrl", "Miniflux:Token"));
            }
        }

        try
        {
            await using var connection = await db.DataSource.OpenConnectionAsync(ct);
            var (feeds, fetched, failing, lastError) = await connection.QuerySingleAsync<(long Feeds, long Fetched, long Failing, string? LastError)>(new CommandDefinition(
                """
                select count(*), count(*) filter (where last_fetched_at is not null), count(*) filter (where fail_count >= 3),
                       (select last_error from feeds where fail_count >= 3 order by last_fetched_at desc nulls last limit 1)
                from feeds
                """, cancellationToken: ct));
            var noun = feeds == 1 ? "feed" : "feeds";
            if (feeds == 0)
            {
                return CheckResult.Fail("no feeds", "Import an OPML (docker compose run --rm feed-eater import-opml /config/feeds.opml) or add one at /ui/sources.");
            }

            if (failing == feeds)
            {
                return CheckResult.Fail($"{feeds} {noun}, every one failing: {lastError}", "Open /ui/sources for each feed's error; a self-hosted feed host needs Source:AllowedHosts.");
            }

            return fetched == 0
                ? CheckResult.Warn($"{feeds} {noun}, none fetched yet", "The first fetch runs within a few minutes of start; check again.")
                : CheckResult.Ok($"{feeds} {noun}, {fetched} fetched{(failing > 0 ? $", {failing} failing" : "")}");
        }
        catch (PostgresException ex) when (ex.SqlState == NoTable)
        {
            return CheckResult.Fail("the database has no tables yet", "They are created when feed-eater starts: run docker compose up -d, then check again.");
        }
    }
}
