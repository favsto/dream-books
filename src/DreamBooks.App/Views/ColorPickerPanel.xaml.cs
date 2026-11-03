using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Views;

/// <summary>
/// The free colour picker hosted by <see cref="ColorSwatches"/> (UI-02 custom palette): its state is a
/// <see cref="ColorPickerViewModel"/>; Add raises <see cref="Confirmed"/> with the colour, Cancel raises
/// <see cref="Cancelled"/>. The square follows the pen, a finger or the mouse (the pen through its promoted mouse
/// events) and the arrow keys. The hex field keeps the code as typed; it is written in the usual form (<c>#RRGGBB</c>)
/// when the field loses the keyboard or on Enter.
/// </summary>
public partial class ColorPickerPanel : UserControl
{
    /// <summary>One arrow key moves the square's point by this much (of 1).</summary>
    private const double _keyStep = 0.05;

    public ColorPickerPanel()
    {
        InitializeComponent();
        Root.DataContext = Model;
        HueStrip.Background = Spectrum();
        Model.PropertyChanged += OnModelChanged;
        Square.SizeChanged += (_, _) =>
        {
            SquareClip.Rect = new Rect(Square.RenderSize);
            PlaceThumb();
        };
    }

    /// <summary>The colour being picked, opaque.</summary>
    public event EventHandler<InkColor>? Confirmed;

    public event EventHandler? Cancelled;

    internal ColorPickerViewModel Model { get; } = new();

    /// <summary>The point of the square under <paramref name="position"/> (square coordinates) becomes the colour.</summary>
    internal void PickAt(Point position)
    {
        var size = Square.RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        Model.Saturation = position.X / size.Width;
        Model.Brightness = 1 - (position.Y / size.Height);
    }

    /// <summary>
    /// The hue spectrum behind the hue slider: the HSV model's pure hues every 60 degrees (the colour model itself, the
    /// same in both themes, not a styling value).
    /// </summary>
    private static LinearGradientBrush Spectrum()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var hue = 0; hue <= 360; hue += 60)
        {
            var c = ColorPickerViewModel.FromHsv(hue % 360, 1, 1);
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(c.R, c.G, c.B), hue / 360.0));
        }

        brush.Freeze();
        return brush;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ColorPickerViewModel.Saturation) or nameof(ColorPickerViewModel.Brightness))
        {
            PlaceThumb();
        }
    }

    private void PlaceThumb()
    {
        var size = Square.RenderSize;
        var half = Thumb.Width / 2;
        Canvas.SetLeft(Thumb, (Model.Saturation * size.Width) - half);
        Canvas.SetTop(Thumb, ((1 - Model.Brightness) * size.Height) - half);
    }

    private void OnSquareMouseDown(object sender, MouseButtonEventArgs e)
    {
        Square.Focus();
        Square.CaptureMouse();
        PickAt(e.GetPosition(Square));
        e.Handled = true;
    }

    private void OnSquareMouseMove(object sender, MouseEventArgs e)
    {
        if (Square.IsMouseCaptured)
        {
            PickAt(e.GetPosition(Square));
        }
    }

    private void OnSquareMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Square.IsMouseCaptured)
        {
            Square.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void OnSquareKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                Model.Saturation -= _keyStep;
                break;
            case Key.Right:
                Model.Saturation += _keyStep;
                break;
            case Key.Up:
                Model.Brightness += _keyStep;
                break;
            case Key.Down:
                Model.Brightness -= _keyStep;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnHexFieldLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Model.CommitHex();

    private void OnHexFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Model.CommitHex();
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Model.CommitHex();
        if (Model.IsHexValid)
        {
            Confirmed?.Invoke(this, Model.Color);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);
}
