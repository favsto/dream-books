using DreamBooks.Core.Geometry;

namespace DreamBooks.Core.Tests.Ink;

/// <summary>
/// Deterministic point sampling of figures filled together with the non-zero rule: winding numbers,
/// scanline rasterisation on a regular grid, signed areas and distances to edges.
/// </summary>
internal static class PolygonRaster
{
    public static double SignedArea(IReadOnlyList<PointD> poly)
    {
        double sum = 0;
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum / 2;
    }

    /// <summary>Sum of the winding numbers of all figures around <paramref name="q"/>.</summary>
    public static int Winding(IReadOnlyList<IReadOnlyList<PointD>> figures, PointD q)
    {
        var w = 0;
        foreach (var f in figures)
        {
            for (var i = 0; i < f.Count; i++)
            {
                var a = f[i];
                var b = f[(i + 1) % f.Count];
                if (a.Y <= q.Y)
                {
                    if (b.Y > q.Y && Cross(a, b, q) > 0)
                    {
                        w++;
                    }
                }
                else if (b.Y <= q.Y && Cross(a, b, q) < 0)
                {
                    w--;
                }
            }
        }

        return w;
    }

    /// <summary>
    /// Samples cell centres (<paramref name="minX"/> + (i + 0.5) * step, ...) and returns true where the
    /// non-zero winding number is not zero.
    /// </summary>
    public static bool[,] Rasterize(
        IReadOnlyList<IReadOnlyList<PointD>> figures, double minX, double minY, double step, int nx, int ny)
    {
        var inside = new bool[nx, ny];
        var crossings = new List<(double X, int Dir)>();
        for (var j = 0; j < ny; j++)
        {
            var y = minY + ((j + 0.5) * step);
            crossings.Clear();
            foreach (var f in figures)
            {
                for (var i = 0; i < f.Count; i++)
                {
                    var a = f[i];
                    var b = f[(i + 1) % f.Count];
                    var up = a.Y <= y && b.Y > y;
                    var down = b.Y <= y && a.Y > y;
                    if (!up && !down)
                    {
                        continue;
                    }

                    var x = a.X + ((y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                    crossings.Add((x, up ? 1 : -1));
                }
            }

            crossings.Sort((p, q) => p.X.CompareTo(q.X));
            var winding = 0;
            var k = 0;
            for (var i = 0; i < nx; i++)
            {
                var x = minX + ((i + 0.5) * step);
                while (k < crossings.Count && crossings[k].X < x)
                {
                    winding += crossings[k].Dir;
                    k++;
                }

                inside[i, j] = winding != 0;
            }
        }

        return inside;
    }

    public static (double MinX, double MaxX, double MinY, double MaxY) Bounds(IReadOnlyList<IReadOnlyList<PointD>> figures)
    {
        var all = figures.SelectMany(f => f).ToList();
        return (all.Min(p => p.X), all.Max(p => p.X), all.Min(p => p.Y), all.Max(p => p.Y));
    }

    /// <summary>Distance from <paramref name="q"/> to the nearest edge of the closed polygon.</summary>
    public static double DistanceToBoundary(IReadOnlyList<PointD> poly, PointD q)
    {
        var best = double.MaxValue;
        for (var i = 0; i < poly.Count; i++)
        {
            best = Math.Min(best, DistanceToSegment(q, poly[i], poly[(i + 1) % poly.Count]));
        }

        return best;
    }

    private static double DistanceToSegment(PointD q, PointD a, PointD b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = (dx * dx) + (dy * dy);
        var t = len2 > 0 ? Math.Clamp((((q.X - a.X) * dx) + ((q.Y - a.Y) * dy)) / len2, 0, 1) : 0;
        return PointD.Distance(q, new PointD(a.X + (t * dx), a.Y + (t * dy)));
    }

    // Positive when q is on the side of a->b that counts as an upward crossing for the winding number.
    private static double Cross(PointD a, PointD b, PointD q) =>
        ((b.X - a.X) * (q.Y - a.Y)) - ((q.X - a.X) * (b.Y - a.Y));
}
