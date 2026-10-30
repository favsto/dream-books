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

        var pieces = Clip(points, 100, 100);

        var piece = Assert.Single(pieces);
        Assert.Equal(points, piece);
    }

    [Fact]
    public void Single_point_inside_is_kept()
    {
        var pieces = Clip([P(10, 10)], 100, 100);

        Assert.Single(Assert.Single(pieces));
    }

    [Fact]
    public void Stroke_starting_outside_is_dropped()
    {
        Assert.Empty(Clip([P(-1, 50), P(50, 50)], 100, 100));
    }

    [Fact]
    public void Stroke_leaving_page_is_cut_at_edge()
    {
        var pieces = Clip([P(90, 50, 0.2f, 0), P(110, 50, 0.6f, 100)], 100, 100);

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
        var pieces = Clip([P(90, 50, null), P(110, 50, 0.6f)], 100, 100);

        Assert.Null(Assert.Single(pieces)[^1].Pressure);
    }

    [Fact]
    public void Stroke_leaving_and_reentering_gives_two_pieces()
    {
        var pieces = Clip([P(50, 50), P(150, 50), P(150, 80), P(50, 80)], 100, 100);

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

    private static IReadOnlyList<IReadOnlyList<InkPoint>> Clip(IReadOnlyList<InkPoint> points, double w, double h)
    {
        var pieces = StrokeClipper.Clip(points, w, h);
        foreach (var piece in pieces)
        {
            Assert.NotEmpty(piece);
            for (var i = 0; i < piece.Count; i++)
            {
                Assert.InRange(piece[i].X, 0, w);
                Assert.InRange(piece[i].Y, 0, h);
                if (i > 0)
                {
                    Assert.NotEqual(piece[i - 1].Position, piece[i].Position);
                }
            }
        }

        return pieces;
    }

    [Fact]
    public void Stroke_starting_on_edge_and_heading_out_is_a_single_point_dot()
    {
        var pieces = Clip([P(100, 50), P(120, 50)], 100, 100);

        Assert.Single(Assert.Single(pieces));
    }

    [Fact]
    public void Vertex_exactly_on_edge_mid_stroke_is_not_duplicated()
    {
        InkPoint[] points = [P(50, 50), P(100, 50), P(50, 60)];

        var piece = Assert.Single(Clip(points, 100, 100));

        Assert.Equal(points, piece);
    }

    [Fact]
    public void Segment_only_touching_corner_adds_no_piece()
    {
        // (150,50) -> (50,150) touches the corner (100,100) only.
        var pieces = Clip([P(50, 50), P(150, 50), P(50, 150), P(50, 50)], 100, 100);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(2, pieces[0].Count);
        Assert.Equal(2, pieces[1].Count);
    }

    [Fact]
    public void Fully_outside_middle_segment_gives_two_pieces()
    {
        var pieces = Clip([P(50, 50), P(150, 50), P(150, 150), P(50, 150), P(50, 80)], 100, 100);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(new[] { 50.0, 100.0 }, pieces[0].Select(p => p.X));
        Assert.Equal(100, pieces[1][0].Y, 9);
        Assert.Equal(80, pieces[1][^1].Y, 9);
    }

    [Fact]
    public void Reentry_point_interpolates_pressure_and_timestamp()
    {
        var pieces = Clip([P(50, 50, 0.1f, 0), P(150, 50, 0.1f, 100), P(150, 80, 0.5f, 200), P(50, 80, 0.9f, 300)], 100, 100);

        var entry = pieces[1][0];
        Assert.Equal(0.7f, entry.Pressure!.Value, 5);
        Assert.Equal(250, entry.TimestampMs);
    }

    [Fact]
    public void Tilt_and_tip_angle_interpolate_when_both_present_else_stay_null()
    {
        var a = new InkPoint(90, 50, 0.5f, 10f, null, 0f, 0);
        var b = new InkPoint(110, 50, 0.5f, 30f, 20f, null, 0);

        var last = Assert.Single(Clip([a, b], 100, 100))[^1];

        Assert.Equal(20f, last.TiltX!.Value, 4);
        Assert.Null(last.TiltY);
        Assert.Null(last.TipAngle);

        var c = new InkPoint(90, 50, 0.5f, 10f, 4f, 2f, 0);
        var d = new InkPoint(110, 50, 0.5f, 30f, 8f, 6f, 0);
        var last2 = Assert.Single(Clip([c, d], 100, 100))[^1];
        Assert.Equal(6f, last2.TiltY!.Value, 4);
        Assert.Equal(4f, last2.TipAngle!.Value, 4);
    }

    [Fact]
    public void Interpolated_points_are_within_page_despite_rounding()
    {
        var pieces = Clip([P(0.1, 0.3), P(100.3, 33.7), P(0.1, 66.6), P(-0.2, 99.9), P(7.7, 7.7)], 100, 100);

        Assert.All(pieces.SelectMany(p => p), p =>
        {
            Assert.InRange(p.X, 0, 100);
            Assert.InRange(p.Y, 0, 100);
        });
    }

    [Fact]
    public void Duplicate_cluster_keeps_max_pressure_and_latest_timestamp()
    {
        var pieces = Clip([P(10, 10, 0.1f, 0), P(10, 10, 0.5f, 8), P(10, 10, 0.9f, 16), P(20, 10, 0.4f, 24)], 100, 100);

        var piece = Assert.Single(pieces);
        Assert.Equal(2, piece.Count);
        Assert.Equal(0.9f, piece[0].Pressure);
        Assert.Equal(16, piece[0].TimestampMs);
    }

    [Fact]
    public void Trailing_duplicate_cluster_carries_max_pressure()
    {
        var pieces = Clip([P(10, 10, 0.3f, 0), P(20, 10, 0.1f, 8), P(20, 10, 0.5f, 16), P(20, 10, 0.9f, 24)], 100, 100);

        var piece = Assert.Single(pieces);
        Assert.Equal(2, piece.Count);
        Assert.Equal(0.9f, piece[^1].Pressure);
        Assert.Equal(24, piece[^1].TimestampMs);
    }

    [Fact]
    public void Dot_keeps_max_pressure()
    {
        var pieces = Clip([P(10, 10, 0.1f, 0), P(10, 10, 0.5f, 8), P(10, 10, 0.9f, 16)], 100, 100);

        var point = Assert.Single(Assert.Single(pieces));
        Assert.Equal(0.9f, point.Pressure);
    }

    [Fact]
    public void Null_pressure_cluster_stays_null()
    {
        var pieces = Clip([P(10, 10, null, 0), P(10, 10, null, 8)], 100, 100);

        var point = Assert.Single(Assert.Single(pieces));
        Assert.Null(point.Pressure);
    }
}
