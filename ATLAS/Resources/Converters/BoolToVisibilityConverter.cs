using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Atlas.Resources.Converters;

/// <summary>
/// Converts a boolean to <see cref="Visibility"/> (true → Visible). Pass ConverterParameter="Invert"
/// to flip the mapping (true → Collapsed).
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (IsInvert(parameter)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility.Visible;
        if (IsInvert(parameter)) visible = !visible;
        return visible;
    }

    private static bool IsInvert(object? parameter) =>
        parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}
