using DreamBooks.Core.Documents;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// One page as the reader sees it (FR-88): the file's page with the strokes already in it plus the session's unsaved
/// changes (strokes added and strokes erased), on white paper, at a given resolution. The changes are applied to the
/// page in memory and the page is rendered; nothing is written, the source file is only read (NFR-10).
/// </summary>
public sealed class PageSnapshotRenderer
{
    /// <summary>The most memory a rendered bitmap may take: a larger page is scaled down to fit.</summary>
    public const long MaxBytes = 400L * 1024 * 1024;

    private const double _pointsPerInch = 72;

    /// <summary>
    /// Renders page <paramref name="pageIndex"/> of <paramref name="sourcePath"/> at <paramref name="dpi"/> (a page of
    /// 612 × 792 points is 1224 × 1584 pixels at 144 dpi). Only the change of that page is applied.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist or the resolution is not positive.</exception>
    /// <exception cref="PdfOpenException">The source cannot be opened.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public Task<RenderedPage> RenderAsync(
        string sourcePath, int pageIndex, IReadOnlyList<PageChange> changes, double dpi, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        if (!double.IsFinite(dpi) || dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), "The resolution must be a positive number.");
        }

        var change = changes.FirstOrDefault(c => c.PageIndex == pageIndex);
        return PdfiumThread.RunAsync(() => Render(sourcePath, pageIndex, change, dpi, ct), ct);
    }

    private static RenderedPage Render(string sourcePath, int pageIndex, PageChange? change, double dpi, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var document = PdfiumDocument.Open(sourcePath);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.PageCount, nameof(pageIndex));
        PdfiumNative.FPDF_GetPageSizeByIndexF(document.Handle, pageIndex, out var size);
        var (width, height) = PixelSize(size.Width, size.Height, dpi);

        var page = document.LoadPage(pageIndex);
        try
        {
            if (change is not null)
            {
                if (change.RemovedIds.Count > 0)
                {
                    StrokeAnnotations.RemoveByIds(page, change.RemovedIds.ToHashSet());
                }

                var geometry = PageGeometry.From(page);
                foreach (var stroke in change.Added)
                {
                    StrokeAnnotations.Write(page, geometry, stroke);
                }
            }

            ct.ThrowIfCancellationRequested();
            return PdfRenderer.RenderLoadedPage(page, width, height); // not saved: the changes live and die in this page
        }
        finally
        {
            document.ClosePage(page);
        }
    }

    private static (int Width, int Height) PixelSize(double widthPt, double heightPt, double dpi)
    {
        var scale = dpi / _pointsPerInch;
        var width = Math.Max(1, widthPt * scale);
        var height = Math.Max(1, heightPt * scale);
        var bytes = width * height * 4;
        if (bytes > MaxBytes)
        {
            var shrink = Math.Sqrt(MaxBytes / bytes);
            width *= shrink;
            height *= shrink;
        }

        return ((int)Math.Max(1, Math.Floor(width)), (int)Math.Max(1, Math.Floor(height)));
    }
}
