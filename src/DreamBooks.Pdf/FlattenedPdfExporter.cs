using DreamBooks.Core.Documents;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// The flattened export (FR-87): a new PDF in which the strokes, highlighters and margin notes are merged into the page
/// content and are no longer editable. The source file and the open document are never touched (NFR-10): the
/// session's changes are written into a verified temporary copy of the source (what the reader sees on screen, saved or
/// not), every page of that copy is flattened, the result is verified (same page count, no stroke annotation left) and
/// only then does it replace the destination. Failures and cancellation leave no file behind and an existing destination
/// as it was.
/// </summary>
public sealed class FlattenedPdfExporter
{
    private const string _tempExtension = ".drbk-tmp";

    /// <summary>
    /// Exports <paramref name="sourcePath"/> with the session's <paramref name="changes"/> (the unsaved edits, as
    /// <c>DocumentSession.GetChanges()</c> gives them) to <paramref name="destinationPath"/>; returns the full destination path.
    /// </summary>
    /// <exception cref="PdfSaveRefusedException">The source is encrypted or restricted.</exception>
    /// <exception cref="SaveVerificationException">The flattened copy did not pass its checks.</exception>
    /// <exception cref="DirectoryNotFoundException">The destination folder does not exist.</exception>
    /// <exception cref="IOException">Writing failed (a full disk among others).</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    public async Task<string> ExportAsync(
        string sourcePath, string destinationPath, IReadOnlyList<PageChange> changes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentNullException.ThrowIfNull(changes);

        var destination = Path.GetFullPath(destinationPath);
        var folder = Path.GetDirectoryName(destination);
        var fileName = Path.GetFileName(destination);
        if (folder is null || fileName.Length == 0)
        {
            throw new ArgumentException("The destination must be a file path.", nameof(destinationPath));
        }

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException("The destination folder does not exist.");
        }

        ct.ThrowIfCancellationRequested();
        string? withStrokes = null;
        string? flattened = null;
        try
        {
            // 1. The source plus the session's changes, verified; the source is only read.
            withStrokes = await new PdfDocumentSaver(packObjects: false)
                .WriteVerifiedCopyAsync(sourcePath, destination, changes, ct).ConfigureAwait(false);

            // 2. Flatten every page of that copy and verify the result, on the PDFium thread.
            flattened = Path.Combine(folder, $"{fileName}.{Guid.NewGuid().ToString("N")[..8]}{_tempExtension}");
            var copy = withStrokes;
            var target = flattened;
            await PdfiumThread.RunAsync(() => FlattenAndVerify(copy, target, ct), ct).ConfigureAwait(false);

            // 3. Only now does it replace the destination.
            ct.ThrowIfCancellationRequested();
            FileReplacer.Replace(flattened, destination);
            flattened = null;
            return destination;
        }
        finally
        {
            TryDelete(withStrokes);
            TryDelete(flattened);
        }
    }

    private static void FlattenAndVerify(string input, string output, CancellationToken ct)
    {
        int pageCount;
        using (var document = PdfiumDocument.Open(input))
        {
            pageCount = document.PageCount;
            for (var i = 0; i < pageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = document.LoadPage(i);
                try
                {
                    if (PdfiumNative.FPDFPage_Flatten(page, PdfiumNative.FLAT_NORMALDISPLAY) == PdfiumNative.FLATTEN_FAIL)
                    {
                        throw new SaveVerificationException("a page could not be flattened");
                    }
                }
                finally
                {
                    document.ClosePage(page);
                }
            }

            ct.ThrowIfCancellationRequested();
            document.SaveFullRewrite(output);
        }

        using var result = PdfiumDocument.Open(output);
        if (result.PageCount != pageCount)
        {
            throw new SaveVerificationException("the flattened copy has another page count");
        }

        for (var i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = result.LoadPage(i);
            try
            {
                if (StrokeAnnotations.HasAppAnnotation(page))
                {
                    throw new SaveVerificationException("a stroke annotation is left in the flattened copy");
                }
            }
            finally
            {
                result.ClosePage(page);
            }
        }
    }

    private static void TryDelete(string? file)
    {
        if (file is null)
        {
            return;
        }

        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
