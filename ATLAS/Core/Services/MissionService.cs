using System.Text.RegularExpressions;
using Atlas.Core.Models;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IMissionService"/>
public sealed partial class MissionService : IMissionService
{
    /// <summary>The mission subfolders scanned under a server directory.</summary>
    private static readonly string[] MissionFolders = { "MPMissions", "Missions" };

    public async Task<List<MissionInfo>> ScanMissionsAsync(string serverDirectory)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory))
            return new List<MissionInfo>();

        return await Task.Run(() =>
        {
            var results = new List<MissionInfo>();

            foreach (var folder in MissionFolders)
            {
                var dir = Path.Combine(serverDirectory, folder);
                if (!Directory.Exists(dir)) continue;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.pbo", SearchOption.TopDirectoryOnly))
                    {
                        var fi = new FileInfo(file);
                        var info = ParsePboFileName(fi.Name);
                        info.FileSizeBytes = fi.Length;
                        info.LastModified = fi.LastWriteTime;
                        info.SourceFolder = folder;
                        results.Add(info);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Mission scan failed for folder {Folder}; continuing.", dir);
                }
            }

            return results
                .OrderBy(m => m.MissionName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Terrain, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }).ConfigureAwait(false);
    }

    public MissionInfo ParsePboFileName(string pboFileName)
    {
        var name = pboFileName ?? string.Empty;

        // Strip a trailing .pbo (case-insensitive).
        if (name.EndsWith(".pbo", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        var fullPboName = name;

        // Split on the LAST dot: left = mission name, right = terrain (empty if no dot).
        var missionName = fullPboName;
        var terrain = string.Empty;
        var lastDot = fullPboName.LastIndexOf('.');
        if (lastDot >= 0)
        {
            missionName = fullPboName[..lastDot];
            terrain = fullPboName[(lastDot + 1)..];
        }

        // Player count hint: leading alpha token followed by digits (e.g. co10 -> 10, tvt30 -> 30).
        var playerCountHint = 0;
        var match = PrefixDigitsRegex().Match(fullPboName);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed))
            playerCountHint = parsed;

        return new MissionInfo
        {
            PboFileName = pboFileName ?? string.Empty,
            FullPboName = fullPboName,
            MissionName = missionName,
            Terrain = terrain,
            PlayerCountHint = playerCountHint,
        };
    }

    public async Task<string[]> GetAvailableMissionNamesAsync(string serverDirectory)
    {
        var missions = await ScanMissionsAsync(serverDirectory).ConfigureAwait(false);
        return missions
            .Select(m => m.FullPboName)
            .Distinct()
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [GeneratedRegex("^[A-Za-z]+([0-9]+)")]
    private static partial Regex PrefixDigitsRegex();
}
