using System.Windows;
using System.Windows.Media;
using DreamBooks.App.Services;
using DreamBooks.Core.Settings;
using DreamBooks.Pdf;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace DreamBooks.App;

public partial class App : Application
{
    private const string _resources = "/DreamBooks.App;component/Resources/";

    // At most this long the exit waits for the settings write (a small file; never blocks the exit for long).
    private static readonly TimeSpan _flushTimeout = TimeSpan.FromSeconds(2);

    private ThemeService? _theme;
    private SettingsService? _settings;

    public App() => AddThemeResources(Resources);

    /// <summary>
    /// The application's resources, in order: WPF UI's theme colours (swapped by its ApplicationThemeManager) and
    /// implicit control styles, our theme-independent tokens (Theme.xaml) and our light colours (Theme.Light.xaml,
    /// swapped for Theme.Dark.xaml by <see cref="ThemeService"/>). Later dictionaries win on the same key.
    /// </summary>
    internal static void AddThemeResources(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        resources.MergedDictionaries.Add(new ControlsDictionary());
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(_resources + "Theme.xaml", UriKind.Relative) });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeService.ThemeDictionaryUri(ApplicationTheme.Light) });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _theme = new ThemeService(new WindowsSystemThemeSource());
        _settings = new SettingsService(new AppSettingsStore(AppSettingsStore.DefaultRootDirectory()), new DispatcherDelayScheduler());
        _settings.Load(); // one small file; anything unreadable gives the defaults
        var theme = _theme;
        _settings.AttachTheme(s =>
        {
            theme.SetMode(ToAppThemeMode(s.ThemeMode));
            theme.SetAccentOverride(ToAccent(s.AccentOverride));
        });
        var window = new MainWindow(_theme, _settings);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settings?.Flush(_flushTimeout);
        _theme?.Dispose();
        base.OnExit(e);
    }

    // Core's ThemeMode, qualified: inside App the name is Application.ThemeMode.
    private static AppThemeMode ToAppThemeMode(Core.Settings.ThemeMode mode) => mode switch
    {
        Core.Settings.ThemeMode.Light => AppThemeMode.Light,
        Core.Settings.ThemeMode.Dark => AppThemeMode.Dark,
        _ => AppThemeMode.System,
    };

    private static Color? ToAccent(string? hex) =>
        HexColor.TryParse(hex, 255, out var color) ? Color.FromRgb(color.R, color.G, color.B) : null;
}
