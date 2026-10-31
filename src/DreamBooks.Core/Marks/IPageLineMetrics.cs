namespace DreamBooks.Core.Marks;

/// <summary>Supplies the text line height of a page, the unit of the mark rules.</summary>
public interface IPageLineMetrics
{
    double LineHeightPt(int pageIndex);
}
