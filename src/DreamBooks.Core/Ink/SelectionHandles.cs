using DreamBooks.Core.Geometry;

namespace DreamBooks.Core.Ink;

/// <summary>Hit-testing of the selection rectangle's corner handles and body, in page points (FR-51).</summary>
public static class SelectionHandles
{
    private static readonly SelectionHandle[] _corners =
        [SelectionHandle.TopLeft, SelectionHandle.TopRight, SelectionHandle.BottomRight, SelectionHandle.BottomLeft];

    /// <summary>
    /// The handle at <paramref name="p"/>. A corner is grabbed within <paramref name="tolerancePt"/> of it; the body
    /// reaches half the tolerance beyond the rectangle. A rectangle smaller than the tolerance in both directions has
    /// no corner handles (a dot can be moved, not resized); a straight line keeps them at its ends.
    /// </summary>
    public static SelectionHandle HitTest(RectD bounds, PointD p, double tolerancePt)
    {
        if (bounds.Width >= tolerancePt || bounds.Height >= tolerancePt)
        {
            var best = SelectionHandle.None;
            var bestDistance = tolerancePt;
            foreach (var corner in _corners)
            {
                var distance = PointD.Distance(Corner(bounds, corner), p);
                if (distance <= bestDistance)
                {
                    best = corner;
                    bestDistance = distance;
                }
            }

            if (best != SelectionHandle.None)
            {
                return best;
            }
        }

        var slack = tolerancePt / 2;
        var inflated = new RectD(bounds.Left - slack, bounds.Top - slack, bounds.Right + slack, bounds.Bottom + slack);
        return inflated.Contains(p) ? SelectionHandle.Inside : SelectionHandle.None;
    }

    public static PointD Corner(RectD bounds, SelectionHandle corner) => corner switch
    {
        SelectionHandle.TopLeft => new PointD(bounds.Left, bounds.Top),
        SelectionHandle.TopRight => new PointD(bounds.Right, bounds.Top),
        SelectionHandle.BottomRight => new PointD(bounds.Right, bounds.Bottom),
        SelectionHandle.BottomLeft => new PointD(bounds.Left, bounds.Bottom),
        _ => throw new ArgumentOutOfRangeException(nameof(corner), corner, "Not a corner."),
    };

    /// <summary>The corner across the rectangle: the fixed point of a resize dragged from <paramref name="corner"/>.</summary>
    public static PointD Opposite(RectD bounds, SelectionHandle corner) => corner switch
    {
        SelectionHandle.TopLeft => Corner(bounds, SelectionHandle.BottomRight),
        SelectionHandle.TopRight => Corner(bounds, SelectionHandle.BottomLeft),
        SelectionHandle.BottomRight => Corner(bounds, SelectionHandle.TopLeft),
        SelectionHandle.BottomLeft => Corner(bounds, SelectionHandle.TopRight),
        _ => throw new ArgumentOutOfRangeException(nameof(corner), corner, "Not a corner."),
    };
}
