using DreamBooks.Core.Geometry;

namespace DreamBooks.Core.Ink;

/// <summary>
/// Builds a variable-width stroke as one closed polygon whose non-zero fill is exactly the union of
/// the discs at the centers and the hulls between consecutive discs (FR-20, FR-22, FR-23).
/// </summary>
/// <remarks>
/// For every step i the convex hull H(i) of discs i and i+1 is bounded by two tangent edges and two
/// arcs. The polygon is the chain sum of all hull boundaries minus the full circles of the interior
/// discs, rearranged into one loop: start cap, forward side, end cap, back side. At an interior
/// center the two leftover arcs become one signed rotation per side, positive on the outer side of a
/// turn (a visible round join) and negative on the inner side (hidden under the fill). The non-zero
/// winding number at any point is then (hulls containing it) − (interior discs containing it), which
/// is at least 1 inside the union and 0 outside, so tight loops, reversals and width jumps leave no
/// holes. Visible arcs are kept exactly; everything else is simplified with Douglas–Peucker.
/// </remarks>
internal static class StrokeContour
{
    private const double _minWidth = 0.01;
    private const double _minSpacing = 0.01;
    private const double _maxSagitta = 0.02;
    private const double _maxArcStep = Math.PI / 8;
    private const double _minSweep = 1e-9;
    private const double _relativeTolerance = 0.02;

