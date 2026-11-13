using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Ink;

/// <summary>
/// Highlighter outlines and straight-line mode, in PDF page points with y down (FR-42, FR-44, FR-46).
/// Every outline is one closed polygon (the last vertex joins the first) with positive shoelace area,
/// to be filled with the non-zero rule like the pen contour.
/// </summary>
public static class HighlighterGeometry
{
    private const double _contourTolerancePt = 0.05;
    private const double _minSpacing = 0.01;
    private const double _maxSagitta = 0.02;
    private const double _maxArcStep = Math.PI / 8;
    private const double _minSweep = 1e-9;
    private const double _angleEpsilon = 1e-9;
    private const double _maxStraightAngleDeg = 3;
    private const double _maxStraightDeviation = 0.02;
    private const double _straightToleranceFloorPt = 4;

    /// <summary>
    /// The inked outline of a highlighter stroke: the chisel outline if it has a nib (FR-46), the constant-width one
    /// otherwise (FR-42). The one choice shared by the saved appearance, the page layer and the live ink.
    /// </summary>
    public static IReadOnlyList<PointD> Outline(InkStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        return Outline(stroke.Points, stroke.BaseWidthPt, stroke.Chisel);
    }

    /// <summary>
    /// <see cref="Outline(InkStroke)"/> for raw points: both outlines scale uniformly, so the live ink passes screen
    /// points and the width times the scale.
    /// </summary>
    public static IReadOnlyList<PointD> Outline(IReadOnlyList<InkPoint> points, double widthPt, ChiselTip? chisel) =>
        chisel is { } tip ? ChiselOutline(points, tip, widthPt) : ConstantWidthOutline(points, widthPt);

    /// <summary>
    /// The highlighter's nib, the shape swept along the centre-line: the half segment <c>h</c> (zero for a round tip;
    /// <c>(1 − r)·L/2·(cos θ, −sin θ)</c> for a chisel) and the radius of its rounded ends (half the width for a round
    /// tip; <c>r·L/2</c> for a chisel). The ink is the centre-line swept by the segment [−h, h] grown by the radius,
    /// so it reaches the inked extent used by the eraser and the marks (FR-31, FR-100).
    /// </summary>
    public static (PointD Half, double Radius) InkNib(InkStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        if (stroke.Chisel is not { } chisel)
        {
            return (default, stroke.BaseWidthPt / 2);
        }

        var theta = chisel.AngleDeg * Math.PI / 180;
        var half = (1 - chisel.Ratio) * stroke.BaseWidthPt / 2;
        return (new PointD(half * Math.Cos(theta), -half * Math.Sin(theta)), chisel.Ratio * stroke.BaseWidthPt / 2);
    }

    /// <summary>
    /// Round-tip outline of constant width <paramref name="widthPt"/>: the pen contour with the same
    /// width at every point, so pressure never changes it (FR-42).
    /// </summary>
    public static IReadOnlyList<PointD> ConstantWidthOutline(IReadOnlyList<InkPoint> points, double widthPt)
    {
        ArgumentNullException.ThrowIfNull(points);
        RequirePositive(widthPt, nameof(widthPt));
        if (points.Count == 0)
        {
            return [];
        }

        var centers = points.Select(p => p.Position).ToArray();
        var widths = Enumerable.Repeat(widthPt, centers.Length).ToArray();
        return StrokeOutline.BuildContour(centers, widths, _contourTolerancePt)[0];
    }

