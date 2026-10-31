using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// The virtualised multi-page view on the real WPF dispatcher (never shown): one recycled ink surface per retained
/// page, positions from <see cref="DocumentLayout"/>, anchored zoom in the real scroll viewer, ink and erase on any
/// visible page (FR-05, FR-06, FR-07, FR-26, FR-27, NFR-03, NFR-05). Pen feel and visuals are checked on the device.
/// </summary>
public sealed class DocumentViewTests : IDisposable
{
    private const int _pageCount = 30;
    private const double _widthPt = 612;
    private const double _heightPt = 792;
    private const double _dipPerPt = 96.0 / 72.0;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-docview-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public DocumentViewTests() => Directory.CreateDirectory(_dir);

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
    public async Task Only_the_retained_window_of_pages_has_surfaces()
    {
        await WithViewAsync((vm, view) =>
        {
            var (first, last) = vm.Viewport.RetainedRange;
            var visible = vm.Viewport.VisiblePages.Count;
            Assert.Equal(0, first);
            Assert.Equal(Enumerable.Range(first, last - first + 1), view.ActiveSurfaces.Keys.Order());
            Assert.True(view.ActiveSurfaces.Count <= visible + 4);
            Assert.Equal(view.ActiveSurfaces.Count, view.Pages.Children.Count);
            Assert.Null(view.SurfaceFor(20));
            Assert.DoesNotContain(view.Pages.Children.Cast<UIElement>(), c => VirtualPagePanel.GetPageIndex(c) == 20);

            ScrollTo(view, view.Pages.PageRect(20).Top);

            Assert.NotNull(view.SurfaceFor(20));
            Assert.Null(view.SurfaceFor(0));
            var (first2, last2) = vm.Viewport.RetainedRange;
            Assert.Equal(Enumerable.Range(first2, last2 - first2 + 1), view.ActiveSurfaces.Keys.Order());
            Assert.Equal(view.ActiveSurfaces.Count, view.Pages.Children.Count);
        });
    }

    [Fact]
    public async Task Scrolling_recycles_surfaces_and_total_created_stays_bounded()
    {
        await WithViewAsync((vm, view) =>
        {
            var maxActive = view.ActiveSurfaces.Count;
            var extent = view.Pages.DesiredSize.Height;
            for (var y = 0.0; y < extent; y += view.Viewport.ViewportHeight * 0.7)
            {
                ScrollTo(view, y);
                maxActive = Math.Max(maxActive, view.ActiveSurfaces.Count);
                Assert.Equal(view.ActiveSurfaces.Count, view.Pages.Children.Count);
            }

            // A long jump back to the top reuses the surfaces instead of creating a new set.
            ScrollTo(view, 0);
            Assert.NotNull(view.SurfaceFor(0));
            Assert.True(view.Pool.CreatedCount <= maxActive + PageSurfacePool.SpareCount,
                $"created {view.Pool.CreatedCount}, window {maxActive}");
        });
    }

    [Fact]
    public async Task Surfaces_are_positioned_at_the_layout_rects_at_several_zooms()
    {
        await WithViewAsync((vm, view) =>
        {
            foreach (var zoom in new[] { 0.5, 1.0, 2.0, 3.0 })
            {
                vm.ZoomVm.Zoom = zoom;
                Settle(view);
                var layout = vm.Viewport.Layout!;
                var scale = zoom * _dipPerPt;
                var extentWidth = Math.Max(view.Viewport.ViewportWidth, layout.ColumnWidthPt * scale);
                Assert.Equal(extentWidth, view.Viewport.ExtentWidth, 6);
                Assert.Equal(layout.TotalHeightPt * scale, view.Viewport.ExtentHeight, 6);
                foreach (var (index, surface) in view.ActiveSurfaces)
                {
                    var expectedX = ((extentWidth - (layout.ColumnWidthPt * scale)) / 2) + (layout.PageLeftPt(index) * scale);
                    var expectedY = layout.PageTopPt(index) * scale;
                    var origin = surface.TranslatePoint(default, view.Pages);
                    Assert.Equal(expectedX, origin.X, 6);
                    Assert.Equal(expectedY, origin.Y, 6);
                    Assert.Equal(_widthPt * scale, surface.RenderSize.Width, 6);
                    Assert.Equal(_heightPt * scale, surface.RenderSize.Height, 6);
                    Assert.Equal(scale, surface.Scale, 9);
                }
            }
        });
    }

