using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Resources;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The highlighter options (FR-41, FR-44, FR-46): six colour swatches, round or chisel tip, the chisel angle
/// (0..180 degrees) and the straight-line switch. Every real change builds a new <see cref="Settings"/> and raises
/// <see cref="SettingsChanged"/>; <see cref="MainViewModel"/> hands it to the document's page editors.
/// </summary>
public sealed class HighlighterOptionsViewModel : ObservableObject
{
    /// <summary>The smallest chisel angle in degrees (FR-46).</summary>
    public const double MinChiselAngleDeg = 0;

    /// <summary>The largest chisel angle in degrees (FR-46).</summary>
    public const double MaxChiselAngleDeg = 180;

    private HighlighterSettings _settings = HighlighterSettings.Default;

    public HighlighterOptionsViewModel()
    {
        Swatches =
        [
            new(HighlighterPresets.Yellow, Strings.HighlighterYellow),
            new(HighlighterPresets.Green, Strings.HighlighterGreen),
            new(HighlighterPresets.LightBlue, Strings.HighlighterLightBlue),
            new(HighlighterPresets.Pink, Strings.HighlighterPink),
            new(HighlighterPresets.Orange, Strings.HighlighterOrange),
            new(HighlighterPresets.Purple, Strings.HighlighterPurple),
        ];
        SelectColorCommand = new RelayCommand<InkColor>(c => Update(_settings with { Color = c with { A = HighlighterPresets.Alpha } }));
    }

    /// <summary>Raised after <see cref="Settings"/> changed.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>The six presets, in the order of <see cref="HighlighterPresets.All"/>.</summary>
    public IReadOnlyList<HighlighterSwatch> Swatches { get; }

    /// <summary>Selects a swatch's colour; the highlighter alpha is kept whatever the colour's own alpha.</summary>
    public IRelayCommand<InkColor> SelectColorCommand { get; }

    /// <summary>The settings new highlighter strokes are committed with.</summary>
    public HighlighterSettings Settings => _settings;

    public InkColor Color => _settings.Color;

    public bool IsChisel
    {
        get => _settings.Chisel;
        set => Update(_settings with { Chisel = value });
    }

    public bool IsRound
    {
        get => !_settings.Chisel;
        set => Update(_settings with { Chisel = !value });
    }

    /// <summary>The chisel nib's angle in degrees, clamped to 0..180; NaN is ignored.</summary>
    public double ChiselAngleDeg
    {
        get => _settings.ChiselAngleDeg;
        set
        {
            if (!double.IsNaN(value))
            {
                Update(_settings with { ChiselAngleDeg = Math.Clamp(value, MinChiselAngleDeg, MaxChiselAngleDeg) });
            }
        }
    }

    public bool IsStraightLine
    {
        get => _settings.StraightLine;
        set => Update(_settings with { StraightLine = value });
    }

    private void Update(HighlighterSettings next)
    {
        if (next == _settings)
        {
            return;
        }

        var previous = _settings;
        _settings = next;
        OnPropertyChanged(nameof(Settings));
        if (previous.Color != next.Color)
        {
            OnPropertyChanged(nameof(Color));
        }

        if (previous.Chisel != next.Chisel)
        {
            OnPropertyChanged(nameof(IsChisel));
            OnPropertyChanged(nameof(IsRound));
        }

        if (previous.ChiselAngleDeg != next.ChiselAngleDeg)
        {
            OnPropertyChanged(nameof(ChiselAngleDeg));
        }

        if (previous.StraightLine != next.StraightLine)
        {
            OnPropertyChanged(nameof(IsStraightLine));
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
