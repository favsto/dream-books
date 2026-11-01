using System.Diagnostics;

namespace DreamBooks.Core.Search;

/// <summary>
/// Background text search that fills a <see cref="SearchIndex"/> page by page (FR-13, FR-105): from the centre page
/// forward to the end, then from the first page up to the one before the centre (the order the user meets the
/// results when stepping forward with wrap-around).
/// </summary>
/// <remarks>
/// <para><b>Queries and runs.</b> Each <see cref="Start(string, int, SearchIndex)"/> begins a new run and cancels
/// the previous one. A new query needs a new <see cref="SearchIndex"/>: the owner creates one per query, and Start
/// refuses an index already holding another query's hits. Every write is checked against the run token (the
/// current run object, under the lock) right before it, so a superseded or disposed run publishes nothing; its own
/// search in flight is cancelled, and the new run waits for it to return before it searches, so that at most one
/// search is ever in flight. Starting again with the same query and index re-centres: pages already searched are
/// not searched again. The same holds for a new scanner over a partly searched index (renderer swap after Save).</para>
/// <para><b>Hit cap.</b> Each page is given the budget left (<c>maxTotalHits</c> minus the hits in the index) as its
/// hit limit; a page cut short by it, or a page left to search once the budget is spent, marks the index
/// <see cref="SearchIndex.Truncated"/> and ends the run (<see cref="SearchState.Truncated"/>). A single-character
/// query on a thousand-page book therefore stops after the first <c>maxTotalHits</c> hits.</para>
/// <para><b>Text layer.</b> <see cref="HasTextLayer"/> is decided only when a run completes: false when no searched
/// page reported text (failed pages count as without text), true before that and for a truncated run.</para>
/// <para><b>Pacing.</b> As <c>MarkScanner</c>: a pool thread, one search in flight, a yield between pages and a rest
/// (<c>idleGap</c>, default 5 ms) after each slice of work (<c>workSlice</c>, default 10 ms) rather than after every
/// page (a short delay lasts a whole ~15.6 ms timer tick on Windows); it parks while <see cref="Pause"/> is in effect
/// or <c>isBusy</c> returns true (polled every 20 ms). A search in flight when the scan is paused is finished.</para>
/// <para><b>Failures and events.</b> A page whose search throws or returns garbage is counted in
/// <see cref="FailedCount"/>, left unsearched, and the scan goes on; no exception ever escapes, also not from
/// <see cref="Progress"/> handlers or from <c>isBusy</c> (then not busy). <see cref="Progress"/> is raised on the
/// scan's thread after each page and once when the run ends; never for a superseded run, and none starts after
/// <see cref="Dispose"/> has returned, except possibly one whose check was already passed. The query is document
/// content: it is never logged nor put in an exception message. All members are thread-safe and none blocks.</para>
/// </remarks>
public sealed class SearchScanner : IDisposable
{
    private static readonly TimeSpan _defaultIdleGap = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan _defaultWorkSlice = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan _busyPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly ISearchPageSource _source;
    private readonly int _maxTotalHits;
    private readonly Func<bool>? _isBusy;
    private readonly TimeSpan _idleGap;
    private readonly TimeSpan _workSlice;
    private readonly object _gate = new();

    private SearchIndex _index;
    private Run? _run;
    private TaskCompletionSource _resumed = CompletedSignal();
    private int _pageCount = -1;
    private int _pauses;
    private bool _disposed;

