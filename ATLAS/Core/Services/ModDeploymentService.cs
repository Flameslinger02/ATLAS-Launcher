using System.Diagnostics;
using Atlas.Core.Models;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IModDeploymentService"/>
public sealed class ModDeploymentService : IModDeploymentService
{
    private readonly ISettingsService _settings;

    public ModDeploymentService(ISettingsService settings) => _settings = settings;

    // ------------------------------------------------------------------ deploy

    public async Task DeployModsAsync(ServerProfile profile, IProgress<string> progress, CancellationToken ct)
    {
        var stagingRoot = GetStagingRoot();
        Directory.CreateDirectory(profile.ServerDirectory);

        foreach (var mod in profile.Mods.Where(m => m.EnabledForServer))
        {
            ct.ThrowIfCancellationRequested();

            var source = GetModSourceFolder(mod, stagingRoot);
            if (!Directory.Exists(source))
            {
                progress.Report($"WARNING: source folder missing for '{mod.Name}' ({source}); skipping.");
                Log.Warning("Deploy skipped '{Name}': source folder missing at {Source}.", mod.Name, source);
                continue;
            }

            var target = Path.Combine(profile.ServerDirectory, mod.FolderName);

            try
            {
                RemoveExistingDeployTarget(target);

                if (await TryMakeJunctionAsync(target, source, ct).ConfigureAwait(false))
                {
                    progress.Report($"Linked '{mod.Name}' (junction).");
                }
                else if (TryCreateSymbolicLink(target, source))
                {
                    progress.Report($"Linked '{mod.Name}' (symlink).");
                }
                else
                {
                    progress.Report($"Copying '{mod.Name}' (links unavailable — enable Developer Mode or run elevated)…");
                    await CopyDirectoryAsync(source, target, ct).ConfigureAwait(false);
                    progress.Report($"Copied '{mod.Name}'.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                progress.Report($"ERROR deploying '{mod.Name}': {ex.Message}");
                Log.Error(ex, "Failed to deploy mod '{Name}' to {Target}.", mod.Name, target);
            }
        }

        await CopyModKeysAsync(profile, progress, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ keys

    public async Task CopyModKeysAsync(ServerProfile profile, IProgress<string> progress, CancellationToken ct = default)
    {
        var stagingRoot = GetStagingRoot();
        var keysDir = Path.Combine(profile.ServerDirectory, "Keys");
        Directory.CreateDirectory(keysDir);

        var copied = 0;
        foreach (var mod in profile.Mods.Where(m => m.EnabledForServer))
        {
            ct.ThrowIfCancellationRequested();

            var source = GetModSourceFolder(mod, stagingRoot);
            if (!Directory.Exists(source)) continue;

            foreach (var key in EnumerateBikeys(source))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var dest = Path.Combine(keysDir, Path.GetFileName(key));
                    File.Copy(key, dest, overwrite: true);
                    copied++;
                }
                catch (Exception ex)
                {
                    progress.Report($"WARNING: failed to copy key {Path.GetFileName(key)}: {ex.Message}");
                    Log.Warning(ex, "Failed to copy bikey {Key} for mod '{Name}'.", key, mod.Name);
                }
            }
        }

        progress.Report($"Synced {copied} mod key(s) to {keysDir}.");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<List<KeyConflict>> CheckKeyConflictsAsync(ServerProfile profile, CancellationToken ct = default)
    {
        var stagingRoot = GetStagingRoot();
        var conflicts = new List<KeyConflict>();
        // bikey filename (lower-case) -> first mod folder that shipped it.
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in profile.Mods.Where(m => m.EnabledForServer))
        {
            ct.ThrowIfCancellationRequested();

            var source = GetModSourceFolder(mod, stagingRoot);
            if (!Directory.Exists(source)) continue;

            foreach (var key in EnumerateBikeys(source))
            {
                var name = Path.GetFileName(key);
                if (seen.TryGetValue(name, out var firstFolder))
                {
                    if (!string.Equals(firstFolder, mod.FolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        conflicts.Add(new KeyConflict
                        {
                            KeyFileName = name,
                            FolderA = firstFolder,
                            FolderB = mod.FolderName,
                        });
                    }
                }
                else
                {
                    seen[name] = mod.FolderName;
                }
            }
        }

        if (conflicts.Count > 0)
            Log.Warning("Detected {Count} duplicate-key conflict(s) for profile '{Profile}'.", conflicts.Count, profile.Name);

        return await Task.FromResult(conflicts).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ cleanup

    public async Task CleanupStaleDeployedModsAsync(ServerProfile profile, CancellationToken ct = default)
    {
        if (!Directory.Exists(profile.ServerDirectory)) return;

        var enabled = profile.Mods
            .Where(m => m.EnabledForServer)
            .Select(m => m.FolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(profile.ServerDirectory, "@*"))
            {
                ct.ThrowIfCancellationRequested();

                var di = new DirectoryInfo(dir);
                if (!di.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // only remove links, never real dirs
                if (enabled.Contains(di.Name)) continue;

                try
                {
                    // recursive:false is critical on a reparse point — never delete through the link target.
                    Directory.Delete(dir, recursive: false);
                    Log.Information("Removed stale deployed link {Dir}.", dir);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to remove stale deployed link {Dir}.", dir);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enumerate deployed mods for cleanup in {Dir}.", profile.ServerDirectory);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ source resolution

    private string GetStagingRoot()
    {
        var configured = _settings.Settings.ModStagingDirectory;
        return string.IsNullOrEmpty(configured)
            ? Path.Combine(AppConstants.AppDataRoot, "Mods")
            : configured;
    }

    private static string GetModSourceFolder(ArmaModEntry mod, string stagingRoot) =>
        mod.IsLocal
            ? mod.LocalPath
            : Path.Combine(stagingRoot, "steamapps", "workshop", "content",
                AppConstants.Arma3WorkshopAppId, mod.WorkshopId.ToString());

    private static IEnumerable<string> EnumerateBikeys(string source)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in SafeEnumerate(source, "*.bikey", SearchOption.TopDirectoryOnly))
            if (seen.Add(key)) yield return key;

        foreach (var sub in new[] { "keys", "Keys" })
        {
            var keysDir = Path.Combine(source, sub);
            if (!Directory.Exists(keysDir)) continue;
            foreach (var key in SafeEnumerate(keysDir, "*.bikey", SearchOption.TopDirectoryOnly))
                if (seen.Add(key)) yield return key;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern, SearchOption option)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, option);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate {Pattern} in {Dir}.", pattern, dir);
            return [];
        }
    }

    // ------------------------------------------------------------------ link helpers

    /// <summary>
    /// Removes an existing deploy target before (re)linking. A reparse point is unlinked with
    /// <c>recursive:false</c> (never deletes the link's target); a real copied directory is deleted fully.
    /// </summary>
    private static void RemoveExistingDeployTarget(string target)
    {
        // A stale file or file-symlink occupying the path would block mklink — clear it first.
        if (File.Exists(target)) { File.Delete(target); return; }
        if (!Directory.Exists(target)) return;

        var di = new DirectoryInfo(target);
        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
            Directory.Delete(target, recursive: false);
        else
            Directory.Delete(target, recursive: true);
    }

    /// <summary>Creates a directory junction via <c>cmd /c mklink /J</c> (no admin / Developer Mode required).</summary>
    private static async Task<bool> TryMakeJunctionAsync(string target, string source, CancellationToken ct)
    {
        // Strip trailing separators so a backslash never escapes a closing quote in the command line.
        var link = target.TrimEnd('\\', '/');
        var dest = source.TrimEnd('\\', '/');

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                // Not redirecting stdout/stderr: with CreateNoWindow the one-line mklink output is
                // discarded, and redirecting without draining the pipes would risk a stall.
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(dest);

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0 && Directory.Exists(link);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "mklink junction creation failed for {Link} -> {Target}.", link, dest);
            return false;
        }
    }

    /// <summary>Fallback: directory symlink (requires admin or Developer Mode).</summary>
    private static bool TryCreateSymbolicLink(string target, string source)
    {
        try
        {
            Directory.CreateSymbolicLink(target, source);
            return Directory.Exists(target);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Directory symlink creation failed for {Link} -> {Target}.", target, source);
            return false;
        }
    }

    private static async Task CopyDirectoryAsync(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(dir.Replace(source, target, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var dest = file.Replace(source, target, StringComparison.OrdinalIgnoreCase);
            await using var src = File.OpenRead(file);
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
    }
}
