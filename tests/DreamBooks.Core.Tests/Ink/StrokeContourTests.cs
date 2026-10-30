using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Ink;

public class StrokeContourTests
{
    // Explicit budget for the typical 100 pt stroke below; a regression past it must fail.
    private const int _vertexBudget = 400;

    private static (PointD[] Centers, double[] Widths) WavyStroke()
    {
        var raw = new InkPoint[83];
        for (var i = 0; i < raw.Length; i++)
        {
            var x = i * 100.0 / (raw.Length - 1);
            var y = 6 * Math.Sin(x / 9);
            var pressure = (float)(0.25 + (0.6 * (0.5 + (0.5 * Math.Sin((x / 13) + 1)))));
            raw[i] = new InkPoint(x, y, pressure, null, null, null, i * 8L);
        }

        var smoothed = StrokeSmoother.Smooth(raw, SmoothingLevel.Strong);
        var centers = smoothed.Select(p => p.Position).ToArray();
        var widths = smoothed.Select(p => PressureMapping.WidthAt(1.5, p.Pressure, PressureCurve.Firm)).ToArray();
        return (centers, widths);
    }

    private static PointD[] Polyline(params PointD[] corners)
    {
        var result = new List<PointD> { corners[0] };
        for (var i = 1; i < corners.Length; i++)
        {
            var a = corners[i - 1];
            var b = corners[i];
            var steps = Math.Max(1, (int)Math.Ceiling(PointD.Distance(a, b) / 0.5));
            for (var k = 1; k <= steps; k++)
            {
                var t = (double)k / steps;
                result.Add(new PointD(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t)));
            }
        }

