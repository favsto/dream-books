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
    private PressureCurve _curve = PressureCurve.Firm;
    private SmoothingLevel _smoothing = SmoothingLevel.Strong;
    private double _baseWidthMm = 0.5;
    private InkColor _color = Palette.Colors[0];
    private EraserMode _eraserMode = EraserMode.WholeStroke;
    private double _eraserSizeMm = 6;

    public ToolsViewModel()
    {
        SelectColorCommand = new RelayCommand<InkColor>(c => Color = c);
        ToggleHighlighterCommand = new RelayCommand(
            () => ActiveTool = ActiveTool == AppTool.Highlighter ? AppTool.Pen : AppTool.Highlighter);
    }

    public IRelayCommand<InkColor> SelectColorCommand { get; }

    /// <summary>The highlighter as a toggle next to the pen: on selects it, off goes back to the pen (FR-41).</summary>
    public IRelayCommand ToggleHighlighterCommand { get; }

    /// <summary>The highlighter options (FR-41, FR-44, FR-46).</summary>
    public HighlighterOptionsViewModel Highlighter { get; } = new();

    public AppTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (SetProperty(ref _activeTool, value))
            {
                OnPropertyChanged(nameof(EffectiveTool));
                OnPropertyChanged(nameof(IsDrawingTool));
            }
        }
    }

    public bool IsInverted => _isInverted;

    public AppTool EffectiveTool => _isInverted ? AppTool.Eraser : _activeTool;

    /// <summary>True when the pen inks with the effective tool: the pen or the highlighter (FR-30, FR-41).</summary>
    public bool IsDrawingTool => EffectiveTool is AppTool.Pen or AppTool.Highlighter;

    /// <summary>
    /// The colour of a stroke started now with the effective tool: the highlighter's translucent colour, otherwise
    /// the pen colour. Read when a stroke starts (the live stroke keeps it).
    /// </summary>
    public InkColor ActiveInkColor =>
        EffectiveTool == AppTool.Highlighter ? Highlighter.Settings.StrokeColor : _color;

    /// <summary>The base width of a stroke started now: the highlighter's constant width (FR-42), otherwise the pen's.</summary>
    public double ActiveInkWidthPt =>
        EffectiveTool == AppTool.Highlighter ? Highlighter.Settings.WidthPt : BaseWidthPt;

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
        OnPropertyChanged(nameof(IsDrawingTool));
    }
}
