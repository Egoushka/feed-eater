namespace FeedEater.Tests;

/// <summary>The checkout the tests run from, for tests that read or write files in it (docs, the owner-term scan).</summary>
internal static class RepoRoot
{
    public static string Path { get; } = Find();

    public static string File(string relative) => System.IO.Path.Combine(Path, relative);

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "FeedEater.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("FeedEater.slnx was not found above the test output");
    }
}
