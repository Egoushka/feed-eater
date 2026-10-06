using FeedEater.Ranking;

namespace FeedEater.Tests;

public class ProjectionTests
{
    private static float[] OnPlane(double a, double b)
    {
        var v = new float[TestVectors.Dims];
        v[3] = (float)a;
        v[40] = (float)b;
        return v;
    }

    [Fact]
    public void Points_on_a_plane_project_to_that_planes_axes_up_to_sign()
    {
        // A grid, so the two coordinates are uncorrelated and the plane's own axes are the principal ones.
        var grid = (from x in new double[] { -6, -2, 1, 3, 9 } from y in new double[] { -1, 0, 1.5 } select (x, y)).ToList();
        var a = grid.Select(g => g.x).ToArray();
        var b = grid.Select(g => g.y).ToArray();
        var points = Projection.Project(a.Select((x, i) => OnPlane(x, b[i])).ToList());
        var meanA = a.Average();
        var meanB = b.Average();

        Assert.Equal(a.Length, points.Count);
        var signX = Math.Sign(points[0].X * (a[0] - meanA));
        var signY = Math.Sign(points[0].Y * (b[0] - meanB));
        for (var i = 0; i < a.Length; i++)
        {
            Assert.Equal(signX * (a[i] - meanA), points[i].X, 3);
            Assert.Equal(signY * (b[i] - meanB), points[i].Y, 3);
        }
    }

    [Fact]
    public void The_same_input_gives_the_same_points()
    {
        var vectors = Enumerable.Range(0, 20).Select(i => OnPlane(Math.Sin(i), Math.Cos(i * 3))).ToList();

        Assert.Equal(Projection.Project(vectors), Projection.Project(vectors));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Fewer_than_three_points_give_an_empty_map(int count) =>
        Assert.Empty(Projection.Project(Enumerable.Range(0, count).Select(i => OnPlane(i, 2 * i)).ToList()));

    [Fact]
    public void A_set_with_no_spread_projects_to_the_origin_and_one_with_a_single_direction_has_a_flat_second_axis()
    {
        var same = Projection.Project(Enumerable.Range(0, 5).Select(_ => OnPlane(1, 1)).ToList());
        var line = Projection.Project(Enumerable.Range(0, 5).Select(i => OnPlane(i, 0)).ToList());

        Assert.All(same, p => Assert.Equal(new Point2(0, 0), p));
        Assert.All(line, p => Assert.Equal(0, p.Y, 6));
        Assert.Equal(4, line.Max(p => p.X) - line.Min(p => p.X), 3);
    }
}
