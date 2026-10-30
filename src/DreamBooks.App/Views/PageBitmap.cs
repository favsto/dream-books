using System.Windows.Media;
using System.Windows.Media.Imaging;
using DreamBooks.Pdf;

namespace DreamBooks.App.Views;

/// <summary>Wraps a PDFium render as a frozen WPF bitmap, usable from any thread.</summary>
internal static class PageBitmap
{
    private const double _dpi = 96;

    public static BitmapSource FromRendered(RenderedPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var bitmap = BitmapSource.Create(
            page.PixelWidth, page.PixelHeight, _dpi, _dpi, PixelFormats.Bgra32, null, page.Bgra, page.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Runs <paramref name="render"/> and converts the result off the UI thread. Any failure (cancelled,
    /// superseded, document closed, PDFium error, bad size) yields null so the caller keeps the bitmap it
    /// shows: a page that cannot be rendered must never take the app down (NFR-11). Nothing is logged.
    /// </summary>
    public static async Task<BitmapSource?> TryRenderAsync(
        Func<CancellationToken, Task<RenderedPage>> render, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(render);
        try
        {
            var rendered = await render(ct).ConfigureAwait(false);
            return await Task.Run(() => FromRendered(rendered), ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Deliberately broad: see the summary.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
