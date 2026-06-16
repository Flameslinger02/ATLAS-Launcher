namespace Atlas.Core.Models;

/// <summary>
/// Persisted configuration for the embedded Discord bot. Stored inside <see cref="AppSettings"/>.
/// The bot token is DPAPI-encrypted (see <see cref="TokenEncrypted"/>); it is never stored plaintext.
/// </summary>
public class DiscordBotConfig
{
    /// <summary>DPAPI-encrypted (CurrentUser) bot token. Empty when unset.</summary>
    public string TokenEncrypted { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public ulong StatusChannelId { get; set; }
    public ulong? PlayerLogChannelId { get; set; }
    public ulong? ConsoleLogChannelId { get; set; }
    public ulong? CommandChannelId { get; set; }
    public ulong[]? AdminRoleIds { get; set; }
    public ulong? OwnerRoleId { get; set; }
    public ulong? StatusMessageId { get; set; }
    public bool AllowDMs { get; set; }
    public bool NotifyPlayerJoin { get; set; } = true;
    public bool NotifyPlayerLeave { get; set; } = true;
    public bool NotifyCrash { get; set; } = true;
    public bool MirrorConsoleLog { get; set; }
    public ulong? PrimaryGuildId { get; set; }
}
