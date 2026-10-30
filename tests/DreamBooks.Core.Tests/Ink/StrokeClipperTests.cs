using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Ink;

public class StrokeClipperTests
{
    private static InkPoint P(double x, double y, float? pressure = 0.5f, long t = 0)
        => new(x, y, pressure, null, null, null, t);

    [Fact]
    public void Stroke_inside_is_unchanged()
    {
        InkPoint[] points = [P(10, 10), P(50, 50), P(100, 100)];

        var pieces = StrokeClipper.Clip(points, 100, 100);

        var piece = Assert.Single(pieces);
        Assert.Equal(points, piece);
    }

    [Fact]
    public void Single_point_inside_is_kept()
    {
        var pieces = StrokeClipper.Clip([P(10, 10)], 100, 100);

        Assert.Single(Assert.Single(pieces));
    }

    [Fact]
    public void Stroke_starting_outside_is_dropped()
    {
        Assert.Empty(StrokeClipper.Clip([P(-1, 50), P(50, 50)], 100, 100));
    }

    [Fact]
    public void Stroke_leaving_page_is_cut_at_edge()
    {
        var pieces = StrokeClipper.Clip([P(90, 50, 0.2f, 0), P(110, 50, 0.6f, 100)], 100, 100);

        var piece = Assert.Single(pieces);
        Assert.Equal(2, piece.Count);
        var last = piece[^1];
        Assert.Equal(100, last.X, 9);
        Assert.Equal(50, last.Y, 9);
        Assert.Equal(0.4f, last.Pressure!.Value, 5);
        Assert.Equal(50, last.TimestampMs);
    }

    [Fact]
    public void Null_optional_values_stay_null_when_interpolating()
    {
        var pieces = StrokeClipper.Clip([P(90, 50, null), P(110, 50, 0.6f)], 100, 100);

        Assert.Null(Assert.Single(pieces)[^1].Pressure);
    }

    [Fact]
    public void Stroke_leaving_and_reentering_gives_two_pieces()
    {
        var pieces = StrokeClipper.Clip([P(50, 50), P(150, 50), P(150, 80), P(50, 80)], 100, 100);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(100, pieces[0][^1].X, 9);
        Assert.Equal(100, pieces[1][0].X, 9);
        Assert.Equal(80, pieces[1][0].Y, 9);
        Assert.All(pieces.SelectMany(p => p), p =>
        {
            Assert.InRange(p.X, 0, 100);
            Assert.InRange(p.Y, 0, 100);
        });
    }
}
