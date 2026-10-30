using DreamBooks.Core.Geometry;

namespace DreamBooks.Core.Ink;

/// <summary>
/// Builds a variable-width stroke outline as a set of closed figures to be filled together with the
/// non-zero rule: a disc at every center plus, for each consecutive pair, the quadrilateral between the
/// two discs' external tangent points. Every figure has positive shoelace area (y down), so the fill is
/// a pure union. Coordinates are in PDF page points (FR-20, FR-22, FR-26).
/// </summary>
public static class StrokeOutline
{
    private const double _minWidth = 0.01;
    private const double _discSegmentLength = 0.5;
    private const int _minDiscVertices = 8;
    private const int _maxDiscVertices = 48;
    private const double _minArea = 1e-9;

    /// <param name="centers">Stroke centerline points.</param>
    /// <param name="widths">Full diameter at each center; values &lt;= 0 are clamped to a tiny width.</param>
    public static IReadOnlyList<IReadOnlyList<PointD>> Build(IReadOnlyList<PointD> centers, IReadOnlyList<double> widths)
    {
        ArgumentNullException.ThrowIfNull(centers);
        ArgumentNullException.ThrowIfNull(widths);
        if (centers.Count != widths.Count)
        {
            throw new ArgumentException("Centers and widths must have the same length.", nameof(widths));
        }

        var figures = new List<IReadOnlyList<PointD>>();
        var radii = new double[centers.Count];
        for (var i = 0; i < centers.Count; i++)
        {
            var w = double.IsFinite(widths[i]) ? widths[i] : _minWidth;
            radii[i] = Math.Max(w, _minWidth) / 2;
            figures.Add(Disc(centers[i], radii[i]));
        }

        for (var i = 0; i + 1 < centers.Count; i++)
        {
            var quad = Quad(centers[i], radii[i], centers[i + 1], radii[i + 1]);
            if (quad is not null)
            {
                figures.Add(quad);
            }
        }

        return figures;
    }

    private static PointD[] Disc(PointD c, double r)
    {
        var n = (int)Math.Clamp(Math.Ceiling(2 * Math.PI * r / _discSegmentLength), _minDiscVertices, _maxDiscVertices);
        var pts = new PointD[n];
        for (var i = 0; i < n; i++)
        {
            var a = 2 * Math.PI * i / n;
            pts[i] = new PointD(c.X + (r * Math.Cos(a)), c.Y + (r * Math.Sin(a)));
        }

        return pts;
    }

    private static PointD[]? Quad(PointD c1, double r1, PointD c2, double r2)
    {
        var dx = c2.X - c1.X;
        var dy = c2.Y - c1.Y;
        var d = Math.Sqrt((dx * dx) + (dy * dy));
        if (d <= 0 || d + Math.Min(r1, r2) <= Math.Max(r1, r2))
        {
            return null;
        }

        var ux = dx / d;
        var uy = dy / d;
        var k = Math.Clamp((r1 - r2) / d, -1, 1);
        var s = Math.Sqrt(1 - (k * k));

        // Unit normals of the two external tangent lines.
        var n1x = (ux * k) - (uy * s);
        var n1y = (uy * k) + (ux * s);
        var n2x = (ux * k) + (uy * s);
        var n2y = (uy * k) - (ux * s);

        PointD[] quad =
        [
            new(c1.X + (r1 * n1x), c1.Y + (r1 * n1y)),
            new(c2.X + (r2 * n1x), c2.Y + (r2 * n1y)),
            new(c2.X + (r2 * n2x), c2.Y + (r2 * n2y)),
            new(c1.X + (r1 * n2x), c1.Y + (r1 * n2y)),
        ];

        var area = SignedArea(quad);
        if (Math.Abs(area) < _minArea)
        {
            return null;
        }

        if (area < 0)
        {
            Array.Reverse(quad);
        }

        return quad;
    }

    private static double SignedArea(PointD[] poly)
    {
        double sum = 0;
        for (var i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum / 2;
    }
}
