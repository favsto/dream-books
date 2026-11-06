using DreamBooks.App.ViewModels;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

/// <summary>The free colour picker's model (UI-02 custom palette): hue, saturation and brightness, and the hex code.</summary>
public class ColorPickerViewModelTests
{
    [Theory]
    [InlineData(0, 1, 1, 0xFF, 0x00, 0x00)]
    [InlineData(120, 1, 1, 0x00, 0xFF, 0x00)]
    [InlineData(240, 1, 1, 0x00, 0x00, 0xFF)]
    [InlineData(60, 1, 1, 0xFF, 0xFF, 0x00)]
    [InlineData(180, 1, 0.5, 0x00, 0x80, 0x80)]
    [InlineData(0, 0, 1, 0xFF, 0xFF, 0xFF)]
    [InlineData(300, 0.5, 0, 0x00, 0x00, 0x00)]
    public void Hsv_maps_to_rgb(double h, double s, double v, byte r, byte g, byte b)
    {
        Assert.Equal(InkColor.FromRgb(r, g, b), ColorPickerViewModel.FromHsv(h, s, v));
    }

    [Theory]
    [InlineData(0x1F, 0x5B, 0xD8)]
    [InlineData(0xD3, 0x2F, 0x2F)]
    [InlineData(0x00, 0x80, 0x80)]
    [InlineData(0x6B, 0x6B, 0x6B)]
    [InlineData(0x00, 0x00, 0x00)]
    public void Rgb_round_trips_through_hsv(byte r, byte g, byte b)
    {
        var colour = InkColor.FromRgb(r, g, b);
        var (h, s, v) = ColorPickerViewModel.ToHsv(colour);
        Assert.Equal(colour, ColorPickerViewModel.FromHsv(h, s, v));
    }

    [Fact]
    public void The_square_and_the_hue_set_the_colour_and_the_hex_code()
    {
        var picker = new ColorPickerViewModel();
        picker.Reset(InkColor.FromRgb(0x1F, 0x5B, 0xD8));
        Assert.Equal("#1F5BD8", picker.Hex);
        Assert.True(picker.IsHexValid);

        var changed = new List<string?>();
        picker.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        picker.Hue = 120;
        picker.Saturation = 1;
        picker.Brightness = 1;
        Assert.Equal(InkColor.FromRgb(0, 0xFF, 0), picker.Color);
        Assert.Equal("#00FF00", picker.Hex);
        Assert.Equal(InkColor.FromRgb(0, 0xFF, 0), picker.HueColor);
        Assert.Contains(nameof(ColorPickerViewModel.Color), changed);
        Assert.Contains(nameof(ColorPickerViewModel.Hex), changed);

        // Out of range values are clamped (a drag past the square's edge).
        picker.Saturation = 2;
        picker.Brightness = -1;
        Assert.Equal(1, picker.Saturation);
        Assert.Equal(0, picker.Brightness);
        picker.Hue = 400;
        Assert.InRange(picker.Hue, 0, 360);
    }

    [Fact]
    public void A_valid_code_being_typed_is_not_rewritten_until_it_is_committed()
    {
        var picker = new ColorPickerViewModel();
        picker.Reset(InkColor.FromRgb(0, 0, 0));
        var hexChanges = new List<string>();
        picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ColorPickerViewModel.Hex))
            {
                hexChanges.Add(picker.Hex);
            }
        };

        picker.Hex = "1f5bd8"; // valid at the sixth character, lower case, no #
        Assert.Equal("1f5bd8", picker.Hex); // the text under the caret stays as typed
        Assert.True(picker.IsHexValid);
        Assert.Equal(InkColor.FromRgb(0x1F, 0x5B, 0xD8), picker.Color); // exactly the typed colour
        Assert.DoesNotContain("#1F5BD8", hexChanges);

        picker.CommitHex(); // the field lost the keyboard, Enter, or Add
        Assert.Equal("#1F5BD8", picker.Hex);
        Assert.Equal(InkColor.FromRgb(0x1F, 0x5B, 0xD8), picker.Color);

        picker.Hex = "#12";
        picker.CommitHex(); // an invalid code stays as typed
        Assert.Equal("#12", picker.Hex);
        Assert.False(picker.IsHexValid);
    }

    [Fact]
    public void A_typed_hex_code_sets_the_colour_and_an_invalid_one_is_flagged()
    {
        var picker = new ColorPickerViewModel();
        picker.Reset(InkColor.FromRgb(0, 0, 0));

        picker.Hex = "008080"; // the # is optional
        Assert.True(picker.IsHexValid);
        Assert.Equal(InkColor.FromRgb(0x00, 0x80, 0x80), picker.Color);
        Assert.Equal(180, picker.Hue, 1);

        picker.Hex = "#12";
        Assert.False(picker.IsHexValid);
        Assert.Equal(InkColor.FromRgb(0x00, 0x80, 0x80), picker.Color); // keeps the last valid colour
        Assert.Equal("#12", picker.Hex); // the text being typed stays

        picker.Hex = "#zzzzzz";
        Assert.False(picker.IsHexValid);
        picker.Hue = 0;
        Assert.True(picker.IsHexValid); // moving the hue writes a valid code again
        Assert.Equal("#800000", picker.Hex);
    }
}
