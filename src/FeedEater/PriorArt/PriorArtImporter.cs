using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Switchknife.Facts;
using FeedEater.Llm;

namespace FeedEater.PriorArt;

public enum ImportFormat
{
    /// <summary>A chargehand result/v1 document: its claims are the facts, its evidence is their locator.</summary>
    ResultV1,

    /// <summary>An audit markdown file with pipe tables (tool, what, evidence, verdict).</summary>
    Audit,

    /// <summary>A <c>personal/research</c> notes file.</summary>
    Notes,

    /// <summary>A CSV of GitHub counters (repo, stars, pushed, license) for tools the base already knows.</summary>
    Snapshot,
}

/// <summary>What was asked for is wrong: the caller fixes the input, nothing was stored.</summary>
public sealed class PriorArtInputException(string message) : Exception(message);

/// <param name="Format">What <paramref name="Content"/> is.</param>
/// <param name="Content">The document text.</param>
/// <param name="SourceRef">Audit, notes and snapshot: the file's path, which names the source across versions. Result: ignored.</param>
/// <param name="Tool">Result: the tool the run read, <c>owner/name</c> or a tool reference such as <c>gh:owner/name</c>.</param>
/// <param name="Problem">Result: the question the run answered, as a slug or words.</param>
/// <param name="Approach">Result: the approach an untagged claim is filed under; without it an untagged claim stays a statement.</param>
/// <param name="Subject">Audit: the tool reference a table with no tool column is about.</param>
/// <param name="Commit">Result: the commit the run read when its evidence names none.</param>
/// <param name="SourceDate">When the source was written; defaults to the date in the file name, else today.</param>
/// <param name="Confidence">0 to 1 for every fact of the document; defaults to 0.6, or 0.3 when unverified.</param>
/// <param name="Unverified">The source's facts were not rechecked: marked so in each locator and stored with low confidence.</param>
public sealed record ImportRequest(
    ImportFormat Format, string Content, string? SourceRef = null, string? Tool = null, string? Problem = null, string? Approach = null,
    string? Subject = null, string? Commit = null, DateOnly? SourceDate = null, double? Confidence = null, bool Unverified = false);

public sealed record ImportReport(
    string SourceKind, string SourceRef, bool Duplicate, int Facts, int Written, int Corroborated, int Outranked, int Embedded, int EmbedPending, IReadOnlyList<string> Skipped);

public sealed partial class PriorArtImporter(PriorArtStore store, IPriorArtEmbedder embedder, TimeProvider time)
{
    public const int MaxContent = 1_000_000;
    private const int MaxClaims = 200;
    private const int EmbedBatch = 64;
    private const int MinEvidence = 15;
    private const double DefaultConfidence = 0.6;
    private const double UnverifiedConfidence = 0.3;

    [GeneratedRegex(@"^\s*\[(uses|limitation|addresses|statement)(?::\s*([A-Za-z0-9 _-]+))?\]\s*(.+)$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ClaimTag();

    [GeneratedRegex(@"^[0-9a-f]{7,40}$")]
    private static partial Regex CommitSha();

    private sealed record Plan(NewEpisode Episode, List<NewFact> Facts, List<string> Skipped);

    public async Task<ImportReport> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new PriorArtInputException("content is empty.");
        }

        if (request.Content.Length > MaxContent)
        {
            throw new PriorArtInputException($"content is over {MaxContent / 1000} KB.");
        }

        if (request.Confidence is < 0 or > 1)
        {
            throw new PriorArtInputException("confidence is 0 to 1.");
        }

        await store.EnsureReadyAsync(ct);
        var plan = request.Format switch
        {
            ImportFormat.ResultV1 => await PlanResultAsync(request, ct),
            ImportFormat.Audit => await PlanAuditAsync(request, ct),
            ImportFormat.Notes => await PlanNotesAsync(request, ct),
            ImportFormat.Snapshot => await PlanSnapshotAsync(request, ct),
            _ => throw new PriorArtInputException("unknown format."),
        };

        var result = await store.Facts.IngestAsync(plan.Episode, plan.Facts, ct);
        var (embedded, pending) = await EmbedAsync(ct);
        return new ImportReport(
            plan.Episode.SourceKind, plan.Episode.SourceRef, result.Duplicate, plan.Facts.Count, result.Written, result.Corroborated, result.Outranked, embedded, pending, plan.Skipped);
    }

