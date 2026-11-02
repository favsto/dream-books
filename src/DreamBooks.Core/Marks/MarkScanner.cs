using System.Diagnostics;

namespace DreamBooks.Core.Marks;

/// <summary>
/// Background scan that fills a <see cref="MarkIndex"/> for every page of a document (FR-100, FR-101): it loads
/// one page at a time from an <see cref="IMarkPageSource"/>, recognises its marks and publishes them, nearest
/// the centre page first, then alternating outward (the following page before the preceding one).
/// </summary>
/// <remarks>
/// <para>The scan runs on a pool thread and never has more than one load in flight. Between pages it yields;
/// after each slice of work (<c>workSlice</c>, default 10 ms) it rests (<c>idleGap</c>, default 5 ms, in practice
/// at least one timer tick), so cheap pages are not each charged a whole timer tick; and it parks while <see cref="Pause"/> is in effect or <c>isBusy</c> returns
/// true (polled every 20 ms while parked), so that inking, scrolling, rendering and saving are not delayed.
/// A load already in flight when the scan is paused or disposed is finished (paused) or abandoned (disposed),
/// never cut short: the source's own work is awaited so that nothing it does goes unobserved.</para>
/// <para>Pages that already have marks in the index (set live by <see cref="MarkTracker"/> because the page is
/// loaded for editing) are skipped without loading, and a scan result is published only if the page is still
/// unscanned at that moment (<see cref="MarkIndex.TrySetPageIfUnscanned"/>), so stale strokes never overwrite
/// a live update. A page whose load or recognition throws is counted in <see cref="FailedCount"/>, left
/// unscanned, and the scan goes on; no exception ever escapes, also not from <see cref="Progress"/> handlers or
/// from <c>isBusy</c> (which then counts as not busy).</para>
/// <para>All members are thread-safe and none blocks. <see cref="Progress"/> is raised on the scan's pool
/// thread after each page; none starts after <see cref="Dispose"/> has returned, except possibly one whose
/// check was already passed at that moment.</para>
/// </remarks>
public sealed class MarkScanner : IDisposable
{
    private static readonly TimeSpan _defaultIdleGap = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan _defaultWorkSlice = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan _busyPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly IMarkPageSource _source;
    private readonly MarkRecognizer _recognizer;
    private readonly MarkIndex _index;
    private readonly Func<bool>? _isBusy;
    private readonly TimeSpan _idleGap;
    private readonly TimeSpan _workSlice;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    // Remaining pages, the next one last.
    private readonly List<int> _queue = [];
    private TaskCompletionSource _resumed = CompletedSignal();
    private TaskCompletionSource _idle = CompletedSignal();
    private int _pageCount;
    private int _pauses;
    private bool _started;
    private bool _completed;
    private bool _disposed;
    private int _processed;
    private int _failed;

