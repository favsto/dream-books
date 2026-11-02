using DreamBooks.Core.Settings;

namespace DreamBooks.Core.Tests.Settings;

public class CustomPaletteTests
{
    [Fact]
    public void Add_normalises_to_upper_case_with_hash()
    {
        var palette = CustomPalette.Empty.Add("#aabbcc").Add("112233");

        Assert.Equal(["#AABBCC", "#112233"], palette.Colors);
    }

    [Fact]
    public void Add_ignores_invalid_colours()
    {
        var palette = CustomPalette.Empty.Add("#12345").Add("red").Add("#GGGGGG").Add("");

        Assert.Equal(0, palette.Count);
    }

    [Fact]
    public void Add_dedupes_case_insensitively_and_moves_the_colour_to_the_newest_slot()
    {
        var palette = CustomPalette.Empty.Add("#111111").Add("#AAAAAA").Add("#222222").Add("#aaaaaa");

        Assert.Equal(["#111111", "#222222", "#AAAAAA"], palette.Colors);
    }

    [Fact]
    public void Add_beyond_twelve_drops_the_oldest()
    {
        var palette = CustomPalette.Empty;
        for (var i = 0; i < 13; i++)
        {
            palette = palette.Add($"#0000{i:X2}");
        }

        Assert.Equal(12, palette.Count);
        Assert.False(palette.Contains("#000000"));
        Assert.Equal("#000001", palette.Colors[0]);
        Assert.Equal("#00000C", palette.Colors[11]);
    }

    [Fact]
    public void Re_adding_at_twelve_does_not_drop_anything()
    {
        var palette = CustomPalette.Empty;
        for (var i = 0; i < 12; i++)
        {
            palette = palette.Add($"#0000{i:X2}");
        }

        var again = palette.Add("#000000");

        Assert.Equal(12, again.Count);
        Assert.Equal("#000000", again.Colors[11]);
    }

    [Fact]
    public void Remove_is_case_insensitive_and_unknown_colours_change_nothing()
    {
        var palette = CustomPalette.Empty.Add("#ABCDEF").Add("#123456");

        Assert.Equal(["#123456"], palette.Remove("#abcdef").Colors);
        Assert.Same(palette, palette.Remove("#000000"));
        Assert.Same(palette, palette.Remove("junk"));
    }

    [Fact]
    public void Contains_is_case_insensitive()
    {
        var palette = CustomPalette.Empty.Add("#ABCDEF");

        Assert.True(palette.Contains("#abcdef"));
        Assert.False(palette.Contains("#000000"));
    }

    [Fact]
    public void Palettes_are_immutable_values()
    {
        var one = CustomPalette.Empty.Add("#111111");
        var two = one.Add("#222222");

        Assert.Equal(1, one.Count);
        Assert.Equal(2, two.Count);
        Assert.Equal(CustomPalette.Empty.Add("#111111"), one);
        Assert.Equal(CustomPalette.Empty.Add("#111111").GetHashCode(), one.GetHashCode());
        Assert.NotEqual(one, two);
    }

    [Fact]
    public void From_skips_invalid_entries_and_applies_the_add_rules()
    {
        var palette = CustomPalette.From(["#111111", "bad", null, "#111111", "#222222"]);

        Assert.Equal(["#111111", "#222222"], palette.Colors);
    }
}
