namespace Atlas.Core.Models;

/// <summary>
/// In-memory domain object for a server profile. Scalars map to the <c>ServerProfiles</c> table;
/// the collection properties are assembled from child tables by <c>IProfileService</c> and are never
/// serialized into a column. All persistence goes through <c>IProfileService.UpdateProfileAsync</c>,
/// which wraps everything in a single SQLite transaction.
/// </summary>
public class ServerProfile
{
    // Identity
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public GameType GameType { get; set; } = GameType.Arma3;
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Notes { get; set; } = string.Empty;

    // Executable
    public string ServerExecutablePath { get; set; } = string.Empty;
    public bool UseProfilingBranch { get; set; }
    public string ServerDirectory { get; set; } = string.Empty;

    /// <summary>Optional override for the mission scan folder. Blank = scan the server's MPMissions/Missions.</summary>
    public string MissionDirectory { get; set; } = string.Empty;

    // Launch parameters
    public string ArmaProfileName { get; set; } = "ATLAS";
    public int Port { get; set; } = AppConstants.DefaultGamePort;
    public bool EnableBattlEye { get; set; } = true;
    public int FilePatching { get; set; }   // allowedFilePatching: 0 = none, 1 = headless only, 2 = all clients
    public bool NoSound { get; set; } = true;
    public bool NoSplash { get; set; } = true;
    public bool SkipIntro { get; set; } = true;
    public bool WorldEmpty { get; set; } = true;
    public bool NetLog { get; set; }
    public bool LoadMissionToMemory { get; set; }
    public bool NoPause { get; set; } = true;
    public bool NoLogs { get; set; }
    public bool RankingEnabled { get; set; }
    public string RankingFile { get; set; } = string.Empty;

    // Performance
    public bool EnableHT { get; set; }
    public bool HugePages { get; set; }
    public int CpuCount { get; set; }       // 0 = auto
    public int ExThreads { get; set; } = 7;
    public int MaxMem { get; set; }         // 0 = auto
    public string Malloc { get; set; } = string.Empty;
    public int BandwidthAlg { get; set; } = 2;
    public int LimitFPS { get; set; }       // 0 = no limit

