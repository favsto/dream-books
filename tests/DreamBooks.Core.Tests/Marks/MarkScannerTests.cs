using DreamBooks.Core.Documents;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Marks;

[Collection(nameof(MarkScannerCollection))]
public class MarkScannerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private static InkStroke MarginLine(double x = 30, double top = 100, double bottom = 260)
        => new(
            Guid.NewGuid(),
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            1.5,
            Enumerable.Range(0, 20)
                .Select(i => new InkPoint(x, top + ((bottom - top) * i / 19.0), 0.5f, null, null, null, i))
                .ToArray());

    private static PageStrokes WithLine() => new([MarginLine()], 0);

    private static MarkScanner Create(
        FakeMarkPageSource source, MarkIndex index, IPageLineMetrics? metrics = null, Func<bool>? isBusy = null)
        => new(source, new MarkRecognizer(metrics), index, isBusy, TimeSpan.Zero);

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Pages_are_scanned_nearest_the_centre_first_alternating_outward()
    {
        var source = new FakeMarkPageSource(7);
        var index = new MarkIndex(7);
        using var scanner = Create(source, index);

        scanner.Start(3);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal([3, 4, 2, 5, 1, 6, 0], source.Loads);
        Assert.True(scanner.IsCompleted);
        Assert.Equal(7, index.ScannedCount);
    }

    [Fact]
    public async Task Pages_near_an_edge_continue_on_the_other_side()
    {
        var source = new FakeMarkPageSource(5);
        using var scanner = Create(source, new MarkIndex(5));

        scanner.Start(4);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal([4, 3, 2, 1, 0], source.Loads);
    }

    [Fact]
    public async Task Recentre_reorders_the_remaining_queue()
    {
        var gate = NewGate();
        var source = new FakeMarkPageSource(10) { Gate = p => p == 0 ? gate.Task : null };
        using var scanner = Create(source, new MarkIndex(10));

        scanner.Start(0);
        Assert.Equal(0, await source.NextStartedAsync());
        scanner.Recentre(7);
        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal([0, 7, 8, 6, 9, 5, 4, 3, 2, 1], source.Loads);
    }

    [Fact]
    public async Task Calling_Start_again_recentres_and_does_not_start_a_second_scan()
    {
        var gate = NewGate();
        var source = new FakeMarkPageSource(10) { Gate = p => p == 0 ? gate.Task : null };
        using var scanner = Create(source, new MarkIndex(10));

        scanner.Start(0);
        Assert.Equal(0, await source.NextStartedAsync());
        scanner.Start(7);
        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal([0, 7, 8, 6, 9, 5, 4, 3, 2, 1], source.Loads);
        Assert.Equal(1, source.MaxInFlight);
    }

    [Fact]
    public async Task A_document_without_pages_completes_at_once()
    {
        var source = new FakeMarkPageSource(0);
        var progress = 0;
        using var scanner = Create(source, new MarkIndex(0));
        scanner.Progress += (_, _) => progress++;

        scanner.Start(0);
        scanner.Recentre(3);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.True(scanner.IsCompleted);
        Assert.Empty(source.Loads);
        Assert.Equal(0, progress);
        Assert.Equal(0, scanner.ProcessedCount);
    }

    [Fact]
    public async Task Pages_with_no_annotations_are_skipped_without_recognition()
    {
        var metrics = new CountingLineMetrics();
        var source = new FakeMarkPageSource(5) { Strokes = p => p == 2 ? WithLine() : null };
        var index = new MarkIndex(5);
        using var scanner = Create(source, index, metrics);

        scanner.Start(0);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal(1, metrics.Calls); // only page 2 went through the recogniser
        Assert.Equal(5, index.ScannedCount);
        Assert.Single(index.MarksOnPage(2));
        Assert.Equal(1, index.TotalMarkCount);
        Assert.Equal(0, scanner.FailedCount);
    }

    [Fact]
    public async Task A_page_that_throws_or_returns_garbage_is_skipped_and_scanning_continues()
    {
        var source = new FakeMarkPageSource(7)
        {
            ThrowSync = p => p == 1 ? new IOException("sync") : null,
            Strokes = p => p == 2 ? new PageStrokes(null!, 0) : WithLine(),
            DimensionsFail = p => p == 3 ? new InvalidOperationException("dims") : null,
            ThrowAsync = p => p switch
            {
                4 => new IOException("async"),
                5 => new OperationCanceledException("cancelled although nobody cancelled"),
                _ => null,
            },
            Yield = true,
        };
        var index = new MarkIndex(7);
        using var scanner = Create(source, index);

        scanner.Start(0);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.True(scanner.IsCompleted);
        Assert.Equal(7, source.Loads.Count);
        Assert.Equal(5, scanner.FailedCount);
        Assert.Equal(7, scanner.ProcessedCount);
        Assert.Equal([0, 6], Enumerable.Range(0, 7).Where(index.IsScanned));
        Assert.Equal(2, index.TotalMarkCount);
    }

    [Fact]
    public async Task Pause_stops_after_the_current_page_and_Resume_continues()
    {
        var gate = NewGate();
        var source = new FakeMarkPageSource(5) { Gate = p => p == 0 ? gate.Task : null, Strokes = _ => WithLine() };
        var index = new MarkIndex(5);
        using var scanner = Create(source, index);

        scanner.Start(0);
        Assert.Equal(0, await source.NextStartedAsync());
        scanner.Pause();
        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        await Task.Delay(30);

        Assert.Equal([0], source.Loads);
        Assert.True(index.IsScanned(0)); // the current page is finished, not abandoned
        Assert.False(scanner.IsCompleted);

        // Pauses nest: inking and saving can overlap.
        scanner.Pause();
        scanner.Resume();
        await Task.Delay(30);
        Assert.Single(source.Loads);

        scanner.Resume();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.True(scanner.IsCompleted);
        Assert.Equal(5, source.Loads.Count);
        Assert.Equal(5, index.ScannedCount);

        scanner.Resume(); // unbalanced: harmless
    }

    [Fact]
    public async Task isBusy_pauses_the_scan_while_true()
    {
        var busy = true;
        var source = new FakeMarkPageSource(4);
        var index = new MarkIndex(4);
        using var scanner = Create(source, index, isBusy: () => Volatile.Read(ref busy));

        scanner.Start(0);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        await Task.Delay(50);
        Assert.Empty(source.Loads);
        Assert.False(scanner.IsCompleted);

        Volatile.Write(ref busy, false);
        await WaitUntilAsync(() => scanner.IsCompleted);

        Assert.Equal(4, index.ScannedCount);
    }

    [Fact]
    public async Task Dispose_cancels_the_scan_and_no_events_are_raised_afterwards()
    {
        var gate = NewGate();
        var source = new FakeMarkPageSource(5) { Gate = p => p == 0 ? gate.Task : null, Strokes = _ => WithLine() };
        var index = new MarkIndex(5);
        var progress = 0;
        var changed = 0;
        index.Changed += (_, _) => Interlocked.Increment(ref changed);
        var scanner = Create(source, index);
        scanner.Progress += (_, _) => Interlocked.Increment(ref progress);

        scanner.Start(0);
        Assert.Equal(0, await source.NextStartedAsync());
        scanner.Dispose();
        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        await Task.Delay(30);

        Assert.Equal([0], source.Loads); // never more than the load already in flight
        Assert.Equal(0, Volatile.Read(ref progress));
        Assert.Equal(0, Volatile.Read(ref changed));
        Assert.False(index.IsScanned(0));

        // Calls after Dispose are harmless; Start is not.
        scanner.Pause();
        scanner.Resume();
        scanner.Recentre(2);
        scanner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scanner.Start(0));
    }

    [Fact]
    public async Task Pages_already_updated_live_are_not_overwritten_by_the_scan()
    {
        var gate = NewGate();
        var source = new FakeMarkPageSource(4) { Gate = p => p == 0 ? gate.Task : null, Strokes = _ => WithLine() };
        var index = new MarkIndex(4);
        var liveBefore = new Mark(2, MarkSide.Right, 10, 200, [Guid.NewGuid()]);
        var liveDuring = new Mark(0, MarkSide.Right, 300, 500, [Guid.NewGuid()]);
        index.SetPage(2, [liveBefore]);
        using var scanner = Create(source, index);

        scanner.Start(0);
        Assert.Equal(0, await source.NextStartedAsync());
        index.SetPage(0, [liveDuring]); // the tracker updates page 0 while its scan load is in flight
        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.DoesNotContain(2, source.Loads); // set live before the scan reached it: not even loaded
        Assert.Equal([liveDuring], index.MarksOnPage(0)); // the stale scan result was dropped
        Assert.Equal([liveBefore], index.MarksOnPage(2));
        Assert.Single(index.MarksOnPage(1));
        Assert.Single(index.MarksOnPage(3));
    }

    [Fact]
    public async Task A_thousand_page_document_scans_to_completion_with_bounded_concurrency()
    {
        var source = new FakeMarkPageSource(1000)
        {
            Yield = true,
            Strokes = p => p % 7 == 0 ? WithLine() : p % 3 == 0 ? new PageStrokes([], 0) : null,
            ThrowAsync = p => p % 50 == 1 ? new IOException("damaged") : null,
        };
        var index = new MarkIndex(1000);
        var progress = 0;
        using var scanner = Create(source, index);
        scanner.Progress += (_, _) => Interlocked.Increment(ref progress);

        scanner.Start(500);
        for (var c = 0; c < 1000; c += 97)
        {
            scanner.Recentre(c);
            await Task.Yield();
        }

        await WaitUntilAsync(() => scanner.IsCompleted);

        var failed = Enumerable.Range(0, 1000).Count(p => p % 50 == 1);
        Assert.Equal(1, source.MaxInFlight);
        Assert.Equal(1000, source.Loads.Count);
        Assert.Equal(1000, source.Loads.Distinct().Count());
        Assert.Equal(failed, scanner.FailedCount);
        Assert.Equal(1000 - failed, index.ScannedCount);
        Assert.Equal(Enumerable.Range(0, 1000).Count(p => p % 7 == 0 && p % 50 != 1), index.TotalMarkCount);
        Assert.Equal(1000, Volatile.Read(ref progress));
    }

    [Fact]
    public async Task Cheap_pages_are_scanned_back_to_back_and_the_idle_gap_comes_once_per_slice_of_work()
    {
        // Regression (Task 8, real book): a gap after every page made a 1,076-page scan take ~17 s for ~0.6 s of
        // work, because a short Task.Delay lasts a whole timer tick (~15.6 ms on Windows) whatever it asks for.
        var source = new FakeMarkPageSource(200);
        var index = new MarkIndex(200);
        using var scanner = new MarkScanner(source, new MarkRecognizer(), index, idleGap: TimeSpan.FromMilliseconds(50));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        scanner.Start(0);
        await WaitUntilAsync(() => scanner.IsCompleted);
        watch.Stop();

        Assert.Equal(200, index.ScannedCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"200 instant pages took {watch.ElapsedMilliseconds} ms (one rest per slice expected)");
    }

    [Fact]
    public async Task Pages_whose_work_fills_a_slice_still_get_the_idle_gap_after_them()
    {
        var source = new FakeMarkPageSource(8) { Gate = _ => Task.Delay(20) };
        var index = new MarkIndex(8);
        using var scanner = new MarkScanner(
            source, new MarkRecognizer(), index, idleGap: TimeSpan.FromMilliseconds(60), workSlice: TimeSpan.FromMilliseconds(10));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        scanner.Start(0);
        await WaitUntilAsync(() => scanner.IsCompleted);
        watch.Stop();

        Assert.Equal(8, index.ScannedCount);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(7 * 60), $"8 slow pages took only {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_negative_work_slice_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkScanner(
            new FakeMarkPageSource(1), new MarkRecognizer(), new MarkIndex(1), workSlice: TimeSpan.FromMilliseconds(-1)));

    [Fact]
    public async Task No_unobserved_task_exceptions()
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            // A scan full of failures runs to completion...
            var failing = new FakeMarkPageSource(200)
            {
                Yield = true,
                ThrowSync = p => p % 5 == 0 ? new IOException("sync") : null,
                ThrowAsync = p => p % 5 == 1 ? new IOException("async") : p % 5 == 2 ? new OperationCanceledException() : null,
                Strokes = p => p % 5 == 3 ? new PageStrokes(null!, 0) : WithLine(),
            };
            using (var scanner = Create(failing, new MarkIndex(200)))
            {
                scanner.Progress += (_, _) => throw new InvalidOperationException("a broken listener");
                scanner.Start(100);
                await WaitUntilAsync(() => scanner.IsCompleted);
            }

            // ...a scan disposed mid-load, paused and busy, and one whose index is smaller than the source, end quietly.
            var gate = NewGate();
            var held = new FakeMarkPageSource(50) { Gate = p => p == 10 ? gate.Task : null };
            var scanner2 = Create(held, new MarkIndex(50), isBusy: () => false);
            scanner2.Start(10);
            await held.NextStartedAsync();
            scanner2.Pause();
            scanner2.Dispose();
            gate.SetException(new IOException("after dispose"));
            await scanner2.WhenIdleAsync().WaitAsync(_timeout);

            var shrinking = new MarkIndex(10); // smaller than the source: the pages past its end fail, quietly
            var source3 = new FakeMarkPageSource(30) { Strokes = _ => WithLine() };
            using (var scanner3 = Create(source3, shrinking))
            {
                scanner3.Start(0);
                await WaitUntilAsync(() => scanner3.IsCompleted);
                Assert.Equal(10, shrinking.ScannedCount);
                Assert.Equal(20, scanner3.FailedCount);
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
    public async Task One_read_per_page_gives_both_its_marks_and_its_notes()
    {
        var word = new InkStroke(
            Guid.NewGuid(),
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            1.5,
            Enumerable.Range(0, 12).Select(i => new InkPoint(200 + (i * 2.5), i % 2 == 0 ? 400 : 412, 0.5f, null, null, null, i)).ToArray());
        var line = MarginLine();
        var source = new FakeMarkPageSource(4) { Strokes = p => p == 2 ? new PageStrokes([line, word], 0) : null };
        var index = new MarkIndex(4);
        using var scanner = Create(source, index);

        scanner.Start(0);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal(4, source.Loads.Count); // each page read once
        Assert.Equal([line.Id], Assert.Single(index.MarksOnPage(2)).StrokeIds);
        Assert.Equal([word.Id], Assert.Single(index.NotesOnPage(2)).StrokeIds);
        Assert.Equal(1, index.TotalNoteCount);
        Assert.Equal(4, index.ScannedCount);
    }
}
