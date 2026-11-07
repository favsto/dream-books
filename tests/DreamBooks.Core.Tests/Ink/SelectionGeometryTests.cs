using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Ink;

public class SelectionGeometryTests
{
    private static readonly PointD[] Square = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];

    private static InkStroke Stroke(params (double X, double Y)[] points)
        => new(
            Guid.NewGuid(),
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            1,
            points.Select((p, i) => new InkPoint(p.X, p.Y, 0.5f, null, null, null, i)).ToArray());

    [Fact]
    public void ContainsPoint_square_inside_and_outside()
    {
        Assert.True(SelectionGeometry.ContainsPoint(Square, new PointD(50, 50)));
        Assert.False(SelectionGeometry.ContainsPoint(Square, new PointD(150, 50)));
        Assert.False(SelectionGeometry.ContainsPoint(Square, new PointD(-1, 50)));
    }

    [Fact]
    public void StrokesInLoop_selects_stroke_fully_inside()
    {
        var inside = Stroke((10, 10), (50, 50), (90, 90));

        var selected = SelectionGeometry.StrokesInLoop([inside], Square);

        Assert.Same(inside, Assert.Single(selected));
    }

    [Fact]
    public void StrokesInLoop_keeps_stroke_with_exactly_half_inside()
    {
        var half = Stroke((50, 50), (60, 60), (150, 50), (160, 60));

        Assert.Single(SelectionGeometry.StrokesInLoop([half], Square));
    }

    [Fact]
    public void StrokesInLoop_drops_stroke_with_less_than_half_inside()
    {
        var graze = Stroke((50, 50), (150, 50), (160, 60), (170, 70));

        Assert.Empty(SelectionGeometry.StrokesInLoop([graze], Square));
    }

    [Fact]
    public void StrokesInLoop_selects_single_point_stroke()
    {
        var dot = Stroke((20, 20));

        Assert.Single(SelectionGeometry.StrokesInLoop([dot], Square));
    }

    [Fact]
    public void StrokesInLoop_returns_empty_for_two_point_loop()
    {
        var inside = Stroke((10, 10));

        Assert.Empty(SelectionGeometry.StrokesInLoop([inside], [new PointD(0, 0), new PointD(100, 100)]));
    }

    [Fact]
    public void StrokesInLoop_keeps_z_order()
    {
        var a = Stroke((10, 10));
        var b = Stroke((20, 20));

        var selected = SelectionGeometry.StrokesInLoop([a, b], Square);

        Assert.Equal([a, b], selected);
    }

    [Fact]
    public void Bounds_is_null_for_empty()
    {
        Assert.Null(SelectionGeometry.Bounds([]));
    }

    [Fact]
    public void Bounds_covers_all_points()
    {
        var bounds = SelectionGeometry.Bounds([Stroke((10, 20), (30, 5)), Stroke((50, 60))]);

        Assert.Equal(new RectD(10, 5, 50, 60), bounds);
    }
}
