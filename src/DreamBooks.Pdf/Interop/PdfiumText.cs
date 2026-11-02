using System.Runtime.InteropServices;

namespace DreamBooks.Pdf.Interop;

/// <summary>
/// P/Invoke declarations for the subset of fpdf_text.h used by text extraction and search. Every call must run
/// on <see cref="PdfiumThread"/>. A text page (FPDF_TEXTPAGE) is loaded from a loaded page and must be closed with
/// <see cref="FPDFText_ClosePage"/> before the page is closed. Character indexes are PDFium's own: one per text
/// character, including the characters PDFium generates (spaces and line breaks), not UTF-16 positions.
/// Rectangles and boxes are in PDF user space (y up).
/// </summary>
internal static partial class PdfiumText
{
    [LibraryImport(PdfiumNative.Library)]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(PdfiumNative.Library)]
    public static partial void FPDFText_ClosePage(nint textPage);

    /// <summary>The number of characters, or -1 on error.</summary>
    [LibraryImport(PdfiumNative.Library)]
    public static partial int FPDFText_CountChars(nint textPage);

    /// <summary>Writes <paramref name="count"/> UTF-16 units plus a terminator; returns units written including it.</summary>
    [LibraryImport(PdfiumNative.Library)]
    public static unsafe partial int FPDFText_GetText(nint textPage, int startIndex, int count, char* result);

    /// <summary>The Unicode code point of one character (0 when PDFium knows none).</summary>
    [LibraryImport(PdfiumNative.Library)]
    public static partial uint FPDFText_GetUnicode(nint textPage, int index);

    /// <summary>1 when PDFium generated the character (a space or line break it inserted), 0 if not, -1 on error.</summary>
    [LibraryImport(PdfiumNative.Library)]
    public static partial int FPDFText_IsGenerated(nint textPage, int index);

    /// <summary>
    /// Computes the rectangles covering <paramref name="count"/> characters from <paramref name="startIndex"/>
    /// (generated and empty characters skipped, one per run of characters PDFium keeps together) and keeps them in
    /// the text page for <see cref="FPDFText_GetRect"/>. Returns their number, or -1 on error.
    /// </summary>
    [LibraryImport(PdfiumNative.Library)]
    public static partial int FPDFText_CountRects(nint textPage, int startIndex, int count);

    /// <summary>One rectangle computed by the last <see cref="FPDFText_CountRects"/>; top is the larger y.</summary>
    [LibraryImport(PdfiumNative.Library)]
    public static partial int FPDFText_GetRect(
        nint textPage, int rectIndex, out double left, out double top, out double right, out double bottom);
}
