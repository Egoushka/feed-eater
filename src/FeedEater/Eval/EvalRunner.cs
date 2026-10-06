using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Llm;
using FeedEater.Storage;

namespace FeedEater.Eval;

public sealed record EvalSettings(int MaxItems, decimal MaxUsd, bool Reads, string? OutPath)
{
    /// <summary>Arguments: <c>--max-usd 0.25</c> (required), <c>--max-items 40</c>, <c>--reads</c>, <c>--out report.md</c>.</summary>
    public static (EvalSettings? Settings, string? Error) Parse(IReadOnlyList<string> args)
    {
        decimal? usd = null;
        var items = 40;
        var reads = false;
        string? output = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--max-usd" when i + 1 < args.Count && decimal.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0:
                    usd = d;
                    break;
                case "--max-items" when i + 1 < args.Count && int.TryParse(args[++i], out var n) && n > 0:
                    items = n;
                    break;
                case "--reads":
                    reads = true;
                    break;
                case "--out" when i + 1 < args.Count:
                    output = args[++i];
                    break;
                default:
                    return (null, $"unknown or incomplete argument '{args[i]}'. Usage: eval --max-usd <dollars> [--max-items <n>] [--reads] [--out <file.md>]");
            }
        }

        return usd is null
            ? (null, "--max-usd is required: the eval calls the models and must be told what it may spend. Usage: eval --max-usd <dollars> [--max-items <n>] [--reads] [--out <file.md>]")
            : (new EvalSettings(items, usd.Value, reads, output), null);
    }
}

/// <summary>A call came back with no cost: no gateway header and no <c>Llm:Prices</c> entry, so the spend limit cannot be enforced.</summary>
public sealed class CostUnknownException(string model) : Exception(
    $"The cost of {model} is unknown: the gateway sent no x-litellm-response-cost header and FeedEater:Llm:Prices has no entry for it, so --max-usd cannot be enforced. Set the model's price (USD per million tokens) and run again.");

/// <summary>
/// Re-runs the current triage (and optionally read) prompts over voted items and reports how they agree with the votes.
/// It reads the archive and calls the models; it writes nothing but usage rows (purpose eval-triage and eval-read).
/// </summary>
public sealed class EvalRunner(EvalStore store, ProfileStore profiles, LiteLlmClient llm, IOptions<FeedEaterOptions> options)
{
    private const int TriageMaxTokens = 200;
    private const int ReadMaxTokens = 600;

    public async Task<string> RunAsync(EvalSettings settings, CancellationToken ct)
    {
        var o = options.Value;
        var golden = await store.GoldenAsync(ct);
        var (totalUp, totalDown) = (golden.Count(g => g.Vote > 0), golden.Count(g => g.Vote < 0));
        var sample = Balanced(golden, settings.MaxItems);
        var profileList = await profiles.AllAsync(ct);
        var keys = profileList.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var about = Profiles.ProfileFile.LoadOrExample(o.ProfilePath).File.About;

        decimal spent = 0;
        string? stopped = null;
        var rows = new List<EvalRow>();
        foreach (var g in sample)
        {
            if (spent >= settings.MaxUsd)
            {
                stopped = $"the ${settings.MaxUsd:0.00} limit was reached after {rows.Count} of {sample.Count} items";
                break;
            }

            try
            {
                var (system, user) = Prompts.Triage(about, profileList, g.Title, g.Feed, g.Content, o.Caps.TriageChars, Clip(g.ExtraText, 1200));
                var triage = await llm.ChatAsync(o.Llm.TriageModel, system, user, TriageMaxTokens, "eval-triage", ct);
                spent += triage.Cost ?? throw new CostUnknownException(o.Llm.TriageModel);
                var t = LlmJson.Triage(triage.Content, keys);
                string? summary = null;
                if (settings.Reads && t.Relevance >= o.Caps.MinRelevance && spent < settings.MaxUsd)
                {
                    var (rs, ru) = Prompts.Read(about, profileList, null, g.Title, "", g.Feed, g.Content, o.Caps.ReadChars, null, Clip(g.ExtraText, o.Fetch.PageChars));
                    var read = await llm.ChatAsync(o.Llm.ReadModel, rs, ru, ReadMaxTokens, "eval-read", ct);
                    spent += read.Cost ?? throw new CostUnknownException(o.Llm.ReadModel);
                    summary = LlmJson.Read(read.Content, keys)?.Summary;
                }

                rows.Add(new EvalRow(g.Id, g.Title, g.Vote, g.OldRelevance, g.OldKind, g.OldSummary, t.Relevance, t.Kind, summary, $"{g.Title}\n{g.Content}\n{g.ExtraText}"));
            }
            catch (BudgetExceededException)
            {
                stopped = "the LiteLLM key's own budget is spent";
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                stopped = $"the model was unreachable ({ex.Message})";
                break;
            }
        }

        return EvalReport.Render(rows, totalUp, totalDown, o.Caps.MinRelevance, spent, settings.MaxUsd, settings.Reads, stopped);
    }

    /// <summary>Alternates up and down votes, newest first, so a small sample is as balanced as the data allows.</summary>
    internal static IReadOnlyList<GoldenRow> Balanced(IReadOnlyList<GoldenRow> golden, int max)
    {
        var up = golden.Where(g => g.Vote > 0).ToList();
        var down = golden.Where(g => g.Vote < 0).ToList();
        var picked = new List<GoldenRow>();
        for (var i = 0; picked.Count < max && (i < up.Count || i < down.Count); i++)
        {
            if (i < up.Count)
            {
                picked.Add(up[i]);
            }

            if (i < down.Count && picked.Count < max)
            {
                picked.Add(down[i]);
            }
        }

        return picked;
    }

    private static string? Clip(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}
