namespace DreamBooks.Core.Marks;

/// <summary>Where a mark sits in the document: page and vertical extent in page points.</summary>
public readonly record struct MarkLocation(int PageIndex, double TopPt, double BottomPt);
