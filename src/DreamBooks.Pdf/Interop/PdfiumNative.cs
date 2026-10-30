using System.Runtime.InteropServices;

namespace DreamBooks.Pdf.Interop;

/// <summary>
/// P/Invoke declarations for the subset of fpdfview.h used by the renderer.
/// Every call must run on <see cref="PdfiumThread"/>: PDFium is not thread-safe.
/// </summary>
/// <remarks>
/// Calling convention: no explicit <c>UnmanagedCallConv</c>, so the platform default is used.
/// FPDF_CALLCONV is __stdcall on 32-bit Windows (the platform default there), and x64/ARM64
/// have a single native convention, so the default matches every runtime in the package.
/// Handle types (FPDF_DOCUMENT, FPDF_PAGE, FPDF_BITMAP) are opaque pointers mapped to nint;
/// FPDF_BOOL is int, and FPDF_DWORD / unsigned long are 32-bit on Windows (uint).
/// </remarks>
internal static partial class PdfiumNative
{
    public const string Library = "pdfium";

    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;

    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ANNOT = 0x01;
    public const uint White = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_SIZEF
    {
        public float Width;
        public float Height;
    }

    [LibraryImport(Library)]
    public static partial void FPDF_InitLibrary();

    /// <summary>Opens a document. The path is passed as UTF-8, per fpdfview.h.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadDocument(string filePath, string? password);

    [LibraryImport(Library)]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(Library)]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Library)]
    public static partial int FPDF_GetPageSizeByIndexF(nint document, int pageIndex, out FS_SIZEF size);

    [LibraryImport(Library)]
    public static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Library)]
    public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Library)]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library)]
    public static partial void FPDF_RenderPageBitmap(
        nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Library)]
    public static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport(Library)]
    public static partial void FPDFBitmap_Destroy(nint bitmap);
}
