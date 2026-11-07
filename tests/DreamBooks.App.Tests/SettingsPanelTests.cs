using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.Views;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Settings;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>The settings panel in the window: every group, the close routes, the pen and finger rules (FR-110, UI-02, UI-03).</summary>
[Collection(nameof(ThemeCollection))]
public sealed class SettingsPanelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-settingspanel-" + Guid.NewGuid().ToString("N"));

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

    private void WithWindow(Action<MainWindow, SettingsService> test)
    {
        WpfTestThread.Run(() =>
        {
            var settings = new SettingsService(new AppSettingsStore(_dir), new ManualScheduler());
            settings.Load();
            var window = new MainWindow(null, settings) { TransparencyEnabled = () => true, MicaSupported = () => true };
            try
            {
                window.ApplyBackdrop();
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1000, 800);
                test(window, settings);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    private static void Select(RadioButton radio) =>
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)).Select();

    private static Point CentreOf(FrameworkElement element, FrameworkElement ancestor)
    {
        var box = element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
        return new Point(box.X + (box.Width / 2), box.Y + (box.Height / 2));
    }

    [Fact]
    public void The_panel_is_closed_until_the_menu_entry_or_Ctrl_comma_opens_it()
    {
        WithWindow((window, _) =>
        {
            Assert.False(window.SettingsView.IsOpen);
            Assert.True(window.MainMenu.SettingsItem.IsEnabled);
            Assert.Equal(Strings.MenuSettings, AutomationProperties.GetName(window.MainMenu.SettingsItem));
            Assert.Contains(window.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.OemComma && b.Modifiers == ModifierKeys.Control);

            window.MainMenu.Open();
            ToolbarViewTests.Invoke(window.MainMenu.SettingsItem);

            Assert.True(window.SettingsView.IsOpen);
            Assert.False(window.MainMenu.IsOpen);
        });
    }

    [Fact]
    public void The_panel_has_exactly_the_seven_groups_and_no_finger_drawing_option()
    {
        WithWindow((window, _) =>
        {
            window.SettingsVm!.OpenCommand.Execute(null);
            window.UpdateLayout();
            var view = window.SettingsView;
            var texts = ToolbarViewTests.Descendants(view).OfType<TextBlock>().Select(t => t.Text).ToList();

            string[] headers =
            [
                Strings.SettingsThemeHeader, Strings.SettingsAccentHeader, Strings.EraserMode, Strings.PressureCurve,
                Strings.Smoothing, Strings.SettingsToolbarEdgeHeader,
            ];
            Assert.All(headers, h => Assert.Contains(h, texts));
            Assert.NotNull(view.ToolbarFixedCheck);
            Assert.Equal(3 + 2 + 3 + 3 + 4, ToolbarViewTests.Descendants(view).OfType<RadioButton>().Count());
            Assert.NotNull(view.Preview); // the pressure curve's preview (FR-21)
            Assert.DoesNotContain(texts.Concat(ToolbarViewTests.Descendants(view).OfType<ContentControl>().Select(c => c.Content as string ?? "")),
                t => t.Contains("dito", StringComparison.OrdinalIgnoreCase)); // fingers never draw
        });
    }

    [Fact]
    public void Choosing_a_radio_changes_the_setting_at_once()
    {
        WithWindow((window, settings) =>
        {
            window.SettingsVm!.OpenCommand.Execute(null);
            var view = window.SettingsView;

            Select(view.ThemeDarkRadio);
            Select(view.CurveSoftRadio);
            Select(view.SmoothingOffRadio);
            Select(view.EraserPartialRadio);
            Select(view.EdgeLeftRadio);
            view.ToolbarFixedCheck.IsChecked = true;

            var s = settings.Current;
            Assert.Equal(DreamBooks.Core.Settings.ThemeMode.Dark, s.ThemeMode);
            Assert.Equal(PressureCurve.Soft, s.PressureCurve);
            Assert.Equal(SmoothingLevel.Off, s.Smoothing);
            Assert.Equal(EraserMode.Partial, s.EraserMode);
            Assert.Equal(ToolbarEdge.Left, s.ToolbarEdge);
            Assert.True(s.ToolbarFixed);
        });
    }

    [Fact]
    public void The_accent_box_applies_a_valid_colour_and_the_system_button_clears_it()
    {
        WithWindow((window, settings) =>
        {
            window.SettingsVm!.OpenCommand.Execute(null);
            var view = window.SettingsView;

            view.AccentBox.Text = "1e90ff";
            view.AccentBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Assert.Equal("#1E90FF", settings.Current.AccentOverride);

            ToolbarViewTests.Invoke(view.AccentSystemButton);
            Assert.Null(settings.Current.AccentOverride);
        });
    }

    [Fact]
    public void Esc_the_close_button_and_a_press_outside_close_the_panel()
    {
        WithWindow((window, _) =>
        {
            var vm = window.SettingsVm!;
            vm.OpenCommand.Execute(null);
            Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, null));
            Assert.False(window.SettingsView.IsOpen);

            vm.OpenCommand.Execute(null);
            ToolbarViewTests.Invoke(window.SettingsView.CloseButton);
            Assert.False(window.SettingsView.IsOpen);

            // A finger or the mouse outside: closes and is taken; the pen outside: closes and is not taken (it writes).
            foreach (var (kind, taken) in new[] { (PointerKind.Finger, true), (PointerKind.Mouse, true), (PointerKind.Pen, false) })
            {
                vm.OpenCommand.Execute(null);
                window.UpdateLayout();
                var outside = new Point(4, 4);
                Assert.Equal(taken, window.RoutePagePress(outside, kind));
                Assert.False(window.SettingsView.IsOpen, kind.ToString());
            }
        });
    }

    [Fact]
    public void Presses_on_the_surface_are_left_to_its_controls_for_every_pointer()
    {
        WithWindow((window, _) =>
        {
            window.SettingsVm!.OpenCommand.Execute(null);
            window.UpdateLayout();
            var onSurface = CentreOf(window.SettingsView.PanelSurface, window.PageHost);

            foreach (var kind in new[] { PointerKind.Finger, PointerKind.Mouse, PointerKind.Pen })
            {
                Assert.False(window.RoutePagePress(onSurface, kind), kind.ToString());
                Assert.True(window.SettingsView.IsOpen);
            }
        });
    }

    [Fact]
    public void Without_transparency_the_surface_is_opaque_and_the_panel_scrolls_in_a_short_window()
    {
        WithWindow((window, _) =>
        {
            window.TransparencyEnabled = () => false;
            window.ApplyBackdrop();
            Assert.True(window.SettingsView.PanelSurface.IsOpaque);

            window.SettingsVm!.OpenCommand.Execute(null);
            ScrollRailTests.LayOut((FrameworkElement)window.Content, 1000, 300);
            window.UpdateLayout();
            var scroller = ToolbarViewTests.Descendants(window.SettingsView).OfType<ScrollViewer>().First();
            Assert.True(scroller.ScrollableHeight > 0, "the panel scrolls when the window is short");
            Assert.Equal(PanningMode.VerticalOnly, scroller.PanningMode); // a finger scrolls it, not only the thin bar
            Assert.True(window.SettingsView.PanelSurface.ActualHeight <= 300);
        });
    }

    [Fact]
    public void Every_control_has_a_name_for_automation_and_the_targets_are_40_px()
    {
        WithWindow((window, _) =>
        {
            window.SettingsVm!.OpenCommand.Execute(null);
            window.UpdateLayout();
            var controls = ToolbarViewTests.Descendants(window.SettingsView)
                .OfType<Control>()
                .Where(c => c is RadioButton or CheckBox or Button or TextBox)
                .ToList();
            Assert.All(controls, c => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(c)), c.GetType().Name));
            Assert.All(controls.Where(c => c is Button or RadioButton or CheckBox), c => Assert.True(c.ActualHeight >= 40, c.GetType().Name));
        });
    }
}
