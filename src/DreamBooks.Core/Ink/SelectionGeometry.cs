using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Ink;

/// <summary>Lasso hit-testing and bounds (FR-50).</summary>
public static class SelectionGeometry
{
    /// <summary>Even-odd containment; the loop closes implicitly from its last point to its first.</summary>
    public static bool ContainsPoint(IReadOnlyList<PointD> loop, PointD p)
    {
        ArgumentNullException.ThrowIfNull(loop);
        var inside = false;
        for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
        {
            var a = loop[i];
            var b = loop[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < ((b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// The strokes with at least half of their points inside the loop, in their original order. A loop of fewer
    /// than three points selects nothing.
    /// </summary>
    public static IReadOnlyList<InkStroke> StrokesInLoop(IReadOnlyList<InkStroke> strokes, IReadOnlyList<PointD> loop)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(loop);
        if (loop.Count < 3)
        {
            return [];
        }

        return strokes
            .Where(s => s.Points.Count(pt => ContainsPoint(loop, pt.Position)) * 2 >= s.Points.Count)
            .ToArray();
    }

    /// <summary>The bounds of every point of the strokes, or null when there are none.</summary>
    public static RectD? Bounds(IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        RectD? bounds = null;
        foreach (var stroke in strokes)
        {
            foreach (var pt in stroke.Points)
            {
                var point = new RectD(pt.X, pt.Y, pt.X, pt.Y);
                bounds = bounds is { } b ? b.Union(point) : point;
            }
        }

        return bounds;
    }
}
