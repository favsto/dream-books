using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Rendering;
using DreamBooks.Pdf;

namespace DreamBooks.App.Views;

/// <summary>
/// The whole document as one continuous vertical scroll (FR-05) with one ink surface per page of the retained window
/// (visible ± 2, NFR-05), recycled as the window moves. The view model (<see cref="DocumentViewportViewModel"/>) owns
/// the position: scrolling reports offsets to it, and every position it publishes (zoom anchored through
/// <see cref="DocumentLayout.ZoomAt"/>, fit width, jumps, restore) is applied to the scroll viewer, so nothing jumps
/// under the fingers (FR-06, FR-07, FR-04). Every visible page with loaded strokes inks at once; pen-down never changes
/// the current page; an eraser gesture belongs to the page where it starts; a stroke is clipped to its page (FR-26,
/// FR-27). Fingers pan and zoom, never while a surface is inking; the pen never scrolls (FR-30). A flicked pan glides
/// on and slows down (<see cref="StartGlide"/>, <see cref="FlingSettings"/>) until it stops, reaches an end, or any
/// touch, pen, key, wheel, jump or zoom stops it (FR-05). The scroll bars are
/// replaced by the fading <see cref="ScrollRail"/> (UI-07); the navigation keys go through the view model's commands
/// (<see cref="HandleNavigationKey"/>, FR-10). A finger swipe from the left edge raises <see cref="LeftEdgeSwiped"/>
/// (the window opens the left-edge panel, UI-04); the pen there inks.
/// </summary>
/// <remarks>
/// A page entering the window gets a surface at once (white placeholder of the exact size, light fade-in, NFR-03),
/// its strokes after a short debounce (<see cref="DocumentViewportViewModel.CurrentPageDelay"/>, so a fast fling
/// loads nothing it passes) and its bitmap from <see cref="PageBitmapCoordinator"/> (a preview first, then the sharp
/// render; visible pages first). While a save holds the PDFium thread, loads and renders simply wait: placeholders stay
/// and pages already loaded keep inking; when the renderer is replaced every bitmap is rendered again (failed ones
/// included) and failed loads retried.
/// </remarks>
public partial class DocumentView : UserControl
{
    /// <summary>Two taps of one finger within this time and <see cref="DoubleTapDistanceDip"/> are a double tap (FR-07).</summary>
    internal const int DoubleTapTimeMs = 300;

    internal const double DoubleTapDistanceDip = 24;

    /// <summary>A finger move from the left edge zone longer than this, and mostly horizontal, is a swipe (UI-04).</summary>
    internal const double EdgeSwipeDistanceDip = 32;

    private const double _wheelStepFactor = 1.1;
    private const double _wheelNotch = 120;
    private const double _minScaleDelta = 1e-4;
    private const double _sameOffsetDip = 0.5;
    private const double _maxPanRemainderDip = 0.5;

    /// <summary>One wheel line in DIPs, as the scroll viewer's own wheel scrolling.</summary>
    private const double _wheelLineDip = 16;
    private const int _defaultWheelLines = 3;

    /// <summary>How close to an end of the document (DIPs) a glide counts as having reached it.</summary>
    private const double _glideEdgeDip = 1e-3;

    /// <summary>A manipulation step shorter than this (DIPs) is a resting finger's jitter, not a move.</summary>
    private const double _restMoveDip = 0.5;

    /// <summary>WPF's own inertia gets zero velocity; it still wants a deceleration (any, it never moves anything).</summary>
    private const double _wpfInertiaDeceleration = 1;

    private readonly Dictionary<int, PageSlot> _slots = [];
    private readonly HashSet<int> _touches = [];
    private readonly DispatcherTimer _editorTimer;
    private readonly PageBitmapRenderer _pdfBitmaps = new();
    private PageBitmapCoordinator _bitmaps;
    private IBitmapRenderer<BitmapSource> _bitmapRenderer;
    private MainViewModel? _viewModel;
    private PageSurfacePool _pool;
    private (double X, double Y)? _pendingOffsets;
    private bool _settleQueued;
    private bool _updatingSurfaces;
    private PdfRenderer? _requestedFrom;
    private Vector _panRemainder;
    private (Point Position, int Time)? _tapStart;
    private (Point Position, int Time)? _lastTap;
    private int? _railTouchId;
    private StylusDevice? _railStylus;
    private TouchDevice? _edgeTouch;
    private Point? _edgeSwipeStart;
    private readonly FlingGlide _glide = new();
    private FlingSettings? _fling;
    private bool _framesHooked;
    private bool _gestureZoomed;
    private int? _lastMoveTimestamp;

