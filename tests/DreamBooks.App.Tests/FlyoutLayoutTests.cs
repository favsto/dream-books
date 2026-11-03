using System.Windows;
using System.Windows.Controls;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>The option flyouts show all their controls: nothing is cut off at the side (UI-02; device test of 4a).</summary>
public sealed class FlyoutLayoutTests
{
    [Theory]
    [InlineData(OptionsPanel.Pen, 1200)]
    [InlineData(OptionsPanel.Highlighter, 1200)]
    [InlineData(OptionsPanel.Eraser, 1200)]
    [InlineData(OptionsPanel.Pen, 420)]
    [InlineData(OptionsPanel.Highlighter, 420)]
    [InlineData(OptionsPanel.Eraser, 420)]
    public void No_control_of_a_flyout_lies_outside_its_width(OptionsPanel panel, double windowWidth)
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                var toolbar = window.FloatingToolbar;
                toolbar.AnimationsEnabled = () => false;
                toolbar.ApplyMotionSetting();
                ScrollRailTests.LayOut((FrameworkElement)window.Content, windowWidth, 900);
                var button = panel switch
                {
                    OptionsPanel.Pen => toolbar.PenButton,
                    OptionsPanel.Highlighter => toolbar.HighlighterButton,
                    _ => toolbar.EraserButton,
                };
                ToolbarViewTests.Invoke(button);
                if (panel != OptionsPanel.Pen)
                {
                    ToolbarViewTests.Invoke(button); // the first tap selects the tool, the second opens its options
                }

                ScrollRailTests.LayOut((FrameworkElement)window.Content, windowWidth, 900);

                var flyout = toolbar.FlyoutFor(panel);
                Assert.True(flyout.ActualWidth > 0);
                var bounds = new Rect(flyout.RenderSize);
                var cut = new List<string>();
                foreach (var element in ToolbarViewTests.Descendants(flyout).OfType<FrameworkElement>())
                {
                    if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element is ScrollViewer)
                    {
                        continue;
                    }

                    var box = element.TransformToAncestor(flyout).TransformBounds(new Rect(element.RenderSize));
                    if (box.Left < bounds.Left - 0.5 || box.Right > bounds.Right + 0.5)
                    {
                        cut.Add($"{element.GetType().Name} {element.Name} {box.Left:0}..{box.Right:0} of {bounds.Width:0}");
                    }
                }

                Assert.True(cut.Count == 0, string.Join("; ", cut));
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }
}
