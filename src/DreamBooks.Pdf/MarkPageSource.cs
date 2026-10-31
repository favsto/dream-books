using DreamBooks.Core.Documents;
using DreamBooks.Core.Marks;

namespace DreamBooks.Pdf;

/// <summary>
/// The background mark scan's view of an open PDF (FR-100, NFR-02): a cheap pre-check per page, then the
/// renderer's own stroke loader (and its first-touch cache) only for pages that hold the app's annotations.
/// </summary>
/// <remarks>
/// A page costs at most two PDFium jobs: the pre-check (open the page, read each annotation's subtype and the
/// private key's presence, close the page) and, only when the key is there, <see cref="PdfRenderer.LoadStrokesAsync"/>.
/// No payload is read for a page without the app's annotations. A page PDFium cannot load returns null.
/// Cancellation surfaces as <see cref="OperationCanceledException"/>; a job not started yet never runs.
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
            if (!await _renderer.MayHoldAppStrokesAsync(pageIndex, ct).ConfigureAwait(false))
            {
                return null;
            }

            return await _renderer.LoadStrokesAsync(pageIndex, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            return null; // PDFium could not load the page: a damaged page, counted as holding no marks
        }
    }
}
