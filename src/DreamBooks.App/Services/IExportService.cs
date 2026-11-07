using DreamBooks.Core.Documents;

namespace DreamBooks.App.Services;

/// <summary>The two exports (FR-87, FR-88); an interface so the view model can be tested without writing files.</summary>
internal interface IExportService
{
    /// <summary>The flattened PDF of the document as the reader sees it.</summary>
    Task<ExportResult> ExportFlattenedAsync(DocumentSession session, CancellationToken ct);

    /// <summary>One page as a PNG.</summary>
    Task<ExportResult> ExportPageAsync(DocumentSession session, int pageIndex, CancellationToken ct);
}
