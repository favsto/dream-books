using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>
/// Committed strokes of one kind: one retained <see cref="DrawingVisual"/> per stroke, in page points, diffed against
/// <see cref="Strokes"/> by stroke Id so a commit adds one visual and an erase touches only the affected ones.
/// Outlines are cached by stroke Id; zooming changes only the layer's single scale transform (FR-26, NFR-04, NFR-06).
/// <see cref="Strokes"/> is the page's whole list; each layer draws only the strokes it <see cref="Draws"/>.
/// View-only: not hit-testable, no automation peer.
/// </summary>
public abstract class RetainedStrokeLayer : FrameworkElement
{
    private readonly Dictionary<Guid, StreamGeometry> _geometries = [];
    private readonly Dictionary<Guid, DrawingVisual> _visuals = [];
    private readonly Dictionary<InkColor, Brush> _brushes = [];
    private readonly VisualCollection _children;
    private readonly ScaleTransform _transform = new(Units.DipPerPt, Units.DipPerPt);
    private IReadOnlyList<InkStroke> _strokes = [];
    private bool _rebuildPending;

    protected RetainedStrokeLayer()
    {
        _children = new VisualCollection(this);
        IsHitTestVisible = false;
        Focusable = false;
        RenderTransform = _transform;
    }

    /// <summary>The page's strokes (all kinds); only those this layer <see cref="Draws"/> get a visual.</summary>
    public IReadOnlyList<InkStroke> Strokes
    {
        get => _strokes;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _strokes = value;
            Sync();
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

    /// <summary>Number of cached outlines; exposed for tests.</summary>
    internal int CachedCount => _geometries.Count;

    protected override int VisualChildrenCount => _children.Count;

    internal static Color ToColor(InkColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    protected override Visual GetVisualChild(int index) => _children[index];

    /// <summary>True for the strokes this layer draws.</summary>
    protected abstract bool Draws(InkStroke stroke);

    /// <summary>The stroke's filled outline figures in page points.</summary>
    protected abstract IReadOnlyList<IReadOnlyList<PointD>> BuildFigures(InkStroke stroke);

    /// <summary>The outline settings changed (usually several together): drop the cache now, rebuild all visuals once.</summary>
    protected void ScheduleRebuild()
    {
        _geometries.Clear();
        if (_rebuildPending)
        {
            return;
        }

        _rebuildPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (_rebuildPending)
            {
                Sync();
            }
        });
    }

    private void Sync()
    {
        if (_rebuildPending)
        {
            _rebuildPending = false;
            _children.Clear();
            _visuals.Clear();
        }

        var desired = new List<DrawingVisual>(_strokes.Count);
        var alive = new HashSet<Guid>();
        foreach (var stroke in _strokes)
        {
            if (!Draws(stroke) || !alive.Add(stroke.Id))
            {
                continue; // another layer's stroke, or the same stroke twice (drawn once)
            }

            if (!_visuals.TryGetValue(stroke.Id, out var visual))
            {
                visual = CreateVisual(stroke);
                _visuals[stroke.Id] = visual;
            }

            desired.Add(visual);
        }

        foreach (var id in _visuals.Keys.Where(id => !alive.Contains(id)).ToArray())
        {
            _children.Remove(_visuals[id]);
            _visuals.Remove(id);
        }

        foreach (var id in _geometries.Keys.Where(id => !alive.Contains(id)).ToArray())
        {
            _geometries.Remove(id);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _children.Count && ReferenceEquals(_children[i], desired[i]))
            {
                continue;
            }

            var at = _children.IndexOf(desired[i]);
            if (at >= 0)
            {
                _children.RemoveAt(at);
            }

            _children.Insert(i, desired[i]);
        }
    }

    private DrawingVisual CreateVisual(InkStroke stroke)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawGeometry(BrushFor(stroke.Color), null, GeometryFor(stroke));
        }

        return visual;
    }

    private StreamGeometry GeometryFor(InkStroke stroke)
    {
        if (!_geometries.TryGetValue(stroke.Id, out var geometry))
        {
            geometry = OutlineGeometry.ToStreamGeometry(BuildFigures(stroke));
            _geometries[stroke.Id] = geometry;
        }

        return geometry;
    }

    private Brush BrushFor(InkColor color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(ToColor(color));
            brush.Freeze();
            _brushes[color] = brush;
        }

        return brush;
    }
}
