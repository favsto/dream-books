namespace DreamBooks.Pdf.Interop;

/// <summary>
/// Managed helpers over the fpdf_annot.h string, appearance and ink-list calls (wide strings are
/// NUL-terminated UTF-16LE; lengths are in bytes and include the terminator). PDFium thread only.
/// </summary>
internal static class PdfiumAnnotations
{
    /// <summary>Returns the string value of <paramref name="key"/>, or null when the key is absent.</summary>
    public static unsafe string? GetString(nint annotation, string key)
    {
        if (PdfiumNative.FPDFAnnot_HasKey(annotation, key) == 0)
        {
            return null;
        }

        uint length = PdfiumNative.FPDFAnnot_GetStringValue(annotation, key, null, 0);
        return ReadWide(length, (buffer, size) => PdfiumNative.FPDFAnnot_GetStringValue(annotation, key, buffer, size));
    }

    public static unsafe bool SetString(nint annotation, string key, string value)
    {
        fixed (char* p = value + "\0")
        {
            return PdfiumNative.FPDFAnnot_SetStringValue(annotation, key, p) != 0;
        }
    }

    /// <summary>The normal appearance stream content, or an empty string when there is none.</summary>
    public static unsafe string GetAppearance(nint annotation)
    {
        const int mode = PdfiumNative.FPDF_ANNOT_APPEARANCEMODE_NORMAL;
        uint length = PdfiumNative.FPDFAnnot_GetAP(annotation, mode, null, 0);
        return ReadWide(length, (buffer, size) => PdfiumNative.FPDFAnnot_GetAP(annotation, mode, buffer, size));
    }

    public static unsafe bool SetAppearance(nint annotation, string content)
    {
        fixed (char* p = content + "\0")
        {
            return PdfiumNative.FPDFAnnot_SetAP(annotation, PdfiumNative.FPDF_ANNOT_APPEARANCEMODE_NORMAL, p) != 0;
        }
    }

    /// <summary>The points of one /InkList path in user space (empty when it does not exist).</summary>
    public static unsafe PdfiumNative.FS_POINTF[] GetInkPath(nint annotation, uint pathIndex)
    {
        uint count = PdfiumNative.FPDFAnnot_GetInkListPath(annotation, pathIndex, null, 0);
        var points = new PdfiumNative.FS_POINTF[count];
        if (count > 0)
        {
            fixed (PdfiumNative.FS_POINTF* p = points)
            {
                PdfiumNative.FPDFAnnot_GetInkListPath(annotation, pathIndex, p, count);
            }
        }

        return points;
    }

    private unsafe delegate uint WideReader(char* buffer, uint byteLength);

    private static unsafe string ReadWide(uint byteLength, WideReader read)
    {
        if (byteLength <= 2)
        {
            return string.Empty;
        }

        var buffer = new char[byteLength / 2];
        fixed (char* p = buffer)
        {
            read(p, byteLength);
        }

        return new string(buffer, 0, buffer.Length - 1);
    }
}
