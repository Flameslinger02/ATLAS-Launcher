using System.Globalization;
using System.Windows.Data;

namespace Atlas.Resources.Converters;

/// <summary>Converts an enum value to/from its string name. Supports nullable enum targets.</summary>
public sealed class EnumToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return Binding.DoNothing;

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (enumType.IsEnum && Enum.TryParse(enumType, s, ignoreCase: true, out var result))
            return result;

        return Binding.DoNothing;
    }
}
