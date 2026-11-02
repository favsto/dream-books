using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DreamBooks.App.Ink;
using DreamBooks.App.Services;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Rendering;
using DreamBooks.Pdf;

namespace DreamBooks.App.Views;

/// <summary>
/// Gives the page surfaces of the document view their PDF bitmaps through one <see cref="RenderScheduler{TBitmap}"/>
/// (NFR-03, NFR-05, NFR-06, NFR-07): the pages of the retained window are wanted, visible ones first (priority 0), the
/// others by their distance from the visible pages; pages leaving the window are cancelled; a page with nothing yet gets
/// a small preview first, then its sharp render; bitmaps live in a cache of <see cref="DefaultByteBudget"/> bytes.
/// </summary>
/// <remarks>
/// <para><b>Never blank.</b> A surface shows its placeholder until the first bitmap of its page arrives, then always a
/// bitmap: a new zoom, screen scale or renderer leaves the old one on screen (stale, stretched by the surface) until the
/// new sharp one replaces it. A render that fails leaves what is shown (NFR-11).</para>
/// <para><b>Zoom.</b> The zoom is applied to the renders once it has been still for <see cref="ZoomSettleDelay"/>, in
/// one atomic <see cref="RenderScheduler{TBitmap}.SetWanted"/> with invalidation, so a pinch never starts a render at an
/// intermediate size. A screen-scale change and a renderer swap (after a save) invalidate at once; the latter also
/// retries pages whose renders failed while the file was released.</para>
/// <para><b>Rail preview.</b> <see cref="GetPreviewAsync"/> serves small previews (<see cref="PageBitmapRenderer.PreviewSize"/>)
/// from a cache of <see cref="PreviewCacheBudget"/> bytes, fed by the scheduler's preview renders too.</para>
/// <para><b>Pages tab thumbnails.</b> <see cref="RenderThumbnailAsync"/> renders small bitmaps
/// (<see cref="PageBitmapRenderer.ThumbnailSize"/>) for the Pages tab (FR-103) at the lowest priority: a queue of its own,
/// served one render at a time, newest request first (the rows just scrolled into view), and only while the scheduler
/// has no page render running or due, no zoom is settling and the file is not released by a save (looked at again
/// every <see cref="ThumbnailIdlePoll"/>). PDFium runs one job at a
/// time, so a page render asked meanwhile waits at most for the one small thumbnail in progress. A request that is
/// cancelled (its row recycled) leaves the queue at once; another document empties it.</para>
/// <para><b>Threading.</b> Used on the UI thread; renders start on the thread pool (the scheduler's, or
/// <see cref="Task.Run(Action)"/> for the rail and the thumbnails), and the scheduler's events come back through the
/// dispatcher.</para>
/// </remarks>
internal sealed class PageBitmapCoordinator : IDisposable
{
    /// <summary>Byte budget of the page bitmap cache (keeps bitmaps + ink + model under NFR-06's 600 MB).</summary>
    public const long DefaultByteBudget = 250L * 1024 * 1024;

    /// <summary>Byte budget of the rail preview cache.</summary>
    public const long PreviewCacheBudget = 16L * 1024 * 1024;

    /// <summary>The zoom must stay still this long before the pages are rendered again at the new size (NFR-07).</summary>
    public static readonly TimeSpan ZoomSettleDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>While page renders are due, queued thumbnails look again this often whether they may start.</summary>
    public static readonly TimeSpan ThumbnailIdlePoll = TimeSpan.FromMilliseconds(40);

    private readonly IBitmapRenderer<BitmapSource> _renderer;
    private readonly IDelayScheduler _delays;
    private readonly Func<int, InkSurface?> _surfaceFor;
    private readonly long _byteBudget;
    private readonly Dispatcher _dispatcher;
    private readonly Lock _previewLock = new();
    private readonly Dictionary<int, PreviewEntry> _previews = [];

