using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// The left-edge panel shell (UI-04, FR-103, NFR-32): opening and closing (command, Esc, close button, swipe, tap
/// outside), the slide that is instant with animations off, the opaque look with transparency off, the finger-only
/// swipe from the left edge (the pen at the edge inks), and its place in the main window with the previous/next mark
/// controls (FR-102). The look and feel of the slide are checked on the device.
/// </summary>
public sealed class EdgePanelTests : IDisposable
{
    private const int _pageCount = 10;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-edge-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public EdgePanelTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void Panel_opens_and_closes_by_command_and_Esc()
    {
        WpfTestThread.Run(() =>
        {
            var panel = NewPanel();
            Assert.False(panel.IsOpen);
            Assert.Equal(Visibility.Collapsed, panel.Root.Visibility);

            panel.OpenCommand.Execute(null);
            Assert.True(panel.IsOpen);
            Assert.Equal(Visibility.Visible, panel.Root.Visibility);
            Assert.True(panel.Root.IsHitTestVisible);
            Assert.Equal(0, panel.Slide.X);

            // The close button: a 40 x 40 touch target with an Italian automation name.
            Assert.Same(panel.CloseCommand, panel.CloseButton.Command);
            Assert.Equal(Strings.ClosePanel, AutomationProperties.GetName(panel.CloseButton));
            Assert.True(panel.CloseButton.MinWidth >= 40 && panel.CloseButton.MinHeight >= 40);
            panel.CloseCommand.Execute(null);
            Assert.False(panel.IsOpen);
            Assert.Equal(Visibility.Collapsed, panel.Root.Visibility);

            // A finger swipe to the left on the sheet closes it; a vertical one (scrolling the list) does not.
            panel.Open();
            panel.SwipeStart(new Point(200, 300));
            panel.SwipeMove(new Point(195, 360));
            panel.SwipeMove(new Point(150, 420));
            Assert.True(panel.IsOpen);
            panel.SwipeStart(new Point(200, 300));
            panel.SwipeMove(new Point(140, 305));
            Assert.False(panel.IsOpen);
        });

        // Esc in the main window closes the open panel, and is left alone otherwise.
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                window.MarksPanel.AnimationsEnabled = () => false;
                Assert.False(window.RouteKey(Key.Escape, ModifierKeys.None, null));
                window.MarksPanel.Open();
                Assert.False(window.RouteKey(Key.PageDown, ModifierKeys.None, window.MarksList.List)); // the list scrolls
                Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, null));
                Assert.False(window.MarksPanel.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Panel_open_state_animates_unless_animations_are_off()
    {
        WpfTestThread.Run(() =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(180), (TimeSpan)Application.Current.FindResource("EdgePanelOpenDuration"));
            Assert.Equal(TimeSpan.FromMilliseconds(120), (TimeSpan)Application.Current.FindResource("EdgePanelCloseDuration"));
            Assert.Equal(320.0, (double)Application.Current.FindResource("EdgePanelWidth"));

            var panel = NewPanel(animations: true);
            panel.Open();
            panel.UpdateLayout();
            Assert.Equal(320, panel.Sheet.ActualWidth, 3);
            Assert.True(panel.IsOpen);
            Assert.True(panel.IsAnimating); // slides in
            Assert.True(panel.Slide.HasAnimatedProperties);

            panel.Close();
            Assert.False(panel.IsOpen);
            Assert.True(panel.IsAnimating); // slides out...
            Assert.Equal(Visibility.Visible, panel.Root.Visibility);
            Assert.False(panel.Root.IsHitTestVisible); // ...but the pen and fingers reach the page at once

            var instant = NewPanel(animations: false);
            instant.Open();
            Assert.False(instant.IsAnimating);
            Assert.False(instant.Slide.HasAnimatedProperties);
            Assert.Equal(0, instant.Slide.X);
            instant.Close();
            Assert.False(instant.IsAnimating);
            Assert.Equal(Visibility.Collapsed, instant.Root.Visibility);

            // Transparency effects off (NFR-32): opaque background, no shadow.
            instant.TransparencyEnabled = () => false;
            instant.ApplyTransparency();
            Assert.Same(Application.Current.FindResource("EdgePanelBackgroundBrushOpaque"), instant.Sheet.Background);
            Assert.Null(instant.Sheet.Effect);
            instant.TransparencyEnabled = () => true;
            instant.ApplyTransparency();
            Assert.Same(Application.Current.FindResource("EdgePanelBackgroundBrush"), instant.Sheet.Background);
            Assert.NotNull(instant.Sheet.Effect);
        });
    }

    [Fact]
    public async Task Pen_at_the_left_edge_inks_and_does_not_open_the_panel()
    {
        await WithViewAsync((vm, view) =>
        {
            // Zoomed in, the page covers the left edge: the pen there reaches the page's ink surface.
            for (var i = 0; i < 4; i++)
            {
                vm.ZoomVm.ZoomInCommand.Execute(null);
            }

            ScrollRailTests.Settle(view);
            var swipes = 0;
            view.LeftEdgeSwiped += (_, _) => swipes++;
            var edge = new Point(4, 400);
            Assert.True(IsWithinInkSurface(ScrollRailTests.HitAt(view, edge)), "the pen at the edge reaches the page");
            Assert.False(view.RoutePointerDown(edge, PointerKind.Pen));
            Assert.False(view.BeginEdgeSwipe(edge, PointerKind.Pen));
            view.MoveEdgeSwipe(new Point(80, 400));
            Assert.Equal(0, swipes);

            // The mouse does not swipe either (it has the button and the keyboard).
            Assert.False(view.BeginEdgeSwipe(edge, PointerKind.Mouse));
            view.MoveEdgeSwipe(new Point(80, 400));
            Assert.Equal(0, swipes);
        });

        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                var content = (FrameworkElement)window.Content;
                ScrollRailTests.LayOut(content, 1200, 800);
                window.MarksPanel.AnimationsEnabled = () => false;

                // Closed, nothing of the panel is in the way at the edge (the pen inks there).
                var edge = window.DocumentViewer.TranslatePoint(new Point(4, 400), content);
                Assert.False(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(content, edge), window.MarksPanel));
                Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(content, edge), window.DocumentViewer));
                Assert.False(window.DocumentViewer.BeginEdgeSwipe(new Point(4, 400), PointerKind.Pen));
                window.DocumentViewer.MoveEdgeSwipe(new Point(80, 400));
                Assert.False(window.MarksPanel.IsOpen);

                // Open, the pen on the sheet never presses its controls: the panel gives way (closes).
                window.MarksPanel.Open();
                Assert.True(window.MarksPanel.RoutePress(new Point(100, 300), PointerKind.Pen));
                Assert.False(window.MarksPanel.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Finger_swipe_from_the_edge_opens_it()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                window.MarksPanel.AnimationsEnabled = () => false;
                var view = window.DocumentViewer;
                var zone = (double)Application.Current.FindResource("EdgeSwipeZoneWidth");
                Assert.Equal(24, zone);

                // Starting outside the edge zone: no swipe.
                Assert.False(view.BeginEdgeSwipe(new Point(zone + 10, 400), PointerKind.Finger));
                view.MoveEdgeSwipe(new Point(zone + 100, 400));
                Assert.False(window.MarksPanel.IsOpen);

                // A vertical pan starting at the edge stays a pan, even if it drifts right later.
                Assert.True(view.BeginEdgeSwipe(new Point(5, 400), PointerKind.Finger));
                view.MoveEdgeSwipe(new Point(8, 460));
                view.MoveEdgeSwipe(new Point(90, 470));
                Assert.False(window.MarksPanel.IsOpen);
                view.EndEdgeSwipe();

                // A short move is not a swipe yet; a rightward one is.
                Assert.True(view.BeginEdgeSwipe(new Point(5, 400), PointerKind.Finger));
                view.MoveEdgeSwipe(new Point(12, 402));
                Assert.False(window.MarksPanel.IsOpen);
                view.MoveEdgeSwipe(new Point(60, 410));
                Assert.True(window.MarksPanel.IsOpen);

                // The press itself is never taken: a finger at the edge still pans the document.
                window.MarksPanel.Close();
                Assert.False(view.RoutePointerDown(new Point(5, 400), PointerKind.Finger));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Tap_outside_closes_it()
    {
        WpfTestThread.Run(() =>
        {
            var panel = NewPanel();
            var inside = new Point(100, 300);
            var outside = new Point(600, 300);

            // Closed: nothing is taken.
            Assert.False(panel.RoutePress(outside, PointerKind.Finger));
            Assert.False(panel.RoutePress(inside, PointerKind.Pen));

            // A finger or the mouse outside closes it and is taken (it does not also pan or press the page).
            foreach (var kind in new[] { PointerKind.Finger, PointerKind.Mouse })
            {
                panel.Open();
                Assert.True(panel.RoutePress(outside, kind));
                Assert.False(panel.IsOpen);
            }

            // The pen outside closes it and inks (not taken).
            panel.Open();
            Assert.False(panel.RoutePress(outside, PointerKind.Pen));
            Assert.False(panel.IsOpen);

            // A finger or the mouse inside uses the panel's controls.
            panel.Open();
            Assert.False(panel.RoutePress(inside, PointerKind.Finger));
            Assert.False(panel.RoutePress(inside, PointerKind.Mouse));
            Assert.True(panel.IsOpen);
        });
    }

    [Fact]
    public void MainWindow_instantiates_with_the_panel()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                var vm = (MainViewModel)window.DataContext;
                var panel = window.MarksPanel;
                Assert.False(panel.IsOpen);
                Assert.Equal(Strings.MarksList, panel.Title);
                Assert.Equal(Strings.MarksList, AutomationProperties.GetName(panel));
                Assert.Same(window.MarksList, panel.Body);
                Assert.Same(vm.Marks, window.MarksList.DataContext);

                // The menu entry opens it.
                Assert.Same(panel.OpenCommand, window.OpenMarksListButton.Command);
                Assert.Equal(Strings.OpenMarksList, AutomationProperties.GetName(window.OpenMarksListButton));
                Assert.True(window.OpenMarksListButton.MinHeight >= 40);
                window.OpenMarksListButton.Command.Execute(null);
                Assert.True(panel.IsOpen);

                // The count is in the header.
                Assert.Equal("Marks.Count", BindingOperations.GetBinding(panel, EdgePanel.HeaderDetailProperty)?.Path.Path);

                // Automation names are unique among the window's commands.
                var names = new[]
                {
                    AutomationProperties.GetName(window.PreviousMarkButton),
                    AutomationProperties.GetName(window.NextMarkButton),
                    AutomationProperties.GetName(window.OpenMarksListButton),
                    AutomationProperties.GetName(panel.CloseButton),
                    AutomationProperties.GetName(window.MarksList.List),
                    AutomationProperties.GetName(window.BackButton),
                };
                Assert.Equal(names.Length, names.Distinct().Count());
                Assert.All(names, n => Assert.False(string.IsNullOrEmpty(n)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Previous_next_buttons_and_Alt_Up_Down_invoke_the_commands()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                var vm = (MainViewModel)window.DataContext;
                var marks = vm.Marks;

                Assert.Same(marks.PreviousCommand, window.PreviousMarkButton.Command);
                Assert.Same(marks.NextCommand, window.NextMarkButton.Command);
                Assert.Equal(Strings.PreviousMark, AutomationProperties.GetName(window.PreviousMarkButton));
                Assert.Equal(Strings.NextMark, AutomationProperties.GetName(window.NextMarkButton));
                foreach (var button in new[] { window.PreviousMarkButton, window.NextMarkButton })
                {
                    Assert.True(button.ActualWidth >= 40 && button.ActualHeight >= 40, "40 x 40 touch target");
                    Assert.True(button.Focusable && button.IsTabStop, "keyboard reachable");
                }

                // Alt+Up / Alt+Down: left alone by the document's key routing, taken by the bindings.
                Assert.False(window.RouteKey(Key.Up, ModifierKeys.Alt, null));
                Assert.False(window.RouteKey(Key.Down, ModifierKeys.Alt, null));
                var gestures = window.InputBindings.OfType<KeyBinding>()
                    .Select(b => (b.Key, b.Modifiers, b.Command))
                    .ToList();
                Assert.Contains((Key.Up, ModifierKeys.Alt, (ICommand)marks.PreviousCommand), gestures);
                Assert.Contains((Key.Down, ModifierKeys.Alt, (ICommand)marks.NextCommand), gestures);

                // "No other mark" is shown next to the buttons.
                Assert.Equal("Marks.StatusText", BindingOperations.GetBinding(window.MarkStatusText, TextBlock.TextProperty)?.Path.Path);
                Assert.Equal(Visibility.Collapsed, window.MarkStatusText.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Previous_next_commands_move_the_view_from_the_bindings()
    {
        await WithViewAsync((vm, view) =>
        {
            var index = vm.MarkIndexForTest!;
            index.SetPage(3, [new Core.Marks.Mark(3, Core.Marks.MarkSide.Left, 100, 300, [Guid.NewGuid()])]);
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            Assert.Equal(0, vm.Viewport.Position.PageIndex);
            vm.Marks.NextCommand.Execute(null);
            Assert.Contains(3, vm.Viewport.VisiblePages); // the mark is shown
            Assert.True(vm.Viewport.BackCommand.CanExecute(null)); // recorded for Back (FR-09)
            vm.Viewport.BackCommand.Execute(null);
            Assert.Equal(0, vm.Viewport.Position.PageIndex);
        });
    }

    private static EdgePanel NewPanel(bool animations = false)
    {
        var panel = new EdgePanel { AnimationsEnabled = () => animations, Title = "t" };
        ScrollRailTests.LayOut(panel, 1000, 800);
        return panel;
    }

    private static bool IsWithinInkSurface(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is InkSurface)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Opens a generated PDF, then runs the test on a view laid out at 1000 x 800 DIPs.</summary>
    private async Task WithViewAsync(Action<MainViewModel, DocumentView> test)
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfFactory.Write(path, Enumerable.Repeat((612.0, 792.0), _pageCount).ToArray());
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path }, scheduler: _scheduler);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            WpfTestThread.Run(() =>
            {
                var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer() };
                view.DataContext = vm;
                ScrollRailTests.LayOut(view, 1000, 800);
                test(vm, view);
            });
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
    }
}
