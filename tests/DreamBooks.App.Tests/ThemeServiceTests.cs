using System.Windows;
using System.Windows.Media;
using DreamBooks.App.Services;
using Wpf.Ui.Appearance;

namespace DreamBooks.App.Tests;

/// <summary>
/// The theme service (section 3: light and dark, following Windows, switchable by hand; the accent colour with an
/// override) on the shared WPF test thread, whose application resources are those of App.xaml. Each test puts the
/// light theme and the system accent back before it returns, so the other tests always see the default look.
/// </summary>
[Collection(nameof(ThemeCollection))]
public sealed class ThemeServiceTests
{
    private static readonly Color _lightBackdrop = (Color)ColorConverter.ConvertFromString("#E5E5E5");
    private static readonly Color _darkBackdrop = (Color)ColorConverter.ConvertFromString("#1A1A1A");

    [Fact]
    public void ThemeService_applies_light_dark_and_system()
    {
        WpfTestThread.Run(() =>
        {
            var system = new FakeSystemThemeSource { IsDark = true };
            using var service = new ThemeService(system);
            try
            {
                service.SetMode(AppThemeMode.Light);
                AssertApplied(service, ApplicationTheme.Light, _lightBackdrop);

                service.SetMode(AppThemeMode.Dark);
                AssertApplied(service, ApplicationTheme.Dark, _darkBackdrop);

                service.SetMode(AppThemeMode.System); // Windows is dark
                Assert.Equal(AppThemeMode.System, service.Mode);
                AssertApplied(service, ApplicationTheme.Dark, _darkBackdrop);

                system.IsDark = false;
                service.SetMode(AppThemeMode.Light);
                service.SetMode(AppThemeMode.System); // Windows is light
                AssertApplied(service, ApplicationTheme.Light, _lightBackdrop);
            }
            finally
            {
                Restore(service);
            }
        });
    }

    [Fact]
    public void ThemeService_follows_a_system_change_only_in_system_mode()
    {
        WpfTestThread.Run(() =>
        {
            var system = new FakeSystemThemeSource { IsDark = false };
            using var service = new ThemeService(system);
            var changes = 0;
            service.ThemeChanged += (_, _) => changes++;
            try
            {
                service.SetMode(AppThemeMode.System);
                changes = 0;

                system.IsDark = true;
                system.Raise();
                WpfTestThread.Drain();
                AssertApplied(service, ApplicationTheme.Dark, _darkBackdrop);
                Assert.Equal(1, changes);

                // From another thread (SystemEvents raise there): applied on the UI thread.
                system.IsDark = false;
                Task.Run(system.Raise).Wait();
                WpfTestThread.PumpUntil(() => service.EffectiveTheme == ApplicationTheme.Light);
                AssertApplied(service, ApplicationTheme.Light, _lightBackdrop);
                Assert.Equal(2, changes);

                // A manual mode ignores Windows.
                service.SetMode(AppThemeMode.Light);
                changes = 0;
                system.IsDark = true;
                system.Raise();
                WpfTestThread.Drain();
                AssertApplied(service, ApplicationTheme.Light, _lightBackdrop);
                Assert.Equal(0, changes);

                service.SetMode(AppThemeMode.Dark);
                changes = 0;
                system.IsDark = false;
                system.Raise();
                WpfTestThread.Drain();
                AssertApplied(service, ApplicationTheme.Dark, _darkBackdrop);
                Assert.Equal(0, changes);
            }
            finally
            {
                Restore(service);
            }
        });
    }

    [Fact]
    public void Accent_override_is_applied_and_cleared()
    {
        WpfTestThread.Run(() =>
        {
            var systemAccent = Color.FromRgb(0x00, 0x78, 0xD4);
            var chosen = Color.FromRgb(0xC2, 0x18, 0x5B);
            var system = new FakeSystemThemeSource { IsDark = false, AccentColor = systemAccent };
            using var service = new ThemeService(system);
            var changes = 0;
            service.ThemeChanged += (_, _) => changes++;
            try
            {
                service.SetMode(AppThemeMode.Light);
                Assert.Equal(systemAccent, service.EffectiveAccent);
                Assert.Equal(systemAccent, ApplicationAccentColorManager.SystemAccent);
                changes = 0;

                service.SetAccentOverride(chosen);
                Assert.Equal(chosen, service.AccentOverride);
                Assert.Equal(chosen, service.EffectiveAccent);
                Assert.Equal(chosen, ApplicationAccentColorManager.SystemAccent);
                Assert.Equal(chosen, (Color)Application.Current.Resources["SystemAccentColor"]);
                AssertAccentBrushesFollowWpfUi();
                Assert.Equal(1, changes);

                // A system accent change does not touch the override...
                system.AccentColor = Colors.Green;
                system.Raise();
                WpfTestThread.Drain();
                Assert.Equal(chosen, ApplicationAccentColorManager.SystemAccent);

                // ...and clearing it goes back to the system accent.
                service.SetAccentOverride(null);
                Assert.Null(service.AccentOverride);
                Assert.Equal(Colors.Green, service.EffectiveAccent);
                Assert.Equal(Colors.Green, ApplicationAccentColorManager.SystemAccent);
                AssertAccentBrushesFollowWpfUi();
            }
            finally
            {
                Restore(service);
            }
        });
    }

    [Fact]
    public void The_service_stops_listening_to_windows_when_disposed()
    {
        var system = new FakeSystemThemeSource();
        WpfTestThread.Run(() =>
        {
            var service = new ThemeService(system);
            Assert.Equal(1, system.Subscribers);
            service.Dispose();
            Assert.Equal(0, system.Subscribers);
        });
    }

    private static void AssertApplied(ThemeService service, ApplicationTheme theme, Color backdrop)
    {
        Assert.Equal(theme, service.EffectiveTheme);
        Assert.Equal(theme, ApplicationThemeManager.GetAppTheme());
        Assert.Equal(backdrop, (Color)Application.Current.FindResource("PageBackdropColor"));
    }

    /// <summary>Our accent brushes (rail knob, eraser cursor...) take WPF UI's accent, override included.</summary>
    private static void AssertAccentBrushesFollowWpfUi()
    {
        var primary = (Color)Application.Current.Resources["SystemAccentColorPrimary"];
        foreach (var key in new[] { "RailKnobBrush", "EraserCursorOutline", "EdgePanelTabIndicatorBrush", "PagesCurrentRingBrush" })
        {
            Assert.Equal(primary, ((SolidColorBrush)Application.Current.FindResource(key)).Color);
        }
    }

    private static void Restore(ThemeService service)
    {
        service.SetAccentOverride(null);
        service.SetMode(AppThemeMode.Light);
    }
}
