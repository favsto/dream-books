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

    private readonly DispatcherTimer _renderTimer;
    private MainViewModel? _viewModel;
    private CancellationTokenSource? _renderCts;
    private Point? _zoomAnchor;

    public PageView()
    {
        InitializeComponent();
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = _renderDelay };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            RenderPage();
        };
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        Unloaded += (_, _) =>
        {
            _renderTimer.Stop();
            _renderCts?.Cancel();
        };
    }

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
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PageChanged += OnPageChanged;
            viewModel.ZoomVm.PropertyChanged += OnZoomChanged;
            Surface.Scale = viewModel.ZoomVm.Zoom * Units.DipPerPt;
        }
    }

    private void OnPageChanged(object? sender, EventArgs e)
    {
        // A different page: the old bitmap would be wrong, not just blurry.
        Surface.PageImage = null;
        _renderTimer.Stop();
        RenderPage();
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
        var (width, height) = RenderSizePolicy.Compute(
            vm.PageSize, vm.ZoomVm.Zoom, VisualTreeHelper.GetDpi(this).DpiScaleX);
        try
        {
            var rendered = await renderer.RenderPageAsync(editor.Page.Index, width, height, cts.Token);
            var bitmap = await Task.Run(() => PageBitmap.FromRendered(rendered), cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(vm.Editor, editor))
            {
                Surface.PageImage = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer render.
        }
        catch (ObjectDisposedException)
        {
            // The document was replaced while rendering.
        }
        catch (InvalidOperationException)
        {
            // PDFium could not render this page: keep what is shown rather than crash (NFR-11).
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
        _zoomAnchor = e.GetPosition(Viewport);
        _viewModel.ZoomVm.ZoomAt(Math.Pow(_wheelStepFactor, e.Delta / _wheelNotch));
        _zoomAnchor = null;
    }
}
