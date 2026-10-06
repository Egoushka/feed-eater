using FeedEater.Digest;

namespace FeedEater.Tests;

public sealed class LlmJsonTests
{
    private static readonly HashSet<string> Keys = ["homelab", "postgres"];

    [Fact]
    public void Triage_reads_fenced_json()
    {
        var t = LlmJson.Triage("```json\n{\"relevance\": 3, \"project\": \"homelab\", \"kind\": \"improve\", \"reason\": \"new backup tool\"}\n```", Keys);

        Assert.Equal(3, t.Relevance);
        Assert.Equal("homelab", t.Project);
        Assert.Equal("improve", t.Kind);
        Assert.Equal("new backup tool", t.Reason);
    }

    [Fact]
    public void Triage_clamps_and_cleans_bad_fields()
    {
        var t = LlmJson.Triage("""{"relevance": 7, "project": "unknown", "kind": "urgent"}""", Keys);
        var s = LlmJson.Triage("""{"relevance": "2", "project": null, "kind": "new"}""", Keys);

        Assert.Equal(3, t.Relevance);
        Assert.Null(t.Project);
        Assert.Equal("fyi", t.Kind);
        Assert.Equal(2, s.Relevance);
        Assert.Equal("new", s.Kind);
    }

    [Fact]
    public void Triage_of_garbage_is_marginal_not_an_error()
    {
        var t = LlmJson.Triage("I think this is relevant!", Keys);

        Assert.Equal(1, t.Relevance);
        Assert.Equal("fyi", t.Kind);
        Assert.Equal("unparseable model output", t.Reason);
    }

    [Fact]
    public void Read_keeps_a_suggestion_only_for_improve_or_new()
    {
        var improve = LlmJson.Read("""{"summary":"S.","why":"W.","kind":"improve","project":"postgres","suggestion":"Do X."}""", Keys)!;
        var fyi = LlmJson.Read("""{"summary":"S.","why":"W.","kind":"fyi","project":"postgres","suggestion":"Do X."}""", Keys)!;
        var nullText = LlmJson.Read("""{"summary":"S.","why":"W.","kind":"new","suggestion":"null"}""", Keys)!;

        Assert.Equal("Do X.", improve.Suggestion);
        Assert.Equal("postgres", improve.Project);
        Assert.Null(fyi.Suggestion);
        Assert.Null(nullText.Suggestion);
    }

    [Fact]
    public void Read_without_a_summary_is_unusable()
    {
        Assert.Null(LlmJson.Read("""{"why":"W.","kind":"fyi"}""", Keys));
        Assert.Null(LlmJson.Read("no json here", Keys));
    }

    [Fact]
    public void Reply_matches_the_project_ignoring_case_and_keeps_an_unknown_name_apart()
    {
        string[] plane = ["LAB", "JARVIS", "FEED"];

        var known = LlmJson.Reply("""{"action":"Idea","project":"jarvis","idea":"Do X."}""", plane);
        var unknown = LlmJson.Reply("""{"action":"idea","project":"Nytka"}""", plane);
        var none = LlmJson.Reply("""{"action":"idea","project":null}""", plane);

        Assert.Equal(new ReplyIntent("idea", "JARVIS", null, "Do X."), known);
        Assert.Equal(new ReplyIntent("idea", null, "Nytka"), unknown);
        Assert.Equal(new ReplyIntent("idea"), none);
    }

    [Theory]
    [InlineData("""{"action":"delete everything"}""")]
    [InlineData("""{"question":"x"}""")]
    [InlineData("no json")]
    public void Reply_with_an_unknown_or_missing_action_is_unclear(string text) =>
        Assert.Equal(new ReplyIntent("unclear"), LlmJson.Reply(text, ["LAB"]));
}
