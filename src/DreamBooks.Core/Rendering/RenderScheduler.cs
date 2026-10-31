namespace DreamBooks.Core.Rendering;

/// <summary>
/// Decides which page bitmaps to render, in which order, and which to keep (NFR-03, NFR-05, NFR-06,
/// NFR-07). Pure scheduling logic: the actual rendering is delegated to an
/// <see cref="IBitmapRenderer{TBitmap}"/> and is always started on a thread-pool thread, never on the
/// caller's thread (NFR-04).
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Every public member is thread-safe; the view calls <see cref="SetWanted"/>,
/// <see cref="TryGet"/> and the invalidation methods from the UI thread while renders complete on
/// thread-pool threads. All state is guarded by one lock that is never held while user code (the renderer,
/// event handlers, cancellation callbacks) runs, except the pure <c>sizeOf</c> function.</para>
/// <para><b>One render at a time.</b> PDFium has a single thread, so two concurrent renders would only
/// queue behind each other there while hiding the priority order from the scheduler. A newly urgent page
/// therefore waits at most for the render in progress; a render that became useless (page no longer
/// wanted, generation changed, sharp size changed) is cancelled at once and its result discarded.</para>
/// <para><b>Order.</b> Wanted pages are served by <see cref="RenderRequest.Priority"/> (lower first), then
/// previews before sharp renders within the same priority, then page index. A page with nothing cached
/// gets a preview first (unless the request disables it); a page gets a sharp render when its cached
/// bitmap is not a sharp bitmap of the current generation at the requested sharp size.</para>
/// <para><b>Generations and stale bitmaps.</b> <see cref="InvalidateAll"/> and
/// <see cref="InvalidatePage"/> give the affected pages a new generation. Their old bitmaps stay cached
/// (no blank page) and are reported as stale by <see cref="IsStale"/> — i.e. their
/// <see cref="CachedBitmap{TBitmap}.Generation"/> differs from <see cref="GetGeneration"/> — until a new
/// render replaces them. A render result whose generation is no longer current, or for a page no longer
/// wanted, is discarded.</para>
/// <para><b>Budget.</b> After each change the cache evicts the least recently used bitmaps of pages that
/// are not wanted until it is within <c>byteBudget</c>. Bitmaps of wanted pages are never evicted (that
/// would blank a page or make it render again in a loop). If the wanted set alone would exceed the budget,
/// pages with a priority above 0 keep their preview and do not get a sharp render; visible pages
/// (priority 0) always get one, so the budget may be exceeded only by what is visible.</para>
/// <para><b>Failures.</b> A renderer returning <see langword="null"/> (or throwing anything other than a
/// requested cancellation) marks the page as failed: it is retried once at the next
/// <see cref="SetWanted"/>, and after a second failure only after <see cref="InvalidateAll"/> or
/// <see cref="InvalidatePage"/>, or when it leaves and re-enters the wanted set. There is never a tight
/// retry loop.</para>
/// <para><b>Events.</b> <see cref="Rendered"/> is posted to the <see cref="SynchronizationContext"/>
/// captured at construction, or raised inline on the render's thread-pool thread when there is none.
/// Events still queued when the scheduler is disposed are dropped. The scheduler never disposes bitmaps; it
/// only drops its references.</para>
/// </remarks>
public sealed class RenderScheduler<TBitmap> : IDisposable
{
    private const double _defaultBytesPerPixel = 4.0;

    private readonly IBitmapRenderer<TBitmap> _renderer;
    private readonly Func<TBitmap, long> _sizeOf;
    private readonly long _byteBudget;
    private readonly SynchronizationContext? _context;

    private readonly Lock _lock = new();
    private readonly Dictionary<int, RenderRequest> _wanted = [];
    private readonly Dictionary<int, CacheEntry> _cache = [];
    private readonly Dictionary<int, long> _pageGenerations = [];
    private readonly Dictionary<int, Failure> _failures = [];

    private long _generation;
    private long _generationCounter;
    private long _clock;
    private long _cachedBytes;
    private double _bytesPerPixel = _defaultBytesPerPixel;
    private Job? _current;
    private TaskCompletionSource? _idle;
    private int _raising;
    private bool _disposed;

