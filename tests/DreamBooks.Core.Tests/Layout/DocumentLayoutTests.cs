using System.Diagnostics;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Layout;

public class DocumentLayoutTests
{
    private static DocumentLayout Uniform(int n, double w = 600, double h = 800)
        => new(Enumerable.Repeat(new PageDimensions(w, h), n).ToArray());


    [Fact]
    public void Tops_accumulate_page_heights_and_gaps()
    {
        var l = Uniform(3);
        Assert.Equal(0, l.PageTopPt(0));
        Assert.Equal(808, l.PageTopPt(1));
        Assert.Equal(1616, l.PageTopPt(2));
        Assert.Equal(2424, l.TotalHeightPt);
        Assert.Equal(3, l.PageCount);
    }

    [Fact]
    public void Narrower_pages_are_centred_in_the_widest_column()
    {
        var l = new DocumentLayout([new PageDimensions(600, 800), new PageDimensions(400, 800)]);
        Assert.Equal(600, l.ColumnWidthPt);
        Assert.Equal(0, l.PageLeftPt(0));
        Assert.Equal(100, l.PageLeftPt(1));
    }

    [Fact]
    public void PageAt_maps_inside_gap_and_ends_correctly()
    {
        var l = Uniform(3);
        Assert.Equal(0, l.PageAt(-50));
        Assert.Equal(0, l.PageAt(799));
        Assert.Equal(1, l.PageAt(800));
        Assert.Equal(1, l.PageAt(808));
        Assert.Equal(2, l.PageAt(1608));
        Assert.Equal(2, l.PageAt(99999));
    }

    [Fact]
    public void VisibleRange_includes_partially_visible_pages_and_is_clamped()
    {
        var l = Uniform(5);
        Assert.Equal((0, 0), l.VisibleRange(0, 500));
        Assert.Equal((0, 1), l.VisibleRange(700, 200));
        Assert.Equal((1, 1), l.VisibleRange(808, 800));
        Assert.Equal((0, 4), l.VisibleRange(-500, 100000));
        Assert.Equal((4, 4), l.VisibleRange(99999, 100));
    }

    [Fact]
    public void RetainedRange_extends_by_margin_and_clamps()
    {
        var l = Uniform(10);
        Assert.Equal((0, 2), l.RetainedRange(0, 500));
        Assert.Equal((2, 6), l.RetainedRange(l.PageTopPt(4), 100));
        Assert.Equal((7, 9), l.RetainedRange(l.PageTopPt(9), 100));
        Assert.Equal((3, 5), l.RetainedRange(l.PageTopPt(4), 100, 1));
    }

