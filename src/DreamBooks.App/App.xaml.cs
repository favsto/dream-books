using System.IO;
using System.Windows;
using System.Windows.Media;
using DreamBooks.App.Services;
using DreamBooks.App.Views;
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
    private RecentFilesService? _recents;

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

        // The splash first, before the heavy start-up work, so the user sees the app at once; it closes when the window
        // is ready (no minimum time).
        // The look of the theme the app will use: the setting (one small file), or Windows when it says "sistema".
        var dark = SplashTheme.IsDark(
            new AppSettingsStore(AppSettingsStore.DefaultRootDirectory()).Load().ThemeMode,
            new WindowsSystemThemeSource().IsDark);
        var splash = new SplashWindow(dark);
        splash.Show();
        try
        {
            StartMainWindow(e, splash);
        }
        catch
        {
            splash.CloseOnce(); // never leave a splash over an app that failed to start
            throw;
        }
    }

    private void StartMainWindow(StartupEventArgs e, SplashWindow splash)
    {
        _theme = new ThemeService(new WindowsSystemThemeSource());
        _settings = new SettingsService(new AppSettingsStore(AppSettingsStore.DefaultRootDirectory()), new DispatcherDelayScheduler());
        _settings.Load(); // one small file; anything unreadable gives the defaults
        var theme = _theme;
        _settings.AttachTheme(s =>
        {
            theme.SetMode(ToAppThemeMode(s.ThemeMode));
            theme.SetAccentOverride(ToAccent(s.AccentOverride));
        });
        _recents = new RecentFilesService(new RecentFilesStore(RecentFilesStore.DefaultRootDirectory()), File.Exists, new DispatcherDelayScheduler());
        _recents.Load(); // one small file; anything unreadable gives an empty list
        var covers = new CoverService(new CoverStore(RecentFilesStore.DefaultRootDirectory()));
        var window = new MainWindow(_theme, _settings, _recents, covers);
        MainWindow = window;
        window.StartupFile = StartupPlan.FileFromArguments(e.Args);
        // The splash goes when start-up is done and the window has painted its first frame (no blank frame shows), or before a
        // start-up question opens (it must not hide under the splash). Until then it stays on top.
        void CloseSplash() => splash.CloseOnce();
        ChoiceDialog.Showing += CloseSplash;
        Task.WhenAll(window.StartupCompleted, window.FirstRendered).ContinueWith(
            _ =>
            {
                ChoiceDialog.Showing -= CloseSplash;
                splash.CloseOnce();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settings?.Flush(_flushTimeout);
        _recents?.Flush(_flushTimeout);
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
