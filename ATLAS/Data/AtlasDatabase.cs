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

                // Future migrations: if (currentVersion < 2) { ... }

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
