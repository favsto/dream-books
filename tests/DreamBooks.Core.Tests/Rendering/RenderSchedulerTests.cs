using System.Collections.Concurrent;
using System.Threading.Channels;
using DreamBooks.Core.Rendering;

namespace DreamBooks.Core.Tests.Rendering;

[Collection(nameof(RenderSchedulerCollection))]
public class RenderSchedulerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private static RenderRequest Req(int page, int priority, int sharp = 10, int preview = 0) =>
        new(page, priority, sharp, sharp, preview, preview);

    private static long PixelBytes(FakeBitmap b) => (long)b.Width * b.Height;

    private static RenderScheduler<FakeBitmap> Create(FakeRenderer renderer, long budget = 1_000_000) =>
        new(renderer, PixelBytes, budget, () => null);

    private static ConcurrentQueue<RenderedEventArgs<FakeBitmap>> Record(RenderScheduler<FakeBitmap> s)
    {
        var events = new ConcurrentQueue<RenderedEventArgs<FakeBitmap>>();
        s.Rendered += (_, e) => events.Enqueue(e);
        return events;
    }

    private static async Task CompleteAllAsync(FakeRenderer renderer, RenderScheduler<FakeBitmap> s)
    {
        while (true)
        {
            var idle = s.WhenIdleAsync();
            await Task.WhenAny(idle, renderer.WaitForCallAsync()).WaitAsync(_timeout);
            if (renderer.TryTakeCall(out var call))
            {
                call.Succeed();
                continue;
            }

            if (idle.IsCompleted)
            {
                return;
            }
        }
    }

    [Fact]
    public async Task HasPendingWork_is_true_while_a_render_is_running_or_due_and_false_when_idle()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer);
        Assert.False(s.HasPendingWork);

        s.SetWanted([Req(0, 0), Req(1, 0)]);
        var first = await renderer.NextCallAsync();
        Assert.True(s.HasPendingWork);
        first.Succeed();
        var second = await renderer.NextCallAsync();
        Assert.True(s.HasPendingWork); // the next one started at once
        second.Succeed();
        await s.WhenIdleAsync().WaitAsync(_timeout);

        Assert.False(s.HasPendingWork);
    }

    [Fact]
    public async Task Visible_pages_render_before_neighbours()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer);

        s.SetWanted([Req(5, 2), Req(3, 0), Req(4, 1), Req(2, 1)]);

        var order = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var call = await renderer.NextCallAsync();
            order.Add(call.PageIndex);
            call.Succeed();
        }

        await s.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal([3, 2, 4, 5], order);
    }

    [Fact]
    public async Task Preview_arrives_before_sharp_for_a_page_without_a_bitmap()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer);
        var events = Record(s);

        s.SetWanted([Req(0, 0, sharp: 200, preview: 20), Req(1, 0, sharp: 200, preview: 20)]);

        var calls = new List<(int Page, int Width)>();
        for (var i = 0; i < 4; i++)
        {
            var call = await renderer.NextCallAsync();
            calls.Add((call.PageIndex, call.Width));
            if (i == 1)
            {
                // The event of a render is raised before the next render starts.
                Assert.Single(events);
                Assert.True(s.TryGet(0, out var preview));
                Assert.Equal(RenderQuality.Preview, preview!.Quality);
                Assert.Equal(20, preview.PixelWidth);
            }

            call.Succeed();
        }

        await s.WhenIdleAsync().WaitAsync(_timeout);
        // Both visible pages show something quickly before either gets its sharp render.
        Assert.Equal([(0, 20), (1, 20), (0, 200), (1, 200)], calls);
        Assert.Equal(
            [RenderQuality.Preview, RenderQuality.Preview, RenderQuality.Sharp, RenderQuality.Sharp],
            events.Select(e => e.Quality));
        Assert.True(s.TryGet(0, out var sharp));
        Assert.Equal(RenderQuality.Sharp, sharp!.Quality);
        Assert.Equal(200, sharp.PixelWidth);
    }

    [Fact]
    public async Task Page_that_leaves_the_wanted_set_is_cancelled_and_its_result_discarded()
    {
        var renderer = new FakeRenderer { HonourCancellation = false };
        using var s = Create(renderer);
        var events = Record(s);

        s.SetWanted([Req(1, 0)]);
        var first = await renderer.NextCallAsync();
        s.SetWanted([Req(2, 0)]);

        Assert.True(first.Token.IsCancellationRequested);
        first.Succeed(); // a renderer that ignores cancellation still must not pollute the cache

        var second = await renderer.NextCallAsync();
        Assert.Equal(2, second.PageIndex);
        second.Succeed();

        await s.WhenIdleAsync().WaitAsync(_timeout);
        Assert.False(s.TryGet(1, out _));
        Assert.True(s.TryGet(2, out _));
        Assert.Equal([2], events.Select(e => e.PageIndex));
        Assert.Equal(2, renderer.CallCount);
    }

    [Fact]
    public async Task Stale_generation_results_are_discarded_after_InvalidateAll()
    {
        var renderer = new FakeRenderer { HonourCancellation = false };
        using var s = Create(renderer);
        var events = Record(s);

        s.SetWanted([Req(0, 0)]);
        var first = await renderer.NextCallAsync();
        s.InvalidateAll();

        Assert.True(first.Token.IsCancellationRequested);
        first.Succeed();

        var second = await renderer.NextCallAsync();
        Assert.Equal(0, second.PageIndex);
        Assert.False(s.TryGet(0, out _)); // the stale result never entered the cache
        second.Succeed();

        await s.WhenIdleAsync().WaitAsync(_timeout);
        Assert.True(s.TryGet(0, out var bitmap));
        Assert.Equal(s.GetGeneration(0), bitmap!.Generation);
        Assert.False(s.IsStale(0, bitmap));
        var e = Assert.Single(events);
        Assert.Equal(s.GetGeneration(0), e.Generation);
    }

    [Fact]
    public async Task InvalidateAll_keeps_old_bitmaps_available_flagged_stale_until_replaced()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer);

        s.SetWanted([Req(0, 0, sharp: 100, preview: 10)]);
        await CompleteAllAsync(renderer, s);
        Assert.True(s.TryGet(0, out var old));
        Assert.Equal(RenderQuality.Sharp, old!.Quality);

        var callsBefore = renderer.CallCount;

        // Zoom: invalidation and new sizes in one atomic step.
        s.SetWanted([Req(0, 0, sharp: 150, preview: 10)], invalidateAll: true);

        // No blank page: the old bitmap is still there, flagged stale, to be shown stretched.
        Assert.True(s.TryGet(0, out var stale));
        Assert.Same(old, stale);
        Assert.True(s.IsStale(0, stale!));

        // Something is cached already, so the scheduler goes straight to the sharp render, and never
        // starts one at the old size.
        var call = await renderer.NextCallAsync();
        Assert.Equal(150, call.Width);
        Assert.False(call.Token.IsCancellationRequested);
        call.Succeed();
        await s.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal(callsBefore + 1, renderer.CallCount);

        Assert.True(s.TryGet(0, out var fresh));
        Assert.Equal(150, fresh!.PixelWidth);
        Assert.False(s.IsStale(0, fresh));
        Assert.Equal(1, s.CachedCount);
    }

    [Fact]
    public async Task InvalidatePage_rerenders_only_that_page_keeping_its_old_bitmap()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer);

        s.SetWanted([Req(0, 0), Req(1, 0)]);
        await CompleteAllAsync(renderer, s);
        Assert.True(s.TryGet(0, out var page0));
        var callsBefore = renderer.CallCount;

        s.InvalidatePage(1);

        Assert.False(s.IsStale(0, page0!));
        Assert.True(s.TryGet(1, out var page1));
        Assert.True(s.IsStale(1, page1!));

        var call = await renderer.NextCallAsync();
        Assert.Equal(1, call.PageIndex);
        call.Succeed();
        await s.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal(callsBefore + 1, renderer.CallCount);
        Assert.True(s.TryGet(1, out var fresh));
        Assert.False(s.IsStale(1, fresh!));
    }

    [Fact]
    public async Task Cache_stays_within_the_byte_budget_evicting_unwanted_LRU_first()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer, budget: 300); // three 10x10 bitmaps

        s.SetWanted([Req(0, 0), Req(1, 1), Req(2, 2)]);
        await CompleteAllAsync(renderer, s);
        Assert.Equal(300, s.CachedBytes);

        Assert.True(s.TryGet(0, out _)); // page 0 is now the most recently used

        s.SetWanted([Req(3, 0)]);
        await CompleteAllAsync(renderer, s);

        Assert.Equal(300, s.CachedBytes);
        Assert.Equal(3, s.CachedCount);
        Assert.False(s.TryGet(1, out _)); // least recently used and unwanted
        Assert.True(s.TryGet(0, out _));
        Assert.True(s.TryGet(2, out _));
        Assert.True(s.TryGet(3, out _));

        s.SetWanted([Req(4, 0)]);
        await CompleteAllAsync(renderer, s);

        Assert.True(s.CachedBytes <= 300);
        Assert.False(s.TryGet(0, out _)); // 0 was touched before 2 and 3 above
        Assert.True(s.TryGet(4, out _));
    }

    [Fact]
    public async Task Wanted_sharp_bitmaps_are_never_evicted()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer, budget: 250);

        s.SetWanted([Req(0, 0), Req(1, 0), Req(2, 0)]);
        await CompleteAllAsync(renderer, s);

        // Over budget, but every bitmap belongs to a visible page.
        Assert.Equal(3, s.CachedCount);
        Assert.Equal(300, s.CachedBytes);
        foreach (var page in new[] { 0, 1, 2 })
        {
            Assert.True(s.TryGet(page, out var b));
            Assert.Equal(RenderQuality.Sharp, b!.Quality);
        }

        // As soon as they are no longer wanted the cache returns within the budget.
        s.SetWanted([Req(0, 0)]);
        Assert.True(s.CachedBytes <= 250);
        Assert.True(s.TryGet(0, out _));
    }

    [Fact]
    public async Task A_wanted_page_older_in_the_LRU_survives_a_newer_unwanted_one()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer, budget: 200); // two 10x10 bitmaps

        s.SetWanted([Req(0, 0), Req(1, 1)]);
        await CompleteAllAsync(renderer, s);
        Assert.True(s.TryGet(1, out _)); // page 1 is now more recent than page 0

        s.SetWanted([Req(0, 0), Req(2, 1)]);
        await CompleteAllAsync(renderer, s);

        Assert.Equal(200, s.CachedBytes);
        Assert.False(s.TryGet(1, out _)); // newer, but unwanted
        Assert.True(s.TryGet(0, out var kept)); // older, but wanted
        Assert.Equal(RenderQuality.Sharp, kept!.Quality);
        Assert.True(s.TryGet(2, out _));
    }

    [Fact]
    public async Task A_neighbour_with_nothing_cached_is_never_left_blank_when_over_budget()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer, budget: 150);

        // Previews disabled; the two visible pages alone exceed the budget.
        s.SetWanted([Req(0, 0), Req(1, 0), Req(2, 1)]);
        await CompleteAllAsync(renderer, s);

        Assert.True(s.TryGet(2, out var neighbour));
        Assert.Equal(RenderQuality.Sharp, neighbour!.Quality);
        Assert.Equal(3, renderer.CallCount);
    }

    [Fact]
    public async Task Pages_that_leave_the_window_while_scrolling_are_cancelled()
    {
        var renderer = new FakeRenderer { HonourCancellation = false };
        using var s = Create(renderer);
        var events = Record(s);

        s.SetWanted([Req(0, 0), Req(1, 1)]);
        var inFlight = await renderer.NextCallAsync();
        for (var top = 1; top <= 20; top++)
        {
            Assert.Equal(top - 1, inFlight.PageIndex);
            s.SetWanted([Req(top, 0), Req(top + 1, 1)]);
            Assert.True(inFlight.Token.IsCancellationRequested);
            inFlight.Succeed(); // ignored cancellation: the result must still be discarded
            inFlight = await renderer.NextCallAsync();
        }

        Assert.Equal(20, inFlight.PageIndex);
        Assert.False(inFlight.Token.IsCancellationRequested);
        Assert.Equal(0, s.CachedCount);
        Assert.Empty(events);
        inFlight.Succeed();
        await CompleteAllAsync(renderer, s);
        Assert.Equal([20, 21], events.Select(e => e.PageIndex).Order());
    }

    [Fact]
    public async Task A_throwing_sizeOf_counts_as_a_failure_and_does_not_stop_the_pump()
    {
        var renderer = new FakeRenderer();
        using var s = new RenderScheduler<FakeBitmap>(
            renderer,
            b => b.PageIndex == 0 ? throw new InvalidOperationException("boom") : PixelBytes(b),
            1_000_000,
            () => null);

        s.SetWanted([Req(0, 0), Req(1, 1)]);
        await CompleteAllAsync(renderer, s);

        Assert.False(s.TryGet(0, out _));
        Assert.True(s.TryGet(1, out _));
        Assert.Equal(2, renderer.CallCount);
    }

    [Fact]
    public async Task Neighbours_keep_previews_only_when_the_wanted_set_exceeds_the_budget()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer, budget: 250);

        s.SetWanted([Req(0, 0, preview: 2), Req(1, 0, preview: 2), Req(2, 1, preview: 2)]);
        await CompleteAllAsync(renderer, s);

        Assert.True(s.TryGet(0, out var visible));
        Assert.Equal(RenderQuality.Sharp, visible!.Quality);
        Assert.True(s.TryGet(2, out var neighbour));
        Assert.Equal(RenderQuality.Preview, neighbour!.Quality);
        Assert.True(s.CachedBytes <= 250);
        Assert.Equal(5, renderer.CallCount); // three previews, two sharp; no sharp for the neighbour
    }

    [Fact]
    public async Task Renderer_returning_null_is_retried_once_not_in_a_loop()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer);
        var events = Record(s);

        s.SetWanted([Req(0, 0)]);
        (await renderer.NextCallAsync()).Fail();
        await s.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal(1, renderer.CallCount); // no tight loop

        s.SetWanted([Req(0, 0)]); // next SetWanted: one retry
        (await renderer.NextCallAsync()).Fail();
        await s.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal(2, renderer.CallCount);

        s.SetWanted([Req(0, 0)]); // retry used up
        s.SetWanted([Req(0, 0)]);
        await s.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal(2, renderer.CallCount);

        s.InvalidateAll(); // renderer/zoom change: try again
        (await renderer.NextCallAsync()).Succeed();
        await s.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal(3, renderer.CallCount);
        Assert.True(s.TryGet(0, out _));
        Assert.Single(events);
    }

    [Fact]
    public async Task Renderer_throwing_is_treated_as_a_failure()
    {
        var renderer = new FakeRenderer();
        using var s = Create(renderer);

        s.SetWanted([Req(0, 0), Req(1, 1)]);
        (await renderer.NextCallAsync()).Throw(new InvalidOperationException("boom"));
        var next = await renderer.NextCallAsync();
        Assert.Equal(1, next.PageIndex);
        next.Succeed();

        await s.WhenIdleAsync().WaitAsync(_timeout);
        Assert.False(s.TryGet(0, out _));
        Assert.True(s.TryGet(1, out _));
    }

    [Fact]
    public async Task A_thousand_scroll_updates_do_not_leak_tasks_or_bitmaps()
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            // Every tenth page fails to render; every tenth step the top page is invalidated.
            var renderer = new FakeRenderer { AutoComplete = true, FailPage = page => page % 10 == 3 };
            const long budget = 40 * 400; // forty sharp bitmaps of 20x20
            using (var s = Create(renderer, budget))
            {
                for (var top = 0; top < 1000; top++)
                {
                    var window = new List<RenderRequest>();
                    for (var i = 0; i < 6 && top + i < 1000; i++)
                    {
                        window.Add(Req(top + i, i < 2 ? 0 : i - 1, sharp: 20, preview: 10));
                    }

                    s.SetWanted(window);
                    if (top % 10 == 0)
                    {
                        s.InvalidatePage(top);
                    }

                    if (top % 50 == 0)
                    {
                        await s.WhenIdleAsync().WaitAsync(_timeout);
                    }
                }

                await s.WhenIdleAsync().WaitAsync(_timeout);

                Assert.True(s.CachedBytes <= budget, $"{s.CachedBytes} bytes");
                Assert.Equal(0, renderer.InFlight);

                // Pages never re-enter the window, so each one costs at most a preview and a sharp render
                // (a failing page: a first attempt and one retry), plus one re-render per InvalidatePage.
                Assert.True(renderer.CallCount <= (2 * 1000) + 100, $"{renderer.CallCount} renders");

                // Internal bookkeeping is bounded by the window and the cache, not by the pages scrolled.
                Assert.True(
                    s.TrackedPageCount <= (2 * s.CachedCount) + 2,
                    $"{s.TrackedPageCount} tracked for {s.CachedCount} cached");
                Assert.True(s.TryGet(999, out var last));
                Assert.Equal(RenderQuality.Sharp, last!.Quality);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task Rendered_event_is_raised_on_the_captured_context()
    {
        var context = new QueueSynchronizationContext();
        var renderer = new FakeRenderer();
        using var s = new RenderScheduler<FakeBitmap>(renderer, PixelBytes, 1_000_000, () => context);
        SynchronizationContext? seen = null;
        var raised = 0;
        s.Rendered += (_, _) =>
        {
            seen = SynchronizationContext.Current;
            raised++;
        };

        s.SetWanted([Req(0, 0)]);
        (await renderer.NextCallAsync()).Succeed();
        await s.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal(0, raised); // not raised on the render's thread
        Assert.Equal(1, context.RunPending());
        Assert.Equal(1, raised);
        Assert.Same(context, seen);
    }

    [Fact]
    public async Task Dispose_cancels_everything_and_stops_events()
    {
        var context = new QueueSynchronizationContext();
        var renderer = new FakeRenderer { HonourCancellation = false };
        var s = new RenderScheduler<FakeBitmap>(renderer, PixelBytes, 1_000_000, () => context);
        var raised = 0;
        s.Rendered += (_, _) => raised++;

        s.SetWanted([Req(0, 0), Req(1, 1)]);
        (await renderer.NextCallAsync()).Succeed();
        var pending = await renderer.NextCallAsync();

        s.Dispose();

        Assert.True(pending.Token.IsCancellationRequested);
        pending.Succeed();
        await s.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal(1, context.RunPending()); // the event posted before Dispose is dropped on delivery
        Assert.Equal(0, raised);
        Assert.Equal(0, s.CachedCount);
        Assert.Equal(0, s.CachedBytes);
        Assert.False(s.TryGet(0, out _));

        s.SetWanted([Req(2, 0)]);
        s.InvalidateAll();
        s.InvalidatePage(2);
        s.Dispose();
        await s.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal(2, renderer.CallCount);
        Assert.Equal(0, context.RunPending());
    }

    private sealed record FakeBitmap(int PageIndex, int Width, int Height);

    private sealed class RenderCall
    {
        private readonly TaskCompletionSource<FakeBitmap?> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RenderCall(int pageIndex, int width, int height, CancellationToken token, bool honourCancellation)
        {
            PageIndex = pageIndex;
            Width = width;
            Height = height;
            Token = token;
            if (honourCancellation)
            {
                token.Register(() => _tcs.TrySetCanceled(token));
            }
        }

        public int PageIndex { get; }

        public int Width { get; }

        public int Height { get; }

        public CancellationToken Token { get; }

        public Task<FakeBitmap?> Result => _tcs.Task;

        public void Succeed() => _tcs.TrySetResult(new FakeBitmap(PageIndex, Width, Height));

        public void Fail() => _tcs.TrySetResult(null);

        public void Throw(Exception ex) => _tcs.TrySetException(ex);
    }

    private sealed class FakeRenderer : IBitmapRenderer<FakeBitmap>
    {
        private readonly Channel<RenderCall> _calls = Channel.CreateUnbounded<RenderCall>();
        private int _callCount;
        private int _inFlight;

        public bool HonourCancellation { get; init; } = true;

        public bool AutoComplete { get; init; }

        public Func<int, bool>? FailPage { get; init; }

        public int CallCount => Volatile.Read(ref _callCount);

        public int InFlight => Volatile.Read(ref _inFlight);

        public async Task<FakeBitmap?> RenderAsync(int pageIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (AutoComplete)
                {
                    await Task.Yield();
                    ct.ThrowIfCancellationRequested();
                    return FailPage?.Invoke(pageIndex) == true ? null : new FakeBitmap(pageIndex, pixelWidth, pixelHeight);
                }

                var call = new RenderCall(pageIndex, pixelWidth, pixelHeight, ct, HonourCancellation);
                _calls.Writer.TryWrite(call);
                return await call.Result.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<RenderCall> NextCallAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(_timeout);

        public Task WaitForCallAsync() => _calls.Reader.WaitToReadAsync().AsTask();

        public bool TryTakeCall([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RenderCall? call) =>
            _calls.Reader.TryRead(out call);
    }

    private sealed class QueueSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public int RunPending()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                var count = 0;
                while (_queue.TryDequeue(out var item))
                {
                    item.Callback(item.State);
                    count++;
                }

                return count;
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
