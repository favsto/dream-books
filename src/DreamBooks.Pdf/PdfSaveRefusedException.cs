namespace DreamBooks.Pdf;

/// <summary>
/// The document is encrypted or restricted and is never rewritten (FR-82, NFR-11). Thrown before
/// any file is created. The message has no document content or paths (NFR-12).
/// </summary>
public sealed class PdfSaveRefusedException(PdfDocumentInfo info)
    : Exception("The PDF is encrypted or restricted and cannot be rewritten.")
{
    public PdfDocumentInfo Info { get; } = info;
}
