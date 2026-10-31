using System.Windows.Media;
using DreamBooks.Pdf;

namespace DreamBooks.App.Views;

/// <summary>
/// INTERIM bitmap path until the render scheduler (Task 8): one full-resolution render per request straight through
/// the current <see cref="PdfRenderer"/> (on its PDFium thread, off the UI thread, NFR-04), sized by
/// <see cref="RenderSizePolicy"/>. No cache, no low-resolution first pass, no byte budget.
/// </summary>
internal sealed class RendererPageBitmapSource(Func<PdfRenderer?> renderer) : IPageBitmapSource
{
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
                var (width, height) = RenderSizePolicy.Compute(pageSize, zoom, dpiScale);
                return current.RenderPageAsync(pageIndex, width, height, token);
            },
            ct);
    }
}
