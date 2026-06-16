using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Atlas.Core.Models;
using Atlas.Data;
using HtmlAgilityPack;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IModPresetService"/>
public sealed partial class ModPresetService : IModPresetService
{
    private readonly AtlasDatabase _db;

    public ModPresetService(AtlasDatabase db) => _db = db;

    // ------------------------------------------------------------------ reads

    public async Task<List<ModPreset>> GetAllPresetsAsync()
    {
        var result = new List<ModPreset>();
        await using var conn = _db.CreateOpenConnection();

        var ids = new List<int>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM ModPresets ORDER BY Name COLLATE NOCASE;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
        }
        foreach (var id in ids)
        {
            var preset = await LoadAsync(conn, id);
            if (preset is not null) result.Add(preset);
        }
        return result;
    }

    public async Task<ModPreset?> GetPresetByIdAsync(int id)
    {
        await using var conn = _db.CreateOpenConnection();
        return await LoadAsync(conn, id);
    }

    private static async Task<ModPreset?> LoadAsync(SqliteConnection conn, int id)
    {
        ModPreset? preset = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, Description, CreatedAt, UpdatedAt FROM ModPresets WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                preset = new ModPreset
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Description = reader.GetString(2),
                    CreatedAt = ParseDate(reader.GetString(3)),
                    UpdatedAt = ParseDate(reader.GetString(4)),
                };
            }
        }
        if (preset is null) return null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT m.Id, m.WorkshopId, m.Name, m.FolderName, m.IsLocal, m.LocalPath, m.Version,
       m.SteamFileSize, m.LastUpdated, m.LastChecked, m.UpdateAvailable,
       pm.LoadOrder, pm.EnabledForServer, pm.EnabledForClient, pm.EnabledForHeadless,
       pm.IsOptional, pm.IsServerOnly
