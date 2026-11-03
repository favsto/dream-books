using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Model;
using TitleBar = Wpf.Ui.Controls.TitleBar;
using static DreamBooks.App.Tests.ToolbarViewTests;
using Button = System.Windows.Controls.Button;

namespace DreamBooks.App.Tests;

/// <summary>
/// The window's shell once the debug panel is gone (UI-01, UI-03, UI-06, FR-89, phase 4a): the main menu (round button
/// top left, its panel of entries), the save progress line under the title bar, the title bar's small file name with the
/// dirty mark, and the notices. The pen presses the menu's entries like a finger and inks around them. Built on the STA
/// test thread, never shown: keyboard focus itself is not asserted (an unshown window cannot take it).
/// </summary>
public sealed class MainMenuTests : IDisposable
{
    private const double _width = 1200;
    private const double _height = 800;

    private static readonly InkPoint[] _line =
    [
        new(50, 100, 0.5f, null, null, null, 0),
        new(150, 100, 0.5f, null, null, null, 10),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-menu-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeDocumentWriter _writer;
    private readonly FakeViewerHandle _viewer;
    private readonly ManualScheduler _scheduler = new();

    public MainMenuTests()
    {
        Directory.CreateDirectory(_dir);
        _writer = new FakeDocumentWriter(_log);
        _viewer = new FakeViewerHandle(_log);
    }

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
    public async Task The_window_has_no_debug_panel_and_the_document_fills_it()
    {
        await WithWindowAsync((_, window) =>
        {
            var root = (FrameworkElement)window.Content;
            Assert.Empty(Descendants(root).OfType<Expander>());
            Assert.Empty(LogicalDescendants(root).OfType<Expander>());

            // The page host (and the document view in it) covers the whole window, under the title bar too (UI-01).
            var host = Bounds(window.PageHost, root);
            Assert.Equal(new Rect(0, 0, _width, _height), host);
            Assert.Equal(new Rect(0, 0, _width, _height), Bounds(window.DocumentViewer, root));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_window_instantiates_with_an_open_document_and_without_one(bool document)
    {
        await WithWindowAsync(
            (vm, window) =>
            {
                var menu = window.MainMenu;
                Assert.Equal(document, vm.Session is not null);
                Assert.Equal(document ? "a.pdf" : Strings.AppTitle, window.TitleNameText.Text);
                Assert.Equal(document, menu.SaveItem.IsEnabled);
                Assert.Equal(document, menu.SaveAsItem.IsEnabled);
                Assert.True(menu.OpenItem.IsEnabled);
                Assert.Equal(Visibility.Collapsed, window.SaveLine.Visibility);
                Assert.Equal(Visibility.Visible, menu.MenuButton.Visibility); // the menu is always there (Open)
                Assert.False(menu.IsOpen);
            },
            document);
    }

    [Fact]
    public async Task Menu_button_is_a_round_40_px_target_top_left_below_the_title_bar()
    {
        await WithWindowAsync((_, window) =>
        {
            var menu = window.MainMenu;
            var button = Bounds(menu.MenuButton, window.PageHost);
            var titleBar = ((Thickness)window.FindResource("TitleBarClearance")).Top;
            Assert.True(button.Top >= titleBar, $"the button starts at {button.Top}, under the title bar");
            Assert.InRange(button.Left, (double)window.FindResource("EdgeSwipeZoneWidth"), 40); // right of the left-edge swipe zone
            Assert.InRange(button.Top, titleBar, titleBar + 24);
            Assert.Equal(40, menu.MenuButton.ActualWidth, 1);
            Assert.Equal(40, menu.MenuButton.ActualHeight, 1);
            Assert.Equal(999, menu.ButtonSurface.CornerRadius.TopLeft);
            Assert.Equal(Strings.MainMenu, AutomationProperties.GetName(menu.MenuButton));
            Assert.True(menu.MenuButton.Focusable && menu.MenuButton.IsTabStop, "keyboard reachable (NFR-31)");
            Assert.True(menu.ButtonSurface.HasStylusPlugIn);
        });
    }

    [Fact]
    public async Task Menu_entries_invoke_the_existing_commands_and_the_placeholders_are_disabled()
    {
        await WithWindowAsync((vm, window) =>
        {
            var menu = window.MainMenu;
            Invoke(menu.MenuButton);
            Assert.True(menu.IsOpen);
            window.UpdateLayout();

            // The entries in order, with their names and shortcuts (UI-03).
            Button[] order =
            [
                menu.OpenItem, menu.SaveItem, menu.SaveAsItem, menu.RecentItem, menu.ExportItem,
                menu.MarksItem, menu.PagesItem, menu.SearchItem, menu.SettingsItem,
            ];
            Assert.Equal(order, menu.Items);
            Assert.Equal(Strings.OpenPdf, AutomationProperties.GetName(menu.OpenItem));
            Assert.Equal(Strings.Save, AutomationProperties.GetName(menu.SaveItem));
            Assert.Equal(Strings.SaveAs, AutomationProperties.GetName(menu.SaveAsItem));
            Assert.Equal(Strings.MenuRecentFiles, AutomationProperties.GetName(menu.RecentItem));
            Assert.Equal(Strings.MenuExport, AutomationProperties.GetName(menu.ExportItem));
            Assert.Equal(Strings.OpenMarksList, AutomationProperties.GetName(menu.MarksItem));
            Assert.Equal(Strings.OpenPagesList, AutomationProperties.GetName(menu.PagesItem));
            Assert.Equal(Strings.MenuSearchName, AutomationProperties.GetName(menu.SearchItem));
            Assert.Equal(Strings.MenuSettings, AutomationProperties.GetName(menu.SettingsItem));
            Assert.Equal(order.Length, order.Select(AutomationProperties.GetName).Distinct().Count());
            Assert.Equal(Strings.ShortcutOpen, AutomationProperties.GetAcceleratorKey(menu.OpenItem));
            Assert.Equal(Strings.ShortcutSave, AutomationProperties.GetAcceleratorKey(menu.SaveItem));
            Assert.Equal(Strings.ShortcutSaveAs, AutomationProperties.GetAcceleratorKey(menu.SaveAsItem));
            Assert.Equal(Strings.ShortcutMarks, AutomationProperties.GetAcceleratorKey(menu.MarksItem));
            Assert.Equal(Strings.ShortcutPages, AutomationProperties.GetAcceleratorKey(menu.PagesItem));
            Assert.Equal(Strings.ShortcutSearch, AutomationProperties.GetAcceleratorKey(menu.SearchItem));
            foreach (var item in order)
            {
                Assert.True(item.ActualHeight >= 40, $"{AutomationProperties.GetName(item)}: 40 px touch target");
                Assert.True(item.Focusable && item.IsTabStop, "keyboard reachable");
            }

            Assert.Equal(3, menu.Separators.Count);

            // The existing commands (and their shortcuts stay).
            Assert.Same(vm.OpenPdfCommand, menu.OpenItem.Command);
            Assert.Same(vm.SaveCommand, menu.SaveItem.Command);
            Assert.Same(vm.SaveAsCommand, menu.SaveAsItem.Command);
            Assert.Same(window.MarksPanel.OpenTabCommand, menu.MarksItem.Command);
            Assert.Same(window.MarksPanel.OpenTabCommand, menu.PagesItem.Command);
            var gestures = window.InputBindings.OfType<KeyBinding>().Select(b => (b.Key, b.Modifiers, b.Command)).ToList();
            Assert.Contains((Key.O, ModifierKeys.Control, (ICommand)vm.OpenPdfCommand), gestures);
            Assert.Contains((Key.S, ModifierKeys.Control, (ICommand)vm.SaveCommand), gestures);
            Assert.Contains((Key.S, ModifierKeys.Control | ModifierKeys.Shift, (ICommand)vm.SaveAsCommand), gestures);

            // Not yet available (4b): disabled, with no command, and saying so.
            foreach (var placeholder in new[] { menu.RecentItem, menu.ExportItem, menu.SettingsItem })
            {
                Assert.False(placeholder.IsEnabled);
                Assert.Null(placeholder.Command);
                Assert.Equal(Strings.MenuNotYetAvailable, AutomationProperties.GetHelpText(placeholder));
            }

            // Marks: the left-edge panel on its marks tab, the menu closed.
            Invoke(menu.MarksItem);
            Assert.False(menu.IsOpen);
            Assert.True(window.MarksPanel.IsOpen);
            Assert.Equal(MainWindow.MarksTab, window.MarksPanel.SelectedTab);
            window.MarksPanel.Close();

            // Pages: the same panel on the Pages tab.
            Invoke(menu.MenuButton);
            Invoke(menu.PagesItem);
            Assert.False(menu.IsOpen);
            Assert.True(window.MarksPanel.IsOpen);
            Assert.Equal(MainWindow.PagesTab, window.MarksPanel.SelectedTab);
            window.MarksPanel.Close();

            // Search: the pill opens and its field asks for the keyboard (as Ctrl+F).
            var requests = window.SearchFocusRequests;
            Invoke(menu.MenuButton);
            Invoke(menu.SearchItem);
            Assert.False(menu.IsOpen);
            Assert.True(vm.Search.IsOpen);
            Assert.Equal(requests + 1, window.SearchFocusRequests);
            vm.Search.ClearCommand.Execute(null);

            // Save as: the existing command runs (the dialog is cancelled here) and the menu closes first.
            Invoke(menu.MenuButton);
            _dialogs.NextSavePath = null;
            Invoke(menu.SaveAsItem);
            Assert.False(menu.IsOpen);
            Assert.Single(_dialogs.SuggestedNames);
        });
    }

    [Fact]
    public async Task Menu_closes_on_Esc_and_a_press_outside_and_a_pen_press_outside_inks()
    {
        await WithWindowAsync((_, window) =>
        {
            var menu = window.MainMenu;
            var outside = new Point(_width / 2, _height / 2);

            // The button toggles it.
            Invoke(menu.MenuButton);
            Assert.True(menu.IsOpen);
            Assert.True(menu.PanelSurface.IsHitTestVisible);
            Assert.True(menu.PanelSurface.HasStylusPlugIn);
            Invoke(menu.MenuButton);
            Assert.False(menu.IsOpen);

            // Esc (from an entry, or anywhere) closes it before anything else.
            Invoke(menu.MenuButton);
            Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, menu.OpenItem));
            Assert.False(menu.IsOpen);
            Invoke(menu.MenuButton);
            Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, null));
            Assert.False(menu.IsOpen);
            Assert.False(window.RouteKey(Key.Escape, ModifierKeys.None, null)); // closed: Esc is not the menu's

            // A finger or the mouse outside: closed and taken (a light dismiss).
            foreach (var kind in new[] { PointerKind.Finger, PointerKind.Mouse })
            {
                Invoke(menu.MenuButton);
                Assert.True(window.RoutePagePress(outside, kind), kind.ToString());
                Assert.False(menu.IsOpen, kind.ToString());
            }

            // The pen outside: closed and not taken, so it inks on the page there, at once.
            Invoke(menu.MenuButton);
            Assert.False(window.RoutePagePress(outside, PointerKind.Pen));
            Assert.False(menu.IsOpen);
            Assert.False(menu.PanelSurface.IsHitTestVisible);
            Assert.False(menu.PanelSurface.HasStylusPlugIn);
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, outside), window.DocumentViewer));

