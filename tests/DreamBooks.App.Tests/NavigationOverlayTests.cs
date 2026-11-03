using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;
using DreamBooks.Core.Search;
using DreamBooks.Pdf.Tests;
using static DreamBooks.App.Tests.ToolbarViewTests;
using static DreamBooks.Core.Tests.Search.FakeSearchPageSource;

namespace DreamBooks.App.Tests;

/// <summary>
/// The page indicator chip, the navigation buttons above it and the search pill (UI-05, FR-09, FR-13, FR-102,
/// FR-105, phase 4a): what the debug panel's navigation became. The chip and the buttons show and hide with the rail;
/// the pen presses their controls and inks around them (their surfaces carry the inert stylus plug-in). Built on the
/// STA test thread, never shown: keyboard focus itself is not asserted (an unshown window cannot take it).
/// </summary>
public sealed class NavigationOverlayTests : IDisposable
{
    private const int _pageCount = 30;
    private const double _width = 1200;
    private const double _height = 800;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-overlay-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public NavigationOverlayTests() => Directory.CreateDirectory(_dir);

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
    public async Task Chip_shows_the_page_and_zoom_text_bottom_right_and_shows_and_hides_with_the_rail()
    {
        await WithWindowAsync((vm, window) =>
        {
            var chip = window.IndicatorChip;
            var rail = window.DocumentViewer.Rail;
            vm.ZoomVm.Zoom = 1.25;
            ScrollRailTests.Settle(window.DocumentViewer);
            var jump = vm.Viewport.GoToPageCommand.ExecuteAsync(12);
            WpfTestThread.PumpUntil(() => jump.IsCompleted);
            ScrollRailTests.Settle(window.DocumentViewer);

            Assert.Equal("12 / 30 · 125%", vm.Viewport.IndicatorText);
            Assert.Equal(vm.Viewport.IndicatorText, chip.IndicatorButton.Content);
            Assert.Equal(Strings.PageIndicatorLabel, AutomationProperties.GetName(chip.IndicatorButton));
            Assert.Equal(Strings.PageIndicatorLabel, UIElementAutomationPeer.CreatePeerForElement(chip.IndicatorButton).GetName());
            Assert.Equal(Strings.PageIndicatorHelp, AutomationProperties.GetHelpText(chip.IndicatorButton));
            Assert.Equal(Strings.PageIndicatorChipLabel, AutomationProperties.GetName(chip.Surface));
            Assert.True(chip.IndicatorButton.Focusable && chip.IndicatorButton.IsTabStop, "keyboard reachable (NFR-31)");
            Assert.Equal(999, chip.Surface.CornerRadius.TopLeft);

            // Hidden with the rail: invisible, no input, no plug-in: the pen inks on the page there.
            rail.HideNow();
            Assert.False(chip.IsShown);
            Assert.Equal(0, chip.Surface.Opacity);
            Assert.False(chip.Surface.IsHitTestVisible);
            Assert.False(chip.Surface.HasStylusPlugIn);
            window.UpdateLayout();
            var centre = Centre(chip.IndicatorButton, window.PageHost);
            Assert.False(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, centre), chip));
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, centre), window.DocumentViewer));

            // Shown with the rail: the pen presses it (not taken by the page host), and inks just beside it.
            rail.NotifyActivity();
            Assert.True(chip.IsShown);
            Assert.Equal(1, chip.Surface.Opacity);
            Assert.True(chip.Surface.HasStylusPlugIn);
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, centre), chip.IndicatorButton));
            Assert.False(window.RoutePagePress(centre, PointerKind.Pen));
            var surface = Bounds(chip.Surface, window.PageHost);
            foreach (var beside in new[] { new Point(surface.Left - 6, centre.Y), new Point(centre.X, surface.Bottom + 4) })
            {
                Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, beside), window.DocumentViewer), beside.ToString());
            }

            // Bottom right, left of the rail's strip (which stays the rail's), above the bottom edge.
            Assert.True(surface.Right <= window.PageHost.ActualWidth - rail.Strip.Width + 0.5, $"the chip ends at {surface.Right}");
            Assert.InRange(window.PageHost.ActualHeight - surface.Bottom, 0, 40);
            Assert.True(surface.Left > window.PageHost.ActualWidth / 2);
            Assert.True(chip.IndicatorButton.ActualWidth >= 40 && chip.IndicatorButton.ActualHeight >= 40, "40 x 40 touch target");

            // Without a document it is not there at all (not even for the keyboard).
            vm.Viewport.Attach(null, null, null);
            Assert.Equal(Visibility.Collapsed, chip.Visibility);
        });
    }

    [Fact]
    public async Task Go_to_page_field_accepts_valid_numbers_only_Enter_goes_and_Esc_cancels()
    {
        await WithWindowAsync((vm, window) =>
        {
            var chip = window.IndicatorChip;
            var rail = window.DocumentViewer.Rail;
            var viewport = vm.Viewport;
            rail.NotifyActivity();

            // A tap on the indicator (or UI Automation's Invoke) opens the field in its place, the current page selected.
            Invoke(chip.IndicatorButton);
            Assert.True(chip.IsEditing);
            Assert.Equal(Visibility.Visible, chip.GoToPageBox.Visibility);
            Assert.Equal(Visibility.Collapsed, chip.IndicatorButton.Visibility);
            Assert.Equal("1", chip.GoToPageBox.Text);
            Assert.Equal(Strings.GoToPageIndicatorField, AutomationProperties.GetName(chip.GoToPageBox));
            Assert.Equal(Strings.GoToPageHint, AutomationProperties.GetHelpText(chip.GoToPageBox));
            Assert.True(chip.GoToPageBox.MinHeight >= 40);
            Assert.True(rail.State.IsHeld); // the rail (and the chip) stay while typing

            // Invalid numbers are ignored: the field stays, nothing moves.
            foreach (var invalid in new[] { "", "abc", "0", "-3", "31", "2.5", "1e3" })
            {
                chip.GoToPageBox.Text = invalid;
                Assert.True(chip.HandleFieldKey(Key.Enter), invalid); // taken by the field
                Assert.False(chip.ConfirmGoToPage(), invalid);
                Assert.True(chip.IsEditing, invalid);
            }

            Assert.Equal(0, viewport.CurrentPage);
            Assert.False(viewport.History.CanGoBack);

            // Enter on a valid number jumps there (Back returns) and gives the indicator back.
            chip.GoToPageBox.Text = " 20 ";
            Assert.True(chip.HandleFieldKey(Key.Enter));
            ScrollRailTests.Settle(window.DocumentViewer);
            Assert.Equal(19, viewport.CurrentPage);
            Assert.True(viewport.History.CanGoBack);
            Assert.False(chip.IsEditing);
            Assert.Equal(Visibility.Collapsed, chip.GoToPageBox.Visibility);
            Assert.Equal(Visibility.Visible, chip.IndicatorButton.Visibility);
            Assert.False(rail.State.IsHeld);

            // Esc cancels without moving.
            chip.OpenGoToPage();
            chip.GoToPageBox.Text = "3";
            Assert.True(chip.HandleFieldKey(Key.Escape));
            Assert.False(chip.IsEditing);
            Assert.Equal(19, viewport.CurrentPage);
            Assert.False(chip.HandleFieldKey(Key.A)); // other keys are the field's

            // The window's key routing leaves the field's Enter and Esc alone (no flyout, no search taking them).
            chip.OpenGoToPage();
            Assert.False(window.RouteKey(Key.Escape, ModifierKeys.None, chip.GoToPageBox));
            Assert.False(window.RouteKey(Key.Enter, ModifierKeys.None, chip.GoToPageBox));
            Assert.False(window.RouteKey(Key.PageDown, ModifierKeys.None, chip.GoToPageBox));
            chip.CancelGoToPage();
        });
    }

    [Fact]
    public async Task Go_to_page_field_closes_on_any_press_outside_it_and_when_the_document_goes()
    {
        await WithWindowAsync((vm, window) =>
        {
            var chip = window.IndicatorChip;
            var rail = window.DocumentViewer.Rail;
            var page = new Point(500, 400);
            rail.NotifyActivity();
            foreach (var kind in new[] { PointerKind.Pen, PointerKind.Finger, PointerKind.Mouse })
            {
                chip.OpenGoToPage();
                window.UpdateLayout();
                Assert.True(chip.IsEditing);

                // Inside the field: it stays open (the press places the caret), nothing is taken.
                Assert.False(window.RoutePagePress(Centre(chip.GoToPageBox, window.PageHost), kind));
                Assert.True(chip.IsEditing);

                // On the page: closed without moving; the pen still inks (not taken).
                Assert.False(window.RoutePagePress(page, kind));
                Assert.False(chip.IsEditing, kind.ToString());
                Assert.False(rail.State.IsHeld);
                Assert.Equal(0, vm.Viewport.CurrentPage);
            }

            chip.OpenGoToPage();
            chip.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.False(chip.IsEditing);

            chip.OpenGoToPage();
            rail.HideNow(); // another document, or none
            vm.Viewport.Attach(null, null, null);
            Assert.False(chip.IsEditing);
            Assert.False(rail.State.IsHeld);
        });
    }

    [Fact]
    public async Task Zoom_buttons_show_on_hover_long_press_or_keyboard_and_run_the_zoom_commands()
    {
        await WithWindowAsync((vm, window) =>
        {
            var chip = window.IndicatorChip;
            var rail = window.DocumentViewer.Rail;
            var scheduler = new ManualScheduler();
            chip.Scheduler = scheduler;
            rail.NotifyActivity();
            Assert.Same(vm.ZoomVm.ZoomInCommand, chip.ZoomInButton.Command);
            Assert.Same(vm.ZoomVm.ZoomOutCommand, chip.ZoomOutButton.Command);
            Assert.Equal(Strings.ZoomIn, AutomationProperties.GetName(chip.ZoomInButton));
            Assert.Equal(Strings.ZoomOut, AutomationProperties.GetName(chip.ZoomOutButton));
            Assert.False(chip.AreZoomButtonsShown);
            Assert.Equal(Visibility.Collapsed, chip.ZoomInButton.Visibility);

            // Hover (the mouse, or the pen in the air): shown, and the rail stays while it hovers.
            chip.SetHover(true);
            window.UpdateLayout();
            Assert.True(chip.AreZoomButtonsShown);
            Assert.Equal(Visibility.Visible, chip.ZoomInButton.Visibility);
            Assert.True(rail.State.IsHeld);
            foreach (var button in new[] { chip.ZoomOutButton, chip.ZoomInButton })
            {
                Assert.True(button.ActualWidth >= 40 && button.ActualHeight >= 40, "40 x 40 touch target");
                Assert.True(button.Focusable && button.IsTabStop, "keyboard reachable");
            }

            // The buttons are the zoom commands (Ctrl + / Ctrl −): the zoom steps and the indicator follows.
            vm.ZoomVm.Zoom = 1;
            Invoke(chip.ZoomInButton);
            Assert.Equal(1.25, vm.ZoomVm.Zoom, 3);
            Invoke(chip.ZoomOutButton);
            Assert.Equal(1, vm.ZoomVm.Zoom, 3);
            chip.SetHover(false);
            Assert.False(chip.AreZoomButtonsShown);
            Assert.False(rail.State.IsHeld);

            // A long press on the indicator (a finger or the pen): shown, and the tap that ends it opens nothing.
            var hold = (TimeSpan)window.FindResource("LongPressDuration");
            chip.BeginHold(new Point(10, 10));
            scheduler.Advance(hold);
            Assert.True(chip.AreZoomButtonsShown);
            Assert.True(chip.EndHold());
            Invoke(chip.IndicatorButton);
            Assert.False(chip.IsEditing);

            // A short or moving press is a tap: the field opens.
            chip.BeginHold(new Point(10, 10));
            chip.MoveHold(new Point(40, 10));
            scheduler.Advance(hold);
            Assert.False(chip.EndHold());
            chip.BeginHold(new Point(10, 10));
            scheduler.Advance(hold - TimeSpan.FromMilliseconds(1));
            Assert.False(chip.EndHold());
            Invoke(chip.IndicatorButton);
            Assert.True(chip.IsEditing);
            chip.CancelGoToPage();

            // Shown by a long press, they go when the chip hides with the rail.
            Assert.True(chip.AreZoomButtonsShown);
            rail.HideNow();
            Assert.False(chip.AreZoomButtonsShown);

            // The keyboard reaching the chip (Tab) shows it, with the rail, and the zoom buttons for the keyboard.
            chip.SetKeyboardHold(true);
            Assert.True(chip.IsShown);
            Assert.True(rail.State.IsHeld);
            Assert.True(chip.AreZoomButtonsShown);
            chip.SetKeyboardHold(false);
            Assert.False(rail.State.IsHeld);
            Assert.False(chip.AreZoomButtonsShown);
        });
    }

    [Fact]
    public async Task Navigation_buttons_run_the_commands_enable_like_them_and_show_and_hide_with_the_rail()
    {
        await WithWindowAsync((vm, window) =>
        {
            var nav = window.NavButtons;
            var chip = window.IndicatorChip;
            var rail = window.DocumentViewer.Rail;
            rail.NotifyActivity();
            window.UpdateLayout();

            Assert.Same(vm.Viewport.BackCommand, nav.BackButton.Command);
            Assert.Same(vm.Marks.PreviousCommand, nav.PreviousMarkButton.Command);
            Assert.Same(vm.Marks.NextCommand, nav.NextMarkButton.Command);
            Button[] buttons = [nav.BackButton, nav.PreviousMarkButton, nav.NextMarkButton];
            Assert.Equal([Strings.Back, Strings.PreviousMark, Strings.NextMark], buttons.Select(AutomationProperties.GetName));
            Assert.All(buttons, b =>
            {
                Assert.True(b.ActualWidth >= 40 && b.ActualHeight >= 40, "40 x 40 touch target");
                Assert.True(b.Focusable && b.IsTabStop, "keyboard reachable");
                Assert.Equal(b.Command.CanExecute(null), b.IsEnabled);
            });

            // Back is enabled once there is a jump to go back to, and goes back.
            Assert.False(nav.BackButton.IsEnabled);
            var jump = vm.Viewport.GoToPageCommand.ExecuteAsync(12);
            WpfTestThread.PumpUntil(() => jump.IsCompleted);
            Assert.True(nav.BackButton.IsEnabled);
            Invoke(nav.BackButton);
            ScrollRailTests.Settle(window.DocumentViewer);
            Assert.Equal(0, vm.Viewport.CurrentPage);
            Assert.False(nav.BackButton.IsEnabled);

            // Small round buttons in a row above the chip, right-aligned with it.
            rail.NotifyActivity();
            window.UpdateLayout();
            var chipBounds = Bounds(chip.Surface, window.PageHost);
            var xs = buttons.Select(b => Bounds(b, window.PageHost)).ToArray();
            Assert.Equal(xs.Select(r => r.Left).Order(), xs.Select(r => r.Left));
            Assert.All(xs, r => Assert.True(r.Bottom <= chipBounds.Top, "a button reaches into the chip"));
            Assert.Equal(chipBounds.Right, Bounds(nav.Surfaces[^1], window.PageHost).Right, 1);
            Assert.All(nav.Surfaces, s => Assert.Equal(999, s.CornerRadius.TopLeft));

            // The marks' status ("Nessun altro segno") is beside them, shown only while there is one.
            Assert.Equal("Marks.StatusText", BindingOperations.GetBinding(nav.StatusText, TextBlock.TextProperty)?.Path.Path);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(nav.StatusText));
            Assert.Equal(Visibility.Collapsed, nav.StatusSurface.Visibility);

            // Hidden with the rail (no input, no plug-ins: the pen inks there), shown with it.
            var onBack = Centre(nav.BackButton, window.PageHost);
            rail.HideNow();
            Assert.False(nav.IsShown);
            Assert.All(nav.Surfaces, s => Assert.False(s.HasStylusPlugIn));
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onBack), window.DocumentViewer));
            rail.NotifyActivity();
            Assert.True(nav.IsShown);
            Assert.All(nav.Surfaces.Where(s => s.Visibility == Visibility.Visible), s => Assert.True(s.HasStylusPlugIn));
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onBack), nav.BackButton));
            Assert.False(window.RoutePagePress(onBack, PointerKind.Pen)); // the pen presses it

            // Hover or the keyboard holds the rail (and the chip and the buttons) shown.
            nav.SetHover(true);
            Assert.True(rail.State.IsHeld);
            nav.SetHover(false);
            Assert.False(rail.State.IsHeld);
            rail.HideNow();
            nav.SetKeyboardHold(true);
            Assert.True(nav.IsShown && chip.IsShown);
            nav.SetKeyboardHold(false);
        });
    }

    [Fact]
    public async Task Chip_and_buttons_fade_in_150_ms_and_are_instant_with_reduced_motion()
    {
        await WithWindowAsync((_, window) =>
        {
            var chip = window.IndicatorChip;
            var nav = window.NavButtons;
            var rail = window.DocumentViewer.Rail;
            Assert.Equal(TimeSpan.FromMilliseconds(150), (TimeSpan)window.FindResource("NavigationFadeDuration"));

            chip.AnimationsEnabled = () => true;
            nav.AnimationsEnabled = () => true;
            rail.HideNow();
            rail.NotifyActivity();
            Assert.Equal(TimeSpan.FromMilliseconds(150), chip.LastFadeDuration);
            Assert.Equal(TimeSpan.FromMilliseconds(150), nav.LastFadeDuration);
            Assert.True(chip.Surface.HasAnimatedProperties);
            Assert.True(nav.Row.HasAnimatedProperties);

            // Fading out, they take no input at once.
            rail.HideNow();
            Assert.False(chip.Surface.IsHitTestVisible);
            Assert.All(nav.Surfaces, s => Assert.False(s.IsHitTestVisible));

            chip.AnimationsEnabled = () => false;
            nav.AnimationsEnabled = () => false;
            rail.NotifyActivity();
            Assert.False(chip.Surface.HasAnimatedProperties);
            Assert.False(nav.Row.HasAnimatedProperties);
            Assert.Equal(1, chip.Surface.Opacity);
            Assert.Equal(1, nav.Row.Opacity);
            rail.HideNow();
            Assert.Equal(0, chip.Surface.Opacity);
            Assert.Equal(0, nav.Row.Opacity);
        });
    }

    [Fact]
    public void Overlays_use_opaque_brushes_with_transparency_off()
    {
        WithWindow(window =>
        {
            FloatingSurface[] surfaces =
            [
                window.IndicatorChip.Surface, window.SearchBar.Surface, window.SearchBar.MessageSurface,
                .. window.NavButtons.Surfaces,
            ];
            window.IndicatorChip.TransparencyEnabled = () => false;
            window.NavButtons.TransparencyEnabled = () => false;
            window.SearchBar.TransparencyEnabled = () => false;
            window.IndicatorChip.ApplyTransparency();
            window.NavButtons.ApplyTransparency();
            window.SearchBar.ApplyTransparency();
            Assert.All(surfaces, s => Assert.True(s.IsOpaque));

            window.IndicatorChip.TransparencyEnabled = () => true;
            window.NavButtons.TransparencyEnabled = () => true;
            window.SearchBar.TransparencyEnabled = () => true;
            window.IndicatorChip.ApplyTransparency();
            window.NavButtons.ApplyTransparency();
            window.SearchBar.ApplyTransparency();
            Assert.All(surfaces, s => Assert.False(s.IsOpaque));
        });
    }

    [Fact]
    public void Search_pill_opens_on_Ctrl_F_and_Esc_closes_it_and_clears_the_search()
    {
        WithWindow(window =>
        {
            var vm = (MainViewModel)window.DataContext;
            var search = vm.Search;
            var pill = window.SearchBar;
            Assert.Equal(Visibility.Collapsed, pill.Visibility);
            Assert.False(window.RouteKey(Key.Escape, ModifierKeys.None, null)); // nothing to close

            // Ctrl+F opens it and asks for the field's focus.
            var requests = window.SearchFocusRequests;
            Assert.True(window.RouteKey(Key.F, ModifierKeys.Control, null));
            Assert.True(search.IsOpen);
            Assert.Equal(Visibility.Visible, pill.Visibility);
            Assert.Equal(requests + 1, window.SearchFocusRequests);
            WpfTestThread.Drain();

            // Top centre, below the title bar.
            window.UpdateLayout();
            var bounds = Bounds(pill.Surface, window.PageHost);
            Assert.Equal(window.PageHost.ActualWidth / 2, bounds.X + (bounds.Width / 2), 1);
            Assert.True(bounds.Top >= ((Thickness)window.FindResource("TitleBarClearance")).Top);
            Assert.Equal(999, pill.Surface.CornerRadius.TopLeft);

            // Esc in its field: the search is cleared, the pill closes and the keyboard goes back to the document.
            search.QueryText = "sole";
            var focus = window.DocumentFocusRequests;
            Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, pill.Field));
            Assert.Equal(string.Empty, search.QueryText);
            Assert.False(search.IsOpen);
            Assert.Equal(Visibility.Collapsed, pill.Visibility);
            Assert.Equal(focus + 1, window.DocumentFocusRequests);

            // Esc elsewhere (not in another field) closes an open pill, even with an empty field.
            Assert.True(window.RouteKey(Key.F, ModifierKeys.Control, null));
            Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, null));
            Assert.False(search.IsOpen);

            // With a flyout open, Esc in the pill's field is still the search's.
            Assert.True(window.RouteKey(Key.F, ModifierKeys.Control, null));
            Invoke(window.FloatingToolbar.PenButton); // the pen is active: the second tap opens its panel
            Assert.Equal(OptionsPanel.Pen, vm.Toolbar.OpenPanel);
            Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, pill.Field));
            Assert.False(search.IsOpen);
            Assert.Equal(OptionsPanel.Pen, vm.Toolbar.OpenPanel);
            vm.Toolbar.ClosePanel();

            // The close button clears and closes too.
            Assert.True(window.RouteKey(Key.F, ModifierKeys.Control, null));
            search.QueryText = "x";
            Invoke(pill.CloseButton);
            Assert.Equal(string.Empty, search.QueryText);
            Assert.Equal(Visibility.Collapsed, pill.Visibility);

            // A query typed in the old panel's field opens it: the results always have their buttons.
            search.QueryText = "luna";
            Assert.Equal(Visibility.Visible, pill.Visibility);
            search.ClearCommand.Execute(null);
        });
    }

    [Fact]
    public void Search_pill_binds_the_field_the_count_and_next_previous_to_the_search()
    {
        WithWindow(window =>
        {
            var vm = (MainViewModel)window.DataContext;
            var search = vm.Search;
            var pill = window.SearchBar;
            Assert.True(window.RouteKey(Key.F, ModifierKeys.Control, null));
            window.UpdateLayout();

            Assert.Same(search.PreviousCommand, pill.PreviousButton.Command);
            Assert.Same(search.NextCommand, pill.NextButton.Command);
            Assert.Same(search.ClearCommand, pill.CloseButton.Command);
            Assert.Equal(Strings.SearchField, AutomationProperties.GetName(pill.Field));
            Assert.Equal(Strings.SearchHint, AutomationProperties.GetHelpText(pill.Field));
            Assert.Equal(Strings.SearchPillLabel, AutomationProperties.GetName(pill.Surface));
            Assert.Equal(Strings.SearchCountLabel, AutomationProperties.GetName(pill.CountText));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(pill.CountText));
            Button[] buttons = [pill.PreviousButton, pill.NextButton, pill.CloseButton];
            Assert.Equal([Strings.PreviousResult, Strings.NextResult, Strings.CloseSearch], buttons.Select(AutomationProperties.GetName));
            Assert.All(buttons, b =>
            {
                Assert.True(b.ActualWidth >= 40 && b.ActualHeight >= 40, "40 x 40 touch target");
                Assert.True(b.Focusable && b.IsTabStop, "keyboard reachable");
            });
            Assert.True(pill.Field.MinHeight >= 40);

            // The field is the query, two ways.
            pill.Field.Text = "sole";
            Assert.Equal("sole", search.QueryText);
            search.QueryText = "luna";
            Assert.Equal("luna", pill.Field.Text);
            search.QueryText = string.Empty;

            // A search with three results, without a real document: a layout and an index are enough.
            var layout = new DocumentLayout(Enumerable.Repeat(new PageDimensions(612, 792), 6).ToArray());
            vm.Viewport.Attach(null, layout, null);
            var index = new SearchIndex(6);
            search.Attach(index);
            index.SetPage(1, [Hit(1, 100)]);
            index.SetPage(3, [Hit(3, 100), Hit(3, 400)]);
            for (var p = 0; p < 6; p++)
            {
                if (!index.IsSearched(p))
                {
                    index.SetPage(p, []);
                }
            }

            WpfTestThread.PumpUntil(() => search.HasResults);
            Assert.True(pill.NextButton.IsEnabled && pill.PreviousButton.IsEnabled);

            // Enter / Shift+Enter in the pill's field, the buttons and F3 all step through the results.
            Assert.True(window.RouteKey(Key.Enter, ModifierKeys.None, pill.Field));
            Assert.Equal(Hit(1, 100), search.Current);
            Invoke(pill.NextButton);
            Assert.Equal(Hit(3, 100), search.Current);
            Assert.True(window.RouteKey(Key.F3, ModifierKeys.None, null));
            Assert.Equal(Hit(3, 400), search.Current);
            Assert.True(window.RouteKey(Key.Enter, ModifierKeys.Shift, pill.Field));
            Assert.Equal(Hit(3, 100), search.Current);
            Invoke(pill.PreviousButton);
            Assert.Equal(Hit(1, 100), search.Current);
            WpfTestThread.Drain();
            Assert.Equal(search.CountText, pill.CountText.Text);
            Assert.False(string.IsNullOrEmpty(pill.CountText.Text));

            // The messages ("Ripartito dall'inizio", "no text", unread pages) are bound below the pill.
            Assert.Equal("StatusText", BindingOperations.GetBinding(pill.StatusText, TextBlock.TextProperty)?.Path.Path);
            Assert.Equal("NoTextLayerMessage", BindingOperations.GetBinding(pill.NoTextText, TextBlock.TextProperty)?.Path.Path);
            Assert.Equal("FailedPagesText", BindingOperations.GetBinding(pill.FailedPagesText, TextBlock.TextProperty)?.Path.Path);
            Invoke(pill.PreviousButton); // wraps to the last result
            WpfTestThread.Drain();
            Assert.Equal(Strings.SearchWrappedToEnd, search.StatusText);
            Assert.Equal(Visibility.Visible, pill.MessageSurface.Visibility);
            Assert.Equal(Visibility.Visible, pill.StatusText.Visibility);
            Assert.Equal(Visibility.Collapsed, pill.NoTextText.Visibility);
            search.Attach(null); // the search goes (with a document the clear command does this through the main view model)
            Assert.Null(search.StatusText);
            Assert.Equal(Visibility.Collapsed, pill.MessageSurface.Visibility);
            Assert.Equal(Visibility.Collapsed, pill.StatusText.Visibility);
            search.ClearCommand.Execute(null);
        });
    }

    [Fact]
    public void Pen_at_the_pill_surround_inks_and_on_its_field_and_buttons_uses_them()
    {
        WithWindow(window =>
        {
            var pill = window.SearchBar;
            Assert.True(window.RouteKey(Key.F, ModifierKeys.Control, null));
            window.UpdateLayout();
            Assert.True(pill.Surface.HasStylusPlugIn); // no wet ink under the pill when the pen presses a control

            var surface = Bounds(pill.Surface, window.PageHost);
            var middle = surface.Top + (surface.Height / 2);
            foreach (var around in new[]
                     {
                         new Point(surface.Left - 6, middle), new Point(surface.Right + 6, middle),
                         new Point(surface.Left + (surface.Width / 2), surface.Bottom + 6),
                     })
            {
                var hit = ScrollRailTests.HitAt(window.PageHost, around);
                Assert.False(ScrollRailTests.IsWithin(hit, pill), around.ToString());
                Assert.True(ScrollRailTests.IsWithin(hit, window.DocumentViewer), around.ToString());
                Assert.False(window.RoutePagePress(around, PointerKind.Pen));
            }

            foreach (var control in new FrameworkElement[] { pill.Field, pill.PreviousButton, pill.NextButton, pill.CloseButton })
            {
                var centre = Centre(control, window.PageHost);
                Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, centre), control), control.Name);
                Assert.False(window.RoutePagePress(centre, PointerKind.Pen)); // left to the control
            }

            // Closed: nothing of it takes the pen.
            ((MainViewModel)window.DataContext).Search.ClearCommand.Execute(null);
            var field = new Point(surface.Left + (surface.Width / 2), middle);
            Assert.False(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, field), pill));
        });
    }

    [Fact]
    public void Every_new_string_is_in_the_resx_and_the_designer()
    {
        foreach (var key in new[]
                 {
                     "PageIndicatorChipLabel", "PageIndicatorHelp", "ZoomIn", "ZoomOut", "NavigationLabel", "SearchPillLabel",
                     "CloseSearch",
                 })
        {
            var property = typeof(Strings).GetProperty(key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.True(property is not null, $"Strings is missing '{key}'.");
            Assert.False(string.IsNullOrEmpty((string?)property.GetValue(null)), key);
            Assert.NotEqual(key, (string?)property.GetValue(null)); // a resx miss would return the key
        }
    }

    /// <summary>A window without a document, laid out at 1200 x 800 DIPs, its overlays without animations.</summary>
    private static void WithWindow(Action<MainWindow> test)
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                Prepare(window);
                ScrollRailTests.LayOut((FrameworkElement)window.Content, _width, _height);
                test(window);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Prepare(MainWindow window)
    {
        window.MarksPanel.AnimationsEnabled = () => false;
        window.FloatingToolbar.AnimationsEnabled = () => false;
        window.IndicatorChip.AnimationsEnabled = () => false;
        window.NavButtons.AnimationsEnabled = () => false;
        window.SearchBar.AnimationsEnabled = () => false;
    }

    /// <summary>Opens a 30-page generated PDF in a window laid out at 1200 x 800 DIPs.</summary>
    private async Task WithWindowAsync(Action<MainViewModel, MainWindow> test)
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfFactory.Write(path, Enumerable.Repeat((612.0, 792.0), _pageCount).ToArray());
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path }, scheduler: _scheduler)
        {
            MarkScanIdleGap = TimeSpan.Zero,
        };
        await vm.OpenPdfCommand.ExecuteAsync(null);

        // The mark scan must end before the window binds the view (see EdgePanelTests.WithWindowAsync).
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
                    window.DocumentViewer.Rail.AnimationsOverride = false;
                    Prepare(window);
                    ScrollRailTests.LayOut((FrameworkElement)window.Content, _width, _height);
                    ScrollRailTests.Settle(window.DocumentViewer);
                    test(vm, window);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
    }
}
