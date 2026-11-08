using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Services;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Settings;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The settings panel (FR-110): exactly the values 4a only exposed through the toolbar and its panels, in one place:
/// theme, accent colour, eraser mode, pressure curve, smoothing, "Toolbar fissa" and the toolbar's edge. Every property
/// writes through the <see cref="SettingsService"/> (applied at once, saved half a second later) and follows changes made
/// elsewhere. There is no finger-drawing option: fingers never draw.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private AppSettings _shown;
    private bool _isOpen;

    internal SettingsViewModel(SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _shown = settings.Current;
        settings.Changed += (_, _) => Follow();
        OpenCommand = new RelayCommand(() => IsOpen = true);
        CloseCommand = new RelayCommand(() => IsOpen = false);
        ClearAccentCommand = new RelayCommand(() => _settings.Update(s => s with { AccentOverride = null }));
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>The app's name and version, shown at the bottom of the settings.</summary>
    public string VersionText { get; } = string.Format(
        System.Globalization.CultureInfo.CurrentCulture, Resources.Strings.SettingsVersionFormat, AppVersion.Current);

    public IRelayCommand OpenCommand { get; }

    public IRelayCommand CloseCommand { get; }

    /// <summary>Back to the system accent colour.</summary>
    public IRelayCommand ClearAccentCommand { get; }

    public ThemeMode ThemeMode
    {
        get => _shown.ThemeMode;
        set => _settings.Update(s => s with { ThemeMode = value });
    }

    /// <summary>
    /// The accent override as <c>#RRGGBB</c>, or null for the system accent. Text that is not a colour changes nothing and
    /// the property is announced again, so a text box shows the valid value.
    /// </summary>
    public string? AccentHex
    {
        get => _shown.AccentOverride;
        set
        {
            if (HexColor.TryNormalize(value, out var normalized))
            {
                _settings.Update(s => s with { AccentOverride = normalized });
            }
            else
            {
                OnPropertyChanged();
            }
        }
    }

    public EraserMode EraserMode
    {
        get => _shown.EraserMode;
        set => _settings.Update(s => s with { EraserMode = value });
    }

    public PressureCurve PressureCurve
    {
        get => _shown.PressureCurve;
        set => _settings.Update(s => s with { PressureCurve = value });
    }

    public SmoothingLevel Smoothing
    {
        get => _shown.Smoothing;
        set => _settings.Update(s => s with { Smoothing = value });
    }

    public bool ToolbarFixed
    {
        get => _shown.ToolbarFixed;
        set => _settings.Update(s => s with { ToolbarFixed = value });
    }

    public ToolbarEdge ToolbarEdge
    {
        get => _shown.ToolbarEdge;
        set => _settings.Update(s => s with { ToolbarEdge = value });
    }

    private void Follow()
    {
        var previous = _shown;
        _shown = _settings.Current;
        Raise(previous.ThemeMode != _shown.ThemeMode, nameof(ThemeMode));
        Raise(previous.AccentOverride != _shown.AccentOverride, nameof(AccentHex));
        Raise(previous.EraserMode != _shown.EraserMode, nameof(EraserMode));
        Raise(previous.PressureCurve != _shown.PressureCurve, nameof(PressureCurve));
        Raise(previous.Smoothing != _shown.Smoothing, nameof(Smoothing));
        Raise(previous.ToolbarFixed != _shown.ToolbarFixed, nameof(ToolbarFixed));
        Raise(previous.ToolbarEdge != _shown.ToolbarEdge, nameof(ToolbarEdge));
    }

    private void Raise(bool changed, string property)
    {
        if (changed)
        {
            OnPropertyChanged(property);
        }
    }
}