    public DocumentView()
    {
        InitializeComponent();
        _pool = NewPool(null);
        _bitmapRenderer = _pdfBitmaps;
        _bitmaps = NewBitmapCoordinator();
        Rail.PreviewSource = (pageIndex, ct) => _bitmaps.GetPreviewAsync(pageIndex, ct); // the current coordinator
        Rail.CanScroll = () => !AnyInking();
        _editorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = DocumentViewportViewModel.CurrentPageDelay };
        _editorTimer.Tick += (_, _) => FlushEditorLoads();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        Loaded += (_, _) =>
        {
            SyncBitmaps();
            ScheduleEditorLoads();
        };
        Unloaded += (_, _) =>
        {
            StopGlide();
            _editorTimer.Stop();

            // Renders stop and the cache (up to its budget) is released; the surfaces keep what they show. A view that
            // is loaded again starts a new coordinator (Loaded), whose previews never replace those bitmaps.
            _bitmaps.Dispose();
            _bitmaps = NewBitmapCoordinator();
        };
    }

    /// <summary>
    /// What draws the page bitmaps: by default the current <see cref="MainViewModel.Renderer"/> through
    /// <see cref="PageBitmapRenderer"/>. Tests inject a fake; setting it starts over with a new scheduler.
    /// </summary>
    internal IBitmapRenderer<BitmapSource> BitmapRenderer
    {
        get => _bitmapRenderer;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _bitmapRenderer = value;
            _bitmaps.Dispose();
            _bitmaps = NewBitmapCoordinator();
            SyncBitmaps();
        }
    }

    /// <summary>The page bitmaps: scheduler, cache and the rail's previews (<see cref="PageBitmapCoordinator.GetPreviewAsync"/>).</summary>
    internal PageBitmapCoordinator Bitmaps => _bitmaps;

    /// <summary>The surfaces in use, by page index (the retained window, plus a page kept while it is inking).</summary>
    internal IReadOnlyDictionary<int, InkSurface> ActiveSurfaces =>
        _slots.ToDictionary(pair => pair.Key, pair => pair.Value.Surface);

    internal PageSurfacePool Pool => _pool;

    /// <summary>The debounce of stroke loads for pages entering the window (tests lengthen it to see immediate loads).</summary>
    internal TimeSpan EditorLoadDelay
    {
        get => _editorTimer.Interval;
        set => _editorTimer.Interval = value;
    }

    /// <summary>Gives the keyboard to the document (its scroll viewer), e.g. after Esc in the search field.</summary>
    internal void FocusDocument() => Viewport.Focus();

    /// <summary>The surface showing a page, or null when the page is outside the window.</summary>
    internal InkSurface? SurfaceFor(int pageIndex) => _slots.TryGetValue(pageIndex, out var slot) ? slot.Surface : null;

    /// <summary>
    /// Renders every page of the window again at the current zoom (a pending zoom settle applies now); what is shown
    /// stays, stretched, until the new bitmaps arrive. Called when the renderer was replaced; tests use it to settle.
    /// </summary>
    internal void InvalidateBitmaps() => _bitmaps.InvalidateAll();

    /// <summary>Starts the stroke loads of the window's pages that have no editor yet (what the debounce does).</summary>
    internal void FlushEditorLoads()
    {
        _editorTimer.Stop();
        foreach (var slot in _slots.Values.ToArray())
        {
            if (slot.Surface.Editor is null && !slot.LoadingEditor)
            {
                _ = LoadEditorAsync(slot);
            }
        }
    }

    /// <summary>
    /// A one-finger tap at a viewport point (DIPs). The second of two taps within <see cref="DoubleTapTimeMs"/> and
    /// <see cref="DoubleTapDistanceDip"/> toggles fit width and 200% around it (FR-07). Separate so tests can drive it.
    /// </summary>
    internal void RegisterTap(Point position, int timestamp)
    {
        if (_lastTap is { } last
            && unchecked(timestamp - last.Time) is >= 0 and <= DoubleTapTimeMs
            && (position - last.Position).Length <= DoubleTapDistanceDip)
        {
            _lastTap = null;
            if (_viewModel is { } vm && !AnyInking())
            {
                vm.Viewport.ToggleFitWidth(position.Y);
                Rail.NotifyActivity(); // the indicator shows the new zoom (UI-05)
            }

            return;
        }

        _lastTap = (position, timestamp);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyPosition(); // the pages snap to the new pixel grid
        _bitmaps.SetDpiScale(newDpi.DpiScaleX); // same DIPs, more or fewer pixels: rendered again at once
    }

    private double DeviceScale() => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private PageBitmapCoordinator NewBitmapCoordinator() =>
        new(_bitmapRenderer, new DispatcherDelayScheduler(), SurfaceFor);

    /// <summary>
    /// Brings the bitmaps in line with the view model: renderer, document, screen scale, zoom (at once for a new
    /// document) and the window. Each step does nothing when unchanged.
    /// </summary>
    private void SyncBitmaps()
    {
        var vm = _viewModel;
        _pdfBitmaps.Renderer = vm?.Renderer;
        _bitmaps.SetDocument(vm?.Viewport.Layout);
        if (vm?.Viewport is not { Layout: not null } viewport)
        {
            return;
        }

        _bitmaps.SetDpiScale(DeviceScale());
        _bitmaps.SetZoom(viewport.Position.Zoom);
        _bitmaps.SetWindow(viewport.RetainedRange, viewport.VisiblePages);
    }

    private void UpdateBitmapWindow()
    {
        if (_viewModel?.Viewport is { Layout: not null } viewport)
        {
            _bitmaps.SetWindow(viewport.RetainedRange, viewport.VisiblePages);
        }
    }

    private PageSurfacePool NewPool(MainViewModel? viewModel) => new(() => CreateSurface(viewModel), DiscardSurface);

    private InkSurface CreateSurface(MainViewModel? viewModel)
    {
        var surface = new InkSurface { ViewModel = viewModel };
        AutomationProperties.SetName(surface, Strings.Page); // numbered when bound to a page
        if (viewModel is not null)
        {
            surface.SetBinding(InkSurface.CurveProperty, new Binding("Tools.Curve") { Source = viewModel });
            surface.SetBinding(InkSurface.SmoothingProperty, new Binding("Tools.Smoothing") { Source = viewModel });
        }

        surface.InkingEnded += OnSurfaceInkingEnded;
        surface.EditorWanted += OnSurfaceEditorWanted;
        return surface;
    }

    private void DiscardSurface(InkSurface surface)
    {
        surface.InkingEnded -= OnSurfaceInkingEnded;
        surface.EditorWanted -= OnSurfaceEditorWanted;
        BindingOperations.ClearAllBindings(surface);
        surface.ViewModel = null; // unsubscribes from the view model: nothing keeps a discarded surface alive
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is { } old)
        {
            old.Viewport.PositionChanged -= OnPositionChanged;
            old.Viewport.LayoutChanged -= OnLayoutChanged;
            old.Viewport.Jumped -= OnJumped;
            old.ZoomVm.StepZoomed -= OnStepZoomed;
            old.ZoomVm.PropertyChanged -= OnZoomPropertyChanged;
            old.Viewport.PropertyChanged -= OnViewportPropertyChanged;
            old.PropertyChanged -= OnViewModelPropertyChanged;
            old.Search.PageHighlightsChanged -= OnSearchHighlightsChanged;
            old.FirstRendersDone = null;
            old.RendersPending = null;
        }

        StopGlide();
        ReleaseAll();
        _pool.Clear();
        _viewModel = viewModel;
        Rail.Model = viewModel?.Viewport;
        Rail.Marks = viewModel?.Marks;
        _pool = NewPool(viewModel);
        _pendingOffsets = null;
        _requestedFrom = viewModel?.Renderer;
        _bitmaps.SetDocument(null); // whatever the new view model shows starts from scratch
        SyncBitmaps();
        if (viewModel is not null)
        {
            viewModel.Viewport.PositionChanged += OnPositionChanged;
            viewModel.Viewport.LayoutChanged += OnLayoutChanged;
            viewModel.Viewport.Jumped += OnJumped;
            viewModel.ZoomVm.StepZoomed += OnStepZoomed;
            viewModel.ZoomVm.PropertyChanged += OnZoomPropertyChanged;
            viewModel.Viewport.PropertyChanged += OnViewportPropertyChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.Search.PageHighlightsChanged += OnSearchHighlightsChanged;

            // The mark scan waits for the first renders and parks while renders are due (the current coordinator).
            viewModel.FirstRendersDone = () => _bitmaps.FirstRendersDone;
            viewModel.RendersPending = () => _bitmaps.HasPendingRenders;
            ReportViewportSize();
        }

        ApplyPosition();
        UpdateSurfaces();
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        // Another document (or none): every surface goes, a live gesture on one is dropped, every render is cancelled.
        StopGlide();
        ReleaseAll();
        _pendingOffsets = null;
        _requestedFrom = _viewModel?.Renderer;
        SyncBitmaps();
        ApplyPosition();
        UpdateSurfaces();
    }

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        ApplyPosition();
        UpdateSurfaces();
    }

    private void OnViewportPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewportViewModel.RetainedRange))
        {
            UpdateSurfaces();
        }
        else if (e.PropertyName == nameof(DocumentViewportViewModel.VisiblePages))
        {
            UpdateBitmapWindow(); // the visible pages go first
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A save swapped the renderer onto the saved file: same content, so the bitmaps stay until new ones arrive;
        // renders and stroke loads that failed while the file was released are tried again (after repeated failures the
        // scheduler waits for exactly this invalidation).
        // At open the renderer is announced after the layout, whose pages already asked it: not twice.
        if (e.PropertyName != nameof(MainViewModel.Renderer))
        {
            return;
        }

        _pdfBitmaps.Renderer = _viewModel?.Renderer;
        if (_viewModel?.Renderer is not { } renderer)
        {
            _requestedFrom = null; // released by a save: whatever comes back is asked again
        }
        else if (!ReferenceEquals(renderer, _requestedFrom))
        {
            _requestedFrom = renderer;
            _bitmaps.InvalidateAll();
            ScheduleEditorLoads();
        }
    }

    /// <summary>
    /// The pen came near a page whose strokes are not loaded: load them now, not after the debounce (I1). A stroke
    /// started before they arrive does not ink (no wet ink, nothing committed): queueing it would keep the page
    /// "inking" (no scroll, no recycling) for as long as a load waits behind a save, and a failed load would make visible
    /// ink vanish. The pen usually hovers long enough for the load to finish before it touches.
    /// </summary>
    private void OnSurfaceEditorWanted(object? sender, EventArgs e)
    {
        if (sender is InkSurface surface
            && _slots.Values.FirstOrDefault(s => ReferenceEquals(s.Surface, surface)) is { } slot)
        {
            LoadEditorNow(slot);
        }
    }

    /// <summary>After a jump the visible pages were never neighbours: their strokes load at once (the others wait).</summary>
    private void OnJumped(object? sender, EventArgs e)
    {
        StopGlide(); // go to page, Back, Home/End, Page Up/Down, a mark, a rail scrub: the glide is over
        if (_viewModel?.Viewport is not { } viewport)
        {
            return;
        }

        foreach (var page in viewport.VisiblePages)
        {
            if (_slots.TryGetValue(page, out var slot))
            {
                LoadEditorNow(slot);
            }
        }
    }

    /// <summary>Ctrl + / Ctrl − (or the panel's buttons): the user's zoom, the indicator shows (UI-05).</summary>
    private void OnStepZoomed(object? sender, EventArgs e)
    {
        StopGlide();
        Rail.NotifyActivity();
    }

    /// <summary>The zoom lock engages (a stroke or an erase starts): a glide stops at once.</summary>
    private void OnZoomPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ZoomViewModel.IsLocked) && sender is ZoomViewModel { IsLocked: true })
        {
            StopGlide();
        }
    }

    private void LoadEditorNow(PageSlot slot)
    {
        if (slot.Surface.Editor is null && !slot.LoadingEditor)
        {
            _ = LoadEditorAsync(slot);
        }
    }

    private void OnSurfaceInkingEnded(object? sender, EventArgs e)
    {
        // A surface kept for its live stroke may leave now. Deferred to ContextIdle (after the render that drops the
        // dynamic renderer's wet visual), so the old stroke never flashes on the page the surface is rebound to.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, UpdateSurfaces);
    }

    /// <summary>
    /// Applies the view model's position: panel extent and zoom, every surface's scale, then the scroll offsets
    /// (<see cref="DocumentLayout.ToOffsets"/>). The scroll changes this causes are not reported back.
    /// </summary>
    private void ApplyPosition()
    {
        if (_viewModel?.Viewport is not { Layout: { } layout } viewport)
        {
            Pages.Configure(null, 1, 0);
            return;
        }

        var position = viewport.Position;
        Pages.Configure(layout, position.Zoom, viewport.ViewportWidthDip, DeviceScale());
        var scale = Pages.Scale;
        foreach (var slot in _slots.Values)
        {
            slot.Surface.Scale = scale;
        }

        // Ink is vector and rescales at once; the bitmaps stay stretched until the zoom settles.
        _bitmaps.SetZoom(position.Zoom);

        if (viewport.ViewportWidthDip <= 0 || viewport.ViewportHeightDip <= 0)
        {
            return; // not laid out yet: the first layout reports the size, which applies the position
        }

        var (x, y) = layout.ToOffsets(position, viewport.ViewportWidthDip, viewport.ViewportHeightDip);
        if (_pendingOffsets is null
            && Math.Abs(Viewport.HorizontalOffset - x) < _sameOffsetDip
            && Math.Abs(Viewport.VerticalOffset - y) < _sameOffsetDip)
        {
            return;
        }

        _pendingOffsets = (x, y);
        Viewport.ScrollToHorizontalOffset(x);
        Viewport.ScrollToVerticalOffset(y);
        QueueSettleCheck();
    }

    /// <summary>
    /// After the layout passes that apply a position (they run before Background work), the scroll viewer reports
    /// again: from then on its scroll changes are the user's. The real offsets are handed to the view model, which
    /// ignores them when they match (and adopts the scroll viewer's clamping otherwise).
    /// </summary>
    private void QueueSettleCheck()
    {
        if (_settleQueued)
        {
            return;
        }

        _settleQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _settleQueued = false;
            if (_pendingOffsets is null)
            {
                return;
            }

            Viewport.UpdateLayout();
            _pendingOffsets = null;
            if (_viewModel?.Viewport is { Layout: not null } viewport)
            {
                viewport.SetScroll(Viewport.HorizontalOffset, Viewport.VerticalOffset);
            }
        });
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Every scroll re-snaps the pages to whole device pixels at the new offsets (one more arrange, same frame).
        Pages.ScrollOffset = new Vector(Viewport.HorizontalOffset, Viewport.VerticalOffset);
        if (_viewModel is not { } vm)
        {
            return;
        }

        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
        {
            // Resize (or first layout): the view model refits in fit-width mode and publishes the position to show.
            ReportViewportSize();
            ApplyPosition();
            return;
        }

        if (e.ExtentWidthChange != 0 || e.ExtentHeightChange != 0 || _pendingOffsets is not null)
        {
            return; // the view applying a position: not the user's scroll
        }

        if (e.VerticalChange != 0)
        {
            Rail.NotifyActivity(); // the user scrolled (wheel, arrow keys): the rail shows (UI-07)
        }

        vm.Viewport.SetScroll(Viewport.HorizontalOffset, Viewport.VerticalOffset);
    }

    private void ReportViewportSize()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        vm.Viewport.ViewportWidthDip = Viewport.ViewportWidth;
        vm.Viewport.ViewportHeightDip = Viewport.ViewportHeight;
        Pages.Configure(vm.Viewport.Layout, vm.Viewport.Position.Zoom, vm.Viewport.ViewportWidthDip, DeviceScale());
    }

    /// <summary>
    /// Gives a surface to every page of the retained window and takes it from the pages that left: outgoing surfaces
    /// are rebound to incoming pages first (no tree churn, no new surfaces on a jump), the rest go back to the pool.
    /// A surface that is inking keeps its page until pen-up (<see cref="InkSurface.InkingEnded"/>).
    /// </summary>
    private void UpdateSurfaces()
    {
        if (_updatingSurfaces)
        {
            return;
        }

        _updatingSurfaces = true;
        try
        {
            var (first, last) = _viewModel?.Viewport is { Layout: not null } viewport ? viewport.RetainedRange : (0, -1);
            var free = new Stack<InkSurface>();
            foreach (var slot in _slots.Values.Where(s => s.PageIndex < first || s.PageIndex > last).ToArray())
            {
                if (slot.Surface.IsInking)
                {
                    continue; // deferred: recycled once the stroke or erase ends
                }

                _slots.Remove(slot.PageIndex);
                PageSurfacePool.Reset(slot.Surface);
                free.Push(slot.Surface);
            }

            for (var index = first; index <= last; index++)
            {
                if (_slots.ContainsKey(index))
                {
                    continue;
                }

                if (!free.TryPop(out var surface))
                {
                    surface = _pool.Rent();
                    Pages.Children.Add(surface);
                }

                Bind(surface, index);
            }

            while (free.TryPop(out var unused))
            {
                Pages.Children.Remove(unused);
                _pool.Return(unused);
            }
        }
        finally
        {
            _updatingSurfaces = false;
        }

        UpdateBitmapWindow(); // pages that left are cancelled, new ones queued by distance from the visible ones
    }

    /// <summary>Every surface leaves (another document, another view model); live gestures are dropped.</summary>
    private void ReleaseAll()
    {
        foreach (var slot in _slots.Values.ToArray())
        {
            Pages.Children.Remove(slot.Surface);
            _pool.Return(slot.Surface); // resets it, cancelling a live gesture
        }

        _slots.Clear();
        _editorTimer.Stop();
    }

    private void Bind(InkSurface surface, int pageIndex)
    {
        var viewport = _viewModel!.Viewport;
        var layout = viewport.Layout!;
        var slot = new PageSlot(surface, pageIndex);
        _slots[pageIndex] = slot;

        surface.Editor = null;
        surface.Page = null;
        surface.PageImage = null;
        surface.PageSizePt = VirtualPagePanel.PageSizePt(layout, pageIndex);
        surface.Scale = Pages.Scale;
        ShowSearchHighlights(surface, pageIndex); // this page's own results: a recycled surface keeps none of its last page
        VirtualPagePanel.SetPageIndex(surface, pageIndex);
        AutomationProperties.SetName(surface, string.Format(CultureInfo.CurrentCulture, Strings.PageNameFormat, pageIndex + 1));

        if (_viewModel.Session is { } session && session.TryGetEditor(pageIndex, out var editor) && editor is not null)
        {
            surface.Editor = editor; // already loaded: inkable at once
        }
        else
        {
            ScheduleEditorLoads();
        }

        _bitmaps.ShowCached(pageIndex); // a page seen recently shows its bitmap at once, the others their placeholder
        FadeIn(surface, placeholder: surface.PageImage is null); // only white paper fades in; a bitmap appears at once
    }

    /// <summary>
    /// The search results of a page changed (-1: of every page): only the surfaces in the window draw them, each its own
    /// page's (FR-105). Pages outside the window get theirs when a surface is bound to them.
    /// </summary>
    private void OnSearchHighlightsChanged(object? sender, int pageIndex)
    {
        foreach (var slot in _slots.Values)
        {
            if (pageIndex < 0 || slot.PageIndex == pageIndex)
            {
                ShowSearchHighlights(slot.Surface, slot.PageIndex);
            }
        }
    }

    private void ShowSearchHighlights(InkSurface surface, int pageIndex)
    {
        if (_viewModel?.Search is { } search)
        {
            surface.SetSearchHighlights(search.HitsForPage(pageIndex), search.CurrentRectsForPage(pageIndex));
        }
        else
        {
            surface.SetSearchHighlights([], []);
        }
    }

    /// <summary>
    /// A very light fade-in of the white placeholder page (NFR-03); a page shown with a bitmap (cached or stale) appears at
    /// once. Instant when Windows animations are off (NFR-32).
    /// </summary>
    private static void FadeIn(InkSurface surface, bool placeholder)
    {
        if (!placeholder
            || !SystemParameters.ClientAreaAnimation
            || surface.TryFindResource("PageFadeInDuration") is not Duration duration
            || surface.TryFindResource("PageFadeInFromOpacity") is not double from)
        {
            surface.BeginAnimation(OpacityProperty, null);
            surface.Opacity = 1;
            return;
        }

        surface.BeginAnimation(OpacityProperty, new DoubleAnimation(from, 1, duration) { FillBehavior = FillBehavior.Stop });
    }

    private void ScheduleEditorLoads()
    {
        _editorTimer.Stop();
        _editorTimer.Start();
    }

    /// <summary>
    /// Loads a page's strokes for its surface. Until then the surface is a placeholder that accepts no ink; on failure
    /// (e.g. the file is released by a save) it stays so and the load is tried again when the renderer comes back.
    /// The load is not cancelled when the page leaves the window: the session keeps it (and a later visit needs it),
    /// and a result arriving after the surface was rebound to another page is dropped (slot identity).
    /// </summary>
    private async Task LoadEditorAsync(PageSlot slot)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        slot.LoadingEditor = true;
        try
        {
            var editor = await vm.EnsureEditorAsync(slot.PageIndex);
            if (IsBound(slot))
            {
                slot.Surface.Editor = editor;
            }
        }
