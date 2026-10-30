using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Ink;

/// <summary>Hit testing and splitting of strokes against the eraser's swept capsule (FR-31).</summary>
public static class EraserGeometry
{
    /// <summary>Half of the 1.5x maximum stroke width: how far a stroke's ink extends from its centerline.</summary>
    public const double HitMarginFactor = 0.75;

    public const double ResampleStepPt = 0.25;

    public const double MinRunLengthPt = 0.5;

    /// <summary>True if the stroke polyline comes within radius + 0.75 x base width of the segment from-to.</summary>
    public static bool Hits(InkStroke stroke, PointD from, PointD to, double radiusPt)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        return Hits(stroke, Bounds.Of(stroke), from, to, radiusPt + (stroke.BaseWidthPt * HitMarginFactor));
    }

    /// <summary>
    /// Removes the parts of the stroke inside the capsule (distance &lt;= radius, no width margin).
    /// Returns the surviving runs as new strokes, or the same instance in a one-item list if nothing is hit.
    /// </summary>
    public static IReadOnlyList<InkStroke> ErasePartial(InkStroke stroke, PointD from, PointD to, double radiusPt)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        return ErasePartial(stroke, Bounds.Of(stroke), from, to, radiusPt);
    }

    internal static bool Hits(InkStroke stroke, Bounds bounds, PointD from, PointD to, double reachPt)
    {
        if (!bounds.Expanded(reachPt).Intersects(Bounds.Of(from, to)))
        {
            return false;
        }

        return AnyWithin(stroke.Points, from, to, reachPt);
    }

    internal static IReadOnlyList<InkStroke> ErasePartial(
        InkStroke stroke, Bounds bounds, PointD from, PointD to, double radiusPt)
    {
        if (!Hits(stroke, bounds, from, to, radiusPt))
        {
            return [stroke];
        }

        var result = new List<InkStroke>();
        var run = new List<InkPoint>();
        foreach (var sample in Resample(stroke.Points))
        {
            if (DistanceToSegment(sample.Position, from, to) <= radiusPt)
            {
                Flush(stroke, run, result);
            }
            else
            {
                run.Add(sample);
            }
        }

        Flush(stroke, run, result);
        return result;
    }

    private static void Flush(InkStroke source, List<InkPoint> run, List<InkStroke> result)
    {
        if (run.Count > 0 && RunLength(run) >= MinRunLengthPt)
        {
            result.Add(source.WithPoints(run.ToArray()));
        }

        run.Clear();
    }

    private static double RunLength(List<InkPoint> run)
    {
        var length = 0.0;
        for (var i = 1; i < run.Count; i++)
        {
            length += PointD.Distance(run[i - 1].Position, run[i].Position);
        }

        return length;
    }

    private static bool AnyWithin(IReadOnlyList<InkPoint> points, PointD from, PointD to, double reachPt)
    {
        if (points.Count == 1)
        {
            return DistanceToSegment(points[0].Position, from, to) <= reachPt;
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (SegmentDistance(points[i - 1].Position, points[i].Position, from, to) <= reachPt)
            {
                return true;
            }
        }

        return false;
    }

    private static List<InkPoint> Resample(IReadOnlyList<InkPoint> points)
    {
        var samples = new List<InkPoint>(points.Count) { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            var steps = (int)Math.Ceiling(PointD.Distance(a.Position, b.Position) / ResampleStepPt);
            for (var s = 1; s < steps; s++)
            {
                samples.Add(Lerp(a, b, (double)s / steps));
            }

            samples.Add(b);
        }

        return samples;
    }

    private static InkPoint Lerp(InkPoint a, InkPoint b, double t) => new(
        a.X + ((b.X - a.X) * t),
        a.Y + ((b.Y - a.Y) * t),
        LerpOpt(a.Pressure, b.Pressure, t),
        LerpOpt(a.TiltX, b.TiltX, t),
        LerpOpt(a.TiltY, b.TiltY, t),
        LerpOpt(a.TipAngle, b.TipAngle, t),
        a.TimestampMs + (long)Math.Round((b.TimestampMs - a.TimestampMs) * t));

    private static float? LerpOpt(float? a, float? b, double t)
        => a is { } x && b is { } y ? (float)(x + ((y - x) * t)) : null;

    private static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSq = (dx * dx) + (dy * dy);
        if (lengthSq == 0)
        {
            return PointD.Distance(p, a);
        }

        var t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSq, 0, 1);
        return PointD.Distance(p, new PointD(a.X + (t * dx), a.Y + (t * dy)));
    }

    private static double SegmentDistance(PointD a, PointD b, PointD c, PointD d)
    {
        if (SegmentsCross(a, b, c, d))
        {
            return 0;
        }

        return Math.Min(
            Math.Min(DistanceToSegment(a, c, d), DistanceToSegment(b, c, d)),
            Math.Min(DistanceToSegment(c, a, b), DistanceToSegment(d, a, b)));
    }

    private static bool SegmentsCross(PointD a, PointD b, PointD c, PointD d)
    {
        var d1 = Cross(c, d, a);
        var d2 = Cross(c, d, b);
        var d3 = Cross(a, b, c);
        var d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(PointD o, PointD p, PointD q)
        => ((p.X - o.X) * (q.Y - o.Y)) - ((p.Y - o.Y) * (q.X - o.X));

    internal readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public static Bounds Of(InkStroke stroke)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in stroke.Points)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }

            return new Bounds(minX, minY, maxX, maxY);
        }

        public static Bounds Of(PointD a, PointD b)
            => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

        public Bounds Expanded(double margin)
            => new(MinX - margin, MinY - margin, MaxX + margin, MaxY + margin);

        public bool Intersects(Bounds o)
            => MinX <= o.MaxX && MaxX >= o.MinX && MinY <= o.MaxY && MaxY >= o.MinY;
    }
}
