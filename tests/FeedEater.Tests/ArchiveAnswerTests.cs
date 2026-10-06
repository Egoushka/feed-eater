using FeedEater.Search;

namespace FeedEater.Tests;

public sealed class ArchiveAnswerTests
{
    [Fact]
    public void Markers_naming_no_source_are_dropped_and_the_rest_are_listed_once_in_order_of_first_use()
    {
        var (answer, cited) = ArchiveAnswer.Check(" B is fine [2], A too [1][2]; ignore [0] and [9]. ", 3);

        Assert.Equal("B is fine [2], A too [1][2]; ignore  and .", answer);
        Assert.Equal([2, 1], cited);
    }

    [Fact]
    public void An_answer_without_markers_cites_nothing() =>
        Assert.Empty(ArchiveAnswer.Check("The archive has nothing on that.", 5).Cited);
}
