using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DreamBooks.App.Resources;
using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>The title bar's two pills: the file name and the window buttons, readable in both themes (UI-06).</summary>
[Collection(nameof(ThemeCollection))]
public sealed class TitleBarPillsTests
{
    private static void WithWindow(Action<MainWindow, FrameworkElement> test, bool transparency = true) =>
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow { TransparencyEnabled = () => transparency, MicaSupported = () => true };
            try
            {
                window.ApplyBackdrop();
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1000, 700));
                root.Arrange(new Rect(0, 0, 1000, 700));
                root.UpdateLayout();
                window.StyleWindowButtons();
                test(window, root);
            }
            finally
            {
                window.CloseForTests();
            }
        });

    [Fact]
    public void The_window_buttons_are_named_in_italian_40_high_and_inside_their_pill()
    {
        WithWindow((window, root) =>
        {
            var buttons = TitleBarPenInput.Buttons(window.AppTitleBar).ToList();
            Assert.Equal([Strings.TitleBarMinimize, Strings.TitleBarMaximize, Strings.TitleBarClose], buttons.Select(AutomationProperties.GetName));
            Assert.Equal(["Riduci a icona", "Ingrandisci", "Chiudi"], buttons.Select(AutomationProperties.GetName));
            Assert.All(buttons, b => Assert.Equal(40, b.Height));

            var pill = window.TitleButtonsPill.TransformToAncestor(root).TransformBounds(new Rect(window.TitleButtonsPill.RenderSize));
            foreach (var b in buttons)
            {
                var bounds = b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize));
                Assert.True(pill.Contains(bounds.TopLeft) && pill.Contains(new Point(bounds.Right - 0.5, bounds.Bottom - 0.5)), $"{b.Name} {bounds} outside {pill}");
            }
        });
    }

    [Fact]
    public void The_pills_never_take_input_and_the_name_pill_carries_the_file_name()
    {
        WithWindow((window, _) =>
        {
            Assert.False(window.TitleNamePill.IsHitTestVisible);
            Assert.False(window.TitleButtonsPill.IsHitTestVisible);
            Assert.False(window.TitleNamePill.IsOpaque);
            Assert.Equal(40, window.TitleNamePill.ActualHeight);
            Assert.Equal(new CornerRadius(20), window.TitleNamePill.DiffuseLayer.CornerRadius);
        });
    }

    [Fact]
    public void Without_transparency_the_pills_are_opaque()
    {
        WithWindow((window, _) =>
        {
            Assert.True(window.TitleNamePill.IsOpaque);
            Assert.True(window.TitleButtonsPill.IsOpaque);
        }, transparency: false);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void The_bar_text_and_button_glyphs_use_the_theme_text_that_contrasts_with_the_pill_tint(string theme)
    {
        WpfTestThread.Run(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DreamBooks.App;component/Resources/Theme.xaml", UriKind.Relative) });
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/DreamBooks.App;component/Resources/Theme.{theme}.xaml", UriKind.Relative) });
            var text = ((SolidColorBrush)resources["GlyphBrush"]).Color;
            var tint = ((SolidColorBrush)resources["PanelTintBrushOpaque"]).Color;
            Assert.True(ChoiceDialogTests.Contrast(text, tint) >= 4.5);
        });

        WithWindow((window, _) =>
        {
            Assert.Same(window.FindResource("GlyphBrush"), window.AppTitleBar.Foreground);
            Assert.Same(window.FindResource("GlyphBrush"), window.AppTitleBar.ButtonsForeground);
            Assert.All(TitleBarPenInput.Buttons(window.AppTitleBar), b => Assert.Equal(((SolidColorBrush)window.FindResource("GlyphBrush")).Color, ((SolidColorBrush)b.Foreground).Color));
        });
    }
}
