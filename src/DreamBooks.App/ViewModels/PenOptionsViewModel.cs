using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The pen's option panel (UI-02, FR-22, FR-25): the eight preset colours, the custom palette shared with the
/// highlighter (a colour from the free picker is added to it and selected), three preset widths and the width slider.
/// Everything reads and writes <see cref="ToolsViewModel"/>, which the settings remember.
/// </summary>
public sealed class PenOptionsViewModel : ObservableObject
{
    private readonly ToolsViewModel _tools;

    public PenOptionsViewModel(ToolsViewModel tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
        Presets =
        [
            new(Palette.Black, Strings.PenBlack),
            new(Palette.DarkBlue, Strings.PenDarkBlue),
            new(Palette.Blue, Strings.PenBlue),
            new(Palette.Red, Strings.PenRed),
            new(Palette.Green, Strings.PenGreen),
            new(Palette.Orange, Strings.PenOrange),
            new(Palette.Purple, Strings.PenPurple),
            new(Palette.Grey, Strings.PenGrey),
        ];
        SelectColorCommand = new RelayCommand<InkColor>(c => _tools.Color = Opaque(c));
        AddCustomColorCommand = new RelayCommand<InkColor>(c =>
        {
            _tools.Palette.Add(c);
            _tools.Color = Opaque(c);
        });
        RemoveCustomColorCommand = new RelayCommand<InkColor>(_tools.Palette.Remove);
        SelectWidthCommand = new RelayCommand<double>(w => WidthMm = w);
        tools.PropertyChanged += OnToolsChanged;
        tools.Palette.PaletteChanged += (_, _) => OnPropertyChanged(nameof(CustomColors));
    }

    /// <summary>The eight presets (FR-25), in <see cref="Palette.Colors"/> order.</summary>
    public IReadOnlyList<ColorSwatch> Presets { get; }

    /// <summary>The shared custom colours, oldest first, opaque.</summary>
    public IReadOnlyList<InkColor> CustomColors => _tools.Palette.ColorsWithAlpha(255);

    /// <summary>The three preset widths in millimetres (FR-22: fine, medium, bold).</summary>
    public IReadOnlyList<double> WidthPresets { get; } = [0.5, 1.0, 2.0];

    public double MinWidthMm => Core.Settings.AppSettings.MinPenWidthMm;

    public double MaxWidthMm => Core.Settings.AppSettings.MaxPenWidthMm;

    public InkColor Color => _tools.Color;

    /// <summary>The base width (the slider), clamped to 0.25..5 mm (FR-22).</summary>
    public double WidthMm
    {
        get => _tools.BaseWidthMm;
        set => _tools.BaseWidthMm = value;
    }

    /// <summary>Selects a preset or custom colour.</summary>
    public IRelayCommand<InkColor> SelectColorCommand { get; }

    /// <summary>A colour from the free picker: added to the shared palette and selected.</summary>
    public IRelayCommand<InkColor> AddCustomColorCommand { get; }

    /// <summary>Removes a colour from the shared palette (long press); the pen keeps its colour.</summary>
    public IRelayCommand<InkColor> RemoveCustomColorCommand { get; }

    /// <summary>Selects one of <see cref="WidthPresets"/>.</summary>
    public IRelayCommand<double> SelectWidthCommand { get; }

    private static InkColor Opaque(InkColor color) => color with { A = 255 };

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ToolsViewModel.Color):
                OnPropertyChanged(nameof(Color));
                break;
            case nameof(ToolsViewModel.BaseWidthMm):
                OnPropertyChanged(nameof(WidthMm));
                break;
        }
    }
}
