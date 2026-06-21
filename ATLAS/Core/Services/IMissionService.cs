using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Pure-filesystem mission scanner. Enumerates <c>.pbo</c> mission files under a server directory's
/// <c>MPMissions</c>/<c>Missions</c> folders. No database; results are transient <see cref="MissionInfo"/> DTOs.
/// </summary>
public interface IMissionService
{
    /// <summary>
    /// Scans for missions on a background thread. When <paramref name="missionFolderOverride"/> is set, that
    /// exact folder is scanned; otherwise the default MPMissions/Missions folders under
    /// <paramref name="serverDirectory"/> are used.
    /// </summary>
    Task<List<MissionInfo>> ScanMissionsAsync(string serverDirectory, string? missionFolderOverride = null);

    /// <summary>Parses a .pbo file name into a <see cref="MissionInfo"/> (pure; never throws on odd names).</summary>
    MissionInfo ParsePboFileName(string pboFileName);

    /// <summary>Distinct, sorted <see cref="MissionInfo.FullPboName"/> values (for Discord autocomplete later).</summary>
    Task<string[]> GetAvailableMissionNamesAsync(string serverDirectory, string? missionFolderOverride = null);
}
