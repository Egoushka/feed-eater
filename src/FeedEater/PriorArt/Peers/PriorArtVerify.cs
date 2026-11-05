using System.Globalization;

namespace FeedEater.PriorArt.Peers;

/// <summary>Where a cited file can be looked up; a seam so tests need no GitHub.</summary>
public interface IFileLines
{
    Task<int?> LineCountAsync(string owner, string repo, string path, string sha, CancellationToken ct);
}

public sealed record VerifyFailure(Guid FactId, string Repo, string Commit, string Path, string? Lines, string Why);

public sealed record VerifyReport(int Sampled, int Resolved, IReadOnlyList<VerifyFailure> Failures);

/// <summary>Checks that the code evidence of random live facts is real: the file exists at the cited commit and has the cited lines.</summary>
public sealed class PriorArtVerify(PriorArtStore store, IFileLines files)
{
    public async Task<VerifyReport> SampleAsync(int count, CancellationToken ct)
    {
        var sample = await store.SampleCodeEvidenceAsync(count, ct);
        var failures = new List<VerifyFailure>();
        foreach (var e in sample)
        {
            var slash = e.Repo.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
            {
                failures.Add(Fail(e, "repo is not owner/name"));
                continue;
            }

            int? count0;
            try
            {
                count0 = await files.LineCountAsync(e.Repo[..slash], e.Repo[(slash + 1)..], e.Path, e.Commit, ct);
            }
            catch (HttpRequestException ex)
            {
                failures.Add(Fail(e, ex.Message));
                continue;
            }

            if (count0 is null)
            {
                failures.Add(Fail(e, "no such file at that commit"));
            }
            else if (LastLine(e.Lines) is { } last && last > count0)
            {
                failures.Add(Fail(e, $"file has {count0} lines"));
            }
        }

        return new VerifyReport(sample.Count, sample.Count - failures.Count, failures);
    }

    private static VerifyFailure Fail(CodeEvidence e, string why) => new(e.FactId, e.Repo, e.Commit, e.Path, e.Lines, why);

    /// <summary>The last line of <c>12</c> or <c>12-30</c>; null when the evidence cites no lines.</summary>
    internal static int? LastLine(string? lines)
    {
        if (string.IsNullOrWhiteSpace(lines))
        {
            return null;
        }

        var last = lines[(lines.LastIndexOf('-') + 1)..];
        return int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
