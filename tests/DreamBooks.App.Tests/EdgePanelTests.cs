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
                Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, null));
                Assert.False(window.MarksPanel.IsOpen);
            }
            finally
            {
                window.CloseForTests();
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
                window.CloseForTests();
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
                window.CloseForTests();
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

                // The main menu's entry opens it, on the marks tab (3b-2 adds the Pages tab to the same panel).
                var entry = window.MainMenu.MarksItem;
                Assert.Same(panel.OpenTabCommand, entry.Command);
                Assert.Equal(MainWindow.MarksTab, entry.CommandParameter);
                Assert.Equal(Strings.OpenMarksList, AutomationProperties.GetName(entry));
                Assert.True(entry.MinHeight >= 40);
                entry.Command.Execute(entry.CommandParameter);
                Assert.True(panel.IsOpen);
                Assert.Equal(MainWindow.MarksTab, panel.SelectedTab);

                // The count is in the header.
                Assert.Equal("Marks.Count", BindingOperations.GetBinding(panel, EdgePanel.HeaderDetailProperty)?.Path.Path);

                // Automation names are unique among the window's commands.
                var names = new[]
                {
                    AutomationProperties.GetName(window.NavButtons.PreviousMarkButton),
                    AutomationProperties.GetName(window.NavButtons.NextMarkButton),
                    AutomationProperties.GetName(window.MainMenu.MarksItem),
                    AutomationProperties.GetName(panel.CloseButton),
                    AutomationProperties.GetName(window.MarksList.List),
                    AutomationProperties.GetName(window.NavButtons.BackButton),
                };
                Assert.Equal(names.Length, names.Distinct().Count());
                Assert.All(names, n => Assert.False(string.IsNullOrEmpty(n)));
            }
            finally
            {
                window.CloseForTests();
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

                // The round buttons above the indicator chip (the debug panel's are gone, phase 4a).
                var nav = window.NavButtons;
                Assert.Same(marks.PreviousCommand, nav.PreviousMarkButton.Command);
                Assert.Same(marks.NextCommand, nav.NextMarkButton.Command);
                Assert.Equal(Strings.PreviousMark, AutomationProperties.GetName(nav.PreviousMarkButton));
                Assert.Equal(Strings.NextMark, AutomationProperties.GetName(nav.NextMarkButton));
                foreach (var button in new[] { nav.PreviousMarkButton, nav.NextMarkButton })
                {
                    // Collapsed without a document: the size the style gives them.
                    Assert.True(button.Width >= 40 && button.Height >= 40, "40 x 40 touch target");
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
                Assert.Equal("Marks.StatusText", BindingOperations.GetBinding(nav.StatusText, TextBlock.TextProperty)?.Path.Path);
                Assert.Equal(Visibility.Collapsed, nav.StatusSurface.Visibility);
            }
            finally
            {
                window.CloseForTests();
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

    [Fact]
    public void Edge_swipe_rejects_a_palm_and_a_second_finger()
    {
        WpfTestThread.Run(() =>
        {
            var view = new DocumentView();
            ScrollRailTests.LayOut(view, 1000, 800);
            var swipes = 0;
            view.LeftEdgeSwiped += (_, _) => swipes++;
            var edge = new Point(5, 400);

            // A palm-sized contact at the edge (the heel of the writing hand) never starts a swipe.
            Assert.False(view.BeginEdgeSwipe(edge, PointerKind.Finger, new Size(ScrollRail.MaxFingerContactDip + 1, 20)));
            view.MoveEdgeSwipe(new Point(80, 400));
            Assert.Equal(0, swipes);

            // A finger-sized one does.
            Assert.True(view.BeginEdgeSwipe(edge, PointerKind.Finger, new Size(12, 12)));

            // A second finger lands (a pinch): the candidate is dropped and the pinch is not a swipe.
            Assert.False(view.BeginEdgeSwipe(new Point(300, 400), PointerKind.Finger, new Size(12, 12)));
            view.MoveEdgeSwipe(new Point(80, 400));
            Assert.Equal(0, swipes);
            view.EndEdgeSwipe();

            Assert.True(view.BeginEdgeSwipe(edge, PointerKind.Finger, new Size(12, 12)));
            view.MoveEdgeSwipe(new Point(80, 400));
            Assert.Equal(1, swipes);
        });
    }

    [Fact]
    public async Task Edge_swipe_never_starts_or_continues_while_a_page_is_inking()
    {
        await WithViewAsync((vm, view) =>
        {
            var swipes = 0;
            view.LeftEdgeSwiped += (_, _) => swipes++;
            LoadEditors(view);
            var surface = view.SurfaceFor(vm.Viewport.CurrentPage)!;

            // Started before the pen comes down: the pen's stroke ends it.
            Assert.True(view.BeginEdgeSwipe(new Point(5, 400), PointerKind.Finger));
            StartInking(surface);
            view.MoveEdgeSwipe(new Point(80, 400));
            Assert.Equal(0, swipes);

            // While the page is inking: none starts.
            Assert.False(view.BeginEdgeSwipe(new Point(5, 400), PointerKind.Finger));
            view.MoveEdgeSwipe(new Point(80, 400));
            Assert.Equal(0, swipes);
            StopInking(surface);
        });
    }

    [Fact]
    public void Finger_light_dismiss_captures_the_touch_until_it_lifts()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                window.MarksPanel.AnimationsEnabled = () => false;
                var outside = new Point(700, 300);
                var captured = 0;
                var released = 0;

                // Closed: nothing is taken or captured.
                Assert.False(window.RouteDismissTouchDown(outside, 7, () => ++captured > 0));
                Assert.Equal(0, captured);

                // Open: the dismissing finger closes it and stays with the host (not the document's manipulation)
                // until it lifts, so a drag never pans the page.
                window.MarksPanel.Open();
                Assert.True(window.RouteDismissTouchDown(outside, 7, () => ++captured > 0));
                Assert.False(window.MarksPanel.IsOpen);
                Assert.Equal(1, captured);
                Assert.Equal(7, window.DismissTouchId);
                Assert.True(window.RouteDismissTouchMove(7));
                Assert.False(window.RouteDismissTouchMove(8)); // another finger is the document's
                Assert.True(window.RouteDismissTouchUp(7, () => released++));
                Assert.Equal(1, released);
                Assert.Null(window.DismissTouchId);
                Assert.False(window.RouteDismissTouchMove(7));

                // A finger on the sheet is the sheet's: not captured.
                window.MarksPanel.Open();
                Assert.False(window.RouteDismissTouchDown(new Point(100, 300), 9, () => ++captured > 0));
                Assert.Equal(1, captured);
                Assert.True(window.MarksPanel.IsOpen);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public async Task Keys_inside_the_open_panel_stay_the_panel_s_with_a_document_open()
    {
        await WithWindowAsync((vm, window) =>
        {
            var list = window.MarksList.List;
            var before = vm.Viewport.Position;

            // Open: PgDn in the list scrolls the list, the document stays.
            window.MarksPanel.Open();
            Assert.False(window.RouteKey(Key.PageDown, ModifierKeys.None, list));
            Assert.Equal(before, vm.Viewport.Position);

            // Control case: the same key from the same source with the panel closed moves the document.
            window.MarksPanel.Close();
            Assert.True(window.RouteKey(Key.PageDown, ModifierKeys.None, list));
            Assert.NotEqual(before, vm.Viewport.Position);
        });
    }

    [Fact]
    public void Esc_in_the_go_to_page_field_clears_it_and_keeps_the_panel_open()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                window.MarksPanel.AnimationsEnabled = () => false;
                window.MarksPanel.Open();
                Assert.False(window.RouteKey(Key.Escape, ModifierKeys.None, window.IndicatorChip.GoToPageBox)); // the field's own Esc
                Assert.True(window.MarksPanel.IsOpen);
                Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, window.MarksList.List));
                Assert.False(window.MarksPanel.IsOpen);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public void Opening_moves_focus_into_the_panel_and_Tab_cycles_inside_it()
    {
        WpfTestThread.Run(() =>
        {
            var panel = NewPanel();
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(panel.Sheet));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetControlTabNavigation(panel.Sheet));
            var requests = panel.FocusRequests;
            panel.Open();
            WpfTestThread.Drain();
            Assert.Equal(requests + 1, panel.FocusRequests); // after layout, focus goes to the first control inside
        });
    }

    [Fact]
    public void Ctrl_M_opens_and_closes_the_marks_list()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                window.MarksPanel.AnimationsEnabled = () => false;
                var binding = window.InputBindings.OfType<KeyBinding>()
                    .Single(b => b.Key == Key.M && b.Modifiers == ModifierKeys.Control);
                Assert.Same(window.MarksPanel.ToggleCommand, binding.Command);
                Assert.Single(window.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.M); // no clash
                binding.Command.Execute(null);
                Assert.True(window.MarksPanel.IsOpen);
                binding.Command.Execute(null);
                Assert.False(window.MarksPanel.IsOpen);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public async Task Status_text_is_announced_by_its_text_and_the_count_is_named()
    {
        await WithWindowAsync((vm, window) =>
        {
            // The status is named by its own text (no fixed label overriding it) and announced when it changes.
            Assert.True(string.IsNullOrEmpty(AutomationProperties.GetName(window.NavButtons.StatusText)));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.NavButtons.StatusText));
            WpfTestThread.PumpUntil(() =>
            {
                _scheduler.Advance(MarksViewModel.RefreshInterval);
                return !vm.Marks.IsScanning;
            });
            vm.MarkIndexForTest!.SetPage(5, [new Core.Marks.Mark(5, Core.Marks.MarkSide.Left, 100, 300, [Guid.NewGuid()])]);
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            var announced = window.StatusAnnouncements;
            vm.Marks.PreviousCommand.Execute(null); // nothing above the first page
            Assert.Equal(Strings.MarksNoOther, vm.Marks.StatusText);
            WpfTestThread.Drain();
            Assert.Equal(Visibility.Visible, window.NavButtons.StatusSurface.Visibility);
            Assert.True(window.NavButtons.IsShown); // the status shows the buttons (and the rail) when it appears
            Assert.Equal(announced + 1, window.StatusAnnouncements);
            var statusPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(window.NavButtons.StatusText);
            Assert.Equal(Strings.MarksNoOther, statusPeer.GetName());

            // The buttons (and the rail) stay while the status is on screen, then may fade.
            Assert.True(window.DocumentViewer.Rail.State.IsHeld);
            _scheduler.Advance(MarksViewModel.StatusDuration);
            Assert.Null(vm.Marks.StatusText);
            Assert.Equal(Visibility.Collapsed, window.NavButtons.StatusSurface.Visibility);
            Assert.False(window.DocumentViewer.Rail.State.IsHeld);

            // The count in the panel's header has a peer and a name that includes the number.
            var countPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(window.MarksPanel.DetailText);
            Assert.NotNull(countPeer);
            Assert.Equal(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.MarksCountNameFormat, 1),
                countPeer.GetName());
        });
    }

    private static void LoadEditors(DocumentView view)
    {
        view.FlushEditorLoads();
        WpfTestThread.PumpUntil(() => view.ActiveSurfaces.Values.All(s => s.Editor is not null));
    }

    private static void StartInking(InkSurface surface)
    {
        surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
        Assert.True(surface.IsInking);
    }

    private static void StopInking(InkSurface surface) =>
        surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });

    /// <summary>Opens a generated PDF in a view model and runs the test on a main window bound to it (never shown).</summary>
    private async Task WithWindowAsync(Action<MainViewModel, MainWindow> test)
    {
        var path = Path.Combine(_dir, "win.pdf");
        TestPdfFactory.Write(path, Enumerable.Repeat((612.0, 792.0), _pageCount).ToArray());
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path }, scheduler: _scheduler)
        {
            MarkScanIdleGap = TimeSpan.Zero,
        };
        await vm.OpenPdfCommand.ExecuteAsync(null);

        // Without a view the mark scan starts at open. It must end before the window binds the view: its fake renders
        // never complete, and a scan still running then parks for good behind them (a race that failed under load).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (vm.MarkScannerForTest is { IsCompleted: false } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(vm.MarkScannerForTest is null or { IsCompleted: true }, "the mark scan did not finish before the window");
        try
        {
            WpfTestThread.Run(() =>
            {
                var window = new MainWindow { DataContext = vm };
                try
                {
                    window.DocumentViewer.BitmapRenderer = new FakeBitmapRenderer();
                    window.MarksPanel.AnimationsEnabled = () => false;
                    ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                    ScrollRailTests.Settle(window.DocumentViewer);
                    test(vm, window);
                }
                finally
                {
                    window.CloseForTests();
                }
            });
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
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
