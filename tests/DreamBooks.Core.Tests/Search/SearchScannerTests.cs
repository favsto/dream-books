using DreamBooks.Core.Search;

namespace DreamBooks.Core.Tests.Search;

/// <summary>The background search over every page (FR-13, FR-105). Tests print no query text, only counts.</summary>
[Collection(nameof(SearchScannerCollection))]
public class SearchScannerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private static SearchScanner Create(
        FakeSearchPageSource source, SearchIndex index, int maxTotalHits = 5000, Func<bool>? isBusy = null)
        => new(source, index, maxTotalHits, isBusy, TimeSpan.Zero);

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
    public async Task Pages_are_searched_from_the_centre_forward_then_wrapped()
    {
        var source = new FakeSearchPageSource(7, hitsPerPage: 1);
        var index = new SearchIndex(7);
        var progress = 0;
        using var scanner = Create(source, index);
        scanner.Progress += (_, _) => Interlocked.Increment(ref progress);
        Assert.Equal(SearchState.NotStarted, scanner.State);

        scanner.Start("  Città  ", 3);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal([3, 4, 5, 6, 0, 1, 2], source.LoadedPages);
        Assert.All(source.Loads, l => Assert.Equal("citta", l.Query)); // folded once, before the scan
        Assert.True(scanner.IsCompleted);
        Assert.Equal(SearchState.Completed, scanner.State);
        Assert.Equal(7, scanner.ProcessedCount);
        Assert.Equal(7, index.SearchedCount);
        Assert.Equal(7, index.TotalHits);
        Assert.False(index.Truncated);
        Assert.True(scanner.HasTextLayer);
        Assert.Equal(8, Volatile.Read(ref progress)); // once per page, and once at the end

        // The centre is clamped to the document.
        var late = new FakeSearchPageSource(4, hitsPerPage: 1);
        using var lateScanner = Create(late, new SearchIndex(4));
        lateScanner.Start("x", 99);
        await lateScanner.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal([3, 0, 1, 2], late.LoadedPages);
    }

    [Fact]
    public async Task A_document_without_pages_or_an_empty_query_completes_at_once()
    {
        var empty = new FakeSearchPageSource(0, hitsPerPage: 1);
        using var scanner = Create(empty, new SearchIndex(0));
        scanner.Start("x", 0);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        Assert.True(scanner.IsCompleted);
        Assert.Empty(empty.Loads);

        var source = new FakeSearchPageSource(5, hitsPerPage: 1);
        using var blank = Create(source, new SearchIndex(5));
        blank.Start(" \t\n ", 2);
        await blank.WhenIdleAsync().WaitAsync(_timeout);
        Assert.True(blank.IsCompleted);
        Assert.Empty(source.Loads);
        Assert.True(blank.HasTextLayer); // nothing was searched: nothing decided
    }

    [Fact]
    public async Task A_new_Start_cancels_the_old_run_and_stale_results_never_reach_the_new_index()
    {
        var gate = NewGate();
        var oldIndex = new SearchIndex(6);
        var newIndex = new SearchIndex(6);
        var source = new FakeSearchPageSource(6, hitsPerPage: 1) { Gate = p => p == 0 && gate.Task is { IsCompleted: false } g ? g : null };
        using var scanner = Create(source, oldIndex);

        scanner.Start("old", 0);
        Assert.Equal(0, await source.NextStartedAsync());

        scanner.Start("new", 0, newIndex);
        Assert.Same(newIndex, scanner.Index);
        await Task.Delay(30);
        Assert.Single(source.Loads); // the new run waits for the old run's search in flight

        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal(0, oldIndex.SearchedCount); // the old run's result was dropped
        Assert.Equal(6, newIndex.SearchedCount);
        Assert.All(source.Loads.Skip(1), l => Assert.Equal("new", l.Query));
        Assert.Equal(1, source.MaxInFlight);
        Assert.True(scanner.IsCompleted);

        // A new query never writes into an index holding another query's hits.
        Assert.Throws<ArgumentException>(() => scanner.Start("other", 0));
        Assert.Throws<ArgumentException>(() => scanner.Start("other", 0, newIndex));

        // The same query again only re-centres: searched pages are not searched again.
        var loads = source.Loads.Count;
        scanner.Start("NEW", 3);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal(loads, source.Loads.Count);
        Assert.True(scanner.IsCompleted);
        Assert.Equal(1, source.PageCountReads); // read once, at the first Start
    }

    [Fact]
    public async Task A_run_superseded_while_waiting_for_its_predecessor_does_not_let_the_next_run_overlap()
    {
        // Regression: B, cancelled while waiting for A's search in flight, ended at once and C searched alongside A.
        var gate = NewGate();
        var source = new FakeSearchPageSource(4, hitsPerPage: 1) { Gate = p => p == 0 ? gate.Task : null };
        using var scanner = Create(source, new SearchIndex(4));

        scanner.Start("a", 0);
        Assert.Equal(0, await source.NextStartedAsync());
        scanner.Start("b", 0, new SearchIndex(4));
        var last = new SearchIndex(4);
        scanner.Start("c", 1, last);
        await Task.Delay(50);
        Assert.Single(source.Loads);

        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal(1, source.MaxInFlight);
        Assert.DoesNotContain(source.Loads, l => l.Query == "b");
        Assert.Equal(4, last.SearchedCount);
    }

    [Fact]
    public async Task A_scanner_over_a_partly_searched_index_searches_only_the_rest_within_the_remaining_budget()
    {
        // After a plain Save the owner swaps the renderer: a new scanner continues on the same index.
        var index = new SearchIndex(5);
        index.SetPage(0, [FakeSearchPageSource.NthHit(0, 0), FakeSearchPageSource.NthHit(0, 1)]);
        index.SetPage(1, [], hasText: true);
        var source = new FakeSearchPageSource(5, hitsPerPage: 1) { HasText = _ => false, HitCount = _ => 0 };
        using var scanner = Create(source, index, maxTotalHits: 10);

        scanner.Start("x", 0);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal([2, 3, 4], source.LoadedPages);
        Assert.All(source.Loads, l => Assert.Equal(8, l.MaxHits));
        Assert.True(scanner.HasTextLayer); // page 1, searched before, has text
    }

    [Fact]
    public async Task Pause_and_isBusy_park_the_scan()
    {
        var gate = NewGate();
        var source = new FakeSearchPageSource(5, hitsPerPage: 1) { Gate = p => p == 0 ? gate.Task : null };
        var index = new SearchIndex(5);
        using var scanner = Create(source, index);

        scanner.Start("x", 0);
        Assert.Equal(0, await source.NextStartedAsync());
        scanner.Pause();
        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        await Task.Delay(30);

        Assert.Equal([0], source.LoadedPages);
        Assert.True(index.IsSearched(0)); // the page in flight is finished, not abandoned
        Assert.Equal(SearchState.Searching, scanner.State);

        scanner.Pause(); // pauses nest
        scanner.Resume();
        await Task.Delay(30);
        Assert.Single(source.Loads);

        scanner.Resume();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        Assert.True(scanner.IsCompleted);
        Assert.Equal(5, index.SearchedCount);
        scanner.Resume(); // unbalanced: harmless

        var busy = true;
        var busySource = new FakeSearchPageSource(4, hitsPerPage: 1);
        var busyIndex = new SearchIndex(4);
        using var busyScanner = Create(busySource, busyIndex, isBusy: () => Volatile.Read(ref busy));
        busyScanner.Start("x", 0);
        await busyScanner.WhenIdleAsync().WaitAsync(_timeout);
        await Task.Delay(50);
        Assert.Empty(busySource.Loads);
        Assert.False(busyScanner.IsCompleted);

        Volatile.Write(ref busy, false);
        await WaitUntilAsync(() => busyScanner.IsCompleted);
        Assert.Equal(4, busyIndex.SearchedCount);
    }

    [Fact]
    public async Task The_cap_stops_the_scan_and_marks_the_index_truncated()
    {
        // Each page has 3 hits; the budget left is passed to every page, so the scan stops at 7 hits.
        var source = new FakeSearchPageSource(10, hitsPerPage: 1) { HitCount = _ => 3 };
        var index = new SearchIndex(10);
        var truncatedAtEnd = false;
        using var scanner = Create(source, index, maxTotalHits: 7);
        scanner.Progress += (_, _) => truncatedAtEnd = index.Truncated && scanner.IsCompleted;

        scanner.Start("x", 0);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal([7, 4, 1], source.Loads.Select(l => l.MaxHits));
        Assert.Equal(7, index.TotalHits);
        Assert.True(index.Truncated);
        Assert.Equal(SearchState.Truncated, scanner.State);
        Assert.True(scanner.IsCompleted);
        Assert.True(scanner.HasTextLayer);
        Assert.True(truncatedAtEnd); // the last Progress reports it

        // The budget used up exactly with pages left: there may be more, so the index is marked truncated too.
        var exact = new FakeSearchPageSource(10, hitsPerPage: 1) { HitCount = _ => 3 };
        var exactIndex = new SearchIndex(10);
        using (var s = Create(exact, exactIndex, maxTotalHits: 6))
        {
            s.Start("x", 0);
            await s.WhenIdleAsync().WaitAsync(_timeout);
            Assert.Equal([6, 3], exact.Loads.Select(l => l.MaxHits));
            Assert.True(exactIndex.Truncated);
        }

        // ...but not when the last page used it up.
        var last = new FakeSearchPageSource(2, hitsPerPage: 1) { HitCount = _ => 3 };
        var lastIndex = new SearchIndex(2);
        using (var s = Create(last, lastIndex, maxTotalHits: 6))
        {
            s.Start("x", 0);
            await s.WhenIdleAsync().WaitAsync(_timeout);
            Assert.False(lastIndex.Truncated);
            Assert.Equal(SearchState.Completed, s.State);
        }

        // A source that ignores the budget is cut to it.
        var greedy = new FakeSearchPageSource(3, hitsPerPage: 1)
        {
            Result = p => new SearchPageResult(true, Enumerable.Range(0, 50).Select(i => FakeSearchPageSource.NthHit(p, i)).ToArray(), false),
        };
        var greedyIndex = new SearchIndex(3);
        using (var s = Create(greedy, greedyIndex, maxTotalHits: 20))
        {
            s.Start("x", 0);
            await s.WhenIdleAsync().WaitAsync(_timeout);
            Assert.Equal(20, greedyIndex.TotalHits);
            Assert.True(greedyIndex.Truncated);
            Assert.Single(greedy.Loads);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => Create(source, index, maxTotalHits: 0));
    }

    [Fact]
    public async Task A_failing_page_is_counted_and_scanning_continues()
    {
        var source = new FakeSearchPageSource(7, hitsPerPage: 1)
        {
            ThrowSync = p => p == 1 ? new IOException("sync") : null,
            ThrowAsync = p => p switch
            {
                2 => new IOException("async"),
                3 => new OperationCanceledException("cancelled although nobody cancelled"),
                _ => null,
            },
            Result = p => p switch
            {
                4 => null, // a broken source
                5 => new SearchPageResult(true, [FakeSearchPageSource.NthHit(0, 0)], false), // a hit of another page
                _ => new SearchPageResult(true, [FakeSearchPageSource.NthHit(p, 0)], false),
            },
            Yield = true,
        };
        var index = new SearchIndex(7);
        using var scanner = Create(source, index);

        scanner.Start("x", 0);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.True(scanner.IsCompleted);
        Assert.Equal(7, source.Loads.Count);
        Assert.Equal(5, scanner.FailedCount);
        Assert.Equal(7, scanner.ProcessedCount);
        Assert.Equal([0, 6], Enumerable.Range(0, 7).Where(index.IsSearched));
        Assert.Equal(2, index.TotalHits);
    }

    [Fact]
    public async Task HasTextLayer_is_false_only_when_no_page_has_text_at_completion()
    {
        var gate = NewGate();
        var none = new FakeSearchPageSource(4, hitsPerPage: 1) { HasText = _ => false, HitCount = _ => 0, Gate = p => p == 3 ? gate.Task : null };
        using (var scanner = Create(none, new SearchIndex(4)))
        {
            scanner.Start("x", 0);
            await WaitUntilAsync(() => none.Loads.Count == 4);
            Assert.True(scanner.HasTextLayer); // not decided while searching
            gate.SetResult();
            await scanner.WhenIdleAsync().WaitAsync(_timeout);
            Assert.False(scanner.HasTextLayer);
        }

        // One page with text and no hit is enough.
        var one = new FakeSearchPageSource(4, hitsPerPage: 1) { HasText = p => p == 2, HitCount = _ => 0 };
        using (var scanner = Create(one, new SearchIndex(4)))
        {
            scanner.Start("x", 0);
            await scanner.WhenIdleAsync().WaitAsync(_timeout);
            Assert.True(scanner.HasTextLayer);
        }

        // Failed pages are not text: a document whose every page fails has nothing searchable.
        var failing = new FakeSearchPageSource(3, hitsPerPage: 1) { ThrowAsync = _ => new IOException("damaged") };
        using (var scanner = Create(failing, new SearchIndex(3)))
        {
            scanner.Start("x", 0);
            await scanner.WhenIdleAsync().WaitAsync(_timeout);
            Assert.Equal(3, scanner.FailedCount);
            Assert.False(scanner.HasTextLayer);
        }
    }

    [Fact]
    public async Task Dispose_cancels_and_raises_nothing_afterwards()
    {
        var gate = NewGate();
        var source = new FakeSearchPageSource(5, hitsPerPage: 1) { Gate = p => p == 0 ? gate.Task : null };
        var index = new SearchIndex(5);
        var progress = 0;
        var changed = 0;
        index.Changed += (_, _) => Interlocked.Increment(ref changed);
        var scanner = Create(source, index);
        scanner.Progress += (_, _) => Interlocked.Increment(ref progress);

        scanner.Start("x", 0);
        Assert.Equal(0, await source.NextStartedAsync());
        scanner.Dispose();
        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        await Task.Delay(30);

        Assert.Equal([0], source.LoadedPages); // never more than the search already in flight
        Assert.Equal(0, Volatile.Read(ref progress));
        Assert.Equal(0, Volatile.Read(ref changed));
        Assert.False(index.IsSearched(0));
        Assert.Equal(SearchState.Cancelled, scanner.State);
        Assert.False(scanner.IsCompleted);

        // Calls after Dispose are harmless; Start is not.
        scanner.Pause();
        scanner.Resume();
        scanner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scanner.Start("x", 0));
    }

    [Fact]
    public async Task One_load_in_flight_at_most()
    {
        var source = new FakeSearchPageSource(300, hitsPerPage: 1) { Yield = true, HitCount = p => p % 3 };
        var index = new SearchIndex(300);
        using var scanner = Create(source, index);

        scanner.Start("x", 150);
        for (var c = 0; c < 300; c += 37)
        {
            scanner.Start("x", c); // re-centre
            await Task.Yield();
        }

        // New queries, each on a new index, while a search is in flight.
        var indexes = new List<SearchIndex>();
        for (var q = 0; q < 5; q++)
        {
            var next = new SearchIndex(300);
            indexes.Add(next);
            await WaitUntilAsync(() => source.Loads.Count > 10 * (q + 1));
            scanner.Start("query " + q, q * 50, next);
        }

        await WaitUntilAsync(() => scanner.IsCompleted);

        Assert.Equal(1, source.MaxInFlight);
        var final = indexes[^1];
        Assert.Equal(300, final.SearchedCount);
        Assert.Equal(Enumerable.Range(0, 300).Sum(p => p % 3), final.TotalHits);
        Assert.Equal(300, source.Loads.Count(l => l.Query == "query 4"));
        Assert.Equal(300, source.Loads.Where(l => l.Query == "query 4").Select(l => l.Page).Distinct().Count());
    }

    [Fact]
    public async Task A_scanner_that_replaces_a_disposed_one_waits_for_its_search_in_flight()
    {
        // A new query gets a new scanner (the owner disposes the old one): the old scanner's search in flight, which
        // ignores its token here, returns before the new scanner searches anything, so the PDFium thread never holds
        // two search jobs (Task 6 integration finding).
        var source = new FakeSearchPageSource(50, hitsPerPage: 1);
        var gate = NewGate();
        source.Gate = page => page == 10 ? gate.Task : null;
        var old = Create(source, new SearchIndex(50));
        old.Start("a", 10);
        Assert.Equal(10, await source.NextStartedAsync());
        old.Dispose();
        var stopped = old.WhenStoppedAsync();
        Assert.False(stopped.IsCompleted);

        var index = new SearchIndex(50);
        using var next = new SearchScanner(source, index, idleGap: TimeSpan.Zero, after: stopped);
        next.Start("b", 0, index);
        await Task.Delay(50);
        Assert.Single(source.Loads); // nothing yet: the old search is still in flight

        gate.SetResult();
        await stopped.WaitAsync(_timeout);
        await WaitUntilAsync(() => next.IsCompleted);
        Assert.Equal(1, source.MaxInFlight);
        Assert.Equal(50, index.SearchedCount);
    }

    [Fact]
    public async Task A_faulted_or_cancelled_predecessor_task_does_not_stop_the_scan()
    {
        var source = new FakeSearchPageSource(5, hitsPerPage: 1);
        var index = new SearchIndex(5);
        using var faulted = new SearchScanner(source, index, idleGap: TimeSpan.Zero, after: Task.FromException(new InvalidOperationException()));
        faulted.Start("x", 0);
        await WaitUntilAsync(() => faulted.IsCompleted);
        Assert.Equal(5, index.SearchedCount);

        var other = new SearchIndex(5);
        using var cancelled = new SearchScanner(source, other, idleGap: TimeSpan.Zero, after: Task.FromCanceled(new CancellationToken(true)));
        cancelled.Start("x", 0);
        await WaitUntilAsync(() => cancelled.IsCompleted);
        Assert.Equal(5, other.SearchedCount);
        using var idle = new SearchScanner(source, new SearchIndex(5));
        Assert.True(idle.WhenStoppedAsync().IsCompleted); // never started
    }

    [Fact]
    public async Task Cheap_pages_are_searched_back_to_back_and_the_idle_gap_comes_once_per_slice_of_work()
    {
        // As for the mark scan: a gap after every page would cost a whole timer tick (~15.6 ms) per page.
        var source = new FakeSearchPageSource(200, hitsPerPage: 1);
        var index = new SearchIndex(200);
        using var scanner = new SearchScanner(source, index, idleGap: TimeSpan.FromMilliseconds(50));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        scanner.Start("x", 0);
        await WaitUntilAsync(() => scanner.IsCompleted);
        watch.Stop();

        Assert.Equal(200, index.SearchedCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"200 instant pages took {watch.ElapsedMilliseconds} ms (one rest per slice expected)");
    }

    [Fact]
    public void Bad_arguments_are_rejected()
    {
        var source = new FakeSearchPageSource(1, hitsPerPage: 1);
        var index = new SearchIndex(1);
        Assert.Throws<ArgumentNullException>(() => new SearchScanner(null!, index));
        Assert.Throws<ArgumentNullException>(() => new SearchScanner(source, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchScanner(source, index, idleGap: TimeSpan.FromMilliseconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchScanner(source, index, workSlice: TimeSpan.FromMilliseconds(-1)));
        using var scanner = new SearchScanner(source, index);
        Assert.Throws<ArgumentNullException>(() => scanner.Start(null!, 0));
        Assert.Throws<ArgumentNullException>(() => scanner.Start("x", 0, null!));
    }

    [Fact]
    public async Task No_unobserved_task_exceptions()
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            // A search full of failures runs to completion, despite a broken listener...
            var failing = new FakeSearchPageSource(200, hitsPerPage: 1)
            {
                Yield = true,
                ThrowSync = p => p % 5 == 0 ? new IOException("sync") : null,
                ThrowAsync = p => p % 5 == 1 ? new IOException("async") : p % 5 == 2 ? new OperationCanceledException() : null,
            };
            using (var scanner = Create(failing, new SearchIndex(200), isBusy: () => throw new InvalidOperationException("busy")))
            {
                scanner.Progress += (_, _) => throw new InvalidOperationException("a broken listener");
                scanner.Start("x", 100);
                await WaitUntilAsync(() => scanner.IsCompleted);
            }

            // ...a search superseded and then disposed mid-load ends quietly...
            var gate = NewGate();
            var held = new FakeSearchPageSource(50, hitsPerPage: 1) { Gate = p => p == 10 ? gate.Task : null };
            var scanner2 = Create(held, new SearchIndex(50));
            scanner2.Start("x", 10);
            await held.NextStartedAsync();
            scanner2.Start("y", 10, new SearchIndex(50));
            scanner2.Pause();
            scanner2.Dispose();
            gate.SetException(new IOException("after dispose"));
            await scanner2.WhenIdleAsync().WaitAsync(_timeout);
            await Task.Delay(30);

            // ...and so does one whose index is smaller than the document.
            var small = new SearchIndex(10);
            using (var scanner3 = Create(new FakeSearchPageSource(30, hitsPerPage: 1), small))
            {
                scanner3.Start("x", 0);
                await WaitUntilAsync(() => scanner3.IsCompleted);
                Assert.Equal(10, small.SearchedCount);
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
}
