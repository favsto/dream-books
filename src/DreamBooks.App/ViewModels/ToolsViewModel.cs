using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Ink;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>Active tool and ink settings shown in the debug panel.</summary>
public sealed class ToolsViewModel : ObservableObject
{
    private const double _minWidthMm = 0.25;
    private const double _maxWidthMm = 5;
    private const double _minEraserMm = 2;
    private const double _maxEraserMm = 20;

    private AppTool _activeTool = AppTool.Pen;
    private bool _isInverted;
    private RendererMode _renderer = RendererMode.Custom;
    private PressureCurve _curve = PressureCurve.Linear;
    private SmoothingLevel _smoothing = SmoothingLevel.Light;
    private double _baseWidthMm = 0.5;
    private InkColor _color = Palette.Colors[0];
    private EraserMode _eraserMode = EraserMode.WholeStroke;
    private double _eraserSizeMm = 6;

    public ToolsViewModel() => SelectColorCommand = new RelayCommand<InkColor>(c => Color = c);

    public IRelayCommand<InkColor> SelectColorCommand { get; }

    public AppTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (SetProperty(ref _activeTool, value))
            {
                OnPropertyChanged(nameof(EffectiveTool));
            }
        }
    }

    public bool IsInverted => _isInverted;

    public AppTool EffectiveTool => _isInverted ? AppTool.Eraser : _activeTool;

    public RendererMode Renderer
    {
        get => _renderer;
        set
        {
            if (SetProperty(ref _renderer, value))
            {
                OnPropertyChanged(nameof(IsCustomRenderer));
            }
        }
    }

    public bool IsCustomRenderer => _renderer == RendererMode.Custom;

    public PressureCurve Curve
    {
        get => _curve;
        set => SetProperty(ref _curve, value);
    }

    public SmoothingLevel Smoothing
    {
        get => _smoothing;
        set => SetProperty(ref _smoothing, value);
    }

    public double BaseWidthMm
    {
        get => _baseWidthMm;
        set
        {
            if (SetProperty(ref _baseWidthMm, Math.Clamp(value, _minWidthMm, _maxWidthMm)))
            {
                OnPropertyChanged(nameof(BaseWidthPt));
            }
        }
    }

    public double BaseWidthPt => Units.MmToPt(_baseWidthMm);

    public InkColor Color
    {
        get => _color;
        set => SetProperty(ref _color, value);
    }

    public EraserMode EraserMode
    {
        get => _eraserMode;
        set => SetProperty(ref _eraserMode, value);
    }

    public double EraserSizeMm
    {
        get => _eraserSizeMm;
        set => SetProperty(ref _eraserSizeMm, Math.Clamp(value, _minEraserMm, _maxEraserMm));
    }

    /// <summary>The pen's back end touched the screen: erase whatever tool is active (FR-30).</summary>
    public void BeginInvertedEraser() => SetInverted(true);

    public void EndInvertedEraser() => SetInverted(false);

    private void SetInverted(bool value)
    {
        if (_isInverted == value)
        {
            return;
        }

        _isInverted = value;
        OnPropertyChanged(nameof(IsInverted));
        OnPropertyChanged(nameof(EffectiveTool));
    }
}
