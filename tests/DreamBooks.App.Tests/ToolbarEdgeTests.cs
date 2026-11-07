using System.Windows;
using System.Windows.Controls;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Settings;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>The toolbar on any of the four edges, dragged there by its grip (UI-02, FR-110).</summary>
[Collection(nameof(ThemeCollection))]
public sealed class ToolbarEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-toolbaredge-" + Guid.NewGuid().ToString("N"));

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

    private void WithWindow(Action<MainWindow, SettingsService> test, double width = 1000, double height = 800, bool animations = false)
    {
        WpfTestThread.Run(() =>
        {
            var settings = new SettingsService(new AppSettingsStore(_dir), new ManualScheduler());
            settings.Load();
            var window = new MainWindow(null, settings);
            try
            {
                var toolbar = window.FloatingToolbar;
                toolbar.AnimationsEnabled = () => animations;
                toolbar.TransparencyEnabled = () => true;
                toolbar.ApplyMotionSetting();
                ScrollRailTests.LayOut((FrameworkElement)window.Content, width, height);
                test(window, settings);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static void Move(MainWindow window, ToolbarEdge edge, double width = 1000, double height = 800)
    {
        ((MainViewModel)window.DataContext).Toolbar.Edge = edge;
        ScrollRailTests.LayOut((FrameworkElement)window.Content, width, height);
    }

    [Fact]
    public void The_edge_follows_the_settings_at_start_and_when_they_change()
    {
        WpfTestThread.Run(() =>
        {
            Directory.CreateDirectory(_dir);
            new AppSettingsStore(_dir).Save(AppSettings.Default with { ToolbarEdge = ToolbarEdge.Right });
            var settings = new SettingsService(new AppSettingsStore(_dir), new ManualScheduler());
            settings.Load();
            var window = new MainWindow(null, settings);
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1000, 800);
                Assert.Equal(ToolbarEdge.Right, window.FloatingToolbar.Edge);

                settings.Update(s => s with { ToolbarEdge = ToolbarEdge.Top });
                Assert.Equal(ToolbarEdge.Top, window.FloatingToolbar.Edge);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public void On_the_sides_the_pill_is_a_column_and_on_the_top_and_bottom_a_row()
    {
        WithWindow((window, _) =>
        {
            var toolbar = window.FloatingToolbar;
            foreach (var edge in new[] { ToolbarEdge.Bottom, ToolbarEdge.Top })
            {
                Move(window, edge);
                Assert.False(toolbar.IsVertical);
                Assert.True(toolbar.Pill.ActualWidth > toolbar.Pill.ActualHeight, edge.ToString());
            }

            foreach (var edge in new[] { ToolbarEdge.Left, ToolbarEdge.Right })
            {
                Move(window, edge);
                Assert.True(toolbar.IsVertical);
                Assert.True(toolbar.Pill.ActualHeight > toolbar.Pill.ActualWidth, edge.ToString());
            }
        });
    }

    [Theory]
    [InlineData(ToolbarEdge.Bottom)]
    [InlineData(ToolbarEdge.Top)]
    [InlineData(ToolbarEdge.Left)]
    [InlineData(ToolbarEdge.Right)]
    public void The_pill_sits_on_its_edge_and_the_flyout_opens_towards_the_centre(ToolbarEdge edge)
    {
        WithWindow((window, _) =>
        {
            Move(window, edge);
            var toolbar = window.FloatingToolbar;
            ToolbarViewTests.Invoke(toolbar.PenButton);
            ScrollRailTests.LayOut((FrameworkElement)window.Content, 1000, 800);

            var pill = Bounds(toolbar.Pill, window.PageHost);
            var flyout = Bounds(toolbar.PenFlyout.Surface, window.PageHost);
            switch (edge)
            {
                case ToolbarEdge.Bottom:
                    Assert.True(pill.Bottom > 700 && flyout.Bottom <= pill.Top + 0.5, $"{pill} {flyout}");
                    break;
                case ToolbarEdge.Top:
                    Assert.True(pill.Top < 100 && flyout.Top >= pill.Bottom - 0.5, $"{pill} {flyout}");
                    break;
                case ToolbarEdge.Left:
                    Assert.True(pill.Left < 100 && flyout.Left >= pill.Right - 0.5, $"{pill} {flyout}");
                    break;
                default:
                    Assert.True(pill.Right > 900 && flyout.Right <= pill.Left + 0.5, $"{pill} {flyout}");
                    break;
            }

            Assert.True(flyout.Left >= -0.5 && flyout.Right <= 1000.5 && flyout.Top >= -0.5 && flyout.Bottom <= 800.5, "the flyout is inside the window");
        });
    }

    [Fact]
    public void On_the_top_edge_the_search_pill_and_notices_sit_below_the_toolbar()
    {
        WithWindow((window, _) =>
        {
            var normal = window.TopOverlays.Margin.Top;
            Move(window, ToolbarEdge.Top);

            var pill = Bounds(window.FloatingToolbar.Pill, window.PageHost);
            Assert.True(window.TopOverlays.Margin.Top >= pill.Bottom, $"{window.TopOverlays.Margin.Top} {pill}");
            Assert.True(window.TopOverlays.Margin.Top > normal);

            Move(window, ToolbarEdge.Bottom);
            Assert.Equal(normal, window.TopOverlays.Margin.Top); // back where it was
        });
    }

    [Fact]
    public void On_the_left_the_toolbar_is_below_the_menu_button_and_clear_of_the_swipe_zone_and_on_the_right_clear_of_the_rail()
    {
        WithWindow((window, _) =>
        {
            Move(window, ToolbarEdge.Left);
            var pill = Bounds(window.FloatingToolbar.Pill, window.PageHost);
            var menu = Bounds(window.MainMenu.MenuButton, window.PageHost);
            Assert.True(pill.Top >= menu.Bottom, $"{pill} {menu}");
            Assert.True(pill.Left >= (double)window.FindResource("EdgeSwipeZoneWidth"), pill.ToString());

            Move(window, ToolbarEdge.Right);
            pill = Bounds(window.FloatingToolbar.Pill, window.PageHost);
            Assert.True(pill.Right <= 1000 - (double)window.FindResource("RailTouchWidth"), pill.ToString());
            Assert.True(pill.Top >= (double)((Thickness)window.FindResource("TitleBarClearance")).Top);
        });
    }

    [Fact]
    public void The_active_tool_indicator_slides_along_the_pill_on_every_edge()
    {
        WithWindow((window, _) =>
        {
            var toolbar = window.FloatingToolbar;
            Move(window, ToolbarEdge.Left);
            ToolbarViewTests.Invoke(toolbar.EraserButton);
            ScrollRailTests.LayOut((FrameworkElement)window.Content, 1000, 800);
            var slide = toolbar.IndicatorSlide;
            var eraser = toolbar.EraserButton.TranslatePoint(new Point(0, 0), toolbar.ButtonRow);
            Assert.Equal(eraser.Y, slide.Y, 1);
            Assert.Equal(eraser.X, slide.X, 1);
            Assert.True(slide.Y > 40, "below the grip and the pen and highlighter buttons");

            Move(window, ToolbarEdge.Bottom);
            eraser = toolbar.EraserButton.TranslatePoint(new Point(0, 0), toolbar.ButtonRow);
            Assert.Equal(eraser.X, slide.X, 1);
            Assert.Equal(eraser.Y, slide.Y, 1);
        });
    }

    [Fact]
    public void A_drag_by_the_grip_ends_on_the_nearest_edge_and_is_remembered()
    {
        WithWindow((window, settings) =>
        {
            var toolbar = window.FloatingToolbar;
            Assert.Equal(window.PageHost, toolbar.DragGrip.ReferenceElement ?? window.PageHost);
            toolbar.DragGrip.ReferenceElement = window.PageHost;

            toolbar.DragGrip.SimulateDrag(new Point(500, 760), new Point(20, 400));

            Assert.Equal(ToolbarEdge.Left, ((MainViewModel)window.DataContext).Toolbar.Edge);
            Assert.Equal(ToolbarEdge.Left, settings.Current.ToolbarEdge);
            Assert.True(toolbar.IsVertical);

            toolbar.DragGrip.SimulateDrag(new Point(20, 400), new Point(500, 10));
            Assert.Equal(ToolbarEdge.Top, settings.Current.ToolbarEdge);
        });
    }

    [Fact]
    public void A_drag_that_ends_on_the_same_edge_changes_nothing_and_the_pill_goes_back_to_its_place()
    {
        WithWindow((window, settings) =>
        {
            var toolbar = window.FloatingToolbar;
            toolbar.DragGrip.ReferenceElement = window.PageHost;
            var before = Bounds(toolbar.Pill, window.PageHost);

            toolbar.DragGrip.SimulateDrag(new Point(500, 760), new Point(500, 700));

            Assert.Equal(ToolbarEdge.Bottom, settings.Current.ToolbarEdge);
            Assert.Null(toolbar.LastSnapAnimationDuration);
            Assert.Equal(before, Bounds(toolbar.Pill, window.PageHost));
        });
    }

    [Fact]
    public void The_snap_animation_runs_with_animations_on_and_is_instant_with_animations_off()
    {
        WithWindow(
            (window, _) =>
            {
                var toolbar = window.FloatingToolbar;
                toolbar.DragGrip.ReferenceElement = window.PageHost;
                toolbar.DragGrip.SimulateDrag(new Point(500, 760), new Point(990, 400));
                Assert.Equal((TimeSpan)window.FindResource("ToolbarShowDuration"), toolbar.LastSnapAnimationDuration);
            },
            animations: true);

        WithWindow((window, _) =>
        {
            var toolbar = window.FloatingToolbar;
            toolbar.DragGrip.ReferenceElement = window.PageHost;
            toolbar.DragGrip.SimulateDrag(new Point(500, 760), new Point(990, 400));
            Assert.Null(toolbar.LastSnapAnimationDuration);
            Assert.Equal(1, toolbar.Opacity);
        });
    }

    [Fact]
    public void The_grip_is_named_has_a_touch_sized_target_and_the_pen_cannot_start_a_drag_through_the_simulation_hook_only()
    {
        WithWindow((window, _) =>
        {
            var grip = window.FloatingToolbar.DragGrip;
            Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(grip)));
            Assert.True(grip.ActualHeight >= 40 || grip.ActualWidth >= 40, "40 px along the short side");
            Assert.False(grip.IsDragging);
        });
    }

    [Fact]
    public void The_automatic_visibility_works_on_a_vertical_toolbar()
    {
        WithWindow((window, _) =>
        {
            var vm = (MainViewModel)window.DataContext;
            Move(window, ToolbarEdge.Left);
            var toolbar = window.FloatingToolbar;
            Assert.True(toolbar.Pill.IsHitTestVisible);

            toolbar.ApplyPhase(ToolbarPhase.Hidden);

            Assert.False(toolbar.Pill.IsHitTestVisible); // takes no input while hidden: the pen writes there
            toolbar.ApplyPhase(ToolbarPhase.Visible);
            Assert.True(toolbar.Pill.IsHitTestVisible);
        });
    }

    [Theory]
    [InlineData(ToolbarEdge.Bottom)]
    [InlineData(ToolbarEdge.Top)]
    [InlineData(ToolbarEdge.Left)]
    [InlineData(ToolbarEdge.Right)]
    public void A_tiny_window_lays_out_on_every_edge_without_throwing(ToolbarEdge edge)
    {
        WithWindow(
            (window, _) =>
            {
                Move(window, edge, 200, 120);
                ToolbarViewTests.Invoke(window.FloatingToolbar.PenButton);
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 200, 120);
                Assert.True(window.FloatingToolbar.Pill.ActualWidth > 0);
            },
            width: 200,
            height: 120);
    }

    [Fact]
    public void The_flyout_height_on_the_sides_is_the_height_below_the_title_bar()
    {
        WithWindow((window, _) =>
        {
            Move(window, ToolbarEdge.Left);
            var title = ((Thickness)window.FindResource("TitleBarClearance")).Top;
            var gap = window.FloatingToolbar.Pill.Margin.Bottom;

            Assert.Equal(600 - title - gap, window.FloatingToolbar.FlyoutHeightLimit(600), 3);
        });
    }
}
