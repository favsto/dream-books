using System.Runtime.InteropServices;

namespace DreamBooks.Pdf.Interop;

/// <summary>
/// A PDFium document handle owned by the PDFium thread. Every member, including
/// <see cref="Dispose"/>, must run inside <see cref="PdfiumThread.RunAsync{T}"/>.
/// </summary>
internal sealed class PdfiumDocument : IDisposable
{
    // The stream the native write callback writes to, and the first error it met.
    // Thread-static: only the PDFium thread saves, one save at a time.
    [ThreadStatic]
    private static Stream? _writeTarget;

    [ThreadStatic]
    private static Exception? _writeError;

    private nint _handle;

    private PdfiumDocument(nint handle, int pageCount)
    {
        _handle = handle;
        PageCount = pageCount;
    }

    public nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    public int PageCount { get; }

    /// <summary>Opens a document; failures map to <see cref="PdfOpenException"/> as in <see cref="PdfRenderer"/>.</summary>
    public static PdfiumDocument Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        EnsurePdfiumThread();

        nint handle = PdfiumNative.FPDF_LoadDocument(path, null);
        if (handle == 0)
        {
            throw new PdfOpenException(PdfRenderer.MapError(PdfiumNative.FPDF_GetLastError()));
        }

        int count = PdfiumNative.FPDF_GetPageCount(handle);
        if (count < 0)
        {
            PdfiumNative.FPDF_CloseDocument(handle);
            throw new PdfOpenException(PdfOpenError.InvalidFormat);
        }

        return new PdfiumDocument(handle, count);
    }

    /// <summary>Loads a page; the caller closes it with <see cref="ClosePage"/>.</summary>
    public nint LoadPage(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        nint page = PdfiumNative.FPDF_LoadPage(Handle, pageIndex);
        if (page == 0)
        {
            throw new InvalidOperationException("PDFium could not load the page.");
        }

        return page;
    }

    public void ClosePage(nint page)
    {
        if (page != 0)
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// Writes the whole document (FPDF_NO_INCREMENTAL) to a new file at
    /// <paramref name="targetPath"/> and flushes it to disk. The file must not exist yet; on
    /// any failure the partial file is deleted and the exception propagates.
    /// </summary>
    public void SaveFullRewrite(string targetPath) => SaveFullRewrite(targetPath, wrapTarget: null);

    /// <summary>
    /// Test seam: <paramref name="wrapTarget"/> wraps the file stream PDFium writes to
    /// (e.g. to inject a write failure). The wrapper owns the file stream.
    /// </summary>
    internal unsafe void SaveFullRewrite(string targetPath, Func<Stream, Stream>? wrapTarget)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        nint handle = Handle;
        EnsurePdfiumThread();

        var fileStream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Stream stream = fileStream;
        bool completed = false;
        try
        {
            stream = wrapTarget?.Invoke(fileStream) ?? fileStream;
            _writeTarget = stream;
            _writeError = null;
            var fileWrite = new PdfiumNative.FPDF_FILEWRITE { Version = 1, WriteBlock = &WriteBlock };
            int ok = PdfiumNative.FPDF_SaveAsCopy(handle, &fileWrite, PdfiumNative.FPDF_NO_INCREMENTAL);

            if (_writeError is not null)
            {
                throw new IOException("Writing the PDF failed.", _writeError);
            }

            if (ok == 0)
            {
                throw new IOException("PDFium could not save the document.");
            }

            stream.Flush();
            fileStream.Flush(flushToDisk: true);
            stream.Dispose();
            fileStream.Dispose();
            completed = true;
        }
        finally
        {
            _writeTarget = null;
            _writeError = null;
            if (!completed)
            {
                // Disposing may flush and throw again (e.g. disk full): swallow that so the
                // partial file is always deleted and the original error is what surfaces.
                DisposeQuietly(stream);
                DisposeQuietly(fileStream);
                TryDelete(targetPath);
            }
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            PdfiumNative.FPDF_CloseDocument(_handle);
            _handle = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int WriteBlock(PdfiumNative.FPDF_FILEWRITE* self, void* data, uint size)
    {
        // Exceptions must not cross the native boundary: record the first one and report failure.
        if (_writeError is not null || _writeTarget is null)
        {
            return 0;
        }

        try
        {
            byte* bytes = (byte*)data;
            uint remaining = size;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, int.MaxValue);
                _writeTarget.Write(new ReadOnlySpan<byte>(bytes, chunk));
                bytes += chunk;
                remaining -= (uint)chunk;
            }

            return 1;
        }
        catch (Exception ex)
        {
            _writeError = ex;
            return 0;
        }
    }

    private static void EnsurePdfiumThread()
    {
        if (!PdfiumThread.IsPdfiumThread)
        {
            throw new InvalidOperationException("PDFium documents must be used on the PDFium thread.");
        }
    }

    private static void DisposeQuietly(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
