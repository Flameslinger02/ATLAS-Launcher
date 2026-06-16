using System.Globalization;
using System.Windows.Data;

namespace Atlas.Resources.Converters;

/// <summary>Formats a <see cref="TimeSpan"/> as HH:mm:ss (or mm:ss when under an hour).</summary>
public sealed class TimeSpanToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TimeSpan ts) return "00:00:00";
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"00:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
