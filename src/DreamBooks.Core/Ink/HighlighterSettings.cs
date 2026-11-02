using DreamBooks.Core.Model;

namespace DreamBooks.Core.Ink;

/// <summary>
/// The active highlighter tool settings (FR-41, FR-42, FR-44, FR-46): colour, round or chisel tip with its
/// fixed angle in degrees (0..180), straight-line mode, and the constant widths in page points.
/// </summary>
public sealed record HighlighterSettings(
    InkColor Color,
    bool Chisel,
    double ChiselAngleDeg,
    bool StraightLine,
    double RoundWidthPt,
    double ChiselLengthPt)
{
    /// <summary>Chisel tip thickness / length (FR-46).</summary>
    public const double ChiselRatio = 0.25;

    /// <summary>
    /// Yellow, round tip, 45° chisel angle, straight line off, 16 pt round, 22 pt chisel: one horizontal pass over a
    /// text line (15 pt) is a mark with either tip (FR-100); at 45° the chisel shows 22 × (0.25 + 0.75 sin 45°) ≈ 17.2 pt.
    /// </summary>
    public static HighlighterSettings Default { get; } = new(HighlighterPresets.Yellow, false, 45, false, 16, 22);

    /// <summary>The constant stroke width: the chisel length or the round width (FR-42).</summary>
    public double WidthPt => Chisel ? ChiselLengthPt : RoundWidthPt;

    /// <summary>The chisel nib of new strokes, or null for the round tip.</summary>
    public ChiselTip? Tip => Chisel ? new ChiselTip(ChiselAngleDeg, ChiselRatio) : null;

    /// <summary>The stroke colour: <see cref="Color"/> with the highlighter alpha.</summary>
    public InkColor StrokeColor => Color with { A = HighlighterPresets.Alpha };
}
