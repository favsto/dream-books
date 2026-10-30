using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Ink;

public class StrokeOutlineTests
{
    private static (double MinX, double MaxX, double MinY, double MaxY) Bounds(IReadOnlyList<IReadOnlyList<PointD>> figures)
    {
        var all = figures.SelectMany(f => f).ToList();
        return (all.Min(p => p.X), all.Max(p => p.X), all.Min(p => p.Y), all.Max(p => p.Y));
    }

    private static double SignedArea(IReadOnlyList<PointD> poly)
    {
        double sum = 0;
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum / 2;
    }

    [Fact]
    public void Single_point_gives_one_disc_of_the_given_width()
    {
        var figures = StrokeOutline.Build([new PointD(5, 7)], [2]);

        Assert.Single(figures);
        var b = Bounds(figures);
        Assert.InRange(b.MinX, 4 - 0.05, 4 + 0.05);
        Assert.InRange(b.MaxX, 6 - 0.05, 6 + 0.05);
        Assert.InRange(b.MinY, 6 - 0.05, 6 + 0.05);
        Assert.InRange(b.MaxY, 8 - 0.05, 8 + 0.05);
    }

    [Fact]
    public void All_figures_share_positive_winding()
    {
        PointD[] centers = [new(0, 0), new(3, 1), new(5, 5), new(2, 6)];
        double[] widths = [1, 3, 0.5, 2];

        var figures = StrokeOutline.Build(centers, widths);

        Assert.True(figures.Count > 4);
        Assert.All(figures, f => Assert.True(SignedArea(f) > 0));
    }

    [Fact]
    public void Output_contains_no_NaN_or_infinity()
    {
        PointD[] centers = [new(1, 1), new(1, 1), new(1, 1.0001), new(4, 4)];
        double[] widths = [0.01, 0.01, 0, 2];

        var figures = StrokeOutline.Build(centers, widths);

        Assert.All(figures.SelectMany(f => f), p =>
        {
            Assert.True(double.IsFinite(p.X));
            Assert.True(double.IsFinite(p.Y));
        });
    }

    [Fact]
    public void Straight_constant_width_line_has_expected_bounds()
    {
        var figures = StrokeOutline.Build([new PointD(0, 0), new PointD(10, 0)], [2, 2]);

        var b = Bounds(figures);
        Assert.InRange(b.MinX, -1.05, -0.95);
        Assert.InRange(b.MaxX, 10.95, 11.05);
        Assert.InRange(b.MinY, -1.05, -0.95);
        Assert.InRange(b.MaxY, 0.95, 1.05);
    }

    [Fact]
    public void Contained_disc_produces_no_quad()
    {
        var figures = StrokeOutline.Build([new PointD(0, 0), new PointD(0.1, 0)], [4, 1]);

        Assert.Equal(2, figures.Count);
        Assert.All(figures, f => Assert.True(f.Count >= 8));
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
            var b = Bounds(StrokeGeometry.BuildOutline(stroke, PressureCurve.Linear, SmoothingLevel.Off));
            return b.MaxY - b.MinY;
        }

        var ratio = Height(0f) / Height(1f);

        Assert.InRange(ratio, (0.3 / 1.5) * 0.95, (0.3 / 1.5) * 1.05);
    }

    [Fact]
    public void Centers_and_widths_must_have_same_length()
    {
        Assert.Throws<ArgumentException>(() => StrokeOutline.Build([new PointD(0, 0)], [1, 2]));
    }
}