    public SearchScanner(
        ISearchPageSource source,
        SearchIndex index,
        int maxTotalHits = 5000,
        Func<bool>? isBusy = null,
        TimeSpan? idleGap = null,
        TimeSpan? workSlice = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTotalHits, 1);
        if (idleGap is { } gap && gap < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleGap), "The idle gap cannot be negative.");
        }

        if (workSlice is { } slice && slice < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(workSlice), "The work slice cannot be negative.");
        }

        _source = source;
        _index = index;
        _maxTotalHits = maxTotalHits;
        _isBusy = isBusy;
        _idleGap = idleGap ?? _defaultIdleGap;
        _workSlice = workSlice ?? _defaultWorkSlice;
    }

    /// <summary>Raised on the scan's thread after each page (skipped and failed ones included) and when a run ends.</summary>
    public event EventHandler? Progress;

    /// <summary>The index the current run fills: the constructor's until a Start names another.</summary>
    public SearchIndex Index
    {
        get
        {
            lock (_gate)
            {
                return _index;
            }
        }
    }

    public SearchState State
    {
        get
        {
            lock (_gate)
            {
                return _run?.State ?? SearchState.NotStarted;
            }
        }
    }

    /// <summary>True once the current run has ended by itself: every page searched, or stopped by the hit cap.</summary>
    public bool IsCompleted => State is SearchState.Completed or SearchState.Truncated;

    /// <summary>False only once a run has completed without any searched page having text; see the remarks.</summary>
    public bool HasTextLayer
    {
        get
        {
            Run? run;
            lock (_gate)
            {
                run = _run;
            }

            return run is not { State: SearchState.Completed, Query.Length: > 0 } || run.Index.AnyPageHasText;
        }
    }

    /// <summary>Pages the current run has done: searched, skipped because already searched, or failed.</summary>
    public int ProcessedCount => Current is { } run ? Volatile.Read(ref run.Processed) : 0;

    /// <summary>Pages of the current run whose search threw or returned garbage; they stay unsearched.</summary>
    public int FailedCount => Current is { } run ? Volatile.Read(ref run.Failed) : 0;

    private Run? Current
    {
        get
        {
            lock (_gate)
            {
                return _run;
            }
        }
    }

    /// <summary>Starts searching for <paramref name="rawQuery"/> into the current <see cref="Index"/>.</summary>
    public void Start(string rawQuery, int centrePage) => Start(rawQuery, centrePage, Index);

    /// <summary>
    /// Folds <paramref name="rawQuery"/> and starts searching for it into <paramref name="index"/>, around
    /// <paramref name="centrePage"/> (clamped to the document), cancelling the previous run. An empty folded query
    /// searches nothing and completes at once.
    /// </summary>
    /// <exception cref="ArgumentException">The index already belongs to a run of another query.</exception>
    public void Start(string rawQuery, int centrePage, SearchIndex index)
    {
        ArgumentNullException.ThrowIfNull(rawQuery);
        ArgumentNullException.ThrowIfNull(index);
        var query = TextFolding.FoldQuery(rawQuery);
        Run? previous;
        Run run;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _run;
            if (previous is not null && ReferenceEquals(previous.Index, index)
                && !string.Equals(previous.Query, query, StringComparison.Ordinal))
            {
                throw new ArgumentException("A new query needs a new search index.", nameof(index));
            }

            if (_pageCount < 0)
            {
                _pageCount = _source.PageCount; // read once: a later Start never calls into the source
            }

            run = new Run(index, query, previous?.Finished.Task ?? Task.CompletedTask);
            if (query.Length > 0)
            {
                run.Queue.AddRange(SearchOrder(centrePage, _pageCount).Where(p => !index.IsSearched(p)).Reverse());
            }

            _index = index;
            _run = run;
        }

        // Outside the lock: cancellation may run the old run's continuations inline. The token sources are not
        // disposed (they have no timer): the search in flight may still hold the token.
        previous?.Cts.Cancel();
        _ = Task.Run(() => RunAsync(run), CancellationToken.None);
    }

    /// <summary>
    /// Parks the scan after the page being searched. Pauses nest (inking and saving may overlap): the scan
    /// continues when every <see cref="Pause"/> has had its <see cref="Resume"/>. They outlast a new Start.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_pauses++ == 0)
            {
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>Undoes one <see cref="Pause"/>; an unbalanced call is ignored.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_pauses == 0 || --_pauses > 0)
            {
                return;
            }

            _resumed.TrySetResult();
            if (_run is { State: SearchState.Searching } run && !_disposed)
            {
                SetWorking(run); // synchronously, so that a WhenIdleAsync right after Resume waits for the work
            }
        }
    }

    /// <summary>
    /// Completes when the current run is not working: parked (paused or busy), ended, or ended by
    /// <see cref="Dispose"/> (after its search in flight, if any, returned). Meant for tests.
    /// </summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _run?.Idle.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>Cancels the search; nothing more is searched or published. Idempotent, never blocks.</summary>
    public void Dispose()
    {
        Run? run;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            run = _run;
            if (run is { State: SearchState.Searching })
            {
                run.State = SearchState.Cancelled;
            }
        }

        run?.Cts.Cancel();
    }

    // Centre, centre+1, ..., last, then 0, ..., centre-1.
    private static IEnumerable<int> SearchOrder(int centrePage, int pageCount)
    {
        var centre = Math.Clamp(centrePage, 0, Math.Max(pageCount - 1, 0));
        return Enumerable.Range(centre, pageCount - centre).Concat(Enumerable.Range(0, Math.Min(centre, pageCount)));
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private async Task RunAsync(Run run)
    {
        var ct = run.Cts.Token;
        try
        {
            await run.Previous.WaitAsync(ct); // the previous run's search in flight returns first: one at a time
            var sliceStart = Stopwatch.GetTimestamp();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (await ParkIfNeededAsync(run, ct))
                {
                    sliceStart = Stopwatch.GetTimestamp(); // parked: that was a rest
                    continue; // look again: paused or busy may have changed, or the run was cancelled
                }

                int page;
                lock (_gate)
                {
                    if (run.Queue.Count == 0)
                    {
                        End(run, SearchState.Completed);
                        break;
                    }

                    page = run.Queue[^1];
                    run.Queue.RemoveAt(run.Queue.Count - 1);
                }

                var goOn = await SearchPageAsync(run, page, ct);
                Interlocked.Increment(ref run.Processed);
                if (!goOn)
                {
                    lock (_gate)
                    {
                        End(run, SearchState.Truncated);
                    }

                    break;
                }

                RaiseProgress(run);

                // The rest comes after a slice of work, not after every page: a short Task.Delay lasts a whole timer
                // tick (~15.6 ms on Windows), many times the cost of searching a page.
                if (_idleGap > TimeSpan.Zero && Stopwatch.GetElapsedTime(sliceStart) >= _workSlice)
                {
                    await Task.Delay(_idleGap, ct);
                    sliceStart = Stopwatch.GetTimestamp();
                }
                else
                {
                    await Task.Yield();
                }
            }

            RaiseProgress(run); // the run's end: IsCompleted, and Truncated when the cap stopped it
        }
        catch (Exception)
        {
            // Cancellation by a new Start or by Dispose, or anything unexpected: the run ends quietly, never unobserved.
        }
        finally
        {
            // A run cancelled while still waiting for its predecessor ends only after it: otherwise the next run
            // could search while the predecessor's search is still in flight. The task never faults.
            await run.Previous;
            lock (_gate)
            {
                run.Idle.TrySetResult();
            }

            run.Finished.TrySetResult();
        }
    }

    // Returns false when the run must stop: the hit cap was reached, or the run is no longer current.
    private async Task<bool> SearchPageAsync(Run run, int page, CancellationToken ct)
    {
        var index = run.Index;
        if (index.IsSearched(page))
        {
            return true; // searched meanwhile by a run of the same query
        }

        var budget = _maxTotalHits - index.TotalHits;
        if (budget <= 0)
        {
            // The budget is spent and pages are left: there may be more hits than the index holds.
            if (IsCurrent(run))
            {
                index.Truncated = true;
            }

            return false;
        }

        try
        {
            var result = await _source.SearchPageAsync(page, run.Query, budget, ct);
            ct.ThrowIfCancellationRequested();
            if (result?.Hits is not { } hits)
            {
                throw new InvalidDataException("The search source returned no result.");
            }

            var truncated = result.Truncated || hits.Count > budget;
            if (hits.Count > budget)
            {
                hits = hits.Take(budget).ToArray(); // a source that ignores the limit is cut to it
            }

            if (!IsCurrent(run))
            {
                return false; // the token check: a superseded or disposed run never writes
            }

            // Throws ArgumentException for a hit of another page, ArgumentOutOfRangeException for a page past the
            // end of a smaller index: counted as failed.
            index.SetPage(page, hits, result.HasTextLayer);
            if (truncated)
            {
                index.Truncated = true;
                return false;
            }

            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref run.Failed);
            return true;
        }
    }

    // Returns true after waiting, false when the run may go on.
    private async Task<bool> ParkIfNeededAsync(Run run, CancellationToken ct)
    {
        Task? resumed = null;
        lock (_gate)
        {
            if (_pauses > 0)
            {
                resumed = _resumed.Task;
                run.Idle.TrySetResult();
            }
        }

        if (resumed is not null)
        {
            await resumed.WaitAsync(ct); // Resume marks the run as working again
            lock (_gate)
            {
                SetWorking(run); // also when the pause began before this run started
            }

            return true;
        }

        if (!IsBusy())
        {
            return false;
        }

        lock (_gate)
        {
            run.Idle.TrySetResult();
        }

        await Task.Delay(_busyPollInterval, ct);
        lock (_gate)
        {
            SetWorking(run);
        }

        return true;
    }

    private bool IsBusy()
    {
        try
        {
            return _isBusy?.Invoke() ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsCurrent(Run run)
    {
        lock (_gate)
        {
            return ReferenceEquals(_run, run) && !_disposed;
        }
    }

    private void RaiseProgress(Run run)
    {
        if (!IsCurrent(run))
        {
            return;
        }

        try
        {
            Progress?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A broken listener must not stop the scan.
        }
    }

    // Must be called with the lock held. A run cancelled meanwhile keeps its state.
    private static void End(Run run, SearchState state)
    {
        if (run.State == SearchState.Searching)
        {
            run.State = state;
        }
    }

    // Must be called with the lock held.
    private static void SetWorking(Run run)
    {
        if (run.Idle.Task.IsCompleted && run.State == SearchState.Searching)
        {
            run.Idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>One query's pass over the document: the run token every write is checked against.</summary>
    private sealed class Run(SearchIndex index, string query, Task previous)
    {
        // Counters, updated with Interlocked.
        public int Processed;
        public int Failed;

        public SearchIndex Index { get; } = index;

        public string Query { get; } = query;

        /// <summary>The previous run's end, awaited before the first search.</summary>
        public Task Previous { get; } = previous;

        public CancellationTokenSource Cts { get; } = new();

        /// <summary>Remaining pages, the next one last. Guarded by the scanner's lock.</summary>
        public List<int> Queue { get; } = [];

        /// <summary>Guarded by the scanner's lock.</summary>
        public SearchState State { get; set; } = SearchState.Searching;

        /// <summary>Completed while the run is not working. Guarded by the scanner's lock.</summary>
        public TaskCompletionSource Idle { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed when the run's task has ended, its search in flight included.</summary>
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
