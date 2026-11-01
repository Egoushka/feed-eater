using Microsoft.Extensions.Options;
using FeedEater.Approvals;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Inbox;

public sealed class InboxService(InboxStore store, TelegramClient telegram, IOptions<FeedEaterOptions> options, TimeProvider? time = null, ApprovalsClient? approvals = null) : IInbox
{
    public async Task<InboxPost> AddAsync(InboxEntry entry, OutMessage message, CancellationToken ct, long? replyToMessageId = null)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var id = await store.AddAsync(entry, now, ct);
        if (entry.Kind == InboxKinds.Notice && approvals is { Enabled: true } && entry.Payload?["text"]?.GetValue<string>() is { Length: > 0 } text)
        {
            // Notices are the one kind that must reach the owner without a swipe session; the rest wait in the stack.
            await approvals.NotifyAsync(new ApprovalsNote($"{ApprovalsClient.Source}:{entry.Key}", text, "", text, entry.ExpiresAt ?? now.AddDays(2)), ct);
        }

        var o = options.Value.Telegram;
        if (o.Token.Length == 0)
        {
            return new InboxPost(id, null);
        }

        return new InboxPost(id, await telegram.SendAsync(o.AllowedUserId, message, ct, replyToMessageId));
    }
}
