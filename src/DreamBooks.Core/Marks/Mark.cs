namespace DreamBooks.Core.Marks;

/// <summary>
/// A recognised mark: one or more merged margin lines. Extent is in page points as displayed
/// (top-left origin, y down). Marks are derived data and never stored in the PDF.
/// </summary>
public sealed record Mark(int PageIndex, MarkSide Side, double TopPt, double BottomPt, IReadOnlyList<Guid> StrokeIds);
