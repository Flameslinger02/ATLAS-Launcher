using Atlas.Core.Models;
using Serilog;

namespace Atlas.Core.Services;

/// <summary>
/// Pre-launch check: are the addons the profile's rotation missions require actually provided by the
/// enabled mods / DLC? Best-effort — any read/parse problem yields an empty result so a launch is never
/// blocked by the checker itself.
/// </summary>
public interface IMissionDependencyChecker
{
    /// <summary>Required addons (CfgPatches names) of the profile's active/queued missions that no
    /// enabled server mod or enabled DLC appears to provide. Empty = all met (or nothing checkable).</summary>
    Task<IReadOnlyList<string>> GetUnmetDependenciesAsync(ServerProfile profile, CancellationToken ct = default);
}

/// <inheritdoc cref="IMissionDependencyChecker"/>
public sealed class MissionDependencyChecker : IMissionDependencyChecker
{
    private readonly ISettingsService _settings;

    public MissionDependencyChecker(ISettingsService settings) => _settings = settings;

    public async Task<IReadOnlyList<string>> GetUnmetDependenciesAsync(ServerProfile profile, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => Check(profile), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Mission dependency pre-flight failed; not blocking the launch.");
            return Array.Empty<string>();
        }
    }

    private IReadOnlyList<string> Check(ServerProfile p)
    {
        // Which addons do the rotation missions require?
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ResolveMissionPaths(p))
        {
            try
            {
                foreach (var addon in MissionSqmReader.ReadAddOns(path)) required.Add(addon);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read addOns[] from {Mission}; skipping it in the pre-flight.", path);
            }
        }
        if (required.Count == 0) return Array.Empty<string>();

        // Base-game / official-DLC CfgPatches all use the A3_ prefix — always satisfied.
        var candidates = required.Where(r => !r.StartsWith("a3_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return Array.Empty<string>();

        var available = BuildAvailableAddonIndex(p);
        return candidates
            .Where(r => !available.Contains(r))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>On-disk paths (pbo file or unpacked folder) of the active + queued missions.</summary>
    private static List<string> ResolveMissionPaths(ServerProfile p)
    {
        var names = (p.MissionQueue ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (names.Count == 0 && !string.IsNullOrWhiteSpace(p.MissionName)) names.Add(p.MissionName.Trim());
        if (names.Count == 0) return new List<string>();

        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.MissionDirectory) && Directory.Exists(p.MissionDirectory))
            dirs.Add(p.MissionDirectory);
        else if (!string.IsNullOrWhiteSpace(p.ServerDirectory))
        {
            dirs.Add(Path.Combine(p.ServerDirectory, "MPMissions"));
            dirs.Add(Path.Combine(p.ServerDirectory, "Missions"));
        }

        var paths = new List<string>();
        foreach (var name in names)
        {
            foreach (var dir in dirs)
            {
                var pbo = Path.Combine(dir, name + ".pbo");
                var folder = Path.Combine(dir, name);
                if (File.Exists(pbo)) { paths.Add(pbo); break; }
                if (Directory.Exists(folder)) { paths.Add(folder); break; }
            }
        }
        return paths;
    }

    /// <summary>PBO basenames provided by the enabled server mods and enabled creator-DLC folders.
    /// Mods conventionally name their PBOs after their CfgPatches classes (cba_main.pbo → cba_main),
    /// which makes this a reliable heuristic without parsing every mod's config.bin.</summary>
    private HashSet<string> BuildAvailableAddonIndex(ServerProfile p)
    {
        var index = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stagingRoot = GetStagingRoot();

        foreach (var mod in p.Mods.Where(m => m.EnabledForServer))
        {
            var source = mod.IsLocal ? mod.LocalPath : WorkshopPaths.ResolveModFolder(stagingRoot, mod.WorkshopId);
            AddPboNames(index, source);
        }

        // Creator DLC enabled on the profile lives as folders inside the server directory.
        if (!string.IsNullOrWhiteSpace(p.ServerDirectory))
            foreach (var dlc in ConfigGeneratorService.EnabledDlcFolders(p))
                AddPboNames(index, Path.Combine(p.ServerDirectory, dlc));

        return index;
    }

    private static void AddPboNames(HashSet<string> index, string? modFolder)
    {
        if (string.IsNullOrWhiteSpace(modFolder) || !Directory.Exists(modFolder)) return;
        try
        {
            foreach (var sub in new[] { "addons", "Addons" })
            {
                var dir = Path.Combine(modFolder, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (var pbo in Directory.EnumerateFiles(dir, "*.pbo", SearchOption.TopDirectoryOnly))
                    index.Add(Path.GetFileNameWithoutExtension(pbo));
                break;   // don't double-scan on case-insensitive filesystems
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not enumerate PBOs in {Folder}.", modFolder);
        }
    }

    private string GetStagingRoot()
    {
        var configured = _settings.Settings.ModStagingDirectory;
        return string.IsNullOrEmpty(configured)
            ? Path.Combine(AppConstants.AppDataRoot, "Mods")
            : configured;
    }
}
