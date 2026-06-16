using System.Globalization;
using System.Windows.Data;

namespace Atlas.Resources.Converters;

/// <summary>
/// Returns true when all bound values are equal (uses Equals, with numeric values normalized to long).
/// Used to highlight the active profile by comparing an item's Id to the active profile Id.
/// </summary>
public sealed class EqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        var first = Normalize(values[0]);
        if (first is null) return false;
        for (var i = 1; i < values.Length; i++)
        {
            var v = Normalize(values[i]);
            if (v is null || !first.Equals(v)) return false;
        }
        return true;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static object? Normalize(object? value) => value switch
    {
        null => null,
        sbyte or byte or short or ushort or int or uint or long => System.Convert.ToInt64(value),
        _ => value
    };
}
