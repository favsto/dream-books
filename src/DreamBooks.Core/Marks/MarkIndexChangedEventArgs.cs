namespace DreamBooks.Core.Marks;

/// <summary>Raised when the marks of one page change.</summary>
public sealed class MarkIndexChangedEventArgs(int pageIndex) : EventArgs
{
    public int PageIndex { get; } = pageIndex;
}
