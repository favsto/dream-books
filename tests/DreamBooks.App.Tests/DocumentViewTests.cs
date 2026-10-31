using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;
using DreamBooks.Core.Rendering;
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
                    // The layout rect, snapped to whole device pixels on screen (at most half a pixel away).
                    var expectedX = ((extentWidth - (layout.ColumnWidthPt * scale)) / 2) + (layout.PageLeftPt(index) * scale);
                    var expectedY = layout.PageTopPt(index) * scale;
                    var origin = surface.TranslatePoint(default, view.Pages);
                    Assert.InRange(origin.X - expectedX, -0.5, 0.5);
                    Assert.InRange(origin.Y - expectedY, -0.5, 0.5);
                    Assert.InRange(surface.RenderSize.Width - (_widthPt * scale), -0.5, 0.5);
                    Assert.InRange(surface.RenderSize.Height - (_heightPt * scale), -0.5, 0.5);
                    Assert.Equal(scale, surface.Scale, 9);
                }
            }
        });
    }

    [Fact]
    public async Task Placeholder_is_shown_for_pages_without_a_bitmap()
    {
        var bitmaps = new FakeBitmapRenderer();
        await WithViewAsync(
            (vm, view) =>
            {
                WpfTestThread.PumpUntil(() => bitmaps.Pending.Any(r => r.Page == 0));
                var surface = view.SurfaceFor(0)!;
                Assert.Null(surface.PageImage);
                Assert.InRange(surface.RenderSize.Width - (_widthPt * surface.Scale), -0.5, 0.5);
                Assert.InRange(surface.RenderSize.Height - (_heightPt * surface.Scale), -0.5, 0.5);
                var paper = Assert.IsAssignableFrom<SolidColorBrush>(surface.PlaceholderBrush);
                Assert.Equal(Colors.White, paper.Color); // a white page from the theme, never a hole
                Assert.True(paper.Opacity >= 1);
                Assert.Same(Application.Current.FindResource("PagePlaceholderBrush"), paper);

                bitmaps.Complete(0); // its quick preview
                WpfTestThread.PumpUntil(() => surface.PageImage is not null);
                Assert.Null(view.SurfaceFor(view.ActiveSurfaces.Keys.Max())!.PageImage); // nothing rendered there yet
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
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer(), DataContext = vm };
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
                var pagePt = PagePointUnder(view, page, anchor);

                vm.Viewport.ZoomAt(factor, anchor);
                Settle(view);

                var back = ViewportPointOf(view, page, pagePt);
                Assert.InRange(back.X, anchor.X - 0.5, anchor.X + 0.5);
                Assert.InRange(back.Y, anchor.Y - 0.5, anchor.Y + 0.5);
                AssertSurfaceNearItsRect(view, page);
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
                var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer(), DataContext = vm };
                LayOut(view, 1000, 800);

                var layout = vm.Viewport.Layout!;
                Assert.Equal(1.5, vm.ZoomVm.Zoom, 9);
                Assert.Equal(7, vm.Viewport.Position.PageIndex);
                Assert.Equal(100, vm.Viewport.Position.YPt, 6);
                var (x, y) = layout.ToOffsets(new ViewPosition(7, 100, _widthPt / 2, 1.5), 1000, 800);
                Assert.Equal(y, view.Viewport.VerticalOffset, 3);
                Assert.Equal(x, view.Viewport.HorizontalOffset, 3);
                Assert.NotNull(view.SurfaceFor(7));

                // The user scrolls, then the window is resized (a viewport-size change re-applies the VM position):
                // the restored position is not applied again.
                ScrollTo(view, 0);
                LayOut(view, 900, 700);
                Assert.Equal(0, vm.Viewport.Position.PageIndex);
                Assert.Equal(0, vm.Viewport.Position.YPt, 6);
                Assert.Equal(0, view.Viewport.VerticalOffset, 3);
                Assert.Null(view.SurfaceFor(7));
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
    public async Task Pdf_bitmaps_arrive_sharp_for_the_window_through_the_scheduler()
    {
        await WithViewAsync(
            (vm, view) =>
            {
                // The real renderer: the screen scale is the test machine's.
                var (width, height) = PageBitmapRenderer.SharpSize(new PageSize(_widthPt, _heightPt), vm.ZoomVm.Zoom, view.Pages.DpiScale);
                WpfTestThread.PumpUntil(() => view.SurfaceFor(0)!.PageImage is BitmapSource { PixelWidth: var w } && w == width);
                var image = (BitmapSource)view.SurfaceFor(0)!.PageImage!;
                Assert.Equal(height, image.PixelHeight);
                WpfTestThread.PumpUntil(() => view.ActiveSurfaces.Values.All(s => s.PageImage is not null)); // neighbours too
            },
            bitmaps: null);
    }

    [Fact]
    public async Task Bitmaps_come_back_after_a_plain_save_swaps_the_renderer()
    {
        await WithViewAsync(
            (vm, view) =>
            {
                var viewer = vm.ViewerForTest!;
                Idle(view);
                Assert.All(vm.Viewport.VisiblePages, page => Assert.True(ShowsCurrentSharp(view, page), $"page {page}"));

                // A plain Save releases the file: pages entering the window meanwhile cannot be rendered.
                Wait(viewer.ReleaseAsync());
                Assert.Null(vm.Renderer);
                ScrollTo(view, view.Pages.PageRect(12).Top);
                Idle(view);
                ScrollTo(view, view.Pages.PageRect(12).Top + 20); // their one retry fails too: they wait
                Idle(view);
                var failed = vm.Viewport.VisiblePages.ToArray();
                Assert.All(failed, page => Assert.Null(view.SurfaceFor(page)!.PageImage)); // placeholders

                // The renderer comes back on the saved file: the failed pages and the earlier ones render again.
                Wait(viewer.ReopenAsync(Path.Combine(_dir, "doc.pdf"), CancellationToken.None));
                Assert.NotNull(vm.Renderer);
                Idle(view);
                Assert.All(failed, page => Assert.True(ShowsCurrentSharp(view, page), $"page {page}"));

                ScrollTo(view, 0);
                Idle(view);
                Assert.True(ShowsCurrentSharp(view, 0));
            },
            bitmaps: null);
    }

    [Fact]
    public async Task Bitmaps_are_released_when_the_view_unloads_and_come_back_when_it_loads()
    {
        var bitmaps = new FakeBitmapRenderer { Auto = true };
        await WithViewAsync(
            (vm, view) =>
            {
                Idle(view);
                var before = view.Bitmaps;
                var surface = view.SurfaceFor(0)!;
                var shown = surface.PageImage;
                Assert.True(before.Scheduler!.CachedBytes > 0);

                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.Null(before.Scheduler); // disposed: the cache is gone
                Assert.NotSame(before, view.Bitmaps);
                Assert.Same(shown, surface.PageImage); // nothing blanks

                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Idle(view);
                Assert.Same(surface, view.SurfaceFor(0));
                Assert.True(ShowsCurrentSharp(view, 0));
            },
            bitmaps);
    }

    [Fact]
    public void Sharp_render_size_is_capped_on_its_longest_side()
    {
        var a4 = new PageSize(_widthPt, _heightPt);
        Assert.Equal(RenderSizePolicy.Compute(a4, 1, 1.5), PageBitmapRenderer.SharpSize(a4, 1, 1.5));

        var (width, height) = PageBitmapRenderer.SharpSize(a4, 8, 2);
        Assert.Equal(PageBitmapRenderer.MaxLongSidePx, height);
        Assert.Equal(_widthPt / _heightPt, (double)width / height, 3); // same proportions, stretched to the page

        var (wide, low) = PageBitmapRenderer.SharpSize(new PageSize(5000, 300), 1, 1);
        Assert.Equal(PageBitmapRenderer.MaxLongSidePx, wide);
        Assert.True(low >= 1);
    }

    [Fact]
    public async Task Bitmaps_are_asked_for_the_retained_window_only_visible_pages_first()
    {
        var bitmaps = new FakeBitmapRenderer { Auto = true };
        await WithViewAsync(
            (vm, view) =>
            {
                Idle(view);
                var everRetained = new HashSet<int>(Retained(vm));
                Assert.Contains(bitmaps.Requests[0].Page, vm.Viewport.VisiblePages);

                for (var page = 1; page < 12; page += 2)
                {
                    ScrollTo(view, view.Pages.PageRect(page).Top + 300);
                    everRetained.UnionWith(Retained(vm));
                    Idle(view);
                    Assert.All(vm.Viewport.VisiblePages, visible => Assert.NotNull(view.SurfaceFor(visible)!.PageImage));
                }

                Assert.All(bitmaps.Requests, r => Assert.Contains(r.Page, everRetained));

                var before = bitmaps.Requests.Length;
                view.InvalidateBitmaps(); // e.g. the renderer was replaced
                Idle(view);
                var again = bitmaps.Requests.Skip(before).ToArray();
                Assert.Equal(Retained(vm).Order(), again.Select(r => r.Page).Order());
                Assert.All(again.Take(vm.Viewport.VisiblePages.Count), r => Assert.Contains(r.Page, vm.Viewport.VisiblePages));
            },
            bitmaps);
    }

    [Fact]
    public async Task Bitmap_that_completes_after_its_surface_was_rebound_is_not_applied()
    {
        var bitmaps = new FakeBitmapRenderer { HonourCancellation = false }; // a render that finishes anyway
        await WithViewAsync(
            (vm, view) =>
            {
                var surface = view.SurfaceFor(0)!;
                WpfTestThread.PumpUntil(() => bitmaps.Pending.Any(r => r.Page == 0));

                ScrollTo(view, view.Pages.PageRect(20).Top);
                Assert.NotEqual(0, VirtualPagePanel.GetPageIndex(surface)); // rebound to another page (or spare)
                bitmaps.CompleteAll(0);
                WpfTestThread.PumpUntil(() => bitmaps.Pending.Any(r => r.Page >= 18)); // page 0's result was handled
                WpfTestThread.Drain();

                Assert.Null(surface.PageImage);
            },
            bitmaps);
    }

    [Fact]
    public async Task Editor_load_that_completes_after_its_surface_was_rebound_is_not_applied()
    {
        var source = new FakePageSource(Enumerable.Repeat((_widthPt, _heightPt), _pageCount).ToArray());
        var gate = new TaskCompletionSource();
        source.Gates[2] = gate;
        var far = new TaskCompletionSource();
        for (var page = 15; page < _pageCount; page++)
        {
            source.Gates[page] = far;
        }

        var vm = new MainViewModel(new FakeFileDialogService { NextPath = WriteFakeFile() }, scheduler: _scheduler);
        source.InstallOn(vm);
        await vm.OpenPdfCommand.ExecuteAsync(null);

        WpfTestThread.Run(() =>
        {
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer(), DataContext = vm };
            LayOut(view, 1000, 800);
            view.FlushEditorLoads();
            var surface = view.SurfaceFor(2)!;
            Assert.Null(surface.Editor);

            ScrollTo(view, view.Pages.PageRect(20).Top);
            view.FlushEditorLoads();
            Assert.NotEqual(2, VirtualPagePanel.GetPageIndex(surface));

            gate.SetResult(); // page 3's strokes arrive late
            WpfTestThread.Drain();

            Assert.True(vm.Session!.TryGetEditor(2, out _));
            Assert.Null(surface.Editor);
            Assert.Null(surface.Page);
        });
    }

    [Fact]
    public async Task Pan_and_pinch_in_one_delta_keep_the_point_under_the_fingers()
    {
        await WithViewAsync((vm, view) =>
        {
            vm.ZoomVm.Zoom = 1.5; // the column is wider than the viewport: x follows the fingers too
            Settle(view);
            ScrollTo(view, view.Pages.PageRect(5).Top + 200, x: 150);
            var origin = new Point(400, 300);
            var translation = new Vector(30, -20);
            var grabbed = origin - translation; // where the fingers were before this delta
            var page = PageUnder(view, grabbed);
            var pagePt = PagePointUnder(view, page, grabbed);

            view.ApplyManipulation(translation, 1.4, origin);
            Settle(view);

            Assert.Equal(1.5 * 1.4, vm.ZoomVm.Zoom, 9);
            var back = ViewportPointOf(view, page, pagePt);
            Assert.InRange(back.X, origin.X - 0.5, origin.X + 0.5);
            Assert.InRange(back.Y, origin.Y - 0.5, origin.Y + 0.5);
        });
    }

    [Fact]
    public async Task Slow_pans_below_half_a_dip_still_scroll()
    {
        await WithViewAsync((vm, view) =>
        {
            ScrollTo(view, 1000);
            var start = view.Viewport.VerticalOffset;

            for (var i = 0; i < 10; i++)
            {
                view.ApplyManipulation(new Vector(0, -0.2), 1, new Point(500, 400));
                Settle(view);
            }

            Assert.InRange(view.Viewport.VerticalOffset - start, 1.5, 2.5);
        });
    }

    [Fact]
    public async Task Pans_are_clamped_at_the_document_edges()
    {
        await WithViewAsync((vm, view) =>
        {
            var origin = new Point(500, 400);
            view.ApplyManipulation(new Vector(40, 300), 1, origin); // past the top and sideways (column fits)
            Settle(view);
            Assert.Equal(0, view.Viewport.VerticalOffset, 6);
            Assert.Equal(0, view.Viewport.HorizontalOffset, 6);
            Assert.Equal(0, vm.Viewport.Position.PageIndex);

            view.ApplyManipulation(new Vector(0, -10), 1, origin); // the overshoot is not banked
            Settle(view);
            Assert.InRange(view.Viewport.VerticalOffset, 9.5, 10.5);

            ScrollTo(view, view.Viewport.ScrollableHeight);
            var bottom = view.Viewport.ScrollableHeight;
            view.ApplyManipulation(new Vector(0, -300), 1, origin);
            Settle(view);
            Assert.Equal(bottom, view.Viewport.VerticalOffset, 3);
            Assert.Equal(_pageCount - 1, vm.Viewport.VisiblePages[^1]);
        });
    }

    [Fact]
    public async Task Pages_of_different_sizes_are_centred_in_the_column_and_sized_like_their_rects()
    {
        (double, double)[] sizes = [(612, 792), (400, 600), (900, 500)];
        var source = new FakePageSource(Enumerable.Range(0, _pageCount).Select(i => sizes[i % 3]).ToArray());
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = WriteFakeFile() }, scheduler: _scheduler);
        source.InstallOn(vm);
        await vm.OpenPdfCommand.ExecuteAsync(null);

        WpfTestThread.Run(() =>
        {
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer(), DataContext = vm };
            LayOut(view, 1000, 2400);
            foreach (var zoom in new[] { 0.5, 1.4 })
            {
                vm.ZoomVm.Zoom = zoom;
                Settle(view);
                view.FlushEditorLoads(); // synchronous loads: every surface has its page now
                Settle(view);
                var layout = vm.Viewport.Layout!;
                var scale = zoom * _dipPerPt;
                var columnCentre = view.Viewport.ExtentWidth / 2;
                Assert.True(view.ActiveSurfaces.Count >= 4);
                foreach (var (index, surface) in view.ActiveSurfaces)
                {
                    var rect = view.Pages.PageRect(index);
                    var (widthPt, heightPt) = sizes[index % 3];
                    Assert.Same(surface.Editor!.Page, surface.Page);
                    Assert.Equal(widthPt * scale, rect.Width, 6);
                    Assert.Equal(heightPt * scale, rect.Height, 6);
                    Assert.Equal(rect.Width, surface.Page!.WidthPt * surface.Scale, 6); // the surface's own page size
                    Assert.Equal(rect.Height, surface.Page.HeightPt * surface.Scale, 6);
                    Assert.InRange(surface.RenderSize.Width - rect.Width, -0.5, 0.5);
                    Assert.InRange(surface.RenderSize.Height - rect.Height, -0.5, 0.5);
                    Assert.Equal(columnCentre, rect.X + (rect.Width / 2), 6); // centred in the column
                    AssertSurfaceNearItsRect(view, index);
                }

                Assert.Equal(900, layout.ColumnWidthPt);
            }
        });
    }

    [Fact]
    public void Page_rects_land_on_whole_device_pixels_at_100_and_150_percent_dpi()
    {
        WpfTestThread.Run(() =>
        {
            var layout = new DocumentLayout([new(612, 792), new(400, 600.3), new(900.7, 500)]);
            var panel = new VirtualPagePanel();
            foreach (var dpi in new[] { 1.0, 1.5 })
            {
                foreach (var zoom in new[] { 1.0, 1.37 })
                {
                    foreach (var offset in new[] { new Vector(0, 0), new Vector(13.37, 1234.567) })
                    {
                        panel.Configure(layout, zoom, 1000, dpi);
                        panel.ScrollOffset = offset;
                        for (var i = 0; i < layout.PageCount; i++)
                        {
                            var exact = panel.PageRect(i);
                            var placed = panel.ArrangedRect(i);
                            AssertWholePixels((placed.X - offset.X) * dpi);
                            AssertWholePixels((placed.Y - offset.Y) * dpi);
                            AssertWholePixels(placed.Width * dpi);
                            AssertWholePixels(placed.Height * dpi);
                            Assert.InRange(placed.X - exact.X, -0.5 / dpi, 0.5 / dpi);
                            Assert.InRange(placed.Y - exact.Y, -0.5 / dpi, 0.5 / dpi);
                            Assert.InRange(placed.Width - exact.Width, -0.5 / dpi, 0.5 / dpi);
                            Assert.InRange(placed.Height - exact.Height, -0.5 / dpi, 0.5 / dpi);
                        }
                    }
                }
            }
        });
    }

    [Fact]
    public async Task Ink_stays_registered_with_the_pixel_aligned_bitmap()
    {
        var bitmaps = new FakeBitmapRenderer { Auto = true };
        await WithViewAsync(
            (vm, view) =>
            {
                var dpi = view.Pages.DpiScale; // the screen scale of the machine running the tests (often not 1)
                foreach (var zoom in new[] { 0.77, 1.0, 1.9 })
                {
                    vm.ZoomVm.Zoom = zoom;
                    Settle(view);
                    ScrollTo(view, view.Pages.PageRect(2).Top + 37.3, x: 11.7);
                    view.InvalidateBitmaps(); // the zoom settles now
                    Idle(view);
                    Settle(view);
                    foreach (var page in vm.Viewport.VisiblePages)
                    {
                        var surface = view.SurfaceFor(page)!;
                        var screen = surface.TranslatePoint(default, view.Viewport);
                        AssertWholePixels(screen.X * dpi);
                        AssertWholePixels(screen.Y * dpi);
                        AssertWholePixels(surface.RenderSize.Width * dpi);
                        AssertWholePixels(surface.RenderSize.Height * dpi);

                        // The bitmap layer covers the surface exactly, and is asked at exactly its pixel size (1:1).
                        Assert.Equal(surface.RenderSize, surface.BitmapLayer.RenderSize);
                        Assert.Equal(default, surface.BitmapLayer.TranslatePoint(default, surface));
                        var pixels = ((int)Math.Round(surface.RenderSize.Width * dpi), (int)Math.Round(surface.RenderSize.Height * dpi));
                        var request = bitmaps.Requests.Last(r => r.Page == page);
                        Assert.Equal(pixels, (request.Width, request.Height));
                        var shown = Assert.IsAssignableFrom<BitmapSource>(surface.PageImage);
                        Assert.Equal(pixels, (shown.PixelWidth, shown.PixelHeight));
                        Assert.False(view.Bitmaps.Scheduler!.TryGet(page, out var cached) && view.Bitmaps.Scheduler.IsStale(page, cached!));

                        // The ink layer shares the surface origin and maps page points with the exact scale: its far
                        // edge is within half a pixel of the bitmap's.
                        Assert.Equal(default, surface.CustomLayer.TranslatePoint(default, surface));
                        Assert.Equal(surface.Scale, surface.CustomLayer.Scale, 9);
                        Assert.InRange((_widthPt * surface.Scale) - surface.RenderSize.Width, -0.5 / dpi, 0.5 / dpi);
                        Assert.InRange((_heightPt * surface.Scale) - surface.RenderSize.Height, -0.5 / dpi, 0.5 / dpi);
                    }
                }
            },
            bitmaps);
    }

    [Fact]
    public async Task Pen_coming_near_a_page_without_strokes_loads_them_at_once()
    {
        var vm = await OpenFakeAsync();
        WpfTestThread.Run(() =>
        {
            var view = NewSlowLoadingView(vm);
            var surface = view.SurfaceFor(1)!;
            Assert.Null(surface.Editor); // a neighbour: its load waits for the debounce

            surface.NotifyPenNearby(); // what pen-in-range and pen-down do
            WpfTestThread.Drain();

            Assert.Equal(1, surface.Editor!.Page.Index); // no timer tick needed
            Assert.Null(view.SurfaceFor(2)!.Editor); // only the page under the pen
            surface.NotifyPenNearby(); // already loaded: nothing more
            Assert.Same(surface.Editor, view.SurfaceFor(1)!.Editor);
        });
    }

    [Fact]
    public async Task Pages_shown_by_a_jump_load_their_strokes_at_once()
    {
        var vm = await OpenFakeAsync();
        WpfTestThread.Run(() =>
        {
            var view = NewSlowLoadingView(vm);

            vm.Viewport.EndCommand.Execute(null);
            Settle(view);
            AssertVisibleLoadedAndNeighboursWaiting(vm, view);

            // A rail scrub that ends somewhere new is a jump too.
            vm.Viewport.BeginRailDrag();
            vm.Viewport.SetScroll(0, view.Pages.PageRect(14).Top + 50);
            Settle(view);
            Assert.Null(view.SurfaceFor(vm.Viewport.VisiblePages[0])!.Editor); // still scrubbing: nothing loaded
            vm.Viewport.EndRailDrag();
            Settle(view);
            AssertVisibleLoadedAndNeighboursWaiting(vm, view);
        });
    }

    [Fact]
    public async Task Wheel_and_arrow_keys_do_not_scroll_while_a_page_is_inking()
    {
        await WithViewAsync((vm, view) =>
        {
            Key[] arrows = [Key.Up, Key.Down, Key.Left, Key.Right];
            Assert.False(Wheel(view, -120).Handled); // the scroll viewer scrolls
            Assert.All(arrows, key => Assert.False(view.HandleNavigationKey(key, ModifierKeys.None, view.Viewport)));

            LoadEditors(view);
            var surface = view.SurfaceFor(0)!;
            MouseDown(surface);
            Assert.True(surface.IsInking);
            var before = view.Viewport.VerticalOffset;

            Assert.True(Wheel(view, -120).Handled); // the wheel, and a touchpad's two-finger scroll
            Assert.All(arrows, key => Assert.True(view.HandleNavigationKey(key, ModifierKeys.None, view.Viewport)));
            Assert.False(view.HandleNavigationKey(Key.Down, ModifierKeys.None, new TextBox())); // a field keeps its keys
            Settle(view);
            Assert.Equal(before, view.Viewport.VerticalOffset, 6);

            MouseUp(surface);
            Assert.False(Wheel(view, -120).Handled);
        });
    }

    [Fact]
    public async Task Only_a_page_without_any_bitmap_fades_in()
    {
        var bitmaps = new FakeBitmapRenderer { Auto = true };
        await WithViewAsync(
            (vm, view) =>
            {
                Idle(view);
                Assert.NotNull(view.SurfaceFor(0)!.PageImage);

                ScrollTo(view, view.Pages.PageRect(20).Top);
                Idle(view);
                ScrollTo(view, 0); // page 0 comes back from the cache

                var back = view.SurfaceFor(0)!;
                Assert.NotNull(back.PageImage);
                Assert.False(back.HasAnimatedProperties); // shown at once, no blink
                Assert.Equal(1, back.Opacity);
            },
            bitmaps);
    }

    private async Task<MainViewModel> OpenFakeAsync()
    {
        var source = new FakePageSource(Enumerable.Repeat((_widthPt, _heightPt), _pageCount).ToArray());
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = WriteFakeFile() }, scheduler: _scheduler);
        source.InstallOn(vm);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        return vm;
    }

    /// <summary>A view whose debounced stroke loads never fire by themselves, so only immediate loads show.</summary>
    private static DocumentView NewSlowLoadingView(MainViewModel vm)
    {
        var view = new DocumentView { EditorLoadDelay = TimeSpan.FromHours(1), BitmapRenderer = new FakeBitmapRenderer() };
        view.DataContext = vm;
        LayOut(view, 1000, 800);
        return view;
    }

    private static void AssertVisibleLoadedAndNeighboursWaiting(MainViewModel vm, DocumentView view)
    {
        var visible = vm.Viewport.VisiblePages;
        Assert.All(visible, page => Assert.Equal(page, view.SurfaceFor(page)!.Editor?.Page.Index));
        var neighbours = Retained(vm).Except(visible).Where(p => !vm.Session!.IsPageLoaded(p)).ToArray();
        Assert.NotEmpty(neighbours);
        Assert.All(neighbours, page => Assert.Null(view.SurfaceFor(page)!.Editor));
    }

    private static MouseWheelEventArgs Wheel(DocumentView view, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        view.Viewport.RaiseEvent(args);
        return args;
    }

    private Task WithViewAsync(Action<MainViewModel, DocumentView> test) => WithViewAsync(test, new FakeBitmapRenderer());

    /// <summary>
    /// Opens a 30-page generated PDF in a real view model, then runs the test on a view laid out at 1000 x 800 DIPs.
    /// <paramref name="bitmaps"/> null keeps the view's own renderer (PDFium through <see cref="PageBitmapRenderer"/>).
    /// </summary>
    private async Task WithViewAsync(Action<MainViewModel, DocumentView> test, IBitmapRenderer<BitmapSource>? bitmaps)
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
                    view.BitmapRenderer = bitmaps;
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

    /// <summary>Until no render is running or due and every bitmap event was delivered.</summary>
    private static void Idle(DocumentView view)
    {
        var scheduler = view.Bitmaps.Scheduler!;
        WpfTestThread.PumpUntil(() => scheduler.WhenIdleAsync().IsCompleted);
        WpfTestThread.Drain();
    }

    private static bool ShowsCurrentSharp(DocumentView view, int page) =>
        view.Bitmaps.Scheduler is { } scheduler
        && scheduler.TryGet(page, out var cached)
        && cached!.Quality == RenderQuality.Sharp
        && !scheduler.IsStale(page, cached)
        && ReferenceEquals(view.SurfaceFor(page)?.PageImage, cached.Bitmap);

    private static void Wait(Task task)
    {
        WpfTestThread.PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<int> Retained(MainViewModel vm)
    {
        var (first, last) = vm.Viewport.RetainedRange;
        return Enumerable.Range(first, last - first + 1);
    }

    /// <summary>A page point (in points) under a viewport point, from the exact layout and the real scroll offsets.</summary>
    private static Point PagePointUnder(DocumentView view, int page, Point viewportPoint)
    {
        var rect = view.Pages.PageRect(page);
        var scale = view.Pages.Scale;
        return new Point(
            (viewportPoint.X + view.Viewport.HorizontalOffset - rect.X) / scale,
            (viewportPoint.Y + view.Viewport.VerticalOffset - rect.Y) / scale);
    }

    /// <summary>Where a page point is in the viewport now, from the exact layout and the real scroll offsets.</summary>
    private static Point ViewportPointOf(DocumentView view, int page, Point pagePt)
    {
        var rect = view.Pages.PageRect(page);
        var scale = view.Pages.Scale;
        return new Point(
            rect.X + (pagePt.X * scale) - view.Viewport.HorizontalOffset,
            rect.Y + (pagePt.Y * scale) - view.Viewport.VerticalOffset);
    }

    /// <summary>The surface on screen is its exact layout rect snapped by at most half a pixel.</summary>
    private static void AssertSurfaceNearItsRect(DocumentView view, int page)
    {
        var surface = view.SurfaceFor(page)!;
        var exact = view.Pages.PageRect(page);
        var origin = surface.TranslatePoint(default, view.Pages);
        Assert.InRange(origin.X - exact.X, -0.5, 0.5);
        Assert.InRange(origin.Y - exact.Y, -0.5, 0.5);
    }

    private static void AssertWholePixels(double value) =>
        Assert.True(Math.Abs(value - Math.Round(value)) < 1e-6, $"{value} is not a whole device pixel");
}
