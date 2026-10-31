using Microsoft.Win32;

namespace DreamBooks.App.Services;

/// <summary>
/// The Windows "Transparency effects" setting (Settings > Personalisation > Colours), honoured by the app's
/// semi-transparent overlays (NFR-32). Read from
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency</c>; anything unreadable
/// counts as on (the Windows default). Changes arrive through <see cref="SystemEvents.UserPreferenceChanged"/>.
/// </summary>
internal static class TransparencySetting
{
    private const string _keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string _valueName = "EnableTransparency";

    /// <summary>True unless Windows says transparency effects are off; never throws.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return Parse(key?.GetValue(_valueName));
        }
#pragma warning disable CA1031 // A setting that cannot be read leaves the default (on).
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }
    }

    /// <summary>The registry value: a DWORD 0 means off; missing or anything else means on.</summary>
    public static bool Parse(object? value) => value is not int number || number != 0;
}
