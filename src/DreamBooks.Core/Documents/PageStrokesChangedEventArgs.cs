namespace DreamBooks.Core.Documents;

/// <summary>The strokes of a loaded page changed, or the page just finished loading.</summary>
public sealed class PageStrokesChangedEventArgs(int pageIndex) : EventArgs
{
    public int PageIndex { get; } = pageIndex;
}
