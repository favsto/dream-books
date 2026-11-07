using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Ink;

public class StrokeTransformTests
{
    private const double _pageW = 200;
    private const double _pageH = 200;

    private static InkStroke Stroke(InkTool tool = InkTool.Pen, InkColor? color = null, double width = 2, ChiselTip? chisel = null, params (double X, double Y)[] points)
        => new(
            Guid.NewGuid(),
            tool,
            color ?? InkColor.FromRgb(10, 20, 30),
            width,
            points.Select((p, i) => new InkPoint(p.X, p.Y, 0.7f, 0.1f, 0.2f, 0.3f, i * 10)).ToArray(),
            chisel);

    private static InkStroke Simple(params (double X, double Y)[] points) => Stroke(points: points);

    [Fact]
    public void Translate_moves_points_and_keeps_pressure_and_time()
    {
        var s = Simple((10, 10), (20, 30));

        var moved = Assert.Single(StrokeTransform.Translate([s], 5, 7, _pageW, _pageH));

        Assert.Equal(15, moved.Points[0].X);
        Assert.Equal(17, moved.Points[0].Y);
        Assert.Equal(25, moved.Points[1].X);
        Assert.Equal(37, moved.Points[1].Y);
        Assert.Equal(0.7f, moved.Points[1].Pressure);
        Assert.Equal(10, moved.Points[1].TimestampMs);
        Assert.Equal(s.BaseWidthPt, moved.BaseWidthPt);
    }

    [Fact]
    public void Translate_clamps_to_page_edge()
    {
        var s = Simple((150, 10), (190, 30));

        var moved = Assert.Single(StrokeTransform.Translate([s], 100, -50, _pageW, _pageH));

        Assert.Equal(200, moved.Points[1].X);
        Assert.Equal(0, moved.Points[0].Y);
    }

    [Fact]
    public void Scale_doubles_distances_and_width_about_anchor()
    {
        var s = Simple((10, 10), (20, 20));

        var scaled = Assert.Single(StrokeTransform.Scale([s], 2, new PointD(10, 10), _pageW, _pageH));

        Assert.Equal(10, scaled.Points[0].X);
        Assert.Equal(30, scaled.Points[1].X);
        Assert.Equal(30, scaled.Points[1].Y);
        Assert.Equal(4, scaled.BaseWidthPt);
    }

    [Fact]
    public void Scale_clamps_to_minimum_0_2()
    {
        var s = Simple((10, 10), (110, 10));

        var scaled = Assert.Single(StrokeTransform.Scale([s], -3, new PointD(10, 10), _pageW, _pageH));

        Assert.Equal(30, scaled.Points[1].X, 6);
        Assert.Equal(0.4, scaled.BaseWidthPt, 6);
    }

    [Fact]
    public void Scale_never_leaves_the_page()
    {
        var s = Simple((100, 100), (150, 150));

        var scaled = Assert.Single(StrokeTransform.Scale([s], 10, new PointD(100, 100), _pageW, _pageH));

        Assert.All(scaled.Points, p => Assert.InRange(p.X, 0, _pageW));
        Assert.All(scaled.Points, p => Assert.InRange(p.Y, 0, _pageH));
        Assert.Equal(200, scaled.Points[1].X, 6);
    }

    [Fact]
    public void Scale_keeps_chisel()
    {
        var chisel = new ChiselTip(30, 0.4);
        var s = Stroke(InkTool.Highlighter, chisel: chisel, points: [(10, 10), (20, 20)]);

        var scaled = Assert.Single(StrokeTransform.Scale([s], 1.5, new PointD(10, 10), _pageW, _pageH));

        Assert.Equal(chisel, scaled.Chisel);
    }

    [Fact]
    public void Recolor_pen_takes_new_colour()
    {
        var s = Simple((10, 10));

        var recoloured = Assert.Single(StrokeTransform.Recolor([s], InkColor.FromRgb(200, 0, 0)));

        Assert.Equal(InkColor.FromRgb(200, 0, 0), recoloured.Color);
        Assert.Equal(s.BaseWidthPt, recoloured.BaseWidthPt);
        Assert.Equal(InkTool.Pen, recoloured.Tool);
    }

    [Fact]
    public void Recolor_highlighter_keeps_alpha()
    {
        var s = Stroke(InkTool.Highlighter, new InkColor(90, 255, 255, 0), 12, null, (10, 10));

        var recoloured = Assert.Single(StrokeTransform.Recolor([s], InkColor.FromRgb(0, 200, 0)));

        Assert.Equal(new InkColor(90, 0, 200, 0), recoloured.Color);
    }

    [Fact]
    public void All_transforms_give_new_ids()
    {
        var s = Simple((10, 10), (20, 20));

        Assert.NotEqual(s.Id, StrokeTransform.Translate([s], 1, 1, _pageW, _pageH)[0].Id);
        Assert.NotEqual(s.Id, StrokeTransform.Scale([s], 1.1, new PointD(0, 0), _pageW, _pageH)[0].Id);
        Assert.NotEqual(s.Id, StrokeTransform.Recolor([s], InkColor.FromRgb(1, 2, 3))[0].Id);
    }
}
