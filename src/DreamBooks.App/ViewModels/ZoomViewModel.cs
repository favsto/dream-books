using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>Zoom factor, 1.0 = 100% (FR-06).</summary>
public sealed class ZoomViewModel : ObservableObject
{
    private const double _min = 0.25;
    private const double _max = 8.0;
    private const double _epsilon = 1e-6;

    private static readonly double[] _steps = [0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8];

    private double _zoom = 1.0;

    public ZoomViewModel()
    {
        ZoomInCommand = new RelayCommand(() => Zoom = _steps.FirstOrDefault(s => s > _zoom + _epsilon, _max));
        ZoomOutCommand = new RelayCommand(() => Zoom = _steps.LastOrDefault(s => s < _zoom - _epsilon, _min));
    }

    public IRelayCommand ZoomInCommand { get; }

    public IRelayCommand ZoomOutCommand { get; }

    /// <summary>
    /// True while a stroke is being drawn: every zoom request is ignored, so the live (wet) ink is never
    /// left at a scale different from the page under it.
    /// </summary>
    public bool IsLocked { get; set; }

    public double Zoom
    {
        get => _zoom;
        set
        {
            if (!IsLocked)
            {
                SetProperty(ref _zoom, Math.Clamp(value, _min, _max));
            }
        }
    }

    /// <summary>Pinch or wheel: multiplies the current zoom by <paramref name="factor"/>.</summary>
    public void ZoomAt(double factor) => Zoom = _zoom * factor;

    public void FitWidth(double viewportWidthDip, double pageWidthPt)
    {
        if (viewportWidthDip <= 0 || pageWidthPt <= 0)
        {
            return;
        }

        Zoom = viewportWidthDip / (pageWidthPt * Units.DipPerPt);
    }
}
