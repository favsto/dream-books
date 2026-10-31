using System.Windows.Media.Imaging;
using DreamBooks.Core.Rendering;
using DreamBooks.Pdf;

namespace DreamBooks.App.Views;

/// <summary>
/// Renders page bitmaps for the render scheduler through the document's current <see cref="PdfRenderer"/> (the
/// PDFium work runs on its own thread, the BGRA → frozen <see cref="BitmapSource"/> conversion on the thread pool:
/// nothing on the UI thread, NFR-04), and chooses the sizes: <see cref="SharpSize"/> for the current zoom and screen
/// scale, <see cref="PreviewSize"/> for the quick first render and the rail bubble.
/// </summary>
/// <remarks>
/// <see cref="Renderer"/> is swapped by the view (a save replaces the renderer; it is null while the file is
/// released). Any failure — no renderer, a closed one, cancelled, PDFium error — yields null, never an exception
/// (NFR-11): the scheduler keeps the page's placeholder or old bitmap and retries after an invalidation.
/// </remarks>
internal sealed class PageBitmapRenderer : IBitmapRenderer<BitmapSource>
{
    /// <summary>Cap on a sharp render's longest side, in pixels (about 52 MB for an A4 page as BGRA, NFR-06).</summary>
    public const int MaxLongSidePx = 4096;

    /// <summary>Width of a preview in DIPs (rendered at the screen's scale): the quick first render and the rail bubble.</summary>
    public const double PreviewWidthDip = 300;

    /// <summary>A preview's height is at most this many times its width (very tall pages stay small).</summary>
    private const double _maxPreviewAspect = 2;

    private PdfRenderer? _renderer;

    /// <summary>The renderer to draw with; set on the UI thread, read on render threads.</summary>
    public PdfRenderer? Renderer
    {
        get => Volatile.Read(ref _renderer);
        set => Volatile.Write(ref _renderer, value);
    }

    /// <summary>
    /// The pixel size of a sharp render: <see cref="RenderSizePolicy"/> (the page's exact pixel size on screen, the size
    /// the page surface is snapped to), scaled down when its longest side exceeds <see cref="MaxLongSidePx"/>; the
    /// surface then stretches it.
    /// </summary>
    public static (int Width, int Height) SharpSize(PageSize pageSize, double zoom, double dpiScale)
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

    /// <summary>
    /// The pixel size of a preview: <see cref="PreviewWidthDip"/> wide at the screen's scale, with the page's
    /// proportions; a page taller than <see cref="_maxPreviewAspect"/> times its width is fitted in that box instead.
    /// </summary>
    public static (int Width, int Height) PreviewSize(PageSize pageSize, double dpiScale)
    {
        if (!(pageSize.WidthPt > 0) || !(pageSize.HeightPt > 0) || !(dpiScale > 0)
            || !double.IsFinite(pageSize.WidthPt) || !double.IsFinite(pageSize.HeightPt) || !double.IsFinite(dpiScale))
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size and screen scale must be positive.");
        }

        var boxWidth = PreviewWidthDip * dpiScale;
        var scale = Math.Min(boxWidth / pageSize.WidthPt, boxWidth * _maxPreviewAspect / pageSize.HeightPt);
        return (
            Math.Max(1, (int)Math.Round(pageSize.WidthPt * scale)),
            Math.Max(1, (int)Math.Round(pageSize.HeightPt * scale)));
    }

    public Task<BitmapSource?> RenderAsync(int pageIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        if (Renderer is not { } current)
        {
            return Task.FromResult<BitmapSource?>(null); // no document, or a save has the file released
        }

        // TryRenderAsync swallows every failure, including a renderer disposed meanwhile (NFR-11).
        return PageBitmap.TryRenderAsync(token => current.RenderPageAsync(pageIndex, pixelWidth, pixelHeight, token), ct);
    }
}
