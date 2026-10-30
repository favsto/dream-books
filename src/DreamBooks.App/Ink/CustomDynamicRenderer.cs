using System.Windows;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>
/// Mode B wet ink, drawn on WPF's dynamic-renderer thread as the points arrive: a disc at every point
/// plus the tangent quad to the previous one, with width from our pressure curve (FR-21, FR-22, FR-24).
/// It is deliberately unsmoothed; on pen-up the smoothed outline from Core replaces it (the "settle").
/// </summary>
public sealed class CustomDynamicRenderer : PenDynamicRenderer
{
    private readonly Lock _gate = new();
    private volatile WetSettings _settings = new(Brushes.Black, 1, Units.DipPerPt, PressureCurve.Linear, true);
    private PointD _previous;
    private double _previousWidth;
    private bool _hasPrevious;

    /// <summary>Called on the UI thread before contact; takes effect for the next points drawn.</summary>
    public void Configure(InkColor color, double baseWidthPt, double scale, PressureCurve curve, bool hasPressure)
    {
        var brush = new SolidColorBrush(StockStrokeConverter.ToColor(color));
        brush.Freeze();
        _settings = new WetSettings(brush, baseWidthPt, scale, curve, hasPressure);
    }

    protected override void OnStylusDown(RawStylusInput rawStylusInput)
    {
        if (!IsIgnored(rawStylusInput))
        {
            lock (_gate)
            {
                _hasPrevious = false;
            }
        }

        base.OnStylusDown(rawStylusInput);
    }

    protected override void OnDraw(
        DrawingContext drawingContext, StylusPointCollection stylusPoints, Geometry geometry, Brush fillBrush)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (stylusPoints is null || stylusPoints.Count == 0)
        {
            return;
        }

        var s = _settings;
        var centers = new List<PointD>(stylusPoints.Count + 1);
        var widths = new List<double>(stylusPoints.Count + 1);
        lock (_gate)
        {
            if (_hasPrevious)
            {
                centers.Add(_previous);
                widths.Add(_previousWidth);
            }

            foreach (var sp in stylusPoints)
            {
                float? pressure = s.HasPressure ? sp.PressureFactor : null;
                centers.Add(new PointD(sp.X, sp.Y));
                widths.Add(PressureMapping.WidthAt(s.BaseWidthPt, pressure, s.Curve) * s.Scale);
            }

            _previous = centers[^1];
            _previousWidth = widths[^1];
            _hasPrevious = true;
        }

        drawingContext.DrawGeometry(s.Brush, null, OutlineGeometry.ToStreamGeometry(StrokeOutline.Build(centers, widths)));
    }

    private sealed record WetSettings(Brush Brush, double BaseWidthPt, double Scale, PressureCurve Curve, bool HasPressure);
}
