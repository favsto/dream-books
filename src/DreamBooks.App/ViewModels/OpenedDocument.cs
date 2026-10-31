using DreamBooks.App.Services;
using DreamBooks.Core.Documents;
using DreamBooks.Pdf;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// What the open seam yields: the pages to edit, what to dispose to release them, and, for a real PDF,
/// the viewer's hold on the file (the renderer the view draws with, swapped by saving). Without a viewer
/// (a test source) no bitmap is drawn and the document cannot be saved.
/// </summary>
internal sealed class OpenedDocument
{
    public OpenedDocument(IPageSource source, IAsyncDisposable lifetime, string path, IViewerHandle? viewer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Source = source;
        Lifetime = lifetime;
        Path = path;
        Viewer = viewer;
    }

    public IPageSource Source { get; }

    public IAsyncDisposable Lifetime { get; }

    public string Path { get; }

    public IViewerHandle? Viewer { get; }

    /// <summary>
    /// The production seam: opens the PDF with PDFium behind a <see cref="SwappableRenderer"/>, which is the
    /// session's page source for life and the viewer handle that saving swaps.
    /// </summary>
    public static async Task<OpenedDocument> OpenPdfAsync(string path, CancellationToken ct)
    {
        var renderer = await PdfRenderer.OpenAsync(path, ct);
        try
        {
            var swappable = new SwappableRenderer(renderer);
            return new OpenedDocument(swappable, swappable, renderer.Path, swappable);
        }
        catch
        {
            await renderer.DisposeAsync();
            throw;
        }
    }
}
