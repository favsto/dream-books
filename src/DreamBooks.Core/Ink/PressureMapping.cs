namespace DreamBooks.Core.Ink;

/// <summary>Maps pen pressure to stroke width (FR-21, FR-22).</summary>
public static class PressureMapping
{
    public const double MinFactor = 0.3;
    public const double MaxFactor = 1.5;

    /// <summary>Applies the curve to a pressure clamped to 0-1; the result is in 0-1.</summary>
    public static double Apply(PressureCurve curve, float pressure)
    {
        double p = Math.Clamp(pressure, 0f, 1f);
        return curve switch
        {
            PressureCurve.Soft => Math.Pow(p, 0.6),
            PressureCurve.Firm => Math.Pow(p, 1.7),
            _ => p,
        };
    }

    /// <summary>Width in points at the given pressure; a missing pressure yields the base width.</summary>
    public static double WidthAt(double baseWidthPt, float? pressure, PressureCurve curve)
    {
        if (pressure is null)
        {
            return baseWidthPt;
        }

        return baseWidthPt * (MinFactor + ((MaxFactor - MinFactor) * Apply(curve, pressure.Value)));
    }
}
