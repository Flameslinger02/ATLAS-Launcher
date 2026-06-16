namespace Atlas.Core.Models;

/// <summary>
/// Transient result of a filesystem mission scan (one entry per <c>.pbo</c> found under a server's
/// <c>MPMissions</c>/<c>Missions</c> folder). Never persisted; rebuilt on every scan.
/// </summary>
public class MissionInfo
{
    /// <summary>The .pbo file name as found on disk (e.g. <c>co10_Assault.Altis.pbo</c>).</summary>
    public string PboFileName { get; set; } = string.Empty;

    /// <summary>Mission name — the part of <see cref="FullPboName"/> before the last dot.</summary>
    public string MissionName { get; set; } = string.Empty;

    /// <summary>Terrain/world — the segment after the last dot in <see cref="FullPboName"/> (empty if none).</summary>
    public string Terrain { get; set; } = string.Empty;

    /// <summary>The file name without the <c>.pbo</c> extension (e.g. <c>co10_Assault.Altis</c>).</summary>
    public string FullPboName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }
    public DateTime LastModified { get; set; }

    /// <summary>The source folder name: "MPMissions" or "Missions".</summary>
    public string SourceFolder { get; set; } = string.Empty;

    /// <summary>First run of digits in the leading prefix (e.g. <c>co10</c> -&gt; 10, <c>tvt30</c> -&gt; 30), else 0.</summary>
    public int PlayerCountHint { get; set; }

    /// <summary>View-state flag: true when this mission matches the active profile's MissionName. Set during load.</summary>
    public bool IsActive { get; set; }
}
