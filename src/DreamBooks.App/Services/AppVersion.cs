using System.Reflection;

namespace DreamBooks.App.Services;

/// <summary>
/// The version of the running app, read from the build (the <c>Version</c> in <c>Directory.Build.props</c>), so what the
/// settings show is always the number the installer carries.
/// </summary>
public static class AppVersion
{
    /// <summary>The running app's version, for example <c>0.5.0</c>.</summary>
    public static string Current { get; } = Format(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// The version as shown to the user: without the build metadata the SDK appends after a plus sign (the commit);
    /// empty when there is none.
    /// </summary>
    public static string Format(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return string.Empty;
        }

        var text = informationalVersion.Trim();
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? text[..plus] : text;
    }
}
