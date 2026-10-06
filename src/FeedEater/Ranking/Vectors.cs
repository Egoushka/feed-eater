namespace FeedEater.Ranking;

public sealed record WeightedVector(float[] X, double Weight);

public static class Vectors
{
    public static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"vector lengths differ: {a.Length} and {b.Length}");
        }

        var sum = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * (double)b[i];
        }

        return sum;
    }

    public static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(Dot(v, v));
        return norm == 0 ? v : v.Select(x => (float)(x / norm)).ToArray();
    }

    public static float[]? Centroid(IReadOnlyList<float[]> vectors) => WeightedCentroid(vectors.Select(v => new WeightedVector(v, 1)).ToList());

    /// <summary>The normalised weighted mean; a weight of 1 for every vector gives the plain centroid.</summary>
    public static float[]? WeightedCentroid(IReadOnlyList<WeightedVector> vectors)
    {
        if (vectors.Count == 0)
        {
            return null;
        }

        var sum = new float[vectors[0].X.Length];
        foreach (var v in vectors)
        {
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += (float)(v.X[i] * v.Weight);
            }
        }

        return Normalize(sum);
    }
}
