using FeedEater.Duels;
using FeedEater.Ranking;

namespace FeedEater.Tests;

public sealed class DuelPickerTests
{
    private static readonly IReadOnlySet<(long, long)> None = new HashSet<(long, long)>();

    /// <summary>Item <paramref name="rank"/> has id rank + 1, its own story and score rank, so rank is its place in the pool.</summary>
    private static DuelItem Item(int rank, float[]? embedding = null, long? story = null) =>
        new() { Id = rank + 1, StoryId = story ?? rank + 1, Score = rank, Embedding = embedding ?? TestVectors.OneHot(rank) };

    private static List<DuelItem> Pool(int count) => Enumerable.Range(0, count).Select(r => Item(r)).ToList();

    [Fact]
    public void Fewer_than_six_items_gives_no_duel_and_six_give_the_two_at_the_middle()
    {
        Assert.Null(DuelPicker.Pick(Pool(5), None));

        var (first, second) = DuelPicker.Pick(Pool(6), None)!.Value;

        Assert.Equal([3L, 4L], [first.Id, second.Id]);   // ranks 2 and 3 of 0..5
    }

    [Fact]
    public void Only_the_middle_band_is_used_and_the_least_alike_pair_in_it_wins()
    {
        // Ranks 0..10: the band is ranks 4, 5 and 6. Rank 5 lies between 4 and 6, so (4, 6) is the least alike pair of the band,
        // although every pair that reaches outside the band is just as unlike.
        var pool = Pool(11);
        pool[5] = Item(5, Vectors.Normalize([.. TestVectors.OneHot(4).Zip(TestVectors.OneHot(6), (x, y) => x + y)]));

        var (first, second) = DuelPicker.Pick(pool, None)!.Value;

        Assert.Equal([5L, 7L], [first.Id, second.Id]);
    }

    [Fact]
    public void The_band_is_by_score_not_by_input_order()
    {
        var pool = Pool(11).AsEnumerable().Reverse().ToList();

        var (first, second) = DuelPicker.Pick(pool, None)!.Value;

        Assert.Equal([5L, 6L], [first.Id, second.Id]);   // all pairs equally unlike: the first of the band in score order
    }

    [Fact]
    public void A_pair_already_offered_is_left_out_and_when_none_is_left_there_is_no_duel()
    {
        var pool = Pool(6);

        var next = DuelPicker.Pick(Pool(11), new HashSet<(long, long)> { (5, 6) })!.Value;

        Assert.Equal([5L, 7L], [next.First.Id, next.Second.Id]);
        Assert.Null(DuelPicker.Pick(pool, new HashSet<(long, long)> { DuelPicker.Pair(4, 3) }));
    }

    [Fact]
    public void One_item_per_story_counts_toward_the_pool_and_the_first_in_input_order_stays()
    {
        var sameStory = Pool(6);
        sameStory[1] = Item(1, story: 1);

        Assert.Null(DuelPicker.Pick(sameStory, None));   // five stories

        var repeat = new DuelItem { Id = 8, StoryId = 1, Score = 3.5, Embedding = TestVectors.OneHot(8) };
        var (first, second) = DuelPicker.Pick([.. Pool(7), repeat], None)!.Value;

        Assert.Equal([4L, 5L], [first.Id, second.Id]);   // item 8 scores in the middle, but its story is already in the pool
    }

    [Fact]
    public void A_pool_too_small_for_a_band_of_two_takes_the_two_ranks_at_its_middle()
    {
        var (first, second) = DuelPicker.Pick(Pool(7), None)!.Value;   // 40th to 60th percentile holds only rank 3

        Assert.Equal([4L, 5L], [first.Id, second.Id]);
    }
}