    [Fact]
    public void FitWidthZoom_uses_the_widest_page_and_is_clamped()
    {
        var l = new DocumentLayout([new PageDimensions(400, 800), new PageDimensions(600, 800)]);
        Assert.Equal(1.25, l.FitWidthZoom(1000), 9);
        Assert.Equal(0.25, l.FitWidthZoom(10));
        Assert.Equal(8, l.FitWidthZoom(1_000_000));
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1)]
    [InlineData(2.5)]
    [InlineData(8)]
    public void ToPosition_then_ToOffsets_round_trips_at_several_zooms(double zoom)
    {
        var l = new DocumentLayout(Enumerable.Range(0, 20).Select(i => new PageDimensions(300 + (i * 17 % 300), 500 + (i * 53 % 400))).ToArray());
        var rnd = new Random(42);
        const double vw = 900;
        const double vh = 700;
        double maxX = Math.Max(0, l.ExtentWidthDip(zoom, vw) - vw);
        double maxY = Math.Max(0, l.ExtentHeightDip(zoom) - vh);
        for (int i = 0; i < 200; i++)
        {
            double ox = rnd.NextDouble() * maxX;
            double oy = rnd.NextDouble() * maxY;
            ViewPosition p = l.ToPosition(ox, oy, vw, zoom);
            (double rx, double ry) = l.ToOffsets(p, vw, vh);
            Assert.Equal(ox, rx, 6);
            Assert.Equal(oy, ry, 6);
        }
    }

    // Document point (page points) under a viewport point, derived only from scroll offsets.
    private static (double X, double Y) DocPointFromOffsets(DocumentLayout l, double ox, double oy, double ax, double ay, double zoom, double vw)
    {
        double s = zoom * Units.DipPerPt;
        double margin = (l.ExtentWidthDip(zoom, vw) - (l.ColumnWidthPt * s)) / 2;
        return ((ox + ax - margin) / s, (oy + ay) / s);
    }

    [Fact]
    public void ZoomAt_keeps_the_anchor_point_within_half_a_pixel()
    {
        var l = new DocumentLayout(Enumerable.Range(0, 30).Select(i => new PageDimensions(300 + (i % 4 * 60), 700)).ToArray());
        var rnd = new Random(7);
        const double vw = 800;
        const double vh = 600;
        int checkedCases = 0;
        for (int i = 0; i < 400 && checkedCases < 50; i++)
        {
            double z0 = 0.25 + (rnd.NextDouble() * 3);
            double z1 = 0.25 + (rnd.NextDouble() * 3);
            double maxX0 = Math.Max(0, l.ExtentWidthDip(z0, vw) - vw);
            double ox0 = rnd.NextDouble() * maxX0;
            double oy0 = rnd.NextDouble() * (l.ExtentHeightDip(z0) - vh);
            double ax = rnd.NextDouble() * vw;
            double ay = rnd.NextDouble() * vh;
            ViewPosition start = l.ToPosition(ox0, oy0, vw, z0);
            ViewPosition next = l.ZoomAt(start, z1, ax, ay, vw);

            // Skip cases where the extent would clamp the target offsets.
            double s1 = z1 * Units.DipPerPt;
            double rawY = (l.PageTopPt(next.PageIndex) + next.YPt) * s1;
            double rawX = (next.ColumnXPt * s1) + ((l.ExtentWidthDip(z1, vw) - (l.ColumnWidthPt * s1)) / 2) - (vw / 2);
            double maxX1 = Math.Max(0, l.ExtentWidthDip(z1, vw) - vw);
            if (rawY < 0 || rawY > l.ExtentHeightDip(z1) - vh || rawX < 0 || rawX > maxX1)
            {
                continue;
            }

            (double ox1, double oy1) = l.ToOffsets(next, vw, vh);
            var before = DocPointFromOffsets(l, ox0, oy0, ax, ay, z0, vw);
            var after = DocPointFromOffsets(l, ox1, oy1, ax, ay, z1, vw);
            double halfPixelPt = 0.5 / s1;
            Assert.InRange(Math.Abs(before.X - after.X), 0, halfPixelPt);
            Assert.InRange(Math.Abs(before.Y - after.Y), 0, halfPixelPt);
            Assert.Equal(z1, next.Zoom);
            checkedCases++;
        }

        Assert.True(checkedCases >= 20, $"only {checkedCases} unclamped cases");
    }

    [Fact]
    public void ZoomAt_with_a_viewport_wider_than_the_column_keeps_the_anchor_on_screen()
    {
        var l = Uniform(6, 400, 600); // column 400 pt = 533 dip at 100%
        const double vw = 1000;
        const double vh = 500;
        ViewPosition start = l.ToPosition(0, 1500, vw, 1); // margin applies, x clamped at 0
        const double ax = 600;
        const double ay = 250;
        (double ox0, double oy0) = l.ToOffsets(start, vw, vh);
        ViewPosition next = l.ZoomAt(start, 2.5, ax, ay, vw); // 1333 dip column: scrollable, margin gone
        (double ox1, double oy1) = l.ToOffsets(next, vw, vh);
        var before = DocPointFromOffsets(l, ox0, oy0, ax, ay, 1, vw);
        var after = DocPointFromOffsets(l, ox1, oy1, ax, ay, 2.5, vw);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
    }

    [Fact]
    public void ZoomAt_works_at_the_document_start_and_end_and_with_a_narrow_viewport()
    {
        var l = Uniform(4);
        const double vw = 120; // narrower than any page at 100%
        const double vh = 300;

        ViewPosition atStart = l.ToPosition(0, 0, vw, 1);
        ViewPosition zoomedStart = l.ZoomAt(atStart, 2, 0, 0, vw);
        Assert.Equal(0, zoomedStart.PageIndex);
        Assert.Equal(0, zoomedStart.YPt, 9);
        (double sx, double sy) = l.ToOffsets(zoomedStart, vw, vh);
        Assert.Equal(0, sx, 6);
        Assert.Equal(0, sy, 6);

        double endY = l.ExtentHeightDip(1) - vh;
        ViewPosition atEnd = l.ToPosition(0, endY, vw, 1);

        // Zooming in with the anchor at the bottom edge lands exactly on the new end, anchor held.
        ViewPosition zoomedIn = l.ZoomAt(atEnd, 2, 0, vh, vw);
        (double ix, double iy) = l.ToOffsets(zoomedIn, vw, vh);
        Assert.Equal(l.ExtentHeightDip(2) - vh, iy, 6);
        Assert.Equal(
            DocPointFromOffsets(l, 0, endY, 0, vh, 1, vw).Y,
            DocPointFromOffsets(l, ix, iy, 0, vh, 2, vw).Y,
            6);

        // Zooming out with the anchor at the top would scroll past the end: clamped to the new end.
        ViewPosition zoomedOut = l.ZoomAt(atEnd, 0.5, 0, 0, vw);
        (_, double oy) = l.ToOffsets(zoomedOut, vw, vh);
        Assert.Equal(l.ExtentHeightDip(0.5) - vh, oy, 6);

        // Mid-document (unclamped) the anchor holds.
        double midY = l.ExtentHeightDip(1) / 2;
        ViewPosition mid = l.ToPosition(0, midY, vw, 1);
        ViewPosition midNext = l.ZoomAt(mid, 2, 0, 100, vw);
        (double mx, double my) = l.ToOffsets(midNext, vw, vh);
        Assert.Equal(
            DocPointFromOffsets(l, 0, midY, 0, 100, 1, vw).Y,
            DocPointFromOffsets(l, mx, my, 0, 100, 2, vw).Y,
            6);
    }

    [Fact]
    public void ZoomAt_clamps_the_new_zoom()
    {
        var l = Uniform(3);
        ViewPosition p = l.ToPosition(0, 100, 800, 1);
        Assert.Equal(LayoutConstants.MaxZoom, l.ZoomAt(p, 100, 0, 0, 800).Zoom);
        Assert.Equal(LayoutConstants.MinZoom, l.ZoomAt(p, 0.001, 0, 0, 800).Zoom);
        Assert.Equal(LayoutConstants.MinZoom, l.ZoomAt(p, -3, 0, 0, 800).Zoom);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_zoom_is_rejected_by_ToPosition_and_ToOffsets(double zoom)
    {
        var l = Uniform(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => l.ToPosition(0, 0, 800, zoom));
        Assert.Throws<ArgumentOutOfRangeException>(() => l.ToOffsets(new ViewPosition(0, 0, 0, zoom), 800, 600));
    }

    [Fact]
    public void An_offset_inside_a_gap_round_trips_with_negative_YPt()
    {
        var l = Uniform(3);
        const double zoom = 1.5;
        double scale = zoom * Units.DipPerPt;
        ViewPosition p = l.ToPosition(0, (l.PageTopPt(1) - 4) * scale, 800, zoom);
        Assert.Equal(1, p.PageIndex);
        Assert.Equal(-4, p.YPt, 9);
        (_, double oy) = l.ToOffsets(p, 800, 600);
        Assert.Equal((l.PageTopPt(1) - 4) * scale, oy, 6);
    }

    [Theory]
    [InlineData(0.25, 600)]
    [InlineData(1, 500)]
    [InlineData(2.5, 900)]
    public void Horizontal_offsets_with_and_without_margin_round_trip(double zoom, double vw)
    {
        var l = Uniform(3, 600, 800);
        double maxX = Math.Max(0, l.ExtentWidthDip(zoom, vw) - vw);
        foreach (double f in new[] { 0.0, 0.3, 1.0 })
        {
            double ox = maxX * f;
            ViewPosition p = l.ToPosition(ox, 123, vw, zoom);
            (double rx, _) = l.ToOffsets(p, vw, 400);
            Assert.Equal(ox, rx, 6);
        }

        if (maxX == 0)
        {
            // Column narrower than the viewport: offset 0 puts the column centre under the viewport centre.
            Assert.Equal(l.ColumnWidthPt / 2, l.ToPosition(0, 0, vw, zoom).ColumnXPt, 6);
        }
    }

    [Fact]
    public void Position_is_stable_across_viewport_width_changes_in_fit_mode()
    {
        var l = Uniform(5);
        double zoomA = l.FitWidthZoom(800);
        double zoomB = l.FitWidthZoom(1200);
        var pos = new ViewPosition(2, 120, l.ColumnWidthPt / 2, zoomA);
        (double xa, double ya) = l.ToOffsets(pos, 800, 600);
        (double xb, double yb) = l.ToOffsets(pos with { Zoom = zoomB }, 1200, 600);
        Assert.NotEqual(ya, yb);
        Assert.Equal(0, xa, 6);
        Assert.Equal(0, xb, 6);
        ViewPosition back = l.ToPosition(xb, yb, 1200, zoomB);
        Assert.Equal(2, back.PageIndex);
        Assert.Equal(120, back.YPt, 6);
        Assert.Equal(l.ColumnWidthPt / 2, back.ColumnXPt, 6);
    }

    [Fact]
    public void Ten_thousand_pages_of_mixed_sizes_perform_in_microseconds()
    {
        var l = new DocumentLayout(Enumerable.Range(0, 10_000).Select(i => new PageDimensions(400 + (i % 5 * 50), 600 + (i % 7 * 40))).ToArray());
        int sink = 0;
        long elapsedMs = 0;
        for (int pass = 0; pass < 2; pass++) // first pass warms up the JIT
        {
            var rnd = new Random(1);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 10_000; i++)
            {
                double y = rnd.NextDouble() * l.TotalHeightPt;
                sink += l.PageAt(y);
                sink += l.VisibleRange(y, 1000).Last;
            }

            sw.Stop();
            elapsedMs = sw.ElapsedMilliseconds;
        }

        Assert.True(elapsedMs < 50, $"took {elapsedMs} ms (sink {sink})");
    }

    [Theory]
    [InlineData(0, 800)]
    [InlineData(600, 0)]
    [InlineData(-1, 800)]
    [InlineData(double.NaN, 800)]
    [InlineData(600, double.PositiveInfinity)]
    public void Pages_with_zero_or_invalid_size_are_rejected(double w, double h)
    {
        Assert.Throws<ArgumentException>(() => new DocumentLayout([new PageDimensions(600, 800), new PageDimensions(w, h)]));
    }

    [Fact]
    public void Empty_page_list_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DocumentLayout([]));
    }
}
