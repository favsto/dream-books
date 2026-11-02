using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DreamBooks.App.Services;

/// <summary>
/// The app's theme (section 3): light or dark, following Windows in <see cref="AppThemeMode.System"/> mode (and only
/// then), switchable by hand; the system accent colour, or an override. Applies WPF UI's theme and accent through its
/// <see cref="ApplicationThemeManager"/> and <see cref="ApplicationAccentColorManager"/>, then merges a fresh copy of
/// our colour tokens (Theme.Light.xaml or Theme.Dark.xaml) in place of the previous one, so views holding
/// DynamicResource references repaint and the accent brushes take the new accent. WPF UI's SystemThemeWatcher is not
/// used: it would follow Windows in a manual mode too.
/// Callable from any thread: the work runs on the application's dispatcher (synchronously from another thread);
/// Windows changes, raised on a system thread, are posted there. <see cref="ThemeChanged"/> is raised on that thread
/// after an effective change (theme or accent).
/// </summary>
internal sealed class ThemeService : IDisposable
{
    private const string _resources = "/DreamBooks.App;component/Resources/";

    private readonly ISystemThemeSource _system;
    private readonly Dispatcher _dispatcher;
    private AppThemeMode _mode = AppThemeMode.System;
    private Color? _accentOverride;
    private bool _applied;
    private bool _disposed;

    public ThemeService(ISystemThemeSource system)
    {
        ArgumentNullException.ThrowIfNull(system);
        _system = system;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _system.Changed += OnSystemChanged;
    }

    /// <summary>The theme or the accent changed (raised on the UI thread).</summary>
    public event EventHandler? ThemeChanged;

    public AppThemeMode Mode => _mode;

    /// <summary>The accent chosen in the settings, or null for the system accent.</summary>
    public Color? AccentOverride => _accentOverride;

    /// <summary>The theme applied last (Light or Dark); Unknown before the first apply.</summary>
    public ApplicationTheme EffectiveTheme { get; private set; } = ApplicationTheme.Unknown;

    /// <summary>The accent applied last.</summary>
    public Color EffectiveAccent { get; private set; }

    /// <summary>
    /// The main window's backdrop (Mica, or None with transparency effects off): WPF UI re-applies it to the main
    /// window on each theme change, so it must be the one in use.
    /// </summary>
    public WindowBackdropType Backdrop { get; set; } = WindowBackdropType.Mica;

    /// <summary>The pack URI of our colour tokens for <paramref name="theme"/> (light for anything but dark).</summary>
    internal static Uri ThemeDictionaryUri(ApplicationTheme theme) =>
        new(_resources + (theme == ApplicationTheme.Dark ? "Theme.Dark.xaml" : "Theme.Light.xaml"), UriKind.Relative);

    /// <summary>Chooses the mode and applies it (always, so the first call puts the theme in place).</summary>
    public void SetMode(AppThemeMode mode) => OnDispatcher(() =>
    {
        _mode = mode;
        Apply(force: true);
    });

    /// <summary>Sets or clears (null) the accent override and applies it.</summary>
    public void SetAccentOverride(Color? accent) => OnDispatcher(() =>
    {
        _accentOverride = accent;
        Apply(force: false);
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _system.Changed -= OnSystemChanged;
    }

    private void OnSystemChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Apply(force: false);
        }
        else
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Apply(force: false));
        }
    }

    private void OnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }

    /// <summary>
    /// Puts the wanted theme and accent in place when they differ from those applied (or when forced); a Windows change
    /// in a manual mode with an accent override changes nothing.
    /// </summary>
    private void Apply(bool force)
    {
        if (_disposed)
        {
            return;
        }

        var theme = _mode switch
        {
            AppThemeMode.Light => ApplicationTheme.Light,
            AppThemeMode.Dark => ApplicationTheme.Dark,
            _ => _system.IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light,
        };
        var accent = _accentOverride ?? _system.AccentColor;
        var changed = !_applied || theme != EffectiveTheme || accent != EffectiveAccent;
        if (!changed && !force)
        {
            return;
        }

        ApplicationThemeManager.Apply(theme, Backdrop, updateAccent: false);
        ApplicationAccentColorManager.Apply(accent, theme);
        ReplaceTokens(theme);
        _applied = true;
        EffectiveTheme = theme;
        EffectiveAccent = accent;
        if (changed)
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A fresh copy of our tokens for <paramref name="theme"/>, in the place of the current one.</summary>
    private static void ReplaceTokens(ApplicationTheme theme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var tokens = new ResourceDictionary { Source = ThemeDictionaryUri(theme) };
        for (var i = 0; i < dictionaries.Count; i++)
        {
            if (IsTokens(dictionaries[i]))
            {
                dictionaries[i] = tokens;
                return;
            }
        }

        dictionaries.Add(tokens);
    }

    private static bool IsTokens(ResourceDictionary dictionary) =>
        dictionary.Source?.OriginalString is { } source
        && (source.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase));
}
