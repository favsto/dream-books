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
/// FR-27). Fingers pan and zoom, never while a surface is inking; the pen never scrolls (FR-30). The scroll bars are
/// replaced by the fading <see cref="ScrollRail"/> (UI-07); the navigation keys go through the view model's commands
/// (<see cref="HandleNavigationKey"/>, FR-10).
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

    private const double _wheelStepFactor = 1.1;
    private const double _wheelNotch = 120;
    private const double _minScaleDelta = 1e-4;
    private const double _sameOffsetDip = 0.5;
    private const double _maxPanRemainderDip = 0.5;

    /// <summary>One wheel line in DIPs, as the scroll viewer's own wheel scrolling.</summary>
    private const double _wheelLineDip = 16;
    private const int _defaultWheelLines = 3;

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
        return surface;
    }

    private void DiscardSurface(InkSurface surface)
    {
        surface.InkingEnded -= OnSurfaceInkingEnded;
        BindingOperations.ClearAllBindings(surface);
        surface.ViewModel = null; // unsubscribes from the view model: nothing keeps a discarded surface alive
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is { } old)
        {
            old.Viewport.PositionChanged -= OnPositionChanged;
            old.Viewport.LayoutChanged -= OnLayoutChanged;
            old.Viewport.PropertyChanged -= OnViewportPropertyChanged;
            old.PropertyChanged -= OnViewModelPropertyChanged;
        }

        ReleaseAll();
        _pool.Clear();
        _viewModel = viewModel;
        Rail.Model = viewModel?.Viewport;
        _pool = NewPool(viewModel);
        _pendingOffsets = null;
        _requestedFrom = viewModel?.Renderer;
        _bitmaps.SetDocument(null); // whatever the new view model shows starts from scratch
        SyncBitmaps();
        if (viewModel is not null)
        {
            viewModel.Viewport.PositionChanged += OnPositionChanged;
            viewModel.Viewport.LayoutChanged += OnLayoutChanged;
            viewModel.Viewport.PropertyChanged += OnViewportPropertyChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ReportViewportSize();
        }

        ApplyPosition();
        UpdateSurfaces();
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        // Another document (or none): every surface goes, a live gesture on one is dropped, every render is cancelled.
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
        VirtualPagePanel.SetPageIndex(surface, pageIndex);
        AutomationProperties.SetName(surface, string.Format(CultureInfo.CurrentCulture, Strings.PageNameFormat, pageIndex + 1));
        FadeIn(surface);

        if (_viewModel.Session is { } session && session.TryGetEditor(pageIndex, out var editor) && editor is not null)
        {
            surface.Editor = editor; // already loaded: inkable at once
        }
        else
        {
            ScheduleEditorLoads();
        }

        _bitmaps.ShowCached(pageIndex); // a page seen recently shows its bitmap at once, the others their placeholder
    }

    /// <summary>A very light fade-in of the white page (NFR-03); instant when Windows animations are off (NFR-32).</summary>
    private static void FadeIn(InkSurface surface)
    {
        if (!SystemParameters.ClientAreaAnimation
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
        e.ManipulationContainer = Viewport;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        e.Handled = true;
    }

    private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        e.Handled = true;
        var delta = e.DeltaManipulation;
        ApplyManipulation(delta.Translation, (delta.Scale.X + delta.Scale.Y) / 2, e.ManipulationOrigin);
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
        }
    }

    private void OnManipulationBoundaryFeedback(object? sender, ManipulationBoundaryFeedbackEventArgs e) =>
        e.Handled = true; // No window "bounce" at the scroll limits.

    /// <summary>
    /// PgUp, PgDn, Home and End move the document through the view model's commands (FR-10; Home and End are jumps
    /// recorded for Back). Not with modifiers, and not while the keys belong to a field or a slider
    /// (<paramref name="source"/> is where the key event comes from). True when the key was used.
    /// </summary>
    internal bool HandleNavigationKey(Key key, ModifierKeys modifiers, object? source)
    {
        if (modifiers != ModifierKeys.None || source is TextBoxBase or PasswordBox or RangeBase
            || _viewModel?.Viewport is not { Layout: not null } viewport)
        {
            return false;
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
            return false;
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
    /// page field closes it. The pen is never taken (false: it inks on the page under it, FR-30). A finger or the
    /// mouse in the shown rail's strip starts a drag, on its indicator opens the page field (true: the press is the
    /// rail's); a finger near the edge of a hidden rail shows it and still pans (false).
    /// </summary>
    internal bool RoutePointerDown(Point position, PointerKind kind)
    {
        if (Rail.IsEditing && !Rail.IsOnGoToPageBox(position))
        {
            Rail.CancelGoToPage();
        }

        if (kind == PointerKind.Pen)
        {
            return false;
        }

        switch (Rail.ZoneAt(position))
        {
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

    /// <summary>The finger or mouse on the rail lifts (or loses capture): the drag ends.</summary>
    internal void RoutePointerUp()
    {
        _railTouchId = null;
        Rail.PointerUp();
    }

    private void OnPreviewMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
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
        if (AnyInking())
        {
            return; // the zoom is locked during a stroke anyway
        }

        vm.Viewport.ZoomAt(Math.Pow(_wheelStepFactor, e.Delta / _wheelNotch), e.GetPosition(Viewport));
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
            if (Rail.IsDragging)
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
        if (Rail.IsDragging && _railTouchId is null && Host.IsMouseCaptured)
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
        if (e.StylusDevice is null && _railTouchId is null && Rail.IsDragging)
        {
            e.Handled = true;
            Host.ReleaseMouseCapture(); // LostMouseCapture ends the drag
            RoutePointerUp();
        }
    }

    private void OnHostLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_railTouchId is null && Rail.IsDragging)
        {
            RoutePointerUp();
        }
    }

    private void OnHostMouseLeave(object sender, MouseEventArgs e) => Rail.SetHover(false);

    /// <summary>A finger press: in the shown rail's strip or on its indicator it is the rail's (never panned).</summary>
    private void OnHostTouchDown(object? sender, TouchEventArgs e)
    {
        var kind = IsPenInput() ? PointerKind.Pen : PointerKind.Finger;
        if (kind == PointerKind.Pen || Rail.IsDragging)
        {
            return; // the pen's press is routed by PreviewStylusDown; a second finger during a drag is ignored
        }

        if (RoutePointerDown(e.GetTouchPoint(Rail).Position, kind))
        {
            e.Handled = true; // not promoted to the mouse, never seen by the scroll viewer's manipulation
            if (Rail.IsDragging)
            {
                _railTouchId = e.TouchDevice.Id;
                Host.CaptureTouch(e.TouchDevice); // the host is not manipulation-enabled: no pan starts
            }
        }
    }

    private void OnHostTouchMove(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id == _railTouchId)
        {
            e.Handled = true;
            Rail.PointerMove(Rail.TrackYAt(e.GetTouchPoint(Rail).Position));
        }
    }

    private void OnHostTouchUp(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id == _railTouchId)
        {
            e.Handled = true;
            Host.ReleaseTouchCapture(e.TouchDevice);
            RoutePointerUp();
        }
    }

    private void OnHostLostTouchCapture(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id == _railTouchId)
        {
            RoutePointerUp();
        }
    }

    /// <summary>The pen's press: never the rail's (it inks), but it closes an open page field.</summary>
    private void OnHostStylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Stylus)
        {
            RoutePointerDown(e.GetPosition(Rail), PointerKind.Pen);
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
