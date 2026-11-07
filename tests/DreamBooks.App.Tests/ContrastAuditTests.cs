using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace DreamBooks.App.Tests;

/// <summary>
/// WCAG AA contrast of the theme colours (NFR-30): 4.5:1 for text, 3:1 for icons and other graphics, in both themes.
/// The pairs are read from the theme dictionaries themselves, so a changed colour is checked without touching the test.
/// The system accent colour is the user's: it is checked with the Windows default accents only.
/// </summary>
public class ContrastAuditTests
{
    private const double _text = 4.5;
    private const double _graphic = 3.0;

    // Windows' default accent in both themes (the accent override of the settings can be any colour).
    private static readonly Color _defaultAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Text_on_the_panel_reads_at_least_4_5_to_1(string theme) // NFR-30
    {
        var dictionary = Load($"Theme.{theme}.xaml");
        var panel = ColorOf(dictionary, "ThemePanelColor");

        AssertRatio($"{theme} GlyphBrush on panel", Brush(dictionary, "GlyphBrush", panel), panel, _text);
        AssertRatio($"{theme} FlyoutHeaderBrush on panel", Brush(dictionary, "FlyoutHeaderBrush", panel), panel, _text);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Icons_and_marks_read_at_least_3_to_1(string theme) // NFR-30
    {
        var dictionary = Load($"Theme.{theme}.xaml");
        var panel = ColorOf(dictionary, "ThemePanelColor");

        // The active tool's icon is drawn in the accent colour on the panel.
        AssertRatio($"{theme} accent on panel", _defaultAccent, panel, _graphic);

        // The marks on the scroll rail: the text colour (75%) on the rail track (55% grey) over the page backdrop.
        var backdrop = ColorOf(dictionary, "PageBackdropColor");
        var track = Brush(dictionary, "RailTrackBrush", backdrop);
        AssertRatio($"{theme} rail mark tick on track", Brush(dictionary, "RailMarkTickBrush", track), track, _graphic);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Splash_text_reads_at_least_4_5_to_1_on_its_background(string variant) // NFR-30
    {
        var brushes = LiteralBrushes("Theme.xaml");
        var background = brushes[$"Splash{variant}BackgroundBrush"];

        AssertRatio($"Splash {variant} text", brushes[$"Splash{variant}TextBrush"], background, _text);
        AssertRatio($"Splash {variant} subtext", brushes[$"Splash{variant}SubtextBrush"], background, _text);
    }

    [Fact]
    public void The_ratio_matches_known_wcag_values()
    {
        Assert.Equal(21, Ratio(Colors.Black, Colors.White), 3);
        Assert.Equal(1, Ratio(Colors.White, Colors.White), 3);
        Assert.Equal(4.54, Ratio(Color.FromRgb(0x76, 0x76, 0x76), Colors.White), 2); // #767676 on white: the AA limit
    }

    private static ResourceDictionary Load(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "DreamBooks.App", "Resources")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var stream = File.OpenRead(Path.Combine(directory.FullName, "src", "DreamBooks.App", "Resources", file));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static Color ColorOf(ResourceDictionary dictionary, string key) => (Color)dictionary[key];

    /// <summary>The opaque brushes of a file that cannot be loaded alone (templates refer to other dictionaries): key to colour.</summary>
    private static Dictionary<string, Color> LiteralBrushes(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "DreamBooks.App", "Resources")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(directory.FullName, "src", "DreamBooks.App", "Resources", file));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return xaml.Descendants()
            .Where(e => e.Name.LocalName == "SolidColorBrush" && e.Attribute(x + "Key") is not null && e.Attribute("Color")?.Value.StartsWith('#') == true)
            .ToDictionary(e => e.Attribute(x + "Key")!.Value, e => (Color)ColorConverter.ConvertFromString(e.Attribute("Color")!.Value));
    }

    /// <summary>A theme brush as it looks over <paramref name="behind"/>: its colour at its opacity.</summary>
    private static Color Brush(ResourceDictionary dictionary, string key, Color behind)
    {
        var brush = (SolidColorBrush)dictionary[key];
        var alpha = brush.Opacity * brush.Color.A / 255.0;
        return Color.FromRgb(
            Blend(brush.Color.R, behind.R, alpha),
            Blend(brush.Color.G, behind.G, alpha),
            Blend(brush.Color.B, behind.B, alpha));
    }

    private static byte Blend(byte top, byte bottom, double alpha) => (byte)Math.Round((top * alpha) + (bottom * (1 - alpha)));

    private static void AssertRatio(string what, Color foreground, Color background, double minimum)
    {
        var ratio = Ratio(foreground, background);
        Assert.True(ratio >= minimum, $"{what}: {ratio:0.00}:1, needs {minimum}:1 ({foreground} on {background})");
    }

    /// <summary>The WCAG 2 contrast ratio, (lighter + 0.05) / (darker + 0.05).</summary>
    private static double Ratio(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c) =>
        (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));

    private static double Linear(byte channel)
    {
        var s = channel / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
