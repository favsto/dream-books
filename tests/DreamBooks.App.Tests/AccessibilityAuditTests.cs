using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

/// <summary>
/// A sweep of the whole window in every state that shows controls (NFR-31): each visible, enabled interactive control
/// has a name for screen readers and can be reached with the keyboard. The per-view tests check their own names; this
/// one catches a control nobody checked.
/// </summary>
[Collection(nameof(ThemeCollection))]
public sealed class AccessibilityAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-a11y-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();
    private readonly ManualScheduler _scheduler = new();

    public AccessibilityAuditTests() => Directory.CreateDirectory(_dir);

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
    public async Task Every_visible_control_has_a_name_and_a_keyboard_stop_in_every_state() // NFR-31
    {
        var path = Path.Combine(_dir, "a.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        _dialogs.NextPath = path;
        var vm = new MainViewModel(_dialogs, scheduler: _scheduler, clock: () => _scheduler.Now)
        {
            OpenDocument = (opened, _) =>
            {
                var source = new FakePageSource(Enumerable.Repeat((612.0, 792.0), 12).ToArray());
                return Task.FromResult(new OpenedDocument(source, source, opened, new FakeViewerHandle([])));
            },
        };
        await vm.OpenPdfCommand.ExecuteAsync(null);
        var problems = new List<string>();

        WpfTestThread.Run(() =>
        {
            var window = new MainWindow(null) { DataContext = vm, TransparencyEnabled = () => true, MicaSupported = () => true };
            try
            {
                window.DocumentViewer.BitmapRenderer = new FakeBitmapRenderer { Auto = true };
                window.MarksPanel.AnimationsEnabled = () => false;
                window.FloatingToolbar.AnimationsEnabled = () => false;
                window.MainMenu.AnimationsEnabled = () => false;
                Lay(window);

                Audit("document", (FrameworkElement)window.Content, problems);

                foreach (var tool in new[] { ToolbarTool.Pen, ToolbarTool.Highlighter, ToolbarTool.Eraser })
                {
                    vm.Toolbar.TapTool(tool);
                    vm.Toolbar.TapTool(tool); // the second tap opens the panel
                    Lay(window);
                    Audit($"{tool} flyout", (FrameworkElement)window.Content, problems);
                    vm.Toolbar.ClosePanel();
                }

                window.MainMenu.Open();
                Lay(window);
                Audit("main menu", (FrameworkElement)window.Content, problems);
                window.MainMenu.Close();

                window.MarksPanel.Open(0);
                Lay(window);
                Audit("marks panel", (FrameworkElement)window.Content, problems);
                window.MarksPanel.Open(1);
                Lay(window);
                Audit("pages panel", (FrameworkElement)window.Content, problems);
                window.MarksPanel.Close();

                vm.Search.Open();
                Lay(window);
                Audit("search", (FrameworkElement)window.Content, problems);

                vm.Editor!.CommitStroke(
                    [new InkPoint(50, 50, 0.5f, null, null, null, 0), new InkPoint(80, 80, 0.5f, null, null, null, 10)],
                    InkTool.Pen,
                    InkColor.FromRgb(0, 0, 0),
                    2);
                vm.Tools.ActiveTool = AppTool.Selection;
                vm.Selection.SelectLoop([new(0, 0), new(200, 0), new(200, 200), new(0, 200)]);
                Lay(window);
                Audit("selection bar", (FrameworkElement)window.Content, problems);

                window.SettingsView.Visibility = Visibility.Visible;
                Lay(window);
                Audit("settings", (FrameworkElement)window.Content, problems);
            }
            finally
            {
                window.CloseForTests();
            }
        });

        Assert.True(
            _audited >= 60,
            $"only {_audited} controls were audited: the sweep is not seeing the window{Environment.NewLine}" + string.Join(Environment.NewLine, problems.Distinct()));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct()));
    }

    private static int _audited;

    [Fact]
    public void The_sweep_catches_an_unnamed_button_and_a_button_without_a_keyboard_stop() // NFR-31
    {
        WpfTestThread.Run(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new Button { Content = new System.Windows.Shapes.Rectangle { Width = 10, Height = 10 }, Name = "Unnamed" });
            panel.Children.Add(new Button { Content = "Named", IsTabStop = false, Name = "NoStop" });
            panel.Children.Add(new Button { Content = "Fine", Name = "Fine" });
            panel.Children.Add(new Button
            {
                Content = new System.Windows.Shapes.Rectangle { Width = 10, Height = 10 },
                Name = "ByProperty",
            });
            AutomationProperties.SetName((Button)panel.Children[3], "Con nome");
            panel.Measure(new Size(300, 300));
            panel.Arrange(new Rect(0, 0, 300, 300));
            var problems = new List<string>();

            Audit("negative", panel, problems);

            Assert.Equal(2, problems.Count);
            Assert.Contains(problems, p => p.Contains("no name") && p.Contains("Unnamed"));
            Assert.Contains(problems, p => p.Contains("keyboard stop") && p.Contains("NoStop"));
        });
    }

    private static void Audit(string state, FrameworkElement root, List<string> problems)
    {
        var all = MainMenuTests.LogicalDescendants(root).Concat(ToolbarViewTests.Descendants(root)).Distinct().OfType<FrameworkElement>().ToList();
        foreach (var element in MainMenuTests.LogicalDescendants(root).Concat(ToolbarViewTests.Descendants(root)).Distinct().OfType<FrameworkElement>())
        {
            if (!IsInteractive(element) || !EffectivelyVisible(element) || !element.IsEnabled)
            {
                continue;
            }

            _audited++;
            var control = (Control)element;
            if (!HasName(control))
            {
                problems.Add($"{state}: no name: {Describe(control)}");
            }

            // The caption buttons (minimise, maximise, close) are the system's: no app tabs to them, Alt+Space opens the
            // same commands from the keyboard (system menu).
            if ((!control.Focusable || !control.IsTabStop) && control.GetType().Name != "TitleBarButton")
            {
                problems.Add($"{state}: not a keyboard stop: {Describe(control)}");
            }
        }
    }

    private static bool IsInteractive(FrameworkElement element) =>
        element is not RepeatButton { TemplatedParent: Slider or ScrollBar } // the track parts: the slider or bar itself is the control
        && element is ButtonBase or Slider or ComboBox or TextBox or ListBoxItem;

    private static bool HasName(Control control)
    {
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))
            || AutomationProperties.GetLabeledBy(control) is not null)
        {
            return true;
        }

        return control is ContentControl { Content: string { Length: > 0 } }
            or ContentControl { Content: TextBlock { Text.Length: > 0 } };
    }

    private static bool EffectivelyVisible(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is Window)
            {
                break; // a window the test never shows is Hidden; what is inside it is what counts
            }

            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }

            if (current is UIElement { Opacity: 0 })
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return element.ActualWidth > 0 && element.ActualHeight > 0;
    }

    private static string Describe(FrameworkElement element)
    {
        var chain = new List<string>();
        DependencyObject? current = element;
        while (current is not null && chain.Count < 5)
        {
            chain.Add(current is FrameworkElement { Name.Length: > 0 } named
                ? $"{current.GetType().Name}#{named.Name}"
                : current.GetType().Name);
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return string.Join(" < ", chain);
    }

    private static void Lay(MainWindow window)
    {
        ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
        ScrollRailTests.Settle(window.DocumentViewer);
    }
}
