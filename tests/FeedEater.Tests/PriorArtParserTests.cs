using FeedEater.PriorArt;

namespace FeedEater.Tests;

public sealed class PriorArtParserTests
{
    private const string Tools =
        """
        ## Security

        | # | Candidate | What it does, and what it adds or replaces | Evidence | RAM, money | D-39 | Verdict |
        |---|---|---|---|---|---|---|
        | 1 | [zizmorcore/zizmor](https://github.com/zizmorcore/zizmor) (Rust) | Static audit of GitHub Actions workflows. | 6,827 / 10-29 / v1.31.1, MIT. | One-shot CLI. | Fits | **adopt**. Forgejo coverage: UNKNOWN. |
        | 2 | [x/y](https://github.com/x/y) | Pipes in a cell: `curl \| bash`. | 1 star. | Free. | Fits | **skip the repo, copy the design.** |
        | 3 | NoLink Tool | Has no link. | none | n | n | maybe later |

        | Repo | Sessions | $ total |
        |---|---|---|
        | a/b | 129 | 1,810 |

        ## Seen and dropped

        | Item | Why dropped |
        |---|---|
        | VictoriaLogs (the homelab researcher's #4) | **Already running**: uses 403 of 512 MiB. |
        """;

    [Fact]
    public void Audit_reads_subject_description_evidence_and_verdict_and_skips_other_tables()
    {
        var rows = AuditParser.Parse(Tools, null);

        Assert.Equal(4, rows.Count);
        var zizmor = rows[0];
        Assert.Equal("zizmorcore/zizmor", zizmor.Name);
        Assert.Equal("https://github.com/zizmorcore/zizmor", zizmor.Url);
        Assert.Equal("Static audit of GitHub Actions workflows.", zizmor.Description);
        Assert.Equal("6,827 / 10-29 / v1.31.1, MIT.", zizmor.Evidence);
        Assert.Equal("adopt", zizmor.Verdict);
        Assert.Equal("Forgejo coverage: UNKNOWN.", zizmor.Reason);
        Assert.Equal("Security", zizmor.Section);
        Assert.Equal("1", zizmor.Index);
    }

    [Fact]
    public void Audit_keeps_an_escaped_pipe_inside_a_cell()
    {
        Assert.Equal("Pipes in a cell: curl | bash.", AuditParser.Parse(Tools, null)[1].Description);
    }

    [Fact]
    public void Audit_verdict_needs_one_of_the_four_words()
    {
        var rows = AuditParser.Parse(Tools, null);

        Assert.Equal("skip", rows[1].Verdict);
        Assert.Equal("the repo, copy the design.", rows[1].Reason);
        Assert.Null(rows[2].Verdict);
    }

    [Fact]
    public void Audit_why_dropped_is_a_skip_verdict()
    {
        var dropped = AuditParser.Parse(Tools, null)[3];

        Assert.Equal("VictoriaLogs", dropped.Name);
        Assert.Equal("skip", dropped.Verdict);
        Assert.StartsWith("Already running", dropped.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_label_value_table_belongs_to_the_default_subject()
    {
        const string md = "| Fact | Value | Evidence |\n|---|---|---|\n| Stars, license | 479, Apache-2.0 | `gh api` |\n";

        Assert.Empty(AuditParser.Parse(md, null));
        var row = Assert.Single(AuditParser.Parse(md, "TokenEconomizer"));
        Assert.Equal("TokenEconomizer", row.Name);
        Assert.Equal("Stars, license: 479, Apache-2.0", row.Description);
        Assert.Equal("gh api", row.Evidence);
        Assert.True(row.LabelRow);
    }

    [Fact]
    public void Audit_item_cell_without_a_what_column_is_the_description()
    {
        const string md = "| # | Item | Evidence |\n|---|---|---|\n| 1 | Harbor, the Terminal-Bench harness, which runs any agent with any model ([docs](https://docs.harborframework.com/)) | beta |\n";

        var row = Assert.Single(AuditParser.Parse(md, null));
        Assert.Equal("Harbor", row.Name);
        Assert.StartsWith("Harbor, the Terminal-Bench harness", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_keep_entries_that_have_a_url_and_a_fact()
    {
        const string notes =
            """
            # Notes: A

            ### Postiz: AGPL-3.0, very active
            - URL: https://github.com/gitroomhq/postiz-app
            - Source date: 2026-10-02
            - Retrieved: 2026-10-04
            - Fact: License AGPL-3.0; 36,689 stars.
            - Quote: "one two three"
            - Label: [marketing]

            ### No fact here
            - URL: https://example.com/x
            - Retrieved: 2026-10-04
            """;

        var entry = Assert.Single(NotesParser.Parse(notes));
        Assert.Equal("Postiz: AGPL-3.0, very active", entry.Title);
        Assert.Equal(new DateOnly(2026, 10, 2), entry.SourceDate);
        Assert.Equal(new DateOnly(2026, 10, 4), entry.Retrieved);
        Assert.Equal("one two three", entry.Quote);
        Assert.Equal("[marketing]", entry.Label);
    }

    [Fact]
    public void Snapshot_csv_reads_quoted_fields()
    {
        const string csv = "repo,stars,created,pushed,topics,license,has_paper\nmicrosoft/vscode,194413,2015-09-03,2026-10-29,\"editor;a,b\",MIT,0\nx/y,,,,,,\n";

        var rows = SnapshotCsv.Parse(csv);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new SnapshotRow("microsoft/vscode", 194413, new DateOnly(2026, 10, 29), "MIT"), rows[0]);
        Assert.Equal(new SnapshotRow("x/y", null, null, null), rows[1]);
    }

    [Theory]
    [InlineData("https://github.com/getzep/graphiti/tree/main", "gh:getzep/graphiti")]
    [InlineData("https://www.nuget.org/packages/Switchknife.Facts/0.1.0", "pkg:nuget/Switchknife.Facts")]
    [InlineData("https://pypi.org/project/ruff/", "pkg:pypi/ruff")]
    [InlineData("https://docs.harborframework.com/", "url:docs.harborframework.com")]
    [InlineData("ftp://x/y", null)]
    public void Tool_reference_from_a_url(string url, string? expected) => Assert.Equal(expected, PriorArtText.RefFromUrl(url));

    [Fact]
    public void Cut_keeps_a_fact_within_the_library_limit()
    {
        var cut = PriorArtText.Cut(string.Join(' ', Enumerable.Repeat("word", 200)));

        Assert.True(cut.Length <= PriorArtText.MaxFactText);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }
}
