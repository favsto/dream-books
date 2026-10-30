using DreamBooks.Pdf;

namespace DreamBooks.Pdf.Tests;

public class RenderSizePolicyTests
{
    private static readonly PageSize _a4 = new(595, 842);

    [Fact]
    public void A4_at_100_percent_and_150_dpi_scale()
    {
        var (w, h) = RenderSizePolicy.Compute(_a4, 1.0, 1.5);

        Assert.InRange(w, 1189, 1191);
        Assert.InRange(h, 1683, 1685);
    }

    [Fact]
    public void A4_at_800_percent_is_capped()
    {
        var (w, h) = RenderSizePolicy.Compute(_a4, 8.0, 1.5);

        Assert.True((long)w * h <= RenderSizePolicy.MaxPixels);
        Assert.Equal(595.0 / 842.0, (double)w / h, 0.005 * 595.0 / 842.0);
    }

    [Fact]
    public void Tiny_zoom_gives_at_least_one_pixel()
    {
        var (w, h) = RenderSizePolicy.Compute(_a4, 0.0001, 1.0);

        Assert.Equal(1, w);
        Assert.Equal(1, h);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.0, double.NaN)]
    public void Invalid_zoom_or_dpi_scale_throws(double zoom, double dpiScale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RenderSizePolicy.Compute(_a4, zoom, dpiScale));
    }

    [Theory]
    [InlineData(0.0, 842.0)]
    [InlineData(595.0, -1.0)]
    [InlineData(double.NaN, 842.0)]
    public void Invalid_page_size_throws(double widthPt, double heightPt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RenderSizePolicy.Compute(new PageSize(widthPt, heightPt), 1.0, 1.0));
    }
}
