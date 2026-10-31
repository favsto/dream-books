using DreamBooks.Core.Layout;

namespace DreamBooks.Core.Marks;

/// <summary>
/// Where the view goes to show a mark (FR-102, FR-103), in points from the top of the document: the mark's vertical
/// extent centred in the window when it fits, otherwise its top aligned with the window's top, clamped to the
/// document. The one rule shared by the jump (the viewport) and the rail ticks, so the knob lands on a mark's tick.
/// </summary>
public static class MarkTarget
{
    /// <param name="mark">A mark on a page of <paramref name="layout"/>.</param>
    /// <param name="layout">The document's page frames.</param>
    /// <param name="windowHeightPt">The window's height in document points; 0 or less when not known yet.</param>
    public static double TopPt(Mark mark, DocumentLayout layout, double windowHeightPt)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentNullException.ThrowIfNull(layout);
        var extentPt = Math.Max(0, mark.BottomPt - mark.TopPt);
        var topPt = layout.PageTopPt(mark.PageIndex) + mark.TopPt;
        if (windowHeightPt > 0 && extentPt <= windowHeightPt)
        {
            topPt -= (windowHeightPt - extentPt) / 2;
        }

        return Math.Clamp(topPt, 0, layout.TotalHeightPt);
    }
}