FROM PresetMods pm JOIN Mods m ON m.Id = pm.ModId
WHERE pm.PresetId = @id ORDER BY pm.LoadOrder;";
            cmd.Parameters.AddWithValue("@id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                preset.Mods.Add(new ArmaModEntry
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
                });
            }
        }
        return preset;
    }

    // ------------------------------------------------------------------ writes

    public async Task<ModPreset> CreatePresetAsync(string name, string description, List<ArmaModEntry> mods)
    {
        var now = DateTime.UtcNow;
        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var tx = conn.BeginTransaction();

            int id;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO ModPresets (Name, Description, CreatedAt, UpdatedAt) VALUES (@n, @d, @c, @u);" +
                    "SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@d", description);
                cmd.Parameters.AddWithValue("@c", now.ToString("o"));
                cmd.Parameters.AddWithValue("@u", now.ToString("o"));
                id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }
            await SavePresetModsAsync(conn, tx, id, mods);
            await tx.CommitAsync();
            Log.Information("Created mod preset '{Name}' (Id {Id}) with {Count} mods.", name, id, mods.Count);
            return (await GetPresetByIdAsync(id))!;
        }
        finally { _db.WriteLock.Release(); }
    }

    public async Task UpdatePresetAsync(ModPreset preset)
    {
        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var tx = conn.BeginTransaction();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE ModPresets SET Name = @n, Description = @d, UpdatedAt = @u WHERE Id = @id;";
                cmd.Parameters.AddWithValue("@n", preset.Name);
                cmd.Parameters.AddWithValue("@d", preset.Description);
                cmd.Parameters.AddWithValue("@u", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@id", preset.Id);
                await cmd.ExecuteNonQueryAsync();
            }
            await SavePresetModsAsync(conn, tx, preset.Id, preset.Mods);
            await tx.CommitAsync();
            Log.Information("Updated mod preset '{Name}' (Id {Id}).", preset.Name, preset.Id);
        }
        finally { _db.WriteLock.Release(); }
    }

    public async Task DeletePresetAsync(int id)
    {
        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var cmd = conn.CreateCommand();
            // PresetMods cascade-delete; ServerProfiles.ActiveModPresetId is set NULL by the FK.
            cmd.CommandText = "DELETE FROM ModPresets WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
            Log.Information("Deleted mod preset Id {Id}.", id);
        }
        finally { _db.WriteLock.Release(); }
    }

    public async Task<ModPreset> ClonePresetAsync(int id, string newName)
    {
        var source = await GetPresetByIdAsync(id)
            ?? throw new InvalidOperationException($"Preset {id} not found.");
        return await CreatePresetAsync(newName, source.Description, source.Mods.Select(CopyMod).ToList());
    }

    public async Task ApplyPresetToProfileAsync(int presetId, ServerProfile profile)
    {
        var preset = await GetPresetByIdAsync(presetId)
            ?? throw new InvalidOperationException($"Preset {presetId} not found.");
        profile.Mods = preset.Mods.Select(CopyMod).ToList();
        profile.ActiveModPresetId = presetId;
    }

    public void DetachPresetFromProfile(ServerProfile profile) => profile.ActiveModPresetId = null;

    public Task<ModPreset> CreatePresetFromProfileAsync(string name, string description, ServerProfile profile)
        => CreatePresetAsync(name, description, profile.Mods.Select(CopyMod).ToList());

    // ------------------------------------------------------------------ A3 Launcher .html

    public async Task<List<ArmaModEntry>> ParseA3LauncherPresetAsync(string filePath)
    {
        var doc = new HtmlDocument();
        doc.Load(filePath, Encoding.UTF8);

        var mods = new List<ArmaModEntry>();
        var rows = doc.DocumentNode.SelectNodes("//tr[@data-type='ModContainer']");
        if (rows is not null)
        {
            var order = 0;
            foreach (var row in rows)
            {
                var name = row.SelectSingleNode(".//td[@data-type='DisplayName']")?.InnerText.Trim();
                var href = row.SelectSingleNode(".//a[@data-type='Link']")?.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var workshopId = ExtractWorkshopId(href ?? string.Empty);
                mods.Add(new ArmaModEntry
                {
                    Name = HtmlEntity.DeEntitize(name),
                    WorkshopId = workshopId,
                    IsLocal = workshopId == 0,
                    FolderName = "@" + SanitizeFolder(name),
                    LoadOrder = order++,
                });
            }
        }
        Log.Information("Parsed {Count} mods from A3 launcher preset {File}.", mods.Count, filePath);
        return await Task.FromResult(mods).ConfigureAwait(false);
    }

    public async Task<ModPreset> ImportFromA3LauncherPresetAsync(string filePath, string presetName)
    {
        var mods = await ParseA3LauncherPresetAsync(filePath).ConfigureAwait(false);
        return await CreatePresetAsync(presetName, "Imported from an Arma 3 Launcher preset.", mods);
    }

    public async Task ExportToA3LauncherFormatAsync(ModPreset preset, string outputPath)
    {
        var workshopMods = preset.Mods.Where(m => m.WorkshopId != 0).ToList();
        var skipped = preset.Mods.Count - workshopMods.Count;

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<html>");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta name=\"arma:Type\" content=\"preset\" />");
        sb.AppendLine($"<meta name=\"arma:PresetName\" content=\"{Escape(preset.Name)}\" />");
        sb.AppendLine("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=UTF-8\" />");
        sb.AppendLine($"<title>Arma 3 Preset {Escape(preset.Name)}</title>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine($"<h1>Arma 3 Preset <strong>{Escape(preset.Name)}</strong></h1>");
        sb.AppendLine("<div class=\"mod-list\"><table>");
        foreach (var mod in workshopMods)
        {
            var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={mod.WorkshopId}";
            sb.AppendLine("<tr data-type=\"ModContainer\">");
            sb.AppendLine($"<td data-type=\"DisplayName\">{Escape(mod.Name)}</td>");
            sb.AppendLine($"<td><a href=\"{url}\" data-type=\"Link\">{url}</a></td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</table></div>");
        sb.AppendLine("</body></html>");

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        Log.Information("Exported preset '{Name}' to A3 format ({Count} mods, {Skipped} local mods skipped).",
            preset.Name, workshopMods.Count, skipped);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task SavePresetModsAsync(SqliteConnection conn, SqliteTransaction tx, int presetId,
        IEnumerable<ArmaModEntry> mods)
    {
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM PresetMods WHERE PresetId = @p;";
            del.Parameters.AddWithValue("@p", presetId);
            await del.ExecuteNonQueryAsync();
        }

        var order = 0;
        foreach (var mod in mods)
        {
            var modId = await UpsertModAsync(conn, tx, mod);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO PresetMods (PresetId, ModId, LoadOrder, EnabledForServer, EnabledForClient,
                        EnabledForHeadless, IsOptional, IsServerOnly)
VALUES (@p, @m, @lo, @es, @ec, @eh, @io, @iso);";
            cmd.Parameters.AddWithValue("@p", presetId);
            cmd.Parameters.AddWithValue("@m", modId);
            cmd.Parameters.AddWithValue("@lo", mod.LoadOrder != 0 ? mod.LoadOrder : order);
            cmd.Parameters.AddWithValue("@es", mod.EnabledForServer);
            cmd.Parameters.AddWithValue("@ec", mod.EnabledForClient);
            cmd.Parameters.AddWithValue("@eh", mod.EnabledForHeadless);
            cmd.Parameters.AddWithValue("@io", mod.IsOptional);
            cmd.Parameters.AddWithValue("@iso", mod.IsServerOnly);
            await cmd.ExecuteNonQueryAsync();
            order++;
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

    private static ArmaModEntry CopyMod(ArmaModEntry m) => new()
    {
        ModId = 0, // re-upserted on save
        WorkshopId = m.WorkshopId,
        Name = m.Name,
        FolderName = m.FolderName,
        LocalPath = m.LocalPath,
        IsLocal = m.IsLocal,
        Version = m.Version,
        SteamFileSize = m.SteamFileSize,
        LastUpdated = m.LastUpdated,
        LastChecked = m.LastChecked,
        UpdateAvailable = m.UpdateAvailable,
        LoadOrder = m.LoadOrder,
        EnabledForServer = m.EnabledForServer,
        EnabledForClient = m.EnabledForClient,
        EnabledForHeadless = m.EnabledForHeadless,
        IsOptional = m.IsOptional,
        IsServerOnly = m.IsServerOnly,
        IsHeadlessOnly = m.IsHeadlessOnly,
    };

    private static ulong ExtractWorkshopId(string href)
    {
        var match = WorkshopIdRegex().Match(href);
        return match.Success && ulong.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    private static string SanitizeFolder(string name)
    {
        var cleaned = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "Mod" : cleaned;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : default;

    [GeneratedRegex(@"id=(\d+)")]
    private static partial Regex WorkshopIdRegex();
}
