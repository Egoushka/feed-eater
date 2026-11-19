using FeedEater.Approvals;
using FeedEater.Storage;

namespace FeedEater.Inbox;

public sealed class InboxService(InboxStore store, TimeProvider? time = null, ApprovalsClient? approvals = null) : IInbox
{
    public async Task<InboxPost> AddAsync(InboxEntry entry, CancellationToken ct)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var id = await store.AddAsync(entry, now, ct);
        if (entry.Kind == InboxKinds.Notice && approvals is { Enabled: true } && entry.Payload?["text"]?.GetValue<string>() is { Length: > 0 } text)
        {
            // Notices are the one kind that must reach the owner without a swipe session; the rest wait in the stack.
            await approvals.NotifyAsync(new ApprovalsNote($"{ApprovalsClient.Source}:{entry.Key}", text, "", text, entry.ExpiresAt ?? now.AddDays(2)), ct);
        }

        return new InboxPost(id);
    }
}
