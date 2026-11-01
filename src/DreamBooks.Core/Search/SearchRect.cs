namespace DreamBooks.Core.Search;

/// <summary>
/// A highlight rectangle of a search hit in displayed page points (top-left origin, y down, rotation applied):
/// converted to screen coordinates only in the view (FR-26).
/// </summary>
public readonly record struct SearchRect(double LeftPt, double TopPt, double RightPt, double BottomPt);
