using System.Windows.Media.Imaging;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Ink;

namespace DreamBooks.App.Tests;

/// <summary>
/// The Pages tab's caches (FR-103, UI-04, NFR-06): page bitmaps within a byte budget, least recently used out; the
/// page ink within its entry cap; both dropped together on a document change, with reads already under way never kept.
/// </summary>
public sealed class PageThumbnailCacheTests
{
    [Fact]
    public void Thumbnail_cache_respects_its_budget_and_evicts_least_recently_used()
    {
        WpfTestThread.Run(() =>
        {
            Assert.Equal(40L * 1024 * 1024, PageThumbnailCache.DefaultByteBudget);
            Assert.Equal(64, PageThumbnailCache.DefaultInkCapacity);

            var loads = new List<int>();
            const int bytesPerBitmap = 100 * 100 * 4;
            var cache = new PageThumbnailCache(
                (page, _) =>
                {
                    loads.Add(page);
                    return Task.FromResult<BitmapSource?>(FakeBitmapRenderer.BitmapOf(100, 100));
                },
                NoInk,
                byteBudget: 3 * bytesPerBitmap);

            Get(cache, 0);
            Get(cache, 1);
            Get(cache, 2);
            Assert.Equal(3 * bytesPerBitmap, cache.Bytes);
            Assert.True(cache.TryGetBitmap(0, out _)); // 0 is now the most recently used
            Get(cache, 3); // over budget: 1, the least recently used, goes
            Assert.Equal(3, cache.Count);
            Assert.True(cache.Bytes <= 3 * bytesPerBitmap);
            Assert.False(cache.TryGetBitmap(1, out _));
            Assert.True(cache.TryGetBitmap(0, out _));
            Assert.True(cache.TryGetBitmap(2, out _));
            Assert.True(cache.TryGetBitmap(3, out _));

            Get(cache, 0); // a hit: no new read
            Assert.Equal([0, 1, 2, 3], loads);

            // A failed or cancelled read is not cached: the next request tries again.
            var failing = new PageThumbnailCache((_, _) => Task.FromResult<BitmapSource?>(null), NoInk);
            Assert.Null(Get(failing, 5));
            Assert.Equal(0, failing.Count);

            // The ink cache keeps at most its capacity.
            Assert.Equal(PageThumbnailCache.DefaultInkCapacity, cache.Ink.Capacity);
        });
    }

    [Fact]
    public void Clear_drops_bitmaps_and_ink_and_never_keeps_a_read_under_way()
    {
        WpfTestThread.Run(() =>
        {
            var pending = new TaskCompletionSource<BitmapSource?>();
            var inkReads = 0;
            var cache = new PageThumbnailCache(
                (page, _) => page == 9 ? pending.Task : Task.FromResult<BitmapSource?>(FakeBitmapRenderer.BitmapOf(10, 10)),
                (page, _) =>
                {
                    inkReads++;
                    return Task.FromResult<PageInk?>(new PageInk(page, 612, 792, [], PressureCurve.Firm, SmoothingLevel.Strong));
                });
            Get(cache, 1);
            var ink = cache.Ink.GetAsync(1, CancellationToken.None);
            WpfTestThread.PumpUntil(() => ink.IsCompleted);
            Assert.Equal(1, cache.Ink.Count);
            var late = cache.GetBitmapAsync(9, CancellationToken.None);

            var cleared = 0;
            cache.Cleared += (_, _) => cleared++;
            cache.Clear();
            Assert.Equal(1, cleared);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.Bytes);
            Assert.Equal(0, cache.Ink.Count);

            pending.SetResult(FakeBitmapRenderer.BitmapOf(10, 10)); // the old document's render arrives late
            WpfTestThread.PumpUntil(() => late.IsCompleted);
            Assert.False(cache.TryGetBitmap(9, out _));

            // A stroke change: that page's ink is read again and its rows are told.
            var invalidated = new List<int>();
            cache.InkInvalidated += (_, page) => invalidated.Add(page);
            var again = cache.Ink.GetAsync(1, CancellationToken.None);
            WpfTestThread.PumpUntil(() => again.IsCompleted);
            cache.InvalidateInk(1);
            Assert.Equal([1], invalidated);
            Assert.False(cache.Ink.TryGet(1, out _));
            Assert.Equal(2, inkReads);
        });
    }

    private static Task<PageInk?> NoInk(int page, CancellationToken ct) => Task.FromResult<PageInk?>(null);

    private static BitmapSource? Get(PageThumbnailCache cache, int page)
    {
        var task = cache.GetBitmapAsync(page, CancellationToken.None);
        WpfTestThread.PumpUntil(() => task.IsCompleted);
        return task.Result;
    }
}
