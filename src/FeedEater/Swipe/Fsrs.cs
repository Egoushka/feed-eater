namespace FeedEater.Swipe;

public sealed record FsrsState(double Stability, double Difficulty);

/// <summary>
/// The FSRS-6 memory model with its default weights (port of ts-fsrs 6.0.0, MIT, https://github.com/open-spaced-repetition/ts-fsrs). Long-term scheduling only:
/// no fuzz and no intra-day learning steps. Pure.
/// </summary>
public static class Fsrs
{
    public const int Again = 1;
    public const int Hard = 2;
    public const int Good = 3;
    public const int Easy = 4;
    public const double RequestRetention = 0.9;

    private const double SMin = 0.001;
    private const double SMax = 36500;
    private static readonly double[] W =
    [
        0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194, 0.001, 1.8722, 0.1666, 0.796, 1.4835, 0.0614, 0.2629, 1.6483, 0.6014, 1.8729, 0.5425, 0.0912, 0.0658, 0.1542,
    ];

    private static readonly double Decay = -W[20];
    private static readonly double Factor = Round(Math.Exp(Math.Log(0.9) / Decay) - 1);

    public static FsrsState Start(int grade) => new(Math.Clamp(W[grade - 1], SMin, SMax), Math.Clamp(InitDifficulty(grade), 1, 10));

    public static double Retrievability(double elapsedDays, double stability) => Round(Math.Pow(1 + (Factor * elapsedDays / stability), Decay));

    /// <summary>Days until retrievability falls to <paramref name="retention"/>, at least one.</summary>
    public static int Interval(double stability, double retention = RequestRetention) =>
        Math.Max((int)Math.Round(stability / Factor * (Math.Pow(retention, 1 / Decay) - 1), MidpointRounding.AwayFromZero), 1);

    /// <summary>The state after a review <paramref name="elapsedDays"/> after the last one. A first review has no state: pass null.</summary>
    public static FsrsState Review(FsrsState? state, double elapsedDays, int grade)
    {
        if (state is null || (state.Stability == 0 && state.Difficulty == 0))
        {
            return Start(grade);
        }

        var (s, d) = (state.Stability, state.Difficulty);
        var r = Retrievability(Math.Max(elapsedDays, 0), s);
        double next;
        if (grade == Again)
        {
            var afterFail = Round(Math.Clamp(W[11] * Math.Pow(d, -W[12]) * (Math.Pow(s + 1, W[13]) - 1) * Math.Exp((1 - r) * W[14]), SMin, SMax));
            next = Math.Clamp(Round(s), SMin, afterFail);
        }
        else
        {
            var hard = grade == Hard ? W[15] : 1;
            var easy = grade == Easy ? W[16] : 1;
            next = Round(Math.Clamp(s * (1 + (Math.Exp(W[8]) * (11 - d) * Math.Pow(s, -W[9]) * (Math.Exp((1 - r) * W[10]) - 1) * hard * easy)), SMin, SMax));
        }

        return new FsrsState(next, NextDifficulty(d, grade));
    }

    private static double InitDifficulty(int grade) => Round(W[4] - Math.Exp((grade - 1) * W[5]) + 1);

    private static double NextDifficulty(double d, int grade)
    {
        var delta = -W[6] * (grade - 3);
        var damped = d + Round(delta * (10 - d) / 9);
        return Math.Clamp(Round((W[7] * InitDifficulty(Easy)) + ((1 - W[7]) * damped)), 1, 10);
    }

    private static double Round(double v) => Math.Round(v, 8, MidpointRounding.AwayFromZero);
}
