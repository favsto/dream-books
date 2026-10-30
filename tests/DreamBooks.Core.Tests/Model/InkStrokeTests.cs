using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Model;

public class InkStrokeTests
{
    private static InkPoint P(double x) => new(x, 0, 0.5f, null, null, null, 0);

    [Fact]
    public void Stroke_requires_at_least_one_point()
        => Assert.Throws<ArgumentException>(() => new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1, []));

    [Fact]
    public void Stroke_requires_positive_width()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 0, [P(1)]));

    [Fact]
    public void Stroke_copies_points()
    {
        var source = new List<InkPoint> { P(1), P(2) };
        var stroke = new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1, source);
        source.Add(P(3));
        source[0] = P(9);

        Assert.Equal(2, stroke.Points.Count);
        Assert.Equal(1, stroke.Points[0].X);
    }

    [Fact]
    public void WithPoints_keeps_attributes_and_changes_id()
    {
        var color = InkColor.FromRgb(10, 20, 30);
        var stroke = new InkStroke(Guid.NewGuid(), InkTool.Highlighter, color, 4.5, [P(1)]);

        var copy = stroke.WithPoints([P(5), P(6)]);

        Assert.NotEqual(stroke.Id, copy.Id);
        Assert.Equal(InkTool.Highlighter, copy.Tool);
        Assert.Equal(color, copy.Color);
        Assert.Equal(4.5, copy.BaseWidthPt);
        Assert.Equal(2, copy.Points.Count);
    }

    [Fact]
    public void PointD_Distance_and_InkPoint_Position()
    {
        Assert.Equal(5.0, Geometry.PointD.Distance(new(0, 0), new(3, 4)), 9);
        Assert.Equal(new Geometry.PointD(1, 2), new InkPoint(1, 2, null, null, null, null, 0).Position);
    }
}
