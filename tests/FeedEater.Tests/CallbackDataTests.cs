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
        Assert.IsType<NoopCallback>(CallbackData.Parse(CallbackData.Noop));
    }

    [Fact]
    public void Follow_and_unfollow_round_trip_and_only_follow_names_an_item()
    {
        Assert.Equal(new FollowCallback(7), CallbackData.Parse(CallbackData.Follow(7)));
        Assert.Equal(new UnfollowCallback(3), CallbackData.Parse(CallbackData.Unfollow(3)));
        Assert.Equal(7, CallbackData.ItemIdOf("f:7"));
        Assert.Null(CallbackData.ItemIdOf("u:3"));
        Assert.Equal(3, CallbackData.FollowIdOf("u:3"));
        Assert.Null(CallbackData.FollowIdOf("f:7"));
        Assert.Null(CallbackData.FollowIdOf("v:3:u"));
        Assert.Null(CallbackData.Parse("u:abc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x:1")]
    [InlineData("v:abc:u")]
    [InlineData("v:1:z")]
    [InlineData("i:-3")]
    public void Rejects_anything_else(string? data) => Assert.Null(CallbackData.Parse(data));

    [Fact]
    public void Fits_telegrams_64_byte_limit_for_the_largest_id() =>
        Assert.True(Encoding.UTF8.GetByteCount(CallbackData.Vote(long.MaxValue, -1)) <= 64);

    [Fact]
    public void Follow_buttons_fit_telegrams_64_byte_limit_for_the_largest_id() =>
        Assert.True(Encoding.UTF8.GetByteCount(CallbackData.Follow(long.MaxValue)) <= 64 && Encoding.UTF8.GetByteCount(CallbackData.Unfollow(long.MaxValue)) <= 64);
}
