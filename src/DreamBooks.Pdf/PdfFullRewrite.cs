using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// Rewrites a whole PDF (no incremental update) from a fresh PDFium instance that is used
/// only for this write, never rendered (FR-82, NFR-10). The source file is only read.
/// </summary>
public static class PdfFullRewrite
{
    /// <summary>
    /// Writes a full copy of <paramref name="sourcePath"/> to the new file
    /// <paramref name="targetPath"/> (which must not exist). Open failures surface as
    /// <see cref="PdfOpenException"/>; write failures as <see cref="IOException"/>, leaving no file.
    /// </summary>
    public static Task RewriteAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        return PdfiumThread.RunAsync(
            () =>
            {
                using PdfiumDocument document = PdfiumDocument.Open(sourcePath);
                document.SaveFullRewrite(targetPath);
            },
            ct);
    }
}