    public static IReadOnlyList<IReadOnlyList<PointD>> Build(
        IReadOnlyList<PointD> centers, IReadOnlyList<double> widths, double tolerancePt)
    {
        ArgumentNullException.ThrowIfNull(centers);
        ArgumentNullException.ThrowIfNull(widths);
        if (centers.Count != widths.Count)
        {
            throw new ArgumentException("Centers and widths must have the same length.", nameof(widths));
        }

        if (!(tolerancePt >= 0) || double.IsPositiveInfinity(tolerancePt))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerancePt), "Tolerance must be finite and not negative.");
        }

        if (centers.Count == 0)
        {
            return [];
        }

        var (c, r) = Merge(centers, widths);
        var poly = new Polygon();
        if (c.Count == 1)
        {
            poly.FullDisc(c[0], r[0]);
            return [poly.Vertices.ToArray()];
        }

        var m = c.Count - 1;
        var dir = new double[m];
        var asinK = new double[m];
        for (var i = 0; i < m; i++)
        {
            var dx = c[i + 1].X - c[i].X;
            var dy = c[i + 1].Y - c[i].Y;
            var d = Math.Sqrt((dx * dx) + (dy * dy));
            dir[i] = Math.Atan2(dy, dx);
            asinK[i] = Math.Asin(Math.Clamp((r[i] - r[i + 1]) / d, -1, 1));
        }

        // Tangent-point angles of step i: forward side dir - pi/2 + asin(k), back side dir + pi/2 - asin(k).
        double Forward(int i) => dir[i] - (Math.PI / 2) + asinK[i];
        double Back(int i) => dir[i] + (Math.PI / 2) - asinK[i];

        var turn = new double[m];
        for (var j = 1; j < m; j++)
        {
            turn[j] = Math.IEEERemainder(dir[j] - dir[j - 1], 2 * Math.PI);
        }

        // Start cap: back arc of H(0) on disc 0.
        poly.Arc(c[0], r[0], Back(0), Math.PI + (2 * asinK[0]));
        for (var j = 1; j < m; j++)
        {
            poly.Arc(c[j], r[j], Forward(j - 1), turn[j] + asinK[j] - asinK[j - 1]);
        }

        // End cap: front arc of H(m-1) on disc m.
        poly.Arc(c[m], r[m], Forward(m - 1), Math.PI - (2 * asinK[m - 1]));
        for (var j = m - 1; j >= 1; j--)
        {
            poly.Arc(c[j], r[j], Back(j), -turn[j] + asinK[j] - asinK[j - 1]);
        }

        // The error the eye sees is relative to the stroke width, so thin strokes get a finer tolerance;
        // the quarter of the thinnest radius also keeps every center well inside the simplified contour.
        var tolerance = Math.Min(tolerancePt, Math.Min(_relativeTolerance * r.Average(), 0.25 * r.Min()));
        return [poly.Simplify(tolerance)];
    }

    // Drops centers closer than the minimum spacing to the previous kept one, keeping the larger radius.
    private static (List<PointD> Centers, List<double> Radii) Merge(IReadOnlyList<PointD> centers, IReadOnlyList<double> widths)
    {
        var c = new List<PointD>(centers.Count);
        var r = new List<double>(centers.Count);
        for (var i = 0; i < centers.Count; i++)
        {
            var w = double.IsFinite(widths[i]) ? widths[i] : _minWidth;
            var radius = Math.Max(w, _minWidth) / 2;
            if (c.Count > 0 && PointD.Distance(c[^1], centers[i]) < _minSpacing)
            {
                r[^1] = Math.Max(r[^1], radius);
                continue;
            }

            c.Add(centers[i]);
            r.Add(radius);
        }

        return (c, r);
    }

    private sealed class Polygon
    {
        private readonly List<bool> _locked = [];

        public List<PointD> Vertices { get; } = [];

        public void FullDisc(PointD center, double radius)
        {
            var n = (int)Math.Ceiling(2 * Math.PI / MaxStep(radius));
            var rv = MeanRadius(radius, 2 * Math.PI / n);
            for (var i = 0; i < n; i++)
            {
                Add(At(center, rv, 2 * Math.PI * i / n), locked: true);
            }
        }

        /// <summary>
        /// Appends an arc on the disc from <paramref name="start"/> rotating by <paramref name="sweep"/>
        /// (positive increases the angle, which is the positive winding with y down). The end points are
        /// exact tangent points; interior points of positive arcs sit at the mean radius and are locked.
        /// </summary>
        public void Arc(PointD center, double radius, double start, double sweep)
        {
            var n = Math.Abs(sweep) < _minSweep ? 1 : (int)Math.Ceiling(Math.Abs(sweep) / MaxStep(radius));
            var visible = sweep > 0 && n > 1;
            Add(At(center, radius, start), locked: visible);
            if (Math.Abs(sweep) < _minSweep)
            {
                return;
            }

            var step = sweep / n;
            var rv = visible ? MeanRadius(radius, step) : radius;
            for (var i = 1; i < n; i++)
            {
                Add(At(center, rv, start + (step * i)), locked: visible);
            }

            Add(At(center, radius, start + sweep), locked: visible);
        }

        public PointD[] Simplify(double tolerance)
        {
            var n = Vertices.Count;
            if (Vertices.Count > 1 && PointD.Distance(Vertices[0], Vertices[^1]) < 1e-9)
            {
                _locked[0] |= _locked[^1];
                Vertices.RemoveAt(n - 1);
                _locked.RemoveAt(n - 1);
                n--;
            }

            if (tolerance <= 0 || n < 4)
            {
                return [.. Vertices];
            }

            var keep = _locked.ToArray();
            var anchors = Enumerable.Range(0, n).Where(i => keep[i]).ToList();
            if (anchors.Count == 0)
            {
                anchors.Add(0);
            }

            if (anchors.Count == 1)
            {
                var far = Enumerable.Range(0, n).MaxBy(i => PointD.Distance(Vertices[i], Vertices[anchors[0]]));
                anchors.Add(far);
                anchors.Sort();
            }

            foreach (var a in anchors)
            {
                keep[a] = true;
            }

            for (var k = 0; k < anchors.Count; k++)
            {
                var from = anchors[k];
                var to = k + 1 < anchors.Count ? anchors[k + 1] : anchors[0] + n;
                DouglasPeucker(from, to, tolerance, keep);
            }

            var result = new List<PointD>(n);
            for (var i = 0; i < n; i++)
            {
                if (keep[i])
                {
                    result.Add(Vertices[i]);
                }
            }

            return result.Count >= 3 ? [.. result] : [.. Vertices];
        }

        // Iterative Douglas-Peucker over the cyclic index range [from, to]; marks survivors in keep.
        private void DouglasPeucker(int from, int to, double tolerance, bool[] keep)
        {
            var n = Vertices.Count;
            var stack = new Stack<(int From, int To)>();
            stack.Push((from, to));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                if (b - a < 2)
                {
                    continue;
                }

                var pa = Vertices[a % n];
                var pb = Vertices[b % n];
                var worst = -1;
                var worstDist = tolerance;
                for (var i = a + 1; i < b; i++)
                {
                    var dist = DistanceToSegment(Vertices[i % n], pa, pb);
                    if (dist > worstDist)
                    {
                        worstDist = dist;
                        worst = i;
                    }
                }

                if (worst < 0)
                {
                    continue;
                }

                keep[worst % n] = true;
                stack.Push((a, worst));
                stack.Push((worst, b));
            }
        }

        private void Add(PointD p, bool locked)
        {
            if (Vertices.Count > 0 && PointD.Distance(Vertices[^1], p) < 1e-9)
            {
                _locked[^1] |= locked;
                return;
            }

            Vertices.Add(p);
            _locked.Add(locked);
        }

        // Largest angular step whose chord stays within the sagitta budget.
        private static double MaxStep(double radius) =>
            radius <= _maxSagitta ? _maxArcStep : Math.Min(_maxArcStep, 2 * Math.Acos(1 - (_maxSagitta / radius)));

        // Radius that puts the chords half inside and half outside the true circle.
        private static double MeanRadius(double radius, double step) => 2 * radius / (1 + Math.Cos(step / 2));

        private static PointD At(PointD center, double radius, double angle) =>
            new(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)));

        private static double DistanceToSegment(PointD q, PointD a, PointD b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len2 = (dx * dx) + (dy * dy);
            var t = len2 > 0 ? Math.Clamp((((q.X - a.X) * dx) + ((q.Y - a.Y) * dy)) / len2, 0, 1) : 0;
            return PointD.Distance(q, new PointD(a.X + (t * dx), a.Y + (t * dy)));
        }
    }
}
