namespace DreamBooks.Pdf;

/// <summary>
/// Thrown when a PDF cannot be opened. The message never contains document content
/// or the file path (NFR-12).
/// </summary>
public sealed class PdfOpenException(PdfOpenError error)
    : Exception($"The PDF could not be opened: {error}.")
{
    public PdfOpenError Error { get; } = error;
}
