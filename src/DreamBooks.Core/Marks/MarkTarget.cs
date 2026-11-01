using DreamBooks.Core.Layout;

namespace DreamBooks.Core.Marks;

/// <summary>
/// Where the view goes to show a mark (FR-102, FR-103) or a note (FR-101), in points from the top of the document: the
/// vertical extent centred in the window when it fits, otherwise its top aligned with the window's top, clamped to the
/// document. The one rule shared by the jump (the viewport) and the rail ticks, so the knob lands on a tick.
/// </summary>
public static class MarkTarget
{
    /// <param name="mark">A mark on a page of <paramref name="layout"/>.</param>
    /// <param name="layout">The document's page frames.</param>
    /// <param name="windowHeightPt">The window's height in document points; 0 or less when not known yet.</param>
    public static double TopPt(Mark mark, DocumentLayout layout, double windowHeightPt)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return TopPt(mark.PageIndex, mark.TopPt, mark.BottomPt, layout, windowHeightPt);
    }

    /// <summary>The same rule for a note.</summary>
    public static double TopPt(Note note, DocumentLayout layout, double windowHeightPt)
    {
        ArgumentNullException.ThrowIfNull(note);
        return TopPt(note.PageIndex, note.TopPt, note.BottomPt, layout, windowHeightPt);
    }

    /// <summary>The rule for any vertical extent <paramref name="topPt"/>..<paramref name="bottomPt"/> on a page.</summary>
    public static double TopPt(int pageIndex, double topPt, double bottomPt, DocumentLayout layout, double windowHeightPt)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var extentPt = Math.Max(0, bottomPt - topPt);
        var documentTopPt = layout.PageTopPt(pageIndex) + topPt;
        if (windowHeightPt > 0 && extentPt <= windowHeightPt)
        {
            documentTopPt -= (windowHeightPt - extentPt) / 2;
        }

        return Math.Clamp(documentTopPt, 0, layout.TotalHeightPt);
    }
}
