using System.Windows;
using System.Windows.Media;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>
/// What the lasso draws over a page (FR-50, FR-51): the loop being drawn (dashed), and the selection rectangle with
/// its four corner handles. The selected strokes themselves are drawn by the ink layers (a drag shows them moved
/// through the surface's display strokes). Everything is in page points scaled by <see cref="Scale"/>; colours come
/// from the theme. Not hit-testable: the surface handles the input.
/// </summary>
public sealed class SelectionLayer : FrameworkElement
{
    private IReadOnlyList<PointD> _loop = [];
    private RectD? _bounds;
    private double _scale = Units.DipPerPt;

    public SelectionLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>DIPs per page point.</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            if (_scale != value)
            {
                _scale = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>The loop in progress, in page points; empty hides it.</summary>
    internal IReadOnlyList<PointD> Loop => _loop;

    /// <summary>The selection rectangle in page points, or null.</summary>
    internal RectD? Bounds => _bounds;

    public void SetLoop(IReadOnlyList<PointD> loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        _loop = loop;
        InvalidateVisual();
    }

    public void SetBounds(RectD? bounds)
    {
        if (_bounds != bounds)
        {
            _bounds = bounds;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var outline = (Brush)(TryFindResource("SelectionOutline") ?? Brushes.DodgerBlue);
        var thickness = TryFindResource("SelectionOutlineThickness") is double t ? t : 1.5;
        var dashed = new Pen(outline, thickness) { DashStyle = new DashStyle([4, 3], 0), LineJoin = PenLineJoin.Round };
        var solid = new Pen(outline, thickness);

        if (_loop.Count >= 2)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(ToView(_loop[0]), isFilled: false, isClosed: false);
                ctx.PolyLineTo([.. _loop.Skip(1).Select(ToView)], isStroked: true, isSmoothJoin: true);
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, dashed, geometry);
        }

        if (_bounds is { } b)
        {
            var rect = new Rect(ToView(new PointD(b.Left, b.Top)), ToView(new PointD(b.Right, b.Bottom)));
            rect.Inflate(4, 4);
            var tint = TryFindResource("SelectionTint") as Brush;
            drawingContext.DrawRectangle(tint, dashed, rect);

            var size = TryFindResource("SelectionHandleSize") is double s ? s : 12;
            var fill = (Brush)(TryFindResource("SelectionHandleFill") ?? Brushes.White);
            foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft })
            {
                drawingContext.DrawEllipse(fill, solid, corner, size / 2, size / 2);
            }
        }
    }

    private Point ToView(PointD pagePt) => new(pagePt.X * _scale, pagePt.Y * _scale);
}
