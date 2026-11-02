using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DreamBooks.App.Services;
using Wpf.Ui.Controls;

namespace DreamBooks.App.Tests;

/// <summary>
/// The window shell (UI-01, UI-06, section 3): a WPF UI FluentWindow with Mica and a transparent title bar laid over
/// the document, theme switches at runtime, and the solid fallback with Windows transparency effects off (NFR-32).
/// Built on the STA test thread, never shown.
/// </summary>
[Collection(nameof(ThemeCollection))]
public sealed class MainWindowShellTests
{
    [Fact]
    public void The_window_is_a_FluentWindow_with_Mica_and_content_into_the_title_bar()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow { TransparencyEnabled = () => true, MicaSupported = () => true };
            try
            {
                window.ApplyBackdrop();
                Assert.IsAssignableFrom<FluentWindow>(window);
                Assert.Equal(WindowBackdropType.Mica, window.WindowBackdropType);
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(window.Background).Color); // Mica shows
                Assert.True(window.ExtendsContentIntoTitleBar);

                // The title bar shows the window title (the file name) and the window buttons...
                var titleBar = window.AppTitleBar;
                Assert.Equal(nameof(ViewModels.MainViewModel.WindowTitle),
                    BindingOperations.GetBinding(titleBar, TitleBar.TitleProperty)?.Path.Path);
                Assert.Equal(nameof(ViewModels.MainViewModel.WindowTitle),
                    BindingOperations.GetBinding(window, Window.TitleProperty)?.Path.Path);
                Assert.True(titleBar.ShowMinimize && titleBar.ShowMaximize && titleBar.ShowClose);

                // ...laid over the document, which fills the window from its top edge (under the title bar).
                var root = (FrameworkElement)window.Content;
                Lay(root, 1000, 700);
                Assert.Equal(0, window.DocumentViewer.TranslatePoint(new Point(0, 0), root).Y, 3);
                Assert.Equal(700, window.DocumentViewer.ActualHeight, 3);
                Assert.Equal(0, titleBar.TranslatePoint(new Point(0, 0), root).Y, 3);
                Assert.True(Panel.GetZIndex(titleBar) > Panel.GetZIndex(window.ShellContent));
                Assert.InRange(titleBar.ActualHeight, 1, ((Thickness)window.FindResource("TitleBarClearance")).Top);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Switching_the_theme_at_runtime_does_not_throw_and_swaps_the_resources()
    {
        WpfTestThread.Run(() =>
        {
            var system = new FakeSystemThemeSource();
            using var theme = new ThemeService(system);
            var window = new MainWindow(theme) { TransparencyEnabled = () => true, MicaSupported = () => true };
            try
            {
                var root = (FrameworkElement)window.Content;
                Lay(root, 1000, 700);
                var lightBackdrop = BackdropColor(window);
                var lightGlyph = ((SolidColorBrush)window.FindResource("GlyphBrush")).Color;

                theme.SetMode(AppThemeMode.Dark);
                WpfTestThread.Drain();
                Lay(root, 1000, 700);
                Assert.NotEqual(lightBackdrop, BackdropColor(window));
                Assert.NotEqual(lightGlyph, ((SolidColorBrush)window.FindResource("GlyphBrush")).Color);
                Assert.Equal(WindowBackdropType.Mica, window.WindowBackdropType); // the backdrop stays
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(window.Background).Color);

                theme.SetMode(AppThemeMode.Light);
                WpfTestThread.Drain();
                Lay(root, 1000, 700);
                Assert.Equal(lightBackdrop, BackdropColor(window));
            }
            finally
            {
                theme.SetMode(AppThemeMode.Light);
                window.Close();
            }
        });
    }

    [Fact]
    public void Without_transparency_the_backdrop_falls_back_to_a_solid_brush()
    {
        WpfTestThread.Run(() =>
        {
            var transparent = false;
            var mica = true;
            var window = new MainWindow { TransparencyEnabled = () => transparent, MicaSupported = () => mica };
            try
            {
                window.ApplyBackdrop();
                Assert.Equal(WindowBackdropType.None, window.WindowBackdropType);
                var background = Assert.IsType<SolidColorBrush>(window.Background);
                Assert.Equal(255, background.Color.A);
                Assert.Equal(1, background.Opacity);
                Assert.Same(window.FindResource("WindowBackgroundOpaqueBrush"), background);
                var pages = Assert.IsType<SolidColorBrush>(window.DocumentViewer.Viewport.Background);
                Assert.Same(window.FindResource("PageBackdropBrushOpaque"), pages);

                // Transparency effects back on: Mica again, the translucent grey around the pages.
                transparent = true;
                window.ApplyBackdrop();
                Assert.Equal(WindowBackdropType.Mica, window.WindowBackdropType);
                Assert.Same(window.FindResource("PageBackdropBrush"), window.DocumentViewer.Viewport.Background);
                Assert.True(window.DocumentViewer.Viewport.Background.Opacity < 1);
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(window.Background).Color);

                // Windows without Mica (Windows 10): the solid brushes even with transparency effects on.
                mica = false;
                window.ApplyBackdrop();
                Assert.Equal(WindowBackdropType.None, window.WindowBackdropType);
                Assert.Same(window.FindResource("WindowBackgroundOpaqueBrush"), window.Background);
                Assert.Same(window.FindResource("PageBackdropBrushOpaque"), window.DocumentViewer.Viewport.Background);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Color BackdropColor(MainWindow window) =>
        ((SolidColorBrush)window.DocumentViewer.Viewport.Background).Color;

    private static void Lay(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }
}
