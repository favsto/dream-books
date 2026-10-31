using System.Windows.Media;
using DreamBooks.Pdf;

namespace DreamBooks.App.Views;

/// <summary>
/// Where <see cref="DocumentView"/> gets page bitmaps: the single seam the render scheduler (phase 3a, Task 8)
/// replaces. The view asks once per page entering its window, again for its pages when the zoom settles, the DPI
/// changes or the renderer is replaced (<see cref="DocumentView.InvalidateBitmaps"/>), and cancels the request of a
/// page that leaves the window.
/// </summary>
internal interface IPageBitmapSource
{
    /// <summary>
    /// A bitmap of the page at <paramref name="zoom"/> for a screen scale of <paramref name="dpiScale"/>, or null when
    /// none can be made now (no renderer, cancelled, failed). Never throws.
    /// </summary>
    Task<ImageSource?> RenderAsync(int pageIndex, PageSize pageSize, double zoom, double dpiScale, CancellationToken ct);
}
