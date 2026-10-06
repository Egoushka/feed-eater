namespace FeedEater.Ranking;

public readonly record struct Point2(double X, double Y);

/// <summary>
/// Principal component analysis to two dimensions by power iteration, for the taste map. The start vector is fixed, so the same input
/// always gives the same output, and each axis's sign is fixed by its largest entry, so a map does not mirror itself between rebuilds.
/// </summary>
public static class Projection
{
    public const int Iterations = 40;
    public const int MinPoints = 3;

    /// <summary>One point per input vector (same order): its coordinates on the top two components of the centred set. Empty when there are fewer than <see cref="MinPoints"/> vectors.</summary>
    public static IReadOnlyList<Point2> Project(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count < MinPoints)
        {
            return [];
        }

        var mean = new double[vectors[0].Length];
        foreach (var v in vectors)
        {
            for (var i = 0; i < mean.Length; i++)
            {
                mean[i] += v[i];
            }
        }

        for (var i = 0; i < mean.Length; i++)
        {
            mean[i] /= vectors.Count;
        }

        var first = Component(vectors, mean, null);
        var second = Component(vectors, mean, first);
        return vectors.Select(v => new Point2(Along(v, mean, first), Along(v, mean, second))).ToList();
    }

    /// <summary>Power iteration on the covariance without forming it: each step multiplies by X (centred) and then by its transpose. A set with no spread left gives the zero vector.</summary>
    private static double[] Component(IReadOnlyList<float[]> vectors, double[] mean, double[]? orthogonalTo)
    {
        var x = new double[mean.Length];
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = 1 + i * 7919 % 13 / 13.0;
        }

        if (!Unit(x, orthogonalTo))
        {
            return new double[x.Length];
        }

        var w = new double[x.Length];
        var t = new double[vectors.Count];
        for (var step = 0; step < Iterations; step++)
        {
            for (var k = 0; k < t.Length; k++)
            {
                t[k] = Along(vectors[k], mean, x);
            }

            Array.Clear(w);
            var sum = 0d;
            for (var k = 0; k < t.Length; k++)
            {
                var row = vectors[k];
                for (var i = 0; i < w.Length; i++)
                {
                    w[i] += t[k] * row[i];
                }

                sum += t[k];
            }

            for (var i = 0; i < w.Length; i++)
            {
                w[i] -= sum * mean[i];
            }

            if (!Unit(w, orthogonalTo))
            {
                return new double[x.Length];
            }

            (x, w) = (w, x);
        }

        var biggest = 0;
        for (var i = 1; i < x.Length; i++)
        {
            if (Math.Abs(x[i]) > Math.Abs(x[biggest]))
            {
                biggest = i;
            }
        }

        if (x[biggest] < 0)
        {
            for (var i = 0; i < x.Length; i++)
            {
                x[i] = -x[i];
            }
        }

        return x;
    }

    /// <summary>Removes the part along <paramref name="orthogonalTo"/> and scales to length 1; false when nothing is left.</summary>
    private static bool Unit(double[] v, double[]? orthogonalTo)
    {
        if (orthogonalTo is not null)
        {
            var dot = 0d;
            for (var i = 0; i < v.Length; i++)
            {
                dot += v[i] * orthogonalTo[i];
            }

            for (var i = 0; i < v.Length; i++)
            {
                v[i] -= dot * orthogonalTo[i];
            }
        }

        var norm = 0d;
        for (var i = 0; i < v.Length; i++)
        {
            norm += v[i] * v[i];
        }

        norm = Math.Sqrt(norm);
        if (norm < 1e-12)
        {
            return false;
        }

        for (var i = 0; i < v.Length; i++)
        {
            v[i] /= norm;
        }

        return true;
    }

    private static double Along(float[] v, double[] mean, double[] axis)
    {
        var sum = 0d;
        for (var i = 0; i < axis.Length; i++)
        {
            sum += (v[i] - mean[i]) * axis[i];
        }

        return sum;
    }
}
