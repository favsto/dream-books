using DreamBooks.Core.Settings;

namespace DreamBooks.App.Services;

/// <summary>Which look the splash takes: the theme the app will use (FR-110), Windows' own when the setting is "sistema".</summary>
internal static class SplashTheme
{
    public static bool IsDark(Core.Settings.ThemeMode mode, bool windowsIsDark) => mode switch
    {
        Core.Settings.ThemeMode.Dark => true,
        Core.Settings.ThemeMode.Light => false,
        _ => windowsIsDark,
    };
}
