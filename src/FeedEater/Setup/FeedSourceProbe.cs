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
            var feeds = await connection.ExecuteScalarAsync<long>(new CommandDefinition("select count(*) from feeds", cancellationToken: ct));
            return feeds > 0
                ? CheckResult.Ok($"{feeds} {(feeds == 1 ? "feed" : "feeds")}")
                : CheckResult.Fail("no feeds", "Import an OPML (docker compose run --rm feed-eater import-opml /config/feeds.opml) or add one at /ui/sources.");
        }
        catch (PostgresException ex) when (ex.SqlState == NoTable)
        {
            return CheckResult.Fail("the database has no tables yet", "They are created when feed-eater starts: run docker compose up -d, then check again.");
        }
    }
}
