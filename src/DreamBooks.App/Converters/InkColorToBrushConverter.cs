using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Converters;

public sealed class InkColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not InkColor c)
        {
            return Binding.DoNothing;
        }

        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
