using System.Globalization;
using System.Text;

namespace FeedEater.Ranking;

public static class TasteReport
{
    /// <summary>A learned model must beat the current heuristic by this much held-out agreement before enabling it is suggested.</summary>
    public const double RequiredGain = 0.03;

    public static string Render(IReadOnlyList<LabeledVector> data, TasteOptions o)
    {
        var up = data.Count(d => d.Liked);
        var down = data.Count - up;
        var sb = new StringBuilder("# feed-eater taste report (offline)\n\n");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Votes with an embedding: {data.Count} ({up} up, {down} down). Needed to enable learning: {o.MinVotes}, at least 10 of each kind.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"`FeedEater:Taste:Learn` is {(o.Learn ? "ON" : "off")}. Nothing in this report changes the ranking.");

        var (model, note) = LearnedTaste.Prepare(data, o);
        if (model is null)
        {
            return sb.AppendLine().AppendLine($"**Too few votes to learn anything.** {note}").AppendLine("Enabling the learned term now would be refused at the next digest, which says so in its header.").ToString();
        }

        var (heuristic, learned) = LearnedTaste.CrossValidate(data);
        sb.AppendLine().AppendLine("## Held-out agreement with the votes (5-fold; 0.50 is chance)").AppendLine();
        sb.AppendLine("| ranking | agreement |").AppendLine("|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| current (liked centroid minus disliked centroid) | {Format(heuristic)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| learned (logistic regression on embeddings) | {Format(learned)} |");
        var gain = learned - heuristic;
        sb.AppendLine().AppendLine(gain >= RequiredGain
            ? string.Create(CultureInfo.InvariantCulture, $"The learned model is ahead by {gain:0.000}. Enabling `FeedEater:Taste:Learn` is reasonable; it adds a bounded term (weight {new WeightsOptions().Learned:0.0}) and the default ranking stays the base.")
            : string.Create(CultureInfo.InvariantCulture, $"The learned model is not ahead by the required {RequiredGain:0.00} (difference {gain:0.000}). Leave `FeedEater:Taste:Learn` off."));
        return sb.ToString();
    }

    private static string Format(double? v) => v is { } d ? d.ToString("0.000", CultureInfo.InvariantCulture) : "n/a";
}
