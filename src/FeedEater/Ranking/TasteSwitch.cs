using System.Globalization;
using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Ranking;

/// <summary>
/// Whether the learned term is on: <c>/learn on|off</c> from Telegram wins over <c>FeedEater:Taste:Learn</c>, so turning it on needs
/// no deploy. The weekly review says when the learned model has pulled ahead.
/// </summary>
public sealed class TasteSwitch(CursorStore cursors, FeedbackStore feedback, IOptions<FeedEaterOptions> options)
{
    private const string Cursor = "taste:learn";

    public async Task<bool> IsOnAsync(CancellationToken ct) => await cursors.GetAsync(Cursor, ct) switch
    {
        "on" => true,
        "off" => false,
        _ => options.Value.Taste.Learn,
    };

    public async Task<string> SetAsync(bool on, CancellationToken ct)
    {
        await cursors.SetAsync(Cursor, on ? "on" : "off", ct);
        return await StatusAsync(ct);
    }

    public async Task<string> StatusAsync(CancellationToken ct)
    {
        var data = await feedback.LabeledVectorsAsync(ct);
        var state = await IsOnAsync(ct) ? "on" : "off";
        if (LearnedTaste.Prepare(data, options.Value.Taste).Model is null)
        {
            var up = data.Count(d => d.Liked);
            return string.Create(CultureInfo.InvariantCulture,
                $"Learned ranking is {state}. It needs {options.Value.Taste.MinVotes} votes with at least {LearnedTaste.MinPerClass} of each kind; there are {data.Count} ({up} up, {data.Count - up} down), so the current ranking is used.");
        }

        var (heuristic, learned) = LearnedTaste.CrossValidate(data);
        return string.Create(CultureInfo.InvariantCulture,
            $"Learned ranking is {state}. Held-out agreement with your {data.Count} votes: current {heuristic:0.00}, learned {learned:0.00}.");
    }

    /// <summary>A line for the weekly review when the learned model beats the current ranking and is still off; otherwise null.</summary>
    public async Task<string?> SuggestionAsync(CancellationToken ct)
    {
        if (await IsOnAsync(ct))
        {
            return null;
        }

        var data = await feedback.LabeledVectorsAsync(ct);
        if (LearnedTaste.Prepare(data, options.Value.Taste).Model is null)
        {
            return null;
        }

        var (heuristic, learned) = LearnedTaste.CrossValidate(data);
        return heuristic is { } h && learned is { } l && l - h >= TasteReport.RequiredGain
            ? string.Create(CultureInfo.InvariantCulture,
                $"The learned ranking now matches your votes better than the current one ({l:0.00} vs {h:0.00}, held out). Send /learn on to use it.")
            : null;
    }
}
