using System.Windows.Media.Imaging;
using DreamBooks.App.Ink;
using DreamBooks.App.Views;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Rendering;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// Page bitmaps through the render scheduler: visible pages first, a preview then the sharp render, zoom/DPI/renderer
/// invalidation keeping the old bitmap stretched, cancellation of pages leaving the window, a bounded cache, the rail
/// preview, and no render on the UI thread (NFR-03, NFR-04, NFR-05, NFR-06, NFR-07, NFR-11). Runs on the WPF test
/// thread because the bitmaps go to real ink surfaces and the scheduler's events to its dispatcher.
/// </summary>
public sealed class PageBitmapCoordinatorTests
{
    private const double _widthPt = 300;
    private const double _heightPt = 400;
    private static readonly PageSize _page = new(_widthPt, _heightPt);

    [Fact]
    public void Visible_pages_are_requested_before_neighbours_and_far_pages_not_at_all()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30) { Renderer = { Auto = true } };
            f.Open();
            f.Show(3, 9, [5, 6]);
            f.Idle();

            var requests = f.Renderer.Requests;
            Assert.All(requests, r => Assert.InRange(r.Page, 3, 9));
            Assert.All(requests.Take(4), r => Assert.Contains(r.Page, new[] { 5, 6 }));
            var priorities = requests.Select(r => Math.Min(Math.Abs(r.Page - 5), Math.Abs(r.Page - 6))).ToArray();
            Assert.Equal(priorities.Order(), priorities); // by distance from the visible pages
            for (var page = 3; page <= 9; page++)
            {
                Assert.True(f.ShowsSharp(page), $"page {page}");
            }
        });
    }

    [Fact]
    public void Fast_scroll_shows_preview_first_then_sharp()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30);
            f.Open();
            f.Show(0, 2, [0]);
            var preview = PageBitmapRenderer.PreviewSize(_page, 1);
            var sharp = PageBitmapRenderer.SharpSize(_page, 1, 1);

            var first = f.WaitForPending(0);
            Assert.Equal((preview.Width, preview.Height), (first.Width, first.Height));
            f.Renderer.Complete(0);
            WpfTestThread.PumpUntil(() => f.Surfaces[0].PageImage is not null);
            Assert.Equal(preview.Width, ((BitmapSource)f.Surfaces[0].PageImage!).PixelWidth);

            var second = f.WaitForPending(0);
            Assert.Equal((sharp.Width, sharp.Height), (second.Width, second.Height));
            f.Renderer.Complete(0);
            WpfTestThread.PumpUntil(() => ((BitmapSource)f.Surfaces[0].PageImage!).PixelWidth == sharp.Width);

            // A fling far away: the new visible page gets its quick preview first.
            f.Show(10, 14, [12]);
            var far = f.WaitForPending(12);
            Assert.Equal((preview.Width, preview.Height), (far.Width, far.Height));
        });
    }

    [Fact]
    public void Zoom_settle_invalidates_and_keeps_old_bitmaps_stretched_until_replaced()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30) { Renderer = { Auto = true } };
            f.Open();
            f.Show(0, 2, [0]);
            f.Idle();
            var before = f.Surfaces[0].PageImage;
            Assert.True(f.ShowsSharp(0));
            var count = f.Renderer.Requests.Length;
            f.Renderer.Auto = false;

            // A pinch: no render at any intermediate zoom, the old bitmap stays (stretched by the surface).
            f.Coordinator.SetZoom(1.5);
            f.Delays.Advance(100);
            f.Coordinator.SetZoom(2);
            f.Delays.Advance(100);
            f.Idle();
            Assert.Equal(count, f.Renderer.Requests.Length);
            Assert.Same(before, f.Surfaces[0].PageImage);

            f.Coordinator.SetZoom(2); // scrolling re-applies the same zoom: it does not postpone the settle
            f.Delays.Advance(60); // 160 ms since the zoom last changed: settled
            var sharp2 = PageBitmapRenderer.SharpSize(_page, 2, 1);
            var request = f.WaitForPending(0);
            Assert.Equal((sharp2.Width, sharp2.Height), (request.Width, request.Height)); // straight to sharp
            Assert.All(f.Renderer.Requests.Skip(count), r => Assert.Equal(sharp2.Width, r.Width));
            Assert.Same(before, f.Surfaces[0].PageImage);
            Assert.True(f.IsStale(0));

            f.Renderer.Complete(0);
            WpfTestThread.PumpUntil(() => !ReferenceEquals(before, f.Surfaces[0].PageImage));
            Assert.Equal(sharp2.Width, ((BitmapSource)f.Surfaces[0].PageImage!).PixelWidth);
            Assert.False(f.IsStale(0));
        });
    }

    [Fact]
    public void Dpi_change_invalidates()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30) { Renderer = { Auto = true } };
            f.Open();
            f.Show(0, 2, [0]);
            f.Idle();
            var before = f.Surfaces[0].PageImage;
            f.Renderer.Auto = false;

            f.Coordinator.SetDpiScale(1.5); // no debounce: same DIPs, more pixels
            Assert.Equal(0, f.Delays.Pending);
            var sharp = PageBitmapRenderer.SharpSize(_page, 1, 1.5);
            var request = f.WaitForPending(0);
            Assert.Equal((sharp.Width, sharp.Height), (request.Width, request.Height));
            Assert.Same(before, f.Surfaces[0].PageImage);
            Assert.True(f.IsStale(0));

            f.Renderer.Complete(0);
            WpfTestThread.PumpUntil(() => ((BitmapSource)f.Surfaces[0].PageImage!).PixelWidth == sharp.Width);
            Assert.False(f.IsStale(0));
        });
    }

    [Fact]
    public void Renderer_swap_after_save_retries_failed_pages()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30) { Renderer = { Fails = _ => true } }; // the file is released by a save
            f.Open();
            f.Show(0, 0, [0]);
            f.Idle();
            f.Show(0, 0, [0]); // one retry at the next window update
            f.Idle();
            f.Show(0, 0, [0]); // then it waits for an invalidation: no tight loop
            f.Idle();
            Assert.Equal(2, f.Renderer.Requests.Length);
            Assert.Null(f.Surfaces[0].PageImage); // the placeholder stays

            f.Renderer.Fails = null; // the new renderer is in place
            f.Renderer.Auto = true;
            f.Coordinator.InvalidateAll();
            f.Idle();
            Assert.True(f.ShowsSharp(0));
        });
    }

    [Fact]
    public void Pages_that_leave_the_window_are_cancelled()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30);
            f.Open();
            f.Show(0, 2, [0]);
            var request = f.WaitForPending(0);

            f.Show(20, 24, [22]);
            WpfTestThread.PumpUntil(() => request.Token.IsCancellationRequested);
            f.WaitForPending(22);
            f.Renderer.Auto = true;
            f.Renderer.CompletePending();
            f.Idle();
            Assert.Same(request, f.Renderer.Requests[0]);
            Assert.All(f.Renderer.Requests.Skip(1), r => Assert.InRange(r.Page, 20, 24)); // nothing more for the old pages
            Assert.False(f.Coordinator.Scheduler!.TryGet(0, out _)); // the cancelled render stored nothing
        });
    }

    [Fact]
    public void Cache_stays_within_budget_while_scrolling_300_pages()
    {
        WpfTestThread.Run(() =>
        {
            // A window of 6 pages does not fit: 2 visible sharp pages and 4 neighbour previews already exceed it.
            const long budget = 3L * 1024 * 1024;
            using var f = new Fixture(300, budget) { Renderer = { Auto = true } };
            f.Open();
            var sharp = PageBitmapRenderer.SharpSize(_page, 1, 1);
            var preview = PageBitmapRenderer.PreviewSize(_page, 1);
            var sharpBytes = (long)sharp.Width * sharp.Height * 4;
            var previewBytes = (long)preview.Width * preview.Height * 4;
            Assert.True((2 * sharpBytes) + (4 * previewBytes) > budget);
            var neighboursKeptAsPreviews = 0;
            for (var top = 0; top < 300; top += 3)
            {
                var last = Math.Min(299, top + 1);
                var (first, end) = (Math.Max(0, top - 2), Math.Min(299, last + 2));
                f.Show(first, end, [top, last]);
                f.Idle();
                var scheduler = f.Coordinator.Scheduler!;
                Assert.True(f.ShowsSharp(top));
                Assert.True(f.ShowsSharp(last));

                // Over budget: nothing outside the window is kept, and the neighbours get no sharp render (the guard).
                Assert.InRange(scheduler.CachedCount, 1, end - first + 1);
                Assert.InRange(scheduler.CachedBytes, 0, budget + (2 * sharpBytes) + ((end - first + 1) * previewBytes));
                for (var page = first; page <= end; page++)
                {
                    if (page != top && page != last && scheduler.TryGet(page, out var cached) && cached!.Quality == RenderQuality.Preview)
                    {
                        neighboursKeptAsPreviews++;
                    }
                }

                Assert.InRange(f.Coordinator.PreviewBytes, 0, PageBitmapCoordinator.PreviewCacheBudget);
            }

            Assert.True(neighboursKeptAsPreviews > 100, $"{neighboursKeptAsPreviews} neighbours kept as previews");
            Assert.InRange(f.Renderer.Requests.Length, 1, 300 * 3);
        });
    }

    [Fact]
    public void Failure_never_throws_and_leaves_the_placeholder()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30) { Renderer = { Throws = _ => true } };
            f.Open();
            f.Show(0, 2, [0, 1]);
            f.Idle();
            Assert.NotEmpty(f.Renderer.Requests);
            Assert.All(f.Surfaces.Values, s => Assert.Null(s.PageImage));

            var preview = f.Coordinator.GetPreviewAsync(4, CancellationToken.None);
            WpfTestThread.PumpUntil(() => preview.IsCompleted);
            Assert.True(preview.IsCompletedSuccessfully);
            Assert.Null(preview.Result);
        });
    }

    [Fact]
    public async Task Pdf_bitmap_renderer_without_a_document_or_with_a_closed_one_returns_null()
    {
        var renderer = new PageBitmapRenderer();
        Assert.Null(await renderer.RenderAsync(0, 10, 10, CancellationToken.None));

        var path = Path.Combine(Path.GetTempPath(), "dreambooks-bitmaps-" + Guid.NewGuid().ToString("N") + ".pdf");
        TestPdfFactory.Write(path, [(_widthPt, _heightPt)]);
        try
        {
            var pdf = await PdfRenderer.OpenAsync(path);
            renderer.Renderer = pdf;
            var bitmap = await renderer.RenderAsync(0, 30, 40, CancellationToken.None);
            Assert.Equal((30, 40), (bitmap!.PixelWidth, bitmap.PixelHeight));
            Assert.True(bitmap.IsFrozen);

            await pdf.DisposeAsync(); // e.g. released by a save before the swap
            Assert.Null(await renderer.RenderAsync(0, 30, 40, CancellationToken.None));
            Assert.Null(await renderer.RenderAsync(5, 30, 40, CancellationToken.None)); // out of range
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Preview_for_the_rail_is_small_and_cached()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(300) { Renderer = { Auto = true } };
            f.Open(dpiScale: 1.5);
            var expected = PageBitmapRenderer.PreviewSize(_page, 1.5);
            Assert.Equal((int)Math.Round(PageBitmapRenderer.PreviewWidthDip * 1.5), expected.Width);

            var first = Await(f.Coordinator.GetPreviewAsync(40, CancellationToken.None))!;
            Assert.Equal((expected.Width, expected.Height), (first.PixelWidth, first.PixelHeight));
            var count = f.Renderer.Requests.Length;
            Assert.Same(first, Await(f.Coordinator.GetPreviewAsync(40, CancellationToken.None)));
            Assert.Equal(count, f.Renderer.Requests.Length);

            // A preview the scheduler already rendered for the page is reused.
            f.Show(7, 7, [7]);
            f.Idle();
            count = f.Renderer.Requests.Length;
            Assert.NotNull(Await(f.Coordinator.GetPreviewAsync(7, CancellationToken.None)));
            Assert.Equal(count, f.Renderer.Requests.Length);

            // Bounded while scrubbing the whole book.
            for (var page = 0; page < 300; page += 2)
            {
                Assert.NotNull(Await(f.Coordinator.GetPreviewAsync(page, CancellationToken.None)));
                Assert.InRange(f.Coordinator.PreviewBytes, 0, PageBitmapCoordinator.PreviewCacheBudget);
            }

            // A very tall page keeps its preview small.
            var (w, h) = PageBitmapRenderer.PreviewSize(new PageSize(100, 20_000), 2);
            Assert.InRange(w, 1, 600); // within a box 300 DIPs wide and twice as tall, at the screen scale
            Assert.Equal(1200, h);
        });
    }

    [Fact]
    public void No_render_is_requested_on_the_UI_thread()
    {
        WpfTestThread.Run(() =>
        {
            var ui = Environment.CurrentManagedThreadId;
            using var f = new Fixture(30) { Renderer = { Auto = true } };
            f.Open();
            f.Show(0, 3, [0, 1]);
            f.Idle();
            f.Coordinator.SetZoom(2);
            f.Delays.Advance(200);
            f.Coordinator.SetDpiScale(2);
            f.Coordinator.InvalidateAll();
            f.Idle();
            Assert.NotNull(Await(f.Coordinator.GetPreviewAsync(20, CancellationToken.None)));

            Assert.True(f.Renderer.Requests.Length > 4);
            Assert.All(f.Renderer.Requests, r => Assert.NotEqual(ui, r.ThreadId));
        });
    }

    [Fact]
    public void Pages_entering_the_window_while_a_zoom_settles_get_only_previews()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30) { Renderer = { Auto = true } };
            f.Open();
            f.Show(0, 2, [0]);
            f.Idle();
            var count = f.Renderer.Requests.Length;

            f.Coordinator.SetZoom(2); // a pinch: the settle is pending
            f.Show(5, 9, [6, 7]); // new pages come into view during the gesture
            f.Idle();
            var preview = PageBitmapRenderer.PreviewSize(_page, 1);
            var during = f.Renderer.Requests.Skip(count).ToArray();
            Assert.NotEmpty(during);
            Assert.All(during, r => Assert.Equal((preview.Width, preview.Height), (r.Width, r.Height)));
            Assert.All(Enumerable.Range(5, 5), page => Assert.NotNull(f.Surfaces[page].PageImage)); // never blank

            count = f.Renderer.Requests.Length;
            f.Delays.Advance(150); // settled: one sharp render per page, at the new size only
            f.Idle();
            var sharp2 = PageBitmapRenderer.SharpSize(_page, 2, 1);
            var after = f.Renderer.Requests.Skip(count).ToArray();
            Assert.All(after, r => Assert.Equal((sharp2.Width, sharp2.Height), (r.Width, r.Height)));
            Assert.Equal(Enumerable.Range(5, 5), after.Select(r => r.Page).Order());
            Assert.True(f.ShowsSharp(6));
            Assert.True(f.ShowsSharp(7));
        });
    }

    [Fact]
    public void A_zoom_change_before_anything_sharp_is_shown_applies_at_once()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30);
            f.Open(); // e.g. a provisional zoom before the first layout
            f.Show(0, 0, [0]);
            f.WaitForPending(0);

            f.Coordinator.SetZoom(2); // fit width computed at the first layout: nothing to keep stretched
            Assert.Equal(0, f.Delays.Pending);
            f.Renderer.Auto = true;
            f.Renderer.CompletePending();
            f.Idle();
            var sharp2 = PageBitmapRenderer.SharpSize(_page, 2, 1);
            var sharp1 = PageBitmapRenderer.SharpSize(_page, 1, 1);
            Assert.DoesNotContain(f.Renderer.Requests, r => r.Width == sharp1.Width);
            Assert.Equal(sharp2.Width, ((BitmapSource)f.Surfaces[0].PageImage!).PixelWidth);
        });
    }

    [Fact]
    public void A_preview_delivered_after_a_dpi_change_does_not_enter_the_rail_cache()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30);
            f.Open();
            f.Show(0, 0, [0]);
            f.WaitForPending(0);
            f.Renderer.Complete(0); // the preview at the old scale: stored, its event not yet delivered
            var scheduler = f.Coordinator.Scheduler!;
            Assert.True(SpinWait.SpinUntil(() => scheduler.CachedCount == 1, 5000));

            f.Coordinator.SetDpiScale(2);
            WpfTestThread.PumpUntil(() => f.Surfaces[0].PageImage is not null); // the late event (shown, stretched)

            Assert.Equal(0, f.Coordinator.PreviewBytes);
        });
    }

    [Fact]
    public void Rail_previews_are_shared_per_page_and_the_latest_request_wins()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30);
            f.Open();
            var a = f.Coordinator.GetPreviewAsync(5, CancellationToken.None);
            var b = f.Coordinator.GetPreviewAsync(5, CancellationToken.None);
            var first = f.WaitForPending(5);
            WpfTestThread.Drain();
            Assert.Single(f.Renderer.Requests, r => r.Page == 5); // one render for both callers

            var c = f.Coordinator.GetPreviewAsync(6, CancellationToken.None); // the knob moved on
            WpfTestThread.PumpUntil(() => first.Token.IsCancellationRequested && a.IsCompleted && b.IsCompleted);
            Assert.Null(a.Result);
            Assert.Null(b.Result);

            f.WaitForPending(6);
            f.Renderer.Complete(6);
            var preview = PageBitmapRenderer.PreviewSize(_page, 1);
            Assert.Equal(preview.Width, Await(c)!.PixelWidth);
            Assert.Single(f.Renderer.Requests, r => r.Page == 6);
        });
    }

    [Fact]
    public void A_cancelled_rail_request_leaves_no_stuck_entry()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30) { Renderer = { HonourCancellation = false } }; // the render finishes anyway
            f.Open();
            using var cts = new CancellationTokenSource();
            var a = f.Coordinator.GetPreviewAsync(5, cts.Token);
            var first = f.WaitForPending(5);

            cts.Cancel(); // the only caller gave up: the render is cancelled too
            WpfTestThread.PumpUntil(() => a.IsCompleted);
            Assert.Null(a.Result);
            Assert.True(first.Token.IsCancellationRequested);

            var b = f.Coordinator.GetPreviewAsync(5, CancellationToken.None); // not joined to the abandoned render
            WpfTestThread.PumpUntil(() => f.Renderer.Requests.Count(r => r.Page == 5) == 2);
            f.Renderer.Complete(5);
            Assert.NotNull(Await(b));

            f.Renderer.CompleteAll(5); // the abandoned render ends late: harmless
            WpfTestThread.Drain();
            Assert.Same(b.Result, Await(f.Coordinator.GetPreviewAsync(5, CancellationToken.None)));
        });
    }

    [Fact]
    public void Thumbnail_renders_wait_until_no_page_render_is_due_newest_first_and_cancel_when_abandoned()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30);
            f.Open(dpiScale: 1.5);
            f.Show(0, 2, [0]);
            var thumb = PageBitmapRenderer.ThumbnailSize(_page, 1.5);
            Assert.Equal((int)Math.Round(PageBitmapRenderer.ThumbnailWidthDip * 1.5), thumb.Width);
            Assert.InRange(thumb.Height, (int)(thumb.Width * _heightPt / _widthPt) - 1, (int)(thumb.Width * _heightPt / _widthPt) + 1);

            using var abandon = new CancellationTokenSource();
            var a = f.Coordinator.RenderThumbnailAsync(10, CancellationToken.None);
            var b = f.Coordinator.RenderThumbnailAsync(11, abandon.Token);
            var c = f.Coordinator.RenderThumbnailAsync(12, CancellationToken.None);
            f.WaitForPending(0);

            // Page renders are due: no thumbnail render starts, however long they take.
            for (var i = 0; i < 5; i++)
            {
                f.Delays.Advance(PageBitmapCoordinator.ThumbnailIdlePoll);
                WpfTestThread.Drain();
            }

            Assert.DoesNotContain(f.Renderer.Requests, r => r.Page >= 10);
            abandon.Cancel(); // its row was recycled: it leaves the queue without a render
            WpfTestThread.PumpUntil(() => b.IsCompleted);
            Assert.Null(b.Result);

            // Renders done: the thumbnails follow one at a time, the most recently asked first.
            f.Renderer.Auto = true;
            f.Renderer.CompletePending();
            f.Idle();
            f.Renderer.Auto = false;
            WpfTestThread.PumpUntil(() =>
            {
                f.Delays.Advance(PageBitmapCoordinator.ThumbnailIdlePoll);
                return f.Renderer.Pending.Any(r => r.Page >= 10);
            });
            Assert.Equal([12], f.Renderer.Pending.Where(r => r.Page >= 10).Select(r => r.Page));
            Assert.Equal((thumb.Width, thumb.Height), (f.Renderer.Pending.Last().Width, f.Renderer.Pending.Last().Height));

            // A visible page wanted again while a thumbnail runs: the next thumbnail waits for it.
            f.Show(20, 22, [21]);
            f.Renderer.Complete(12);
            Assert.Equal(thumb.Width, Await(c)!.PixelWidth);
            f.WaitForPending(21);
            f.Delays.Advance(PageBitmapCoordinator.ThumbnailIdlePoll);
            WpfTestThread.Drain();
            Assert.DoesNotContain(f.Renderer.Requests, r => r.Page == 10);
            f.Renderer.Auto = true;
            f.Renderer.CompletePending();
            f.Idle();
            WpfTestThread.PumpUntil(() =>
            {
                f.Delays.Advance(PageBitmapCoordinator.ThumbnailIdlePoll);
                return a.IsCompleted;
            });
            Assert.NotNull(a.Result);
            Assert.DoesNotContain(f.Renderer.Requests, r => r.Page == 11);
            var uiThread = Environment.CurrentManagedThreadId;
            Assert.All(f.Renderer.Requests, r => Assert.NotEqual(uiThread, r.ThreadId)); // never on the UI thread

            // Another document: queued thumbnails end with nothing, a failing render yields null, never an exception.
            f.Renderer.Auto = false;
            f.Show(0, 2, [0]);
            var queued = f.Coordinator.RenderThumbnailAsync(15, CancellationToken.None);
            f.Coordinator.SetDocument(null);
            WpfTestThread.PumpUntil(() => queued.IsCompleted);
            Assert.Null(queued.Result);
            Assert.Null(Await(f.Coordinator.RenderThumbnailAsync(3, CancellationToken.None)));
        });
    }

    [Fact]
    public void Other_thumbnail_work_waits_in_the_same_low_priority_queue()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30);
            f.Open();
            f.Show(0, 2, [0]);
            f.WaitForPending(0);
            var runs = 0;
            var ink = f.Coordinator.RunWhenIdleAsync(
                _ =>
                {
                    runs++;
                    return Task.FromResult<string?>("ink");
                },
                CancellationToken.None);
            f.Delays.Advance(PageBitmapCoordinator.ThumbnailIdlePoll);
            WpfTestThread.Drain();
            Assert.Equal(0, runs); // a page render is due

            f.Renderer.Auto = true;
            f.Renderer.CompletePending();
            f.Idle();
            WpfTestThread.PumpUntil(() =>
            {
                f.Delays.Advance(PageBitmapCoordinator.ThumbnailIdlePoll);
                return ink.IsCompleted;
            });
            Assert.Equal("ink", ink.Result);
            Assert.Equal(1, runs);
        });
    }

    [Fact]
    public void Thumbnails_wait_for_the_first_zoom_and_while_a_zoom_settles()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30) { Renderer = { Auto = true } };

            // A document whose view has not asked for any zoom yet: no thumbnail goes ahead of its first pages.
            f.Coordinator.SetDocument(f.Layout);
            var early = f.Coordinator.RenderThumbnailAsync(10, CancellationToken.None);
            f.Delays.Advance(PageBitmapCoordinator.ThumbnailIdlePoll);
            WpfTestThread.Drain();
            Assert.Empty(f.Renderer.Requests);
            Assert.False(early.IsCompleted);

            f.Coordinator.SetZoom(1);
            f.Show(0, 1, [0]);
            f.Idle();
            WpfTestThread.PumpUntil(() =>
            {
                f.Delays.Advance(PageBitmapCoordinator.ThumbnailIdlePoll);
                return early.IsCompleted;
            });
            Assert.NotNull(early.Result);

            // A pinch: while the zoom settles (its renders are about to come), thumbnails wait.
            f.Coordinator.SetZoom(2);
            var pinched = f.Coordinator.RenderThumbnailAsync(11, CancellationToken.None);
            WpfTestThread.Drain();
            Assert.False(pinched.IsCompleted);
            Assert.DoesNotContain(f.Renderer.Requests, r => r.Page == 11);
            f.Delays.Advance(PageBitmapCoordinator.ZoomSettleDelay); // settled: the sharp renders run first
            f.Idle();
            WpfTestThread.PumpUntil(() =>
            {
                f.Delays.Advance(PageBitmapCoordinator.ThumbnailIdlePoll);
                return pinched.IsCompleted;
            });
            Assert.NotNull(pinched.Result);
            var sharp2 = PageBitmapRenderer.SharpSize(_page, 2, 1);
            var thumbnailAt = Array.FindIndex(f.Renderer.Requests, r => r.Page == 11);
            Assert.True(Array.FindLastIndex(f.Renderer.Requests, r => r.Width == sharp2.Width) < thumbnailAt);
        });
    }

    [Fact]
    public void The_view_is_told_when_a_zoom_starts_and_when_it_has_settled()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30) { Renderer = { Auto = true } };
            f.Coordinator.SetDocument(f.Layout);
            f.Coordinator.SetZoom(1);
            f.Show(0, 1, [0]);
            f.Idle(); // a sharp bitmap is on screen: later zooms settle first
            var changes = new List<bool>();
            f.Coordinator.ZoomSettlingChanged += (_, settling) => changes.Add(settling);

            f.Coordinator.SetZoom(2);
            f.Coordinator.SetZoom(2.2); // a pinch goes on: still the same settling
            Assert.Equal([true], changes);
            Assert.True(f.Coordinator.IsZoomSettling);

            f.Delays.Advance(PageBitmapCoordinator.ZoomSettleDelay);
            Assert.Equal([true, false], changes);
            Assert.False(f.Coordinator.IsZoomSettling);
        });
    }

    [Fact]
    public void A_screen_scale_change_and_a_renderer_swap_are_signalled_to_the_thumbnails()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30);
            f.Open();
            var (scale, retry) = (0, 0);
            f.Coordinator.ThumbnailScaleChanged += (_, _) => scale++;
            f.Coordinator.ThumbnailRetryRequested += (_, _) => retry++;

            f.Coordinator.InvalidateAll(); // the renderer is back after a save
            Assert.Equal((0, 1), (scale, retry));
            f.Coordinator.SetDpiScale(2);
            Assert.Equal(1, scale);
            f.Coordinator.SetDpiScale(2); // unchanged: nothing
            Assert.Equal(1, scale);
        });
    }

    [Fact]
    public void Thumbnails_wait_while_a_save_has_the_file_released()
    {
        WpfTestThread.Run(() =>
        {
            var released = new PageBitmapRenderer(); // no PdfRenderer: as during a plain save
            using var coordinator = new PageBitmapCoordinator(released, new ManualScheduler(), _ => null);
            coordinator.SetDocument(new DocumentLayout(Enumerable.Repeat(new PageDimensions(_widthPt, _heightPt), 5).ToArray()));
            coordinator.SetZoom(1);
            coordinator.SetWindow((0, 1), [0]);
            WpfTestThread.PumpUntil(() => !coordinator.HasPendingRenders); // the page renders fail meanwhile

            var thumbnail = coordinator.RenderThumbnailAsync(3, CancellationToken.None);
            WpfTestThread.Drain();
            Assert.False(thumbnail.IsCompleted); // queued, not failed: it renders once the file is back
            Assert.Equal(1, coordinator.PendingThumbnailCount);
        });
    }

    [Fact]
    public void A_failing_thumbnail_render_yields_null()
    {
        WpfTestThread.Run(() =>
        {
            using var f = new Fixture(30) { Renderer = { Auto = true, Throws = page => page == 7, Fails = page => page == 8 } };
            f.Open();
            f.Show(0, 1, [0]);
            f.Idle();
            var thrown = f.Coordinator.RenderThumbnailAsync(7, CancellationToken.None);
            var failed = f.Coordinator.RenderThumbnailAsync(8, CancellationToken.None);
            WpfTestThread.PumpUntil(() =>
            {
                f.Delays.Advance(PageBitmapCoordinator.ThumbnailIdlePoll);
                return thrown.IsCompleted && failed.IsCompleted;
            });
            Assert.True(thrown.IsCompletedSuccessfully);
            Assert.Null(thrown.Result);
            Assert.Null(failed.Result);
            Assert.NotNull(Await(f.Coordinator.RenderThumbnailAsync(9, CancellationToken.None)));
        });
    }

    private static T Await<T>(Task<T> task)
    {
        WpfTestThread.PumpUntil(() => task.IsCompleted);
        return task.Result;
    }

    /// <summary>A coordinator over a fake renderer, with real surfaces for the pages of the window.</summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture(int pageCount, long budget = PageBitmapCoordinator.DefaultByteBudget)
        {
            Layout = new DocumentLayout(Enumerable.Repeat(new PageDimensions(_widthPt, _heightPt), pageCount).ToArray());
            Coordinator = new PageBitmapCoordinator(Renderer, Delays, page => Surfaces.GetValueOrDefault(page), budget);
        }

        public FakeBitmapRenderer Renderer { get; } = new();

        public ManualScheduler Delays { get; } = new();

        public Dictionary<int, InkSurface> Surfaces { get; } = [];

        public DocumentLayout Layout { get; }

        public PageBitmapCoordinator Coordinator { get; }

        public void Open(double dpiScale = 1)
        {
            Coordinator.SetDocument(Layout);
            Coordinator.SetDpiScale(dpiScale);
            Coordinator.SetZoom(1);
        }

        /// <summary>As the view does: surfaces for the window (cached bitmaps shown at once), then the new window.</summary>
        public void Show(int first, int last, int[] visible)
        {
            foreach (var page in Surfaces.Keys.Where(p => p < first || p > last).ToArray())
            {
                Surfaces.Remove(page);
            }

            for (var page = first; page <= last; page++)
            {
                if (!Surfaces.ContainsKey(page))
                {
                    Surfaces[page] = new InkSurface();
                    Coordinator.ShowCached(page);
                }
            }

            Coordinator.SetWindow((first, last), visible);
        }

        /// <summary>Until no render is running or due and every event was delivered.</summary>
        public void Idle()
        {
            var scheduler = Coordinator.Scheduler!;
            WpfTestThread.PumpUntil(() => scheduler.WhenIdleAsync().IsCompleted);
            WpfTestThread.Drain();
        }

        public FakeBitmapRenderer.Request WaitForPending(int page)
        {
            WpfTestThread.PumpUntil(() => Renderer.Pending.Any(r => r.Page == page && !r.Token.IsCancellationRequested));
            return Renderer.Pending.Last(r => r.Page == page);
        }

        public bool IsStale(int page) =>
            Coordinator.Scheduler!.TryGet(page, out var cached) && Coordinator.Scheduler.IsStale(page, cached!);

        /// <summary>The surface shows the current sharp bitmap of its page.</summary>
        public bool ShowsSharp(int page) =>
            Coordinator.Scheduler!.TryGet(page, out var cached)
            && cached!.Quality == RenderQuality.Sharp
            && !Coordinator.Scheduler.IsStale(page, cached)
            && Surfaces.TryGetValue(page, out var surface)
            && ReferenceEquals(surface.PageImage, cached.Bitmap);

        public void Dispose() => Coordinator.Dispose();
    }
}
