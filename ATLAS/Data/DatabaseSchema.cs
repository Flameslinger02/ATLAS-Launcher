namespace Atlas.Data;

/// <summary>
/// Canonical SQLite schema (3NF) and the current schema version. <see cref="AtlasDatabase"/> reads
/// <c>PRAGMA user_version</c> and applies migrations up to <see cref="SchemaVersion"/>.
/// </summary>
public static class DatabaseSchema
{
    /// <summary>Increment on every schema change and add a migration step in <see cref="AtlasDatabase"/>.</summary>
    public const int SchemaVersion = 9;

    /// <summary>
    /// Full DDL for schema version 1. All statements use <c>IF NOT EXISTS</c> so this is idempotent.
    /// PRAGMAs (journal_mode, foreign_keys) are applied per-connection in code, not here.
    /// </summary>
    public const string CreateAllTables = @"
-- ============================================================
-- MODS (master registry — one row per unique mod ATLAS knows about)
-- ============================================================
CREATE TABLE IF NOT EXISTS Mods (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    WorkshopId      INTEGER NOT NULL DEFAULT 0,
    Name            TEXT    NOT NULL,
    FolderName      TEXT    NOT NULL,
    IsLocal         INTEGER NOT NULL DEFAULT 0,
    LocalPath       TEXT    NOT NULL DEFAULT '',
    Version         TEXT    NOT NULL DEFAULT '',
    SteamFileSize   INTEGER NOT NULL DEFAULT 0,
    LastUpdated     TEXT    NOT NULL DEFAULT '',
    LastChecked     TEXT    NOT NULL DEFAULT '',
    UpdateAvailable INTEGER NOT NULL DEFAULT 0,
    UNIQUE(WorkshopId, LocalPath)
);

-- ============================================================
-- MOD PRESETS (named reusable mod collections)
-- ============================================================
CREATE TABLE IF NOT EXISTS ModPresets (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT    NOT NULL UNIQUE,
    Description TEXT    NOT NULL DEFAULT '',
    CreatedAt   TEXT    NOT NULL,
    UpdatedAt   TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS PresetMods (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    PresetId           INTEGER NOT NULL REFERENCES ModPresets(Id) ON DELETE CASCADE,
    ModId              INTEGER NOT NULL REFERENCES Mods(Id) ON DELETE CASCADE,
    LoadOrder          INTEGER NOT NULL DEFAULT 0,
    EnabledForServer   INTEGER NOT NULL DEFAULT 1,
    EnabledForClient   INTEGER NOT NULL DEFAULT 1,
    EnabledForHeadless INTEGER NOT NULL DEFAULT 1,
    IsOptional         INTEGER NOT NULL DEFAULT 0,
    IsServerOnly       INTEGER NOT NULL DEFAULT 0,
    UNIQUE(PresetId, ModId)
);

-- ============================================================
-- SERVER PROFILES
-- ============================================================
CREATE TABLE IF NOT EXISTS ServerProfiles (
    Id               INTEGER PRIMARY KEY AUTOINCREMENT,
    Name             TEXT    NOT NULL UNIQUE,
    GameType         TEXT    NOT NULL DEFAULT 'Arma3',
    IsDefault        INTEGER NOT NULL DEFAULT 0,
    CreatedAt        TEXT    NOT NULL,
    UpdatedAt        TEXT    NOT NULL,
    Notes            TEXT    NOT NULL DEFAULT '',

    ServerExePath       TEXT    NOT NULL DEFAULT '',
    UseProfilingBranch  INTEGER NOT NULL DEFAULT 0,
    ServerDirectory     TEXT    NOT NULL DEFAULT '',

    ArmaProfileName     TEXT    NOT NULL DEFAULT 'ATLAS',
    Port                INTEGER NOT NULL DEFAULT 2302,
    EnableBattlEye      INTEGER NOT NULL DEFAULT 1,
    FilePatching        INTEGER NOT NULL DEFAULT 0,
    NoSound             INTEGER NOT NULL DEFAULT 1,
    NoSplash            INTEGER NOT NULL DEFAULT 1,
    SkipIntro           INTEGER NOT NULL DEFAULT 1,
    WorldEmpty          INTEGER NOT NULL DEFAULT 1,
    NetLog              INTEGER NOT NULL DEFAULT 0,
    LoadMissionToMemory INTEGER NOT NULL DEFAULT 0,
    NoPause             INTEGER NOT NULL DEFAULT 1,
    NoLogs              INTEGER NOT NULL DEFAULT 0,
    RankingEnabled      INTEGER NOT NULL DEFAULT 0,
    RankingFile         TEXT    NOT NULL DEFAULT '',

    EnableHT        INTEGER NOT NULL DEFAULT 0,
    HugePages       INTEGER NOT NULL DEFAULT 0,
    CpuCount        INTEGER NOT NULL DEFAULT 0,
    ExThreads       INTEGER NOT NULL DEFAULT 7,
    MaxMem          INTEGER NOT NULL DEFAULT 0,
    Malloc          TEXT    NOT NULL DEFAULT '',
    BandwidthAlg    INTEGER NOT NULL DEFAULT 2,
    LimitFPS        INTEGER NOT NULL DEFAULT 0,

    ServerName      TEXT    NOT NULL DEFAULT 'ATLAS Server',
    ServerPassword  TEXT    NOT NULL DEFAULT '',
    AdminPassword   TEXT    NOT NULL DEFAULT '',
    MaxPlayers      INTEGER NOT NULL DEFAULT 32,
    MotdInterval    INTEGER NOT NULL DEFAULT 5,
    LogFile         TEXT    NOT NULL DEFAULT 'server_console.log',
    TimeStampFormat INTEGER NOT NULL DEFAULT 0,
    DrawingInMap    INTEGER NOT NULL DEFAULT 1,

    MaxPing             INTEGER NOT NULL DEFAULT 300,
    MaxDesync           INTEGER NOT NULL DEFAULT 150,
    MaxPacketLoss       INTEGER NOT NULL DEFAULT 50,
    MaxMsgSend          INTEGER NOT NULL DEFAULT 128,
    MaxSizeNonguaranteed INTEGER NOT NULL DEFAULT 512,
    MaxSizeGuaranteed   INTEGER NOT NULL DEFAULT 512,
    MinBandwidth        INTEGER NOT NULL DEFAULT 131072,
    MaxBandwidth        INTEGER NOT NULL DEFAULT 10000000,
    MinErrorToSend      REAL    NOT NULL DEFAULT 0.001,
    MinErrorToSendNear  REAL    NOT NULL DEFAULT 0.01,
    Loopback            INTEGER NOT NULL DEFAULT 0,
    DisconnectTimeout   INTEGER NOT NULL DEFAULT 5,
    MaxDisconnectTimeout INTEGER NOT NULL DEFAULT 90,

    KickDuplicates      INTEGER NOT NULL DEFAULT 1,
    VerifySignatures    INTEGER NOT NULL DEFAULT 2,
    RequiredSecureId    INTEGER NOT NULL DEFAULT 1,

    MissionName         TEXT    NOT NULL DEFAULT '',
    MissionQueue        TEXT    NOT NULL DEFAULT '',
    MissionDifficulty   TEXT    NOT NULL DEFAULT 'Regular',
    AutoInit            INTEGER NOT NULL DEFAULT 0,
    Persistent          INTEGER NOT NULL DEFAULT 0,

    DisableVoN          INTEGER NOT NULL DEFAULT 0,
    VonCodecQuality     INTEGER NOT NULL DEFAULT 30,
    VotingEnabled       INTEGER NOT NULL DEFAULT 1,
    VoteMissionPlayers  REAL    NOT NULL DEFAULT 1.0,
    VoteThreshold       REAL    NOT NULL DEFAULT 0.33,

    EnableDebugConsole  INTEGER NOT NULL DEFAULT 0,

    RconPassword        TEXT    NOT NULL DEFAULT '',
    RconPort            INTEGER NOT NULL DEFAULT 2301,

    UseHeadlessClients      INTEGER NOT NULL DEFAULT 0,
    HeadlessClientCount     INTEGER NOT NULL DEFAULT 1,
    HeadlessClientExePath   TEXT    NOT NULL DEFAULT '',
    HeadlessAutoRestart     INTEGER NOT NULL DEFAULT 1,

    ActiveModPresetId   INTEGER REFERENCES ModPresets(Id) ON DELETE SET NULL,

    AutoRestartOnCrash      INTEGER NOT NULL DEFAULT 1,
    AutoRestartDelaySecs    INTEGER NOT NULL DEFAULT 10,
    MaxCrashesBeforeGiveUp  INTEGER NOT NULL DEFAULT 5,

    CustomLaunchParameters  TEXT    NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS ProfileMotdLines (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId INTEGER NOT NULL REFERENCES ServerProfiles(Id) ON DELETE CASCADE,
    LineOrder INTEGER NOT NULL DEFAULT 0,
    LineText  TEXT    NOT NULL,
    UNIQUE(ProfileId, LineOrder)
);

CREATE TABLE IF NOT EXISTS ProfileAllowedExtensions (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId     INTEGER NOT NULL REFERENCES ServerProfiles(Id) ON DELETE CASCADE,
    ExtType       TEXT    NOT NULL,
    Extension     TEXT    NOT NULL,
    UNIQUE(ProfileId, ExtType, Extension)
);

CREATE TABLE IF NOT EXISTS ProfileAllowedHTMLURIs (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId INTEGER NOT NULL REFERENCES ServerProfiles(Id) ON DELETE CASCADE,
    Uri       TEXT    NOT NULL,
    UNIQUE(ProfileId, Uri)
);

CREATE TABLE IF NOT EXISTS ProfileHeadlessClientIPs (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId INTEGER NOT NULL REFERENCES ServerProfiles(Id) ON DELETE CASCADE,
    IpAddress TEXT    NOT NULL,
    UNIQUE(ProfileId, IpAddress)
);

CREATE TABLE IF NOT EXISTS ProfileMods (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId          INTEGER NOT NULL REFERENCES ServerProfiles(Id) ON DELETE CASCADE,
    ModId              INTEGER NOT NULL REFERENCES Mods(Id) ON DELETE CASCADE,
    LoadOrder          INTEGER NOT NULL DEFAULT 0,
    EnabledForServer   INTEGER NOT NULL DEFAULT 1,
    EnabledForClient   INTEGER NOT NULL DEFAULT 1,
    EnabledForHeadless INTEGER NOT NULL DEFAULT 1,
    IsOptional         INTEGER NOT NULL DEFAULT 0,
    IsServerOnly       INTEGER NOT NULL DEFAULT 0,
    IsHeadlessOnly     INTEGER NOT NULL DEFAULT 0,
    UNIQUE(ProfileId, ModId)
);

-- ============================================================
-- SCHEDULED TASKS (PayloadJson is intentional EAV by TaskType)
-- ============================================================
CREATE TABLE IF NOT EXISTS ScheduledTasks (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId      INTEGER NOT NULL REFERENCES ServerProfiles(Id) ON DELETE CASCADE,
    Name           TEXT    NOT NULL,
    TaskType       TEXT    NOT NULL,
    CronExpression TEXT    NOT NULL DEFAULT '',
    NextRunAt      TEXT,
    LastRunAt      TEXT,
    LastRunResult  TEXT    NOT NULL DEFAULT '',
    IsEnabled      INTEGER NOT NULL DEFAULT 1,
    PayloadJson    TEXT    NOT NULL DEFAULT '{}'
);

-- ============================================================
-- AUDIT / HISTORY
-- ============================================================
CREATE TABLE IF NOT EXISTS BanHistory (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId  INTEGER NOT NULL REFERENCES ServerProfiles(Id) ON DELETE CASCADE,
    PlayerGuid TEXT    NOT NULL,
    PlayerName TEXT    NOT NULL DEFAULT '',
    Reason     TEXT    NOT NULL DEFAULT '',
    Duration   INTEGER NOT NULL DEFAULT 0,
    BannedAt   TEXT    NOT NULL,
    BannedBy   TEXT    NOT NULL DEFAULT 'ATLAS'
);

CREATE TABLE IF NOT EXISTS PlayerHistory (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId  INTEGER NOT NULL REFERENCES ServerProfiles(Id) ON DELETE CASCADE,
    PlayerGuid TEXT    NOT NULL,
    PlayerName TEXT    NOT NULL,
    EventType  TEXT    NOT NULL,
    EventAt    TEXT    NOT NULL,
    SessionIp  TEXT    NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS DiscordCommandLog (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId       INTEGER REFERENCES ServerProfiles(Id) ON DELETE SET NULL,
    DiscordUserId   TEXT    NOT NULL,
    DiscordUserName TEXT    NOT NULL,
    Command         TEXT    NOT NULL,
    Arguments       TEXT    NOT NULL DEFAULT '',
    Result          TEXT    NOT NULL DEFAULT '',
    ExecutedAt      TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS CrashLog (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileId     INTEGER REFERENCES ServerProfiles(Id) ON DELETE SET NULL,
    CrashedAt     TEXT    NOT NULL,
    ExitCode      INTEGER NOT NULL DEFAULT -1,
    AutoRestarted INTEGER NOT NULL DEFAULT 0,
    Notes         TEXT    NOT NULL DEFAULT ''
);

-- ServerBrowserFavorites retained for schema completeness (Server Browser feature disabled, Q6=No).
CREATE TABLE IF NOT EXISTS ServerBrowserFavorites (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Name      TEXT    NOT NULL DEFAULT '',
    IpAddress TEXT    NOT NULL,
    Port      INTEGER NOT NULL,
    AddedAt   TEXT    NOT NULL,
    UNIQUE(IpAddress, Port)
);

-- ============================================================
-- INDEXES
-- ============================================================
CREATE INDEX IF NOT EXISTS idx_profile_mods_profile    ON ProfileMods(ProfileId);
CREATE INDEX IF NOT EXISTS idx_profile_mods_mod        ON ProfileMods(ModId);
CREATE INDEX IF NOT EXISTS idx_preset_mods_preset      ON PresetMods(PresetId);
CREATE INDEX IF NOT EXISTS idx_scheduled_tasks_profile ON ScheduledTasks(ProfileId, IsEnabled, NextRunAt);
CREATE INDEX IF NOT EXISTS idx_player_history_guid     ON PlayerHistory(PlayerGuid);
CREATE INDEX IF NOT EXISTS idx_player_history_profile  ON PlayerHistory(ProfileId, EventAt);
CREATE INDEX IF NOT EXISTS idx_ban_history_guid        ON BanHistory(PlayerGuid);
CREATE INDEX IF NOT EXISTS idx_mods_workshop_id        ON Mods(WorkshopId);
";
}
