using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Ink;

public class HighlighterGeometryTests
{
    private const double _length = 8;
    private const double _ratio = 0.25;
    private const double _tolerance = 1e-6;

    // A straight stroke from (x0, y0) to (x1, y1) sampled every `spacing` points.
    private static InkPoint[] Line(double x0, double y0, double x1, double y1, double spacing = 0.5)
    {
        var length = Math.Sqrt(((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)));
        var steps = Math.Max(1, (int)Math.Ceiling(length / spacing));
        var points = new InkPoint[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            points[i] = new InkPoint(x0 + ((x1 - x0) * t), y0 + ((y1 - y0) * t), 0.5f, null, null, null, i * 8L);
        }

        return points;
    }

    // Extent of the outline perpendicular to a motion of direction phi (degrees, counter-clockwise on screen, y down).
    private static double WidthAcross(IReadOnlyList<PointD> outline, double phiDeg)
    {
        var phi = phiDeg * Math.PI / 180;
        var nx = Math.Sin(phi);
        var ny = Math.Cos(phi);
        var projections = outline.Select(p => (p.X * nx) + (p.Y * ny)).ToList();
        return projections.Max() - projections.Min();
    }

    // Vertical extent of the polygon's cross-section on the vertical line x = c.
    private static double VerticalSection(IReadOnlyList<PointD> poly, double c)
    {
        var ys = new List<double>();
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            if ((a.X <= c && b.X > c) || (b.X <= c && a.X > c))
            {
                ys.Add(a.Y + ((c - a.X) * (b.Y - a.Y) / (b.X - a.X)));
            }
        }

