using System.Text.Json;
using System.Text.Json.Nodes;
using Switchknife.Facts;

namespace FeedEater.PriorArt;

/// <summary>Why a fact is believed: <c>ExplainAsync</c> of the library, with the entity names the library does not hold.</summary>
public sealed class PriorArtExplain(PriorArtStore store)
{
    public async Task<JsonNode?> ExplainAsync(Guid factId, CancellationToken ct)
    {
        await store.EnsureReadyAsync(ct);
        var why = await store.Facts.ExplainAsync(factId, ct);
        if (why is null)
        {
            return null;
        }

        var related = why.Successors.Concat(why.Predecessors).Select(v => v.Fact.Id).Append(factId).Distinct().ToArray();
        var names = (await store.DetailsAsync(related, ct)).ToDictionary(d => d.Id);

        JsonObject Version(FactVersion v) => new()
        {
            ["id"] = v.Fact.Id,
            ["predicate"] = v.Fact.Predicate,
            ["text"] = v.Fact.Text,
            ["tool"] = names.TryGetValue(v.Fact.Id, out var n) ? n.SubjectName : null,
            ["tool_ref"] = names.TryGetValue(v.Fact.Id, out var r) ? r.SubjectRef : null,
            ["object"] = names.TryGetValue(v.Fact.Id, out var o) ? o.ObjectName : null,
            ["valid_from"] = v.Fact.ValidFrom,
            ["valid_to"] = v.Fact.ValidTo,
            ["recorded_at"] = v.Fact.RecordedAt,
            ["retired_at"] = v.RetiredAt,
            ["retired_reason"] = v.RetiredReason,
            ["superseded_by"] = v.SupersededBy,
        };

        return new JsonObject
        {
            ["fact"] = Version(why.Version),
            ["trust"] = why.Trust.ToString(),
            ["sources"] = new JsonArray(why.Sources.Select(s => (JsonNode)new JsonObject
            {
                ["episode"] = s.EpisodeId,
                ["kind"] = s.SourceKind,
                ["ref"] = s.SourceRef,
                ["content_hash"] = s.ContentHash,
                ["trust"] = s.Trust.ToString(),
                ["locator"] = Locator(s.LocatorJson),
                ["quote"] = s.Quote,
                ["occurred_at"] = s.OccurredAt,
                ["recorded_at"] = s.RecordedAt,
                ["extractor"] = s.Extractor,
                ["model"] = s.Model,
            }).ToArray()),
            ["retraction"] = why.Retraction is { } x ? new JsonObject { ["retracted_at"] = x.RetractedAt, ["restored_at"] = x.RestoredAt } : null,
            ["successors"] = new JsonArray(why.Successors.Select(v => (JsonNode)Version(v)).ToArray()),
            ["predecessors"] = new JsonArray(why.Predecessors.Select(v => (JsonNode)Version(v)).ToArray()),
        };
    }

    private static JsonNode? Locator(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
