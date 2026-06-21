using Microsoft.Data.Sqlite;
using Serilog;

namespace Atlas.Data;

/// <summary>
/// Owns the SQLite database file: creation, PRAGMA setup, schema migration, and a shared write lock.
/// All write operations across the app must be serialized through <see cref="WriteLock"/>.
/// </summary>
public sealed class AtlasDatabase
{
    private readonly string _connectionString;

    /// <summary>Serializes writes — SQLite allows a single writer. Acquire before any write/transaction.</summary>
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public AtlasDatabase()
    {
        Directory.CreateDirectory(AppConstants.AppDataRoot);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppConstants.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    /// <summary>Opens a new connection with WAL + foreign keys enabled. Caller disposes it.</summary>
    public SqliteConnection CreateOpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Creates the database (if missing) and runs pending migrations up to the current version.</summary>
    public async Task InitializeAsync()
    {
        await WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            // WAL must be set outside any transaction.
            await using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
                await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            long currentVersion;
            await using (var versionCmd = conn.CreateCommand())
            {
                versionCmd.CommandText = "PRAGMA user_version;";
                currentVersion = Convert.ToInt64(await versionCmd.ExecuteScalarAsync().ConfigureAwait(false));
            }

            if (currentVersion >= DatabaseSchema.SchemaVersion)
            {
                Log.Information("Database schema is current (v{Version}).", currentVersion);
                return;
            }

            Log.Information("Migrating database from v{From} to v{To}.", currentVersion, DatabaseSchema.SchemaVersion);

            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                // v0 -> v1: full schema creation (idempotent).
                if (currentVersion < 1)
                {
                    await using var create = conn.CreateCommand();
                    create.Transaction = tx;
                    create.CommandText = DatabaseSchema.CreateAllTables;
                    await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // v1 -> v2: per-profile UPnP toggle + custom mission-folder override.
                if (currentVersion < 2)
                {
                    await using var alter = conn.CreateCommand();
                    alter.Transaction = tx;
                    alter.CommandText =
                        "ALTER TABLE ServerProfiles ADD COLUMN Upnp INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN MissionDirectory TEXT NOT NULL DEFAULT '';";
                    await alter.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // v2 -> v3: creator/platform DLC toggles (loaded via -mod= folder names).
                if (currentVersion < 3)
                {
                    await using var alter = conn.CreateCommand();
                    alter.Transaction = tx;
                    alter.CommandText =
                        "ALTER TABLE ServerProfiles ADD COLUMN DlcContact INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DlcGlobalMobilization INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DlcPrairieFire INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DlcCsla INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DlcWesternSahara INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DlcSpearhead1944 INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DlcReactionForces INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DlcExpeditionaryForces INTEGER NOT NULL DEFAULT 0;";
                    await alter.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // v3 -> v4: requiredBuild, VoN codec type, and server.cfg scripting callbacks.
                if (currentVersion < 4)
                {
                    await using var alter = conn.CreateCommand();
                    alter.Transaction = tx;
                    alter.CommandText =
                        "ALTER TABLE ServerProfiles ADD COLUMN RequiredBuild INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN VonCodecLegacy INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN ServerCommandPassword TEXT NOT NULL DEFAULT '';" +
                        "ALTER TABLE ServerProfiles ADD COLUMN OnUserConnected TEXT NOT NULL DEFAULT '';" +
                        "ALTER TABLE ServerProfiles ADD COLUMN OnUserDisconnected TEXT NOT NULL DEFAULT '';" +
                        "ALTER TABLE ServerProfiles ADD COLUMN OnHackedData TEXT NOT NULL DEFAULT '';" +
                        "ALTER TABLE ServerProfiles ADD COLUMN OnDifferentData TEXT NOT NULL DEFAULT '';" +
                        "ALTER TABLE ServerProfiles ADD COLUMN OnUnsignedData TEXT NOT NULL DEFAULT '';" +
                        "ALTER TABLE ServerProfiles ADD COLUMN OnUserKicked TEXT NOT NULL DEFAULT '';";
                    await alter.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // v4 -> v5: FilePatching became a 3-state (0 none / 1 headless / 2 all). The old bool "on" (1)
                // meant "all clients", so promote existing 1 -> 2 to preserve behaviour.
                if (currentVersion < 5)
                {
                    await using var patch = conn.CreateCommand();
                    patch.Transaction = tx;
                    patch.CommandText = "UPDATE ServerProfiles SET FilePatching = 2 WHERE FilePatching = 1;";
                    await patch.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // v5 -> v6: basic.cfg max packet size + server.cfg max custom file size.
                if (currentVersion < 6)
                {
                    await using var alter = conn.CreateCommand();
                    alter.Transaction = tx;
                    alter.CommandText =
                        "ALTER TABLE ServerProfiles ADD COLUMN MaxCustomFileSize INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN MaxPacketSize INTEGER NOT NULL DEFAULT 1400;";
                    await alter.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // v6 -> v7: mission flags (auto-select, random order, skip lobby).
                if (currentVersion < 7)
                {
                    await using var alter = conn.CreateCommand();
                    alter.Transaction = tx;
                    alter.CommandText =
                        "ALTER TABLE ServerProfiles ADD COLUMN AutoSelectMission INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN RandomMissionOrder INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN SkipLobby INTEGER NOT NULL DEFAULT 0;";
                    await alter.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // v7 -> v8: Arma3Profile granular difficulty grid + AI level + server view distance.
                if (currentVersion < 8)
                {
                    await using var alter = conn.CreateCommand();
                    alter.Transaction = tx;
                    alter.CommandText =
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffGroupIndicators INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffFriendlyTags INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffEnemyTags INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffDetectedMines INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffCommands INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffWaypoints INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffWeaponInfo INTEGER NOT NULL DEFAULT 2;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffStanceIndicator INTEGER NOT NULL DEFAULT 2;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffThirdPersonView INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffReducedDamage INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffStaminaBar INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffWeaponCrosshair INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffVisionAid INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffCameraShake INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffScoreTable INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffDeathMessages INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffVonID INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffMapContentFriendly INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffMapContentEnemy INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffMapContentMines INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffAutoReport INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffMultipleSaves INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN DiffTacticalPing INTEGER NOT NULL DEFAULT 1;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN SkillAI REAL NOT NULL DEFAULT 0.6;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN PrecisionAI REAL NOT NULL DEFAULT 0.5;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN ViewDistance INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN ObjectViewDistance INTEGER NOT NULL DEFAULT 0;" +
                        "ALTER TABLE ServerProfiles ADD COLUMN TerrainGrid REAL NOT NULL DEFAULT 0;";
                    await alter.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await using (var setVersion = conn.CreateCommand())
                {
                    setVersion.Transaction = tx;
                    // PRAGMA user_version doesn't accept parameters; SchemaVersion is a trusted int constant.
                    setVersion.CommandText = $"PRAGMA user_version = {DatabaseSchema.SchemaVersion};";
                    await setVersion.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await tx.CommitAsync().ConfigureAwait(false);
                Log.Information("Database migrated to v{Version}.", DatabaseSchema.SchemaVersion);
            }
            catch
            {
                await tx.RollbackAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Deletes every row from all user tables (schema-agnostic), leaving the schema intact.
    /// Used by the Settings "Clear Entire Database" action. Serialized via <see cref="WriteLock"/>.
    /// </summary>
    public async Task ClearAllDataAsync()
    {
        await WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            // Collect user tables (exclude SQLite internal/bookkeeping tables).
            var tables = new List<string>();
            await using (var list = conn.CreateCommand())
            {
                list.CommandText =
                    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                await using var reader = await list.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                    tables.Add(reader.GetString(0));
            }

            // Defer FK enforcement so delete order doesn't matter, all within one transaction.
            await using (var off = conn.CreateCommand())
            {
                off.CommandText = "PRAGMA foreign_keys=OFF;";
                await off.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await using (var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false))
            {
                try
                {
                    foreach (var table in tables)
                    {
                        await using var del = conn.CreateCommand();
                        del.Transaction = tx;
                        // Table names come from sqlite_master (not user input); quote defensively anyway.
                        del.CommandText = $"DELETE FROM \"{table.Replace("\"", "\"\"")}\";";
                        await del.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // Reset AUTOINCREMENT counters if the bookkeeping table exists.
                    await using (var seq = conn.CreateCommand())
                    {
                        seq.Transaction = tx;
                        seq.CommandText = "DELETE FROM sqlite_sequence;";
                        try { await seq.ExecuteNonQueryAsync().ConfigureAwait(false); }
                        catch (SqliteException) { /* sqlite_sequence absent when no AUTOINCREMENT tables */ }
                    }

                    await tx.CommitAsync().ConfigureAwait(false);
                }
                catch
                {
                    await tx.RollbackAsync().ConfigureAwait(false);
                    throw;
                }
            }

            await using (var on = conn.CreateCommand())
            {
                on.CommandText = "PRAGMA foreign_keys=ON;";
                await on.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            Log.Warning("Cleared all data from {Count} database table(s).", tables.Count);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
