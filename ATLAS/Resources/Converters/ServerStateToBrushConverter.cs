using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Atlas.Core.Models;

namespace Atlas.Resources.Converters;

/// <summary>Maps a <see cref="ServerState"/> to a frozen status <see cref="Brush"/>.</summary>
public sealed class ServerStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is ServerState s
            ? ServerStateToColorConverter.ColorFor(s)
            : (Color)ColorConverter.ConvertFromString("#888888")!;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