    /// <summary>Facts stay searchable by words when the embedder is down; a later import or <c>prior-art embed</c> catches them up.</summary>
    public async Task<(int Embedded, int Pending)> EmbedAsync(CancellationToken ct)
    {
        try
        {
            return (await store.EmbedPendingAsync(embedder, EmbedBatch, ct), 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or BudgetExceededException or InvalidOperationException)
        {
            return (0, (await store.StatsAsync(embedder.Model, ct)).Unembedded);
        }
    }

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static DateTimeOffset At(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static string Json(params (string Key, object? Value)[] fields) =>
        JsonSerializer.Serialize(fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value));

    private static double ConfidenceOf(ImportRequest r) => r.Confidence ?? (r.Unverified ? UnverifiedConfidence : DefaultConfidence);

    private static string SourceRefOf(ImportRequest r) =>
        string.IsNullOrWhiteSpace(r.SourceRef) ? throw new PriorArtInputException("source_ref is required: the file's path, which names the source across versions.") : r.SourceRef.Trim();

    private static NewFact Statement(Guid subject, string text, DateTimeOffset from, string locator, string? quote = null)
    {
        var cut = PriorArtText.Cut(text);
        return new NewFact(subject, PriorArtSchema.Statement, cut, from, Text: cut, LocatorJson: locator, Quote: quote);
    }

    private static string WithName(string name, string text) => text.Contains(name, StringComparison.OrdinalIgnoreCase) ? text : $"{name}: {text}";

    // ---- audit ----

    private async Task<Plan> PlanAuditAsync(ImportRequest r, CancellationToken ct)
    {
        var source = SourceRefOf(r);
        var date = r.SourceDate ?? PriorArtText.DateInName(source) ?? Today;
        var from = At(date);
        var defaultRef = r.Subject;
        if (defaultRef is not null && !PriorArtText.IsToolRef(defaultRef))
        {
            throw new PriorArtInputException("subject is a tool reference such as gh:owner/name.");
        }

        var rows = AuditParser.Parse(r.Content, defaultRef is null ? null : NameOfRef(defaultRef));
        var facts = new List<NewFact>();
        var skipped = new List<string>();
        var verdictSeen = new HashSet<Guid>();
        foreach (var row in rows)
        {
            var toolRef = defaultRef is not null && row.Name == NameOfRef(defaultRef) ? defaultRef : row.Url is null ? null : PriorArtText.RefFromUrl(row.Url);
            toolRef ??= "name:" + PriorArtText.Slug(row.Name);
            if (!PriorArtText.IsToolRef(toolRef))
            {
                skipped.Add($"line {row.Line}: no usable name for a tool");
                continue;
            }

            var tool = await store.ToolAsync(row.Name, toolRef, ct);
            string Locator(string column) => Json(
                ("file", source), ("line", row.Line), ("section", row.Section), ("row", row.Index.Length > 0 ? row.Index : null), ("column", column),
                ("url", row.Url), ("retrieved", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("evidence", row.LabelRow ? row.Evidence : null), ("confidence", ConfidenceOf(r)), ("verified", r.Unverified ? false : null));
            if (row.Description is { } what)
            {
                facts.Add(Statement(tool, WithName(row.Name, what), from, Locator("what")));
            }

            if (!row.LabelRow && row.Evidence is { Length: >= MinEvidence } evidence)
            {
                facts.Add(Statement(tool, $"{row.Name} evidence: {evidence}", from, Locator("evidence")));
            }

            if (row.Verdict is { } verdict && verdictSeen.Add(tool))
            {
                var text = PriorArtText.Cut(row.Reason is { Length: > 0 } why ? $"{row.Name} assessed {verdict}: {why}" : $"{row.Name} assessed {verdict}");
                facts.Add(new NewFact(tool, "assessed", verdict, from, Text: text, LocatorJson: Locator("verdict")));
            }
        }

        if (facts.Count == 0)
        {
            skipped.Add("no table with a tool column and a description, evidence or verdict column");
        }

        return new Plan(new NewEpisode(PriorArtSchema.SourceAudit, source, PriorArtText.Sha256(r.Content), from, PriorArtSchema.TrustOf(PriorArtSchema.SourceAudit)), facts, skipped);
    }

    private static string NameOfRef(string toolRef) => toolRef[(toolRef.LastIndexOf('/') + 1)..].Split(':').Last();

    // ---- research notes ----

    private async Task<Plan> PlanNotesAsync(ImportRequest r, CancellationToken ct)
    {
        var source = SourceRefOf(r);
        var facts = new List<NewFact>();
        var skipped = new List<string>();
        foreach (var entry in NotesParser.Parse(r.Content))
        {
            if (PriorArtText.RefFromUrl(entry.Url) is not { } toolRef)
            {
                skipped.Add($"line {entry.Line}: URL is not http(s)");
                continue;
            }

            var name = entry.Title.IndexOf(':') is var colon and >= 2 and <= 40 ? entry.Title[..colon].Trim() : NameOfRef(toolRef);
            var tool = await store.ToolAsync(name, toolRef, ct);
            var marketing = entry.Label?.Contains("marketing", StringComparison.OrdinalIgnoreCase) == true;
            var date = entry.SourceDate ?? entry.Retrieved ?? Today;
            var locator = Json(
                ("file", source), ("line", entry.Line), ("url", entry.Url),
                ("source_date", entry.SourceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "UNKNOWN"),
                ("retrieved", (entry.Retrieved ?? Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("label", marketing ? "marketing" : null), ("start_defaulted", entry.SourceDate is null ? true : null),
                ("confidence", (r.Confidence ?? DefaultConfidence) * (marketing ? 0.5 : 1)));
            var text = WithName(name, entry.Fact) + (marketing ? " [marketing]" : "");
            facts.Add(Statement(tool, text, At(date), locator, PriorArtText.Words(entry.Quote)));
        }

        if (facts.Count == 0)
        {
            skipped.Add("no entry with a URL and a fact");
        }

        return new Plan(new NewEpisode(PriorArtSchema.SourceWebNote, source, PriorArtText.Sha256(r.Content), null, PriorArtSchema.TrustOf(PriorArtSchema.SourceWebNote)), facts, skipped);
    }

    // ---- snapshot ----

    private async Task<Plan> PlanSnapshotAsync(ImportRequest r, CancellationToken ct)
    {
        var source = SourceRefOf(r);
        var date = r.SourceDate ?? PriorArtText.DateInName(source) ?? Today;
        var from = At(date);
        var facts = new List<NewFact>();
        var unknown = 0;
        foreach (var row in SnapshotCsv.Parse(r.Content))
        {
            if (await store.FindAsync("gh:" + row.Repo, ct) is not { } tool)
            {
                unknown++;
                continue;
            }

            string Locator() => Json(("file", source), ("repo", row.Repo), ("retrieved", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("confidence", 1.0));
            var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (row.Stars is { } stars)
            {
                facts.Add(new NewFact(tool, "stars", stars.ToString(CultureInfo.InvariantCulture), from, Text: $"{row.Repo} had {stars:N0} stars on {day}.", LocatorJson: Locator()));
            }

            if (row.Pushed is { } pushed)
            {
                facts.Add(new NewFact(tool, "last_push", pushed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), from, Text: $"{row.Repo} was last pushed to on {pushed:yyyy-MM-dd} (seen {day}).", LocatorJson: Locator()));
            }

            if (row.License is { } license)
            {
                facts.Add(new NewFact(tool, "license", license, from, Text: $"{row.Repo} is licensed {license} (seen {day}).", LocatorJson: Locator()));
            }
        }

        var skipped = new List<string>();
        if (unknown > 0)
        {
            skipped.Add($"{unknown} repositories are not tools in the base yet");
        }

        return new Plan(new NewEpisode(PriorArtSchema.SourceSnapshot, source, PriorArtText.Sha256(r.Content), from, PriorArtSchema.TrustOf(PriorArtSchema.SourceSnapshot)), facts, skipped);
    }

    // ---- chargehand result/v1 ----

    private sealed record Evidence(string Id, string Kind, string Locator, string? Commit)
    {
        public (string Path, string? Lines) Parts()
        {
            var colon = Locator.LastIndexOf(':');
            return colon > 0 && Regex.IsMatch(Locator[(colon + 1)..], @"^\d+(-\d+)?$") ? (Locator[..colon], Locator[(colon + 1)..]) : (Locator, null);
        }
    }

    private async Task<Plan> PlanResultAsync(ImportRequest r, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(r.Content);
        }
        catch (JsonException)
        {
            throw new PriorArtInputException("content is not JSON.");
        }

        if (FeedEater.Json.Str(root, "contract_version") != "result/v1")
        {
            throw new PriorArtInputException("content is not a result/v1 document.");
        }

        if (FeedEater.Json.Str(root, "status") != "completed")
        {
            throw new PriorArtInputException("only a completed run is stored.");
        }

        var toolRef = ToolRefOf(r.Tool);
        var problem = PriorArtText.Slug(r.Problem ?? "");
        if (problem.Length == 0)
        {
            throw new PriorArtInputException("problem is required: the question the run answered, in words or as a slug.");
        }

        var defaultApproach = string.IsNullOrWhiteSpace(r.Approach) ? null : PriorArtText.Slug(r.Approach);
        var run = FeedEater.Json.Str(root, "task_id");
        var evidence = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        if (root.TryGetProperty("evidence", out var evidenceList) && evidenceList.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in evidenceList.EnumerateArray())
            {
                if (FeedEater.Json.Str(e, "id") is { } id && FeedEater.Json.Str(e, "kind") is { } kind && FeedEater.Json.Str(e, "locator") is { } locator)
                {
                    evidence[id] = new Evidence(id, kind, locator, FeedEater.Json.Str(e, "commit"));
                }
            }
        }

        var claims = root.TryGetProperty("claims", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];
        var skipped = new List<string>();
        if (claims.Count > MaxClaims)
        {
            skipped.Add($"{claims.Count - MaxClaims} claims over the limit of {MaxClaims}");
            claims = claims.Take(MaxClaims).ToList();
        }

        var commit = r.Commit ?? evidence.Values.Where(e => e.Kind == "file" && e.Commit is not null).GroupBy(e => e.Commit).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (commit is not null && !CommitSha().IsMatch(commit))
        {
            throw new PriorArtInputException("commit is a git sha.");
        }

        var name = NameOfRef(toolRef);
        var tool = await store.ToolAsync(name, toolRef, ct);
        var problemId = await store.ProblemAsync(problem, ct);
        var from = At(r.SourceDate ?? Today);
        var repo = toolRef.StartsWith("gh:", StringComparison.Ordinal) ? toolRef[3..] : null;
        var facts = new List<NewFact>();
        var linked = new HashSet<string>();
        for (var i = 0; i < claims.Count; i++)
        {
            var claim = claims[i];
            var text = FeedEater.Json.Str(claim, "text")?.Trim() ?? "";
            var refs = claim.TryGetProperty("evidence", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null && evidence.ContainsKey(x)).Select(x => evidence[x!]).ToList()
                : [];
            if (text.Length == 0 || refs.Count == 0)
            {
                skipped.Add($"claim {i + 1}: " + (text.Length == 0 ? "no text" : "its evidence ids are not in the evidence list"));
                continue;
            }

            var primary = refs.FirstOrDefault(e => e.Kind == "file") ?? refs[0];
            var (path, lines) = primary.Parts();
            var confidence = Math.Clamp(claim.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.5, 0, 1);
            var support = FeedEater.Json.Str(claim, "support");
            if (support is "partial" or "unchecked")
            {
                confidence *= 0.5;
            }

            var locator = Json(
                ("repo", repo), ("commit", primary.Commit ?? commit), ("path", primary.Kind == "file" ? path : null), ("lines", primary.Kind == "file" ? lines : null),
                ("url", primary.Kind == "url" ? primary.Locator : null), ("kind", primary.Kind), ("run", run), ("problem", problem), ("support", support),
                ("confidence", Math.Round(confidence, 4)), ("start_defaulted", r.SourceDate is null ? true : null),
                ("also", refs.Count > 1 ? refs.Where(e => e != primary).Select(e => new { e.Kind, e.Locator, e.Commit }).ToArray() : null));

            var (predicate, argument, body) = Tagged(text, defaultApproach);
            body = WithName(name, body);
            switch (predicate)
            {
                case "uses":
                    var approach = await store.ApproachAsync(argument!, ct);
                    facts.Add(new NewFact(tool, "uses", null, from, Text: PriorArtText.Cut(body), LocatorJson: locator, ObjectEntity: approach));
                    if (linked.Add(argument!))
                    {
                        var approachName = argument!.Replace('-', ' ');
                        facts.Add(new NewFact(approach, "approach_for", null, from, Text: PriorArtText.Cut($"{approachName} is an approach for {problem.Replace('-', ' ')}"),
                            LocatorJson: locator, ObjectEntity: problemId));
                    }

                    break;
                case "addresses":
                    var target = argument is null ? problemId : await store.ProblemAsync(argument, ct);
                    facts.Add(new NewFact(tool, "addresses", null, from, Text: PriorArtText.Cut(body), LocatorJson: locator, ObjectEntity: target));
                    break;
                case "limitation":
                    var cut = PriorArtText.Cut(body);
                    facts.Add(new NewFact(tool, "limitation", cut, from, Text: cut, LocatorJson: locator));
                    break;
                default:
                    facts.Add(Statement(tool, body, from, locator));
                    break;
            }
        }

        if (facts.Count == 0)
        {
            skipped.Add("no claim could be stored");
        }

        var hash = commit ?? PriorArtText.Sha256(string.Join('\n', claims.Select(x => x.GetRawText())));
        var episode = new NewEpisode(PriorArtSchema.SourceRepoRead, $"{toolRef}#{problem}", hash, null, PriorArtSchema.TrustOf(PriorArtSchema.SourceRepoRead));
        return new Plan(episode, facts, skipped);
    }

    /// <summary>A claim starts with <c>[uses:approach-slug]</c>, <c>[limitation]</c>, <c>[addresses]</c> or <c>[statement]</c>; without a tag it is filed under the default approach, else it stays a statement.</summary>
    private static (string Predicate, string? Argument, string Body) Tagged(string text, string? defaultApproach)
    {
        if (ClaimTag().Match(text) is not { Success: true } m)
        {
            return defaultApproach is null ? (PriorArtSchema.Statement, null, text) : ("uses", defaultApproach, text);
        }

        var predicate = m.Groups[1].Value.ToLowerInvariant();
        var argument = m.Groups[2].Success ? PriorArtText.Slug(m.Groups[2].Value) : null;
        if (predicate == "uses")
        {
            argument = argument is { Length: > 0 } ? argument : defaultApproach;
            return argument is null ? (PriorArtSchema.Statement, null, m.Groups[3].Value.Trim()) : ("uses", argument, m.Groups[3].Value.Trim());
        }

        return (predicate, argument is { Length: > 0 } ? argument : null, m.Groups[3].Value.Trim());
    }

    private static string ToolRefOf(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            throw new PriorArtInputException("tool is required: owner/name or a tool reference such as gh:owner/name.");
        }

        var value = tool.Trim();
        if (Regex.IsMatch(value, @"^[\w.-]+/[\w.-]+$"))
        {
            value = "gh:" + value;
        }

        return PriorArtText.IsToolRef(value) ? value : throw new PriorArtInputException("tool is owner/name or a tool reference such as gh:owner/name, pkg:nuget/x, url:host/path.");
    }
}
