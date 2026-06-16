namespace Atlas.Core.Models;

/// <summary>Outcome of a GitHub-release update check (Phase 13).</summary>
public sealed class UpdateCheckResult
{
    /// <summary>True when the latest published release is newer than the running build.</summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>The latest release version (without the leading "v"), or null when none was found.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>The currently running assembly version (e.g. "1.0.0").</summary>
    public string? CurrentVersion { get; set; }

    /// <summary>Browser URL of the latest release page.</summary>
    public string? ReleaseUrl { get; set; }

    /// <summary>First few lines of the release notes (trimmed for display).</summary>
    public string? ReleaseNotesSummary { get; set; }

    /// <summary>When the latest release was published.</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Set when the check could not complete (network/API). Null on success.</summary>
    public string? Error { get; set; }
}
