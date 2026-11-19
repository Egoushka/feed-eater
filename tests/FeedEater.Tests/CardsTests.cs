using System.Net;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Cards;
using FeedEater.Storage;

namespace FeedEater.Tests;

/// <summary>A2.1: the inventory parsers, the card rules and the collector's fences. No database, so these run without Docker.</summary>
public sealed class CardsParserTests
{
    [Fact]
    public void Cards_inventory_reads_a_csproj_with_attribute_and_element_versions()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <PackageReference Include="Dapper" Version="2.1.66" />
              <PackageReference Include="Npgsql"><Version>9.0.3</Version></PackageReference>
              <PackageVersion Include="xunit" Version="2.9.3" />
              <PackageReference Include="Central" />
            </ItemGroup></Project>
            """;

        var items = InventoryParsers.Csproj(xml, "feed-eater", "src/App.csproj");

        Assert.Equal(["Dapper 2.1.66", "Npgsql 9.0.3", "xunit 2.9.3", "Central "], items.Select(i => $"{i.Name} {i.Version}"));
        Assert.All(items, i => Assert.Equal(("nuget", "feed-eater", "src/App.csproj"), (i.Kind, i.Repo, i.SourcePath)));
    }

    [Fact]
    public void Cards_inventory_reads_package_json_dependencies_and_ignores_broken_json()
    {
        var items = InventoryParsers.PackageJson("""{"dependencies":{"react":"^19.0.0"},"devDependencies":{"vite":"7.1.0"},"scripts":{"a":"b"}}""", "site", "package.json");

        Assert.Equal(["react ^19.0.0", "vite 7.1.0"], items.Select(i => $"{i.Name} {i.Version}"));
        Assert.Empty(InventoryParsers.PackageJson("{not json", "site", "package.json"));
    }

    [Fact]
    public void Cards_inventory_reads_compose_images_without_registry_digest_or_variables()
    {
        const string yaml = """
            services:
              db:
                image: postgres:18-alpine
              vec:
                image: "pgvector/pgvector:0.8.7-pg18"
              app:
                image: ghcr.io/acme/app:1.2.3@sha256:abc
              bare:
                image: redis
              templ:
                image: ${IMAGE}
            """;

        var items = InventoryParsers.Compose(yaml, "homelab", "stacks/a/compose.yaml");

        Assert.Equal(["postgres 18-alpine", "pgvector/pgvector 0.8.7-pg18", "acme/app 1.2.3", "redis "], items.Select(i => $"{i.Name} {i.Version}"));
        Assert.Equal(["postgres", "pgvector", "app", "redis"], items.Select(i => i.Term));
    }

    [Fact]
    public void Cards_inventory_reads_mcp_and_skill_rows_of_an_inventory_toml()
    {
        const string toml = """
            [[client]]
            name = "claude-code"

            [[mcp]]
            name = "context7"
            status = "active"

            [[skill]]
            name = "engineering"
            version = "1.0"
            status = "active"

            [[skill]]
            name = "paused"
            status = "disabled"

            [[mcp]]
            name = "side-tool"
            status = "active"
            work = true

            [[skill]]
            name = "scoped"
            status = "active"
            scope = "work"
            """;

        var items = InventoryParsers.InventoryToml(toml, "workspace", "context/ai-setup/inventory.toml", DenyList.Empty);

        Assert.Equal(["mcp context7", "skill engineering 1.0"], items.Select(i => $"{i.Kind} {i.Name} {i.Version}".Trim()));
    }

    [Fact]
    public void Cards_manifest_keeps_only_personal_and_infra_repos_that_are_not_archived_or_escaping()
    {
        const string toml = """
            [[repo]]
            name = "a"
            path = "personal/a"
            kind = "personal"
            status = "active"

            [[repo]]
            name = "b"
            path = "work/b"
            kind = "work"
            status = "active"

            [[repo]]
            name = "c"
            path = "infra/c"
            kind = "infra"
            status = "archived"

            [[repo]]
            name = "d"
            path = "../outside"
            kind = "personal"

            [[repo]]
            name = "e"
            path = "/etc"
            kind = "infra"

