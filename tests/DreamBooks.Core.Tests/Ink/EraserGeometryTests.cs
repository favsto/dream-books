using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Ink;

public class EraserGeometryTests
{
    private static InkPoint P(double x, double y)
        => new(x, y, 0.5f, null, null, null, 0);

    private static InkStroke Stroke(params InkPoint[] points)
        => new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2, points);

    [Fact]
    public void Hits_detects_stroke_crossed_between_samples()
    {
        var stroke = Stroke(P(50, 0), P(50, 100));

        Assert.True(EraserGeometry.Hits(stroke, new PointD(0, 50), new PointD(100, 50), 1));
    }

    [Fact]
    public void Hits_misses_distant_stroke()
    {
        var stroke = Stroke(P(50, 0), P(50, 100));

        Assert.False(EraserGeometry.Hits(stroke, new PointD(0, 50), new PointD(20, 50), 1));
    }

    [Fact]
    public void Hits_uses_width_margin()
    {
        var stroke = Stroke(P(50, 0), P(50, 100));

        // distance 2.0 from the line; radius 1 + 0.75 * 2 = 2.5 reaches it, radius 1 alone would not.
        Assert.True(EraserGeometry.Hits(stroke, new PointD(48, 50), new PointD(48, 50), 1));
    }

    [Fact]
    public void Partial_erase_in_the_middle_splits_into_two_strokes_with_new_ids()
    {
        var stroke = Stroke(P(0, 0), P(100, 0));

        var pieces = EraserGeometry.ErasePartial(stroke, new PointD(50, 0), new PointD(50, 0), 5);

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.NotEqual(stroke.Id, p.Id));
        Assert.NotEqual(pieces[0].Id, pieces[1].Id);
        Assert.Equal(0, pieces[0].Points[0].X, 6);
        Assert.InRange(pieces[0].Points[^1].X, 44.5, 45.1);
        Assert.InRange(pieces[1].Points[0].X, 54.9, 55.5);
        Assert.Equal(100, pieces[1].Points[^1].X, 6);
        Assert.All(pieces[0].Points, p => Assert.InRange(p.Pressure!.Value, 0.49f, 0.51f));
    }

    [Fact]
    public void Partial_erase_at_the_end_shortens_the_stroke()
    {
        var stroke = Stroke(P(0, 0), P(100, 0));

        var pieces = EraserGeometry.ErasePartial(stroke, new PointD(100, 0), new PointD(100, 0), 10);

        var piece = Assert.Single(pieces);
        Assert.Equal(0, piece.Points[0].X, 6);
        Assert.InRange(piece.Points[^1].X, 89.5, 90.1);
    }

    [Fact]
    public void Partial_erase_of_whole_stroke_returns_empty()
    {
        var stroke = Stroke(P(0, 0), P(10, 0));

        Assert.Empty(EraserGeometry.ErasePartial(stroke, new PointD(5, 0), new PointD(5, 0), 20));
    }

    [Fact]
    public void Partial_erase_without_hit_returns_same_instance()
    {
        var stroke = Stroke(P(0, 0), P(100, 0));

        var pieces = EraserGeometry.ErasePartial(stroke, new PointD(50, 50), new PointD(60, 50), 5);

        Assert.Same(stroke, Assert.Single(pieces));
    }

    [Fact]
    public void Partial_erase_discards_runs_shorter_than_half_a_point()
    {
        var stroke = Stroke(P(0, 0), P(10.3, 0));

        // Erasing [0.3 .. 10.3] leaves a 0.3 pt stub at the start, which is discarded.
        var pieces = EraserGeometry.ErasePartial(stroke, new PointD(5.3, 0), new PointD(5.3, 0), 5);

        Assert.Empty(pieces);
    }

    [Fact]
    public void Partial_erase_with_fast_sweep_cuts_stroke_between_samples()
    {
        var stroke = Stroke(P(50, 0), P(50, 100));

        var pieces = EraserGeometry.ErasePartial(stroke, new PointD(0, 50), new PointD(100, 50), 2);

        Assert.Equal(2, pieces.Count);
    }

    [Fact]
    public void Resampled_points_interpolate_attributes_and_keep_null_fields_null()
    {
        var a = new InkPoint(0, 0, 0.2f, null, 10f, null, 0);
        var b = new InkPoint(100, 0, 0.6f, null, 30f, null, 100);
        var stroke = Stroke(a, b);

        var pieces = EraserGeometry.ErasePartial(stroke, new PointD(100, 0), new PointD(100, 0), 20);

        var piece = Assert.Single(pieces);
        var mid = piece.Points.First(p => p.X > 49.9 && p.X < 50.1);
        Assert.Equal(0.4f, mid.Pressure!.Value, 0.01f);
        Assert.Equal(20f, mid.TiltY!.Value, 0.1f);
        Assert.Null(mid.TiltX);
        Assert.Null(mid.TipAngle);
    }
}
