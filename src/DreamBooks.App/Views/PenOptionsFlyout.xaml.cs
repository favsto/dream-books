using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;

namespace DreamBooks.App.Views;

/// <summary>
/// The pen's option panel (UI-02), hosted and animated by <see cref="ToolbarView"/>; its data context is the
/// <see cref="ToolbarViewModel"/>. The preset width buttons show a dot as thick as the width.
/// </summary>
public partial class PenOptionsFlyout : UserControl
{
    private const double _dipPerMm = 96 / 25.4;

    public PenOptionsFlyout()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BuildWidthPresets();
    }

    internal IReadOnlyList<Button> WidthPresetButtons => WidthPresetPanel.Children.OfType<Button>().ToList();

    private void BuildWidthPresets()
    {
        WidthPresetPanel.Children.Clear();
        if (DataContext is not ToolbarViewModel toolbar)
        {
            return;
        }

        foreach (var width in toolbar.Pen.WidthPresets)
        {
            var diameter = width * _dipPerMm * (TryFindResource("WidthPresetDotScale") as double? ?? 1);
            var dot = new Ellipse { Width = diameter, Height = diameter, IsHitTestVisible = false };
            dot.SetResourceReference(Shape.FillProperty, "GlyphBrush");
            var name = string.Format(CultureInfo.CurrentCulture, Strings.WidthPresetNameFormat, width.ToString("0.#", CultureInfo.CurrentCulture));
            var button = new Button
            {
                Style = (Style)FindResource("SwatchButtonStyle"),
                Content = dot,
                ToolTip = name,
                Command = toolbar.Pen.SelectWidthCommand,
                CommandParameter = width,
            };
            AutomationProperties.SetName(button, name);
            WidthPresetPanel.Children.Add(button);
        }
    }
}