    /// <summary>
    /// Chisel outline (FR-46): the centre-line swept by a nib of length <paramref name="lengthPt"/> held at
    /// the fixed angle θ of <paramref name="chisel"/>, with <c>h = (L/2)·(cos θ, −sin θ)</c> (counter-clockwise
    /// on screen, y down). The nib is a segment of length (1 − r)·L with round ends of diameter r·L, so its
    /// width across a motion of direction φ is exactly <c>L·(r + (1 − r)·|sin(θ − φ)|)</c>.
    /// </summary>
    /// <remarks>
    /// The polygon is built like <see cref="StrokeContour"/> with the nib's support point in place of the
    /// disc's: the chain sum of the convex hulls of consecutive nibs minus the interior nibs, so a stroke
    /// that scrubs back and forth leaves no holes under the non-zero rule.
    /// </remarks>
    public static IReadOnlyList<PointD> ChiselOutline(IReadOnlyList<InkPoint> points, ChiselTip chisel, double lengthPt)
    {
        ArgumentNullException.ThrowIfNull(points);
        RequirePositive(lengthPt, nameof(lengthPt));
        if (points.Count == 0)
        {
            return [];
        }

        var theta = chisel.AngleDeg * Math.PI / 180;
        var half = (1 - chisel.Ratio) * lengthPt / 2;
        var nib = new Nib(new PointD(half * Math.Cos(theta), -half * Math.Sin(theta)), chisel.Ratio * lengthPt / 2);

        var c = new List<PointD>(points.Count);
        foreach (var p in points)
        {
            if (c.Count == 0 || PointD.Distance(c[^1], p.Position) >= _minSpacing)
            {
                c.Add(p.Position);
            }
        }

        var poly = new List<PointD>();
        if (c.Count == 1)
        {
            nib.Arc(poly, c[0], 0, 2 * Math.PI);
            return Close(poly);
        }

        var m = c.Count - 1;
        var dir = new double[m];
        for (var i = 0; i < m; i++)
        {
            dir[i] = Math.Atan2(c[i + 1].Y - c[i].Y, c[i + 1].X - c[i].X);
        }

        double Forward(int i) => dir[i] - (Math.PI / 2);
        double Back(int i) => dir[i] + (Math.PI / 2);

        var turn = new double[m];
        for (var j = 1; j < m; j++)
        {
            turn[j] = Math.IEEERemainder(dir[j] - dir[j - 1], 2 * Math.PI);
        }

        nib.Arc(poly, c[0], Back(0), Math.PI);
        for (var j = 1; j < m; j++)
        {
            nib.Arc(poly, c[j], Forward(j - 1), turn[j]);
        }

        nib.Arc(poly, c[m], Forward(m - 1), Math.PI);
        for (var j = m - 1; j >= 1; j--)
        {
            nib.Arc(poly, c[j], Back(j), -turn[j]);
        }

        return Close(poly);
    }

    /// <summary>
    /// Straight-line mode (FR-44): when the chord from the first to the last point is within 3° of
    /// horizontal and every point lies within 2% of the chord length from it, returns the two end points
    /// made horizontal (both at the mean height of the ends; x, timestamps and the other samples kept).
    /// Both tolerances are at least 4 pt (a quarter of the highlighter's width, well inside a text line), so a
    /// short highlight over a word or two, where 3° and 2% are a fraction of a point, still straightens despite the
    /// hand's tremor. Otherwise, or with fewer than two points or no chord, returns null.
    /// </summary>
    public static IReadOnlyList<InkPoint>? StraightenIfNearHorizontal(IReadOnlyList<InkPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            return null;
        }

        var first = points[0];
        var last = points[^1];
        var dx = last.X - first.X;
        var dy = last.Y - first.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (!(length >= _minSpacing))
        {
            return null;
        }

        if (Math.Atan2(Math.Abs(dy), Math.Abs(dx)) > _maxStraightAngleDeg * Math.PI / 180
            && !(Math.Abs(dy) <= _straightToleranceFloorPt && Math.Abs(dy) < Math.Abs(dx)))
        {
            return null;
        }

        var limit = Math.Max(_maxStraightDeviation * length, _straightToleranceFloorPt);
        foreach (var p in points)
        {
            var t = Math.Clamp((((p.X - first.X) * dx) + ((p.Y - first.Y) * dy)) / (length * length), 0, 1);
            var distance = PointD.Distance(p.Position, new PointD(first.X + (t * dx), first.Y + (t * dy)));
            if (!(distance <= limit))
            {
                return null;
            }
        }

