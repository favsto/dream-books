namespace DreamBooks.Core.Documents;

/// <summary>
/// Asks the view to bring a page into sight, e.g. the page an undo or redo just changed. The view scrolls
/// only when the page is not already visible (FR-62).
/// </summary>
public sealed class RevealRequestedEventArgs(int pageIndex) : EventArgs
{
    public int PageIndex { get; } = pageIndex;
}
