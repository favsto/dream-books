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

    // ---- Page geometry (fpdfview.h, fpdf_edit.h) ----

    /// <summary>FS_RECTF: <c>{ left, top, right, bottom }</c> in user space, top is the larger y.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    /// <summary>The visible box: CropBox intersected with MediaBox, as PDFium renders it.</summary>
    [LibraryImport(Library)]
    public static partial int FPDF_GetPageBoundingBox(nint page, out FS_RECTF rect);

    /// <summary>0..3 quarter turns clockwise, or -1 on error.</summary>
    [LibraryImport(Library)]
    public static partial int FPDFPage_GetRotation(nint page);

    [LibraryImport(Library)]
    public static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport(Library)]
    public static partial float FPDF_GetPageHeightF(nint page);

    [LibraryImport(Library)]
    public static partial int FPDF_DeviceToPage(
        nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
        int deviceX, int deviceY, out double pageX, out double pageY);

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

    // ---- Saving (fpdf_save.h) ----

    public const uint FPDF_NO_INCREMENTAL = 2;

    /// <summary>
    /// FPDF_FILEWRITE: <c>{ int version = 1; int (*WriteBlock)(self, const void* data, unsigned long size); }</c>.
    /// WriteBlock returns non-zero on success.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FPDF_FILEWRITE
    {
        public int Version;
        public delegate* unmanaged<FPDF_FILEWRITE*, void*, uint, int> WriteBlock;
    }

    [LibraryImport(Library)]
    public static unsafe partial int FPDF_SaveAsCopy(nint document, FPDF_FILEWRITE* fileWrite, uint flags);

    // ---- Text (fpdf_text.h) ----

    [LibraryImport(Library)]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Library)]
    public static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Library)]
    public static partial int FPDFText_CountChars(nint textPage);

    /// <summary>Writes <paramref name="count"/> UTF-16 units plus a terminator; returns units written including it.</summary>
    [LibraryImport(Library)]
    public static unsafe partial int FPDFText_GetText(nint textPage, int startIndex, int count, char* result);

    // ---- Annotations (fpdf_annot.h) ----

    public const int FPDF_ANNOT_LINK = 2;
    public const int FPDF_ANNOT_WIDGET = 20;

    [LibraryImport(Library)]
    public static partial int FPDFPage_GetAnnotCount(nint page);

    [LibraryImport(Library)]
    public static partial nint FPDFPage_GetAnnot(nint page, int index);

    [LibraryImport(Library)]
    public static partial void FPDFPage_CloseAnnot(nint annotation);

    [LibraryImport(Library)]
    public static partial int FPDFAnnot_GetSubtype(nint annotation);

    public const int FPDF_ANNOT_SQUARE = 5;
    public const int FPDF_ANNOT_INK = 15;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;
    public const int FPDF_ANNOT_APPEARANCEMODE_NORMAL = 0;
    public const int FPDFANNOT_COLORTYPE_Color = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_POINTF
    {
        public float X;
        public float Y;
    }

    /// <summary>Creates an annotation; the handle must be closed with <see cref="FPDFPage_CloseAnnot"/>.</summary>
    [LibraryImport(Library)]
    public static partial nint FPDFPage_CreateAnnot(nint page, int subtype);

    /// <summary>Removes the annotation at <paramref name="index"/> from the page's /Annots.</summary>
    [LibraryImport(Library)]
    public static partial int FPDFPage_RemoveAnnot(nint page, int index);

    [LibraryImport(Library)]
    public static unsafe partial int FPDFAnnot_SetRect(nint annotation, FS_RECTF* rect);

    [LibraryImport(Library)]
    public static partial int FPDFAnnot_GetRect(nint annotation, out FS_RECTF rect);

    /// <summary>Sets /C (or /IC); fails once the annotation has an appearance stream.</summary>
    [LibraryImport(Library)]
    public static partial int FPDFAnnot_SetColor(nint annotation, int type, uint r, uint g, uint b, uint a);

    /// <summary>Sets /Border; removes an existing appearance stream on success.</summary>
    [LibraryImport(Library)]
    public static partial int FPDFAnnot_SetBorder(nint annotation, float horizontalRadius, float verticalRadius, float width);

    /// <summary>Appends one path to /InkList; returns its index or -1. <paramref name="count"/> is size_t.</summary>
    [LibraryImport(Library)]
    public static unsafe partial int FPDFAnnot_AddInkStroke(nint annotation, FS_POINTF* points, nuint count);

    [LibraryImport(Library)]
    public static partial uint FPDFAnnot_GetInkListCount(nint annotation);

    /// <summary>Returns the number of points in the path; fills the buffer when it is large enough.</summary>
    [LibraryImport(Library)]
    public static unsafe partial uint FPDFAnnot_GetInkListPath(nint annotation, uint pathIndex, FS_POINTF* buffer, uint length);

    [LibraryImport(Library)]
    public static partial int FPDFAnnot_SetFlags(nint annotation, int flags);

    [LibraryImport(Library)]
    public static partial int FPDFAnnot_GetFlags(nint annotation);

    /// <summary>Key is UTF-8; returns non-zero when the annotation dictionary has it.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FPDFAnnot_HasKey(nint annotation, string key);

    /// <summary>Key is UTF-8; value is NUL-terminated UTF-16LE.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int FPDFAnnot_SetStringValue(nint annotation, string key, char* value);

    /// <summary>Returns the byte length including the two-byte terminator; key is UTF-8.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial uint FPDFAnnot_GetStringValue(nint annotation, string key, char* buffer, uint bufferLength);

    /// <summary>Sets the appearance stream content (UTF-16LE); needs /Rect, BBox becomes the rect.</summary>
    [LibraryImport(Library)]
    public static unsafe partial int FPDFAnnot_SetAP(nint annotation, int appearanceMode, char* value);

    /// <summary>Returns the byte length including the two-byte terminator.</summary>
    [LibraryImport(Library)]
    public static unsafe partial uint FPDFAnnot_GetAP(nint annotation, int appearanceMode, char* buffer, uint bufferLength);

    // ---- Page objects (fpdf_edit.h) ----

    public const int FPDF_PAGEOBJ_FORM = 5;

    [LibraryImport(Library)]
    public static partial int FPDFPage_CountObjects(nint page);

    [LibraryImport(Library)]
    public static partial nint FPDFPage_GetObject(nint page, int index);

    [LibraryImport(Library)]
    public static partial int FPDFPageObj_GetType(nint pageObject);

    [LibraryImport(Library)]
    public static partial int FPDFFormObj_CountObjects(nint formObject);

    [LibraryImport(Library)]
    public static partial nint FPDFFormObj_GetObject(nint formObject, uint index);

    // ---- Outline and metadata (fpdf_doc.h) ----

    [LibraryImport(Library)]
    public static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);

    [LibraryImport(Library)]
    public static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);

    /// <summary>
    /// Reads an /Info entry as UTF-16LE. Returns the byte length including the two-byte terminator;
    /// the buffer is left untouched when it is null or too small.
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial uint FPDF_GetMetaText(nint document, string tag, char* buffer, uint bufferLength);

    // ---- Security and signatures (fpdfview.h, fpdf_signature.h) ----

    [LibraryImport(Library)]
    public static partial uint FPDF_GetDocPermissions(nint document);

    /// <summary>Returns -1 when the document is not encrypted.</summary>
    [LibraryImport(Library)]
    public static partial int FPDF_GetSecurityHandlerRevision(nint document);

    [LibraryImport(Library)]
    public static partial int FPDF_GetSignatureCount(nint document);
}