    // server.cfg — Basic
    public string ServerName { get; set; } = "ATLAS Server";
    public string ServerPassword { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public int MaxPlayers { get; set; } = 32;
    public int MotdInterval { get; set; } = 5;
    public string LogFile { get; set; } = "server_console.log";
    public bool TimeStampFormat { get; set; }
    public bool DrawingInMap { get; set; } = true;

    // server.cfg — Network
    public int MaxPing { get; set; } = 300;
    public int MaxDesync { get; set; } = 150;
    public int MaxPacketLoss { get; set; } = 50;
    public int MaxMsgSend { get; set; } = 128;
    public int MaxSizeNonguaranteed { get; set; } = 512;
    public int MaxSizeGuaranteed { get; set; } = 512;
    public long MinBandwidth { get; set; } = 131072;
    public long MaxBandwidth { get; set; } = 10000000;
    public double MinErrorToSend { get; set; } = 0.001;
    public double MinErrorToSendNear { get; set; } = 0.01;
    public bool Loopback { get; set; }
    /// <summary>Writes <c>upnp = 1;</c> to server.cfg so the server maps its ports via a UPnP/IGD router.</summary>
    public bool Upnp { get; set; }
    public int DisconnectTimeout { get; set; } = 5;
    public int MaxDisconnectTimeout { get; set; } = 90;
    public int MaxCustomFileSize { get; set; }        // server.cfg; 0 = no limit (line omitted)
    public int MaxPacketSize { get; set; } = 1400;    // basic.cfg class sockets maxPacketSize

    // server.cfg — Security
    public bool KickDuplicates { get; set; } = true;
    public int VerifySignatures { get; set; } = 2;
    public bool RequiredSecureId { get; set; } = true;
    public int RequiredBuild { get; set; }   // 0 = any build; otherwise clients must match this game build

    // server.cfg — Mission
    public string MissionName { get; set; } = string.Empty;
    /// <summary>Ordered ';'-separated mission templates (FullPboName) forming the server rotation
    /// (server.cfg class Missions). Empty = fall back to the single <see cref="MissionName"/>.</summary>
    public string MissionQueue { get; set; } = string.Empty;
    public string MissionDifficulty { get; set; } = "Regular";
    public bool AutoInit { get; set; }
    public bool Persistent { get; set; }
    public bool AutoSelectMission { get; set; }   // autoSelectMission = 1
    public bool RandomMissionOrder { get; set; }  // randomMissionOrder = 1
    public bool SkipLobby { get; set; }           // skipLobby = 1 (inside the mission class)

    // Arma3Profile — granular difficulty, AI level and server view distance (Phase 3f).
    // Tri-state ints map to class DifficultyPresets/CustomDifficulty/Options (0/1/2); on-off bools to
    // the 0/1 options; AI to CustomAILevel (aiLevelPreset is forced to "AILevelCustom" so they apply).
    public int DiffGroupIndicators { get; set; } = 1;   // 0=never, 1=limited, 2=always
    public int DiffFriendlyTags { get; set; } = 1;
    public int DiffEnemyTags { get; set; }
    public int DiffDetectedMines { get; set; } = 1;
    public int DiffCommands { get; set; } = 1;
    public int DiffWaypoints { get; set; } = 1;
    public int DiffWeaponInfo { get; set; } = 2;
    public int DiffStanceIndicator { get; set; } = 2;
    public int DiffThirdPersonView { get; set; } = 1;   // 0=disabled, 1=enabled, 2=vehicles only
    public bool DiffReducedDamage { get; set; }
    public bool DiffStaminaBar { get; set; }
    public bool DiffWeaponCrosshair { get; set; } = true;
    public bool DiffVisionAid { get; set; }
    public bool DiffCameraShake { get; set; } = true;
    public bool DiffScoreTable { get; set; } = true;
    public bool DiffDeathMessages { get; set; } = true;
    public bool DiffVonID { get; set; } = true;
    public bool DiffMapContentFriendly { get; set; } = true;
    public bool DiffMapContentEnemy { get; set; }
    public bool DiffMapContentMines { get; set; }
    public bool DiffAutoReport { get; set; } = true;
    public bool DiffMultipleSaves { get; set; }
    public bool DiffTacticalPing { get; set; } = true;
    public double SkillAI { get; set; } = 0.6;          // CustomAILevel skillAI (0.0 - 1.0)
    public double PrecisionAI { get; set; } = 0.5;      // CustomAILevel precisionAI (0.0 - 1.0)
    public int ViewDistance { get; set; }               // 0 = omit (engine/mission default)
    public int ObjectViewDistance { get; set; }         // 0 = omit
    public double TerrainGrid { get; set; }             // 0 = omit

    // server.cfg — Voice & Voting
    public bool DisableVoN { get; set; }
    public int VonCodecQuality { get; set; } = 30;
    public bool VonCodecLegacy { get; set; }   // false = vonCodec 1 (default), true = vonCodec 0 (legacy)
    public bool VotingEnabled { get; set; } = true;
    public float VoteMissionPlayers { get; set; } = 1;
    public float VoteThreshold { get; set; } = 0.33f;

    // server.cfg — Advanced
    public bool EnableDebugConsole { get; set; }

    // server.cfg — Scripting callbacks (advanced; each blank string is omitted from server.cfg)
    public string ServerCommandPassword { get; set; } = string.Empty;
    public string OnUserConnected { get; set; } = string.Empty;
    public string OnUserDisconnected { get; set; } = string.Empty;
    public string OnHackedData { get; set; } = string.Empty;
    public string OnDifferentData { get; set; } = string.Empty;
    public string OnUnsignedData { get; set; } = string.Empty;
    public string OnUserKicked { get; set; } = string.Empty;

    // RCON
    public string RconPassword { get; set; } = string.Empty;
    public int RconPort { get; set; } = AppConstants.DefaultRconPort;

    // Headless Clients (scalar)
    public bool UseHeadlessClients { get; set; }
    public int HeadlessClientCount { get; set; } = 1;
    public string HeadlessClientExecutablePath { get; set; } = string.Empty;
    public bool HeadlessAutoRestart { get; set; } = true;

    // Crash behavior
    public bool AutoRestartOnCrash { get; set; } = true;
    public int AutoRestartDelaySeconds { get; set; } = 10;
    public int MaxCrashesBeforeGiveUp { get; set; } = 5;  // 0 = unlimited

    // Custom
    public string CustomLaunchParameters { get; set; } = string.Empty;

    // Creator / platform DLC — appended to the client-facing -mod= list by their folder names so
    // connecting players who own the DLC get full content (the server itself needn't own them).
    public bool DlcContact { get; set; }
    public bool DlcGlobalMobilization { get; set; }
    public bool DlcPrairieFire { get; set; }
    public bool DlcCsla { get; set; }
    public bool DlcWesternSahara { get; set; }
    public bool DlcSpearhead1944 { get; set; }
    public bool DlcReactionForces { get; set; }
    public bool DlcExpeditionaryForces { get; set; }

    // Mod preset link (null = manual mod list)
    public int? ActiveModPresetId { get; set; }

    // ----- Collections (populated from child tables; never serialized to a column) -----
    public List<string> MotdLines { get; set; } = [];
    public List<string> AllowedLoadFileExtensions { get; set; } = DefaultExtensions();
    public List<string> AllowedPreprocessFileExtensions { get; set; } = DefaultExtensions();
    public List<string> AllowedHTMLLoadExtensions { get; set; } = [];
    public List<string> AllowedHTMLLoadURIs { get; set; } = [];
    public List<string> HeadlessClientIPs { get; set; } = ["127.0.0.1"];
    public List<ArmaModEntry> Mods { get; set; } = [];

    public static List<string> DefaultExtensions() =>
    [
        "hpp", "sqs", "sqf", "fsm", "cpp", "paa", "txt", "xml", "inc",
        "ext", "sqm", "ods", "fxy", "lip", "csv", "kb", "bik", "bikb", "html", "htm", "biedi"
    ];
}
