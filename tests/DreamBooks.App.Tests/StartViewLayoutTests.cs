using System.Windows;

namespace DreamBooks.App.Tests;

/// <summary>The start screen's layout against the floating shell (FR-03, UI-03).</summary>
public sealed class StartViewLayoutTests
{
    [Fact]
    public void The_start_title_starts_below_the_main_menu_button() // FR-03, UI-03
    {
        WpfTestThread.Run(() =>
        {
            var start = (Thickness)Application.Current.FindResource("StartMargin");
            var menu = (Thickness)Application.Current.FindResource("MainMenuMargin");
            var button = (double)Application.Current.FindResource("TouchTarget");

            // In a narrow window the start screen begins at its left margin, under the menu button's column: its title
            // must begin below the button, with some air, not beside or under it.
            Assert.True(start.Left < menu.Left + button);
            Assert.True(start.Top >= menu.Top + button + 8, $"start top {start.Top}, menu bottom {menu.Top + button}");
        });
    }
}
