namespace DreamBooks.Core.Marks;

/// <summary>
/// Where a mark was drawn, in the displayed orientation: a page margin, or the middle of the page for a highlight
/// (FR-100, <see cref="MarkRules.HighlightCentreBandFraction"/>).
/// </summary>
public enum MarkSide
{
    Left,
    Right,
    Centre,
}
