using DreamBooks.Core.Model;

namespace DreamBooks.Core.Ink;

/// <summary>Clips a stroke to the page rectangle [0,w] x [0,h] (FR-27).</summary>
public static class StrokeClipper
{
    /// <summary>
    /// Returns the runs of the stroke that lie inside the page. A stroke whose first point is outside
    /// the page is ignored entirely (empty result). Segments crossing an edge are cut at the edge.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<InkPoint>> Clip(IReadOnlyList<InkPoint> points, double widthPt, double heightPt)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0 || !IsInside(points[0], widthPt, heightPt))
        {
            return [];
        }

        var pieces = new List<IReadOnlyList<InkPoint>>();
        var run = new List<InkPoint> { points[0] };

        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            if (!TryClipSegment(a, b, widthPt, heightPt, out var t0, out var t1))
            {
                Close(pieces, ref run);
                continue;
            }

            if (t0 > 0)
            {
                Close(pieces, ref run);
            }

            run ??= [t0 > 0 ? Lerp(a, b, t0) : a];
            run.Add(t1 < 1 ? Lerp(a, b, t1) : b);

            if (t1 < 1)
            {
                Close(pieces, ref run);
            }
        }

        if (run is not null)
        {
            pieces.Add(run);
        }

        return pieces;
    }

    private static void Close(List<IReadOnlyList<InkPoint>> pieces, ref List<InkPoint>? run)
    {
        if (run is not null)
        {
            pieces.Add(run);
            run = null;
        }
    }

    private static bool IsInside(InkPoint p, double w, double h)
        => p.X >= 0 && p.X <= w && p.Y >= 0 && p.Y <= h;

    // Liang-Barsky: parameter range [t0,t1] of segment a->b inside the rectangle.
    private static bool TryClipSegment(InkPoint a, InkPoint b, double w, double h, out double t0, out double t1)
    {
        t0 = 0;
        t1 = 1;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Clip(-dx, a.X, ref t0, ref t1)
            && Clip(dx, w - a.X, ref t0, ref t1)
            && Clip(-dy, a.Y, ref t0, ref t1)
            && Clip(dy, h - a.Y, ref t0, ref t1);
    }

    private static bool Clip(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0)
        {
            return q >= 0;
        }

        var r = q / p;
        if (p < 0)
        {
            if (r > t1)
            {
                return false;
            }

            t0 = Math.Max(t0, r);
        }
        else
        {
            if (r < t0)
            {
                return false;
            }

            t1 = Math.Min(t1, r);
        }

        return true;
    }

    private static InkPoint Lerp(InkPoint a, InkPoint b, double t) => new(
        a.X + ((b.X - a.X) * t),
        a.Y + ((b.Y - a.Y) * t),
        Lerp(a.Pressure, b.Pressure, t),
        Lerp(a.TiltX, b.TiltX, t),
        Lerp(a.TiltY, b.TiltY, t),
        Lerp(a.TipAngle, b.TipAngle, t),
        a.TimestampMs + (long)Math.Round((b.TimestampMs - a.TimestampMs) * t));

    private static float? Lerp(float? a, float? b, double t)
        => a is { } x && b is { } y ? (float)(x + ((y - x) * t)) : null;
}
