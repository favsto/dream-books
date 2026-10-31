using System.Runtime.InteropServices;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// Opens a PDF with PDFium and renders pages to BGRA bitmaps. All native work runs on the
/// dedicated <see cref="PdfiumThread"/> (NFR-04); open failures surface as
/// <see cref="PdfOpenException"/> (NFR-11). Never logs document content (NFR-12).
/// </summary>
public sealed class PdfRenderer : IAsyncDisposable, IPageSource
{
    private readonly PageSize[] _pageSizes;
    private readonly PageDimensions[] _dimensions;

    // Strokes read on the first touch of each page (ruling 6); PDFium thread only.
    private readonly Dictionary<int, PageStrokes> _strokes = [];

    // Only read and written on the PDFium thread; 0 once closed.
    private nint _document;
    private int _disposed;

    private PdfRenderer(
        string path, nint document, PageSize[] pageSizes, PageDimensions[] dimensions, PdfDocumentInfo info)
    {
        Path = path;
        _document = document;
        _pageSizes = pageSizes;
        _dimensions = dimensions;
        Info = info;
    }

    /// <summary>The full path this document was opened from.</summary>
    public string Path { get; }

    /// <summary>Encryption, restrictions and signatures, read once at open.</summary>
    public PdfDocumentInfo Info { get; }

    public int PageCount
    {
        get
        {
            ThrowIfDisposed();
            return _pageSizes.Length;
        }
    }

