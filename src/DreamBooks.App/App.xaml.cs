using System.Windows;
using DreamBooks.App.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace DreamBooks.App;

public partial class App : Application
{
    private const string _resources = "/DreamBooks.App;component/Resources/";

    private ThemeService? _theme;

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
        _theme.SetMode(AppThemeMode.System); // the settings file (phase 4a, Task 1) will choose
        var window = new MainWindow(_theme);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _theme?.Dispose();
        base.OnExit(e);
    }
}
