using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Checks the project's GitHub releases for a newer version (Octokit) and opens release pages.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Queries the latest GitHub release for the configured owner/repo and compares its tag to the
    /// running assembly version. Never throws — failures are reported via
    /// <see cref="UpdateCheckResult.Error"/> with <see cref="UpdateCheckResult.UpdateAvailable"/> false.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>Opens a release URL in the user's default browser.</summary>
    void OpenReleasePage(string url);
}
