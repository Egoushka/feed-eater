using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Profiles;
using FeedEater.Storage;

namespace FeedEater.Digest;

/// <summary>What a read needs besides the item: who the reader is and which project keys exist. Built once per run or pass.</summary>
public sealed record ReadContext(string About, IReadOnlyList<Profile> Profiles, HashSet<string> Keys, bool IsExample);

/// <summary>The strong-model read of one item, shared by the digest and the library's background enrichment; stores the result.</summary>
public sealed class ItemReader(AnalysisStore analysis, ProfileStore profiles, LiteLlmClient llm, IOptions<FeedEaterOptions> options)
{
    private const int ReadMaxTokens = 600;

    public async Task<ReadContext> ContextAsync(CancellationToken ct)
    {
        var profileList = await profiles.AllAsync(ct);
        var (profile, isExample) = ProfileFile.LoadOrExample(options.Value.ProfilePath);
        return new ReadContext(profile.About, profileList, profileList.Select(p => p.Key).ToHashSet(StringComparer.Ordinal), isExample);
    }

    /// <summary>Null when the model's answer had no usable read. Model and transport errors propagate to the caller.</summary>
    public async Task<ReadResult?> ReadAsync(Candidate c, string text, ReadContext ctx, string? projectKey, string? facts, string? linked, CancellationToken ct)
    {
        var o = options.Value;
        var match = ctx.Profiles.FirstOrDefault(p => p.Key == projectKey);
        var (system, user) = Prompts.Read(ctx.About, ctx.Profiles, match, c.Title, c.Url, c.FeedTitle, text, o.Caps.ReadChars, facts, linked);
        var r = LlmJson.Read((await llm.ChatAsync(o.Llm.ReadModel, system, user, ReadMaxTokens, "read", ct)).Content, ctx.Keys);
        if (r is not null)
        {
            await analysis.SaveReadAsync(c.Id, r, o.Llm.ReadModel, ct);
        }

        return r;
    }
}
