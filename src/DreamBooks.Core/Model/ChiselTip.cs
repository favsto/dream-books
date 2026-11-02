namespace DreamBooks.Core.Model;

/// <summary>Chisel nib of a highlighter stroke: fixed angle in degrees (0..180) and tip thickness / length ratio (0..1) (FR-46).</summary>
public readonly record struct ChiselTip
{
    public ChiselTip(double angleDeg, double ratio)
    {
        if (!(angleDeg >= 0 && angleDeg <= 180))
        {
            throw new ArgumentOutOfRangeException(nameof(angleDeg), angleDeg, "Chisel angle must be within 0..180 degrees.");
        }

        if (!(ratio >= 0 && ratio <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Chisel ratio must be within 0..1.");
        }

        AngleDeg = angleDeg;
        Ratio = ratio;
    }

    public double AngleDeg { get; }

    public double Ratio { get; }
}
