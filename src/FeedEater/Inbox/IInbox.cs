namespace FeedEater.Inbox;

public interface IInbox
{
    /// <summary>Stores the entry; the stack and the web UI show it.</summary>
    Task<InboxPost> AddAsync(InboxEntry entry, CancellationToken ct);
}
