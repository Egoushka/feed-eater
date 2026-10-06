using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    /// <summary>Rows are summed in this many fixed blocks, in parallel but always added in block order, so the result does not depend on the core count.</summary>
    private const int Blocks = 8;

    /// <summary>The axes are fitted on at most this many evenly spaced points; every point is still projected. The top two components of 1,000 points differ little from those of 2,500 and the fit costs time in proportion.</summary>
    private const int FitRows = 1000;

    /// <summary>One point per input vector (same order): its coordinates on the top two components of the centred set. Empty when there are fewer than <see cref="MinPoints"/> vectors.</summary>
    public static IReadOnlyList<Point2> Project(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count < MinPoints)
        {
            return [];
        }

        var centred = Centre(vectors);
        var fit = centred.Length <= FitRows ? centred : Enumerable.Range(0, FitRows).Select(i => centred[(int)((long)i * centred.Length / FitRows)]).ToArray();
        var first = Component(fit, null);
        var second = Component(fit, first);
        return centred.Select(v => new Point2(Dot(v, first), Dot(v, second))).ToList();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float[][] Centre(IReadOnlyList<float[]> vectors)
    {
        var mean = new double[vectors[0].Length];
        foreach (var v in vectors)
        {
            for (var i = 0; i < mean.Length; i++)
            {
                mean[i] += v[i];
            }
        }

        var centred = new float[vectors.Count][];
        for (var k = 0; k < centred.Length; k++)
        {
            var row = new float[mean.Length];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = (float)(vectors[k][i] - mean[i] / centred.Length);
            }

            centred[k] = row;
        }

        return centred;
    }

    /// <summary>Power iteration on the covariance without forming it: each step multiplies by X and then by its transpose. A set with no spread left gives the zero vector.</summary>
    private static float[] Component(float[][] centred, float[]? orthogonalTo)
    {
        var size = centred[0].Length;
        var x = new float[size];
        for (var i = 0; i < size; i++)
        {
            x[i] = 1 + i * 7919 % 13 / 13f;
        }

        if (!Unit(x, orthogonalTo))
        {
            return new float[size];
        }

        var w = new float[size];
        var blocks = Enumerable.Range(0, Blocks).Select(_ => new float[size]).ToArray();
        for (var step = 0; step < Iterations; step++)
        {
            var axis = x;
            Parallel.For(0, Blocks, b =>
            {
                Array.Clear(blocks[b]);
                for (var k = b * centred.Length / Blocks; k < (b + 1) * centred.Length / Blocks; k++)
                {
                    AddScaled(blocks[b], centred[k], Dot(centred[k], axis));
                }
            });
            Array.Clear(w);
            foreach (var block in blocks)
            {
                AddScaled(w, block, 1);
            }

            if (!Unit(w, orthogonalTo))
            {
                return new float[size];
            }

            (x, w) = (w, x);
        }

        var biggest = 0;
        for (var i = 1; i < size; i++)
        {
            if (Math.Abs(x[i]) > Math.Abs(x[biggest]))
            {
                biggest = i;
            }
        }

        if (x[biggest] < 0)
        {
            for (var i = 0; i < size; i++)
            {
                x[i] = -x[i];
            }
        }

        return x;
    }

    /// <summary>Removes the part along <paramref name="orthogonalTo"/> and scales to length 1; false when nothing is left.</summary>
    private static bool Unit(float[] v, float[]? orthogonalTo)
    {
        if (orthogonalTo is not null)
        {
            AddScaled(v, orthogonalTo, -Dot(v, orthogonalTo));
        }

        var norm = Math.Sqrt(Dot(v, v));
        if (norm < 1e-9)
        {
            return false;
        }

        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(v[i] / norm);
        }

        return true;
    }

    // Dot and AddScaled are the whole cost of a map (about 600 million multiply-adds for 2,500 items): SIMD, four accumulators, and
    // optimised even in a Debug build so the tests do not run 5 times slower than production. Both arrays have the same length.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Dot(float[] a, float[] b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a.AsSpan());
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b.AsSpan());
        Vector<float> s0 = default, s1 = default, s2 = default, s3 = default;
        var k = 0;
        for (; k <= va.Length - 4; k += 4)
        {
            s0 += va[k] * vb[k];
            s1 += va[k + 1] * vb[k + 1];
            s2 += va[k + 2] * vb[k + 2];
            s3 += va[k + 3] * vb[k + 3];
        }

        for (; k < va.Length; k++)
        {
            s0 += va[k] * vb[k];
        }

        var total = Vector.Sum(s0 + s1 + s2 + s3);
        for (var i = va.Length * Vector<float>.Count; i < a.Length; i++)
        {
            total += a[i] * b[i];
        }

        return total;
    }

    /// <summary><c>target += scale * row</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void AddScaled(float[] target, float[] row, float scale)
    {
        var vt = MemoryMarshal.Cast<float, Vector<float>>(target.AsSpan());
        var vr = MemoryMarshal.Cast<float, Vector<float>>(row.AsSpan());
        for (var k = 0; k < vt.Length; k++)
        {
            vt[k] += vr[k] * scale;
        }

        for (var i = vt.Length * Vector<float>.Count; i < target.Length; i++)
        {
            target[i] += row[i] * scale;
        }
    }
}
