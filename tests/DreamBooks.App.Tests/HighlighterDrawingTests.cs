using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// Highlighters on the page surface (FR-40, FR-42, FR-46): a retained layer under the pen ink (above the bitmap and the
/// search highlights), the saved outline at the page scale (FR-26), the live stroke with the same shape, the same pen
/// and eraser rules as the pen (FR-30), not hit-testable. How it looks is checked on the device.
/// </summary>
public sealed class HighlighterDrawingTests : IDisposable
{
    private const double _tolerance = 1e-6;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-hl-draw-" + Guid.NewGuid().ToString("N"));

    public HighlighterDrawingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Highlighter_layer_sits_above_the_bitmap_and_search_highlights_and_below_the_pen_ink()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 1);
            var children = Children(surface);
            var bitmapAt = children.IndexOf(surface.BitmapLayer);
            var searchAt = children.IndexOf(surface.HighlightLayer);
            var highlighterAt = children.IndexOf(surface.HighlighterInkLayer);
            var penAt = children.IndexOf(surface.CustomLayer);
            Assert.True(
                bitmapAt >= 0 && bitmapAt < searchAt && searchAt < highlighterAt && highlighterAt < penAt,
                $"{bitmapAt} < {searchAt} < {highlighterAt} < {penAt}");
            Assert.Equal(surface.CustomLayer.RenderSize, surface.HighlighterInkLayer.RenderSize);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task Saved_round_highlighter_outline_on_screen_is_the_page_point_outline_times_the_scale(double zoom) // FR-26, FR-42
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            Draw(surface, (20, 30, 0.2f), (90, 34, 0.9f), (150, 40, 0.5f));

            var stroke = Assert.Single(surface.Editor!.Page.Strokes);
            Assert.Equal(InkTool.Highlighter, stroke.Tool);
            var expected = HighlighterGeometry.ConstantWidthOutline(stroke.Points, stroke.BaseWidthPt);
            AssertDrawnOutline(surface, surface.HighlighterInkLayer, expected, stroke.Color);
            Assert.Equal(0, PenDrawingCount(surface)); // never in the pen layer
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task Saved_chisel_highlighter_outline_on_screen_is_the_chisel_outline_times_the_scale(double zoom) // FR-26, FR-46
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            vm.Tools.Highlighter.IsChisel = true;
            vm.Tools.Highlighter.ChiselAngleDeg = 30;
            Draw(surface, (20, 30, 0.2f), (60, 80, 0.9f), (150, 60, 0.5f));

            var stroke = Assert.Single(surface.Editor!.Page.Strokes);
            var chisel = Assert.IsType<ChiselTip>(stroke.Chisel);
            Assert.Equal(30, chisel.AngleDeg);
            var expected = HighlighterGeometry.ChiselOutline(stroke.Points, chisel, stroke.BaseWidthPt);
            AssertDrawnOutline(surface, surface.HighlighterInkLayer, expected, stroke.Color);
        });
    }

    [Fact]
    public async Task Pen_still_inks_above_a_highlighter_and_its_saved_shape_is_unchanged() // FR-20, FR-40
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 1);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            Draw(surface, (20, 30, 0.5f), (150, 40, 0.5f));
            vm.Tools.ActiveTool = AppTool.Pen;
            Assert.True(surface.ActiveRenderer.Enabled);
            Draw(surface, (20, 30, 0.2f), (60, 36, 0.6f), (150, 40, 0.9f));

            var strokes = surface.Editor!.Page.Strokes;
            Assert.Equal(2, strokes.Count);
            var pen = strokes.Single(s => s.Tool == InkTool.Pen);
            var highlighter = strokes.Single(s => s.Tool == InkTool.Highlighter);
            Assert.Equal(1, PenDrawingCount(surface));
            Assert.Single(Drawings(surface.HighlighterInkLayer));

            // The pen's saved outline is the pressure-shaped one from Core, exactly as before (regression).
            var expected = StrokeGeometry.BuildOutline(pen, surface.Curve, surface.Smoothing);
            var drawing = Assert.Single(Drawings(surface.CustomLayer));
            AssertFigures(expected, ToSurface(surface, surface.CustomLayer, drawing.Geometry), surface.Scale);
            Assert.Equal(CustomInkLayer.ToColor(pen.Color), Assert.IsType<SolidColorBrush>(drawing.Brush).Color);
            Assert.NotEqual(pen.Id, highlighter.Id);
        });
    }

    [Fact]
    public async Task Highlighter_drawing_is_translucent_with_the_stroke_alpha() // FR-40
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 1);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            Draw(surface, (20, 30, 0.5f), (150, 40, 0.5f));

            var drawing = Assert.Single(Drawings(surface.HighlighterInkLayer));
            var brush = Assert.IsType<SolidColorBrush>(drawing.Brush);
            Assert.Equal(HighlighterPresets.Alpha, brush.Color.A);
            Assert.Equal(1, brush.Opacity);
            Assert.Equal(1, surface.HighlighterInkLayer.Opacity);
        });
    }

    [Fact]
    public async Task Highlighter_layer_is_not_hit_testable_and_the_pen_reaches_the_page_over_a_highlight()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 1);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            Draw(surface, (20, 30, 0.5f), (150, 40, 0.5f));
            LayOut(surface);

            var layer = surface.HighlighterInkLayer;
            Assert.False(layer.IsHitTestVisible);
            Assert.False(layer.Focusable);
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(layer));
            var onHighlight = new Point(80 * surface.Scale, 35 * surface.Scale);
            var hit = ScrollRailTests.HitAt(surface, onHighlight);
            Assert.Same(surface, hit);
        });
    }

    [Fact]
    public async Task Back_end_erases_a_highlighter_stroke_and_undo_brings_it_back() // FR-30, FR-31
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 1);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            Draw(surface, (20, 30, 0.5f), (150, 40, 0.5f));
            Assert.Single(Drawings(surface.HighlighterInkLayer));

            Assert.True(surface.BeginEraseGesture(inverted: true, stylusId: 3));
            surface.EraseAtView(new Point(80 * surface.Scale, 35 * surface.Scale));
            Assert.Empty(Drawings(surface.HighlighterInkLayer)); // the preview drops it at once
            surface.EndEraseGesture();

            Assert.Empty(surface.Editor!.Page.Strokes);
            Assert.Empty(Drawings(surface.HighlighterInkLayer));
            Assert.Equal(AppTool.Highlighter, vm.Tools.EffectiveTool);

            vm.Undo.Undo();
            Assert.Single(Drawings(surface.HighlighterInkLayer));
            Assert.Equal(0, PenDrawingCount(surface));
        });
    }

    [Fact]
    public async Task Partial_erase_splits_a_chisel_highlighter_and_both_pieces_stay_chisel_highlighters() // FR-31, FR-46
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 1);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            vm.Tools.Highlighter.IsChisel = true;
            Draw(surface, (20, 40, 0.5f), (60, 40, 0.5f), (100, 40, 0.5f), (140, 40, 0.5f), (180, 40, 0.5f));
            vm.Tools.EraserMode = EraserMode.Partial;

            Assert.True(surface.BeginEraseGesture(inverted: true, stylusId: 3));
            surface.EraseAtView(new Point(100 * surface.Scale, 40 * surface.Scale));
            surface.EndEraseGesture();

            var pieces = surface.Editor!.Page.Strokes;
            Assert.Equal(2, pieces.Count);
            Assert.All(pieces, p => Assert.Equal(InkTool.Highlighter, p.Tool));
            Assert.All(pieces, p => Assert.NotNull(p.Chisel));
            Assert.Equal(2, Drawings(surface.HighlighterInkLayer).Count);
            Assert.Equal(0, PenDrawingCount(surface));
        });
    }

    [Fact]
    public async Task Live_chisel_highlighter_draws_the_chisel_outline_translucent_as_a_whole() // FR-46, FR-40
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 2);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            vm.Tools.Highlighter.IsChisel = true;
            vm.Tools.Highlighter.ChiselAngleDeg = 30;
            var settings = vm.Tools.Highlighter.Settings;
            var renderer = surface.ActiveRenderer;
            var scale = surface.Scale;

            renderer.BeginWetStroke();
            var first = new StylusPoint[] { new(40, 60, 0.2f), new(120, 160, 0.9f) };
            var second = new StylusPoint[] { new(300, 120, 0.5f) };
            var a = renderer.BuildWetGeometry(first);
            var b = renderer.BuildWetGeometry(second);

            // Each update is the chisel outline of its points (joined to the previous one), pressure ignored.
            var tip = settings.Tip!.Value;
            AssertFigures(
                [HighlighterGeometry.ChiselOutline(Dip((40, 60), (120, 160)), tip, settings.ChiselLengthPt * scale)],
                Figures(a),
                1);
            AssertFigures(
                [HighlighterGeometry.ChiselOutline(Dip((120, 160), (300, 120)), tip, settings.ChiselLengthPt * scale)],
                Figures(b),
                1);

            // Opaque pieces under one translucent group: overlaps between updates do not darken.
            var brush = Assert.IsType<SolidColorBrush>(renderer.WetBrush);
            Assert.Equal(255, brush.Color.A);
            Assert.Equal((settings.StrokeColor.R, settings.StrokeColor.G, settings.StrokeColor.B), (brush.Color.R, brush.Color.G, brush.Color.B));
            Assert.Equal(HighlighterPresets.Alpha / 255.0, renderer.WetOpacity, 9);
            Assert.Equal(HighlighterPresets.Alpha / 255.0, ((ContainerVisual)renderer.RootVisual).Opacity, 9);
        });
    }

    [Fact]
    public async Task Live_round_highlighter_has_constant_width_whatever_the_pressure() // FR-42
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 1);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            var settings = vm.Tools.Highlighter.Settings;
            var renderer = surface.ActiveRenderer;

            renderer.BeginWetStroke();
            var geometry = renderer.BuildWetGeometry(new StylusPoint[] { new(40, 60, 0.05f), new(200, 70, 1f) });

            AssertFigures(
                [HighlighterGeometry.ConstantWidthOutline(Dip((40, 60), (200, 70)), settings.RoundWidthPt * surface.Scale)],
                Figures(geometry),
                1);
        });
    }

    [Fact]
    public async Task Live_pen_shape_and_pressure_are_unchanged_after_using_the_highlighter() // FR-20, FR-22, regression
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 2);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            vm.Tools.ActiveTool = AppTool.Pen;
            var renderer = surface.ActiveRenderer;
            var scale = surface.Scale;

            renderer.BeginWetStroke();
            var geometry = renderer.BuildWetGeometry(new StylusPoint[] { new(10, 10, 0.2f), new(30, 15, 0.6f), new(60, 40, 0.9f) });

            var widths = new[] { 0.2f, 0.6f, 0.9f }
                .Select(p => PressureMapping.WidthAt(vm.Tools.BaseWidthPt, p, surface.Curve) * scale)
                .ToArray();
            var expected = StrokeOutline.Build([new(10, 10), new(30, 15), new(60, 40)], widths);
            AssertFigures(expected, Figures(geometry), 1);
            Assert.True(widths[2] > widths[0]); // pressure still shapes the pen

            var brush = Assert.IsType<SolidColorBrush>(renderer.WetBrush);
            Assert.Equal(CustomInkLayer.ToColor(vm.Tools.Color), brush.Color);
            Assert.Equal(1, renderer.WetOpacity);
            Assert.Equal(1, ((ContainerVisual)renderer.RootVisual).Opacity);
        });
    }

    [Fact]
    public async Task Live_highlighter_follows_option_changes_before_the_stroke() // FR-41
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 1);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            vm.Tools.Highlighter.SelectColorCommand.Execute(HighlighterPresets.Green);

            var brush = Assert.IsType<SolidColorBrush>(surface.ActiveRenderer.WetBrush);
            Assert.Equal((HighlighterPresets.Green.R, HighlighterPresets.Green.G, HighlighterPresets.Green.B), (brush.Color.R, brush.Color.G, brush.Color.B));
        });
    }

    [Fact]
    public async Task Fingers_and_the_back_end_never_draw_with_the_highlighter() // FR-30
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm, zoom: 1);
            vm.Tools.ActiveTool = AppTool.Highlighter;
            Assert.True(surface.ActiveRenderer.Enabled);
            surface.ActiveRenderer.SetIgnoredDevices([1], [11]);
            Assert.True(surface.ActiveRenderer.IsIgnoredDevice(tabletId: 1, stylusId: 10)); // touch
            Assert.True(surface.ActiveRenderer.IsIgnoredDevice(tabletId: 2, stylusId: 11)); // back end

            vm.Tools.BeginInvertedEraser();
            Assert.False(surface.ActiveRenderer.Enabled);
            vm.Tools.EndInvertedEraser();
            Assert.True(surface.ActiveRenderer.Enabled);
        });
    }

    [Fact]
    public void Thumbnails_draw_highlighters_first_with_their_own_outline() // FR-40, UI-07
    {
        var pen = new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1, Line());
        var round = new InkStroke(Guid.NewGuid(), InkTool.Highlighter, HighlighterPresets.Yellow, 16, Line());
        var chisel = new InkStroke(Guid.NewGuid(), InkTool.Highlighter, HighlighterPresets.Green, 22, Line(), new ChiselTip(45, 0.25));

        var outlines = InkDrawings.BuildOutlines([pen, round, chisel], PressureCurve.Firm, SmoothingLevel.Strong);

        Assert.Equal([round.Color, chisel.Color, pen.Color], outlines.Select(o => o.Color));
        AssertFigures([HighlighterGeometry.ConstantWidthOutline(round.Points, 16)], outlines[0].Figures, 1);
        AssertFigures([HighlighterGeometry.ChiselOutline(chisel.Points, chisel.Chisel!.Value, 22)], outlines[1].Figures, 1);
        AssertFigures(StrokeGeometry.BuildOutline(pen, PressureCurve.Firm, SmoothingLevel.Strong), outlines[2].Figures, 1);

        static InkPoint[] Line() => [new(10, 10, 0.3f, null, null, null, 0), new(80, 14, 0.9f, null, null, null, 10)];
    }

    private static InkPoint[] Dip(params (double X, double Y)[] points) =>
        points.Select(p => new InkPoint(p.X, p.Y, null, null, null, null, 0)).ToArray();

    private static void Draw(InkSurface surface, params (double X, double Y, float Pressure)[] pointsPt)
    {
        surface.BeginPenStroke(stylusId: 1, hasPressure: true);
        surface.AppendPenPoints(
            pointsPt.Select(p => new StylusPoint(p.X * surface.Scale, p.Y * surface.Scale, p.Pressure)).ToArray(),
            timestamp: 10);
        surface.FinishPenStroke();
    }

    private static void AssertDrawnOutline(InkSurface surface, FrameworkElement layer, IReadOnlyList<PointD> expectedPt, InkColor color)
    {
        LayOut(surface);
        var drawing = Assert.Single(Drawings(layer));
        AssertFigures([expectedPt], ToSurface(surface, layer, drawing.Geometry), surface.Scale);
        Assert.Equal(CustomInkLayer.ToColor(color), Assert.IsType<SolidColorBrush>(drawing.Brush).Color);
    }

    /// <summary>Compares page-point figures times <paramref name="scale"/> with drawn figures.</summary>
    private static void AssertFigures(
        IReadOnlyList<IReadOnlyList<PointD>> expected, IReadOnlyList<IReadOnlyList<PointD>> actual, double scale)
    {
        var expectedFigures = expected.Where(f => f.Count >= 3).ToArray();
        Assert.Equal(expectedFigures.Length, actual.Count);
        for (var f = 0; f < expectedFigures.Length; f++)
        {
            Assert.Equal(expectedFigures[f].Count, actual[f].Count);
            for (var i = 0; i < expectedFigures[f].Count; i++)
            {
                Assert.Equal(expectedFigures[f][i].X * scale, actual[f][i].X, _tolerance);
                Assert.Equal(expectedFigures[f][i].Y * scale, actual[f][i].Y, _tolerance);
            }
        }
    }

    private static List<IReadOnlyList<PointD>> ToSurface(InkSurface surface, Visual layer, Geometry geometry)
    {
        var transform = layer.TransformToAncestor(surface);
        return Figures(geometry)
            .Select(f => (IReadOnlyList<PointD>)f.Select(p =>
            {
                var q = transform.Transform(new Point(p.X, p.Y));
                return new PointD(q.X, q.Y);
            }).ToArray())
            .ToList();
    }

    private static List<IReadOnlyList<PointD>> Figures(Geometry geometry)
    {
        var path = PathGeometry.CreateFromGeometry(geometry);
        var figures = new List<IReadOnlyList<PointD>>();
        foreach (var figure in path.Figures)
        {
            var points = new List<PointD> { new(figure.StartPoint.X, figure.StartPoint.Y) };
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case PolyLineSegment poly:
                        points.AddRange(poly.Points.Select(p => new PointD(p.X, p.Y)));
                        break;
                    case LineSegment line:
                        points.Add(new PointD(line.Point.X, line.Point.Y));
                        break;
                    default:
                        throw new InvalidOperationException(segment.GetType().Name);
                }
            }

            figures.Add(points);
        }

        return figures;
    }

    private static int PenDrawingCount(InkSurface surface) => Drawings(surface.CustomLayer).Count;

    private static List<GeometryDrawing> Drawings(Visual layer)
    {
        var found = new List<GeometryDrawing>();
        void Walk(Drawing? drawing)
        {
            switch (drawing)
            {
                case GeometryDrawing geometry:
                    found.Add(geometry);
                    break;
                case DrawingGroup group:
                    foreach (var child in group.Children)
                    {
                        Walk(child);
                    }

                    break;
            }
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(layer); i++)
        {
            if (VisualTreeHelper.GetChild(layer, i) is DrawingVisual visual)
            {
                Walk(visual.Drawing);
            }
        }

        return found;
    }

    private static List<DependencyObject> Children(Visual surface) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(surface))
            .Select(i => VisualTreeHelper.GetChild(surface, i))
            .ToList();

    private static InkSurface CreateSurface(MainViewModel vm, double zoom)
    {
        var surface = new InkSurface { ViewModel = vm, Editor = vm.Editor, Scale = zoom * Units.DipPerPt };
        LayOut(surface);
        return surface;
    }

    private static void LayOut(InkSurface surface)
    {
        surface.Measure(new Size(4000, 4000));
        surface.Arrange(new Rect(surface.DesiredSize));
        surface.UpdateLayout();
    }

    private async Task WithDocumentAsync(Action<MainViewModel> test)
    {
        var path = Path.Combine(_dir, "page.pdf");
        TestPdfFactory.Write(path, (200, 300));
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path });
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            Assert.NotNull(vm.Editor);
            WpfTestThread.Run(() => test(vm));
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
    }
}
