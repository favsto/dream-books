using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;

namespace DreamBooks.App.Views;

/// <summary>
/// The lasso's actions at the top centre (FR-51): the preset colours, copy, paste and delete. Shown through the view
/// model's <c>IsSelectionBarVisible</c>; the buttons are the <see cref="SelectionViewModel"/>'s commands, so they are
/// disabled while they have nothing to act on. The swatches are the pen's presets (FR-25), named for screen readers
/// (NFR-31). With Windows transparency effects off the surface is opaque (NFR-32), checked each time the bar shows.
/// </summary>
public partial class SelectionActionBar : UserControl
{
    private bool _built;

    public SelectionActionBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BuildSwatches();
        IsVisibleChanged += (_, _) => ApplyTransparency();
        Loaded += (_, _) =>
        {
            BuildSwatches();
            ApplyTransparency();
        };
    }

    /// <summary>The colour buttons, left to right; exposed for tests.</summary>
    internal IReadOnlyList<Button> SwatchButtons => [.. SwatchRow.Children.OfType<Button>()];

    /// <summary>The Windows transparency setting (NFR-32); tests inject it.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    internal void ApplyTransparency() => Surface.IsOpaque = !TransparencyEnabled();

    /// <summary>The Copy, Paste and Delete buttons and the surface state; exposed for tests.</summary>
    internal Button CopyButton => CopyButtonElement;

    internal Button PasteButton => PasteButtonElement;

    internal Button DeleteButton => DeleteButtonElement;

    internal bool OpaqueSurface => Surface.IsOpaque;

    private void BuildSwatches()
    {
        if (_built || DataContext is not MainViewModel vm)
        {
            return;
        }

        _built = true;
        foreach (var swatch in vm.Toolbar.Pen.Presets)
        {
            var fill = new SolidColorBrush(Color.FromArgb(swatch.Color.A, swatch.Color.R, swatch.Color.G, swatch.Color.B));
            fill.Freeze();
            var disc = new Ellipse { Fill = fill, StrokeThickness = (double)FindResource("SwatchOutlineThickness"), IsHitTestVisible = false };
            disc.SetResourceReference(WidthProperty, "SwatchSize");
            disc.SetResourceReference(HeightProperty, "SwatchSize");
            disc.SetResourceReference(Shape.StrokeProperty, "SwatchOutlineBrush");
            var button = new Button
            {
                Style = (Style)FindResource("SwatchButtonStyle"),
                Content = disc,
                ToolTip = swatch.Name,
                CommandParameter = swatch.Color,
            };
            button.SetBinding(Button.CommandProperty, new Binding("Selection.RecolorCommand"));
            AutomationProperties.SetName(button, swatch.Name);
            SwatchRow.Children.Add(button);
        }
    }
}