    private RenderScheduler<BitmapSource>? _scheduler;
    private DocumentLayout? _layout;
    private int _document;
    private (int First, int Last) _retained = (0, -1);
    private IReadOnlyList<int> _visible = [];
    private double _zoom = double.NaN;
    private double _targetZoom = double.NaN;
    private double _dpiScale = 1;
    private IDisposable? _settle;
    private RailRender? _rail;
    private bool _sharpShown;
    private long _previewBytes;
    private long _previewClock;
    private readonly LinkedList<ThumbnailJob> _thumbnails = new();
    private ThumbnailJob? _thumbnailRunning;
    private IDisposable? _thumbnailPoll;
    private bool _disposed;

    /// <param name="renderer">Renders one page; called on thread-pool threads only.</param>
    /// <param name="delays">Debounces the zoom (tests drive it by hand).</param>
    /// <param name="surfaceFor">The surface showing a page now, or null: bitmaps of other pages are ignored.</param>
    /// <param name="byteBudget">Byte budget of the page bitmap cache.</param>
    public PageBitmapCoordinator(
        IBitmapRenderer<BitmapSource> renderer,
        IDelayScheduler delays,
        Func<int, InkSurface?> surfaceFor,
        long byteBudget = DefaultByteBudget)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(delays);
        ArgumentNullException.ThrowIfNull(surfaceFor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        _renderer = renderer;
        _delays = delays;
        _surfaceFor = surfaceFor;
        _byteBudget = byteBudget;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>The scheduler of the current document, or null without one (tests).</summary>
    internal RenderScheduler<BitmapSource>? Scheduler => _scheduler;

    /// <summary>True while a render runs or is due; any thread (the mark scan parks meanwhile).</summary>
    internal bool HasPendingRenders => _scheduler?.HasPendingWork ?? false;

    /// <summary>The document's first sharp bitmap is shown and no render is running or due. UI thread.</summary>
    internal bool FirstRendersDone => _sharpShown && !HasPendingRenders;

    /// <summary>Bytes held by the rail preview cache (tests).</summary>
    internal long PreviewBytes
    {
        get
        {
            lock (_previewLock)
            {
                return _previewBytes;
            }
        }
    }

    /// <summary>
    /// Another document (or none): every render is cancelled and every bitmap dropped. The zoom of the new document is
    /// applied at once by the next <see cref="SetZoom"/>. Nothing happens for the document already shown.
    /// </summary>
    public void SetDocument(DocumentLayout? layout)
    {
        if (_disposed || (ReferenceEquals(layout, _layout) && (layout is null || _scheduler is not null)))
        {
            return;
        }

        ReleaseDocument();
        _layout = layout;
        if (layout is not null)
        {
            _scheduler = new RenderScheduler<BitmapSource>(
                _renderer, SizeOf, _byteBudget, () => new DispatcherSynchronizationContext(_dispatcher));
            _scheduler.Rendered += OnRendered;
        }
    }

    /// <summary>The retained window and the visible pages changed (scroll, resize, jump).</summary>
    public void SetWindow((int First, int Last) retained, IReadOnlyList<int> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);
        _retained = retained;
        _visible = visible.ToArray();
        Push(invalidateAll: false);
    }

    /// <summary>
    /// The view's zoom. The first one of a document is used at once; later ones once the zoom has been still for
    /// <see cref="ZoomSettleDelay"/> (meanwhile the shown bitmaps are stretched).
    /// </summary>
    public void SetZoom(double zoom)
    {
        if (_disposed || !double.IsFinite(zoom) || zoom <= 0)
        {
            return;
        }

        if (double.IsNaN(_zoom) || !_sharpShown)
        {
            // Nothing sharp on screen yet (a new document, or fit width computed at its first layout): nothing to keep
            // stretched, so the zoom applies at once instead of rendering at a provisional one first.
            CancelSettle();
            _zoom = _targetZoom = zoom;
            Push(invalidateAll: false);
            return;
        }

        if (zoom == _targetZoom)
        {
            return; // nothing new (scrolling re-applies the position): a pending settle is not postponed
        }

        _targetZoom = zoom;
        var starting = _settle is null;
        _settle?.Dispose();
        _settle = _delays.Schedule(ZoomSettleDelay, SettleZoom);
        if (starting)
        {
            Push(invalidateAll: false); // previews only from now on: no sharp render at a size about to be obsolete
        }
    }

