using System.Globalization;
using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Loops;

public sealed record QuietStatus(bool Quiet, string Text);

/// <summary>
/// Holds scheduled sends (the digest, the weekly review, urgent release alerts) while he is asleep. Two inputs: a configured local
/// window (<c>FeedEater:Quiet:From</c> and <c>To</c>, none by default) and a manual toggle (<c>/quiet</c>, or the button on Today)
/// that turns quiet on for <c>ManualHours</c>, or off until the current window ends. A digest asked for by hand is never held.
/// A senses-driven version (asleep from the senses service) is a follow-up; that service needs a token this one does not have.
/// </summary>
public sealed class QuietHours(CursorStore cursors, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    private const string Cursor = "quiet:manual";

    public bool WindowConfigured => Window is not null;

    private (TimeSpan From, TimeSpan To)? Window => options.Value.Quiet is { From: { } from, To: { } to } && from != to ? (from, to) : null;

    public async Task<bool> IsQuietAsync(CancellationToken ct) => (await StatusAsync(ct)).Quiet;

    public async Task<QuietStatus> StatusAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var zone = options.Value.Zone;
        if (await ManualAsync(ct) is { } manual && manual.Until > now)
        {
            var until = TimeZoneInfo.ConvertTime(manual.Until, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
            return manual.On ? new QuietStatus(true, $"Quiet mode is on until {until}.") : new QuietStatus(false, $"Quiet mode is off until {until}.");
        }

        if (Window is { } w)
        {
            var span = $"{w.From.ToString(@"hh\:mm", CultureInfo.InvariantCulture)} to {w.To.ToString(@"hh\:mm", CultureInfo.InvariantCulture)}";
            return InWindow(Schedule.Local(now, zone).TimeOfDay, w)
                ? new QuietStatus(true, $"Quiet hours ({span}) are in effect.")
                : new QuietStatus(false, $"Quiet hours are {span}; not in effect now.");
        }

        return new QuietStatus(false, "Quiet mode is off.");
    }

    /// <summary>On: quiet for the configured hours. Off: not quiet until the current window ends; with no window running it just clears the toggle.</summary>
    public async Task<QuietStatus> SetAsync(bool on, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (on)
        {
            await cursors.SetAsync(Cursor, $"on|{now.AddHours(options.Value.Quiet.ManualHours).ToString("O", CultureInfo.InvariantCulture)}", ct);
        }
        else if (Window is { } w && InWindow(Schedule.Local(now, options.Value.Zone).TimeOfDay, w))
        {
            await cursors.SetAsync(Cursor, $"off|{NextEnd(now, w.To).ToString("O", CultureInfo.InvariantCulture)}", ct);
        }
        else
        {
            await cursors.DeleteAsync(Cursor, ct);
        }

        return await StatusAsync(ct);
    }

    public async Task<QuietStatus> ToggleAsync(CancellationToken ct) => await SetAsync(!await IsQuietAsync(ct), ct);

    internal static bool InWindow(TimeSpan t, (TimeSpan From, TimeSpan To) w) =>
        w.From < w.To ? t >= w.From && t < w.To : t >= w.From || t < w.To;

    private DateTimeOffset NextEnd(DateTimeOffset now, TimeSpan to)
    {
        var zone = options.Value.Zone;
        var local = Schedule.Local(now, zone);
        var end = local.Date + to;
        if (end <= local)
        {
            end = end.AddDays(1);
        }

        return new DateTimeOffset(end, zone.GetUtcOffset(end));
    }

    private async Task<(bool On, DateTimeOffset Until)?> ManualAsync(CancellationToken ct) =>
        await cursors.GetAsync(Cursor, ct) is { } v && v.Split('|') is [var mode and ("on" or "off"), var until]
            && DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? (mode == "on", at)
            : null;
}
