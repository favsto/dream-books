namespace DreamBooks.Core.Geometry;

/// <summary>An axis-aligned rectangle in page points.</summary>
public readonly record struct RectD(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public RectD Union(RectD other)
        => new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public bool Contains(PointD p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;
}
