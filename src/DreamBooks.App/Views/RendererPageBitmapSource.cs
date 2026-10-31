using System.Windows.Media;
using DreamBooks.Pdf;

namespace DreamBooks.App.Views;

/// <summary>
/// INTERIM bitmap path until the render scheduler (Task 8): one render per request straight through the current
/// <see cref="PdfRenderer"/> (on its PDFium thread, off the UI thread, NFR-04), sized by <see cref="RenderSizePolicy"/>
/// and capped at <see cref="MaxLongSidePx"/> on the longest side (lower resolution, stretched to the page) so that the
/// few visible pages stay within memory (NFR-06). No cache, no low-resolution first pass, no byte budget.
/// </summary>
internal sealed class RendererPageBitmapSource(Func<PdfRenderer?> renderer) : IPageBitmapSource
{
    /// <summary>Interim cap on one render's longest side, in pixels (about 52 MB for an A4 page as BGRA).</summary>
    public const int MaxLongSidePx = 4096;

    /// <summary>The pixel size of a render: <see cref="RenderSizePolicy"/>, scaled down to the cap when larger.</summary>
    internal static (int Width, int Height) PixelSize(PageSize pageSize, double zoom, double dpiScale)
    {
        var (width, height) = RenderSizePolicy.Compute(pageSize, zoom, dpiScale);
        var longest = Math.Max(width, height);
        if (longest <= MaxLongSidePx)
        {
            return (width, height);
        }

        var factor = (double)MaxLongSidePx / longest;
        return (
            Math.Clamp((int)Math.Round(width * factor), 1, MaxLongSidePx),
            Math.Clamp((int)Math.Round(height * factor), 1, MaxLongSidePx));
    }

    public async Task<ImageSource?> RenderAsync(
        int pageIndex, PageSize pageSize, double zoom, double dpiScale, CancellationToken ct)
    {
        if (renderer() is not { } current)
        {
            return null; // no document, a test page source, or a Save has the file released
        }

        // TryRenderAsync swallows every failure (NFR-11): a page that cannot be rendered keeps its placeholder.
        return await PageBitmap.TryRenderAsync(
            token =>
            {
                var (width, height) = PixelSize(pageSize, zoom, dpiScale);
                return current.RenderPageAsync(pageIndex, width, height, token);
            },
            ct);
    }
}