        return [.. result];
    }

    private static double[] Constant(int count, double width) => Enumerable.Repeat(width, count).ToArray();

    private static (int Contour, int Union, int Mismatch) CompareWithUnion(PointD[] centers, double[] widths, double step)
    {
        var contour = StrokeOutline.BuildContour(centers, widths);
        var union = StrokeOutline.Build(centers, widths);
        var b = PolygonRaster.Bounds(union);
        var minX = b.MinX - 1;
        var minY = b.MinY - 1;
        var nx = (int)Math.Ceiling((b.MaxX - b.MinX + 2) / step);
        var ny = (int)Math.Ceiling((b.MaxY - b.MinY + 2) / step);
        var c = PolygonRaster.Rasterize(contour, minX, minY, step, nx, ny);
        var u = PolygonRaster.Rasterize(union, minX, minY, step, nx, ny);
        int inContour = 0, inUnion = 0, mismatch = 0;
        for (var i = 0; i < nx; i++)
        {
            for (var j = 0; j < ny; j++)
            {
                inContour += c[i, j] ? 1 : 0;
                inUnion += u[i, j] ? 1 : 0;
                mismatch += c[i, j] != u[i, j] ? 1 : 0;
            }
        }

        return (inContour, inUnion, mismatch);
    }

    private static void AssertDisc(IReadOnlyList<IReadOnlyList<PointD>> figures, PointD center, double radius)
    {
        var figure = Assert.Single(figures);
        var b = PolygonRaster.Bounds(figures);
        Assert.InRange(b.MinX, center.X - radius - 0.03, center.X - radius + 0.03);
        Assert.InRange(b.MaxX, center.X + radius - 0.03, center.X + radius + 0.03);
        Assert.InRange(b.MinY, center.Y - radius - 0.03, center.Y - radius + 0.03);
        Assert.InRange(b.MaxY, center.Y + radius - 0.03, center.Y + radius + 0.03);
        var expected = Math.PI * radius * radius;
        Assert.InRange(PolygonRaster.SignedArea(figure), expected * 0.99, expected * 1.01);
    }

    [Fact]
    public void Single_point_gives_one_disc_of_the_given_width()
    {
        AssertDisc(StrokeOutline.BuildContour([new PointD(5, 7)], [2]), new PointD(5, 7), 1);
        AssertDisc(StrokeOutline.BuildContour([new PointD(5, 7)], [0.3]), new PointD(5, 7), 0.15);
    }

    [Fact]
    public void Identical_points_give_a_disc()
    {
        PointD[] centers = [new(3, 3), new(3, 3), new(3, 3.005), new(3.004, 3)];

        AssertDisc(StrokeOutline.BuildContour(centers, [2, 2, 2, 2]), new PointD(3, 3), 1);
    }

    [Fact]
    public void Output_is_one_closed_figure_with_positive_winding()
    {
        PointD[] centers = [new(0, 0), new(3, 1), new(5, 5), new(2, 6)];
        double[] widths = [1, 3, 0.5, 2];

        var figure = Assert.Single(StrokeOutline.BuildContour(centers, widths));

        Assert.True(figure.Count >= 3);
        Assert.True(PolygonRaster.SignedArea(figure) > 0);
        Assert.True(PolygonRaster.SignedArea(Assert.Single(StrokeOutline.BuildContour(WavyStroke().Centers, WavyStroke().Widths))) > 0);
    }

    [Fact]
    public void Output_contains_no_NaN_or_infinity()
    {
        PointD[] centers = [new(1, 1), new(1, 1), new(1, 1.0001), new(4, 4), new(4, 4), new(4.001, 4)];
        double[] widths = [0.01, 0.01, 0, 2, double.NaN, 0.01];

        var figures = StrokeOutline.BuildContour(centers, widths);

        Assert.Single(figures);
        Assert.All(figures.SelectMany(f => f), p =>
        {
            Assert.True(double.IsFinite(p.X));
            Assert.True(double.IsFinite(p.Y));
        });
    }

    [Fact]
    public void Straight_constant_width_line_has_expected_bounds()
    {
        var figures = StrokeOutline.BuildContour([new PointD(0, 0), new PointD(10, 0)], [2, 2]);

        Assert.Single(figures);
        var b = PolygonRaster.Bounds(figures);
        Assert.InRange(b.MinX, -1.03, -0.97);
        Assert.InRange(b.MaxX, 10.97, 11.03);
        Assert.InRange(b.MinY, -1.03, -0.97);
        Assert.InRange(b.MaxY, 0.97, 1.03);
    }

    [Fact]
    public void Covers_every_centerline_point_under_nonzero_rule()
    {
        // A tight loop whose radius (0.8) is smaller than the half width (1.5).
        var loop = Enumerable.Range(0, 60)
            .Select(i => new PointD(10 + (0.8 * Math.Cos(i * 0.13)), 10 + (0.8 * Math.Sin(i * 0.13))))
            .ToArray();

        // A 180-degree reversal, with constant and with varying widths.
        var reversal = Polyline(new PointD(0, 0), new PointD(10, 0), new PointD(0, 0));
        var reversalWidths = reversal.Select((_, i) => 0.5 + (2.0 * i / reversal.Length)).ToArray();
        var bentBack = Polyline(new PointD(0, 0), new PointD(10, 0), new PointD(0, 0.3));

        (PointD[] Centers, double[] Widths)[] cases =
        [
            (loop, Constant(loop.Length, 3)),
            (reversal, Constant(reversal.Length, 2)),
            (reversal, reversalWidths),
            (bentBack, Constant(bentBack.Length, 1.5)),
        ];

        foreach (var (centers, widths) in cases)
        {
            var figures = StrokeOutline.BuildContour(centers, widths);
            Assert.Single(figures);
            for (var i = 0; i < centers.Length; i++)
            {
                Assert.NotEqual(0, PolygonRaster.Winding(figures, centers[i]));
                if (i + 1 < centers.Length)
                {
                    var mid = new PointD((centers[i].X + centers[i + 1].X) / 2, (centers[i].Y + centers[i + 1].Y) / 2);
                    Assert.NotEqual(0, PolygonRaster.Winding(figures, mid));
                }
            }

            // No holes anywhere: the union of discs and the contour agree except along the edge.
            var (_, union, mismatch) = CompareWithUnion(centers, widths, 0.05);
            Assert.True(mismatch <= union * 0.02, $"mismatch {mismatch} of {union}");
        }
    }

    [Fact]
    public void Random_scribbles_cover_every_disc()
    {
        // Deterministic scribbles with sharp turns, tiny steps and width jumps.
        var rng = new Random(1234);
        for (var s = 0; s < 100; s++)
        {
            var n = rng.Next(1, 60);
            var centers = new PointD[n];
            var widths = new double[n];
            double x = 0, y = 0, angle = 0;
            var stepScale = rng.NextDouble() < 0.3 ? 0.05 : 0.6;
            for (var i = 0; i < n; i++)
            {
                angle += (rng.NextDouble() - 0.5) * (rng.NextDouble() < 0.2 ? 6.3 : 1.5);
                var step = rng.NextDouble() * stepScale;
                x += step * Math.Cos(angle);
                y += step * Math.Sin(angle);
                centers[i] = new PointD(x, y);
                widths[i] = rng.NextDouble() < 0.2 ? 0.05 + (rng.NextDouble() * 4) : 1 + (rng.NextDouble() * 0.5);
            }

            var figures = StrokeOutline.BuildContour(centers, widths);

            Assert.True(PolygonRaster.SignedArea(Assert.Single(figures)) > 0);
            for (var i = 0; i < n; i++)
            {
                Assert.NotEqual(0, PolygonRaster.Winding(figures, centers[i]));
                var r = 0.8 * widths[i] / 2;
                for (var k = 0; k < 8; k++)
                {
                    var p = new PointD(centers[i].X + (r * Math.Cos(k * Math.PI / 4)), centers[i].Y + (r * Math.Sin(k * Math.PI / 4)));
                    Assert.NotEqual(0, PolygonRaster.Winding(figures, p));
                }
            }
        }
    }

    [Fact]
    public void Matches_the_union_outline_within_one_percent_of_area()
    {
        var (centers, widths) = WavyStroke();

        var (contour, union, _) = CompareWithUnion(centers, widths, 0.025);

        Assert.True(Math.Abs(contour - union) <= union * 0.01, $"contour {contour} union {union}");
    }

    [Fact]
    public void Sample_grid_agrees_with_the_union_outline_almost_everywhere()
    {
        var (centers, widths) = WavyStroke();

        var (_, union, mismatch) = CompareWithUnion(centers, widths, 0.1);

        Assert.True(mismatch <= union * 0.01, $"mismatch {mismatch} of {union}");
    }

    [Fact]
    public void Width_jump_between_neighbours_leaves_no_gap_or_spike()
    {
        PointD[] centers = [new(0, 0), new(0.5, 0), new(1, 0), new(1.5, 0), new(2, 0)];
        double[] growing = [0.3, 0.3, 0.3, 1.5, 1.5];
        double[] shrinking = [1.5, 1.5, 0.3, 0.3, 0.3];

        foreach (var widths in new[] { growing, shrinking })
        {
            var figures = StrokeOutline.BuildContour(centers, widths);
            var figure = Assert.Single(figures);

            for (var i = 0; i < centers.Length; i++)
            {
                var r = widths[i] / 2;
                for (var k = 0; k < 16; k++)
                {
                    var a = k * Math.PI / 8;
                    var p = new PointD(centers[i].X + (0.97 * r * Math.Cos(a)), centers[i].Y + (0.97 * r * Math.Sin(a)));
                    Assert.NotEqual(0, PolygonRaster.Winding(figures, p));
                }
            }

            // No spike: every vertex lies on (or within the arc tolerance of) one of the discs.
            Assert.All(figure, v => Assert.Contains(
                Enumerable.Range(0, centers.Length),
                i => PointD.Distance(v, centers[i]) <= (widths[i] / 2) + 0.03));
            var b = PolygonRaster.Bounds(figures);
            Assert.True(b.MaxX - b.MinX <= 2 + 0.75 + 0.15 + 0.03);
            Assert.True(b.MaxY - b.MinY <= 1.5 + 0.03);
        }
    }

    [Fact]
    public void Vertex_count_is_a_small_fraction_of_the_union_outline()
    {
        var (centers, widths) = WavyStroke();

        var contour = StrokeOutline.BuildContour(centers, widths).Sum(f => f.Count);
        var union = StrokeOutline.Build(centers, widths).Sum(f => f.Count);

        Assert.True(contour <= _vertexBudget, $"{contour} vertices");
        Assert.True(contour <= union * 0.15, $"{contour} vs {union}");
    }

    [Fact]
    public void Simplification_keeps_sharp_corner()
    {
        var centers = Polyline(new PointD(0, 0), new PointD(10, 0), new PointD(10, 10));
        const double tolerance = 0.05;

        var figure = Assert.Single(StrokeOutline.BuildContour(centers, Constant(centers.Length, 2), tolerance));

        // With y down the path turns from +x to +y, so the outer corner is towards (+x, -y).
        var corner = new PointD(10 + Math.Sqrt(0.5), -Math.Sqrt(0.5));
        Assert.True(PolygonRaster.DistanceToBoundary(figure, corner) <= tolerance);
        Assert.True(PolygonRaster.DistanceToBoundary(figure, new PointD(11, 0)) <= tolerance);
        Assert.True(PolygonRaster.DistanceToBoundary(figure, new PointD(10, -1)) <= tolerance);
    }

    [Fact]
    public void Pressure_changes_outline_width()
    {
        static double Height(float pressure)
        {
            InkPoint[] pts =
            [
                new(0, 0, pressure, null, null, null, 0),
                new(20, 0, pressure, null, null, null, 10),
            ];
            var stroke = new InkStroke(Guid.NewGuid(), InkTool.Pen, default, 1.0, pts);
            var figures = StrokeGeometry.BuildOutline(stroke, PressureCurve.Linear, SmoothingLevel.Off);
            Assert.Single(figures);
            var b = PolygonRaster.Bounds(figures);
            return b.MaxY - b.MinY;
        }

        var ratio = Height(0f) / Height(1f);

        Assert.InRange(ratio, (0.3 / 1.5) * 0.95, (0.3 / 1.5) * 1.05);
    }
}