    [Fact]
    public async Task Placeholder_is_shown_for_pages_without_a_bitmap()
    {
        var bitmaps = new FakeBitmapSource();
        await WithViewAsync(
            (vm, view) =>
            {
                Assert.NotEmpty(bitmaps.Requests);
                var surface = view.SurfaceFor(1)!;
                Assert.Null(surface.PageImage);
                Assert.Equal(_widthPt * surface.Scale, surface.RenderSize.Width, 6);
                Assert.Equal(_heightPt * surface.Scale, surface.RenderSize.Height, 6);
                var paper = Assert.IsAssignableFrom<SolidColorBrush>(surface.PlaceholderBrush);
                Assert.Equal(Colors.White, paper.Color); // a white page, never a hole
                Assert.True(paper.Opacity >= 1);

                bitmaps.Complete(1, BitmapOf());
                WpfTestThread.Drain();
                Assert.NotNull(surface.PageImage);
                Assert.Null(view.SurfaceFor(0)!.PageImage);
            },
            bitmaps);
    }

    [Fact]
    public async Task Pen_stroke_on_a_visible_page_that_is_not_current_commits_there_and_keeps_current_page()
    {
        await WithViewAsync((vm, view) =>
        {
            vm.ZoomVm.Zoom = 0.5;
            Settle(view);
            LoadEditors(view);
            var current = vm.Viewport.CurrentPage;
            var sessionCurrent = vm.Session!.CurrentPageIndex;
            var target = vm.Viewport.VisiblePages.First(p => p != current);
            var surface = view.SurfaceFor(target)!;

            surface.BeginPenStroke(stylusId: 1, hasPressure: true);
            surface.AppendPenPoints([new(20, 20, 0.5f), new(80, 60, 0.7f)], timestamp: 10);
            surface.FinishPenStroke();

            Assert.Single(surface.Editor!.Page.Strokes);
            Assert.Equal(target, surface.Editor.Page.Index);
            Assert.Equal(current, vm.Viewport.CurrentPage);
            Assert.Equal(sessionCurrent, vm.Session.CurrentPageIndex);
            foreach (var (index, other) in view.ActiveSurfaces.Where(s => s.Key != target))
            {
                Assert.Empty(other.Editor!.Page.Strokes);
            }

            Assert.False(vm.ZoomVm.IsLocked);
        });
    }

    [Fact]
    public async Task Erase_gesture_starts_on_the_page_under_the_eraser_only()
    {
        await WithViewAsync((vm, view) =>
        {
            vm.ZoomVm.Zoom = 0.5;
            Settle(view);
            LoadEditors(view);
            var page1 = view.SurfaceFor(1)!;
            var page2 = view.SurfaceFor(2)!;
            Line(page1);
            Line(page2);
            var kept = page1.Editor!.Page.Strokes.ToArray();

            Assert.True(page2.BeginEraseGesture(inverted: true, stylusId: 5));
            Assert.Same(page2.Editor, vm.ActiveErasePage);
            Assert.False(page1.BeginEraseGesture(inverted: true, stylusId: 6)); // one gesture at a time
            page2.EraseAtView(new Point(20 * page2.Scale, 20 * page2.Scale));

            Assert.Empty(page2.DisplayStrokes); // the preview of its own page
            Assert.Equal(kept, page1.DisplayStrokes); // the page above is untouched

            // Leaving the page downwards keeps erasing in page 2's coordinates only.
            page2.EraseAtView(new Point(20 * page2.Scale, (_heightPt + 40) * page2.Scale));
            page2.EndEraseGesture();

            Assert.Empty(page2.Editor!.Page.Strokes);
            Assert.Equal(kept, page1.Editor.Page.Strokes);
            Assert.Null(vm.ActiveErasePage);
        });
    }

    [Fact]
    public async Task Stroke_that_starts_on_one_page_and_leaves_it_is_clipped_to_that_page()
    {
        await WithViewAsync((vm, view) =>
        {
            vm.ZoomVm.Zoom = 0.5;
            Settle(view);
            LoadEditors(view);
            var surface = view.SurfaceFor(1)!;
            var scale = surface.Scale;

            surface.BeginPenStroke(stylusId: 1, hasPressure: true);
            surface.AppendPenPoints(
                [
                    new(100 * scale, (_heightPt - 100) * scale, 0.5f),
                    new(100 * scale, (_heightPt + 100) * scale, 0.5f), // in the gap and on page 3
                ],
                timestamp: 10);
            surface.FinishPenStroke();

            var stroke = Assert.Single(surface.Editor!.Page.Strokes);
            Assert.All(stroke.Points, p => Assert.InRange(p.Y, 0, _heightPt));
            Assert.Equal(_heightPt, stroke.Points.Max(p => p.Y), 6);
            Assert.Empty(view.SurfaceFor(2)!.Editor!.Page.Strokes);
        });
    }

