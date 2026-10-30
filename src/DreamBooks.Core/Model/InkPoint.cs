using DreamBooks.Core.Geometry;

namespace DreamBooks.Core.Model;

/// <summary>
/// One sampled ink point in PDF page points. A null optional value means the device did not report it.
/// </summary>
public readonly record struct InkPoint(
    double X,
    double Y,
    float? Pressure,
    float? TiltX,
    float? TiltY,
    float? TipAngle,
    long TimestampMs)
{
    public PointD Position => new(X, Y);
}
