using System.Windows;
using System.Windows.Controls;

namespace DreamBooks.App.Views;

/// <summary>
/// A border shaped as a pill (section 3): a rounded band whose ends are half circles and whose long sides are straight.
/// The corner radius is half the shorter side, set on every arrange; a WPF border given a huge radius would draw an
/// ellipse when it is wider than tall.
/// </summary>
public sealed class PillBorder : Border
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var radius = Math.Min(finalSize.Width, finalSize.Height) / 2;
        if (CornerRadius.TopLeft != radius)
        {
            CornerRadius = new CornerRadius(radius);
        }

        return base.ArrangeOverride(finalSize);
    }
}
