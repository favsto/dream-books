using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>
/// The floating toolbar and its option flyouts in the window (UI-02, section 3): the seven buttons, the second tap,
/// closing a flyout, the pen rule (it never hits the toolbar outside its pill; a pen press on a flyout closes it
/// without writing), the theme durations and NFR-32. Built on the STA test thread, never shown.
/// </summary>
public sealed class ToolbarViewTests
{
    private const double _width = 1200;
    private const double _height = 800;

    [Fact]
    public void Toolbar_shows_the_seven_buttons_with_Italian_names_and_the_right_disabled_states()
    {
        WithWindow((window, toolbar, _) =>
        {
            Button[] buttons =
            [
                toolbar.PenButton, toolbar.HighlighterButton, toolbar.EraserButton, toolbar.SelectionButton,
                toolbar.UndoButton, toolbar.RedoButton, toolbar.BookmarkButton,
            ];
            Assert.Equal(
                [Strings.ToolPen, Strings.ToolHighlighter, Strings.ToolEraser, Strings.ToolSelection, Strings.Undo, Strings.Redo, Strings.ToolBookmark],
                buttons.Select(AutomationProperties.GetName));

            // Left to right in that order, with the two separators after Selection and after Redo.
            var xs = buttons.Select(b => b.TranslatePoint(new Point(0, 0), toolbar.Pill).X).ToArray();
            Assert.Equal(xs.Order(), xs);
            Assert.Equal(2, toolbar.Separators.Count);
            Assert.InRange(toolbar.Separators[0].TranslatePoint(new Point(0, 0), toolbar.Pill).X, xs[3], xs[4]);
            Assert.InRange(toolbar.Separators[1].TranslatePoint(new Point(0, 0), toolbar.Pill).X, xs[5], xs[6]);

            // Lasso and bookmark wait for their phases; nothing to undo or redo without a document.
            Assert.True(toolbar.PenButton.IsEnabled && toolbar.HighlighterButton.IsEnabled && toolbar.EraserButton.IsEnabled);
            Assert.False(toolbar.SelectionButton.IsEnabled);
            Assert.False(toolbar.BookmarkButton.IsEnabled);
            Assert.False(toolbar.UndoButton.IsEnabled);
            Assert.False(toolbar.RedoButton.IsEnabled);

            // 40 px targets and 20 px Segoe Fluent Icons.
            Assert.All(buttons, b =>
            {
                Assert.True(b.ActualWidth >= 40 && b.ActualHeight >= 40, $"{AutomationProperties.GetName(b)} is smaller than 40 px");
                Assert.Equal(20, b.FontSize);
                Assert.Contains("Segoe Fluent Icons", b.FontFamily.Source, StringComparison.Ordinal);
            });

            // A pill (999 px corners) at the bottom centre of the document.
            Assert.Equal(999, toolbar.Pill.CornerRadius.TopLeft);
            var pill = Bounds(toolbar.Pill, window.PageHost);
            Assert.Equal(window.PageHost.ActualWidth / 2, pill.X + (pill.Width / 2), 1);
            Assert.InRange(_height - pill.Bottom, 0, 40);
        });
    }