#pragma warning disable CA1031 // A page whose strokes cannot be loaded shows its placeholder; the view model reports errors.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            slot.LoadingEditor = false;
        }
    }

    private bool IsBound(PageSlot slot) => _slots.TryGetValue(slot.PageIndex, out var current) && ReferenceEquals(current, slot);

    private bool AnyInking() => _slots.Values.Any(s => s.Surface.IsInking);

    private void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        StopGlide(); // a finger came down: like on every phone, the glide stops under it
        _gestureZoomed = false;
        _lastMoveTimestamp = null;
        e.ManipulationContainer = Viewport;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        e.Handled = true;
    }

    private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        e.Handled = true;
        if (e.IsInertial)
        {
            e.Complete(); // WPF's own inertia is never used: the glide runs per frame (OnManipulationInertiaStarting)
            return;
        }

        var delta = e.DeltaManipulation;
        var scale = (delta.Scale.X + delta.Scale.Y) / 2;
        _gestureZoomed |= Math.Abs(scale - 1) > _minScaleDelta;
        if (delta.Translation.Length >= _restMoveDip)
        {
            _lastMoveTimestamp = e.Timestamp; // jitter of a resting finger does not count as moving
        }

        ApplyManipulation(delta.Translation, scale, e.ManipulationOrigin);
    }

    /// <summary>
    /// The fingers lifted: the pan glides on from the release velocity (<see cref="StartGlide"/>), unless the finger
    /// rested before lifting (the time since its last real move, from the input timestamps); a pinch ends where it is
    /// (no zoom inertia, no pan glide). WPF's own inertia is given nothing to do: it advances on a dispatcher timer out
    /// of step with the screen's frames (judder at reading speeds), so the glide is integrated once per rendered frame.
    /// </summary>
    private void OnManipulationInertiaStarting(object? sender, ManipulationInertiaStartingEventArgs e)
    {
        e.Handled = true;
        var release = e.InitialVelocities.LinearVelocity;
        e.TranslationBehavior.InitialVelocity = default;
        e.TranslationBehavior.DesiredDeceleration = _wpfInertiaDeceleration;
        e.ExpansionBehavior.InitialVelocity = default;
        e.ExpansionBehavior.DesiredDeceleration = _wpfInertiaDeceleration;

        // Never moved: a tap, no fling. A clock that looks wrong (negative) is ignored: the release velocity is used.
        var rested = _lastMoveTimestamp is { } moved
            ? TimeSpan.FromMilliseconds(Math.Max(0, unchecked(e.Timestamp - moved)))
            : TimeSpan.MaxValue;
        StartGlide(release, afterPinch: _gestureZoomed, restedFor: rested);
    }

    /// <summary>The fling settings from the theme dictionary (<see cref="FlingSettings.From"/>).</summary>
    internal FlingSettings Fling => _fling ??= FlingSettings.From(key => TryFindResource(key));

    /// <summary>True while a fling glides on after the finger lifted.</summary>
    internal bool IsGliding => _glide.IsActive;

    /// <summary>
    /// Starts a fling from the finger's velocity at the lift (DIP/ms, the direction the content moves) at the
    /// <see cref="Fling"/> launch speed (capped). Nothing after a pinch, for a lift slower than the minimum, after the
    /// finger rested longer than <see cref="FlingSettings.StaleRelease"/> (<paramref name="restedFor"/>), without a
    /// document, while the zoom is locked or a page is inking. Rendered frames advance it (<see cref="AdvanceGlide"/>)
    /// while the view is on screen. True when a glide started.
    /// </summary>
    internal bool StartGlide(Vector releaseVelocity, bool afterPinch = false, TimeSpan restedFor = default)
    {
        StopGlide();
        if (afterPinch || _viewModel is not { Viewport.Layout: not null } vm || vm.ZoomVm.IsLocked || AnyInking())
        {
            return false;
        }

        var launch = Fling.LaunchVelocity(releaseVelocity, restedFor);
        if (launch == default)
        {
            return false;
        }

        _glide.Start(launch, Fling.DecayTimeConstantMs, Fling.MinSpeed);
        HookFrames(true);
        return true;
    }

    /// <summary>
    /// One rendered frame of the glide at <paramref name="frameTime"/>: the step goes through
    /// <see cref="ApplyManipulation"/> like a finger's (sub-DIP remainder kept, the rail shows). It stops when the
    /// document reaches its top or bottom (exactly there: the scroll is clamped, no bounce), when it has slowed to a
    /// stop, and at once while a page is inking, the zoom is locked or without a document. Separate so tests can
    /// drive it.
    /// </summary>
    internal void AdvanceGlide(TimeSpan frameTime)
    {
        if (!_glide.IsActive
            || _viewModel is not { Viewport: { Layout: { } layout } viewport } vm
            || vm.ZoomVm.IsLocked
            || AnyInking())
        {
            StopGlide();
            return;
        }

        var step = _glide.Step(frameTime);
        if (step.X != 0 || step.Y != 0)
        {
            ApplyManipulation(step, 1, default);
            if (ReachedEnd(layout, viewport, step))
            {
                StopGlide();
                return;
            }
        }

        if (!_glide.IsActive)
        {
            StopGlide();
        }
    }

    /// <summary>Stops a glide where it is (a new touch, the pen, a key, the wheel, a jump, a zoom, another document).</summary>
    internal void StopGlide()
    {
        _glide.Stop();
        HookFrames(false);
    }

    /// <summary>The pen came into range: a glide stops before it touches.</summary>
    internal void NotifyPenNear() => StopGlide();

    /// <summary>
    /// The glide reached the top or the bottom in the direction it moves (or, for a purely sideways glide, the side):
    /// there is nothing more to scroll.
    /// </summary>
    private static bool ReachedEnd(DocumentLayout layout, DocumentViewportViewModel viewport, Vector step)
    {
        var width = viewport.ViewportWidthDip;
        var height = viewport.ViewportHeightDip;
        var zoom = viewport.Position.Zoom;
        var (x, y) = layout.ToOffsets(viewport.Position, width, height);
        var maxY = Math.Max(0, layout.ExtentHeightDip(zoom) - height);
        var maxX = Math.Max(0, layout.ExtentWidthDip(zoom, width) - width);
        var blockedY = step.Y > 0 ? y <= _glideEdgeDip : step.Y < 0 && y >= maxY - _glideEdgeDip;
        var blockedX = step.X > 0 ? x <= _glideEdgeDip : step.X < 0 && x >= maxX - _glideEdgeDip;
        return blockedY || (step.Y == 0 && blockedX);
    }

    /// <summary>Rendered frames drive the glide only while the view is on screen (tests drive it themselves).</summary>
    private void HookFrames(bool on)
    {
        if (on && !_framesHooked && PresentationSource.FromVisual(this) is not null)
        {
            CompositionTarget.Rendering += OnRendering;
            _framesHooked = true;
        }
        else if (!on && _framesHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _framesHooked = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            AdvanceGlide(frame.RenderingTime);
        }
    }

    /// <summary>
    /// One finger-manipulation step: the pan first (through the view model, keeping the sub-threshold remainder so
    /// slow pans are not lost; the overshoot past an edge is not banked), then the pinch around
    /// <paramref name="origin"/> (viewport DIPs). Nothing while a surface is inking. Separate so tests can drive it.
    /// </summary>
    internal void ApplyManipulation(Vector translation, double scale, Point origin)
    {
        if (_viewModel?.Viewport is not { Layout: { } layout } viewport || AnyInking())
        {
            return; // Never move a page under a stroke being written.
        }

        if (translation.X != 0 || translation.Y != 0)
        {
            var (x, y) = layout.ToOffsets(viewport.Position, viewport.ViewportWidthDip, viewport.ViewportHeightDip);
            var targetX = x - translation.X + _panRemainder.X;
            var targetY = y - translation.Y + _panRemainder.Y;
            viewport.SetScroll(targetX, targetY);
            var (newX, newY) = layout.ToOffsets(viewport.Position, viewport.ViewportWidthDip, viewport.ViewportHeightDip);
            _panRemainder = new Vector(
                Math.Clamp(targetX - newX, -_maxPanRemainderDip, _maxPanRemainderDip),
                Math.Clamp(targetY - newY, -_maxPanRemainderDip, _maxPanRemainderDip));
            if (translation.Y != 0)
            {
                Rail.NotifyActivity(); // a finger pan is the user's scrolling: the rail shows
            }
        }

        if (Math.Abs(scale - 1) > _minScaleDelta && double.IsFinite(scale) && scale > 0)
        {
            viewport.ZoomAt(scale, origin);
            Rail.NotifyActivity(); // a pinch is the user's zoom: the indicator shows (UI-05)
        }
    }

    private void OnManipulationBoundaryFeedback(object? sender, ManipulationBoundaryFeedbackEventArgs e) =>
        e.Handled = true; // No window "bounce" at the scroll limits.

    /// <summary>
    /// PgUp, PgDn, Home and End move the document through the view model's commands (FR-10; Home and End are jumps
    /// recorded for Back). Not with modifiers, and not while the keys belong to a field or a slider
    /// (<paramref name="source"/> is where the key event comes from). The arrow keys are left to the scroll viewer, but
    /// taken (and do nothing) while a page is inking. True when the key was used.
    /// </summary>
    internal bool HandleNavigationKey(Key key, ModifierKeys modifiers, object? source)
    {
        if (modifiers != ModifierKeys.None || source is TextBoxBase or PasswordBox or RangeBase
            || _viewModel?.Viewport is not { Layout: not null } viewport)
        {
            return false;
        }

        if (key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down or Key.Left or Key.Right)
        {
            StopGlide(); // the keyboard takes over
        }

        var command = key switch
        {
            Key.PageUp => viewport.PageUpCommand,
            Key.PageDown => viewport.PageDownCommand,
            Key.Home => viewport.HomeCommand,
            Key.End => viewport.EndCommand,
            _ => null,
        };
        if (command is null)
        {
            // The arrow keys scroll in the scroll viewer itself, except while a page is inking: then they are taken
            // and nothing moves under the pen.
            return key is Key.Up or Key.Down or Key.Left or Key.Right && AnyInking();
        }

        if (!AnyInking()) // never move a page under a stroke being written
        {
            command.Execute(null);
            Rail.NotifyActivity(); // keyboard navigation is the user's: the rail shows
        }

        return true;
    }

    /// <summary>
    /// Shift+wheel: one wheel step (<paramref name="delta"/> as in the wheel event, 120 per notch, positive = left)
    /// pans the document horizontally through the view model. Nothing while a page is inking.
    /// </summary>
    internal void PanByWheel(int delta)
    {
        if (_viewModel?.Viewport is not { Layout: { } layout } viewport || AnyInking())
        {
            return;
        }

        var lines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : _defaultWheelLines;
        var step = delta / _wheelNotch * lines * _wheelLineDip;
        var (x, y) = layout.ToOffsets(viewport.Position, viewport.ViewportWidthDip, viewport.ViewportHeightDip);
        viewport.SetScroll(x - step, y);
    }

    /// <summary>
    /// A press at <paramref name="position"/> (rail coordinates, the same as this view's). Any press outside the open
    /// page field closes it. The pen is taken only on the shown rail's knob (true: it grabs it, unless
    /// <paramref name="mayGrab"/> is false, e.g. the back end); anywhere else it inks on the page under it (false,
    /// FR-30). A finger or the mouse on the knob's wider zone grabs it, in the strip presses it (a tap jumps on lift), on its indicator
    /// opens the page field (true: the press is the rail's); a palm-sized <paramref name="contact"/> there is taken and
    /// does nothing; a finger near the edge of a hidden rail shows it and still pans (false).
    /// </summary>
    internal bool RoutePointerDown(Point position, PointerKind kind, Size contact = default, bool mayGrab = true)
    {
        StopGlide(); // any press, anywhere (the rail included), stops a glide
        if (Rail.IsEditing && !Rail.IsOnGoToPageBox(position))
        {
            Rail.CancelGoToPage();
        }

        if (kind == PointerKind.Pen)
        {
            // The pen only grabs the knob of the shown rail (UI-07); anywhere else, the strip and the ticks included,
            // it inks (FR-30). Never while a page is inking.
            return mayGrab && Rail.ZoneAt(position, PointerKind.Pen) == RailZone.Knob && !AnyInking()
                && Rail.PointerDown(Rail.TrackYAt(position), onKnob: true);
        }

        var zone = Rail.ZoneAt(position, kind);
        if (zone != RailZone.None && kind == PointerKind.Finger && ScrollRail.IsPalmContact(contact))
        {
            return true; // a hand coming down to write: neither the rail nor the document moves
        }

        switch (zone)
        {
            case RailZone.Knob:
                // Grabbed wherever in its zone (also beside the strip, over the page): taken, so no pan starts.
                return Rail.PointerDown(Rail.TrackYAt(position), onKnob: true) || AnyInking();
            case RailZone.Strip:
                return Rail.PointerDown(Rail.TrackYAt(position)) || AnyInking(); // while inking: taken, nothing moves
            case RailZone.Indicator:
                Rail.OpenGoToPage();
                return true;
            default:
                if (IsNearRail(position))
                {
                    Rail.NotifyActivity(); // shown for the next press; this one is the document's
                }

                return false;
        }
    }

    /// <summary>
    /// A finger swiped right from the left edge (UI-04): the window opens the left-edge panel. Never raised for the pen
    /// (at the edge it inks, FR-30) or the mouse (it has the menu entry and the keyboard).
    /// </summary>
    internal event EventHandler? LeftEdgeSwiped;

    /// <summary>
    /// A press at <paramref name="position"/> (this view's coordinates) that may start a swipe from the left edge: only
    /// a finger-sized contact (never a palm, <see cref="ScrollRail.IsPalmContact"/>), only within
    /// <c>EdgeSwipeZoneWidth</c> of the left edge, never while a page is inking. A second finger while a candidate is
    /// live (a pinch) drops it. The press itself is never taken (the finger still pans the document, as near the rail).
    /// True when a candidate started.
    /// </summary>
    internal bool BeginEdgeSwipe(Point position, PointerKind kind, Size contact = default)
    {
        if (_edgeSwipeStart is not null)
        {
            _edgeSwipeStart = null; // a second finger: a pinch, not a swipe
            return false;
        }

        if (kind != PointerKind.Finger || ScrollRail.IsPalmContact(contact) || AnyInking()
            || position.X < 0 || position.X > EdgeZoneWidth())
        {
            return false;
        }

        _edgeSwipeStart = position;
        return true;
    }

    /// <summary>
    /// The finger moves: <see cref="EdgeSwipeDistanceDip"/> to the right, more horizontal than vertical, is a swipe
    /// (<see cref="LeftEdgeSwiped"/>, once); a mostly vertical move first is a pan and ends the candidate.
    /// </summary>
    internal void MoveEdgeSwipe(Point position)
    {
        if (_edgeSwipeStart is not { } start)
        {
            return;
        }

        if (AnyInking())
        {
            _edgeSwipeStart = null; // the pen came down: never open a panel over a stroke being written
            return;
        }

        var dx = position.X - start.X;
        var dy = Math.Abs(position.Y - start.Y);
        if (dy > EdgeSwipeDistanceDip / 2 && dy > Math.Abs(dx))
        {
            _edgeSwipeStart = null; // a vertical pan from the edge stays a pan
            return;
        }

        if (dx >= EdgeSwipeDistanceDip && dx > dy)
        {
            _edgeSwipeStart = null;
            LeftEdgeSwiped?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The finger lifts (or is lost): no swipe.</summary>
    internal void EndEdgeSwipe()
    {
        _edgeSwipeStart = null;
        _edgeTouch = null;
    }

    private double EdgeZoneWidth() => TryFindResource("EdgeSwipeZoneWidth") as double? ?? 0;

    /// <summary>The finger or mouse on the rail lifts: a drag ends, a press off the knob that did not move jumps.</summary>
    internal void RoutePointerUp()
    {
        _railTouchId = null;
        _railStylus = null;
        Rail.PointerUp();
    }

    private void OnPreviewMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        StopGlide(); // the wheel or a touchpad takes over
        if (AnyInking())
        {
            // Never move a page under a stroke being written: the wheel, a touchpad's two-finger scroll, Ctrl+wheel and
            // Shift+wheel are all taken and do nothing (FR-26).
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) == 0)
        {
            if ((modifiers & ModifierKeys.Shift) != 0)
            {
                e.Handled = true; // the scroll bars are hidden: Shift+wheel pans sideways with a plain mouse
                PanByWheel(e.Delta);
            }

            return; // the plain wheel scrolls vertically in the scroll viewer
        }

        e.Handled = true;
        ZoomByWheel(e.Delta, e.GetPosition(Viewport));
    }

    /// <summary>
    /// Ctrl+wheel: one wheel step (<paramref name="delta"/>, 120 per notch) zooms around <paramref name="anchor"/>
    /// (viewport DIPs), and the indicator shows (UI-05). Nothing while a page is inking.
    /// </summary>
    internal void ZoomByWheel(int delta, Point anchor)
    {
        StopGlide();
        if (_viewModel?.Viewport is not { Layout: not null } viewport || AnyInking())
        {
            return;
        }

        viewport.ZoomAt(Math.Pow(_wheelStepFactor, delta / _wheelNotch), anchor);
        Rail.NotifyActivity();
    }

    private bool IsNearRail(Point position) => position.X >= Rail.ActualWidth - Rail.Strip.Width;

    /// <summary>A real mouse press (not promoted from the pen or a finger): maybe the rail's.</summary>
    private void OnHostMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null)
        {
            return; // promoted from the pen or a finger: handled by their own events
        }

        if (RoutePointerDown(e.GetPosition(Rail), PointerKind.Mouse))
        {
            e.Handled = true;
            if (Rail.IsPressed)
            {
                Host.CaptureMouse();
            }
        }
    }

    /// <summary>The mouse drags the knob, or hovers: near the edge it shows the rail and over it keeps it.</summary>
    private void OnHostMouseMove(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice is not null)
        {
            return; // the pen hovering on its way to ink, or a finger
        }

        var position = e.GetPosition(Rail);
        if (Rail.IsPressed && _railTouchId is null && Host.IsMouseCaptured)
        {
            Rail.PointerMove(Rail.TrackYAt(position));
            return;
        }

        if (IsNearRail(position))
        {
            Rail.NotifyActivity();
        }

        Rail.SetHover(Rail.ZoneAt(position) != RailZone.None);
    }

    private void OnHostMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is null && _railTouchId is null && Rail.IsPressed)
        {
            e.Handled = true;
            RoutePointerUp(); // first: a tap jumps on this lift (the capture loss below then finds nothing pressed)
            Host.ReleaseMouseCapture();
        }
    }

    private void OnHostLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_railTouchId is null && Rail.IsPressed)
        {
            Rail.PointerCancel(); // capture taken away: a drag ends where it is, a pending tap never jumps
        }
    }

    private void OnHostMouseLeave(object sender, MouseEventArgs e) => Rail.SetHover(false);

    /// <summary>A finger press: in the shown rail's strip or on its indicator it is the rail's (never panned).</summary>
    private void OnHostTouchDown(object? sender, TouchEventArgs e)
    {
        StopGlide(); // a finger (or the pen's tip) touching down stops a glide, wherever it lands
        var kind = IsPenInput() ? PointerKind.Pen : PointerKind.Finger;
        if (kind == PointerKind.Pen || Rail.IsPressed)
        {
            return; // the pen's press is routed by PreviewStylusDown; a second finger during a press is ignored
        }

        var touch = e.GetTouchPoint(Rail);
        if (_edgeTouch is not { IsActive: true })
        {
            _edgeSwipeStart = null; // a candidate whose finger lifted elsewhere
        }

        if (BeginEdgeSwipe(e.GetTouchPoint(this).Position, kind, touch.Size))
        {
            _edgeTouch = e.TouchDevice; // not taken: the same finger may still pan
        }

        if (RoutePointerDown(touch.Position, kind, touch.Size))
        {
            e.Handled = true; // not promoted to the mouse, never seen by the scroll viewer's manipulation
            if (Rail.IsPressed)
            {
                _railTouchId = e.TouchDevice.Id;
                Host.CaptureTouch(e.TouchDevice); // the host is not manipulation-enabled: no pan starts
            }
        }
    }

    private void OnHostTouchMove(object? sender, TouchEventArgs e)
    {
        if (ReferenceEquals(e.TouchDevice, _edgeTouch))
        {
            MoveEdgeSwipe(e.GetTouchPoint(this).Position);
        }

        if (e.TouchDevice.Id == _railTouchId)
        {
            e.Handled = true;
            Rail.PointerMove(Rail.TrackYAt(e.GetTouchPoint(Rail).Position));
        }
    }

    private void OnHostTouchUp(object? sender, TouchEventArgs e)
    {
        if (ReferenceEquals(e.TouchDevice, _edgeTouch))
        {
            EndEdgeSwipe();
        }

        if (e.TouchDevice.Id == _railTouchId)
        {
            e.Handled = true;
            RoutePointerUp(); // first: a tap jumps on this lift
            Host.ReleaseTouchCapture(e.TouchDevice);
        }
    }

    private void OnHostLostTouchCapture(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id == _railTouchId)
        {
            _railTouchId = null;
            Rail.PointerCancel();
        }
    }

    /// <summary>The pen comes into range (hovering on its way to write): a glide stops before it touches.</summary>
    private void OnHostStylusInRange(object sender, StylusEventArgs e)
    {
        if (e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Stylus)
        {
            NotifyPenNear();
        }
    }

    /// <summary>
    /// The pen's press: on the shown rail's knob it grabs it (the stylus is captured by the knob's grip, so the drag
    /// goes on wherever the pen moves and nothing inks); anywhere else it inks. It closes an open page field.
    /// </summary>
    private void OnHostStylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice?.Type != TabletDeviceType.Stylus)
        {
            return; // fingers go through the touch events
        }

        // The back end never grabs (it always erases), but it still closes the page field and stops a glide.
        if (RoutePointerDown(e.GetPosition(Rail), PointerKind.Pen, mayGrab: !e.StylusDevice.Inverted))
        {
            e.Handled = true; // the page under the grip never sees this press
            _railStylus = e.StylusDevice;
            Rail.KnobGrip.CaptureStylus();
        }
    }

    private void OnHostStylusMove(object sender, StylusEventArgs e)
    {
        if (ReferenceEquals(e.StylusDevice, _railStylus))
        {
            e.Handled = true;
            Rail.PointerMove(Rail.TrackYAt(e.GetPosition(Rail)));
        }
    }

    private void OnHostStylusUp(object sender, StylusEventArgs e)
    {
        if (ReferenceEquals(e.StylusDevice, _railStylus))
        {
            e.Handled = true;
            RoutePointerUp(); // first: the capture loss below then finds nothing pressed
            Rail.KnobGrip.ReleaseStylusCapture();
        }
    }

    private void OnHostLostStylusCapture(object sender, StylusEventArgs e)
    {
        if (_railStylus is not null && ReferenceEquals(e.StylusDevice, _railStylus))
        {
            _railStylus = null;
            Rail.PointerCancel(); // capture taken away: the drag ends where it is
        }
    }

    private void OnPreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (IsPenInput())
        {
            return; // only fingers tap; the pen never zooms
        }

        _touches.Add(e.TouchDevice.Id);
        _tapStart = _touches.Count == 1 ? (e.GetTouchPoint(Viewport).Position, e.Timestamp) : null;
        if (_touches.Count > 1)
        {
            _lastTap = null; // a pinch is never half of a double tap
        }
    }

    private void OnPreviewTouchUp(object? sender, TouchEventArgs e)
    {
        var wasSingle = _touches.Count == 1;
        if (!_touches.Remove(e.TouchDevice.Id) || !wasSingle || _tapStart is not { } start)
        {
            return;
        }

        _tapStart = null;
        var position = e.GetTouchPoint(Viewport).Position;
        if (unchecked(e.Timestamp - start.Time) <= DoubleTapTimeMs && (position - start.Position).Length <= DoubleTapDistanceDip)
        {
            RegisterTap(position, e.Timestamp);
        }
    }

    private static bool IsPenInput() => Stylus.CurrentStylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus;

    private void OnTouchEnded(object? sender, TouchEventArgs e)
    {
        if (!e.TouchDevice.IsActive)
        {
            _touches.Remove(e.TouchDevice.Id); // lifted elsewhere: no stale finger blocks the next tap
        }
    }

    /// <summary>
    /// A page of the window: its surface and its stroke load. The load is not cancelled when the page leaves (the
    /// session keeps it; a result arriving after a rebind is dropped by slot identity). Bitmaps go through
    /// <see cref="PageBitmapCoordinator"/>, which cancels the renders of pages that left.
    /// </summary>
    private sealed class PageSlot(InkSurface surface, int pageIndex)
    {
        public InkSurface Surface { get; } = surface;

        public int PageIndex { get; } = pageIndex;

        /// <summary>True while its strokes are being loaded (one load at a time).</summary>
        public bool LoadingEditor { get; set; }
    }
}