            // Closed, nothing of the panel takes input: where an entry was, the pen inks.
            Invoke(menu.MenuButton);
            window.UpdateLayout();
            var entry = Centre(menu.SaveAsItem, window.PageHost);
            menu.Close();
            window.UpdateLayout();
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, entry), window.DocumentViewer));
            Assert.False(window.RoutePagePress(entry, PointerKind.Pen));
        });
    }

    [Fact]
    public async Task The_pen_uses_the_menu_entries_and_inks_around_the_button()
    {
        await WithWindowAsync((_, window) =>
        {
            var menu = window.MainMenu;
            Invoke(menu.MenuButton);
            window.UpdateLayout();

            // On an entry the pen is not taken by the page host: the entry gets it, the menu stays open.
            foreach (var item in new[] { menu.OpenItem, menu.MarksItem, menu.SettingsItem })
            {
                var centre = Centre(item, window.PageHost);
                var hit = ScrollRailTests.HitAt(window.PageHost, centre);
                Assert.True(ScrollRailTests.IsWithin(hit, menu.PanelSurface), AutomationProperties.GetName(item));
                Assert.False(window.RoutePagePress(centre, PointerKind.Pen));
                Assert.True(menu.IsOpen);
            }

            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, Centre(menu.OpenItem, window.PageHost)), menu.OpenItem));

            // On the button too (it toggles the menu, for every pointer).
            var onButton = Centre(menu.MenuButton, window.PageHost);
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onButton), menu.MenuButton));
            Assert.False(window.RoutePagePress(onButton, PointerKind.Pen));
            Assert.True(menu.IsOpen);

            // Around the button and beside the panel the pen reaches the page (no background around them).
            var button = Bounds(menu.ButtonSurface, window.PageHost);
            var panel = Bounds(menu.PanelSurface, window.PageHost);
            menu.Close();
            foreach (var beside in new[]
                     {
                         new Point(button.Right + 6, button.Top + (button.Height / 2)),
                         new Point(button.Left + (button.Width / 2), button.Bottom + 3),
                         new Point(button.Right + 1, button.Bottom + 1), // past the round corner
                     })
            {
                Assert.True(
                    ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, beside), window.DocumentViewer),
                    beside.ToString());
            }

            Invoke(menu.MenuButton);
            window.UpdateLayout();
            var right = new Point(panel.Right + 6, panel.Top + (panel.Height / 2));
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, right), window.DocumentViewer));
        });
    }

    [Fact]
    public async Task Menu_opens_with_the_flyout_animation_and_at_once_with_reduced_motion()
    {
        await WithWindowAsync((_, window) =>
        {
            var menu = window.MainMenu;
            menu.AnimationsEnabled = () => true;
            menu.Open();
            Assert.Equal((TimeSpan)window.FindResource("FlyoutOpenDuration"), menu.LastOpenDuration);
            menu.Close();
            Assert.Equal(Visibility.Collapsed, menu.PanelHost.Visibility); // closing is immediate: the pen inks there again

            menu.AnimationsEnabled = () => false;
            menu.LastOpenDuration = null;
            menu.Open();
            Assert.Null(menu.LastOpenDuration);
            Assert.Equal(1, menu.PanelHost.Opacity);

            // Transparency effects off: opaque surfaces (NFR-32).
            menu.TransparencyEnabled = () => false;
            menu.ApplyTransparency();
            Assert.True(menu.ButtonSurface.IsOpaque && menu.PanelSurface.IsOpaque);
            menu.TransparencyEnabled = () => true;
            menu.ApplyTransparency();
            Assert.False(menu.ButtonSurface.IsOpaque || menu.PanelSurface.IsOpaque);
        });
    }

    [Fact]
    public async Task The_save_line_shows_only_while_saving_under_the_title_bar()
    {
        await WithWindowAsync((vm, window) =>
        {
            var line = window.SaveLine;
            Assert.Equal(nameof(MainViewModel.IsSaving), BindingOperations.GetBinding(line, SaveProgressLine.IsActiveProperty)?.Path.Path);
            Assert.Equal(Visibility.Collapsed, line.Visibility);
            Assert.False(line.IsHitTestVisible); // the pen writes under it
            Assert.Equal(Strings.Saving, AutomationProperties.GetName(line));

            line.AnimationsEnabled = () => true;
            _writer.Gate = new TaskCompletionSource();
            var saving = vm.SaveCommand.ExecuteAsync(null);
            WpfTestThread.PumpUntil(() => vm.IsSaving);
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, line.Visibility);
            Assert.True(line.IsAnimating);
            var bounds = Bounds(line, (FrameworkElement)window.Content);
            var titleBar = ((Thickness)window.FindResource("TitleBarClearance")).Top;
            Assert.Equal(titleBar, bounds.Top, 1);
            Assert.Equal(_width, bounds.Width, 1);
            Assert.InRange(bounds.Height, 1, 4);

            _writer.Gate.SetResult();
            WpfTestThread.PumpUntil(() => saving.IsCompleted);
            Assert.False(vm.IsSaving);
            Assert.Equal(Visibility.Collapsed, line.Visibility);
            Assert.False(line.IsAnimating);
        });
    }

    [Fact]
    public void The_save_line_is_still_with_reduced_motion()
    {
        WpfTestThread.Run(() =>
        {
            var line = new SaveProgressLine { AnimationsEnabled = () => false };
            var host = new Grid { Width = 600, Height = 40 };
            host.Children.Add(line);
            ScrollRailTests.LayOut(host, 600, 40);
            line.IsActive = true;
            host.UpdateLayout();
            Assert.Equal(Visibility.Visible, line.Visibility);
            Assert.False(line.IsAnimating);
            Assert.Equal(600, line.Bar.ActualWidth, 1); // a full, still line

            line.IsActive = false;
            line.AnimationsEnabled = () => true;
            line.IsActive = true;
            host.UpdateLayout();
            Assert.True(line.IsAnimating);
            Assert.True(line.Bar.ActualWidth < 600);
            line.IsActive = false;
            Assert.False(line.IsAnimating);
            Assert.Equal(Visibility.Collapsed, line.Visibility);
        });
    }

    [Fact]
    public async Task The_title_bar_shows_the_file_name_and_the_dirty_mark()
    {
        await WithWindowAsync((vm, window) =>
        {
            Assert.Equal(nameof(MainViewModel.DocumentTitle), BindingOperations.GetBinding(window.TitleNameText, TextBlock.TextProperty)?.Path.Path);
            Assert.Equal(nameof(MainViewModel.WindowTitle), BindingOperations.GetBinding(window, Window.TitleProperty)?.Path.Path);
            Assert.Equal("a.pdf", window.TitleNameText.Text);
            Assert.Equal(vm.WindowTitle, window.Title);

            _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave; // the window closes at the end
            Assert.True(vm.Editor!.CommitStroke(_line, InkTool.Pen, Palette.Colors[0], 1));
            WpfTestThread.Drain();
            Assert.Equal(string.Format(Strings.DocumentTitleDirtyFormat, "a.pdf"), window.TitleNameText.Text);
            Assert.Contains("*", window.TitleNameText.Text, StringComparison.Ordinal);
            Assert.Contains("*", window.Title, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Notices_show_over_the_page_until_closed_and_the_pen_inks_around_them()
    {
        _viewer.Info = new(IsEncrypted: false, HasRestrictions: true, SignatureCount: 0); // a notice at open
        await WithWindowAsync((vm, window) =>
        {
            var notices = window.Notices;
            Assert.Equal(Strings.NoticeNotSavable, vm.Notice);
            Assert.True(notices.IsShown);
            Assert.Equal(Visibility.Visible, notices.Visibility);
            Assert.Equal(vm.Notice, notices.NoticeText.Text);
            Assert.Equal(Visibility.Collapsed, notices.WarningText.Visibility); // no journal fault
            Assert.Equal(nameof(MainViewModel.JournalWarning), BindingOperations.GetBinding(notices.WarningText, TextBlock.TextProperty)?.Path.Path);
            Assert.Equal(Strings.JournalWarningLabel, AutomationProperties.GetHelpText(notices.WarningText));
            Assert.Equal(Strings.NoticeLabel, AutomationProperties.GetHelpText(notices.NoticeText));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(notices.NoticeText));
            Assert.Equal(Strings.CloseNotice, AutomationProperties.GetName(notices.CloseButton));
            Assert.True(notices.CloseButton.ActualWidth >= 40 && notices.CloseButton.ActualHeight >= 40);

            // Top centre, below the title bar; the pen presses its close button and inks beside it.
            window.UpdateLayout();
            var surface = Bounds(notices.Surface, window.PageHost);
            Assert.True(surface.Top >= ((Thickness)window.FindResource("TitleBarClearance")).Top);
            Assert.True(Math.Abs(surface.Left + (surface.Width / 2) - (_width / 2)) < 2, "centred");
            Assert.True(notices.Surface.HasStylusPlugIn);
            var beside = new Point(surface.Left - 6, surface.Top + (surface.Height / 2));
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, beside), window.DocumentViewer));
            var onClose = Centre(notices.CloseButton, window.PageHost);
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onClose), notices.CloseButton));
            Assert.False(window.RoutePagePress(onClose, PointerKind.Pen));

            // Closed: gone, the pen inks there.
            Invoke(notices.CloseButton);
            Assert.False(notices.IsShown);
            Assert.Equal(Visibility.Collapsed, notices.Visibility);
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, onClose), window.DocumentViewer));
        });
    }

    [Fact]
    public void A_new_notice_shows_again_after_the_bar_was_closed()
    {
        WpfTestThread.Run(() =>
        {
            var bar = new NoticeBar();
            bar.Show(notice: "uno", warning: null);
            Assert.True(bar.IsShown);
            bar.Dismiss();
            Assert.False(bar.IsShown);
            bar.Show(notice: "uno", warning: null); // the same text: stays closed
            Assert.False(bar.IsShown);
            bar.Show(notice: "uno", warning: "due"); // something new: shown again
            Assert.True(bar.IsShown);
            Assert.Equal(Visibility.Visible, bar.WarningText.Visibility);
            bar.Show(notice: null, warning: null);
            Assert.False(bar.IsShown);
            Assert.Equal(Visibility.Collapsed, bar.Visibility);
        });
    }

    internal static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in LogicalDescendants(child))
            {
                yield return nested;
            }
        }
    }

    private static void Prepare(MainWindow window)
    {
        window.MarksPanel.AnimationsEnabled = () => false;
        window.FloatingToolbar.AnimationsEnabled = () => false;
        window.IndicatorChip.AnimationsEnabled = () => false;
        window.NavButtons.AnimationsEnabled = () => false;
        window.SearchBar.AnimationsEnabled = () => false;
        window.MainMenu.AnimationsEnabled = () => false;
        window.SaveLine.AnimationsEnabled = () => false;
    }

    /// <summary>A window over a view model with a fake one-page document (or none), laid out at 1200 x 800 DIPs.</summary>
    private async Task WithWindowAsync(Action<MainViewModel, MainWindow> test, bool document = true)
    {
        var vm = new MainViewModel(_dialogs, scheduler: _scheduler);
        if (document)
        {
            var path = Path.Combine(_dir, "a.pdf");
            File.WriteAllBytes(path, [1, 2, 3]); // fingerprinted and copied by the fake writer, never parsed
            _dialogs.NextPath = path;
            vm.OpenDocument = (opened, _) =>
            {
                var source = new FakePageSource((612.0, 792.0));
                return Task.FromResult(new OpenedDocument(source, source, opened, _viewer));
            };
            vm.SaveService = new DocumentSaveService(_writer, _dialogs);
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Assert.NotNull(vm.Editor);
        }

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
                window.CloseForTests();
            }
        });
    }
}
