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
            const long budget = 4L * 1024 * 1024;
            using var f = new Fixture(300, budget) { Renderer = { Auto = true } };
            f.Open();
            var sharp = PageBitmapRenderer.SharpSize(_page, 1, 1);
            var sharpBytes = (long)sharp.Width * sharp.Height * 4;
            for (var top = 0; top < 300; top += 3)
            {
                var last = Math.Min(299, top + 1);
                f.Show(Math.Max(0, top - 2), Math.Min(299, last + 2), [top, last]);
                f.Idle();
                Assert.True(f.ShowsSharp(top));
                Assert.InRange(f.Coordinator.Scheduler!.CachedBytes, 0, budget + (6 * sharpBytes));
                Assert.InRange(f.Coordinator.Scheduler.CachedCount, 0, 16);
                Assert.InRange(f.Coordinator.PreviewBytes, 0, PageBitmapCoordinator.PreviewCacheBudget);
            }

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
            Assert.InRange((long)w * h, 1, 600L * 600 * 4);
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