    /// <param name="renderer">Renders one page; called on a thread-pool thread, one call at a time.</param>
    /// <param name="sizeOf">Memory cost of a bitmap in bytes (pure, cheap).</param>
    /// <param name="byteBudget">Target size of the cache in bytes.</param>
    /// <param name="context">
    /// Returns the context <see cref="Rendered"/> is raised on; evaluated once, here. Defaults to the
    /// constructing thread's <see cref="SynchronizationContext.Current"/>.
    /// </param>
    public RenderScheduler(
        IBitmapRenderer<TBitmap> renderer,
        Func<TBitmap, long> sizeOf,
        long byteBudget,
        Func<SynchronizationContext?>? context = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(sizeOf);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);

        _renderer = renderer;
        _sizeOf = sizeOf;
        _byteBudget = byteBudget;
        _context = context is null ? SynchronizationContext.Current : context();
    }

    /// <summary>A page bitmap entered the cache. Carries the page index, quality and generation.</summary>
    public event EventHandler<RenderedEventArgs<TBitmap>>? Rendered;

    /// <summary>Total <c>sizeOf</c> of the cached bitmaps.</summary>
    public long CachedBytes
    {
        get
        {
            lock (_lock)
            {
                return _cachedBytes;
            }
        }
    }

    /// <summary>Number of cached bitmaps (at most one per page).</summary>
    public int CachedCount
    {
        get
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>
    /// Replaces the wanted set. Pages no longer wanted are cancelled and become eviction candidates; new
    /// ones are queued by priority. A page listed twice keeps its most urgent request.
    /// </summary>
    public void SetWanted(IReadOnlyList<RenderRequest> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        Job? toCancel;
        Step step;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _wanted.Clear();
            foreach (var request in wanted)
            {
                if (!_wanted.TryGetValue(request.PageIndex, out var existing) || request.Priority < existing.Priority)
                {
                    _wanted[request.PageIndex] = request;
                }
            }

            List<int>? forgotten = null;
            foreach (var (page, failure) in _failures)
            {
                if (!_wanted.ContainsKey(page))
                {
                    (forgotten ??= []).Add(page);
                }
                else if (failure.Count == 1)
                {
                    failure.RetryArmed = true;
                }
            }

            forgotten?.ForEach(page => _failures.Remove(page));

            toCancel = TakeObsoleteCurrent();
            Evict();
            step = NextStep();
        }

        toCancel?.Cancel();
        Run(step);
    }

    /// <summary>
    /// Gets the cached bitmap of a page, if any, and marks it as recently used. The bitmap may be stale
    /// (see <see cref="IsStale"/>): show it stretched until the <see cref="Rendered"/> event brings the
    /// current one.
    /// </summary>
    public bool TryGet(int pageIndex, out CachedBitmap<TBitmap>? bitmap)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pageIndex, out var entry))
            {
                entry.LastUsed = ++_clock;
                bitmap = entry.Bitmap;
                return true;
            }
        }

        bitmap = null;
        return false;
    }

    /// <summary>The current generation of a page: bitmaps rendered for another one are stale.</summary>
    public long GetGeneration(int pageIndex)
    {
        lock (_lock)
        {
            return GenerationOf(pageIndex);
        }
    }

    /// <summary>
    /// True when <paramref name="bitmap"/> was rendered for an older generation of the page (old zoom, DPI,
    /// renderer or page content) and must only be shown as a stretched placeholder.
    /// </summary>
    public bool IsStale(int pageIndex, CachedBitmap<TBitmap> bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return bitmap.Generation != GetGeneration(pageIndex);
    }

    /// <summary>
    /// Zoom, DPI or renderer changed: every page gets a new generation, cached bitmaps stay available
    /// flagged stale, failures are forgotten and the wanted pages are rendered again.
    /// </summary>
    public void InvalidateAll()
    {
        Job? toCancel;
        Step step;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _generation = ++_generationCounter;
            _pageGenerations.Clear();
            _failures.Clear();
            toCancel = TakeObsoleteCurrent();
            step = NextStep();
        }

        toCancel?.Cancel();
        Run(step);
    }

    /// <summary>As <see cref="InvalidateAll"/>, for one page only.</summary>
    public void InvalidatePage(int pageIndex)
    {
        Job? toCancel;
        Step step;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _pageGenerations[pageIndex] = ++_generationCounter;
            _failures.Remove(pageIndex);
            toCancel = TakeObsoleteCurrent();
            step = NextStep();
        }

        toCancel?.Cancel();
        Run(step);
    }

    /// <summary>
    /// Cancels the render in progress, drops every cached bitmap and stops events, including events
    /// already posted but not yet delivered. Later calls are ignored.
    /// </summary>
    public void Dispose()
    {
        Job? toCancel;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _wanted.Clear();
            _cache.Clear();
            _failures.Clear();
            _pageGenerations.Clear();
            _cachedBytes = 0;
            toCancel = _current;
        }

        toCancel?.Cancel();
    }

    /// <summary>Completes when no render is in progress and none is due (tests).</summary>
    internal Task WhenIdleAsync()
    {
        lock (_lock)
        {
            if (_current is null && _raising == 0)
            {
                return Task.CompletedTask;
            }

            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }

    private long GenerationOf(int pageIndex) =>
        _pageGenerations.TryGetValue(pageIndex, out var generation) ? generation : _generation;

    /// <summary>
    /// Under the lock: marks the render in progress as obsolete when its result could no longer be used,
    /// and returns it so the caller cancels it after releasing the lock.
    /// </summary>
    private Job? TakeObsoleteCurrent()
    {
        if (_current is not { IsObsolete: false } job || IsUseful(job))
        {
            return null;
        }

        job.IsObsolete = true;
        return job;
    }

    private bool IsUseful(Job job)
    {
        if (_disposed || !_wanted.TryGetValue(job.PageIndex, out var request) || job.Generation != GenerationOf(job.PageIndex))
        {
            return false;
        }

        return job.Quality == RenderQuality.Preview
            || (job.Width == request.SharpWidth && job.Height == request.SharpHeight);
    }

    /// <summary>Under the lock: picks and claims the next render, or detaches the idle waiter.</summary>
    private Step NextStep()
    {
        if (_current is not null)
        {
            return default;
        }

        (RenderRequest Request, RenderQuality Quality, int Width, int Height)? best = null;
        if (!_disposed)
        {
            var wantedBytes = -1L;
            foreach (var request in _wanted.Values)
            {
                if (NeededRender(request, ref wantedBytes) is not { } needed)
                {
                    continue;
                }

                if (best is not { } b || IsBefore(request, needed.Quality, b.Request, b.Quality))
                {
                    best = (request, needed.Quality, needed.Width, needed.Height);
                }
            }
        }

        if (best is { } chosen)
        {
            var page = chosen.Request.PageIndex;
            _current = new Job(page, chosen.Quality, chosen.Width, chosen.Height, GenerationOf(page));
            return new Step(_current, null);
        }

        return new Step(null, TakeIdleWaiter());
    }

    /// <summary>
    /// Under the lock: detaches the idle waiter when no render is in progress and no event is being
    /// raised inline, so that "idle" also means every event of the finished renders was delivered or posted.
    /// </summary>
    private TaskCompletionSource? TakeIdleWaiter()
    {
        if (_current is not null || _raising > 0)
        {
            return null;
        }

        var idle = _idle;
        _idle = null;
        return idle;
    }

    private static bool IsBefore(RenderRequest a, RenderQuality qa, RenderRequest b, RenderQuality qb)
    {
        if (a.Priority != b.Priority)
        {
            return a.Priority < b.Priority;
        }

        if (qa != qb)
        {
            return qa == RenderQuality.Preview;
        }

        return a.PageIndex < b.PageIndex;
    }

    private (RenderQuality Quality, int Width, int Height)? NeededRender(RenderRequest request, ref long wantedBytes)
    {
        if (_failures.TryGetValue(request.PageIndex, out var failure) && !failure.RetryArmed)
        {
            return null;
        }

        _cache.TryGetValue(request.PageIndex, out var entry);
        if (entry is null && request.PreviewWidth > 0 && request.PreviewHeight > 0)
        {
            return (RenderQuality.Preview, request.PreviewWidth, request.PreviewHeight);
        }

        if (request.SharpWidth <= 0 || request.SharpHeight <= 0)
        {
            return null;
        }

        var cached = entry?.Bitmap;
        if (cached is not null
            && cached.Quality == RenderQuality.Sharp
            && cached.Generation == GenerationOf(request.PageIndex)
            && cached.PixelWidth == request.SharpWidth
            && cached.PixelHeight == request.SharpHeight)
        {
            return null;
        }

        if (request.Priority > 0)
        {
            if (wantedBytes < 0)
            {
                wantedBytes = 0;
                foreach (var (page, e) in _cache)
                {
                    if (_wanted.ContainsKey(page))
                    {
                        wantedBytes += e.Bytes;
                    }
                }
            }

            var estimate = (long)Math.Ceiling((double)request.SharpWidth * request.SharpHeight * _bytesPerPixel);
            if (wantedBytes - (entry?.Bytes ?? 0) + estimate > _byteBudget)
            {
                return null; // keep the preview: the wanted set alone would not fit
            }
        }

        return (RenderQuality.Sharp, request.SharpWidth, request.SharpHeight);
    }

    /// <summary>Under the lock: evicts unwanted bitmaps, least recently used first, down to the budget.</summary>
    private void Evict()
    {
        while (_cachedBytes > _byteBudget)
        {
            var victim = -1;
            var oldest = long.MaxValue;
            foreach (var (page, entry) in _cache)
            {
                if (entry.LastUsed < oldest && !_wanted.ContainsKey(page))
                {
                    oldest = entry.LastUsed;
                    victim = page;
                }
            }

            if (oldest == long.MaxValue)
            {
                return; // only wanted bitmaps left: never evicted
            }

            Remove(victim);
        }
    }

    private void Remove(int pageIndex)
    {
        if (_cache.Remove(pageIndex, out var entry))
        {
            _cachedBytes -= entry.Bytes;
        }
    }

    /// <summary>Outside the lock: starts the claimed render or releases the idle waiter.</summary>
    private void Run(Step step)
    {
        if (step.Job is { } job)
        {
            _ = Task.Run(() => RenderAsync(job));
        }

        step.Idle?.TrySetResult();
    }

    /// <summary>Never faults because of the renderer: every outcome ends in <see cref="Complete"/>.</summary>
    private async Task RenderAsync(Job job)
    {
        TBitmap? result = default;
        var failed = false;
        try
        {
            result = await _renderer
                .RenderAsync(job.PageIndex, job.Width, job.Height, job.Token)
                .ConfigureAwait(false);
            failed = result is null;
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            // Requested: neither a result nor a failure.
        }
#pragma warning disable CA1031 // A misbehaving renderer must not take the scheduler down (NFR-11).
        catch (Exception)
#pragma warning restore CA1031
        {
            failed = true;
        }

        Complete(job, result, failed);
    }

    private void Complete(Job job, TBitmap? result, bool failed)
    {
        RenderedEventArgs<TBitmap>? args = null;
        Step step;
        lock (_lock)
        {
            if (ReferenceEquals(_current, job))
            {
                _current = null;
            }

            var useful = !job.IsObsolete && !job.Token.IsCancellationRequested && IsUseful(job);
            job.Dispose();

            if (useful && failed)
            {
                RecordFailure(job.PageIndex);
            }
            else if (useful && result is not null)
            {
                args = Store(job, result);
            }

            if (args is not null)
            {
                _raising++;
            }

            step = NextStep();
        }

        if (args is null)
        {
            Run(step);
            return;
        }

        // The event goes out before the next render starts, so events keep the render order.
        try
        {
            Raise(args);
        }
        finally
        {
            TaskCompletionSource? idle;
            lock (_lock)
            {
                _raising--;
                idle = TakeIdleWaiter();
            }

            Run(step);
            idle?.TrySetResult();
        }
    }

    private void RecordFailure(int pageIndex)
    {
        if (_failures.TryGetValue(pageIndex, out var failure))
        {
            failure.Count++;
            failure.RetryArmed = false;
        }
        else
        {
            _failures[pageIndex] = new Failure { Count = 1 };
        }
    }

    private RenderedEventArgs<TBitmap>? Store(Job job, TBitmap result)
    {
        if (job.Quality == RenderQuality.Preview
            && _cache.TryGetValue(job.PageIndex, out var existing)
            && existing.Bitmap.Quality == RenderQuality.Sharp
            && existing.Bitmap.Generation == job.Generation)
        {
            return null; // never downgrade a current sharp bitmap
        }

        var bytes = Math.Max(0, _sizeOf(result));
        var pixels = (long)job.Width * job.Height;
        if (pixels > 0 && bytes > 0)
        {
            _bytesPerPixel = (double)bytes / pixels;
        }

        Remove(job.PageIndex);
        var bitmap = new CachedBitmap<TBitmap>(result, job.Quality, job.Width, job.Height, job.Generation);
        _cache[job.PageIndex] = new CacheEntry(bitmap, bytes) { LastUsed = ++_clock };
        _cachedBytes += bytes;
        _failures.Remove(job.PageIndex);
        Evict();
        return new RenderedEventArgs<TBitmap>(job.PageIndex, bitmap);
    }

    private void Raise(RenderedEventArgs<TBitmap>? args)
    {
        if (args is null)
        {
            return;
        }

        if (_context is null)
        {
            RaiseIfAlive(args);
        }
        else
        {
            _context.Post(static state =>
            {
                var (scheduler, e) = ((RenderScheduler<TBitmap>, RenderedEventArgs<TBitmap>))state!;
                scheduler.RaiseIfAlive(e);
            }, (this, args));
        }
    }

    private void RaiseIfAlive(RenderedEventArgs<TBitmap> args)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
        }

        Rendered?.Invoke(this, args);
    }

    private readonly record struct Step(Job? Job, TaskCompletionSource? Idle);

    private sealed class CacheEntry(CachedBitmap<TBitmap> bitmap, long bytes)
    {
        public CachedBitmap<TBitmap> Bitmap { get; } = bitmap;

        public long Bytes { get; } = bytes;

        public long LastUsed { get; set; }
    }

    private sealed class Failure
    {
        public int Count { get; set; }

        public bool RetryArmed { get; set; }
    }

    /// <summary>
    /// One render. Owns its <see cref="CancellationTokenSource"/>. <see cref="Cancel"/> runs outside the
    /// scheduler lock and may race with <see cref="Dispose"/> (the render completing on another thread, or
    /// inline from a cancellation callback); a small state machine makes sure the source is disposed
    /// exactly once and never while <see cref="CancellationTokenSource.Cancel()"/> is running.
    /// </summary>
    private sealed class Job : IDisposable
    {
        private const int _active = 0;
        private const int _cancelling = 1;
        private const int _disposeRequested = 2;
        private const int _released = 3;

        private readonly CancellationTokenSource _cts = new();
        private int _state;

        public Job(int pageIndex, RenderQuality quality, int width, int height, long generation)
        {
            PageIndex = pageIndex;
            Quality = quality;
            Width = width;
            Height = height;
            Generation = generation;
            Token = _cts.Token;
        }

        public int PageIndex { get; }

        public RenderQuality Quality { get; }

        public int Width { get; }

        public int Height { get; }

        public long Generation { get; }

        public CancellationToken Token { get; }

        /// <summary>Set under the scheduler lock once the result can no longer be used.</summary>
        public bool IsObsolete { get; set; }

        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, _cancelling, _active) != _active)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            finally
            {
                if (Interlocked.CompareExchange(ref _state, _active, _cancelling) != _cancelling)
                {
                    // Dispose was requested while cancelling: finish it here.
                    Volatile.Write(ref _state, _released);
                    _cts.Dispose();
                }
            }
        }

        public void Dispose()
        {
            while (true)
            {
                switch (Volatile.Read(ref _state))
                {
                    case _active:
                        if (Interlocked.CompareExchange(ref _state, _released, _active) == _active)
                        {
                            _cts.Dispose();
                            return;
                        }

                        break;
                    case _cancelling:
                        if (Interlocked.CompareExchange(ref _state, _disposeRequested, _cancelling) == _cancelling)
                        {
                            return;
                        }

                        break;
                    default:
                        return;
                }
            }
        }
    }
}
