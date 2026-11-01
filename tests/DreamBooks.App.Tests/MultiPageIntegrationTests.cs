using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;
using DreamBooks.Core.Rendering;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;
using Xunit.Abstractions;

namespace DreamBooks.App.Tests;

/// <summary>
/// Cross-layer sweep of the multi-page viewer: the real <see cref="MainViewModel"/>, the real
/// <see cref="DocumentView"/> (never shown), PDFium rendering and the real save pipeline on generated PDFs, plus a
/// copy of a real e-book when one is available (NFR-02, NFR-05, NFR-06, FR-04, FR-26, FR-62, FR-80, FR-84).
/// Everything runs on the shared WPF test thread; asynchronous work is awaited by pumping its dispatcher. Real
/// documents are only read as copies and only sizes and times are reported (NFR-12).
/// </summary>
public sealed class MultiPageIntegrationTests : IDisposable
{
    private const string _realDocumentsFolder = @"C:\Users\Favsto\workspace\temp";
    private const string _fixturesVariable = "DREAMBOOKS_FIXTURES";
    private const double _widthPt = 612;
    private const double _heightPt = 792;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-integration-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly FakeFileDialogService _dialogs = new();

    public MultiPageIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    private string RecoveryRoot => Path.Combine(_dir, "recovery");

    private string PositionsRoot => Path.Combine(_dir, "positions");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_300_page_document_opens_fast_and_scrolling_it_keeps_surfaces_and_cache_bounded()
    {
        var path = WritePdf("big.pdf", 300);
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                unobserved.Add(e.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            WpfTestThread.Run(() =>
            {
                var scheduler = new ManualScheduler();
                var vm = NewVm(path, scheduler);
                var view = new DocumentView { DataContext = vm }; // PDFium bitmaps through the scheduler
                try
                {
                    var watch = Stopwatch.StartNew();
                    Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                    LayOut(view, 1000, 800);
                    var layoutMs = watch.ElapsedMilliseconds;
                    Assert.NotNull(view.SurfaceFor(0));
                    Assert.Equal(300, vm.Viewport.PageCount);
                    WpfTestThread.PumpUntil(() => view.SurfaceFor(0)!.PageImage is not null);
                    var firstBitmapMs = watch.ElapsedMilliseconds;
                    _output.WriteLine($"300 generated pages: open + layout {layoutMs} ms, first bitmap {firstBitmapMs} ms");
                    Assert.True(layoutMs < 1000, $"open + layout took {layoutMs} ms");

                    vm.ZoomVm.Zoom = 0.5;
                    Settle(view);
                    _output.WriteLine(ScrollThroughAndCheckBounds(vm, view, idleEvery: 25));
                    Assert.Equal(299, vm.Viewport.VisiblePages[^1]);
                }
                finally
                {
                    Close(vm, view);
                }
            });
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved)
        {
            Assert.Empty(unobserved);
        }
    }

