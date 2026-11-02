using DreamBooks.Core.Documents;
using DreamBooks.Core.Marks;

namespace DreamBooks.Pdf;

/// <summary>
/// The background mark scan's view of an open PDF (FR-100, NFR-02): one PDFium job per page, a cheap key check and,
/// only for pages that hold the app's annotations, an uncached read of their strokes.
/// </summary>
/// <remarks>
/// See <see cref="PdfRenderer.ReadStrokesForScanAsync"/>: no payload is read for a page without the app's
/// annotations; the strokes read are neither cached nor hidden from the page (a page already touched by the viewer
/// answers from the first-touch cache), so the scan keeps marks, not strokes, for pages nobody opened. A page PDFium
/// cannot load returns null. Cancellation surfaces as <see cref="OperationCanceledException"/>; a job not started yet
/// never runs.
/// </remarks>
public sealed class MarkPageSource(PdfRenderer renderer) : IMarkPageSource
{
    private readonly PdfRenderer _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    /// <summary>Read once at open by the renderer: a cheap cached value.</summary>
    public int PageCount => _renderer.PageCount;

    public PageDimensions GetPageDimensions(int pageIndex) => _renderer.GetPageDimensions(pageIndex);

    public async Task<PageStrokes?> TryLoadStrokesForScanAsync(int pageIndex, CancellationToken ct)
    {
        try
        {
            return await _renderer.ReadStrokesForScanAsync(pageIndex, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            return null; // PDFium could not load the page: a damaged page, counted as holding no marks
        }
    }
}
