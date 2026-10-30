using FeedEater.PriorArt.Peers;

namespace FeedEater.Tests;

public sealed class SbomParserTests
{
    private const string Sbom =
        """
        {"sbom": {"packages": [
            {"name": "root-document", "SPDXID": "SPDXRef-DOCUMENT"},
            {"name": "widget", "versionInfo": "2.3.0", "externalRefs": [{"referenceType": "purl", "referenceLocator": "pkg:nuget/widget@2.3.0"}]},
            {"name": "gadget", "versionInfo": "NOASSERTION", "externalRefs": [{"referenceType": "purl", "referenceLocator": "pkg:npm/gadget@1.0.0"}]},
            {"name": "bare", "externalRefs": [{"referenceType": "purl", "referenceLocator": "pkg:pypi/bare"}]},
            {"name": "dup", "externalRefs": [{"referenceType": "purl", "referenceLocator": "pkg:nuget/widget@2.3.0"}]}
        ]}}
        """;

    [Fact]
    public void Skips_the_root_document_package_with_no_purl()
    {
        var packages = SbomParser.Parse(Sbom);

        Assert.DoesNotContain(packages, p => p.Name == "root-document");
    }

    [Fact]
    public void Splits_the_purl_at_the_version()
    {
        var packages = SbomParser.Parse(Sbom);

        var widget = Assert.Single(packages, p => p.Name == "widget");
        Assert.Equal("pkg:nuget/widget", widget.Purl);
        Assert.Equal("2.3.0", widget.Version);
    }

    [Fact]
    public void A_NOASSERTION_versionInfo_falls_back_to_the_purl_version()
    {
        var gadget = Assert.Single(SbomParser.Parse(Sbom), p => p.Name == "gadget");

        Assert.Equal("1.0.0", gadget.Version);
    }

    [Fact]
    public void A_purl_with_no_version_has_a_null_version()
    {
        var bare = Assert.Single(SbomParser.Parse(Sbom), p => p.Name == "bare");

        Assert.Null(bare.Version);
    }

    [Fact]
    public void Dedupes_by_purl_and_sorts()
    {
        var packages = SbomParser.Parse(Sbom);

        Assert.Equal(3, packages.Count);
        Assert.Equal(["pkg:npm/gadget", "pkg:nuget/widget", "pkg:pypi/bare"], packages.Select(p => p.Purl));
    }

    [Fact]
    public void No_sbom_object_gives_an_empty_list() => Assert.Empty(SbomParser.Parse("""{"other": 1}"""));
}

public sealed class PeerSetParserTests
{
    [Fact]
    public void Reads_the_repo_and_its_peers()
    {
        const string yaml =
            """
            # the feed-eater peer set
            repo: Egoushka/feed-eater
            peers:
              - a/one
              - "b/two" # quoted, with a comment
              - 'c/three'
            """;

        var set = PeerSetParser.Parse(yaml);

        Assert.Equal("Egoushka/feed-eater", set.Repo);
        Assert.Equal(["a/one", "b/two", "c/three"], set.Peers);
    }

    [Fact]
    public void All_puts_the_own_repo_first_and_dedupes_case_insensitively()
    {
        var set = new PeerSet("a/one", ["A/One", "b/two"]);

        Assert.Equal(["a/one", "b/two"], set.All);
    }

    [Fact]
    public void A_peer_set_with_no_repo_throws() =>
        Assert.Throws<PeerSetFormatException>(() => PeerSetParser.Parse("peers:\n  - a/one\n"));

    [Fact]
    public void A_malformed_repo_value_throws() =>
        Assert.Throws<PeerSetFormatException>(() => PeerSetParser.Parse("repo: not-a-repo\n"));

    [Fact]
    public void A_malformed_peer_value_throws() =>
        Assert.Throws<PeerSetFormatException>(() => PeerSetParser.Parse("repo: a/one\npeers:\n  - not-a-repo\n"));

    [Fact]
    public void Blank_lines_and_comments_are_ignored()
    {
        var set = PeerSetParser.Parse("\n# a comment\n\nrepo: a/one\n\n");

        Assert.Equal("a/one", set.Repo);
        Assert.Empty(set.Peers);
    }
}

public sealed class PeerCardTests
{
    [Fact]
    public void Parses_addresses_features_and_uses_with_their_marketing_flag()
    {
        const string reply =
            """
            Here is the card:
            {"addresses": [{"problem": "Flaky Tests", "text": "Retries flaky tests.", "marketing": false}],
             "features": [{"slug": "Fast Startup", "text": "Starts in under a second.", "marketing": true}],
             "uses": [{"approach": "Postgres", "text": "Stores state in Postgres.", "marketing": false}]}
            """;

        var card = PeerCard.Parse(reply);

        var addresses = Assert.Single(card.Addresses);
        Assert.Equal("flaky-tests", addresses.Argument);
        Assert.Equal("Retries flaky tests.", addresses.Text);
        Assert.False(addresses.Marketing);

        var features = Assert.Single(card.Features);
        Assert.Equal("fast-startup", features.Argument);
        Assert.True(features.Marketing);

        var uses = Assert.Single(card.Uses);
        Assert.Equal("postgres", uses.Argument);
    }

    [Fact]
    public void Caps_each_list_at_six_entries()
    {
        var items = string.Join(',', Enumerable.Range(0, 10).Select(i => $$"""{"problem": "p{{i}}", "text": "t{{i}}", "marketing": false}"""));
        var reply = $$"""{"addresses": [{{items}}], "features": [], "uses": []}""";

        Assert.Equal(6, PeerCard.Parse(reply).Addresses.Count);
    }

    [Fact]
    public void An_entry_with_no_slug_able_argument_is_skipped()
    {
        const string reply = """{"addresses": [{"problem": "!!!", "text": "x", "marketing": false}], "features": [], "uses": []}""";

        Assert.Empty(PeerCard.Parse(reply).Addresses);
    }

    [Theory]
    [InlineData("no json here at all")]
    [InlineData("{not valid json}")]
    public void No_or_unparseable_json_gives_the_empty_result(string reply) => Assert.Same(PeerCard.Empty, PeerCard.Parse(reply));

    [Fact]
    public void A_field_that_is_not_an_array_contributes_nothing()
    {
        var card = PeerCard.Parse("""{"addresses": "not an array", "features": [], "uses": []}""");

        Assert.Empty(card.Addresses);
        Assert.Empty(card.Features);
        Assert.Empty(card.Uses);
    }
}