    [Fact]
    public void A_real_e_book_scrolls_end_to_end_with_bounded_surfaces_cache_and_memory()
    {
        var path = CopyLongestRealDocument();
        if (path is null)
        {
            return; // skipped (reported in the output)
        }

        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler);
            var view = new DocumentView { DataContext = vm };
            var before = PrivateBytes();
            var peak = before;
            try
            {
                var watch = Stopwatch.StartNew();
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                var openMs = watch.ElapsedMilliseconds;
                LayOut(view, 1000, 800);
                var layoutMs = watch.ElapsedMilliseconds;
                WpfTestThread.PumpUntil(() => view.SurfaceFor(vm.Viewport.VisiblePages[0])!.PageImage is not null, 30_000);
                var firstBitmapMs = watch.ElapsedMilliseconds;
                Assert.Null(vm.ErrorMessage);

                watch.Restart();
                var summary = ScrollThroughAndCheckBounds(vm, view, idleEvery: 40, sample: () => peak = Math.Max(peak, PrivateBytes()));
                var scrollMs = watch.ElapsedMilliseconds;
                Idle(view);
                peak = Math.Max(peak, PrivateBytes());
                var after = PrivateBytes();
                _output.WriteLine(
                    $"pages {vm.Viewport.PageCount}: open {openMs} ms, open + layout {layoutMs} ms, first bitmap {firstBitmapMs} ms, " +
                    $"scroll through {scrollMs} ms; private memory delta peak {(peak - before) / (1024 * 1024)} MB, " +
                    $"end {(after - before) / (1024 * 1024)} MB; {summary}");
                Assert.True(peak - before < 400L * 1024 * 1024, $"memory grew by {(peak - before) / (1024 * 1024)} MB");
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    [Fact]
    public void Strokes_on_three_visible_pages_are_saved_and_come_back_on_reopen()
    {
        var path = WritePdf("three.pdf", 12);
        WpfTestThread.Run(() =>
        {
            var vm = NewVm(path, new ManualScheduler());
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            Guid[] ids;
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 2400);
                vm.ZoomVm.Zoom = 0.5;
                Settle(view);
                ScrollTo(view, view.Pages.PageRect(2).Top + 40);
                LoadEditors(view);
                var pages = vm.Viewport.VisiblePages.Take(3).ToArray();
                Assert.Equal(3, pages.Length);
                ids = pages.Select(page => PenLine(view.SurfaceFor(page)!, 100 + (page * 10))).ToArray();
                Assert.True(vm.IsDirty);

                Wait(vm.SaveCommand.ExecuteAsync(null));

                Assert.Null(vm.ErrorMessage);
                Assert.False(vm.IsDirty);
                Assert.Empty(Directory.GetFiles(_dir, "*.drbk-tmp"));

                // Reopened in another view model: every page has its own stroke back, the others none.
                var reopened = NewVm(path, new ManualScheduler());
                try
                {
                    Wait(reopened.OpenPdfCommand.ExecuteAsync(null));
                    for (var page = 0; page < 12; page++)
                    {
                        var editor = Wait(reopened.EnsureEditorAsync(page));
                        var index = Array.IndexOf(pages, page);
                        if (index >= 0)
                        {
                            Assert.Equal(ids[index], Assert.Single(editor.Page.Strokes).Id);
                        }
                        else
                        {
                            Assert.Empty(editor.Page.Strokes);
                        }
                    }

                    Assert.False(reopened.IsDirty);
                }
                finally
                {
                    DisposeRenderer(reopened);
                }
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void After_a_crash_strokes_and_the_page_relative_position_come_back_at_another_width(bool fitWidth)
    {
        var path = WritePdf("crash.pdf", 12);
        var (width, otherWidth) = fitWidth ? (500.0, 700.0) : (1000.0, 1400.0);
        WpfTestThread.Run(() =>
        {
            // Before the crash: three strokes on visible pages, then a scroll that settles (journalled).
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, withRecovery: true);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            int[] pages;
            Guid[] ids;
            ViewPosition shown;
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, width, 2400);
                Assert.True(vm.ZoomVm.IsFitWidth); // a document without a remembered position opens in fit width
                if (!fitWidth)
                {
                    vm.ZoomVm.Zoom = 0.5;
                    Settle(view);
                }

                ScrollTo(view, view.Pages.PageRect(1).Top + 30);
                LoadEditors(view);
                pages = vm.Viewport.VisiblePages.Take(3).ToArray();
                Assert.Equal(3, pages.Length);
                ids = pages.Select(page => PenLine(view.SurfaceFor(page)!, 150)).ToArray();

                ScrollTo(view, view.Pages.PageRect(3).Top + 123.4);
                scheduler.Advance(MainViewModel.ViewSettleDelay); // the settled view is journalled
                shown = vm.Viewport.Position;
                Assert.Equal(3, shown.PageIndex);
                Assert.Equal(fitWidth, vm.ZoomVm.IsFitWidth);
                Wait(vm.PositionWritesForTest);
            }
            finally
            {
                Wait(vm.SimulateCrashForTestAsync()); // the OS releases the journal and the lock
                view.DataContext = null;
                DisposeRenderer(vm);
            }

            // The remembered position of the saved file says something else: the journal's view must win.
            new ViewPositionStore(PositionsRoot).Save(path, new ViewPosition(0, 0, 306, 3));

            // After the restart, in a window of another width that is laid out before the restore (as at startup).
            var next = NewVm(path, new ManualScheduler(), withRecovery: true);
            var nextView = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = next };
            try
            {
                LayOut(nextView, otherWidth, 2400);
                Wait(next.TryRestoreLastSessionAsync());
                Settle(nextView);

                Assert.NotNull(next.Session);
                Assert.True(next.IsDirty);
                var position = next.Viewport.Position;
                var layout = next.Viewport.Layout!;
                Assert.Equal(shown.PageIndex, position.PageIndex);
                Assert.Equal(shown.YPt, position.YPt, 6); // the same spot of the same page
                Assert.Equal(fitWidth, next.ZoomVm.IsFitWidth);
                Assert.Equal(fitWidth ? layout.FitWidthZoom(otherWidth) : shown.Zoom, position.Zoom, 9);

                // The scroll viewer shows exactly that position.
                var (x, y) = layout.ToOffsets(position, next.Viewport.ViewportWidthDip, next.Viewport.ViewportHeightDip);
                Assert.InRange(nextView.Viewport.VerticalOffset - y, -0.5, 0.5);
                Assert.InRange(nextView.Viewport.HorizontalOffset - x, -0.5, 0.5);
                var topPt = nextView.Viewport.VerticalOffset / nextView.Pages.Scale;
                Assert.InRange(topPt - (layout.PageTopPt(shown.PageIndex) + shown.YPt), -0.5, 0.5);

                for (var i = 0; i < pages.Length; i++)
                {
                    var editor = Wait(next.EnsureEditorAsync(pages[i]));
                    Assert.Equal(ids[i], Assert.Single(editor.Page.Strokes).Id);
                }
            }
            finally
            {
                _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
                Assert.True(Wait(next.CanCloseAsync()));
                Close(next, nextView);
            }
        });
    }

    [Fact]
    public void Saving_while_scrolling_keeps_shown_bitmaps_navigation_and_ink_gating()
    {
        var path = WritePdf("busy.pdf", 30);
        WpfTestThread.Run(() =>
        {
            var vm = NewVm(path, new ManualScheduler());
            var view = new DocumentView { DataContext = vm }; // real PDFium bitmaps and a real renderer swap
            var writer = new GatedWriter(new PdfDocumentSaver());
            var replaceGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replaceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.SaveService = new DocumentSaveService(writer, _dialogs, async (temp, target) =>
            {
                replaceStarted.TrySetResult();
                await replaceGate.Task;
                await DocumentSaveService.ReplaceOnBackgroundThreadAsync(temp, target);
            });
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 2400);
                vm.ZoomVm.Zoom = 0.5;
                Settle(view);
                Assert.True(vm.Viewport.VisiblePages.Count >= 4);
                LoadEditors(view);
                var saved = PenLine(view.SurfaceFor(1)!, 100);

                // If a sharp bitmap was already shown at the fit zoom, the 50% zoom waits for its 150 ms settle and the
                // scheduler goes idle after the previews: apply the settle now (as its timer would), then wait.
                view.InvalidateBitmaps();
                Idle(view);
                Assert.All(vm.Viewport.VisiblePages, page => Assert.True(ShowsCurrentSharp(view, page), $"page {page}"));

                var saving = vm.SaveCommand.ExecuteAsync(null);
                WpfTestThread.PumpUntil(() => writer.Started.Task.IsCompleted);
                Assert.True(vm.IsSaving);

                // 1. While the copy is written, the renderer is still there: scrolling and navigation go on.
                ScrollTo(view, view.Pages.PageRect(3).Top + 50);
                Wait(vm.GoToPageCommand.ExecuteAsync(8));
                Assert.Equal(7, vm.Viewport.CurrentPage);
                Wait(vm.NextPageCommand.ExecuteAsync(null));
                Assert.Equal(8, vm.Viewport.CurrentPage);
                Assert.Null(vm.ErrorMessage);
                Idle(view);
                LoadEditors(view);
                Assert.All(vm.Viewport.VisiblePages, page => Assert.NotNull(view.SurfaceFor(page)!.PageImage));

                // 2. The plain Save releases the file: what is shown stays, even when the window asks again.
                writer.Gate.SetResult();
                WpfTestThread.PumpUntil(() => replaceStarted.Task.IsCompleted, 20_000);
                Assert.Null(vm.Renderer);
                var shown = ShownBitmaps(view);
                Assert.NotEmpty(shown);
                view.InvalidateBitmaps();
                Idle(view);
                AssertStillShown(view, shown);

                // Pages entering the window now get placeholders and accept no ink: their strokes cannot be loaded.
                Wait(vm.GoToPageCommand.ExecuteAsync(20));
                Assert.Equal(19, vm.Viewport.CurrentPage);
                Settle(view);
                view.FlushEditorLoads();
                Idle(view);
                WpfTestThread.Drain();
                var entered = vm.Viewport.VisiblePages.ToArray();
                Assert.All(entered, page =>
                {
                    var surface = view.SurfaceFor(page)!;
                    Assert.Null(surface.PageImage);
                    Assert.Null(surface.Editor);
                    Assert.False(surface.ActiveRenderer.Enabled);
                    Assert.False(surface.BeginEraseGesture(inverted: true, stylusId: 1));
                    MouseDown(surface);
                    Assert.False(surface.IsInking);
                });

                // Back on pages seen before the release: their cached bitmaps show at once, editors were kept.
                Wait(vm.GoToPageCommand.ExecuteAsync(8));
                Settle(view);
                shown = ShownBitmaps(view);
                Assert.All(vm.Viewport.VisiblePages, page => Assert.True(shown.ContainsKey(page), $"page {page}"));

                // 3. The renderer comes back on the saved file: no page that had a bitmap loses it during the swap, and
                // every visible page ends sharp at the new renderer's generation.
                replaceGate.SetResult();
                PumpWhileChecking(view, shown, () => saving.IsCompleted);
                saving.GetAwaiter().GetResult();
                Assert.False(vm.IsSaving);
                Assert.NotNull(vm.Renderer);
                Assert.True(vm.ErrorMessage is null || vm.ErrorMessage == Strings.ErrorPageChangeFailed, "unexpected error");
                PumpWhileChecking(view, shown, () => vm.Viewport.VisiblePages.All(page => ShowsCurrentSharp(view, page)));

                // The pages that entered during the release get their bitmaps and editors now, and accept ink.
                Wait(vm.GoToPageCommand.ExecuteAsync(20));
                Settle(view);
                WpfTestThread.PumpUntil(() => entered.All(page =>
                    ShowsCurrentSharp(view, page) && view.SurfaceFor(page)!.Editor is not null));
                foreach (var page in entered)
                {
                    var surface = view.SurfaceFor(page)!;
                    Assert.True(surface.ActiveRenderer.Enabled);
                    MouseDown(surface);
                    Assert.True(surface.IsInking);
                    surface.CancelLiveGesture(); // nothing committed
                }

                Assert.False(vm.IsDirty);
                var fresh = Wait(PdfRenderer.OpenAsync(path));
                Assert.Equal(saved, Assert.Single(Wait(fresh.LoadStrokesAsync(1)).Strokes).Id);
                Wait(fresh.DisposeAsync().AsTask());
            }
            finally
            {
                replaceGate.TrySetResult();
                writer.Gate.TrySetResult();
                Close(vm, view);
            }
        });
    }

    [Fact]
    public void Undo_of_an_erase_on_a_hidden_page_scrolls_to_it_and_undo_on_a_visible_page_does_not()
    {
        var path = WritePdf("undo.pdf", 10);
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                vm.ZoomVm.Zoom = 1;
                Settle(view);

                // A stroke on page 2, erased with the pen's back end.
                ScrollTo(view, view.Pages.PageRect(1).Top);
                LoadEditors(view);
                var page2 = view.SurfaceFor(1)!;
                PenLine(page2, 100);
                Assert.True(page2.BeginEraseGesture(inverted: true, stylusId: 5));
                var scale = page2.Scale;
                for (var x = 40.0; x <= 220; x += 4)
                {
                    page2.EraseAtView(new Point(x * scale, 110 * scale));
                }

                page2.EndEraseGesture();
                Assert.Empty(page2.Editor!.Page.Strokes);

                // On page 5, page 2 is out of view: undoing the erase brings it back into view (no Back entry).
                ScrollTo(view, view.Pages.PageRect(4).Top);
                scheduler.Advance(DocumentViewportViewModel.CurrentPageDelay);
                Assert.DoesNotContain(1, vm.Viewport.VisiblePages);
                Assert.Equal(4, vm.Session!.CurrentPageIndex);
                Wait(vm.UndoCommand.ExecuteAsync(null));
                Settle(view);

                Assert.Contains(1, vm.Viewport.VisiblePages);
                Assert.Equal(1, vm.Viewport.CurrentPage);
                Assert.InRange(view.Viewport.VerticalOffset - view.Pages.PageRect(1).Top, -0.5, 0.5);
                Assert.False(vm.Viewport.History.CanGoBack);
                Assert.True(vm.Session.TryGetEditor(1, out var editor));
                Assert.Single(editor!.Page.Strokes);
                Assert.Single(view.SurfaceFor(1)!.DisplayStrokes);

                // Page 2 is visible now (not at the top): undoing its stroke changes it in place, nothing scrolls.
                ScrollTo(view, view.Pages.PageRect(1).Top + 300);
                var before = vm.Viewport.Position;
                var offset = view.Viewport.VerticalOffset;
                Wait(vm.UndoCommand.ExecuteAsync(null));
                Settle(view);

                Assert.Empty(editor.Page.Strokes);
                Assert.Equal(before, vm.Viewport.Position);
                Assert.Equal(offset, view.Viewport.VerticalOffset, 6);
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    [Fact]
    public void Position_store_is_written_when_the_view_settles_and_on_close_never_for_a_failed_open()
    {
        var path = WritePdf("remember.pdf", 20);
        var broken = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(broken, "not a pdf at all");
        var store = new ViewPositionStore(PositionsRoot);
        WpfTestThread.Run(() =>
        {
            // A document that fails to open leaves no position file behind, also after closing.
            var failing = NewVm(broken, new ManualScheduler());
            Wait(failing.OpenPdfCommand.ExecuteAsync(null));
            Assert.Null(failing.Session);
            Assert.NotNull(failing.ErrorMessage);
            Assert.True(Wait(failing.CanCloseAsync()));
            Assert.False(Directory.Exists(PositionsRoot) && Directory.EnumerateFileSystemEntries(PositionsRoot).Any());

            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                ScrollTo(view, view.Pages.PageRect(6).Top + 77);
                scheduler.Advance(MainViewModel.ViewSettleDelay - TimeSpan.FromMilliseconds(1));
                Wait(vm.PositionWritesForTest);
                Assert.Null(store.TryLoad(path)); // not settled yet
                scheduler.Advance(TimeSpan.FromMilliseconds(1));
                Wait(vm.PositionWritesForTest);
                Assert.Equal(vm.Viewport.Position, store.TryLoad(path)?.Position);
                Assert.True(store.TryLoad(path)?.FitWidth);

                // A later scroll that never settles is written on close.
                ScrollTo(view, view.Pages.PageRect(11).Top + 5);
                var last = vm.Viewport.Position;
                Assert.Equal(11, last.PageIndex);
                Assert.True(Wait(vm.CanCloseAsync()));
                Assert.Equal(last, store.TryLoad(path)?.Position);
                Assert.Null(store.TryLoad(broken));
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    [Fact]
    public void A_fling_is_remembered_once_its_glide_has_settled_and_leaves_no_back_entry()
    {
        var path = WritePdf("fling.pdf", 20);
        var store = new ViewPositionStore(PositionsRoot);
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                ScrollTo(view, view.Pages.PageRect(3).Top);
                var before = vm.Viewport.Position;

                Assert.True(view.StartGlide(new System.Windows.Vector(0, -4)));
                var now = TimeSpan.FromSeconds(5);
                for (var i = 0; view.IsGliding; i++)
                {
                    Assert.True(i < 1000);
                    view.AdvanceGlide(now);
                    now += TimeSpan.FromMilliseconds(16);
                }

                var landed = vm.Viewport.Position;
                Assert.True(landed.PageIndex > before.PageIndex + 2, $"{before} -> {landed}");
                Assert.False(vm.Viewport.History.CanGoBack); // not a jump

                scheduler.Advance(MainViewModel.ViewSettleDelay - TimeSpan.FromMilliseconds(1));
                Wait(vm.PositionWritesForTest);
                Assert.Null(store.TryLoad(path)); // the glide's last step restarted the settle delay
                scheduler.Advance(TimeSpan.FromMilliseconds(1));
                Wait(vm.PositionWritesForTest);
                Assert.Equal(landed, store.TryLoad(path)?.Position);
                Assert.Equal(vm.Viewport.CurrentPage, vm.Session!.CurrentPageIndex);
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    /// <summary>
    /// Scrolls from the top to the bottom in steps of 90% of the viewport (renders are cancelled as pages fly past),
    /// waiting for the renders every <paramref name="idleEvery"/> steps. At every step the surfaces are the retained
    /// window only; at every wait the bitmap caches are within their budgets. Returns a summary in numbers.
    /// </summary>
    private static string ScrollThroughAndCheckBounds(MainViewModel vm, DocumentView view, int idleEvery, Action? sample = null)
    {
        var maxActive = view.ActiveSurfaces.Count;
        var maxCachedBytes = 0L;
        var maxCached = 0;
        void CheckCaches()
        {
            Idle(view);
            AssertCachesBounded(vm, view);
            maxCachedBytes = Math.Max(maxCachedBytes, view.Bitmaps.Scheduler!.CachedBytes);
            maxCached = Math.Max(maxCached, view.Bitmaps.Scheduler.CachedCount);
        }

        var step = view.Viewport.ViewportHeight * 0.9;
        var steps = 0;
        for (var y = 0.0; y <= view.Viewport.ScrollableHeight + step; y += step)
        {
            ScrollTo(view, Math.Min(y, view.Viewport.ScrollableHeight));
            var (first, last) = vm.Viewport.RetainedRange;
            Assert.Equal(Enumerable.Range(first, last - first + 1), view.ActiveSurfaces.Keys.Order());
            Assert.True(view.ActiveSurfaces.Count <= vm.Viewport.VisiblePages.Count + 4);
            Assert.Equal(view.ActiveSurfaces.Count, view.Pages.Children.Count);
            maxActive = Math.Max(maxActive, view.ActiveSurfaces.Count);
            if (++steps % idleEvery == 0)
            {
                CheckCaches();
                sample?.Invoke();
            }
        }

        CheckCaches();
        Assert.True(view.Pool.CreatedCount <= maxActive + PageSurfacePool.SpareCount,
            $"created {view.Pool.CreatedCount}, window {maxActive}");
        return $"{steps} scroll steps, surfaces created {view.Pool.CreatedCount} (window at most {maxActive}), " +
            $"bitmap cache at most {maxCachedBytes / (1024 * 1024)} MB in {maxCached} bitmaps";
    }

    private static void AssertCachesBounded(MainViewModel vm, DocumentView view)
    {
        // Pages that left the window may stay cached (least recently used first out) up to the byte budget: the cache
        // is bounded by bytes, not by the window. The per-page bookkeeping follows the window and the cache only.
        var scheduler = view.Bitmaps.Scheduler!;
        Assert.InRange(scheduler.CachedBytes, 0, PageBitmapCoordinator.DefaultByteBudget);
        Assert.InRange(view.Bitmaps.PreviewBytes, 0, PageBitmapCoordinator.PreviewCacheBudget);
        var (first, last) = vm.Viewport.RetainedRange;
        var window = last - first + 1;
        Assert.InRange(scheduler.TrackedPageCount, 0, (3 * window) + (2 * scheduler.CachedCount));
    }

    private MainViewModel NewVm(string path, ManualScheduler scheduler, bool withRecovery = false)
    {
        _dialogs.NextPath = path;
        return new MainViewModel(
            _dialogs,
            withRecovery ? new RecoveryStore(RecoveryRoot) : null,
            new ViewPositionStore(PositionsRoot),
            scheduler);
    }

    private string WritePdf(string name, int pages)
    {
        var path = Path.Combine(_dir, name);
        TestPdfFactory.Write(path, Enumerable.Repeat((_widthPt, _heightPt), pages).ToArray());
        return path;
    }

    /// <summary>A copy of the real PDF with the most pages, or null (reported) when there is none.</summary>
    private string? CopyLongestRealDocument()
    {
        var folder = Environment.GetEnvironmentVariable(_fixturesVariable) is { Length: > 0 } custom
            ? custom
            : _realDocumentsFolder;
        var candidates = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.pdf") : [];
        string? longest = null;
        var most = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                var count = Task.Run(async () =>
                {
                    await using var renderer = await PdfRenderer.OpenAsync(candidate);
                    return renderer.PageCount;
                }).GetAwaiter().GetResult();
                if (count > most)
                {
                    (longest, most) = (candidate, count);
                }
            }
            catch (Exception ex) when (ex is PdfOpenException or IOException)
            {
            }
        }

        if (longest is null || most < 100)
        {
            // xUnit 2 has no runtime skip: report it and pass.
            _output.WriteLine($"SKIPPED: no long real PDF in the fixtures folder (set {_fixturesVariable}).");
            return null;
        }

        var path = Path.Combine(_dir, "book.pdf");
        File.Copy(longest, path);
        return path;
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    /// <summary>A pen stroke on a surface (through its own editor, as the stylus handlers do); returns its id.</summary>
    private static Guid PenLine(InkSurface surface, double yPt)
    {
        var scale = surface.Scale;
        surface.BeginPenStroke(stylusId: 1, hasPressure: true);
        surface.AppendPenPoints([new(50 * scale, yPt * scale, 0.5f), new(200 * scale, (yPt + 20) * scale, 0.7f)], timestamp: 10);
        surface.FinishPenStroke();
        return surface.Editor!.Page.Strokes[^1].Id;
    }

    /// <summary>The bitmap each surface shows now, for the pages that show one.</summary>
    private static Dictionary<int, (InkSurface Surface, object Bitmap)> ShownBitmaps(DocumentView view) =>
        view.ActiveSurfaces
            .Where(pair => pair.Value.PageImage is not null)
            .ToDictionary(pair => pair.Key, pair => (pair.Value, (object)pair.Value.PageImage!));

    /// <summary>Every page that showed a bitmap and is still on the same surface shows one (the same or a newer one).</summary>
    private static void AssertStillShown(DocumentView view, Dictionary<int, (InkSurface Surface, object Bitmap)> shown)
    {
        foreach (var (page, (surface, _)) in shown)
        {
            if (ReferenceEquals(view.SurfaceFor(page), surface) && VirtualPagePanel.GetPageIndex(surface) == page)
            {
                Assert.True(surface.PageImage is not null, $"page {page} lost its bitmap");
            }
        }
    }

    /// <summary>Pumps until <paramref name="done"/>, checking at every turn that no shown page lost its bitmap.</summary>
    private static void PumpWhileChecking(DocumentView view, Dictionary<int, (InkSurface Surface, object Bitmap)> shown, Func<bool> done)
    {
        WpfTestThread.PumpUntil(
            () =>
            {
                AssertStillShown(view, shown);
                return done();
            },
            20_000);
        AssertStillShown(view, shown);
    }

    private static void Close(MainViewModel vm, DocumentView view)
    {
        view.DataContext = null;
        DisposeRenderer(vm);
    }

    private static void DisposeRenderer(MainViewModel vm)
    {
        if (vm.Renderer is { } renderer)
        {
            Wait(renderer.DisposeAsync().AsTask());
        }
    }

    private static void LayOut(DocumentView view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        Settle(view);
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

    private static void ScrollTo(DocumentView view, double y)
    {
        view.Viewport.ScrollToVerticalOffset(y);
        Settle(view);
    }

    private static void LoadEditors(DocumentView view)
    {
        view.FlushEditorLoads();
        WpfTestThread.PumpUntil(() => view.ActiveSurfaces.Values.All(s => s.Editor is not null));
    }

    /// <summary>Until no render is running or due and every bitmap event was delivered.</summary>
    private static void Idle(DocumentView view)
    {
        var scheduler = view.Bitmaps.Scheduler!;
        WpfTestThread.PumpUntil(() => scheduler.WhenIdleAsync().IsCompleted, 60_000);
        WpfTestThread.Drain();
    }

    /// <summary>
    /// The page shows a sharp bitmap of the current generation, rendered at the size of the current zoom (not a sharp
    /// bitmap of an earlier zoom whose settle is still pending).
    /// </summary>
    private static bool ShowsCurrentSharp(DocumentView view, int page) =>
        view.Bitmaps.Scheduler is { } scheduler
        && scheduler.TryGet(page, out var cached)
        && cached!.Quality == RenderQuality.Sharp
        && !scheduler.IsStale(page, cached)
        && (cached.PixelWidth, cached.PixelHeight) == PageBitmapRenderer.SharpSize(
            new PageSize(_widthPt, _heightPt), ((MainViewModel)view.DataContext).ZoomVm.Zoom, view.Pages.DpiScale)
        && ReferenceEquals(view.SurfaceFor(page)?.PageImage, cached.Bitmap);

    private static void MouseDown(InkSurface surface) =>
        surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });

    private static void Wait(Task task)
    {
        WpfTestThread.PumpUntil(() => task.IsCompleted, 60_000);
        task.GetAwaiter().GetResult();
    }

    private static T Wait<T>(Task<T> task)
    {
        WpfTestThread.PumpUntil(() => task.IsCompleted, 60_000);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>The real writer behind a gate, so a test can act while a save is between its steps.</summary>
    private sealed class GatedWriter(IDocumentWriter inner) : IDocumentWriter
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> WriteVerifiedCopyAsync(
            string sourcePath, string destinationPath, IReadOnlyList<PageChange> changes, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Gate.Task;
            return await inner.WriteVerifiedCopyAsync(sourcePath, destinationPath, changes, ct);
        }
    }
}
