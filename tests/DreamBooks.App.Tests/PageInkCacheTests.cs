using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

/// <summary>
/// The scroll rail preview's ink (UI-07): each page's strokes read once and kept as frozen vector drawings, read again
/// when the page's strokes change, dropped when the document changes; a read that was superseded is never kept.
/// </summary>
public sealed class PageInkCacheTests
{
    [Fact]
    public void Page_ink_is_read_once_then_served_from_the_cache()
    {
        WpfTestThread.Run(() =>
        {
            var reads = new List<int>();
            var cache = new PageInkCache((page, _) =>
            {
                reads.Add(page);
                return Task.FromResult<PageInk?>(InkOf(page, strokes: 2));
            });

            var first = Await(cache.GetAsync(4, CancellationToken.None))!;
            Assert.Equal(4, first.PageIndex);
            Assert.Equal(612, first.WidthPt);
            Assert.Equal(792, first.HeightPt);
            Assert.Equal(2, first.Strokes.Count);
            Assert.All(first.Strokes, s => Assert.True(s.IsFrozen));

            Assert.Same(first, Await(cache.GetAsync(4, CancellationToken.None)));
            Assert.True(cache.TryGet(4, out var cached));
            Assert.Same(first, cached);
            Assert.Equal([4], reads);
        });
    }

    [Fact]
    public void A_page_without_ink_is_cached_too_and_a_failed_read_is_not()
    {
        WpfTestThread.Run(() =>
        {
            var reads = 0;
            PageInk? next = null;
            var cache = new PageInkCache((page, _) =>
            {
                reads++;
                return Task.FromResult(next);
            });

            Assert.Null(Await(cache.GetAsync(1, CancellationToken.None))); // released by a save: asked again later
            next = InkOf(1, strokes: 0);
            Assert.Empty(Await(cache.GetAsync(1, CancellationToken.None))!.Strokes);
            Assert.NotNull(Await(cache.GetAsync(1, CancellationToken.None)));
            Assert.Equal(2, reads);
        });
    }

    [Fact]
    public void Invalidating_a_page_reads_it_again_and_drops_a_read_in_flight()
    {
        WpfTestThread.Run(() =>
        {
            var gate = new TaskCompletionSource<PageInk?>();
            var strokes = 1;
            var cache = new PageInkCache((page, _) => page == 2 && !gate.Task.IsCompleted
                ? gate.Task
                : Task.FromResult<PageInk?>(InkOf(page, strokes)));

            Assert.Single(Await(cache.GetAsync(3, CancellationToken.None))!.Strokes);
            strokes = 2;
            cache.Invalidate(3); // a stroke added on page 4
            Assert.False(cache.TryGet(3, out _));
            Assert.Equal(2, Await(cache.GetAsync(3, CancellationToken.None))!.Strokes.Count);

            var late = cache.GetAsync(2, CancellationToken.None); // read before the change...
            cache.Invalidate(2);
            gate.SetResult(InkOf(2, strokes: 1)); // ...arrives after it: returned, never kept
            Assert.NotNull(Await(late));
            Assert.False(cache.TryGet(2, out _));
        });
    }

    [Fact]
    public void Clearing_drops_every_page_and_reads_in_flight()
    {
        WpfTestThread.Run(() =>
        {
            var gate = new TaskCompletionSource<PageInk?>();
            var cache = new PageInkCache((page, _) => page == 9 ? gate.Task : Task.FromResult<PageInk?>(InkOf(page, 1)));
            Assert.NotNull(Await(cache.GetAsync(1, CancellationToken.None)));
            var late = cache.GetAsync(9, CancellationToken.None);

            cache.Clear(); // another document, or the same one reopened without saving
            gate.SetResult(InkOf(9, 1));
            Await(late);

            Assert.False(cache.TryGet(1, out _));
            Assert.False(cache.TryGet(9, out _));
        });
    }

    [Fact]
    public void Least_recently_used_pages_leave_beyond_the_capacity()
    {
        WpfTestThread.Run(() =>
        {
            var cache = new PageInkCache((page, _) => Task.FromResult<PageInk?>(InkOf(page, 1)), capacity: 3);
            foreach (var page in new[] { 0, 1, 2 })
            {
                Await(cache.GetAsync(page, CancellationToken.None));
            }

            Assert.True(cache.TryGet(0, out _)); // used again: page 1 is now the oldest
            Await(cache.GetAsync(3, CancellationToken.None));

            Assert.Equal(3, cache.Count);
            Assert.False(cache.TryGet(1, out _));
            Assert.True(cache.TryGet(0, out _));
        });
    }

    private static PageInk InkOf(int page, int strokes) =>
        new(
            page,
            612,
            792,
            Enumerable.Range(0, strokes).Select(i => new InkStroke(
                Guid.NewGuid(),
                InkTool.Pen,
                InkColor.FromRgb(0, 0, 0),
                1.5,
                [new InkPoint(100, 100 + (i * 20), 0.5f, null, null, null, 0), new InkPoint(300, 110 + (i * 20), 0.5f, null, null, null, 8)]))
                .ToArray(),
            PressureCurve.Linear,
            SmoothingLevel.Light);

    private static T Await<T>(Task<T> task)
    {
        WpfTestThread.PumpUntil(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }
}
