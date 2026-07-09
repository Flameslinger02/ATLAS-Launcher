using System.Globalization;
using System.Text.Json;
using Atlas.Core.Models;
using Atlas.Data;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IProfileService"/>
public sealed class ProfileService : IProfileService
{
    private readonly AtlasDatabase _db;
    private ServerProfile? _activeProfile;

    private static readonly JsonSerializerOptions ExportJson = new() { WriteIndented = true };

    public ProfileService(AtlasDatabase db) => _db = db;

    public ServerProfile? ActiveProfile => _activeProfile;
    public event EventHandler<ServerProfile>? ActiveProfileChanged;
    public event EventHandler? ProfilesChanged;

    private void RaiseProfilesChanged() => ProfilesChanged?.Invoke(this, EventArgs.Empty);

    public void SetActiveProfile(ServerProfile profile)
    {
        _activeProfile = profile;
        ActiveProfileChanged?.Invoke(this, profile);
        Log.Information("Active profile set to '{Name}' (Id {Id}).", profile.Name, profile.Id);
    }

    // ------------------------------------------------------------------ reads

    public async Task<List<ServerProfile>> GetAllProfilesAsync()
    {
        var result = new List<ServerProfile>();
        await using var conn = _db.CreateOpenConnection();

        var ids = new List<int>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM ServerProfiles ORDER BY Name COLLATE NOCASE;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
        }

        foreach (var id in ids)
        {
            var profile = await LoadFullAsync(conn, id);
            if (profile is not null) result.Add(profile);
        }
        return result;
    }

    public async Task<ServerProfile?> GetProfileByIdAsync(int id)
    {
        await using var conn = _db.CreateOpenConnection();
        return await LoadFullAsync(conn, id);
    }

    public async Task<ServerProfile?> GetDefaultProfileAsync()
    {
        await using var conn = _db.CreateOpenConnection();
        int? id = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM ServerProfiles WHERE IsDefault = 1 LIMIT 1;";
            var scalar = await cmd.ExecuteScalarAsync();
            if (scalar is not null && scalar != DBNull.Value) id = Convert.ToInt32(scalar);
        }
        return id is null ? null : await LoadFullAsync(conn, id.Value);
    }

    private static async Task<ServerProfile?> LoadFullAsync(SqliteConnection conn, int id)
    {
        ServerProfile? profile = null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM ServerProfiles WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync()) profile = MapScalars(reader);
        }
        if (profile is null) return null;

        // MOTD lines (ordered)
        profile.MotdLines = await ReadStringsAsync(conn,
            "SELECT LineText FROM ProfileMotdLines WHERE ProfileId = @id ORDER BY LineOrder;", id);

        // Allowed extensions by type
        profile.AllowedLoadFileExtensions.Clear();
        profile.AllowedPreprocessFileExtensions.Clear();
        profile.AllowedHTMLLoadExtensions.Clear();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ExtType, Extension FROM ProfileAllowedExtensions WHERE ProfileId = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var type = reader.GetString(0);
                var ext = reader.GetString(1);
                switch (type)
                {
                    case "Load": profile.AllowedLoadFileExtensions.Add(ext); break;
                    case "Preprocess": profile.AllowedPreprocessFileExtensions.Add(ext); break;
                    case "HTMLLoad": profile.AllowedHTMLLoadExtensions.Add(ext); break;
                }
            }
        }

        profile.AllowedHTMLLoadURIs = await ReadStringsAsync(conn,
            "SELECT Uri FROM ProfileAllowedHTMLURIs WHERE ProfileId = @id;", id);

        profile.HeadlessClientIPs = await ReadStringsAsync(conn,
            "SELECT IpAddress FROM ProfileHeadlessClientIPs WHERE ProfileId = @id;", id);

        // Mods (ProfileMods JOIN Mods)
        profile.Mods = new List<ArmaModEntry>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT m.Id, m.WorkshopId, m.Name, m.FolderName, m.IsLocal, m.LocalPath, m.Version,
       m.SteamFileSize, m.LastUpdated, m.LastChecked, m.UpdateAvailable,
       pm.LoadOrder, pm.EnabledForServer, pm.EnabledForClient, pm.EnabledForHeadless,
       pm.IsOptional, pm.IsServerOnly, pm.IsHeadlessOnly
