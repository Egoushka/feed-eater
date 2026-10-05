using FeedEater.Text;

namespace FeedEater.Tests;

public sealed class TextTests
{
    [Fact]
    public void Html_becomes_plain_text_without_scripts()
    {
        Assert.Equal("Hello world\nTwo", HtmlText.ToPlain("<p>Hello&nbsp;<b>world</b></p><script>x()</script><p>Two</p>"));
        Assert.Equal("a & b <c>", HtmlText.ToPlain("a &amp; b &lt;c&gt;"));
        Assert.Equal("a\n\nb", HtmlText.ToPlain("<p>a</p><p></p><p></p><p>b</p>"));
        Assert.Equal("", HtmlText.ToPlain(null));
        Assert.Equal("", HtmlText.ToPlain("   "));
    }

    [Theory]
    [InlineData("HTTP://WWW.Example.com/a/b/?utm_source=x&b=2&a=1#frag", "https://example.com/a/b?a=1&b=2")]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("http://example.com:8080/x?ref=hn&fbclid=1", "https://example.com:8080/x")]
    [InlineData("not a url", "not a url")]
    public void Urls_are_canonicalised(string url, string expected) => Assert.Equal(expected, UrlCanonicalizer.Canonical(url));

    [Fact]
    public void Title_hash_ignores_case_and_punctuation()
    {
        Assert.Equal(TitleHash.Of("Postgres 18.1 released"), TitleHash.Of("Postgres 18.1 Released!"));
        Assert.NotEqual(TitleHash.Of("Postgres 18.1 released"), TitleHash.Of("Postgres 18.2 released"));
        Assert.Equal(16, TitleHash.Of("Postgres 18.1 released").Length);
        Assert.NotEqual("", TitleHash.Of("Привіт, світ — новини"));
    }

    [Fact]
    public void Short_or_empty_titles_get_no_hash_so_they_never_match()
    {
        Assert.Equal("", TitleHash.Of(""));
        Assert.Equal("", TitleHash.Of("News"));
        Assert.Equal("", TitleHash.Of("!!! ???"));
    }
}
