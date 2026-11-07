namespace DreamBooks.Core.Settings;

/// <summary>Where the toolbar goes when the user lets go of it (UI-02): the window edge nearest to the release point.</summary>
public static class ToolbarPlacement
{
    /// <summary>
    /// The edge of a <paramref name="width"/> × <paramref name="height"/> window nearest to the point
    /// (<paramref name="x"/>, <paramref name="y"/>). A point outside the window counts as on its nearest edge; a tie goes to
    /// <see cref="ToolbarEdge.Bottom"/> first, then top, then left, then right; a broken point or window (not a finite
    /// number, empty) gives <see cref="ToolbarEdge.Bottom"/>.
    /// </summary>
    public static ToolbarEdge NearestEdge(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0)
        {
            return ToolbarEdge.Bottom;
        }

        var px = Math.Clamp(x, 0, width);
        var py = Math.Clamp(y, 0, height);
        var best = ToolbarEdge.Bottom;
        var distance = height - py;
        Consider(ToolbarEdge.Top, py);
        Consider(ToolbarEdge.Left, px);
        Consider(ToolbarEdge.Right, width - px);
        return best;

        void Consider(ToolbarEdge edge, double d)
        {
            if (d < distance)
            {
                best = edge;
                distance = d;
            }
        }
    }
}
