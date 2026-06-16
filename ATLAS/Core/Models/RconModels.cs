namespace Atlas.Core.Models;

/// <summary>Connection state of the BattlEye RCon client.</summary>
public enum RconState
{
    Disconnected,
    Connecting,
    Connected,
    Failed
}

/// <summary>A player parsed from the BattlEye <c>players</c> command output.</summary>
public class RconPlayer
{
    public int Id { get; set; }
    public string IpPort { get; set; } = string.Empty;
    public int Ping { get; set; }
    public string Guid { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>True while the player is still in the lobby (mission not yet joined).</summary>
    public bool InLobby { get; set; }

    /// <summary>The IP portion of <see cref="IpPort"/> (without the trailing <c>:port</c>).</summary>
    public string Ip
    {
        get
        {
            var i = IpPort.LastIndexOf(':');
            return i > 0 ? IpPort[..i] : IpPort;
        }
    }
}

/// <summary>A ban parsed from the BattlEye <c>bans</c> command output (GUID and IP sections).</summary>
public class RconBan
{
    public int Index { get; set; }
    public string GuidOrIp { get; set; } = string.Empty;
    public int MinutesRemaining { get; set; }   // -1 = permanent
    public string Reason { get; set; } = string.Empty;

    /// <summary>Best-effort name resolved from <c>PlayerHistory</c> by the view model (not parsed from RCON).</summary>
    public string KnownName { get; set; } = string.Empty;

    public bool IsPermanent => MinutesRemaining < 0;
}
