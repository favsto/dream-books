using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Ink;

/// <summary>Pure edits of a stroke selection: move, uniform resize, recolour (FR-51). Every result has new ids.</summary>
public static class StrokeTransform
{
    /// <summary>The smallest factor <see cref="Scale"/> applies, so a resize never collapses the selection.</summary>
    public const double MinScale = 0.2;

    /// <summary>Moves the strokes by (dx, dy), clamped so their bounds stay on the page.</summary>
    public static IReadOnlyList<InkStroke> Translate(IReadOnlyList<InkStroke> strokes, double dx, double dy, double pageWidthPt, double pageHeightPt)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (SelectionGeometry.Bounds(strokes) is not { } b)
        {
            return [];
        }

        dx = Math.Max(-b.Left, Math.Min(pageWidthPt - b.Right, dx));
        dy = Math.Max(-b.Top, Math.Min(pageHeightPt - b.Bottom, dy));
        return strokes
            .Select(s => Rebuild(s, s.BaseWidthPt, s.Points.Select(p => p with { X = p.X + dx, Y = p.Y + dy }).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Scales the strokes about <paramref name="anchor"/>, width included. The factor is at least
    /// <see cref="MinScale"/> and at most what keeps every point on the page.
    /// </summary>
    public static IReadOnlyList<InkStroke> Scale(IReadOnlyList<InkStroke> strokes, double factor, PointD anchor, double pageWidthPt, double pageHeightPt)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        var max = double.PositiveInfinity;
        foreach (var p in strokes.SelectMany(s => s.Points))
        {
            max = Math.Min(max, MaxFactor(anchor.X, p.X, pageWidthPt));
            max = Math.Min(max, MaxFactor(anchor.Y, p.Y, pageHeightPt));
        }

        var applied = Math.Min(Math.Max(factor, MinScale), max);
        return strokes
            .Select(s => Rebuild(
                s,
                s.BaseWidthPt * applied,
                s.Points.Select(p => p with
                {
                    X = Math.Clamp(anchor.X + ((p.X - anchor.X) * applied), 0, pageWidthPt),
                    Y = Math.Clamp(anchor.Y + ((p.Y - anchor.Y) * applied), 0, pageHeightPt),
                }).ToArray()))
            .ToArray();
    }

    /// <summary>Gives the strokes a new colour; highlighter strokes keep their own alpha so they stay translucent.</summary>
    public static IReadOnlyList<InkStroke> Recolor(IReadOnlyList<InkStroke> strokes, InkColor color)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        return strokes
            .Select(s => new InkStroke(
                Guid.NewGuid(),
                s.Tool,
                s.Tool == InkTool.Highlighter ? color with { A = s.Color.A } : color,
                s.BaseWidthPt,
                s.Points,
                s.Chisel))
            .ToArray();
    }

    private static double MaxFactor(double anchor, double coordinate, double limit)
    {
        var offset = coordinate - anchor;
        if (offset > 0)
        {
            return (limit - anchor) / offset;
        }

        return offset < 0 ? anchor / -offset : double.PositiveInfinity;
    }

    private static InkStroke Rebuild(InkStroke source, double widthPt, IReadOnlyList<InkPoint> points)
        => new(Guid.NewGuid(), source.Tool, source.Color, widthPt, points, source.Chisel);
}
