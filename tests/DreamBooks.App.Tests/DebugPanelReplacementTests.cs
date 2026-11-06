using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Ink;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// Review focus 2 of phase 4a: nothing the spartan debug panel did is lost. Each of its functions (spec section 5) is
/// listed with its new home in the window: a named control bound to the same command or view model property, and the
/// shortcut where there was one. The window is built over a real 30-page PDF, never shown.
/// </summary>
public sealed class DebugPanelReplacementTests : IDisposable
{
    private const int _pageCount = 30;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-replace-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public DebugPanelReplacementTests() => Directory.CreateDirectory(_dir);

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
    public async Task Every_debug_panel_function_has_a_reachable_replacement()
    {
        await WithWindowAsync((vm, window) =>
        {
            var menu = window.MainMenu;
            var toolbar = window.FloatingToolbar;
            var pen = toolbar.PenFlyout;
            var highlighter = toolbar.HighlighterFlyout;
            var eraser = toolbar.EraserFlyout;
            var chip = window.IndicatorChip;
            var nav = window.NavButtons;
            var pill = window.SearchBar;
            var gestures = window.InputBindings.OfType<KeyBinding>().Select(b => (b.Key, b.Modifiers, b.Command)).ToList();
            var checkedFunctions = new List<string>();

            void Command(string function, ICommandSource source, ICommand expected, string name)
            {
                Assert.Same(expected, source.Command);
                Assert.Equal(name, AutomationProperties.GetName((DependencyObject)source));
                checkedFunctions.Add(function);
            }

            void Bound(string function, FrameworkElement element, DependencyProperty property, string path)
            {
                Assert.Equal(path, BindingOperations.GetBinding(element, property)?.Path.Path);
                checkedFunctions.Add(function);
            }

            void Shortcut(Key key, ModifierKeys modifiers, ICommand command) =>
                Assert.Contains((key, modifiers, command), gestures);

            // Open, Save, Save as: the main menu (UI-03), Ctrl+O / Ctrl+S / Ctrl+Shift+S as before.
            Command("Open", menu.OpenItem, vm.OpenPdfCommand, Strings.OpenPdf);
            Shortcut(Key.O, ModifierKeys.Control, vm.OpenPdfCommand);
            Command("Save", menu.SaveItem, vm.SaveCommand, Strings.Save);
            Shortcut(Key.S, ModifierKeys.Control, vm.SaveCommand);
            Command("Save as", menu.SaveAsItem, vm.SaveAsCommand, Strings.SaveAs);
            Shortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift, vm.SaveAsCommand);

            // The saving indicator: the line under the title bar (FR-89); the dirty mark in the title (UI-06).
            Bound("Saving indicator", window.SaveLine, SaveProgressLine.IsActiveProperty, nameof(MainViewModel.IsSaving));
            Bound("Dirty mark", window.TitleNameText, System.Windows.Controls.TextBlock.TextProperty, nameof(MainViewModel.DocumentTitle));

            // Previous / next page: PgUp / PgDn (FR-10, a screen at a time) and the chip's go to page.
            var top = vm.Viewport.Position;
            Assert.True(window.RouteKey(Key.PageDown, ModifierKeys.None, null));
            Assert.NotEqual(top, vm.Viewport.Position);
            Assert.True(window.RouteKey(Key.PageUp, ModifierKeys.None, null));
            Assert.Equal(top.PageIndex, vm.Viewport.Position.PageIndex);
            Assert.Equal(top.YPt, vm.Viewport.Position.YPt, 1);
            checkedFunctions.Add("Previous / next page");

            // Page N / M and the zoom percentage: the indicator chip's text (UI-05).
            Bound("Page indicator and zoom", chip.IndicatorButton, ContentControl.ContentProperty, "Viewport.IndicatorText");
            Assert.Equal(Strings.PageIndicatorLabel, AutomationProperties.GetName(chip.IndicatorButton));

            // Go to page: the chip's field (FR-09).
            Assert.Equal(Strings.GoToPageIndicatorField, AutomationProperties.GetName(chip.GoToPageBox));
            chip.OpenGoToPage();
            chip.GoToPageBox.Text = "7";
            Assert.True(chip.ConfirmGoToPage());
            Assert.Equal(6, vm.Viewport.CurrentPage);
            checkedFunctions.Add("Go to page");

            // Back, previous / next mark and the mark status: the round buttons above the chip (FR-09, FR-102).
            Command("Back", nav.BackButton, vm.Viewport.BackCommand, Strings.Back);
            Shortcut(Key.Left, ModifierKeys.Alt, vm.Viewport.BackCommand);
            Command("Previous mark", nav.PreviousMarkButton, vm.Marks.PreviousCommand, Strings.PreviousMark);
            Shortcut(Key.Up, ModifierKeys.Alt, vm.Marks.PreviousCommand);
            Command("Next mark", nav.NextMarkButton, vm.Marks.NextCommand, Strings.NextMark);
            Shortcut(Key.Down, ModifierKeys.Alt, vm.Marks.NextCommand);
            Bound("Mark status", nav.StatusText, TextBlock.TextProperty, "Marks.StatusText");

            // The marks list and the page thumbnails: menu entries, Ctrl+M / Ctrl+Shift+P as before (UI-04).
            Command("Open the marks list", menu.MarksItem, window.MarksPanel.OpenTabCommand, Strings.OpenMarksList);
            Assert.Equal(MainWindow.MarksTab, Convert.ToInt32(menu.MarksItem.CommandParameter, System.Globalization.CultureInfo.InvariantCulture));
            Command("Open the page thumbnails", menu.PagesItem, window.MarksPanel.OpenTabCommand, Strings.OpenPagesList);
            Assert.Equal(MainWindow.PagesTab, Convert.ToInt32(menu.PagesItem.CommandParameter, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains(gestures, g => g.Key == Key.M && g.Modifiers == ModifierKeys.Control);
            Assert.Contains(gestures, g => g.Key == Key.P && g.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));

            // Search: the pill at the top centre (Ctrl+F, also a menu entry); field, count, previous / next, clear and
            // its messages (FR-13, FR-105).
            Bound("Search field", pill.Field, TextBox.TextProperty, nameof(SearchViewModel.QueryText));
            Assert.Equal(Strings.SearchField, AutomationProperties.GetName(pill.Field));
            Assert.True(window.RouteKey(Key.F, ModifierKeys.Control, null));
            Assert.True(vm.Search.IsOpen);
            Assert.Equal(Strings.MenuSearchName, AutomationProperties.GetName(menu.SearchItem));
            Assert.NotNull(menu.SearchItem.Command);
            Command("Previous result", pill.PreviousButton, vm.Search.PreviousCommand, Strings.PreviousResult);
            Command("Next result", pill.NextButton, vm.Search.NextCommand, Strings.NextResult);
            Command("Clear the search", pill.CloseButton, vm.Search.ClearCommand, Strings.CloseSearch);
            Bound("Result count", pill.CountText, TextBlock.TextProperty, nameof(SearchViewModel.CountText));
            Bound("Pages not searched", pill.FailedPagesText, TextBlock.TextProperty, nameof(SearchViewModel.FailedPagesText));
            Bound("No text layer", pill.NoTextText, TextBlock.TextProperty, nameof(SearchViewModel.NoTextLayerMessage));
            Bound("Search status", pill.StatusText, TextBlock.TextProperty, nameof(SearchViewModel.StatusText));
            vm.Search.ClearCommand.Execute(null);

            // The journal warning and the notices: the notice bar at the top centre (FR-84, FR-86).
            Bound("Journal warning", window.Notices.WarningText, TextBlock.TextProperty, nameof(MainViewModel.JournalWarning));
            Bound("Notice", window.Notices.NoticeText, TextBlock.TextProperty, nameof(MainViewModel.Notice));

            // Tools: the toolbar (UI-02).
            Command("Pen", toolbar.PenButton, vm.Toolbar.Buttons[0].Command, Strings.ToolPen);
            Command("Highlighter", toolbar.HighlighterButton, vm.Toolbar.Buttons[1].Command, Strings.ToolHighlighter);
            Command("Eraser", toolbar.EraserButton, vm.Toolbar.Buttons[2].Command, Strings.ToolEraser);

            // Undo / redo: the toolbar, Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z as before.
            Command("Undo", toolbar.UndoButton, vm.Toolbar.Buttons[4].Command, Strings.Undo);
            Command("Redo", toolbar.RedoButton, vm.Toolbar.Buttons[5].Command, Strings.Redo);
            Shortcut(Key.Z, ModifierKeys.Control, vm.UndoCommand);
            Shortcut(Key.Y, ModifierKeys.Control, vm.RedoCommand);

            // The pen's options: the pen flyout (colours, width, pressure curve, smoothing).
            Bound("Pen colour", pen.Swatches, ColorSwatches.SelectedColorProperty, "Pen.Color");
            Bound("Pen width", pen.WidthSlider, RangeBase.ValueProperty, "Pen.WidthMm");
            Assert.Equal(Strings.Width, AutomationProperties.GetName(pen.WidthSlider));
            foreach (var (radio, name) in new[]
                     {
                         (pen.CurveLinearRadio, Strings.CurveLinear), (pen.CurveSoftRadio, Strings.CurveSoft),
                         (pen.CurveFirmRadio, Strings.CurveFirm),
                     })
            {
                Bound("Pressure curve: " + name, radio, ToggleButton.IsCheckedProperty, "Tools.Curve");
                Assert.Equal(name, AutomationProperties.GetName(radio));
            }

            foreach (var (radio, name) in new[]
                     {
                         (pen.SmoothingOffRadio, Strings.SmoothingOff), (pen.SmoothingLightRadio, Strings.SmoothingLight),
                         (pen.SmoothingStrongRadio, Strings.SmoothingStrong),
                     })
            {
                Bound("Smoothing: " + name, radio, ToggleButton.IsCheckedProperty, "Tools.Smoothing");
                Assert.Equal(name, AutomationProperties.GetName(radio));
            }

            // The highlighter's options: its flyout.
            Bound("Highlighter colour", highlighter.Swatches, ColorSwatches.SelectedColorProperty, "Highlighter.Color");
            Bound("Highlighter round tip", highlighter.RoundRadio, ToggleButton.IsCheckedProperty, "Highlighter.IsRound");
            Bound("Highlighter chisel tip", highlighter.ChiselRadio, ToggleButton.IsCheckedProperty, "Highlighter.IsChisel");
            Bound("Highlighter angle", highlighter.AngleSlider, RangeBase.ValueProperty, "Highlighter.ChiselAngleDeg");
            Bound("Highlighter straight line", highlighter.StraightLineCheck, ToggleButton.IsCheckedProperty, "Highlighter.IsStraightLine");

            // The eraser's options: its flyout (one mode, also the pen's back end).
            Bound("Eraser whole stroke", eraser.WholeRadio, ToggleButton.IsCheckedProperty, "Eraser.IsWholeStroke");
            Bound("Eraser partial", eraser.PartialRadio, ToggleButton.IsCheckedProperty, "Eraser.IsPartial");
            Bound("Eraser size", eraser.SizeSlider, RangeBase.ValueProperty, "Eraser.SizeMm");

            // Zoom − / + and fit width: the chip's buttons (UI-05, FR-06, FR-07), Ctrl − / Ctrl + as before.
            Command("Zoom out", chip.ZoomOutButton, vm.ZoomVm.ZoomOutCommand, Strings.ZoomOut);
            Command("Zoom in", chip.ZoomInButton, vm.ZoomVm.ZoomInCommand, Strings.ZoomIn);
            Command("Fit width", chip.FitWidthButton, vm.Viewport.FitWidthCommand, Strings.FitWidth);
            Shortcut(Key.OemPlus, ModifierKeys.Control, vm.ZoomVm.ZoomInCommand);
            Shortcut(Key.OemMinus, ModifierKeys.Control, vm.ZoomVm.ZoomOutCommand);
            vm.ZoomVm.ZoomInCommand.Execute(null);
            Assert.False(vm.ZoomVm.IsFitWidth);
            chip.FitWidthButton.Command.Execute(null);
            Assert.True(vm.ZoomVm.IsFitWidth);

            // The whole list (spec section 5): nothing was skipped.
            Assert.Equal(checkedFunctions.Count, checkedFunctions.Distinct().Count());
            Assert.Equal(48, checkedFunctions.Count);

            // The radio buttons drive the tools as the panel's did.
            pen.SmoothingOffRadio.IsChecked = true;
            Assert.Equal(SmoothingLevel.Off, vm.Tools.Smoothing);
            pen.SmoothingStrongRadio.IsChecked = true;
            Assert.Equal(SmoothingLevel.Strong, vm.Tools.Smoothing);
        });
    }

