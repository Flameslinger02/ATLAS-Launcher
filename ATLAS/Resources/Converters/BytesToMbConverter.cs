using System.Globalization;
using System.Windows.Data;

namespace Atlas.Resources.Converters;

/// <summary>Formats a byte count as a human-readable size (B / KB / MB / GB / TB).</summary>
public sealed class BytesToMbConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double bytes = value switch
        {
            long l => l,
            int i => i,
            ulong u => u,
            double d => d,
            _ => 0
        };
        return FormatSize(bytes);
    }

    public static string FormatSize(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var index = 0;
        var size = bytes;
        while (size >= 1024 && index < units.Length - 1)
        {
            size /= 1024;
            index++;
        }
        return $"{size:0.##} {units[index]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
