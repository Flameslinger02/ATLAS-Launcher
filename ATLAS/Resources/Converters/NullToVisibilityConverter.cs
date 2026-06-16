using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Atlas.Resources.Converters;

/// <summary>
/// Converts null/empty to <see cref="Visibility.Collapsed"/> and non-null to Visible. Pass
/// ConverterParameter="Invert" to flip (null → Visible).
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is null || (value is string s && s.Length == 0);
        if (IsInvert(parameter)) isEmpty = !isEmpty;
        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool IsInvert(object? parameter) =>
        parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}
