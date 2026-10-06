using System.Text;
using FeedEater.Telegram;

namespace FeedEater.Tests;

public sealed class CallbackDataTests
{
    [Fact]
    public void Round_trips_votes_ideas_and_noop()
    {
        Assert.Equal(new VoteCallback(5, 1), CallbackData.Parse(CallbackData.Vote(5, 1)));
        Assert.Equal(new VoteCallback(5, -1), CallbackData.Parse(CallbackData.Vote(5, -1)));
        Assert.Equal(new IdeaCallback(9), CallbackData.Parse(CallbackData.Idea(9)));
        Assert.Equal(new SaveCallback(7), CallbackData.Parse(CallbackData.Save(7)));
        Assert.Equal(new DuelCallback(12, 'a'), CallbackData.Parse(CallbackData.Duel(12, 'a')));
        Assert.Equal(new DuelCallback(12, 'b'), CallbackData.Parse(CallbackData.Duel(12, 'b')));
        Assert.Equal(new DuelCallback(12, 's'), CallbackData.Parse(CallbackData.Duel(12, 's')));
        Assert.IsType<NoopCallback>(CallbackData.Parse(CallbackData.Noop));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x:1")]
    [InlineData("v:abc:u")]
    [InlineData("v:1:z")]
    [InlineData("i:-3")]
    [InlineData("d:1:x")]
    [InlineData("d:abc:a")]
    [InlineData("d:-1:a")]
    [InlineData("d:1")]
    public void Rejects_anything_else(string? data) => Assert.Null(CallbackData.Parse(data));

    [Fact]
    public void A_duel_button_names_no_item_so_a_reply_to_its_message_is_not_an_item_reply() =>
        Assert.Null(CallbackData.ItemIdOf(CallbackData.Duel(5, 'a')));

    [Fact]
    public void Fits_telegrams_64_byte_limit_for_the_largest_id() =>
        Assert.True(Encoding.UTF8.GetByteCount(CallbackData.Vote(long.MaxValue, -1)) <= 64);

    [Fact]
    public void A_duel_button_fits_telegrams_64_byte_limit_for_the_largest_id() =>
        Assert.True(Encoding.UTF8.GetByteCount(CallbackData.Duel(long.MaxValue, 's')) <= 64);
}
