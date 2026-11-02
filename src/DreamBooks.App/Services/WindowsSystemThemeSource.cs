using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace DreamBooks.App.Services;

/// <summary>
/// The real Windows theme: the app mode from
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme</c> (0 means dark; anything
/// unreadable counts as light, the Windows default) and the accent from WPF UI (the DWM colorization colour). Changes
/// arrive through <see cref="SystemEvents.UserPreferenceChanged"/> on a system thread.
/// </summary>
internal sealed class WindowsSystemThemeSource : ISystemThemeSource, IDisposable
{
    private const string _keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string _valueName = "AppsUseLightTheme";

    private EventHandler? _changed;

    public bool IsDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
                return IsDarkValue(key?.GetValue(_valueName));
            }
#pragma warning disable CA1031 // A setting that cannot be read leaves the default (light).
            catch (Exception)
#pragma warning restore CA1031
            {
                return false;
            }
        }
    }

    public Color AccentColor => ApplicationAccentColorManager.GetColorizationColor();

    public event EventHandler? Changed
    {
        add
        {
            if (_changed is null)
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }

            _changed += value;
        }

        remove
        {
            _changed -= value;
            if (_changed is null)
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }
        }
    }

    /// <summary>The registry value: a DWORD 0 means dark; missing or anything else means light.</summary>
    internal static bool IsDarkValue(object? value) => value is int number && number == 0;

    public void Dispose()
    {
        if (_changed is not null)
        {
            _changed = null;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // The app mode arrives as General ("ImmersiveColorSet"), the accent as General or Color.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            _changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
