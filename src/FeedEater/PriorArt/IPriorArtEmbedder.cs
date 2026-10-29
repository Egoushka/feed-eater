using FeedEater.Llm;
using Microsoft.Extensions.Options;

namespace FeedEater.PriorArt;

/// <summary>The embedder behind fact search; a seam so tests need no gateway.</summary>
public interface IPriorArtEmbedder
{
    string Model { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct);
}

public sealed class LiteLlmEmbedder(LiteLlmClient llm, IOptions<FeedEaterOptions> options) : IPriorArtEmbedder
{
    public string Model => options.Value.Llm.EmbedModel;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct) => llm.EmbedAsync(inputs, "prior-art", ct);
}