    public MarkScanner(
        IMarkPageSource source,
        MarkRecognizer recognizer,
        MarkIndex index,
        Func<bool>? isBusy = null,
        TimeSpan? idleGap = null,
        TimeSpan? workSlice = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(index);
        if (idleGap is { } gap && gap < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleGap), "The idle gap cannot be negative.");
        }

        if (workSlice is { } slice && slice < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(workSlice), "The work slice cannot be negative.");
        }

        _source = source;
        _recognizer = recognizer;
        _index = index;
        _isBusy = isBusy;
        _idleGap = idleGap ?? _defaultIdleGap;
        _workSlice = workSlice ?? _defaultWorkSlice;
    }

    /// <summary>Raised on the scan's thread after each page, including skipped and failed ones.</summary>
    public event EventHandler? Progress;

    /// <summary>Pages done so far: scanned, skipped because set live, or failed.</summary>
    public int ProcessedCount => Volatile.Read(ref _processed);

    /// <summary>Pages whose load or recognition threw; they stay unscanned.</summary>
    public int FailedCount => Volatile.Read(ref _failed);

    /// <summary>True once every page has been processed.</summary>
    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    /// <summary>
    /// Starts the scan around <paramref name="centrePage"/> (clamped to the document). Calling it again acts as
    /// <see cref="Recentre"/>.
    /// </summary>
    public void Start(int centrePage)
    {
        CancellationToken token;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                Reorder(centrePage);
                return;
            }

            _started = true;
            _pageCount = _source.PageCount; // read once: Recentre must never call into the source
            _queue.AddRange(Enumerable.Range(0, _pageCount));
            Reorder(centrePage);
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token = _cts.Token; // under the lock, together with the _disposed check
        }

        _ = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    /// <summary>
    /// Re-orders the pages not scanned yet around <paramref name="centrePage"/> (clamped). The page being loaded
    /// is not affected. Ignored before <see cref="Start"/> and after <see cref="Dispose"/>.
    /// </summary>
    public void Recentre(int centrePage)
    {
        lock (_gate)
        {
            if (_started && !_disposed)
            {
                Reorder(centrePage);
            }
        }
    }

    /// <summary>
    /// Parks the scan after the page being loaded. Pauses nest (inking and saving may overlap): the scan
    /// continues when every <see cref="Pause"/> has had its <see cref="Resume"/>.
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
            if (_started && !_completed && !_disposed)
            {
                SetWorking(); // synchronously, so that a WhenIdleAsync right after Resume waits for the work
            }
        }
    }

    /// <summary>
    /// Completes when the scan is not working: not started, parked (paused or busy), finished, or ended by
    /// <see cref="Dispose"/> (after the load in flight, if any, returned). Meant for tests.
    /// </summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _idle.Task;
        }
    }

    /// <summary>Cancels the scan; nothing more is loaded or published. Idempotent, never blocks.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Outside the lock: cancellation may run the scan's continuations inline. The token source is deliberately
        // not disposed (it has no timer, so there is nothing to release): the load in flight may still hold the
        // token, and every use of it, WaitHandle included, stays valid.
        _cts.Cancel();
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var sliceStart = Stopwatch.GetTimestamp();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (await ParkIfNeededAsync(ct))
                {
                    sliceStart = Stopwatch.GetTimestamp(); // parked: that was a rest
                    continue; // look again: paused or busy may have changed, or the scan was disposed
                }

                int page;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        _completed = true;
                        return;
                    }

                    page = _queue[^1];
                    _queue.RemoveAt(_queue.Count - 1);
                }

                await ScanPageAsync(page, ct);
                Interlocked.Increment(ref _processed);
                RaiseProgress();

                // The rest comes after a slice of work, not after every page: a short Task.Delay lasts a whole timer
                // tick (~15.6 ms on Windows), many times the cost of a page without annotations.
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
        }
        catch (Exception)
        {
            // Cancellation by Dispose, or anything unexpected: the scan ends quietly, never unobserved.
        }
        finally
        {
            lock (_gate)
            {
                _idle.TrySetResult();
            }
        }
    }

    // Returns true after waiting, false when the scan may go on.
    private async Task<bool> ParkIfNeededAsync(CancellationToken ct)
    {
        Task? resumed = null;
        lock (_gate)
        {
            if (_pauses > 0)
            {
                resumed = _resumed.Task;
                _idle.TrySetResult();
            }
        }

        if (resumed is not null)
        {
            await resumed.WaitAsync(ct); // Resume marks the scan as working again
            return true;
        }

        if (!IsBusy())
        {
            return false;
        }

        lock (_gate)
        {
            _idle.TrySetResult();
        }

        await Task.Delay(_busyPollInterval, ct);
        lock (_gate)
        {
            SetWorking();
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

    private async Task ScanPageAsync(int page, CancellationToken ct)
    {
        if (_index.IsScanned(page))
        {
            return; // set live meanwhile: the index already holds newer marks than the stored strokes
        }

        try
        {
            var strokes = await _source.TryLoadStrokesForScanAsync(page, ct);
            ct.ThrowIfCancellationRequested();
            var (marks, notes) = strokes is null
                ? ([], [])
                : _recognizer.RecognizePage(page, _source.GetPageDimensions(page).WidthPt, strokes.Strokes);
            ct.ThrowIfCancellationRequested();

            // Throws ArgumentOutOfRangeException for a page past the end of a smaller index: counted as failed.
            _index.TrySetPageIfUnscanned(page, marks, notes);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failed);
        }
    }

    private void RaiseProgress()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
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

    // Must be called with the lock held.
    private void SetWorking()
    {
        if (_idle.Task.IsCompleted)
        {
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    // Must be called with the lock held. Scan order: centre, centre+1, centre-1, centre+2, ...; stored reversed.
    private void Reorder(int centrePage)
    {
        var centre = Math.Clamp(centrePage, 0, Math.Max(_pageCount - 1, 0));
        _queue.Sort((a, b) => Key(b, centre).CompareTo(Key(a, centre)));
    }

    private static (int Distance, int Before) Key(int page, int centre)
        => (Math.Abs(page - centre), page < centre ? 1 : 0);
}
