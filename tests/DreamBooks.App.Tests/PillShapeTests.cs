using System.Windows;
using System.Windows.Controls;
using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>
/// A pill is a rounded band: straight long sides, ends as half circles (section 3). A WPF border does not clamp a huge
/// corner radius the way CSS does, so a wide 999 px "pill" would be an ellipse (UI-02, UI-06).
/// </summary>
public sealed class PillShapeTests
{
    private static void Arrange(FrameworkElement element, double width, double height)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    [Fact]
    public void A_wide_pill_surface_rounds_its_ends_by_half_its_height()
    {
        WpfTestThread.Run(() =>
        {
            var surface = new FloatingSurface { CornerRadius = (CornerRadius)Application.Current.FindResource("PillCornerRadius") };
            Arrange(surface, 400, 52);

            Assert.Equal(new CornerRadius(26), surface.DiffuseLayer.CornerRadius);
            Assert.Equal(new CornerRadius(26), surface.ContactLayer.CornerRadius);
        });
    }

    [Fact]
    public void A_panel_surface_keeps_its_radius_and_a_tall_pill_uses_half_its_width()
    {
        WpfTestThread.Run(() =>
        {
            var panel = new FloatingSurface { CornerRadius = (CornerRadius)Application.Current.FindResource("PanelCornerRadius") };
            Arrange(panel, 300, 200);
            Assert.Equal(new CornerRadius(12), panel.DiffuseLayer.CornerRadius);

            var round = new FloatingSurface { CornerRadius = (CornerRadius)Application.Current.FindResource("PillCornerRadius") };
            Arrange(round, 40, 40);
            Assert.Equal(new CornerRadius(20), round.DiffuseLayer.CornerRadius);
        });
    }

    [Fact]
    public void A_pill_border_rounds_by_half_its_shorter_side()
    {
        WpfTestThread.Run(() =>
        {
            var wide = new PillBorder();
            Arrange(wide, 120, 40);
            Assert.Equal(new CornerRadius(20), wide.CornerRadius);

            var tall = new PillBorder();
            Arrange(tall, 30, 90);
            Assert.Equal(new CornerRadius(15), tall.CornerRadius);
        });
    }

    [Fact]
    public void The_chip_button_background_is_a_pill_border_not_an_ellipse()
    {
        WpfTestThread.Run(() =>
        {
            var button = new Button { Style = (Style)Application.Current.FindResource("ChipTextButtonStyle"), Content = "12 / 48 · 125%" };
            Arrange(button, 160, 40);
            var border = Assert.IsType<PillBorder>(button.Template.FindName("Disc", button));
            Assert.Equal(new CornerRadius(20), border.CornerRadius);
        });
    }
}
