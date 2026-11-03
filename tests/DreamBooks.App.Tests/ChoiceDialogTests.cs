using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using Wpf.Ui.Appearance;

namespace DreamBooks.App.Tests;

/// <summary>The confirmation dialog is readable in both themes (UI-02): its buttons were invisible on the device.</summary>
public sealed class ChoiceDialogTests
{
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    internal static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static ResourceDictionary Theme(ApplicationTheme theme)
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DreamBooks.App;component/Resources/Theme.xaml", UriKind.Relative) });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeService.ThemeDictionaryUri(theme) });
        return resources;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void Button_text_and_window_text_contrast_with_their_backing_in_both_themes(ApplicationTheme theme)
    {
        WpfTestThread.Run(() =>
        {
            var resources = Theme(theme);
            var text = ((SolidColorBrush)resources["GlyphBrush"]).Color;
            Assert.True(Contrast(text, ((SolidColorBrush)resources["DialogButtonBrush"]).Color) >= 4.5);
            Assert.True(Contrast(text, ((SolidColorBrush)resources["DialogButtonHoverBrush"]).Color) >= 4.5);
            Assert.True(Contrast(text, ((SolidColorBrush)resources["WindowBackgroundOpaqueBrush"]).Color) >= 4.5);
        });
    }

    [Fact]
    public void The_dialog_uses_the_themed_styles_and_italian_labels_with_enter_and_escape()
    {
        WpfTestThread.Run(() =>
        {
            var dialog = new ChoiceDialog(FileDialogService.SaveChangesQuestion);
            var style = (Style)Application.Current.FindResource("DialogButtonStyle");
            Assert.All(dialog.Buttons, b => Assert.Same(style, b.Style));
            Assert.Equal([Strings.AnswerSave, Strings.AnswerDontSave, Strings.AnswerCancel], dialog.Buttons.Select(b => (string)b.Content));
            Assert.Equal(["Salva", "Non salvare", "Annulla"], dialog.Buttons.Select(b => (string)b.Content));
            Assert.True(dialog.Buttons[0].IsDefault);
            Assert.True(dialog.Buttons[2].IsCancel);
            Assert.NotNull(dialog.FindResource("WindowBackgroundOpaqueBrush"));
            dialog.Close();
        });
    }
}