            [[repo]]
            name = "f"
            path = "infra/f"
            kind = "infra"
            """;

        Assert.Equal(["a", "f"], InventoryParsers.Manifest(toml).Select(r => r.Name));
    }

    [Fact]
    public void Cards_collector_reads_only_manifest_repos_inside_the_root_and_skips_symlinks_and_build_folders()
    {
        var root = Directory.CreateTempSubdirectory("cards-root").FullName;
        var outside = Directory.CreateTempSubdirectory("cards-outside").FullName;
        try
        {
            void Write(string path, string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
                File.WriteAllText(Path.Combine(root, path), text);
            }

            Write("repos.toml", """
                [[repo]]
                name = "mine"
                path = "personal/mine"
                kind = "personal"
                [[repo]]
                name = "job"
                path = "work/job"
                kind = "work"
                """);
            Write("personal/mine/src/App.csproj", """<Project><ItemGroup><PackageReference Include="Dapper" Version="2.1.66" /></ItemGroup></Project>""");
            Write("personal/mine/node_modules/x/package.json", """{"dependencies":{"ignored":"1.0.0"}}""");
            Write("personal/mine/obj/Old.csproj", """<Project><ItemGroup><PackageReference Include="Ignored" Version="1" /></ItemGroup></Project>""");
            Write("work/job/App.csproj", """<Project><ItemGroup><PackageReference Include="Secret" Version="1" /></ItemGroup></Project>""");
            File.WriteAllText(Path.Combine(outside, "package.json"), """{"dependencies":{"leaked":"1.0.0"}}""");
            Directory.CreateSymbolicLink(Path.Combine(root, "personal/mine/link"), outside);

            var collector = new InventoryCollector(Options.Create(new FeedEaterOptions()), null!, NullLogger<InventoryCollector>.Instance);
            var (found, read) = collector.Scan(new CardsOptions { Root = root, InventoryToml = "" }, default);

            Assert.Equal(["mine"], read);
            Assert.Equal(["Dapper"], found.Select(i => i.Name));
            Assert.Equal("src/App.csproj", found.Single().SourcePath);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    internal const string Fake = "acmecorp";   // a neutral stand-in for a term on the deny list; the real list is a file named by Cards:DenyTermsFile

    internal static string DenyFile(string dir)
    {
        var path = Path.Combine(dir, "terms.txt");
        File.WriteAllText(path, $"# comment\n{Fake}\nab\n");
        return path;
    }

    [Fact]
    public void Cards_a_deny_listed_row_is_dropped_by_name_note_or_text_and_inactive_rows_too()
    {
        var dir = Directory.CreateTempSubdirectory("cards-deny").FullName;
        try
        {
            var deny = DenyList.Load(DenyFile(dir))!;
            var toml = $"""
                [[skill]]
                name = "{Fake}-review"
                status = "active"

                [[skill]]
                name = "plain"
                status = "active"
                note = "used for the {Fake.ToUpperInvariant()} project"

                [[mcp]]
                name = "fine"
                status = "active"
                why = "general use"
                """;

            Assert.Equal(["fine"], InventoryParsers.InventoryToml(toml, "workspace", "inventory.toml", deny).Select(i => i.Name));
            Assert.Null(DenyList.Load(Path.Combine(dir, "missing.txt")));
            Assert.Null(DenyList.Load(""));
            Assert.False(deny.Matches("ab", "nothing"));   // lines under 3 characters are ignored
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Cards_collector_skips_the_skills_file_without_a_deny_list_and_filters_it_with_one()
    {
        var root = Directory.CreateTempSubdirectory("cards-toml").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "context"));
            File.WriteAllText(Path.Combine(root, "repos.toml"), "");
            File.WriteAllText(Path.Combine(root, "context/inv.toml"), $"""
                [[skill]]
                name = "{Fake}-tool"
                status = "active"

