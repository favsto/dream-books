using System.Globalization;
using System.IO;
using DreamBooks.App.Resources;
using DreamBooks.Core.Documents;
using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>How an export ended.</summary>
internal enum ExportOutcome
{
    Done,
    Cancelled,
    Failed,
}

/// <summary>The outcome and, for a failure, the Italian message (never with a path or an exception text, NFR-12).</summary>
internal sealed record ExportResult(ExportOutcome Outcome, string? Message = null);

/// <summary>
/// The two exports (FR-87, FR-88): ask where to save, suggest a name that is never the open file, export what the reader
/// sees (the session, unsaved strokes included), and report the outcome. The open document, its dirty state and the
/// original file are never touched.
/// </summary>
internal sealed class ExportService : IExportService
{
    private readonly IFileDialogService _dialogs;
    private readonly FlattenedPdfExporter _pdf = new();
    private readonly PageImageExporter _image = new();

    public ExportService(IFileDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        _dialogs = dialogs;
    }

    /// <summary>The flattened PDF (FR-87): strokes merged into the pages.</summary>
    public Task<ExportResult> ExportFlattenedAsync(DocumentSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var suggested = string.Format(CultureInfo.CurrentCulture, Strings.ExportFlattenedNameFormat, Path.GetFileNameWithoutExtension(session.Path));
        return RunAsync(
            session,
            _dialogs.PickSavePdf(suggested),
            (destination, changes) => _pdf.ExportAsync(session.Path, destination, changes, ct));
    }

    /// <summary>One page as a PNG (FR-88).</summary>
    public Task<ExportResult> ExportPageAsync(DocumentSession session, int pageIndex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var suggested = string.Format(
            CultureInfo.CurrentCulture, Strings.ExportPageNameFormat, Path.GetFileNameWithoutExtension(session.Path), pageIndex + 1);
        return RunAsync(
            session,
            _dialogs.PickSavePng(suggested),
            (destination, changes) => _image.ExportAsync(session.Path, pageIndex, changes, destination, ct));
    }

    private static async Task<ExportResult> RunAsync(
        DocumentSession session, string? picked, Func<string, IReadOnlyList<PageChange>, Task<string>> export)
    {
        if (picked is null)
        {
            return new ExportResult(ExportOutcome.Cancelled);
        }

        var destination = Path.GetFullPath(picked);
        if (string.Equals(destination, Path.GetFullPath(session.Path), StringComparison.OrdinalIgnoreCase))
        {
            return new ExportResult(ExportOutcome.Failed, Strings.ErrorExportSameFile); // never over the open document
        }

        try
        {
            await export(destination, session.GetChanges());
            return new ExportResult(ExportOutcome.Done);
        }
        catch (OperationCanceledException)
        {
            return new ExportResult(ExportOutcome.Cancelled);
        }
        catch (ExportDestinationIsSourceException)
        {
            return new ExportResult(ExportOutcome.Failed, Strings.ErrorExportSameFile); // the same file under another spelling
        }
        catch (PdfSaveRefusedException)
        {
            return new ExportResult(ExportOutcome.Failed, Strings.ErrorExportEncrypted);
        }
#pragma warning disable CA1031 // Any failure means "not exported"; its text may hold paths, so it is never shown (NFR-12).
        catch (Exception)
#pragma warning restore CA1031
        {
            return new ExportResult(ExportOutcome.Failed, Strings.ErrorExportFailed);
        }
    }
}
