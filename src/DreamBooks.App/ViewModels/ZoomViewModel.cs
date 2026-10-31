using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>Zoom factor, 1.0 = 100% (FR-06), and whether it follows the viewport width (fit-width mode, FR-07).</summary>
public sealed class ZoomViewModel : ObservableObject
{
    private const double _min = 0.25;
    private const double _max = 8.0;
    private const double _epsilon = 1e-6;

    private static readonly double[] _steps = [0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8];

    private double _zoom = 1.0;
    private bool _isFitWidth;
    private bool _isLocked;

    public ZoomViewModel()
    {
        ZoomInCommand = new RelayCommand(() => Zoom = _steps.FirstOrDefault(s => s > _zoom + _epsilon, _max));
        ZoomOutCommand = new RelayCommand(() => Zoom = _steps.LastOrDefault(s => s < _zoom - _epsilon, _min));
    }

    public IRelayCommand ZoomInCommand { get; }

    public IRelayCommand ZoomOutCommand { get; }

    /// <summary>
    /// True while a stroke is being drawn: every zoom request is ignored, so the live (wet) ink is never
    /// left at a scale different from the page under it. Raises PropertyChanged, so a fit-width recompute skipped
    /// meanwhile can catch up on release.
    /// </summary>
    public bool IsLocked
    {
        get => _isLocked;
        set => SetProperty(ref _isLocked, value);
    }

    /// <summary>
    /// True while the zoom fits the widest page to the viewport width and follows resizes. Any manual zoom (setting
    /// <see cref="Zoom"/>, a step, <see cref="ZoomAt"/>) turns it off; <see cref="DocumentViewportViewModel"/>
    /// turns it on (double tap, a document opened without a remembered position).
    /// </summary>
    public bool IsFitWidth
    {
        get => _isFitWidth;
        private set => SetProperty(ref _isFitWidth, value);
    }

    /// <summary>A manual zoom, clamped to 25%–800%; turns fit-width mode off. Ignored while locked.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (!IsLocked)
            {
                IsFitWidth = false; // first: listeners of Zoom already see the manual mode
                SetProperty(ref _zoom, Math.Clamp(value, _min, _max));
            }
        }
    }

    /// <summary>Pinch or wheel: multiplies the current zoom by <paramref name="factor"/>.</summary>
    public void ZoomAt(double factor) => Zoom = _zoom * factor;

    /// <summary>
    /// Fits a page width to the viewport and turns fit-width mode on (the document view then fits the widest page of
    /// the document instead).
    /// </summary>
    public void FitWidth(double viewportWidthDip, double pageWidthPt)
    {
        if (viewportWidthDip <= 0 || pageWidthPt <= 0)
        {
            return;
        }

        ApplyFitZoom(viewportWidthDip / (pageWidthPt * Units.DipPerPt));
    }

    /// <summary>Sets a zoom computed by fit width, keeping fit-width mode on. Ignored while locked.</summary>
    internal void ApplyFitZoom(double zoom)
    {
        if (IsLocked || !double.IsFinite(zoom) || zoom <= 0)
        {
            return;
        }

        IsFitWidth = true; // first: listeners of Zoom see the fit mode
        SetProperty(ref _zoom, Math.Clamp(zoom, _min, _max), nameof(Zoom));
    }

    /// <summary>Turns fit-width mode on or off without changing the zoom (the viewport applies it once it knows its width).</summary>
    internal void SetFitWidthMode(bool on) => IsFitWidth = on;
}
