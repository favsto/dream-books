using System.Windows;
using System.Windows.Media;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>
/// Mode B committed strokes: one frozen outline geometry per stroke, in page points, cached by stroke Id
/// and drawn under a scale transform, so zooming only changes the transform (FR-26, NFR-07).
/// </summary>
public sealed class CustomInkLayer : FrameworkElement
{
    private readonly Dictionary<Guid, StreamGeometry> _geometries = [];
    private readonly Dictionary<InkColor, Brush> _brushes = [];
    private readonly ScaleTransform _transform = new(Units.DipPerPt, Units.DipPerPt);
    private IReadOnlyList<InkStroke> _strokes = [];
    private PressureCurve _curve = PressureCurve.Linear;
    private SmoothingLevel _smoothing = SmoothingLevel.Light;

    public CustomInkLayer() => IsHitTestVisible = false;

    public IReadOnlyList<InkStroke> Strokes
    {
        get => _strokes;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _strokes = value;
            PruneCache();
            InvalidateVisual();
        }
    }

    /// <summary>DIPs per page point (zoom × DipPerPt).</summary>
    public double Scale
    {
        get => _transform.ScaleX;
        set
        {
            _transform.ScaleX = value;
            _transform.ScaleY = value;
        }
    }

    public PressureCurve Curve
    {
        get => _curve;
        set
        {
            if (_curve != value)
            {
                _curve = value;
                ResetCache();
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
                ResetCache();
            }
        }
    }

    /// <summary>Number of cached outlines; exposed for tests.</summary>
    internal int CachedCount => _geometries.Count;

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_strokes.Count == 0)
        {
            return;
        }

        drawingContext.PushTransform(_transform);
        foreach (var stroke in _strokes)
        {
            drawingContext.DrawGeometry(BrushFor(stroke.Color), null, GeometryFor(stroke));
        }

        drawingContext.Pop();
    }

    private StreamGeometry GeometryFor(InkStroke stroke)
    {
        if (!_geometries.TryGetValue(stroke.Id, out var geometry))
        {
            geometry = OutlineGeometry.ToStreamGeometry(StrokeGeometry.BuildOutline(stroke, _curve, _smoothing));
            _geometries[stroke.Id] = geometry;
        }

        return geometry;
    }

    private Brush BrushFor(InkColor color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(StockStrokeConverter.ToColor(color));
            brush.Freeze();
            _brushes[color] = brush;
        }

        return brush;
    }

    private void ResetCache()
    {
        _geometries.Clear();
        InvalidateVisual();
    }

    private void PruneCache()
    {
        if (_geometries.Count == 0)
        {
            return;
        }

        var alive = _strokes.Select(s => s.Id).ToHashSet();
        foreach (var id in _geometries.Keys.Where(id => !alive.Contains(id)).ToArray())
        {
            _geometries.Remove(id);
        }
    }
}
