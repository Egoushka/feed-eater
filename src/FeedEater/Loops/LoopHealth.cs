using System.Collections.Concurrent;

namespace FeedEater.Loops;

public sealed record LoopStatus(string Name, bool Up, DateTimeOffset? LastSuccess, string? LastError);

public sealed class LoopHealth(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, LoopStatus> _status = new();

    public void Succeeded(string name) => _status[name] = new LoopStatus(name, true, time.GetUtcNow(), null);

    public void Failed(string name, string error) => _status.AddOrUpdate(
        name, n => new LoopStatus(n, false, null, error), (_, old) => old with { Up = false, LastError = error });

    public bool IsDown(string name) => _status.TryGetValue(name, out var s) && !s.Up;
}
