using DreamBooks.Core.Documents;
using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>
/// The page source a <see cref="DocumentSession"/> holds for life, forwarding to whichever
/// <see cref="PdfRenderer"/> is current. Saving swaps the renderer underneath: a plain Save releases it (so
/// the file can be replaced) and reopens it on the saved file; Save as reopens it on the new file.
/// </summary>
/// <remarks>
/// Page count and dimensions are taken once from the first renderer (a save never changes them; the writer
/// verifies the page count), so the session's synchronous lookups work even while released. Loading strokes
/// while released fails with <see cref="InvalidOperationException"/>, which the session reports as a failed
/// page load (nothing is cached). Swaps happen on the UI thread.
/// </remarks>
public sealed class SwappableRenderer : IPageSource, IViewerHandle, IAsyncDisposable
{
    private readonly PageDimensions[] _dimensions;
    private readonly Func<string, CancellationToken, Task<PdfRenderer>> _open;
    private PdfRenderer? _current;
    private bool _disposed;

    public SwappableRenderer(PdfRenderer renderer, Func<string, CancellationToken, Task<PdfRenderer>>? open = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _current = renderer;
        _open = open ?? ((path, ct) => PdfRenderer.OpenAsync(path, ct));
        Info = renderer.Info;
        _dimensions = new PageDimensions[renderer.PageCount];
        for (var i = 0; i < _dimensions.Length; i++)
        {
            _dimensions[i] = renderer.GetPageDimensions(i);
        }
    }

    public event EventHandler? CurrentChanged;

    public PdfRenderer? Current => _current;

    public PdfDocumentInfo Info { get; private set; }

    public int PageCount => _dimensions.Length;

    public PageDimensions GetPageDimensions(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _dimensions.Length);
        return _dimensions[pageIndex];
    }

    public Task<PageStrokes> LoadStrokesAsync(int pageIndex, CancellationToken ct = default)
    {
        if (_current is not { } renderer)
        {
            return Task.FromException<PageStrokes>(
                new InvalidOperationException("The document file is released while it is being saved."));
        }

        try
        {
            return renderer.LoadStrokesAsync(pageIndex, ct);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException)
        {
            return Task.FromException<PageStrokes>(ex);
        }
    }

    public async Task ReleaseAsync()
    {
        var old = _current;
        if (old is null)
        {
            return;
        }

        _current = null;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        await DisposeQuietlyAsync(old);
    }

    public async Task ReopenAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var renderer = await _open(path, ct);
        if (_disposed)
        {
            await DisposeQuietlyAsync(renderer);
            throw new ObjectDisposedException(nameof(SwappableRenderer));
        }

        var old = _current;
        _current = renderer;
        Info = renderer.Info;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        if (old is not null)
        {
            await DisposeQuietlyAsync(old);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var old = _current;
        _current = null;
        if (old is not null)
        {
            await old.DisposeAsync();
        }
    }

    private static async Task DisposeQuietlyAsync(PdfRenderer renderer)
    {
        try
        {
            await renderer.DisposeAsync();
        }
#pragma warning disable CA1031 // Closing a document must never fail a save or a swap.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
