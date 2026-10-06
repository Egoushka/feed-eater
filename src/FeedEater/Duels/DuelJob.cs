using System.Globalization;
using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Duels;

/// <summary>
/// At each of <c>Duel:Times</c>: two unvoted items from the middle of the ranking in one message, for the votes the learned ranking needs.
/// A slot runs once; one missed while down runs at start only within two hours of its time. Quiet hours hold it.
/// </summary>
public sealed class DuelJob(
    DuelStore duels, TelegramClient telegram, QuietHours quiet,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<DuelJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(2);
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    protected override string Name => "duel";
    protected override string? DueKey(DateTime localNow) => LatestSlot(Slots(Settings.Duel.Times), localNow);

    protected override async Task<bool> HoldAsync(CancellationToken ct) => await quiet.IsQuietAsync(ct);

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var now = Time.GetUtcNow();
        var zone = Settings.Zone;
        var today = Schedule.Local(now, zone).Date;
        if (await duels.SentSinceAsync(new DateTimeOffset(today, zone.GetUtcOffset(today)), ct) >= Settings.Duel.PerDay)
        {
            Logger.LogInformation("Duel {Slot} skipped: {PerDay} sent today", key, Settings.Duel.PerDay);
            return;
        }

        var pair = DuelPicker.Pick(await duels.PoolAsync(now - Window, ct), await duels.SeenPairsAsync(ct));
        if (pair is not { } chosen)
        {
            Logger.LogInformation("Duel {Slot} skipped: no pair to offer", key);
            return;
        }

        var (first, second) = chosen;

        // The buttons carry the duel id, so the row exists before the send and goes again when the send fails.
        var id = await duels.CreateAsync(first.Id, second.Id, now, ct);
        try
        {
            await telegram.SendAsync(Settings.Telegram.AllowedUserId, DuelFormatter.Message(id, first, second), ct);
        }
        catch
        {
            await duels.DeleteAsync(id, CancellationToken.None);
            throw;
        }
    }

    /// <summary>The configured times, ascending; an entry that is not a time of day is dropped.</summary>
    internal static IReadOnlyList<TimeSpan> Slots(string times) => times
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var at) ? at : (TimeSpan?)null)
        .OfType<TimeSpan>().Where(at => at >= TimeSpan.Zero && at < TimeSpan.FromDays(1)).Distinct().Order().ToList();

    /// <summary>The date and time of the latest slot that has passed, if it is within the grace period; one key per slot.</summary>
    internal static string? LatestSlot(IReadOnlyList<TimeSpan> slots, DateTime local)
    {
        var passed = slots.SelectMany(t => new[] { local.Date.AddDays(-1) + t, local.Date + t }).Where(at => at <= local).Select(at => (DateTime?)at).Max();
        return passed is { } slot && local - slot <= Grace
            ? string.Create(CultureInfo.InvariantCulture, $"{Schedule.Key(slot.Date)}@{slot:HH:mm}")
            : null;
    }
}
