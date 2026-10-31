namespace DreamBooks.Core.Layout;

/// <summary>
/// Scroll position that survives zoom and viewport changes. <c>PageIndex</c> is the page covering the
/// viewport's top edge (when the edge is in the gap above page k, k with negative <c>YPt</c>);
/// <c>YPt</c> is the viewport top below that page's top, in page points; <c>ColumnXPt</c> is the x of
/// the viewport's horizontal centre in page points from the left edge of the document column;
/// <c>Zoom</c> is a plain factor (1.0 = 100%).
/// </summary>
public readonly record struct ViewPosition(int PageIndex, double YPt, double ColumnXPt, double Zoom);
