using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;

namespace DreamBooks.App.Views;

/// <summary>
/// The whole document as one continuous vertical scroll (FR-05) with one ink surface per page of the retained window
/// (visible ± 2, NFR-05), recycled as the window moves. The view model (<see cref="DocumentViewportViewModel"/>) owns
/// the position: scrolling reports offsets to it, and every position it publishes (zoom anchored through
/// <see cref="DocumentLayout.ZoomAt"/>, fit width, jumps, restore) is applied to the scroll viewer, so nothing jumps
/// under the fingers (FR-06, FR-07, FR-04). Every visible page with loaded strokes inks at once; pen-down never changes
/// the current page; an eraser gesture belongs to the page where it starts; a stroke is clipped to its page (FR-26,
/// FR-27). Fingers pan and zoom, never while a surface is inking; the pen never scrolls (FR-30).
/// </summary>
/// <remarks>
/// A page entering the window gets a surface at once (white placeholder of the exact size, light fade-in, NFR-03),
/// its strokes after a short debounce (<see cref="DocumentViewportViewModel.CurrentPageDelay"/>, so a fast fling
/// loads nothing it passes) and its bitmap from <see cref="BitmapSource"/>. While a save holds the PDFium thread, loads
/// and renders simply wait: placeholders stay and pages already loaded keep inking; when the renderer is replaced the
/// bitmaps are asked again and failed loads retried.
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

    /// <summary>The zoom must stay still this long before the bitmaps are rendered again at the new size (NFR-07).</summary>
    private static readonly TimeSpan _renderDelay = TimeSpan.FromMilliseconds(150);

    private readonly Dictionary<int, PageSlot> _slots = [];
    private readonly HashSet<int> _touches = [];
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _editorTimer;
    private MainViewModel? _viewModel;
    private PageSurfacePool _pool;
    private (double X, double Y)? _pendingOffsets;
    private bool _settleQueued;
    private bool _updatingSurfaces;
    private double _renderedZoom = double.NaN;
    private PdfRenderer? _requestedFrom;
    private Vector _panRemainder;
    private (Point Position, int Time)? _tapStart;
    private (Point Position, int Time)? _lastTap;

    public DocumentView()
    {
        InitializeComponent();
        _pool = NewPool(null);
        BitmapSource = new RendererPageBitmapSource(() => _viewModel?.Renderer);
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = _renderDelay };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            InvalidateBitmaps();
        };
        _editorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = DocumentViewportViewModel.CurrentPageDelay };
        _editorTimer.Tick += (_, _) => FlushEditorLoads();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        Loaded += (_, _) =>
        {
            InvalidateBitmaps();
            ScheduleEditorLoads();
        };
        Unloaded += (_, _) =>
        {
            _renderTimer.Stop();
            _editorTimer.Stop();
            foreach (var slot in _slots.Values)
            {
                slot.CancelWork();
            }
        };
    }

    /// <summary>
    /// Where page bitmaps come from. INTERIM: <see cref="RendererPageBitmapSource"/>, one render per request through
    /// the current renderer; the render scheduler (Task 8) replaces it here. Tests inject their own.
    /// </summary>
    internal IPageBitmapSource BitmapSource { get; set; }

    /// <summary>The surfaces in use, by page index (the retained window, plus a page kept while it is inking).</summary>
    internal IReadOnlyDictionary<int, InkSurface> ActiveSurfaces =>
        _slots.ToDictionary(pair => pair.Key, pair => pair.Value.Surface);

    internal PageSurfacePool Pool => _pool;

    /// <summary>The surface showing a page, or null when the page is outside the window.</summary>
    internal InkSurface? SurfaceFor(int pageIndex) => _slots.TryGetValue(pageIndex, out var slot) ? slot.Surface : null;

    /// <summary>
    /// Asks every page of the window for a new bitmap; the one shown stays (stretched) until it arrives. Called when the
    /// zoom settled, the DPI changed or the renderer was replaced (the hook the render scheduler takes over in Task 8).
    /// </summary>
    internal void InvalidateBitmaps()
    {
        _renderTimer.Stop();
        foreach (var slot in _slots.Values.ToArray())
        {
            RequestBitmap(slot);
        }
    }

    /// <summary>Starts the stroke loads of the window's pages that have no editor yet (what the debounce does).</summary>
    internal void FlushEditorLoads()
    {
        _editorTimer.Stop();
        foreach (var slot in _slots.Values.ToArray())
        {
            if (slot.Surface.Editor is null && slot.EditorLoad is null)
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
        InvalidateBitmaps(); // same DIPs, more or fewer pixels
    }

    private PageSurfacePool NewPool(MainViewModel? viewModel) => new(() => CreateSurface(viewModel), DiscardSurface);

    private InkSurface CreateSurface(MainViewModel? viewModel)
    {
        var surface = new InkSurface { ViewModel = viewModel };
        AutomationProperties.SetName(surface, Strings.Page);
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
        _pool = NewPool(viewModel);
        _pendingOffsets = null;
        _renderedZoom = double.NaN;
        _requestedFrom = viewModel?.Renderer;
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
        // Another document (or none): every surface goes, a live gesture on one is dropped.
        ReleaseAll();
        _pendingOffsets = null;
        _renderedZoom = double.NaN;
        _requestedFrom = _viewModel?.Renderer;
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
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A save swapped the renderer onto the saved file: same content, so the bitmaps stay until new ones arrive;
        // stroke loads that failed while the file was released are tried again.
        // At open the renderer is announced after the layout, whose pages already asked it: not twice.
        if (e.PropertyName != nameof(MainViewModel.Renderer))
        {
            return;
        }

        if (_viewModel?.Renderer is not { } renderer)
        {
            _requestedFrom = null; // released by a save: whatever comes back is asked again
        }
        else if (!ReferenceEquals(renderer, _requestedFrom))
        {
            _requestedFrom = renderer;
            InvalidateBitmaps();
            ScheduleEditorLoads();
        }
    }

    private void OnSurfaceInkingEnded(object? sender, EventArgs e)
    {
        // A surface kept for its live stroke may leave now; deferred so the commit finishes first.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, UpdateSurfaces);
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
        Pages.Configure(layout, position.Zoom, viewport.ViewportWidthDip);
        var scale = Pages.Scale;
        foreach (var slot in _slots.Values)
        {
            slot.Surface.Scale = scale;
        }

        if (position.Zoom != _renderedZoom)
        {
            if (!double.IsNaN(_renderedZoom))
            {
                // Ink is vector and rescales at once; the bitmap stays stretched until the zoom settles.
                _renderTimer.Stop();
                _renderTimer.Start();
            }

            _renderedZoom = position.Zoom;
        }

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
        Pages.Configure(vm.Viewport.Layout, vm.Viewport.Position.Zoom, vm.Viewport.ViewportWidthDip);
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
                slot.CancelWork();
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
    }

    /// <summary>Every surface leaves (another document, another view model); live gestures are dropped.</summary>
    private void ReleaseAll()
    {
        foreach (var slot in _slots.Values.ToArray())
        {
            slot.CancelWork();
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
        FadeIn(surface);

        if (_viewModel.Session is { } session && session.TryGetEditor(pageIndex, out var editor) && editor is not null)
        {
            surface.Editor = editor; // already loaded: inkable at once
        }
        else
        {
            ScheduleEditorLoads();
        }

        RequestBitmap(slot);
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
    /// </summary>
    private async Task LoadEditorAsync(PageSlot slot)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        slot.EditorLoad = cts;
        try
        {
            var editor = await vm.EnsureEditorAsync(slot.PageIndex, cts.Token);
            if (!cts.IsCancellationRequested && IsBound(slot))
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
            if (ReferenceEquals(slot.EditorLoad, cts))
            {
                slot.EditorLoad = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Asks for the page's bitmap at the current zoom; a newer request or recycling cancels it.</summary>
    private async void RequestBitmap(PageSlot slot)
    {
        slot.CancelRender();
        if (_viewModel?.Viewport.Layout is not { } layout)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        slot.Render = cts;
        var size = VirtualPagePanel.PageSizePt(layout, slot.PageIndex);
        var zoom = _viewModel.Viewport.Position.Zoom;
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        try
        {
            var image = await BitmapSource.RenderAsync(
                slot.PageIndex, new PageSize(size.Width, size.Height), zoom, dpiScale, cts.Token);
            if (image is not null && !cts.IsCancellationRequested && IsBound(slot))
            {
                slot.Surface.PageImage = image;
            }
        }
#pragma warning disable CA1031 // A bitmap source must not take the app down from this async void (NFR-11).
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            if (ReferenceEquals(slot.Render, cts))
            {
                slot.Render = null;
            }

            cts.Dispose();
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
        if (_viewModel?.Viewport is not { Layout: { } layout } viewport || AnyInking())
        {
            return; // Never move a page under a stroke being written.
        }

        var delta = e.DeltaManipulation;
        if (delta.Translation.X != 0 || delta.Translation.Y != 0)
        {
            // Through the view model, keeping the sub-threshold remainder so slow pans are not lost.
            var (x, y) = layout.ToOffsets(viewport.Position, viewport.ViewportWidthDip, viewport.ViewportHeightDip);
            var targetX = x - delta.Translation.X + _panRemainder.X;
            var targetY = y - delta.Translation.Y + _panRemainder.Y;
            viewport.SetScroll(targetX, targetY);
            var (newX, newY) = layout.ToOffsets(viewport.Position, viewport.ViewportWidthDip, viewport.ViewportHeightDip);
            _panRemainder = new Vector(
                Math.Clamp(targetX - newX, -_maxPanRemainderDip, _maxPanRemainderDip),
                Math.Clamp(targetY - newY, -_maxPanRemainderDip, _maxPanRemainderDip));
        }

        var factor = (delta.Scale.X + delta.Scale.Y) / 2;
        if (Math.Abs(factor - 1) > _minScaleDelta && double.IsFinite(factor) && factor > 0)
        {
            viewport.ZoomAt(factor, e.ManipulationOrigin);
        }
    }

    private void OnManipulationBoundaryFeedback(object? sender, ManipulationBoundaryFeedbackEventArgs e) =>
        e.Handled = true; // No window "bounce" at the scroll limits.

    private void OnPreviewMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        if (_viewModel is not { } vm || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        if (AnyInking())
        {
            return; // the zoom is locked during a stroke anyway
        }

        vm.Viewport.ZoomAt(Math.Pow(_wheelStepFactor, e.Delta / _wheelNotch), e.GetPosition(Viewport));
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

    /// <summary>A page of the window: its surface and the work in flight for it.</summary>
    private sealed class PageSlot(InkSurface surface, int pageIndex)
    {
        public InkSurface Surface { get; } = surface;

        public int PageIndex { get; } = pageIndex;

        public CancellationTokenSource? EditorLoad { get; set; }

        public CancellationTokenSource? Render { get; set; }

        public void CancelRender() => Render?.Cancel();

        public void CancelWork()
        {
            EditorLoad?.Cancel();
            Render?.Cancel();
        }
    }
}
