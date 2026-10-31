using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>
/// The viewer's hold on the open file. A plain Save must release it before the file is replaced (an open
/// PDFium document locks the file) and reopen it afterwards; Save as reopens it on the new file.
/// </summary>
public interface IViewerHandle
{
    /// <summary>Raised on the calling thread whenever <see cref="Current"/> changes.</summary>
    event EventHandler? CurrentChanged;

    /// <summary>Security and signature facts of the document as last opened; kept while released.</summary>
    PdfDocumentInfo Info { get; }

    /// <summary>The renderer the view draws with; null while released (or without a real PDF).</summary>
    PdfRenderer? Current { get; }

    /// <summary>Closes the file. Idempotent; never throws.</summary>
    Task ReleaseAsync();

    /// <summary>Opens <paramref name="path"/> and makes it current, then closes the previous renderer, if any.</summary>
    Task ReopenAsync(string path, CancellationToken ct);
}
