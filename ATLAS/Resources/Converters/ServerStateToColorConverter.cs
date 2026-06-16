using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Atlas.Core.Models;

namespace Atlas.Resources.Converters;

/// <summary>Maps a <see cref="ServerState"/> to a status <see cref="Color"/>.</summary>
public sealed class ServerStateToColorConverter : IValueConverter
{
    public static Color ColorFor(ServerState state) => state switch
    {
        ServerState.Running => Hex("#22CC44"),
        ServerState.Starting => Hex("#FFAA00"),
        ServerState.Stopping => Hex("#FFAA00"),
        ServerState.Updating => Hex("#3A8DDE"),
        ServerState.Crashed => Hex("#FF2222"),
        _ => Hex("#888888"),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ServerState s ? ColorFor(s) : Hex("#888888");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}
