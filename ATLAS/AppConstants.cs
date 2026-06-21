namespace Atlas;

/// <summary>
/// Central location for all magic strings, file-system paths, port defaults and
/// other constants used across ATLAS. Nothing here should be duplicated elsewhere.
/// </summary>
public static class AppConstants
{
    public const string AppName = "ATLAS";
    public const string AppFullName = "Arma Tactical Launch and Administration System";

    // ----- File system (rooted at %AppData%\ATLAS) -----
    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string LogsDirectory => Path.Combine(AppDataRoot, "Logs");
    public static string DatabasePath => Path.Combine(AppDataRoot, "atlas.db");
    public static string SettingsPath => Path.Combine(AppDataRoot, "settings.json");
    public static string SteamCmdDirectory => Path.Combine(AppDataRoot, "SteamCMD");
    public static string HeadlessClientProfilesDirectory => Path.Combine(AppDataRoot, "HCProfiles");

    // ----- Ports -----
    public const int DefaultGamePort = 2302;
    public const int DefaultRconPort = 2301;
    public const int ReservedPortRangeStart = 2302; // 2302-2306 reserved by the game; never use for RCON
    public const int ReservedPortRangeEnd = 2306;
    public const int MinUserPort = 1024;
    public const int MaxPort = 65535;

    // ----- Steam -----
    public const string Arma3ServerAppId = "233780"; // Arma 3 Dedicated Server
    public const string Arma3WorkshopAppId = "107410"; // Arma 3 game (workshop content)
    public const string SteamCmdDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    // ----- GitHub (Phase 0 Q1 — update checker / About) -----
    public const string GitHubOwner = "Flameslinger02";
    public const string GitHubRepo = "ATLAS-Launcher";

    // ----- Navigation page keys -----
    public static class Pages
    {
        public const string Dashboard = "Dashboard";
        public const string ServerConfig = "ServerConfig";
        public const string Mods = "Mods";
        public const string ModPresets = "ModPresets";
        public const string Missions = "Missions";
        public const string Profiles = "Profiles";                // all-profiles overview
        public const string ProfileWorkspace = "ProfileWorkspace"; // single-profile editor (4 tabs)
        public const string HeadlessClients = "HeadlessClients";
        public const string DiscordBot = "DiscordBot";
        public const string Scheduler = "Scheduler";
        public const string Console = "Console";   // logs (ATLAS Log + Server RPT) + the Updates tab
        public const string Rcon = "Rcon";         // live BattlEye RCON management
        public const string Settings = "Settings";
        // NOTE: ServerBrowser intentionally omitted (Phase 0 Q6 = No).
    }
}
