using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

/// <summary>
/// The colour swatches of the pen and highlighter flyouts (UI-02, FR-25, FR-41): presets and the shared custom palette,
/// 40 px targets, "+" opening the free colour picker, a long press (or Delete) removing a custom colour.
/// </summary>
public sealed class ColorSwatchesTests
{
    private static readonly InkColor _teal = InkColor.FromRgb(0x00, 0x80, 0x80);
    private static readonly InkColor _coral = InkColor.FromRgb(0xFF, 0x7F, 0x50);

    [Fact]
    public void Swatches_select_presets_and_custom_colours()
    {
        WithPen((swatches, tools, pen, _) =>
        {
            Assert.Equal(8, swatches.PresetButtons.Count);
            Assert.Equal(pen.Presets.Select(p => p.Name), swatches.PresetButtons.Select(AutomationProperties.GetName));
            Assert.All(swatches.PresetButtons.Append(swatches.AddButton), b =>
            {
                Assert.True(b.ActualWidth >= 40 && b.ActualHeight >= 40);
            });
            Assert.True(swatches.IsChosen(swatches.PresetButtons[0])); // black, the factory colour

            ToolbarViewTests.Invoke(swatches.PresetButtons[3]);
            Assert.Equal(Palette.Red, tools.Color);
            Assert.True(swatches.IsChosen(swatches.PresetButtons[3]));
            Assert.False(swatches.IsChosen(swatches.PresetButtons[0]));

            // Custom colours follow the presets, named by their code, and are selected the same way.
            tools.Palette.Add(_teal);
            tools.Palette.Add(_coral);
            WpfTestThread.Drain();
            Assert.Equal(2, swatches.CustomButtons.Count);
            Assert.Equal(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.CustomColorNameFormat, "#008080"),
                AutomationProperties.GetName(swatches.CustomButtons[0]));
            Assert.Equal(Strings.CustomColorHelp, AutomationProperties.GetHelpText(swatches.CustomButtons[0]));
            ToolbarViewTests.Invoke(swatches.CustomButtons[0]);
            Assert.Equal(_teal, tools.Color);
            Assert.True(swatches.IsChosen(swatches.CustomButtons[0]));
            Assert.False(swatches.IsChosen(swatches.PresetButtons[3]));
        });
    }

    [Fact]
    public void Plus_opens_the_picker_and_adds_its_colour_to_the_palette()
    {
        WithPen((swatches, tools, _, _) =>
        {
            Assert.Equal(Strings.AddCustomColor, AutomationProperties.GetName(swatches.AddButton));
            Assert.False(swatches.IsPickerOpen);
            Assert.Equal(Visibility.Collapsed, swatches.Picker.Visibility);

            ToolbarViewTests.Invoke(swatches.AddButton);
            Assert.True(swatches.IsPickerOpen);
            Assert.Equal(Visibility.Visible, swatches.Picker.Visibility);
            Assert.Equal(tools.Color, swatches.Picker.Model.Color); // starts from the colour in use

            // Cancel: nothing added.
            ToolbarViewTests.Invoke(swatches.Picker.CancelButton);
            Assert.False(swatches.IsPickerOpen);
            Assert.Empty(tools.Palette.Palette.Colors);

            ToolbarViewTests.Invoke(swatches.AddButton);
            swatches.Picker.Model.Hex = "#008080";
            ToolbarViewTests.Invoke(swatches.Picker.ConfirmButton);
            Assert.False(swatches.IsPickerOpen);
            Assert.Equal(["#008080"], tools.Palette.Palette.Colors);
            Assert.Equal(_teal, tools.Color); // and selected
            WpfTestThread.Drain();
            Assert.Single(swatches.CustomButtons);
            Assert.True(swatches.IsChosen(swatches.CustomButtons[0]));
        });
    }

    [Fact]
    public void A_long_press_on_a_custom_swatch_removes_it_and_a_short_one_selects_it()
    {
        WithPen((swatches, tools, _, scheduler) =>
        {
            var hold = (TimeSpan)Application.Current.FindResource("LongPressDuration");
            Assert.Equal(TimeSpan.FromMilliseconds(600), hold);
            tools.Palette.Add(_teal);
            tools.Palette.Add(_coral);
            WpfTestThread.Drain();

            // Short: no removal; the tap selects it.
            var teal = swatches.CustomButtons[0];
            swatches.BeginHold(teal, new Point(10, 10));
            scheduler.Advance(hold - TimeSpan.FromMilliseconds(1));
            Assert.False(swatches.EndHold());
            Assert.Equal(2, tools.Palette.Palette.Colors.Count);

            // Moving away cancels the hold (a scroll, not a press).
            swatches.BeginHold(teal, new Point(10, 10));
            swatches.MoveHold(new Point(30, 10));
            scheduler.Advance(hold);
            Assert.False(swatches.EndHold());
            Assert.Equal(2, tools.Palette.Palette.Colors.Count);

            // Held: removed, and the tap that ends the hold selects nothing.
            var colour = tools.Color;
            swatches.BeginHold(teal, new Point(10, 10));
            scheduler.Advance(hold);
            Assert.Equal(["#FF7F50"], tools.Palette.Palette.Colors);
            Assert.True(swatches.EndHold());
            Assert.Equal(colour, tools.Color);
            WpfTestThread.Drain();
            Assert.Single(swatches.CustomButtons);

            // A preset is never removed.
            swatches.BeginHold(swatches.PresetButtons[1], new Point(10, 10));
            scheduler.Advance(hold);
            Assert.False(swatches.EndHold());
            Assert.Equal(8, swatches.PresetButtons.Count);

            // Delete on a focused custom swatch does the same (keyboard, NFR-31).
            Assert.True(swatches.HandleKey(swatches.CustomButtons[0], Key.Delete));
            Assert.Empty(tools.Palette.Palette.Colors);
            Assert.False(swatches.HandleKey(swatches.PresetButtons[0], Key.Delete));
        });
    }

    [Fact]
    public void The_highlighter_swatches_share_the_palette_at_its_alpha()
    {
        WpfTestThread.Run(() =>
        {
            var tools = new ToolsViewModel();
            var highlighter = tools.Highlighter;
            var swatches = new ColorSwatches();
            Bind(swatches, highlighter, nameof(HighlighterOptionsViewModel.Swatches), nameof(HighlighterOptionsViewModel.CustomColors),
                nameof(HighlighterOptionsViewModel.Color));
            swatches.SelectCommand = highlighter.SelectColorCommand;
            swatches.AddCommand = highlighter.AddCustomColorCommand;
            swatches.RemoveCommand = highlighter.RemoveCustomColorCommand;
            ScrollRailTests.LayOut(swatches, 300, 400);

            Assert.Equal(6, swatches.PresetButtons.Count);
            Assert.True(swatches.IsChosen(swatches.PresetButtons[0])); // yellow
            ToolbarViewTests.Invoke(swatches.AddButton);
            swatches.Picker.Model.Hex = "#008080";
            ToolbarViewTests.Invoke(swatches.Picker.ConfirmButton);
            Assert.Equal(_teal with { A = HighlighterPresets.Alpha }, highlighter.Color);
            WpfTestThread.Drain();
            Assert.True(swatches.IsChosen(Assert.Single(swatches.CustomButtons)));
        });
    }

    private static void Bind(ColorSwatches swatches, object source, string presets, string custom, string selected)
    {
        BindingOperations.SetBinding(swatches, ColorSwatches.PresetsProperty, new Binding(presets) { Source = source });
        BindingOperations.SetBinding(swatches, ColorSwatches.CustomColorsProperty, new Binding(custom) { Source = source });
        BindingOperations.SetBinding(swatches, ColorSwatches.SelectedColorProperty, new Binding(selected) { Source = source, Mode = BindingMode.OneWay });
    }

    private static void WithPen(Action<ColorSwatches, ToolsViewModel, PenOptionsViewModel, ManualScheduler> test)
    {
        WpfTestThread.Run(() =>
        {
            var tools = new ToolsViewModel();
            var pen = new PenOptionsViewModel(tools);
            var scheduler = new ManualScheduler();
            var swatches = new ColorSwatches { Scheduler = scheduler };
            Bind(swatches, pen, nameof(PenOptionsViewModel.Presets), nameof(PenOptionsViewModel.CustomColors), nameof(PenOptionsViewModel.Color));
            swatches.SelectCommand = pen.SelectColorCommand;
            swatches.AddCommand = pen.AddCustomColorCommand;
            swatches.RemoveCommand = pen.RemoveCustomColorCommand;
            ScrollRailTests.LayOut(swatches, 300, 400);
            test(swatches, tools, pen, scheduler);
        });
    }
}
