using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Atlas.Resources.Converters;

/// <summary>
/// Resolves a mod's status indicator <see cref="Brush"/> from its check state. Bind two values in order:
/// [0] <c>LastChecked</c> (<see cref="DateTime"/>), [1] <c>UpdateAvailable</c> (<see cref="bool"/>).
/// Never checked (<c>LastChecked == default</c>) ⇒ gray (TextSecondary); update available ⇒ amber (Warning);
/// otherwise up to date ⇒ green (Success). Falls back to a gray brush if a theme key is missing.
/// </summary>
public sealed class ModStatusToBrushConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var lastChecked = values is { Length: > 0 } && values[0] is DateTime dt ? dt : default;
        var updateAvailable = values is { Length: > 1 } && values[1] is bool b && b;

        var key = lastChecked == default
            ? "Atlas.Brush.TextSecondary"
            : updateAvailable ? "Atlas.Brush.Warning" : "Atlas.Brush.Success";

        return ResolveBrush(key);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush ResolveBrush(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
            return brush;

        var fallback = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        fallback.Freeze();
        return fallback;
    }
}
