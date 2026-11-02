using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Views;

/// <summary>
/// Small vector pictures of ink, shared by the marks-list thumbnails (FR-103) and the scroll rail's page preview
/// (UI-07): the outlines are built as the page's ink layer builds them (<see cref="StrokeGeometry.BuildOutline"/>), on
/// the thread pool; the frozen drawings are made on the UI thread and drawn at any scale with a minimum stroke width.
/// </summary>
internal static class InkDrawings
{
    /// <summary>
    /// Thread pool: each stroke's outline in page points, with its colour; highlighters first, with their own
    /// outline, so they lie under the pen ink as on the page (FR-40).
    /// </summary>
    public static List<(InkColor Color, IReadOnlyList<IReadOnlyList<PointD>> Figures)> BuildOutlines(
        IEnumerable<InkStroke> strokes, PressureCurve curve, SmoothingLevel smoothing)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        var outlines = new List<(InkColor, IReadOnlyList<IReadOnlyList<PointD>>)>();
        var pen = new List<InkStroke>();
        foreach (var stroke in strokes)
        {
            if (stroke.Tool == InkTool.Highlighter)
            {
                outlines.Add((stroke.Color, [HighlighterLayer.OutlineFor(stroke)]));
            }
            else
            {
                pen.Add(stroke);
            }
        }

        foreach (var stroke in pen)
        {
            outlines.Add((stroke.Color, StrokeGeometry.BuildOutline(stroke, curve, smoothing)));
        }

        return outlines;
    }

    /// <summary>UI thread: one frozen drawing per outline, filled with its colour (one frozen brush per colour).</summary>
    public static IReadOnlyList<GeometryDrawing> ToDrawings(
        IReadOnlyList<(InkColor Color, IReadOnlyList<IReadOnlyList<PointD>> Figures)> outlines)
    {
        ArgumentNullException.ThrowIfNull(outlines);
        var brushes = new Dictionary<InkColor, Brush>();
        var drawings = new List<GeometryDrawing>(outlines.Count);
        foreach (var (color, figures) in outlines)
        {
            if (!brushes.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(CustomInkLayer.ToColor(color));
                brush.Freeze();
                brushes[color] = brush;
            }

            var drawing = new GeometryDrawing(brush, null, OutlineGeometry.ToStreamGeometry(figures));
            drawing.Freeze();
            drawings.Add(drawing);
        }

        return drawings;
    }

    /// <summary>
    /// Draws <paramref name="strokes"/> (page points) under a transform of <paramref name="scale"/> already pushed by the
    /// caller, each with a pen of <paramref name="minimumDip"/> around its fill so that thin ink stays visible.
    /// </summary>
    public static void Draw(DrawingContext drawingContext, IReadOnlyList<GeometryDrawing> strokes, double scale, double minimumDip)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        ArgumentNullException.ThrowIfNull(strokes);
        var pens = new Dictionary<Brush, Pen>();
        foreach (var stroke in strokes)
        {
            if (stroke.Brush is not { } brush)
            {
                continue;
            }

            if (!pens.TryGetValue(brush, out var pen))
            {
                pen = RoundPen(brush, minimumDip / scale)!;
                pens[brush] = pen;
            }

            drawingContext.DrawGeometry(brush, pen, stroke.Geometry);
        }
    }

    /// <summary>A round-capped pen, frozen when it can be (a theme brush with a dynamic colour cannot).</summary>
    public static Pen? RoundPen(Brush? brush, double thickness)
    {
        if (brush is null)
        {
            return null;
        }

        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }
}
