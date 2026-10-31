namespace DreamBooks.Pdf;

/// <summary>
/// The input is not a shape <see cref="PdfObjectStreamPacker"/> handles (or packing would not
/// gain enough); the caller keeps the unpacked file (NFR-20, FR-81). The message names only the rule,
/// never document content or paths (NFR-12).
/// </summary>
internal sealed class PdfPackException(string message) : Exception(message);
