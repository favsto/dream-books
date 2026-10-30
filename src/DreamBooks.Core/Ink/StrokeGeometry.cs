using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Ink;

/// <summary>Turns a recorded stroke into filled outline figures (FR-20, FR-22, FR-23).</summary>
public static class StrokeGeometry
{
    public static IReadOnlyList<IReadOnlyList<PointD>> BuildOutline(
        InkStroke stroke,
        PressureCurve curve,
        SmoothingLevel smoothing)
    {
        ArgumentNullException.ThrowIfNull(stroke);

        var smoothed = StrokeSmoother.Smooth(stroke.Points, smoothing);
        var centers = new PointD[smoothed.Count];
        var widths = new double[smoothed.Count];
        for (var i = 0; i < smoothed.Count; i++)
        {
            centers[i] = smoothed[i].Position;
            widths[i] = PressureMapping.WidthAt(stroke.BaseWidthPt, smoothed[i].Pressure, curve);
        }

        return StrokeOutline.BuildContour(centers, widths);
    }
}
