using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// The fading scroll rail with its page preview, the page indicator with go to page, the navigation keys and the back
/// button (UI-07, FR-09, FR-10, NFR-32). The fade timing is a pure state machine on an injected clock; the rail itself
/// runs on the real WPF dispatcher (never shown). The look and feel of the fades is checked on the device.
/// </summary>
public sealed class ScrollRailTests : IDisposable
{
    private const int _pageCount = 30;
    private const double _widthPt = 612;
    private const double _heightPt = 792;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-rail-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public ScrollRailTests() => Directory.CreateDirectory(_dir);

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
    public void Rail_state_machine_fades_in_visible_and_out_after_idle_with_the_stated_durations()
    {
        var timings = ThemeTimings();
        Assert.Equal(TimeSpan.FromMilliseconds(180), timings.FadeIn);
        Assert.Equal(TimeSpan.FromMilliseconds(200), timings.FadeOut);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), timings.Idle);

        var now = TimeSpan.Zero;
        var state = new RailVisibilityState(() => now, timings);
        Assert.Equal(RailPhase.Hidden, state.Phase);
        Assert.Equal(0, state.Opacity);
        Assert.Null(state.TimeToNextChange);

        state.Activity();
        Assert.Equal(RailPhase.FadingIn, state.Phase);
        Assert.Equal(0, state.Opacity, 9);
        Assert.Equal(TimeSpan.FromMilliseconds(180), state.TimeToNextChange);

        now = TimeSpan.FromMilliseconds(90);
        Assert.Equal(RailPhase.FadingIn, state.Phase);
        Assert.InRange(state.Opacity, 0.6, 0.99); // ease-out: more than half way at half the time

        now = TimeSpan.FromMilliseconds(180);
        Assert.Equal(RailPhase.Visible, state.Phase);
        Assert.Equal(1, state.Opacity);
        Assert.Equal(TimeSpan.FromMilliseconds(1320), state.TimeToNextChange); // idle counted from the activity

        now = TimeSpan.FromMilliseconds(1499);
        Assert.Equal(RailPhase.Visible, state.Phase);

        now = TimeSpan.FromMilliseconds(1600);
        Assert.Equal(RailPhase.FadingOut, state.Phase);
        Assert.InRange(state.Opacity, 0.01, 0.4); // ease-out on the way out too
        Assert.Equal(TimeSpan.FromMilliseconds(100), state.TimeToNextChange);

        now = TimeSpan.FromMilliseconds(1700);
        Assert.Equal(RailPhase.Hidden, state.Phase);
        Assert.Equal(0, state.Opacity);
        Assert.Null(state.TimeToNextChange);

        // Held (drag, hover, typing a page): never fades; idle counts again from the release.
        state.Activity();
        state.IsHeld = true;
        now += TimeSpan.FromSeconds(10);
        Assert.Equal(RailPhase.Visible, state.Phase);
        Assert.Null(state.TimeToNextChange);
        state.IsHeld = false;
        now += TimeSpan.FromMilliseconds(1499);
        Assert.Equal(RailPhase.Visible, state.Phase);
        now += TimeSpan.FromMilliseconds(301);
        Assert.Equal(RailPhase.Hidden, state.Phase);

        state.Activity();
        state.Hide(); // the document went away: at once
        Assert.Equal(RailPhase.Hidden, state.Phase);
    }

    [Fact]
    public void Reduced_motion_makes_changes_instant()
    {
        var now = TimeSpan.Zero;
        var state = new RailVisibilityState(() => now, new RailTimings(
            TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(1500)))
        {
            ReducedMotion = true,
        };

        state.Activity();
        Assert.Equal(RailPhase.Visible, state.Phase);
        Assert.Equal(1, state.Opacity);

        now = TimeSpan.FromMilliseconds(1499);
        Assert.Equal(RailPhase.Visible, state.Phase);
        now = TimeSpan.FromMilliseconds(1500);
        Assert.Equal(RailPhase.Hidden, state.Phase);
        Assert.Equal(0, state.Opacity);
    }

    [Fact]
    public void Scroll_activity_resets_the_idle_timer()
    {
        var now = TimeSpan.Zero;
        var state = new RailVisibilityState(() => now, ThemeTimings());
        state.Activity();
        now = TimeSpan.FromMilliseconds(1000);
        state.Activity();
        now = TimeSpan.FromMilliseconds(2400);
        Assert.Equal(RailPhase.Visible, state.Phase);
        now = TimeSpan.FromMilliseconds(2600);
        Assert.Equal(RailPhase.FadingOut, state.Phase);
        var fading = state.Opacity;

        // Scrolling again while it fades out brings it back from where it is, without a flash.
        state.Activity();
        Assert.Equal(RailPhase.FadingIn, state.Phase);
        Assert.Equal(fading, state.Opacity, 9);
        now += TimeSpan.FromMilliseconds(180);
        Assert.Equal(RailPhase.Visible, state.Phase);
    }

    [Fact]
    public async Task Drag_scrubs_continuously_and_pushes_history_once()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var pushes = 0;
            vm.Viewport.History.Changed += (_, _) => pushes++;
            var length = rail.TrackLength;
            Assert.True(length > 100);

            rail.PointerDown(rail.KnobHeight / 2); // on the knob, at the top
            Assert.True(rail.IsDragging);
            var previous = -1.0;
            foreach (var fraction in new[] { 0.1, 0.25, 0.5, 0.75 })
            {
                rail.PointerMove((rail.KnobHeight / 2) + (fraction * length));
                Settle(view);
                var offset = view.Viewport.VerticalOffset;
                Assert.True(offset > previous);
                Near(fraction * Scrollable(view), offset);
                previous = offset;
            }

            rail.PointerUp();
            Assert.False(rail.IsDragging);
            Assert.Equal(1, pushes);

            vm.Viewport.BackCommand.Execute(null); // back to where the drag started
            Settle(view);
            Near(0, view.Viewport.VerticalOffset);
        });
    }

    [Fact]
    public async Task Track_tap_jumps_to_that_position()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var y = (rail.KnobHeight / 2) + (0.6 * rail.TrackLength); // well below the knob
            rail.PointerDown(y);
            rail.PointerUp();
            Settle(view);

            Near(0.6 * Scrollable(view), view.Viewport.VerticalOffset);
            Assert.True(vm.Viewport.History.CanGoBack); // a jump: Back returns
            Near(0.6 * rail.TrackLength, rail.KnobOffset.Y);
        });
    }

    [Fact]
    public async Task Rail_drag_does_not_scroll_while_a_page_is_inking()
    {
        await WithViewAsync((vm, view) =>
        {
            LoadEditors(view);
            var surface = view.SurfaceFor(0)!;
            surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
            Assert.True(surface.IsInking);

            view.Rail.PointerDown((view.Rail.KnobHeight / 2) + (0.5 * view.Rail.TrackLength));
            view.Rail.PointerUp();
            Settle(view);
            Near(0, view.Viewport.VerticalOffset);

            surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
        });
    }

    [Fact]
    public async Task Preview_bubble_shows_the_page_number_and_requests_a_preview()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            Assert.NotNull(rail.PreviewSource); // wired to the bitmap coordinator by the view
            var requests = new List<(int Page, CancellationToken Token, TaskCompletionSource<BitmapSource?> Result)>();
            rail.PreviewSource = (page, ct) =>
            {
                var result = new TaskCompletionSource<BitmapSource?>();
                requests.Add((page, ct, result));
                return result.Task;
            };

            Assert.Equal(Visibility.Collapsed, rail.PreviewBubble.Visibility);
            Assert.Equal(Strings.RailPreviewLabel, AutomationProperties.GetName(rail.PreviewBubble));
            rail.PointerDown(rail.KnobHeight / 2);
            rail.PointerMove((rail.KnobHeight / 2) + (0.5 * rail.TrackLength));
            Settle(view);

            var page = vm.Viewport.CurrentPage;
            Assert.InRange(page, 10, 20);
            Assert.Equal(Visibility.Visible, rail.PreviewBubble.Visibility);
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.RailPreviewPageFormat, page + 1), rail.PreviewText.Text);
            var request = requests[^1];
            Assert.Equal(page, request.Page);
            Assert.Null(rail.PreviewImage.Source); // the white placeholder until the preview arrives
            Assert.True(rail.PreviewPlaceholder.Height > 0);

            var bitmap = FakeBitmapRenderer.BitmapOf(30, 40);
            request.Result.SetResult(bitmap);
            WpfTestThread.Drain();
            Assert.Same(bitmap, rail.PreviewImage.Source);

            // Moving on to another page asks for that one and gives up the previous request (the latest wins).
            rail.PointerMove((rail.KnobHeight / 2) + (0.9 * rail.TrackLength));
            Settle(view);
            Assert.NotEqual(page, vm.Viewport.CurrentPage);
            Assert.Equal(vm.Viewport.CurrentPage, requests[^1].Page);
            Assert.Null(rail.PreviewImage.Source);
            rail.PointerMove((rail.KnobHeight / 2) + (0.2 * rail.TrackLength));
            Settle(view);
            Assert.True(requests[^2].Token.IsCancellationRequested);
            rail.PointerUp();

            // The bubble does not hit-test: it never takes the pen from the page under it.
            Assert.False(rail.PreviewBubble.IsHitTestVisible);
        });
    }

    [Fact]
    public async Task Indicator_text_formats_page_count_and_zoom()
    {
        await WithViewAsync((vm, view) =>
        {
            vm.ZoomVm.Zoom = 1.25;
            Settle(view);
            var jump = vm.Viewport.GoToPageCommand.ExecuteAsync(12);
            WpfTestThread.PumpUntil(() => jump.IsCompleted);
            Settle(view);

            Assert.Equal("12 / 30 · 125%", vm.Viewport.IndicatorText);
            Assert.Equal(vm.Viewport.IndicatorText, view.Rail.IndicatorButton.Content);
            Assert.Equal(Strings.PageIndicatorLabel, AutomationProperties.GetName(view.Rail.IndicatorButton));
            Assert.True(view.Rail.IndicatorButton.MinHeight >= 40);
            Assert.False(view.Rail.IndicatorButton.Focusable); // tapping it never takes focus from the document
        });
    }

    [Fact]
    public async Task Go_to_page_field_accepts_valid_input_and_ignores_invalid()
    {
        await WithViewAsync((vm, view) =>
        {
            var viewport = vm.Viewport;
            foreach (var invalid in new[] { "", "  ", "abc", "0", "-3", "31", "2.5", "1e3" })
            {
                Assert.False(viewport.TryGoToPage(invalid), invalid);
            }

            Assert.Equal(0, viewport.CurrentPage);
            Assert.False(viewport.History.CanGoBack);

            Assert.True(viewport.TryGoToPage(" 7 "));
            Assert.Equal(6, viewport.CurrentPage);
            Assert.True(viewport.History.CanGoBack);

            // The rail's field: tap the indicator, type, Enter; invalid text keeps it open, Esc closes it.
            var rail = view.Rail;
            rail.OpenGoToPage();
            Assert.Equal(Visibility.Visible, rail.GoToPageBox.Visibility);
            Assert.Equal(Visibility.Collapsed, rail.IndicatorButton.Visibility);
            Assert.Equal("7", rail.GoToPageBox.Text);
            Assert.Equal(Strings.GoToPage, AutomationProperties.GetName(rail.GoToPageBox));
            Assert.Equal(Strings.GoToPageHint, AutomationProperties.GetHelpText(rail.GoToPageBox));
            Assert.True(rail.State.IsHeld); // the rail stays while typing

            rail.GoToPageBox.Text = "xyz";
            Assert.False(rail.ConfirmGoToPage());
            Assert.Equal(Visibility.Visible, rail.GoToPageBox.Visibility);
            Assert.Equal(6, viewport.CurrentPage);

            rail.GoToPageBox.Text = "20";
            Assert.True(rail.ConfirmGoToPage());
            Settle(view);
            Assert.Equal(19, viewport.CurrentPage);
            Assert.Equal(Visibility.Collapsed, rail.GoToPageBox.Visibility);
            Assert.Equal(Visibility.Visible, rail.IndicatorButton.Visibility);

            rail.OpenGoToPage();
            rail.GoToPageBox.Text = "3";
            rail.CancelGoToPage();
            Assert.Equal(19, viewport.CurrentPage);
            Assert.Equal(Visibility.Collapsed, rail.GoToPageBox.Visibility);
            Assert.False(rail.State.IsHeld);
        });
    }

    [Fact]
    public async Task PgUp_PgDn_Home_End_move_by_viewport_page_and_to_the_ends()
    {
        await WithViewAsync((vm, view) =>
        {
            var viewport = vm.Viewport;
            var step = view.Viewport.ViewportHeight * DocumentViewportViewModel.PageStepFraction;

            Assert.True(view.HandleNavigationKey(Key.PageDown, ModifierKeys.None, view.Viewport));
            Settle(view);
            Near(step, view.Viewport.VerticalOffset);
            Assert.True(view.HandleNavigationKey(Key.PageDown, ModifierKeys.None, view.Viewport));
            Settle(view);
            Near(2 * step, view.Viewport.VerticalOffset);
            Assert.True(view.HandleNavigationKey(Key.PageUp, ModifierKeys.None, view.Viewport));
            Settle(view);
            Near(step, view.Viewport.VerticalOffset);
            Assert.False(viewport.History.CanGoBack); // a page step is not a jump

            Assert.True(view.HandleNavigationKey(Key.End, ModifierKeys.None, view.Viewport));
            Settle(view);
            Near(Scrollable(view), view.Viewport.VerticalOffset);
            Assert.Equal(_pageCount - 1, viewport.CurrentPage);
            Assert.True(viewport.History.CanGoBack);

            Assert.True(view.HandleNavigationKey(Key.Home, ModifierKeys.None, view.Viewport));
            Settle(view);
            Near(0, view.Viewport.VerticalOffset);

            // Not while typing in a field, nor on a slider, nor with modifiers, nor for other keys.
            var offset = view.Viewport.VerticalOffset;
            Assert.False(view.HandleNavigationKey(Key.PageDown, ModifierKeys.None, view.Rail.GoToPageBox));
            Assert.False(view.HandleNavigationKey(Key.End, ModifierKeys.None, new TextBox()));
            Assert.False(view.HandleNavigationKey(Key.PageDown, ModifierKeys.None, new Slider()));
            Assert.False(view.HandleNavigationKey(Key.End, ModifierKeys.Control, view.Viewport));
            Assert.False(view.HandleNavigationKey(Key.A, ModifierKeys.None, view.Viewport));
            Settle(view);
            Near(offset, view.Viewport.VerticalOffset);
        });
    }

    [Fact]
    public async Task Shift_wheel_pans_horizontally_and_the_wheel_over_the_rail_scrolls()
    {
        await WithViewAsync((vm, view) =>
        {
            vm.ZoomVm.Zoom = 3; // much wider than the window
            Settle(view);
            var x = view.Viewport.HorizontalOffset;
            var y = view.Viewport.VerticalOffset;

            view.ScrollByWheel(-120, horizontal: true); // Shift + wheel down: to the right
            Settle(view);
            Assert.True(view.Viewport.HorizontalOffset > x);
            Near(y, view.Viewport.VerticalOffset);

            view.ScrollByWheel(-120, horizontal: false); // the wheel over the rail: down
            Settle(view);
            Assert.True(view.Viewport.VerticalOffset > y);
        });
    }

    [Fact]
    public async Task Alt_Left_goes_back()
    {
        await WithViewAsync((vm, view) =>
        {
            var jump = vm.Viewport.GoToPageCommand.ExecuteAsync(15);
            WpfTestThread.PumpUntil(() => jump.IsCompleted);
            Settle(view);
            Assert.Equal(14, vm.Viewport.CurrentPage);

            var window = new MainWindow();
            try
            {
                window.DataContext = vm;
                LayOut((FrameworkElement)window.Content, 1000, 700);
                var back = window.InputBindings.OfType<KeyBinding>()
                    .Single(b => b.Key == Key.Left && b.Modifiers == ModifierKeys.Alt);
                Assert.Same(vm.Viewport.BackCommand, back.Command);
                Assert.True(back.Command.CanExecute(null));
                back.Command.Execute(null);
                Assert.Equal(0, vm.Viewport.CurrentPage);
                Assert.False(back.Command.CanExecute(null));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Rail_does_not_block_pen_input_outside_its_strip()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var strip = new Point(view.ActualWidth - 10, 400);
            var page = new Point(500, 400);
            var besideStrip = new Point(view.ActualWidth - 60, 400);

            // Hidden: nothing of the rail hit-tests, the right edge of the page inks too.
            rail.HideNow();
            Assert.False(IsWithin(HitAt(view, strip), rail));
            Assert.True(IsWithin(HitAt(view, strip), view.Viewport));

            rail.NotifyActivity();
            Assert.True(IsWithin(HitAt(view, strip), rail.Strip)); // the 40 px strip is the rail's
            Assert.True(rail.Strip.ActualWidth <= 40.0001 && rail.Strip.ActualWidth >= 40);
            Assert.True(IsWithin(HitAt(view, page), view.SurfaceFor(vm.Viewport.CurrentPage)!));
            Assert.False(IsWithin(HitAt(view, besideStrip), rail));

            // Dragging: the bubble beside the knob is shown, but the page under it still takes the pen.
            rail.PointerDown(rail.KnobHeight / 2);
            Settle(view);
            var bubble = rail.PreviewBubble.TranslatePoint(
                new Point(rail.PreviewBubble.ActualWidth / 2, rail.PreviewBubble.ActualHeight / 2), view);
            Assert.False(IsWithin(HitAt(view, bubble), rail));
            Assert.True(IsWithin(HitAt(view, bubble), view.Viewport));
            rail.PointerUp();

            Assert.False(rail.Focusable);
            Assert.False(rail.Strip.Focusable);
        });
    }

    [Fact]
    public async Task Every_page_surface_has_its_own_automation_name()
    {
        await WithViewAsync((vm, view) =>
        {
            var names = view.ActiveSurfaces.ToDictionary(p => p.Key, p => AutomationProperties.GetName(p.Value));
            Assert.True(names.Count > 1);
            foreach (var (index, name) in names)
            {
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.PageNameFormat, index + 1), name);
            }

            Assert.Equal(names.Count, names.Values.Distinct().Count());
            Assert.Equal(Strings.RailLabel, AutomationProperties.GetName(view.Rail.Strip));
        });
    }

    [Fact]
    public void Main_window_has_the_rail_the_back_button_and_the_go_to_page_field()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                LayOut((FrameworkElement)window.Content, 1000, 700);
                var vm = (MainViewModel)window.DataContext;
                Assert.NotNull(window.DocumentViewer.Rail);
                Assert.Same(vm.Viewport.BackCommand, window.BackButton.Command);
                Assert.False(window.BackButton.IsEnabled); // nothing to go back to
                Assert.Equal(Strings.Back, AutomationProperties.GetName(window.BackButton));
                Assert.True(window.BackButton.MinHeight >= 40);
                Assert.Equal(Strings.GoToPage, AutomationProperties.GetName(window.GoToPageField));
                Assert.Equal(Strings.GoToPageHint, AutomationProperties.GetHelpText(window.GoToPageField));
                Assert.True(window.GoToPageField.MinHeight >= 40);
                Assert.Equal(Visibility.Visible, window.GoToPageField.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static RailTimings ThemeTimings()
    {
        RailTimings timings = default;
        WpfTestThread.Run(() => timings = ScrollRail.TimingsFrom(key => Application.Current.TryFindResource(key)));
        return timings;
    }

    /// <summary>Within one DIP: offsets go through page points and back.</summary>
    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 1, expected + 1);

    private static double Scrollable(DocumentView view) => view.Viewport.ExtentHeight - view.Viewport.ViewportHeight;

    /// <summary>
    /// Where input at a point goes: a visual hit test that skips what input routing skips (elements that are not
    /// hit-testable or not visible, with their children). InputHitTest itself needs a shown window.
    /// </summary>
    private static DependencyObject? HitAt(Visual root, Point point)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(
            root,
            candidate => candidate is UIElement { IsHitTestVisible: false } or UIElement { Visibility: not Visibility.Visible }
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result =>
            {
                hit = result.VisualHit;
                return HitTestResultBehavior.Stop;
            },
            new PointHitTestParameters(point));
        return hit;
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Opens a 30-page generated PDF, then runs the test on a view laid out at 1000 x 800 DIPs.</summary>
    private async Task WithViewAsync(Action<MainViewModel, DocumentView> test)
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfFactory.Write(path, Enumerable.Repeat((_widthPt, _heightPt), _pageCount).ToArray());
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path }, scheduler: _scheduler);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            WpfTestThread.Run(() =>
            {
                var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer() };
                view.DataContext = vm;
                LayOut(view, 1000, 800);
                test(vm, view);
            });
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
    }

    private static void LayOut(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        if (element is DocumentView view)
        {
            Settle(view);
        }
        else
        {
            element.UpdateLayout();
        }
    }

    private static void Settle(DocumentView view)
    {
        for (var i = 0; i < 6; i++)
        {
            view.UpdateLayout();
            WpfTestThread.Drain();
        }
    }

    private static void LoadEditors(DocumentView view)
    {
        view.FlushEditorLoads();
        WpfTestThread.PumpUntil(() => view.ActiveSurfaces.Values.All(s => s.Editor is not null));
    }
}
