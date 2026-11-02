using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>
/// Committed pen ink: the pressure-shaped, smoothed outline from Core (FR-20, FR-22, FR-23), one retained visual per
/// stroke. Highlighter strokes are never drawn here: <see cref="HighlighterLayer"/> draws them underneath (FR-40).
/// </summary>
public sealed class CustomInkLayer : RetainedStrokeLayer
{
    private PressureCurve _curve = PressureCurve.Linear;
    private SmoothingLevel _smoothing = SmoothingLevel.Light;

    public PressureCurve Curve
    {
        get => _curve;
        set
        {
            if (_curve != value)
            {
                _curve = value;
                ScheduleRebuild();
            }
        }
    }

    public SmoothingLevel Smoothing
    {
        get => _smoothing;
        set
        {
            if (_smoothing != value)
            {
                _smoothing = value;
                ScheduleRebuild();
            }
        }
    }

    protected override bool Draws(InkStroke stroke) => stroke.Tool != InkTool.Highlighter;

    protected override IReadOnlyList<IReadOnlyList<PointD>> BuildFigures(InkStroke stroke) =>
        StrokeGeometry.BuildOutline(stroke, _curve, _smoothing);
}
