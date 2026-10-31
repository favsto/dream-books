using System.Diagnostics;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Layout;

public class DocumentLayoutTests
{
    private static DocumentLayout Uniform(int n, double w = 600, double h = 800)
        => new(Enumerable.Repeat(new PageDimensions(w, h), n).ToArray());

    private static (double X, double Y) DocPoint(DocumentLayout l, ViewPosition p, double anchorX, double anchorY, double vw)
    {
        double s = p.Zoom * Units.DipPerPt;
        return (p.ColumnXPt + ((anchorX - (vw / 2)) / s), l.PageTopPt(p.PageIndex) + p.YPt + (anchorY / s));
    }

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

    [Fact]
    public void ZoomAt_keeps_the_anchor_point_within_half_a_pixel()
    {
        var l = Uniform(30, 500, 700);
        var rnd = new Random(7);
        const double vw = 800;
        const double vh = 600;
        for (int i = 0; i < 50; i++)
        {
            double z0 = 0.25 + (rnd.NextDouble() * 4);
            double z1 = 0.25 + (rnd.NextDouble() * 7.5);
            ViewPosition start = l.ToPosition(rnd.NextDouble() * 200, 5000 + (rnd.NextDouble() * 5000), vw, z0);
            double ax = rnd.NextDouble() * vw;
            double ay = rnd.NextDouble() * vh;
            ViewPosition next = l.ZoomAt(start, z1, ax, ay, vw);
            var before = DocPoint(l, start, ax, ay, vw);
            var after = DocPoint(l, next, ax, ay, vw);
            double halfPixelPt = 0.5 / (z1 * Units.DipPerPt);
            Assert.InRange(Math.Abs(before.X - after.X), 0, halfPixelPt);
            Assert.InRange(Math.Abs(before.Y - after.Y), 0, halfPixelPt);
            Assert.Equal(z1, next.Zoom);
        }
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
        ViewPosition zoomedEnd = l.ZoomAt(atEnd, 0.5, vw / 2, vh, vw);
        var before = DocPoint(l, atEnd, vw / 2, vh, vw);
        var after = DocPoint(l, zoomedEnd, vw / 2, vh, vw);
        Assert.Equal(before.Y, after.Y, 6);
        (_, double ey) = l.ToOffsets(zoomedEnd, vw, vh);
        Assert.InRange(ey, 0, l.ExtentHeightDip(0.5));
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
        var rnd = new Random(1);
        int sink = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            double y = rnd.NextDouble() * l.TotalHeightPt;
            sink += l.PageAt(y);
            sink += l.VisibleRange(y, 1000).Last;
        }

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 50, $"took {sw.ElapsedMilliseconds} ms (sink {sink})");
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
