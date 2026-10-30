using System.Globalization;
using System.Windows.Data;

namespace DreamBooks.App.Converters;

/// <summary>Two-way binding of an enum property to a RadioButton; the parameter names the enum member.</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}
