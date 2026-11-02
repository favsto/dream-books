using System.Collections.Frozen;
using System.Windows;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>
/// The wet ink, drawn on WPF's dynamic-renderer thread as the points arrive: a disc at every point
/// plus the tangent quad to the previous one, with width from our pressure curve (FR-21, FR-22, FR-24).
/// It is deliberately unsmoothed; on pen-up the smoothed outline from Core replaces it (the "settle").
/// With the highlighter it draws the saved shape instead: constant width or the chisel outline, translucent as a whole
/// (FR-40, FR-42, FR-46, see <see cref="ConfigureHighlighter"/>).
/// It also refuses input which must never ink: touch (fingers, FR-30) and the pen's back end (Review Focus 3).
/// The filter runs on the real-time stylus thread, so it is effective even before the UI thread sees the
/// contact. The UI thread keeps the device sets current through <see cref="SetIgnoredDevices"/>.
/// </summary>
public sealed class CustomDynamicRenderer : DynamicRenderer
{
    private readonly Lock _gate = new();
    private volatile FrozenSet<int> _ignoredTablets = FrozenSet<int>.Empty;
    private volatile FrozenSet<int> _ignoredStyluses = FrozenSet<int>.Empty;
    private volatile WetSettings _settings = new(Brushes.Black, 1, 1, Units.DipPerPt, PressureCurve.Linear, true, false, null);
    private PointD _previous;
    private double _previousWidth;
    private bool _hasPrevious;

    /// <summary>Called on the UI thread: touch tablets and inverted (eraser) stylus cursors.</summary>
    public void SetIgnoredDevices(IReadOnlyCollection<int> touchTabletIds, IReadOnlyCollection<int> invertedStylusIds)
    {
        ArgumentNullException.ThrowIfNull(touchTabletIds);
        ArgumentNullException.ThrowIfNull(invertedStylusIds);
        if (!_ignoredTablets.SetEquals(touchTabletIds))
        {
            _ignoredTablets = touchTabletIds.ToFrozenSet();
        }

        if (!_ignoredStyluses.SetEquals(invertedStylusIds))
        {
            _ignoredStyluses = invertedStylusIds.ToFrozenSet();
        }
    }

    /// <summary>True for a touch tablet or a pen back end; the stylus-thread filter and tests use it.</summary>
    internal bool IsIgnoredDevice(int tabletId, int stylusId) =>
        _ignoredTablets.Contains(tabletId) || _ignoredStyluses.Contains(stylusId);

    /// <summary>The brush of the next wet points; exposed for tests.</summary>
    internal Brush WetBrush => _settings.Brush;

    /// <summary>The opacity of the whole wet ink (1 for the pen, the colour's alpha for the highlighter); for tests.</summary>
    internal double WetOpacity => _settings.Opacity;

    /// <summary>
    /// Pen: called on the UI thread before contact; takes effect for the next points drawn. The colour (with its
    /// alpha) is in the brush and the width follows the pressure curve (FR-20, FR-22).
    /// </summary>
    public void Configure(InkColor color, double baseWidthPt, double scale, PressureCurve curve, bool hasPressure)
    {
        var brush = new SolidColorBrush(CustomInkLayer.ToColor(color));
        brush.Freeze();
        Apply(new WetSettings(brush, 1, baseWidthPt, scale, curve, hasPressure, Highlighter: false, Chisel: null));
    }

    /// <summary>
    /// Highlighter: called on the UI thread before contact. The live stroke has the saved shape: constant width
    /// (FR-42) or the chisel outline (FR-46), pressure ignored. Its pieces are drawn opaque under one translucent
    /// group (the colour's alpha on <see cref="DynamicRenderer.RootVisual"/>), so the joins between updates do not
    /// darken; it stays translucent with Windows transparency effects off (NFR-32), or it would hide the text.
    /// </summary>
    public void ConfigureHighlighter(InkColor color, double widthPt, ChiselTip? chisel, double scale)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        Apply(new WetSettings(brush, color.A / 255.0, widthPt, scale, PressureCurve.Linear, false, Highlighter: true, chisel));
    }

    /// <summary>A new wet stroke starts: the next points are not joined to the previous ones. Tests drive it too.</summary>
    internal void BeginWetStroke()
    {
        lock (_gate)
        {
            _hasPrevious = false;
        }
    }

    /// <summary>
    /// The wet geometry of a batch of points (surface DIPs), joined to the last point of the previous batch, in the
    /// configured shape. Called by <see cref="OnDraw"/> on the renderer thread; separate so tests can drive it.
    /// </summary>
    internal Geometry BuildWetGeometry(IReadOnlyList<StylusPoint> stylusPoints) => BuildWet(stylusPoints, _settings);

    protected override void OnStylusDown(RawStylusInput rawStylusInput)
    {
        if (!IsIgnored(rawStylusInput))
        {
            BeginWetStroke();
            base.OnStylusDown(rawStylusInput);
        }
    }

    protected override void OnStylusMove(RawStylusInput rawStylusInput)
    {
        if (!IsIgnored(rawStylusInput))
        {
            base.OnStylusMove(rawStylusInput);
        }
    }

    protected override void OnStylusUp(RawStylusInput rawStylusInput)
    {
        if (!IsIgnored(rawStylusInput))
        {
            base.OnStylusUp(rawStylusInput);
        }
    }

    private bool IsIgnored(RawStylusInput rawStylusInput)
    {
        ArgumentNullException.ThrowIfNull(rawStylusInput);
        return IsIgnoredDevice(rawStylusInput.TabletDeviceId, rawStylusInput.StylusDeviceId);
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
        drawingContext.DrawGeometry(s.Brush, null, BuildWet(stylusPoints, s));
    }

    private StreamGeometry BuildWet(IReadOnlyList<StylusPoint> stylusPoints, WetSettings s)
    {
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
                widths.Add(s.Highlighter
                    ? s.BaseWidthPt * s.Scale
                    : PressureMapping.WidthAt(s.BaseWidthPt, pressure, s.Curve) * s.Scale);
            }

            _previous = centers[^1];
            _previousWidth = widths[^1];
            _hasPrevious = true;
        }

        if (!s.Highlighter)
        {
            return OutlineGeometry.ToStreamGeometry(StrokeOutline.Build(centers, widths));
        }

        // The saved shape, scaled: both outlines scale uniformly with the page (FR-26, FR-42, FR-46).
        var points = centers.Select(c => new InkPoint(c.X, c.Y, null, null, null, null, 0)).ToArray();
        return OutlineGeometry.ToStreamGeometry([HighlighterGeometry.Outline(points, s.BaseWidthPt * s.Scale, s.Chisel)]);
    }

    /// <summary>UI thread: the settings for the next points, and the opacity of the whole wet ink.</summary>
    private void Apply(WetSettings settings)
    {
        _settings = settings;
        if (RootVisual is ContainerVisual root && root.Opacity != settings.Opacity)
        {
            root.Opacity = settings.Opacity;
        }
    }

    private sealed record WetSettings(
        Brush Brush, double Opacity, double BaseWidthPt, double Scale, PressureCurve Curve, bool HasPressure, bool Highlighter, ChiselTip? Chisel);
}
