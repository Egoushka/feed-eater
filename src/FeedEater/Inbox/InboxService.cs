using Microsoft.Extensions.Options;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Inbox;

public sealed class InboxService(InboxStore store, TelegramClient telegram, IOptions<FeedEaterOptions> options, TimeProvider? time = null) : IInbox
{
    public async Task<InboxPost> AddAsync(InboxEntry entry, OutMessage message, CancellationToken ct, long? replyToMessageId = null)
    {
        var id = await store.AddAsync(entry, (time ?? TimeProvider.System).GetUtcNow(), ct);
        var o = options.Value.Telegram;
        if (o.Token.Length == 0)
        {
            return new InboxPost(id, null);
        }

        return new InboxPost(id, await telegram.SendAsync(o.AllowedUserId, message, ct, replyToMessageId));
    }
}