    public static Task<PdfRenderer> OpenAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return PdfiumThread.RunAsync(() => Open(path), ct);
    }

    /// <summary>Size of a page in points, read once at open.</summary>
    public PageSize GetPageSize(int pageIndex)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _pageSizes.Length);
        return _pageSizes[pageIndex];
    }

    /// <summary>
    /// Displayed size of a page (visible box with rotation applied) in points, computed from
    /// <see cref="PageGeometry"/> once at open, so this is a cheap synchronous lookup (FR-26).
    /// </summary>
    public PageDimensions GetPageDimensions(int pageIndex)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _dimensions.Length);
        return _dimensions[pageIndex];
    }

    /// <summary>
    /// The app's strokes on a page. Read on the first touch of the page (by this call or by a
    /// render) and cached, so every call returns the same result whatever was rendered.
    /// Each stroke Id appears once, the first occurrence winning.
    /// </summary>
    public Task<PageStrokes> LoadStrokesAsync(int pageIndex, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _pageSizes.Length);
        return PdfiumThread.RunAsync(() => LoadStrokes(pageIndex), ct);
    }

    /// <summary>
    /// Renders a page (with annotations) onto a white background, scaled to the given pixel size.
    /// </summary>
    public Task<RenderedPage> RenderPageAsync(
        int pageIndex, int pixelWidth, int pixelHeight, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _pageSizes.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        if ((long)pixelWidth * pixelHeight * 4 > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "The requested bitmap is too large.");
        }

        return PdfiumThread.RunAsync(() => Render(pageIndex, pixelWidth, pixelHeight), ct);
    }

    /// <summary>Closes the document on the PDFium thread. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await PdfiumThread.RunAsync(
            () =>
            {
                if (_document != 0)
                {
                    PdfiumNative.FPDF_CloseDocument(_document);
                    _document = 0;
                    _strokes.Clear();
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    internal static bool IsValidPageSize(float width, float height) =>
        float.IsFinite(width) && float.IsFinite(height) && width > 0 && height > 0;

    private static PdfRenderer Open(string path)
    {
        nint document = PdfiumNative.FPDF_LoadDocument(path, null);
        if (document == 0)
        {
            throw new PdfOpenException(MapError(PdfiumNative.FPDF_GetLastError()));
        }

        try
        {
            int count = PdfiumNative.FPDF_GetPageCount(document);
            if (count < 0)
            {
                throw new PdfOpenException(PdfOpenError.InvalidFormat);
            }

            var sizes = new PageSize[count];
            var dimensions = new PageDimensions[count];
            for (int i = 0; i < count; i++)
            {
                if (PdfiumNative.FPDF_GetPageSizeByIndexF(document, i, out PdfiumNative.FS_SIZEF size) == 0)
                {
                    throw new PdfOpenException(PdfOpenError.InvalidFormat);
                }

                if (!IsValidPageSize(size.Width, size.Height))
                {
                    throw new PdfOpenException(PdfOpenError.InvalidFormat);
                }

                // One source: both APIs derive from the same geometry number (FR-26); the PDFium
                // size above only decides validity.
                dimensions[i] = ReadDimensions(document, i);
                sizes[i] = new PageSize((float)dimensions[i].WidthPt, (float)dimensions[i].HeightPt);
            }

            return new PdfRenderer(path, document, sizes, dimensions, PdfDocumentInfo.Read(document));
        }
        catch
        {
            PdfiumNative.FPDF_CloseDocument(document);
            throw;
        }
    }

    private static PageDimensions ReadDimensions(nint document, int index)
    {
        nint page = PdfiumNative.FPDF_LoadPage(document, index);
        if (page == 0)
        {
            throw new PdfOpenException(PdfOpenError.InvalidFormat);
        }

        try
        {
            PageDimensions dimensions = PageGeometry.From(page).Dimensions;
            if (!IsValidPageSize((float)dimensions.WidthPt, (float)dimensions.HeightPt))
            {
                throw new PdfOpenException(PdfOpenError.InvalidFormat);
            }

            return dimensions;
        }
        catch (InvalidOperationException)
        {
            throw new PdfOpenException(PdfOpenError.InvalidFormat);
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private PageStrokes LoadStrokes(int pageIndex)
    {
        // Re-checked on the worker: a dispose queued earlier has already closed the document.
        ObjectDisposedException.ThrowIf(_document == 0, this);
        PdfiumNative.FPDF_ClosePage(LoadPageHidingStrokes(pageIndex));
        return _strokes[pageIndex];
    }

    /// <summary>
    /// Loads a page and, on its first touch, caches its strokes; then removes the app annotations
    /// from the in-memory page (never saved) so they cannot be drawn. Unreadable ones stay.
    /// The caller closes the page. PDFium thread only.
    /// </summary>
    private nint LoadPageHidingStrokes(int pageIndex)
    {
        nint page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
        if (page == 0)
        {
            throw new InvalidOperationException("PDFium could not load the page.");
        }

        try
        {
            if (!_strokes.ContainsKey(pageIndex))
            {
                StrokeReadResult read = StrokeAnnotations.Read(page, PageGeometry.From(page));
                var seen = new HashSet<Guid>();
                var unique = new List<InkStroke>(read.Strokes.Count);
                foreach (InkStroke stroke in read.Strokes)
                {
                    if (seen.Add(stroke.Id))
                    {
                        unique.Add(stroke);
                    }
                }

                _strokes[pageIndex] = new PageStrokes(unique, read.UnreadableAnnotationIndexes.Count);
            }

            // Idempotent; also covers a page reloaded from a document that still holds them.
            StrokeAnnotations.RemoveAllApp(page);
            return page;
        }
        catch
        {
            PdfiumNative.FPDF_ClosePage(page);
            throw;
        }
    }

    private RenderedPage Render(int pageIndex, int pixelWidth, int pixelHeight)
    {
        // Re-checked on the worker: a dispose queued earlier has already closed the document.
        ObjectDisposedException.ThrowIf(_document == 0, this);

        nint page = LoadPageHidingStrokes(pageIndex);
        try
        {
            return RenderLoadedPage(page, pixelWidth, pixelHeight);
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// Renders an already loaded page (with annotations) onto a white BGRA bitmap.
    /// PDFium thread only.
    /// </summary>
    internal static RenderedPage RenderLoadedPage(nint page, int pixelWidth, int pixelHeight) =>
        RenderLoadedPage(page, pixelWidth, pixelHeight, withAnnotations: true);

    /// <summary>
    /// Renders an already loaded page onto a white BGRA bitmap, with or without its annotations
    /// (FPDF_ANNOT). PDFium thread only.
    /// </summary>
    internal static RenderedPage RenderLoadedPage(nint page, int pixelWidth, int pixelHeight, bool withAnnotations)
    {
        nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(
            pixelWidth, pixelHeight, PdfiumNative.FPDFBitmap_BGRA, 0, 0);
        if (bitmap == 0)
        {
            throw new InvalidOperationException("PDFium could not allocate the page bitmap.");
        }

        try
        {
            PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, pixelWidth, pixelHeight, PdfiumNative.White);
            PdfiumNative.FPDF_RenderPageBitmap(
                bitmap, page, 0, 0, pixelWidth, pixelHeight, 0, withAnnotations ? PdfiumNative.FPDF_ANNOT : 0);

            int stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
            byte[] pixels = new byte[checked(stride * pixelHeight)];
            Marshal.Copy(PdfiumNative.FPDFBitmap_GetBuffer(bitmap), pixels, 0, pixels.Length);
            return new RenderedPage(pixelWidth, pixelHeight, stride, pixels);
        }
        finally
        {
            PdfiumNative.FPDFBitmap_Destroy(bitmap);
        }
    }

    internal static PdfOpenError MapError(uint error) => error switch
    {
        PdfiumNative.FPDF_ERR_FILE => PdfOpenError.FileNotFound,
        PdfiumNative.FPDF_ERR_FORMAT => PdfOpenError.InvalidFormat,
        PdfiumNative.FPDF_ERR_PASSWORD => PdfOpenError.PasswordRequired,
        PdfiumNative.FPDF_ERR_SECURITY => PdfOpenError.UnsupportedSecurity,
        _ => PdfOpenError.Unknown,
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