    /// <summary>The screen scale (device pixels per DIP) changed: every page is rendered again at once.</summary>
    public void SetDpiScale(double dpiScale)
    {
        if (_disposed || !double.IsFinite(dpiScale) || dpiScale <= 0 || dpiScale == _dpiScale)
        {
            return;
        }

        _dpiScale = dpiScale;
        ClearPreviews(); // previews are drawn at the screen's scale
        ThumbnailScaleChanged?.Invoke(this, EventArgs.Empty); // and so are the Pages tab's thumbnails
        InvalidateAll();
    }

    /// <summary>The screen scale changed: thumbnails rendered for the old one should be rendered again. UI thread.</summary>
    public event EventHandler? ThumbnailScaleChanged;

    /// <summary>
    /// Every page is rendered again (the renderer is back after a save): a thumbnail or ink read that returned nothing
    /// while the file was released should be tried again. UI thread.
    /// </summary>
    public event EventHandler? ThumbnailRetryRequested;

    /// <summary>
    /// Renders every page again (the renderer was replaced after a save, or a settled zoom must be applied now); pages
    /// whose renders failed are tried again. What is shown stays until the new bitmaps arrive.
    /// </summary>
    public void InvalidateAll()
    {
        CancelSettle();
        if (!double.IsNaN(_targetZoom))
        {
            _zoom = _targetZoom;
        }

        Push(invalidateAll: true);
        if (!_disposed)
        {
            ThumbnailRetryRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A surface was just bound to a page: it shows the page's cached bitmap at once, if there is one.</summary>
    public void ShowCached(int pageIndex)
    {
        if (_scheduler is { } scheduler
            && scheduler.TryGet(pageIndex, out var cached)
            && cached is not null
            && _surfaceFor(pageIndex) is { } surface)
        {
            surface.PageImage = cached.Bitmap;
        }
    }

    /// <summary>
    /// A small, low-resolution bitmap of a page (<see cref="PageBitmapRenderer.PreviewSize"/>) for the scroll rail's
    /// bubble, cached; null when there is no document, the page does not exist, the render failed, or the request was
    /// cancelled or superseded. Never throws. Call it on the UI thread.
    /// </summary>
    /// <remarks>
    /// At most one rail render is in flight: callers asking for the same page share it, a request for another page
    /// cancels it (the latest wins, so scrubbing never queues PDFium work), and it is cancelled when every caller
    /// waiting for it has cancelled. A cancelled render is never joined again, nor cached.
    /// </remarks>
    public async Task<BitmapSource?> GetPreviewAsync(int pageIndex, CancellationToken ct)
    {
        if (_disposed || _layout is not { } layout || pageIndex < 0 || pageIndex >= layout.PageCount)
        {
            return null;
        }

        lock (_previewLock)
        {
            if (_previews.TryGetValue(pageIndex, out var entry))
            {
                entry.LastUsed = ++_previewClock;
                return entry.Bitmap;
            }
        }

        if (PreviewSizeOf(layout, pageIndex) is not { } size)
        {
            return null;
        }

        if (_rail is not { } rail || rail.PageIndex != pageIndex || rail.Cancellation.IsCancellationRequested)
        {
            _rail?.Cancellation.Cancel(); // the latest request wins
            rail = new RailRender(pageIndex);
            _rail = rail;
            rail.Render = RenderRailPreviewAsync(rail, size);
        }

        rail.Waiters++;
        try
        {
            return await rail.Render.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (--rail.Waiters == 0 && !rail.Render.IsCompleted)
            {
                rail.Cancellation.Cancel(); // nobody waits for it any more
            }
        }
    }

    /// <summary>
    /// A small bitmap of a page (<see cref="PageBitmapRenderer.ThumbnailSize"/>) for the Pages tab (FR-103), rendered at
    /// the lowest priority (see the remarks); not cached here. Null when there is no document, the page does not exist,
    /// the render failed, the request was cancelled or the document changed meanwhile. Never throws. UI thread.
    /// </summary>
    public Task<BitmapSource?> RenderThumbnailAsync(int pageIndex, CancellationToken ct)
    {
        if (_layout is not { } layout
            || pageIndex < 0
            || pageIndex >= layout.PageCount
            || PageSizeOf(layout, pageIndex) is not { } page)
        {
            return Task.FromResult<BitmapSource?>(null);
        }

        var (width, height) = PageBitmapRenderer.ThumbnailSize(page, _dpiScale);
        return RunWhenIdleAsync(token => Task.Run(() => _renderer.RenderAsync(pageIndex, width, height, token), token), ct);
    }

    /// <summary>
    /// Runs other small PDFium work of the Pages tab (a page's stored ink) in the thumbnails' low-priority queue, so it
    /// never goes ahead of a page render either. The result, or null when the work failed, was cancelled, or the document
    /// changed meanwhile. Never throws. UI thread; <paramref name="work"/> is started on the UI thread and must move its
    /// heavy part off it (the page reads go to the PDFium thread).
    /// </summary>
    public async Task<T?> RunWhenIdleAsync<T>(Func<CancellationToken, Task<T?>> work, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_disposed || ct.IsCancellationRequested || _layout is null)
        {
            return null;
        }

        var job = new ThumbnailJob(async token => await work(token).ConfigureAwait(false), _document);
        job.Node = _thumbnails.AddFirst(job); // newest first: the rows just shown
        job.Registration = ct.Register(() =>
        {
            if (_dispatcher.CheckAccess())
            {
                AbandonThumbnail(job);
            }
            else
            {
                _dispatcher.BeginInvoke(() => AbandonThumbnail(job));
            }
        });
        PumpThumbnails();
        return await job.Completion.Task as T;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseDocument();
        _disposed = true;
    }

    /// <summary>Thumbnails queued, plus the one rendering (tests).</summary>
    internal int PendingThumbnailCount => _thumbnails.Count + (_thumbnailRunning is null ? 0 : 1);

    /// <summary>Starts the next thumbnail when none runs and no page render is running or due; otherwise looks again later.</summary>
    private void PumpThumbnails()
    {
        if (_disposed || _thumbnailRunning is not null || _thumbnails.First is not { } next)
        {
            return;
        }

        if (_scheduler is not { } scheduler
            || scheduler.HasPendingWork
            || double.IsNaN(_zoom)
            || _settle is not null
            || _renderer is PageBitmapRenderer { Renderer: null }) // a save has the file released: wait for it
        {
            _thumbnailPoll ??= _delays.Schedule(ThumbnailIdlePoll, () =>
            {
                _thumbnailPoll = null;
                PumpThumbnails();
            });
            return;
        }

        _thumbnails.RemoveFirst();
        var job = next.Value;
        job.Node = null;
        _thumbnailRunning = job;
        _ = RunThumbnailAsync(job);
    }

    /// <summary>One thumbnail job; never faults. Continues on the UI thread, then starts the next.</summary>
    private async Task RunThumbnailAsync(ThumbnailJob job)
    {
        var token = job.Cancellation.Token;
        object? result;
        try
        {
            result = await job.Work(token);
        }
#pragma warning disable CA1031 // A thumbnail that cannot be rendered keeps its placeholder (NFR-11).
        catch (Exception)
#pragma warning restore CA1031
        {
            result = null;
        }

        if (ReferenceEquals(_thumbnailRunning, job))
        {
            _thumbnailRunning = null;
        }

        job.Finish(token.IsCancellationRequested || job.Document != _document || _disposed ? null : result);
        PumpThumbnails();
    }

    /// <summary>The caller gave up (its row was recycled): out of the queue, or its render cancelled. UI thread.</summary>
    private void AbandonThumbnail(ThumbnailJob job)
    {
        if (job.Node is { } node)
        {
            _thumbnails.Remove(node);
            job.Node = null;
        }

        job.Cancellation.Cancel();
        job.Finish(null);
    }

    private void ReleaseThumbnails()
    {
        _thumbnailPoll?.Dispose();
        _thumbnailPoll = null;
        foreach (var job in _thumbnails.ToArray())
        {
            job.Node = null;
            job.Finish(null);
        }

        _thumbnails.Clear();
        if (_thumbnailRunning is { } running)
        {
            running.Cancellation.Cancel(); // it frees its slot when the renderer returns
            running.Finish(null);
        }
    }

    /// <summary>The rail render of <paramref name="rail"/>; never faults. Continues on the UI thread.</summary>
    private async Task<BitmapSource?> RenderRailPreviewAsync(RailRender rail, (int Width, int Height) size)
    {
        var document = _document;
        var dpiScale = _dpiScale;
        var token = rail.Cancellation.Token;
        BitmapSource? bitmap;
        try
        {
            bitmap = await Task.Run(() => _renderer.RenderAsync(rail.PageIndex, size.Width, size.Height, token), token);
        }
#pragma warning disable CA1031 // The rail shows no preview rather than failing (NFR-11).
        catch (Exception)
#pragma warning restore CA1031
        {
            bitmap = null;
        }

        if (ReferenceEquals(_rail, rail))
        {
            _rail = null;
        }

        if (token.IsCancellationRequested)
        {
            return null; // superseded or abandoned: a late result is neither cached nor handed out
        }

        if (bitmap is not null && document == _document && dpiScale == _dpiScale && !_disposed)
        {
            AddPreview(rail.PageIndex, bitmap);
        }

        return bitmap;
    }

    private static long SizeOf(BitmapSource bitmap) =>
        (long)bitmap.PixelWidth * bitmap.PixelHeight * ((bitmap.Format.BitsPerPixel + 7) / 8);

    private static PageSize? PageSizeOf(DocumentLayout layout, int pageIndex)
    {
        var size = VirtualPagePanel.PageSizePt(layout, pageIndex);
        return size.Width > 0 && size.Height > 0 && double.IsFinite(size.Width) && double.IsFinite(size.Height)
            ? new PageSize(size.Width, size.Height)
            : null;
    }

    private (int Width, int Height)? PreviewSizeOf(DocumentLayout layout, int pageIndex) =>
        PageSizeOf(layout, pageIndex) is { } page ? PageBitmapRenderer.PreviewSize(page, _dpiScale) : null;

    private void ReleaseDocument()
    {
        CancelSettle();
        if (_scheduler is { } scheduler)
        {
            scheduler.Rendered -= OnRendered;
            scheduler.Dispose(); // cancels the render in progress; queued events are dropped
            _scheduler = null;
        }

        _rail?.Cancellation.Cancel();
        _rail = null;
        ReleaseThumbnails();
        _sharpShown = false;
        _layout = null;
        _document++;
        _retained = (0, -1);
        _visible = [];
        _zoom = double.NaN;
        _targetZoom = double.NaN;
        ClearPreviews();
    }

    private void CancelSettle()
    {
        _settle?.Dispose();
        _settle = null;
    }

    private void SettleZoom()
    {
        _settle = null;
        if (_targetZoom != _zoom)
        {
            _zoom = _targetZoom;
            Push(invalidateAll: true); // new sizes and the invalidation together: no render at the old size
        }
    }

    /// <summary>Hands the scheduler the pages wanted now, at the applied zoom and screen scale.</summary>
    private void Push(bool invalidateAll)
    {
        if (_scheduler is not { } scheduler)
        {
            return;
        }

        scheduler.SetWanted(BuildRequests(), invalidateAll);
    }

    private List<RenderRequest> BuildRequests()
    {
        var requests = new List<RenderRequest>();
        if (_layout is not { } layout || double.IsNaN(_zoom))
        {
            return requests;
        }

        var first = Math.Max(0, _retained.First);
        var last = Math.Min(layout.PageCount - 1, _retained.Last);
        for (var page = first; page <= last; page++)
        {
            if (PageSizeOf(layout, page) is not { } size)
            {
                continue;
            }

            var preview = PageBitmapRenderer.PreviewSize(size, _dpiScale);
            (int Width, int Height) sharp;
            if (_settle is not null)
            {
                // A zoom is settling: the applied zoom is about to be obsolete. Pages with nothing yet get their
                // preview only; the settle renders them sharp once, at the new size.
                sharp = (0, 0);
            }
            else
            {
                sharp = PageBitmapRenderer.SharpSize(size, _zoom, _dpiScale);
                if (preview.Width >= sharp.Width || preview.Height >= sharp.Height)
                {
                    preview = (0, 0); // no smaller than the sharp render: go straight to it
                }
            }

            requests.Add(new RenderRequest(page, PriorityOf(page), sharp.Width, sharp.Height, preview.Width, preview.Height));
        }

        return requests;
    }

    /// <summary>0 for a visible page, otherwise its distance (in pages) from the nearest visible one.</summary>
    private int PriorityOf(int page)
    {
        if (_visible.Count == 0)
        {
            return 1;
        }

        var distance = int.MaxValue;
        foreach (var visible in _visible)
        {
            distance = Math.Min(distance, Math.Abs(page - visible));
        }

        return distance;
    }

    private void OnRendered(object? sender, RenderedEventArgs<BitmapSource> e)
    {
        if (!ReferenceEquals(sender, _scheduler) || _scheduler is not { } scheduler)
        {
            return; // an event of a document that is gone
        }

        var isPreview = e.Quality == RenderQuality.Preview;
        if (isPreview
            && !scheduler.IsStale(e.PageIndex, e.Bitmap)
            && _layout is { } layout
            && PreviewSizeOf(layout, e.PageIndex) == (e.Bitmap.PixelWidth, e.Bitmap.PixelHeight))
        {
            AddPreview(e.PageIndex, e.Bitmap.Bitmap); // only at the current screen scale (a late event may be older)
        }

        if (_surfaceFor(e.PageIndex) is not { } surface)
        {
            return; // the page is no longer shown
        }

        if (isPreview && surface.PageImage is not null)
        {
            return; // never replace what is shown by a preview (e.g. a sharp bitmap kept across a reload)
        }

        _sharpShown |= !isPreview;

        // The cache holds the newest bitmap of the page (events may be delivered after a newer one was shown).
        surface.PageImage = scheduler.TryGet(e.PageIndex, out var cached) && cached is not null
            ? cached.Bitmap
            : e.Bitmap.Bitmap;
    }

    private void AddPreview(int pageIndex, BitmapSource bitmap)
    {
        var bytes = SizeOf(bitmap);
        lock (_previewLock)
        {
            if (_previews.Remove(pageIndex, out var old))
            {
                _previewBytes -= old.Bytes;
            }

            if (bytes > PreviewCacheBudget)
            {
                return;
            }

            _previews[pageIndex] = new PreviewEntry(bitmap, bytes) { LastUsed = ++_previewClock };
            _previewBytes += bytes;
            while (_previewBytes > PreviewCacheBudget)
            {
                var victim = _previews.MinBy(pair => pair.Value.LastUsed);
                _previews.Remove(victim.Key);
                _previewBytes -= victim.Value.Bytes;
            }
        }
    }

    private void ClearPreviews()
    {
        lock (_previewLock)
        {
            _previews.Clear();
            _previewBytes = 0;
        }
    }

    /// <summary>
    /// The rail render in flight, shared by the callers waiting for its page. Touched on the UI thread only. Its
    /// cancellation source is not disposed: it has no timer, and a late cancel must never throw.
    /// </summary>
    private sealed class RailRender(int pageIndex)
    {
        public int PageIndex { get; } = pageIndex;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<BitmapSource?> Render { get; set; } = Task.FromResult<BitmapSource?>(null);

        public int Waiters { get; set; }
    }

    /// <summary>
    /// A low-priority request (a thumbnail render, or a page's stored ink): queued, then running. Touched on the UI
    /// thread only. Its cancellation source is not disposed (no timer; a late cancel must never throw).
    /// </summary>
    private sealed class ThumbnailJob(Func<CancellationToken, Task<object?>> work, int document)
    {
        public Func<CancellationToken, Task<object?>> Work { get; } = work;

        public int Document { get; } = document;

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<ThumbnailJob>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }

        public void Finish(object? result)
        {
            Registration.Unregister();
            Completion.TrySetResult(result);
        }
    }

    private sealed class PreviewEntry(BitmapSource bitmap, long bytes)
    {
        public BitmapSource Bitmap { get; } = bitmap;

        public long Bytes { get; } = bytes;

        public long LastUsed { get; set; }
    }
}
