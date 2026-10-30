using System.Runtime.InteropServices;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// Opens a PDF with PDFium and renders pages to BGRA bitmaps. All native work runs on the
/// dedicated <see cref="PdfiumThread"/> (NFR-04); open failures surface as
/// <see cref="PdfOpenException"/> (NFR-11). Never logs document content (NFR-12).
/// </summary>
public sealed class PdfRenderer : IAsyncDisposable
{
    private readonly PageSize[] _pageSizes;

    // Only read and written on the PDFium thread; 0 once closed.
    private nint _document;
    private int _disposed;

    private PdfRenderer(nint document, PageSize[] pageSizes)
    {
        _document = document;
        _pageSizes = pageSizes;
    }

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

                sizes[i] = new PageSize(size.Width, size.Height);
            }

            return new PdfRenderer(document, sizes);
        }
        catch
        {
            PdfiumNative.FPDF_CloseDocument(document);
            throw;
        }
    }

    private RenderedPage Render(int pageIndex, int pixelWidth, int pixelHeight)
    {
        // Re-checked on the worker: a dispose queued earlier has already closed the document.
        ObjectDisposedException.ThrowIf(_document == 0, this);

        nint page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
        if (page == 0)
        {
            throw new InvalidOperationException("PDFium could not load the page.");
        }

        try
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
                    bitmap, page, 0, 0, pixelWidth, pixelHeight, 0, PdfiumNative.FPDF_ANNOT);

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
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private static PdfOpenError MapError(uint error) => error switch
    {
        PdfiumNative.FPDF_ERR_FILE => PdfOpenError.FileNotFound,
        PdfiumNative.FPDF_ERR_FORMAT => PdfOpenError.InvalidFormat,
        PdfiumNative.FPDF_ERR_PASSWORD => PdfOpenError.PasswordRequired,
        PdfiumNative.FPDF_ERR_SECURITY => PdfOpenError.UnsupportedSecurity,
        _ => PdfOpenError.Unknown,
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
