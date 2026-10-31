using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// Security and signature facts about an opened PDF (FR-82, NFR-11). A full rewrite
/// would invalidate signatures and cannot preserve encryption, so only documents
/// that are neither encrypted nor restricted may be rewritten.
/// </summary>
public sealed record PdfDocumentInfo(bool IsEncrypted, bool HasRestrictions, int SignatureCount)
{
    // FPDF_GetDocPermissions returns 0xFFFFFFFF for unprotected documents and for documents
    // unlocked by the owner password. Anything else means at least one permission bit is
    // cleared (modify, annotate, copy, print...), so we conservatively treat it as restricted.
    private const uint _allPermissions = 0xFFFFFFFF;

    /// <summary>Whether the document may be saved with a full rewrite.</summary>
    public bool CanBeRewritten => !IsEncrypted && !HasRestrictions;

    /// <summary>Reads the info from an open document. PDFium thread only.</summary>
    internal static PdfDocumentInfo Read(nint documentHandle)
    {
        if (!PdfiumThread.IsPdfiumThread)
        {
            throw new InvalidOperationException("PDFium documents must be used on the PDFium thread.");
        }

        // The security handler revision is -1 when the document is not encrypted.
        bool encrypted = PdfiumNative.FPDF_GetSecurityHandlerRevision(documentHandle) != -1;
        bool restricted = PdfiumNative.FPDF_GetDocPermissions(documentHandle) != _allPermissions;

        // -1 signals an error; treat it as no signatures.
        int signatures = Math.Max(0, PdfiumNative.FPDF_GetSignatureCount(documentHandle));
        return new PdfDocumentInfo(encrypted, restricted, signatures);
    }
}
