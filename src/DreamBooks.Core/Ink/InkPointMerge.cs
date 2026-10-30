using DreamBooks.Core.Model;

namespace DreamBooks.Core.Ink;

/// <summary>
/// Merges a point that landed on top of an already kept point (a pen pressing in place), so the
/// pressure ramp of a dot or press-in is not lost (FR-20, FR-23).
/// </summary>
internal static class InkPointMerge
{
    /// <summary>
    /// Keeps the position of <paramref name="kept"/>, the maximum non-null pressure and the latest
    /// timestamp. Tilt and tip angle come from the point with the larger pressure (the kept one on ties).
    /// </summary>
    public static InkPoint Merge(InkPoint kept, InkPoint next)
    {
        var useNext = next.Pressure is { } np && (kept.Pressure is not { } kp || np > kp);
        var source = useNext ? next : kept;
        return new InkPoint(
            kept.X,
            kept.Y,
            useNext ? next.Pressure : kept.Pressure,
            source.TiltX,
            source.TiltY,
            source.TipAngle,
            Math.Max(kept.TimestampMs, next.TimestampMs));
    }
}
