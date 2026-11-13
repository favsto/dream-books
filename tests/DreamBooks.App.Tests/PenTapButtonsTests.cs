using System.Windows.Controls;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>
/// The pen's taps on the floating bars' buttons (UI-02, FR-51): one press and lift on an enabled button clicks it, once,
/// from the pen's own events, so the first tap after writing acts.
/// </summary>
public sealed class PenTapButtonsTests
{
    [Fact]
    public void A_press_and_lift_on_the_same_button_clicks_it_once() // UI-02
    {
        WithBar((taps, first, _) =>
        {
            var clicks = new List<Button>();
            taps.Click = clicks.Add;

            Assert.True(taps.PenDown(first, 7));
            Assert.Same(first, taps.Pressed);
            Assert.True(taps.PenUp(first, 7));

            Assert.Equal([first], clicks);
            Assert.Null(taps.Pressed);
            Assert.False(taps.PenUp(first, 7)); // a second lift without a press clicks nothing
            Assert.Single(clicks);
        });
    }

    [Fact]
    public void A_press_inside_a_button_s_content_presses_that_button()
    {
        WithBar((taps, first, _) =>
        {
            var glyph = new TextBlock();
            first.Content = glyph;
            first.UpdateLayout();

            Assert.Same(first, taps.ButtonAt(glyph));
        });
    }

    [Fact]
    public void A_lift_off_the_button_a_disabled_button_or_another_pen_does_not_click()
    {
        WithBar((taps, first, second) =>
        {
            var clicks = new List<Button>();
            taps.Click = clicks.Add;

            taps.PenDown(first, 7);
            Assert.False(taps.PenUp(second, 7)); // slid onto another button
            taps.PenDown(first, 7);
            Assert.False(taps.PenUp(null, 7)); // lifted off the bar
            taps.PenDown(first, 7);
            Assert.False(taps.PenUp(first, 8)); // another pen

            second.IsEnabled = false;
            Assert.False(taps.PenDown(second, 7));
            Assert.False(taps.PenUp(second, 7));

            Assert.Empty(clicks);
        });
    }

    [Fact]
    public void The_toolbar_and_the_lasso_bar_click_their_buttons_from_the_pen() // UI-02, FR-51
    {
        WpfTestThread.Run(() =>
        {
            var toolbar = new ToolbarView();
            var none = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
            toolbar.DataContext = new ToolbarViewModel(new ToolsViewModel(), none, none, () => null, new ManualScheduler(), () => TimeSpan.Zero);
            var clicks = new List<Button>();
            toolbar.PenTaps.Click = clicks.Add;

            var eraser = toolbar.ButtonFor(ToolbarTool.Eraser);
            Assert.True(toolbar.PenTaps.PenDown(eraser, 1));
            Assert.True(toolbar.PenTaps.PenUp(eraser, 1));
            Assert.Equal([eraser], clicks);

            var bar = new SelectionActionBar();
            Assert.Same(bar.DeleteButton, bar.PenTaps.ButtonAt(bar.DeleteButton));
        });
    }

    private static void WithBar(Action<PenTapButtons, Button, Button> test)
    {
        WpfTestThread.Run(() =>
        {
            var first = new Button();
            var second = new Button();
            var root = new StackPanel { Children = { first, second } };
            root.Measure(new System.Windows.Size(200, 100));
            root.Arrange(new System.Windows.Rect(0, 0, 200, 100));
            test(PenTapButtons.Attach(root), first, second);
        });
    }
}