    [Fact]
    public async Task Surface_without_an_editor_does_not_accept_ink_until_the_editor_arrives()
    {
        var source = new FakePageSource(Enumerable.Repeat((_widthPt, _heightPt), _pageCount).ToArray());
        var gate = new TaskCompletionSource();
        source.Gates[2] = gate;
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = WriteFakeFile() }, scheduler: _scheduler);
        source.InstallOn(vm);
        await vm.OpenPdfCommand.ExecuteAsync(null);

        WpfTestThread.Run(() =>
        {
            var view = new DocumentView { BitmapSource = new FakeBitmapSource(), DataContext = vm };
            LayOut(view, 1000, 2400);
            view.FlushEditorLoads();
            var surface = view.SurfaceFor(2)!;
            Assert.Null(surface.Editor);
            Assert.False(surface.ActiveRenderer.Enabled);
            Assert.False(surface.BeginEraseGesture(inverted: true, stylusId: 1));
            MouseDown(surface);
            Assert.False(surface.IsInking);
            Assert.NotNull(view.SurfaceFor(1)!.Editor); // loaded pages ink meanwhile

            gate.SetResult();
            WpfTestThread.PumpUntil(() => surface.Editor is not null);

            Assert.Equal(2, surface.Editor!.Page.Index);
            Assert.Same(surface.Editor.Page, surface.Page);
            Assert.True(surface.ActiveRenderer.Enabled);
            MouseDown(surface);
            Assert.True(surface.IsInking);
            MouseUp(surface);
        });
    }

    [Fact]
    public async Task Recycling_a_surface_that_is_inking_is_deferred()
    {
        await WithViewAsync((vm, view) =>
        {
            LoadEditors(view);
            var surface = view.SurfaceFor(1)!;
            surface.BeginPenStroke(stylusId: 1, hasPressure: true);
            surface.AppendPenPoints([new(20, 20, 0.5f), new(80, 60, 0.7f)], timestamp: 10);

            ScrollTo(view, view.Pages.PageRect(20).Top);

            Assert.Same(surface, view.SurfaceFor(1)); // still bound to its page, still in the tree
            Assert.Contains(surface, view.Pages.Children.Cast<UIElement>());
            Assert.Equal(1, VirtualPagePanel.GetPageIndex(surface));

            surface.FinishPenStroke();
            Settle(view);

            Assert.Null(view.SurfaceFor(1));
            Assert.DoesNotContain(view.Pages.Children.Cast<UIElement>(), c => VirtualPagePanel.GetPageIndex(c) == 1);
            Assert.True(vm.Session!.TryGetEditor(1, out var editor));
            Assert.Single(editor!.Page.Strokes); // committed on its own page
            Assert.Equal(view.ActiveSurfaces.Count, view.Pages.Children.Count);
        });
    }

    [Fact]
    public async Task Zoom_keeps_the_page_point_under_the_anchor_still_in_the_real_scroll_viewer()
    {
        await WithViewAsync((vm, view) =>
        {
            vm.ZoomVm.Zoom = 1;
            Settle(view);
            ScrollTo(view, view.Pages.PageRect(5).Top + 123.4, x: 0);
            // Off-centre anchors only where the column is wider than the viewport before and after (a narrower column
            // is centred, so its x cannot follow the fingers); the centre anchor holds at any zoom.
            (double Factor, Point Anchor)[] steps =
            [
                (1.5, new Point(500, 420)),
                (0.6, new Point(500, 120)),
                (2.5, new Point(500, 700)),
                (1.3, new Point(310, 200)),
                (0.8, new Point(820, 650)),
            ];

            foreach (var (factor, anchor) in steps)
            {
                var page = PageUnder(view, anchor);
                var surface = view.SurfaceFor(page)!;
                var onPage = view.Viewport.TranslatePoint(anchor, surface);
                var pagePt = new Point(onPage.X / surface.Scale, onPage.Y / surface.Scale);

                vm.Viewport.ZoomAt(factor, anchor);
                Settle(view);

                var after = view.SurfaceFor(page)!;
                var back = after.TranslatePoint(new Point(pagePt.X * after.Scale, pagePt.Y * after.Scale), view.Viewport);
                Assert.InRange(back.X, anchor.X - 0.5, anchor.X + 0.5);
                Assert.InRange(back.Y, anchor.Y - 0.5, anchor.Y + 0.5);
            }
        });
    }

    [Fact]
    public async Task Double_tap_toggles_fit_width_and_200_percent_around_the_tap()
    {
        await WithViewAsync((vm, view) =>
        {
            ScrollTo(view, view.Pages.PageRect(3).Top + 50);
            Assert.True(vm.Viewport.IsFitWidth);
            var tap = new Point(200, 300);
            var second = new Point(210, 310); // the double tap zooms around the second tap
            var docY = DocumentYUnder(view, second.Y);

            view.RegisterTap(tap, timestamp: 1000);
            view.RegisterTap(second, timestamp: 1200);
            Settle(view);

            Assert.False(vm.Viewport.IsFitWidth);
            Assert.Equal(DocumentViewportViewModel.DoubleTapZoom, vm.ZoomVm.Zoom, 9);
            Assert.InRange(DocumentYUnder(view, second.Y) - docY, -0.5 / (2 * _dipPerPt), 0.5 / (2 * _dipPerPt));

            view.RegisterTap(tap, timestamp: 5000);
            view.RegisterTap(tap, timestamp: 5400); // too slow: two single taps
            view.RegisterTap(new Point(260, 300), timestamp: 5500); // too far
            Assert.False(vm.Viewport.IsFitWidth);

            view.RegisterTap(tap, timestamp: 9000);
            view.RegisterTap(tap, timestamp: 9100);
            Settle(view);
            Assert.True(vm.Viewport.IsFitWidth);
            Assert.Equal(vm.Viewport.Layout!.FitWidthZoom(view.Viewport.ViewportWidth), vm.ZoomVm.Zoom, 9);
        });
    }

    [Fact]
    public async Task Resize_in_fit_mode_recomputes_zoom_and_keeps_the_top_position()
    {
        await WithViewAsync((vm, view) =>
        {
            Assert.True(vm.Viewport.IsFitWidth);
            ScrollTo(view, view.Pages.PageRect(4).Top + 200);
            var before = vm.Viewport.Position;

            LayOut(view, 700, 900);

            var layout = vm.Viewport.Layout!;
            Assert.Equal(700, view.Viewport.ViewportWidth, 6);
            Assert.Equal(layout.FitWidthZoom(700), vm.ZoomVm.Zoom, 9);
            Assert.Equal(before.PageIndex, vm.Viewport.Position.PageIndex);
            Assert.Equal(before.YPt, vm.Viewport.Position.YPt, 3);
            var (x, y) = layout.ToOffsets(vm.Viewport.Position, 700, 900);
            Assert.Equal(y, view.Viewport.VerticalOffset, 3);
            Assert.Equal(x, view.Viewport.HorizontalOffset, 3);
            Assert.Equal(layout.FitWidthZoom(700) * _dipPerPt, view.SurfaceFor(before.PageIndex)!.Scale, 9);
        });
    }

    [Fact]
    public async Task Restored_position_is_applied_once_after_first_layout()
    {
        var path = Path.Combine(_dir, "restored.pdf");
        TestPdfFactory.Write(path, Enumerable.Repeat((_widthPt, _heightPt), _pageCount).ToArray());
        var positions = new ViewPositionStore(Path.Combine(_dir, "positions"));
        positions.Save(path, new ViewPosition(7, 100, _widthPt / 2, 1.5));
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path }, positions: positions, scheduler: _scheduler);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            WpfTestThread.Run(() =>
            {
                var view = new DocumentView { BitmapSource = new FakeBitmapSource(), DataContext = vm };
                LayOut(view, 1000, 800);

                var layout = vm.Viewport.Layout!;
                Assert.Equal(1.5, vm.ZoomVm.Zoom, 9);
                Assert.Equal(7, vm.Viewport.Position.PageIndex);
                Assert.Equal(100, vm.Viewport.Position.YPt, 6);
                var (x, y) = layout.ToOffsets(new ViewPosition(7, 100, _widthPt / 2, 1.5), 1000, 800);
                Assert.Equal(y, view.Viewport.VerticalOffset, 3);
                Assert.Equal(x, view.Viewport.HorizontalOffset, 3);
                Assert.NotNull(view.SurfaceFor(7));

                // The user scrolls: the restore is not applied again.
                ScrollTo(view, 0);
                LayOut(view, 1000, 800);
                Assert.Equal(0, vm.Viewport.Position.PageIndex);
                Assert.Equal(0, view.Viewport.VerticalOffset, 3);
            });
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
    }

    [Fact]
    public void MainWindow_still_instantiates_with_the_new_view()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                Assert.IsType<DocumentView>(window.DocumentViewer);
                Assert.Same(window.DataContext, window.DocumentViewer.DataContext);
                LayOut((FrameworkElement)window.Content, 1000, 700);
                Assert.Empty(window.DocumentViewer.ActiveSurfaces); // no document yet
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Interim_renderer_bitmaps_arrive_for_the_retained_pages()
    {
        await WithViewAsync(
            (vm, view) =>
            {
                WpfTestThread.PumpUntil(() => view.SurfaceFor(0)!.PageImage is not null);
                var image = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(view.SurfaceFor(0)!.PageImage);
                var (width, _) = RenderSizePolicy.Compute(new PageSize(_widthPt, _heightPt), vm.ZoomVm.Zoom, 1.0);
                Assert.InRange(image.PixelWidth, width - 1, (width * 4) + 1); // at the zoom, times the screen scale
            },
            bitmaps: null);
    }

    private Task WithViewAsync(Action<MainViewModel, DocumentView> test) => WithViewAsync(test, new FakeBitmapSource());

    /// <summary>
    /// Opens a 30-page generated PDF in a real view model, then runs the test on a view laid out at 1000 x 800 DIPs.
    /// <paramref name="bitmaps"/> null keeps the view's own bitmap source (the interim renderer path).
    /// </summary>
    private async Task WithViewAsync(Action<MainViewModel, DocumentView> test, IPageBitmapSource? bitmaps)
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfFactory.Write(path, Enumerable.Repeat((_widthPt, _heightPt), _pageCount).ToArray());
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path }, scheduler: _scheduler);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            WpfTestThread.Run(() =>
            {
                var view = new DocumentView();
                if (bitmaps is not null)
                {
                    view.BitmapSource = bitmaps;
                }

                view.DataContext = vm;
                LayOut(view, 1000, 800);
                test(vm, view);
            });
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
    }

    private string WriteFakeFile()
    {
        var path = Path.Combine(_dir, "fake.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static void LayOut(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        if (element is DocumentView view)
        {
            Settle(view);
        }
        else
        {
            element.UpdateLayout();
        }
    }

    /// <summary>Runs layout and the queued dispatcher work until the scroll viewer and the view model agree.</summary>
    private static void Settle(DocumentView view)
    {
        for (var i = 0; i < 6; i++)
        {
            view.UpdateLayout();
            WpfTestThread.Drain();
        }
    }

    private static void ScrollTo(DocumentView view, double y, double? x = null)
    {
        if (x is { } horizontal)
        {
            view.Viewport.ScrollToHorizontalOffset(horizontal);
        }

        view.Viewport.ScrollToVerticalOffset(y);
        Settle(view);
    }

    private static void LoadEditors(DocumentView view)
    {
        view.FlushEditorLoads();
        WpfTestThread.PumpUntil(() => view.ActiveSurfaces.Values.All(s => s.Editor is not null));
    }

    /// <summary>The page whose surface is under (or, in a gap, nearest to) a viewport point.</summary>
    private static int PageUnder(DocumentView view, Point anchor) =>
        view.ActiveSurfaces.MinBy(s =>
        {
            var y = view.Viewport.TranslatePoint(anchor, s.Value).Y;
            return y < 0 ? -y : Math.Max(0, y - s.Value.RenderSize.Height);
        }).Key;

    private static double DocumentYUnder(DocumentView view, double viewportY) =>
        (view.Viewport.VerticalOffset + viewportY) / (((MainViewModel)view.DataContext).ZoomVm.Zoom * _dipPerPt);

    private static void Line(InkSurface surface)
    {
        InkPoint[] points = [new(10, 20, 0.5f, null, null, null, 0), new(30, 20, 0.5f, null, null, null, 10)];
        Assert.True(surface.Editor!.CommitStroke(points, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1));
    }

    private static void MouseDown(InkSurface surface) =>
        surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });

    private static void MouseUp(InkSurface surface) =>
        surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });

    private static ImageSource BitmapOf()
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Bitmaps on demand: every request waits until the test completes it.</summary>
    private sealed class FakeBitmapSource : IPageBitmapSource
    {
        private readonly Dictionary<int, TaskCompletionSource<ImageSource?>> _pending = [];

        public List<int> Requests { get; } = [];

        public Task<ImageSource?> RenderAsync(int pageIndex, PageSize pageSize, double zoom, double dpiScale, CancellationToken ct)
        {
            Requests.Add(pageIndex);
            var request = new TaskCompletionSource<ImageSource?>();
            _pending[pageIndex] = request;
            return request.Task;
        }

        public void Complete(int pageIndex, ImageSource image) => _pending[pageIndex].SetResult(image);
    }
}
