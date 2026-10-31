using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;

namespace DreamBooks.App.Views;

/// <summary>
/// Scrollable, zoomable viewport around the page surface. Zoom changes rescale the ink at once (vector,
/// FR-26); the PDF bitmap is re-rendered off the UI thread once the zoom has settled for 150 ms, and the
/// previous bitmap stays stretched until then (NFR-04, NFR-07, NFR-22).
/// </summary>
public partial class PageView : UserControl
{
    private static readonly TimeSpan _renderDelay = TimeSpan.FromMilliseconds(150);
    private const double _wheelStepFactor = 1.1;
    private const double _wheelNotch = 120;
    private const double _minScaleDelta = 1e-4;

    /// <summary>How long the view must stay still before its position is journalled (FR-04, FR-84).</summary>
    internal static readonly TimeSpan ViewStateDelay = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _viewStateTimer;
    private MainViewModel? _viewModel;
    private CancellationTokenSource? _renderCts;
    private Point? _zoomAnchor;
    private RestoredView? _pendingRestore;

    public PageView()
    {
        InitializeComponent();
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = _renderDelay };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            RenderPage();
        };
        _viewStateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ViewStateDelay };
        _viewStateTimer.Tick += (_, _) => ReportViewState();
        Viewport.ScrollChanged += (_, _) => ScheduleViewStateReport();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        Unloaded += (_, _) =>
        {
            _renderTimer.Stop();
            _viewStateTimer.Stop();
            _renderCts?.Cancel();
        };
    }

    /// <summary>True while a settled-view report is waiting for the view to stay still.</summary>
    internal bool IsViewStateReportPending => _viewStateTimer.IsEnabled;

    /// <summary>Width available to the page for "fit width", leaving room for the vertical scroll bar.</summary>
    public double FitWidthViewport => Math.Max(0, Viewport.ActualWidth - SystemParameters.VerticalScrollBarWidth - 1);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ScheduleRender();
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PageChanged -= OnPageChanged;
            _viewModel.ZoomVm.PropertyChanged -= OnZoomChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PageChanged += OnPageChanged;
            viewModel.ZoomVm.PropertyChanged += OnZoomChanged;
            viewModel.PropertyChanged += OnViewModelChanged;
            Surface.Scale = viewModel.ZoomVm.Zoom * Units.DipPerPt;
            if (viewModel.ViewToRestore is { } restored)
            {
                ApplyRestoredView(restored);
            }
        }
    }

    private void OnPageChanged(object? sender, EventArgs e)
    {
        // A different page: the old bitmap would be wrong, not just blurry.
        Surface.PageImage = null;
        _renderTimer.Stop();
        RenderPage();
        _pendingRestore = null; // the user moved on before the restored position could be applied
        ScheduleViewStateReport();
    }

    /// <summary>Restarts the 500 ms wait: the position is reported once the user stops scrolling or zooming.</summary>
    private void ScheduleViewStateReport()
    {
        _viewStateTimer.Stop();
        _viewStateTimer.Start();
    }

    /// <summary>Hands the settled position to the view model, which journals it if there is something to restore.</summary>
    internal void ReportViewState()
    {
        _viewStateTimer.Stop();
        if (_viewModel is not { PageNumber: > 0 } vm)
        {
            return;
        }

        if (Surface.IsInking)
        {
            ScheduleViewStateReport(); // never write while a stroke is being drawn
            return;
        }

        vm.ReportViewState(vm.PageNumber - 1, vm.ZoomVm.Zoom, Viewport.HorizontalOffset, Viewport.VerticalOffset);
    }

    /// <summary>
    /// Scrolls to a restored position once the page is laid out at its restored zoom (the zoom itself is already
    /// applied by the view model). Applied once; offsets beyond the extent are clamped by the scroll viewer.
    /// </summary>
    internal void ApplyRestoredView(RestoredView view)
    {
        _pendingRestore = view;
        Viewport.LayoutUpdated -= OnLayoutUpdatedForRestore;
        Viewport.LayoutUpdated += OnLayoutUpdatedForRestore;
        Viewport.InvalidateMeasure();
    }

    private void OnLayoutUpdatedForRestore(object? sender, EventArgs e)
    {
        if (_pendingRestore is not { } view)
        {
            Viewport.LayoutUpdated -= OnLayoutUpdatedForRestore;
            return;
        }

        if (Surface.Page is null || Viewport.ExtentWidth <= 0 || Viewport.ExtentHeight <= 0)
        {
            return; // not laid out yet: wait for the next pass
        }

        Viewport.LayoutUpdated -= OnLayoutUpdatedForRestore;
        _pendingRestore = null;
        Viewport.ScrollToHorizontalOffset(view.ScrollX);
        Viewport.ScrollToVerticalOffset(view.ScrollY);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ViewToRestore) && _viewModel?.ViewToRestore is { } restored)
        {
            ApplyRestoredView(restored);
            return;
        }

        // A save swapped the renderer onto the saved file: same page content, so the current bitmap stays
        // until the new one is ready (a render attempted while the file was released yielded nothing).
        if (e.PropertyName == nameof(MainViewModel.Renderer) && _viewModel?.Renderer is not null)
        {
            ScheduleRender();
        }
    }

    private void OnZoomChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ZoomViewModel.Zoom) || _viewModel is null)
        {
            return;
        }

        var anchor = _zoomAnchor ?? new Point(Viewport.ViewportWidth / 2, Viewport.ViewportHeight / 2);
        _zoomAnchor = null;
        ApplyScale(_viewModel.ZoomVm.Zoom * Units.DipPerPt, anchor);
        ScheduleRender();
    }

    /// <summary>Rescales the surface keeping the page point under <paramref name="anchor"/> (viewport DIPs) still.</summary>
    private void ApplyScale(double newScale, Point anchor)
    {
        var oldScale = Surface.Scale;
        if (newScale == oldScale)
        {
            return;
        }

        if (!IsLoaded || Surface.Page is null)
        {
            Surface.Scale = newScale;
            return;
        }

        Viewport.UpdateLayout();
        var onSurface = Viewport.TranslatePoint(anchor, Surface);
        var pagePt = new Point(onSurface.X / oldScale, onSurface.Y / oldScale);

        Surface.Scale = newScale;
        Viewport.UpdateLayout();

        var moved = Surface.TranslatePoint(new Point(pagePt.X * newScale, pagePt.Y * newScale), Viewport);
        Viewport.ScrollToHorizontalOffset(Viewport.HorizontalOffset + moved.X - anchor.X);
        Viewport.ScrollToVerticalOffset(Viewport.VerticalOffset + moved.Y - anchor.Y);
    }

    private void ScheduleRender()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private async void RenderPage()
    {
        _renderCts?.Cancel();
        if (_viewModel is not { Renderer: { } renderer, Editor: { } editor } vm)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _renderCts = cts;
        var pageSize = vm.PageSize;
        var zoom = vm.ZoomVm.Zoom;
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var pageIndex = editor.Page.Index;
        try
        {
            // TryRenderAsync swallows every failure (NFR-11): this async void handler never throws.
            var bitmap = await PageBitmap.TryRenderAsync(
                ct =>
                {
                    var (width, height) = RenderSizePolicy.Compute(pageSize, zoom, dpiScale);
                    return renderer.RenderPageAsync(pageIndex, width, height, ct);
                },
                cts.Token);
            if (bitmap is not null && !cts.IsCancellationRequested && ReferenceEquals(vm.Editor, editor))
            {
                Surface.PageImage = bitmap;
            }
        }
        finally
        {
            if (ReferenceEquals(_renderCts, cts))
            {
                _renderCts = null;
            }

            cts.Dispose();
        }
    }

    private void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = Viewport;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        e.Handled = true;
    }

    private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        e.Handled = true;
        if (_viewModel is null || Surface.IsInking)
        {
            return; // Never move the page under a stroke being written.
        }

        var delta = e.DeltaManipulation;
        Viewport.UpdateLayout();
        Viewport.ScrollToHorizontalOffset(Viewport.HorizontalOffset - delta.Translation.X);
        Viewport.ScrollToVerticalOffset(Viewport.VerticalOffset - delta.Translation.Y);

        var factor = (delta.Scale.X + delta.Scale.Y) / 2;
        if (Math.Abs(factor - 1) > _minScaleDelta && double.IsFinite(factor) && factor > 0)
        {
            Viewport.UpdateLayout();
            _zoomAnchor = e.ManipulationOrigin;
            _viewModel.ZoomVm.ZoomAt(factor);
            _zoomAnchor = null;
        }
    }

    private void OnManipulationBoundaryFeedback(object? sender, ManipulationBoundaryFeedbackEventArgs e) =>
        e.Handled = true; // No window "bounce" at the scroll limits.

    private void OnPreviewMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        if (_viewModel is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        if (Surface.IsInking)
        {
            return; // ZoomVm is locked during a stroke too; this just skips the anchor bookkeeping.
        }

        _zoomAnchor = e.GetPosition(Viewport);
        _viewModel.ZoomVm.ZoomAt(Math.Pow(_wheelStepFactor, e.Delta / _wheelNotch));
        _zoomAnchor = null;
    }
}
