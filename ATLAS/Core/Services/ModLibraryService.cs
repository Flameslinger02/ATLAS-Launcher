using System.Globalization;
using Atlas.Core.Models;
using Atlas.Data;
using Microsoft.Data.Sqlite;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IModLibraryService"/>
public sealed class ModLibraryService : IModLibraryService
{
    private readonly AtlasDatabase _db;

    public ModLibraryService(AtlasDatabase db) => _db = db;

    public async Task<List<ArmaModEntry>> GetAllModsAsync()
    {
        var result = new List<ArmaModEntry>();
        await using var conn = _db.CreateOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT Id, WorkshopId, Name, FolderName, IsLocal, LocalPath, Version, SteamFileSize,
       LastUpdated, LastChecked, UpdateAvailable
FROM Mods
ORDER BY Name COLLATE NOCASE;";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ArmaModEntry
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
            });
        }
        return result;
    }

    public async Task<Dictionary<int, List<string>>> GetModUsageAsync()
    {
        var map = new Dictionary<int, List<string>>();
        await using var conn = _db.CreateOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT pm.ModId, p.Name
FROM ProfileMods pm
JOIN ServerProfiles p ON p.Id = pm.ProfileId
ORDER BY p.Name COLLATE NOCASE;";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var modId = reader.GetInt32(0);
            var name = reader.GetString(1);
            if (!map.TryGetValue(modId, out var list)) { list = new List<string>(); map[modId] = list; }
            if (!list.Contains(name)) list.Add(name);
        }
        return map;
    }

    public async Task<int> UpsertModAsync(ArmaModEntry mod)
    {
        await using var conn = _db.CreateOpenConnection();
        using var tx = conn.BeginTransaction();
        var id = await UpsertModAsync(conn, tx, mod);
        tx.Commit();
        return id;
    }

    public async Task DeleteModAsync(int modId)
    {
        await using var conn = _db.CreateOpenConnection();
        using var tx = conn.BeginTransaction();

        // Remove assignments first (explicit, so this works regardless of the FK-cascade pragma).
        foreach (var table in new[] { "ProfileMods", "PresetMods" })
        {
            await using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {table} WHERE ModId = @id;";
            del.Parameters.AddWithValue("@id", modId);
            await del.ExecuteNonQueryAsync();
        }
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM Mods WHERE Id = @id;";
            del.Parameters.AddWithValue("@id", modId);
            await del.ExecuteNonQueryAsync();
        }
        tx.Commit();
    }

    private static DateTime ParseDate(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d : DateTime.MinValue;

    /// <summary>Master-registry upsert — mirrors the private helper in ProfileService / ModPresetService.</summary>
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
}
