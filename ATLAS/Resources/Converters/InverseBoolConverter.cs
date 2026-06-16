using System.Globalization;
using System.Windows.Data;

namespace Atlas.Resources.Converters;

/// <summary>Inverts a boolean value (true ↔ false).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}
