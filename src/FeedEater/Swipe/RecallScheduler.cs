namespace FeedEater.Swipe;

/// <summary>The review ladder: a good answer moves a card up one step and pushes it out; a forgotten one starts again at tomorrow.</summary>
public static class RecallScheduler
{
    public static readonly int[] LadderDays = [1, 3, 7, 16, 35];

    public static (int Step, DateTimeOffset Due) Next(int step, bool good, DateTimeOffset now) =>
        good ? (step + 1, now.AddDays(LadderDays[Math.Min(step, LadderDays.Length - 1)])) : (0, now.AddDays(LadderDays[0]));

    /// <summary>A good answer on a card that had been answered before: it was remembered across at least a day, not just seen.</summary>
    public static bool Remembered(int stepBefore, bool good) => good && stepBefore >= 1;
}
