namespace FeedEater.Loops;

/// <summary>Runs <see cref="PollAsync"/> forever, records health, backs off on failure up to 5 minutes.</summary>
public abstract class PollingLoop(LoopHealth health, TimeProvider time, ILogger logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    protected abstract string Name { get; }
    protected abstract TimeSpan Interval { get; }
    protected abstract Task PollAsync(CancellationToken ct);

    // Subclasses use these instead of their own copies: capturing a parameter that is also passed to the base is CS9107.
    protected TimeProvider Time => time;
    protected ILogger Logger => logger;

    public static TimeSpan Backoff(TimeSpan interval, int failures) =>
        TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds * Math.Pow(2, failures - 1), MaxBackoff.TotalSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
                failures = 0;
                health.Succeeded(Name);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                failures++;
                logger.LogWarning(ex, "{Loop} failed ({Failures} in a row)", Name, failures);
                health.Failed(Name, ex.Message);
            }

            await Task.Delay(failures == 0 ? Interval : Backoff(Interval, failures), time, stoppingToken);
        }
    }
}
