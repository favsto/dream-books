namespace DreamBooks.Pdf;

/// <summary>
/// How highlighter strokes are written as ink annotations (FR-40), chosen by the phase 3c opacity spike
/// (docs/superpowers/specs/2026-11-02-phase3c-opacity-spike-notes.md, regression in HighlighterOpacitySpikeTests).
/// </summary>
internal static class HighlighterWriteRoute
{
    /// <summary>
    /// Our own appearance stream (keeps the chisel outline, FR-46). The colour alpha goes through
    /// <c>FPDFAnnot_SetColor</c> before <c>FPDFAnnot_SetAP</c>, so /CA is set and SetAP adds an ExtGState named
    /// <c>/GS</c> to the appearance's resources; the content must start with <c>/GS gs</c> or PDFium renders it
    /// opaque.
    /// </summary>
    public const Route Current = Route.CustomAppearance;

    public enum Route
    {
        /// <summary>Route A: our filled-contour appearance stream that selects the /GS state PDFium adds.</summary>
        CustomAppearance,

        /// <summary>Route B: no appearance stream; readers build it from /InkList, the border width and /CA.</summary>
        PdfiumGenerated,
    }
}