    [Fact]
    public void The_active_tool_shows_the_indicator_and_its_status()
    {
        WithWindow((_, toolbar, vm) =>
        {
            Assert.Equal(ToolbarTool.Pen, vm.Toolbar.ActiveTool);
            Assert.Equal(Strings.ToolActive, AutomationProperties.GetItemStatus(toolbar.PenButton));
            Assert.Equal(toolbar.PenButton.TranslatePoint(new Point(0, 0), toolbar.ButtonRow).X, toolbar.IndicatorSlide.X, 1);

            Invoke(toolbar.EraserButton);
            toolbar.UpdateLayout();
            Assert.Equal(ToolbarTool.Eraser, vm.Toolbar.ActiveTool);
            Assert.Equal(toolbar.EraserButton.TranslatePoint(new Point(0, 0), toolbar.ButtonRow).X, toolbar.IndicatorSlide.X, 1);
            Assert.Equal(Strings.ToolActive, AutomationProperties.GetItemStatus(toolbar.EraserButton));
            Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(toolbar.PenButton));

            // The debug panel binds the same view model: a tool chosen there moves the indicator too.
            vm.Tools.ActiveTool = AppTool.Highlighter;
            toolbar.UpdateLayout();
            Assert.Equal(toolbar.HighlighterButton.TranslatePoint(new Point(0, 0), toolbar.ButtonRow).X, toolbar.IndicatorSlide.X, 1);
        });
    }

    [Theory]
    [InlineData(ToolbarTool.Pen, OptionsPanel.Pen)]
    [InlineData(ToolbarTool.Highlighter, OptionsPanel.Highlighter)]
    [InlineData(ToolbarTool.Eraser, OptionsPanel.Eraser)]
    public void A_second_tap_opens_the_flyout_of_the_active_tool(ToolbarTool tool, OptionsPanel panel)
    {
        WithWindow((window, toolbar, vm) =>
        {
            var button = toolbar.ButtonFor(tool);
            if (tool == ToolbarTool.Pen)
            {
                Invoke(toolbar.EraserButton); // the pen is active at startup: start from another tool
            }

            Invoke(button);
            Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel);
            Assert.Null(toolbar.ShownFlyout);

            Invoke(button);
            Assert.Equal(panel, vm.Toolbar.OpenPanel);
            var flyout = Assert.IsAssignableFrom<FrameworkElement>(toolbar.ShownFlyout);
            Assert.Same(toolbar.FlyoutFor(panel), flyout);
            Assert.Equal(Visibility.Visible, flyout.Visibility);
            Assert.True(flyout.IsHitTestVisible);
            Assert.All(
                new[] { OptionsPanel.Pen, OptionsPanel.Highlighter, OptionsPanel.Eraser }.Where(p => p != panel),
                other => Assert.NotEqual(Visibility.Visible, toolbar.FlyoutFor(other).Visibility));

            // Above the toolbar, a 12 px panel.
            toolbar.UpdateLayout();
            Assert.True(Bounds(flyout, window.PageHost).Bottom <= Bounds(toolbar.Pill, window.PageHost).Top);
            Assert.Equal(12, toolbar.SurfaceOf(panel).CornerRadius.TopLeft);

            Invoke(button);
            Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel);
            Assert.NotEqual(Visibility.Visible, flyout.Visibility);
        });
    }

    [Fact]
    public void The_flyout_closes_on_Esc_a_tap_outside_and_a_pen_press_without_writing()
    {
        WithWindow((window, toolbar, vm) =>
        {
            var outside = new Point(200, 300);

            // Esc.
            Invoke(toolbar.PenButton);
            Assert.Equal(OptionsPanel.Pen, vm.Toolbar.OpenPanel);
            Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, window.DocumentViewer));
            Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel);
            Assert.False(window.RouteKey(Key.Escape, ModifierKeys.None, window.DocumentViewer)); // nothing to close

            // A finger or the mouse outside: closed and taken (it does not also pan or press the page).
            foreach (var kind in new[] { PointerKind.Finger, PointerKind.Mouse })
            {
                Invoke(toolbar.PenButton);
                Assert.True(window.RoutePagePress(outside, kind));
                Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel);
            }

            // The pen outside: closed and not taken (it inks).
            Invoke(toolbar.PenButton);
            Assert.False(window.RoutePagePress(outside, PointerKind.Pen));
            Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel);

            // On the flyout: a finger or the mouse use its controls; the pen closes it and is taken, so no stroke.
            Invoke(toolbar.PenButton);
            toolbar.UpdateLayout();
            var onFlyout = Centre(toolbar.SurfaceOf(OptionsPanel.Pen), window.PageHost);
            Assert.False(window.RoutePagePress(onFlyout, PointerKind.Finger));
            Assert.False(window.RoutePagePress(onFlyout, PointerKind.Mouse));
            Assert.Equal(OptionsPanel.Pen, vm.Toolbar.OpenPanel);
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onFlyout), toolbar.SurfaceOf(OptionsPanel.Pen)));
            Assert.True(window.RoutePagePress(onFlyout, PointerKind.Pen));
            Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel);
            Assert.Null(vm.Session); // no document, no stroke: the press never reached a page

            // Closed at once for input: the closing flyout no longer takes the pen or a finger.
            Assert.False(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onFlyout), toolbar.SurfaceOf(OptionsPanel.Pen)));

            // On the pill the buttons work, for the pen too.
            Invoke(toolbar.PenButton);
            var onPill = Centre(toolbar.RedoButton, window.PageHost);
            Assert.False(window.RoutePagePress(onPill, PointerKind.Pen));
            Assert.False(window.RoutePagePress(onPill, PointerKind.Finger));
            Assert.Equal(OptionsPanel.Pen, vm.Toolbar.OpenPanel);
        });
    }

    [Fact]
    public void A_pen_press_at_the_toolbar_transparent_surround_reaches_the_ink_surface()
    {
        WithWindow((window, toolbar, _) =>
        {
            var pill = Bounds(toolbar.Pill, window.PageHost);

            // Beside the pill, in the gap between the pill and the bottom edge, and just above it.
            foreach (var point in new[]
                     {
                         new Point(pill.Left - 20, pill.Top + (pill.Height / 2)),
                         new Point(pill.Right + 20, pill.Top + (pill.Height / 2)),
                         new Point(pill.Left + (pill.Width / 2), pill.Bottom + 4),
                         new Point(pill.Left + (pill.Width / 2), pill.Top - 6),
                         new Point(pill.Left + 2, pill.Top + 2), // outside the pill's rounded corner
                     })
            {
                var hit = ScrollRailTests.HitAt(window.PageHost, point);
                Assert.False(ScrollRailTests.IsWithin(hit, toolbar), $"the toolbar takes the pen at {point}");
                Assert.True(ScrollRailTests.IsWithin(hit, window.DocumentViewer), $"the document misses the pen at {point}");
                Assert.False(window.RoutePagePress(point, PointerKind.Pen));
            }

            // On the pill: the toolbar.
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, Centre(toolbar.PenButton, window.PageHost)), toolbar.PenButton));

            // Around an open flyout too.
            Invoke(toolbar.PenButton);
            toolbar.UpdateLayout();
            var flyout = Bounds(toolbar.SurfaceOf(OptionsPanel.Pen), window.PageHost);
            var beside = new Point(flyout.Left - 30, flyout.Top + (flyout.Height / 2));
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, beside), window.DocumentViewer));
        });
    }

    [Fact]
    public void Flyout_animations_use_the_theme_durations_and_are_instant_with_reduced_motion()
    {
        WpfTestThread.Run(() =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(180), (TimeSpan)Application.Current.FindResource("FlyoutOpenDuration"));
            Assert.Equal(TimeSpan.FromMilliseconds(120), (TimeSpan)Application.Current.FindResource("FlyoutCloseDuration"));
            Assert.Equal(TimeSpan.FromMilliseconds(150), (TimeSpan)Application.Current.FindResource("ToolIndicatorSlideDuration"));
            Assert.Equal(TimeSpan.FromMilliseconds(200), (TimeSpan)Application.Current.FindResource("ToolbarRetractDuration"));
            Assert.Equal(0.95, (double)Application.Current.FindResource("FlyoutOpenFromScale"));
            Assert.Equal(0.97, (double)Application.Current.FindResource("FlyoutCloseToScale"));
        });

        WithWindow(
            (_, toolbar, vm) =>
            {
                Invoke(toolbar.PenButton, drain: false);
                var flyout = toolbar.FlyoutFor(OptionsPanel.Pen);
                Assert.True(toolbar.IsFlyoutAnimating);
                Assert.Equal(TimeSpan.FromMilliseconds(180), toolbar.LastFlyoutAnimationDuration);
                Assert.True(flyout.HasAnimatedProperties);

                Invoke(toolbar.PenButton, drain: false);
                Assert.True(toolbar.IsFlyoutAnimating); // fading out...
                Assert.Equal(TimeSpan.FromMilliseconds(120), toolbar.LastFlyoutAnimationDuration);
                Assert.Equal(Visibility.Visible, flyout.Visibility);
                Assert.False(flyout.IsHitTestVisible); // ...but the pen and fingers reach the page at once

                // The tool indicator slides in 150 ms.
                Invoke(toolbar.EraserButton, drain: false);
                Assert.Equal(TimeSpan.FromMilliseconds(150), toolbar.LastIndicatorAnimationDuration);
                Assert.True(toolbar.IndicatorSlide.HasAnimatedProperties);
                Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel);
            },
            animations: true);

        WithWindow(
            (_, toolbar, vm) =>
            {
                Assert.True(vm.Toolbar.ReducedMotion); // the retract is instant too
                Invoke(toolbar.PenButton);
                var flyout = toolbar.FlyoutFor(OptionsPanel.Pen);
                Assert.False(toolbar.IsFlyoutAnimating);
                Assert.False(flyout.HasAnimatedProperties);
                Assert.Equal(1, flyout.Opacity);
                Invoke(toolbar.PenButton);
                Assert.False(toolbar.IsFlyoutAnimating);
                Assert.Equal(Visibility.Collapsed, flyout.Visibility);

                Invoke(toolbar.EraserButton);
                toolbar.UpdateLayout();
                Assert.False(toolbar.IndicatorSlide.HasAnimatedProperties);
                Assert.Equal(toolbar.EraserButton.TranslatePoint(new Point(0, 0), toolbar.ButtonRow).X, toolbar.IndicatorSlide.X, 1);
            },
            animations: false);
    }

    [Fact]
    public void The_toolbar_fades_out_while_writing_and_no_longer_takes_input()
    {
        WithWindow(
            (window, toolbar, vm) =>
            {
                var onPill = Centre(toolbar.PenButton, window.PageHost);
                toolbar.ApplyPhase(ToolbarPhase.Retracting);
                Assert.Equal(TimeSpan.FromMilliseconds(200), toolbar.LastRetractAnimationDuration);
                Assert.True(toolbar.Pill.HasAnimatedProperties);
                Assert.False(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onPill), toolbar));
                Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onPill), window.DocumentViewer));

                toolbar.ApplyPhase(ToolbarPhase.Hidden);
                Assert.Equal(0, toolbar.Pill.Opacity);
                Assert.False(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onPill), toolbar));

                toolbar.ApplyPhase(ToolbarPhase.Visible);
                Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onPill), toolbar.PenButton));
                Assert.Equal(ToolbarPhase.Visible, vm.Toolbar.Phase);
            },
            animations: true);

        WithWindow(
            (_, toolbar, _) =>
            {
                toolbar.ApplyPhase(ToolbarPhase.Hidden);
                Assert.False(toolbar.Pill.HasAnimatedProperties);
                Assert.Equal(0, toolbar.Pill.Opacity);
                toolbar.ApplyPhase(ToolbarPhase.Visible);
                Assert.Equal(1, toolbar.Pill.Opacity);
            },
            animations: false);
    }

    [Fact]
    public void Panels_use_opaque_brushes_with_transparency_off()
    {
        WithWindow((window, toolbar, _) =>
        {
            FloatingSurface[] surfaces =
            [
                toolbar.Pill, toolbar.SurfaceOf(OptionsPanel.Pen), toolbar.SurfaceOf(OptionsPanel.Highlighter),
                toolbar.SurfaceOf(OptionsPanel.Eraser),
            ];

            toolbar.TransparencyEnabled = () => true;
            toolbar.ApplyTransparency();
            Assert.All(surfaces, s =>
            {
                Assert.False(s.IsOpaque);
                Assert.Same(window.FindResource("PanelLayerBrush"), s.DiffuseLayer.Background);
                Assert.True(s.DiffuseLayer.Background.Opacity < 1);
                Assert.Same(window.FindResource("PanelDiffuseShadow"), s.DiffuseLayer.Effect);
                Assert.Same(window.FindResource("PanelContactShadow"), s.ContactLayer.Effect);
            });

            toolbar.TransparencyEnabled = () => false;
            toolbar.ApplyTransparency();
            Assert.All(surfaces, s =>
            {
                Assert.True(s.IsOpaque);
                foreach (var layer in new[] { s.DiffuseLayer, s.ContactLayer })
                {
                    var brush = Assert.IsType<SolidColorBrush>(layer.Background);
                    Assert.Same(window.FindResource("PanelTintBrushOpaque"), brush);
                    Assert.Equal(1, brush.Opacity);
                    Assert.Equal(255, brush.Color.A);
                }
            });
        });
    }

    [Fact]
    public void Automation_names_are_present_on_every_command_of_the_toolbar_and_the_flyouts()
    {
        WithWindow((_, toolbar, _) =>
        {
            Assert.Equal(Strings.ToolbarLabel, AutomationProperties.GetName(toolbar.Pill));
            foreach (var panel in new[] { OptionsPanel.Pen, OptionsPanel.Highlighter, OptionsPanel.Eraser })
            {
                var flyout = OpenFlyout(toolbar, panel);
                flyout.UpdateLayout();
                Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(flyout)));
                var commands = Descendants(flyout)
                    .Where(e => e is System.Windows.Controls.Primitives.ButtonBase or Slider or TextBox)
                    .Cast<FrameworkElement>()
                    .Where(e => e.TemplatedParent is null) // not the parts of a control's template (a slider's repeat buttons)
                    .Where(e => IsShownWithin(e, flyout))
                    .ToList();
                Assert.NotEmpty(commands);
                Assert.All(commands, c => Assert.False(
                    string.IsNullOrEmpty(AutomationProperties.GetName(c)),
                    $"a {c.GetType().Name} in the {panel} flyout has no automation name"));
                toolbar.ViewModel!.ClosePanel();
            }
        });
    }

    [Fact]
    public void The_eraser_flyout_asks_before_clearing_the_page()
    {
        WithWindow((_, toolbar, vm) =>
        {
            var flyout = (EraserOptionsFlyout)OpenFlyout(toolbar, OptionsPanel.Eraser);
            Assert.Same(vm.Toolbar.Eraser.ClearPageCommand, flyout.ClearPageButton.Command);
            Assert.Equal(Visibility.Collapsed, flyout.ConfirmPanel.Visibility);
            Assert.Same(vm.Toolbar.Eraser.ConfirmClearPageCommand, flyout.ConfirmClearButton.Command);
            Assert.Same(vm.Toolbar.Eraser.CancelClearPageCommand, flyout.CancelClearButton.Command);
            Assert.False(flyout.ClearPageButton.IsEnabled); // no page with strokes

            // The mode radio buttons are the one eraser mode (also the pen's back end).
            flyout.PartialRadio.IsChecked = true;
            Assert.Equal(Core.Ink.EraserMode.Partial, vm.Tools.EraserMode);
            flyout.WholeRadio.IsChecked = true;
            Assert.Equal(Core.Ink.EraserMode.WholeStroke, vm.Tools.EraserMode);
        });
    }

    /// <summary>Opens a flyout as the user does: activates its tool if needed, then taps it again.</summary>
    internal static FrameworkElement OpenFlyout(ToolbarView toolbar, OptionsPanel panel)
    {
        var tool = panel switch
        {
            OptionsPanel.Highlighter => ToolbarTool.Highlighter,
            OptionsPanel.Eraser => ToolbarTool.Eraser,
            _ => ToolbarTool.Pen,
        };
        if (toolbar.ViewModel!.ActiveTool != tool)
        {
            Invoke(toolbar.ButtonFor(tool));
        }

        Invoke(toolbar.ButtonFor(tool));
        Assert.Equal(panel, toolbar.ViewModel.OpenPanel);
        var flyout = toolbar.FlyoutFor(panel);
        flyout.UpdateLayout();
        return flyout;
    }

    /// <summary>
    /// Presses <paramref name="button"/> as a screen reader does (the click is posted, then the dispatcher drains). With
    /// <paramref name="drain"/> false its command runs at once and nothing is pumped, so an animation just started is
    /// still running when the test looks (the shared test thread may run other tests' work, and real time, while it
    /// drains).
    /// </summary>
    internal static void Invoke(Button button, bool drain = true)
    {
        if (!drain)
        {
            Assert.True(button.IsEnabled);
            button.Command.Execute(button.CommandParameter);
            return;
        }

        var peer = (ButtonAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(button);
        if (!button.IsEnabled)
        {
            return;
        }

        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        WpfTestThread.Drain();
    }

    internal static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    internal static Point Centre(FrameworkElement element, Visual ancestor)
    {
        var bounds = Bounds(element, ancestor);
        return new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
    }

    /// <summary>Visible up to <paramref name="root"/> (IsVisible needs a shown window).</summary>
    internal static bool IsShownWithin(DependencyObject element, DependencyObject root)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, root); current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>A window laid out at 1200 x 800 DIPs, its toolbar with animations on or off.</summary>
    private static void WithWindow(Action<MainWindow, ToolbarView, MainViewModel> test, bool animations = false)
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                var toolbar = window.FloatingToolbar;
                toolbar.AnimationsEnabled = () => animations;
                toolbar.TransparencyEnabled = () => true;
                toolbar.ApplyMotionSetting();
                ScrollRailTests.LayOut((FrameworkElement)window.Content, _width, _height);
                window.MarksPanel.AnimationsEnabled = () => false;
                test(window, toolbar, (MainViewModel)window.DataContext);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
