using Dapper;
using FeedEater.Storage;

namespace FeedEater.Swipe;

public static class Labels
{
    public const string Surface = "feed-eater";
    public const string Kept = "kept";
    public const string Dismissed = "dismissed";
    public const string Snoozed = "snoozed";
    public const string Opened = "opened";
    public const string Promoted = "promoted";
    public const string Ignored = "ignored";

    /// <summary>The label an act makes; null for an act with none (so a new act is noticed by the test that lists every act).</summary>
    public static string? Of(string act) => act switch
    {
        "like" or "save" or "good" or "keep" or "pick_a" or "pick_b" or "idea_keep" => Kept,
        "dislike" or "dismiss" or "drop" or "stop" or "remove" or "read" or "idea_archive" => Dismissed,
        "again" or "idea_later" => Snoozed,
        "open" => Opened,
        "idea" or "send" or "share" or "idea_promote" => Promoted,
        _ => null,
    };
}

/// <summary>Append-only label rows: ts, surface, item kind, item id, action. The same keys as wheelhouse's ledger.</summary>
public sealed class LabelStore(FeedDb db)
{
    public async Task AddAsync(string itemKind, string itemId, string action, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "insert into labels (surface, item_kind, item_id, action) values (@surface, @itemKind, @itemId, @action)",
            new { surface = Labels.Surface, itemKind, itemId, action }, cancellationToken: ct));
    }
}