        var y = (first.Y + last.Y) / 2;
        return [first with { Y = y }, last with { Y = y }];
    }

    private static void RequirePositive(double value, string name)
    {
        if (!(value > 0) || double.IsPositiveInfinity(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be finite and greater than zero.");
        }
    }

    // Drops the closing duplicate: the polygon is implicitly closed.
    private static PointD[] Close(List<PointD> poly)
    {
        if (poly.Count > 1 && PointD.Distance(poly[0], poly[^1]) < 1e-9)
        {
            poly.RemoveAt(poly.Count - 1);
        }

        return [.. poly];
    }

    /// <summary>
    /// The chisel nib: the segment [−h, h] plus a disc of radius rho. Its support point in the direction
    /// of angle a is s·h + rho·(cos a, sin a), with s the sign of h·(cos a, sin a); s jumps where the
    /// direction is perpendicular to h.
    /// </summary>
    private sealed class Nib(PointD h, double rho)
    {
        private readonly bool _hasSegment = (h.X * h.X) + (h.Y * h.Y) > 1e-18;
        private readonly double _hAngle = Math.Atan2(h.Y, h.X);

        /// <summary>
        /// Appends the support points of the nib at <paramref name="center"/> for directions from
        /// <paramref name="start"/> rotating by <paramref name="sweep"/>, including both ends of every
        /// jump of the segment side met on the way.
        /// </summary>
        public void Arc(List<PointD> poly, PointD center, double start, double sweep)
        {
            var end = start + sweep;
            var step = MaxStep();
            var n = Math.Abs(sweep) < _minSweep ? 0 : (int)Math.Ceiling(Math.Abs(sweep) / step);
            var angles = new List<double>(n + 3);
            for (var i = 0; i <= n; i++)
            {
                angles.Add(n == 0 ? start : start + (sweep * i / n));
            }

            var jumps = Jumps(Math.Min(start, end), Math.Max(start, end));
            angles.RemoveAll(a => IsJump(a, jumps));
            angles.AddRange(jumps);
            angles.Sort();
            if (sweep < 0)
            {
                angles.Reverse();
            }

            var direction = sweep < 0 ? -1 : 1;
            foreach (var a in angles)
            {
                if (!IsJump(a, jumps))
                {
                    Add(poly, Support(center, a, Math.Sign(Math.Cos(a - _hAngle))));
                    continue;
                }

                // Before and after the jump in the traversal order; at an end of the arc the value at
                // the jump itself (the before-side of increasing angles) stands in for the side outside it.
                var sin = Math.Sign(Math.Sin(a - _hAngle));
                var atJump = sin;
                var before = Math.Abs(a - start) < _angleEpsilon ? atJump : direction * sin;
                var after = Math.Abs(a - end) < _angleEpsilon ? atJump : -direction * sin;
                Add(poly, Support(center, a, before));
                Add(poly, Support(center, a, after));
            }
        }

        // Jump directions a = hAngle + pi/2 + k*pi within [lo, hi].
        private List<double> Jumps(double lo, double hi)
        {
            var result = new List<double>();
            if (!_hasSegment)
            {
                return result;
            }

            var first = _hAngle + (Math.PI / 2);
            var k = Math.Ceiling((lo - first - _angleEpsilon) / Math.PI);
            for (var a = first + (k * Math.PI); a <= hi + _angleEpsilon; a += Math.PI)
            {
                result.Add(Math.Clamp(a, lo, hi));
            }

            return result;
        }

        private static bool IsJump(double a, List<double> jumps) => jumps.Exists(j => Math.Abs(j - a) < _angleEpsilon);

        private PointD Support(PointD center, double a, int side) =>
            new(
                center.X + (side * h.X) + (rho * Math.Cos(a)),
                center.Y + (side * h.Y) + (rho * Math.Sin(a)));

        // Largest angular step whose chord stays within the sagitta budget.
        private double MaxStep() =>
            rho <= _maxSagitta ? _maxArcStep : Math.Min(_maxArcStep, 2 * Math.Acos(1 - (_maxSagitta / rho)));

        private static void Add(List<PointD> poly, PointD p)
        {
            if (poly.Count == 0 || PointD.Distance(poly[^1], p) >= 1e-9)
            {
                poly.Add(p);
            }
        }
    }
}
