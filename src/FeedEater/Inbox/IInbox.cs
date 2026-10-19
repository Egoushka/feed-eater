using FeedEater.Telegram;

namespace FeedEater.Inbox;

public interface IInbox
{
    /// <summary>Stores the entry, then sends <paramref name="telegram"/> while the bot is configured. A failed send keeps the row and rethrows.</summary>
    Task<InboxPost> AddAsync(InboxEntry entry, OutMessage telegram, CancellationToken ct, long? replyToMessageId = null);
}
