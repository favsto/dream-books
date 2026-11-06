using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Settings;

namespace DreamBooks.Core.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void Default_holds_the_factory_values()
    {
        var d = AppSettings.Default;

        Assert.Equal(ThemeMode.System, d.ThemeMode);
        Assert.Null(d.AccentOverride);
        Assert.Equal(EraserMode.WholeStroke, d.EraserMode);
        Assert.Equal(PressureCurve.Firm, d.PressureCurve);
        Assert.Equal(SmoothingLevel.Strong, d.Smoothing);
        Assert.False(d.ToolbarFixed);
        Assert.Equal(ToolbarEdge.Bottom, d.ToolbarEdge);
        Assert.Equal(InkColor.FromRgb(0, 0, 0), d.PenColor);
        Assert.Equal(0.5, d.PenWidthMm);
        Assert.Equal(HighlighterPresets.Yellow, d.HighlighterColor);
        Assert.False(d.HighlighterChisel);
        Assert.Equal(45, d.HighlighterAngleDeg);
        Assert.False(d.HighlighterStraightLine);
        Assert.Equal(6, d.EraserSizeMm);
        Assert.Equal(0, d.CustomPalette.Count);
    }

    [Fact]
    public void With_copies_keep_the_other_fields()
    {
        var changed = AppSettings.Default with { ThemeMode = ThemeMode.Dark };

        Assert.Equal(ThemeMode.Dark, changed.ThemeMode);
        Assert.Equal(AppSettings.Default.PenWidthMm, changed.PenWidthMm);
        Assert.NotEqual(AppSettings.Default, changed);
    }

    [Theory]
    [InlineData("#a1B2c3", true, "#A1B2C3")]
    [InlineData("A1B2C3", true, "#A1B2C3")]
    [InlineData("#A1B2C", false, "")]
    [InlineData("#A1B2C3D", false, "")]
    [InlineData("#ZZZZZZ", false, "")]
    [InlineData(null, false, "")]
    public void HexColor_normalises_or_rejects(string? text, bool ok, string expected)
    {
        Assert.Equal(ok, HexColor.TryNormalize(text, out var n));
        Assert.Equal(expected, n);
    }

    [Fact]
    public void HexColor_parses_and_formats()
    {
        Assert.True(HexColor.TryParse("#0A0B0C", 89, out var c));
        Assert.Equal(new InkColor(89, 10, 11, 12), c);
        Assert.Equal("#0A0B0C", HexColor.Format(c));
    }
}