                [[skill]]
                name = "general"
                status = "active"
                """);
            var collector = new InventoryCollector(Options.Create(new FeedEaterOptions()), null!, NullLogger<InventoryCollector>.Instance);

            Assert.Empty(collector.Scan(new CardsOptions { Root = root, InventoryToml = "context/inv.toml" }, default).Found);
            var (found, _) = collector.Scan(new CardsOptions { Root = root, InventoryToml = "context/inv.toml", DenyTermsFile = DenyFile(root) }, default);

            Assert.Equal(["general"], found.Select(i => i.Name));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Cards_collector_follows_a_symlink_in_the_middle_of_a_path_and_refuses_one_that_leaves_the_root()
    {
        var root = Directory.CreateTempSubdirectory("cards-mid").FullName;
        var outside = Directory.CreateTempSubdirectory("cards-mid-out").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "repo"));
            File.WriteAllText(Path.Combine(outside, "repo/package.json"), """{"dependencies":{"leaked":"1.0.0"}}""");
            Directory.CreateDirectory(Path.Combine(root, "real/repo"));
            File.WriteAllText(Path.Combine(root, "real/repo/package.json"), """{"dependencies":{"kept":"1.0.0"}}""");
            Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
            Directory.CreateSymbolicLink(Path.Combine(root, "alias"), Path.Combine(root, "real"));
            File.WriteAllText(Path.Combine(root, "repos.toml"), """
                [[repo]]
                name = "out"
                path = "escape/repo"
                kind = "personal"
                [[repo]]
                name = "in"
                path = "alias/repo"
                kind = "personal"
                """);
            var collector = new InventoryCollector(Options.Create(new FeedEaterOptions()), null!, NullLogger<InventoryCollector>.Instance);

            var (found, read) = collector.Scan(new CardsOptions { Root = root, InventoryToml = "escape/inv.toml", DenyTermsFile = DenyFile(root) }, default);

            Assert.Equal(["in"], read);
            Assert.Equal(["kept"], found.Select(i => i.Name));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Cards_a_reason_over_the_cap_is_rejected()
    {
        Assert.Null(CardRules.CheckTransition("hold", new string('x', CardRules.MaxReason)));
        Assert.Contains("longer than", CardRules.CheckTransition("hold", new string('x', CardRules.MaxReason + 1)), StringComparison.Ordinal);
    }

    private static InventoryItem Usage(string name = "Dapper") => new("nuget", name, "2.1.60", "feed-eater", "src/App.csproj");

    [Fact]
    public void Cards_a_version_bump_only_candidate_yields_no_card()
    {
        var bump = new CardSignal("your feeds", "https://example.com/dapper-2-1-66", "Dapper 2.1.66 released", CardRules.IsVersionBump("Dapper 2.1.66 released", "Dapper"), 1, null, 40);

        Assert.True(bump.VersionBump);
        Assert.Null(CardRules.Build(new CardCandidate("Dapper", [Usage()], [bump])));
    }

    [Theory]
    [InlineData("Dapper 2.1.66 released", true)]
    [InlineData("Release v2.1.66 of Dapper", true)]
    [InlineData("Dapper v2.1 is out", true)]
    [InlineData("Dapper is now faster than raw ADO.NET in a new benchmark", false)]
    [InlineData("Show HN: a Dapper alternative", false)]
    public void Cards_version_bump_detection(string title, bool expected) => Assert.Equal(expected, CardRules.IsVersionBump(title, "Dapper"));

    [Fact]
    public void Cards_a_real_signal_beside_a_bump_makes_a_card_with_where_in_stack_and_evidence()
    {
        var signals = new[]
        {
            new CardSignal("your feeds", "https://example.com/bump", "Dapper 2.1.66 released", true, 1, null, 90),
            new CardSignal("Hacker News", "https://news.ycombinator.com/item?id=42", "Dapper AOT is here (120 points)", false, null, null, 22),
        };

        var card = CardRules.Build(new CardCandidate("Dapper", [Usage()], signals))!;

        Assert.Equal("feed-eater", card.Repo);
        Assert.Contains("src/App.csproj", card.WhereInStack, StringComparison.Ordinal);
        Assert.Equal(["https://news.ycombinator.com/item?id=42"], card.Evidence.Select(e => e.Url));
        Assert.Null(CardRules.Validate(card));
    }

    [Fact]
    public void Cards_a_card_without_where_in_stack_or_evidence_is_rejected()
    {
        var good = CardRules.Build(new CardCandidate("Dapper", [Usage()], [new CardSignal("deps.dev", "https://osv.dev/vulnerability/GHSA-1", "Advisory", false, null, null, 100)]))!;

        Assert.Contains("where_in_stack", CardRules.Validate(good with { WhereInStack = " " }), StringComparison.Ordinal);
        Assert.Contains("evidence", CardRules.Validate(good with { Evidence = [new EvidenceLink("javascript:alert(1)", "x", "s", null)] }), StringComparison.Ordinal);
        Assert.Null(CardRules.Build(new CardCandidate("Dapper", [], [new CardSignal("deps.dev", "https://osv.dev/vulnerability/GHSA-1", "Advisory", false, null, null, 100)])));
    }

    [Fact]
    public void Cards_state_change_needs_a_known_state_and_a_reason()
    {
        Assert.Null(CardRules.CheckTransition("hold", "needs the control plane to send the attribute"));
        Assert.Contains("unknown state", CardRules.CheckTransition("maybe", "x"), StringComparison.Ordinal);
        Assert.Contains("reason", CardRules.CheckTransition("hold", "  "), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cards_hacker_news_search_keeps_stories_that_name_the_term_and_links_the_discussion()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json("""
            {"hits":[
              {"objectID":"42","title":"Dapper AOT lands","points":120,"created_at_i":1760000000},
              {"objectID":"43","title":"Something unrelated","points":300,"created_at_i":1760000000},
              {"objectID":"x1","title":"Dapper bad id","points":1,"created_at_i":1760000000}]}
            """));

        var signals = await new HnSearch(stub.Client("https://hn.example/api/v1/")).StoriesAsync("Dapper", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 20, default);

        var s = Assert.Single(signals);
        Assert.Equal("https://news.ycombinator.com/item?id=42", s.Url);
        Assert.Contains("Dapper", Uri.UnescapeDataString(stub.Calls[0].Uri), StringComparison.Ordinal);
        Assert.Contains("points>=20", Uri.UnescapeDataString(stub.Calls[0].Uri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cards_deps_dev_reports_advisories_for_an_exact_version_only()
    {
        var stub = new StubHandler((r, _) => r.RequestUri!.AbsolutePath.Contains("Dapper", StringComparison.Ordinal)
            ? StubHandler.Json("""{"advisoryKeys":[{"id":"GHSA-xxxx-yyyy-zzzz"}]}""")
            : StubHandler.Json("{}", HttpStatusCode.NotFound));
        var client = new DepsDevClient(stub.Client("https://deps.example/v3/"));

        var found = await client.AdvisoriesAsync(new InventoryItem("nuget", "Dapper", "2.1.60", "r", "p"), default);
        var range = await client.AdvisoriesAsync(new InventoryItem("npm", "react", "^19.0.0", "r", "p"), default);
        var unknown = await client.AdvisoriesAsync(new InventoryItem("nuget", "Nope", "1.0.0", "r", "p"), default);

        Assert.Equal("https://osv.dev/vulnerability/GHSA-xxxx-yyyy-zzzz", Assert.Single(found).Url);
        Assert.Empty(range);
        Assert.Empty(unknown);
        Assert.Equal(2, stub.Calls.Count);   // the range made no call
    }
}

/// <summary>The store, the generator and the job against Postgres (CI: Testcontainers).</summary>
[Collection(PostgresCollection.Name)]
public sealed class CardsStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    private CardStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _store = new CardStore(pg.Db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static NewCard Card(string title = "Dapper: AOT", string where = "feed-eater: Dapper (nuget, src/App.csproj)") =>
        new(title.ToLowerInvariant(), title, "what", "feed-eater", where, [new EvidenceLink("https://example.com/a", "A", "your feeds", null)], "small", "gain", []);

    [Fact]
    public async Task Cards_store_round_trips_evidence_and_ignores_a_rerun()
    {
        Assert.True(await _store.AddAsync(Card(), default));
        Assert.False(await _store.AddAsync(Card(), default));

        var row = Assert.Single(await _store.ListAsync(null, 10, default));
        Assert.Equal("proposed", row.State);
        Assert.Equal("https://example.com/a", Assert.Single(row.Evidence).Url);
        Assert.False(row.EvidenceChecked);
    }

    [Fact]
    public async Task Cards_store_rejects_a_card_without_where_in_stack_and_the_table_does_too()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await _store.AddAsync(Card(where: ""), default));

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await c.ExecuteAsync(
            "insert into card (title, what, repo, where_in_stack, evidence) values ('t', 'w', 'r', ' ', '[{\"url\":\"https://x.example\"}]')"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await c.ExecuteAsync(
            "insert into card (title, what, repo, where_in_stack, evidence) values ('t', 'w', 'r', 'here', '[]')"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await c.ExecuteAsync(
            "insert into card (title, what, repo, where_in_stack, evidence, state) values ('t', 'w', 'r', 'here', '[{}]', 'later')"));
    }

    [Fact]
    public async Task Cards_store_sets_state_with_a_reason_and_filters_by_state()
    {
        await _store.AddAsync(Card("a"), default);
        await _store.AddAsync(Card("b"), default);
        var id = (await _store.ListAsync(null, 10, default)).Single(c => c.Title == "a").Id;

        Assert.True(await _store.SetStateAsync(id, "hold", " needs the control plane ", default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await _store.SetStateAsync(id, "later", "x", default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await _store.SetStateAsync(id, "adopt", "", default));

        var held = Assert.Single(await _store.ListAsync("hold", 10, default));
        Assert.Equal(("a", "needs the control plane"), (held.Title, held.StateReason));
        Assert.NotNull(held.StateChangedAt);
        Assert.False(await _store.SetStateAsync(999999, "hold", "x", default));
        Assert.Equal(1, (await _store.CountsAsync(default))["proposed"]);
    }

    [Fact]
    public async Task Cards_inventory_is_replaced_per_repo_read_and_kept_for_a_repo_not_read()
    {
        var a = new InventoryItem("nuget", "Dapper", "2.1.60", "r1", "a.csproj");
        var b = new InventoryItem("nuget", "Npgsql", "9.0.3", "r2", "b.csproj");
        await _store.ReplaceInventoryAsync([a, b], ["r1", "r2"], default);

        await _store.ReplaceInventoryAsync([a with { Version = "2.1.66" }], ["r1"], default);

        var all = await _store.InventoryAsync(default);
        Assert.Equal(["Dapper 2.1.66", "Npgsql 9.0.3"], all.Select(i => $"{i.Name} {i.Version}"));
    }

    [Fact]
    public async Task Cards_generator_writes_a_card_from_a_ranked_item_and_none_for_a_bump_or_a_low_rank()
    {
        await _store.ReplaceInventoryAsync(
            [new InventoryItem("nuget", "Dapper", "2.1.60", "feed-eater", "src/App.csproj"), new InventoryItem("nuget", "Npgsql", "9.0.3", "feed-eater", "src/App.csproj"),
             new InventoryItem("image", "postgres", "18-alpine", "homelab", "stacks/a/compose.yaml")], ["feed-eater", "homelab"], default);
        var real = await Seed.ItemAsync(pg, 1, "Dapper AOT is production ready", TestVectors.OneHot(0));
        var bump = await Seed.ItemAsync(pg, 1, "Npgsql 9.0.4 released", TestVectors.OneHot(1));
        var low = await Seed.ItemAsync(pg, 1, "Postgres tips of the week", TestVectors.OneHot(2));
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into triage (item_id, relevance, kind, model) values (@real, 3, 'improve', 'm'), (@bump, 3, 'fyi', 'm'), (@low, 1, 'fyi', 'm')", new { real, bump, low });
        }

        var options = Options.Create(new FeedEaterOptions { Cards = new CardsOptions { DepsDevBase = "", HnMinPoints = 20 } });
        var hn = new HnSearch(new StubHandler((_, _) => StubHandler.Json("""{"hits":[]}""")).Client("https://hn.example/api/v1/"));
        var generator = new CardGenerator(_store, hn, new DepsDevClient(new HttpClient()), options, TimeProvider.System, NullLogger<CardGenerator>.Instance);

        Assert.Equal(1, await generator.GenerateAsync(default));
        Assert.Equal(0, await generator.GenerateAsync(default));

        var card = Assert.Single(await _store.ListAsync(null, 10, default));
        Assert.StartsWith("Dapper:", card.Title, StringComparison.Ordinal);
        Assert.Equal([real], card.SourceSignalIds);
        Assert.Contains("src/App.csproj", card.WhereInStack, StringComparison.Ordinal);
        Assert.NotEmpty(card.Evidence);
    }

    [Fact]
    public async Task Cards_a_deny_listed_term_is_never_queried_stored_or_logged()
    {
        var dir = Directory.CreateTempSubdirectory("cards-gen").FullName;
        try
        {
            await _store.ReplaceInventoryAsync(
                [new InventoryItem("skill", $"{CardsParserTests.Fake}-review", "", "workspace", "inv.toml"), new InventoryItem("skill", "general", "", "workspace", "inv.toml")], ["workspace"], default);
            var item = await Seed.ItemAsync(pg, 1, $"{CardsParserTests.Fake}-review is out for general use", TestVectors.OneHot(0));
            await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
            {
                await c.ExecuteAsync("insert into triage (item_id, relevance, kind, model) values (@item, 3, 'improve', 'm')", new { item });
            }

            var logs = new List<string>();
            var requests = new List<string>();
            var hn = new HnSearch(new StubHandler((r, _) =>
            {
                requests.Add(r.RequestUri!.ToString());
                return StubHandler.Json("""{"hits":[]}""");
            }).Client("https://hn.example/api/v1/"));
            var options = Options.Create(new FeedEaterOptions { Cards = new CardsOptions { DepsDevBase = "", DenyTermsFile = CardsParserTests.DenyFile(dir) } });

            await new CardGenerator(_store, hn, new DepsDevClient(new HttpClient()), options, TimeProvider.System, new ListLogger<CardGenerator>(logs)).GenerateAsync(default);

            Assert.DoesNotContain(requests, r => r.Contains(CardsParserTests.Fake, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(requests, r => r.Contains("general", StringComparison.Ordinal));
            Assert.Empty(await _store.ListAsync(null, 10, default));
            Assert.DoesNotContain(logs, l => l.Contains(CardsParserTests.Fake, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Cards_a_held_term_never_comes_back_under_a_new_headline()
    {
        await _store.ReplaceInventoryAsync([new InventoryItem("nuget", "Dapper", "2.1.60", "feed-eater", "src/App.csproj")], ["feed-eater"], default);
        var first = await Seed.ItemAsync(pg, 1, "Dapper AOT is production ready", TestVectors.OneHot(0));
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into triage (item_id, relevance, kind, model) values (@first, 3, 'improve', 'm')", new { first });
        }

        var options = Options.Create(new FeedEaterOptions { Cards = new CardsOptions { DepsDevBase = "" } });
        var hn = new HnSearch(new StubHandler((_, _) => StubHandler.Json("""{"hits":[]}""")).Client("https://hn.example/api/v1/"));
        var generator = new CardGenerator(_store, hn, new DepsDevClient(new HttpClient()), options, TimeProvider.System, NullLogger<CardGenerator>.Instance);
        Assert.Equal(1, await generator.GenerateAsync(default));
        var id = (await _store.ListAsync(null, 10, default)).Single().Id;
        await _store.SetStateAsync(id, "hold", "not now", default);

        var second = await Seed.ItemAsync(pg, 1, "Dapper 3 brings a new source generator", TestVectors.OneHot(1));
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into triage (item_id, relevance, kind, model) values (@second, 3, 'improve', 'm')", new { second });
        }

        Assert.Equal(0, await generator.GenerateAsync(default));
        Assert.Equal("hold", Assert.Single(await _store.ListAsync(null, 10, default)).State);
        Assert.False(await _store.AddAsync(Card("Dapper: a different headline") with { Term = "dapper" }, default));
    }

    [Fact]
    public async Task Cards_inventory_kind_is_limited_to_the_known_kinds()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await c.ExecuteAsync("insert into inventory_item (kind, name, repo, source_path) values ('gem', 'x', 'r', 'p')"));
    }
}
