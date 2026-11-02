namespace DreamBooks.Core.Search;

/// <summary>Searches one page's text layer at a time, for the background <see cref="SearchScanner"/> (FR-13).</summary>
public interface ISearchPageSource
{
    /// <summary>Read once, when the scanner first starts, possibly on the UI thread: must be a cheap cached value.</summary>
    int PageCount { get; }

    /// <summary>
    /// Searches page <paramref name="pageIndex"/> for <paramref name="foldedQuery"/> (non-empty, already folded with
    /// <see cref="TextFolding.FoldQuery"/>), returning at most <paramref name="maxHits"/> (at least 1) hits in
    /// displayed page points and setting <see cref="SearchPageResult.Truncated"/> when the page has more. The query
    /// is document content: never log it or put it in an exception message. The scanner survives any exception,
    /// counting the page as failed.
    /// </summary>
    Task<SearchPageResult> SearchPageAsync(int pageIndex, string foldedQuery, int maxHits, CancellationToken ct);
}
