namespace Atlas.Core.Models;

/// <summary>
/// Application-level settings, persisted as JSON at <c>%AppData%\ATLAS\settings.json</c>.
/// Secrets (Steam API key, Discord token) are stored DPAPI-encrypted in their *Encrypted fields.
/// </summary>
public class AppSettings
{
    // ----- SteamCMD / Steam -----
    public string SteamCmdPath { get; set; } = string.Empty;
    public string ModStagingDirectory { get; set; } = string.Empty;

    /// <summary>Auto-detected (and editable) Arma 3 server install directory; used to pre-fill new profiles.</summary>
    public string ArmaServerDirectory { get; set; } = string.Empty;

    /// <summary>DPAPI-encrypted Steam Web API key (Phase 0 Q10). Empty when unset.</summary>
    public string SteamApiKeyEncrypted { get; set; } = string.Empty;

    public string SteamUsername { get; set; } = string.Empty;

    // NOTE (Phase 0 Q13): ATLAS uses SteamCMD's own cached login token (FASTER-style); the Steam
    // password is NEVER stored. This field is retained for forward-compat but is intentionally unused.
    public string SteamPasswordEncrypted { get; set; } = string.Empty;

    /// <summary>When true, remember the Steam username and rely on SteamCMD's cached session token.</summary>
    public bool RememberSteamCredentials { get; set; }

    // ----- Startup -----
    public bool AutoStartServerOnLaunch { get; set; } // Phase 0 Q3 = No (default off)
    public int LastActiveProfileId { get; set; }
    public bool MinimizeToTray { get; set; } = true;  // Phase 0 Q2 = Yes, default on (toggleable setting)

    // ----- Updates -----
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string GitHubOwner { get; set; } = AppConstants.GitHubOwner;
    public string GitHubRepo { get; set; } = AppConstants.GitHubRepo;
    public DateTime? LastUpdateCheck { get; set; }
    public string? LastKnownLatestVersion { get; set; }

    // ----- Logging -----
    public string LogLevel { get; set; } = "Information";

    // ----- Discord -----
    public DiscordBotConfig DiscordBot { get; set; } = new();

    // ----- Window / UI -----
    public double LastWindowWidth { get; set; } = 1280;
    public double LastWindowHeight { get; set; } = 800;

    /// <summary>Last window position; null until the window has been moved/closed once (then centered).</summary>
    public double? LastWindowLeft { get; set; }
    public double? LastWindowTop { get; set; }

    public bool SidebarCollapsed { get; set; }

    /// <summary>Active theme (Phase 0 Q14 = toggleable). "Dark" (default) or "Light".</summary>
    public string Theme { get; set; } = "Dark";
}
