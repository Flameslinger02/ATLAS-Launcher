using Atlas.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atlas.Pages.Console;

/// <summary>
/// View wrapper around a parsed <see cref="RconPlayer"/> for the Players grid. Adds per-row IP masking
/// (hidden by default, revealed via the context menu) and a friendly status string. The underlying
/// <see cref="Player"/> stays a clean parse model.
/// </summary>
public partial class PlayerRow : ObservableObject
{
    public RconPlayer Player { get; }

    [ObservableProperty] private bool _isUnmasked;

    public PlayerRow(RconPlayer player) => Player = player;

    public int Id => Player.Id;
    public string Name => Player.Name;
    public int Ping => Player.Ping;
    public string Guid => Player.Guid;
    public bool Verified => Player.Verified;
    public string Status => Player.InLobby ? "Lobby" : "In-game";

    /// <summary>The IP column text: full <c>ip:port</c> when unmasked, otherwise the last octet only.</summary>
    public string DisplayIp => IsUnmasked ? Player.IpPort : MaskIp(Player.Ip);

    partial void OnIsUnmaskedChanged(bool value) => OnPropertyChanged(nameof(DisplayIp));

    private static string MaskIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return string.Empty;
        var octets = ip.Split('.');
        if (octets.Length == 4) return $"•••.•••.•••.{octets[3]}";
        return "•••";
    }
}