    [Fact]
    public async Task The_fit_width_button_shows_with_the_zoom_buttons()
    {
        await WithWindowAsync((_, window) =>
        {
            var chip = window.IndicatorChip;
            window.DocumentViewer.Rail.NotifyActivity();
            Assert.Equal(Visibility.Collapsed, chip.FitWidthButton.Visibility);
            chip.SetHover(true);
            Assert.Equal(Visibility.Visible, chip.FitWidthButton.Visibility);
            Assert.Equal(Visibility.Visible, chip.ZoomInButton.Visibility);
            window.UpdateLayout();
            Assert.True(chip.FitWidthButton.ActualWidth >= 40 && chip.FitWidthButton.ActualHeight >= 40);
            Assert.True(chip.FitWidthButton.Focusable && chip.FitWidthButton.IsTabStop);
            chip.SetHover(false);
            Assert.Equal(Visibility.Collapsed, chip.FitWidthButton.Visibility);
        });
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

        try
        {
            WpfTestThread.Run(() =>
            {
                var window = new MainWindow { DataContext = vm };
                try
                {
                    window.DocumentViewer.BitmapRenderer = new FakeBitmapRenderer();
                    window.DocumentViewer.Rail.AnimationsOverride = false;
                    window.MarksPanel.AnimationsEnabled = () => false;
                    window.FloatingToolbar.AnimationsEnabled = () => false;
                    window.IndicatorChip.AnimationsEnabled = () => false;
                    window.NavButtons.AnimationsEnabled = () => false;
                    window.SearchBar.AnimationsEnabled = () => false;
                    window.MainMenu.AnimationsEnabled = () => false;
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
}
