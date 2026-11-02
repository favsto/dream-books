namespace DreamBooks.Core.Search;

/// <summary>Raised when the search result of one page changes.</summary>
public sealed class SearchIndexChangedEventArgs(int pageIndex) : EventArgs
{
    public int PageIndex { get; } = pageIndex;
}
