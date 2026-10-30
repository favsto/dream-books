namespace DreamBooks.Core.Geometry;

/// <summary>A point in PDF page points (1/72 inch), origin top-left, y down.</summary>
public readonly record struct PointD(double X, double Y)
{
    public static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
