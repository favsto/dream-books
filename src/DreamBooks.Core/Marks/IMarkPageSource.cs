using DreamBooks.Core.Documents;

namespace DreamBooks.Core.Marks;

/// <summary>Read access to the stored strokes of every page, for the background mark scan.</summary>
public interface IMarkPageSource
{
    int PageCount { get; }

    PageDimensions GetPageDimensions(int pageIndex);

    /// <summary>
    /// Loads a page's stored strokes for the scan. Returns null when the page certainly holds none of our
    /// annotations (a cheap pre-check that parses nothing) and also for a damaged page; expected failures
    /// never throw. The scanner still survives any exception, counting the page as failed.
    /// </summary>
    Task<PageStrokes?> TryLoadStrokesForScanAsync(int pageIndex, CancellationToken ct);
}
