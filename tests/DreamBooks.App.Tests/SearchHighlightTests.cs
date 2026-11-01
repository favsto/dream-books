using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Windows;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Search;
using static DreamBooks.App.Tests.FakeSearchPageSource;

namespace DreamBooks.App.Tests;

/// <summary>
/// The search highlights on the page surfaces (FR-13, FR-105, FR-26): page-point rectangles scaled only in the view,
/// a layer between the PDF bitmap and the ink that never takes input, the current result stronger, following zoom and
/// rotation, bound per page as the pool recycles surfaces. How they look is checked on the device.
/// </summary>
public sealed class SearchHighlightTests : IDisposable
{
    private const double _pad = 1.5;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-highlight-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public SearchHighlightTests() => Directory.CreateDirectory(_dir);

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
    public void Highlight_layer_draws_rects_converted_from_page_points_at_the_surface_scale_and_sits_below_the_ink_layer()
    {
        WpfTestThread.Run(() =>
        {
            var surface = NewSurface(612, 792, scale: 2);
            surface.SetSearchHighlights([new SearchRect(100, 200, 150, 210)], []);
            LayOut(surface);

            var layer = surface.HighlightLayer;
            Assert.Equal(_pad, (double)Application.Current.FindResource("SearchHighlightPaddingPt"));
            var (rect, isCurrent) = Assert.Single(layer.ViewRects());
            Assert.False(isCurrent);
            AssertRect(new Rect(200, (200 - _pad) * 2, 100, (10 + (2 * _pad)) * 2), rect);

            // The rectangle is really drawn, where it says.
            var drawing = VisualTreeHelper.GetDrawing(layer);
            Assert.NotNull(drawing);
            AssertRect(rect, drawing.Bounds);

            // Between the bitmap and the ink, the size of the page like the ink layer.
            var children = Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(surface))
                .Select(i => VisualTreeHelper.GetChild(surface, i))
                .ToList();
            var bitmapAt = children.IndexOf(surface.BitmapLayer);
            var highlightAt = children.IndexOf(layer);
            var inkAt = children.IndexOf(surface.CustomLayer);
            Assert.True(bitmapAt >= 0 && bitmapAt < highlightAt && highlightAt < inkAt, $"{bitmapAt} < {highlightAt} < {inkAt}");
            Assert.Equal(surface.CustomLayer.RenderSize, layer.RenderSize);

            // Nothing is drawn without results.
            surface.SetSearchHighlights([], []);
            LayOut(surface);
            Assert.Empty(layer.ViewRects());
            Assert.True(VisualTreeHelper.GetDrawing(layer) is null || VisualTreeHelper.GetDrawing(layer).Bounds.IsEmpty);
        });
    }

    [Fact]
    public void Highlight_layer_is_not_hit_testable_and_the_pen_still_reaches_the_ink_surface()
    {
        WpfTestThread.Run(() =>
        {
            var surface = NewSurface(612, 792, scale: 1);
            surface.SetSearchHighlights([new SearchRect(100, 200, 300, 260)], [new SearchRect(100, 300, 300, 360)]);
            LayOut(surface);
            var layer = surface.HighlightLayer;
            Assert.False(layer.IsHitTestVisible);
            Assert.False(layer.Focusable);
            Assert.Null(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(layer));

            // A press on a highlighted word goes to the page surface itself (which inks), never to the layer.
            foreach (var point in new[] { new Point(200, 230), new Point(200, 330) })
            {
                var hit = ScrollRailTests.HitAt(surface, point);
                Assert.Same(surface, hit);
                Assert.False(ScrollRailTests.IsWithin(hit, layer));
            }
        });
    }

    [Fact]
    public void Current_result_uses_the_stronger_brush()
    {
        WpfTestThread.Run(() =>
        {
            var surface = NewSurface(612, 792, scale: 1);
            surface.SetSearchHighlights([new SearchRect(100, 200, 150, 210)], [new SearchRect(100, 400, 150, 410)]);
            LayOut(surface);
            var layer = surface.HighlightLayer;

            var hitBrush = Assert.IsType<SolidColorBrush>(Application.Current.FindResource("SearchHighlightBrush"));
            var currentBrush = Assert.IsType<SolidColorBrush>(Application.Current.FindResource("SearchCurrentHighlightBrush"));
            Assert.Same(hitBrush, layer.HitBrush);
            Assert.Same(currentBrush, layer.CurrentBrush);
            Assert.True(Strength(currentBrush) > Strength(hitBrush), "the current result is stronger");
            Assert.True(Strength(hitBrush) < 1 && Strength(currentBrush) < 1, "translucent: the text under them shows");

            Assert.Equal([false, true], layer.ViewRects().Select(r => r.IsCurrent));
            var drawn = Drawings(layer);
            Assert.Equal(2, drawn.Count);
            Assert.Same(hitBrush, drawn[0].Brush);
            Assert.Same(currentBrush, drawn[1].Brush); // drawn last, on top
            AssertRect(layer.ViewRects()[1].Rect, drawn[1].Geometry.Bounds);
        });
    }

    [Fact]
    public void Highlights_follow_zoom_and_page_rotation()
    {
        WpfTestThread.Run(() =>
        {
            var rect = new SearchRect(100, 200, 150, 210);
            var surface = NewSurface(612, 792, scale: 2);
            surface.SetSearchHighlights([rect], []);
            LayOut(surface);
            AssertRect(new Rect(200, (200 - _pad) * 2, 100, 26), surface.HighlightLayer.ViewRects()[0].Rect);

            // Zoom: the surface scale changes, the rectangles follow at once (vector, like the ink).
            surface.Scale = 0.5;
            LayOut(surface);
            AssertRect(new Rect(50, (200 - _pad) * 0.5, 25, 6.5), surface.HighlightLayer.ViewRects()[0].Rect);
            AssertRect(surface.HighlightLayer.ViewRects()[0].Rect, VisualTreeHelper.GetDrawing(surface.HighlightLayer)!.Bounds);

            // A page shown rotated (/Rotate 90: landscape as displayed): the rectangles are already in displayed page
            // points, so a result near the right edge of the landscape page lands there, inside the surface.
            var rotated = NewSurface(792, 612, scale: 4.0 / 3.0);
            rotated.SetSearchHighlights([new SearchRect(700, 500, 780, 512)], []);
            LayOut(rotated);
            var view = rotated.HighlightLayer.ViewRects()[0].Rect;
            AssertRect(new Rect(700 * 4.0 / 3.0, (500 - _pad) * 4.0 / 3.0, 80 * 4.0 / 3.0, 15 * 4.0 / 3.0), view);
            Assert.True(new Rect(rotated.RenderSize).Contains(view), "inside the landscape page");
        });
    }

    [Fact]
    public async Task Surfaces_show_their_own_page_results_and_none_after_being_recycled()
    {
        var source = new FakeSearchPageSource(30);
        source.Hits[0] = [Hit(0, 100), Hit(0, 300)];
        source.Hits[20] = [Hit(20, 500)];
        var pages = new FakePageSource(Enumerable.Repeat((612.0, 792.0), 30).ToArray());
        var path = Path.Combine(_dir, "a.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path }, scheduler: _scheduler)
        {
            MarkSourceFactory = _ => null,
            SearchSourceFactory = _ => source,
            MarkScanIdleGap = TimeSpan.Zero,
        };
        pages.InstallOn(vm);
        await vm.OpenPdfCommand.ExecuteAsync(null);

        WpfTestThread.Run(() =>
        {
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            ScrollRailTests.LayOut(view, 1000, 800);
            Assert.All(view.ActiveSurfaces.Values, s => Assert.False(s.HighlightLayer.HasHighlights)); // no search: nothing

            vm.Search.QueryText = "x";
            _scheduler.Advance(SearchViewModel.QueryDelay);
            var scanner = vm.SearchScannerForTest!;
            WpfTestThread.PumpUntil(() => scanner.IsCompleted);
            _scheduler.Advance(SearchViewModel.RefreshInterval);

            Assert.Equal(2, view.SurfaceFor(0)!.HighlightLayer.ViewRects().Count);
            Assert.All(view.ActiveSurfaces.Where(s => s.Key != 0), s => Assert.False(s.Value.HighlightLayer.HasHighlights));

            // The current result moves to page 20: that page's surface is bound with its own result, the current one.
            vm.Search.NextCommand.Execute(null);
            vm.Search.NextCommand.Execute(null);
            vm.Search.NextCommand.Execute(null);
            Assert.Equal(Hit(20, 500), vm.Search.Current);
            ScrollRailTests.LayOut(view, 1000, 800);
            WpfTestThread.Drain();
            Assert.Null(view.SurfaceFor(0));
            var rects = view.SurfaceFor(20)!.HighlightLayer.ViewRects();
            Assert.True(Assert.Single(rects).IsCurrent);
            Assert.All(view.ActiveSurfaces.Where(s => s.Key != 20), s => Assert.False(s.Value.HighlightLayer.HasHighlights));

            // Cleared: every surface drops them.
            vm.Search.ClearCommand.Execute(null);
            Assert.All(view.ActiveSurfaces.Values, s => Assert.False(s.HighlightLayer.HasHighlights));
        });
    }

    [Fact]
    public void Recycling_a_surface_clears_its_highlights()
    {
        WpfTestThread.Run(() =>
        {
            var surface = NewSurface(612, 792, scale: 1);
            surface.SetSearchHighlights([new SearchRect(1, 2, 3, 4)], [new SearchRect(5, 6, 7, 8)]);
            Assert.True(surface.HighlightLayer.HasHighlights);
            PageSurfacePool.Reset(surface);
            Assert.False(surface.HighlightLayer.HasHighlights);
        });
    }

    [Fact]
    public void Strings_Designer_stays_in_sync()
    {
        var manager = new ResourceManager("DreamBooks.App.Resources.Strings", typeof(Strings).Assembly);
        string[] keys =
        [
            "Search", "SearchField", "SearchHint", "PreviousResult", "NextResult", "ClearSearch", "SearchCountLabel",
            "SearchInProgress", "SearchNoResults", "SearchOneResult", "SearchResultsFormat", "SearchPositionFormat",
            "SearchTruncatedFormat", "SearchNoTextLayer", "SearchFailed", "SearchWrappedToStart", "SearchWrappedToEnd",
        ];
        foreach (var key in keys)
        {
            var value = manager.GetString(key, CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrEmpty(value), $"Strings.resx is missing '{key}'.");
            var property = typeof(Strings).GetProperty(key, BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(property);
            Assert.Equal(value, property.GetValue(null));
        }

        Assert.Equal("Ricerca in corso…", Strings.SearchInProgress);
        Assert.Equal("Nessun testo ricercabile in questo PDF", Strings.SearchNoTextLayer);
        Assert.Equal("Ripartito dall'inizio", Strings.SearchWrappedToStart);
        Assert.Equal("3 di 17", string.Format(CultureInfo.CurrentCulture, Strings.SearchPositionFormat, 3, 17));
        Assert.Equal("17+", string.Format(CultureInfo.CurrentCulture, Strings.SearchTruncatedFormat, 17));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private static InkSurface NewSurface(double widthPt, double heightPt, double scale) =>
        new() { PageSizePt = new Size(widthPt, heightPt), Scale = scale };

    private static void LayOut(InkSurface surface)
    {
        var size = new Size(surface.PageSizePt.Width * surface.Scale, surface.PageSizePt.Height * surface.Scale);
        surface.Measure(size);
        surface.Arrange(new Rect(size));
        surface.UpdateLayout();
    }

    private static double Strength(SolidColorBrush brush) => brush.Opacity * brush.Color.A / 255.0;

    private static List<GeometryDrawing> Drawings(Visual visual)
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

        Walk(VisualTreeHelper.GetDrawing(visual));
        return found;
    }

    private static void AssertRect(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }
}