        return ys.Max() - ys.Min();
    }

    private static double ExpectedChiselWidth(double thetaDeg, double phiDeg) =>
        _length * (_ratio + ((1 - _ratio) * Math.Abs(Math.Sin((thetaDeg - phiDeg) * Math.PI / 180))));

    [Fact]
    public void Chisel_horizontal_stroke_at_0_degrees_is_thin_and_vertical_stroke_is_full_width()
    {
        foreach (var theta in new[] { 0.0, 180.0 })
        {
            var chisel = new ChiselTip(theta, _ratio);
            var horizontal = HighlighterGeometry.ChiselOutline(Line(10, 50, 110, 50), chisel, _length);
            var vertical = HighlighterGeometry.ChiselOutline(Line(50, 110, 50, 10), chisel, _length);

            Assert.Equal(_ratio * _length, WidthAcross(horizontal, 0), _tolerance);
            Assert.Equal(_length, WidthAcross(vertical, 90), _tolerance);
        }
    }

    [Fact]
    public void Chisel_at_90_degrees_swaps_the_horizontal_and_vertical_widths()
    {
        var chisel = new ChiselTip(90, _ratio);
        var horizontal = HighlighterGeometry.ChiselOutline(Line(10, 50, 110, 50), chisel, _length);
        var vertical = HighlighterGeometry.ChiselOutline(Line(50, 110, 50, 10), chisel, _length);

        Assert.Equal(_length, WidthAcross(horizontal, 0), _tolerance);
        Assert.Equal(_ratio * _length, WidthAcross(vertical, 90), _tolerance);
    }

    [Fact]
    public void Chisel_at_45_degrees_gives_the_expected_width_for_a_horizontal_stroke()
    {
        var chisel = new ChiselTip(45, _ratio);
        var horizontal = HighlighterGeometry.ChiselOutline(Line(10, 50, 110, 50), chisel, _length);

        // Along the nib (up and to the right on screen, y down): thin.
        var alongNib = HighlighterGeometry.ChiselOutline(Line(10, 110, 90, 30), chisel, _length);

        // Across the nib (down and to the right on screen): full length.
        var acrossNib = HighlighterGeometry.ChiselOutline(Line(10, 30, 90, 110), chisel, _length);

        Assert.Equal(_length * (_ratio + ((1 - _ratio) * Math.Sin(Math.PI / 4))), WidthAcross(horizontal, 0), _tolerance);
        Assert.Equal(ExpectedChiselWidth(45, 0), WidthAcross(horizontal, 0), _tolerance);
        Assert.Equal(_ratio * _length, WidthAcross(alongNib, 45), _tolerance);
        Assert.Equal(_length, WidthAcross(acrossNib, -45), _tolerance);
    }

    [Fact]
    public void Chisel_outline_is_closed_and_has_no_points_beyond_the_nib_envelope()
    {
        foreach (var theta in new[] { 0.0, 30.0, 45.0, 90.0, 135.0, 180.0 })
        {
            AssertClosedWithinEnvelope(theta);
        }
    }

    private static void AssertClosedWithinEnvelope(double theta)
    {
        var chisel = new ChiselTip(theta, _ratio);

        // A scrub back and forth and a turn: the fill must have no hole along the centre line.
        var corners = new[] { (10.0, 50.0), (80.0, 52.0), (20.0, 55.0), (90.0, 58.0), (90.0, 90.0) };
        var points = corners.Zip(corners.Skip(1), (a, b) => Line(a.Item1, a.Item2, b.Item1, b.Item2))
            .SelectMany((segment, i) => i == 0 ? segment : segment.Skip(1))
            .ToArray();

        var outline = HighlighterGeometry.ChiselOutline(points, chisel, _length);

        Assert.True(outline.Count >= 3);
        Assert.True(PolygonRaster.SignedArea(outline) > 0);
        IReadOnlyList<IReadOnlyList<PointD>> figures = [outline];
        foreach (var p in points)
        {
            Assert.NotEqual(0, PolygonRaster.Winding(figures, new PointD(p.X + 1e-3, p.Y + 1e-3)));
        }

        // Every outline vertex lies within the nib (a segment of length (1 - r)L with round ends of
        // diameter rL) placed at some point of the centre line.
        var t = theta * Math.PI / 180;
        var half = (1 - _ratio) * _length / 2;
        var h = new PointD(half * Math.Cos(t), -half * Math.Sin(t));
        var dense = new List<PointD>();
        for (var i = 0; i + 1 < points.Length; i++)
        {
            for (var k = 0; k < 20; k++)
            {
                var s = k / 20.0;
                dense.Add(new PointD(
                    points[i].X + ((points[i + 1].X - points[i].X) * s),
                    points[i].Y + ((points[i + 1].Y - points[i].Y) * s)));
            }
        }

        dense.Add(points[^1].Position);

        // The fill covers the nib's core at every centre-line point (no holes under the non-zero rule).
        foreach (var c in dense.Where((_, i) => i % 7 == 0))
        {
            foreach (var s in new[] { -0.95, -0.5, 0, 0.5, 0.95 })
            {
                var inner = new PointD(c.X + (s * h.X) + 1e-4, c.Y + (s * h.Y) + 1e-4);
                Assert.NotEqual(0, PolygonRaster.Winding(figures, inner));
            }
        }

        foreach (var q in outline)
        {
            var reach = (_length / 2) + 0.1;
            var nearest = dense
                .Where(c => Math.Abs(c.X - q.X) <= reach && Math.Abs(c.Y - q.Y) <= reach)
                .DefaultIfEmpty(dense[0])
                .Min(c => PolygonRaster.DistanceToBoundary(
                    [new PointD(c.X + h.X, c.Y + h.Y), new PointD(c.X - h.X, c.Y - h.Y)], q));
            Assert.True(nearest <= (_ratio * _length / 2) + 0.05, $"Vertex {q} is {nearest} pt from the nib core.");
        }
    }

    [Fact]
    public void Constant_width_outline_has_the_same_width_everywhere_regardless_of_point_spacing()
    {
        // Irregular spacing and changing pressure: neither may change the width (FR-42).
        var xs = new[] { 0.0, 0.3, 0.4, 5, 5.2, 17, 17.1, 17.15, 30, 31, 45, 70, 70.5, 100 };
        var points = xs.Select((x, i) => new InkPoint(x, 40, (float)(0.1 + (0.8 * (i % 3) / 2.0)), null, null, null, i * 8L))
            .ToArray();

        var outline = HighlighterGeometry.ConstantWidthOutline(points, 6);

        Assert.True(PolygonRaster.SignedArea(outline) > 0);
        foreach (var c in new[] { 1.0, 4.9, 10, 17.12, 25, 40, 69, 85, 99 })
        {
            Assert.Equal(6, VerticalSection(outline, c), 1e-3);
        }

        Assert.Equal(6, WidthAcross(outline, 0), 1e-3);
    }

    [Fact]
    public void Straightening_accepts_a_line_within_3_degrees_and_2_percent_and_rejects_a_wavy_one()
    {
        // Chord tilted by about 2.3 degrees, wobble of 1.5% of the chord length.
        var nearlyStraight = Line(0, 100, 100, 96, 2)
            .Select((p, i) => p with { Y = p.Y + (1.5 * Math.Sin(i * 0.7)) })
            .ToArray();
        nearlyStraight[0] = nearlyStraight[0] with { Y = 100 };
        nearlyStraight[^1] = nearlyStraight[^1] with { Y = 96 };

        var wavy = Line(0, 100, 100, 100, 2)
            .Select((p, i) => p with { Y = p.Y + (3 * Math.Sin(i * 0.5)) })
            .ToArray();
        wavy[0] = wavy[0] with { Y = 100 };
        wavy[^1] = wavy[^1] with { Y = 100 };

        var tilted = Line(0, 100, 100, 100 - (100 * Math.Tan(4 * Math.PI / 180)), 2);

        Assert.NotNull(HighlighterGeometry.StraightenIfNearHorizontal(nearlyStraight));
        Assert.Null(HighlighterGeometry.StraightenIfNearHorizontal(wavy));
        Assert.Null(HighlighterGeometry.StraightenIfNearHorizontal(tilted));
    }

    [Fact]
    public void Straightening_keeps_the_end_points_and_their_timestamps()
    {
        var points = Line(10, 100, 110, 97, 2);
        var first = points[0] with { TimestampMs = 1000, Pressure = 0.3f };
        var last = points[^1] with { TimestampMs = 1850, Pressure = 0.7f };
        points[0] = first;
        points[^1] = last;

        var straight = HighlighterGeometry.StraightenIfNearHorizontal(points);

        Assert.NotNull(straight);
        Assert.Equal(2, straight.Count);
        Assert.Equal(first.X, straight[0].X);
        Assert.Equal(last.X, straight[1].X);
        Assert.Equal(1000, straight[0].TimestampMs);
        Assert.Equal(1850, straight[1].TimestampMs);
        Assert.Equal(first.Pressure, straight[0].Pressure);
        Assert.Equal(last.Pressure, straight[1].Pressure);

        // Horizontal (FR-44): both ends at the mean height of the drawn ends.
        Assert.Equal(98.5, straight[0].Y, 1e-9);
        Assert.Equal(98.5, straight[1].Y, 1e-9);
    }

    [Fact]
    public void A_stroke_with_fewer_than_two_points_is_not_straightened()
    {
        Assert.Null(HighlighterGeometry.StraightenIfNearHorizontal([]));
        Assert.Null(HighlighterGeometry.StraightenIfNearHorizontal([new InkPoint(5, 5, null, null, null, null, 0)]));

        // Two points at the same place have no chord.
        Assert.Null(HighlighterGeometry.StraightenIfNearHorizontal(
            [new InkPoint(5, 5, null, null, null, null, 0), new InkPoint(5, 5, null, null, null, null, 8)]));
    }
}
