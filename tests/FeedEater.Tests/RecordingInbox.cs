using FeedEater.Inbox;

namespace FeedEater.Tests;

/// <summary>An inbox that records what a job adds and can be told to fail, for jobs whose only output is an inbox entry.</summary>
internal sealed class RecordingInbox(IInbox inner, Func<bool>? fails = null) : IInbox
{
    public List<InboxEntry> Entries { get; } = [];

    public async Task<InboxPost> AddAsync(InboxEntry entry, CancellationToken ct)
    {
        if (fails?.Invoke() == true)
        {
            throw new InvalidOperationException("inbox down");
        }

        var post = await inner.AddAsync(entry, ct);
        Entries.Add(entry);
        return post;
    }
}
