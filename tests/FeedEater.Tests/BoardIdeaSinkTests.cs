using FeedEater.Issues;

namespace FeedEater.Tests;

public sealed class BoardIdeaSinkTests
{
    private sealed class FakeRunner(int exit = 0) : IBoardRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult((exit, exit == 0 ? "item-1" : "no key"));
        }
    }

    [Fact]
    public async Task An_idea_becomes_a_board_task_with_work_feature_and_source_feed_item()
    {
        var runner = new FakeRunner();

        await new BoardIdeaSink(runner).FileAsync(new IdeaDraft("homelab", "Back it up", "Body text", 42), default);

        var args = Assert.Single(runner.Calls);
        Assert.Equal(["board", "add", "--kind", "task", "--work", "feature"], args.Take(6));
        Assert.Equal("feed:42", args[args.ToList().IndexOf("--source") + 1]);
        Assert.Contains("[homelab] Back it up", args);
    }

    [Fact]
    public async Task Research_ideas_use_work_research_and_no_item_means_no_source()
    {
        var runner = new FakeRunner();

        await new BoardIdeaSink(runner).FileAsync(new IdeaDraft("inbox", "Why", "b", null, "research"), default);

        var args = runner.Calls[0];
        Assert.Equal("research", args[args.ToList().IndexOf("--work") + 1]);
        Assert.DoesNotContain("--source", args);
    }

    [Fact]
    public async Task A_failing_wh_is_an_InvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BoardIdeaSink(new FakeRunner(1)).FileAsync(new IdeaDraft("p", "t", "b", 1), default));
    }
}