FROM ProfileMods pm JOIN Mods m ON m.Id = pm.ModId
WHERE pm.ProfileId = @id ORDER BY pm.LoadOrder;";
            cmd.Parameters.AddWithValue("@id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                profile.Mods.Add(new ArmaModEntry
                {
                    ModId = reader.GetInt32(0),
                    WorkshopId = (ulong)reader.GetInt64(1),
                    Name = reader.GetString(2),
                    FolderName = reader.GetString(3),
                    IsLocal = reader.GetInt64(4) != 0,
                    LocalPath = reader.GetString(5),
                    Version = reader.GetString(6),
                    SteamFileSize = (ulong)reader.GetInt64(7),
                    LastUpdated = ParseDate(reader.GetString(8)),
                    LastChecked = ParseDate(reader.GetString(9)),
                    UpdateAvailable = reader.GetInt64(10) != 0,
                    LoadOrder = reader.GetInt32(11),
                    EnabledForServer = reader.GetInt64(12) != 0,
                    EnabledForClient = reader.GetInt64(13) != 0,
                    EnabledForHeadless = reader.GetInt64(14) != 0,
                    IsOptional = reader.GetInt64(15) != 0,
                    IsServerOnly = reader.GetInt64(16) != 0,
                    IsHeadlessOnly = reader.GetInt64(17) != 0,
                });
            }
        }

        return profile;
    }

    // ------------------------------------------------------------------ writes

    public async Task<ServerProfile> CreateProfileAsync(ServerProfile profile)
    {
        profile.CreatedAt = DateTime.UtcNow;
        profile.UpdatedAt = profile.CreatedAt;

        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var tx = conn.BeginTransaction();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                var cols = ScalarColumns;
                cmd.CommandText =
                    $"INSERT INTO ServerProfiles ({string.Join(", ", cols)}) " +
                    $"VALUES ({string.Join(", ", cols.Select(c => "@" + c))});";
                AddScalarParameters(cmd, profile);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var idCmd = conn.CreateCommand())
            {
                idCmd.Transaction = tx;
                idCmd.CommandText = "SELECT last_insert_rowid();";
                profile.Id = Convert.ToInt32(await idCmd.ExecuteScalarAsync());
            }

            await SaveChildrenAsync(conn, tx, profile);
            await tx.CommitAsync();
            Log.Information("Created profile '{Name}' (Id {Id}).", profile.Name, profile.Id);
            RaiseProfilesChanged();
            return profile;
        }
        finally
        {
            _db.WriteLock.Release();
        }
    }

    public async Task UpdateProfileAsync(ServerProfile profile)
    {
        profile.UpdatedAt = DateTime.UtcNow;

        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var tx = conn.BeginTransaction();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    $"UPDATE ServerProfiles SET {string.Join(", ", ScalarColumns.Select(c => $"{c} = @{c}"))} " +
                    "WHERE Id = @Id;";
                AddScalarParameters(cmd, profile);
                cmd.Parameters.AddWithValue("@Id", profile.Id);
                await cmd.ExecuteNonQueryAsync();
            }

            await SaveChildrenAsync(conn, tx, profile);
            await tx.CommitAsync();
            Log.Information("Updated profile '{Name}' (Id {Id}).", profile.Name, profile.Id);
        }
        finally
        {
            _db.WriteLock.Release();
        }

        if (_activeProfile?.Id == profile.Id) SetActiveProfile(profile);
        RaiseProfilesChanged();
    }

    public async Task DeleteProfileAsync(int id)
    {
        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ServerProfiles WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
            Log.Information("Deleted profile Id {Id}.", id);
        }
        finally
        {
            _db.WriteLock.Release();
        }
        if (_activeProfile?.Id == id) _activeProfile = null;
        RaiseProfilesChanged();
    }

    public async Task<ServerProfile> CloneProfileAsync(int id, string newName)
    {
        var source = await GetProfileByIdAsync(id)
            ?? throw new InvalidOperationException($"Profile {id} not found.");
        source.Id = 0;
        source.Name = await EnsureUniqueNameAsync(newName);
        source.IsDefault = false;
        foreach (var mod in source.Mods) mod.ModId = 0; // re-upserted on save
        return await CreateProfileAsync(source);
    }

    public async Task SetDefaultProfileAsync(int id)
    {
        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var tx = conn.BeginTransaction();
            await using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "UPDATE ServerProfiles SET IsDefault = 0 WHERE IsDefault = 1;";
                await clear.ExecuteNonQueryAsync();
            }
            await using (var set = conn.CreateCommand())
            {
                set.Transaction = tx;
                set.CommandText = "UPDATE ServerProfiles SET IsDefault = 1 WHERE Id = @id;";
                set.Parameters.AddWithValue("@id", id);
                await set.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            Log.Information("Set default profile to Id {Id}.", id);
        }
        finally
        {
            _db.WriteLock.Release();
        }
        RaiseProfilesChanged();
    }

    // ------------------------------------------------------------------ import / export

    public async Task ExportProfileAsync(int id, string outputPath)
    {
        var profile = await GetProfileByIdAsync(id)
            ?? throw new InvalidOperationException($"Profile {id} not found.");
        await using var fs = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(fs, profile, ExportJson);
        Log.Information("Exported profile '{Name}' to {Path}.", profile.Name, outputPath);
    }

    public async Task<ServerProfile> ImportProfileAsync(string filePath)
    {
        ServerProfile? imported;
        await using (var fs = File.OpenRead(filePath))
        {
            imported = await JsonSerializer.DeserializeAsync<ServerProfile>(fs);
        }
        if (imported is null) throw new InvalidOperationException("Profile file could not be parsed.");

        imported.Id = 0;
        imported.IsDefault = false;
        imported.Name = await EnsureUniqueNameAsync(string.IsNullOrWhiteSpace(imported.Name) ? "Imported Profile" : imported.Name);
        foreach (var mod in imported.Mods) mod.ModId = 0;
        return await CreateProfileAsync(imported);
    }

    // ------------------------------------------------------------------ child-table persistence

    private static async Task SaveChildrenAsync(SqliteConnection conn, SqliteTransaction tx, ServerProfile p)
    {
        await DeleteChildrenAsync(conn, tx, p.Id);

        // MOTD
        for (var i = 0; i < p.MotdLines.Count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO ProfileMotdLines (ProfileId, LineOrder, LineText) VALUES (@p, @o, @t);";
            cmd.Parameters.AddWithValue("@p", p.Id);
            cmd.Parameters.AddWithValue("@o", i);
            cmd.Parameters.AddWithValue("@t", p.MotdLines[i]);
            await cmd.ExecuteNonQueryAsync();
        }

        // Allowed extensions
        await InsertExtensionsAsync(conn, tx, p.Id, "Load", p.AllowedLoadFileExtensions);
        await InsertExtensionsAsync(conn, tx, p.Id, "Preprocess", p.AllowedPreprocessFileExtensions);
        await InsertExtensionsAsync(conn, tx, p.Id, "HTMLLoad", p.AllowedHTMLLoadExtensions);

        // HTML URIs
        foreach (var uri in p.AllowedHTMLLoadURIs.Distinct())
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO ProfileAllowedHTMLURIs (ProfileId, Uri) VALUES (@p, @u);";
            cmd.Parameters.AddWithValue("@p", p.Id);
            cmd.Parameters.AddWithValue("@u", uri);
            await cmd.ExecuteNonQueryAsync();
        }

        // Headless client IPs
        foreach (var ip in p.HeadlessClientIPs.Distinct())
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO ProfileHeadlessClientIPs (ProfileId, IpAddress) VALUES (@p, @ip);";
            cmd.Parameters.AddWithValue("@p", p.Id);
            cmd.Parameters.AddWithValue("@ip", ip);
            await cmd.ExecuteNonQueryAsync();
        }

        // Mods: upsert into Mods, then ProfileMods
        var order = 0;
        foreach (var mod in p.Mods)
        {
            var modId = await UpsertModAsync(conn, tx, mod);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO ProfileMods (ProfileId, ModId, LoadOrder, EnabledForServer, EnabledForClient,
                         EnabledForHeadless, IsOptional, IsServerOnly, IsHeadlessOnly)
VALUES (@pid, @mid, @lo, @es, @ec, @eh, @io, @iso, @iho);";
            cmd.Parameters.AddWithValue("@pid", p.Id);
            cmd.Parameters.AddWithValue("@mid", modId);
            cmd.Parameters.AddWithValue("@lo", mod.LoadOrder != 0 ? mod.LoadOrder : order);
            cmd.Parameters.AddWithValue("@es", mod.EnabledForServer);
            cmd.Parameters.AddWithValue("@ec", mod.EnabledForClient);
            cmd.Parameters.AddWithValue("@eh", mod.EnabledForHeadless);
            cmd.Parameters.AddWithValue("@io", mod.IsOptional);
            cmd.Parameters.AddWithValue("@iso", mod.IsServerOnly);
            cmd.Parameters.AddWithValue("@iho", mod.IsHeadlessOnly);
            await cmd.ExecuteNonQueryAsync();
            order++;
        }
    }

    private static async Task DeleteChildrenAsync(SqliteConnection conn, SqliteTransaction tx, int profileId)
    {
        string[] tables =
        {
            "ProfileMotdLines", "ProfileAllowedExtensions", "ProfileAllowedHTMLURIs",
            "ProfileHeadlessClientIPs", "ProfileMods"
        };
        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE ProfileId = @p;";
            cmd.Parameters.AddWithValue("@p", profileId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertExtensionsAsync(SqliteConnection conn, SqliteTransaction tx,
        int profileId, string extType, IEnumerable<string> extensions)
    {
        foreach (var ext in extensions.Distinct())
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO ProfileAllowedExtensions (ProfileId, ExtType, Extension) VALUES (@p, @t, @e);";
            cmd.Parameters.AddWithValue("@p", profileId);
            cmd.Parameters.AddWithValue("@t", extType);
            cmd.Parameters.AddWithValue("@e", ext);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> UpsertModAsync(SqliteConnection conn, SqliteTransaction tx, ArmaModEntry mod)
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO Mods (WorkshopId, Name, FolderName, IsLocal, LocalPath, Version, SteamFileSize,
                  LastUpdated, LastChecked, UpdateAvailable)
VALUES (@w, @n, @f, @il, @lp, @v, @sz, @lu, @lc, @ua)
ON CONFLICT(WorkshopId, LocalPath) DO UPDATE SET
    Name = excluded.Name, FolderName = excluded.FolderName, IsLocal = excluded.IsLocal,
    Version = excluded.Version, SteamFileSize = excluded.SteamFileSize,
    LastUpdated = excluded.LastUpdated, LastChecked = excluded.LastChecked,
    UpdateAvailable = excluded.UpdateAvailable;";
            cmd.Parameters.AddWithValue("@w", (long)mod.WorkshopId);
            cmd.Parameters.AddWithValue("@n", mod.Name);
            cmd.Parameters.AddWithValue("@f", mod.FolderName);
            cmd.Parameters.AddWithValue("@il", mod.IsLocal);
            cmd.Parameters.AddWithValue("@lp", mod.LocalPath);
            cmd.Parameters.AddWithValue("@v", mod.Version);
            cmd.Parameters.AddWithValue("@sz", (long)mod.SteamFileSize);
            cmd.Parameters.AddWithValue("@lu", mod.LastUpdated.ToString("o"));
            cmd.Parameters.AddWithValue("@lc", mod.LastChecked.ToString("o"));
            cmd.Parameters.AddWithValue("@ua", mod.UpdateAvailable);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var idCmd = conn.CreateCommand())
        {
            idCmd.Transaction = tx;
            idCmd.CommandText = "SELECT Id FROM Mods WHERE WorkshopId = @w AND LocalPath = @lp;";
            idCmd.Parameters.AddWithValue("@w", (long)mod.WorkshopId);
            idCmd.Parameters.AddWithValue("@lp", mod.LocalPath);
            return Convert.ToInt32(await idCmd.ExecuteScalarAsync());
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<string> EnsureUniqueNameAsync(string desired)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = _db.CreateOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM ServerProfiles;";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) existing.Add(reader.GetString(0));

        if (!existing.Contains(desired)) return desired;
        for (var i = 2; ; i++)
        {
            var candidate = $"{desired} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private static async Task<List<string>> ReadStringsAsync(SqliteConnection conn, string sql, int id)
    {
        var list = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(reader.GetString(0));
        return list;
    }

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d : default;

    /// <summary>All ServerProfiles columns except the autoincrement Id, in a stable order.</summary>
    private static readonly string[] ScalarColumns =
    {
        "Name", "GameType", "IsDefault", "CreatedAt", "UpdatedAt", "Notes",
        "ServerExePath", "UseProfilingBranch", "ServerDirectory", "MissionDirectory",
        "ArmaProfileName", "Port", "EnableBattlEye", "FilePatching", "ServerFilePatching", "NoSound", "NoSplash", "SkipIntro",
        "WorldEmpty", "NetLog", "LoadMissionToMemory", "NoPause", "NoLogs", "RankingEnabled", "RankingFile",
        "EnableHT", "HugePages", "CpuCount", "ExThreads", "MaxMem", "Malloc", "BandwidthAlg", "LimitFPS",
        "ServerName", "ServerPassword", "AdminPassword", "MaxPlayers", "MotdInterval", "LogFile",
        "TimeStampFormat", "DrawingInMap",
        "MaxPing", "MaxDesync", "MaxPacketLoss", "MaxMsgSend", "MaxSizeNonguaranteed", "MaxSizeGuaranteed",
        "MinBandwidth", "MaxBandwidth", "MinErrorToSend", "MinErrorToSendNear", "Loopback", "Upnp",
        "DisconnectTimeout", "MaxDisconnectTimeout",
        "KickDuplicates", "VerifySignatures", "RequiredSecureId",
        "MissionName", "MissionQueue", "MissionDifficulty", "AutoInit", "Persistent",
        "DisableVoN", "VonCodecQuality", "VotingEnabled", "VoteMissionPlayers", "VoteThreshold",
        "EnableDebugConsole",
        "RconPassword", "RconPort",
        "UseHeadlessClients", "HeadlessClientCount", "HeadlessClientExePath", "HeadlessAutoRestart",
        "ActiveModPresetId",
        "AutoRestartOnCrash", "AutoRestartDelaySecs", "MaxCrashesBeforeGiveUp",
        "CustomLaunchParameters",
        "DlcContact", "DlcGlobalMobilization", "DlcPrairieFire", "DlcCsla",
        "DlcWesternSahara", "DlcSpearhead1944", "DlcReactionForces", "DlcExpeditionaryForces",
        "RequiredBuild", "VonCodecLegacy", "ServerCommandPassword",
        "OnUserConnected", "OnUserDisconnected", "OnHackedData", "OnDifferentData",
        "OnUnsignedData", "OnUserKicked",
        "MaxCustomFileSize", "MaxPacketSize",
        "AutoSelectMission", "RandomMissionOrder", "SkipLobby",
        "DiffGroupIndicators", "DiffFriendlyTags", "DiffEnemyTags", "DiffDetectedMines", "DiffCommands",
        "DiffWaypoints", "DiffWeaponInfo", "DiffStanceIndicator", "DiffThirdPersonView", "DiffReducedDamage",
        "DiffStaminaBar", "DiffWeaponCrosshair", "DiffVisionAid", "DiffCameraShake", "DiffScoreTable",
        "DiffDeathMessages", "DiffVonID", "DiffMapContentFriendly", "DiffMapContentEnemy", "DiffMapContentMines",
        "DiffAutoReport", "DiffMultipleSaves", "DiffTacticalPing",
        "SkillAI", "PrecisionAI", "ViewDistance", "ObjectViewDistance", "TerrainGrid",
        "AdminUids", "IdleFPSLimit", "LobbyIdleTimeout", "RoleTimeOut",
        "MissionWhitelistEnabled", "MissionWhitelistExtra",
        "AntiFloodEnabled", "AntiFloodCycleTime", "AntiFloodCycleLimit", "AntiFloodCycleHardLimit", "AntiFloodKick"
    };

    private static void AddScalarParameters(SqliteCommand cmd, ServerProfile p)
    {
        cmd.Parameters.AddWithValue("@Name", p.Name);
        cmd.Parameters.AddWithValue("@GameType", p.GameType.ToString());
        cmd.Parameters.AddWithValue("@IsDefault", p.IsDefault);
        cmd.Parameters.AddWithValue("@CreatedAt", p.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@UpdatedAt", p.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@Notes", p.Notes);
        cmd.Parameters.AddWithValue("@ServerExePath", p.ServerExecutablePath);
        cmd.Parameters.AddWithValue("@UseProfilingBranch", p.UseProfilingBranch);
        cmd.Parameters.AddWithValue("@ServerDirectory", p.ServerDirectory);
        cmd.Parameters.AddWithValue("@MissionDirectory", p.MissionDirectory);
        cmd.Parameters.AddWithValue("@ArmaProfileName", p.ArmaProfileName);
        cmd.Parameters.AddWithValue("@Port", p.Port);
        cmd.Parameters.AddWithValue("@EnableBattlEye", p.EnableBattlEye);
        cmd.Parameters.AddWithValue("@FilePatching", p.FilePatching);
        cmd.Parameters.AddWithValue("@ServerFilePatching", p.ServerFilePatching);
        cmd.Parameters.AddWithValue("@NoSound", p.NoSound);
        cmd.Parameters.AddWithValue("@NoSplash", p.NoSplash);
        cmd.Parameters.AddWithValue("@SkipIntro", p.SkipIntro);
        cmd.Parameters.AddWithValue("@WorldEmpty", p.WorldEmpty);
        cmd.Parameters.AddWithValue("@NetLog", p.NetLog);
        cmd.Parameters.AddWithValue("@LoadMissionToMemory", p.LoadMissionToMemory);
        cmd.Parameters.AddWithValue("@NoPause", p.NoPause);
        cmd.Parameters.AddWithValue("@NoLogs", p.NoLogs);
        cmd.Parameters.AddWithValue("@RankingEnabled", p.RankingEnabled);
        cmd.Parameters.AddWithValue("@RankingFile", p.RankingFile);
        cmd.Parameters.AddWithValue("@EnableHT", p.EnableHT);
        cmd.Parameters.AddWithValue("@HugePages", p.HugePages);
        cmd.Parameters.AddWithValue("@CpuCount", p.CpuCount);
        cmd.Parameters.AddWithValue("@ExThreads", p.ExThreads);
        cmd.Parameters.AddWithValue("@MaxMem", p.MaxMem);
        cmd.Parameters.AddWithValue("@Malloc", p.Malloc);
        cmd.Parameters.AddWithValue("@BandwidthAlg", p.BandwidthAlg);
        cmd.Parameters.AddWithValue("@LimitFPS", p.LimitFPS);
        cmd.Parameters.AddWithValue("@ServerName", p.ServerName);
        cmd.Parameters.AddWithValue("@ServerPassword", p.ServerPassword);
        cmd.Parameters.AddWithValue("@AdminPassword", p.AdminPassword);
        cmd.Parameters.AddWithValue("@MaxPlayers", p.MaxPlayers);
        cmd.Parameters.AddWithValue("@MotdInterval", p.MotdInterval);
        cmd.Parameters.AddWithValue("@LogFile", p.LogFile);
        cmd.Parameters.AddWithValue("@TimeStampFormat", p.TimeStampFormat);
        cmd.Parameters.AddWithValue("@DrawingInMap", p.DrawingInMap);
        cmd.Parameters.AddWithValue("@MaxPing", p.MaxPing);
        cmd.Parameters.AddWithValue("@MaxDesync", p.MaxDesync);
        cmd.Parameters.AddWithValue("@MaxPacketLoss", p.MaxPacketLoss);
        cmd.Parameters.AddWithValue("@MaxMsgSend", p.MaxMsgSend);
        cmd.Parameters.AddWithValue("@MaxSizeNonguaranteed", p.MaxSizeNonguaranteed);
        cmd.Parameters.AddWithValue("@MaxSizeGuaranteed", p.MaxSizeGuaranteed);
        cmd.Parameters.AddWithValue("@MinBandwidth", p.MinBandwidth);
        cmd.Parameters.AddWithValue("@MaxBandwidth", p.MaxBandwidth);
        cmd.Parameters.AddWithValue("@MinErrorToSend", p.MinErrorToSend);
        cmd.Parameters.AddWithValue("@MinErrorToSendNear", p.MinErrorToSendNear);
        cmd.Parameters.AddWithValue("@Loopback", p.Loopback);
        cmd.Parameters.AddWithValue("@Upnp", p.Upnp);
        cmd.Parameters.AddWithValue("@DisconnectTimeout", p.DisconnectTimeout);
        cmd.Parameters.AddWithValue("@MaxDisconnectTimeout", p.MaxDisconnectTimeout);
        cmd.Parameters.AddWithValue("@KickDuplicates", p.KickDuplicates);
        cmd.Parameters.AddWithValue("@VerifySignatures", p.VerifySignatures);
        cmd.Parameters.AddWithValue("@RequiredSecureId", p.RequiredSecureId);
        cmd.Parameters.AddWithValue("@MissionName", p.MissionName);
        cmd.Parameters.AddWithValue("@MissionQueue", p.MissionQueue);
        cmd.Parameters.AddWithValue("@MissionDifficulty", p.MissionDifficulty);
        cmd.Parameters.AddWithValue("@AutoInit", p.AutoInit);
        cmd.Parameters.AddWithValue("@Persistent", p.Persistent);
        cmd.Parameters.AddWithValue("@DisableVoN", p.DisableVoN);
        cmd.Parameters.AddWithValue("@VonCodecQuality", p.VonCodecQuality);
        cmd.Parameters.AddWithValue("@VotingEnabled", p.VotingEnabled);
        cmd.Parameters.AddWithValue("@VoteMissionPlayers", p.VoteMissionPlayers);
        cmd.Parameters.AddWithValue("@VoteThreshold", p.VoteThreshold);
        cmd.Parameters.AddWithValue("@EnableDebugConsole", p.EnableDebugConsole);
        cmd.Parameters.AddWithValue("@RconPassword", p.RconPassword);
        cmd.Parameters.AddWithValue("@RconPort", p.RconPort);
        cmd.Parameters.AddWithValue("@UseHeadlessClients", p.UseHeadlessClients);
        cmd.Parameters.AddWithValue("@HeadlessClientCount", p.HeadlessClientCount);
        cmd.Parameters.AddWithValue("@HeadlessClientExePath", p.HeadlessClientExecutablePath);
        cmd.Parameters.AddWithValue("@HeadlessAutoRestart", p.HeadlessAutoRestart);
        cmd.Parameters.AddWithValue("@ActiveModPresetId", (object?)p.ActiveModPresetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AutoRestartOnCrash", p.AutoRestartOnCrash);
        cmd.Parameters.AddWithValue("@AutoRestartDelaySecs", p.AutoRestartDelaySeconds);
        cmd.Parameters.AddWithValue("@MaxCrashesBeforeGiveUp", p.MaxCrashesBeforeGiveUp);
        cmd.Parameters.AddWithValue("@CustomLaunchParameters", p.CustomLaunchParameters);
        cmd.Parameters.AddWithValue("@DlcContact", p.DlcContact);
        cmd.Parameters.AddWithValue("@DlcGlobalMobilization", p.DlcGlobalMobilization);
        cmd.Parameters.AddWithValue("@DlcPrairieFire", p.DlcPrairieFire);
        cmd.Parameters.AddWithValue("@DlcCsla", p.DlcCsla);
        cmd.Parameters.AddWithValue("@DlcWesternSahara", p.DlcWesternSahara);
        cmd.Parameters.AddWithValue("@DlcSpearhead1944", p.DlcSpearhead1944);
        cmd.Parameters.AddWithValue("@DlcReactionForces", p.DlcReactionForces);
        cmd.Parameters.AddWithValue("@DlcExpeditionaryForces", p.DlcExpeditionaryForces);
        cmd.Parameters.AddWithValue("@RequiredBuild", p.RequiredBuild);
        cmd.Parameters.AddWithValue("@VonCodecLegacy", p.VonCodecLegacy);
        cmd.Parameters.AddWithValue("@ServerCommandPassword", p.ServerCommandPassword);
        cmd.Parameters.AddWithValue("@OnUserConnected", p.OnUserConnected);
        cmd.Parameters.AddWithValue("@OnUserDisconnected", p.OnUserDisconnected);
        cmd.Parameters.AddWithValue("@OnHackedData", p.OnHackedData);
        cmd.Parameters.AddWithValue("@OnDifferentData", p.OnDifferentData);
        cmd.Parameters.AddWithValue("@OnUnsignedData", p.OnUnsignedData);
        cmd.Parameters.AddWithValue("@OnUserKicked", p.OnUserKicked);
        cmd.Parameters.AddWithValue("@MaxCustomFileSize", p.MaxCustomFileSize);
        cmd.Parameters.AddWithValue("@MaxPacketSize", p.MaxPacketSize);
        cmd.Parameters.AddWithValue("@AutoSelectMission", p.AutoSelectMission);
        cmd.Parameters.AddWithValue("@RandomMissionOrder", p.RandomMissionOrder);
        cmd.Parameters.AddWithValue("@SkipLobby", p.SkipLobby);
        cmd.Parameters.AddWithValue("@DiffGroupIndicators", p.DiffGroupIndicators);
        cmd.Parameters.AddWithValue("@DiffFriendlyTags", p.DiffFriendlyTags);
        cmd.Parameters.AddWithValue("@DiffEnemyTags", p.DiffEnemyTags);
        cmd.Parameters.AddWithValue("@DiffDetectedMines", p.DiffDetectedMines);
        cmd.Parameters.AddWithValue("@DiffCommands", p.DiffCommands);
        cmd.Parameters.AddWithValue("@DiffWaypoints", p.DiffWaypoints);
        cmd.Parameters.AddWithValue("@DiffWeaponInfo", p.DiffWeaponInfo);
        cmd.Parameters.AddWithValue("@DiffStanceIndicator", p.DiffStanceIndicator);
        cmd.Parameters.AddWithValue("@DiffThirdPersonView", p.DiffThirdPersonView);
        cmd.Parameters.AddWithValue("@DiffReducedDamage", p.DiffReducedDamage);
        cmd.Parameters.AddWithValue("@DiffStaminaBar", p.DiffStaminaBar);
        cmd.Parameters.AddWithValue("@DiffWeaponCrosshair", p.DiffWeaponCrosshair);
        cmd.Parameters.AddWithValue("@DiffVisionAid", p.DiffVisionAid);
        cmd.Parameters.AddWithValue("@DiffCameraShake", p.DiffCameraShake);
        cmd.Parameters.AddWithValue("@DiffScoreTable", p.DiffScoreTable);
        cmd.Parameters.AddWithValue("@DiffDeathMessages", p.DiffDeathMessages);
        cmd.Parameters.AddWithValue("@DiffVonID", p.DiffVonID);
        cmd.Parameters.AddWithValue("@DiffMapContentFriendly", p.DiffMapContentFriendly);
        cmd.Parameters.AddWithValue("@DiffMapContentEnemy", p.DiffMapContentEnemy);
        cmd.Parameters.AddWithValue("@DiffMapContentMines", p.DiffMapContentMines);
        cmd.Parameters.AddWithValue("@DiffAutoReport", p.DiffAutoReport);
        cmd.Parameters.AddWithValue("@DiffMultipleSaves", p.DiffMultipleSaves);
        cmd.Parameters.AddWithValue("@DiffTacticalPing", p.DiffTacticalPing);
        cmd.Parameters.AddWithValue("@SkillAI", p.SkillAI);
        cmd.Parameters.AddWithValue("@PrecisionAI", p.PrecisionAI);
        cmd.Parameters.AddWithValue("@ViewDistance", p.ViewDistance);
        cmd.Parameters.AddWithValue("@ObjectViewDistance", p.ObjectViewDistance);
        cmd.Parameters.AddWithValue("@TerrainGrid", p.TerrainGrid);
        cmd.Parameters.AddWithValue("@AdminUids", p.AdminUids);
        cmd.Parameters.AddWithValue("@IdleFPSLimit", p.IdleFPSLimit);
        cmd.Parameters.AddWithValue("@LobbyIdleTimeout", p.LobbyIdleTimeout);
        cmd.Parameters.AddWithValue("@RoleTimeOut", p.RoleTimeOut);
        cmd.Parameters.AddWithValue("@MissionWhitelistEnabled", p.MissionWhitelistEnabled);
        cmd.Parameters.AddWithValue("@MissionWhitelistExtra", p.MissionWhitelistExtra);
        cmd.Parameters.AddWithValue("@AntiFloodEnabled", p.AntiFloodEnabled);
        cmd.Parameters.AddWithValue("@AntiFloodCycleTime", p.AntiFloodCycleTime);
        cmd.Parameters.AddWithValue("@AntiFloodCycleLimit", p.AntiFloodCycleLimit);
        cmd.Parameters.AddWithValue("@AntiFloodCycleHardLimit", p.AntiFloodCycleHardLimit);
        cmd.Parameters.AddWithValue("@AntiFloodKick", p.AntiFloodKick);
    }

    private static ServerProfile MapScalars(SqliteDataReader r)
    {
        string S(string c) => r.IsDBNull(r.GetOrdinal(c)) ? string.Empty : r.GetString(r.GetOrdinal(c));
        int I(string c) => r.IsDBNull(r.GetOrdinal(c)) ? 0 : Convert.ToInt32(r.GetValue(r.GetOrdinal(c)));
        long L(string c) => r.IsDBNull(r.GetOrdinal(c)) ? 0 : Convert.ToInt64(r.GetValue(r.GetOrdinal(c)));
        double D(string c) => r.IsDBNull(r.GetOrdinal(c)) ? 0 : Convert.ToDouble(r.GetValue(r.GetOrdinal(c)));
        bool B(string c) => I(c) != 0;
        int? NI(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : Convert.ToInt32(r.GetValue(r.GetOrdinal(c)));

        return new ServerProfile
        {
            Id = I("Id"),
            Name = S("Name"),
            GameType = Enum.TryParse<GameType>(S("GameType"), out var gt) ? gt : GameType.Arma3,
            IsDefault = B("IsDefault"),
            CreatedAt = ParseDate(S("CreatedAt")),
            UpdatedAt = ParseDate(S("UpdatedAt")),
            Notes = S("Notes"),
            ServerExecutablePath = S("ServerExePath"),
            UseProfilingBranch = B("UseProfilingBranch"),
            ServerDirectory = S("ServerDirectory"),
            MissionDirectory = S("MissionDirectory"),
            ArmaProfileName = S("ArmaProfileName"),
            Port = I("Port"),
            EnableBattlEye = B("EnableBattlEye"),
            FilePatching = I("FilePatching"),
            ServerFilePatching = B("ServerFilePatching"),
            NoSound = B("NoSound"),
            NoSplash = B("NoSplash"),
            SkipIntro = B("SkipIntro"),
            WorldEmpty = B("WorldEmpty"),
            NetLog = B("NetLog"),
            LoadMissionToMemory = B("LoadMissionToMemory"),
            NoPause = B("NoPause"),
            NoLogs = B("NoLogs"),
            RankingEnabled = B("RankingEnabled"),
            RankingFile = S("RankingFile"),
            EnableHT = B("EnableHT"),
            HugePages = B("HugePages"),
            CpuCount = I("CpuCount"),
            ExThreads = I("ExThreads"),
            MaxMem = I("MaxMem"),
            Malloc = S("Malloc"),
            BandwidthAlg = I("BandwidthAlg"),
            LimitFPS = I("LimitFPS"),
            ServerName = S("ServerName"),
            ServerPassword = S("ServerPassword"),
            AdminPassword = S("AdminPassword"),
            MaxPlayers = I("MaxPlayers"),
            MotdInterval = I("MotdInterval"),
            LogFile = S("LogFile"),
            TimeStampFormat = B("TimeStampFormat"),
            DrawingInMap = B("DrawingInMap"),
            MaxPing = I("MaxPing"),
            MaxDesync = I("MaxDesync"),
            MaxPacketLoss = I("MaxPacketLoss"),
            MaxMsgSend = I("MaxMsgSend"),
            MaxSizeNonguaranteed = I("MaxSizeNonguaranteed"),
            MaxSizeGuaranteed = I("MaxSizeGuaranteed"),
            MinBandwidth = L("MinBandwidth"),
            MaxBandwidth = L("MaxBandwidth"),
            MinErrorToSend = D("MinErrorToSend"),
            MinErrorToSendNear = D("MinErrorToSendNear"),
            Loopback = B("Loopback"),
            Upnp = B("Upnp"),
            DisconnectTimeout = I("DisconnectTimeout"),
            MaxDisconnectTimeout = I("MaxDisconnectTimeout"),
            KickDuplicates = B("KickDuplicates"),
            VerifySignatures = I("VerifySignatures"),
            RequiredSecureId = B("RequiredSecureId"),
            MissionName = S("MissionName"),
            MissionQueue = S("MissionQueue"),
            MissionDifficulty = S("MissionDifficulty"),
            AutoInit = B("AutoInit"),
            Persistent = B("Persistent"),
            DisableVoN = B("DisableVoN"),
            VonCodecQuality = I("VonCodecQuality"),
            VotingEnabled = B("VotingEnabled"),
            VoteMissionPlayers = (float)D("VoteMissionPlayers"),
            VoteThreshold = (float)D("VoteThreshold"),
            EnableDebugConsole = B("EnableDebugConsole"),
            RconPassword = S("RconPassword"),
            RconPort = I("RconPort"),
            UseHeadlessClients = B("UseHeadlessClients"),
            HeadlessClientCount = I("HeadlessClientCount"),
            HeadlessClientExecutablePath = S("HeadlessClientExePath"),
            HeadlessAutoRestart = B("HeadlessAutoRestart"),
            ActiveModPresetId = NI("ActiveModPresetId"),
            AutoRestartOnCrash = B("AutoRestartOnCrash"),
            AutoRestartDelaySeconds = I("AutoRestartDelaySecs"),
            MaxCrashesBeforeGiveUp = I("MaxCrashesBeforeGiveUp"),
            CustomLaunchParameters = S("CustomLaunchParameters"),
            DlcContact = B("DlcContact"),
            DlcGlobalMobilization = B("DlcGlobalMobilization"),
            DlcPrairieFire = B("DlcPrairieFire"),
            DlcCsla = B("DlcCsla"),
            DlcWesternSahara = B("DlcWesternSahara"),
            DlcSpearhead1944 = B("DlcSpearhead1944"),
            DlcReactionForces = B("DlcReactionForces"),
            DlcExpeditionaryForces = B("DlcExpeditionaryForces"),
            RequiredBuild = I("RequiredBuild"),
            VonCodecLegacy = B("VonCodecLegacy"),
            ServerCommandPassword = S("ServerCommandPassword"),
            OnUserConnected = S("OnUserConnected"),
            OnUserDisconnected = S("OnUserDisconnected"),
            OnHackedData = S("OnHackedData"),
            OnDifferentData = S("OnDifferentData"),
            OnUnsignedData = S("OnUnsignedData"),
            OnUserKicked = S("OnUserKicked"),
            MaxCustomFileSize = I("MaxCustomFileSize"),
            MaxPacketSize = I("MaxPacketSize"),
            AutoSelectMission = B("AutoSelectMission"),
            RandomMissionOrder = B("RandomMissionOrder"),
            SkipLobby = B("SkipLobby"),
            DiffGroupIndicators = I("DiffGroupIndicators"),
            DiffFriendlyTags = I("DiffFriendlyTags"),
            DiffEnemyTags = I("DiffEnemyTags"),
            DiffDetectedMines = I("DiffDetectedMines"),
            DiffCommands = I("DiffCommands"),
            DiffWaypoints = I("DiffWaypoints"),
            DiffWeaponInfo = I("DiffWeaponInfo"),
            DiffStanceIndicator = I("DiffStanceIndicator"),
            DiffThirdPersonView = I("DiffThirdPersonView"),
            DiffReducedDamage = B("DiffReducedDamage"),
            DiffStaminaBar = B("DiffStaminaBar"),
            DiffWeaponCrosshair = B("DiffWeaponCrosshair"),
            DiffVisionAid = B("DiffVisionAid"),
            DiffCameraShake = B("DiffCameraShake"),
            DiffScoreTable = B("DiffScoreTable"),
            DiffDeathMessages = B("DiffDeathMessages"),
            DiffVonID = B("DiffVonID"),
            DiffMapContentFriendly = B("DiffMapContentFriendly"),
            DiffMapContentEnemy = B("DiffMapContentEnemy"),
            DiffMapContentMines = B("DiffMapContentMines"),
            DiffAutoReport = B("DiffAutoReport"),
            DiffMultipleSaves = B("DiffMultipleSaves"),
            DiffTacticalPing = B("DiffTacticalPing"),
            SkillAI = D("SkillAI"),
            PrecisionAI = D("PrecisionAI"),
            ViewDistance = I("ViewDistance"),
            ObjectViewDistance = I("ObjectViewDistance"),
            TerrainGrid = D("TerrainGrid"),
            AdminUids = S("AdminUids"),
            IdleFPSLimit = I("IdleFPSLimit"),
            LobbyIdleTimeout = I("LobbyIdleTimeout"),
            RoleTimeOut = I("RoleTimeOut"),
            MissionWhitelistEnabled = B("MissionWhitelistEnabled"),
            MissionWhitelistExtra = S("MissionWhitelistExtra"),
            AntiFloodEnabled = B("AntiFloodEnabled"),
            AntiFloodCycleTime = D("AntiFloodCycleTime"),
            AntiFloodCycleLimit = I("AntiFloodCycleLimit"),
            AntiFloodCycleHardLimit = I("AntiFloodCycleHardLimit"),
            AntiFloodKick = B("AntiFloodKick"),
        };
    }
}
