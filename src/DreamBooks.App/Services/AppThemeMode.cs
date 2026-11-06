namespace DreamBooks.App.Services;

/// <summary>The app's theme choice (section 3): follow Windows, or light or dark by hand.</summary>
internal enum AppThemeMode
{
    /// <summary>Light or dark as Windows says ("Choose your app mode"), following its changes.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}
