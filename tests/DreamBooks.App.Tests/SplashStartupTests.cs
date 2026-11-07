using System.Windows;
using System.Windows.Controls;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.Views;
using ThemeMode = DreamBooks.Core.Settings.ThemeMode;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>The splash and the start-up order: file argument, restored session, start screen (NFR-01, FR-86).</summary>
[Collection(nameof(ThemeCollection))]
public sealed class SplashStartupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-splash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(@"C:\a.pdf", true, StartupAction.OpenFile)]
    [InlineData(@"C:\a.pdf", false, StartupAction.OpenFile)]
    [InlineData(null, true, StartupAction.Restore)]
    [InlineData(null, false, StartupAction.StartScreen)]
    [InlineData("", true, StartupAction.Restore)]
    [InlineData("   ", false, StartupAction.StartScreen)]
    public void The_start_up_action_is_the_file_then_the_restore_then_the_start_screen(string? file, bool restorable, StartupAction expected)
    {
        Assert.Equal(expected, StartupPlan.Decide(file, restorable));
    }

    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { @"C:\a.pdf" }, @"C:\a.pdf")]
    [InlineData(new[] { "--flag", @"C:\a.pdf", @"C:\b.pdf" }, @"C:\a.pdf")]
    [InlineData(new[] { "/x" }, null)]
    [InlineData(new[] { "", "  " }, null)]
    public void The_command_line_file_is_the_first_argument_that_is_not_a_flag(string[] args, string? expected)
    {
        Assert.Equal(expected, StartupPlan.FileFromArguments(args));
    }

    [Fact]
    public void The_splash_shows_the_icon_the_name_the_tagline_and_an_indicator_that_stops_with_reduced_motion()
    {
        WpfTestThread.Run(() =>
        {
            var splash = new SplashWindow();
            try
            {
                splash.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var texts = Find<TextBlock>(splash).Select(t => t.Text).ToList();
                Assert.Contains("Dream Books", texts);
                Assert.Contains(Strings.SplashTagline, texts);
                Assert.Equal("Leggi. Annota. Sogna.", Strings.SplashTagline);
                Assert.NotNull(Find<Image>(splash).Single().Source);
                Assert.Equal(WindowStyle.None, splash.WindowStyle);
                Assert.False(splash.ShowInTaskbar);

                splash.ReducedMotion = false;
                Assert.True(splash.Indicator.IsIndeterminate);
                splash.ReducedMotion = true;
                Assert.False(splash.Indicator.IsIndeterminate); // static with animations off (NFR-32)
            }
            finally
            {
                splash.Close();
            }
        });
    }

    [Theory]
    [InlineData(ThemeMode.System, true, true)]
    [InlineData(ThemeMode.System, false, false)]
    [InlineData(ThemeMode.Dark, false, true)]
    [InlineData(ThemeMode.Light, true, false)]
    public void The_splash_follows_the_theme_setting_and_Windows_only_when_the_setting_is_system(ThemeMode mode, bool windowsIsDark, bool expectedDark)
    {
        Assert.Equal(expectedDark, SplashTheme.IsDark(mode, windowsIsDark));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_splash_looks_light_or_dark_and_its_text_is_readable_either_way(bool dark)
    {
        WpfTestThread.Run(() =>
        {
            var splash = new SplashWindow(dark);
            try
            {
                var background = ((System.Windows.Media.SolidColorBrush)splash.Background).Color;
                var title = Find<TextBlock>(splash).First(t => t.Text == "Dream Books");
                var text = ((System.Windows.Media.SolidColorBrush)title.Foreground).Color;
                var tagline = Find<TextBlock>(splash).First(t => t.Text == Strings.SplashTagline);
                var subtext = ((System.Windows.Media.SolidColorBrush)tagline.Foreground).Color;

                Assert.Equal(dark, background.R < 128);
                Assert.True(ChoiceDialogTests.Contrast(text, background) >= 4.5);
                Assert.True(ChoiceDialogTests.Contrast(subtext, background) >= 4.5);
            }
            finally
            {
                splash.Close();
            }
        });
    }

    [Fact]
    public void The_splash_stays_on_top_until_it_is_closed_and_closing_twice_is_harmless()
    {
        // Device test of 4b: a splash that stepped back while the main window was still blank showed that window's black
        // first frame in light mode. It stays on top and goes only when the app has painted (App).
        WpfTestThread.Run(() =>
        {
            var splash = new SplashWindow(dark: false);
            splash.Show();
            Assert.True(splash.Topmost);
            Assert.False(splash.IsClosed);

            splash.CloseOnce();
            splash.CloseOnce(); // asked twice (the first start-up question, then the end of start-up)

            Assert.True(splash.IsClosed);
        });
    }

    [Fact]
    public void A_start_up_question_is_announced_so_the_splash_can_go_before_the_dialog_opens()
    {
        var announced = 0;
        void OnShowing() => announced++;
        ChoiceDialog.Showing += OnShowing;
        try
        {
            ChoiceDialog.NotifyShowing();
            ChoiceDialog.NotifyShowing();
        }
        finally
        {
            ChoiceDialog.Showing -= OnShowing;
        }

        Assert.Equal(2, announced);
    }

    [Fact]
    public void The_main_window_says_when_it_has_painted_its_first_frame()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow(null, null, NewRecents()) { ShowInTaskbar = false };
            try
            {
                Assert.False(window.FirstRendered.IsCompleted);
                window.Show();
                WpfTestThread.PumpUntil(() => window.FirstRendered.IsCompleted);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    private static IEnumerable<T> Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        void Walk(DependencyObject node)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                if (child is T match)
                {
                    found.Add(match);
                }

                Walk(child);
            }
        }

        Walk(root);
        return found;
    }

    [Fact]
    public void StartupCompleted_finishes_when_the_start_up_file_has_opened_with_no_minimum_time()
    {
        var path = Path.Combine(_dir, "a.pdf");
        Directory.CreateDirectory(_dir);
        TestPdfFactory.Write(path, (612, 792));
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow(null, null, NewRecents()) { StartupFile = path, ShowInTaskbar = false };
            try
            {
                Assert.False(window.StartupCompleted.IsCompleted);
                var started = DateTime.UtcNow;
                window.Show();
                WpfTestThread.PumpUntil(() => window.StartupCompleted.IsCompleted);

                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
                var vm = (ViewModels.MainViewModel)window.DataContext;
                Assert.Equal(path, vm.Session!.Path);
                Assert.Equal(Visibility.Collapsed, window.StartScreen.Visibility); // the start screen is skipped
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public void A_start_up_file_that_cannot_be_opened_still_completes_and_leaves_the_start_screen_with_the_error()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow(null, null, NewRecents()) { StartupFile = Path.Combine(_dir, "missing.pdf"), ShowInTaskbar = false };
            try
            {
                window.Show();
                WpfTestThread.PumpUntil(() => window.StartupCompleted.IsCompleted);

                var vm = (ViewModels.MainViewModel)window.DataContext;
                Assert.Null(vm.Session);
                Assert.NotNull(vm.ErrorMessage);
                Assert.Equal(Visibility.Visible, window.StartScreen.Visibility);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public void Without_a_file_or_a_journal_the_start_screen_shows_and_start_up_completes()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow(null, null, NewRecents()) { ShowInTaskbar = false };
            try
            {
                window.Show();
                WpfTestThread.PumpUntil(() => window.StartupCompleted.IsCompleted);

                Assert.Equal(Visibility.Visible, window.StartScreen.Visibility);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    private RecentFilesService NewRecents()
    {
        Directory.CreateDirectory(_dir);
        var recents = new RecentFilesService(new RecentFilesStore(Path.Combine(_dir, "state")), _ => true, new ManualScheduler());
        recents.Load();
        return recents;
    }
}
