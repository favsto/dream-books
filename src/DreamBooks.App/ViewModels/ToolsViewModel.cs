using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Settings;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The active tool and the ink settings: pen colour and width, the pressure curve and smoothing, the one eraser mode
/// and size (used by the eraser tool and by the pen's back end alike, FR-30, FR-31), the highlighter options and the
/// custom palette shared by the pen and the highlighter (UI-02). The values the settings file remembers (FR-110) come
/// in through <see cref="Apply"/> and go out through <see cref="CaptureInto"/>; <see cref="RememberedChanged"/> says
/// when one of them changed.
/// </summary>
public sealed class ToolsViewModel : ObservableObject
{
    private AppTool _activeTool = AppTool.Pen;
    private bool _isInverted;
    private bool _isBarrelSelection;
    private PressureCurve _curve = AppSettings.Default.PressureCurve;
    private SmoothingLevel _smoothing = AppSettings.Default.Smoothing;
    private double _baseWidthMm = AppSettings.DefaultPenWidthMm;
    private InkColor _color = Ink.Palette.Colors[0];
    private EraserMode _eraserMode = AppSettings.Default.EraserMode;
    private double _eraserSizeMm = AppSettings.DefaultEraserSizeMm;

    public ToolsViewModel()
    {
        Highlighter = new HighlighterOptionsViewModel(Palette);
        SelectColorCommand = new RelayCommand<InkColor>(c => Color = c);
        ToggleHighlighterCommand = new RelayCommand(
            () => ActiveTool = ActiveTool == AppTool.Highlighter ? AppTool.Pen : AppTool.Highlighter);
        Highlighter.SettingsChanged += (_, _) => RaiseRemembered();
        Palette.PaletteChanged += (_, _) => RaiseRemembered();
    }

    /// <summary>Raised after a value the settings remember changed (colours, widths, modes, curve, palette).</summary>
    public event EventHandler? RememberedChanged;

    public IRelayCommand<InkColor> SelectColorCommand { get; }

    /// <summary>The highlighter as a toggle next to the pen: on selects it, off goes back to the pen (FR-41).</summary>
    public IRelayCommand ToggleHighlighterCommand { get; }

    /// <summary>The custom colours shared by the pen and the highlighter (UI-02).</summary>
    public CustomPaletteViewModel Palette { get; } = new();

    /// <summary>The highlighter options (FR-41, FR-44, FR-46).</summary>
    public HighlighterOptionsViewModel Highlighter { get; }

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

    /// <summary>True while the pen's barrel button is held: the lasso is the effective tool (FR-28).</summary>
    public bool IsBarrelSelection => _isBarrelSelection;

    /// <summary>The tool the pen acts with now: the back end's eraser, else the lasso under the barrel button, else the active tool.</summary>
    public AppTool EffectiveTool => _isInverted ? AppTool.Eraser : _isBarrelSelection ? AppTool.Selection : _activeTool;

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
        set => SetRemembered(ref _curve, value);
    }

    public SmoothingLevel Smoothing
    {
        get => _smoothing;
        set => SetRemembered(ref _smoothing, value);
    }

    /// <summary>The pen's base width, clamped to 0.25..5 mm (FR-22); NaN is ignored.</summary>
    public double BaseWidthMm
    {
        get => _baseWidthMm;
        set
        {
            if (!double.IsNaN(value)
                && SetRemembered(ref _baseWidthMm, Math.Clamp(value, AppSettings.MinPenWidthMm, AppSettings.MaxPenWidthMm)))
            {
                OnPropertyChanged(nameof(BaseWidthPt));
            }
        }
    }

    public double BaseWidthPt => Units.MmToPt(_baseWidthMm);

    /// <summary>The pen colour (opaque).</summary>
    public InkColor Color
    {
        get => _color;
        set => SetRemembered(ref _color, value);
    }

    /// <summary>The one eraser mode: the eraser tool and the pen's back end both use it (FR-30, FR-31).</summary>
    public EraserMode EraserMode
    {
        get => _eraserMode;
        set => SetRemembered(ref _eraserMode, value);
    }

    /// <summary>The eraser circle's diameter, clamped to 2..20 mm (FR-32); NaN is ignored.</summary>
    public double EraserSizeMm
    {
        get => _eraserSizeMm;
        set
        {
            if (!double.IsNaN(value))
            {
                SetRemembered(ref _eraserSizeMm, Math.Clamp(value, AppSettings.MinEraserSizeMm, AppSettings.MaxEraserSizeMm));
            }
        }
    }

    /// <summary>The pen's back end touched the screen: erase whatever tool is active (FR-30).</summary>
    public void BeginInvertedEraser() => SetInverted(true);

    public void EndInvertedEraser() => SetInverted(false);

    /// <summary>The pen's barrel button went down: the lasso until it is released, whatever tool is active (FR-28).</summary>
    public void BeginBarrelSelection() => SetBarrelSelection(true);

    public void EndBarrelSelection() => SetBarrelSelection(false);

    /// <summary>Takes the remembered values from the settings (startup, or a change made in the settings).</summary>
    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Curve = settings.PressureCurve;
        Smoothing = settings.Smoothing;
        EraserMode = settings.EraserMode;
        EraserSizeMm = settings.EraserSizeMm;
        Color = settings.PenColor with { A = 255 };
        BaseWidthMm = settings.PenWidthMm;
        Palette.Palette = settings.CustomPalette;
        Highlighter.SelectColorCommand.Execute(settings.HighlighterColor);
        Highlighter.IsChisel = settings.HighlighterChisel;
        Highlighter.ChiselAngleDeg = settings.HighlighterAngleDeg;
        Highlighter.IsStraightLine = settings.HighlighterStraightLine;
    }

    /// <summary><paramref name="settings"/> with the values of the tools the settings remember.</summary>
    public AppSettings CaptureInto(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var highlighter = Highlighter.Settings;
        return settings with
        {
            PressureCurve = Curve,
            Smoothing = Smoothing,
            EraserMode = EraserMode,
            EraserSizeMm = EraserSizeMm,
            PenColor = Color,
            PenWidthMm = BaseWidthMm,
            CustomPalette = Palette.Palette,
            HighlighterColor = highlighter.StrokeColor,
            HighlighterChisel = highlighter.Chisel,
            HighlighterAngleDeg = highlighter.ChiselAngleDeg,
            HighlighterStraightLine = highlighter.StraightLine,
        };
    }

    private bool SetRemembered<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
        {
            return false;
        }

        RaiseRemembered();
        return true;
    }

    private void RaiseRemembered() => RememberedChanged?.Invoke(this, EventArgs.Empty);

    private void SetBarrelSelection(bool value)
    {
        if (_isBarrelSelection == value)
        {
            return;
        }

        _isBarrelSelection = value;
        OnPropertyChanged(nameof(IsBarrelSelection));
        OnPropertyChanged(nameof(EffectiveTool));
        OnPropertyChanged(nameof(IsDrawingTool));
    }

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
