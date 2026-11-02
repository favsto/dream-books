namespace DreamBooks.Core.Rendering;

/// <summary>
/// Renders one page to a bitmap of the given pixel size. Implementations run the actual work off the
/// UI thread (NFR-04).
/// </summary>
public interface IBitmapRenderer<TBitmap>
{
    /// <summary>
    /// Renders <paramref name="pageIndex"/> at <paramref name="pixelWidth"/> x <paramref name="pixelHeight"/>.
    /// Returns <see langword="null"/> when the render failed or is not available (for example while the
    /// document is being swapped after a save); expected failures never throw. May throw
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<TBitmap?> RenderAsync(int pageIndex, int pixelWidth, int pixelHeight, CancellationToken ct);
}
