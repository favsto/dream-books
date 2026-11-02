using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

public class HighlighterOptionsViewModelTests
{
    [Fact]
    public void Defaults_are_the_core_highlighter_defaults()
    {
        var options = new HighlighterOptionsViewModel();

        Assert.Equal(HighlighterSettings.Default, options.Settings);
        Assert.Equal(HighlighterPresets.Yellow, options.Color);
        Assert.True(options.IsRound);
        Assert.False(options.IsChisel);
        Assert.Equal(45, options.ChiselAngleDeg);
        Assert.False(options.IsStraightLine);
    }

    [Fact]
    public void Six_swatches_carry_the_presets_and_their_italian_names()
    {
        var options = new HighlighterOptionsViewModel();

        Assert.Equal(HighlighterPresets.All, options.Swatches.Select(s => s.Color));
        Assert.Equal(
            [Strings.HighlighterYellow, Strings.HighlighterGreen, Strings.HighlighterLightBlue,
             Strings.HighlighterPink, Strings.HighlighterOrange, Strings.HighlighterPurple],
            options.Swatches.Select(s => s.Name));
        Assert.All(options.Swatches, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
        Assert.Equal(6, options.Swatches.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void A_swatch_sets_the_colour_with_the_highlighter_alpha()
    {
        var options = new HighlighterOptionsViewModel();

        foreach (var swatch in options.Swatches)
        {
            Assert.True(options.SelectColorCommand.CanExecute(swatch.Color));
            options.SelectColorCommand.Execute(swatch.Color);

            Assert.Equal(swatch.Color, options.Color);
            Assert.Equal(swatch.Color, options.Settings.Color);
            Assert.Equal(HighlighterPresets.Alpha, options.Settings.StrokeColor.A);
            Assert.Equal(89, options.Color.A);
        }
    }

    [Fact]
    public void An_opaque_colour_keeps_the_highlighter_alpha()
    {
        var options = new HighlighterOptionsViewModel();

        options.SelectColorCommand.Execute(new InkColor(255, 10, 20, 30));

        Assert.Equal(new InkColor(HighlighterPresets.Alpha, 10, 20, 30), options.Color);
    }

    [Theory]
    [InlineData(200, 180)]
    [InlineData(180, 180)]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(double.PositiveInfinity, 180)]
    [InlineData(double.NegativeInfinity, 0)]
    public void The_chisel_angle_is_clamped_to_0_180(double value, double expected)
    {
        var options = new HighlighterOptionsViewModel { ChiselAngleDeg = value };

        Assert.Equal(expected, options.ChiselAngleDeg);
        Assert.Equal(expected, options.Settings.ChiselAngleDeg);
    }

    [Fact]
    public void A_nan_angle_is_ignored()
    {
        var options = new HighlighterOptionsViewModel { ChiselAngleDeg = 60 };

        options.ChiselAngleDeg = double.NaN;

        Assert.Equal(60, options.ChiselAngleDeg);
    }

    [Fact]
    public void The_shape_switch_selects_round_or_chisel()
    {
        var options = new HighlighterOptionsViewModel();
        var changed = new List<string?>();
        options.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        options.IsChisel = true;

        Assert.True(options.Settings.Chisel);
        Assert.False(options.IsRound);
        Assert.Contains(nameof(HighlighterOptionsViewModel.IsRound), changed);
        Assert.Equal(options.Settings.ChiselLengthPt, options.Settings.WidthPt);

        options.IsRound = true;

        Assert.False(options.Settings.Chisel);
        Assert.False(options.IsChisel);
        Assert.Equal(options.Settings.RoundWidthPt, options.Settings.WidthPt);
    }

    [Fact]
    public void The_straight_line_switch_changes_the_settings()
    {
        var options = new HighlighterOptionsViewModel { IsStraightLine = true };

        Assert.True(options.Settings.StraightLine);

        options.IsStraightLine = false;

        Assert.False(options.Settings.StraightLine);
    }

    [Fact]
    public void Each_real_change_raises_SettingsChanged_once()
    {
        var options = new HighlighterOptionsViewModel();
        var raised = 0;
        options.SettingsChanged += (_, _) => raised++;

        options.IsChisel = true;
        options.IsChisel = true; // no change
        options.ChiselAngleDeg = 90;
        options.ChiselAngleDeg = 90; // no change
        options.IsStraightLine = true;
        options.SelectColorCommand.Execute(HighlighterPresets.Green);
        options.SelectColorCommand.Execute(HighlighterPresets.Green); // no change

        Assert.Equal(4, raised);
    }

    [Fact]
    public void The_options_texts_come_from_the_resources()
    {
        foreach (var text in new[]
                 {
                     Strings.ToolHighlighter, Strings.HighlighterOptions, Strings.HighlighterTipRound,
                     Strings.HighlighterTipChisel, Strings.HighlighterAngle, Strings.HighlighterStraightLine,
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(text));
        }

        Assert.Equal("Evidenziatore", Strings.ToolHighlighter);
        Assert.Equal("Punta tonda", Strings.HighlighterTipRound);
        Assert.Equal("Punta a scalpello", Strings.HighlighterTipChisel);
        Assert.Equal("Angolo", Strings.HighlighterAngle);
        Assert.Equal("Linea dritta", Strings.HighlighterStraightLine);
        Assert.Equal(
            ["Giallo", "Verde", "Azzurro", "Rosa", "Arancio", "Viola"],
            new HighlighterOptionsViewModel().Swatches.Select(s => s.Name));
    }

    [Fact]
    public void View_model_holds_no_wpf_types()
    {
        var wpf = typeof(System.Windows.UIElement).Assembly;
        foreach (var property in typeof(HighlighterOptionsViewModel).GetProperties())
        {
            Assert.NotEqual(wpf, property.PropertyType.Assembly);
        }
    }
}
