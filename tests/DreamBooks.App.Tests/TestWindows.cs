using System.Windows;
using System.Windows.Media;
using TitleBar = Wpf.Ui.Controls.TitleBar;

namespace DreamBooks.App.Tests;

/// <summary>
/// Closing the windows the STA tests build. They are never shown, so WPF never raises Unloaded on their content when they
/// close; WPF UI's <see cref="TitleBar"/> subscribes to the static <c>ApplicationThemeManager.Changed</c> event and
/// leaves it only on Unloaded, so without that event a closed test window (with its view model and document) stays
/// alive for the rest of the run. A shown window that closes gets Unloaded from WPF (no leak in the app):
/// <see cref="CloseForTests"/> raises it on the title bars as WPF would, then closes.
/// </summary>
internal static class TestWindows
{
    public static void CloseForTests(this Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        foreach (var bar in TitleBars(window).ToList())
        {
            bar.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, bar));
        }

        window.Close();
    }

    private static IEnumerable<TitleBar> TitleBars(DependencyObject root)
    {
        var children = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        if (children == 0)
        {
            // Not laid out yet: the logical tree still holds the title bar.
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is TitleBar bar)
                {
                    yield return bar;
                }
                else
                {
                    foreach (var nested in TitleBars(child))
                    {
                        yield return nested;
                    }
                }
            }

            yield break;
        }

        for (var i = 0; i < children; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TitleBar bar)
            {
                yield return bar;
            }
            else
            {
                foreach (var nested in TitleBars(child))
                {
                    yield return nested;
                }
            }
        }
    }
}
