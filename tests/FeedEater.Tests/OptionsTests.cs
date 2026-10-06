using Microsoft.Extensions.Configuration;

namespace FeedEater.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var o = new FeedEaterOptions();

        Assert.Equal(new TimeSpan(7, 30, 0), o.DigestAt);
        Assert.Equal(new TimeSpan(12, 0, 0), o.DigestGiveUpAt);
        Assert.Equal(60, o.Caps.Triage);
        Assert.Equal(12, o.Caps.Read);
        Assert.Equal(3, o.Caps.CandidateDays);
        Assert.Equal(2, o.Caps.MinRelevance);
        Assert.Equal("gpt-4.1-nano", o.Llm.TriageModel);
        Assert.Equal("gpt-4.1-mini", o.Llm.ReadModel);
        Assert.Equal("text-embedding-3-small", o.Llm.EmbedModel);
        Assert.Equal(0.5, o.Weights.Taste);
        Assert.Equal(0.2, o.Weights.Prior);
        Assert.Equal("", o.Plane.FallbackProject);
        Assert.Equal("UTC", o.Zone.Id);
        Assert.Equal("https://api.openai.com/v1/", o.Llm.BaseUrl);
    }

    [Fact]
    public void Binds_nested_settings_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FeedEater:DigestAt"] = "06:45:00",
            ["FeedEater:Caps:Read"] = "8",
            ["FeedEater:Telegram:AllowedUserId"] = "123456789",
        }).Build();

        var o = config.GetSection(FeedEaterOptions.Section).Get<FeedEaterOptions>()!;

        Assert.Equal(new TimeSpan(6, 45, 0), o.DigestAt);
        Assert.Equal(8, o.Caps.Read);
        Assert.Equal(123456789, o.Telegram.AllowedUserId);
    }
}
