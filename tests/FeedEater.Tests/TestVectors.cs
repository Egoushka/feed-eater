using System.Text.Json;
using FeedEater.Ranking;

namespace FeedEater.Tests;

public static class TestVectors
{
    public const int Dims = 1536;

    public static float[] OneHot(int dim)
    {
        var v = new float[Dims];
        v[dim] = 1;
        return v;
    }

    /// <summary>A unit vector mostly along <paramref name="a"/>, partly along <paramref name="b"/>.</summary>
    public static float[] Blend(int a, int b, float weightB)
    {
        var v = new float[Dims];
        v[a] = 1 - weightB;
        v[b] = weightB;
        return Vectors.Normalize(v);
    }

    /// <summary>An OpenAI-style embeddings response with <paramref name="count"/> vectors.</summary>
    public static string EmbeddingResponse(int count, Func<int, float[]>? vector = null)
    {
        var data = Enumerable.Range(0, count).Select(i => new { index = i, embedding = (vector ?? (_ => OneHot(0)))(i) });
        return JsonSerializer.Serialize(new { data, usage = new { prompt_tokens = 10 * count, total_tokens = 10 * count } });
    }

    public static int InputCount(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.GetProperty("input").GetArrayLength();
    }
}
