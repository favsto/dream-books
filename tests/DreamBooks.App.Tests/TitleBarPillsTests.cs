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
            Assert.All(buttons, b => Assert.Equal((double)window.FindResource("TitleBarHeight"), b.Height));
            var close = buttons[2].TransformToAncestor(root).TransformBounds(new Rect(buttons[2].RenderSize));
            Assert.Equal(0, close.Top, 3); // the corner of the window closes it
            Assert.Equal(root.ActualWidth, close.Right, 3);

            var pill = window.TitleButtonsPill.TransformToAncestor(root).TransformBounds(new Rect(window.TitleButtonsPill.RenderSize));
            foreach (var b in buttons)
            {
                var bounds = b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize));
                Assert.True(bounds.Left >= pill.Left - 0.5 && bounds.Right <= pill.Right + 0.5 && bounds.Top <= pill.Top, $"{b.Name} {bounds} outside {pill}");
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
    public void The_scroll_rail_and_the_edge_panel_start_below_the_title_bar()
    {
        WithWindow((window, root) =>
        {
            var clearance = ((Thickness)window.FindResource("TitleBarClearance")).Top;
            var rail = window.DocumentViewer.Rail.TransformToAncestor(root).TransformBounds(new Rect(window.DocumentViewer.Rail.RenderSize));
            Assert.True(rail.Top >= clearance - 0.5, $"the rail starts at {rail.Top}");

            window.MarksPanel.OpenTabCommand.Execute(MainWindow.MarksTab);
            root.UpdateLayout();
            var sheet = window.MarksPanel.Sheet.TransformToAncestor(root).TransformBounds(new Rect(window.MarksPanel.Sheet.RenderSize));
            Assert.True(sheet.Top >= clearance - 0.5, $"the panel starts at {sheet.Top}");
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
