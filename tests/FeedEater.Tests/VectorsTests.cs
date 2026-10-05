using FeedEater.Ranking;

namespace FeedEater.Tests;

public sealed class VectorsTests
{
    [Fact]
    public void Normalize_gives_unit_length_and_dot_is_cosine()
    {
        var v = Vectors.Normalize([3f, 4f]);

        Assert.Equal(0.6f, v[0], 5);
        Assert.Equal(0.8f, v[1], 5);
        Assert.Equal(1.0, Vectors.Dot(v, v), 5);
        Assert.Equal([0f, 0f], Vectors.Normalize([0f, 0f]));
    }

    [Fact]
    public void Centroid_is_the_normalised_mean()
    {
        var c = Vectors.Centroid([TestVectors.OneHot(0), TestVectors.OneHot(1)])!;

        Assert.Equal(Math.Sqrt(0.5), c[0], 5);
        Assert.Equal(Math.Sqrt(0.5), c[1], 5);
        Assert.Null(Vectors.Centroid([]));
    }

    [Fact]
    public void Dot_refuses_vectors_of_different_length() =>
        Assert.Throws<ArgumentException>(() => Vectors.Dot([1f], [1f, 0f]));
}
