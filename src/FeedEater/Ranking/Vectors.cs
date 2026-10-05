namespace FeedEater.Ranking;

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

    public static float[]? Centroid(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
        {
            return null;
        }

        var sum = new float[vectors[0].Length];
        foreach (var v in vectors)
        {
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += v[i];
            }
        }

        return Normalize(sum);
    }
}
