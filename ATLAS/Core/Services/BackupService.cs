using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Atlas.Core.Models;
using Serilog;

namespace Atlas.Core.Services;

/// <summary>
/// Creates and restores full profile snapshots — the generated configs, the server Keys folder, a JSON
/// export of the profile, and the mission folder(s) — as a single timestamped zip under
/// <c>%AppData%\ATLAS\Backups\&lt;Profile&gt;\</c>.
/// </summary>
public interface IBackupService
{
    Task<string> CreateBackupAsync(ServerProfile profile, IProgress<string>? progress = null, CancellationToken ct = default);
    Task RestoreBackupAsync(string zipPath, ServerProfile profile, IProgress<string>? progress = null, CancellationToken ct = default);
    string GetBackupsRoot(ServerProfile profile);
}

/// <inheritdoc cref="IBackupService"/>
public sealed class BackupService : IBackupService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Prefixes for the zip's internal layout.
    private const string ConfigPrefix = "configs/";
    private const string KeysPrefix = "keys/";
    private const string MissionsPrefix = "missions/";
    private const string ProfileJson = "profile.json";

    public string GetBackupsRoot(ServerProfile profile)
    {
        var safe = MakeSafe(profile.Name);
        return Path.Combine(AppConstants.AppDataRoot, "Backups", safe);
    }

    public async Task<string> CreateBackupAsync(
        ServerProfile profile, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var serverDir = profile.ServerDirectory;
        if (string.IsNullOrWhiteSpace(serverDir) || !Directory.Exists(serverDir))
            throw new InvalidOperationException("The profile's server directory is not set or does not exist.");

        var root = GetBackupsRoot(profile);
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, $"backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            // 1) Profile JSON (the ATLAS settings, so a restore-to-new-machine is possible).
            progress?.Report("Writing profile settings…");
            var entry = zip.CreateEntry(ProfileJson, CompressionLevel.Optimal);
            using (var s = entry.Open())
                JsonSerializer.Serialize(s, profile, Json);

            // 2) Generated config files at the server root.
            foreach (var name in new[] { "server.cfg", "basic.cfg" })
            {
                var path = Path.Combine(serverDir, name);
                if (File.Exists(path)) AddFile(zip, path, ConfigPrefix + name, ct, progress);
            }
            // beserver + Arma3Profile live in subfolders — capture them by relative path under configs/.
            AddTree(zip, Path.Combine(serverDir, "BattlEye"), ConfigPrefix + "BattlEye/", ct, progress);
            AddTree(zip, Path.Combine(serverDir, "profiles"), ConfigPrefix + "profiles/", ct, progress,
                filter: f => f.EndsWith(".Arma3Profile", StringComparison.OrdinalIgnoreCase));

            // 3) Keys folder (mod signatures).
            foreach (var keys in new[] { "Keys", "keys" })
                AddTree(zip, Path.Combine(serverDir, keys), KeysPrefix, ct, progress);

            // 4) Mission folder(s) — full scope.
            foreach (var dir in MissionDirs(profile))
                AddTree(zip, dir, MissionsPrefix + Path.GetFileName(dir) + "/", ct, progress);
        }, ct).ConfigureAwait(false);

        Log.Information("Created backup for '{Profile}' at {Path}.", profile.Name, zipPath);
        progress?.Report($"Backup complete: {Path.GetFileName(zipPath)}");
        return zipPath;
    }

    public async Task RestoreBackupAsync(
        string zipPath, ServerProfile profile, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var serverDir = profile.ServerDirectory;
        if (string.IsNullOrWhiteSpace(serverDir))
            throw new InvalidOperationException("The profile's server directory is not set.");
        Directory.CreateDirectory(serverDir);

        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/') || entry.Length == 0 && entry.Name.Length == 0) continue;

                // Map the zip's internal layout back onto the server directory. profile.json is metadata
                // only — it is NOT written back over the live DB profile here (restore is file-level).
                string? dest = entry.FullName switch
                {
                    ProfileJson => null,
                    var f when f.StartsWith(ConfigPrefix) => Path.Combine(serverDir, f[ConfigPrefix.Length..]),
                    var f when f.StartsWith(KeysPrefix) => Path.Combine(serverDir, "Keys", f[KeysPrefix.Length..]),
                    var f when f.StartsWith(MissionsPrefix) => Path.Combine(serverDir, f[MissionsPrefix.Length..]),
                    _ => null,
                };
                if (dest is null) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                progress?.Report($"Restoring {entry.FullName}…");
                entry.ExtractToFile(dest, overwrite: true);
            }
        }, ct).ConfigureAwait(false);

        Log.Information("Restored backup {Zip} into {Dir}.", zipPath, serverDir);
        progress?.Report("Restore complete.");
    }

    private static IEnumerable<string> MissionDirs(ServerProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.MissionDirectory) && Directory.Exists(p.MissionDirectory))
        {
            yield return p.MissionDirectory;
            yield break;
        }
        foreach (var name in new[] { "MPMissions", "Missions" })
        {
            var dir = Path.Combine(p.ServerDirectory, name);
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    private static void AddFile(ZipArchive zip, string path, string entryName, CancellationToken ct, IProgress<string>? progress)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report($"Adding {entryName}…");
        zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
    }

    private static void AddTree(ZipArchive zip, string sourceDir, string entryPrefix, CancellationToken ct,
        IProgress<string>? progress, Func<string, bool>? filter = null)
    {
        if (!Directory.Exists(sourceDir)) return;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (filter is not null && !filter(file)) continue;
            var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            progress?.Report($"Adding {entryPrefix}{rel}…");
            zip.CreateEntryFromFile(file, entryPrefix + rel, CompressionLevel.Optimal);
        }
    }

    private static string MakeSafe(string name)
    {
        var safe = string.Join("_", (name ?? "profile").Split(Path.GetInvalidFileNameChars()));
        return string.IsNullOrWhiteSpace(safe) ? "profile" : safe;
    }
}
