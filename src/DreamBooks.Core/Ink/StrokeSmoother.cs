using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Ink;

/// <summary>
/// Smooths a raw stroke on pen-up: duplicate removal, corner-preserving moving average,
/// then centripetal Catmull-Rom resampling with a maximum spacing (FR-22, FR-23).
/// </summary>
public static class StrokeSmoother
{
    private const double _minSpacing = 0.01;
    private const double _maxSpacing = 0.5;
    private const double _cornerAngleDegrees = 70;
    private const int _maxRefinements = 10;

    public static IReadOnlyList<InkPoint> Smooth(IReadOnlyList<InkPoint> points, SmoothingLevel level)
    {
        var deduped = RemoveDuplicates(points);
        if (deduped.Count < 2)
        {
            return deduped;
        }

        var averaged = level switch
        {
            SmoothingLevel.Light => MovingAverage(deduped, 1),
            SmoothingLevel.Strong => MovingAverage(deduped, 2),
            _ => deduped,
        };

        return Resample(averaged);
    }

    private static List<InkPoint> RemoveDuplicates(IReadOnlyList<InkPoint> points)
    {
        var result = new List<InkPoint>(points.Count);
        foreach (var p in points)
        {
            if (result.Count == 0 || PointD.Distance(result[^1].Position, p.Position) >= _minSpacing)
            {
                result.Add(p);
            }
            else
            {
                result[^1] = InkPointMerge.Merge(result[^1], p);
            }
        }

        return result;
    }

    private static List<InkPoint> MovingAverage(List<InkPoint> src, int halfWindow)
    {
        var n = src.Count;
        var result = new List<InkPoint>(src);
        for (var i = 1; i < n - 1; i++)
        {
            if (IsCorner(src[i - 1], src[i], src[i + 1]))
            {
                continue;
            }

            var h = Math.Min(halfWindow, Math.Min(i, n - 1 - i));
            double sx = 0, sy = 0, sp = 0;
            var pc = 0;
            for (var j = i - h; j <= i + h; j++)
            {
                sx += src[j].X;
                sy += src[j].Y;
                if (src[j].Pressure is { } pr)
                {
                    sp += pr;
                    pc++;
                }
            }

            var count = (2 * h) + 1;
            result[i] = src[i] with
            {
                X = sx / count,
                Y = sy / count,
                Pressure = pc > 0 ? (float)(sp / pc) : null,
            };
        }

        return result;
    }

    private static bool IsCorner(InkPoint a, InkPoint b, InkPoint c)
    {
        var ux = b.X - a.X;
        var uy = b.Y - a.Y;
        var vx = c.X - b.X;
        var vy = c.Y - b.Y;
        var angle = Math.Abs(Math.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy)));
        return angle * 180 / Math.PI > _cornerAngleDegrees;
    }

    private static List<InkPoint> Resample(List<InkPoint> pts)
    {
        var n = pts.Count;
        var result = new List<InkPoint>(n * 2) { pts[0] };
        var scratch = new PointD[64];
        for (var i = 0; i < n - 1; i++)
        {
            var p0 = pts[Math.Max(i - 1, 0)].Position;
            var p1 = pts[i].Position;
            var p2 = pts[i + 1].Position;
            var p3 = pts[Math.Min(i + 2, n - 1)].Position;

            var steps = Math.Max(1, (int)Math.Ceiling(PointD.Distance(p1, p2) / _maxSpacing));
            for (var attempt = 0; ; attempt++)
            {
                if (scratch.Length < steps + 1)
                {
                    scratch = new PointD[steps + 1];
                }

                var maxGap = 0.0;
                scratch[0] = p1;
                for (var k = 1; k <= steps; k++)
                {
                    scratch[k] = k == steps ? p2 : CatmullRom(p0, p1, p2, p3, (double)k / steps);
                    maxGap = Math.Max(maxGap, PointD.Distance(scratch[k - 1], scratch[k]));
                }

                if (maxGap <= _maxSpacing || attempt >= _maxRefinements)
                {
                    break;
                }

                steps = (int)Math.Ceiling(steps * Math.Max(1.1, maxGap / _maxSpacing)) + 1;
            }

            for (var k = 1; k < steps; k++)
            {
                result.Add(Interpolate(pts[i], pts[i + 1], scratch[k], (double)k / steps));
            }

            result.Add(pts[i + 1]);
        }

        return result;
    }

    private static InkPoint Interpolate(InkPoint a, InkPoint b, PointD position, double u)
    {
        var nearer = u < 0.5 ? a : b;
        float? pressure = a.Pressure is { } pa && b.Pressure is { } pb ? (float)(pa + ((pb - pa) * u)) : null;
        return new InkPoint(
            position.X,
            position.Y,
            pressure,
            nearer.TiltX,
            nearer.TiltY,
            nearer.TipAngle,
            (long)Math.Round(a.TimestampMs + ((b.TimestampMs - a.TimestampMs) * u)));
    }

    // Centripetal Catmull-Rom (alpha 0.5) via the Barry-Goldman pyramid; u in 0-1 spans p1 to p2.
    private static PointD CatmullRom(PointD p0, PointD p1, PointD p2, PointD p3, double u)
    {
        var t0 = 0.0;
        var t1 = t0 + Math.Sqrt(PointD.Distance(p0, p1));
        var t2 = t1 + Math.Sqrt(PointD.Distance(p1, p2));
        var t3 = t2 + Math.Sqrt(PointD.Distance(p2, p3));
        var t = t1 + ((t2 - t1) * u);

        var a1 = Blend(p0, p1, t0, t1, t);
        var a2 = Blend(p1, p2, t1, t2, t);
        var a3 = Blend(p2, p3, t2, t3, t);
        var b1 = Blend(a1, a2, t0, t2, t);
        var b2 = Blend(a2, a3, t1, t3, t);
        return Blend(b1, b2, t1, t2, t);
    }

    // Linear blend of a (at ta) and b (at tb) evaluated at t; a zero-length interval collapses to b.
    private static PointD Blend(PointD a, PointD b, double ta, double tb, double t)
    {
        var d = tb - ta;
        if (d <= 0)
        {
            return b;
        }

        var w = (t - ta) / d;
        return new PointD(a.X + ((b.X - a.X) * w), a.Y + ((b.Y - a.Y) * w));
    }
}
