using System.IO.Compression;
using System.Text;
using FeedEater.Instagram;

namespace FeedEater.Tests;

public sealed class InstagramInspectTests : IDisposable
{
    private const string Likes = """{"likes_media_likes":[{"title":"secret_handle","string_list_data":[{"href":"https://www.instagram.com/p/abc","value":"X","timestamp":1700000000}]},{"title":"other_handle","string_list_data":[{"href":"https://www.instagram.com/p/def","timestamp":1700000001}]}]}""";
    private const string Viewed = """[{"string_map_data":{"Author":{"value":"secret_author"},"Time":{"timestamp":1700000002}}}]""";
    private const string Interests = """{"label_values":[{"label":"Interests","vals":["Cooking"]}]}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inspect-" + Guid.NewGuid().ToString("N"));

    public InstagramInspectTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string MakeZip(params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_dir, "export.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        return path;
    }

    private static async Task<(int Code, string Out, string Error)> RunAsync(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await InspectCommand.RunAsync(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string StandardZip() => MakeZip(
        ("your_instagram_activity/likes/liked_posts.json", Likes),
        ("ads_information/posts_viewed.json", Viewed),
        ("preferences/other_settings.json", Interests.Replace("Interests", "Plain")),
        ("your_instagram_activity/messages/inbox/a.html", "<html></html>"),
        ("media/photo.jpg", "x"));

    [Fact]
    public async Task It_counts_the_files_and_shows_the_structure_of_the_likely_ones()
    {
        var (code, output, _) = await RunAsync(StandardZip(), "--inspect");

        Assert.Equal(0, code);
        Assert.Contains("Files: 5; 3 JSON, 1 HTML, 1 other", output, StringComparison.Ordinal);
        Assert.Contains("your_instagram_activity/likes/liked_posts.json", output, StringComparison.Ordinal);
        Assert.Contains("likes_media_likes: array (up to 2 items)", output, StringComparison.Ordinal);
        Assert.Contains("string_list_data: array", output, StringComparison.Ordinal);
        Assert.Contains("timestamp: number", output, StringComparison.Ordinal);
        Assert.Contains("value?: string", output, StringComparison.Ordinal);   // only the first item has it
        Assert.Contains("Author: object", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_never_prints_a_value()
    {
        var (_, output, _) = await RunAsync(StandardZip(), "--inspect", "--all");

        foreach (var value in new[] { "secret_handle", "other_handle", "secret_author", "instagram.com/p/", "1700000000", "Cooking" })
        {
            Assert.DoesNotContain(value, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_file_with_an_unlikely_name_shows_only_its_top_keys_until_all_is_asked_for()
    {
        var zip = MakeZip(("preferences/other_settings.json", """{"alpha":{"deep":1},"beta":[]}"""));

        var brief = (await RunAsync(zip, "--inspect")).Out;
        var full = (await RunAsync(zip, "--inspect", "--all")).Out;

        Assert.Contains("keys: alpha, beta", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("deep", brief, StringComparison.Ordinal);
        Assert.Contains("deep: number", full, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Label_values_are_hidden_unless_asked_for()
    {
        var zip = MakeZip(("ads_information/ads_interests.json", Interests));

        Assert.DoesNotContain("Interests", (await RunAsync(zip, "--inspect")).Out.Replace("ads_interests", ""), StringComparison.Ordinal);
        Assert.Contains("Interests", (await RunAsync(zip, "--inspect", "--labels")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_works_like_a_zip_and_a_huge_or_broken_file_is_reported_not_read()
    {
        var folder = Path.Combine(_dir, "folder");
        Directory.CreateDirectory(Path.Combine(folder, "a"));
        await File.WriteAllTextAsync(Path.Combine(folder, "a", "liked_posts.json"), Likes);
        await File.WriteAllTextAsync(Path.Combine(folder, "a", "broken.json"), "{not json");
        await using (var big = File.Create(Path.Combine(folder, "a", "huge.json")))
        {
            big.SetLength(InspectCommand.MaxJsonBytes + 1);
        }

        var (code, output, _) = await RunAsync(folder, "--inspect");

        Assert.Equal(0, code);
        Assert.Contains("(folder)", output, StringComparison.Ordinal);
        Assert.Contains("a/liked_posts.json", output, StringComparison.Ordinal);
        Assert.Contains("a/broken.json  (9 B)  not valid JSON", output, StringComparison.Ordinal);
        Assert.Contains("huge.json  (20 MB)  too large to inspect", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_archive_path_that_climbs_out_is_only_read_never_written()
    {
        var zip = MakeZip(("../../evil.json", """{"x":1}"""), ("ok.json", "{}"));

        var (code, output, _) = await RunAsync(zip, "--inspect");

        Assert.Equal(0, code);
        Assert.Contains("evil.json", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_dir, "../../evil.json"))));
    }

    [Fact]
    public async Task An_export_without_json_says_how_to_ask_for_it()
    {
        var (code, output, _) = await RunAsync(MakeZip(("a.html", "<p/>")), "--inspect");

        Assert.Equal(0, code);
        Assert.Contains("No JSON files", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--inspect")]
    [InlineData("x.zip")]
    [InlineData("x.zip", "--inspect", "--bogus")]
    public async Task Anything_but_a_path_with_inspect_is_a_usage_error(params string[] args)
    {
        var (code, _, error) = await RunAsync(args);

        Assert.Equal(2, code);
        Assert.Contains("Usage: import-instagram", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_path_is_reported()
    {
        var (code, _, error) = await RunAsync(Path.Combine(_dir, "nope.zip"), "--inspect");

        Assert.Equal(2, code);
        Assert.Contains("No such file or folder", error, StringComparison.Ordinal);
    }
}
