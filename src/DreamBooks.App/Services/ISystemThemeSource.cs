using System.Windows.Media;

namespace DreamBooks.App.Services;

/// <summary>
/// The Windows side of the theme (section 3): whether apps should be dark and the system accent colour. Injectable so
/// the tests play Windows changes; <see cref="WindowsSystemThemeSource"/> reads the real settings.
/// </summary>
internal interface ISystemThemeSource
{
    /// <summary>True when Windows asks apps for the dark mode.</summary>
    bool IsDark { get; }

    /// <summary>The system accent colour.</summary>
    Color AccentColor { get; }

    /// <summary>The app mode or the accent may have changed; raised on any thread.</summary>
    event EventHandler? Changed;
}
